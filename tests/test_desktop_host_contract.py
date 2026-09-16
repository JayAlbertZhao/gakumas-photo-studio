"""Public orchestration boundaries; runtime/native evidence remains required."""
from pathlib import Path
import json
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'
SOURCE = (RUNTIME / 'DesktopFrameRenderer.cs').read_text(encoding='utf-8')


class DesktopHostContract(unittest.TestCase):
    def test_public_example_is_compiled_without_private_content(self):
        folder = ROOT / 'packages/com.digital-kotone.toolkit/Examples/DesktopHost'
        assembly = json.loads((folder / 'Gakumas.Toolkit.Examples.asmdef').read_text(encoding='utf-8'))
        self.assertEqual(assembly['references'], ['Gakumas.Toolkit'])
        example = (folder / 'DesktopHostExample.cs').read_text(encoding='utf-8')
        for forbidden in ('Shader.SetGlobal', 'Shader.GetGlobal', 'File.Read',
                          'AssetBundle', 'FindObjectsOfType', '_FaceDebugMode'):
            self.assertNotIn(forbidden, example)
        self.assertIn('new DesktopFrameRenderer(', example)
        self.assertIn('new Vector4(.5f,.25f,0,1)', example)
        self.assertIn('public bool HasCompletedFrame => displayReady', example)
        self.assertIn('LastError=null;displayReady=false;', example)
        self.assertIn('if(presentThisContext&&displayReady)', example)
        self.assertIn('DesktopExampleRequest:ScriptableObject', example)
        self.assertIn('LastRenderedTimeSeconds=seconds', example)

    def test_example_acceptance_covers_failure_and_ownership(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.DesktopExample.cs').read_text(encoding='utf-8')
        for control in ('full-actor-lighting-positive-control',
                        'self-shadow-positive-control', 'second-owner-rejected-before-allocation',
                        'failed-post-not-a-completed-or-presentable-frame',
                        'failure-retires-work-and-recovers-color',
                        'shutdown-preserves-new-owner-pipeline'):
            self.assertIn(control, fixture)
        self.assertIn('example.LastRenderedTimeSeconds==time', fixture)
        self.assertIn('expectedBodyY==actualBodyY', fixture)

    def test_host_keeps_application_authority_explicit(self):
        for forbidden in ('Camera.Render(', '.Submit(', 'FindObjectsOfType',
                          'Shader.SetGlobal', 'Shader.GetGlobal', 'File.Read',
                          'renderPipelineAsset=', 'ReadPixels(', 'Time.time'):
            self.assertNotIn(forbidden, SOURCE)
        self.assertIn('public bool enabled;', SOURCE)
        self.assertIn('double timeSeconds', SOURCE)

    def test_existing_producers_are_ordered_not_reimplemented(self):
        order = ['TileSceneRenderer.TryPrepare', 'shadow.TryRecord(',
                 'ActorForwardDrawSet.TryPrepare', 'scene.TryRecord(',
                 'planar.TryRecord(', 'reflection.TryRecord(', 'actor.TryRecord(']
        positions = [SOURCE.index(x) for x in order]
        self.assertEqual(positions, sorted(positions))
        post = [SOURCE.index(x) for x in ('effects.TryRender(', 'dof.TryRender(', 'grade.TryRender(')]
        self.assertEqual(post, sorted(post))
        self.assertIn('new FogVolumeDepth(actorFrame.eyeDepth)', SOURCE)
        self.assertNotIn('new Material(', SOURCE)

    def test_current_foreign_and_failed_attempts_are_distinct(self):
        for required in ('!ReferenceEquals(input.owner, this)', '!input.IsCurrent',
                         'phase != Phase.Opaque', 'phase != Phase.Idle',
                         'value == 0 || value <= sequence', 'phase = Phase.Failed',
                         'phase == Phase.Failed) reflection.ResetHistory()',
                         'RetireAfterGpuCompletion()', 'draws?.Dispose()', 'scene?.Dispose()'):
            self.assertIn(required, SOURCE)

    def test_scene_history_excludes_actors_and_shadow_ownership_is_explicit(self):
        for required in ('actors.Contains(surface.renderer)',
                         's.selfShadow.enabled && s.actors.selfShadow.HasValue',
                         'selfShadow = shadowFrame ?? a.selfShadow',
                         's.planar.enabled && !s.reflections.enabled'):
            self.assertIn(required, SOURCE)

    def test_integrated_fixture_appends_after_prior_shadow_suite(self):
        app = ROOT / 'unity/Assets/Applications/PhotoStudio'
        main = (app / 'ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertGreater(main.rindex('VerifyDesktopHost(report)'), main.rindex('VerifyActorShadow(report)'))
        fixture = (app / 'ActorRenderingSelfTest.DesktopHost.cs').read_text(encoding='utf-8')
        for control in ('full-transparent-independent-ray-blend',
                        'independent-profile-measured-weights-whole-color',
                        'real-projected-planar-coverage', 'planar-disabled-whole-color-restored',
                        'post-failure-invalidates-opaque-requires-retire',
                        'recovered-after-failed-post', 'disabled-post-exact-passthrough'):
            self.assertIn(control, fixture)


if __name__ == '__main__':
    unittest.main()
