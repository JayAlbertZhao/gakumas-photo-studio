"""Public source/ABI guards; numerical GPU/bake evidence is produced by Player."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class BakedShadowContractTests(unittest.TestCase):
    def source(self, name):
        return (RUNTIME / name).read_text(encoding='utf-8')

    def test_explicit_borrowed_inputs_validate_linear_uv2(self):
        text = self.source('SceneBakedShadowInput.cs')
        for term in ('None, Constant, Texture, RendererLightmap', 'None, R, G, B, A',
                     'if (!Enabled) return true', 'VertexAttribute.TexCoord1',
                     'GraphicsFormatUtility.IsSRGBFormat', 'maps[index].shadowMask',
                     'renderer.lightmapScaleOffset', 'rt.antiAliasing != 1'):
            self.assertIn(term, text)
        self.assertNotIn('Destroy(', text)

    def test_optional_attachment_preserves_existing_mrt_slots(self):
        text = self.source('SceneDeferredCamera.cs')
        for term in ('GraphicsFormat.R8G8_UNorm', 'filterMode = FilterMode.Point',
                     'targets[4] = _bakedMask', 'targets[bakedMask ? 5 : 4] = _gi',
                     'SystemInfo.supportedRenderTargetCount < (_usesGi ? 6 : 5)',
                     'mask == target || mask == _bakedMask', 'ReleaseBakedMask()',
                     '(_bakedMask != null && _bakedMask.IsCreated())'):
            self.assertIn(term, text)

    def test_byte_encoding_has_no_temporal_dependency(self):
        text = self.source('Resources/SceneBakedShadow.hlsl')
        for term in ('float3(7,7,3)', 'float3(32,4,1)', 'floor(bytes.y/32)/7',
                     'fmod(floor(bytes.y/4),8)/7', 'fmod(bytes.y,4)/3'):
            self.assertIn(term, text)
        self.assertNotIn('_Time', text)
        self.assertIn('g * 32 + b * 4 + a', self.source('SceneBakedShadowInput.cs'))

    def test_separate_current_channel_table_and_unchanged_light_stride(self):
        text = self.source('SceneBakedShadowChannels.cs')
        for term in ('lights[i].bakedShadowChannel', 'if (!Active) { Dispose(); return; }',
                     'new ComputeBuffer(capacity, 4)', 'buffer.SetData(channels)'):
            self.assertIn(term, text)
        self.assertIn('bakedChannels.Prepare(snapshot.PreparedSources, true)', self.source('SceneForwardLightResources.cs'))
        self.assertIn('lights.count * 144', self.source('SceneForwardLightResources.cs'))

    def test_shared_forward_consumers_compile_explicit_keywords(self):
        for name in ('SceneForwardLighting.shader', 'LowResolutionFxLit.shader', 'HeavyFxLit.shader'):
            text = self.source('Resources/' + name)
            for program in text.split('CGPROGRAM')[1:]:
                program = program.split('ENDCG')[0]
                self.assertIn('SCENE_BAKED_SHADOW_INPUT', program)
                self.assertIn('SCENE_BAKED_LIGHT_CHANNELS', program)
        forward = self.source('Resources/SceneForwardLighting.hlsl')
        self.assertIn('SceneBakedSample(input.uv2)', forward)
        self.assertIn('min(maskVisibility, SceneLightVisibility', forward)
        self.assertIn('min(ForwardMainVisibility', forward)
        self.assertIn('min(maskVisibility, realtimeVisibility)', self.source('Resources/SceneDecalLight.hlsl'))

    def test_bake_is_explicit_copy_and_records_actual_channels(self):
        text = (RUNTIME.parent / 'Editor/SceneGiBaker.cs').read_text(encoding='utf-8')
        for term in ('public bool shadowMask;', 'AssetDatabase.CopyAsset(sourceScene, destination)',
                     'if (options.shadowMask) settings.mixedBakeMode = MixedLightingMode.Shadowmask',
                     'options.shadowMask ? LightmapBakeType.Mixed : LightmapBakeType.Baked',
                     'light.bakingOutput.occlusionMaskChannel', 'asset.sha256 == asset.sha256After'):
            self.assertIn(term, text)

    def test_real_geometry_and_negative_controls_are_retained(self):
        text = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.BakedMask.cs').read_text(encoding='utf-8')
        for term in ('actual-all-pixels-byte-', 'independent-dither-entire-target-',
                     'independent-five-lights-GI-emission-', 'fractional-PCF-reject-double-multiply-',
                     'near-unmasked-cutout-writes-white', 'current-native-UV2-skin-',
                     'GAKUMAS_SELFTEST_BAKED_MASK_BUNDLE', 'actual-four-distinct-mixed-channels-',
                     'actual-light-consumer-entire-target-', 'remove-authored-occluder-restores-light-',
                     'feedback-rejection-invalidates-borrowed-frame'):
            self.assertIn(term, text)


if __name__ == '__main__':
    unittest.main()
