"""Structure/ownership guards; actual HDR/native acceptance runs in the Player."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class CrowdLightsticksContractTests(unittest.TestCase):
    def source(self, name):
        return (RUNTIME / name).read_text(encoding='utf-8')

    def test_explicit_clock_and_borrowed_linear_mask(self):
        text = self.source('CrowdLightsticks.cs')
        for term in ('public bool enabled;', 'public Texture mask;', 'GraphicsFormatUtility.IsSRGBFormat',
                     'rt.IsCreated()', '!rt.useDynamicScale', 'double timeSeconds', 'Math.Floor(cycles)',
                     'if (!Enabled(source)) { Dispose(); return; }'):
            self.assertIn(term, text)
        for forbidden in ('Time.time', 'UnityEngine.Random', 'ReadPixels', 'GetData(', 'Destroy('):
            self.assertNotIn(forbidden, text)

    def test_placement_abi_and_selection_not_widened(self):
        selection = self.source('CrowdSelection.cs')
        self.assertIn('public Vector4 positionScale, rotationType, tint;', selection)
        self.assertIn('Instances = New(Capacity, 48,', selection)
        self.assertNotIn('lightstick', selection.lower())
        renderer = self.source('CrowdRenderer.cs')
        self.assertLess(renderer.index('CrowdLightsticks.Bytes(source)'), renderer.index('lightsticks.Prepare(source'))
        self.assertIn('mask != target && !Owns(mask)', renderer)
        self.assertIn('selection.Dispose(); lightsticks.Dispose();', renderer)

    def test_current_atlas_mask_and_per_instance_consumer(self):
        capture = self.source('Resources/CrowdCapture.shader')
        draw = self.source('Resources/CrowdDraw.shader')
        self.assertIn('o.emission.a=saturate(tex2D(_CrowdLightstickMask,input.uv).r)', capture)
        self.assertIn('mask=tex2D(_EmissionMap,atlasUv).a', draw)
        self.assertIn('lightstickMask*_CrowdLightstickRadiance[input.id].rgb', draw)
        self.assertEqual(draw.count('#pragma multi_compile_local __ CROWD_LIGHTSTICKS'), 2)
        self.assertNotIn('SV_Target4', capture)

    def test_real_image_reference_and_lifecycle_controls(self):
        text = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.CrowdLightsticks.cs').read_text(encoding='utf-8')
        for term in ('whole-hdr-independent-mask', 'current-gpu-cpu-whole-hdr', 'all-four-mask-atlas-texels-independent-rgb',
                     'current-bone-shape-', 'caller-clock-pause-exact', 'disable-releases-optional-stream-exact',
                     'atlas-feedback', 'srgb-mask', 'reject-lost-borrowed-mask', 'native-current-mixed-indirect'):
            self.assertIn(term, text)


if __name__ == '__main__':
    unittest.main()
