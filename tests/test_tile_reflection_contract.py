"""Source guards; actual SRP rendering is exercised by the standalone Player fixture."""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
RUNTIME=ROOT/'packages/com.digital-kotone.toolkit/Runtime'

class TileReflectionContractTests(unittest.TestCase):
    def source(self,name='SrpTileReflection.cs'):
        return (RUNTIME/name).read_text(encoding='utf-8')

    def test_explicit_host_and_content_contract(self):
        source=self.source()
        for value in ('public bool enabled;', 'sceneOnlyInput', '!scene.IsRecorded', 'scene.Camera!=Camera', 'sequence<=lastSequence', 'ReferenceEquals(scene,source)'):
            self.assertIn(value,source)
        for value in ('Camera.Render(', 'context.Submit(', 'Graphics.Blit(', 'GraphicsSettings.renderPipelineAsset='):
            self.assertNotIn(value,source)

    def test_exports_do_not_add_attachments(self):
        source=self.source('TileSceneRenderer.cs')
        for value in ('target=settings.materialBase, store=settings.materialBase!=null', 'target=settings.materialMos, store=settings.materialMos!=null', '_complete = true', '_complete && !_disposed'):
            self.assertIn(value,source)
        self.assertEqual(source.count('new TileRenderPass.Attachment {'),6)

    def test_history_uses_uncomposited_scene(self):
        source=self.source()
        self.assertIn('commands.Blit(scene.Color,historyColor)',source)
        self.assertNotIn('commands.Blit(output,historyColor)',source)
        for value in ('sequence==lastSequence+1', 'sceneRevision==revision', 'MatrixDistance(projection,historyProjection)', 'historyOrtho==scene.Orthographic'):
            self.assertIn(value,source)

    def test_compute_records_commands_and_owns_instance(self):
        source=self.source()
        self.assertIn('UnityEngine.Object.Instantiate(asset)',source)
        for value in ('SetComputeTextureParam', 'SetComputeMatrixParam', 'DispatchCompute', '(width+7)/8', 'BackendFallbackReason'):
            self.assertIn(value,source)
        self.assertNotIn('compute.Dispatch(',source)

    def test_raster_filter_materials_are_independent(self):
        source=self.source()
        self.assertIn('axis.x>0?filterX:filterY',source)
        self.assertIn('material.CopyPropertiesFromMaterial(trace)',source)

    def test_depth_background_and_shared_trace_are_explicit(self):
        shader=self.source('Resources/TileReflection.shader')
        self.assertIn('depth>0?min(depth,_TileOptions.x):_TileOptions.x',shader)
        self.assertIn('Material("ScreenSpaceReflection")',self.source())
        self.assertIn('commands.Blit(depth[level-1],depth[level],reduce,1)',self.source())

    def test_material_and_geometry_are_distinct(self):
        shader=self.source('Resources/TileReflection.shader')
        for value in ('_TileMos.Load', '_TileGeometry.Load', '_TileBase.Load', 'delta.xz*_TileDistortion.xy', 'Group(other)-Group(n.a)', 'lerp(.04,'):
            self.assertIn(value,shader)
        self.assertIn('Properties { [HideInInspector] _MainTex',shader)

    def test_budget_and_texture_lifetime(self):
        source=self.source()
        for value in ('EstimateBytes(w,h)', 'Owns(t)', 't.graphicsFormat!=formats[i]', 't.useDynamicScale', 'ReleaseTargets()', 'source.IsRecorded && TargetsAlive()'):
            self.assertIn(value,source)

    def test_fixture_contains_real_positive_and_negative_controls(self):
        fixture=(ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.TileReflection.cs').read_text(encoding='utf-8')
        for value in ('analytic-two-color-wall-interior', 'whole-ceil-min-hierarchy', 'whole-two-axis-world-space-filter', 'queued-two-axis-raster-compute-equivalence',
                      'mapped-normal-does-not-drive-trace', 'post-decal-roughness-disables-ssr', 'rough-probe-mip', 'missing-material-export', 'lost-target-recovery'):
            self.assertIn(value,fixture)

if __name__=='__main__':
    unittest.main()
