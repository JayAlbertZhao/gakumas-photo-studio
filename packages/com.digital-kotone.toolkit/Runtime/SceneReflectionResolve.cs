using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Explicit scene indirect-specular resolve. Registered main materials must
    /// omit their previous indirect-specular term; all other pixels stay intact.
    /// No material discovery/writes, implicit image effect or global bindings.
    /// </summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class SceneReflectionResolve : MonoBehaviour
    {
        [Serializable]
        public sealed class Receiver
        {
            public SceneDepthData.Surface surface = new SceneDepthData.Surface();
            // Linear reflectance, not an sRGB UI color or an already-lit term.
            public Vector3 f0 = new Vector3(.04f, .04f, .04f);
            public Texture f0Map;
            public Vector4 f0MapST = new Vector4(1, 1, 0, 0);
            [Range(0, 1)] public float occlusion = 1;
            [Range(0, 8)] public float specularScale = 1;
            public Texture normalMap;
            public Vector4 normalMapST = new Vector4(1, 1, 0, 0);
            [Range(0, 2)] public float normalStrength = 1;
            // Linear tangent-space RGB normals. Packed Unity import formats
            // require explicit conversion, not guessed alpha/green swizzles.
            public Vector2 distortionScale = new Vector2(.1f, -.1f);
            public Texture probe;
            public bool decodeProbeHdr;
            public Vector4 probeHdrDecode = new Vector4(1, 1, 0, 0);
            [Range(0, 12)] public float probeMaximumMip = 6;
        }

        public bool reflectionsEnabled;
        public bool inputExcludesIndirectSpecular;
        public Receiver[] receivers = Array.Empty<Receiver>();
        public ScreenSpaceReflection screenSpaceReflection;
        public PlanarReflection planarReflection;
        public string UnavailableReason { get; private set; }
        private Camera _camera;
        private Shader _shader;
        private Material _resolve;
        private readonly List<Material> _surfaceMaterials = new List<Material>();
        private CommandBuffer _commands;
        private RenderTexture _probe, _response, _offset, _radiance, _composite, _sourceTarget;
        private int _preparedFrame = -1, _renderedFrame = -1;
        private uint _sequence, _consumed;

        private void OnEnable()
        {
            _camera = GetComponent<Camera>(); _shader = Resources.Load<Shader>("SceneReflectionResolve");
            _commands = new CommandBuffer { name = "Toolkit indirect reflection inputs" };
            _camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _commands);
        }

        private void OnPreCull()
        {
            _preparedFrame = _renderedFrame = -1; _commands.Clear();
            UnavailableReason = Validate();
            if (UnavailableReason != null) { ReleaseResources(); return; }
            int width = _camera.targetTexture != null ? _camera.targetTexture.width : _camera.pixelWidth;
            int height = _camera.targetTexture != null ? _camera.targetTexture.height : _camera.pixelHeight;
            if (!EnsureResources(width, height)) { UnavailableReason = "Reflection resolve allocation failed"; ReleaseResources(); return; }
            _commands.SetRenderTarget(new[] { new RenderTargetIdentifier(_probe), new RenderTargetIdentifier(_response),
                new RenderTargetIdentifier(_offset) }, BuiltinRenderTextureType.CurrentActive);
            _commands.ClearRenderTarget(false, true, Color.clear);
            int submitted = 0;
            for (int index = 0; index < receivers.Length; index++)
            {
                Receiver receiver = receivers[index];
                if (!Valid(receiver)) continue;
                var surface = receiver.surface;
                if ((_camera.cullingMask & (1 << surface.renderer.gameObject.layer)) == 0 || !surface.renderer.enabled ||
                    surface.renderer.forceRenderingOff || !surface.renderer.gameObject.activeInHierarchy) continue;
                while (_surfaceMaterials.Count <= submitted)
                    _surfaceMaterials.Add(new Material(_shader) { hideFlags = HideFlags.HideAndDontSave });
                Material material = _surfaceMaterials[submitted++];
                material.SetFloat("_Cull", (int)surface.cull);
                material.SetVector("_ReflectVertexScale", surface.vertexScale);
                material.SetTexture("_ReflectAlpha", surface.alphaMask != null ? surface.alphaMask : Texture2D.whiteTexture);
                material.SetVector("_ReflectAlphaST", surface.alphaMaskST);
                material.SetFloat("_ReflectCutoff", surface.alphaCutoff);
                material.SetTexture("_ReflectSmoothness", surface.smoothnessMap != null ? surface.smoothnessMap : Texture2D.whiteTexture);
                material.SetVector("_ReflectSmoothnessST", surface.smoothnessMapST);
                material.SetVector("_ReflectSurface", new Vector4(surface.smoothness, receiver.occlusion, receiver.specularScale, index + 1));
                material.SetVector("_ReflectF0", receiver.f0);
                material.SetTexture("_ReflectF0Map", receiver.f0Map != null ? receiver.f0Map : Texture2D.whiteTexture);
                material.SetVector("_ReflectF0ST", receiver.f0MapST);
                material.SetTexture("_ReflectNormal", receiver.normalMap != null ? receiver.normalMap : Texture2D.whiteTexture);
                material.SetVector("_ReflectNormalST", receiver.normalMapST);
                material.SetVector("_ReflectNormalOptions", new Vector4(receiver.normalMap != null ? 1 : 0, receiver.normalStrength,
                    receiver.distortionScale.x, receiver.distortionScale.y));
                material.SetTexture("_ReflectProbe", receiver.probe);
                material.SetVector("_ReflectProbeOptions", new Vector4(receiver.probe != null ? 1 : 0, receiver.decodeProbeHdr ? 1 : 0, receiver.probeMaximumMip, 0));
                material.SetVector("_ReflectProbeDecode", receiver.probeHdrDecode);
                _commands.DrawRenderer(surface.renderer, material, surface.materialIndex, 0);
            }
            if (submitted == 0) { _commands.Clear(); UnavailableReason = "No valid reflection receivers"; ReleaseResources(); return; }
            _sourceTarget = _camera.targetTexture; _preparedFrame = Time.frameCount;
        }

        private void OnPostRender()
        { if (_preparedFrame == Time.frameCount) { _renderedFrame = Time.frameCount; _sequence++; } }

        /// <summary>HDR source must omit indirect specular on registered receivers. Borrowed result, once per render.</summary>
        public bool TryComposite(Camera camera, RenderTexture sourceWithoutIndirect, out RenderTexture result)
        {
            result = sourceWithoutIndirect;
            if (!Ready(camera) || sourceWithoutIndirect == null || _consumed == _sequence ||
                sourceWithoutIndirect.width != _probe.width || sourceWithoutIndirect.height != _probe.height) return false;
            _consumed = _sequence;
            RenderTexture planar = null, ssr = null;
            if (planarReflection != null) planarReflection.TryGetReflection(camera, _probe.width, _probe.height, out planar);
            // No additive SSR intermediate is produced or added over the base.
            if (screenSpaceReflection != null) screenSpaceReflection.TryTrace(camera, sourceWithoutIndirect, planar, out ssr);
            _resolve.SetTexture("_ResolveProbe", _probe); _resolve.SetTexture("_ResolveResponse", _response);
            _resolve.SetTexture("_ResolveOffset", _offset);
            _resolve.SetTexture("_ResolvePlanar", planar != null ? planar : Texture2D.blackTexture);
            _resolve.SetTexture("_ResolveSsr", ssr != null ? ssr : Texture2D.blackTexture);
            _resolve.SetVector("_ResolveInputs", new Vector4(planar != null ? 1 : 0, ssr != null ? 1 : 0, 0, 0));
            Graphics.Blit(sourceWithoutIndirect, _radiance, _resolve, 1);
            _resolve.SetTexture("_ResolveRadiance", _radiance);
            Graphics.Blit(sourceWithoutIndirect, _composite, _resolve, 2);
            result = _composite; return true;
        }

        public bool TryGetRadiance(Camera camera, out RenderTexture radiance)
        {
            radiance = null;
            if (!Ready(camera) || _consumed != _sequence) return false;
            radiance = _radiance; return true;
        }

        private bool Ready(Camera camera) => isActiveAndEnabled && reflectionsEnabled && inputExcludesIndirectSpecular &&
            camera == _camera && _renderedFrame == Time.frameCount && _sourceTarget == _camera.targetTexture &&
            _probe != null && _probe.IsCreated() && _response.IsCreated() && _offset.IsCreated() && _radiance.IsCreated() && _composite.IsCreated();

        private string Validate()
        {
            if (!reflectionsEnabled || receivers == null || receivers.Length == 0) return "Disabled or no receivers";
            if (!inputExcludesIndirectSpecular) return "Source must explicitly exclude receiver indirect specular";
            if (receivers.Length > 1024) return "At most 1024 receiver IDs in half-float buffer";
            if (_shader == null || !_shader.isSupported || SystemInfo.supportedRenderTargetCount < 3 ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)) return "Unsupported reflection MRT/HDR capability";
            if (GraphicsSettings.currentRenderPipeline != null || _camera.actualRenderingPath != RenderingPath.Forward ||
                _camera.stereoEnabled || _camera.rect != new Rect(0, 0, 1, 1) || _camera.allowDynamicResolution ||
                (_camera.targetTexture != null && (_camera.targetTexture.dimension != TextureDimension.Tex2D ||
                    _camera.targetTexture.antiAliasing > 1 || _camera.targetTexture.useDynamicScale)) ||
                (_camera.targetTexture == null && _camera.allowMSAA && QualitySettings.antiAliasing > 1))
                return "Requires fixed-size Built-in Forward non-XR/MSAA full viewport";
            return null;
        }

        private static bool Valid(Receiver value) => value != null && SceneDepthData.ValidSurface(value.surface) &&
            value.surface.receiveReflections &&
            VectorRange(value.f0, 0, 1) && Range(value.occlusion, 0, 1) && Range(value.specularScale, 0, 8) &&
            Range(value.normalStrength, 0, 2) && Range(value.distortionScale.x, -.5f, .5f) && Range(value.distortionScale.y, -.5f, .5f) &&
            Range(value.probeMaximumMip, 0, 12) && Finite(value.f0MapST) && Finite(value.normalMapST) && Finite(value.probeHdrDecode) &&
            (value.probe == null || value.probe.dimension == TextureDimension.Cube);
        private static bool Range(float v, float min, float max) => !float.IsNaN(v) && v >= min && v <= max;
        private static bool VectorRange(Vector3 v, float min, float max) => Range(v.x, min, max) && Range(v.y, min, max) && Range(v.z, min, max);
        private static bool Finite(Vector4 v) => Range(v.x, -1e6f, 1e6f) && Range(v.y, -1e6f, 1e6f) && Range(v.z, -1e6f, 1e6f) && Range(v.w, -1e6f, 1e6f);

        private bool EnsureResources(int width, int height)
        {
            if (width < 1 || height < 1) return false;
            if (_probe != null && (_probe.width != width || _probe.height != height || !_probe.IsCreated() ||
                !_response.IsCreated() || !_offset.IsCreated() || !_radiance.IsCreated() || !_composite.IsCreated())) ReleaseResources();
            if (_resolve == null) _resolve = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
            if (_probe == null)
            {
                _probe = Target(width, height, "Reflection probe radiance");
                _response = Target(width, height, "Reflection material response");
                _offset = Target(width, height, "Reflection normal offset and receiver ID");
                _radiance = Target(width, height, "Unified reflection radiance");
                _composite = Target(width, height, "Unified reflection HDR composite");
            }
            return _probe.IsCreated() && _response.IsCreated() && _offset.IsCreated() && _radiance.IsCreated() && _composite.IsCreated();
        }
        private static RenderTexture Target(int width, int height, string name)
        {
            var texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) {
                name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create(); return texture;
        }
        private void ReleaseResources()
        {
            _preparedFrame = _renderedFrame = -1;
            foreach (Material material in _surfaceMaterials) if (material != null) Destroy(material);
            _surfaceMaterials.Clear(); if (_resolve != null) { Destroy(_resolve); _resolve = null; }
            Release(ref _probe); Release(ref _response); Release(ref _offset); Release(ref _radiance); Release(ref _composite);
        }
        private void Release(ref RenderTexture texture)
        { if (texture != null) { texture.Release(); Destroy(texture); texture = null; } }
        private void OnDisable()
        {
            if (_commands != null)
            {
                if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _commands);
                _commands.Release(); _commands = null;
            }
            ReleaseResources();
        }
    }
}
