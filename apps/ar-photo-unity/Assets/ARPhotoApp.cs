using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GakumasPhotoMode;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace DigitalKotone.ARPhoto
{
    /// <summary>
    /// Asset-free Android AR host for the reconstructed character renderer.
    /// Compatible character bundles are loaded from app-private storage.
    /// </summary>
    public sealed class ARPhotoApp : MonoBehaviour
    {
        private const string DataDirectory = "character-data";
        private readonly List<ARRaycastHit> _hits = new List<ARRaycastHit>();
        private ARRaycastManager _raycasts;
        private ARPlaneManager _planes;
        private ARAnchorManager _anchors;
        private Camera _camera;
        private CharacterSceneRuntime _runtime;
        private GameObject _character;
        private ARAnchor _anchor;
        private string _status = "Starting AR";
        private bool _capturing;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateApplication()
        {
            if (FindObjectOfType<ARPhotoApp>() == null)
                new GameObject("AR Photo Application").AddComponent<ARPhotoApp>();
        }

        private IEnumerator Start()
        {
            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            BuildArRig();
            yield return null;
            yield return null;
            TryLoadCharacter();
        }

        private void BuildArRig()
        {
            var input = new GameObject("AR Input Manager");
            input.AddComponent<ARInputManager>();

            var sessionObject = new GameObject("AR Session");
            sessionObject.AddComponent<ARSession>();

            var originObject = new GameObject("XR Origin");
            XROrigin origin = originObject.AddComponent<XROrigin>();
            _raycasts = originObject.AddComponent<ARRaycastManager>();
            _planes = originObject.AddComponent<ARPlaneManager>();
            _anchors = originObject.AddComponent<ARAnchorManager>();

            var offset = new GameObject("Camera Offset");
            offset.transform.SetParent(originObject.transform, false);
            var cameraObject = new GameObject("AR Camera");
            cameraObject.transform.SetParent(offset.transform, false);
            cameraObject.tag = "MainCamera";
            _camera = cameraObject.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.nearClipPlane = 0.02f;
            _camera.farClipPlane = 30f;
            cameraObject.AddComponent<AudioListener>();
            cameraObject.AddComponent<ARCameraManager>();
            cameraObject.AddComponent<ARCameraBackground>();
            cameraObject.AddComponent<ARCameraPoseDriver>();

            origin.Camera = _camera;
            origin.CameraFloorOffsetObject = offset;
            origin.Origin = originObject;
            _status = "Move the phone until a plane is detected";
        }

        private void TryLoadCharacter()
        {
            string dataRoot = Path.Combine(Application.persistentDataPath, DataDirectory);
            if (!Directory.Exists(dataRoot))
            {
                _status = "Character data missing: " + dataRoot;
                return;
            }

            try
            {
                _runtime = gameObject.AddComponent<CharacterSceneRuntime>();
                _runtime.Initialize(new CharacterSceneOptions
                {
                    DataRoot = dataRoot,
                    CharacterId = "fktn",
                    CostumeLabel = "cstm-0000",
                    MotionLabel = "photo-idle-001",
                    StartStory = false,
                    EnableOrbitInput = false,
                    HostCamera = _camera,
                    DisableDefaultEnvironment = true,
                });
                _character = _runtime.CharacterRoot;
                _character.SetActive(false);
                _status = "Renderer ready. Tap a detected plane to place the character";
            }
            catch (Exception exception)
            {
                _status = "Renderer failed: " + exception.Message;
                Debug.LogException(exception);
            }
        }

        private void Update()
        {
            if (_capturing || _character == null || ARSession.state != ARSessionState.SessionTracking)
                return;
            if (Input.touchCount == 1)
            {
                Touch touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began && touch.position.y < Screen.height - 280f)
                    Place(touch.position);
            }
            else if (Input.touchCount == 2 && _character.activeSelf)
            {
                Touch first = Input.GetTouch(0);
                Touch second = Input.GetTouch(1);
                Vector2 previous = first.position - first.deltaPosition -
                                   (second.position - second.deltaPosition);
                Vector2 current = first.position - second.position;
                if (previous.sqrMagnitude < 1f) return;
                float factor = current.magnitude / previous.magnitude;
                float scale = Mathf.Clamp(_character.transform.localScale.x * factor, 0.1f, 3f);
                _character.transform.localScale = Vector3.one * scale;
                _character.transform.Rotate(Vector3.up,
                    -Vector2.SignedAngle(previous, current), Space.Self);
            }
        }

        private bool Place(Vector2 screenPoint)
        {
            _hits.Clear();
            if (!_raycasts.Raycast(screenPoint, _hits, TrackableType.PlaneWithinPolygon))
            {
                _status = "No tracked plane under the tap";
                return false;
            }
            ARPlane plane = _planes.GetPlane(_hits[0].trackableId);
            if (plane == null || plane.trackingState != TrackingState.Tracking) return false;
            ARAnchor next = _anchors.AttachAnchor(plane, _hits[0].pose);
            if (next == null)
            {
                _status = "Could not create an AR anchor";
                return false;
            }

            ClearPlacement();
            _anchor = next;
            _character.transform.SetParent(_anchor.transform, false);
            _character.transform.localPosition = Vector3.zero;
            Vector3 towardCamera = _camera.transform.position - _anchor.transform.position;
            towardCamera.y = 0f;
            _character.transform.rotation = towardCamera.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(towardCamera.normalized, Vector3.up)
                : _anchor.transform.rotation;
            _character.transform.localScale = Vector3.one;
            _character.SetActive(true);
            _status = "Character placed. Pinch to scale and twist to rotate";
            return true;
        }

        private void ClearPlacement()
        {
            if (_character != null)
            {
                _character.transform.SetParent(null, true);
                _character.SetActive(false);
            }
            if (_anchor != null) Destroy(_anchor.gameObject);
            _anchor = null;
        }

        private void OnGUI()
        {
            float scale = Mathf.Max(1f, Screen.dpi / 180f);
            GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);
            float width = Screen.width / scale;
            GUI.Box(new Rect(12f, 12f, width - 24f, 126f), "Gakumas AR Photo\n" + _status);
            if (GUI.Button(new Rect(24f, 82f, 132f, 42f), "Capture"))
                StartCoroutine(Capture());
            if (GUI.Button(new Rect(168f, 82f, 116f, 42f), "Clear")) ClearPlacement();
            if (_runtime == null && GUI.Button(new Rect(296f, 82f, 148f, 42f), "Reload data"))
                TryLoadCharacter();
        }

        private IEnumerator Capture()
        {
            if (_capturing) yield break;
            _capturing = true;
            yield return new WaitForEndOfFrame();
            string path = Path.Combine(Application.persistentDataPath,
                "ar-photo-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return new WaitForEndOfFrame();
            _status = "Saved " + path;
            _capturing = false;
        }

        private void OnDestroy()
        {
            ClearPlacement();
        }
    }

    /// <summary>Applies the provider's center-eye pose without a serialized scene dependency.</summary>
    public sealed class ARCameraPoseDriver : MonoBehaviour
    {
        private void Update()
        {
            transform.localPosition = InputTracking.GetLocalPosition(XRNode.CenterEye);
            transform.localRotation = InputTracking.GetLocalRotation(XRNode.CenterEye);
        }
    }
}
