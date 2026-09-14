"""Fixture/source guards; numerical material acceptance requires the actual Player."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class ActorMapContractTests(unittest.TestCase):
    def test_independent_whole_fields_and_actual_final_path(self):
        source = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.ActorMaps.cs').read_text(encoding='utf-8')
        self.assertIn('class ActorMapTexture', source)
        self.assertIn('Texel(ix + 1, iy + 1)', source)
        self.assertIn('int[] modes={16,19,20,23,0}', source)
        self.assertIn('e<=.0003f', source)
        self.assertIn('actual.All(p=>p.a==1)', source)
        self.assertIn('camera.Render()', source)
        self.assertNotIn('Graphics.Blit', source)

    def test_sampler_profile_is_selected_without_observing_rendered_output(self):
        source = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.ActorMaps.cs').read_text(encoding='utf-8')
        oracle = source.split('private sealed class ActorMapTexture', 1)[1].split('private IEnumerator', 1)[0]
        self.assertIn('Mathf.Floor(a * b * 256 + .5f) / 256', oracle)
        self.assertIn('(1 - a - b + cross)', oracle)
        self.assertIn('(a - cross)', oracle)
        self.assertIn('(b - cross)', oracle)
        self.assertNotIn('ReadSceneTarget', oracle)
        self.assertNotIn('GetPixels', oracle)
        self.assertIn('GraphicsDeviceType.Direct3D11 && SystemInfo.graphicsDeviceVendorID == 0x10de', source)
        self.assertIn('anisoLevel = 0', source)
        self.assertIn('forced-anisotropy-is-different-input', source)
        self.assertIn('zero-anisotropy-restores-whole-frame', source)

    def test_default_source_preserves_existing_material_equations(self):
        source = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float metallic = isSkin ? 0.0 : saturate(definition.b)', source)
        self.assertIn('float smoothness = saturate(definition.g * _ActorMatcapParameters.y)', source)
        self.assertIn('baseSample.rgb += authoredRampAddRgb', source)
        self.assertIn('shadeSample.rgb += authoredRampAddRgb', source)
        self.assertIn('float2(type4RampX, 0.0)', source)

if __name__ == '__main__':
    unittest.main()
