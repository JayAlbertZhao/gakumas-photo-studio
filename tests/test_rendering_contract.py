"""Source wiring checks. These do not replace standalone GPU/image validation."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ActorRenderingWiringTests(unittest.TestCase):
    def test_shared_surface_and_explicit_supplementary_passes(self):
        surface = (ROOT / 'unity/Assets/Resources/PhotoModeFallback.shader').read_text(encoding='utf-8')
        extra = (ROOT / 'unity/Assets/Resources/ActorSupplemental.shader').read_text(encoding='utf-8')
        self.assertIn('#include "ActorSurface.cginc"', surface)
        self.assertIn('#include "ActorSurface.cginc"', extra)
        self.assertIn('#include "ActorOutline.cginc"', extra)
        self.assertNotIn('Name "ACTOR_HAIR_COVER"', surface)
        self.assertIn('Ref 4 ReadMask 4 WriteMask 0 Comp Equal', extra)
        self.assertLess(extra.index('Name "ACTOR_OUTLINE"'), extra.index('Name "ACTOR_HAIR_COVER"'))

    def test_profile_parameters_have_runtime_consumers(self):
        app = (ROOT / 'unity/Assets/Scripts/PhotoModeApp.cs').read_text(encoding='utf-8')
        for parameter in ('matCapOffset', 'matCapSmoothScale', 'shadeApplyRatio',
                          'giScale', 'additiveLightScale', 'additiveLightSpecularScale', 'eyeHighlightColor'):
            self.assertIn('profile.' + parameter, app)
        shader = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        for uniform in ('_ActorMatcapParameters', '_ActorLightingScales', '_ActorRimColor',
                        '_ActorEyeHighlightColor', '_CapturedShadeAdditive'):
            self.assertGreater(len(re.findall(re.escape(uniform), shader)), 1)
        self.assertIn('input.uv1 * float2(0.5, 1.0)', shader)
        self.assertIn('input.layerUv + float2(0.5, 0.0)', shader)

    def test_hair_is_not_globally_transparent(self):
        repair = (ROOT / 'unity/Assets/Scripts/MaterialRepairer.cs').read_text(encoding='utf-8')
        self.assertIn('CopyFloat(source, target, "_SrcBlend", 1f)', repair)
        self.assertIn('CopyFloat(source, target, "_ZWrite", 1f)', repair)
        controls = (ROOT / 'unity/Assets/Scripts/ActorRenderControls.cs').read_text(encoding='utf-8')
        self.assertIn('material.GetFloat("_ShaderType") - 8f', controls)
        self.assertIn('CameraEvent.BeforeForwardAlpha', controls)
        self.assertIn('_camera.RemoveCommandBuffer', controls)
        self.assertIn('_commands.Release()', controls)

    def test_hair_coverage_consumes_authored_view_fade_mask(self):
        repair = (ROOT / 'unity/Assets/Scripts/MaterialRepairer.cs').read_text(encoding='utf-8')
        shader = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('source.GetVector("_FadeParam")', repair)
        self.assertIn('dot(v, _HeadDirection.xyz)', shader)
        self.assertIn('dot(v, _HeadUpDirection.xyz)', shader)
        self.assertIn('1.0 - saturate(rawBaseSample.a)', shader)
        self.assertIn('max(obliqueCoverage.x, obliqueCoverage.y)', shader)


if __name__ == '__main__':
    unittest.main()
