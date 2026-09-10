import argparse
from contextlib import redirect_stdout, redirect_stderr
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('studio', Path(__file__).resolve().parents[1] / 'studio.py')
studio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(studio)


def dataset(root):
    data = root / 'data with spaces'
    data.mkdir()
    records = []
    for role, name in [('face', 'mdl_chr_demo-base-0000_face'),
                       ('hair', 'mdl_chr_demo-hair-0000_hair'),
                       ('costume', 'mdl_chr_demo-cstm-0000_body'), ('motion', 'demo_idle')]:
        filename = name + '.bundle'
        (data / filename).write_bytes(b'Synthetic file-existence fixture, NOT an AssetBundle')
        records.append({'name': name, 'role': role, 'output_relative_path': filename})
    manifest = {'bundles': records, 'voices': []}
    (data / 'staging-manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
    return data, manifest


class StudioOnboardingTests(unittest.TestCase):
    def test_fresh_clone_missing_assets_explains_next_step_and_does_not_write(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {}, clear=True):
            root = Path(temporary)
            output = io.StringIO()
            with redirect_stdout(output):
                self.assertEqual(studio.main(['doctor'], root), 2)
            self.assertIn('staging-manifest.json is missing', output.getvalue())
            self.assertEqual(list(root.iterdir()), [])

    def test_config_is_local_and_explicit_override_wins(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {}, clear=True):
            root = Path(temporary)
            data, _ = dataset(root)
            with redirect_stdout(io.StringIO()):
                self.assertEqual(studio.main(['configure', '--data', str(data)], root), 0)
            config = studio.read_object(root / 'studio.local.json')
            self.assertEqual(Path(config['dataRoot']), data.resolve())
            args = argparse.Namespace(data=str(root), editor=None, riverbed=None)
            self.assertEqual(studio.settings(args, root)['dataRoot'], root.resolve())

    def test_doctor_validates_structure_but_does_not_claim_asset_compatibility(self):
        with tempfile.TemporaryDirectory() as temporary:
            data, _ = dataset(Path(temporary))
            report, _ = studio.inspect_dataset(data)
            self.assertEqual(report['errors'], [])
            self.assertEqual(report['characters'], ['demo'])
            self.assertIn('File checks do not validate bundle contents', report['warnings'][-1])

    def test_empty_invalid_and_duplicate_manifests_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            data, manifest = dataset(Path(temporary))
            path = data / 'staging-manifest.json'
            for invalid in ({'bundles': []}, {'bundles': {}},
                            {'bundles': [None]}, {'bundles': manifest['bundles'] * 2}):
                with self.subTest(invalid=invalid):
                    path.write_text(json.dumps(invalid), encoding='utf-8')
                    self.assertTrue(studio.inspect_dataset(data)[0]['errors'])

    def test_dataset_paths_cannot_escape_and_missing_files_are_reported(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data, manifest = dataset(root)
            for relative in ('../outside', '/outside', 'C' + ':/outside', 'a\\b', 'missing.bundle'):
                with self.subTest(relative=relative), self.assertRaises(ValueError):
                    studio.local_file(data, relative)
            manifest['bundles'][0]['output_relative_path'] = '../outside'
            (data / 'staging-manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
            self.assertTrue(studio.inspect_dataset(data)[0]['errors'])

    def test_dependency_gaps_are_warnings_not_false_visual_acceptance(self):
        with tempfile.TemporaryDirectory() as temporary:
            data, manifest = dataset(Path(temporary))
            manifest['bundles'][0]['dependencies'] = ['missing_dependency']
            (data / 'staging-manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
            report, _ = studio.inspect_dataset(data)
            self.assertEqual(report['errors'], [])
            self.assertTrue(any('dependency IDs' in warning for warning in report['warnings']))

    def test_launch_passes_existing_flags_and_environment_without_shell(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {}, clear=True):
            root = Path(temporary)
            data, _ = dataset(root)
            player = root / 'unity/output/KotonePhotoStudio.exe'
            player.parent.mkdir(parents=True)
            player.write_bytes(b'not executable; launch is mocked')
            with patch.object(studio, 'check_baseline'), patch.object(studio.subprocess, 'Popen') as launch:
                launch.return_value.pid = 42
                with redirect_stdout(io.StringIO()):
                    result = studio.main(['photo', '--data', str(data), '--character', 'demo'], root)
            self.assertEqual(result, 0)
            argv = launch.call_args.args[0]
            self.assertEqual(argv[:4], [str(player), '--photo-mode', '--character-id', 'demo'])
            self.assertEqual(argv[4], '-logFile')
            self.assertTrue(argv[5].endswith('.local.log'))
            self.assertNotIn('shell', launch.call_args.kwargs)
            self.assertEqual(Path(launch.call_args.kwargs['env']['GAKUMAS_PHOTO_STAGING']), data.resolve())

    def test_missing_default_character_requires_explicit_selection(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data, _ = dataset(root)
            with redirect_stdout(io.StringIO()), patch.object(studio.subprocess, 'Popen') as launch:
                self.assertEqual(studio.main(['photo', '--data', str(data)], root), 2)
                launch.assert_not_called()

    def test_story_rejects_missing_file_unknown_motion_and_invalid_times(self):
        with tempfile.TemporaryDirectory() as temporary:
            data, manifest = dataset(Path(temporary))
            self.assertTrue(studio.inspect_story(data, manifest))
            story = {'duration': 12, 'body_motions': [{'time': 0, 'duration': 12, 'motion': 'demo_idle'}]}
            path = data / 'story-timeline.json'
            path.write_text(json.dumps(story), encoding='utf-8')
            self.assertEqual(studio.inspect_story(data, manifest), [])
            story['body_motions'][0]['motion'] = 'missing'
            path.write_text(json.dumps(story), encoding='utf-8')
            self.assertTrue(studio.inspect_story(data, manifest))
            story['body_motions'][0]['time'] = float('nan')
            path.write_text(json.dumps(story), encoding='utf-8')
            self.assertTrue(any('finite' in error for error in studio.inspect_story(data, manifest)))

    def test_missing_editor_does_not_start_build(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {}, clear=True):
            with patch.object(studio, 'check_baseline'), patch.object(studio.subprocess, 'run') as run:
                with redirect_stderr(io.StringIO()):
                    self.assertEqual(studio.main(['build'], Path(temporary)), 2)
                run.assert_not_called()

    def test_build_rejects_compile_errors_even_with_success_marker(self):
        success = '[PhotoMode] Build succeeded: synthetic fixture\n'
        cases = [
            (0, success, 0),
            (0, success + 'Shader warning: synthetic warning\n', 0),
            (0, success + '[Licensing::Client] Error: synthetic non-compiler message\n', 0),
            (0, success + "Shader error in 'Fixture': maximum ps_5_0 sampler register index (16) exceeded\n", 2),
            (0, 'Fixture.cs(1,1): error CS1002: ; expected\n' + success, 2),
            (0, 'No success marker\n', 2),
            (1, success, 2),
            (0, None, 2),
        ]
        for exit_code, contents, expected in cases:
            with self.subTest(exit_code=exit_code, contents=contents), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                (root / 'unity').mkdir()
                editor = root / 'Editor.exe'
                editor.write_bytes(b'Synthetic fixture, never executed')

                def complete(command, **kwargs):
                    if contents is not None:
                        Path(command[command.index('-logFile') + 1]).write_text(contents, encoding='utf-8')
                    return studio.subprocess.CompletedProcess(command, exit_code)

                with patch.object(studio, 'check_baseline'), patch.object(studio.subprocess, 'run', side_effect=complete):
                    with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                        self.assertEqual(studio.main(['build', '--editor', str(editor)], root), expected)


if __name__ == '__main__':
    unittest.main()
