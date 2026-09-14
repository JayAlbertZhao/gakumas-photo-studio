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

    def test_position_bridge_is_separate_and_budgeted(self):
        source=(ROOT/'packages/com.digital-kotone.toolkit/Runtime/TileSceneRenderer.cs').read_text(encoding='utf-8')
        bridge=(ROOT/'packages/com.digital-kotone.toolkit/Runtime/TileScenePositionResources.cs').read_text(encoding='utf-8')
        for token in ('public bool positionLighting;', 'DepthBudget', 'EyeDepth', '_position.Validate',
                      'budget.nominalBytes+position.Budget.nominalBytes', 'require explicit positionLighting'):
            self.assertIn(token,source)
        for token in ('GraphicsFormat.R32_SFloat', 'GraphicsFormat.D32_SFloat', 'SceneForwardLightResources',
                      'lights.Record(commands)', 'lights.Bind(Lighting)', 'surface.vertexScale', 'surface.alphaCutoff'):
            self.assertIn(token,bridge)
        self.assertNotIn('context.Submit()',bridge)

    def test_position_consumer_reuses_light_equations(self):
        shader=(ROOT/'packages/com.digital-kotone.toolkit/Runtime/Resources/TileScenePosition.shader').read_text(encoding='utf-8')
        for token in ('_SceneEyeDepth.Load', 'TOOLKIT_FORWARD_EVALUATION_ONLY', 'ForwardLocalForReceiver',
                      'ForwardMainVisibility', 'SceneBakedUnpack', 'normal.a-1-', 'firstbitlow', '(depth-da)/(db-da)'):
            self.assertIn(token,shader)
        self.assertNotIn('FRAMEBUFFER_INPUT_FLOAT(4)',shader)

    def test_position_fixture_cannot_pass_on_far_ray_only(self):
        fixture=(ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.TilePosition.cs').read_text(encoding='utf-8')
        for token in ('2.2f,4.1f', 'Quaternion.Euler(7,13,0)', 'whole-real-depth', 'whole-independent-position-light',
                      'SceneDecalLightShape.Area', 'SceneDecalLightShape.Spot', '110-lights-brute', '110-lights-grid'):
            self.assertIn(token,fixture)

if __name__ == '__main__':
    unittest.main()
