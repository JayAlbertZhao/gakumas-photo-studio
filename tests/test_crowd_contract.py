"""Crowd API/source guards; actual raster/compute correctness belongs to Player acceptance."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class CrowdContractTests(unittest.TestCase):
    def source(self, name):
        return (RUNTIME / name).read_text(encoding='utf-8')

    def test_default_off_and_bounded_authoring_contract(self):
        code = self.source('CrowdDefinition.cs')
        for term in ('public bool enabled;', 'MaximumPrototypes = 8', 'MaximumInstances = 65536',
                     'CrowdDefinition : ScriptableObject', 'public ulong contentVersion', 'public float scale = 1', 'public Vector3 tint = Vector3.one'):
            self.assertIn(term, code)

    def test_actual_indirect_arguments_and_stable_gpu_compaction(self):
        code = self.source('Resources/CrowdSelection.compute')
        for term in ('void Classify', 'void Bitonic', 'void ScanBlocks', 'void ScanTotals', 'void Scatter',
                     'RWBuffer<uint> _CrowdArguments', '_CrowdArguments[bucket*5+1]=scan[lane]', 'a.x==b.x&&a.y<b.y'):
            self.assertIn(term, code)
        self.assertIn('ComputeBufferType.IndirectArguments', self.source('CrowdSelection.cs'))
        self.assertIn('BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value))', self.source('CrowdSelection.cs'))
        self.assertEqual(self.source('CrowdRenderer.cs').count('commands.DrawMeshInstancedIndirect('), 2)

    def test_current_prototype_pose_is_not_bake_or_readback(self):
        for name in ('CrowdRenderer.cs', 'CrowdCamera.cs', 'CrowdSelection.cs', 'CrowdPrototypeGeometry.cs'):
            code = self.source(name)
            for forbidden in ('BakeMesh(', 'GetData(', 'ReadPixels(', 'AsyncGPUReadback', '.sharedMaterial =', '.cullingMask ='):
                self.assertNotIn(forbidden, code)
        pose = self.source('CrowdPrototypeGeometry.cs')
        for term in ('commands.DispatchCompute(', 'GetBonesPerVertex()', 'GetAllBoneWeights()', 'counts[i] <= 4', 'GetBlendShapeFrameCount(s) == 1',
                     'pose.bones[b].localToWorldMatrix * bindposes[b]', 'DeformCpu()'):
            self.assertIn(term, pose)

    def test_generated_material_views_and_current_fragment_lighting(self):
        code = self.source('CrowdRenderer.cs')
        self.assertIn('Vector3.back, Vector3.right, Vector3.forward, Vector3.left', code)
        self.assertIn('commands.DrawMesh(geometry[i].Mesh', code)
        self.assertIn('SceneForwardLightResources', code)
        shader = self.source('Resources/CrowdDraw.shader')
        for term in ('ForwardFragment(surface)', '_CrowdNormalDepth', 'SV_Depth', 'SV_Target1', 'CrowdDirection(facing,item.rotationType.xy)'):
            self.assertIn(term, shader)
        self.assertIn('#if defined(TOOLKIT_FORWARD_TOON)', self.source('Resources/SceneForwardLighting.hlsl'))

    def test_budget_before_geometry_and_gpu_allocation(self):
        code = self.source('CrowdRenderer.cs')
        budget = code.index('Require(required <=')
        self.assertLess(budget, code.index('new CrowdPrototypeGeometry(MeshFor'))
        self.assertLess(budget, code.index('geometry[i].Allocate()'))
        for term in ('!Owns(target)', 'input.allowCpuFallback', 'p.gi.source == SceneGiSource.Probe', 'texture.dimension == TextureDimension.Tex2D'):
            self.assertIn(term, code)

    def test_camera_order_lifetime_and_no_global_depth_claim(self):
        code = self.source('CrowdCamera.cs')
        self.assertIn('CameraEvent.AfterForwardOpaque', code)
        self.assertIn('view.RemoveCommandBuffer(', code)
        self.assertIn('LinearEyeDepth = null', code)
        self.assertIn('not _CameraDepthTexture', code)
        self.assertIn('commands.SetRenderTarget(destination)', self.source('CrowdRenderer.cs'))

    def test_real_acceptance_not_count_only(self):
        code = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.Crowd.cs').read_text(encoding='utf-8')
        for term in ('camera.Render()', 'current-native-skin-whole-rgba', 'every-gpu-cpu-vertex-attribute',
                     'every-stable-gpu-rank', 'all-indirect-argument-words', 'every-live-compact-id-no-duplicates',
                     '10000', 'CrowdDefinition.MaximumInstances - 1', 'mapped-real-camera-native-whole-rgba',
                     'shadow-near-real-native-camera-whole-rgba', 'independent-native-capture-whole-rgba',
                     'requested-10000-native-gpu-capture', 'syncInclusiveRenderReadbackMs='):
            self.assertIn(term, code)


if __name__ == '__main__':
    unittest.main()
