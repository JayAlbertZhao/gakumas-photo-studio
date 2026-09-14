"""Source/API guards, separate from real SRP and native whole-field validation."""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
RUNTIME=ROOT/'packages/com.digital-kotone.toolkit/Runtime'

class TileDecalContractTests(unittest.TestCase):
    def test_default_path_and_explicit_capabilities(self):
        source=(RUNTIME/'TileSceneRenderer.cs').read_text(encoding='utf-8')
        for text in ('public bool geometryDepthId;', 'Array.Empty<SceneDeferredCamera.Decal>()',
                     'supportsSeparatedRenderTargetsBlend', 'FormatUsage.Blend', 'D32_SFloat_S8_UInt',
                     'budget.nominalBytes+position.Budget.nominalBytes', 'reflectionExcludedLayers'):
            self.assertIn(text,source)
        self.assertNotIn('context.Submit()',source)
        self.assertNotIn('GraphicsSettings.renderPipelineAsset =',source)

    def test_geometric_normal_has_no_normal_map_dependency(self):
        shader=(RUNTIME/'Resources/TileGeometryDepthId.shader').read_text(encoding='utf-8')
        for text in ('UnityObjectToWorldNormal(v.normal/_VertexScale)', 'tex2D(_AlbedoMap,i.uv).a*_Alpha-_Cutoff',
                     '_ReceiveReflections*step', 'tex2D(_MosMap,i.uv).b*_Mos.b', 'SV_Target1'):
            self.assertIn(text,shader)
        self.assertNotIn('_NormalMap',shader)

    def test_prepass_owns_separate_outputs_and_no_implicit_lights(self):
        source=(RUNTIME/'TileScenePositionResources.cs').read_text(encoding='utf-8')
        for text in ('GraphicsFormat.R32_SFloat', 'GraphicsFormat.R8G8B8A8_UNorm',
                     'if(!settings.positionLighting)return true;', 'GeometryDepthId.Release()', 'plan.subpasses[0].colors=new[]{0,1}'):
            self.assertIn(text,source)

    def test_decal_draws_share_geometry_subpass(self):
        source=(RUNTIME/'TileSceneRenderer.cs').read_text(encoding='utf-8')
        self.assertIn('geometry.AddRange(TileSceneDecals.Prepare',source)
        self.assertNotIn('passes.Insert(',source)
        decal=(RUNTIME/'TileSceneDecals.cs').read_text(encoding='utf-8')
        self.assertIn('for(int pass=1;pass<=3;pass++)',decal)
        self.assertNotIn('new RenderTexture',decal)
        self.assertNotIn('new TileRenderPass.Subpass',decal)

    def test_zero_is_exact_and_invalid_weights_cannot_hide(self):
        source=(RUNTIME/'TileSceneDecals.cs').read_text(encoding='utf-8')
        self.assertIn('d.mosWeight.x!=0||d.mosWeight.y!=0||d.mosWeight.z!=0',source)
        self.assertNotIn('d.mosWeight!=Vector3.zero',source)
        for text in ('d.receiverGroup<1||d.receiverGroup>255', 'd.localToWorld.m33!=1', '!Unit(d.mosWeight.x)',
                     'values.Length>256', 'rt.antiAliasing!=1'):
            self.assertIn(text,source)

    def test_stencil_and_write_masks_preserve_gi_and_metadata(self):
        source=(RUNTIME/'Resources/TileSceneDecal.shader').read_text(encoding='utf-8')
        self.assertEqual(source.count('ColorMask 0 4'),3)
        self.assertEqual(source.count('Blend 4 Off'),3)
        self.assertEqual(source.count('Comp Equal Pass Keep ReadMask 255 WriteMask 0'),3)
        self.assertIn('Comp Always Pass Replace ReadMask 255 WriteMask 255',source)
        for text in ('ColorMask R 1', 'ColorMask G 1', 'ColorMask B 1', 'ColorMask RGB 2'):
            self.assertIn(text,source)

    def test_stamped_geometry_matches_existing_authored_shader(self):
        old=(RUNTIME/'Resources/TileScene.shader').read_text(encoding='utf-8')
        new=(RUNTIME/'Resources/TileSceneDecal.shader').read_text(encoding='utf-8')
        self.assertEqual(old.split('CGPROGRAM',1)[1].split('ENDCG',1)[0],new.split('CGPROGRAM',1)[1].split('ENDCG',1)[0])

    def test_projector_reads_completed_geometry_only(self):
        source=(RUNTIME/'Resources/TileSceneDecal.hlsl').read_text(encoding='utf-8')
        for text in ('_SceneEyeDepth.Load(pixel)', '_GeometryDepthId.Load(pixel)', 'clip(.5-abs(box))',
                     'o.normal=float4(n,weight)', 'TILE_DECAL_OCCLUSION', 'TILE_DECAL_SMOOTHNESS'):
            self.assertIn(text,source)
        self.assertNotIn('FRAMEBUFFER_INPUT',source)
        self.assertNotIn('_SceneGBuffer',source)

    def test_real_fixture_and_counterexamples_remain(self):
        source=(ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.TileDecal.cs').read_text(encoding='utf-8')
        for text in ('ViewportPointToRay', 'whole-current-depth', 'whole-reflection-mask', 'whole-group-gi-preserved',
                     'independent-weights', 'overlap-order-matters', 'coplanar-group-255', 'cutout-visible-groups',
                     'gi-shadow-metadata', 'live-srp-monitor-update', 'tiny-negative-mos-weight', 'output-feedback'):
            self.assertIn(text,source)

if __name__=='__main__':
    unittest.main()
