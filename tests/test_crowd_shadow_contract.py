"""Source/API guards only; current GPU/whole-image acceptance runs in Player."""
from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'

class CrowdShadowContractTests(unittest.TestCase):
    def test_explicit_borrowed_adapter(self):
        source = (RUNTIME / 'CrowdShadowSource.cs').read_text(encoding='utf-8')
        for token in ('public bool enabled;', 'public bool TryPrepare(', 'public ulong Revision', 'CrowdPrototypeGeometry', 'DrawMeshInstancedIndirect', 'item.hidden', 'source.contentVersion'):
            self.assertIn(token, source)
        for token in ('GetData(', 'ReadPixels', 'BakeMesh', 'Shader.SetGlobal', 'Time.time', 'CrowdSelection'):
            self.assertNotIn(token, source)
        self.assertLess(source.index('resource budget exceeded before allocation'), source.index('geometry[i].Allocate()'))

    def test_current_atlas_integration_and_budgets(self):
        source = (RUNTIME / 'SceneLightShadowAtlas.cs').read_text(encoding='utf-8')
        self.assertLess(source.index('RecordCrowdGeometry(commands)'), source.index('commands.SetRenderTarget(Atlas)'))
        self.assertIn('settings.casters.Length + CrowdDrawCount', source)
        self.assertIn('input.stablePointTexels ? -1 : 0', source)
        source = (RUNTIME / 'SceneLightShadowAtlas.Crowd.cs').read_text(encoding='utf-8')
        for token in ('triangles * maps', 'source.IsPrepared', 'unique.Add(source)', 'Revision != _crowdRevisions[i]', 'settings.crowds.Clone()'):
            self.assertIn(token, source)
        for name in ('SceneDirectionalShadowSettings.cs', 'SceneLightShadowSettings.cs'):
            source = (RUNTIME / name).read_text(encoding='utf-8')
            self.assertIn('[NonSerialized] public CrowdShadowSource[] crowds = Array.Empty<CrowdShadowSource>()', source)

    def test_indirect_current_pose_and_original_layouts(self):
        source = (RUNTIME / 'Resources/CrowdShadowCaster.shader').read_text(encoding='utf-8')
        for token in ('SV_VertexID', 'SV_InstanceID', '_CrowdVertices[vertex]', '_CrowdInstances[_CrowdShadowStart + instance]', 'SCENE_SHADOW_POINT', 'SCENE_SHADOW_ORTHOGRAPHIC', 'clip(_ShadowFar - radial)'):
            self.assertIn(token, source)
        self.assertIn('float4 positionScale, rotationType, tint', source)

    def test_unique_guids(self):
        names = ('CrowdShadowSource.cs', 'SceneLightShadowAtlas.Crowd.cs', 'Resources/CrowdShadowCaster.shader')
        guids = []
        for name in names:
            text = (RUNTIME / (name + '.meta')).read_text(encoding='utf-8')
            self.assertRegex(text, r'(?m)^guid: [0-9a-f]{32}$')
            guids.append(next(line for line in text.splitlines() if line.startswith('guid:')))
        self.assertEqual(len(guids), len(set(guids)))

if __name__ == '__main__': unittest.main()
