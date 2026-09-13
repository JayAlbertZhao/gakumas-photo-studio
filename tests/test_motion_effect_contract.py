"""Public source/API guardrails. Particle state and image gates execute in the Unity Player."""
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class MotionEffectContractTests(unittest.TestCase):
    def test_independent_explicit_clock_and_input(self):
        source = (RUNTIME / 'MotionEffectSequence.cs').read_text(encoding='utf-8')
        for value in ('TryBind(Entry[] entries', 'TrySample(double seconds)', 'entry.Copy()',
                      'StringComparer.Ordinal', 'time < e.startSeconds + e.durationSeconds'):
            self.assertIn(value, source)
        for forbidden in ('Campus.', 'using VL', 'BundleCatalog', 'Time.time', 'Time.deltaTime',
                          'UnityEngine.Random', 'FindObjectsOfType', 'GetInstanceID'):
            self.assertNotIn(forbidden, source)

    def test_private_particle_state_no_double_simulation_or_global_time(self):
        source = (RUNTIME / 'MotionEffectSequence.cs').read_text(encoding='utf-8')
        for value in ('StepSeconds = 1.0 / 60', 'Simulate(0, false, true, false)',
                      'Simulate((float)StepSeconds, false, false, false)', 'particle.Pause(false)',
                      'ps.useAutoRandomSeed = false', 'SystemSeed(slot.entry.seed, i)',
                      'staging.SetActive(false)', 'ParticleSystemStopAction.None'):
            self.assertIn(value, source)
        self.assertNotIn('Time.fixedDeltaTime', source)
        self.assertNotIn('.sharedMaterial =', source)

    def test_invalid_inputs_and_external_history_are_not_silently_approximated(self):
        source = (RUNTIME / 'MotionEffectSequence.cs').read_text(encoding='utf-8')
        for value in ('MaxEntries = 32', 'MaxSystemsPerEntry = 16', 'MaxSimulationSteps = 60000',
                      'Zero(main.gravityModifier)', 'Zero(ps.emission.rateOverDistance)',
                      '!ps.subEmitters.enabled', '!ps.collision.enabled', 'IsChildOf',
                      'ValidateTemplate(e.prefab)', 'work <= MaxSimulationSteps'):
            self.assertIn(value, source)
        self.assertIn('long particles = 0', source)
        self.assertIn('? curve.constant == 0', source)
        self.assertLess(source.index('work <= MaxSimulationSteps'), source.index('Spawn(slot)'))

    def test_disposal_hides_before_destroy_and_is_terminal(self):
        source = (RUNTIME / 'MotionEffectSequence.cs').read_text(encoding='utf-8')
        for value in ('slot.instance.SetActive(false); DestroyOwned(slot.instance)',
                      'public void Stop()', 'public void Clear()', 'disposed = true',
                      'if (disposed) return Fail', 'Stop(); LastError = reason'):
            self.assertIn(value, source)

    def test_playable_owns_and_clones_independently(self):
        source = (RUNTIME / 'MotionEffectPlayable.cs').read_text(encoding='utf-8')
        for value in ('Clone() => new MotionEffectPlayable()', 'sequence.TrySample(playable.GetTime())',
                      'OnGraphStop', 'sequence.Stop()', 'OnPlayableDestroy', 'sequence.Dispose()'):
            self.assertIn(value, source)

    def test_particle_module_declared_for_consumers(self):
        for path in ('packages/com.digital-kotone.toolkit/package.json', 'unity/Packages/manifest.json'):
            data = json.loads((ROOT / path).read_text(encoding='utf-8'))
            self.assertEqual('1.0.0', data['dependencies']['com.unity.modules.particlesystem'])

    def test_actual_player_acceptance_has_positive_numeric_render_and_graph_cases(self):
        source = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.MotionEffects.cs').read_text(encoding='utf-8')
        for value in ('GetParticles(particles)', 'independent-constant-velocity', 'positive-rendered-particles',
                      'backward-whole-image-exact', 'independent-owner-whole-image-exact', 'attachment-trs',
                      'actual-graph-evaluate', 'actual-graph-seek-exact', 'graph-destroy-cleans-owned-only',
                      'source-template-unchanged', 'global-clock-random-unchanged', 'camera.Render()'):
            self.assertIn(value, source)

    def test_docs_disclose_remaining_geometry_history_and_cost(self):
        source = (ROOT / 'docs/motion-effects.md').read_text(encoding='utf-8')
        for value in ('逐顶点定位器', 'Maya', '移动端帧时', '60000', 'Local', 'subemitters', '只读借用'):
            self.assertIn(value, source)


if __name__ == '__main__':
    unittest.main()
