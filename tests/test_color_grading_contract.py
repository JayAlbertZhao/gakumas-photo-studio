"""Public color-authoring ownership/default-source boundaries; GPU fixtures test behavior."""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
RUNTIME=ROOT/'packages/com.digital-kotone.toolkit/Runtime'

class ColorGradingContract(unittest.TestCase):
    def test_authoring_has_no_private_lut_or_application_dependency(self):
        source='\n'.join((RUNTIME/name).read_text(encoding='utf-8') for name in
                         ('ColorGradingProfile.cs','ColorGradingMath.cs','ColorGradingLut.cs','ColorGradingRenderer.cs'))
        for text in ('BundleCatalog','PhotoModeApp','File.Read','SetGlobal','FindObjectsOfType','CapturedColorLut'):
            self.assertNotIn(text,source)
        for text in ('schemaVersion','FromJsonOverwrite','sourceWhite','targetWhite','GranTurismo','hueVsHue',
                     'hueVsSaturation','saturationVsSaturation','luminanceVsSaturation','ProfileJson','CopyValues'):
            self.assertIn(text,source)

    def test_shader_binds_blit_source_and_explicit_samplers(self):
        source=(RUNTIME/'Resources/AuthoredColorLut.shader').read_text(encoding='utf-8')
        self.assertIn('Properties { _MainTex',source)
        self.assertIn('Texture3D<float4> _AuthoredLut;',source)
        self.assertIn('_AuthoredLut.SampleLevel(sampler_LinearClamp',source)
        self.assertIn('_MainTex.SampleLevel(sampler_PointClamp',source)
        self.assertIn('value.a',source)
        self.assertNotIn('tex2D(',source)

    def test_explicit_bridge_is_alternative_not_double_grade(self):
        source=(RUNTIME/'OriginalStyleRenderPipeline.cs').read_text(encoding='utf-8')
        self.assertIn('public bool useAuthoredColorGrading;',source)
        self.assertIn('if(!useAuthoredColorGrading) EnsureCapturedColorLut();',source)
        self.assertIn('ApplyAuthoredColor(postInput,finalColor,temporaries);',source)
        self.assertIn('Graphics.Blit(postInput, finalColor, _postMaterial, 5);',source)
        compose=(RUNTIME/'Resources/AuthoredPostComposite.shader').read_text(encoding='utf-8')
        for forbidden in ('_CapturedColorLut','tex3D','AcesFilm','_CapturedPostExposure'):
            self.assertNotIn(forbidden,compose)

    def test_renderer_owns_output_not_shared_lut(self):
        source=(RUNTIME/'ColorGradingRenderer.cs').read_text(encoding='utf-8')
        for text in ('source==output','generation','IsCurrent','RenderTexture.active','IsCreated()',
                     'UnavailableReason','RenderTextureFormat.RGB111110Float'):
            self.assertIn(text,source)
        self.assertNotIn('lut.Dispose()',source)
        lut=(RUNTIME/'ColorGradingLut.cs').read_text(encoding='utf-8')
        self.assertIn('ColorGradingProfile.FromJson(profile.ToJson(false))',lut)
        self.assertIn('r+size*(g+size*b)',lut)

if __name__=='__main__':unittest.main()
