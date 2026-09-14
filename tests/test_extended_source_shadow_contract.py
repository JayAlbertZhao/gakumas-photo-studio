"""Source/API guards; enabled whole-image/native visibility needs separate Player evidence."""
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
RUNTIME=ROOT/'packages/com.digital-kotone.toolkit/Runtime'

class ExtendedSourceShadowContractTests(unittest.TestCase):
    def test_explicit_model_and_budgets(self):
        source=(RUNTIME/'SceneLightShadowSettings.cs').read_text(encoding='utf-8')
        for token in ('public bool extendedSourceCoverage;', 'extendedSamplesPerAxis = 2', 'maxExtendedSourceSamples = 64', 'maxExtendedCasterDraws = 32768'):
            self.assertIn(token,source)
        source=(RUNTIME/'SceneLightShadowAtlas.cs').read_text(encoding='utf-8')
        self.assertIn('Extended(light) && !input.extendedSourceCoverage',source)
        self.assertLess(source.index('extendedSamples > settings.maxExtendedSourceSamples'),source.index('if (!PrepareAtlas(settings'))
        self.assertIn('var data = _mapData[tile]',source)

    def test_native_sample_sources_and_no_global_readback(self):
        source=(RUNTIME/'SceneLightShadowAtlas.Extended.cs').read_text(encoding='utf-8')
        for token in ('firstTile+1','face<6','_views.Add(projection*view);_mapData.Add(sample)','input.nearPlane/Mathf.Sqrt(3)'):
            self.assertIn(token,source)
        for token in ('Shader.SetGlobal','ReadPixels','GetData(','BakeMesh'):
            self.assertNotIn(token,source)

    def test_finite_source_coverage_is_not_center_shadow(self):
        source=(RUNTIME/'Resources/SceneLightShadow.hlsl').read_text(encoding='utf-8')
        for token in ('data.options.w < 0','sampleData.options.w = -data.options.w + (y * nx + x) * 6','ScenePointVisibility(receiver - source, sampleData)','visibility / (nx * ny)'):
            self.assertIn(token,source)
        for token in ('sampleData.atlasST.w = -1','data.atlasST.w < 0','resolution / 1048576','pixel + .5'):
            self.assertIn(token,source)

    def test_added_unity_guids(self):
        for p in (RUNTIME/'SceneLightShadowAtlas.Extended.cs.meta',ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.ExtendedShadow.cs.meta'):
            self.assertRegex(p.read_text(encoding='utf-8'),r'(?m)^guid: [0-9a-f]{32}$')

if __name__=='__main__':unittest.main()
