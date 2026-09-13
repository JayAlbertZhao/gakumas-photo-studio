"""Static reduced-volume contracts; actual Player/native whole-image gates remain separate."""
from pathlib import Path
import unittest

RUNTIME=Path(__file__).resolve().parents[1]/'packages/com.digital-kotone.toolkit/Runtime'


class VolumetricReconstructionContract(unittest.TestCase):
    def test_explicit_full_default_and_memory_budget(self):
        settings=(RUNTIME/'VolumetricLightingSettings.cs').read_text(encoding='utf-8')
        self.assertIn('resolution=VolumetricResolution.Full',settings)
        self.assertIn('Full=1, Half=2, Quarter=4',settings)
        code=(RUNTIME/'VolumetricLightingRenderer.Reconstruction.cs').read_text(encoding='utf-8')
        for text in ('maximumTargetMiB','FormatUsage.Blend','FormatUsage.Render','FormatUsage.Sample','TargetBytes'):
            self.assertIn(text,code)

    def test_actual_low_grid_and_conditional_full_integration(self):
        code=(RUNTIME/'VolumetricLightingRenderer.Reconstruction.cs').read_text(encoding='utf-8')
        for text in ('width,height,out var lowView','Graphics.Blit(source,lowScattering,materials[i],0)','Graphics.Blit(source,scattering,material,3)','shadows.Record(commands)'):
            self.assertIn(text,code)
        shader=(RUNTIME/'Resources/VolumetricReconstruction.shader').read_text(encoding='utf-8')
        self.assertLess(shader.index('_VolumeReintegration.Load',shader.index('float4 Reintegrate(')),shader.index('return Scatter(input);'))

    def test_shared_owned_integrator_and_unchanged_full_passes(self):
        legacy=(RUNTIME/'Resources/VolumetricLighting.shader').read_text(encoding='utf-8')
        reduced=(RUNTIME/'Resources/VolumetricReconstruction.shader').read_text(encoding='utf-8')
        for source in (legacy,reduced):self.assertIn('#include "VolumetricLightingShared.hlsl"',source)
        self.assertEqual(legacy.count('\n        Pass\n'),2)
        for required in ('#pragma fragment Scatter','#pragma fragment Composite','Blend One One, Zero One'):
            self.assertIn(required,legacy)

    def test_full_depth_transmission_and_manual_reconstruction(self):
        code=(RUNTIME/'VolumetricLightingRenderer.Reconstruction.cs').read_text(encoding='utf-8')
        shader=(RUNTIME/'Resources/VolumetricReconstruction.shader').read_text(encoding='utf-8')
        self.assertIn('Bind(composite,false)',code)
        for token in ('range.y-range.x','ReconstructionDepth','LowFootprint','largest-smallest','return float4(sum,0)'):
            self.assertIn(token,shader)
        self.assertNotIn('SamplerState',shader)
        self.assertNotIn('tex2D(',shader)

    def test_input_isolation_and_owned_guide_lifetime(self):
        code=(RUNTIME/'VolumetricLightingRenderer.cs').read_text(encoding='utf-8')+(RUNTIME/'VolumetricLightingRenderer.Reconstruction.cs').read_text(encoding='utf-8')
        for token in ('Shader.GetGlobal','Shader.SetGlobal','FindObjectsOfType','ReadPixels','Time.time','File.Read'):
            self.assertNotIn(token,code)
        for token in ('ReducedTargetsCreated','t==lowScattering','t==depthRange','t==reconstructionMask','ReleaseReduced();','lowScattering=depthRange=reconstructionMask=null'):
            self.assertIn(token,code)


if __name__=='__main__':unittest.main()
