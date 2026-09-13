"""DCC data tests and public contracts. Actual FBX callback and rendered playback are verified in Unity."""
from copy import deepcopy
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('performance_authoring', ROOT / 'tools/dcc/performance_authoring.py')
dcc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dcc)


class PerformanceAuthoringTests(unittest.TestCase):
    def setUp(self):
        self.template = json.loads((ROOT / 'examples/authored-performance.json').read_text(encoding='utf-8'))

    def test_baked_morph_samples_and_template_immutability(self):
        before = deepcopy(self.template)
        clip = dcc.bake(self.template, [dict(kind='morphs', target='face.smile', attribute='value')], 10, 40, 30, lambda a, f: (f - 10) / 30)
        self.assertEqual(self.template, before)
        self.assertEqual(clip['duration'], 1)
        self.assertEqual(len(clip['morphs'][0]['keys']), 31)
        self.assertEqual(clip['morphs'][0]['keys'][15]['value'], .5)
        self.assertEqual(json.loads(dcc.encode(clip)), clip)

    def test_effect_activation_intervals_preserve_order_and_end_exclusion(self):
        clip = dcc.bake(self.template, [dict(kind='effects', target='marker', attribute='on')], 0, 30, 30, lambda a, f: 1 if 6 <= f < 12 or 24 <= f else 0)
        first, second = clip['effects']
        self.assertEqual(first['id'], 'marker.0')
        self.assertEqual(second['id'], 'marker.1')
        self.assertEqual(first['start'], dcc.float32(.2))
        self.assertEqual(first['start'] + first['duration'], dcc.float32(.4))
        self.assertEqual(second['start'] + second['duration'], 1)
        self.assertEqual(first['seed'], 713)

    def test_fractional_fps_boundaries_are_float32_safe(self):
        clip = dcc.bake(self.template, [dict(kind='effects', target='marker', attribute='on')], 0, 3, 30, lambda a, f: f >= 2)
        entry = clip['effects'][0]
        self.assertEqual(entry['start'] + entry['duration'], clip['duration'])

    def test_decal_replaces_only_named_channel(self):
        clip = dcc.bake(self.template, [dict(kind='decals', target='cheek', channel=24, attribute='opacity')], 0, 2, 2, lambda a, f: f * .2)
        self.assertEqual(len(clip['decals'][0]['tracks']), 1)
        self.assertAlmostEqual(clip['decals'][0]['tracks'][0]['keys'][-1]['value'], .4)
        self.assertEqual(clip['decals'][0]['baseline'], self.template['decals'][0]['baseline'])

    def test_inactive_effect_has_no_interval(self):
        clip = dcc.bake(self.template, [dict(kind='effects', target='marker', attribute='on')], 0, 2, 2, lambda a, f: 0)
        self.assertEqual(clip['effects'], [])

    def test_invalid_clock_and_capacity_rejected_before_sampling(self):
        def forbidden(*args):
            self.fail('invalid input called DCC evaluator')
        for start, end, fps in [(0, 0, 30), (0, 8192, 30), (0, 10, 0), (0, 10, float('nan')), (False, 2, 30)]:
            with self.assertRaises(ValueError):
                dcc.bake(self.template, [], start, end, fps, forbidden)

    def test_invalid_scalar_and_duplicate_mapping_rejected(self):
        mapping = dict(kind='morphs', target='face.smile', attribute='value')
        for value in (float('nan'), float('inf'), 65):
            with self.assertRaises(ValueError):
                dcc.bake(self.template, [mapping], 0, 1, 30, lambda a, f: value)
        with self.assertRaises(ValueError):
            dcc.bake(self.template, [mapping, mapping], 0, 1, 30, lambda a, f: 0)

    def test_opaque_ids_and_schema(self):
        for value in ('../x', '/x', '', 'x/y', 'x:y'):
            with self.assertRaises(ValueError):
                dcc.identifier(value)
        self.template['schema'] = 'another-format'
        with self.assertRaises(ValueError):
            dcc.encode(self.template)

    def test_runtime_binds_explicitly_and_orders_geometry(self):
        source = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/PerformancePlayer.cs').read_text(encoding='utf-8')
        for value in ('TrySamplePose', 'TrySampleEffects', 'definition.TryCopy', 'Multiple IDs own', 'Material owner changed', 'ReleaseMaterial', 'topology'):
            self.assertIn(value, source)
        for value in ('Resources.Load', 'AssetDatabase', 'Time.time', 'FindObjectsOfType', 'File.ReadAllText'):
            self.assertNotIn(value, source)

    def test_importers_are_opt_in_and_converted(self):
        source = (ROOT / 'packages/com.digital-kotone.toolkit/Editor/PerformanceClipImporter.cs').read_text(encoding='utf-8')
        for value in ('ScriptedImporter(1, "performance")', 'OnPostprocessGameObjectWithUserProperties', 'photoStudioPerformance', 'PerformanceClip.TryParse', 'ImportedPerformance'):
            self.assertIn(value, source)

    def test_actual_import_and_player_gates_remain(self):
        source = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.Performance.cs').read_text(encoding='utf-8')
        for value in ('AssetBundle.LoadFromFile', 'actual-fbx-sidecar-whole-rgba-exact', 'independent-hermite', 'cpu-gpu-', 'negative-restored-whole-image', 'camera.Render()'):
            self.assertIn(value, source)
        source = (ROOT / 'unity/Assets/Editor/PerformanceAuthoringValidation.cs').read_text(encoding='utf-8')
        for value in ('actual-fbx-custom-property-callback', 'unmarked-model-untouched', 'sidecar-reimport-updates-data', 'BuildAssetBundles'):
            self.assertIn(value, source)

    def test_docs_separate_maya_execution_and_transport(self):
        source = (ROOT / 'docs/performance-authoring.md').read_text(encoding='utf-8')
        for value in ('Maya', '尚未实测', 'Blender', 'float32', 'TrySamplePose', 'TrySampleEffects', '不自动', '16384'):
            self.assertIn(value, source)


if __name__ == '__main__':
    unittest.main()
