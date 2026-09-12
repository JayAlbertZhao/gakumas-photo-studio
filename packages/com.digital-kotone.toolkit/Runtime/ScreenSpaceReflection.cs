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

        public bool HistoryAvailable { get; private set; }
        public string UnavailableReason { get; private set; }
        private Camera _camera, _sceneCamera;
        private SceneDepthData _geometry;
        private SceneDepthData.Frame _frame;
        private Shader _shader;
        private Material _material;
        private readonly List<Material> _visibilityMaterials = new List<Material>();
        private CommandBuffer _visibilityCommands;
        private RenderTexture _sceneColor, _historyColor, _historyDepth, _visibility, _reflection, _composite;
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
            _visibilityCommands = new CommandBuffer { name = "Toolkit SSR visible receivers" };
            _camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _visibilityCommands);
        }

        private void OnPreCull()
        {
            _preparedFrame = _renderedFrame = -1;
            _visibilityCommands.Clear();
            UnavailableReason = Validate();
            if (UnavailableReason != null) { ReleaseResources(); return; }
            int width = _camera.targetTexture != null ? _camera.targetTexture.width : _camera.pixelWidth;
            int height = _camera.targetTexture != null ? _camera.targetTexture.height : _camera.pixelHeight;
            if (width < 1 || height < 1) { UnavailableReason = "Empty target"; ReleaseResources(); return; }
            if (!EnsureResources(width, height)) { UnavailableReason = "Target allocation failed"; ReleaseResources(); return; }
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
            _sceneCamera.Render();
            if (!_geometry.TryGetFrame(_sceneCamera, width, height, out _frame))
            {
                UnavailableReason = _geometry.UnavailableReason ?? "Scene geometry capture unavailable";
                ResetHistory(); return;
            }
            if (_frame.DepthLevelCount > 15) { UnavailableReason = "Depth hierarchy exceeds 15 levels"; ResetHistory(); return; }
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
                _visibilityCommands.DrawRenderer(renderer, material, surface.materialIndex, 0);
            }
            _preparedFrame = Time.frameCount;
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
            reflection = _reflection; return true;
        }

        private bool TryRender(Camera camera, RenderTexture source, Texture planarCoverage, bool composite, out RenderTexture result)
        {
            result = source;
            if (planarCoverage != null && (source == null || planarCoverage.dimension != TextureDimension.Tex2D ||
                planarCoverage.width != source.width || planarCoverage.height != source.height)) return false;
            if (!isActiveAndEnabled || !reflectionsEnabled || camera != _camera || source == null ||
                _preparedFrame != Time.frameCount || _renderedFrame != Time.frameCount ||
                _consumedSequence == _renderSequence || _sceneColor == null ||
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
            Graphics.Blit(source, _reflection, _material, 1);
            _material.SetTexture("_SsrReflection", _reflection);
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
        {
            texture = null;
            if (!isActiveAndEnabled || camera != _camera || _renderedFrame != Time.frameCount ||
                _consumedSequence != _renderSequence || _reflection == null) return false;
            texture = _reflection; return true;
        }

        public void ResetHistory() { HistoryAvailable = false; _historyFrame = -1; _renderedFrame = -1; }

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
                !_sceneColor.IsCreated() || !_historyColor.IsCreated() || !_historyDepth.IsCreated() ||
                !_visibility.IsCreated() || !_reflection.IsCreated() || !_composite.IsCreated())) ReleaseResources();
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
                _visibility = Target(width, height, 0, SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8)
                    ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32, "SSR visible receivers");
                _reflection = Target(width, height, 0, RenderTextureFormat.ARGBHalf, "SSR radiance and confidence");
                _composite = Target(width, height, 0, RenderTextureFormat.ARGBHalf, "SSR HDR composite");
            }
            return _sceneColor.IsCreated() && _historyColor.IsCreated() && _historyDepth.IsCreated() &&
                _visibility.IsCreated() && _reflection.IsCreated() && _composite.IsCreated();
        }

        private static RenderTexture Target(int width, int height, int depth, RenderTextureFormat format, string name)
        {
            var target = new RenderTexture(width, height, depth, format, RenderTextureReadWrite.Linear) {
                name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                useMipMap = false, autoGenerateMips = false, hideFlags = HideFlags.HideAndDontSave
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
            if (_material != null) { Destroy(_material); _material = null; }
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
