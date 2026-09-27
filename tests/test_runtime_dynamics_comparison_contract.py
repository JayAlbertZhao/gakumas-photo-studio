import json
from pathlib import Path
import sys
import unittest


ROOT = Path(__file__).resolve().parents[1]
RECEIPT = ROOT / "references" / "hooks" / "open-swing-runtime-comparison-receipt.json"
PROBE = ROOT / "tools" / "replay" / "RuntimeDynamicsReplayProbe.cs"
RUNNER = ROOT / "tools" / "run_open_swing_runtime_replay.py"
sys.path.insert(0, str(ROOT / "tools"))
from run_open_swing_runtime_replay import parse_runtime_receipt


class RuntimeDynamicsComparisonContractTests(unittest.TestCase):
    def test_receipt_is_a_finite_runtime_comparison_with_explicit_policy_gap(self):
        receipt = json.loads(RECEIPT.read_text(encoding="utf-8"))
        self.assertEqual(receipt["schema"], "photo-studio.runtime-comparison.v1")
        self.assertEqual(receipt["status"], "passed")
        self.assertTrue(receipt["equivalent"])
        self.assertEqual(
            receipt["equivalence_scope"],
            "synthetic dynamics replay; invalid capsule policy is reported separately",
        )
        self.assertTrue(receipt["finite"]["reference"])
        self.assertTrue(receipt["finite"]["independent"])
        self.assertEqual(receipt["reference_nodes"], 3)
        self.assertEqual(receipt["independent_entries"], 3)
        self.assertEqual(receipt["independent_segments"], 3)
        self.assertEqual(receipt["terminal_candidates"], 1)
        self.assertEqual(receipt["terminal_proxies"], 1)
        self.assertEqual(receipt["terminal_proxy_mode"], "opt-in")
        self.assertEqual(receipt["prewarm_mode"], "reference-half-pass")
        self.assertEqual(receipt["reset_boundary_mode"], "deferred-first-step")
        self.assertEqual(receipt["collision_fixture"], "empty-static-collider-set")
        self.assertEqual(receipt["steps"], 8)
        self.assertEqual(len(receipt["tip_deltas"]), 3)
        self.assertAlmostEqual(max(receipt["tip_deltas"]), receipt["max_tip_delta"], places=6)
        self.assertAlmostEqual(receipt["fixed_step"], 0.01667, places=5)
        self.assertAlmostEqual(receipt["max_tip_delta"], 0.0)
        self.assertEqual(receipt["capsule_invalid_axis"], {
            "reference": "throws",
            "independent": "clamps",
            "strict": "throws",
        })
        self.assertLess(receipt["skirt_root_math_angle_delta"], 0.1)
        self.assertTrue(receipt["reset"]["finite"])
        self.assertEqual(receipt["reset"]["reference"], "RequestReset + Step")
        self.assertEqual(receipt["reset"]["independent"], "ResetSimulation + AdvanceSimulation")
        self.assertTrue(PROBE.is_file())
        self.assertTrue(RUNNER.is_file())

    def test_runner_parses_the_unity_receipt_without_normalizing_the_gap(self):
        receipt = parse_runtime_receipt(
            "RUNTIME_DYNAMICS_REPLAY_OK referenceNodes=3 oursEntries=3 "
            "oursSegments=3 terminalCandidates=1 terminalProxies=1 steps=8 "
            "tipDelta0=0 tipDelta1=0 tipDelta2=0 maxTipDelta=0 step=0.01667"
            " capsuleInvalidAxis=reference-throws,independent-clamps,strict-throws"
            " skirtRootMathAngleDelta=0 resetFinite=True resetMaxTipDelta=0"
        )
        self.assertEqual(receipt["reference_nodes"], 3)
        self.assertEqual(receipt["terminal_candidates"], 1)
        self.assertEqual(receipt["terminal_proxies"], 1)
        self.assertEqual(receipt["independent_segments"], 3)
        self.assertEqual(receipt["tip_deltas"], [0.0, 0.0, 0.0])
        self.assertAlmostEqual(receipt["max_tip_delta"], 0.0)
        self.assertAlmostEqual(receipt["fixed_step"], 0.01667)
        self.assertEqual(receipt["capsule_invalid_axis"], "reference-throws,independent-clamps,strict-throws")
        self.assertEqual(receipt["skirt_root_math_angle_delta"], 0.0)
        self.assertTrue(receipt["reset_finite"])
        self.assertAlmostEqual(receipt["reset_max_tip_delta"], 0.0)


if __name__ == "__main__":
    unittest.main()
