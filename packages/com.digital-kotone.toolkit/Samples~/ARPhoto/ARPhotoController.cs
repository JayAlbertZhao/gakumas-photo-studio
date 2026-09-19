using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GakumasPhotoMode.Samples.ARPhoto
{
    /// <summary>
    /// A deliberately small AR application host. The subject is an ordinary prefab,
    /// so the sample contains no game data and does not depend on a particular model loader.
    /// Requires AR Foundation 5.x and an active platform provider in the host project.
    /// </summary>
    public sealed class ARPhotoController : MonoBehaviour
    {
        [SerializeField] private ARRaycastManager raycastManager;
        [SerializeField] private ARPlaneManager planeManager;
        [SerializeField] private ARAnchorManager anchorManager;
        [SerializeField] private GameObject subjectPrefab;
        [SerializeField] private Canvas captureUi;
        [SerializeField, Range(0.05f, 5f)] private float initialScale = 1f;
        [SerializeField, Range(0.05f, 5f)] private float minimumScale = 0.1f;
        [SerializeField, Range(0.05f, 5f)] private float maximumScale = 3f;

        private readonly List<ARRaycastHit> hits = new List<ARRaycastHit>();
        private ARAnchor currentAnchor;
        private GameObject subject;
        private GameObject externalSubject;
        private bool capturing;

        public bool HasSubject { get { return subject != null; } }
        public string LastCapturePath { get; private set; }

        /// <summary>
        /// Supply a live subject owned by another component, for example
        /// CharacterSceneRuntime. The controller only places it; Clear never destroys it.
        /// </summary>
        public void SetExternalSubject(GameObject value)
        {
            Clear();
            externalSubject = value;
            if (externalSubject != null) externalSubject.SetActive(false);
        }

        private void Update()
        {
            if (ARSession.state != ARSessionState.SessionTracking || capturing) return;
            if (Input.touchCount == 1)
            {
                Touch touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began &&
                    (EventSystem.current == null ||
                     !EventSystem.current.IsPointerOverGameObject(touch.fingerId)))
                    PlaceAt(touch.position);
            }
            else if (Input.touchCount == 2 && subject != null)
            {
                Touch first = Input.GetTouch(0);
                Touch second = Input.GetTouch(1);
                if (first.phase == TouchPhase.Began || second.phase == TouchPhase.Began) return;
                Vector2 previous = (first.position - first.deltaPosition) -
                                   (second.position - second.deltaPosition);
                Vector2 current = first.position - second.position;
                float previousLength = previous.magnitude;
                if (previousLength < 1f) return;
                float scale = Mathf.Clamp(subject.transform.localScale.x *
                    current.magnitude / previousLength, minimumScale, maximumScale);
                subject.transform.localScale = Vector3.one * scale;
                float yaw = Vector2.SignedAngle(previous, current);
                subject.transform.Rotate(Vector3.up, -yaw, Space.Self);
            }
        }

        /// <summary>Tap a tracked plane to place or replace the subject's anchor.</summary>
        public bool PlaceAt(Vector2 screenPoint)
        {
            if (ARSession.state != ARSessionState.SessionTracking ||
                raycastManager == null || planeManager == null || anchorManager == null ||
                !raycastManager.enabled || !planeManager.enabled || !anchorManager.enabled ||
                anchorManager.subsystem == null)
                return false;
            hits.Clear();
            if (!raycastManager.Raycast(screenPoint, hits, TrackableType.PlaneWithinPolygon))
                return false;
            ARPlane plane = planeManager.GetPlane(hits[0].trackableId);
            if (plane == null || plane.trackingState != TrackingState.Tracking)
                return false;
            ARAnchor next = anchorManager.AttachAnchor(plane, hits[0].pose);
            if (next == null) return false;

            // Do not discard the old placement until the new anchor succeeds.
            Clear();
            currentAnchor = next;
            subject = externalSubject != null
                ? externalSubject
                : subjectPrefab == null
                    ? GameObject.CreatePrimitive(PrimitiveType.Capsule)
                    : Instantiate(subjectPrefab);
            subject.name = externalSubject != null
                ? externalSubject.name
                : subjectPrefab == null ? "AR Photo Placeholder" : subjectPrefab.name;
            subject.transform.SetParent(next.transform, false);
            subject.transform.localPosition = Vector3.zero;
            subject.transform.localRotation = Quaternion.identity;
            subject.transform.localScale = Vector3.one * initialScale;
            subject.SetActive(true);
            return true;
        }

        public void Clear()
        {
            if (subject != null)
            {
                if (subject == externalSubject)
                {
                    subject.transform.SetParent(null, true);
                    subject.SetActive(false);
                }
                else Destroy(subject);
            }
            if (currentAnchor != null) Destroy(currentAnchor.gameObject);
            subject = null;
            currentAnchor = null;
        }

        /// <summary>Optional UI hook for an Animator Controller on the supplied subject.</summary>
        public bool SetAnimationTrigger(string triggerName)
        {
            if (subject == null || string.IsNullOrWhiteSpace(triggerName)) return false;
            Animator animator = subject.GetComponentInChildren<Animator>();
            if (animator == null) return false;
            animator.SetTrigger(triggerName);
            return true;
        }

        /// <summary>Capture the actual device display, including the AR camera background.</summary>
        public void Capture()
        {
            if (!capturing && ARSession.state == ARSessionState.SessionTracking)
                StartCoroutine(CaptureAfterUiHides());
        }

        private IEnumerator CaptureAfterUiHides()
        {
            capturing = true;
            bool wasEnabled = captureUi != null && captureUi.enabled;
            if (captureUi != null) captureUi.enabled = false;
            yield return new WaitForEndOfFrame();
            string filename = "ar-photo-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png";
            LastCapturePath = Path.Combine(Application.persistentDataPath, filename);
            ScreenCapture.CaptureScreenshot(LastCapturePath);
            // Keep UI hidden through the capture frame; file encoding may finish later.
            yield return new WaitForEndOfFrame();
            if (captureUi != null) captureUi.enabled = wasEnabled;
            capturing = false;
            Debug.Log("AR photo queued: " + LastCapturePath);
        }

        private void OnDisable()
        {
            Clear();
        }
    }
}
