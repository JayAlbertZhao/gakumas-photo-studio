"""Source wiring checks. These do not replace standalone GPU/image validation."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ActorRenderingWiringTests(unittest.TestCase):
    def test_outline_depth_nibble_is_integer_not_normalized(self):
        outline = (ROOT / 'unity/Assets/Resources/ActorOutline.cginc').read_text(encoding='utf-8')
        self.assertIn('float depthOffset = floor(bytes.b / 16.0) * packed;', outline)
        self.assertNotIn('float depthOffset = high.b * packed;', outline)
        self.assertIn('output.position.z -= depthOffset * (0.001 / 15.0);', outline)
        self.assertIn('output.position.z += depthOffset * (0.001 / 15.0);', outline)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('outline-packed-depth-', probe)
        self.assertIn('new[] { 0, 1, 8, 15 }', probe)
        self.assertIn('Mathf.Max(nibble, 1) * unitDistance * fraction', probe)

    def test_outline_respects_material_depth_write_and_draw_order(self):
        extra = (ROOT / 'unity/Assets/Resources/ActorSupplemental.shader').read_text(encoding='utf-8')
        outline, hair = extra.split('Name "ACTOR_HAIR_COVER"', 1)
        outline = outline.split('Name "ACTOR_OUTLINE"', 1)[1]
        self.assertIn('ZWrite [_ZWrite]', outline)
        self.assertIn('Cull Front', outline)
        self.assertIn('ZTest LEqual', outline)
        self.assertNotIn('ZWrite Off', outline)
        self.assertIn('ZWrite Off', hair)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for name in ('VerifyOutlineDepth(report);', 'outline-depth-near-then-far',
                     'outline-depth-far-then-near', 'outline-depth-optout-near-then-far',
                     'outline-depth-blocks-late-surface-behind',
                     'outline-depth-optout-allows-late-surface-behind',
                     'outline-depth-allows-late-surface-in-front',
                     'outline-depth-actual-commands-submitted'):
            self.assertIn(name, probe)

    def test_main_specular_preserves_receiver_basis_in_both_light_modes(self):
        surface = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float3 halfVector = l + float3(0.0, 0.0, 1.0);', surface)
        self.assertNotIn('receiverNormal = lerp(receiverNormal, n,', surface)
        self.assertIn('saturate(dot(receiverNormal, l))', surface)
        # Additional lights remain world-space, unlike the authored main light.
        self.assertIn('float3 additionalHalf = direction + v;', surface)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for name in ('VerifyMainSpecularBasis(report);', 'main-spec-nonconstant-response',
                     'main-spec-material-', 'main-spec-zero-mask-', 'main-spec-no-strand-lobe-'):
            self.assertIn(name, probe)
        self.assertIn('Quaternion.Euler(12, 20, -15)', probe)
        self.assertIn('origin-direction*(origin.z/direction.z)', probe)

    def test_hair_highlight_keeps_camera_receiver_under_world_light(self):
        surface = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float3 hairHalfVector = l + float3(0.0, 0.0, 1.0);', surface)
        self.assertIn('float3 hairReceiver = lerp(n, capturedReceiverNormal, saturate(_UseCapturedReceiverNormal));', surface)
        self.assertIn('dot(hairReceiver, hairHalf)', surface)
        self.assertNotIn('float hairResponse = pow(saturate(dot(receiverNormal, h))', surface)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyHairHighlightBasis(report);', probe)
        self.assertIn('hair-basis-nonconstant-highlight', probe)
        self.assertIn('hair-basis-zero-mask-', probe)
        self.assertIn('hair-basis-nonhair-', probe)
        validation = (ROOT / 'unity/Assets/Scripts/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        for name in ('10-world-light-orbit', '10a-world-light-side', '10b-world-light-elevated'):
            self.assertIn('Capture("' + name + '", expectedWorldSpace: true)', validation)
        self.assertIn('Light-space probe input was overwritten:', validation)

    def test_shadow_offset_uses_layered_definition_not_depth_comparison(self):
        surface = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float authoredOffset = max(definitionRed * 2.0 - 1.0, 0.0);', surface)
        self.assertIn('authoredOffset * saturate(_CapturedActorShadowUseOffset) + filtered', surface)
        call = 'CapturedActorShadow(input.worldPosition, definition.r)'
        self.assertIn(call, surface)
        self.assertLess(surface.index('definition = lerp(definition, layerDefinition, layerMask);'), surface.index(call))
        self.assertNotIn('centre * saturate(_CapturedActorShadowUseOffset)', surface)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('shadow-definition-offset-', probe)
        self.assertIn('new Vector3(0.75f, 0.5f, 0.5f)', probe)
        self.assertIn('new[] { 0, 8, 9 }', probe)

    def test_shadow_filter_preserves_subtexel_phase_and_strict_comparison(self):
        surface = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float2 fraction = frac(grid);', surface)
        self.assertIn('if (_UseCapturedActorShadow <= 0.0) return 1.0;', surface)
        self.assertIn('(floor(grid) + 0.5) * texel', surface)
        self.assertIn('float3(1.0 - fraction.x, 1.0, fraction.x) * 0.5', surface)
        self.assertIn('float3(1.0 - fraction.y, 1.0, fraction.y) * 0.5', surface)
        self.assertIn('receiverDepth > mapDepth ? 1.0 : 0.0', surface)
        self.assertIn('receiverDepth < mapDepth ? 1.0 : 0.0', surface)
        self.assertNotIn('return step(mapDepth, receiverDepth);', surface)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyShadowSubtexelFiltering(report);', probe)
        self.assertIn('ShadowBilinearOracle(depth, uv +', probe)
        self.assertIn('shadow-subtexel-bilinear-', probe)

    def test_additional_lights_modulate_key_colored_shading(self):
        surface = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float3 mainLighting = capturedBrdf * (_ActorKeyColor.rgb * _CapturedLightColor.rgb);', surface)
        self.assertGreater(surface.index('float3 mainLighting ='), surface.index('for (int lightIndex'))
        self.assertIn('(mainLighting + additionalSpecular)', surface)
        self.assertIn('distribution * environmentBrdf * definitionVisibility', surface)
        self.assertIn('saturate(angularWeight + shadeStrength)', surface)
        self.assertNotIn('float radiance = saturate(dot(n, direction)) * attenuation;', surface)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyAdditionalLighting(report);', probe)
        for case in ('additional-two-lights-add-once', 'additional-shade-floor-',
                     'additional-outside-range', 'additional-spot-outside',
                     'additional-spec-normal-', 'additional-spec-scale-zero'):
            self.assertIn(case, probe)

    def test_fullbody_additional_lights_have_input_and_restoration_checks(self):
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        self.assertIn('Capture("13b-fullbody-point", expectedLights: 2)', probe)
        self.assertIn('Capture("13c-fullbody-spot", expectedLights: 2)', probe)
        self.assertIn('Capture("13d-fullbody-tinted-spot", expectedLights: 2)', probe)
        self.assertIn('expectedLights.HasValue && _controls.AdditionalLightCount != expectedLights.Value', probe)
        self.assertIn('_report.additionalRestoreChangedPixels == 0', probe)
        self.assertIn('_report.additionalRestoreChangedPixels = changed', probe)

    def test_sky_light_uses_material_diffuse_independent_of_direct_scale(self):
        surface = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float dielectricDiffuse = 0.96 * (1.0 - metallic);', surface)
        self.assertIn('float3 ambientDiffuse = diffuse * (isEye ? 0.96 : dielectricDiffuse);', surface)
        self.assertIn('ambientDiffuse * ambient * _ActorLightingScales.x', surface)
        self.assertNotIn('lit = diffuse * ambient', surface)
        self.assertNotIn('lit = directDiffuse * ambient', surface)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyAmbientMaterialResponse(report);', probe)
        self.assertIn('new[] { 0, 1, 4, 9 }', probe)
        self.assertIn('new[] { 0f, 2f }', probe)
        self.assertIn('ambient-scale-restored', probe)

    def test_full_body_sky_probe_checks_input_and_strict_restoration(self):
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        self.assertIn('Capture("15a-fullbody-noambient", expectedGi: 0f)', probe)
        self.assertIn('Capture("15b-fullbody-ambient", expectedGi: 0.5f)', probe)
        self.assertIn('Capture("15c-fullbody-restored", expectedGi: 0f)', probe)
        self.assertIn('expectedGi.HasValue && Shader.GetGlobalVector("_ActorLightingScales").x != expectedGi.Value', probe)
        self.assertIn('_report.ambientRestoreChangedPixels == 0', probe)
        self.assertIn('_report.ambientRestoreChangedPixels = changed', probe)

    def test_ramp_add_keeps_diffuse_and_specular_alpha_roles_separate(self):
        surface = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float3 authoredRampAddRgb = rampAddColor * (1.0 - authoredRampAdd.a);', surface)
        self.assertIn('baseSample.rgb += authoredRampAddRgb;', surface)
        self.assertIn('shadeSample.rgb += authoredRampAddRgb;', surface)
        self.assertIn('1.0.xxx, rampAddColor, saturate(authoredRampAdd.a)', surface)
        self.assertNotIn('1.0.xxx, authoredRampAddRgb, saturate(authoredRampAdd.a)', surface)
        self.assertIn('_CapturedType1Variant < 1.5', surface)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyRampAddSpecular(report);', probe)
        self.assertIn('QualitySettings.activeColorSpace == ColorSpace.Linear ? tint.linear : tint', probe)
        self.assertIn('new[] { 0f, 0.5f, 1f }', probe)
        self.assertIn('types[i] != 1 || variants[i] != 2', probe)

    def test_hair_strands_and_accessories_have_separate_specular_regions(self):
        surface = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('(input.uv.x > 0.75 && input.uv.y > 0.75)', surface)
        self.assertNotIn('step(0.75, input.uv.x)', surface)
        self.assertIn('(1.0 - hairAccessoryMask)', surface)
        gate = 'if (isHair) definitionVisibility *= hairAccessoryMask;'
        self.assertIn(gate, surface)
        self.assertLess(surface.index('hairHighlightWeight);'), surface.index(gate))
        self.assertLess(surface.index(gate), surface.index('float specularVisibility ='))
        self.assertIn('distribution * environmentBrdf * definitionVisibility', surface)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyHairSpecularRegions(report);', probe)
        self.assertIn('new Vector2(0.75f, 0.9f)', probe)
        self.assertIn('new Vector2(0.9f, 0.75f)', probe)
        self.assertIn('hair-spec-strands-additional', probe)

    def test_skin_saturation_uses_authored_mask_not_material_id(self):
        surface = (ROOT / 'unity/Assets/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('_CapturedSkinSaturation * saturate(shadeSample.a)', surface)
        self.assertNotIn('isSkin && abs(_CapturedSkinSaturation)', surface)
        self.assertIn('float3(0.2126729, 0.7151522, 0.0721750)', surface)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifySkinSaturation(report);', probe)
        self.assertIn('foreach (int type in new[] { 0, 1, 9 })', probe)
        self.assertIn('foreach (float mask in new[] { 0f, 0.25f, 1f })', probe)
        self.assertIn('Color.LerpUnclamped(gray, baseline, 1f + delta * mask)', probe)

    def test_head_triangle_uses_reflected_basis_once(self):
        driver = (ROOT / 'unity/Assets/Scripts/ActorHeadLightingDriver.cs').read_text(encoding='utf-8')
        reference = (ROOT / 'unity/Assets/Scripts/ActorShaderReferenceFeature.cs').read_text(encoding='utf-8')
        self.assertIn('Vector3 right = -_head.right.normalized;', driver)
        self.assertIn('head.SetColumn(0, right)', reference)
        self.assertNotIn('head.SetColumn(0, -right)', reference)
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('head.AddComponent<ActorHeadLightingDriver>()', probe)
        for name in ('head-reflection-basis-', 'head-triangle-reflects-dark-side',
                     'head-triangle-preserves-zero-mask'):
            self.assertIn(name, probe)
        validation = (ROOT / 'unity/Assets/Scripts/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        for name in ('09a-world-light-left', '09b-world-light-right'):
            self.assertIn('Capture("' + name + '", expectedWorldSpace: true)', validation)

    def test_quality_respects_per_texture_sampling(self):
        app = (ROOT / 'unity/Assets/Scripts/PhotoModeApp.cs').read_text(encoding='utf-8')
        self.assertIn('QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;', app)
        self.assertNotRegex(app, r'QualitySettings\.anisotropicFiltering\s*=\s*AnisotropicFiltering\.ForceEnable')
        repair = (ROOT / 'unity/Assets/Scripts/MaterialRepairer.cs').read_text(encoding='utf-8')
        self.assertIn('texture.filterMode, texture.wrapModeU, texture.wrapModeV', repair)
        self.assertIn('texture.mipMapBias, texture.anisoLevel', repair)

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

    def test_hair_cover_selftest_uses_actual_composition_and_records_expression(self):
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyHairCoverComposition(report);', probe)
        self.assertIn('_camera.gameObject.AddComponent<ActorRenderControls>()', probe)
        for name in ('hair-cover-disabled-retains-eye', 'hair-cover-enabled-oblique-restored',
                     'hair-cover-actual-command-submitted', 'hair-cover-respects-nearer-depth', '-outside-stencil'):
            self.assertIn(name, probe)
        validation = (ROOT / 'unity/Assets/Scripts/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        self.assertIn('expression = FindObjectOfType<PhotoModeApp>().CurrentExpression', validation)

    def test_probe_comparisons_reset_history_and_check_restoration(self):
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        pipeline = (ROOT / 'unity/Assets/Scripts/OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('photo-studio.actor-rendering-probes.v2', probe)
        self.assertIn('Capture("01b-front-repeat")', probe)
        self.assertLess(probe.index('_pipeline.ResetTemporalHistory()'),
                        probe.index('ScreenCapture.CaptureScreenshotAsTexture()'))
        for field in ('repeatChangedPixels', 'profileRestoreChangedPixels', 'lightRemovalChangedPixels',
                      'skinRestoreChangedPixels'):
            self.assertIn('public int ' + field + ' = -1', probe)
            self.assertIn('_report.' + field + ' == 0', probe)
        self.assertRegex(pipeline, r'public void ResetTemporalHistory\(\)\s*\{\s*'
                                   r'_historyValid = false;\s*_historyFrame = -1;\s*\}')
        callers = [path.name for path in (ROOT / 'unity/Assets/Scripts').glob('*.cs')
                   if '.ResetTemporalHistory(' in path.read_text(encoding='utf-8')]
        self.assertEqual(callers, ['ActorRenderingValidation.cs'])

    def test_skin_probe_checks_real_full_body_inputs_and_restores_camera(self):
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        for capture in ('Capture("18-skin-neutral", 0f)', 'Capture("18a-skin-desaturated", -1f)',
                        'Capture("18b-skin-saturated", 0.5f)', 'Capture("18c-skin-restored", 0f)'):
            self.assertIn(capture, probe)
        self.assertIn('orbit.distance = 3.2f;', probe)
        self.assertIn('orbit.distance = previousDistance;', probe)
        self.assertIn('orbit.target = previousTarget;', probe)
        self.assertIn('expectedSkin.HasValue && Shader.GetGlobalFloat("_CapturedSkinSaturation") != expectedSkin.Value', probe)
        self.assertIn('Shader.SetGlobalFloat("_CapturedSkinSaturation", previousSkin)', probe)
        self.assertIn('name == "18c-skin-restored" ? "18-skin-neutral" :', probe)
        self.assertIn('name == "15c-fullbody-restored" ? "15a-fullbody-noambient" :', probe)
        self.assertIn('name == "13g-fullbody-lights-restored" ? "13a-fullbody-no-lights" : "01-front";', probe)

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

    def test_builtin_build_retains_urp_variants_and_restores_setting(self):
        builder = (ROOT / 'unity/Assets/Editor/Phase1Builder.cs').read_text(encoding='utf-8')
        helper = builder[builder.index('private static BuildReport BuildWithPipelineSettings('):]
        helper = helper[:helper.index('private static void RenderCamera(')]
        self.assertIn('BuildWithPipelineSettings(options, false)', builder)
        self.assertIn('}, originalShaderReference);', builder)
        self.assertLess(helper.index('if (originalShaderReference) return BuildPipeline.BuildPlayer(options)'),
                        helper.index('new SerializedObject(settings)'))
        self.assertIn('FindProperty("m_StripUnusedVariants")', helper)
        self.assertIn('stripUnused.boolValue = false;', helper)
        cleanup = helper[helper.index('finally'):]
        self.assertIn('stripUnused.boolValue = previousStripUnused;', cleanup)
        self.assertIn('serialized.ApplyModifiedPropertiesWithoutUndo();', cleanup)
        self.assertIn('AssetDatabase.SaveAssetIfDirty(settings);', cleanup)
        self.assertNotIn('renderPipelineAsset =', helper)
        self.assertNotIn('renderPipeline =', helper)

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
        pose = app[app.index('private HashSet<Renderer> ApplyCapturedPosedGeometry()'):]
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

    def test_presenter_does_not_redirect_explicit_capture_target(self):
        source = (ROOT / 'unity/Assets/Scripts/SupersamplePresenter.cs').read_text(encoding='utf-8')
        lookup = source[source.index('public static bool TryGetPresentationTarget'):]
        lookup = lookup[:lookup.index('private void ReleaseTarget')]
        guard = 'if (sourceCamera.targetTexture != presenter._sourceTarget) return false;'
        self.assertIn(guard, lookup)
        self.assertLess(lookup.index(guard), lookup.index('target = presenter._presentationTarget'))
        self.assertLess(lookup.index(guard), lookup.index('BySourceCamera.Remove'))
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for name in ('presenter-owns-normal-source', 'presenter-does-not-steal-offscreen-target',
                     'presenter-registration-survives-offscreen-render'):
            self.assertIn(name, probe)

    def test_captured_uv_is_opt_in_and_validated_before_capture(self):
        app = (ROOT / 'unity/Assets/Scripts/PhotoModeApp.cs').read_text(encoding='utf-8')
        self.assertLess(app.index('CapturedMaterialUvState.TryReadOption'), app.index('Initialize(BundleCatalog.DefaultStagingRoot)'))
        capture = app[app.index('private IEnumerator CaptureGpaCameraAndQuit()'):]
        capture = capture[:capture.index('private IEnumerator CaptureGpaCameraSweepAndQuit()')]
        self.assertLess(capture.index('capturedRenderers = ApplyCapturedPosedGeometry()'), capture.index('CapturedMaterialUvState.TryLoad'))
        self.assertIn('capturedUv != null && !WriteCapturedMaterialUvReport(capturedUv)', capture)
        self.assertIn('Application.Quit(3)', capture)
        state = (ROOT / 'unity/Assets/Scripts/CapturedMaterialUvState.cs').read_text(encoding='utf-8')
        self.assertIn('capturedRenderers.Contains(selected)', state)
        self.assertIn('if (block.isEmpty) selected.GetPropertyBlock(block)', state)
        self.assertIn('float.IsNaN(value) || float.IsInfinity(value)', state)
        self.assertLess(state.index('if (!keys.Add('), state.index('binding.renderer.SetPropertyBlock'))
        self.assertIn('block.GetVector("_BaseMap_ST").Equals(binding.value)', state)
        self.assertNotIn('sharedMaterials =', state)
        self.assertNotIn('0.0031', state)

    def test_captured_camera_requires_explicit_fixed_capture(self):
        app = (ROOT / 'unity/Assets/Scripts/PhotoModeApp.cs').read_text(encoding='utf-8')
        self.assertLess(app.index('CapturedCameraState.TryReadOption'), app.index('Initialize(BundleCatalog.DefaultStagingRoot)'))
        capture = app[app.index('private IEnumerator CaptureGpaCameraAndQuit()'):]
        capture = capture[:capture.index('private IEnumerator CaptureGpaCameraSweepAndQuit()')]
        self.assertLess(capture.index('capturedRenderers = ApplyCapturedPosedGeometry()'), capture.index('CapturedCameraState.TryLoad'))
        self.assertLess(capture.index('capturedCamera.Apply(PreviewCamera)'), capture.index('_orbit.enabled = false'))
        self.assertLess(capture.index('yield return new WaitForEndOfFrame()'), capture.index('WriteCapturedCameraReport(capturedCamera)'))
        self.assertLess(capture.index('WriteCapturedCameraReport(capturedCamera)'), capture.index('path = SavePresentedFrameScreenshot()'))
        state = (ROOT / 'unity/Assets/Scripts/CapturedCameraState.cs').read_text(encoding='utf-8')
        for flag in ('--capture-gpa-camera-and-quit', '--use-captured-posed-geometry', '--capture-presented-window'):
            self.assertIn('Array.IndexOf(args, "' + flag + '") < 0', state)
        self.assertIn('if (found < 0) return true', state)
        self.assertIn('if (_capturedCameraPath != null)', capture)

    def test_captured_camera_validates_and_reports_exact_state(self):
        state = (ROOT / 'unity/Assets/Scripts/CapturedCameraState.cs').read_text(encoding='utf-8')
        self.assertIn('Matrix4x4 inverse = view.inverse', state)
        self.assertIn('matrix[i/4, i%4] = values[i]', state)
        self.assertIn('float.IsNaN(values[i]) || float.IsInfinity(values[i])', state)
        self.assertIn('values.Length != 16', state)
        self.assertIn('bytes.Length > 8192', state)
        self.assertIn('GL.GetGPUProjectionMatrix(camera.projectionMatrix, true)', state)
        self.assertIn('camera.worldToCameraMatrix = view', state)
        self.assertIn('camera.projectionMatrix = projection', state)
        self.assertIn('Exact(camera.projectionMatrix, projection)', state)
        self.assertIn('aspect = camera.aspect', state)
        self.assertLess(state.index('value.targetTexture.width != document.width'), state.index('camera.transform.SetPositionAndRotation'))
        probe = (ROOT / 'unity/Assets/Scripts/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for name in ('captured-camera-exact-view-projection-and-origin', 'captured-camera-reject-later-projection-write',
                     'captured-camera-reject-target-before-mutation', 'captured-camera-reject-later-transform-write'):
            self.assertIn(name, probe)


if __name__ == '__main__':
    unittest.main()
