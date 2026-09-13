using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Reversible per-camera native skybox binding. Never changes global sky/GI or camera clear flags.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class SceneSkyCamera : MonoBehaviour
    {
        public SceneSkySettings settings = new SceneSkySettings();
        public string UnavailableReason { get; private set; }
        public bool IsBound => sky != null && assigned != null && sky.material == assigned && sky.enabled;
        private readonly SceneSkyMaterial resources = new SceneSkyMaterial();
        private Skybox sky, ownedSky;
        private Material previous, assigned;
        private bool created, previousEnabled, externallyChanged;

        private void OnEnable() => externallyChanged = false;
        private void OnPreCull() { if (!externallyChanged) TryApply(); }
        public bool TryApply()
        {
            // An explicit call or an enable cycle may reacquire ownership;
            // automatic camera renders never undo a detected external override.
            externallyChanged = false;
            var camera = GetComponent<Camera>();
            if (!isActiveAndEnabled || settings == null || !settings.enabled)
            { UnavailableReason = "Disabled"; ReleaseBinding(); return false; }
            if (camera.clearFlags != CameraClearFlags.Skybox || camera.stereoEnabled || camera.rect != new Rect(0, 0, 1, 1))
            { UnavailableReason = "Host requires full-viewport Skybox clear flags without XR"; ReleaseBinding(); return false; }
            var projection = camera.projectionMatrix;
            if (!SceneDeferredCamera.Matrix(projection) || Mathf.Abs(projection.m00) < 1e-8f || Mathf.Abs(projection.m11) < 1e-8f || projection.m01 != 0 || projection.m10 != 0)
            { UnavailableReason = "Sky requires a finite ordinary or off-axis camera projection"; ReleaseBinding(); return false; }
            if (sky != null && assigned != null && (sky.material != assigned || !sky.enabled))
            { externallyChanged = true; UnavailableReason = "Per-camera skybox binding changed externally"; ReleaseBinding(); return false; }
            if (!resources.TryUpdate(settings, out var material))
            { UnavailableReason = resources.UnavailableReason; ReleaseBinding(); return false; }
            if (sky == null)
            {
                sky = camera.GetComponent<Skybox>(); created = sky == null || sky == ownedSky && sky.material == null && !sky.enabled;
                if (sky == null) ownedSky = sky = camera.gameObject.AddComponent<Skybox>();
                if (!created && sky == ownedSky) ownedSky = null;
                previous = sky.material; previousEnabled = sky.enabled;
            }
            assigned = material; sky.material = material; sky.enabled = true; UnavailableReason = null; return true;
        }
        private void ReleaseBinding()
        {
            if (sky != null && sky.material == assigned)
            {
                // Keep our inert component until controller destruction, avoiding
                // deferred-Destroy races on a same-frame disable/enable cycle.
                if (created) { sky.material = null; sky.enabled = false; }
                else { sky.material = previous; if (sky.enabled) sky.enabled = previousEnabled; }
            }
            sky = null; previous = assigned = null; created = false; resources.Dispose();
        }
        private void OnDisable() { ReleaseBinding(); UnavailableReason = "Disabled"; }
        private void OnDestroy()
        { if (ownedSky != null && ownedSky.material == null && !ownedSky.enabled) Destroy(ownedSky); ownedSky = null; }
    }
}
