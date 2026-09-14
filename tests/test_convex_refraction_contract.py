"""Source/API ownership guards, not a substitute for enabled native optical validation."""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
RUNTIME=ROOT/'packages/com.digital-kotone.toolkit/Runtime'


class ConvexRefractionContractTests(unittest.TestCase):
    def test_unity_asset_guids_parse(self):
        paths=[RUNTIME/n for n in ('ConvexRefractionShape.cs','SceneRefractionSettings.cs','SceneRefractionRenderer.cs','Resources/SceneRefraction.shader')]
        paths.append(ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.Refraction.cs')
        for p in paths:
            self.assertRegex(Path(str(p)+'.meta').read_text(encoding='utf-8'),r'(?m)^guid: [0-9a-f]{32}$')

    def test_owned_explicit_convex_snapshot(self):
        s=(RUNTIME/'ConvexRefractionShape.cs').read_text(encoding='utf-8')
        for token in ('MaximumTriangles = 128','source.isReadable','old.direction','e.count!=2','GeometryBytes','Dispose()'):
            self.assertIn(token,s)
        for token in ('source.vertices=','source.triangles=','BakeMesh','GetData('):
            self.assertNotIn(token,s)

    def test_explicit_optical_targets_and_no_default_activation(self):
        s=(RUNTIME/'SceneRefractionRenderer.cs').read_text(encoding='utf-8')
        for token in ('unresolvedThroughput','firstEyeDepth','maximumTargetMiB','Owns(source)','DrawMesh(s.shape.Mesh','generation++','Projection(camera)'):
            self.assertIn(token,s)
        for token in ('Shader.SetGlobal','ReadPixels','GetData(','camera.Render()'):
            self.assertNotIn(token,s)
        self.assertIn('public bool enabled;', (RUNTIME/'SceneRefractionSettings.cs').read_text(encoding='utf-8'))

    def test_multiple_boundaries_keep_unresolved_energy(self):
        s=(RUNTIME/'Resources/SceneRefraction.shader').read_text(encoding='utf-8')
        for token in ('Interface(', 'NextBoundary(', 'if(k<=0)return 1', 'weight*=f', 'remainder=weight*(eta*eta)', 'SV_Target2'):
            self.assertIn(token,s)
        self.assertNotIn('_Time',s)


if __name__=='__main__':unittest.main()
