import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('verify_baseline', ROOT / 'tools/verify_baseline.py')
baseline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(baseline)


class FrozenSourceTests(unittest.TestCase):
    def test_unity_project_matches_historical_baseline_and_explicit_revisions(self):
        report = baseline.verify(ROOT)
        self.assertTrue(report['accepted'], report['findings'])

    def test_only_line_ending_normalization_is_allowed(self):
        self.assertEqual(baseline.digest(b'a\r\nb\r\n'), baseline.digest(b'a\nb\n'))
        self.assertNotEqual(baseline.digest(b'a\n'), baseline.digest(b'a \n'))
        self.assertNotEqual(baseline.digest(b'speed = 1;'), baseline.digest(b'speed = 2;'))

    def test_missing_modified_and_added_source_are_detected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'config').mkdir()
            source = root / 'unity/Assets/Scripts'
            source.mkdir(parents=True)
            (source / 'Frozen.cs').write_text('changed', encoding='utf-8')
            (source / 'Added.cs').write_text('extra', encoding='utf-8')
            (root / 'config/source-baseline.json').write_text(json.dumps({'files': {
                'unity/Assets/Scripts/Frozen.cs': baseline.digest(b'original'),
                'unity/Assets/Scripts/Missing.cs': baseline.digest(b'missing'),
            }}), encoding='utf-8')
            report = baseline.verify(root)
            self.assertEqual({f['rule'] for f in report['findings']}, {
                'missing_baseline_file', 'baseline_mismatch', 'unexpected_unity_source'})

    def test_revision_keeps_parent_and_rejects_unlocked_shader_include(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'config').mkdir()
            source = root / 'unity/Assets'
            source.mkdir(parents=True)
            name = 'unity/Assets/Actor.shader'
            old = baseline.digest(b'old')
            (source / 'Actor.shader').write_bytes(b'new')
            (source / 'Actor.cginc').write_bytes(b'include')
            (root / 'config/source-baseline.json').write_text(json.dumps({
                'archive_sha256': 'archive', 'files': {name: old}}), encoding='utf-8')
            revision = {'base_archive_sha256': 'archive', 'replacements': {
                name: {'baseline_sha256': old, 'sha256': baseline.digest(b'new')}},
                'additions': {}}
            revision_path = root / 'config/runtime-revisions.json'
            revision_path.write_text(json.dumps(revision), encoding='utf-8')
            self.assertEqual(baseline.verify(root)['findings'], [{
                'file': 'unity/Assets/Actor.cginc', 'rule': 'unexpected_unity_source'}])
            revision['additions']['unity/Assets/Actor.cginc'] = baseline.digest(b'include')
            revision_path.write_text(json.dumps(revision), encoding='utf-8')
            self.assertTrue(baseline.verify(root)['accepted'])
            revision['replacements'][name]['baseline_sha256'] = 'wrong-parent'
            revision_path.write_text(json.dumps(revision), encoding='utf-8')
            self.assertIn('invalid_revision_parent', {f['rule'] for f in baseline.verify(root)['findings']})

    def test_revision_cannot_read_outside_repository(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'config').mkdir()
            (root / 'config/source-baseline.json').write_text(json.dumps({
                'archive_sha256': 'archive', 'files': {}}), encoding='utf-8')
            (root / 'config/runtime-revisions.json').write_text(json.dumps({
                'base_archive_sha256': 'archive', 'additions': {'unity/../../outside': 'hash'}}), encoding='utf-8')
            self.assertEqual(baseline.verify(root)['findings'][0]['rule'], 'unsafe_revision_path')

    def test_relocation_preserves_hash_and_rejects_collisions_chains_and_missing_source(self):
        old = {'archive_sha256': 'base', 'files': {'unity/a.cs': 'a', 'unity/b.cs': 'b'}}
        revision = {'base_archive_sha256': 'base', 'relocations': {
            'unity/a.cs': 'packages/com.digital-kotone.toolkit/Runtime/a.cs'}}
        expected, findings = baseline.resolve_expected(old, revision)
        self.assertEqual(findings, [])
        self.assertEqual(expected, {'packages/com.digital-kotone.toolkit/Runtime/a.cs': 'a', 'unity/b.cs': 'b'})
        for moves in ({'unity/a.cs': 'unity/b.cs'}, {'unity/missing.cs': 'unity/c.cs'},
                      {'unity/a.cs': 'unity/c.cs', 'unity/c.cs': 'unity/d.cs'},
                      {'unity/a.cs': 'unity/c.cs', 'unity/b.cs': 'unity/c.cs'}):
            with self.subTest(relocations=moves):
                _, findings = baseline.resolve_expected(old, dict(revision, relocations=moves))
                self.assertIn('invalid_revision_relocation', {f['rule'] for f in findings})

    def test_relocated_package_is_scanned_and_cannot_escape_repository(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'config').mkdir()
            target = 'packages/com.digital-kotone.toolkit/Runtime/a.cs'
            (root / target).parent.mkdir(parents=True)
            (root / target).write_bytes(b'original')
            (root / 'config/source-baseline.json').write_text(json.dumps({
                'archive_sha256': 'base', 'files': {'unity/a.cs': baseline.digest(b'original')}}), encoding='utf-8')
            revision = {'base_archive_sha256': 'base', 'relocations': {'unity/a.cs': target}}
            path = root / 'config/runtime-revisions.json'
            path.write_text(json.dumps(revision), encoding='utf-8')
            self.assertTrue(baseline.verify(root)['accepted'])
            (root / target).with_name('unreviewed.cginc').write_text('new', encoding='utf-8')
            self.assertIn('unexpected_unity_source', {f['rule'] for f in baseline.verify(root)['findings']})
            revision['relocations']['unity/a.cs'] = 'packages/com.digital-kotone.toolkit/../../outside'
            path.write_text(json.dumps(revision), encoding='utf-8')
            self.assertIn('unsafe_revision_path', {f['rule'] for f in baseline.verify(root)['findings']})


class TimelineFormatTests(unittest.TestCase):
    def test_original_examples_use_existing_timeline_event_fields(self):
        paths = sorted((ROOT / 'examples').glob('*.timeline.json'))
        self.assertEqual([path.name for path in paths], [
            '01-single-motion.timeline.json', '02-motion-transition.timeline.json',
            '03-motion-sequence.timeline.json'])
        for path in paths:
            with self.subTest(file=path.name):
                scene = json.loads(path.read_text(encoding='utf-8'))
                self.assertNotIn('timeline', scene)  # Withdrawn wrapper format.
                self.assertGreater(scene['duration'], 8.45)  # Frozen entrypoint start time.
                events = scene['body_motions']
                self.assertTrue(events)
                self.assertEqual(events, sorted(events, key=lambda e: (e['time'], e['order'])))
                for event in events:
                    self.assertLessEqual(set(event), {'time', 'duration', 'order', 'motion',
                                                     'clipIn', 'transition', 'facialTransition', 'ease'})
                    self.assertGreaterEqual(event['time'], 0)
                    self.assertGreater(event['duration'], 0)
                    self.assertLessEqual(event['time'] + event['duration'], scene['duration'])
                    self.assertTrue(event['motion'].startswith('YOUR_'))
                for message in scene['messages']:
                    self.assertEqual(set(message), {'time', 'duration', 'order', 'name', 'text'})
                    self.assertGreaterEqual(message['time'], 0)
                    self.assertGreater(message['duration'], 0)
                    self.assertLessEqual(message['time'] + message['duration'], scene['duration'])
                    self.assertTrue(message['text'])


if __name__ == '__main__':
    unittest.main()
