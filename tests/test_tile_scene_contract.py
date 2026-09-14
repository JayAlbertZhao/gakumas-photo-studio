"""Source/API guards; these do not substitute for actual SRP/native rendering evidence."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class TileSceneContractTests(unittest.TestCase):
    def test_explicit_one_shot_and_borrowed_outputs(self):
        source = (ROOT/'packages/com.digital-kotone.toolkit/Runtime/TileSceneRenderer.cs').read_text(encoding='utf-8')
        for token in ('public bool enabled;', 'TryPrepare(Camera camera', 'PreparedFrame : IDisposable',
                      '_disposed || _recorded', 'SceneDeferredCamera.BindInputs', 'surface.gi.Bind',
                      'SceneBakedShadowInput.Bind', 'TileRenderPass.Validate', 'depthReadOnly=true',
                      'inputs=new[]{0,1,2,4}', 'inputs=new[]{3,2}', 'store=true'):
            self.assertIn(token, source)
        self.assertNotIn('new RenderTexture(', source)
        self.assertNotIn('GraphicsSettings.renderPipelineAsset =', source)
        self.assertNotIn('context.Submit()', source)

    def test_material_light_and_inline_mask_contract(self):
        shader = (ROOT/'packages/com.digital-kotone.toolkit/Runtime/Resources/TileScene.shader').read_text(encoding='utf-8')
        for token in ('SceneGi(i.uv2,n)', 'SceneBakedPack(SceneBakedSample(i.uv2)', 'SceneBakedUnpack(float2(base.a,mos.a))',
                      'unity_ObjectToWorld', '1+_ReceiverGroup', 'Blend One One', 'float3(65024,65024,64512)',
                      'float4(xy,0,1)', 'TILE_SCENE_FINAL_REUSE_GI'):
            self.assertIn(token, shader)

    def test_scope_rejections_and_whole_frame_checks(self):
        source = (ROOT/'packages/com.digital-kotone.toolkit/Runtime/TileSceneRenderer.cs').read_text(encoding='utf-8')
        self.assertIn('leaf transmission is not integrated', source)
        self.assertIn('currently supports desktop Vulkan', source)
        self.assertIn('finite camera-ray endpoints', source)
        fixture = (ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.TileScene.cs').read_text(encoding='utf-8')
        for token in ('TileSceneCpuLight', 'TileScenePackedNeighbors', 'whole-independent-light', 'whole-normal-identity',
                      'rejection-preserves-output', 'perspective-depth-view', 'mirrored-normal-basis',
                      'uv2-baked-gi', 'inline-baked-mask-', 'restores-default-host'):
            self.assertIn(token, fixture)

if __name__ == '__main__':
    unittest.main()
