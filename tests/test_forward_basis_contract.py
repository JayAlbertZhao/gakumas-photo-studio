"""Shared normal-basis source guards; independent rendered proof is in Player."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class ForwardBasisContractTests(unittest.TestCase):
    def test_parity_uses_current_matrix_and_explicit_scale(self):
        source = (RUNTIME / 'Resources/SceneForwardLighting.hlsl').read_text(encoding='utf-8')
        self.assertIn('sign(determinant((float3x3)unity_ObjectToWorld))', source)
        self.assertIn('sign(_VertexScale.x * _VertexScale.y * _VertexScale.z)', source)
        self.assertIn('input.tangent.w * handedness', source)
        self.assertNotIn('input.tangent.w * unity_WorldTransformParams.w', source)
        self.assertIn('input.tangent.xyz - n * dot(n, input.tangent.xyz)', source)

    def test_consumers_share_the_actual_vertex_routine(self):
        camera = (RUNTIME / 'Resources/SceneForwardLighting.shader').read_text(encoding='utf-8')
        fx = (RUNTIME / 'Resources/FxForwardSurface.hlsl').read_text(encoding='utf-8')
        self.assertIn('#pragma vertex ForwardVertex', camera)
        self.assertIn('ForwardVarying value=ForwardVertex(input)', fx)
        for name in ('LowResolutionFxLit.shader', 'HeavyFxLit.shader'):
            self.assertIn('FxForwardSurface.hlsl', (RUNTIME / 'Resources' / name).read_text(encoding='utf-8'))

    def test_independent_world_normal_and_native_consumers(self):
        source = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.ForwardBasis.cs').read_text(encoding='utf-8')
        for term in ('combined.inverse.transpose.MultiplyVector(n)', 'Vector3.Cross(worldN, worldT)',
                     'referenceMesh.normals = Enumerable.Repeat(mapped, 4)', 'reference ? null : normalMap',
                     'current-rejects-missing-bitangent', 'current-native-skin-pose-',
                     'normal-biased-shadows-independent-', 'probe-independent-', 'fx-working-normal-',
                     'FxResolution.Full, FxResolution.Half, FxResolution.Quarter'):
            self.assertIn(term, source)


if __name__ == '__main__':
    unittest.main()
