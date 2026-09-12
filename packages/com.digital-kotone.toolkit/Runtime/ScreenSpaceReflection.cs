using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Opt-in scene SSR. Captures actor-free scene color and geometry together,
    /// traces current geometry, and reprojects hits into the previous capture.
    /// The host calls TryComposite at its HDR stage; no implicit image effect.
    /// </summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class ScreenSpaceReflection : MonoBehaviour
    {
        public bool reflectionsEnabled;
        public LayerMask sceneLayers;
        public SceneDepthData.Surface[] surfaces = Array.Empty<SceneDepthData.Surface>();
        [Range(0, 1)] public float smoothnessThreshold = .5f;
        [Range(0, 2)] public float intensity = .35f;
        [Min(.01f)] public float maximumDistance = 30;
        [Min(.001f)] public float thickness = .12f;
        [Min(.001f)] public float normalBias = .025f;
        [Range(8, 512)] public int maximumSteps = 128;
        [Range(0, .25f)] public float edgeFade = .04f;
        [Min(.001f)] public float historyDepthTolerance = .15f;
        [Min(.01f)] public float cameraCutDistance = 2;
        [Range(1, 180)] public float cameraCutAngle = 35;
        public bool useHierarchy = true;
        public SceneShaderBackend backend = SceneShaderBackend.Raster;
        public bool allowComputeFallback = true;
        public SsrRoughnessSettings roughness = new SsrRoughnessSettings();
        public SceneShaderBackend ActiveBackend { get; private set; }
        public SceneShaderBackend ActiveHierarchyBackend => _geometry != null ? _geometry.ActiveHierarchyBackend : SceneShaderBackend.Raster;
        public string ComputeFallbackReason { get; private set; }
        public int ComputeDispatchCount { get; private set; }

        public bool HistoryAvailable { get; private set; }
        public string UnavailableReason { get; private set; }
        private Camera _camera, _sceneCamera;
        private SceneDepthData _geometry;
        private SceneDepthData.Frame _frame;
        private Shader _shader;
        private Material _material;
        private ComputeShader _computeAsset, _compute;
        private int _computeKernel, _filterKernel;
        private bool _filterPrepared, _filterApplied;
        private Vector4 _filterOptions;
        private readonly List<Material> _visibilityMaterials = new List<Material>();
        private CommandBuffer _visibilityCommands;
        private RenderTexture _sceneColor, _historyColor, _historyDepth, _visibility, _reflection, _composite;
        private RenderTexture _filterHorizontal, _filtered;
        private bool FilterRequested => roughness != null && roughness.IsActive;
        private RenderTexture OutputReflection => _filterApplied ? _filtered : _reflection;
        private RenderTexture _sourceTarget;
        private Matrix4x4 _historyView, _historyProjection;
        private Vector3 _historyPosition;
        private Quaternion _historyRotation;
        private bool _historyOrthographic;
        private int _historyFrame = -1, _historyLayers, _preparedFrame = -1, _renderedFrame = -1;
        private uint _renderSequence, _consumedSequence;
        private int _surfaceSignature, _historySurfaceSignature;

        private void OnEnable()
        {
            _camera = GetComponent<Camera>();
            _shader = Resources.Load<Shader>("ScreenSpaceReflection");
            _computeAsset = Resources.Load<ComputeShader>("ScreenSpaceReflection");
            _visibilityCommands = new CommandBuffer { name = "Toolkit SSR visible receivers" };
            _camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _visibilityCommands);
        }

        private void OnPreCull()
        {
            _preparedFrame = _renderedFrame = -1;
            _filterApplied = false;
            _filterPrepared = FilterRequested;
            ComputeDispatchCount = 0; ComputeFallbackReason = null;
            _visibilityCommands.Clear();
            UnavailableReason = Validate();
            if (UnavailableReason != null) { ReleaseResources(); return; }
            if (!SceneComputeSupport.Select(backend, allowComputeFallback, _computeAsset, "TraceReflections", RenderTextureFormat.ARGBHalf,
                out var selected, out _computeKernel, out var fallback, out var error))
            { UnavailableReason = error; ReleaseResources(); return; }
            ActiveBackend = selected; ComputeFallbackReason = fallback;
            if (_filterPrepared && ActiveBackend == SceneShaderBackend.Compute)
            {
                if (!SceneComputeSupport.Select(backend, allowComputeFallback, _computeAsset, "FilterRoughness", RenderTextureFormat.ARGBHalf,
                    out selected, out _filterKernel, out fallback, out error))
                { UnavailableReason = error; ReleaseResources(); return; }
                ActiveBackend = selected; ComputeFallbackReason = fallback;
            }
            int width = _camera.targetTexture != null ? _camera.targetTexture.width : _camera.pixelWidth;
            int height = _camera.targetTexture != null ? _camera.targetTexture.height : _camera.pixelHeight;
            if (width < 1 || height < 1) { UnavailableReason = "Empty target"; ReleaseResources(); return; }
            if (_filterPrepared) _filterOptions = new Vector4(roughness.RadiusAtHeight(height),
                roughness.planeTolerance, roughness.normalThreshold, roughness.smoothnessTolerance);
            if (!EnsureResources(width, height))
            {
                if (ActiveBackend == SceneShaderBackend.Compute && allowComputeFallback)
                { ActiveBackend = SceneShaderBackend.Raster; ComputeFallbackReason = "Compute reflection allocation failed"; }
                else { UnavailableReason = "Target allocation failed"; ReleaseResources(); return; }
                if (!EnsureResources(width, height)) { UnavailableReason = "Fallback allocation failed"; ReleaseResources(); return; }
            }
            if (ActiveBackend == SceneShaderBackend.Compute && _compute == null) _compute = Instantiate(_computeAsset);
            _sceneCamera.CopyFrom(_camera);
            _sceneCamera.enabled = false;
            _sceneCamera.allowMSAA = false;
            _sceneCamera.depthTextureMode = DepthTextureMode.None;
            _sceneCamera.cullingMask = _camera.cullingMask & sceneLayers.value;
            _sceneCamera.targetTexture = _sceneColor;
            // Never inherit an uncleared color target into scene history.
            if (_sceneCamera.clearFlags == CameraClearFlags.Nothing || _sceneCamera.clearFlags == CameraClearFlags.Depth)
                _sceneCamera.clearFlags = CameraClearFlags.SolidColor;
            _sceneCamera.transform.SetPositionAndRotation(_camera.transform.position, _camera.transform.rotation);
            // Preserve explicitly authored camera matrices, not just the
            // Transform/FOV approximation (capture tools and off-axis hosts).
            _sceneCamera.worldToCameraMatrix = _camera.worldToCameraMatrix;
            _sceneCamera.projectionMatrix = _camera.projectionMatrix;
            Skybox sourceSky = _camera.GetComponent<Skybox>();
            Skybox captureSky = _sceneCamera.GetComponent<Skybox>();
            if (sourceSky != null)
            {
                if (captureSky == null) captureSky = _sceneCamera.gameObject.AddComponent<Skybox>();
                captureSky.material = sourceSky.material; captureSky.enabled = sourceSky.enabled;
            }
            else if (captureSky != null) captureSky.enabled = false;
            _geometry.surfaces = surfaces;
            _geometry.smoothnessThreshold = smoothnessThreshold;
            _geometry.buildDepthHierarchy = useHierarchy;
            _geometry.hierarchyBackend = ActiveBackend;
            _geometry.allowComputeFallback = allowComputeFallback;
            _sceneCamera.Render();
            if (!_geometry.TryGetFrame(_sceneCamera, width, height, out _frame))
            {
                UnavailableReason = _geometry.UnavailableReason ?? "Scene geometry capture unavailable";
                ResetHistory(); return;
            }
            if (_frame.DepthLevelCount > 15) { UnavailableReason = "Depth hierarchy exceeds 15 levels"; ResetHistory(); return; }
            if (_geometry.ComputeFallbackReason != null) ComputeFallbackReason = _geometry.ComputeFallbackReason;
            _surfaceSignature = 17;
            _visibilityCommands.SetRenderTarget(new RenderTargetIdentifier(_visibility), BuiltinRenderTextureType.CurrentActive);
            _visibilityCommands.ClearRenderTarget(false, true, Color.clear);
            int submitted = 0;
            foreach (SceneDepthData.Surface surface in surfaces)
            {
                if (!SceneDepthData.ValidSurface(surface)) continue;
                Renderer renderer = surface.renderer;
                int layer = 1 << renderer.gameObject.layer;
                if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy ||
                    (_sceneCamera.cullingMask & layer) == 0) continue;
                unchecked { _surfaceSignature = _surfaceSignature * 31 + renderer.GetInstanceID() * 17 + surface.materialIndex; }
                if (!surface.receiveReflections) continue;
                while (_visibilityMaterials.Count <= submitted)
                    _visibilityMaterials.Add(new Material(_shader) { hideFlags = HideFlags.HideAndDontSave });
                Material material = _visibilityMaterials[submitted++];
                material.SetFloat("_Cull", (int)surface.cull);
                material.SetVector("_SsrVertexScale", surface.vertexScale);
                material.SetTexture("_SsrAlphaMask", surface.alphaMask != null ? surface.alphaMask : Texture2D.whiteTexture);
                material.SetVector("_SsrAlphaST", surface.alphaMaskST);
                material.SetFloat("_SsrAlphaCutoff", surface.alphaCutoff);
                material.SetTexture("_SsrSmoothnessMap", surface.smoothnessMap != null ? surface.smoothnessMap : Texture2D.whiteTexture);
                material.SetVector("_SsrSmoothnessST", surface.smoothnessMapST);
                material.SetFloat("_SsrSmoothness", surface.smoothness);
                material.SetFloat("_SsrSmoothnessThreshold", smoothnessThreshold);
                material.SetFloat("_SsrFilterMetadata", _filterPrepared ? 1 : 0);
                material.SetFloat("_SsrReceiverId", submitted);
                _visibilityCommands.DrawRenderer(renderer, material, surface.materialIndex, 0);
            }
            _sourceTarget = _camera.targetTexture; _preparedFrame = Time.frameCount;
        }

        private void OnPostRender()
        {
            if (_preparedFrame != Time.frameCount) return;
            _renderedFrame = Time.frameCount;
            _renderSequence++;
        }

        /// <summary>Borrowed HDR result; call once per render from the owning camera's image effect.</summary>
        public bool TryComposite(Camera camera, RenderTexture source, out RenderTexture result)
            => TryRender(camera, source, null, true, out result);

        /// <summary>Trace without additive composition. Planar coverage skips covered pixels; either path consumes this render.</summary>
        public bool TryTrace(Camera camera, RenderTexture source, Texture planarCoverage, out RenderTexture reflection)
        {
            reflection = null;
            if (!TryRender(camera, source, planarCoverage, false, out _)) return false;
            reflection = OutputReflection; return true;
        }

        private bool TryRender(Camera camera, RenderTexture source, Texture planarCoverage, bool composite, out RenderTexture result)
        {
            result = source;
            if (planarCoverage != null && (source == null || planarCoverage.dimension != TextureDimension.Tex2D ||
                planarCoverage.width != source.width || planarCoverage.height != source.height)) return false;
            if (!isActiveAndEnabled || !reflectionsEnabled || camera != _camera || source == null ||
                _preparedFrame != Time.frameCount || _renderedFrame != Time.frameCount ||
                _consumedSequence == _renderSequence || _sceneColor == null ||
                _sourceTarget != _camera.targetTexture || _reflection == null || !_reflection.IsCreated() ||
                (_filterPrepared && (_filterHorizontal == null || !_filterHorizontal.IsCreated() || _filtered == null || !_filtered.IsCreated())) ||
                source.width != _sceneColor.width || source.height != _sceneColor.height) return false;
            _consumedSequence = _renderSequence;
            Matrix4x4 inverseView = _frame.worldToCamera.inverse;
            Vector3 viewPosition = inverseView.MultiplyPoint(Vector3.zero);
            Quaternion viewRotation = Quaternion.LookRotation(inverseView.MultiplyVector(Vector3.back), inverseView.MultiplyVector(Vector3.up));
            bool continuous = HistoryAvailable && (_historyFrame == Time.frameCount || _historyFrame == Time.frameCount - 1) &&
                _historyLayers == _sceneCamera.cullingMask && _historySurfaceSignature == _surfaceSignature &&
                _historyOrthographic == camera.orthographic &&
                Vector3.Distance(_historyPosition, viewPosition) <= cameraCutDistance &&
                Quaternion.Angle(_historyRotation, viewRotation) <= cameraCutAngle &&
                MatrixDistance(_historyProjection, _frame.gpuProjection) < .1f;
            _material.SetTexture("_SsrNormalMask", _frame.normalMask);
            _material.SetTexture("_SsrVisibility", _visibility);
            _material.SetTexture("_SsrPlanarCoverage", planarCoverage != null ? planarCoverage : Texture2D.blackTexture);
            _material.SetFloat("_SsrPlanarAvailable", planarCoverage != null ? 1 : 0);
            for (int level = 0; level < 15; level++)
                _material.SetTexture("_SsrDepth" + level, _frame.GetDepthLevel(Mathf.Min(level, _frame.DepthLevelCount - 1)));
            _material.SetTexture("_SsrHistoryColor", _historyColor);
            _material.SetTexture("_SsrHistoryDepth", _historyDepth);
            _material.SetMatrix("_SsrInverseProjection", _frame.gpuProjection.inverse);
            _material.SetMatrix("_SsrProjection", _frame.gpuProjection);
            _material.SetMatrix("_SsrView", _frame.worldToCamera);
            _material.SetMatrix("_SsrInverseView", _frame.worldToCamera.inverse);
            _material.SetMatrix("_SsrHistoryView", _historyView);
            _material.SetMatrix("_SsrHistoryViewProjection", _historyProjection * _historyView);
            _material.SetVector("_SsrSize", new Vector4(source.width, source.height, 1f / source.width, 1f / source.height));
            _material.SetVector("_SsrTrace", new Vector4(maximumDistance, thickness, normalBias, maximumSteps));
            _material.SetVector("_SsrFrame", new Vector4(_frame.farClip, camera.nearClipPlane, camera.orthographic ? 1 : 0, _frame.DepthLevelCount - 1));
            _material.SetVector("_SsrHistory", new Vector4(continuous ? 1 : 0, historyDepthTolerance, edgeFade, intensity));
            if (ActiveBackend == SceneShaderBackend.Compute) DispatchTrace();
            else Graphics.Blit(source, _reflection, _material, 1);
            if (_filterPrepared)
            {
                _material.SetVector("_SsrFilterOptions", _filterOptions);
                FilterPass(_reflection, _filterHorizontal, new Vector4(1, 0, 0, 0));
                FilterPass(_filterHorizontal, _filtered, new Vector4(0, 1, 0, 0));
                _filterApplied = true;
            }
            _material.SetTexture("_SsrReflection", OutputReflection);
            if (continuous && composite) Graphics.Blit(source, _composite, _material, 2);
            // Copy only the actor-free auxiliary capture, never the composited
            // main frame. This also prevents recursive reflection feedback.
            Graphics.Blit(_sceneColor, _historyColor);
            Graphics.Blit(_frame.linearDepth, _historyDepth);
            _historyView = _frame.worldToCamera; _historyProjection = _frame.gpuProjection;
            _historyPosition = viewPosition; _historyRotation = viewRotation;
            _historyOrthographic = camera.orthographic;
            _historyLayers = _sceneCamera.cullingMask; _historySurfaceSignature = _surfaceSignature;
            _historyFrame = Time.frameCount; HistoryAvailable = true;
            result = continuous && composite ? _composite : source;
            return true;
        }

        public bool TryGetReflection(Camera camera, out Texture texture)
            => TryGetResult(camera, false, out texture);

        /// <summary>Borrowed unfiltered trace for diagnostics; it has the same lifetime as TryGetReflection.</summary>
        public bool TryGetRawReflection(Camera camera, out Texture texture)
            => TryGetResult(camera, true, out texture);

        private bool TryGetResult(Camera camera, bool raw, out Texture texture)
        {
            texture = null;
            RenderTexture output = raw ? _reflection : OutputReflection;
            if (!isActiveAndEnabled || !reflectionsEnabled || camera != _camera || _renderedFrame != Time.frameCount ||
                _sourceTarget != _camera.targetTexture || _consumedSequence != _renderSequence ||
                output == null || !output.IsCreated()) return false;
            texture = output; return true;
        }

        public void ResetHistory() { HistoryAvailable = false; _historyFrame = -1; _renderedFrame = -1; }

        // Bind the same fully prepared input set as the reference raster path.
        // Instance-local compute parameters avoid cross-camera asset mutations.
        private void DispatchTrace()
        {
            foreach (string name in TraceTextures) _compute.SetTexture(_computeKernel, name, _material.GetTexture(name));
            for (int level = 0; level < 15; level++)
                _compute.SetTexture(_computeKernel, "_SsrDepth" + level, _frame.GetDepthLevel(Mathf.Min(level, _frame.DepthLevelCount - 1)));
            foreach (string name in TraceMatrices) _compute.SetMatrix(name, _material.GetMatrix(name));
            foreach (string name in TraceVectors) _compute.SetVector(name, _material.GetVector(name));
            _compute.SetFloat("_SsrPlanarAvailable", _material.GetFloat("_SsrPlanarAvailable"));
            _compute.SetTexture(_computeKernel, "_SsrOutput", _reflection);
            _compute.Dispatch(_computeKernel, (_reflection.width + 7) / 8, (_reflection.height + 7) / 8, 1);
            ComputeDispatchCount++;
        }
        private static readonly string[] TraceTextures = { "_SsrNormalMask", "_SsrVisibility", "_SsrPlanarCoverage", "_SsrHistoryColor", "_SsrHistoryDepth" };
        private static readonly string[] TraceMatrices = { "_SsrInverseProjection", "_SsrProjection", "_SsrView", "_SsrInverseView", "_SsrHistoryView", "_SsrHistoryViewProjection" };
        private static readonly string[] TraceVectors = { "_SsrSize", "_SsrTrace", "_SsrFrame", "_SsrHistory" };

        private void FilterPass(RenderTexture source, RenderTexture destination, Vector4 axis)
        {
            _material.SetTexture("_SsrFilterInput", source);
            _material.SetVector("_SsrFilterAxis", axis);
            if (ActiveBackend == SceneShaderBackend.Raster) { Graphics.Blit(source, destination, _material, 3); return; }
            foreach (string name in FilterTextures) _compute.SetTexture(_filterKernel, name, _material.GetTexture(name));
            _compute.SetMatrix("_SsrInverseProjection", _frame.gpuProjection.inverse);
            _compute.SetMatrix("_SsrView", _frame.worldToCamera);
            _compute.SetVector("_SsrSize", _material.GetVector("_SsrSize"));
            _compute.SetVector("_SsrFilterOptions", _material.GetVector("_SsrFilterOptions"));
            _compute.SetVector("_SsrFilterAxis", axis);
            _compute.SetFloat("_SsrPlanarAvailable", _material.GetFloat("_SsrPlanarAvailable"));
            _compute.SetTexture(_filterKernel, "_SsrOutput", destination);
            _compute.Dispatch(_filterKernel, (destination.width + 7) / 8, (destination.height + 7) / 8, 1);
            ComputeDispatchCount++;
        }
        private static readonly string[] FilterTextures = { "_SsrFilterInput", "_SsrVisibility", "_SsrNormalMask", "_SsrDepth0", "_SsrPlanarCoverage" };

        private string Validate()
        {
            if (!reflectionsEnabled || intensity == 0 || sceneLayers.value == 0 || surfaces == null || surfaces.Length == 0)
                return "Disabled or no scene layers/surfaces";
            if (_shader == null || !_shader.isSupported || SystemInfo.graphicsShaderLevel < 45 ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat) || SystemInfo.supportedRenderTargetCount < 2)
                return "Unsupported shader/target capability";
            if (GraphicsSettings.currentRenderPipeline != null || _camera.actualRenderingPath != RenderingPath.Forward ||
                _camera.stereoEnabled || _camera.rect != new Rect(0, 0, 1, 1) || _camera.allowDynamicResolution ||
                (_camera.targetTexture != null && (_camera.targetTexture.dimension != TextureDimension.Tex2D ||
                    _camera.targetTexture.antiAliasing > 1 || _camera.targetTexture.useDynamicScale)) ||
                (_camera.targetTexture == null && _camera.allowMSAA && QualitySettings.antiAliasing > 1))
                return "Requires fixed-size Built-in Forward, non-XR/MSAA full viewport";
            if (!Range(smoothnessThreshold, 0, 1) || !Range(intensity, 0, 2) || !Range(maximumDistance, .01f, 10000) ||
                !Range(thickness, .001f, 100) || !Range(normalBias, .001f, 10) || maximumSteps < 8 || maximumSteps > 512 ||
                !Range(edgeFade, 0, .25f) || !Range(historyDepthTolerance, .001f, 100) ||
                !Range(cameraCutDistance, .01f, 10000) || !Range(cameraCutAngle, 1, 180)) return "Invalid SSR settings";
            if (roughness != null && !roughness.IsValid) return "Invalid SSR roughness settings";
            if (FilterRequested && surfaces.Length > 1024) return "Roughness filtering supports at most 1024 surface entries";
            return null;
        }

        private static bool Range(float value, float low, float high) => !float.IsNaN(value) && value >= low && value <= high;
        private static float MatrixDistance(Matrix4x4 a, Matrix4x4 b)
        {
            float error = 0;
            for (int i = 0; i < 16; i++) error = Mathf.Max(error, Mathf.Abs(a[i] - b[i]));
            return error;
        }

        private bool EnsureResources(int width, int height)
        {
            if (_sceneColor != null && (_sceneColor.width != width || _sceneColor.height != height ||
                _reflection.enableRandomWrite != (ActiveBackend == SceneShaderBackend.Compute) ||
                !_sceneColor.IsCreated() || !_historyColor.IsCreated() || !_historyDepth.IsCreated() ||
                !_reflection.IsCreated() || !_composite.IsCreated())) ReleaseResources();
            if (_sceneCamera == null)
            {
                var host = new GameObject("Toolkit SSR scene capture") { hideFlags = HideFlags.HideAndDontSave };
                host.transform.SetParent(transform, false);
                _sceneCamera = host.AddComponent<Camera>(); _sceneCamera.enabled = false;
                _geometry = host.AddComponent<SceneDepthData>();
            }
            if (_material == null) _material = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
            if (_sceneColor == null)
            {
                _sceneColor = Target(width, height, 24, RenderTextureFormat.ARGBHalf, "SSR actor-free current color");
                _historyColor = Target(width, height, 0, RenderTextureFormat.ARGBHalf, "SSR actor-free history color");
                _historyDepth = Target(width, height, 0, RenderTextureFormat.RFloat, "SSR history depth");
                _reflection = Target(width, height, 0, RenderTextureFormat.ARGBHalf, "SSR radiance and confidence", ActiveBackend == SceneShaderBackend.Compute);
                _composite = Target(width, height, 0, RenderTextureFormat.ARGBHalf, "SSR HDR composite");
            }
            // Receiver metadata/filter toggles must not invalidate actor-free history.
            RenderTextureFormat visibilityFormat = _filterPrepared ? RenderTextureFormat.ARGBHalf :
                (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8) ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32);
            if (_visibility != null && (_visibility.format != visibilityFormat || !_visibility.IsCreated())) Release(ref _visibility);
            if (_visibility == null) _visibility = Target(width, height, 0, visibilityFormat, "SSR visible receivers");
            if (!_filterPrepared) { Release(ref _filterHorizontal); Release(ref _filtered); }
            else
            {
                if (_filterHorizontal != null && !_filterHorizontal.IsCreated()) Release(ref _filterHorizontal);
                if (_filtered != null && !_filtered.IsCreated()) Release(ref _filtered);
                if (_filterHorizontal == null) _filterHorizontal = Target(width, height, 0, RenderTextureFormat.ARGBHalf,
                    "SSR horizontal roughness", ActiveBackend == SceneShaderBackend.Compute);
                if (_filtered == null) _filtered = Target(width, height, 0, RenderTextureFormat.ARGBHalf,
                    "SSR filtered radiance and confidence", ActiveBackend == SceneShaderBackend.Compute);
            }
            return _sceneColor.IsCreated() && _historyColor.IsCreated() && _historyDepth.IsCreated() &&
                _visibility.IsCreated() && _reflection.IsCreated() && _composite.IsCreated() &&
                (!_filterPrepared || (_filterHorizontal.IsCreated() && _filtered.IsCreated()));
        }

        private static RenderTexture Target(int width, int height, int depth, RenderTextureFormat format, string name, bool randomWrite = false)
        {
            var target = new RenderTexture(width, height, depth, format, RenderTextureReadWrite.Linear) {
                name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                useMipMap = false, autoGenerateMips = false, enableRandomWrite = randomWrite, hideFlags = HideFlags.HideAndDontSave
            };
            target.Create(); return target;
        }

        private void ReleaseResources()
        {
            ResetHistory(); _preparedFrame = _renderedFrame = -1;
            if (_sceneCamera != null)
            {
                _sceneCamera.targetTexture = null;
                _geometry.enabled = false;
                Destroy(_sceneCamera.gameObject); _sceneCamera = null; _geometry = null;
            }
            Release(ref _sceneColor); Release(ref _historyColor); Release(ref _historyDepth);
            Release(ref _visibility); Release(ref _reflection); Release(ref _composite);
            Release(ref _filterHorizontal); Release(ref _filtered); _filterApplied = false;
            if (_material != null) { Destroy(_material); _material = null; }
            if (_compute != null) { Destroy(_compute); _compute = null; }
            ComputeDispatchCount = 0;
            foreach (Material material in _visibilityMaterials) if (material != null) Destroy(material);
            _visibilityMaterials.Clear();
        }
        private void Release(ref RenderTexture target)
        {
            if (target != null) { target.Release(); Destroy(target); target = null; }
        }
        private void OnDisable()
        {
            if (_visibilityCommands != null)
            {
                if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _visibilityCommands);
                _visibilityCommands.Release(); _visibilityCommands = null;
            }
            ReleaseResources();
        }
    }
}
