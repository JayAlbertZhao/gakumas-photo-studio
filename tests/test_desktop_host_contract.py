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
        self.assertIn('public void ResetHistory()', example)
        self.assertIn('WaitForOwnedGpuWork();frameRenderer.ResetHistoryAfterGpuCompletion();', example)

    def test_real_joined_temporal_controls_reset_all_histories(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/SrpActorCharacterValidation.DesktopHost.cs').read_text(encoding='utf-8')
        for control in ('-motion-keeps-full-color-', '-cold-resolve-exact',
                        '-animated-temporal-positive-control', '-reuse-two-gbuffers-exact-',
                        '-seek-resets-whole-chain'):
            self.assertIn(control, fixture)
        self.assertIn('s.reuseSceneMotionStorage=true;example.ResetHistory();', fixture)

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

    def test_actor_motion_oracle_uses_owned_geometry_and_queried_raster_limit(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.DesktopHost.cs').read_text(encoding='utf-8')
        for required in ('Own(Instantiate(sourceMesh))', 'filter.sharedMesh=sourceMesh',
                         'GAKUMAS_SELFTEST_RASTER_SUBPIXEL_BITS', 'priorWorld[triangles[t+k]]',
                         'oracle-perspective-deformation', 'oracle-off-axis-change',
                         'uvError<.00004f', 'depthError<.00001f',
                         'oracle-applied-jitter-independent-current-filter',
                         'oracle-applied-jitter-independent-motion',
                         'oracle-applied-jitter-reprojects-history',
                         'oracle-disoccluded-scene-rejects-actor-history',
                         'oracle-disocclusion-keeps-corresponding-actor-history',
                         'VerifyDesktopSkinMotion', 'independent-two-bone-uv',
                         'independent-two-bone-previous-depth', 'off-axis-blend-and-bone',
                         'stationary-negative-control', 'Independent Actor blend deformation',
                         'outline-bone-deformation', 'outline-blendshape', 'outline-width-change',
                         'outline-packed-stationary', 'outline-packed-bone-motion',
                         'identity!=surfaceIdentity'):
            self.assertIn(required, fixture)
        self.assertNotIn('.BakeMesh(', fixture)

    def test_skin_outline_oracle_authors_tangents_before_binding_skin(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.DesktopHost.cs').read_text(encoding='utf-8')
        skin = fixture.split('private IEnumerator VerifyDesktopSkinMotion(')[1].split('private void VerifyDesktopStorage(')[0]
        self.assertLess(skin.index('mesh.tangents=authoredTangents'), skin.index('mesh.AddBlendShapeFrame'))
        self.assertLess(skin.index('mesh.tangents=authoredTangents'), skin.index('skin.sharedMesh=mesh'))
        self.assertEqual(skin.count('mesh.tangents='), 1)
        for required in ('bone0.localToWorldMatrix.MultiplyPoint3x4(v)*weights[i].weight0',
                         'bone1.localToWorldMatrix.MultiplyPoint3x4(v)*weights[i].weight1',
                         'skin.GetBlendShapeWeight(0)/100', 'identity!=surfaceIdentity',
                         'Case("outline-stationary",false,false)'):
            self.assertIn(required, skin)
        self.assertNotIn('GetVertexBuffer(', skin)
        self.assertNotIn('GetPreviousVertexBuffer(', skin)

    def test_existing_producers_are_ordered_not_reimplemented(self):
        order = ['TileSceneRenderer.TryPrepare', 'shadow.TryRecord(',
                 'ActorForwardDrawSet.TryPrepare', 'scene.TryRecord(',
                 'planar.TryRecord(', 'reflection.TryRecord(', 'actor.TryRecord(']
        positions = [SOURCE.index(x) for x in order]
        self.assertEqual(positions, sorted(positions))
        post = [SOURCE.index(x) for x in ('effects.TryRender(', 'temporal.TryRender(', 'dof.TryRender(', 'motionBlur.TryRender(', 'grade.TryRender(')]
        self.assertEqual(post, sorted(post))
        self.assertIn('new FogVolumeDepth(actorFrame.eyeDepth)', SOURCE)
        self.assertNotIn('new Material(', SOURCE)

    def test_frame_blur_reuses_filter_and_preserves_packed_identity_abi(self):
        adapter = (RUNTIME / 'FrameMotionBlur.cs').read_text(encoding='utf-8')
        guide = (RUNTIME / 'Resources/FrameMotionBlurGuide.shader').read_text(encoding='utf-8')
        for token in ('new MotionBlurRenderer()', 'new MotionBlurInput(', 'current.SameOwner(input)',
                      'current.sequence==sequence+1&&current.MotionContinuous', 'timeSeconds-previousTime',
                      'dejittered?-jitterUv:Vector2.zero', 'renderer.Owns(t)', 'current.BindMotionBlurExclusions',
                      '33+32L', 'public void ResetHistory()'):
            self.assertIn(token, adapter)
        for token in ('id=packed>>4', '_FrameBlurExcluded[id*2+(packed&1u)]',
                      '(packed&4u)', '(packed&8u)', 'float4(m.xy,m.b,1)',
                      '_FrameBlurFx.Load', '_FrameBlurOpaque.Load'):
            self.assertIn(token, guide)
        self.assertNotIn('(packed&2u)', guide)
        self.assertNotIn('Time.', adapter)
        self.assertNotIn('.Submit(', adapter)
        self.assertIn('var preTemporalColor=finalColor;', SOURCE)
        self.assertIn('else motionBlur.ResetHistory();', SOURCE)

    def test_motion_blur_fixture_checks_current_geometry_clock_and_filter_order(self):
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.DesktopMotionBlur.cs').read_text(encoding='utf-8')
        for token in ('independent-half4-guide', 'independent-protection',
                      'existing-filter-independent-guide-whole-color', 'moving-striped-actor-positive',
                      'paused-exact-current', 'rewind-exact-current', 'long-gap-exact-current',
                      'seek-exact-current', 'sequence-gap-exact-current',
                      'exclude-taa-not-blur-exclusion', 'blended-preserves-current',
                      'authored-actor-preserves-current', 'no-jitter-preserves-current',
                      'taa-dof-moving', 'fx-positive-protection', 'reuse-moving-positive',
                      'reenabled-exact-current', 'invalid-budget-before-record'):
            self.assertIn(token, fixture)

    def test_current_foreign_and_failed_attempts_are_distinct(self):
        for required in ('!ReferenceEquals(input.owner, this)', '!input.IsCurrent',
                         'phase != Phase.Opaque', 'phase != Phase.Idle',
                         'value == 0 || value <= sequence', 'phase = Phase.Failed',
                         'phase == Phase.Failed) { reflection.ResetHistory();actor.ResetMotionHistoryAfterGpuCompletion();temporal.ResetHistory(); }',
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
