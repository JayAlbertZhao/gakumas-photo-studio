"""Source-level contracts for the Unity AR host and private-data boundary."""

import importlib.util
import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "apps" / "ar-photo-unity"
PREPARE_DATA = ROOT / "tools" / "prepare_ar_character_data.py"


class ArPhotoUnityContractTest(unittest.TestCase):
    def test_host_uses_arcore_and_the_shared_character_runtime(self):
        manifest = json.loads((APP / "Packages/manifest.json").read_text(encoding="utf-8"))
        dependencies = manifest["dependencies"]
        self.assertEqual(dependencies["com.unity.xr.arcore"], "5.1.6")
        self.assertEqual(dependencies["com.unity.xr.arfoundation"], "5.1.6")
        self.assertTrue(dependencies["com.digital-kotone.toolkit"].startswith("file:"))

        source = (APP / "Assets/ARPhotoApp.cs").read_text(encoding="utf-8")
        for contract in (
            "CharacterSceneRuntime",
            "HostCamera = _camera",
            "DisableDefaultEnvironment = true",
            "ARCameraBackground",
            "TrackableType.PlaneWithinPolygon",
            "Application.persistentDataPath",
        ):
            self.assertIn(contract, source)

    def test_build_is_generated_and_targets_arcore_supported_android(self):
        source = (APP / "Assets/Editor/ARPhotoBuild.cs").read_text(encoding="utf-8")
        for contract in (
            "BuildTarget.Android",
            "ScriptingImplementation.IL2CPP",
            "AndroidArchitecture.ARM64",
            "GraphicsDeviceType.OpenGLES3",
            '"UnityEngine.XR.ARCore.ARCoreLoader"',
            '"ANDROID_NDK_ROOT"',
        ):
            self.assertIn(contract, source)
        self.assertNotIn("GraphicsDeviceType.Vulkan", source)

    def test_private_data_tool_selects_recursive_dependencies(self):
        spec = importlib.util.spec_from_file_location("prepare_ar_character_data", PREPARE_DATA)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        records = {
            "body": {"dependencies": ["actor-shader"]},
            "actor-shader": {"dependencies": ["shader"]},
            "shader": {"dependencies": []},
        }
        self.assertEqual(
            module.dependency_closure(records, ["body"]),
            {"body", "actor-shader", "shader"},
        )
        self.assertIn("mot_photo_chr_cmmn_stand-idle-001_lp", module.DEFAULT_SELECTION)

    def test_public_app_contains_no_character_payload(self):
        forbidden = {".bundle", ".unity3d", ".ab", ".glb", ".fbx", ".wav", ".mp4"}
        payloads = [
            path for path in (APP / "Assets").rglob("*")
            if path.is_file() and path.suffix.lower() in forbidden
        ]
        self.assertEqual(payloads, [])


if __name__ == "__main__":
    unittest.main()
