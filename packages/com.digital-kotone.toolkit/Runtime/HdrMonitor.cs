using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public enum MonitorUpdateMode { WhenDirty, FixedRate, EveryCall }

    /// <summary>Explicit HDR UI/scene camera producer; no automatic frame loop or global bindings.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class HdrMonitor : MonoBehaviour
    {
        public readonly struct Frame
        {
            private readonly HdrMonitor _owner;
            public readonly RenderTexture texture;
            public readonly uint sequence;
            public readonly double seconds;
            public readonly ulong contentVersion;
            public bool IsCurrent => _owner != null && _owner.Current(this);
            internal Frame(HdrMonitor owner)
            { _owner = owner; texture = owner._output; sequence = owner.RenderSequence; seconds = owner._renderTime; contentVersion = owner._version; }
        }

        public bool monitorEnabled;
        [Range(1, 4096)] public int width = 512, height = 512;
        public MonitorUpdateMode updateMode = MonitorUpdateMode.WhenDirty;
        [Range(1, 240)] public float updatesPerSecond = 30;
        public uint RenderSequence { get; private set; }
        public bool DidRender { get; private set; }
        public string UnavailableReason { get; private set; }
        // Runs only for a scheduled capture, after its target is installed.
        // The host can evaluate UI animation/layout without work on skipped calls.
        public event Action<double> PrepareCapture;

        private Camera _camera;
        private RenderTexture _capture, _output;
        private bool _ready, _requested = true, _rendering, _releasePending;
        private double _renderTime, _observedTime;
        private ulong _version;
        private Matrix4x4 _view, _projection;
        private int _layers;
        private Color _background;
        private CameraClearFlags _clear;
        private RenderTexture _originalTarget;
        private MonitorUpdateMode _mode;
        private float _rate;

        private void OnEnable() { _camera = GetComponent<Camera>(); _requested = true; }
        public void RequestUpdate() { _requested = true; }

        /// <summary>Call after evaluating content/Canvas layout, outside camera render callbacks.</summary>
        public bool TryUpdate(double seconds, ulong contentVersion, out Frame frame)
        {
            frame = default; DidRender = false;
            if (_rendering || Camera.current != null) { UnavailableReason = "Monitor update inside camera rendering"; return false; }
            string invalid = Validate(seconds);
            if (invalid != null) return Fail(invalid);
            bool changed = !ConfigurationMatches();
            bool due = !_ready || changed || _requested || contentVersion != _version || seconds < _observedTime ||
                updateMode == MonitorUpdateMode.EveryCall ||
                (updateMode == MonitorUpdateMode.FixedRate && seconds - _renderTime >= 1.0 / updatesPerSecond);
            _observedTime = seconds;
            if (!due) { UnavailableReason = null; return TryGetFrame(out frame); }
            if (!Allocate()) return Fail("Monitor HDR target allocation failed");

            // Preserve implicit/custom view and projection modes: never assign
            // either matrix or aspect. Only the dedicated camera's target and
            // required render flags are temporarily changed, then restored.
            RenderTexture oldTarget = _camera.targetTexture, oldActive = RenderTexture.active;
            bool oldHdr = _camera.allowHDR, oldMsaa = _camera.allowMSAA, oldSrgb = GL.sRGBWrite;
            RenderingPath oldPath = _camera.renderingPath;
            Matrix4x4 view = _camera.worldToCameraMatrix, projection = _camera.projectionMatrix;
            _rendering = true; _requested = false;
            try
            {
                _camera.targetTexture = _capture; _camera.allowHDR = true; _camera.allowMSAA = false;
                _camera.renderingPath = RenderingPath.Forward;
                PrepareCapture?.Invoke(seconds);
                if (_releasePending || !isActiveAndEnabled || !monitorEnabled)
                    throw new InvalidOperationException("Monitor disabled during content preparation");
                _camera.Render();
                if (_releasePending || !isActiveAndEnabled || !monitorEnabled || _camera == null || _camera.enabled)
                    throw new InvalidOperationException("Monitor disabled or camera ownership changed during capture");
                // Stable published texture, distinct from the camera target:
                // a source may intentionally read the last published frame,
                // but never samples its own active color attachment.
                GL.sRGBWrite = false;
                Graphics.Blit(_capture, _output);
                _view = view; _projection = projection; _layers = _camera.cullingMask;
                _background = _camera.backgroundColor; _clear = _camera.clearFlags; _originalTarget = oldTarget;
                _mode = updateMode; _rate = updatesPerSecond;
                _renderTime = seconds; _version = contentVersion; _ready = true;
                RenderSequence++; DidRender = true; UnavailableReason = null;
            }
            catch (Exception error) { _releasePending = true; UnavailableReason = error.Message; }
            finally
            {
                if (_camera != null) { _camera.targetTexture = oldTarget; _camera.allowHDR = oldHdr; _camera.allowMSAA = oldMsaa; _camera.renderingPath = oldPath; }
                RenderTexture.active = oldActive; GL.sRGBWrite = oldSrgb; _rendering = false;
                if (_releasePending) ReleaseResources();
            }
            return TryGetFrame(out frame);
        }

        public bool TryGetFrame(out Frame frame)
        {
            frame = default;
            if (!_ready || Validate(_renderTime) != null || !ConfigurationMatches()) return false;
            frame = new Frame(this); return true;
        }
        private bool Current(Frame frame) => frame.sequence == RenderSequence && frame.texture == _output && TryGetFrame(out _);
        private string Validate(double seconds)
        {
            if (!isActiveAndEnabled || !monitorEnabled) return "Monitor disabled";
            if (_camera == null || _camera.enabled) return "Requires a dedicated disabled camera";
            if (GraphicsSettings.currentRenderPipeline != null || _camera.stereoEnabled || _camera.allowDynamicResolution || _camera.rect != new Rect(0, 0, 1, 1))
                return "Requires Built-in, full viewport, non-XR and fixed resolution";
            if (_camera.clearFlags != CameraClearFlags.SolidColor) return "Monitor camera requires SolidColor clear";
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)) return "ARGBHalf unavailable";
            if (width < 1 || height < 1 || width > Mathf.Min(4096, SystemInfo.maxTextureSize) || height > Mathf.Min(4096, SystemInfo.maxTextureSize)) return "Invalid monitor dimensions";
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || Math.Abs(seconds) > 1e12 ||
                !Range(updatesPerSecond, 1, 240) || !Enum.IsDefined(typeof(MonitorUpdateMode), updateMode)) return "Invalid monitor schedule";
            Color clear = _camera.backgroundColor;
            if (!Range(clear.r, 0, 65504) || !Range(clear.g, 0, 65504) || !Range(clear.b, 0, 65504) || !Range(clear.a, 0, 1)) return "Invalid monitor clear color";
            Matrix4x4 view = _camera.worldToCameraMatrix, projection = _camera.projectionMatrix;
            for (int i = 0; i < 16; i++)
                if (!Range(view[i], -1e8f, 1e8f) || !Range(projection[i], -1e8f, 1e8f)) return "Nonfinite monitor camera matrix";
            return null;
        }
        private bool ConfigurationMatches() => _camera != null && !_camera.enabled &&
            _output != null && _capture != null && _output.IsCreated() && _capture.IsCreated() &&
            _output.width == width && _output.height == height && _camera.rect == new Rect(0, 0, 1, 1) &&
            _camera.worldToCameraMatrix == _view && _camera.projectionMatrix == _projection &&
            _layers == _camera.cullingMask && _clear == _camera.clearFlags && _background == _camera.backgroundColor &&
            _originalTarget == _camera.targetTexture && _mode == updateMode && _rate == updatesPerSecond;
        private bool Allocate()
        {
            if (_capture != null && _output != null && _capture.IsCreated() && _output.IsCreated() &&
                _capture.width == width && _capture.height == height && _output.width == width && _output.height == height) return true;
            ReleaseResources();
            try { _capture = NewTarget("Monitor capture", 24); _output = NewTarget("Monitor published HDR", 0); return _capture.IsCreated() && _output.IsCreated(); }
            catch (Exception) { return false; }
        }
        private RenderTexture NewTarget(string label, int depth)
        {
            var target = new RenderTexture(width, height, depth, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) {
                name = label, hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1, useMipMap = false,
                autoGenerateMips = false, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            target.Create(); return target;
        }
        private bool Fail(string reason) { UnavailableReason = reason; ReleaseResources(); return false; }
        private void OnDisable() { ReleaseResources(); }
        private void OnDestroy() { ReleaseResources(); }
        private void ReleaseResources()
        {
            if (_rendering) { _releasePending = true; return; }
            _ready = false; _requested = true; _releasePending = false; DidRender = false;
            Release(ref _capture); Release(ref _output);
        }
        private static void Release(ref RenderTexture target)
        { if (target != null) { target.Release(); if (Application.isPlaying) Destroy(target); else DestroyImmediate(target); target = null; } }
        internal static bool Range(float value, float min, float max) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;
        internal static bool Finite(Vector4 value)
        { for (int i = 0; i < 4; i++) if (!Range(value[i], -1e8f, 1e8f)) return false; return true; }
    }
}
