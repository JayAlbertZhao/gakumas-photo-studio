import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('verify_local_assets',
    Path(__file__).resolve().parents[1] / 'tools/verify_local_assets.py')
assets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assets)


def fixture(root):
    (root / 'LocalAssets/runtime').mkdir(parents=True)
    (root / 'config').mkdir()
    (root / 'config/source-baseline.json').write_bytes(b'{}\r\n')
    data = b'Synthetic transport fixture, not a game asset'
    (root / 'LocalAssets/runtime/demo.bin').write_bytes(data)
    manifest = {'schema': 'photo-studio.local-assets.v1',
                'source_baseline_sha256': hashlib.sha256(b'{}\n').hexdigest(),
                'files': [{'path': 'LocalAssets/runtime/demo.bin', 'bytes': len(data),
                           'sha256': hashlib.sha256(data).hexdigest()}]}
    (root / 'LocalAssets/PACKAGE.json').write_text(json.dumps(manifest), encoding='utf-8')
    return manifest


class PrivateTransferIntegrityTests(unittest.TestCase):
    def test_valid_pack_is_read_only_and_baseline_ignores_only_crlf(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture(root)
            before = {p.relative_to(root): p.read_bytes() for p in root.rglob('*') if p.is_file()}
            self.assertTrue(assets.verify(root)['accepted'])
            self.assertEqual(before, {p.relative_to(root): p.read_bytes() for p in root.rglob('*') if p.is_file()})
            (root / 'config/source-baseline.json').write_bytes(b'{"changed":true}\n')
            self.assertFalse(assets.verify(root)['accepted'])

    def test_missing_and_modified_payloads_are_detected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture(root)
            data = root / 'LocalAssets/runtime/demo.bin'
            data.write_bytes(b'changed')
            self.assertEqual(assets.verify(root)['findings'][0]['rule'], 'content_mismatch')
            data.unlink()
            self.assertEqual(assets.verify(root)['findings'][0]['rule'], 'missing_file')

    def test_paths_cannot_target_code_git_or_escape_project(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for path in ('../outside', '/absolute', 'C' + ':/absolute', '.git/config',
                         'studio.py', 'LocalAssets/../studio.py', 'LocalAssets//file', 'LocalAssets\\file'):
                with self.subTest(path=path), self.assertRaises(ValueError):
                    assets.local_path(root, path)

    def test_empty_duplicate_case_alias_and_invalid_records_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest = fixture(root)
            record = manifest['files'][0]
            variants = [[], [None], [record, record],
                        [record, {**record, 'path': record['path'].upper()}],
                        [{**record, 'bytes': True}], [{**record, 'sha256': 'invalid'}],
                        [{**record, 'path': 'LocalAssets/PACKAGE.json'}]]
            for records in variants:
                with self.subTest(records=records):
                    manifest['files'] = records
                    (root / 'LocalAssets/PACKAGE.json').write_text(json.dumps(manifest), encoding='utf-8')
                    self.assertFalse(assets.verify(root)['accepted'])


if __name__ == '__main__':
    unittest.main()
