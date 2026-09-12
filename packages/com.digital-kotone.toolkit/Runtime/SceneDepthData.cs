using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Optional Built-in scene geometry prepass. Explicit opaque/cutout surfaces
    /// have their own depth, independent of actors and the main camera depth.
    /// No material discovery, source-material writes or global texture bindings.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class SceneDepthData : MonoBehaviour
    {
        [Serializable]
        public sealed class Surface
        {
            public Renderer renderer;
            [Min(0)] public int materialIndex;
            public CullMode cull = CullMode.Back;
            public Vector3 vertexScale = Vector3.one;
            public Texture alphaMask;
            public Vector4 alphaMaskST = new Vector4(1, 1, 0, 0);
            [Range(0, 1)] public float alphaCutoff;
            public bool receiveReflections = true;
            [Range(0, 1)] public float smoothness = 1;
            // Red channel, multiplied by smoothness; no implied game asset ABI.
            public Texture smoothnessMap;
            public Vector4 smoothnessMapST = new Vector4(1, 1, 0, 0);
        }

        /// <summary>Borrowed camera-local GPU data; valid only until the next render/configuration change.</summary>
        public readonly struct Frame
        {
            // RGB = normalized world mesh normal * .5 + .5, A = SSR eligibility (0 or 1).
            public readonly RenderTexture normalMask;
            // Positive view-space depth in world units, farClip on clear pixels.
            public readonly RenderTexture linearDepth;
            public readonly Matrix4x4 worldToCamera, gpuProjection;
            public readonly float farClip;
            public readonly uint sequence;
            private readonly RenderTexture[] _levels;
            public int DepthLevelCount => _levels == null ? 0 : _levels.Length;
            public RenderTexture GetDepthLevel(int level) => _levels[level];

            internal Frame(RenderTexture normals, RenderTexture[] levels, Matrix4x4 view,
                Matrix4x4 projection, float far, uint version)
            {
                normalMask = normals; _levels = levels; linearDepth = levels[0];
                worldToCamera = view; gpuProjection = projection; farClip = far; sequence = version;
            }
        }

        public Surface[] surfaces = Array.Empty<Surface>();
        public LayerMask excludedLayers;
        [Range(0, 1)] public float smoothnessThreshold = 0.5f;
        public bool buildDepthHierarchy = true;
        public SceneShaderBackend hierarchyBackend = SceneShaderBackend.Raster;
        public bool allowComputeFallback = true;
        public SceneShaderBackend ActiveHierarchyBackend { get; private set; }
        public string ComputeFallbackReason { get; private set; }
        public int ComputeDispatchCount { get; private set; }
        public int SubmittedSurfaces { get; private set; }
        public uint RenderSequence { get; private set; }
        public string UnavailableReason { get; private set; }

        private Camera _camera;
        private CommandBuffer _commands;
        private Shader _shader;
        private Material _reduction;
        private ComputeShader _computeAsset, _compute;
        private int _computeKernel;
        private SceneShaderBackend _allocatedBackend;
        private readonly List<Material> _materials = new List<Material>();
        private RenderTexture _normalMask;
        private RenderTexture[] _levels;
        private RenderTexture _cameraTarget;
        private Matrix4x4 _view, _projection;
        private float _far;
        private int _preparedFrame = -1, _renderedFrame = -1;
        private bool _hierarchy;

        private void OnEnable()
        {
            _camera = GetComponent<Camera>();
            _shader = Resources.Load<Shader>("SceneDepthData");
            _computeAsset = Resources.Load<ComputeShader>("SceneDepthHierarchy");
            _commands = new CommandBuffer { name = "Toolkit scene DepthID and min-depth hierarchy" };
            _camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _commands);
        }

        private void OnPreCull()
        {
            _preparedFrame = _renderedFrame = -1;
            SubmittedSurfaces = 0;
            ComputeDispatchCount = 0; ComputeFallbackReason = null;
            if (_commands == null) return;
            _commands.Clear();
            UnavailableReason = ValidateCamera();
            if (UnavailableReason != null) { ReleaseResources(); return; }
            if (!SceneComputeSupport.Select(buildDepthHierarchy ? hierarchyBackend : SceneShaderBackend.Raster,
                allowComputeFallback, _computeAsset, "ReduceMinDepth", RenderTextureFormat.RFloat,
                out var selected, out _computeKernel, out var fallback, out var error))
            { UnavailableReason = error; ReleaseResources(); return; }
            ActiveHierarchyBackend = selected; ComputeFallbackReason = fallback;
            int width = _camera.targetTexture != null ? _camera.targetTexture.width : _camera.pixelWidth;
            int height = _camera.targetTexture != null ? _camera.targetTexture.height : _camera.pixelHeight;
            if (width < 1 || height < 1) { UnavailableReason = "Empty target"; ReleaseResources(); return; }
            if (!EnsureTargets(width, height))
            {
                if (ActiveHierarchyBackend == SceneShaderBackend.Compute && allowComputeFallback)
                { ActiveHierarchyBackend = SceneShaderBackend.Raster; ComputeFallbackReason = "Compute depth allocation failed"; }
                else { UnavailableReason = "Target creation failed"; ReleaseResources(); return; }
                if (!EnsureTargets(width, height)) { UnavailableReason = "Fallback target creation failed"; ReleaseResources(); return; }
            }
            if (ActiveHierarchyBackend == SceneShaderBackend.Compute && _compute == null)
                _compute = Instantiate(_computeAsset);
            else if (ActiveHierarchyBackend == SceneShaderBackend.Raster && _compute != null)
            { Destroy(_compute); _compute = null; }
            _cameraTarget = _camera.targetTexture;
            _view = _camera.worldToCameraMatrix;
            _projection = GL.GetGPUProjectionMatrix(_camera.projectionMatrix, true);
            _far = _camera.farClipPlane;
            // Clear each color attachment separately; only our own depth is touched.
            _commands.SetRenderTarget(_normalMask);
            _commands.ClearRenderTarget(false, true, Color.clear);
            _commands.SetRenderTarget(_levels[0]);
            _commands.ClearRenderTarget(true, true, new Color(_far, 0, 0, 0));
            _commands.SetRenderTarget(new[] { new RenderTargetIdentifier(_normalMask),
                new RenderTargetIdentifier(_levels[0]) }, new RenderTargetIdentifier(_levels[0]));
            for (int i = 0; i < surfaces.Length; i++)
            {
                Surface surface = surfaces[i];
                if (!ValidSurface(surface)) continue;
                Renderer renderer = surface.renderer;
                int layer = 1 << renderer.gameObject.layer;
                if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy ||
                    (_camera.cullingMask & layer) == 0 || (excludedLayers.value & layer) != 0) continue;
                while (_materials.Count <= SubmittedSurfaces)
                    _materials.Add(new Material(_shader) { hideFlags = HideFlags.HideAndDontSave });
                Material material = _materials[SubmittedSurfaces++];
                material.SetMatrix("_SceneViewProjection", _projection * _view);
                material.SetMatrix("_SceneView", _view);
                material.SetFloat("_Cull", (int)surface.cull);
                material.SetVector("_SceneVertexScale", surface.vertexScale);
                material.SetTexture("_SceneAlphaMask", surface.alphaMask != null ? surface.alphaMask : Texture2D.whiteTexture);
                material.SetVector("_SceneAlphaMaskST", surface.alphaMaskST);
                material.SetFloat("_SceneAlphaCutoff", surface.alphaCutoff);
                material.SetTexture("_SceneSmoothnessMap", surface.smoothnessMap != null ? surface.smoothnessMap : Texture2D.whiteTexture);
                material.SetVector("_SceneSmoothnessMapST", surface.smoothnessMapST);
                material.SetFloat("_SceneSmoothness", surface.smoothness);
                material.SetFloat("_SceneSmoothnessThreshold", smoothnessThreshold);
                material.SetFloat("_SceneReceiveReflections", surface.receiveReflections ? 1 : 0);
                _commands.DrawRenderer(renderer, material, surface.materialIndex, 0);
            }
            if (_reduction == null) _reduction = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
            // Unbind the geometry MRT before its depth is read as a compute SRV.
            if (ActiveHierarchyBackend == SceneShaderBackend.Compute) _commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            for (int level = 1; level < _levels.Length; level++)
            {
                if (ActiveHierarchyBackend == SceneShaderBackend.Raster)
                    _commands.Blit(_levels[level - 1], _levels[level], _reduction, 1);
                else
                {
                    RenderTexture input = _levels[level - 1], output = _levels[level];
                    _commands.SetComputeVectorParam(_compute, "_DepthSize", new Vector4(input.width, input.height, output.width, output.height));
                    _commands.SetComputeTextureParam(_compute, _computeKernel, "_DepthInput", input);
                    _commands.SetComputeTextureParam(_compute, _computeKernel, "_DepthOutput", output);
                    _commands.DispatchCompute(_compute, _computeKernel, (output.width + 7) / 8, (output.height + 7) / 8, 1);
                    ComputeDispatchCount++;
                }
            }
            // Unity restores active framebuffer state after this command buffer.
            // Shaders use explicit camera matrices, leaving camera globals alone.
            _preparedFrame = Time.frameCount;
        }

        private void OnPostRender()
        {
            if (_preparedFrame != Time.frameCount) return;
            _renderedFrame = Time.frameCount;
            RenderSequence++;
        }

        /// <summary>Call from OnRenderImage or after Camera.Render, not before camera rendering.</summary>
        public bool TryGetFrame(Camera camera, int width, int height, out Frame frame)
        {
            frame = default;
            if (!isActiveAndEnabled || camera != _camera || _renderedFrame != Time.frameCount ||
                _preparedFrame != Time.frameCount || _normalMask == null || !_normalMask.IsCreated() ||
                _normalMask.width != width || _normalMask.height != height || _camera.targetTexture != _cameraTarget)
                return false;
            if (_levels == null) return false;
            foreach (RenderTexture level in _levels) if (level == null || !level.IsCreated()) return false;
            frame = new Frame(_normalMask, _levels, _view, _projection, _far, RenderSequence);
            return true;
        }

        private string ValidateCamera()
        {
            if (surfaces == null || surfaces.Length == 0) return "No registered surfaces";
            if (_shader == null || !_shader.isSupported || SystemInfo.supportedRenderTargetCount < 2 ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat) ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32)) return "Unsupported target/shader capability";
            if (GraphicsSettings.currentRenderPipeline != null || _camera.actualRenderingPath != RenderingPath.Forward)
                return "Requires Built-in Forward";
            if (_camera.stereoEnabled || _camera.rect != new Rect(0, 0, 1, 1) || _camera.allowDynamicResolution ||
                (_camera.targetTexture != null && (_camera.targetTexture.dimension != TextureDimension.Tex2D ||
                 _camera.targetTexture.antiAliasing > 1 || _camera.targetTexture.useDynamicScale)) ||
                (_camera.targetTexture == null && _camera.allowMSAA && QualitySettings.antiAliasing > 1))
                return "Requires full viewport, non-XR, non-MSAA, fixed-size 2D target";
            if (!Unit(smoothnessThreshold) || !Finite(_camera.nearClipPlane) || _camera.nearClipPlane <= 0 ||
                !Finite(_camera.farClipPlane) || _camera.farClipPlane <= _camera.nearClipPlane)
                return "Invalid depth range or threshold";
            return null;
        }

        internal static bool ValidSurface(Surface s)
        {
            if (s == null || s.renderer == null || s.materialIndex < 0 ||
                s.materialIndex >= s.renderer.sharedMaterials.Length || !Unit(s.alphaCutoff) || !Unit(s.smoothness) ||
                (int)s.cull < 0 || (int)s.cull > 2) return false;
            // MeshRenderer and SkinnedMeshRenderer have the tested DrawRenderer contract.
            if (!(s.renderer is MeshRenderer) && !(s.renderer is SkinnedMeshRenderer)) return false;
            var skin = s.renderer as SkinnedMeshRenderer;
            var filter = s.renderer.GetComponent<MeshFilter>();
            Mesh mesh = skin != null ? skin.sharedMesh : filter != null ? filter.sharedMesh : null;
            if (mesh == null || s.materialIndex >= mesh.subMeshCount) return false;
            for (int i = 0; i < 3; i++) if (!Finite(s.vertexScale[i]) || Mathf.Abs(s.vertexScale[i]) < 1e-6f) return false;
            for (int i = 0; i < 4; i++) if (!Finite(s.alphaMaskST[i]) || !Finite(s.smoothnessMapST[i])) return false;
            return true;
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool Unit(float v) => Finite(v) && v >= 0 && v <= 1;

        private bool EnsureTargets(int width, int height)
        {
            bool created = _normalMask != null && _normalMask.IsCreated() && _levels != null;
            if (_levels != null) foreach (RenderTexture level in _levels)
                created &= level != null && level.IsCreated();
            if (created && _normalMask.width == width &&
                _normalMask.height == height && _hierarchy == buildDepthHierarchy && _allocatedBackend == ActiveHierarchyBackend) return true;
            ReleaseTargets();
            _hierarchy = buildDepthHierarchy;
            _allocatedBackend = ActiveHierarchyBackend;
            _normalMask = MakeTarget(width, height, 0, RenderTextureFormat.ARGB32, "ToolkitSceneNormalMask");
            var levels = new List<RenderTexture>();
            levels.Add(MakeTarget(width, height, 24, RenderTextureFormat.RFloat, "ToolkitSceneLinearDepth"));
            while (_hierarchy && (width > 1 || height > 1))
            {
                // Ceil sizes preserve odd right/bottom edges; these are explicit
                // levels, not hardware floor-sized mips or averaged depth.
                width = (width + 1) / 2; height = (height + 1) / 2;
                levels.Add(MakeTarget(width, height, 0, RenderTextureFormat.RFloat, "ToolkitSceneMinDepth" + levels.Count,
                    ActiveHierarchyBackend == SceneShaderBackend.Compute));
            }
            _levels = levels.ToArray();
            if (!_normalMask.IsCreated()) return false;
            foreach (RenderTexture level in _levels) if (!level.IsCreated()) return false;
            return true;
        }

        private static RenderTexture MakeTarget(int width, int height, int depth, RenderTextureFormat format, string name, bool randomWrite = false)
        {
            var target = new RenderTexture(width, height, depth, format, RenderTextureReadWrite.Linear) {
                name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                useMipMap = false, autoGenerateMips = false, enableRandomWrite = randomWrite, hideFlags = HideFlags.HideAndDontSave
            };
            target.Create();
            return target;
        }

        private void ReleaseTargets()
        {
            if (_normalMask != null) { _normalMask.Release(); Destroy(_normalMask); _normalMask = null; }
            if (_levels != null) foreach (RenderTexture level in _levels)
                if (level != null) { level.Release(); Destroy(level); }
            _levels = null;
        }

        private void ReleaseResources()
        {
            ReleaseTargets();
            foreach (Material material in _materials) if (material != null) Destroy(material);
            _materials.Clear();
            if (_reduction != null) { Destroy(_reduction); _reduction = null; }
            if (_compute != null) { Destroy(_compute); _compute = null; }
            ComputeDispatchCount = 0;
        }

        private void OnDisable()
        {
            _preparedFrame = _renderedFrame = -1;
            SubmittedSurfaces = 0;
            if (_commands != null)
            {
                if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, _commands);
                _commands.Release(); _commands = null;
            }
            ReleaseResources();
        }
    }
}
