"""API/source guards only. Actual UI and GPU evidence is recorded separately."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT/'packages/com.digital-kotone.toolkit/Runtime'

class SrpMonitorContractTests(unittest.TestCase):
    def test_separate_prepare_before_srp_and_one_shot_record(self):
        source=(RUNTIME/'SrpHdrMonitor.cs').read_text(encoding='utf-8')
        for token in ('TryPrepare(double seconds,ulong contentVersion)', 'TryRecord(ScriptableRenderContext context,out Frame frame)',
                      'PrepareCapture?.Invoke(seconds)', 'pendingCapture ||', '!prepared ||', '!PreparedMatches()',
                      'requested|=prepared&&needsCapture',
                      'ScriptableRenderContext.EmitGeometryForCamera(Camera)', 'context.Cull(ref culling)', 'context.DrawRenderers'):
            self.assertIn(token,source)
        self.assertNotIn('PrepareCapture?.Invoke',source.split('public bool TryRecord(')[1])
        for token in ('Camera.Render(', 'context.Submit()', 'GraphicsSettings.renderPipelineAsset ='):
            self.assertNotIn(token,source)

    def test_stable_hdr_publication_and_explicit_completion(self):
        source=(RUNTIME/'SrpHdrMonitor.cs').read_text(encoding='utf-8')
        for token in ('commands.CopyTexture(capture,0,0,output,0,0)', 'RenderTextureFormat.ARGBHalf',
                      'RenderTextureReadWrite.Linear', 'filterMode=FilterMode.Bilinear', 'NominalColorBytes',
                      'Frame is not a completion fence', 'Camera.targetTexture=oldTarget'):
            self.assertIn(token,source)
        self.assertLess(source.index('Draw(context,results,RenderQueueRange.transparent'),source.index('commands.CopyTexture'))

    def test_existing_builtin_rejection_unchanged(self):
        builtin=(RUNTIME/'HdrMonitor.cs').read_text(encoding='utf-8')
        self.assertIn('GraphicsSettings.currentRenderPipeline != null',builtin)
        self.assertNotIn('SrpHdrMonitor',builtin)

    def test_live_consumers_reject_ambiguous_or_stale_sources(self):
        settings=(RUNTIME/'SceneDecalLightSettings.cs').read_text(encoding='utf-8')
        renderer=(RUNTIME/'SceneDecalLightRenderer.cs').read_text(encoding='utf-8')
        material=(RUNTIME/'MonitorEmissionMaterial.cs').read_text(encoding='utf-8')
        self.assertIn('[NonSerialized] public SrpHdrMonitor srpMonitor',settings)
        self.assertIn('settings.monitor != null && settings.srpMonitor != null',renderer)
        self.assertIn('settings.srpMonitor.TryGetFrame(out var frame)',renderer)
        self.assertIn('TryBind(HdrMonitor.Frame frame',material)
        self.assertIn('TryBind(SrpHdrMonitor.Frame frame',material)
        self.assertIn('TryBind(frame.IsCurrent, frame.texture, settings, out error)',material)

    def test_fixture_renders_ui_emission_and_three_live_light_shapes(self):
        fixture=(ROOT/'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.SrpMonitor.cs').read_text(encoding='utf-8')
        for token in ('typeof(RawImage)', 'RenderMode.WorldSpace', 'RenderMode.ScreenSpaceCamera',
                      'whole-real-ui-hdr-alpha', 'whole-real-emission', 'whole-independent-live-light',
                      'SceneDecalLightShape.Point,SceneDecalLightShape.Capsule,SceneDecalLightShape.Area',
                      '110-live-grid-lights', 'srpMonitor=producer', 'superseded-prepare-keeps-pending-capture'):
            self.assertIn(token,fixture)

if __name__=='__main__':
    unittest.main()
