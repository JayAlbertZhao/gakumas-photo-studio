"""Source/ownership contracts, separate from actual current vertex/native image evidence."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class VegetationWindContractTests(unittest.TestCase):
    def source(self, name):
        return (RUNTIME / name).read_text(encoding='utf-8')

    def test_explicit_clock_snapshot_and_current_output(self):
        text = self.source('VegetationWindDeformer.cs')
        for term in ('double seconds', 'phase -= Math.Floor(phase)', 'UnityEngine.Object.Instantiate(source)',
                     'LastUpdateSucceeded && mesh != null', 'source.bindposes.Length == 0',
                     'source.blendShapeCount == 0', 'using (var indices = source.GetIndexBuffer())'):
            self.assertIn(term, text)
        for forbidden in ('Time.time', 'Random.', 'ReadPixels', 'GetData(', 'Shader.SetGlobal', 'source.MarkDynamic'):
            self.assertNotIn(forbidden, text)
        self.assertIn('public bool enabled;', self.source('VegetationWindSettings.cs'))
        self.assertIn('wind.Equals(Vector3.zero) && flutter.Equals(Vector3.zero)', text)
        self.assertIn('axis = Normalize(settings.upLocal)', text)
        self.assertIn('private static Vector3 Normalize(Vector3 v) => v / v.magnitude;', text)

    def test_actual_native_mesh_streams_and_resource_budget(self):
        text = self.source('VegetationWindDeformer.cs')
        self.assertLess(text.index('Vegetation owned GPU resource budget exceeded'), text.index('mesh = UnityEngine.Object.Instantiate'))
        for term in ('mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw', 'mesh.GetVertexBuffer(s)',
                     'outputs[s]?.Dispose()', 'mesh.bounds = bounds', 'VegetationWindBackend.Cpu',
                     'Vegetation field would fold', 'source.normals', 'source.tangents'):
            self.assertIn(term, text)
        shader = self.source('Resources/VegetationWind.compute')
        for term in ('RWByteAddressBuffer', '_VegetationAtRest', 'h*h*(3-2*h)', '6*h*(1-h)', 'Store(id,0', 'Store(id,1', 'Store(id,2'):
            self.assertIn(term, shader)

    def test_real_current_scene_consumers_and_reference(self):
        text = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.VegetationWind.cs').read_text(encoding='utf-8')
        for term in ('finite differences', 'every-vertex-field-and-jacobian', 'every-cpu-gpu-attribute',
                     'whole-native-color', 'a.normalGroup,b.normalGroup', 'a.mosDepth,b.mosDepth',
                     'a.lightShadowAtlas,b.lightShadowAtlas', 'a.motionVectors,b.motionVectors',
                     'positive-current-shadow', 'positive-current-motion', 'native-compute-current-depth-shadow-motion'):
            self.assertIn(term, text)


if __name__ == '__main__':
    unittest.main()
