"""Public motion-blur ownership and default boundaries; real GPU fixtures verify behavior."""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
RUNTIME=ROOT/'packages/com.digital-kotone.toolkit/Runtime'

class MotionBlurContract(unittest.TestCase):
    def test_independent_current_input_no_private_or_application_dependency(self):
        source='\n'.join((RUNTIME/n).read_text(encoding='utf-8') for n in ('MotionBlurSettings.cs','MotionBlurRenderer.cs'))
        for forbidden in ('BundleCatalog','PhotoModeApp','File.Read','Shader.SetGlobal','FindObjectsOfType','Time.frameCount'):
            self.assertNotIn(forbidden,source)
        for field in ('sampleInterval','jitterDeltaUv','colorToGuideUv','noJitterFlags','ShutterAngle','Seconds','maximumSampleInterval'):
            self.assertIn(field,source)

    def test_default_off_owned_generation_and_non_aliasing(self):
        settings=(RUNTIME/'MotionBlurSettings.cs').read_text(encoding='utf-8')
        self.assertIn('public bool enabled;',settings)
        renderer=(RUNTIME/'MotionBlurRenderer.cs').read_text(encoding='utf-8')
        for token in ('generation','IsCurrent','Owns(source)','Owns(guide)','source==guide','DrawCalls=3','RenderTexture.active','!texture.useDynamicScale'):
            self.assertIn(token,renderer)
        self.assertIn('Guide', (RUNTIME/'SceneMotionBlurRenderer.cs').read_text(encoding='utf-8'))

    def test_visible_geometry_and_classification_not_previous_color(self):
        guide=(RUNTIME/'Resources/SceneMotionBlurGuide.shader').read_text(encoding='utf-8')
        for token in ('ZTest LEqual ZWrite Off','_Motion.Load','_Correspondence','_Excluded','_AlphaMap','_Cutoff'):
            self.assertIn(token,guide)
        adapter=(RUNTIME/'SceneMotionBlurRenderer.cs').read_text(encoding='utf-8')
        for token in ('time-previousTime','motion.Continuous','excludeMotionBlur','BuiltinRenderTextureType.CurrentActive','dejittered?-PreparedJitter'):
            self.assertIn(token,adapter)
        self.assertNotIn('previousColor',adapter)

    def test_integer_sampling_and_explicit_three_stage_algorithm(self):
        shader=(RUNTIME/'Resources/MotionBlur.shader').read_text(encoding='utf-8')
        for token in ('Properties { _MainTex','TILE_MAXIMUM','NEIGHBORHOOD_MAXIMUM','CURRENT_HDR_MOTION_RECONSTRUCTION','asuint(sample)','color.a','int2 candidate'):
            self.assertIn(token,shader)
        for token in ('tex2D(','.Sample(','_PreviousColor'):
            self.assertNotIn(token,shader)

    def test_short_exposure_is_guarded_opt_in_with_analytic_reference(self):
        settings=(RUNTIME/'MotionBlurSettings.cs').read_text(encoding='utf-8')
        shader=(RUNTIME/'Resources/MotionBlur.shader').read_text(encoding='utf-8')
        renderer=(RUNTIME/'MotionBlurRenderer.cs').read_text(encoding='utf-8')
        self.assertIn('public bool subpixelReconstruction;',settings)
        self.assertIn('if(settings.subpixelReconstruction)material.EnableKeyword',renderer)
        self.assertIn('else material.DisableKeyword',renderer)
        self.assertEqual(shader.count('#pragma multi_compile_local __ TOOLKIT_MOTION_BLUR_SUBPIXEL'),3)
        for token in ('float3 ShortExposure', 'other.w==1', 'abs(other.z-local.z)<=_Filter.y',
                      'UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_MainTex)',
                      'UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_MotionDepth)',
                      'length(other.xy-local.xy)<=.5', 'compatible?_MainTex.Load',
                      'origin=center+(int2)floor(offset);float2 f=frac(offset)',
                      'if(radius==0)return color', 'smoothstep(1,1.5,radius)'):
            self.assertIn(token,shader)
        fixture=(ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.DesktopMotionBlur.cs').read_text(encoding='utf-8')
        for token in ('Analytic integral', 'a*b/6', 'quadrature-converges', 'protected-center-exact', 'retains-integer-baseline', 'near-axis-analytic', 'near-axis-continuity'):
            self.assertIn(token,fixture)

    def test_post_order_and_failure_current_passthrough(self):
        post=(RUNTIME/'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('public SceneDeferredCamera sceneMotionBlurSource;',post)
        self.assertLess(post.index('ApplyDepthOfField(temporal, temporaries)'),post.index('TryResolveMotionBlur('))
        self.assertRegex(post, r'out var blurred\)\)\s*\{\s*postInput\s*=\s*blurred;\s*sceneMotionBlurResolved\s*=\s*true;\s*\}')
        self.assertIn('bool sceneMotionBlurResolved = false;',post)
        self.assertLess(post.index('TryResolveMotionBlur('),post.index('RenderTexture bloom ='))
        self.assertIn('sceneMotionBlurSource?.ResetMotionBlurHistory();',post)

    def test_coupled_exposure_reference_keeps_failures_and_separate_motion(self):
        fixture=(ROOT/'unity/Assets/Applications/PhotoStudio/SrpActorCharacterValidation.Exposure.cs').read_text(encoding='utf-8')
        for token in ('GAKUMAS_CHARACTER_EXPOSURE_COUPLED_POST', '"camera","animation","transparent"',
                      'FxResolution.Full', 'FxBlend.Alpha', 'transparent.localToWorld=Matrix4x4.TRS',
                      'referenceSamples=32,convergenceSamples=16', 'frames.Add(Render(',
                      'fx-positive-control', 'dof-positive-control', 'exposure-improves-unblurred',
                      'NOT physical aperture integration', 's.effects.geometry.surfaces=priorSurfaces'):
            self.assertIn(token,fixture)
        self.assertNotIn('blurError.mappedMse<=rawError.mappedMse',fixture)

if __name__=='__main__':unittest.main()
