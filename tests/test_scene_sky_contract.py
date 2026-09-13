"""Public architecture guards, not a replacement for the real Player image suite."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class SceneSkyContractTests(unittest.TestCase):
    def source(self, name):
        return (RUNTIME / name).read_text(encoding='utf-8')

    def test_explicit_disabled_authored_inputs(self):
        text = self.source('SceneSkySettings.cs')
        for term in ('public bool enabled;', 'Gradient, Cubemap, Equirectangular',
                     'Quaternion.identity', 'Repeat U and Clamp V',
                     'GraphicsFormatUtility.IsSRGBFormat', 'texture.mipmapCount - 1',
                     'rt.IsCreated()', '!rt.useDynamicScale'):
            self.assertIn(term, text)
        self.assertNotIn('Destroy(', text)

    def test_native_material_does_not_change_global_lighting(self):
        for name in ('SceneSkyMaterial.cs', 'SceneSkyCamera.cs', 'SceneSkyCapture.cs'):
            text = self.source(name)
            for forbidden in ('RenderSettings.', 'DynamicGI.', 'Shader.SetGlobal',
                              'ReadPixels', 'GetPixels', 'AsyncGPUReadback'):
                self.assertNotIn(forbidden, text)
        self.assertIn('CameraClearFlags.Skybox', self.source('SceneSkyCamera.cs'))
        self.assertNotIn('camera.clearFlags =', self.source('SceneSkyCamera.cs'))

    def test_explicit_native_six_face_hdr_producer(self):
        text = self.source('SceneSkyCapture.cs')
        for term in ('camera.RenderToCubemap(cube, 63)', 'TextureDimension.Cube',
                     'GraphicsFormat.R16G16B16A16_SFloat', 'camera.cullingMask = 0',
                     'camera.enabled = false', 'autoGenerateMips = false',
                     'cube.GenerateMips()', 'if (capturing)', 'settings.texture == cube',
                     'owner.generation == generation', 'radiance.IsCreated()'):
            self.assertIn(term, text)
        self.assertNotIn('Camera.main', text)

    def test_tiny_sun_and_far_depth_contract(self):
        text = self.source('Resources/SceneSkybox.shader')
        for term in ('ZWrite Off ZTest LEqual', 'UNITY_REVERSED_Z',
                     'distanceSquared', 'atan2(direction.x, direction.z)',
                     'texCUBElod', 'tex2Dlod', '65504), 1'):
            self.assertIn(term, text)
        self.assertNotIn('_Time', text)
        self.assertIn('outerChord * outerChord', self.source('SceneSkyMaterial.cs'))

    def test_current_cube_can_reach_both_existing_consumers(self):
        for term in ('public Cubemap reflectionProbe;', 'public RenderTexture reflectionCapture;',
                     'reflectionCapture ?? (Texture)reflectionProbe'):
            self.assertIn(term, self.source('SceneWaterSettings.cs'))
        for name in ('SceneWaterRenderer.cs', 'SceneReflectionResolve.cs'):
            text = self.source(name)
            self.assertIn('TextureDimension.Cube', text)
            self.assertIn('IsCreated()', text)
            self.assertIn('useDynamicScale', text)

    def test_real_camera_and_consumer_negative_controls_remain(self):
        text = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.SceneSky.cs').read_text(encoding='utf-8')
        for term in ('default-disabled-exact', 'latlong-entire-image-seam-pole-',
                     'native-opaque-cutout-depth-entire-image', 'real-capture-face-full-image-',
                     'native-generated-mip-', 'producer-update-actual-resolve-consumption',
                     'producer-update-actual-water-consumption', 'water-rejects-lost-cube',
                     'resolve-rejects-lost-cube', 'reject-output-feedback-releases'):
            self.assertIn(term, text)


if __name__ == '__main__':
    unittest.main()
