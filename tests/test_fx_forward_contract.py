"""API/source safeguards; numerical acceptance uses actual Unity Player fixtures."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class FxForwardContractTests(unittest.TestCase):
    def source(self, path):
        return (RUNTIME / path).read_text(encoding='utf-8')

    def test_common_settings_preserve_camera_api_and_defaults(self):
        settings = self.source('SceneForwardLightingSettings.cs')
        self.assertIn('SceneForwardLightingSettings : SceneForwardLightSettings', settings)
        self.assertIn('public bool enabled;', settings)
        self.assertIn('public bool enabled;', self.source('FxSurfaceLighting.cs'))
        self.assertIn('if (!Active) { Dispose(); return true; }', self.source('FxForwardLightingBinding.cs'))

    def test_both_producers_prepare_explicit_source_size(self):
        for file, binding in [('LowResolutionFxRenderer.cs', 'lighting'), ('HeavyFxRenderer.cs', 'surfaceLighting')]:
            code = self.source(file)
            self.assertIn(binding + '.Prepare(camera,source.width,source.height,', code)
            self.assertIn(binding + '.Dispose()', code)
            self.assertIn('LightingBufferBytes', code)
            self.assertIn('LightingFallbackReason', code)
        resources = self.source('SceneForwardLightResources.cs')
        self.assertNotIn('camera.targetTexture', resources)
        self.assertIn('commands.DispatchCompute(', resources)

    def test_fragment_reuses_lighting_with_current_full_source_pixel(self):
        code = self.source('Resources/FxForwardSurface.hlsl')
        for term in ('ForwardVertex(input)', 'ForwardFragment(surface)', 'surface.position=float4(fullPixel,0,1)',
                     'surface.normal=input.normal', 'surface.uv2=input.uv2', 'material.rgb*fx.rgb*_FxRadiance.rgb', 'fx.a*material.a'):
            self.assertIn(term, code)

    def test_separate_surface_and_medium_shadow_namespaces(self):
        code = self.source('Resources/HeavyFxLit.shader')
        for term in ('FX_LIT_LOCAL_SHADOWS', 'FX_LIT_MAIN_SHADOWS', 'FX_MEDIUM_SHADOWS',
                     '_FxMediumShadowAtlas', '_FxMediumShadowMatrix', 'FxLitAdditionalMedium', 'value.a*alpha'):
            self.assertIn(term, code)
        self.assertIn('FX_MEDIUM_SHADOWS', self.source('HeavyFxRenderer.cs'))
        binding = self.source('FxForwardLightingBinding.cs')
        for getter in ('material.GetTexture(', 'material.GetMatrix(', 'material.GetVector('):
            self.assertNotIn(getter, binding)

    def test_actual_geometry_and_no_cpu_render_substitute(self):
        for name in ('SceneForwardLightResources.cs', 'FxForwardLightingBinding.cs', 'HeavyFxRenderer.cs', 'LowResolutionFxRenderer.cs'):
            code = self.source(name)
            for forbidden in ('BakeMesh(', 'GetData(', 'ReadPixels(', 'AsyncGPUReadback', '.sharedMaterial =', '.cullingMask ='):
                self.assertNotIn(forbidden, code)
        for name in ('HeavyFxRenderer.cs', 'LowResolutionFxRenderer.cs'):
            self.assertIn('commands.DrawRenderer(', self.source(name))
            self.assertIn('commands.DrawMesh(', self.source(name))

    def test_invalid_lit_surface_fails_closed(self):
        code = self.source('FxForwardLightingBinding.cs')
        for term in ('Lit distortion', 'VertexAttribute.Normal', 'VertexAttribute.Tangent', 'VertexAttribute.TexCoord0',
                     'VertexAttribute.Color', 'r.HasPropertyBlock()', 'SceneGiSource.RendererLightmap', 'rt.IsCreated()'):
            self.assertIn(term, code)

    def test_rgb_only_replay_preserves_source_alpha(self):
        for name in ('Resources/LowResolutionFxLit.shader', 'Resources/HeavyFxLit.shader'):
            self.assertIn('Zero One ColorMask RGB', self.source(name))
        code = self.source('Resources/FxForwardSurface.hlsl')
        self.assertIn('fog.rgb*material.a', code)
        self.assertIn('FxLitMedium(color,input.world,fullPixel,material.a)', code)

    def test_public_fixture_names_define_real_acceptance_scope(self):
        code = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.FxForward.cs').read_text(encoding='utf-8')
        for term in ('camera.Render()', 'tiled-brute-whole-rgba', 'independent-camera-full-rgba', 'independent-deformation-whole-rgba',
                     '4096', 'forced-repair-', 'stale-monitor-rejected', 'actual-camera-bridge-direct-consumer-whole-rgba',
                     'independent-unlit-whole-rgba'):
            self.assertIn(term, code)


if __name__ == '__main__':
    unittest.main()
