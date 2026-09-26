import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
RECEIPT = ROOT / "references" / "hooks" / "open-swing-replay-receipt.json"
HOOK = ROOT / "references" / "hooks" / "OpenSwingReferenceHook.cs"


class ReferenceReplayReceiptTests(unittest.TestCase):
    def test_unity_replay_receipt_records_a_synthetic_hook_run(self):
        receipt = json.loads(RECEIPT.read_text(encoding="utf-8"))
        self.assertEqual(receipt["schema"], "photo-studio.runtime-replay-receipt.v1")
        self.assertEqual(receipt["status"], "passed")
        self.assertEqual(receipt["editor"], "Unity 2022.3.57f1")
        self.assertEqual(receipt["fixture"], "synthetic-chain-example")
        self.assertEqual(receipt["nodes"], 2)
        self.assertEqual(receipt["steps"], 8)
        self.assertAlmostEqual(receipt["fixed_step"], 0.01667, places=5)
        self.assertEqual(
            receipt["hook_calls"],
            ["RestoreBeforeAnimation", "SimulateAfterAnimation", "ResetReference"],
        )
        self.assertTrue(HOOK.is_file())


if __name__ == "__main__":
    unittest.main()
