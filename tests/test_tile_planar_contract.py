"""API/source guards; the standalone Player and native captures test actual GPU behavior."""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
RUNTIME=ROOT/'packages/com.digital-kotone.toolkit/Runtime'

class TilePlanarContractTests(unittest.TestCase):
    def source(self,name='SrpTilePlanarReflection.cs'):
        return (RUNTIME/name).read_text(encoding='utf-8')

    def test_explicit_srp_recording_without_recursive_camera(self):
        text=self.source()
        for value in ('public bool enabled;', '!scene.IsRecorded', 'scene.Camera!=Camera', 'sequence<=lastSequence', 'ReferenceEquals(source,scene)'):
            self.assertIn(value,text)
        for value in ('Camera.Render(', 'context.Submit(', 'SetViewProjectionMatrices(', 'GL.invertCulling=', 'GraphicsSettings.renderPipelineAsset='):
            self.assertNotIn(value,text)

    def test_camera_and_culling_are_scoped(self):
        text=self.source()
        for value in ('context.SetupCameraProperties(mirror)', 'context.SetupCameraProperties(Camera)', 'commands.SetInvertCulling(!s.hostInvertCulling)',
                      'commands.SetInvertCulling(s.hostInvertCulling)', 'finally', 'CalculateObliqueMatrix', 'ReflectionMatrix'):
            self.assertIn(value,text)

    def test_capture_coverage_replays_depth_stencil(self):
        text=self.source()
        for value in ('draw.coverageMaterial', 'draw.coverageShaderPass', 'commands.ClearRenderTarget(true,false,Color.clear)',
                      'commands.GenerateMips(capture)', 'GraphicsFormat.D32_SFloat_S8_UInt', '(long)x*y*8'):
            self.assertIn(value,text)

    def test_tile_projection_is_current_and_planar(self):
        shader=self.source('Resources/TilePlanarReflection.shader')
        for value in ('_TilePlanarDepth.Load', '_TilePlanarNormal.Load', '_TilePlanarMos.Load', 'fmod(identity-1,256)',
                      'dot(_TilePlanarPlane,world)', 'roughness*roughness', 'reflection.rgb/reflection.a'):
            self.assertIn(value,shader)

    def test_consumer_rejects_invalid_tickets(self):
        text=self.source('SrpTileReflection.cs')
        for value in ('planar.Value.Matches(scene,sequence)', 'planarSource.Value.Matches(source,sequence)', 'planar.Value.reflection',
                      'trace.GetFloat("_SsrPlanarAvailable")', '=> Record(context,scene,sequence,sceneRevision,null,out frame,out error)'):
            self.assertIn(value,text)

    def test_resolve_samples_same_perturbed_receiver_group(self):
        shader=self.source('Resources/TileReflection.shader')
        for value in ('Group(other)-Group(n.a)', '_TilePlanar.Load(int3(q,0))', 'lerp(radiance,planar.rgb,saturate(planar.a))'):
            self.assertIn(value,shader)
        for name in ('ScreenSpaceReflectionTrace.hlsl','ScreenSpaceReflectionFilter.hlsl'):
            self.assertIn('_SsrPlanarCoverage.Load',self.source('Resources/'+name))

    def test_real_gpu_controls_are_registered(self):
        fixture=(ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.TilePlanar.cs').read_text(encoding='utf-8')
        for value in ('analytic-mirrored-ray-depth-clipping','whole-visible-receiver-projection','tilted-translated-plane','gpu-camera-and-culling-restored',
                      'same-group-off-plane-occlusion','actor-stencil-negative-control','actor-stencil-cleared-replayed-every-frame','producer-disposal-invalidates-consumer'):
            self.assertIn(value,fixture)
        host=(ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('--self-test-tile-planar',host)
        self.assertIn('_owned.Clear();var fixture=VerifyTilePlanar(report)',host)

    def test_actual_character_validation_is_opt_in(self):
        text=(ROOT/'unity/Assets/Applications/PhotoStudio/PlanarCharacterValidation.cs').read_text(encoding='utf-8')
        for value in ('--validate-srp-planar-character','if(!_srp)','new SrpTilePlanarReflection','new SrpTileReflection',
                      'actual-srp-projection-priority','new[] { 0, 90, 180, 270 }','_app.EvaluateMotion(.7f)'):
            self.assertIn(value,text)

    def test_reference_shares_only_hardware_sampler(self):
        shader=(ROOT/'unity/Assets/Resources/TilePlanarSamplingReference.shader').read_text(encoding='utf-8')
        for value in ('_ReferenceQuery.Load','tex2Dlod(_ReferenceCapture,float4(query.xy,0,query.z))'):
            self.assertIn(value,shader)
        for value in ('_TilePlanarDepth','_TilePlanarOptions','_TilePlanarCaptureVP','reflection.rgb/reflection.a'):
            self.assertNotIn(value,shader)
        fixture=(ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.TilePlanar.cs').read_text(encoding='utf-8')
        for value in ('camera.WorldToViewportPoint(world-2*distance*n)','independent-query-negative-control','whole-visible-receiver-projection',
                      'floor-ssr-positive-control','real-ssr-hits-suppressed-by-planar','partial-planar-keeps-real-uncovered-ssr','floor-planar-outside-probe'):
            self.assertIn(value,fixture)
        self.assertNotIn('SystemInfo.graphicsUVStartsAtTop',fixture)

if __name__=='__main__':
    unittest.main()
