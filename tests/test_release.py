import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('release', Path(__file__).resolve().parents[1] / 'tools/release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseBoundaryTests(unittest.TestCase):
    def test_rejects_private_and_game_payloads(self):
        for name in ('../escape.cs', 'research/dump.cs', 'game.dll', 'model.fbx',
                     'unity/Assets/PrivateResources/a.cs', 'settings.local.json',
                     'unity/Assets/Resources/body.asset', 'private-reference/a.md',
                     'image.png.meta', '/absolute.cs', 'a//b.cs', './a.cs',
                     'a.cs:stream.cs', 'unity/Assets/body.ASSET'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                release.validate_name(name)

    def test_accepts_source_names(self):
        for name in ('unity/Assets/Scripts/Example.cs', '.gitignore', 'docs/assets.md',
                     'unity/Assets/Resources/Example.shader.meta'):
            release.validate_name(name)

    def test_only_documentation_allowed_under_local_assets(self):
        release.validate_name('LocalAssets/README.md')
        for name in ('LocalAssets/runtime/README.md', 'LocalAssets/model.cs',
                     'LocalAssets/README.md.meta', 'unity/Assets/Scenes/Imported.unity'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                release.validate_name(name)

    def test_worktree_release_boundary(self):
        report, _ = release.collect(Path(__file__).resolve().parents[1])
        self.assertTrue(report['accepted'], report['findings'])

    def test_legacy_path_exception_is_content_pinned_and_not_a_secret_exception(self):
        import hashlib
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'config').mkdir()
            name = 'Legacy.cs'
            data = ('// ' + 'D' + ':/' + 'nonpersonal/project\n').encode()
            (root / name).write_bytes(data)
            (root / 'public-files.json').write_text(json.dumps({'files': [name]}), encoding='utf-8')
            policy = {'files': {name: hashlib.sha256(data).hexdigest()},
                      'nonpersonal_legacy_path_files': [name]}
            (root / 'config/source-baseline.json').write_text(json.dumps(policy), encoding='utf-8')
            self.assertTrue(release.collect(root)[0]['accepted'])
            (root / name).write_bytes(data + b'// another path\n')
            rules = {f['rule'] for f in release.collect(root)[0]['findings']}
            self.assertIn('windows_absolute_path', rules)
            self.assertIn('baseline_mismatch', rules)
            secret = data + ('gh' + 'p_' + 'z' * 36).encode()
            (root / name).write_bytes(secret)
            policy['files'][name] = hashlib.sha256(secret).hexdigest()
            (root / 'config/source-baseline.json').write_text(json.dumps(policy), encoding='utf-8')
            rules = {f['rule'] for f in release.collect(root)[0]['findings']}
            self.assertIn('service_token', rules)

    def test_missing_allowlisted_index_entry_blocks_release(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            subprocess.run(['git', '-C', str(root), 'init', '-q'], check=True, capture_output=True)
            (root / 'README.md').write_text('# Unstaged', encoding='utf-8')
            (root / 'public-files.json').write_text(json.dumps({'files': ['README.md']}), encoding='utf-8')
            rules = {f['rule'] for f in release.collect(root, tracked=True)[0]['findings']}
            self.assertIn('allowlisted_not_tracked', rules)

    def test_runtime_revision_requires_parent_hash_and_still_scans_content(self):
        import hashlib
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'config').mkdir()
            source = root / 'unity/Assets'
            source.mkdir(parents=True)
            name = 'unity/Assets/Actor.cginc'
            old = hashlib.sha256(b'old').hexdigest()
            (root / 'public-files.json').write_text(json.dumps({'files': [name]}), encoding='utf-8')
            (root / 'config/source-baseline.json').write_text(json.dumps({
                'archive_sha256': 'archive', 'files': {name: old}}), encoding='utf-8')
            revision = {'base_archive_sha256': 'archive', 'replacements': {name: {
                'baseline_sha256': old, 'sha256': hashlib.sha256(b'new').hexdigest()}}}
            revision_path = root / 'config/runtime-revisions.json'
            revision_path.write_text(json.dumps(revision), encoding='utf-8')
            (root / name).write_bytes(b'new')
            self.assertTrue(release.collect(root)[0]['accepted'])
            revision['replacements'][name]['baseline_sha256'] = 'wrong-parent'
            revision_path.write_text(json.dumps(revision), encoding='utf-8')
            self.assertIn('invalid_revision_parent', {f['rule'] for f in release.collect(root)[0]['findings']})
            data = ('gh' + 'p_' + 'z' * 36).encode()
            (root / name).write_bytes(data)
            revision['replacements'][name] = {'baseline_sha256': old, 'sha256': hashlib.sha256(data).hexdigest()}
            revision_path.write_text(json.dumps(revision), encoding='utf-8')
            self.assertIn('service_token', {f['rule'] for f in release.collect(root)[0]['findings']})

    def test_generated_ignore_is_default_deny(self):
        content = release.render_gitignore(['README.md', 'unity/Assets/Scripts/Example.cs'])
        self.assertIn('\n*\n', content)
        self.assertIn('!/unity/Assets/Scripts/\n', content)
        self.assertIn('!/unity/Assets/Scripts/Example.cs\n', content)
        self.assertNotIn('!*/', content)

    def test_redacts_sensitive_matches(self):
        personal = 'C' + ':/' + 'Users/' + 'example/private'
        token = 'gh' + 'p_' + 'x' * 36
        text = (personal + '\n' + token).encode()
        findings = release.audit_content('fixture.cs', text)
        self.assertEqual({f['rule'] for f in findings},
                         {'windows_absolute_path', 'home_path', 'service_token'})
        self.assertNotIn(token, str(findings))
        self.assertNotIn(personal, str(findings))

    def test_rejects_embedded_binary(self):
        self.assertEqual(release.audit_content('a.cs', b'a\x00b')[0]['rule'], 'binary_or_oversized')

    def test_archive_contains_only_payload_and_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / 'source.zip'
            release.pack(output, {'README.md': b'# Test\n'})
            with zipfile.ZipFile(output) as archive:
                self.assertEqual(set(archive.namelist()), {'README.md', 'SOURCE-MANIFEST.json'})
                self.assertEqual(archive.read('README.md'), b'# Test\n')
            with self.assertRaises(FileExistsError):
                release.pack(output, {'README.md': b'changed'})

    def test_staged_secret_cannot_hide_behind_clean_worktree(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            def git(*args):
                return subprocess.run(['git', '-C', str(root), *args], check=True,
                                      capture_output=True)
            git('init', '-q')
            (root / 'public-files.json').write_text(
                json.dumps({'files': ['README.md', 'public-files.json']}), encoding='utf-8')
            readme = root / 'README.md'
            readme.write_text('gh' + 'p_' + 'y' * 36, encoding='utf-8')
            git('add', 'README.md', 'public-files.json')
            readme.write_text('# Clean worktree\n', encoding='utf-8')
            report, _ = release.collect(root, tracked=True)
            self.assertFalse(report['accepted'])
            rules = {finding['rule'] for finding in report['findings']}
            self.assertIn('service_token', rules)
            self.assertIn('index_worktree_mismatch', rules)

    def test_unreviewed_tracked_file_blocks_release(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            subprocess.run(['git', '-C', str(root), 'init', '-q'], check=True,
                           capture_output=True)
            (root / 'public-files.json').write_text(
                json.dumps({'files': ['public-files.json']}), encoding='utf-8')
            (root / 'private.bin').write_bytes(b'not-public')
            subprocess.run(['git', '-C', str(root), 'add', '.'], check=True,
                           capture_output=True)
            report, _ = release.collect(root, tracked=True)
            self.assertIn('tracked_not_allowlisted', {f['rule'] for f in report['findings']})


if __name__ == '__main__':
    unittest.main()
