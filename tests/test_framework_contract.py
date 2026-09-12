"""Source/API boundaries only; generated-rig/GPU behavior runs in the Player self-test."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class FrameworkContractTests(unittest.TestCase):
    def test_wind_is_independent_of_asset_and_application_state(self):
        source = (RUNTIME / 'NaturalWindSettings.cs').read_text(encoding='utf-8')
        for forbidden in ('BundleCatalog', 'PhotoModeApp', 'UnityEngine.Random', 'Time.time', 'Environment.GetCommandLineArgs'):
            self.assertNotIn(forbidden, source)
        for contract in ('public bool enabled;', 'Sample(double seconds)', 'Envelope(double seconds)', 'IsValid'):
            self.assertIn(contract, source)

    def test_wind_routes_to_child_settings_and_survives_rebuild(self):
        solver = (RUNTIME / 'HairDynamicsSystem.cs').read_text(encoding='utf-8')
        self.assertIn('child.setting.useWindGlobalForce', solver)
        self.assertIn('acceleration += naturalForce * child.setting.wind;', solver)
        self.assertIn('naturalWindTimeOverride ?? Time.timeAsDouble', solver)
        host = (RUNTIME / 'CharacterSceneRuntime.cs').read_text(encoding='utf-8')
        self.assertIn('SetNaturalWind(NaturalWindSettings settings)', host)
        for name in ('_hairDynamics', '_garmentDynamics', '_skirtDynamics'):
            self.assertIn('BindNaturalWind(' + name + ');', host)
        self.assertNotIn('BindNaturalWind(_bodySoftTissueDynamics)', host)

    def test_temporal_mask_uses_actual_depth_and_explicit_ownership(self):
        source = (RUNTIME / 'TemporalClassification.cs').read_text(encoding='utf-8')
        self.assertIn('BuiltinRenderTextureType.CurrentActive', source)
        self.assertNotIn('BuiltinRenderTextureType.Depth)', source)
        self.assertNotIn('FindObjectsOfType', source)
        for contract in ('camera != _camera', '_preparedFrame != Time.frameCount', 'FilterMode.Point', 'RemoveCommandBuffer', 'ValidSurface(surface)'):
            self.assertIn(contract, source)
        pipeline = (RUNTIME / 'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('BindTemporalClassification(source.width, source.height);', pipeline)

    def test_temporal_classification_keeps_actor_material_discriminator_separate(self):
        post = (RUNTIME / 'Resources/OriginalStylePost.shader').read_text(encoding='utf-8')
        temporal = post.split('Name "TEMPORAL_RESOLVE"', 1)[1].split('ENDCG', 1)[0]
        self.assertIn('_TemporalFlagsTex', temporal)
        self.assertNotIn('_ActorDataTex', temporal)
        self.assertLess(temporal.index('flags / 4.0'), temporal.index('flags / 2.0'))
        self.assertIn('uv - _TemporalJitterUv', temporal)

    def test_authoring_encoding_does_not_mutate_or_normalize_meshes(self):
        source = (RUNTIME / 'ActorVertexEncoding.cs').read_text(encoding='utf-8')
        self.assertIn('public static Color32 Pack(Channels value)', source)
        self.assertIn('public static Channels Unpack(Color32 value)', source)
        self.assertNotIn('.normalized', source)
        self.assertNotIn('AssetDatabase', source)
        self.assertNotIn('Mesh mesh', source)

    def test_scene_depth_has_explicit_surface_and_camera_ownership(self):
        source = (RUNTIME / 'SceneDepthData.cs').read_text(encoding='utf-8')
        for forbidden in ('PhotoModeApp', 'BundleCatalog', 'FindObjectsOfType', 'Shader.SetGlobal', 'SetGlobalTexture', 'BuiltinRenderTextureType.Depth'):
            self.assertNotIn(forbidden, source)
        for contract in ('public Surface[] surfaces', 'public LayerMask excludedLayers', 'camera != _camera',
                         '_renderedFrame != Time.frameCount', 'RemoveCommandBuffer', 'public bool TryGetFrame'):
            self.assertIn(contract, source)

    def test_scene_depth_is_not_automatically_added_to_the_character_host(self):
        source = (RUNTIME / 'CharacterSceneRuntime.cs').read_text(encoding='utf-8')
        self.assertNotIn('AddComponent<SceneDepthData>', source)
        scene = (RUNTIME / 'SceneDepthData.cs').read_text(encoding='utf-8')
        self.assertIn('RenderTextureReadWrite.Linear', scene)
        self.assertIn('RenderTextureFormat.RFloat', scene)
        self.assertIn('width = (width + 1) / 2', scene)

    def test_scene_depth_has_separate_mask_and_nonaveraging_hierarchy(self):
        source = (RUNTIME / 'Resources/SceneDepthData.shader').read_text(encoding='utf-8')
        for forbidden in ('_ActorDataTex', '_TemporalFlags', '_BumpMap', 'GenerateMips'):
            self.assertNotIn(forbidden, source)
        self.assertIn('SV_Target1', source)
        self.assertIn('step(_SceneSmoothnessThreshold, smoothness)', source)
        self.assertIn('result = min(result', source)
        self.assertIn('_MainTex ("Depth reduction input", 2D)', source)

    def test_ssr_has_actor_free_history_and_owned_visibility_depth(self):
        source = (RUNTIME / 'ScreenSpaceReflection.cs').read_text(encoding='utf-8')
        for forbidden in ('BundleCatalog', 'PhotoModeApp', 'FindObjectsOfType', 'Shader.SetGlobal', 'BuiltinRenderTextureType.Depth)'):
            self.assertNotIn(forbidden, source)
        for required in ('public bool reflectionsEnabled;', 'BuiltinRenderTextureType.CurrentActive',
                         'Graphics.Blit(_sceneColor, _historyColor)', 'Graphics.Blit(_frame.linearDepth, _historyDepth)',
                         'camera != _camera', '_consumedSequence == _renderSequence', 'RemoveCommandBuffer'):
            self.assertIn(required, source)
        self.assertNotIn('Graphics.Blit(source, _historyColor)', source)

    def test_ssr_is_explicitly_bound_before_fog_and_temporal(self):
        source = (RUNTIME / 'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        body = source.split('private void OnRenderImage', 1)[1]
        self.assertLess(body.index('screenSpaceReflection.TryComposite'), body.index('ApplySceneDistanceFog(current'))
        self.assertNotIn('AddComponent<ScreenSpaceReflection>', (RUNTIME / 'CharacterSceneRuntime.cs').read_text(encoding='utf-8'))

    def test_ssr_uses_explicit_projection_conventions_and_depth_rejection(self):
        source = (RUNTIME / 'Resources/ScreenSpaceReflectionTrace.hlsl').read_text(encoding='utf-8')
        self.assertIn('UNITY_UV_STARTS_AT_TOP', source)
        self.assertIn('TextureUv(previousClip)', source)
        self.assertIn('abs(previousDepth - expectedDepth) > _SsrHistory.y', source)
        self.assertIn('sceneDepth - _SsrTrace.y', source)
        self.assertIn('if (level > 0) { level--; continue; }', source)

    def test_planar_capture_is_explicit_and_restores_culling(self):
        source = (RUNTIME / 'PlanarReflection.cs').read_text(encoding='utf-8')
        for forbidden in ('PhotoModeApp', 'BundleCatalog', 'FindObjectsOfType', 'Shader.SetGlobal',
                          '.sharedMaterial =', 'OnRenderImage'):
            self.assertNotIn(forbidden, source)
        for required in ('public bool reflectionsEnabled;', '_captureCamera.cullingMask = 0',
                         'draw.shaderPass < draw.material.passCount', 'CalculateObliqueMatrix(viewClip)',
                         'finally { GL.invertCulling = oldCulling; _rendering = false; }',
                         'reflectedView.inverse.transpose * worldClip', 'RemoveCommandBuffer'):
            self.assertIn(required, source)

    def test_planar_coverage_is_independent_and_projection_uses_real_depth(self):
        source = (RUNTIME / 'PlanarReflection.cs').read_text(encoding='utf-8')
        self.assertIn('BuiltinRenderTextureType.CurrentActive', source)
        self.assertIn('_sourceTarget != _camera.targetTexture', source)
        shader = (RUNTIME / 'Resources/PlanarReflection.shader').read_text(encoding='utf-8')
        for required in ('Name "CLEAR_COVERAGE"', 'ZTest Equal Blend Off ColorMask A',
                         'reflected.rgb / reflected.a', 'UNITY_UV_STARTS_AT_TOP',
                         'abs(dot(_PlanarPlane, float4(i.world, 1)))'):
            self.assertIn(required, shader)

    def test_planar_reduced_forward_has_explicit_light_without_automatic_passes(self):
        shader = (RUNTIME / 'Resources/PlanarCapture.shader').read_text(encoding='utf-8')
        for required in ('_LightDirection', '_AmbientColor', '_Emission', 'ZWrite On', 'clip(color.a - _Cutoff)'):
            self.assertIn(required, shader)
        for forbidden in ('ForwardAdd', 'ShadowCaster', '_WorldSpaceLightPos0', '_LightColor0'):
            self.assertNotIn(forbidden, shader)

    def test_character_planar_adapter_is_owned_explicit_and_not_a_default_app_change(self):
        source = (RUNTIME / 'ActorPlanarCaptureSet.cs').read_text(encoding='utf-8')
        for forbidden in ('PhotoModeApp', 'BundleCatalog', 'Shader.SetGlobal', 'Shader.GetGlobal',
                          'FindObjectsOfType', '.sharedMaterials =', '.SetPropertyBlock('):
            self.assertNotIn(forbidden, source)
        for required in ('public bool TryRefresh(Renderer[]', 'public void Dispose()',
                         'Requires this toolkit\'s ActorToon', '_materialBlock.isEmpty ? _rendererBlock',
                         'coverageMaterial = material', 'GetShaderPassEnabled("ActorHairCover")'):
            self.assertIn(required, source)
        host = (RUNTIME / 'CharacterSceneRuntime.cs').read_text(encoding='utf-8')
        self.assertNotIn('ActorPlanarCaptureSet', host)

    def test_character_planar_custom_coverage_replays_matching_depth_and_stencil(self):
        source = (RUNTIME / 'PlanarReflection.cs').read_text(encoding='utf-8')
        for required in ('public Material coverageMaterial;', 'Invalid custom coverage pass',
                         'ClearRenderTarget(true, false, Color.clear)', 'draw.coverageShaderPass'):
            self.assertIn(required, source)
        shader = (RUNTIME / 'Resources/ActorPlanarCapture.shader').read_text(encoding='utf-8')
        self.assertEqual(shader.count('Ref [_CapStencilRef]'), 2)
        self.assertEqual(shader.count('ZWrite [_CapZWrite]'), 2)
        self.assertIn('Blend One OneMinusSrcAlpha', shader)
        self.assertIn('ColorMask A', shader)
        surface = (RUNTIME / 'Resources/ActorPlanarSurface.hlsl').read_text(encoding='utf-8')
        self.assertIn('float4 sample = CaptureBase(i)', surface)
        for forbidden in ('_LightColor0', '_WorldSpaceLightPos0', '_CapturedLightDirection', '_ActorDataTex'):
            self.assertNotIn(forbidden, surface)

    def test_unified_reflection_requires_indirect_source_contract(self):
        source = (RUNTIME / 'SceneReflectionResolve.cs').read_text(encoding='utf-8')
        for forbidden in ('FindObjectsOfType', 'Shader.SetGlobal', '.sharedMaterial =', 'PhotoModeApp', 'OnRenderImage'):
            self.assertNotIn(forbidden, source)
        for required in ('public bool inputExcludesIndirectSpecular;', 'if (!inputExcludesIndirectSpecular)',
                         'BuiltinRenderTextureType.CurrentActive', 'screenSpaceReflection.TryTrace(',
                         '_consumed == _sequence', 'camera == _camera', 'RemoveCommandBuffer'):
            self.assertIn(required, source)
        self.assertNotIn('screenSpaceReflection.TryComposite', source)

    def test_unified_reflection_has_planar_priority_and_geometry_trace(self):
        ssr = (RUNTIME / 'Resources/ScreenSpaceReflectionTrace.hlsl').read_text(encoding='utf-8')
        self.assertLess(ssr.index('_SsrPlanarCoverage.Load'), ssr.index('float4 packed ='))
        resolve = (RUNTIME / 'Resources/SceneReflectionResolve.shader').read_text(encoding='utf-8')
        self.assertLess(resolve.index('if (planar.a > 1e-5)'), resolve.index('float4 ssr ='))
        for required in ('lerp(probe, planar.rgb', 'lerp(probe, ssr.rgb',
                         'tex2D(_ResolveOffset, uv).z - data.z', 'shading - geometric',
                         'determinant((float3x3)unity_ObjectToWorld)'):
            self.assertIn(required, resolve)
        self.assertNotIn('v.tangent.w * unity_WorldTransformParams.w', resolve)

    def test_unified_reflection_pipeline_does_not_double_consume_ssr(self):
        source = (RUNTIME / 'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        render = source.split('private void OnRenderImage', 1)[1]
        self.assertLess(render.index('sceneReflectionResolve.TryComposite'), render.index('screenSpaceReflection.TryComposite'))
        self.assertIn('else if (screenSpaceReflection != null', render)
        self.assertNotIn('AddComponent<SceneReflectionResolve>', (RUNTIME / 'CharacterSceneRuntime.cs').read_text(encoding='utf-8'))

    def test_compute_selector_checks_kernel_and_format_with_explicit_fallback(self):
        source = (RUNTIME / 'SceneComputeSupport.cs').read_text(encoding='utf-8')
        for required in ('SystemInfo.supportsComputeShaders', 'SupportsRandomWriteOnRenderTextureFormat',
                         'asset.HasKernel', 'asset.IsSupported', 'if (!allowFallback)', 'Invalid scene shader backend'):
            self.assertIn(required, source)
        for file in ('ScreenSpaceReflection.cs', 'SceneDepthData.cs'):
            text = (RUNTIME / file).read_text(encoding='utf-8')
            self.assertIn('= SceneShaderBackend.Raster;', text)
            self.assertIn('_compute = Instantiate(_computeAsset)', text)
            self.assertNotIn('_computeAsset.Set', text)

    def test_compute_trace_shares_math_and_writes_every_valid_thread(self):
        source = (RUNTIME / 'Resources/ScreenSpaceReflection.compute').read_text(encoding='utf-8')
        raster = (RUNTIME / 'Resources/ScreenSpaceReflection.shader').read_text(encoding='utf-8')
        for text in (source, raster):
            self.assertIn('#include "ScreenSpaceReflectionTrace.hlsl"', text)
        self.assertIn('RWTexture2D<float4> _SsrOutput', source)
        self.assertIn('any(id.xy >= (uint2)_SsrSize.xy)', source)
        self.assertIn('_SsrOutput[id.xy] = TraceSceneReflection(uv);', source)

    def test_compute_depth_is_ceil_reduction_not_averaged_mips(self):
        source = (RUNTIME / 'Resources/SceneDepthHierarchy.compute').read_text(encoding='utf-8')
        self.assertIn('RWTexture2D<float> _DepthOutput', source)
        self.assertIn('closest = min(closest', source)
        self.assertIn('(int2)_DepthSize.xy - 1', source)
        runtime = (RUNTIME / 'SceneDepthData.cs').read_text(encoding='utf-8')
        self.assertIn('(output.width + 7) / 8', runtime)
        self.assertIn('enableRandomWrite = randomWrite', runtime)
        self.assertNotIn('.GenerateMips(', runtime)
        self.assertNotIn('autoGenerateMips = true', runtime)


    def test_ssr_roughness_is_opt_in_and_returns_filtered_result_to_all_hosts(self):
        settings = (RUNTIME / 'SsrRoughnessSettings.cs').read_text(encoding='utf-8')
        self.assertIn('public bool enabled;', settings)
        self.assertIn('enabled && maximumRadiusPixels > 0', settings)
        source = (RUNTIME / 'ScreenSpaceReflection.cs').read_text(encoding='utf-8')
        self.assertIn('reflection = OutputReflection;', source)
        self.assertIn('SetTexture("_SsrReflection", OutputReflection)', source)
        self.assertIn('TryGetRawReflection', source)
        self.assertIn('surfaces.Length > 1024', source)
        self.assertIn('Release(ref _filterHorizontal); Release(ref _filtered)', source)
        self.assertNotIn('AddComponent<ScreenSpaceReflection>', (RUNTIME / 'CharacterSceneRuntime.cs').read_text(encoding='utf-8'))

    def test_ssr_roughness_preserves_confidence_and_geometry_boundaries(self):
        source = (RUNTIME / 'Resources/ScreenSpaceReflectionFilter.hlsl').read_text(encoding='utf-8')
        for contract in ('center.a <= 1e-5', 'abs(other.b - receiver.b)', 'dot(normal, otherNormal)',
                         'dot(delta, normal)', 'dot(delta, otherNormal)', 'min(center.a, trustedWeight / totalWeight)',
                         'sum / trustedWeight', '_SsrPlanarCoverage.Load'):
            self.assertIn(contract, source)
        for suffix in ('shader', 'compute'):
            self.assertIn('#include "ScreenSpaceReflectionFilter.hlsl"',
                          (RUNTIME / ('Resources/ScreenSpaceReflection.' + suffix)).read_text(encoding='utf-8'))

    def test_monitor_is_explicit_owned_and_restores_camera_state(self):
        source = (RUNTIME / 'HdrMonitor.cs').read_text(encoding='utf-8')
        for forbidden in ('PhotoModeApp', 'BundleCatalog', 'UnityEngine.UI', 'Shader.SetGlobal',
                          'void Update(', 'void LateUpdate('):
            self.assertNotIn(forbidden, source)
        self.assertNotRegex(source, r'_camera\.(?:enabled|projectionMatrix|aspect)\s*=(?!=)')
        for required in ('public bool monitorEnabled;', 'public event Action<double> PrepareCapture',
                         'Camera.current != null', '_camera.targetTexture = oldTarget', 'GL.sRGBWrite = oldSrgb',
                         'Graphics.Blit(_capture, _output)', 'seconds < _observedTime', 'Release(ref _capture); Release(ref _output)'):
            self.assertIn(required, source)

    def test_monitor_material_reads_radiance_without_second_opacity(self):
        source = (RUNTIME / 'MonitorEmissionMaterial.cs').read_text(encoding='utf-8')
        self.assertIn('!frame.IsCurrent', source)
        self.assertIn('public void Unbind()', source)
        self.assertNotIn('.sharedMaterial', source)
        shader = (RUNTIME / 'Resources/MonitorEmission.shader').read_text(encoding='utf-8')
        self.assertIn('tex2D(_MonitorTex, i.uv).rgb', shader)
        self.assertIn('float2 uv3:TEXCOORD3', shader)
        self.assertNotIn('radiance * sample.a', shader)
        canvas = (RUNTIME / 'Resources/MonitorCanvas.shader').read_text(encoding='utf-8')
        self.assertIn('Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha', canvas)
        self.assertIn('UnityGet2DClipping', canvas)


    def test_deferred_scene_requires_explicit_layer_ownership_and_private_targets(self):
        source = (RUNTIME / 'SceneDeferredCamera.cs').read_text(encoding='utf-8')
        for forbidden in ('PhotoModeApp', 'Shader.SetGlobal', '.sharedMaterial =', 'Resources.Load<Material>'):
            self.assertNotIn(forbidden, source)
        self.assertNotRegex(source, r'_camera\.(?:cullingMask|targetTexture|enabled)\s*=(?!=)')
        for required in ('public bool sceneEnabled;', '(_camera.cullingMask & sceneLayers.value) != 0',
                         'seen.Add((r, surface.materialIndex))', 'CameraEvent.BeforeForwardOpaque',
                         'ReleaseTargets(ref _scratch)', 'RenderTextureFormat.ARGBFloat',
                         '_commands.DrawRenderer', '_commands.DrawMesh', 'SubmittedDecals++'):
            self.assertIn(required, source)

    def test_deferred_channels_feed_lighting_and_real_scene_depth(self):
        source = (RUNTIME / 'Resources/SceneDeferred.shader').read_text(encoding='utf-8')
        for required in ('float depth : SV_Depth', 'data.mos.a', 'direct + indirect + data.emission.rgb',
                         'o.mos.rgb = lerp', 'o.normal.xyz = safeNormal', 'o.emission.rgb = lerp',
                         'abs(o.normal.a - _ReceiverGroup)', 'heightCoverage', 'determinant((float3x3)unity_ObjectToWorld)'):
            self.assertIn(required, source)
        self.assertNotIn('_CameraGBufferTexture', source)
        self.assertNotIn('_CameraDepthTexture', source)


    def test_decal_lights_are_optional_instanced_owned_and_float_accumulated(self):
        source = (RUNTIME / 'SceneDecalLightRenderer.cs').read_text(encoding='utf-8')
        for token in ('commands.DrawProcedural', 'MeshTopology.Triangles, 6,', '_SceneLightOffset',
                      'ComputeBufferType.Structured', 'RenderTextureFormat.ARGBFloat',
                      'settings.monitor.TryGetFrame', 'SystemInfo.supportsInstancing', 'settings.allowInstancingFallback'):
            self.assertIn(token, source)
        self.assertNotIn('Shader.SetGlobal', source)
        self.assertNotIn('AddComponent<Light>', source)
        host = (RUNTIME / 'SceneDeferredCamera.cs').read_text(encoding='utf-8')
        self.assertLess(host.index('_decalLights?.Record'), host.index('_commands.DrawMesh(Quad(), Matrix4x4.identity, lighting'))
        self.assertIn('_decalLights?.Dispose()', host)

    def test_decal_light_shapes_share_brdf_and_do_not_reapply_monitor_alpha(self):
        source = (RUNTIME / 'Resources/SceneDecalLight.hlsl').read_text(encoding='utf-8')
        for token in ('SV_InstanceID', 'StructuredBuffer<SceneLightData>', 'input.instance + _SceneLightOffset',
                      'along / (2 * light.axisXLength.w)', 'projectedHalf', 'LightBrdf(', 'mos.a',
                      'clamp(tex2Dlod(_LightAtlas, float4(atlasUv, 0, 0)).rgb, 0, 65504)'):
            self.assertIn(token, source)
        for shader in ('SceneDecalLightInstanced.shader', 'SceneDecalLightScalar.shader'):
            self.assertIn('#include "SceneDecalLight.hlsl"', (RUNTIME / 'Resources' / shader).read_text(encoding='utf-8'))
        self.assertNotIn('atlas.a', source)


    def test_scene_gi_explicit_inputs_do_not_mutate_shared_lighting(self):
        source = (RUNTIME / 'SceneGiInput.cs').read_text(encoding='utf-8')
        for token in ('VertexAttribute.TexCoord1', 'renderer.lightmapIndex', 'renderer.lightmapScaleOffset',
                      'LightmapSettings.lightmaps', 'LightProbes.GetInterpolatedProbe',
                      'CopySHCoefficientArraysFrom', 'material.SetVector("_SceneGiSH"', 'no ambient fallback'):
            self.assertIn(token, source)
        for forbidden in ('Shader.SetGlobal', 'renderer.SetPropertyBlock', 'LightmapSettings.lightmaps ='):
            self.assertNotIn(forbidden, source)

    def test_scene_gi_has_independent_geometry_output_and_diffuse_only_backlight(self):
        scene = (RUNTIME / 'Resources/SceneDeferred.shader').read_text(encoding='utf-8')
        for token in ('SCENE_GI_OUTPUT', 'float4 gi : SV_Target4', 'SceneGi(i.uv2, n)',
                      'data.albedo.rgb * (1 - metallic) * gi.rgb * _GiBaseScale * ao',
                      'saturate(-dot(n, l))', '_HasBakedGi > .5'):
            self.assertIn(token, scene)
        lights = (RUNTIME / 'Resources/SceneDecalLight.hlsl').read_text(encoding='utf-8')
        self.assertIn('response.x * response.w * saturate(-dot(n, l))', lights)
        self.assertIn('response *= lerp(1, gi.rgb, light.response.z)', lights)


    def test_reference_baker_is_editor_only_and_clones_before_uv_changes(self):
        source = (ROOT / 'packages/com.digital-kotone.toolkit/Editor/SceneGiBaker.cs').read_text(encoding='utf-8')
        for token in ('BakeSceneCopy(', 'AssetDatabase.CopyAsset(sourceScene, destination)',
                      'UnityEngine.Object.Instantiate(filter.sharedMesh)', 'new Material(bound[i])',
                      'Lightmapping.Bake()', 'Lightmapping.lightingDataAsset == null',
                      'EditorSceneManager.RestoreSceneManagerSetup(setup)', 'AssetDatabase.GetDependencies(sourceScene, true)',
                      'asset.sha256 == asset.sha256After', 'actualLightmapper', 'settings.lightmapper != options.lightmapper'):
            self.assertIn(token, source)
        self.assertLess(source.index('UnityEngine.Object.Instantiate(filter.sharedMesh)'), source.index('Unwrapping.GenerateSecondaryUVSet(mesh)'))
        self.assertLess(source.index('SceneManager.GetSceneAt(i).isDirty'), source.index('Directory.CreateDirectory(destinationFolder)'))
        self.assertIn('Directory.Exists(folder) || File.Exists(folder)', source)
        self.assertNotIn('Lightmapping.ClearDiskCache', source)
        self.assertNotIn('AssetDatabase.DeleteAsset', source)

    def test_actual_baked_scene_tests_require_explicit_private_bundle(self):
        app = ROOT / 'unity/Assets/Applications/PhotoStudio'
        entry = (app / 'ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        fixture = (app / 'ActorRenderingSelfTest.BakedGi.cs').read_text(encoding='utf-8')
        self.assertIn('GAKUMAS_SELFTEST_GI_BAKE_BUNDLE', entry)
        self.assertLess(entry.index('VerifySceneGi(report)'), entry.index('VerifyRealGiBake(report, bakeBundle)'))
        for token in ('LoadSceneMode.Additive', 'LightProbes.Tetrahedralize()', 'LightProbes.GetInterpolatedProbe',
                      'BakedGiRayUv(', 'BakedGiBilinear(', 'SkinnedMeshRenderer', 'UnloadSceneAsync',
                      'SceneGiSource.RendererLightmap', 'SceneGiSource.SceneProbe'):
            self.assertIn(token, fixture)
        self.assertNotIn('AddAmbientLight', fixture)
        self.assertNotIn('LightmapSettings.lightmaps =', fixture)


    def test_spot_is_a_new_shape_without_changing_existing_instance_stride(self):
        settings = (RUNTIME / 'SceneDecalLightSettings.cs').read_text(encoding='utf-8')
        self.assertIn('SceneDecalLightShape { Point, Capsule, Area, Spot }', settings)
        self.assertIn('spotInnerAngle = 30', settings)
        self.assertIn('spotOuterAngle = 60', settings)
        renderer = (RUNTIME / 'SceneDecalLightRenderer.cs').read_text(encoding='utf-8')
        struct = renderer[renderer.index('private struct LightData'):renderer.index('private readonly List<LightData>')]
        self.assertEqual(struct.count('Vector4'), 2)
        self.assertIn('parameters, response, clipRect', struct)
        self.assertIn('light.shape == SceneDecalLightShape.Spot', renderer)
        self.assertIn('light.spotOuterAngle * Mathf.Deg2Rad * .5f', renderer)
        self.assertIn('!Range(light.spotInnerAngle, 0, light.spotOuterAngle)', renderer)
        self.assertNotIn('AddComponent<Light>', renderer)

    def test_spot_uses_radial_range_and_shared_cone_attenuation(self):
        shader = (RUNTIME / 'Resources/SceneDecalLight.hlsl').read_text(encoding='utf-8')
        for token in ('else if (light.radianceShape.w < 2.5)', 'else distanceToSource = length(delta)',
                      'dot(delta / distanceToSource, light.axisZHeight.xyz)', 'if (cosine < outer) discard',
                      'inner > outer ? saturate((cosine - outer) / (inner - outer)) : 1'):
            self.assertIn(token, shader)
        self.assertLess(shader.index('if (light.radianceShape.w > 2.5)'), shader.index('float3 response = LightBrdf'))
        for name in ('SceneDecalLightScalar.shader', 'SceneDecalLightInstanced.shader'):
            self.assertIn('#include "SceneDecalLight.hlsl"', (RUNTIME / 'Resources' / name).read_text(encoding='utf-8'))


    def test_shadow_producer_is_opt_in_borrowed_geometry_and_separate_metadata(self):
        settings = (RUNTIME / 'SceneLightShadowSettings.cs').read_text(encoding='utf-8')
        self.assertIn('public bool enabled;', settings)
        producer = (RUNTIME / 'SceneLightShadowAtlas.cs').read_text(encoding='utf-8')
        for token in ('Matrix4x4.Perspective(light.spotOuterAngle', 'GL.GetGPUProjectionMatrix',
                      'RenderTextureFormat.RFloat', 'commands.DrawRenderer',
                      'commands.ClearRenderTarget(true, true, Color.white)', 'renderer.HasPropertyBlock()',
                      'SkinnedMeshRenderer', 'Marshal.SizeOf<ShadowData>()', 'maxShadowedLights',
                      'material.EnableKeyword("SCENE_LIGHT_SHADOWS")'):
            self.assertIn(token, producer)
        for forbidden in ('Shader.SetGlobal', 'renderer.SetPropertyBlock', 'renderer.sharedMaterial =',
                          'renderer.enabled =', 'AddComponent<Camera>', 'RenderWithShader'):
            self.assertNotIn(forbidden, producer)
        self.assertIn('light.shape != SceneDecalLightShape.Spot', producer)

    def test_shadow_consumption_uses_light_depth_and_bounded_per_tile_filter(self):
        shader = (RUNTIME / 'Resources/SceneLightShadow.hlsl').read_text(encoding='utf-8')
        for token in ('mul(data.worldToShadow', 'world + normal * data.depth.w',
                      '(projected.w - data.depth.z) / data.depth.y', 'clamp(uv + float2(x,y) * texel, low, high)',
                      'visibility /= 9', 'lerp(1, visibility, data.options.x)'):
            self.assertIn(token, shader)
        self.assertNotIn('_CameraDepth', shader)
        caster = (RUNTIME / 'Resources/SceneLightShadowCaster.shader').read_text(encoding='utf-8')
        self.assertIn('o.depth = o.position.w / _ShadowFar', caster)
        self.assertIn('ZTest LEqual ZWrite On', caster)
        for name in ('SceneDecalLightScalar.shader', 'SceneDecalLightInstanced.shader'):
            self.assertIn('#pragma multi_compile_local __ SCENE_LIGHT_SHADOWS', (RUNTIME / 'Resources' / name).read_text(encoding='utf-8'))
        light = (RUNTIME / 'Resources/SceneDecalLight.hlsl').read_text(encoding='utf-8')
        self.assertIn('attenuation *= SceneLightVisibility(world, n, shadow)', light)

    def test_point_shadow_renders_six_radial_faces_without_changing_light_abi(self):
        source = (RUNTIME / 'SceneLightShadowAtlas.cs').read_text(encoding='utf-8')
        for token in ('light.shape != SceneDecalLightShape.Point', 'face < 6',
                      'Matrix4x4.Translate(-light.position)', 'input.nearPlane / Mathf.Sqrt(3)',
                      '_lightCount > settings.maxShadowedLights', '_views[tile]', 'tile += 5'):
            self.assertIn(token, source)
        caster = (RUNTIME / 'Resources/SceneLightShadowCaster.shader').read_text(encoding='utf-8')
        for token in ('SCENE_SHADOW_POINT', 'float3 fromLight', 'float radial = length(input.fromLight)',
                      'clip(radial - _ShadowPointOrigin.w)', 'return radial / _ShadowFar'):
            self.assertIn(token, caster)
        shader = (RUNTIME / 'Resources/SceneLightShadow.hlsl').read_text(encoding='utf-8')
        for token in ('ScenePointFace(direction)', 'ScenePointDirection(face.xy + float2(x,y) * step',
                      'data.options.w - 1 + face.z', '(radial - data.depth.z) / data.depth.y'):
            self.assertIn(token, shader)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.PointShadow.cs').read_text(encoding='utf-8')
        for token in ('six-face-radial-depth', 'adjacent-face-taps-differ-from-border-clamp',
                      'corner-radial-near-preserves-caster-below-axial-near', 'skin-vs-static-depth-',
                      'GAKUMAS_SELFTEST_CAPTURE_POINT_SHADOW', 'mixed-scalar-instanced-image'):
            self.assertIn(token, fixture)

    def test_screen_shadow_has_real_prepass_rg8_and_independent_ambient_capsules(self):
        settings = (RUNTIME / 'SceneScreenShadowSettings.cs').read_text(encoding='utf-8')
        for token in ('public bool enabled;', 'SceneCapsuleOccluder[]', 'capsuleSamples = 32', 'capsuleNormalBias'):
            self.assertIn(token, settings)
        producer = (RUNTIME / 'SceneScreenShadowRenderer.cs').read_text(encoding='utf-8')
        for token in ('GraphicsFormat.R8G8_UNorm', 'FormatUsage.Render', 'FormatUsage.Sample',
                      's.capsules.Length > 16', 'main?.BindMain(_material)', 'commands.DrawMesh', 'ReleaseTargets()'):
            self.assertIn(token, producer)
        self.assertNotIn('Shader.SetGlobal', producer)
        host = (RUNTIME / 'SceneDeferredCamera.cs').read_text(encoding='utf-8')
        self.assertLess(host.index('_decalLights?.RecordShadows(_commands)'), host.index('_screenShadow.Record(_commands'))
        self.assertLess(host.index('_screenShadow.Record(_commands'), host.index('foreach (var rt in _gbuffer)'))
        self.assertIn('_screenShadow?.Visibility == null || _screenShadow.IsCreated', host)
        shader = (RUNTIME / 'Resources/SceneScreenShadow.shader').read_text(encoding='utf-8')
        for token in ('CapsuleHit(', 'SphereHit(', 'sqrt(1 - u) * normal', 'nearest / _CapsuleParameters.z', 'SceneLightVisibility(world, normal, data)'):
            self.assertIn(token, shader)
        scene = (RUNTIME / 'Resources/SceneDeferred.shader').read_text(encoding='utf-8')
        self.assertIn('direct *= screenVisibility.r; ao *= screenVisibility.g;', scene)
        prepass = scene[scene.index('Name "SCENE_GEOMETRY_ONLY_NORMAL_DEPTH"'):]
        self.assertNotIn('mappedNormal(', prepass)
        self.assertIn('tex2D(_AlbedoMap, input.uv).a * _Alpha - _Cutoff', prepass)

    def test_gtao_uses_view_axis_horizons_and_keeps_legacy_variant(self):
        settings = (RUNTIME / 'SceneGtaoSettings.cs').read_text(encoding='utf-8')
        for token in ('public bool enabled;', 'slices = 4', 'stepsPerSide = 8', 'SceneAmbientCombination', 'normalBias', 'thicknessBlend'):
            self.assertIn(token, settings)
        source = (RUNTIME / 'SceneScreenShadowRenderer.cs').read_text(encoding='utf-8')
        self.assertIn('!hasMain && CapsuleCount == 0 && !_usesGtao', source)
        self.assertLess(source.index('Invalid GTAO configuration'), source.index('_usesGtao = g.strength > 0'))
        self.assertIn('_material.DisableKeyword("SCENE_GTAO")', source)
        shader = (RUNTIME / 'Resources/SceneScreenShadow.shader').read_text(encoding='utf-8')
        self.assertIn('#pragma multi_compile_local __ SCENE_GTAO', shader)
        self.assertIn('min(ambient, gtao) : ambient * gtao', shader)
        gtao = (RUNTIME / 'Resources/SceneGtao.hlsl').read_text(encoding='utf-8')
        for token in ('GtaoPrimitive(', 'GtaoArc(', 'GtaoHorizon(', 'abs(sin(theta))',
                      'World(sampleUv, depth)', 'sampleUv < 1', 'transverse > 0',
                      'blocked / _GtaoQuality.x', 'tangentClip.w', 'distance < _GtaoParameters.x', 'dot(delta, normal) > 0'):
            self.assertIn(token, gtao)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.Gtao.cs').read_text(encoding='utf-8')
        for token in ('numerical-slice-oracle', 'Math.Sin(theta)', 'thin-decay-reduces-overocclusion',
                      'world-unit-radius-scale-invariance', 'direct-and-emission-not-ao-darkened',
                      'GAKUMAS_SELFTEST_CAPTURE_GTAO', 'invalid-before-zero-strength-pruning'):
            self.assertIn(token, fixture)

    def test_gtao_half_produces_actual_coarse_receiver_data_and_owned_resources(self):
        settings = (RUNTIME / 'SceneGtaoSettings.cs').read_text(encoding='utf-8')
        self.assertIn('resolution = SceneGtaoResolution.Full', settings)
        source = (RUNTIME / 'SceneScreenShadowRenderer.cs').read_text(encoding='utf-8')
        for token in ('(target.width + 1) / 2', '(target.height + 1) / 2', 'GtaoCoarse.IsCreated()',
                      'GraphicsFormat.R32G32B32A32_SFloat', 'GtaoCoarseDrawCalls++', 'else ReleaseCoarse()',
                      'commands.DrawMesh(quad, Matrix4x4.identity, _coarseMaterial', 'FormatUsage.Sample'):
            self.assertIn(token, source)
        self.assertLess(source.index('Invalid GTAO configuration'), source.index('_usesHalf = _usesGtao'))
        self.assertLess(source.index('commands.SetRenderTarget(GtaoCoarse)'), source.index('commands.SetRenderTarget(Visibility)'))
        shader = (RUNTIME / 'Resources/SceneGtaoHalf.shader').read_text(encoding='utf-8')
        for token in ('g.a > 0', 'g.a < selected.a', 'p < _GtaoPixelSize.zw', 'float4(pixel, selected.a, GtaoVisibility'):
            self.assertIn(token, shader)

    def test_gtao_reconstruction_rejects_incompatible_guides_and_recomputes_unsupported_receivers(self):
        shader = (RUNTIME / 'Resources/SceneGtaoReconstruction.hlsl').read_text(encoding='utf-8')
        for token in ('y < 4', 'x < 4', 'dot(delta,normal)', 'dot(delta,n)', 'dot(n,normal)',
                      'weightSum <= 1e-6', 'return GtaoVisibility(uv,world,normal,depth)', 'return sum / weightSum'):
            self.assertIn(token, shader)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.GtaoSpatial.cs').read_text(encoding='utf-8')
        for token in ('independent-bilateral-whole-image-oracle', 'nearest-positive-depth-and-exact-source-pixel',
                      'perforated-disconnected-fallback', 'true-tangent-white-', 'two-camera-coarse-isolation',
                      'validate-before-zero-strength', 'full-resolution-capsule-combination-', 'GAKUMAS_SELFTEST_CAPTURE_GTAO_SPATIAL'):
            self.assertIn(token, fixture)

    def test_main_shadow_is_explicit_orthographic_and_owned_before_scene_geometry(self):
        settings = (RUNTIME / 'SceneDirectionalShadowSettings.cs').read_text(encoding='utf-8')
        for token in ('public bool enabled;', 'public Vector3 origin', 'public Vector3 up',
                      'public Vector2 halfSize', 'nearPlane', 'farPlane', 'SceneShadowCaster[] casters'):
            self.assertIn(token, settings)
        source = (RUNTIME / 'SceneLightShadowAtlas.cs').read_text(encoding='utf-8')
        for token in ('PrepareDirectional(', 'Matrix4x4.Ortho(', 'var forward = -direction.normalized',
                      '_depthPlane = new Vector4(forward.x', 'BindMain(Material material)',
                      'material.EnableKeyword("SCENE_SHADOW_ORTHOGRAPHIC")', 'settings.farPlane <= settings.nearPlane'):
            self.assertIn(token, source)
        host = (RUNTIME / 'SceneDeferredCamera.cs').read_text(encoding='utf-8')
        self.assertLess(host.index('_mainShadow?.Record(_commands)'), host.index('foreach (var rt in _gbuffer)'))
        self.assertIn('_mainShadow?.Dispose()', host)
        self.assertIn('material.DisableKeyword("SCENE_MAIN_LIGHT_SHADOWS")', host)
        self.assertIn('mainLightShadowDepth = owner._mainShadow?.Atlas', host)
        self.assertNotIn('Shader.SetGlobal', source)

    def test_main_shadow_uses_axial_world_plane_and_excludes_other_lamps_and_indirect(self):
        shader = (RUNTIME / 'Resources/SceneLightShadow.hlsl').read_text(encoding='utf-8')
        self.assertIn('float axial = dot(_ShadowDepthPlane', shader)
        self.assertIn('(axial - data.depth.z) / data.depth.y', shader)
        caster = (RUNTIME / 'Resources/SceneLightShadowCaster.shader').read_text(encoding='utf-8')
        self.assertIn('dot(_ShadowDepthPlane, world) / _ShadowFar', caster)
        scene = (RUNTIME / 'Resources/SceneDeferred.shader').read_text(encoding='utf-8')
        self.assertIn('#pragma multi_compile_local __ SCENE_MAIN_LIGHT_SHADOWS', scene)
        self.assertLess(scene.index('direct *= SceneLightVisibility'), scene.index('float3 indirect ='))
        self.assertLess(scene.index('direct *= SceneLightVisibility'), scene.index('direct += tex2D(_DecalLightAccumulation'))

    def test_scene_motion_is_opt_in_owned_and_follows_completed_camera_renders(self):
        settings = (RUNTIME / 'SceneMotionSettings.cs').read_text(encoding='utf-8')
        self.assertIn('public bool enabled;', settings)
        source = (RUNTIME / 'SceneMotionHistory.cs').read_text(encoding='utf-8')
        for token in ('!SystemInfo.supportsGeometryShaders', 'FormatUsage.Sample', 'source.isReadable',
                      'renderer.isPartOfStaticBatch', 'maximumTrackedVertices-TrackedVertices',
                      'GetIndices(surface.materialIndex)', 'entry.reusable=Continuous&&Compatible',
                      'entry.previousVertices=entry.currentVertices', 'ReleaseVertices(entry)',
                      'b.alphaMap is RenderTexture&&b.cutoff>0', 'a.revision!=b.revision'):
            self.assertIn(token, source)
        for token in ('skin.BakeMesh(', 'AsyncGPUReadback', 'Shader.SetGlobal', 'Time.frameCount'):
            self.assertNotIn(token, source)
        host = (RUNTIME / 'SceneDeferredCamera.cs').read_text(encoding='utf-8')
        self.assertLess(host.index('_motion?.Record(_commands)'), host.index('foreach (var rt in _gbuffer)'))
        self.assertIn('_motion?.Complete()', host)
        self.assertIn('_motion?.ResetHistory(); _prepared = _rendered = -1;', host)
        self.assertIn('_motion?.Motion == null || _motion.IsCreated', host)

    def test_scene_motion_saves_actual_renderer_vertices_and_never_accumulates_ao(self):
        shader = (RUNTIME / 'Resources/SceneMotion.shader').read_text(encoding='utf-8')
        for token in ('SV_VertexID', 'PointStream<Pixel>', 'maxvertexcount(6)', 'vertices[i].id*2+k',
                      'mul(unity_ObjectToWorld,v.vertex)', 'UnityObjectToWorldNormal(v.normal/_VertexScale)',
                      '_PreviousVertices.Load(', 'input.previousWorld.w>.9999', 'currentUv-previousUv',
                      'clip(tex2D(_AlphaMap,input.uv).a*_Alpha-_Cutoff)'):
            self.assertIn(token, shader)
        self.assertNotIn('_CameraMotionVectorsTexture', shader)
        self.assertNotIn('Gtao', shader)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.SceneMotion.cs').read_text(encoding='utf-8')
        for token in ('previous-projection-and-depth-oracle', 'alpha-holes-match-actual-scene-and-clear-history',
                      'submesh-base-vertex-correspondence', 'surface-reorder-preserves-two-visible-identities',
                      'skin-blendshape-deformation', 'skin-parent-shear', 'skin-shader-vertex-scale',
                      'reactivated-surface-invalid-history', 'two-camera-independent-snapshots',
                      'reset-invalidates-borrowed-frame', 'GAKUMAS_SELFTEST_CAPTURE_SCENE_MOTION'):
            self.assertIn(token, fixture)

    def test_temporal_gtao_requires_explicit_motion_and_owns_five_float_targets(self):
        settings = (RUNTIME / 'SceneGtaoTemporalSettings.cs').read_text(encoding='utf-8')
        self.assertIn('public bool enabled;', settings)
        for token in ('historyWeight = .85f', 'maximumHistory = 16', 'maximumFrameGap = 1',
                      'reactiveThreshold = .2f', '!float.IsNaN(v)', '!float.IsInfinity(v)'):
            self.assertIn(token, settings)
        source = (RUNTIME / 'SceneGtaoTemporalRenderer.cs').read_text(encoding='utf-8')
        for token in ('motion==null||!motion.IsCreated', 'new RenderTexture[2]', 'TargetCount => Raw==null?0:5',
                      't.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat', 'filterMode=FilterMode.Point',
                      'gap>=0&&gap<=_settings.maximumFrameGap', 'MatrixDifference(_projection,_previousProjection)<.1f',
                      '_read=_write', '_nextPhase=(Phase+1)%6', '(_previousProjection*_previousView).inverse'):
            self.assertIn(token, source)
        for forbidden in ('Shader.SetGlobal', '_CameraMotionVectorsTexture', 'motion.enabled=true', 'BakeMesh'):
            self.assertNotIn(forbidden, source)

    def test_temporal_gtao_pixel_geometry_rejection_and_stable_variance_contract(self):
        shader = (RUNTIME / 'Resources/SceneGtaoTemporal.shader').read_text(encoding='utf-8')
        for token in ('input.uv=input.position.xy/_TemporalSize.zw',
                      'precise float2 texel=floor(input.position.xy)-motion.xy*_TemporalSize.zw',
                      'data.a!=mapping.a', 'dot(n,expectedNormal)<_TemporalRejection.y',
                      'abs(dot(delta,expectedNormal))', 'if(total<=1e-6)return o',
                      'if(abs(oldAo-current)>_TemporalRejection.z)return o',
                      'float d=a-current;sum+=d;square+=d*d', 'square/count-deltaMean*deltaMean',
                      'oldAge/(oldAge+1)', 'min(oldAge,_TemporalHistory.z-1)*saturate(total)'):
            self.assertIn(token, shader)
        for forbidden in ('_CapsuleA', '_SingleShadow', '_CameraDepthTexture'):
            self.assertNotIn(forbidden, shader)

    def test_temporal_gtao_is_separate_from_default_shader_and_capsules(self):
        source = (RUNTIME / 'SceneScreenShadowRenderer.cs').read_text(encoding='utf-8')
        self.assertLess(source.index('!g.temporal.IsValid'), source.index('_usesGtao = g.strength > 0'))
        self.assertIn('!_usesGtao||g.temporal==null||!g.temporal.enabled', source)
        self.assertIn('Temporal?.Dispose();Temporal=null', source)
        self.assertLess(source.index('Temporal.Record('), source.index('_material.SetTexture("_GtaoHistory",Temporal.Result)'))
        shader = (RUNTIME / 'Resources/SceneScreenShadow.shader').read_text(encoding='utf-8')
        self.assertIn('#pragma multi_compile_local __ SCENE_GTAO_HISTORY', shader)
        self.assertLess(shader.index('float gtao = tex2D(_GtaoHistory'), shader.index('ambient = _GtaoMinimumAmbient'))
        host = (RUNTIME / 'SceneDeferredCamera.cs').read_text(encoding='utf-8')
        self.assertIn('_screenShadow?.Temporal?.Complete()', host)
        self.assertIn('_screenShadow?.Temporal?.ResetHistory(); _prepared = _rendered = -1;', host)

    def test_temporal_gtao_fixture_declares_actual_quality_and_lifecycle_controls(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.GtaoTemporal.cs').read_text(encoding='utf-8')
        for token in ('whole-image-reproject-reject-clip-age-oracle', 'six-rotations-match-fresh-twelve-direction-integral-',
                      'stationary-temporal-error-improves-', 'stationary-temporal-variation-reduces-',
                      'uncovered-white-reference-has-bounded-ghost', 'alpha-cutout-disocclusion',
                      'two-camera-independent-history', 'skin-deform-motion-and-history-nonvacuous-',
                      'full-resolution-capsules-combined-after-history-', 'direct-emission-identical-without-indirect',
                      'default-full-rg8-and-hdr-restored-exact', 'invalid-before-zero-strength-pruning',
                      'GAKUMAS_SELFTEST_CAPTURE_GTAO_TEMPORAL', 'disable-releases-all-history'):
            self.assertIn(token, fixture)

    def test_scene_taa_is_explicit_owned_and_preserves_legacy_temporal_shader(self):
        settings = (RUNTIME / 'SceneTemporalAntialiasingSettings.cs').read_text(encoding='utf-8')
        for token in ('public bool enabled;', 'historyWeight = .95f', 'maximumHistory = 32',
                      'maximumFrameGap = 1', 'public uint contentRevision', '!float.IsNaN(x)', '!float.IsInfinity(x)'):
            self.assertIn(token, settings)
        source = (RUNTIME / 'SceneTemporalAntialiasingRenderer.cs').read_text(encoding='utf-8')
        for token in ('motion==null||!motion.IsCreated', 'TargetCount => VisibleGeometry==null?0:8',
                      'SystemInfo.supportedRenderTargetCount<3', 'sequence-_lastSequence==1',
                      'source.useDynamicScale', 'Owns(source)', 'RenderTexture.active=active',
                      '_hasResult&&_lastSequence==sequence', 'gap<=settings.maximumFrameGap',
                      'settings.contentRevision>>16', '(_previousProjection*_previousView).inverse'):
            self.assertIn(token, source)
        for token in ('Shader.SetGlobal', '_CameraMotionVectorsTexture', 'BakeMesh', 'camera.projectionMatrix='):
            self.assertNotIn(token, source)

    def test_scene_taa_visibility_and_classification_are_camera_local(self):
        renderer = (RUNTIME / 'SceneTemporalAntialiasingRenderer.cs').read_text(encoding='utf-8')
        self.assertIn('BuiltinRenderTextureType.CurrentActive', renderer)
        self.assertIn('commands.ClearRenderTarget(false,true', renderer)
        host = (RUNTIME / 'SceneDeferredCamera.cs').read_text(encoding='utf-8')
        for token in ('CameraEvent.BeforeImageEffects', 'mask.IsCreated()',
                      'classification.jitterUv!=_temporalAntialiasing.PreparedJitter',
                      'TryGetTemporalColorFrame', 'ResetTemporalColorHistory',
                      '_temporalAntialiasing.HasResult(_sequence)'):
            self.assertIn(token, host)
        pipeline = (RUNTIME / 'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('TryResolveTemporalColor(_sourceCamera,current,temporalClassification,out temporal)', pipeline)
        self.assertIn('Graphics.Blit(current, temporal, _postMaterial, 7)', pipeline)
        self.assertLess(pipeline.index('TryResolveTemporalColor('), pipeline.index('ApplyDepthOfField(temporal, temporaries)'))

    def test_scene_taa_point_depth_ray_and_hdr_variance_contract(self):
        shader = (RUNTIME / 'Resources/SceneTemporalAntialiasing.shader').read_text(encoding='utf-8')
        for token in ('ZTest LEqual ZWrite Off', 'SV_Target2', 'precise float2 currentPixel=floor(input.position.xy)',
                      'GuideUv(currentUv)-motion.xy', 'GuideUv(tapUv-_Jitter.zw)',
                      'meta.r!=id', 'dot(n,oldNormal)<_Rejection.y', 'Flags(currentUv)',
                      'square/count-deltaMean*deltaMean', 'age/(age+1)',
                      'current*wc+clipped*wh', 'max(wc+wh,1e-20)', 'clamp(c,0,65504)'):
            self.assertIn(token, shader)
        self.assertNotIn('_CameraMotionVectorsTexture', shader)
        self.assertNotIn('_CameraDepthTexture', shader)

    def test_scene_taa_fixture_has_nonvacuous_quality_motion_and_lifecycle_controls(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.SceneTaa.cs').read_text(encoding='utf-8')
        for token in ('temporal-improves-over-dejitter-only-bilinear-control', 'zero-history-is-only-explicit-current-resampling',
                      'temporal-reduces-dejittered-adjacent-frame-variation', 'sloped-jitter-history-nonvacuous-',
                      'skin-bone-blendshape-color-history-nonvacuous-', 'newly-visible-different-identity-rejects-history',
                      'updated-alpha-content-rejects-old-correspondence', 'jittered-flag-semantics-',
                      'hdr-compression-does-not-cap-bright-constant', 'missed-resolve-invalidates-sequence-history',
                      'two-camera-independent-color-history', 'released-attachment-invalidates-lease-',
                      'actual-production-post-consumes-completed-scene', 'component-disable-releases-color-history',
                      'GAKUMAS_SELFTEST_CAPTURE_SCENE_TAA'):
            self.assertIn(token, fixture)

if __name__ == '__main__':
    unittest.main()
