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
    def test_receipt_is_a_finite_runtime_comparison_with_visible_gap(self):
        receipt = json.loads(RECEIPT.read_text(encoding="utf-8"))
        self.assertEqual(receipt["schema"], "photo-studio.runtime-comparison.v1")
        self.assertEqual(receipt["status"], "passed")
        self.assertFalse(receipt["equivalent"])
        self.assertTrue(receipt["finite"]["reference"])
        self.assertTrue(receipt["finite"]["independent"])
        self.assertEqual(receipt["reference_nodes"], 3)
        self.assertEqual(receipt["independent_entries"], 3)
        self.assertEqual(receipt["independent_segments"], 2)
        self.assertEqual(receipt["steps"], 8)
        self.assertAlmostEqual(receipt["fixed_step"], 0.01667, places=5)
        self.assertGreater(receipt["max_tip_delta"], 0.0)
        self.assertTrue(PROBE.is_file())
        self.assertTrue(RUNNER.is_file())

    def test_runner_parses_the_unity_receipt_without_normalizing_the_gap(self):
        receipt = parse_runtime_receipt(
            "RUNTIME_DYNAMICS_REPLAY_OK referenceNodes=3 oursEntries=3 "
            "oursSegments=2 steps=8 maxTipDelta=0.4555227 step=0.01667"
        )
        self.assertEqual(receipt["reference_nodes"], 3)
        self.assertEqual(receipt["independent_segments"], 2)
        self.assertAlmostEqual(receipt["max_tip_delta"], 0.4555227)
        self.assertAlmostEqual(receipt["fixed_step"], 0.01667)


if __name__ == "__main__":
    unittest.main()
