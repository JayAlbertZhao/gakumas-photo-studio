"""Source wiring checks. These do not replace standalone GPU/image validation."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ActorRenderingWiringTests(unittest.TestCase):
    def test_shared_surface_and_explicit_supplementary_passes(self):
        surface = (ROOT / 'unity/Assets/Resources/PhotoModeFallback.shader').read_text(encoding='utf-8')
        extra = (ROOT / 'unity/Assets/Resources/ActorSupplemental.shader').read_text(encoding='utf-8')
        self.assertIn('#include "ActorSurface.cginc"', surface)
        self.assertIn('#include "ActorSurface.cginc"', extra)
        self.assertIn('#include "ActorOutline.cginc"', extra)
        self.assertNotIn('Name "ACTOR_HAIR_COVER"', surface)
        self.assertIn('Ref 4 ReadMask 4 WriteMask 0 Comp Equal', extra)
        self.assertLess(extra.index('Name "ACTOR_OUTLINE"'), extra.index('Name "ACTOR_HAIR_COVER"'))

    def test_profile_parameters_have_runtime_consumers(self):
        app = (ROOT / 'unity/Assets/Scripts/PhotoModeApp.cs').read_text(encoding='utf-8')
        for parameter in ('matCapOffset', 'matCapSmoothScale', 'shadeApplyRatio',
                          'giScale', 'additiveLightScale', 'additiveLightSpecularScale', 'eyeHighlightColor'):
            self.assertIn('profile.' + parameter, app)
        shader = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        for uniform in ('_ActorMatcapParameters', '_ActorLightingScales', '_ActorRimColor',
                        '_ActorEyeHighlightColor', '_CapturedShadeAdditive'):
            self.assertGreater(len(re.findall(re.escape(uniform), shader)), 1)
        self.assertIn('input.uv1 * float2(0.5, 1.0)', shader)
        self.assertIn('input.layerUv + float2(0.5, 0.0)', shader)

    def test_hair_is_not_globally_transparent(self):
        repair = (ROOT / 'unity/Assets/Scripts/MaterialRepairer.cs').read_text(encoding='utf-8')
        self.assertIn('CopyFloat(source, target, "_SrcBlend", 1f)', repair)
        self.assertIn('CopyFloat(source, target, "_ZWrite", 1f)', repair)
        controls = (ROOT / 'unity/Assets/Scripts/ActorRenderControls.cs').read_text(encoding='utf-8')
        self.assertIn('material.GetFloat("_ShaderType") - 8f', controls)
        self.assertIn('CameraEvent.BeforeForwardAlpha', controls)
        self.assertIn('_camera.RemoveCommandBuffer', controls)
        self.assertIn('_commands.Release()', controls)

    def test_hair_coverage_consumes_authored_view_fade_mask(self):
        repair = (ROOT / 'unity/Assets/Scripts/MaterialRepairer.cs').read_text(encoding='utf-8')
        shader = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('source.GetVector("_FadeParam")', repair)
        self.assertIn('dot(v, _HeadDirection.xyz)', shader)
        self.assertIn('dot(v, _HeadUpDirection.xyz)', shader)
        self.assertIn('1.0 - saturate(rawBaseSample.a)', shader)
        self.assertIn('max(obliqueCoverage.x, obliqueCoverage.y)', shader)

    def test_probe_comparisons_reset_history_and_check_restoration(self):
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        pipeline = (ROOT / 'unity/Assets/Scripts/OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('photo-studio.actor-rendering-probes.v2', probe)
        self.assertIn('Capture("01b-front-repeat")', probe)
        self.assertLess(probe.index('_pipeline.ResetTemporalHistory()'),
                        probe.index('ScreenCapture.CaptureScreenshotAsTexture()'))
        for field in ('repeatChangedPixels', 'profileRestoreChangedPixels', 'lightRemovalChangedPixels'):
            self.assertIn('public int ' + field + ' = -1', probe)
            self.assertIn('_report.' + field + ' == 0', probe)
        self.assertRegex(pipeline, r'public void ResetTemporalHistory\(\)\s*\{\s*'
                                   r'_historyValid = false;\s*_historyFrame = -1;\s*\}')
        callers = [path.name for path in (ROOT / 'unity/Assets/Scripts').glob('*.cs')
                   if '.ResetTemporalHistory(' in path.read_text(encoding='utf-8')]
        self.assertEqual(callers, ['ActorRenderingValidation.cs'])

    def test_reference_rejects_missing_original_forward_pass(self):
        reference = (ROOT / 'unity/Assets/Scripts/ActorShaderReferenceFeature.cs').read_text(encoding='utf-8')
        self.assertIn('material.FindPass("Forward")', reference)
        self.assertIn('material.GetPassName(index)', reference)
        self.assertIn('if (!ValidateOriginalPasses())', reference)
        self.assertIn('if (!Application.isEditor) Application.Quit(3)', reference)
        self.assertIn('No original actor materials found; comparison rejected.', reference)
        self.assertLess(reference.index('if (!ValidateOriginalPasses())'),
                        reference.index('context.DrawRenderers('))

    def test_reference_build_restores_pipeline_and_antialiasing(self):
        builder = (ROOT / 'unity/Assets/Editor/Phase1Builder.cs').read_text(encoding='utf-8')
        build = builder[builder.index('private static void BuildPlayer(bool originalShaderReference)'):]
        self.assertLess(build.index('int previousAntiAliasing = QualitySettings.antiAliasing'),
                        build.index('QualitySettings.antiAliasing = 8'))
        cleanup = build[build.index('finally'):]
        for assignment in ('GraphicsSettings.renderPipelineAsset = previousGraphicsPipeline',
                           'QualitySettings.renderPipeline = previousQualityPipeline',
                           'QualitySettings.antiAliasing = previousAntiAliasing'):
            self.assertIn(assignment, cleanup)

    def test_stability_trace_is_opt_in_and_pose_fingerprint_is_read_only(self):
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        trace = probe[probe.index('"--trace-rendering-stability"'):]
        self.assertLess(trace.index('RequestPostInputDump'), trace.index('WaitForEndOfFrame'))
        for frame in ('01-front', '01b-front-repeat', '11-profile-restored'):
            self.assertIn('name == "' + frame + '"', trace[:trace.index('RequestPostInputDump')])
        digest = probe[probe.index('private static string ActorPoseDigest()'):]
        self.assertIn('SHA256.Create()', digest)
        self.assertIn('value.localToWorldMatrix', digest)
        self.assertIn('skin.GetBlendShapeWeight(i)', digest)
        self.assertIn('OriginalStyleRenderPipeline.ActorLayer', digest)
        self.assertNotRegex(digest, r'\.(?:position|rotation|localScale)\s*=|SetBlendShapeWeight|SetPositionAndRotation')

    def test_captured_geometry_refreshes_supplementary_renderers(self):
        app = (ROOT / 'unity/Assets/Scripts/PhotoModeApp.cs').read_text(encoding='utf-8')
        pose = app[app.index('private void ApplyCapturedPosedGeometry()'):]
        pose = pose[:pose.index('Exact captured posed geometry + tangent/color attributes applied')]
        self.assertIn('_actorRenderControls.RefreshRenderers()', pose)
        self.assertLess(pose.index('renderer.enabled = false'), pose.index('_actorRenderControls.RefreshRenderers()'))
        capture = app[app.index('private IEnumerator CaptureGpaCameraAndQuit()'):]
        capture = capture[:capture.index('private IEnumerator CaptureGpaCameraSweepAndQuit()')]
        self.assertIn('captureArgs.Contains("--capture-actor-rendering-pass")', capture)
        self.assertIn('if (!RenderDocCaptureBridge.TriggerCapture()) { Application.Quit(2); yield break; }', capture)
        self.assertIn('GPA-camera submitted passes: outline=', capture)

    def test_head_triangle_and_common_ramp_share_offset(self):
        shader = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        head = shader[shader.index('float headSurfaceRamp ='):shader.index('float type9Ramp =')]
        self.assertIn('0.5 * _ActorMatcapParameters.x', head)
        self.assertNotIn('- 0.15', head)

    def test_asset_free_gpu_probe_precedes_asset_loading(self):
        app = (ROOT / 'unity/Assets/Scripts/PhotoModeApp.cs').read_text(encoding='utf-8')
        self.assertLess(app.index('ActorRenderingSelfTest.TryStart(gameObject)'),
                        app.index('Initialize(BundleCatalog.DefaultStagingRoot)'))
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('new Mesh', probe)
        self.assertIn('new Texture2D(256, 1', probe)
        self.assertIn('RenderTextureFormat.ARGBFloat', probe)
        self.assertIn('_camera.Render()', probe)
        self.assertIn('nonconstant-ramp-positive-control', probe)
        self.assertIn('Application.Quit(report.accepted ? 0 : 2)', probe)
        self.assertNotIn('AssetBundle', probe)
        self.assertNotIn('BundleCatalog', probe)


if __name__ == '__main__':
    unittest.main()
