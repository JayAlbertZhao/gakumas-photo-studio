"""Water API/source guardrails. Numerical and geometry evidence runs in Player."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class WaterContractTests(unittest.TestCase):
    def source(self, name):
        return (RUNTIME / name).read_text(encoding='utf-8')

    def test_default_off_and_explicit_inputs(self):
        source = self.source('SceneWaterSettings.cs')
        for term in ('public bool enabled;', 'public double seconds;', 'MaximumSurfaces = 64',
                     'maximumTargetMiB', 'PlanarReflection planarReflection', 'Cubemap reflectionProbe'):
            self.assertIn(term, source)

    def test_no_discovery_readback_shared_writes(self):
        source = self.source('SceneWaterRenderer.cs')
        for forbidden in ('ReadPixels(', 'GetData(', 'AsyncGPUReadback', 'BakeMesh(', 'SetGlobal',
                          'FindObjectsOfType', '.sharedMaterial =', '.cullingMask =', 'File.Read'):
            self.assertNotIn(forbidden, source)
        self.assertNotIn('GrabPass', self.source('Resources/SceneWater.shader'))

    def test_lights_before_current_geometry_and_distinct_feedback(self):
        source = self.source('SceneWaterRenderer.cs')
        self.assertLess(source.index('lighting.Record(commands)'), source.index('commands.DrawRenderer('))
        self.assertIn('lighting.Bind(m)', source)
        self.assertIn('s.gi.Bind(m,s.renderer', source)
        self.assertIn('var next=current==a?b:a', source)
        self.assertIn('Owns(source)', source)
        self.assertIn('Owns(settings.lighting?.localLights?.atlas as RenderTexture)', source)
        self.assertIn('Owns(s.gi.lightmap as RenderTexture)', source)
        self.assertIn('generation++', source)
        self.assertIn('lighting.Dispose()', source)

    def test_transmission_rejects_foreground_per_tap(self):
        source = self.source('Resources/SceneWater.shader')
        for term in ('WaterDepth(tap)<eye-_WaterInput.y', 'any(tap<0)', '!FogFinite3(color)',
                     'result/total:fallback', 'exp(-_WaterAbsorption*thickness)', 'WaterPlanar(',
                     'ForwardFragment(input)', '_WaterTransformSign', '_WaterPixelAxes', 'ZTest Always ZWrite Off Blend Off'):
            self.assertIn(term, source)

    def test_planar_extension_preserves_old_default(self):
        source = self.source('PlanarReflection.cs')
        self.assertIn('public bool allowExcludedLayer;', source)
        self.assertIn('!receiver.allowExcludedLayer &&', source)

    def test_actual_camera_independent_image_reference(self):
        source = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.Water.cs').read_text(encoding='utf-8')
        for term in ('camera.Render()', 'camera.ViewportPointToRay(', 'Math.Exp(', 'Math.Pow(',
                     'actual-opaque-foreground-exact', 'explicit-tangent-handedness-',
                     'time-replay-whole-image-exact', 'independent-current-PBR-local-shapes'):
            self.assertIn(term, source)


if __name__ == '__main__':
    unittest.main()
