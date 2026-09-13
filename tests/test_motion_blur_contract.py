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

    def test_post_order_and_failure_current_passthrough(self):
        post=(RUNTIME/'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('public SceneDeferredCamera sceneMotionBlurSource;',post)
        self.assertLess(post.index('ApplyDepthOfField(temporal, temporaries)'),post.index('TryResolveMotionBlur('))
        self.assertIn('out var blurred))postInput=blurred;',post)
        self.assertIn('sceneMotionBlurSource?.ResetMotionBlurHistory();',post)

if __name__=='__main__':unittest.main()
