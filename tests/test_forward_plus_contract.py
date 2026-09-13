"""Public source/API boundaries; actual compute/geometry/numerics execute in Unity's self-test."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class ForwardPlusContractTests(unittest.TestCase):
    def source(self, name):
        return (RUNTIME / name).read_text(encoding='utf-8')

    def test_opt_in_camera_does_not_mutate_host_or_materials(self):
        camera = self.source('SceneForwardLightingCamera.cs')
        settings = self.source('SceneForwardLightingSettings.cs')
        self.assertIn('public bool enabled;', settings)
        self.assertIn('CameraEvent.BeforeForwardAlpha', camera)
        self.assertNotIn('.cullingMask =', camera)
        self.assertNotIn('.sharedMaterial =', camera)
        self.assertIn('Host culling mask must exclude owned transparent layers', camera)

    def test_actual_compute_then_actual_current_geometry(self):
        camera = self.source('SceneForwardLightingCamera.cs')
        resources = self.source('SceneForwardLightResources.cs')
        self.assertIn('commands.DispatchCompute(', resources)
        self.assertIn('_lighting.Record(_commands)', camera)
        self.assertIn('_commands.DrawRenderer(s.renderer, material, s.submesh, 0)', camera)
        self.assertLess(camera.index('_lighting.Record(_commands)'), camera.index('_commands.DrawRenderer('))
        for forbidden in ('GetData(', 'AsyncGPUReadback', 'BakeMesh(', 'FindObjectsOfType'):
            self.assertNotIn(forbidden, camera)
            self.assertNotIn(forbidden, resources)

    def test_all_light_bits_written_without_append_capacity_loss(self):
        compute = self.source('Resources/SceneForwardLightGrid.compute')
        shader = self.source('Resources/SceneForwardLighting.hlsl')
        self.assertIn('RWStructuredBuffer<uint> _ForwardTiles', compute)
        self.assertIn('light >= (uint)_ForwardLightCount', compute)
        self.assertIn('= mask;', compute)
        self.assertNotIn('AppendStructuredBuffer', compute)
        self.assertNotIn('_CameraDepthTexture', compute)
        self.assertIn('firstbitlow(mask)', shader)
        self.assertIn('mask &= mask - 1', shader)
        self.assertIn('word * 32 + bit', shader)

    def test_bruteforce_fallback_keeps_same_fragment_evaluation(self):
        shader = self.source('Resources/SceneForwardLighting.hlsl')
        resources = self.source('SceneForwardLightResources.cs')
        self.assertIn('ForwardLocal(word * 32 + bit', shader)
        self.assertIn('ForwardLocal(index', shader)
        self.assertIn('settings.allowBruteForceFallback', resources)
        self.assertIn('(long)tilesX * tilesY * words', resources)
        self.assertIn('elements * 4 <= (long)settings.maximumGridMiB', resources)

    def test_owned_buffers_and_commands_release(self):
        camera = self.source('SceneForwardLightingCamera.cs')
        resources = self.source('SceneForwardLightResources.cs')
        self.assertIn('lights?.Dispose(); lights = null', resources)
        self.assertIn('tiles?.Dispose(); tiles = null', resources)
        self.assertIn('_lighting.Dispose()', camera)
        self.assertIn('_camera.RemoveCommandBuffer(', camera)
        self.assertIn('_commands?.Dispose(); _commands = null', camera)

    def test_transparent_depth_and_alpha_contract(self):
        shader = self.source('Resources/SceneForwardLighting.shader')
        self.assertIn('ZTest LEqual ZWrite Off', shader)
        self.assertIn('Blend One [_DestinationBlend], One [_DestinationBlend]', shader)
        source = self.source('Resources/SceneForwardLighting.hlsl')
        self.assertIn('input.world', source)
        self.assertIn('SceneGi(input.uv2, n)', source)
        self.assertIn('_Additive > .5 ? 0 : alpha', source)
        self.assertNotIn('tex2D(_G0', source)

    def test_shared_light_snapshot_is_separate_from_deferred_allocation(self):
        snapshot = self.source('SceneDecalLightRenderer.cs')
        self.assertIn('internal bool PrepareSnapshot(', snapshot)
        self.assertIn('return PrepareDeferred(camera, settings, out error)', snapshot)
        camera = self.source('SceneForwardLightingCamera.cs')
        self.assertIn('_lighting.Prepare(_camera, settings, _camera.targetTexture.width, _camera.targetTexture.height', camera)
        self.assertIn('snapshot.PrepareSnapshot(', self.source('SceneForwardLightResources.cs'))
        self.assertNotIn('RenderTexture.GetTemporary', camera)
        self.assertNotIn('new RenderTexture(', camera)

    def test_documentation_keeps_platform_and_integration_limits_explicit(self):
        text = (ROOT / 'docs/scene-forward-plus.md').read_text(encoding='utf-8')
        for term in ('4096', '256', 'BruteForce', 'BeforeForwardAlpha', 'LowResolutionFx', 'HeavyFx', 'Metal', 'Vulkan', 'CPU'):
            self.assertIn(term, text)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.ForwardPlus.cs').read_text(encoding='utf-8')
        for term in ('buffer.GetData(actual)', 'independent-pbr-shapes-alpha-full-image', '4096th-changed', 'gpu-grid-every-word'):
            self.assertIn(term, fixture)


if __name__ == '__main__':
    unittest.main()
