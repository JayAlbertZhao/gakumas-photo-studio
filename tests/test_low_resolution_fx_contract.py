"""Static API/isolation contracts; actual Player and native image evidence are separate gates."""
from pathlib import Path
import unittest

RUNTIME = Path(__file__).resolve().parents[1] / 'packages/com.digital-kotone.toolkit/Runtime'


class LowResolutionFxContract(unittest.TestCase):
    def test_explicit_owned_inputs_without_discovery(self):
        code = (RUNTIME / 'LowResolutionFxRenderer.cs').read_text(encoding='utf-8')
        for forbidden in ('Shader.GetGlobal', 'Shader.SetGlobal', 'FindObjectsOfType', 'ReadPixels', 'File.Read', 'Time.time', 'PhotoModeApp'):
            self.assertNotIn(forbidden, code)
        for required in ('FogVolumeDepth depth', 'source==depth.texture', 'Owns(source)', 'maximumTargetMiB', 'commands.DrawMesh', 'commands.DrawRenderer'):
            self.assertIn(required, code)

    def test_default_off_and_explicit_blend_order(self):
        settings = (RUNTIME / 'LowResolutionFxSettings.cs').read_text(encoding='utf-8')
        for required in ('public bool enabled;', 'MaximumSurfaces = 256', 'Alpha, Additive, Distortion', 'Full = 1, Half = 2, Quarter = 4'):
            self.assertIn(required, settings)
        code = (RUNTIME / 'LowResolutionFxRenderer.cs').read_text(encoding='utf-8')
        for required in ('active[end].blend!=FxBlend.Distortion', 'active[end].resolution==first.resolution', 'current=next;', 'Draw(start,end,next,2'):
            self.assertIn(required, code)
        self.assertNotIn('.Sort(', code)

    def test_shader_depth_repair_and_sampler_independence(self):
        code = (RUNTIME / 'Resources/LowResolutionFx.shader').read_text(encoding='utf-8')
        code += (RUNTIME / 'Resources/LowResolutionFxShared.hlsl').read_text(encoding='utf-8')
        for required in ('ReduceDepth', 'FxNeedsRepair', 'FxRefract', 'range.y-range.x', 'ColorMask RGB', 'original.a', 'Blend One OneMinusSrcAlpha'):
            self.assertIn(required, code)
        for forbidden in ('tex2D(', 'SamplerState', '_CameraDepthTexture'):
            self.assertNotIn(forbidden, code)

    def test_current_leases_and_complete_cleanup(self):
        code = (RUNTIME / 'LowResolutionFxRenderer.cs').read_text(encoding='utf-8')
        for required in ('generation++', 'owner.Created', 'TryGetLastBatch', 'ReleaseScratch(i)', 'ReleaseTargets();', 'RenderTexture.active=saved'):
            self.assertIn(required, code)

    def test_default_post_shader_untouched_and_bridge_before_taa(self):
        code = (RUNTIME / 'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertLess(code.index('current=ApplyLowResolutionFx(current);'), code.index('RenderTexture temporal;'))
        for required in ('!lowResolutionFx.enabled', 'lowResolutionFxDepthProvider==null', '_lowResolutionFx?.Dispose();_lowResolutionFx=null;'):
            self.assertIn(required.replace(' ', ''), code.replace(' ', ''))
        self.assertNotIn('LowResolutionFx', (RUNTIME / 'Resources/OriginalStylePost.shader').read_text(encoding='utf-8'))


if __name__ == '__main__':
    unittest.main()
