import json
from pathlib import Path
import subprocess
import sys
import unittest


ROOT = Path(__file__).resolve().parents[1]


class ReferenceCompareContractTests(unittest.TestCase):
    def test_comparison_is_explicit_about_matches_and_gaps(self):
        result = subprocess.run(
            [sys.executable, "-I", "tools/compare_open_swing_reference.py"],
            cwd=ROOT,
            check=True,
            capture_output=True,
            text=True,
        )
        report = json.loads(result.stdout)
        self.assertEqual(report["schema"], "photo-studio.reference-comparison.v1")
        self.assertTrue(report["accepted"])
        ids = {item["id"] for item in report["checks"]}
        self.assertIn("fixed-step", ids)
        self.assertIn("capsule-invalid-axis", ids)
        self.assertFalse(report["equivalent"])


if __name__ == "__main__":
    unittest.main()
