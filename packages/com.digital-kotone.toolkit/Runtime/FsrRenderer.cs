using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>
    /// FSR1 adapter to the AMD implementation supplied by UPM Core. Input texture
    /// metadata must be linear; encoding describes its actual numeric RGB values.
    /// Output is borrowed until the next attempt or Dispose. No runtime readback.
    /// </summary>
    public sealed class FsrRenderer : IDisposable
    {
        public readonly struct Frame
        {
            private readonly FsrRenderer _owner;
            private readonly uint _generation;
            public readonly RenderTexture prepared, expanded, color;
            public readonly FsrBackend backend;
            internal Frame(FsrRenderer owner)
            {
                _owner = owner; _generation = owner._generation;
                prepared = owner._targets[0]; expanded = owner._targets[1]; color = owner._targets[2];
                backend = owner.ActiveBackend;
            }
            public bool IsCurrent => _owner != null && _owner._hasFrame &&
                _owner._generation == _generation && _owner.Created;
        }

        private static readonly int InputId = Shader.PropertyToID("_FsrInput");
        private static readonly int OutputId = Shader.PropertyToID("_FsrOutput");
        private static readonly int InputSizeId = Shader.PropertyToID("_FsrInputSize");
        private static readonly int OutputSizeId = Shader.PropertyToID("_FsrOutputSize");
        private static readonly int ViewportId = Shader.PropertyToID("_FsrViewport");
        private static readonly int OptionsId = Shader.PropertyToID("_FsrOptions");
        private readonly RenderTexture[] _targets = new RenderTexture[3];
        private readonly int[] _kernels = new int[3];
        private Material _material;
        private ComputeShader _compute;
        private uint _generation;
        private bool _hasFrame;
        public FsrBackend ActiveBackend { get; private set; }
        public string UnavailableReason { get; private set; }
        public string FallbackReason { get; private set; }
        public int TargetCount { get; private set; }
        public int DrawCalls { get; private set; }
        public int Dispatches { get; private set; }
        public long EstimatedTargetBytes { get; private set; }
        private bool Created
        {
            get
            {
                if (TargetCount != 3) return false;
                foreach (var t in _targets) if (t == null || !t.IsCreated()) return false;
                return true;
            }
        }

        public bool TryGetFrame(out Frame frame)
        {
            frame = default;
            if (!_hasFrame || !Created) return false;
            frame = new Frame(this); return true;
        }

        public bool TryRender(Texture source, RectInt viewport, Vector2Int output,
            FsrSettings settings, out Frame frame)
        {
            frame = default; _generation++; _hasFrame = false;
            DrawCalls = Dispatches = 0; UnavailableReason = FallbackReason = null;
            if (settings == null || !settings.enabled) { Release(); return false; }
            if (!settings.IsValid) return Fail("Invalid FSR settings");
            var input = new Vector2Int(viewport.width, viewport.height);
            if (!ValidSource(source) || Owns(source) || !FsrSettings.ValidSize(input) ||
                !FsrSettings.ValidSize(output) || viewport.x < 0 || viewport.y < 0 ||
                (long)viewport.x + viewport.width > source.width || (long)viewport.y + viewport.height > source.height ||
                output.x < input.x || output.y < input.y || output.x > 2 * input.x || output.y > 2 * input.y)
                return Fail("FSR requires a valid linear-metadata 2D source, contained viewport and 1x to 2x output per axis");
            long bytes = FsrSettings.EstimateTargetBytes(input, output);
            if (bytes > settings.memoryBudgetMiB * 1048576L) return Fail("FSR target memory budget exceeded");
            if (!Supports(FormatUsage.Render) || !Supports(FormatUsage.Sample))
                return Fail("FSR requires RGBAFloat render and sample support");
            var active = RenderTexture.active;
            bool srgbWrite = GL.sRGBWrite;
            try
            {
                var shader = Resources.Load<Shader>("Fsr");
                var compute = Resources.Load<ComputeShader>("Fsr");
                bool rasterSupported = SystemInfo.graphicsShaderLevel >= 45 && shader != null && shader.isSupported;
                bool computeSupported = SystemInfo.supportsComputeShaders && Supports(FormatUsage.LoadStore) &&
                    compute != null && compute.HasKernel("Prepare") && compute.HasKernel("Expand") && compute.HasKernel("Finish");
                FsrBackend chosen = settings.backend == FsrBackend.Raster ? FsrBackend.Raster : FsrBackend.Compute;
                if (chosen == FsrBackend.Compute && !computeSupported)
                {
                    if (!settings.allowRasterFallback) return Fail("FSR compute unavailable; raster fallback is disabled");
                    chosen = FsrBackend.Raster; FallbackReason = "FSR compute unavailable; using explicit raster fallback";
                }
                if (chosen == FsrBackend.Raster && !rasterSupported) return Fail("FSR SM4.5 raster shader unavailable");
                if (!Created || ActiveBackend != chosen || _targets[0].width != input.x || _targets[0].height != input.y ||
                    _targets[1].width != output.x || _targets[1].height != output.y)
                    Allocate(input, output, chosen);
                ActiveBackend = chosen; EstimatedTargetBytes = bytes;
                var inputSize = new Vector4(input.x, input.y, 1f / input.x, 1f / input.y);
                var outputSize = new Vector4(output.x, output.y, 1f / output.x, 1f / output.y);
                var rectangle = new Vector4(viewport.x, viewport.y, input.x, input.y);
                var options = new Vector4((int)settings.encoding, settings.sharpen ? 1 : 0, settings.sharpnessStops, settings.accurateRcasNormalization ? 1 : 0);
                GL.sRGBWrite = false;
                if (chosen == FsrBackend.Compute)
                {
                    if (_compute == null)
                    {
                        _compute = UnityEngine.Object.Instantiate(compute);
                        _compute.hideFlags = HideFlags.HideAndDontSave;
                        _kernels[0] = _compute.FindKernel("Prepare"); _kernels[1] = _compute.FindKernel("Expand");
                        _kernels[2] = _compute.FindKernel("Finish");
                    }
                    _compute.SetVector(InputSizeId, inputSize); _compute.SetVector(OutputSizeId, outputSize);
                    _compute.SetVector(ViewportId, rectangle); _compute.SetVector(OptionsId, options);
                    for (int pass = 0; pass < 3; pass++)
                    {
                        _compute.SetTexture(_kernels[pass], InputId, pass == 0 ? source : _targets[pass - 1]);
                        _compute.SetTexture(_kernels[pass], OutputId, _targets[pass]);
                        _compute.Dispatch(_kernels[pass], (_targets[pass].width + 7) / 8, (_targets[pass].height + 7) / 8, 1);
                        Dispatches++;
                    }
                }
                else
                {
                    if (_material == null) _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    _material.SetVector(InputSizeId, inputSize); _material.SetVector(OutputSizeId, outputSize);
                    _material.SetVector(ViewportId, rectangle); _material.SetVector(OptionsId, options);
                    for (int pass = 0; pass < 3; pass++)
                    {
                        Texture from = pass == 0 ? source : _targets[pass - 1];
                        _material.SetTexture(InputId, from);
                        Graphics.Blit(from, _targets[pass], _material, pass); DrawCalls++;
                    }
                }
                _hasFrame = true; frame = new Frame(this); return true;
            }
            catch (Exception error) { return Fail("FSR render failed: " + error.GetType().Name + ": " + error.Message); }
            finally
            {
                GL.sRGBWrite = srgbWrite;
                RenderTexture.active = active != null && active.IsCreated() ? active : null;
            }
        }

        private static bool Supports(FormatUsage usage) => SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, usage);
        internal static bool ValidSource(Texture source)
        {
            if (source == null || source.dimension != TextureDimension.Tex2D || source.width < 1 || source.height < 1 ||
                source.mipmapCount != 1 || GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat)) return false;
            var format = source.graphicsFormat;
            if (format != GraphicsFormat.R32G32B32A32_SFloat && format != GraphicsFormat.R16G16B16A16_SFloat &&
                format != GraphicsFormat.R8G8B8A8_UNorm && format != GraphicsFormat.B10G11R11_UFloatPack32) return false;
            if (source is RenderTexture rt)
                return rt.IsCreated() && rt.antiAliasing == 1 && rt.volumeDepth == 1 && !rt.useDynamicScale && !rt.useMipMap;
            return source is Texture2D;
        }

        private bool Owns(Texture source)
        {
            if (source == null) return false;
            foreach (var target in _targets) if (target == source) return true;
            return false;
        }
        internal bool OwnsTexture(Texture source) => Owns(source);
        private void Allocate(Vector2Int input, Vector2Int output, FsrBackend backend)
        {
            ReleaseTargets();
            var names = new[] { "prepared perceptual viewport", "EASU perceptual output", "RCAS decoded output" };
            for (int i = 0; i < 3; i++)
            {
                var size = i == 0 ? input : output;
                var target = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                {
                    name = "Toolkit FSR " + names[i], hideFlags = HideFlags.HideAndDontSave,
                    enableRandomWrite = backend == FsrBackend.Compute, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                };
                _targets[i] = target; target.Create();
                if (!target.IsCreated() || target.graphicsFormat != GraphicsFormat.R32G32B32A32_SFloat)
                    throw new InvalidOperationException("FSR RGBAFloat allocation failed");
            }
            TargetCount = 3;
        }
        private bool Fail(string reason) { Release(); UnavailableReason = reason; return false; }
        private void ReleaseTargets()
        {
            if (Owns(RenderTexture.active)) RenderTexture.active = null;
            foreach (var target in _targets) if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
            Array.Clear(_targets, 0, _targets.Length); TargetCount = 0; EstimatedTargetBytes = 0; _hasFrame = false;
        }
        private void Release()
        {
            ReleaseTargets();
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_compute != null) UnityEngine.Object.Destroy(_compute);
            _material = null; _compute = null; DrawCalls = Dispatches = 0; ActiveBackend = FsrBackend.Auto;
        }
        public void Dispose() { _generation++; Release(); UnavailableReason = FallbackReason = null; }
    }
}
