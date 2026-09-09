from contextlib import redirect_stdout, redirect_stderr
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('prepare_demo', ROOT / 'examples/prepare_demo.py')
demo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(demo)


class OriginalDemoTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {'bundles': [{'name': 'original_a', 'role': 'motion'},
                                    {'name': 'original_b', 'role': 'motion'},
                                    {'name': 'original_face', 'role': 'face'}]}

    def test_all_templates_bind_exact_motion_names_without_mutating_templates(self):
        for name, filename in demo.TEMPLATES.items():
            with self.subTest(template=name):
                template = ROOT / 'examples' / filename
                original = template.read_bytes()
                scene = demo.instantiate(name, 'original_a', None if name == 'single' else 'original_b', self.manifest)
                self.assertNotIn('YOUR_', json.dumps(scene))
                self.assertEqual(scene['body_motions'][0]['motion'], 'original_a')
                if name != 'single':
                    self.assertEqual(scene['body_motions'][1]['motion'], 'original_b')
                self.assertEqual(template.read_bytes(), original)
                with tempfile.TemporaryDirectory() as temporary:
                    data = Path(temporary)
                    demo.write_new(data / 'story-timeline.json', scene)
                    self.assertEqual(demo.studio.inspect_story(data, self.manifest), [])

    def test_missing_unknown_and_non_motion_ids_are_rejected(self):
        for template, first, second in [('sequence', 'original_a', None),
                                         ('single', 'original_a', 'original_b'),
                                         ('single', 'missing', None),
                                         ('single', 'original_face', None)]:
            with self.subTest(template=template, first=first), self.assertRaises(ValueError):
                demo.instantiate(template, first, second, self.manifest)

    def test_existing_output_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'story-timeline.json'
            path.write_bytes(b'Existing private story, not a fixture template')
            original = path.read_bytes()
            with self.assertRaisesRegex(ValueError, 'already exists'):
                demo.write_new(path, {})
            self.assertEqual(path.read_bytes(), original)

    def test_output_requires_json_extension_before_creating_directories(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaises(ValueError):
                demo.write_new(root / 'new' / 'bad.cs', {})
            self.assertEqual(list(root.iterdir()), [])

    def test_dry_run_does_not_write_or_launch(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with patch.object(demo.studio, 'settings', return_value={'dataRoot': root}), \
                    patch.object(demo.studio, 'inspect_dataset', return_value=(
                        {'errors': [], 'warnings': []}, self.manifest)), \
                    patch.object(demo.studio.subprocess, 'Popen') as launch, \
                    redirect_stdout(io.StringIO()) as output:
                self.assertEqual(demo.main(['--template', 'single', '--motion', 'original_a',
                                            '--output', str(root / 'demo.json'), '--dry-run']), 0)
                self.assertEqual(json.loads(output.getvalue())['body_motions'][0]['motion'], 'original_a')
                launch.assert_not_called()
            self.assertEqual(list(root.iterdir()), [])

    def test_invalid_dataset_does_not_write(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with patch.object(demo.studio, 'settings', return_value={'dataRoot': root}), \
                    redirect_stderr(io.StringIO()):
                self.assertEqual(demo.main(['--template', 'single', '--motion', 'original_a',
                                            '--output', str(root / 'demo.json')]), 2)
            self.assertEqual(list(root.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
