"""DOF public API/default-source boundaries; actual GPU behavior is a Player fixture."""
from pathlib import Path
import hashlib
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class BokehContractTests(unittest.TestCase):
    def test_module_has_explicit_inputs_and_no_application_discovery(self):
        renderer = (RUNTIME / 'BokehDepthOfFieldRenderer.cs').read_text(encoding='utf-8')
        shader = (RUNTIME / 'Resources/BokehDepthOfField.shader').read_text(encoding='utf-8')
        for forbidden in ('FindObjectsOfType', 'PhotoModeApp', 'BundleCatalog', 'SetGlobal', '_CameraDepthTexture'):
            self.assertNotIn(forbidden, renderer + shader)
        for required in ('IDisposable', 'RenderTexture linearDepth', 'Owns(source)', 'Owns(linearDepth)',
                         'RenderTextureFormat.RGB111110Float', 'RenderTextureFormat.RFloat',
                         '_generation', 'IsCreated()', 'UnavailableReason'):
            self.assertIn(required, renderer)

    def test_full_sampling_budgets_and_correct_near_channel(self):
        shader = (RUNTIME / 'Resources/BokehDepthOfField.shader').read_text(encoding='utf-8')
        kernel = (RUNTIME / 'BokehKernel.cs').read_text(encoding='utf-8')
        for required in ('#define SAMPLE_COUNT 29', '#define SAMPLE_COUNT 42',
                         'SAMPLE_COUNT + 1.0', 'GatherAlpha', '4.0, true', '8.0, false'):
            self.assertIn(required, shader)
        self.assertEqual(shader.count('#pragma multi_compile_local __ BOKEH_30'), 2)
        self.assertNotIn('tex2D(', shader)
        self.assertIn('_MainTex.SampleLevel(sampler_LinearClamp', shader)
        self.assertIn('ring==1?7:ring==2?9:13', kernel)
        self.assertIn('x*reciprocalAspect', kernel)

    def test_old_43_sample_body_and_kernel_are_unchanged(self):
        pipeline = (RUNTIME / 'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        legacy = pipeline.split('if (!_depthOfFieldActive || _depthOfFieldMaterial == null) return source;', 1)[1].split('public void SetDepthOfField(', 1)[0]
        self.assertEqual(hashlib.sha256(legacy.encode()).hexdigest(),
                         '0a754d273aa546db414e2bcec689adde56ef7fd92cb388c8f6de506d936109a5')
        self.assertIn('public bool enabled;', (RUNTIME / 'BokehDepthOfFieldSettings.cs').read_text(encoding='utf-8'))

    def test_post_bridge_is_explicit_after_temporal_and_before_bloom(self):
        source = (RUNTIME / 'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('public Func<Camera, RenderTexture, RenderTexture> bokehDepthProvider;', source)
        self.assertIn('bokehDepthProvider(_sourceCamera,source)', source)
        self.assertLess(source.index('ApplyDepthOfField(temporal, temporaries)'), source.index('Graphics.Blit(postInput, first'))
        self.assertIn('bokehDepthOfField!=null&&bokehDepthOfField.enabled', source)
        self.assertIn('Bokeh depth provider failed:', source)


if __name__ == '__main__':
    unittest.main()
