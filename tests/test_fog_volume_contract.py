"""Public API/default isolation; real Player medium integration tests cover behavior."""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
RUNTIME=ROOT/'packages/com.digital-kotone.toolkit/Runtime'

class FogVolumeContract(unittest.TestCase):
    def test_independent_inputs_and_linear_color(self):
        text='\n'.join((RUNTIME/n).read_text(encoding='utf-8') for n in ('FogVolumeSettings.cs','FogVolumeBinding.cs','FogVolumeRenderer.cs'))
        for forbidden in ('Shader.SetGlobal','Shader.GetGlobal','FindObjectsOfType','File.Read','BundleCatalog','PhotoModeApp'):
            self.assertNotIn(forbidden,text)
        self.assertNotRegex(text,r'\.linear\b')
        for required in ('LinearEye','Device','linearColor','MaximumSpheres = 8','startDistance','endDistance'):
            self.assertIn(required,text)

    def test_owned_leases_failures_and_explicit_snapshot(self):
        renderer=(RUNTIME/'FogVolumeRenderer.cs').read_text(encoding='utf-8')
        for required in ('IsCurrent','_generation++','Owns(source)','source==depth.texture','ReleaseTargets','RenderTexture.active=saved','protection==depth.texture'):
            self.assertIn(required,renderer)
        binding=(RUNTIME/'FogVolumeBinding.cs').read_text(encoding='utf-8')
        self.assertIn('active.Sort(Compare)',binding)
        self.assertIn('GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)',binding)

    def test_shared_interval_medium_model_and_surface_depth(self):
        medium=(RUNTIME/'Resources/FogVolume.hlsl').read_text(encoding='utf-8')
        for required in ('FogIntegrate','events','chords','weighted+=optical','opticalDepth+=tau','asuint(x)','_FogControl.z<=0'):
            self.assertIn(required,medium)
        surface=(RUNTIME/'Resources/FogVolumeSurface.shader').read_text(encoding='utf-8')
        self.assertIn('FogIntegrate(start,i.world,false)',surface)
        self.assertIn('ZWrite Off ZTest LEqual',surface)
        self.assertIn('_LinearColor("Linear RGB and coverage",Vector)',surface)
        self.assertNotIn('_CameraDepthTexture',surface)

    def test_default_bridge_and_preserved_legacy_shader(self):
        settings=(RUNTIME/'FogVolumeSettings.cs').read_text(encoding='utf-8')
        self.assertIn('public bool enabled;',settings)
        post=(RUNTIME/'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('if(fogVolumes!=null&&fogVolumes.enabled)current=ApplyFogVolumes(current);',post)
        self.assertIn('current = ApplySceneDistanceFog(current, temporaries);',post)
        self.assertIn('current = ApplySphereFog(current, temporaries);',post)
        legacy=(RUNTIME/'Resources/OriginalStylePost.shader').read_text(encoding='utf-8')
        self.assertNotIn('FogVolume.hlsl',legacy)

if __name__=='__main__':unittest.main()
