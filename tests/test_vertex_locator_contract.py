"""Source/API guardrails; current CPU/GPU positions and images are checked in the Player."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class VertexLocatorContractTests(unittest.TestCase):
    def test_sampling_is_explicit_and_not_gpu_readback(self):
        source = (RUNTIME / 'VertexDeformationSampler.cs').read_text(encoding='utf-8')
        for value in ('MaxSelectedVertices = 768', 'selected.Clone()', 'non-shape-major order',
                      'Float32(sum + Float32', 'vertex.inverseWeight', 'out Vector3[] positions',
                      'positions = null', 'Mathf.Abs(weights[delta.shape]) >= .0001f'):
            self.assertIn(value, source)
        for forbidden in ('Resources.Load', 'GetData(', 'AsyncGPUReadback', 'BakeMesh(', 'Time.', '.vertices'):
            self.assertNotIn(forbidden, source)

    def test_face_bridge_consumes_applied_weights_and_owned_current_output(self):
        source = (RUNTIME / 'FaceExpressionRenderer.VertexLocators.cs').read_text(encoding='utf-8')
        for value in ('face._lastWeights, face._skinMatrices', 'ReferenceEquals(topologyIdentity',
                      'face._filter.sharedMesh != expected', 'face._cpuBlendNeedsRefresh',
                      'rig.Hide()', 'Matrix4x4.Scale(vertexScale)', 'sampler = null'):
            self.assertIn(value, source)
        for forbidden in ('.vertices', 'GetData(', 'AsyncGPUReadback', 'BakeMesh(', 'ApplyCurrentWeights()'):
            self.assertNotIn(forbidden, source)

    def test_rig_is_owned_atomic_and_uses_current_world_triangle(self):
        source = (RUNTIME / 'VertexLocatorRig.cs').read_text(encoding='utf-8')
        for value in ('definitions[i] = d.Copy()', 'locators.Length <= 256', 'root.SetActive(false)',
                      'Vector3.Cross(edge, c - a)', 'Quaternion.LookRotation', 'worldPositions',
                      'Position order/count must match', 'Degenerate locator frame',
                      'targets[i].parent == root.transform', 'disposed = true'):
            self.assertIn(value, source)
        self.assertNotIn('FindObjectsOfType', source)

    def test_actual_probe_checks_latch_gpu_and_whole_image(self):
        source = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.VertexLocators.cs').read_text(encoding='utf-8')
        for value in ('ReadFaceGpu(gpu.Mesh)', 'actual-gpu-position', 'absolute-seek-full-image-exact',
                      'gpu-cpu-copy-is-still-rest', 'subthreshold-latch', 'threshold-crossing-current',
                      'source-does-not-advance-pose', 'current-output', 'positive-markers', 'camera.Render()',
                      'independent-owner-full-image-exact', 'rotation-offset'):
            self.assertIn(value, source)

    def test_character_validation_is_explicit_all_shapes_and_actual_buffers(self):
        source = (ROOT / 'unity/Assets/Applications/PhotoStudio/VertexLocatorCharacterValidation.cs').read_text(encoding='utf-8')
        for value in ('--validate-vertex-locator-character', 'shape < face.ShapeCount',
                      'ReadGpuPositions(filter.sharedMesh)', 'buffer.GetData(data)', 'maximum == 0',
                      'visible-markers', 'attachment-cpu-gpu', 'disposed-source-no-stale-markers',
                      'proofSource.TrySample', 'position-change-classification', 'positive-world-follow',
                      'report.positionChangingShapes + report.positionStaticShapes == face.ShapeCount'):
            self.assertIn(value, source)

    def test_documentation_discloses_provider_scope_and_order(self):
        source = (ROOT / 'docs/vertex-locators.md').read_text(encoding='utf-8')
        for value in ('1e-4', '1e-5', '最后实际应用', 'SkinnedMeshRenderer', 'Maya', '移动帧时',
                      '先释放', '世界单位', '768', 'GPU 顶点缓冲'):
            self.assertIn(value, source)


if __name__ == '__main__':
    unittest.main()
