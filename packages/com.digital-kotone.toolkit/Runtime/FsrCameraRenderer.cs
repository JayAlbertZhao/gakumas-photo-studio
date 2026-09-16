using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Explicit manual-camera host. Camera.Render actually uses the chosen lower
    /// resolution. The caller presents the resulting full-size target and draws UI
    /// afterwards. Never attaches itself to a camera or changes application defaults.
    /// </summary>
    public sealed class FsrCameraRenderer : IDisposable
    {
        public readonly struct Frame
        {
            private readonly FsrCameraRenderer _owner;
            private readonly uint _generation;
            public readonly RenderTexture color;
            public readonly Vector2Int renderSize;
            public readonly FsrRenderer.Frame upscale;
            public readonly bool beforeDiffusion;
            internal Frame(FsrCameraRenderer owner)
            {
                _owner = owner; _generation = owner._generation; color = owner._destination;
                renderSize = owner.RenderSize; upscale = owner._upscale; beforeDiffusion = owner._integrated;
            }
            public bool IsCurrent => _owner != null && _owner._hasFrame && _owner._generation == _generation &&
                color != null && color.IsCreated() && upscale.IsCurrent;
        }

        private static readonly Dictionary<Camera, FsrCameraRenderer> Active = new Dictionary<Camera, FsrCameraRenderer>();
        private readonly FsrRenderer _renderer = new FsrRenderer();
        private RenderTexture _input, _bloomInput, _destination;
        private Material _bloomMaterial;
        private FsrSettings _settings;
        private FsrRenderer.Frame _upscale;
        private bool _rendering, _integrated, _upscaled, _presented, _hasFrame;
        private uint _generation;
        public string UnavailableReason { get; private set; }
        public Vector2Int RenderSize { get; private set; }
        public long EstimatedTargetBytes => _renderer.EstimatedTargetBytes +
            (_input != null && _input.IsCreated() ? 20L * _input.width * _input.height : 0) +
            (_bloomInput != null && _bloomInput.IsCreated() ? 16L * _bloomInput.width * _bloomInput.height : 0);
        public int TargetCount => _renderer.TargetCount + (_input != null ? 1 : 0) + (_bloomInput != null ? 1 : 0);

        public bool TryRender(Camera camera, RenderTexture destination, FsrSettings settings, out Frame frame,
            SceneDeferredCamera temporalSource = null, TemporalClassification classification = null)
        {
            frame = default;
            if (_rendering) { UnavailableReason = "FSR camera renderer is already rendering"; return false; }
            _generation++; _hasFrame = false; UnavailableReason = null;
            if (settings == null || !settings.enabled) { Release(); return false; }
            if (GraphicsSettings.currentRenderPipeline != null)
                return Fail("FSR manual Camera.Render host requires the Built-in pipeline; use FsrRenderer within an SRP render pass");
            if (camera == null || camera.enabled || camera.stereoEnabled || camera.allowMSAA ||
                camera.rect != new Rect(0, 0, 1, 1) || Active.ContainsKey(camera) ||
                !FsrRenderer.ValidSource(destination) || destination == _input || destination == _bloomInput ||
                _renderer.OwnsTexture(destination))
                return Fail("FSR manual host requires an idle disabled non-XR/non-MSAA full-viewport camera and distinct live linear output");
            if (SupersamplePresenter.TryGetPresentationTarget(camera, out _))
                return Fail("FSR manual host cannot replace an active supersampling presentation target");
            var output = new Vector2Int(destination.width, destination.height);
            if (!settings.TryGetRenderSize(output, out var input)) return Fail("Invalid FSR camera settings or output size");
            var pipeline = camera.GetComponent<OriginalStyleRenderPipeline>();
            _integrated = pipeline != null && pipeline.isActiveAndEnabled;
            if (_integrated && (settings.encoding != FsrInputEncoding.LinearHdr || temporalSource != null || classification != null))
                return Fail("Before-diffusion pipeline integration requires LinearHdr encoding and the pipeline's own temporal bindings");
            if (temporalSource != null && (!temporalSource.isActiveAndEnabled || temporalSource.GetComponent<Camera>() != camera))
                return Fail("FSR temporal input must belong to the same active scene camera");
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.D24_UNorm_S8_UInt, FormatUsage.Render))
                return Fail("FSR manual camera requires supported D24S8 depth/stencil for its explicit memory budget");
            long estimate = FsrSettings.EstimateTargetBytes(input, output) + (20L + (_integrated ? 16L : 0)) * input.x * input.y;
            if (estimate > settings.memoryBudgetMiB * 1048576L) return Fail("FSR camera plus reconstruction target memory budget exceeded");
            // Snapshot caller settings: camera callbacks cannot change this request mid-frame.
            _settings = new FsrSettings
            {
                enabled = true, quality = settings.quality, backend = settings.backend, encoding = settings.encoding,
                allowRasterFallback = settings.allowRasterFallback, sharpen = settings.sharpen,
                accurateRcasNormalization = settings.accurateRcasNormalization,
                stabilizeLumaGradients = settings.stabilizeLumaGradients,
                sharpnessStops = settings.sharpnessStops, memoryBudgetMiB = settings.memoryBudgetMiB
            };
            var previousTarget = camera.targetTexture; float aspect = camera.aspect;
            var active = RenderTexture.active; bool srgbWrite = GL.sRGBWrite;
            _rendering = true; _upscaled = _presented = false; _destination = destination;
            try
            {
                EnsureTarget(ref _input, input, 24, "Toolkit FSR actual low-resolution camera");
                if (!_integrated) ReleaseTarget(ref _bloomInput);
                RenderSize = input; camera.targetTexture = _input; camera.aspect = output.x / (float)output.y;
                // Ownership covers standalone cameras too: a different host in
                // a camera callback must not recursively render the same camera.
                Active.Add(camera, this);
                camera.Render();
                if (_integrated)
                {
                    if (!_upscaled || !_presented || !_upscale.IsCurrent)
                        return Fail(UnavailableReason ?? "FSR pipeline did not complete before-diffusion reconstruction and presentation");
                }
                else
                {
                    RenderTexture source = _input;
                    if (temporalSource != null && temporalSource.temporalAntialiasing != null && temporalSource.temporalAntialiasing.enabled)
                    {
                        if (!temporalSource.TryResolveTemporalColor(camera, source, classification, out source))
                            return Fail("FSR temporal input unavailable: " + temporalSource.TemporalColorUnavailableReason);
                    }
                    if (!_renderer.TryRender(source, new RectInt(0, 0, input.x, input.y), output, _settings, out _upscale))
                        return Fail(_renderer.UnavailableReason);
                    GL.sRGBWrite = false; Graphics.Blit(_upscale.color, destination);
                }
                _hasFrame = true; UnavailableReason = null; frame = new Frame(this); return true;
            }
            catch (Exception error) { return Fail("FSR camera render failed: " + error.GetType().Name + ": " + error.Message); }
            finally
            {
                Active.Remove(camera);
                if (camera != null) { camera.targetTexture = previousTarget; camera.aspect = aspect; }
                GL.sRGBWrite = srgbWrite;
                RenderTexture.active = active != null && active.IsCreated() ? active : null;
                _rendering = false;
            }
        }

        internal static bool TryGetActive(Camera camera, out FsrCameraRenderer request)
        {
            request = null;
            if (camera == null || !Active.TryGetValue(camera, out var current) || !current._integrated) return false;
            request = current; return true;
        }
        internal void RejectUpstream(string reason) { UnavailableReason = "FSR upstream input unavailable: " + reason; }
        internal bool TryBeforeDiffusion(RenderTexture source, RenderTexture bloom, float bloomWeight, out RenderTexture upscaled)
        {
            upscaled = source;
            if (!_rendering || !_integrated || _upscaled || source == null || source.width != RenderSize.x || source.height != RenderSize.y)
            { UnavailableReason = "FSR before-diffusion source/request mismatch"; return false; }
            var shader = Resources.Load<Shader>("FsrBloomComposite");
            if (shader == null || !shader.isSupported) { UnavailableReason = "FSR bloom composition shader unavailable"; return false; }
            EnsureTarget(ref _bloomInput, RenderSize, 0, "Toolkit FSR bloom-composed low-resolution HDR");
            if (_bloomMaterial == null) _bloomMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _bloomMaterial.SetTexture("_BloomTex", bloom);
            _bloomMaterial.SetFloat("_BloomWeight", bloomWeight);
            GL.sRGBWrite = false; Graphics.Blit(source, _bloomInput, _bloomMaterial, 0);
            var output = new Vector2Int(_destination.width, _destination.height);
            if (!_renderer.TryRender(_bloomInput, new RectInt(0, 0, RenderSize.x, RenderSize.y), output, _settings, out _upscale))
            { UnavailableReason = _renderer.UnavailableReason; return false; }
            upscaled = _upscale.color; _upscaled = true; return true;
        }
        internal bool TryGetPresentationTarget(out RenderTexture target)
        {
            target = _upscaled && _upscale.IsCurrent ? _destination : null;
            return target != null && target.IsCreated();
        }
        internal void MarkPresented() { _presented = _upscaled && _upscale.IsCurrent; }

        private static void EnsureTarget(ref RenderTexture target, Vector2Int size, int depth, string name)
        {
            if (target != null && target.IsCreated() && target.width == size.x && target.height == size.y) return;
            ReleaseTarget(ref target);
            target = new RenderTexture(size.x, size.y, depth, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            { name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            if (depth > 0) target.depthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt;
            target.Create();
            if (!target.IsCreated() || target.graphicsFormat != GraphicsFormat.R32G32B32A32_SFloat ||
                (depth > 0 && target.depthStencilFormat != GraphicsFormat.D24_UNorm_S8_UInt))
                throw new InvalidOperationException("FSR camera HDR/D24S8 target allocation failed");
        }
        private static void ReleaseTarget(ref RenderTexture target)
        {
            if (target == null) return;
            if (RenderTexture.active == target) RenderTexture.active = null;
            target.Release(); UnityEngine.Object.Destroy(target); target = null;
        }
        private bool Fail(string reason) { Release(); UnavailableReason = reason; return false; }
        private void Release()
        {
            _renderer.Dispose(); ReleaseTarget(ref _input); ReleaseTarget(ref _bloomInput);
            if (_bloomMaterial != null) UnityEngine.Object.Destroy(_bloomMaterial);
            _bloomMaterial = null; _hasFrame = false; _settings = null; _destination = null; RenderSize = default;
        }
        public void Dispose()
        {
            if (_rendering) throw new InvalidOperationException("Do not dispose an FSR camera renderer during Camera.Render callbacks");
            _generation++; Release(); UnavailableReason = null;
        }
    }
}
