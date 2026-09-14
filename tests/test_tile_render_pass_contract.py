"""API/source guards only; real attachment and numerical checks require a Player."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class TileRenderPassContractTests(unittest.TestCase):
    def setUp(self):
        self.source = (ROOT / 'packages/com.digital-kotone.toolkit/Runtime/TileRenderPass.cs').read_text(encoding='utf-8')
        self.fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.TilePass.cs').read_text(encoding='utf-8')

    def test_explicit_host_and_order(self):
        self.assertIn('public bool enabled;', self.source)
        self.assertIn('backend = BackendPolicy.RequireNative', self.source)
        self.assertLess(self.source.index('if (!Validate(plan'), self.source.index('context.BeginRenderPass('))
        self.assertIn('context.BeginSubPass(colors, inputs', self.source)
        self.assertIn('context.ExecuteCommandBuffer(_commands)', self.source)
        self.assertIn('context.EndSubPass()', self.source)
        self.assertIn('context.EndRenderPass()', self.source)
        self.assertNotIn('context.Submit();', self.source)
        self.assertNotIn('GraphicsSettings.renderPipelineAsset =', self.source)

    def test_lifetime_and_budget_are_explicit(self):
        for item in ('ConfigureTarget(input.target', 'ConfigureClear(input.clearColor, input.clearDepth',
                     'clearDepth = 1', 'chargePackedHdrAs64Bits = true', 'maximumColorTileBits = 256',
                     'maximumAttachmentMiB', 'maximumDraws', 'Transient attachments cannot load or store',
                     'targets.Add(target)', 'colors.Contains(index)', 'HasPropertyBlock()',
                     'GetTexturePropertyNames()', 'finally { attachments.Dispose(); }'):
            self.assertIn(item, self.source)
        self.assertNotIn('target.Release()', self.source)
        self.assertNotIn('Destroy(', self.source)

    def test_native_and_emulation_are_distinct(self):
        self.assertIn('GraphicsDeviceType.Vulkan', self.source)
        self.assertIn('GraphicsDeviceType.Metal', self.source)
        self.assertIn('emulation must be explicit', self.source)
        self.assertIn('Metal needs a color depth attachment', self.source)
        self.assertIn('not a claim about tile residency or measured cost', self.source)

    def test_actual_request_and_full_suite(self):
        self.assertIn('RenderPipeline.SubmitRenderRequest(camera,request)', self.fixture)
        self.assertIn('context.SetupCameraProperties(camera); value.record(context); context.Submit();', self.fixture)
        full = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertIn('--self-test-tile-render-pass', full)
        self.assertGreater(full.rindex('VerifyTileRenderPass(report)'), full.index('VerifyActorAuthoredMaps(report)'))
        self.assertIn('GraphicsSettings.renderPipelineAsset=previousGraphics', self.fixture)
        self.assertIn('QualitySettings.renderPipeline=previousQuality', self.fixture)
        self.assertIn('Graphics.SetRenderTarget(previousActive)', self.fixture)
        self.assertIn('SystemInfo.renderingThreadingMode', self.fixture)

    def test_counterfactuals_and_independent_whole_fields(self):
        for case in ('wrong-logical-depth-clear', 'swapped-inputs', 'srgb-unorm-midpoint',
                     'odd-spatial-depth-cutout', 'depth-order-independent', 'borrowed-renderer',
                     'preserve-across-gap', 'all-external-stored', 'load-prior-producer',
                     'clear-replaces-stale', 'renderer-property-block', 'property-block-alias',
                     'invalid-plans-preserve-borrowed-outputs', 'borrowed-lifetimes-retained'):
            self.assertIn('"'+case+'"' if case not in ('invalid-plans-preserve-borrowed-outputs', 'borrowed-lifetimes-retained') else 'tile-pass-'+case, self.fixture)
        self.assertIn('finite==values.Length*4', self.fixture)
        self.assertIn('Math.Floor(value/step)*step', self.fixture)
        self.assertIn('Math.Ceiling(value/step)*step', self.fixture)
        self.assertIn('Array.TrueForAll(values,p=>p.Equals(values[0]))', self.fixture)

if __name__ == '__main__':
    unittest.main()
