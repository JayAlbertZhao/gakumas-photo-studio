"""FSR source/API boundaries. Pixel, camera and performance proof is in actual Player acceptance."""
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / 'packages/com.digital-kotone.toolkit'
RUNTIME = PACKAGE / 'Runtime'


class FsrContractTests(unittest.TestCase):
    def source(self, path):
        return (RUNTIME / path).read_text(encoding='utf-8')

    def test_default_off_explicit_quality_encoding_and_memory(self):
        source = self.source('FsrSettings.cs')
        for term in ('public bool enabled;', 'UltraQuality, Quality, Balanced, Performance',
                     'LinearLdr, PerceptualGamma2Ldr, LinearHdr', '1.3f', '1.5f', '1.7f',
                     'Mathf.CeilToInt', 'memoryBudgetMiB', 'accurateRcasNormalization = true'):
            self.assertIn(term, source)

    def test_external_algorithm_includes_and_not_vendoring(self):
        source = self.source('Resources/FsrShared.hlsl')
        for name in ('ffx_a.hlsl', 'ffx_fsr1.hlsl'):
            self.assertIn('Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/ffx/' + name, source)
            self.assertFalse((RUNTIME / 'Resources' / name).exists())
        for term in ('FsrEasuCon(', 'FsrEasuF(rgb,', 'FsrRcasCon(', 'FsrRcasF(value.r', 'ToolkitFsrRcasReciprocal'):
            self.assertIn(term, source)
        package = json.loads((PACKAGE / 'package.json').read_text(encoding='utf-8'))
        self.assertIn('com.unity.render-pipelines.core', package['dependencies'])
        self.assertIn('Advanced Micro Devices', (PACKAGE / 'ThirdPartyNotices.md').read_text(encoding='utf-8'))

    def test_gradient_noise_floor_is_explicit_and_default_variant_is_preserved(self):
        settings = self.source('FsrSettings.cs')
        shared = self.source('Resources/FsrShared.hlsl')
        renderer = self.source('FsrRenderer.cs')
        self.assertIn('public bool stabilizeLumaGradients;', settings)
        self.assertIn('#if defined(TOOLKIT_FSR_STABLE_GRADIENT)', shared)
        self.assertIn('APrxLoRcpF1(max(value,2.0/4096.0))', shared)
        self.assertIn('#undef APrxLoRcpF1', shared)
        self.assertIn('_compute.DisableKeyword("TOOLKIT_FSR_STABLE_GRADIENT")', renderer)
        self.assertIn('_material.DisableKeyword("TOOLKIT_FSR_STABLE_GRADIENT")', renderer)
        self.assertIn('stabilizeLumaGradients = settings.stabilizeLumaGradients', self.source('FsrCameraRenderer.cs'))
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.DesktopFsr.cs').read_text(encoding='utf-8')
        for token in ('identical-source-cross-backend', 'one-ulp-input-stability',
                      'unmodified-variant-ulp-response-metric', 'gradient-floor-reduces-worst-ulp-amplification'):
            self.assertIn(token, fixture)

    def test_production_has_no_readback_or_asset_discovery(self):
        for name in ('FsrRenderer.cs', 'FsrCameraRenderer.cs', 'FsrSettings.cs'):
            source = self.source(name)
            for forbidden in ('ReadPixels(', 'GetData(', 'AsyncGPUReadback', 'BakeMesh(', 'BundleCatalog', 'File.Read', 'SetGlobal', 'FindObjectsOfType'):
                self.assertNotIn(forbidden, source)

    def test_owned_targets_budget_and_lifetime(self):
        source = self.source('FsrRenderer.cs')
        self.assertLess(source.index('bytes > settings.memoryBudgetMiB'), source.index('Allocate(input, output, chosen)'))
        for term in ('Owns(source)', '_generation++', 'ReleaseTargets()', 'source.mipmapCount != 1',
                     'GraphicsFormatUtility.IsSRGBFormat', 'GL.sRGBWrite = srgbWrite', 'RenderTexture.active = active',
                     'allowRasterFallback', 'UnityEngine.Object.Instantiate(compute)'):
            self.assertIn(term, source)

    def test_real_lower_resolution_camera_and_restoration(self):
        source = self.source('FsrCameraRenderer.cs')
        self.assertLess(source.index('camera.targetTexture = _input'), source.index('camera.Render();'))
        for term in ('camera.targetTexture = previousTarget', 'camera.aspect = aspect', 'GraphicsSettings.currentRenderPipeline',
                     'GraphicsFormat.D24_UNorm_S8_UInt', 'Active.Remove(camera)', 'TryResolveTemporalColor(', '_settings = new FsrSettings'):
            self.assertIn(term, source)
        self.assertNotIn('camera.enabled =', source)
        self.assertNotIn('camera.cullingMask =', source)
        self.assertNotIn('camera.projectionMatrix =', source)

    def test_explicit_before_diffusion_and_single_bloom(self):
        source = self.source('OriginalStyleRenderPipeline.cs')
        self.assertLess(source.index('fsrRequest.TryBeforeDiffusion'), source.index('int capturedBlurWidth'))
        self.assertIn('fsrBeforeDiffusion ? (Texture)Texture2D.blackTexture : bloom', source)
        self.assertIn('requestedSceneTaa && !sceneColorResolved', source)
        self.assertIn('fsrRequest.MarkPresented()', source)
        self.assertIn('Properties { _MainTex', self.source('Resources/FsrBloomComposite.shader'))

    def test_real_fixtures_cover_whole_pixels_and_actual_temporal_camera(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.Fsr.cs').read_text(encoding='utf-8')
        for term in ('FsrScalar(', 'whole-easu-rgba', 'whole-backends', 'constant-signal-preserved',
                     'budget-before-allocation', 'own-output-alias-rejected', 'float.PositiveInfinity'):
            self.assertIn(term, fixture)
        camera = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.FsrCamera.cs').read_text(encoding='utf-8')
        for term in ('referenceCamera.Render()', 'historyWeight = 0', 'step < 48', 'step >= 32',
                     'quality-reference-4x', 'all-quality-taa-reduces-dejitter-fsr-reference-error'):
            self.assertIn(term, camera)


if __name__ == '__main__':
    unittest.main()
