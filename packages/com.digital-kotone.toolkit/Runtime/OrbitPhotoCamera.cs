using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed class OrbitPhotoCamera : MonoBehaviour
    {
        public Vector3 target = new Vector3(0f, 0.88f, 0f);
        public float distance = 3.32f;
        public float yaw = 180f;
        public float pitch = 1.5f;
        public float fov = 31f;

        private Camera _camera;
        private bool _storyPoseActive;
        private Vector3 _storyPosition;
        private Quaternion _storyRotation;
        private float _storyFov = 31f;
        private float _storyYawOffset;
        private float _storyPitchOffset;
        private float _storyZoom = 1f;
        private Vector3 _storyPan;
        private Vector3 _defaultTarget = new Vector3(0f, 0.88f, 0f);
        private float _defaultDistance = 3.32f;
        private float _defaultYaw = 180f;
        private float _defaultPitch = 1.5f;
        private float _defaultFov = 31f;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        private void Update()
        {
            if (_storyPoseActive)
            {
                UpdateStoryControls();
                ApplyStoryPoseInternal();
                return;
            }
            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxis("Mouse X") * 4f;
                pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 4f, -45f, 75f);
            }
            if (Input.GetMouseButton(2))
            {
                Vector3 pan = (-transform.right * Input.GetAxis("Mouse X") - transform.up * Input.GetAxis("Mouse Y")) * 0.012f * distance;
                target += pan;
            }
            distance = Mathf.Clamp(distance * Mathf.Exp(-Input.mouseScrollDelta.y * 0.12f), 0.45f, 8f);
            ApplyPose();
        }

        private void UpdateStoryControls()
        {
            if (Input.GetMouseButton(1))
            {
                _storyYawOffset += Input.GetAxis("Mouse X") * 4f;
                _storyPitchOffset = Mathf.Clamp(_storyPitchOffset - Input.GetAxis("Mouse Y") * 4f, -55f, 70f);
            }
            if (Input.GetMouseButton(2))
            {
                _storyPan += (-transform.right * Input.GetAxis("Mouse X") - transform.up * Input.GetAxis("Mouse Y")) * 0.010f;
            }
            _storyZoom = Mathf.Clamp(_storyZoom * Mathf.Exp(-Input.mouseScrollDelta.y * 0.12f), 0.28f, 3.5f);
        }

        public void SetStoryPose(Vector3 position, Quaternion rotation, float fieldOfView)
        {
            _storyPoseActive = true;
            _storyPosition = position;
            _storyRotation = rotation;
            _storyFov = fieldOfView;
            ApplyStoryPoseInternal();
        }

        public void ExitStoryPose()
        {
            _storyPoseActive = false;
            ResetStoryOffsets();
        }

        public void ResetStoryOffsets()
        {
            _storyYawOffset = 0f;
            _storyPitchOffset = 0f;
            _storyZoom = 1f;
            _storyPan = Vector3.zero;
            if (_storyPoseActive) ApplyStoryPoseInternal();
        }

        private void ApplyStoryPoseInternal()
        {
            if (_camera == null) _camera = GetComponent<Camera>();
            Vector3 pivot = target + _storyPan;
            Quaternion delta = Quaternion.Euler(_storyPitchOffset, _storyYawOffset, 0f);
            Vector3 offset = (_storyPosition - target) * _storyZoom;
            transform.position = pivot + delta * offset;
            transform.rotation = delta * _storyRotation;
            _camera.fieldOfView = _storyFov;
        }

        public void ResetPose()
        {
            target = _defaultTarget;
            distance = _defaultDistance;
            yaw = _defaultYaw;
            pitch = _defaultPitch;
            fov = _defaultFov;
            ApplyPose();
        }

        public void ConfigureDefaultPose(
            Vector3 defaultTarget,
            float defaultDistance,
            float defaultYaw,
            float defaultPitch,
            float defaultFov)
        {
            _defaultTarget = defaultTarget;
            _defaultDistance = defaultDistance;
            _defaultYaw = defaultYaw;
            _defaultPitch = defaultPitch;
            _defaultFov = defaultFov;
            ResetPose();
        }

        public void ApplyPose()
        {
            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
            }
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.rotation = rotation;
            transform.position = target - rotation * Vector3.forward * distance;
            _camera.fieldOfView = fov;
        }
    }
}
