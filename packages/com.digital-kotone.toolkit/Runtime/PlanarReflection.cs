using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Camera-owned planar capture of explicit single-pass opaque/cutout draws.
    /// RGB is reflected radiance; A is coverage, independent of source alpha.
    /// Does not install an image effect or change main materials/lighting.
    /// </summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class PlanarReflection : MonoBehaviour
    {
        [Serializable]
        public sealed class Draw
        {
            public SceneDepthData.Surface surface = new SceneDepthData.Surface();
            // Must support CommandBuffer.DrawRenderer with explicit lighting,
            // ZWrite On and matching geometry/alpha. No automatic Unity lights.
            public Material material;
            [Min(0)] public int shaderPass;
        }

        [Serializable]
        public sealed class Receiver
        {
            public SceneDepthData.Surface surface = new SceneDepthData.Surface();
            [Range(0, 1)] public float strength = 1;
        }

        public bool reflectionsEnabled;
        public Vector3 planePoint = Vector3.zero;
        public Vector3 planeNormal = Vector3.up;
        [Min(.001f)] public float clipOffset = .01f;
        [Range(.25f, 1)] public float resolutionScale = .5f;
        [Range(0, 8)] public float maximumRoughnessMip = 5;
        [Min(.001f)] public float receiverPlaneTolerance = .025f;
        public LayerMask reflectedLayers;
        public Draw[] reflectedSurfaces = Array.Empty<Draw>();
        public Receiver[] receivers = Array.Empty<Receiver>();
        public string UnavailableReason { get; private set; }

        private Camera _camera, _captureCamera;
        private Shader _shader;
        private Material _utility;
        private readonly List<Material> _coverageMaterials = new List<Material>();
        private readonly List<Material> _receiverMaterials = new List<Material>();
        private CommandBuffer _captureCommands, _receiverCommands;
        private RenderTexture _capture, _reflection;
        private Matrix4x4 _reflectedViewProjection;
        private int _preparedFrame = -1, _renderedFrame = -1;
        private RenderTexture _sourceTarget;
        private bool _rendering;

        private void OnEnable()
        {
            _camera = GetComponent<Camera>();
            _shader = Resources.Load<Shader>("PlanarReflection");
            _receiverCommands = new CommandBuffer { name = "Toolkit planar receivers" };
            _camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _receiverCommands);
        }

        private void OnPreCull()
        {
            _preparedFrame = _renderedFrame = -1;
            _receiverCommands.Clear();
            if (_rendering) { UnavailableReason = "Recursive planar capture"; return; }
            UnavailableReason = Validate();
            if (UnavailableReason != null) { ReleaseResources(); return; }
            int width = _camera.targetTexture != null ? _camera.targetTexture.width : _camera.pixelWidth;
            int height = _camera.targetTexture != null ? _camera.targetTexture.height : _camera.pixelHeight;
            Vector3 normal = planeNormal.normalized;
            Vector3 eye = _camera.worldToCameraMatrix.inverse.MultiplyPoint(Vector3.zero);
            if (Vector3.Dot(normal, eye - planePoint) <= clipOffset)
            { UnavailableReason = "Camera must be on the positive side of the reflection plane"; ReleaseResources(); return; }
            if (!EnsureResources(width, height))
            { UnavailableReason = "Planar target allocation failed"; ReleaseResources(); return; }

            _captureCamera.CopyFrom(_camera);
            _captureCamera.enabled = false;
            _captureCamera.allowMSAA = false;
            _captureCamera.depthTextureMode = DepthTextureMode.None;
            _captureCamera.cullingMask = 0; // Only registered reduced Forward draws.
            _captureCamera.clearFlags = CameraClearFlags.SolidColor;
            _captureCamera.backgroundColor = Color.clear;
            _captureCamera.targetTexture = _capture;
            Matrix4x4 reflectedView = _camera.worldToCameraMatrix * ReflectionMatrix(planePoint, normal);
            Matrix4x4 inverseView = reflectedView.inverse;
            _captureCamera.transform.SetPositionAndRotation(inverseView.MultiplyPoint(Vector3.zero),
                Quaternion.LookRotation(inverseView.MultiplyVector(Vector3.back), inverseView.MultiplyVector(Vector3.up)));
            _captureCamera.worldToCameraMatrix = reflectedView;
            _captureCamera.projectionMatrix = _camera.projectionMatrix;
            // Transform the entire plane covector, not just its normal. Keep
            // objects on the source-camera side; reject geometry below it.
            Vector3 clipPoint = planePoint + normal * clipOffset;
            Vector4 worldClip = new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(normal, clipPoint));
            Vector4 viewClip = reflectedView.inverse.transpose * worldClip;
            Matrix4x4 projection = _captureCamera.CalculateObliqueMatrix(viewClip);
            if (!Finite(projection)) { UnavailableReason = "Degenerate oblique projection"; ReleaseResources(); return; }
            _captureCamera.projectionMatrix = projection;
            _reflectedViewProjection = GL.GetGPUProjectionMatrix(projection, true) * reflectedView;

            _captureCommands.Clear();
            int count = 0;
            foreach (Draw draw in reflectedSurfaces)
            {
                if (!ValidDraw(draw)) continue;
                _captureCommands.DrawRenderer(draw.surface.renderer, draw.material, draw.surface.materialIndex, draw.shaderPass);
                count++;
            }
            if (count == 0) { UnavailableReason = "No valid reflected draws"; ReleaseResources(); return; }
            // Authored opaque material alpha may be zero. Coverage is an
            // independent data channel, not the material's output opacity.
            _captureCommands.Blit(Texture2D.blackTexture, BuiltinRenderTextureType.CameraTarget, _utility, 0);
            _captureCommands.SetRenderTarget(_capture);
            int materialIndex = 0;
            foreach (Draw draw in reflectedSurfaces)
            {
                if (!ValidDraw(draw)) continue;
                Material mask = GetMaterial(_coverageMaterials, materialIndex++);
                BindSurface(mask, draw.surface);
                _captureCommands.DrawRenderer(draw.surface.renderer, mask, draw.surface.materialIndex, 1);
            }
            bool oldCulling = GL.invertCulling;
            _rendering = true;
            try { GL.invertCulling = !oldCulling; _captureCamera.Render(); }
            finally { GL.invertCulling = oldCulling; _rendering = false; }
            _capture.GenerateMips();

            _receiverCommands.SetRenderTarget(new RenderTargetIdentifier(_reflection), BuiltinRenderTextureType.CurrentActive);
            _receiverCommands.ClearRenderTarget(false, true, Color.clear);
            materialIndex = 0;
            foreach (Receiver receiver in receivers)
            {
                if (receiver == null || !Active(receiver.surface) || !Range(receiver.strength, 0, 1) ||
                    receiver.strength == 0 || !receiver.surface.receiveReflections ||
                    (_camera.cullingMask & (1 << receiver.surface.renderer.gameObject.layer)) == 0) continue;
                Material material = GetMaterial(_receiverMaterials, materialIndex++);
                BindSurface(material, receiver.surface);
                material.SetTexture("_PlanarCapture", _capture);
                material.SetMatrix("_PlanarViewProjection", _reflectedViewProjection);
                material.SetVector("_PlanarPlane", new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(normal, planePoint)));
                material.SetVector("_PlanarOptions", new Vector4(receiver.strength, maximumRoughnessMip, receiverPlaneTolerance, 0));
                _receiverCommands.DrawRenderer(receiver.surface.renderer, material, receiver.surface.materialIndex, 2);
            }
            _sourceTarget = _camera.targetTexture;
            _preparedFrame = Time.frameCount;
        }

        private void OnPostRender()
        {
            if (_preparedFrame == Time.frameCount) _renderedFrame = Time.frameCount;
        }

        /// <summary>Borrowed full-resolution linear RGB radiance/A coverage, not a complete PBR indirect term.</summary>
        public bool TryGetReflection(Camera camera, int width, int height, out RenderTexture reflection)
        {
            reflection = null;
            if (!isActiveAndEnabled || !reflectionsEnabled || camera != _camera ||
                _renderedFrame != Time.frameCount || _sourceTarget != _camera.targetTexture ||
                _reflection == null || !_reflection.IsCreated() || _reflection.width != width || _reflection.height != height)
                return false;
            reflection = _reflection; return true;
        }

        /// <summary>World-space affine reflection across a plane. Normal must be finite and nonzero.</summary>
        public static Matrix4x4 ReflectionMatrix(Vector3 point, Vector3 normal)
        {
            if (!Finite(point) || !Finite(normal) || normal.sqrMagnitude < 1e-12f)
                throw new ArgumentException("Reflection plane must be finite with nonzero normal");
            normal.Normalize();
            var matrix = Matrix4x4.identity;
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++) matrix[row, col] -= 2 * normal[row] * normal[col];
                matrix[row, 3] = 2 * Vector3.Dot(normal, point) * normal[row];
            }
            return matrix;
        }

        private bool ValidDraw(Draw draw) => draw != null && Active(draw.surface) && draw.material != null &&
            draw.material.shader != null && draw.material.shader.isSupported && draw.shaderPass >= 0 &&
            draw.shaderPass < draw.material.passCount &&
            (reflectedLayers.value & (1 << draw.surface.renderer.gameObject.layer)) != 0;

        private static bool Active(SceneDepthData.Surface surface) => SceneDepthData.ValidSurface(surface) &&
            surface.renderer.enabled && !surface.renderer.forceRenderingOff && surface.renderer.gameObject.activeInHierarchy;

        private string Validate()
        {
            if (!reflectionsEnabled || reflectedLayers.value == 0 || reflectedSurfaces == null || reflectedSurfaces.Length == 0 ||
                receivers == null || receivers.Length == 0) return "Disabled or no reflected surfaces/receivers";
            if (_shader == null || !_shader.isSupported || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
                return "Unsupported planar shader/HDR target";
            if (GraphicsSettings.currentRenderPipeline != null || _camera.actualRenderingPath != RenderingPath.Forward ||
                _camera.stereoEnabled || _camera.rect != new Rect(0, 0, 1, 1) || _camera.allowDynamicResolution ||
                (_camera.targetTexture != null && (_camera.targetTexture.dimension != TextureDimension.Tex2D ||
                    _camera.targetTexture.antiAliasing > 1 || _camera.targetTexture.useDynamicScale)) ||
                (_camera.targetTexture == null && _camera.allowMSAA && QualitySettings.antiAliasing > 1))
                return "Requires fixed-size Built-in Forward non-XR/MSAA full viewport";
            if (!Finite(planePoint) || !Finite(planeNormal) || planeNormal.sqrMagnitude < 1e-12f ||
                !Range(clipOffset, .001f, 100) || !Range(resolutionScale, .25f, 1) ||
                !Range(maximumRoughnessMip, 0, 8) || !Range(receiverPlaneTolerance, .001f, 1) ||
                !Finite(_camera.worldToCameraMatrix) || !Finite(_camera.projectionMatrix)) return "Invalid planar settings";
            return null;
        }

        private static bool Range(float value, float min, float max) => !float.IsNaN(value) && value >= min && value <= max;
        private static bool Finite(Vector3 value) => Range(value.x, -1e12f, 1e12f) && Range(value.y, -1e12f, 1e12f) && Range(value.z, -1e12f, 1e12f);
        private static bool Finite(Matrix4x4 value)
        { for (int i = 0; i < 16; i++) if (!Range(value[i], -1e12f, 1e12f)) return false; return Mathf.Abs(value.determinant) > 1e-12f; }

        private Material GetMaterial(List<Material> list, int index)
        {
            while (list.Count <= index) list.Add(new Material(_shader) { hideFlags = HideFlags.HideAndDontSave });
            return list[index];
        }

        private static void BindSurface(Material material, SceneDepthData.Surface surface)
        {
            material.SetFloat("_Cull", (int)surface.cull);
            material.SetVector("_PlanarVertexScale", surface.vertexScale);
            material.SetTexture("_PlanarAlpha", surface.alphaMask != null ? surface.alphaMask : Texture2D.whiteTexture);
            material.SetVector("_PlanarAlphaST", surface.alphaMaskST);
            material.SetFloat("_PlanarCutoff", surface.alphaCutoff);
            material.SetTexture("_PlanarSmoothness", surface.smoothnessMap != null ? surface.smoothnessMap : Texture2D.whiteTexture);
            material.SetVector("_PlanarSmoothnessST", surface.smoothnessMapST);
            material.SetFloat("_PlanarSmoothnessScale", surface.smoothness);
        }

        private bool EnsureResources(int width, int height)
        {
            if (width < 1 || height < 1) return false;
            int captureWidth = Mathf.Max(1, Mathf.CeilToInt(width * resolutionScale));
            int captureHeight = Mathf.Max(1, Mathf.CeilToInt(height * resolutionScale));
            if (_capture != null && (_capture.width != captureWidth || _capture.height != captureHeight ||
                _reflection.width != width || _reflection.height != height || !_capture.IsCreated() || !_reflection.IsCreated()))
                ReleaseResources();
            if (_captureCamera == null)
            {
                var host = new GameObject("Toolkit planar capture") { hideFlags = HideFlags.HideAndDontSave };
                host.transform.SetParent(transform, false);
                _captureCamera = host.AddComponent<Camera>(); _captureCamera.enabled = false;
                _captureCommands = new CommandBuffer { name = "Toolkit planar reduced Forward draws" };
                _captureCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _captureCommands);
                _utility = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_capture == null)
            {
                _capture = Target(captureWidth, captureHeight, 24, true, "Planar capture and coverage");
                _reflection = Target(width, height, 0, false, "Planar radiance and coverage");
            }
            return _capture.IsCreated() && _reflection.IsCreated();
        }

        private static RenderTexture Target(int width, int height, int depth, bool mip, string name)
        {
            var target = new RenderTexture(width, height, depth, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) {
                name = name, filterMode = mip ? FilterMode.Trilinear : FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                useMipMap = mip, autoGenerateMips = false, hideFlags = HideFlags.HideAndDontSave
            };
            target.Create(); return target;
        }

        private void ReleaseResources()
        {
            _preparedFrame = _renderedFrame = -1;
            if (_captureCommands != null)
            {
                if (_captureCamera != null) _captureCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, _captureCommands);
                _captureCommands.Release(); _captureCommands = null;
            }
            if (_captureCamera != null) { _captureCamera.targetTexture = null; Destroy(_captureCamera.gameObject); _captureCamera = null; }
            if (_utility != null) { Destroy(_utility); _utility = null; }
            foreach (Material material in _coverageMaterials) if (material != null) Destroy(material);
            foreach (Material material in _receiverMaterials) if (material != null) Destroy(material);
            _coverageMaterials.Clear(); _receiverMaterials.Clear();
            Release(ref _capture); Release(ref _reflection);
        }
        private void Release(ref RenderTexture target)
        { if (target != null) { target.Release(); Destroy(target); target = null; } }
        private void OnDisable()
        {
            if (_receiverCommands != null)
            {
                if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _receiverCommands);
                _receiverCommands.Release(); _receiverCommands = null;
            }
            ReleaseResources();
        }
    }
}
