using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit scene-only deferred island before the host's Forward actors.
    /// The host excludes sceneLayers from its culling mask; this component never changes it.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class SceneDeferredCamera : MonoBehaviour
    {
        [Serializable]
        public class MaterialInputs
        {
            public Texture albedoMap, normalMap, mosMap, emissionMap;
            public Vector4 uvST = new Vector4(1, 1, 0, 0);
            public Vector3 albedo = Vector3.one, emission = Vector3.zero;
            // Texture RGB: metallic, occlusion, smoothness. Independent from any game Def ABI.
            public Vector3 mos = new Vector3(0, 1, .5f);
            [Range(0, 1)] public float alpha = 1;
        }

        [Serializable]
        public sealed class Surface
        {
            public Renderer renderer;
            public int materialIndex;
            public CullMode cull = CullMode.Back;
            public Vector3 vertexScale = Vector3.one;
            public MaterialInputs inputs = new MaterialInputs();
            [Range(0, 1)] public float alphaCutoff;
            // 0 = no decals; otherwise exact projector receiver group 1..255.
            [Range(0, 255)] public int receiverGroup = 1;
        }

        [Serializable]
        public sealed class Decal
        {
            public bool enabled = true;
            // Unit box [-.5,.5], XY atlas plane, positive local Z is height.
            public Matrix4x4 localToWorld = Matrix4x4.identity;
            public MaterialInputs inputs = new MaterialInputs();
            [Range(1, 255)] public int receiverGroup = 1;
            [Range(0, 1)] public float albedoWeight = 1, normalWeight, emissionWeight;
            public Vector3 mosWeight = Vector3.zero;
            // Independent height-density model, not a claim about the unpublished source formula.
            public bool heightOcclusion;
            public Texture heightMap;
            [Range(0, 1)] public float height = 1;
            [Min(.0001f)] public float heightFade = 1;
            [Range(-1, 1)] public float minimumFacing = -1;
        }

        public bool sceneEnabled;
        public LayerMask sceneLayers;
        public Surface[] surfaces = Array.Empty<Surface>();
        public Decal[] decals = Array.Empty<Decal>();
        // Linear incident radiance, explicit inputs rather than ambient global state.
        public Vector3 lightDirection = new Vector3(0, 0, -1);
        public Vector3 lightRadiance = Vector3.one;
        public Vector3 ambientIrradiance = new Vector3(.1f, .1f, .1f);
        public SceneDecalLightSettings decalLighting = new SceneDecalLightSettings();
        private SceneDecalLightRenderer _decalLights;
        public int SubmittedLights => _decalLights == null ? 0 : _decalLights.SubmittedLights;
        public int CulledLights => _decalLights == null ? 0 : _decalLights.CulledLights;
        public int LightDrawCalls => _decalLights == null ? 0 : _decalLights.DrawCalls;
        public int LightBufferCapacity => _decalLights == null ? 0 : _decalLights.BufferCapacity;
        public int LightTargetCount => _decalLights?.Accumulation != null ? 1 : 0;
        public SceneDecalLightBackend ActiveLightBackend => _decalLights == null ? SceneDecalLightBackend.Scalar : _decalLights.Backend;
        public string LightFallbackReason => _decalLights?.FallbackReason;
        public int SubmittedSurfaces { get; private set; }
        public int SubmittedDecals { get; private set; }
        public int AllocatedTargets => (_gbuffer == null ? 0 : 4) + (_scratch == null ? 0 : 4);
        public string UnavailableReason { get; private set; }
        public uint RenderSequence { get; private set; }

        public readonly struct Frame
        {
            public readonly RenderTexture albedoCoverage, normalGroup, mosDepth, emission;
            private readonly SceneDeferredCamera _owner;
            private readonly uint _sequence;
            internal Frame(SceneDeferredCamera owner)
            {
                _owner = owner; _sequence = owner.RenderSequence;
                var data = owner._output;
                albedoCoverage = data[0]; normalGroup = data[1]; mosDepth = data[2]; emission = data[3];
            }
            public bool IsCurrent => _owner != null && _owner.Current && _sequence == _owner.RenderSequence &&
                albedoCoverage != null && albedoCoverage.IsCreated();
        }

        private Camera _camera;
        private Shader _shader;
        private CommandBuffer _commands;
        private Mesh _quad;
        private readonly List<Material> _materials = new List<Material>();
        private RenderTexture[] _gbuffer, _scratch, _output;
        private RenderTexture _target;
        private int _prepared = -1, _rendered = -1, _materialCount;
        private bool Current => isActiveAndEnabled && sceneEnabled && _prepared == Time.frameCount &&
            _rendered == Time.frameCount && _output != null && _camera.targetTexture == _target && _target != null && _target.IsCreated() &&
            Created(_output) && _output[0].width == _target.width && _output[0].height == _target.height;
        public bool TryGetFrame(out Frame frame)
        { frame = default; if (!Current) return false; frame = new Frame(this); return true; }

        private void OnEnable()
        {
            _camera = GetComponent<Camera>(); _shader = Resources.Load<Shader>("SceneDeferred");
            _commands = new CommandBuffer { name = "Toolkit scene geometry / material decals / HDR lighting" };
            _camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _commands);
        }

        private void OnPreCull()
        {
            _prepared = _rendered = -1; SubmittedSurfaces = SubmittedDecals = _materialCount = 0;
            if (_commands == null) return;
            _commands.Clear(); UnavailableReason = Validate();
            if (UnavailableReason != null) { ReleaseResources(); return; }
            if (decalLighting != null && decalLighting.enabled)
            {
                if (_decalLights == null) _decalLights = new SceneDecalLightRenderer();
                if (!_decalLights.Prepare(_camera, decalLighting, out var error)) { UnavailableReason = error; ReleaseResources(); return; }
            }
            else { _decalLights?.Dispose(); _decalLights = null; }
            int count = 0;
            foreach (var decal in decals) if (decal != null && decal.enabled && HasWeight(decal)) count++;
            if (!EnsureTargets(count > 0)) { UnavailableReason = "Target creation failed"; ReleaseResources(); return; }
            var view = _camera.worldToCameraMatrix;
            var projection = GL.GetGPUProjectionMatrix(_camera.projectionMatrix, true);
            var vp = projection * view;
            foreach (var rt in _gbuffer) { _commands.SetRenderTarget(rt); _commands.ClearRenderTarget(rt == _gbuffer[0], true, Color.clear); }
            SetTargets(_gbuffer);
            foreach (var surface in surfaces)
            {
                var renderer = surface.renderer;
                if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy) continue;
                var material = NextMaterial(); BindInputs(material, surface.inputs);
                material.SetMatrix("_ViewProjection", vp); material.SetMatrix("_View", view);
                material.SetVector("_VertexScale", surface.vertexScale); material.SetFloat("_Cull", (int)surface.cull);
                material.SetFloat("_Cutoff", surface.alphaCutoff); material.SetFloat("_ReceiverGroup", surface.receiverGroup);
                _commands.DrawRenderer(renderer, material, surface.materialIndex, 0); SubmittedSurfaces++;
            }
            _output = _gbuffer;
            foreach (var decal in decals)
            {
                if (decal == null || !decal.enabled || !HasWeight(decal)) continue;
                var destination = _output == _gbuffer ? _scratch : _gbuffer;
                var material = NextMaterial(); BindInputs(material, decal.inputs); BindBuffers(material, _output);
                material.SetMatrix("_InverseViewProjection", vp.inverse); material.SetMatrix("_View", view);
                material.SetMatrix("_WorldToDecal", decal.localToWorld.inverse);
                material.SetVector("_DecalTangent", decal.localToWorld.GetColumn(0));
                material.SetVector("_DecalBitangent", decal.localToWorld.GetColumn(1));
                material.SetVector("_DecalFacing", decal.localToWorld.inverse.transpose.MultiplyVector(Vector3.back).normalized);
                material.SetFloat("_ReceiverGroup", decal.receiverGroup);
                material.SetVector("_Weights", new Vector4(decal.albedoWeight, decal.normalWeight, decal.emissionWeight, decal.heightOcclusion ? 1 : 0));
                material.SetVector("_MosWeight", decal.mosWeight);
                material.SetTexture("_HeightMap", decal.heightMap != null ? decal.heightMap : Texture2D.whiteTexture);
                material.SetVector("_HeightParameters", new Vector4(decal.height, decal.heightFade, decal.minimumFacing, 0));
                SetTargets(destination); _commands.DrawMesh(Quad(), Matrix4x4.identity, material, 0, 1);
                _output = destination; SubmittedDecals++;
            }
            var lighting = NextMaterial(); BindBuffers(lighting, _output);
            lighting.SetMatrix("_InverseViewProjection", vp.inverse); lighting.SetMatrix("_ViewProjection", vp);
            lighting.SetMatrix("_View", view); lighting.SetVector("_CameraPosition", view.inverse.MultiplyPoint(Vector3.zero));
            lighting.SetVector("_CameraForward", view.inverse.MultiplyVector(Vector3.back).normalized); lighting.SetFloat("_Orthographic", _camera.orthographic ? 1 : 0);
            lighting.SetVector("_LightDirection", lightDirection.normalized); lighting.SetVector("_LightRadiance", lightRadiance);
            lighting.SetVector("_AmbientIrradiance", ambientIrradiance);
            _decalLights?.Record(_commands, _output, _camera, Quad());
            lighting.SetFloat("_HasDecalLights", _decalLights?.Accumulation != null ? 1 : 0);
            lighting.SetTexture("_DecalLightAccumulation", _decalLights?.Accumulation != null ? (Texture)_decalLights.Accumulation : Texture2D.blackTexture);
            // Resolve replaces scene radiance once AND writes scene depth before host Forward actors.
            _commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _commands.DrawMesh(Quad(), Matrix4x4.identity, lighting, 0, 2);
            while (_materials.Count > _materialCount)
            { int last = _materials.Count - 1; Destroy(_materials[last]); _materials.RemoveAt(last); }
            _target = _camera.targetTexture; _prepared = Time.frameCount;
        }

        private void OnPostRender() { if (_prepared == Time.frameCount) { _rendered = Time.frameCount; RenderSequence++; } }

        private string Validate()
        {
            if (!sceneEnabled) return "Disabled";
            if (GraphicsSettings.currentRenderPipeline != null || _camera.actualRenderingPath != RenderingPath.Forward)
                return "Requires Built-in Forward host";
            var target = _camera.targetTexture;
            if (target == null || !target.IsCreated() || target.depth < 16 || target.dimension != TextureDimension.Tex2D ||
                target.antiAliasing != 1 || target.useDynamicScale || target.sRGB ||
                (target.format != RenderTextureFormat.ARGBHalf && target.format != RenderTextureFormat.ARGBFloat) ||
                _camera.rect != new Rect(0, 0, 1, 1) || _camera.stereoEnabled || _camera.allowDynamicResolution)
                return "Requires fixed linear HDR 2D target with depth, full viewport, no MSAA/XR";
            if (_camera.clearFlags != CameraClearFlags.SolidColor && _camera.clearFlags != CameraClearFlags.Skybox)
                return "Host must clear color and depth";
            if (sceneLayers.value == 0 || (_camera.cullingMask & sceneLayers.value) != 0)
                return "Host culling mask must exclude all owned scene layers to avoid double lighting";
            if (_shader == null || !_shader.isSupported || SystemInfo.supportedRenderTargetCount < 4 ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                return "Unsupported MRT/shader capability";
            if (!Finite(lightDirection) || lightDirection.sqrMagnitude < 1e-8f || !Positive(lightRadiance) || !Positive(ambientIrradiance) ||
                !Matrix(_camera.worldToCameraMatrix) || !Matrix(_camera.projectionMatrix)) return "Invalid camera or light inputs";
            var inverse = (GL.GetGPUProjectionMatrix(_camera.projectionMatrix, true) * _camera.worldToCameraMatrix).inverse;
            for (int z = 0; z <= 1; z++) for (int y = -1; y <= 1; y += 2) for (int x = -1; x <= 1; x += 2)
            {
                var endpoint = inverse * new Vector4(x, y, z, 1);
                if (!Finite(endpoint.w) || Mathf.Abs(endpoint.w) < 1e-8f || !Finite(new Vector3(endpoint.x, endpoint.y, endpoint.z) / endpoint.w))
                    return "Projection requires finite depth reconstruction endpoints";
            }
            if (surfaces == null || surfaces.Length == 0 || surfaces.Length > 4096 || decals == null || decals.Length > 128 || target.width > 4096 || target.height > 4096)
                return "Requires 1..4096 surfaces, at most 128 decals, target at most 4096 per axis";
            var seen = new HashSet<(Renderer, int)>();
            foreach (var surface in surfaces)
            {
                if (surface == null || surface.renderer == null || !Inputs(surface.inputs) || !Unit(surface.alphaCutoff) ||
                    surface.receiverGroup < 0 || surface.receiverGroup > 255 || (int)surface.cull < 0 || (int)surface.cull > 2 ||
                    !Finite(surface.vertexScale) || Mathf.Abs(surface.vertexScale.x * surface.vertexScale.y * surface.vertexScale.z) < 1e-8f)
                    return "Invalid surface inputs";
                var r = surface.renderer; var skin = r as SkinnedMeshRenderer; var filter = r.GetComponent<MeshFilter>();
                Mesh mesh = skin != null ? skin.sharedMesh : r is MeshRenderer && filter != null ? filter.sharedMesh : null;
                if (mesh == null || surface.materialIndex < 0 || surface.materialIndex >= mesh.subMeshCount || surface.materialIndex >= r.sharedMaterials.Length ||
                    (sceneLayers.value & (1 << r.gameObject.layer)) == 0 || !seen.Add((r, surface.materialIndex)) || !Matrix(r.localToWorldMatrix) ||
                    r.HasPropertyBlock() || !mesh.HasVertexAttribute(VertexAttribute.Normal) ||
                    (surface.inputs.normalMap != null && !mesh.HasVertexAttribute(VertexAttribute.Tangent)))
                    return "Invalid, duplicate, or unowned surface geometry";
            }
            foreach (var d in decals)
            {
                if (d == null || !d.enabled || !HasWeight(d)) continue;
                if (!Inputs(d.inputs) || !Matrix(d.localToWorld) || d.localToWorld.m30 != 0 || d.localToWorld.m31 != 0 || d.localToWorld.m32 != 0 || d.localToWorld.m33 != 1 ||
                    d.receiverGroup < 1 || d.receiverGroup > 255 ||
                    !Unit(d.albedoWeight) || !Unit(d.normalWeight) || !Unit(d.emissionWeight) || !Unit(d.mosWeight.x) || !Unit(d.mosWeight.y) || !Unit(d.mosWeight.z) ||
                    !Unit(d.height) || !Finite(d.heightFade) || d.heightFade < .0001f || !Finite(d.minimumFacing) || Mathf.Abs(d.minimumFacing) > 1)
                    return "Invalid decal inputs";
            }
            return null;
        }

        private static bool HasWeight(Decal d) => d.albedoWeight != 0 || d.normalWeight != 0 || d.emissionWeight != 0 || d.mosWeight != Vector3.zero;
        private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        private static bool Finite(Vector3 x) => Finite(x.x) && Finite(x.y) && Finite(x.z);
        private static bool Positive(Vector3 x) => Finite(x) && x.x >= 0 && x.y >= 0 && x.z >= 0 && x.x <= 65504 && x.y <= 65504 && x.z <= 65504;
        private static bool Unit(float x) => Finite(x) && x >= 0 && x <= 1;
        private static bool Matrix(Matrix4x4 m)
        { for (int i = 0; i < 16; i++) if (!Finite(m[i])) return false; return Finite(m.determinant) && Mathf.Abs(m.determinant) > 1e-12f; }
        private static bool Inputs(MaterialInputs v)
        {
            if (v == null || !Positive(v.albedo) || v.albedo.x > 1 || v.albedo.y > 1 || v.albedo.z > 1 || !Positive(v.emission) ||
                !Unit(v.mos.x) || !Unit(v.mos.y) || !Unit(v.mos.z) || !Unit(v.alpha)) return false;
            for (int i = 0; i < 4; i++) if (!Finite(v.uvST[i])) return false;
            return true;
        }

        private static void BindInputs(Material m, MaterialInputs v)
        {
            m.SetTexture("_AlbedoMap", v.albedoMap != null ? v.albedoMap : Texture2D.whiteTexture);
            m.SetTexture("_NormalMap", v.normalMap != null ? v.normalMap : Texture2D.grayTexture);
            m.SetFloat("_HasNormal", v.normalMap != null ? 1 : 0);
            m.SetTexture("_MosMap", v.mosMap != null ? v.mosMap : Texture2D.whiteTexture);
            m.SetTexture("_EmissionMap", v.emissionMap != null ? v.emissionMap : Texture2D.whiteTexture);
            m.SetVector("_UvST", v.uvST); m.SetVector("_Albedo", v.albedo); m.SetVector("_Emission", v.emission);
            m.SetVector("_Mos", v.mos); m.SetFloat("_Alpha", v.alpha);
        }
        private static void BindBuffers(Material m, RenderTexture[] buffers)
        { for (int i = 0; i < 4; i++) m.SetTexture("_G" + i, buffers[i]); }
        private Material NextMaterial()
        {
            while (_materials.Count <= _materialCount) _materials.Add(new Material(_shader) { hideFlags = HideFlags.HideAndDontSave });
            return _materials[_materialCount++];
        }
        private void SetTargets(RenderTexture[] buffers)
        {
            var targets = new RenderTargetIdentifier[4]; for (int i = 0; i < 4; i++) targets[i] = buffers[i];
            _commands.SetRenderTarget(targets, buffers[0]);
        }
        private Mesh Quad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "Toolkit deferred fullscreen quad", hideFlags = HideFlags.HideAndDontSave };
            _quad.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0), new Vector3(1, -1, 0) };
            _quad.triangles = new[] { 0, 1, 2, 0, 2, 3 }; _quad.UploadMeshData(true); return _quad;
        }
        private bool EnsureTargets(bool scratch)
        {
            var t = _camera.targetTexture;
            if (!Created(_gbuffer) || _gbuffer[0].width != t.width || _gbuffer[0].height != t.height) { ReleaseTargets(ref _gbuffer); ReleaseTargets(ref _scratch); _gbuffer = Allocate(t); }
            if (scratch && !Created(_scratch)) { ReleaseTargets(ref _scratch); _scratch = Allocate(t); }
            if (!scratch) ReleaseTargets(ref _scratch);
            return Created(_gbuffer) && (!scratch || Created(_scratch));
        }
        private static RenderTexture[] Allocate(RenderTexture target)
        {
            var result = new RenderTexture[4];
            for (int i = 0; i < 4; i++)
            {
                result[i] = new RenderTexture(target.width, target.height, i == 0 ? 24 : 0,
                    i == 2 ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) {
                    name = "Toolkit scene GBuffer " + i, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
                }; result[i].Create();
            }
            return result;
        }
        private static bool Created(RenderTexture[] array)
        { if (array == null) return false; foreach (var rt in array) if (rt == null || !rt.IsCreated()) return false; return true; }
        private static void ReleaseTargets(ref RenderTexture[] array)
        { if (array != null) foreach (var rt in array) if (rt != null) { rt.Release(); Destroy(rt); } array = null; }
        private void ReleaseResources()
        {
            _decalLights?.Dispose(); _decalLights = null;
            _output = null; ReleaseTargets(ref _gbuffer); ReleaseTargets(ref _scratch);
            foreach (var m in _materials) if (m != null) Destroy(m); _materials.Clear();
            if (_quad != null) Destroy(_quad); _quad = null;
        }
        private void OnDisable()
        {
            _prepared = _rendered = -1;
            if (_commands != null) { if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, _commands); _commands.Release(); _commands = null; }
            ReleaseResources(); SubmittedSurfaces = SubmittedDecals = 0;
        }
    }
}
