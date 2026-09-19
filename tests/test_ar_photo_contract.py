"""Source-level guardrails for the optional mobile AR sample (not a device test)."""
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / 'packages/com.digital-kotone.toolkit'
SAMPLE = PACKAGE / 'Samples~/ARPhoto'


class ArPhotoSampleTests(unittest.TestCase):
    def test_sample_is_opt_in_and_keeps_provider_out_of_core_dependencies(self):
        package = json.loads((PACKAGE / 'package.json').read_text(encoding='utf-8'))
        self.assertIn('Samples~/ARPhoto', {entry['path'] for entry in package['samples']})
        self.assertNotIn('com.unity.xr.arfoundation', package['dependencies'])
        self.assertNotIn('com.unity.xr.arcore', package['dependencies'])
        self.assertNotIn('com.unity.xr.arkit', package['dependencies'])

    def test_sample_preserves_live_camera_capture_and_anchor_lifecycle(self):
        code = (SAMPLE / 'ARPhotoController.cs').read_text(encoding='utf-8')
        for contract in ('ARSessionState.SessionTracking', 'TrackableType.PlaneWithinPolygon',
                         'planeManager.GetPlane(hits[0].trackableId)',
                         'anchorManager.AttachAnchor(plane, hits[0].pose)',
                         'ScreenCapture.CaptureScreenshot(LastCapturePath)',
                         'Application.persistentDataPath', 'public void Clear()'):
            self.assertIn(contract, code)
        self.assertNotIn('AssetBundle.Load', code)

    def test_sample_can_place_the_existing_character_renderer_without_owning_it(self):
        controller = (SAMPLE / 'ARPhotoController.cs').read_text(encoding='utf-8')
        host = (SAMPLE / 'ARCharacterSceneHost.cs').read_text(encoding='utf-8')
        for contract in ('SetExternalSubject(GameObject value)',
                         'subject == externalSubject', 'subject.SetActive(false)'):
            self.assertIn(contract, controller)
        for contract in ('CharacterSceneRuntime', 'HostCamera = arCamera',
                         'DisableDefaultEnvironment = true',
                         'Path.Combine(Application.persistentDataPath, dataDirectoryName)',
                         'placement.SetExternalSubject(Runtime.CharacterRoot)'):
            self.assertIn(contract, host)


if __name__ == '__main__':
    unittest.main()
