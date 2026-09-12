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


if __name__ == '__main__':
    unittest.main()
