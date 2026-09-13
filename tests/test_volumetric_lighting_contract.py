"""Static API/isolation checks; actual Player and native full images validate rendering."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class VolumetricLightingContract(unittest.TestCase):
    def test_independent_default_off_explicit_inputs(self):
        settings = (RUNTIME / 'VolumetricLightingSettings.cs').read_text(encoding='utf-8')
        renderer = (RUNTIME / 'VolumetricLightingRenderer.cs').read_text(encoding='utf-8')
        for forbidden in ('Shader.SetGlobal', 'Shader.GetGlobal', 'FindObjectsOfType', 'File.Read', 'BundleCatalog', 'PhotoModeApp'):
            self.assertNotIn(forbidden, settings + renderer)
        for required in ('public bool enabled;', 'MaximumLights=16', 'linearRadiance', 'scatteringAlbedo', 'samplesPerLight', 'SceneLightShadowInput'):
            self.assertIn(required, settings)
        self.assertNotRegex(settings + renderer, r'\.linear\b')

    def test_owned_current_frames_and_real_shadow_producer(self):
        renderer = (RUNTIME / 'VolumetricLightingRenderer.cs').read_text(encoding='utf-8')
        for required in ('IsCurrent', 'generation++', 'Owns(source)', 'source==depth.texture', 'shadows.Record(commands)', 'Graphics.ExecuteCommandBuffer(commands)',
                         'shadows.BindSingle(material,i)', 'RenderTexture.active=saved', 'shadows.Dispose()', 'shadows.Atlas.IsCreated()', 'ShadowCasterDrawCalls'):
            self.assertIn(required, renderer)

    def test_finite_shadowed_transport_and_exact_alpha(self):
        shader = (RUNTIME / 'Resources/VolumetricLighting.shader').read_text(encoding='utf-8')
        for required in ('VolumeBox', 'VolumeCone', 'mediumEnter', 'lightPath', 'SceneLightVisibility(world,float3(0,0,0),data)',
                         'denominator=1+g*(g-2*mu)', 'FogOneMinusExp(sigma*stepLength)', 'Blend One One, Zero One', '_VolumeScattering.Load(texel).rgb,original.a'):
            self.assertIn(required, shader)
        self.assertNotIn('_CameraDepthTexture', shader)

    def test_optional_bridge_before_temporal_and_no_legacy_shader_changes(self):
        post = (RUNTIME / 'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertLess(post.index('current=ApplyVolumetricLighting(current);'), post.index('RenderTexture temporal;'))
        self.assertIn('if(volumetricDepthProvider==null)', post)
        self.assertIn('!volumetricLighting.enabled', post)
        self.assertIn('_volumetricLighting?.Dispose();_volumetricLighting=null;', post)
        self.assertNotIn('VolumetricLighting', (RUNTIME / 'Resources/OriginalStylePost.shader').read_text(encoding='utf-8'))


if __name__ == '__main__':
    unittest.main()
