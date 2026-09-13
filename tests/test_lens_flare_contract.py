"""Public isolation/resource/shape contracts. Actual Player/native images prove behavior."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class LensFlareContract(unittest.TestCase):
    def test_explicit_inputs_and_deterministic_time(self):
        code = (RUNTIME / 'LensFlareRenderer.cs').read_text(encoding='utf-8')
        for forbidden in ('Shader.GetGlobal', 'Shader.SetGlobal', 'FindObjectsOfType', 'File.Read', 'ReadPixels', 'Time.time', 'PhotoModeApp'):
            self.assertNotIn(forbidden, code)
        for required in ('double timeSeconds', 'FogVolumeDepth depth', 'settings.Validate', 'settings.atlas', 'time*emitter.pulseFrequency'):
            self.assertIn(required, code)

    def test_bounded_authored_configuration_default_off(self):
        code = (RUNTIME / 'LensFlareSettings.cs').read_text(encoding='utf-8')
        for required in ('public bool enabled;', 'MaximumEmitters = 32', 'MaximumElements = 1024', 'Disc, Ring, Polygon, Star, Texture', 'axisPosition', 'outsideScreenVisibility', 'RectInt atlasRect'):
            self.assertIn(required, code)

    def test_current_depth_real_instanced_producer_and_leases(self):
        code = (RUNTIME / 'LensFlareRenderer.cs').read_text(encoding='utf-8')
        for required in ('generation++', 'IsCurrent', 'Owns(source)', 'source == depth.texture', 'commands.DrawProcedural', 'Graphics.ExecuteCommandBuffer',
                         'emitterBuffer?.Dispose()', 'elementBuffer?.Dispose()', 'RenderTexture.active=saved', 'visibility.IsCreated()'):
            self.assertIn(required, code)

    def test_stable_profiles_sampler_free_low_resolution_resolve(self):
        code = (RUNTIME / 'Resources/LensFlare.shader').read_text(encoding='utf-8')
        code += (RUNTIME / 'Resources/LensFlareShared.hlsl').read_text(encoding='utf-8')
        for required in ('StructuredBuffer<FlareEmitter>', 'StructuredBuffer<FlareElement>', 'input.position.xy/_FlareInput.zw', 'SV_InstanceID',
                         'FlareVisible', 'dot(offset,offset)>1', 'radius>=.0001', 'Blend One One, Zero One', 'original.a'):
            self.assertIn(required, code)
        for forbidden in ('tex2D(', 'SamplerState', '_CameraDepthTexture'):
            self.assertNotIn(forbidden, code)

    def test_optional_bridge_before_temporal_and_preserved_legacy_shader(self):
        code = (RUNTIME / 'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertLess(code.index('current=ApplyLensFlares(current);'), code.index('RenderTexture temporal;'))
        for required in ('!lensFlares.enabled', 'lensFlareDepthProvider==null', 'lensFlareTimeSeconds', '_lensFlares?.Dispose();_lensFlares=null;'):
            self.assertIn(required, code)
        self.assertNotIn('LensFlare', (RUNTIME / 'Resources/OriginalStylePost.shader').read_text(encoding='utf-8'))


if __name__ == '__main__':
    unittest.main()
