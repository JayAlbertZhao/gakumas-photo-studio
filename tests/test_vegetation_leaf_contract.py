"""Public source/ownership contracts; enabled native behavior is validated separately in Player."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class VegetationLeafContractTests(unittest.TestCase):
    def read(self, name):
        return (RUNTIME / name).read_text(encoding='utf-8')

    def test_opt_in_authored_linear_inputs(self):
        source = self.read('VegetationLeafMaterial.cs')
        for token in ('public bool enabled;', 'public Texture thicknessMap;', 'IsSRGBFormat',
                      'Invalid leaf thickness/absorption/tint/strength', 'internal static void Bind'):
            self.assertIn(token, source)
        for token in ('Time.time', 'Shader.SetGlobal', 'GetData(', 'ReadPixels'):
            self.assertNotIn(token, source)

    def test_separate_current_deferred_attachment_and_lifetime(self):
        source = self.read('SceneDeferredCamera.cs')
        for token in ('public readonly RenderTexture leafTransmission;', 'maximumLeafResourceMiB',
                      'LeafResourceBytes', 'ReleaseLeaf()', 'SCENE_LEAF_OUTPUT', 'SCENE_LEAF_SCREEN_SHADOW',
                      'Leaf thickness input aliases an owned/current scene target'):
            self.assertIn(token, source)
        shader = self.read('Resources/SceneDeferred.shader')
        for token in ('float4 leaf : SV_Target6', 'float4 leaf : SV_Target5', 'float4 leaf : SV_Target4',
                      'LeafTransmission(i.uv)', 'LeafFacing', 'SceneGi(i.uv2,-n)', 'LeafDiffuse', 'LeafShadowNormal'):
            self.assertIn(token, shader)

    def test_current_light_consumers_and_explicit_optics(self):
        optics = self.read('Resources/VegetationLeaf.hlsl')
        self.assertIn('exp(-_LeafAbsorptionThickness.rgb * thickness)', optics)
        self.assertIn('backlight*(1-tau)+tau', optics)
        self.assertIn('SCENE_LEAF_LIGHTING', self.read('Resources/SceneDecalLight.hlsl'))
        self.assertIn('TOOLKIT_FORWARD_LEAF', self.read('Resources/SceneForwardLighting.hlsl'))
        self.assertIn('VegetationLeafMaterial.Bind', self.read('SceneForwardLightingCamera.cs'))


if __name__ == '__main__':
    unittest.main()
