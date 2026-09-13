"""Source/API guardrails. Actual full-image and animation checks run in the Player."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'
APP = ROOT / 'unity/Assets/Applications/PhotoStudio'


class FaceDecalContractTests(unittest.TestCase):
    def test_opt_in_camera_adapter_uses_full_size_opaque_depth(self):
        source = (RUNTIME / 'FaceDecalLayer.cs').read_text(encoding='utf-8')
        for value in ('public bool decalsEnabled;', 'if(!decalsEnabled)',
                      'CameraEvent.BeforeForwardAlpha', 'commands?.Clear()',
                      'target.antiAliasing>1', 'cameraOwner.stereoEnabled',
                      'cameraOwner.cullingMask', 'rendererOwner.Record(commands)',
                      'cameraOwner.RemoveCommandBuffer'):
            self.assertIn(value, source)
        self.assertNotIn('RenderTexture.GetTemporary', source)

    def test_owned_materials_explicit_surfaces_and_reserved_blocks(self):
        source = (RUNTIME / 'FaceDecalRenderer.cs').read_text(encoding='utf-8')
        for value in ('projectors.Length > 8', 'receivers.Length > 128',
                      'new Material(shader)', 'surface=new SceneDepthData.Surface',
                      'RequireReceiverBlock', 'slotBlock.isEmpty?rendererBlock:slotBlock',
                      'SceneDepthData.ValidSurface', 'RequireGeometry',
                      'ObjectDisposedException', 'Duplicate receiver submesh'):
            self.assertIn(value, source)
        for forbidden in ('Shader.SetGlobal', '.SetPropertyBlock(', '.sharedMaterial =',
                          'BakeMesh(', '.GetData(', '.vertices', 'Graphics.Blit'):
            self.assertNotIn(forbidden, source)

    def test_overlay_never_writes_depth_alpha_or_stencil(self):
        source = (RUNTIME / 'Resources/AuthoredFaceDecal.shader').read_text(encoding='utf-8')
        for value in ('ZWrite Off ZTest Equal', 'Blend One OneMinusSrcAlpha, Zero One',
                      'ColorMask RGB', 'WriteMask 0', 'Pass Keep',
                      'ColorMask 0', 'PRESERVE_EXISTING_PLANAR_COVERAGE',
                      'result.rgb=result.rgb*(1-alpha)+color*alpha'):
            self.assertIn(value, source)

    def test_plain_component_fields_and_atomic_typed_sampling(self):
        component = (RUNTIME / 'FaceDecalProjector.cs').read_text(encoding='utf-8')
        animation = (RUNTIME / 'FaceDecalAnimation.cs').read_text(encoding='utf-8')
        self.assertIn('public float uvRotationDegrees;', component)
        self.assertIn('public Data Snapshot()', component)
        self.assertIn('public bool TryApplyPose', component)
        for value in ('TrySample(FaceDecalPose baseline, double seconds', 'pose = null',
                      'baseline.Copy()', 'channels.Add(track.channel)', '8192',
                      'FaceDecalInterpolation.Hermite', 'time %= cycle',
                      'FaceDecalRenderer.Validate'):
            self.assertIn(value, animation)
        self.assertNotIn('Time.', animation)
        self.assertNotIn('Random.', animation)

    def test_planar_is_append_only_and_does_not_replace_actor_draws(self):
        source = (RUNTIME / 'FaceDecalRenderer.cs').read_text(encoding='utf-8')
        fixture = (APP / 'FaceDecalCharacterValidation.cs').read_text(encoding='utf-8')
        self.assertIn('coverageShaderPass=1', source)
        self.assertIn('actors.Draws.Concat(decals.PlanarDraws())', fixture)
        self.assertIn('planar-whole-coverage-exact', fixture)

    def test_real_character_suite_is_explicit_and_not_preset_only(self):
        fixture = (APP / 'FaceDecalCharacterValidation.cs').read_text(encoding='utf-8')
        for value in ('--validate-face-decal-character', 'shape<face.ShapeCount',
                      'face.SetDebugShape(shape,.75f)', 'face.GpuDeformationEnabled=true',
                      'positive-overlay', 'off-restored', 'camera.Render()',
                      'a.Length', 'c<4', 'sum/(a.Length*4)'):
            self.assertIn(value, fixture)
        self.assertNotIn('percentile', fixture)

    def test_player_acceptance_has_actual_clip_skin_and_gpu_geometry(self):
        fixture = (APP / 'ActorRenderingSelfTest.FaceDecals.cs').read_text(encoding='utf-8')
        for value in ('clip.SampleAnimation', 'animation-seek-image-exact',
                      'populated-stencil-matches', 'populated-stencil-mismatch',
                      'reserved-block-rejected-not-overridden', 'matching-cutout-depth',
                      'foreground-occlusion', 'SkinnedMeshRenderer', 'GpuFaceDeformer.TryCreate',
                      'gpu-cpu-copy-remains-rest', 'all-28-channels',
                      'curve-overshoot-rejected-not-clamped'):
            self.assertIn(value, fixture)

    def test_docs_state_equations_lifetimes_and_unmeasured_scope(self):
        source = (ROOT / 'docs/animated-face-decals.md').read_text(encoding='utf-8')
        for value in ('[-0.5, 0.5]³', 'AlphaModulated', 'Clear／Dispose',
                      'Maya', '移动端', 'GPU 帧时', '128', '8192'):
            self.assertIn(value, source)


if __name__ == '__main__':
    unittest.main()
