"""Static boundary guards; GPU/native acceptance is a separate requirement."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class SrpActorContractTests(unittest.TestCase):
    def test_all_full_surface_uniforms_are_material_or_explicit_inputs(self):
        shader = (RUNTIME / 'Resources/PhotoModeFallback.shader').read_text(encoding='utf-8')
        properties = set(re.findall(r'^\s*(?:\[[^\]]+\]\s*)*(_\w+)\s*\(', shader, re.M))
        surface = (RUNTIME / 'Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        header = surface.split('float3 TransformCapturedCubeDirection')[0]
        uniforms = set()
        for declaration in re.findall(r'^(?:float(?:[234](?:x[234])?)?|int|sampler2D|samplerCUBE)\s+([^;{}()]+);', header, re.M):
            uniforms.update(re.findall(r'_\w+', declaration))
        uniforms.update(re.findall(r'UNITY_DECLARE_TEX2DARRAY(?:_NOSAMPLER)?\((_\w+)\)', header))
        inputs = (RUNTIME / 'ActorForwardParameters.cs').read_text(encoding='utf-8')
        declared = set(re.findall(r'_\w+', inputs))
        # Unity derives this from the material's main texture transform.
        self.assertEqual(uniforms - properties - declared - {'_MainTex_ST'}, set())
        self.assertGreater(len(uniforms - properties), 70)

    def test_full_passes_and_established_supplemental_order(self):
        source = (RUNTIME / 'ActorForwardDrawSet.cs').read_text(encoding='utf-8')
        for name in ('PhotoModeFallback', 'ActorSupplemental', 'ACTOR_FORWARD_HDR', 'ACTOR_OUTLINE', 'ACTOR_HAIR_COVER'):
            self.assertIn('"' + name + '"', source)
        self.assertNotIn('ActorPlanarCapture', source)
        ordered = ['if(d.queue<=2500)', 'AddRange(bodyOutlines)', 'AddRange(covers)', 'AddRange(hairOutlines)', 'if(d.queue>2500)']
        self.assertEqual(sorted(source.index(s) for s in ordered), [source.index(s) for s in ordered])
        self.assertIn('block.HasProperty("_ShaderType")', source)
        self.assertIn('drawCount>settings.maximumDraws', source)
        self.assertIn('Actor configuration changed fixed input', source)
        self.assertIn('Invoke(renderer,submesh,material)', source)

    def test_scene_raster_depth_store_is_explicit_and_lifetime_checked(self):
        source = (RUNTIME / 'TileSceneRenderer.cs').read_text(encoding='utf-8')
        self.assertIn('public RenderTexture depthStencil;', source)
        self.assertIn('target=settings.depthStencil,store=settings.depthStencil!=null', source)
        self.assertIn('!_storedDepth || (DepthStencil!=null && DepthStencil.IsCreated())', source)
        self.assertIn('s.depthStencil.graphicsFormat!=GraphicsFormat.None', source)
        self.assertIn('decalCount>0||s.depthStencil!=null', source)
        consumer = (RUNTIME / 'SrpActorForward.cs').read_text(encoding='utf-8')
        self.assertIn('scene.DepthStencil,RenderTextureSubElement.Depth', consumer)
        self.assertNotIn('scene.EyeDepth', consumer)

    def test_explicit_srp_lifetime_and_source_ticket(self):
        source = (RUNTIME / 'SrpActorForward.cs').read_text(encoding='utf-8')
        self.assertIn('public bool enabled;', source)
        self.assertIn('ReferenceEquals(source,scene)', source)
        self.assertIn('reflected.Value.Matches(scene,value)', source)
        self.assertIn('draws.sampled.Contains(color)', source)
        self.assertIn('RenderTextureSubElement.Depth', source)
        self.assertIn('value.depthStencilFormat==depth', source)
        self.assertIn('(long)w*h*20', source)
        for forbidden in ('Camera.Render(', '.Submit(', 'FindObjectsOfType<', 'Shader.SetGlobal', 'renderPipelineAsset='):
            self.assertNotIn(forbidden, source)

    def test_shader_depth_view_load_has_explicit_full_precision(self):
        shader = (RUNTIME / 'Resources/ActorForwardDepth.shader').read_text(encoding='utf-8')
        for name in ('_ActorSourceColor', '_ActorSourceHardwareDepth', '_ActorHardwareDepth'):
            self.assertIn('UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(' + name + ')', shader)
        self.assertIn('float depth:SV_Depth', shader)
        self.assertIn('UNITY_REVERSED_Z', shader)
        self.assertIn('ClipXY(i.uv)', shader)
        self.assertIn('o.depth=_ActorSourceHardwareDepth.Load(int3(p,0)).r', shader)
        self.assertNotIn('_ActorSourceDepth', shader)

    def test_parameters_do_not_implicitly_capture_or_write_globals(self):
        source = (RUNTIME / 'ActorForwardParameters.cs').read_text(encoding='utf-8')
        self.assertIn('public static ActorForwardParameters CaptureCurrentGlobals()', source)
        constructor = source.split('public ActorForwardParameters()')[1].split('public void SetFloat')[0]
        self.assertNotIn('Shader.GetGlobal', constructor)
        self.assertNotIn('Shader.SetGlobal', source)
        self.assertIn('values.Length>8', source)
        self.assertIn('SetAmbientProbe(SphericalHarmonicsL2 probe)', source)

    def test_runtime_fixture_has_independent_depth_and_ordinary_forward_control(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.SrpActor.cs').read_text(encoding='utf-8')
        self.assertIn('camera.ViewportPointToRay', fixture)
        self.assertIn('referenceCamera.Render()', fixture)
        self.assertIn('new[]{0,1,2,3,4,5,6,8,9}', fixture)
        self.assertIn('parameters.SetFloat("_FaceDebugMode",0)', fixture)
        self.assertIn('preserves-scene-inputs', fixture)
        self.assertIn('ordinary-forward-whole-color', fixture)

    def test_layered_fixture_keeps_real_depth_order_and_negative_controls(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.SrpActorLayers.cs').read_text(encoding='utf-8')
        for token in ('fully-faded-cover-owns-depth', 'hair-cover-depth-positive-control',
                      'transparent-authored-queue', 'submesh-block-replaces-renderer-block',
                      'authored-cutout', 'actor-tint-and-dither-fade', 'explicit-point-and-spot',
                      'eye-environment-cubemap', 'coplanar-near-', 'coplanar-orthographic',
                      'ordinary-forward-whole-depth', 'native-reference-capture'):
            self.assertIn(token, fixture)
        self.assertIn('referenceCamera.Render()', fixture)
        self.assertNotIn('draws.draws', fixture)

    def test_preparation_structural_lifetime_and_runtime_rejections(self):
        source = (RUNTIME / 'ActorForwardDrawSet.cs').read_text(encoding='utf-8')
        for token in ('Camera.cullingMask!=cullingMask', 'Camera.targetTexture!=target',
                      'Camera.orthographic!=orthographic', 'state.IsCurrent',
                      'current==mesh', 'r.localToWorldMatrix.Equals(transform)',
                      'inputs changed during preparation'):
            self.assertIn(token, source)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.SrpActorGuards.cs').read_text(encoding='utf-8')
        for token in ('budget-before-clones', 'callback-zwrite', 'callback-transform',
                      'excluded-renderer-reactivation-invalidates', 'mesh-replacement',
                      'parameter-reject-', 'array-inputs-copied-and-padded',
                      'color-feedback', 'property-block-depth-feedback',
                      'record-after-rejections-and-global-change',
                      'released-scene-raster-depth-retires-output',
                      'compositor-dispose-preserves-borrowed-targets'):
            self.assertIn(token, fixture)

    def test_real_reflection_history_has_pollution_negative_control(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.SrpActorReflection.cs').read_text(encoding='utf-8')
        for token in ('new SrpTileReflection(', 'sequence,reflected,out current',
                      'independent-after-actor-history-exact', 'real-history-ray-hits',
                      'all-scene-exports-preserved', 'reject-different-scene-same-sequence',
                      'reject-same-scene-different-sequence',
                      'deliberate-actor-history-pollution-positive-control',
                      'reflection-history-reset-retires-actor-ticket'):
            self.assertIn(token, fixture)
        self.assertLess(fixture.index('actor.TryRecord('), fixture.index('control.TryRecord('))

    def test_character_validation_is_opt_in_and_uses_full_forward_controls(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/SrpActorCharacterValidation.cs').read_text(encoding='utf-8')
        for token in ('--validate-srp-actor-character', 'ActorForwardParameters.CaptureCurrentGlobals()',
                      'ActorForwardDrawSet.TryPrepare(', 'referenceCamera.Render()',
                      'new[]{0,90,180,270}', 'app.EvaluateMotion(.7f)',
                      'ordinary-forward-whole-color', 'ordinary-forward-whole-depth',
                      'source-materials-not-mutated', 'authored-material-details-positive-control',
                      'LightProbes.GetInterpolatedProbe', 'explicit-renderer-ambient'):
            self.assertIn(token, fixture)
        self.assertNotIn('ActorPlanarCapture', fixture)
        self.assertNotIn('draws.draws', fixture)
        self.assertIn('if(index<0)return false', fixture)

    def test_explicit_probe_avoids_engine_owned_constant_buffer(self):
        inputs = (RUNTIME / 'ActorForwardParameters.cs').read_text(encoding='utf-8')
        surface = (RUNTIME / 'Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.SrpActorLayers.cs').read_text(encoding='utf-8')
        self.assertIn('BindAmbientProbe(Material material,SphericalHarmonicsL2 probe)', inputs)
        self.assertIn('CopySHCoefficientArraysFrom(new[]{probe})', inputs)
        self.assertIn('material.SetVector("_ActorForwardSH"+i,packed[i])', inputs)
        self.assertIn('ActorForwardAmbientSH(n) : max(ShadeSH9(float4(n, 1.0)), 0.0)', surface)
        for token in ('renderer-sh-engine-oracle', 'renderer-sh-positive-control',
                      'LightProbeUsage.CustomProvided', 'renderer-sh-oblique-',
                      'if(ordinaryProbe)materials[r].SetFloat("_UseActorForwardAmbientSH",0)'):
            self.assertIn(token, fixture)

    def test_full_suite_appends_actor_after_existing_planar_fixture(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertGreater(fixture.rindex('var fixture=VerifySrpActor(report)'), fixture.rindex('var fixture=VerifyTilePlanar(report)'))


if __name__ == '__main__':
    unittest.main()
