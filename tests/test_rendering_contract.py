"""Source wiring checks. These do not replace standalone GPU/image validation."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ActorRenderingWiringTests(unittest.TestCase):
    def test_scene_fog_routes_before_temporal_without_leaking_capture_profile(self):
        pipeline = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        render = pipeline.split('private void OnRenderImage(', 1)[1].split('public bool TryGetSceneDistanceFog(', 1)[0]
        self.assertIn('RenderTexture current = source;', render)
        self.assertLess(render.index('screenSpaceReflection.TryComposite('),
                        render.index('current = ApplySceneDistanceFog(current, temporaries)'))
        self.assertLess(render.index('current = ApplySceneDistanceFog(current, temporaries)'),
                        render.index('Graphics.Blit(current, temporal, _postMaterial, 7)'))
        fog = pipeline.split('public bool TryGetSceneDistanceFog(', 1)[1].split('private RenderTexture ApplyDepthOfField(', 1)[0]
        for contract in ('!overrideSceneDistanceFog && _presentationContext != PresentationContext.CapturedRiverbed',
                         'if (density <= 0f || cap <= 0f) return false;',
                         'Graphics.CopyTexture(source, 0, 0, fogged, 0, 0)',
                         'Graphics.Blit(null, fogged, _postMaterial, 11)',
                         '_sourceCamera.nearClipPlane', '_sourceCamera.farClipPlane',
                         'SystemInfo.usesReversedZBuffer', '_sourceCamera.orthographic'):
            self.assertIn(contract, fog)
        post = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/OriginalStylePost.shader').read_text(encoding='utf-8')
        fog_pass = post.split('Name "SCENE_DISTANCE_FOG"', 1)[1]
        self.assertIn('Blend One OneMinusSrcAlpha, Zero One', fog_pass)
        self.assertIn('_SceneFogColor * weight * opacity', fog_pass)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for contract in ('VerifySceneDistanceFog(report);', '"ApplySceneDistanceFog", BindingFlags.Instance',
                         '"scene-fog-no-riverbed-leak-"', '"scene-fog-actual-pipeline-"',
                         '"scene-fog-override-release-restores-local"', 'accepted = finite && maximum <= 0.00001f'):
            self.assertIn(contract, fixture)

    def test_scene_rgb_format_keeps_actor_data_and_presentation_alpha_separate(self):
        presenter = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/SupersamplePresenter.cs').read_text(encoding='utf-8')
        self.assertIn('SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float)', presenter)
        self.assertIn('return RenderTextureFormat.ARGBHalf;', presenter)
        source = presenter.split('_sourceTarget = new RenderTexture(', 1)[1].split('_sourceTarget.Create()', 1)[0]
        self.assertIn('SceneColorFormat', source)
        presentation = presenter.split('_presentationTarget = new RenderTexture(', 1)[1].split('_presentationTarget.Create()', 1)[0]
        self.assertIn('RenderTextureFormat.ARGBHalf', presentation)
        pipeline = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('_actorData = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf)', pipeline)
        self.assertIn('_history = new RenderTexture(width, height, 0, SupersamplePresenter.SceneColorFormat)', pipeline)

    def test_ramp_add_clamps_after_signed_view_offset(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float rampAddX = saturate(definition.r * 2.0 - 1.0 + dot(n, v));', surface)
        self.assertNotIn('definition.r * 2.0 - 1.0 + saturate(dot(n, v))', surface)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for contract in ('VerifyRampAddSignedView(report);', '"ramp-coordinate-"',
                         'Mathf.Clamp01(2*definition-1+Vector3.Dot(n,view))',
                         '"ramp-coordinate-disabled"', '"ramp-coordinate-restored"'):
            self.assertIn(contract, fixture)

    def test_ambient_input_context_and_hair_pass_binding(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('capturedSHValid = _UseCapturedAmbientSH > 0.5 ? capturedSHValid : 0.0;', surface)
        core = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/CharacterSceneRuntime.cs').read_text(encoding='utf-8')
        app = (ROOT / 'unity/Assets/Applications/PhotoStudio/PhotoModeApp.cs').read_text(encoding='utf-8')
        api = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/CharacterSceneRuntime.Api.cs').read_text(encoding='utf-8')
        self.assertIn('Shader.SetGlobalFloat("_UseCapturedAmbientSH", 0f);', app)
        self.assertIn('Shader.SetGlobalFloat("_UseCapturedAmbientSH", 0f);', api)
        context = core.split('protected void ApplyRenderContextProfile(', 1)[1].split('protected float ActorEnvironmentIntensity(', 1)[0]
        self.assertIn('Shader.SetGlobalFloat("_UseCapturedAmbientSH",\n                desired == OriginalStyleRenderPipeline.PresentationContext.CapturedRiverbed ? 1f : 0f);', context)
        controls = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/ActorRenderControls.cs').read_text(encoding='utf-8')
        self.assertIn('if (isHairCover) BindAmbientProbe(renderer);', controls)
        binder = controls.split('private void BindAmbientProbe(', 1)[1].split('private void PublishLights(', 1)[0]
        for contract in ('LightProbeUsage.CustomProvided', 'LightProbeUsage.Off',
                         'RenderSettings.ambientProbe', 'LightProbes.GetInterpolatedProbe(',
                         'renderer.probeAnchor.position : renderer.bounds.center',
                         '_ambientPacked.CopySHCoefficientArraysFrom(_ambientProbe)',
                         '_commands.SetGlobalVector(name,'):
            self.assertIn(contract, binder)
        self.assertNotIn('renderer.SetPropertyBlock', binder)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for contract in ('VerifyAmbientInputContext(report);', '"ambient-context-"',
                         '"hair-cover-ambient-usage-"', '"hair-cover-ambient-custom-missing-coefficients"',
                         'CameraEvent.AfterForwardOpaque,staleLighting', 'LightProbeUsage.CustomProvided'):
            self.assertIn(contract, fixture)

    def test_common_actor_view_direction_respects_projection(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float3 rawViewDirection = UnityWorldSpaceViewDir(input.worldPosition);', surface)
        self.assertIn('float3 v = unity_OrthoParams.w > 0.5\n        ? normalize(UNITY_MATRIX_V[2].xyz) : normalize(rawViewDirection);', surface)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyProjectionViewDirection(report);', fixture)
        self.assertIn('projection-view-', fixture)
        self.assertIn('-perspective-varies', fixture)

    def test_local_environment_does_not_use_capture_direction_adapters(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('bool capturedEnvironment = _UseCapturedEnvironmentBasis > 0.5;', surface)
        self.assertIn('isTypeOne && capturedEnvironment', surface)
        self.assertIn('float3 transformedEyeReflection = capturedEnvironment', surface)
        self.assertIn('float3 transformedActorReflection = capturedEnvironment', surface)
        core = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/CharacterSceneRuntime.cs').read_text(encoding='utf-8')
        build = core.split('protected void BuildActorEnvironmentCube(', 1)[1].split('protected void ApplyRenderContextProfile(', 1)[0]
        self.assertLess(build.index('Shader.SetGlobalFloat("_UseCapturedEnvironmentBasis", 0f);'),
                        build.index('TryBuildCapturedActorEnvironmentCube()'))
        self.assertLess(build.index('TryBuildCapturedActorEnvironmentCube()'),
                        build.index('Shader.SetGlobalFloat("_UseCapturedEnvironmentBasis", 1f);'))
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyEnvironmentCoordinates(report);', fixture)
        self.assertIn('environment-coordinates-', fixture)

    def test_material_rgb_tint_follows_ramp_and_skin_saturation(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('baseSample.a *= _Color.a;', surface)
        self.assertIn('baseSample.rgb = lerp(baseSample.rgb, layer.rgb, layerMask);', surface)
        self.assertNotIn('float4 baseSample = rawBaseSample * _Color;', surface)
        self.assertLess(surface.index('float skinSaturationDelta'), surface.index('diffuse *= _Color.rgb;'))
        self.assertLess(surface.index('diffuse *= _Color.rgb;'), surface.index('float metallic = isSkin'))
        eye = surface.split('if (isEyeHighlight)\n    {', 1)[1].split('float ndl =', 1)[0]
        self.assertEqual(eye.count('baseSample.rgb *= _Color.rgb;'), 1)
        self.assertIn('eyeHighlightLit * _CapturedType5OutputScale', eye)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for contract in ('VerifyMaterialBaseTint(report);', '"base-tint-shade-"',
                         '"base-tint-painted-"', '"base-tint-eye-highlight-once-"',
                         'expected *= tints[tint] * 0.96f', 'shaderAddTint'):
            self.assertIn(contract, fixture)

    def test_rim_controls_are_independent_reversible_and_reach_shader(self):
        controls = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/ActorRenderControls.cs').read_text(encoding='utf-8')
        for contract in ('public bool overrideRim;', 'ApplyRimOverride();',
                         '_camera.cameraToWorldMatrix.MultiplyVector(view)',
                         'Shader.SetGlobalFloat("_UseExactViewRimBasis", 1f)',
                         '!current.Equals(_appliedGlobals[index])',
                         'Shader.GetGlobalVector(OverrideGlobals[index]).Equals(_appliedGlobals[index])',
                         'RestoreRimBasis();'):
            self.assertIn(contract, controls)
        panel = (ROOT / 'unity/Assets/Applications/PhotoStudio/PhotoActorRenderControls.cs').read_text(encoding='utf-8')
        for contract in ('"Override view-space rim only"', '"Rim power (narrowness)"',
                         '"Rim surface tint"', '"Rim intensity"'):
            self.assertIn(contract, panel)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for contract in ('VerifyRimControl(report);', '"small-script-write-restored"',
                         '"late-writer-preserved"', '"lighting-retains-shared-color"',
                         '"rim-retains-shared-color"', '"small-main-light-write-restored"',
                         '"view-direction-roll-independent"', '"rim-control-gpu-"'):
            self.assertIn(contract, fixture)

    def test_view_profile_uses_authored_shape_and_head_relative_camera(self):
        source = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/FaceExpressionRenderer.cs').read_text(encoding='utf-8')
        profile = source.split('private void ResolveViewProfileCorrection()', 1)[1].split('public string DiagnosticJson()', 1)[0]
        for contract in ('value.blendShapeName == "side090"',
                         'camera.orthographic ? -camera.transform.forward : camera.transform.position - center',
                         'Vector3.Dot(view, head.right)', 'Vector3.Dot(view, head.forward)',
                         'Mathf.Atan2(right, forward)', 'curves[_viewProfileSlot].Evaluate(ViewProfileAngle)',
                         'weights[shape] = Mathf.Max(weights[shape], ViewProfileWeight)'):
            self.assertIn(contract, profile)
        self.assertNotIn('5201', profile)
        self.assertNotIn('weights[85]', profile)
        self.assertIn('if (!enabled && !args.Contains("--face-angle-correction-off")) ApplyViewProfileCorrection(weights);', source)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for contract in ('VerifyViewProfileCorrection(report);', 'view-profile-preserves-authored-weights',
                         'view-profile-disabled-restores-base', 'view-profile-manual-debug-owns-weight',
                         'view-profile-unrelated-shape-not-selected', '"-whole-authored-shape"', '"-chin"'):
            self.assertIn(contract, fixture)

    def test_straight_alpha_keeps_opacity_and_single_rgb_weight(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('bool straightAlphaActor = abs(_SrcBlend - 5.0) < 0.25;', surface)
        self.assertIn('(premultipliedActor || straightAlphaActor) ? baseSample.a : 1.0', surface)
        self.assertIn('if (premultipliedActor) lit *= outputAlpha;', surface)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyStraightAlpha(report);', fixture)
        for contract in ('source * alpha + background * (1f - alpha)',
                         'expected.a = background.a * (1f - alpha)',
                         'expected = source * alpha + background',
                         'new Vector2(1f, 240f/255f)', '"-opaque-alpha"'):
            self.assertIn(contract, fixture)

    def test_motion_material_sequence_uses_owner_clock_and_releases(self):
        core = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/CharacterSceneRuntime.cs').read_text(encoding='utf-8')
        self.assertIn('SamplePhotoMaterialEffects(seconds);', core)
        self.assertIn('_faceMaterialEffects.Sample(blend > 0f ? motionName : previousMotionName,', core)
        self.assertIn('_faceMaterialEffects.Dispose();', core)
        runtime = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/ActorMaterialEffectRuntime.cs').read_text(encoding='utf-8')
        self.assertNotIn('Time.time', runtime)
        self.assertIn('materials[binding.slot] == binding.replacement', runtime)
        self.assertIn('Math.Floor(seconds * fps)', runtime)
        shader = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('actorUv = actorUv * _ActorTextureFrame.xy + _ActorTextureFrame.zw', shader)

    def test_layer_panel_is_scoped_and_restores_material_weights(self):
        source = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/ActorRenderControls.cs').read_text(encoding='utf-8')
        panel = (ROOT / 'unity/Assets/Applications/PhotoStudio/PhotoActorRenderControls.cs').read_text(encoding='utf-8')
        self.assertIn('Override material Layer', panel)
        self.assertIn('Sweat / messy layer', panel)
        self.assertIn('ApplyLayerOverride();', source)
        self.assertIn('current != state.applied', source)
        refresh = source.split('public void RefreshRenderers()', 1)[1].split('private sealed class MaterialScope', 1)[0]
        self.assertIn('RestoreLayerOverride();', refresh)
        self.assertIn('_actor.GetComponentsInChildren<Renderer>(true)', refresh)
        self.assertIn('RestoreLayerOverride();', source.split('protected void OnDisable()', 1)[1])

    def test_eyebrow_highlight_has_strict_uv_mask_and_own_light_contribution(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('isFaceCap && input.uv.x > 0.96875 && input.uv.y > 0.96875', surface)
        self.assertIn('capturedSpecular += 2.0 * diffuse * capturedSpecularModulation', surface)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyEyebrowHighlight(report);', fixture)
        self.assertIn('new Vector2(0.96875f,0.99f)', fixture)
        self.assertIn('new Vector2(0.99f,0.96875f)', fixture)

    def test_optional_sphere_reflection_is_bound_and_composited(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('if (_UseReflection > 0.5)', surface)
        self.assertIn('SphereReflection(n, v) * specularVisibility * capturedSpecularModulation', surface)
        sphere = surface.split('float3 SphereReflection(', 1)[1].split('float AnisotropicHighlight(', 1)[0]
        self.assertNotIn('reflect(', sphere)
        self.assertIn('unity_OrthoParams.w', sphere)
        self.assertIn('_ReflectionSphereMap_HDR', sphere)
        self.assertIn('UNITY_DECLARE_TEX2DARRAY_NOSAMPLER(_ActorEyeEnvironmentArray)', surface)
        self.assertIn('_ActorEyeEnvironmentArray, _ActorEnvironmentArray, float3(uv, face), mip', surface)
        repair = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/MaterialRepairer.cs').read_text(encoding='utf-8')
        self.assertIn('hasReflection && reflectionEnabled', repair)
        self.assertIn('source.IsKeywordEnabled("_USE_REFLECTION_SPHERE")', repair)
        for name in ('PhotoModeFallback.shader', 'ActorSupplemental.shader'):
            self.assertIn('_ReflectionSphereMap_HDR (', (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources' / name).read_text(encoding='utf-8'))
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyReflectionSphere(report);', fixture)
        self.assertIn('reflection-sphere-import-disabled', fixture)
        self.assertIn('reflection-sphere-no-strand-brdf', fixture)

    def test_dynamic_presentation_attaches_whole_chain_without_changing_solver_cache(self):
        source = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/HairDynamicsSystem.cs').read_text(encoding='utf-8')
        present = source.split('private void ApplyRuntimePose()', 1)[1].split('private void ApplySegmentRotation(', 1)[0]
        for contract in ('while (root.parent != null) root = root.parent;',
                         'root.authoredPosition - root.position',
                         'node.bone.position = node.position + presentationOffset;',
                         'node.bone.rotation = node.rotation;'):
            self.assertIn(contract, present)
        for field in ('node.position =', 'node.rotation =', 'node.childSpeed ='):
            self.assertNotIn(field, present)
        self.assertIn('if (steps == 0) ApplyRuntimePose();', source)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for contract in ('VerifyDynamicPresentation(report);', 'dynamic-presentation-generated-rig',
                         'dynamic-presentation-root-', 'dynamic-presentation-segments-',
                         'dynamic-presentation-rotations-', 'dynamic-presentation-cache-',
                         'dynamic-presentation-zero-offset-', '"translated", "rotated", "held-repeat"',
                         'cached[i,j].Equals(DynamicField(node,fields[j]))'):
            self.assertIn(contract, fixture)

    def test_lookat_keeps_fullbody_motion_constraint_before_solving(self):
        profile = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/CapturedLookAtProfile.cs').read_text(encoding='utf-8')
        runtime = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/CapturedLookAtRuntime.cs').read_text(encoding='utf-8')
        self.assertIn('public const float LookAtClampWeight = 0.5f;', profile)
        job = runtime.split('public void ProcessAnimation(AnimationStream stream)', 1)[1]
        clamp = 'human.SetLookAtClampWeight(CapturedLookAtProfile.LookAtClampWeight);'
        self.assertIn(clamp, job)
        self.assertLess(job.index(clamp), job.index('human.SolveIK();'))
        self.assertNotIn('SetLookAtClampWeight(0f)', job)
        # Keep tracking and endpoint weights: disabling the job would hide
        # the rear-target defect without fixing its solver input.
        for weight in ('Body', 'Head', 'Eyes'):
            self.assertIn('human.SetLookAt' + weight + 'Weight(', job)
        self.assertIn('ClampBody(ref human, _upperChestFrontBack', job)

    def test_outline_preserves_authored_object_space_displacement(self):
        outline = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorOutline.cginc').read_text(encoding='utf-8')
        self.assertIn('(input.vertex.xyz + input.tangent.xyz * width) * _WardrobeScaleCorrection.xyz', outline)
        self.assertNotIn('UnityObjectToWorldNormal', outline)
        self.assertNotIn('normalize(', outline)
        self.assertNotIn(': input.normal', outline)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for contract in ('outline-extrusion-transform-', 'Vector3.Scale(tangent * 0.187f, scale)',
                         'expectedCoverage > 0 && maximum < 0.00001f'):
            self.assertIn(contract, fixture)

    def test_paused_orbit_stops_graph_and_checks_actual_pose_and_resume(self):
        core = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/CharacterSceneRuntime.cs').read_text(encoding='utf-8')
        pause = core.split('public void TogglePause()', 1)[1].split('public void SelectExpression', 1)[0]
        self.assertIn('if (_paused) _motionGraph.Stop();', pause)
        self.assertIn('else _motionGraph.Play();', pause)
        self.assertIn('_storyPlayer.TogglePause();', pause)
        self.assertNotIn('SetHairDynamics', pause)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        for name in ('05b-rear-quarter', '05c-back', '05d-back-no-outline',
                     '05e-back-no-cover', '05f-other-rear-quarter'):
            self.assertIn(name, probe)
        for contract in ('poseDigest == _pausedPoseDigest', 'app.PhotoMotionTime == _pausedMotionTime',
                         '_report.pausedOrbitComparedFrames == 11', '_report.animationResumed ? 0 : 2',
                         'app.PhotoMotionPlaying && motionTime > previousMotionTime',
                         'previousMotionTime = motionTime;', '_report.animationResumed &= app.PhotoMotionPlaying;'):
            self.assertIn(contract, probe)

    def test_outline_depth_nibble_is_integer_not_normalized(self):
        outline = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorOutline.cginc').read_text(encoding='utf-8')
        self.assertIn('float depthOffset = floor(bytes.b / 16.0) * packed;', outline)
        self.assertNotIn('float depthOffset = high.b * packed;', outline)
        self.assertIn('output.position.z -= depthOffset * (0.001 / 15.0);', outline)
        self.assertIn('output.position.z += depthOffset * (0.001 / 15.0);', outline)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('outline-packed-depth-', probe)
        self.assertIn('new[] { 0, 1, 8, 15 }', probe)
        self.assertIn('Mathf.Max(nibble, 1) * unitDistance * fraction', probe)

    def test_outline_respects_material_depth_write_and_draw_order(self):
        extra = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSupplemental.shader').read_text(encoding='utf-8')
        outline, hair = extra.split('Name "ACTOR_HAIR_COVER"', 1)
        outline = outline.split('Name "ACTOR_OUTLINE"', 1)[1]
        self.assertIn('ZWrite [_ZWrite]', outline)
        self.assertIn('Cull Front', outline)
        self.assertIn('ZTest LEqual', outline)
        self.assertNotIn('ZWrite Off', outline)
        self.assertIn('ZWrite [_ZWrite]', hair)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for name in ('VerifyOutlineDepth(report);', 'outline-depth-near-then-far',
                     'outline-depth-far-then-near', 'outline-depth-optout-near-then-far',
                     'outline-depth-blocks-late-surface-behind',
                     'outline-depth-optout-allows-late-surface-behind',
                     'outline-depth-allows-late-surface-in-front',
                     'outline-depth-actual-commands-submitted'):
            self.assertIn(name, probe)

    def test_main_specular_preserves_receiver_basis_in_both_light_modes(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float3 halfVector = l + float3(0.0, 0.0, 1.0);', surface)
        self.assertNotIn('receiverNormal = lerp(receiverNormal, n,', surface)
        self.assertIn('saturate(dot(receiverNormal, l))', surface)
        # Additional lights remain world-space, unlike the authored main light.
        self.assertIn('float3 additionalHalf = direction + v;', surface)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for name in ('VerifyMainSpecularBasis(report);', 'main-spec-nonconstant-response',
                     'main-spec-material-', 'main-spec-zero-mask-', 'main-spec-no-strand-lobe-'):
            self.assertIn(name, probe)
        self.assertIn('Quaternion.Euler(12, 20, -15)', probe)
        self.assertIn('origin-direction*(origin.z/direction.z)', probe)

    def test_hair_highlight_keeps_camera_receiver_under_world_light(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float3 hairHalfVector = l + float3(0.0, 0.0, 1.0);', surface)
        self.assertIn('float3 hairReceiver = lerp(n, capturedReceiverNormal, saturate(_UseCapturedReceiverNormal));', surface)
        self.assertIn('dot(hairReceiver, hairHalf)', surface)
        self.assertNotIn('float hairResponse = pow(saturate(dot(receiverNormal, h))', surface)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyHairHighlightBasis(report);', probe)
        self.assertIn('hair-basis-nonconstant-highlight', probe)
        self.assertIn('hair-basis-zero-mask-', probe)
        self.assertIn('hair-basis-nonhair-', probe)
        validation = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        for name in ('10-world-light-orbit', '10a-world-light-side', '10b-world-light-elevated'):
            self.assertIn('Capture("' + name + '", expectedWorldSpace: true)', validation)
        self.assertIn('Light-space probe input was overwritten:', validation)

    def test_shadow_offset_uses_layered_definition_not_depth_comparison(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float authoredOffset = max(definitionRed * 2.0 - 1.0, 0.0);', surface)
        self.assertIn('authoredOffset * saturate(_CapturedActorShadowUseOffset) + filtered', surface)
        call = 'CapturedActorShadow(input.worldPosition, definition.r)'
        self.assertIn(call, surface)
        self.assertLess(surface.index('definition = lerp(definition, layerDefinition, layerMask);'), surface.index(call))
        self.assertNotIn('centre * saturate(_CapturedActorShadowUseOffset)', surface)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('shadow-definition-offset-', probe)
        self.assertIn('new Vector3(0.75f, 0.5f, 0.5f)', probe)
        self.assertIn('new[] { 0, 8, 9 }', probe)

    def test_shadow_filter_preserves_subtexel_phase_and_strict_comparison(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float2 fraction = frac(grid);', surface)
        self.assertIn('if (_UseCapturedActorShadow <= 0.0) return 1.0;', surface)
        self.assertIn('(floor(grid) + 0.5) * texel', surface)
        self.assertIn('float3(1.0 - fraction.x, 1.0, fraction.x) * 0.5', surface)
        self.assertIn('float3(1.0 - fraction.y, 1.0, fraction.y) * 0.5', surface)
        self.assertIn('receiverDepth > mapDepth ? 1.0 : 0.0', surface)
        self.assertIn('receiverDepth < mapDepth ? 1.0 : 0.0', surface)
        self.assertNotIn('return step(mapDepth, receiverDepth);', surface)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyShadowSubtexelFiltering(report);', probe)
        self.assertIn('ShadowBilinearOracle(depth, uv +', probe)
        self.assertIn('shadow-subtexel-bilinear-', probe)

    def test_additional_lights_modulate_key_colored_shading(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float3 mainLighting = capturedBrdf * (_ActorKeyColor.rgb * _CapturedLightColor.rgb);', surface)
        self.assertGreater(surface.index('float3 mainLighting ='), surface.index('for (int lightIndex'))
        self.assertIn('(mainLighting + additionalSpecular)', surface)
        self.assertIn('distribution * environmentBrdf * definitionVisibility', surface)
        self.assertIn('saturate(angularWeight + shadeStrength)', surface)
        self.assertNotIn('float radiance = saturate(dot(n, direction)) * attenuation;', surface)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyAdditionalLighting(report);', probe)
        for case in ('additional-two-lights-add-once', 'additional-shade-floor-',
                     'additional-outside-range', 'additional-spot-outside',
                     'additional-spec-normal-', 'additional-spec-scale-zero'):
            self.assertIn(case, probe)

    def test_fullbody_additional_lights_have_input_and_restoration_checks(self):
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        self.assertIn('Capture("13b-fullbody-point", expectedLights: 2)', probe)
        self.assertIn('Capture("13c-fullbody-spot", expectedLights: 2)', probe)
        self.assertIn('Capture("13d-fullbody-tinted-spot", expectedLights: 2)', probe)
        self.assertIn('expectedLights.HasValue && _controls.AdditionalLightCount != expectedLights.Value', probe)
        self.assertIn('_report.additionalRestoreChangedPixels == 0', probe)
        self.assertIn('_report.additionalRestoreChangedPixels = changed', probe)

    def test_sky_light_uses_material_diffuse_independent_of_direct_scale(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float dielectricDiffuse = 0.96 * (1.0 - metallic);', surface)
        self.assertIn('float3 ambientDiffuse = diffuse * (isEye ? 0.96 : dielectricDiffuse);', surface)
        self.assertIn('ambientDiffuse * ambient * _ActorLightingScales.x', surface)
        self.assertNotIn('lit = diffuse * ambient', surface)
        self.assertNotIn('lit = directDiffuse * ambient', surface)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyAmbientMaterialResponse(report);', probe)
        self.assertIn('new[] { 0, 1, 4, 9 }', probe)
        self.assertIn('new[] { 0f, 2f }', probe)
        self.assertIn('ambient-scale-restored', probe)

    def test_full_body_sky_probe_checks_input_and_strict_restoration(self):
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        self.assertIn('Capture("15a-fullbody-noambient", expectedGi: 0f)', probe)
        self.assertIn('Capture("15b-fullbody-ambient", expectedGi: 0.5f)', probe)
        self.assertIn('Capture("15c-fullbody-restored", expectedGi: 0f)', probe)
        self.assertIn('expectedGi.HasValue && Shader.GetGlobalVector("_ActorLightingScales").x != expectedGi.Value', probe)
        self.assertIn('_report.ambientRestoreChangedPixels == 0', probe)
        self.assertIn('_report.ambientRestoreChangedPixels = changed', probe)

    def test_ramp_add_keeps_diffuse_and_specular_alpha_roles_separate(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('float3 authoredRampAddRgb = rampAddColor * (1.0 - authoredRampAdd.a);', surface)
        self.assertIn('baseSample.rgb += authoredRampAddRgb;', surface)
        self.assertIn('shadeSample.rgb += authoredRampAddRgb;', surface)
        self.assertIn('1.0.xxx, rampAddColor, saturate(authoredRampAdd.a)', surface)
        self.assertNotIn('1.0.xxx, authoredRampAddRgb, saturate(authoredRampAdd.a)', surface)
        self.assertIn('_CapturedType1Variant < 1.5', surface)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyRampAddSpecular(report);', probe)
        self.assertIn('QualitySettings.activeColorSpace == ColorSpace.Linear ? tint.linear : tint', probe)
        self.assertIn('new[] { 0f, 0.5f, 1f }', probe)
        self.assertIn('types[i] != 1 || variants[i] != 2', probe)

    def test_hair_strands_and_accessories_have_separate_specular_regions(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('(input.uv.x > 0.75 && input.uv.y > 0.75)', surface)
        self.assertNotIn('step(0.75, input.uv.x)', surface)
        self.assertIn('(1.0 - hairAccessoryMask)', surface)
        gate = 'if (isHair) definitionVisibility *= hairAccessoryMask;'
        self.assertIn(gate, surface)
        self.assertLess(surface.index('hairHighlightWeight);'), surface.index(gate))
        self.assertLess(surface.index(gate), surface.index('float specularVisibility ='))
        self.assertIn('distribution * environmentBrdf * definitionVisibility', surface)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyHairSpecularRegions(report);', probe)
        self.assertIn('new Vector2(0.75f, 0.9f)', probe)
        self.assertIn('new Vector2(0.9f, 0.75f)', probe)
        self.assertIn('hair-spec-strands-additional', probe)

    def test_skin_saturation_uses_authored_mask_not_material_id(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('_CapturedSkinSaturation * saturate(shadeSample.a)', surface)
        self.assertNotIn('isSkin && abs(_CapturedSkinSaturation)', surface)
        self.assertIn('float3(0.2126729, 0.7151522, 0.0721750)', surface)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifySkinSaturation(report);', probe)
        self.assertIn('foreach (int type in new[] { 0, 1, 9 })', probe)
        self.assertIn('foreach (float mask in new[] { 0f, 0.25f, 1f })', probe)
        self.assertIn('Color.LerpUnclamped(gray, baseline, 1f + delta * mask)', probe)

    def test_head_triangle_uses_reflected_basis_once(self):
        driver = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/ActorHeadLightingDriver.cs').read_text(encoding='utf-8')
        reference = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/ActorShaderReferenceFeature.cs').read_text(encoding='utf-8')
        self.assertIn('Vector3 right = -_head.right.normalized;', driver)
        self.assertIn('head.SetColumn(0, right)', reference)
        self.assertNotIn('head.SetColumn(0, -right)', reference)
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('head.AddComponent<ActorHeadLightingDriver>()', probe)
        for name in ('head-reflection-basis-', 'head-triangle-reflects-dark-side',
                     'head-triangle-preserves-zero-mask'):
            self.assertIn(name, probe)
        validation = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        for name in ('09a-world-light-left', '09b-world-light-right'):
            self.assertIn('Capture("' + name + '", expectedWorldSpace: true)', validation)

    def test_quality_respects_per_texture_sampling(self):
        app = (ROOT / 'unity/Assets/Applications/PhotoStudio/PhotoModeApp.cs').read_text(encoding='utf-8')
        self.assertIn('QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;', app)
        self.assertNotRegex(app, r'QualitySettings\.anisotropicFiltering\s*=\s*AnisotropicFiltering\.ForceEnable')
        repair = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/MaterialRepairer.cs').read_text(encoding='utf-8')
        self.assertIn('texture.filterMode, texture.wrapModeU, texture.wrapModeV', repair)
        self.assertIn('texture.mipMapBias, texture.anisoLevel', repair)

    def test_shared_surface_and_explicit_supplementary_passes(self):
        surface = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/PhotoModeFallback.shader').read_text(encoding='utf-8')
        extra = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSupplemental.shader').read_text(encoding='utf-8')
        self.assertIn('#include "ActorSurface.cginc"', surface)
        self.assertIn('#include "ActorSurface.cginc"', extra)
        self.assertIn('#include "ActorOutline.cginc"', extra)
        self.assertNotIn('Name "ACTOR_HAIR_COVER"', surface)
        hair = extra.split('Name "ACTOR_HAIR_COVER"', 1)[1]
        for contract in ('Ref [_StencilRef]', 'ReadMask [_StencilReadMask]',
                         'WriteMask 0', 'Comp Less', 'Pass Keep', 'ZWrite [_ZWrite]'):
            self.assertIn(contract, hair)
        controls = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/ActorRenderControls.cs').read_text(encoding='utf-8')
        self.assertLess(controls.index('DrawPass("ACTOR_OUTLINE", -1)'),
                        controls.index('DrawPass("ACTOR_HAIR_COVER", 1)'))
        self.assertLess(controls.index('DrawPass("ACTOR_HAIR_COVER", 1)'),
                        controls.index('DrawPass("ACTOR_OUTLINE", 1)'))
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for contract in ('hair-cover-stencil-', '64 < (stencil & 108)',
                         'hair-cover-zero-alpha-depth-blocks-own-outline',
                         'hair-cover-depth-optout-retains-own-outline',
                         'hair-cover-half-alpha-precedes-own-outline'):
            self.assertIn(contract, fixture)

    def test_profile_parameters_have_runtime_consumers(self):
        core = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/CharacterSceneRuntime.cs').read_text(encoding='utf-8')
        for parameter in ('matCapOffset', 'matCapSmoothScale', 'shadeApplyRatio',
                          'giScale', 'additiveLightScale', 'additiveLightSpecularScale', 'eyeHighlightColor'):
            self.assertIn('profile.' + parameter, core)
        shader = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        for uniform in ('_ActorMatcapParameters', '_ActorLightingScales', '_ActorRimColor',
                        '_ActorEyeHighlightColor', '_CapturedShadeAdditive'):
            self.assertGreater(len(re.findall(re.escape(uniform), shader)), 1)
        self.assertIn('input.uv1 * float2(0.5, 1.0)', shader)
        self.assertIn('input.layerUv + float2(0.5, 0.0)', shader)

    def test_hair_is_not_globally_transparent(self):
        repair = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/MaterialRepairer.cs').read_text(encoding='utf-8')
        self.assertIn('CopyFloat(source, target, "_SrcBlend", 1f)', repair)
        self.assertIn('CopyFloat(source, target, "_ZWrite", 1f)', repair)
        controls = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/ActorRenderControls.cs').read_text(encoding='utf-8')
        self.assertIn('material.GetFloat("_ShaderType") - 8f', controls)
        self.assertIn('CameraEvent.BeforeForwardAlpha', controls)
        self.assertIn('_camera.RemoveCommandBuffer', controls)
        self.assertIn('_commands.Release()', controls)

    def test_hair_coverage_consumes_authored_view_fade_mask(self):
        repair = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/MaterialRepairer.cs').read_text(encoding='utf-8')
        shader = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        self.assertIn('source.GetVector("_FadeParam")', repair)
        self.assertIn('dot(v, _HeadDirection.xyz)', shader)
        self.assertIn('dot(v, _HeadUpDirection.xyz)', shader)
        self.assertIn('1.0 - saturate(rawBaseSample.a)', shader)
        self.assertIn('max(obliqueCoverage.x, obliqueCoverage.y)', shader)

    def test_hair_cover_selftest_uses_actual_composition_and_records_expression(self):
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifyHairCoverComposition(report);', probe)
        self.assertIn('_camera.gameObject.AddComponent<ActorRenderControls>()', probe)
        for name in ('hair-cover-disabled-retains-eye', 'hair-cover-enabled-oblique-restored',
                     'hair-cover-actual-command-submitted', 'hair-cover-respects-nearer-depth', '-outside-stencil'):
            self.assertIn(name, probe)
        validation = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        self.assertIn('PhotoModeApp app = FindObjectOfType<PhotoModeApp>();', validation)
        self.assertIn('expression = app.CurrentExpression', validation)

    def test_probe_comparisons_reset_history_and_check_restoration(self):
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingValidation.cs').read_text(encoding='utf-8')
        pipeline = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('photo-studio.actor-rendering-probes.v2', probe)
        self.assertIn('Capture("01b-front-repeat")', probe)
        self.assertLess(probe.index('_pipeline.ResetTemporalHistory()'),
                        probe.index('ScreenCapture.CaptureScreenshotAsTexture()'))
        for field in ('repeatChangedPixels', 'profileRestoreChangedPixels', 'lightRemovalChangedPixels',
                      'skinRestoreChangedPixels'):
            self.assertIn('public int ' + field + ' = -1', probe)
            self.assertIn('_report.' + field + ' == 0', probe)
        self.assertRegex(pipeline, r'public void ResetTemporalHistory\(\)\s*\{\s*'
                                   r'_historyValid = false;\s*_historyFrame = -1;\s*'
                                   r'sceneTemporalSource\?\.ResetTemporalColorHistory\(\);\s*'
                                   r'sceneMotionBlurSource\?\.ResetMotionBlurHistory\(\);\s*\}')
        callers = [path.name for folder in ('packages/com.digital-kotone.toolkit/Runtime', 'unity/Assets/Applications/PhotoStudio')
                   for path in (ROOT / folder).rglob('*.cs')
                   if '.ResetTemporalHistory(' in path.read_text(encoding='utf-8')]
        self.assertCountEqual(callers, ['ActorRenderingValidation.cs', 'ActorRenderingSelfTest.SceneTaa.cs'])

    def test_skin_probe_checks_real_full_body_inputs_and_restores_camera(self):
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingValidation.cs').read_text(encoding='utf-8')
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
        reference = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/ActorShaderReferenceFeature.cs').read_text(encoding='utf-8')
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
        helper = (ROOT / 'packages/com.digital-kotone.toolkit/Editor/ToolkitBuildPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('ToolkitBuildPipeline.BuildPlayer(options, originalShaderReference)', builder)
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
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingValidation.cs').read_text(encoding='utf-8')
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
        app = (ROOT / 'unity/Assets/Applications/PhotoStudio/PhotoModeApp.cs').read_text(encoding='utf-8')
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
        shader = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/ActorSurface.cginc').read_text(encoding='utf-8')
        head = shader[shader.index('float headSurfaceRamp ='):shader.index('float type9Ramp =')]
        self.assertIn('0.5 * _ActorMatcapParameters.x', head)
        self.assertNotIn('- 0.15', head)

    def test_asset_free_gpu_probe_precedes_asset_loading(self):
        app = (ROOT / 'unity/Assets/Applications/PhotoStudio/PhotoModeApp.cs').read_text(encoding='utf-8')
        self.assertLess(app.index('ActorRenderingSelfTest.TryStart(gameObject)'),
                        app.index('Initialize(BundleCatalog.DefaultStagingRoot)'))
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('new Mesh', probe)
        self.assertIn('new Texture2D(256, 1', probe)
        self.assertIn('RenderTextureFormat.ARGBFloat', probe)
        self.assertIn('_camera.Render()', probe)
        self.assertIn('nonconstant-ramp-positive-control', probe)
        self.assertIn('Application.Quit(report.accepted ? 0 : 2)', probe)
        self.assertNotIn('AssetBundle', probe)
        self.assertNotIn('BundleCatalog', probe)

    def test_presenter_does_not_redirect_explicit_capture_target(self):
        source = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/SupersamplePresenter.cs').read_text(encoding='utf-8')
        lookup = source[source.index('public static bool TryGetPresentationTarget'):]
        lookup = lookup[:lookup.index('private void ReleaseTarget')]
        guard = 'if (sourceCamera.targetTexture != presenter._sourceTarget) return false;'
        self.assertIn(guard, lookup)
        self.assertLess(lookup.index(guard), lookup.index('target = presenter._presentationTarget'))
        self.assertLess(lookup.index(guard), lookup.index('BySourceCamera.Remove'))
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for name in ('presenter-owns-normal-source', 'presenter-does-not-steal-offscreen-target',
                     'presenter-registration-survives-offscreen-render'):
            self.assertIn(name, probe)

    def test_captured_uv_is_opt_in_and_validated_before_capture(self):
        app = (ROOT / 'unity/Assets/Applications/PhotoStudio/PhotoModeApp.cs').read_text(encoding='utf-8')
        self.assertLess(app.index('CapturedMaterialUvState.TryReadOption'), app.index('Initialize(BundleCatalog.DefaultStagingRoot)'))
        capture = app[app.index('private IEnumerator CaptureGpaCameraAndQuit()'):]
        capture = capture[:capture.index('private IEnumerator CaptureGpaCameraSweepAndQuit()')]
        self.assertLess(capture.index('capturedRenderers = ApplyCapturedPosedGeometry()'), capture.index('CapturedMaterialUvState.TryLoad'))
        self.assertIn('capturedUv != null && !WriteCapturedMaterialUvReport(capturedUv)', capture)
        self.assertIn('Application.Quit(3)', capture)
        state = (ROOT / 'unity/Assets/Applications/PhotoStudio/CapturedMaterialUvState.cs').read_text(encoding='utf-8')
        self.assertIn('capturedRenderers.Contains(selected)', state)
        self.assertIn('if (block.isEmpty) selected.GetPropertyBlock(block)', state)
        self.assertIn('float.IsNaN(value) || float.IsInfinity(value)', state)
        self.assertLess(state.index('if (!keys.Add('), state.index('binding.renderer.SetPropertyBlock'))
        self.assertIn('block.GetVector("_BaseMap_ST").Equals(binding.value)', state)
        self.assertNotIn('sharedMaterials =', state)
        self.assertNotIn('0.0031', state)

    def test_captured_camera_requires_explicit_fixed_capture(self):
        app = (ROOT / 'unity/Assets/Applications/PhotoStudio/PhotoModeApp.cs').read_text(encoding='utf-8')
        self.assertLess(app.index('CapturedCameraState.TryReadOption'), app.index('Initialize(BundleCatalog.DefaultStagingRoot)'))
        capture = app[app.index('private IEnumerator CaptureGpaCameraAndQuit()'):]
        capture = capture[:capture.index('private IEnumerator CaptureGpaCameraSweepAndQuit()')]
        self.assertLess(capture.index('capturedRenderers = ApplyCapturedPosedGeometry()'), capture.index('CapturedCameraState.TryLoad'))
        self.assertLess(capture.index('capturedCamera.Apply(PreviewCamera)'), capture.index('_orbit.enabled = false'))
        self.assertLess(capture.index('yield return new WaitForEndOfFrame()'), capture.index('WriteCapturedCameraReport(capturedCamera)'))
        self.assertLess(capture.index('WriteCapturedCameraReport(capturedCamera)'), capture.index('path = SavePresentedFrameScreenshot()'))
        state = (ROOT / 'unity/Assets/Applications/PhotoStudio/CapturedCameraState.cs').read_text(encoding='utf-8')
        for flag in ('--capture-gpa-camera-and-quit', '--use-captured-posed-geometry', '--capture-presented-window'):
            self.assertIn('Array.IndexOf(args, "' + flag + '") < 0', state)
        self.assertIn('if (found < 0) return true', state)
        self.assertIn('if (_capturedCameraPath != null)', capture)

    def test_captured_camera_validates_and_reports_exact_state(self):
        state = (ROOT / 'unity/Assets/Applications/PhotoStudio/CapturedCameraState.cs').read_text(encoding='utf-8')
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
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        for name in ('captured-camera-exact-view-projection-and-origin', 'captured-camera-reject-later-projection-write',
                     'captured-camera-reject-target-before-mutation', 'captured-camera-reject-later-transform-write'):
            self.assertIn(name, probe)


    def test_sphere_fog_is_camera_local_and_before_temporal(self):
        pipeline = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        settings = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/SphereFogSettings.cs').read_text(encoding='utf-8')
        shader = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/Resources/OriginalStylePost.shader').read_text(encoding='utf-8')
        self.assertIn('public SphereFogSettings sphereFog = new SphereFogSettings();', pipeline)
        self.assertIn('public bool enabled;', settings)
        self.assertNotRegex(settings, r'FindObject|Shader\.SetGlobal|Resources\.Load|AssetBundle|PhotoModeApp')
        self.assertLess(pipeline.index('current = ApplySphereFog(current, temporaries);'),
                        pipeline.index('Graphics.Blit(current, temporal, _postMaterial, 7);'))
        apply = pipeline.split('private RenderTexture ApplySphereFog(', 1)[1].split('private RenderTexture ApplyDepthOfField(', 1)[0]
        for guard in ('sphereFog == null || !sphereFog.IsActive', '"--disable-sphere-fog"', 'viewProjection.determinant == 0f'):
            self.assertLess(apply.index(guard), apply.index('GetTemporary('))
        self.assertIn('GL.GetGPUProjectionMatrix(_sourceCamera.projectionMatrix, true)', apply)
        self.assertIn('_sourceCamera.worldToCameraMatrix', apply)
        self.assertIn('Graphics.CopyTexture(source, 0, 0, fogged, 0, 0);', apply)
        self.assertIn('Graphics.Blit(null, fogged, _postMaterial, 12);', apply)
        fog_pass = shader.split('Name "SPHERE_FOG"', 1)[1]
        self.assertIn('Blend One OneMinusSrcAlpha, Zero One', fog_pass)
        for term in ('UNITY_REVERSED_Z', 'UNITY_NEAR_CLIP_VALUE', 'UNITY_UV_STARTS_AT_TOP',
                     'clearDepth', 'nearH.xyz / nearH.w', 'endH.xyz / endH.w',
                     'span * span / 12.0', '1.0 - exp(-opticalDepth)'):
            self.assertIn(term, fog_pass)

    def test_sphere_fog_gpu_oracle_is_not_its_own_analytic_formula(self):
        probe = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('VerifySphereFog(report);', probe)
        oracle = probe.split('private static double IntegrateSphereFog(', 1)[1].split('private void VerifySphereFog(', 1)[0]
        self.assertIn('const int steps = 2048;', oracle)
        self.assertNotIn('EvaluateOpticalDepth', oracle)
        self.assertNotIn('halfSquared', oracle)
        for name in ('sphere-fog-default-no-allocation', 'sphere-fog-full-chord-optical-depth',
                     'sphere-fog-tangent-and-zero-segment', 'sphere-fog-gpu-quadrature-',
                     'sphere-fog-cpu-quadrature-', 'sphere-fog-disable-and-null-no-allocation',
                     'sphere-fog-second-camera-independent'):
            self.assertIn(name, probe)


if __name__ == '__main__':
    unittest.main()
