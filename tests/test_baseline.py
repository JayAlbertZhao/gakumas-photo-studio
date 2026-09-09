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
    def test_unity_project_matches_pre_cleanup_baseline(self):
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


class TimelineFormatTests(unittest.TestCase):
    def test_original_examples_use_existing_timeline_event_fields(self):
        paths = sorted((ROOT / 'examples').glob('*.timeline.json'))
        self.assertEqual([path.name for path in paths], [
            '01-single-motion.timeline.json', '02-motion-transition.timeline.json'])
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
                self.assertEqual(scene['messages'], [])


if __name__ == '__main__':
    unittest.main()
