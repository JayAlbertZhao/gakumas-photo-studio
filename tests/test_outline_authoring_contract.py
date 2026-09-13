"""Public authoring API guards; numerical/actual rendering checks run in Player."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'packages/com.digital-kotone.toolkit/Runtime/ActorOutlineAuthoring.cs'


class OutlineAuthoringContractTests(unittest.TestCase):
    def test_explicit_pure_entrypoints_and_budgets(self):
        source = SOURCE.read_text(encoding='utf-8')
        for term in ('public static Result Generate(', 'public static Mesh CreateMesh(',
                     'MaximumVertices = 1000000', 'MaximumIndices = 6000000',
                     'MaximumBlendFrameVertices = 8000000', 'weldGroups', 'position.Equals(other.position)'):
            self.assertIn(term, source)

    def test_no_implicit_mutation_or_shader_dependency(self):
        source = SOURCE.read_text(encoding='utf-8')
        for forbidden in ('source.tangents =', 'source.normals =', 'source.ClearBlendShapes',
                          'RecalculateNormals', 'FindObjectsOfType', 'AssetPostprocessor',
                          'Shader.', 'BakeMesh(', 'File.Read', '.sharedMesh ='):
            self.assertNotIn(forbidden, source)
        self.assertIn('UnityEngine.Object.Instantiate(source)', source)

    def test_all_blend_frames_and_submesh_base_vertex(self):
        source = SOURCE.read_text(encoding='utf-8')
        for term in ('source.GetBlendShapeFrameCount(shape)', 'source.GetBlendShapeFrameVertices(',
                     'target.directions[i] - basis.directions[i]', 'source.GetBlendShapeName(shape)',
                     'source.GetBlendShapeFrameWeight(shape, frame)', 'source.GetTriangles(i, true)',
                     'result.AddBlendShapeFrame(', 'deltaPositions, deltaNormals, deltaTangents'):
            self.assertIn(term, source)

    def test_independent_reference_and_native_consumption(self):
        source = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.OutlineAuthoring.cs').read_text(encoding='utf-8')
        for term in ('Math.Acos(', 'camera.Render()', 'SkinnedMeshRenderer', 'ACTOR_OUTLINE',
                     'whole-image-native-skin-shape-view-', 'hard-normal-seam-negative-control',
                     'clone-preserves-submesh-base-vertices', 'requested-native-skin-draw-capture'):
            self.assertIn(term, source)


if __name__ == '__main__':
    unittest.main()
