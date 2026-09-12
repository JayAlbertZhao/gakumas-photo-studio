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
        source = (RUNTIME / 'Resources/ScreenSpaceReflection.shader').read_text(encoding='utf-8')
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


if __name__ == '__main__':
    unittest.main()
