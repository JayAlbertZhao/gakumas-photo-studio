"""Static public contracts; actual mixed-image/native checks are separate acceptance gates."""
from pathlib import Path
import unittest

RUNTIME = Path(__file__).resolve().parents[1] / 'packages/com.digital-kotone.toolkit/Runtime'


class HeavyFxContract(unittest.TestCase):
    def test_geometry_exposure_is_opt_in_and_tracks_actual_gpu_endpoints(self):
        code=(RUNTIME/'FxGeometryExposure.cs').read_text(encoding='utf-8')
        self.assertIn('public bool enabled;',code)
        for token in ('commands.DrawRenderer','commands.DrawMesh','mesh.GetIndices','s.motionRevision',
                      'time-previousTime','maximumTrackedVertices','remainingBytes','ResetHistory()',
                      'camera==previousCamera','SameProjection(projection,previousProjection)','oldVertices=e.newVertices'):
            self.assertIn(token,code)
        for token in ('BakeMesh(', 'ReadPixels(', 'Time.time', 'FindObjectsOfType'):
            self.assertNotIn(token,code)

    def test_coherent_layer_compositing_precedes_shutter_average(self):
        code=(RUNTIME/'HeavyFxRenderer.cs').read_text(encoding='utf-8')
        start=code.index('for(int sample=0;sample<ExposureSamples;sample++)')
        self.assertLess(start,code.index('foreach(var batch in batches)',start))
        self.assertLess(code.index('current=next;',start),code.index('Graphics.Blit(current,exposureSum',start))
        for token in ('exposure.Capture()', 'exposure.Bind(material,s,exposurePhase)', 'exposure.Complete()',
                      'exposure.TextureBytes', 'exposure.Dispose()', 't==exposureSum', 'exposure.ResetHistory()'):
            self.assertIn(token,code)
        shader=(RUNTIME/'Resources/FxExposure.shader').read_text(encoding='utf-8')
        for token in ('Blend One One ColorMask RGB', '_FxExposureSource.Load(p).a',
                      'if(!isfinite(protection)||protection>0)return _FxExposureSource.Load(p)',
                      'ACTUAL_FX_VERTEX_ENDPOINT','COHERENT_FX_SHUTTER_ACCUMULATION'):
            self.assertIn(token,shader)

    def test_geometry_shutter_does_not_substitute_a_composite_pixel_velocity(self):
        source=(RUNTIME/'Resources/LowResolutionFxShared.hlsl').read_text(encoding='utf-8')
        for token in ('FX_GEOMETRY_EXPOSURE', '_FxExposurePreviousVertices.Load',
                      'o.pos+=(o.pos-oldClip)*phase', 'o.world+=(world.xyz-old.xyz)*phase', 'eye=input.eye'):
            self.assertIn(token,source)
        host=(RUNTIME/'DesktopFrameRenderer.cs').read_text(encoding='utf-8')
        self.assertIn('else effects.ResetMotionHistory();',host)
        self.assertIn('motionBlur.ResetHistory();effects.ResetMotionHistory();',host)

    def test_shared_work_not_a_chain_of_hdr_renderers(self):
        code = (RUNTIME / 'HeavyFxRenderer.cs').read_text(encoding='utf-8')
        for name in ('VolumetricLightingRenderer', 'LowResolutionFxRenderer()'):
            self.assertNotIn(name, code)
        for name in ('DrawBatch(batch,targets.effect,1,targets)', 'optics.DrawShared', 'last.resolution!=s.resolution', 'batch.distortion?5:4'):
            self.assertIn(name, code)
        self.assertNotIn('.Sort(', code)

    def test_explicit_inputs_and_current_ownership(self):
        code = (RUNTIME / 'HeavyFxRenderer.cs').read_text(encoding='utf-8')
        for name in ('Shader.GetGlobal', 'FindObjectsOfType', 'ReadPixels', 'Time.time', 'File.Read'):
            self.assertNotIn(name, code)
        for name in ('double timeSeconds', 'Owns(source)', 'generation++', 'owner.Created', 'needsOptics&&!optics.SharedCreated', 'maximumTargetMiB'):
            self.assertIn(name, code)

    def test_optical_producer_does_not_allocate_private_hdr(self):
        code = (RUNTIME / 'LensFlareRenderer.Shared.cs').read_text(encoding='utf-8')
        self.assertNotIn('artifacts=Allocate', code)
        self.assertNotIn('color=Allocate', code)
        self.assertIn('commands.SetRenderTarget(target)', code)
        self.assertIn('commands.DrawProcedural', code)

    def test_one_current_mask_is_shared_by_resolve_and_replay(self):
        code = (RUNTIME / 'HeavyFxRenderer.cs').read_text(encoding='utf-8')
        self.assertLess(code.index('DrawBatch(batch,targets.effect,1,targets)'), code.index('Graphics.Blit(current,repair,resolve,12)'))
        self.assertLess(code.index('Graphics.Blit(current,repair,resolve,12)'), code.index('Graphics.Blit(current,next,resolve,batch.distortion?5:4)'))
        for name in ('repairMask=value.repair', 'repair==null||!repair.IsCreated()', 't==repair', 'DestroyTarget(repair)', 'source.width*source.height*33'):
            self.assertIn(name, code)
        shader = (RUNTIME / 'Resources/LowResolutionFxShared.hlsl').read_text(encoding='utf-8')
        self.assertIn('return _FxRepair.Load(int3((int2)pixel,0))>.5', shader)

    def test_surface_depth_transport_and_whole_batch_replay(self):
        code = (RUNTIME / 'Resources/HeavyFx.shader').read_text(encoding='utf-8')
        for name in ('HeavyTransmission', 'VolumeScatterRay', 'HeavySurfaceLight', 'HeavyAdditionalSurface', 'FxNeedsRepair(fullPixel,false)'):
            self.assertIn(name, code)
        self.assertIn('DrawBatch(batch,next,2,targets)', (RUNTIME / 'HeavyFxRenderer.cs').read_text(encoding='utf-8'))

    def test_opt_in_exclusive_bridge_preserves_post_shader(self):
        settings = (RUNTIME / 'HeavyFxSettings.cs').read_text(encoding='utf-8')
        self.assertIn('public bool enabled;', settings)
        code = (RUNTIME / 'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertLess(code.index('current=ApplyHeavyFx(current)'), code.index('RenderTexture temporal;'))
        for name in ('heavyFxDepthProvider==null', 'disable independent FX/fog bridges', '_heavyFx?.Dispose();_heavyFx=null;'):
            self.assertIn(name, code)
        self.assertNotIn('HeavyFx', (RUNTIME / 'Resources/OriginalStylePost.shader').read_text(encoding='utf-8'))

    def test_shared_hlsl_keeps_old_and_joint_branches_explicit(self):
        for name in ('LowResolutionFx', 'LensFlare'):
            self.assertIn('#include "'+name+'Shared.hlsl"', (RUNTIME / ('Resources/'+name+'.shader')).read_text(encoding='utf-8'))
        grid = (RUNTIME / 'Resources/LowResolutionFxShared.hlsl').read_text(encoding='utf-8')
        self.assertIn('defined(HEAVY_FX_GRID)', grid)
        self.assertIn('defined(HEAVY_FX_LIGHTING)', grid)

    def test_float_effect_samples_keep_explicit_vulkan_precision(self):
        # Native Vulkan evidence found float32 effect storage followed by a
        # RelaxedPrecision fetch. Typed comma declarations did not protect it.
        grid = (RUNTIME / 'Resources/LowResolutionFxShared.hlsl').read_text(encoding='utf-8')
        for name in ('_MainTex', '_FxEffect', '_FxBackground', '_FxTexture'):
            self.assertIn('UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(' + name + ');', grid)
        self.assertNotIn('Texture2D<float4> _MainTex,', grid)


if __name__ == '__main__':
    unittest.main()
