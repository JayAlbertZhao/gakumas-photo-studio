"""Source/API boundary checks; GPU and local-character acceptance run separately."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class GpuFaceContract(unittest.TestCase):
    def test_independent_core_has_no_private_format_or_cpu_readback(self):
        text = (RUNTIME / 'GpuFaceDeformer.cs').read_text(encoding='utf-8')
        for forbidden in ('VL.', 'Campus.', 'GetData(', 'ReadPixels(', 'Shader.GetGlobal', 'Time.time'):
            self.assertNotIn(forbidden, text)
        for required in ('TryCreate(', 'TryDispatch(', 'LastDispatchSucceeded', 'BufferBytes', 'UnityEngine.Object.Instantiate(asset)'):
            self.assertIn(required, text)

    def test_current_owned_streams_and_bounds_precede_dispatch(self):
        text = (RUNTIME / 'GpuFaceDeformer.cs').read_text(encoding='utf-8')
        self.assertIn('mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw', text)
        self.assertNotIn('mesh.MarkDynamic();', text)
        self.assertLess(text.index('mesh.bounds = bounds;'), text.index('mesh.GetVertexBuffer(s)'))
        self.assertLess(text.index('mesh.GetVertexBuffer(s)'), text.index('shader.Dispatch('))
        for required in ('GetVertexAttributeStream', 'GetVertexAttributeOffset', 'VertexAttributeFormat.Float32', 'Owned mesh attribute layout changed', 'Face buffer lost'):
            self.assertIn(required, text)

    def test_sparse_gather_and_guarded_bone_access(self):
        text = (RUNTIME / 'Resources/GpuFaceDeformer.compute').read_text(encoding='utf-8')
        for required in ('[numthreads(64,1,1)]', 'v >= _FaceVertexCount', '_FaceStarts[v+1]', 'precise float3 blended', 'safeBone', 'rest.tangent.w'):
            self.assertIn(required, text)
        self.assertNotIn('Interlocked', text)

    def test_adapter_retains_weights_latch_and_current_cpu_fallback(self):
        core = (RUNTIME / 'FaceExpressionRenderer.cs').read_text(encoding='utf-8')
        adapter = (RUNTIME / 'FaceExpressionRenderer.Gpu.cs').read_text(encoding='utf-8')
        self.assertLess(core.index('ApplyAuthoredFaceCorrection(weights)'), core.index('TryApplyGpuDeformation(weights, changed)'))
        self.assertIn('changed ? weights : _lastWeights', adapter)
        self.assertIn('_cpuBlendNeedsRefresh = true', adapter)
        self.assertLess(adapter.index('_filter.sharedMesh = _deformedMesh'), adapter.index('_gpuFace.Dispose()'))

    def test_gpu_cpu_diagnostics_are_not_stale_numbers(self):
        text = (RUNTIME / 'FaceExpressionRenderer.cs').read_text(encoding='utf-8')
        self.assertIn('IsGpuDeformationActive ? "null" : FloatJson(_maximumBoneDisplacement)', text)
        self.assertIn('IsGpuDeformationActive ? float.NaN', text)
        self.assertIn('CPU vertex coverage unavailable', (RUNTIME / 'FaceDecalRuntime.cs').read_text(encoding='utf-8'))

    def test_documented_layout_and_non_rendering_consumers(self):
        text = (ROOT / 'docs/gpu-face-deformation.md').read_text(encoding='utf-8')
        for required in ('MeshCollider', 'UUM-9352', 'GpuDeformationEnabled', '同帧', '256 MiB', '±64'):
            self.assertIn(required, text)


if __name__ == '__main__':
    unittest.main()
