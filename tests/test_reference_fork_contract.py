import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
FORK = ROOT / "references" / "forks" / "open-swing"
HOOK = ROOT / "references" / "hooks" / "open-swing-hook.json"
HOOK_SOURCE = ROOT / "references" / "hooks" / "OpenSwingReferenceHook.cs"


class ReferenceForkContractTests(unittest.TestCase):
    def test_open_swing_snapshot_has_provenance_and_mit_license(self):
        provenance = json.loads((FORK / "PROVENANCE.json").read_text(encoding="utf-8"))
        self.assertEqual(provenance["license"], "MIT")
        self.assertRegex(provenance["commit"], r"^[0-9a-f]{40}$")
        self.assertTrue((FORK / "LICENSE").is_file())
        self.assertTrue((FORK / "package.json").is_file())

    def test_hook_is_optional_and_asset_free(self):
        hook = json.loads(HOOK.read_text(encoding="utf-8"))
        self.assertEqual(hook["default_behavior"], "disabled")
        self.assertEqual(hook["hook_kind"], "optional-adapter")
        forbidden_suffixes = {".fbx", ".glb", ".gltf", ".png", ".jpg", ".wav", ".mp3", ".unitypackage"}
        files = [p for p in FORK.rglob("*") if p.is_file()]
        self.assertTrue(files)
        self.assertFalse(any(p.suffix.lower() in forbidden_suffixes for p in files))

    def test_hook_entrypoints_are_documented_by_the_snapshot(self):
        readme = (FORK / "README.md").read_text(encoding="utf-8")
        for entrypoint in ("ActorAnimationSwingSolver", "FixedStepSwingClock", "HairSwingAdapter"):
            self.assertIn(entrypoint, readme + "\n" + "\n".join(p.read_text(encoding="utf-8") for p in (FORK / "Runtime").rglob("*.cs")))

    def test_hook_is_caller_owned_and_compile_time_optional(self):
        source = HOOK_SOURCE.read_text(encoding="utf-8")
        self.assertIn("#if GAKUMAS_OPEN_SWING_REFERENCE", source)
        self.assertIn("public void RestoreBeforeAnimation()", source)
        self.assertIn("public void SimulateAfterAnimation(float deltaTime)", source)
        self.assertIn("solver.CapturePose();", source)
        self.assertNotIn("void Update()", source)
        self.assertNotIn("void LateUpdate()", source)


if __name__ == "__main__":
    unittest.main()
