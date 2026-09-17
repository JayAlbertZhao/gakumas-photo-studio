"""Source-release and synthetic fixture checks for the independent AR app."""

import importlib.util
import json
from pathlib import Path
import struct
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "apps" / "ar-photo-android"
GENERATOR = APP / "tools" / "generate_sample_glb.py"
TOOLKIT = APP / "ar-toolkit/src/main/java/org/digital_kotone/arphoto"


class ArPhotoAndroidContractTest(unittest.TestCase):
    def test_synthetic_glb_is_self_contained_and_well_formed(self):
        spec = importlib.util.spec_from_file_location("ar_photo_sample", GENERATOR)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        data = module.make_glb()
        magic, version, size = struct.unpack_from("<4sII", data)
        self.assertEqual((magic, version, size), (b"glTF", 2, len(data)))
        json_size, chunk_type = struct.unpack_from("<I4s", data, 12)
        self.assertEqual(chunk_type, b"JSON")
        document = json.loads(data[20:20 + json_size])
        self.assertEqual(document["asset"]["version"], "2.0")
        self.assertEqual(document["scenes"][0]["nodes"], [0])
        self.assertEqual(len(document["meshes"]), 1)
        self.assertTrue(all("uri" not in entry for entry in document["buffers"]))
        self.assertTrue(all("uri" not in entry for entry in document.get("images", [])))
        binary_offset = 20 + json_size
        binary_size, binary_type = struct.unpack_from("<I4s", data, binary_offset)
        self.assertEqual(binary_type, b"BIN\0")
        self.assertEqual(binary_offset + 8 + binary_size, len(data))
        self.assertGreaterEqual(binary_size, document["buffers"][0]["byteLength"])

    def test_animated_glb_has_two_selectable_self_contained_clips(self):
        spec = importlib.util.spec_from_file_location("ar_photo_sample", GENERATOR)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        data = module.make_glb(animated=True)
        json_size = struct.unpack_from("<I", data, 12)[0]
        document = json.loads(data[20:20 + json_size])
        self.assertEqual([clip["name"] for clip in document["animations"]], ["Bounce", "Slide"])
        self.assertTrue(all(clip["channels"][0]["target"] ==
                            {"node": 0, "path": "translation"} for clip in document["animations"]))
        self.assertTrue(all("uri" not in entry for entry in document["buffers"]))
        self.assertEqual(document["accessors"][2]["min"], [0.0])
        self.assertEqual(document["accessors"][2]["max"], [2.0])
        binary_offset = 20 + json_size + 8
        time_offset = binary_offset + document["bufferViews"][2]["byteOffset"]
        self.assertEqual(struct.unpack_from("<3f", data, time_offset), (0.0, 1.0, 2.0))
        subject = (APP / "app/src/main/java/org/digital_kotone/arphoto/PhotoSubject.kt").read_text(encoding="utf-8")
        self.assertIn("autoAnimate = false", subject)
        self.assertIn("activeAnimation.get()?.let { node.stopAnimation(it) }", subject)
        self.assertIn("animationName?.let { node.playAnimation(it) }", subject)
        self.assertNotIn("animationName = animationName", subject)

    def test_emulator_smoke_accessibility_helpers(self):
        script = APP / "tools/smoke_emulator.py"
        spec = importlib.util.spec_from_file_location("ar_photo_smoke", script)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.assertEqual(module.bounds_center("[609,2133][845,2259]"), (727, 2196))
        root = ET.fromstring('<hierarchy><node text="大小 1.0×"/><node text="Slide"/></hierarchy>')
        self.assertEqual(module.size_label(root), "大小 1.0×")
        self.assertIsNotNone(module.find_text(root, "Slide"))
        generator_spec = importlib.util.spec_from_file_location("ar_photo_sample", GENERATOR)
        generator = importlib.util.module_from_spec(generator_spec)
        generator_spec.loader.exec_module(generator)
        invalid = module.with_external_image_uri(generator.make_glb(animated=True))
        self.assertEqual(struct.unpack_from("<I", invalid, 8)[0], len(invalid))
        json_size = struct.unpack_from("<I", invalid, 12)[0]
        document = json.loads(invalid[20:20 + json_size])
        self.assertEqual(document["images"], [{"uri": "untrusted-external.png"}])

    def test_replay_smoke_uses_reviewed_public_dataset_hash(self):
        script = APP / "tools/smoke_replay_emulator.py"
        spec = importlib.util.spec_from_file_location("ar_photo_replay_smoke", script)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.assertEqual(len(module.DATASET_SHA256), 64)
        self.assertEqual(module.load_smoke().PACKAGE, "org.digital_kotone.arphoto")

    def test_optional_ar_and_private_import_boundary(self):
        manifest = (APP / "app/src/main/AndroidManifest.xml").read_text(encoding="utf-8")
        importer = (TOOLKIT / "SubjectImporter.kt").read_text(encoding="utf-8")
        self.assertIn('android:value="optional"', manifest)
        self.assertIn('android:required="false"', manifest)
        self.assertIn('context.filesDir, "subject.glb"', importer)
        self.assertIn('uri.startsWith("data:")', importer)

    def test_recording_and_photo_are_distinct_outputs(self):
        recorder = (TOOLKIT / "ArSessionRecorder.kt").read_text(encoding="utf-8")
        photos = (TOOLKIT / "PhotoStore.kt").read_text(encoding="utf-8")
        screen = (APP / "app/src/main/java/org/digital_kotone/arphoto/PhotoScreen.kt").read_text(encoding="utf-8")
        self.assertIn("session.startRecording", recorder)
        self.assertIn("session.stopRecording", recorder)
        self.assertIn("Environment.DIRECTORY_MOVIES", recorder)
        self.assertIn("Environment.DIRECTORY_PICTURES", photos)
        self.assertIn("PixelCopy.request", photos)
        self.assertIn("planeRenderer = anchor == null && !captureInProgress", screen)

    def test_replay_import_is_private_and_bound_before_session_start(self):
        importer = (TOOLKIT / "DatasetImporter.kt").read_text(encoding="utf-8")
        screen = (APP / "app/src/main/java/org/digital_kotone/arphoto/PhotoScreen.kt").read_text(encoding="utf-8")
        self.assertIn('context.filesDir, "session-playback.mp4"', importer)
        self.assertIn("256L * 1024L * 1024L", importer)
        self.assertIn("playbackDataset = if (replayMode) playbackDataset else null", screen)
        self.assertIn("key(replayMode, playbackRevision, replayRun)", screen)
        self.assertIn("PlaybackStatus.FINISHED", screen)
        self.assertIn("pauseForModeSwitch()\n                            replayRun++", screen)
        self.assertIn('Text("收起控件")', screen)
        self.assertIn('Text("显示控件")', screen)
        self.assertIn("else if (!captureInProgress)", screen)

    def test_model_instance_recreated_across_independent_scenes(self):
        screen = (APP / "app/src/main/java/org/digital_kotone/arphoto/PhotoScreen.kt").read_text(encoding="utf-8")
        subject = (APP / "app/src/main/java/org/digital_kotone/arphoto/PhotoSubject.kt").read_text(encoding="utf-8")
        activity = (APP / "app/src/main/java/org/digital_kotone/arphoto/MainActivity.kt").read_text(encoding="utf-8")
        self.assertIn("key(arMode, modelRevision, modelTransformRevision)", screen)
        self.assertIn("LaunchedEffect(pendingModelTransformRevision)", screen)
        self.assertIn("onValueChangeFinished", screen)
        self.assertIn("scaleToUnits = 1.6f * size", subject)
        self.assertIn("position = Position(offset.x + importedPosition.x", subject)
        self.assertIn("anchorPose = placed.pose", screen)
        self.assertIn("renderer.clearOptions = renderer.clearOptions.apply", screen)
        self.assertIn("clear = true", screen)
        self.assertNotIn("previewRoot.get()?.position", screen)
        self.assertIn("modelRevision.intValue++", activity)

    def test_camera_permission_gates_ar_scene_creation(self):
        screen = (APP / "app/src/main/java/org/digital_kotone/arphoto/PhotoScreen.kt").read_text(encoding="utf-8")
        self.assertIn("ActivityResultContracts.RequestPermission()", screen)
        self.assertIn("var permissionTargetReplay by rememberSaveable", screen)
        self.assertIn("context.checkSelfPermission(Manifest.permission.CAMERA)", screen)
        self.assertIn("cameraPermissionLauncher.launch(Manifest.permission.CAMERA)", screen)
        self.assertIn("enterArWhenReady(permissionTargetReplay)", screen)
        self.assertIn("switchToArMode(false)", screen)
        self.assertIn("switchToArMode(true)", screen)

    def test_imported_model_tracks_anchor_refinements(self):
        screen = (APP / "app/src/main/java/org/digital_kotone/arphoto/PhotoScreen.kt").read_text(encoding="utf-8")
        subject = (APP / "app/src/main/java/org/digital_kotone/arphoto/PhotoSubject.kt").read_text(encoding="utf-8")
        placement = (TOOLKIT / "AnchorPlacement.kt").read_text(encoding="utf-8")
        self.assertIn("placed.trackingState == TrackingState.TRACKING", screen)
        self.assertIn("tracked.instance === imported", screen)
        self.assertIn("applyAnchorPose(tracked.node, tracked.alignment, placed.pose, yaw)", screen)
        self.assertIn("trackedImportedNode.set(null)", screen)
        self.assertIn("onImportedNodeReady?.invoke(this, offset)", subject)
        self.assertIn("anchorPose.transformPoint(floatArrayOf(alignment.x, alignment.y, alignment.z))", placement)
        self.assertIn("anchorPose.compose(Pose.makeRotation", placement)
        self.assertIn("node.quaternion = Quaternion(rotation.qx()", placement)

    def test_arcore_install_preflight_precedes_optional_ar_scene(self):
        activity = (APP / "app/src/main/java/org/digital_kotone/arphoto/MainActivity.kt").read_text(encoding="utf-8")
        screen = (APP / "app/src/main/java/org/digital_kotone/arphoto/PhotoScreen.kt").read_text(encoding="utf-8")
        self.assertIn("checkAvailabilityAsync(this)", activity)
        self.assertIn('getApplicationInfo("com.google.ar.core", 0).enabled', activity)
        self.assertIn("requestInstall(this, userRequested)", activity)
        self.assertIn("if (waitingForArCoreInstall) requestArCoreInstall(userRequested = false)", activity)
        self.assertIn("onEnsureArReady = { onReady, onFailure -> ensureArCoreReady", activity)
        self.assertIn("onCancelArRequest = { cancelArCoreRequest() }", activity)
        self.assertIn("onEnsureArReady({", screen)
        self.assertIn("onCancelArRequest()", screen)
        self.assertIn("onSessionFailed = { error ->\n                        sessionReady = false\n                        arMode = false", screen)

    def test_ci_builds_native_ar_app_without_publishing_assets(self):
        workflow = (ROOT / ".github/workflows/source-audit.yml").read_text(encoding="utf-8")
        self.assertIn("android-ar-build:", workflow)
        self.assertIn("'platforms;android-36'", workflow)
        self.assertIn("gradle-version: '9.2.1'", workflow)
        self.assertIn("./gradlew :app:assembleDebug :app:lintDebug :ar-toolkit:lintDebug", workflow)

    def test_reusable_android_toolkit_is_consumed_by_app(self):
        settings = (APP / "settings.gradle.kts").read_text(encoding="utf-8")
        app_gradle = (APP / "app/build.gradle.kts").read_text(encoding="utf-8")
        toolkit_gradle = (APP / "ar-toolkit/build.gradle.kts").read_text(encoding="utf-8")
        self.assertIn('include(":ar-toolkit")', settings)
        self.assertIn('implementation(project(":ar-toolkit"))', app_gradle)
        self.assertIn('id("com.android.library")', toolkit_gradle)
        for name in ("SubjectImporter", "DatasetImporter", "ArSessionRecorder", "PhotoStore"):
            self.assertTrue((TOOLKIT / f"{name}.kt").is_file())


if __name__ == "__main__":
    unittest.main()
