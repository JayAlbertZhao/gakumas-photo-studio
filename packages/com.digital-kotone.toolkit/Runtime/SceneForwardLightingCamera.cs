using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Opt-in full-resolution transparent island; never changes the host culling mask or actor shaders.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class SceneForwardLightingCamera : MonoBehaviour
    {
        public LayerMask surfaceLayers;
        public SceneForwardLightingSettings settings = new SceneForwardLightingSettings();
        public string UnavailableReason { get; private set; }
        public string FallbackReason => _lighting.FallbackReason;
        public SceneForwardLightBackend Backend => _lighting.Backend;
        public int SubmittedSurfaces { get; private set; }
        public int SubmittedLights => _lighting.SubmittedLights;
        public int CulledLights => _lighting.CulledLights;
        public int TileCount => _lighting.TileCount;
        public long GridBytes => _lighting.GridBytes;
        public int AllocatedBuffers => _lighting.AllocatedBuffers;
        public int LocalShadowMapCount => _lighting.LocalShadowMapCount;
        public int MainShadowMapCount => _lighting.MainShadowMapCount;
        public ulong RenderSequence { get; private set; }
        // Fixture-only access is internal; production performs no readback.
        internal ComputeBuffer TileBuffer => _lighting.TileBuffer;
        internal List<SceneDecalLightRenderer.LightData> LightSnapshot => _lighting.LightSnapshot;

        private readonly SceneForwardLightResources _lighting = new SceneForwardLightResources();
        private readonly List<Material> _materials = new List<Material>();
        private Camera _camera;
        private Shader _shader;
        private CommandBuffer _commands;
        private int _prepared = -1;

        private void OnEnable()
        {
            _camera = GetComponent<Camera>(); _shader = Resources.Load<Shader>("SceneForwardLighting");
            _commands = new CommandBuffer { name = "Toolkit full-resolution transparent Forward+" };
            _camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _commands);
        }

        private void OnPreCull()
        {
            if (_commands == null) return;
            _commands.Clear(); _prepared = -1; SubmittedSurfaces = 0;
            try
            {
                UnavailableReason = Validate();
                if (UnavailableReason != null) { Release(); return; }
                bool hasSurfaces = false;
                foreach (var s in settings.surfaces) hasSurfaces |= Visible(s);
                if (!hasSurfaces) { Release(); return; }
                if (!_lighting.Prepare(_camera, settings, _camera.targetTexture.width, _camera.targetTexture.height, out var error))
                { UnavailableReason = error; Release(); return; }
                Record(); _prepared = Time.frameCount;
            }
            catch (Exception error)
            {
                _commands.Clear(); UnavailableReason = "Forward+ preparation failed: " + error.GetType().Name; Release();
            }
        }

        private void Record()
        {
            _lighting.Record(_commands);
            _commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _commands.BeginSample("Toolkit Forward+ current transparent geometry");
            var view = _camera.worldToCameraMatrix;
            var vp = GL.GetGPUProjectionMatrix(_camera.projectionMatrix, true) * view;
            foreach (var s in settings.surfaces)
            {
                if (!Visible(s)) continue;
                while (_materials.Count <= SubmittedSurfaces) _materials.Add(new Material(_shader) { hideFlags = HideFlags.HideAndDontSave });
                var material = _materials[SubmittedSurfaces]; SceneDeferredCamera.BindInputs(material, s.inputs);
                material.SetFloat("_Cull", (int)s.cull); material.SetFloat("_Cutoff", s.alphaCutoff);
                material.SetFloat("_DestinationBlend", (int)(s.additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
                material.SetFloat("_Additive", s.additive ? 1 : 0);
                material.SetFloat("_ReceiverGroup", s.receiverGroup); material.SetVector("_VertexScale", s.vertexScale);
                material.SetMatrix("_ViewProjection", vp);
                _lighting.Bind(material);
                material.SetFloat("_SceneGiMode", 0);
                if (s.gi != null && !s.gi.Bind(material, s.renderer, out var error)) throw new InvalidOperationException(error);
                SceneBakedShadowInput.Bind(material, s.renderer, s.bakedShadow);
                _commands.DrawRenderer(s.renderer, material, s.submesh, 0); SubmittedSurfaces++;
            }
            _commands.EndSample("Toolkit Forward+ current transparent geometry");
            while (_materials.Count > SubmittedSurfaces)
            { int last = _materials.Count - 1; Destroy(_materials[last]); _materials.RemoveAt(last); }
        }

        private string Validate()
        {
            if (settings == null || !settings.enabled) return "Disabled";
            if (GraphicsSettings.currentRenderPipeline != null || _camera.actualRenderingPath != RenderingPath.Forward)
                return "Requires Built-in Forward host";
            var target = _camera.targetTexture;
            if (target == null || !target.IsCreated() || target.depth < 16 || target.dimension != TextureDimension.Tex2D || target.antiAliasing != 1 ||
                target.useDynamicScale || target.sRGB || (target.format != RenderTextureFormat.ARGBHalf && target.format != RenderTextureFormat.ARGBFloat) ||
                target.width > 4096 || target.height > 4096 || _camera.rect != new Rect(0, 0, 1, 1) || _camera.stereoEnabled || _camera.allowDynamicResolution)
                return "Requires fixed linear HDR target with depth, full viewport, no MSAA/XR, at most4096 per axis";
            if (_camera.clearFlags != CameraClearFlags.SolidColor && _camera.clearFlags != CameraClearFlags.Skybox) return "Host must clear color and depth";
            if (surfaceLayers.value == 0 || (_camera.cullingMask & surfaceLayers.value) != 0) return "Host culling mask must exclude owned transparent layers";
            if (_shader == null || !_shader.isSupported || SystemInfo.graphicsShaderLevel < 45) return "Forward light evaluation requires shader model4.5 structured buffers";
            if (!SceneDeferredCamera.Matrix(_camera.worldToCameraMatrix) || !SceneDeferredCamera.Matrix(_camera.projectionMatrix)) return "Invalid camera matrix";
            if ((int)settings.backend < 0 || (int)settings.backend > 2 || (settings.tileSize != 8 && settings.tileSize != 16 && settings.tileSize != 32) ||
                settings.maximumGridMiB < 1 || settings.maximumGridMiB > 128 || settings.surfaces == null || settings.surfaces.Length > SceneForwardLightingSettings.MaximumSurfaces)
                return "Invalid Forward+ backend, tiles, budget or surface collection";
            if (!Vector(settings.lightDirection, -1e6f, 1e6f) || settings.lightDirection.sqrMagnitude < 1e-8f ||
                !Vector(settings.lightRadiance, 0, 65504) || !Vector(settings.ambientIrradiance, 0, 65504) || !Range(settings.giBaseScale, 0, 4) ||
                !Range(settings.diffuseScale, 0, 4) || !Range(settings.specularScale, 0, 4) || !Range(settings.backlightScale, 0, 4) || !Range(settings.directionalGiWeight, 0, 1))
                return "Invalid Forward+ illumination";
            var seen = new HashSet<(Renderer, int)>();
            foreach (var s in settings.surfaces)
            {
                if (s == null || !s.enabled) continue;
                var r = s.renderer;
                if (r == null || !SceneDeferredCamera.Inputs(s.inputs) || !Range(s.alphaCutoff, 0, 1) || s.receiverGroup < 0 || s.receiverGroup > 255 ||
                    (int)s.cull < 0 || (int)s.cull > 2 || !Vector(s.vertexScale, -1e6f, 1e6f) ||
                    Mathf.Abs(s.vertexScale.x) < 1e-6f || Mathf.Abs(s.vertexScale.y) < 1e-6f || Mathf.Abs(s.vertexScale.z) < 1e-6f)
                    return "Invalid transparent surface inputs";
                Mesh mesh = r is SkinnedMeshRenderer skin ? skin.sharedMesh : r is MeshRenderer ? r.GetComponent<MeshFilter>()?.sharedMesh : null;
                if (mesh == null || s.submesh < 0 || s.submesh >= mesh.subMeshCount || s.submesh >= r.sharedMaterials.Length ||
                    mesh.GetTopology(s.submesh) != MeshTopology.Triangles || (surfaceLayers.value & (1 << r.gameObject.layer)) == 0 ||
                    !seen.Add((r, s.submesh)) || !SceneDeferredCamera.Matrix(r.localToWorldMatrix) || r.HasPropertyBlock() ||
                    !mesh.HasVertexAttribute(VertexAttribute.Position) || !mesh.HasVertexAttribute(VertexAttribute.Normal) ||
                    ((s.inputs.albedoMap != null || s.inputs.normalMap != null || s.inputs.mosMap != null || s.inputs.emissionMap != null) && !mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) ||
                    (s.inputs.normalMap != null && !mesh.HasVertexAttribute(VertexAttribute.Tangent)))
                    return "Invalid, duplicate or unowned transparent geometry";
                foreach (var texture in new[] { s.inputs.albedoMap, s.inputs.normalMap, s.inputs.mosMap, s.inputs.emissionMap })
                    if (texture != null && (texture.dimension != TextureDimension.Tex2D || (texture is RenderTexture rt && (!rt.IsCreated() || rt.antiAliasing != 1))))
                        return "Transparent material requires created non-MSAA2D textures";
                if (s.gi != null && !s.gi.Validate(r, mesh, out var error)) return error;
                if (s.bakedShadow != null && !s.bakedShadow.Validate(r, mesh, out var bakedError)) return bakedError;
                if (s.bakedShadow != null && s.bakedShadow.Enabled && s.bakedShadow.Resolve(r, out var bakedMap, out _, out _) && bakedMap == target)
                    return "Baked shadow input cannot alias current camera output";
            }
            return null;
        }
        private static bool Visible(SceneForwardSurface s) => s != null && s.enabled && s.renderer != null && s.renderer.enabled && !s.renderer.forceRenderingOff && s.renderer.gameObject.activeInHierarchy;
        private static bool Range(float x, float a, float b) => !float.IsNaN(x) && !float.IsInfinity(x) && x >= a && x <= b;
        private static bool Vector(Vector3 v, float a, float b) => Range(v.x, a, b) && Range(v.y, a, b) && Range(v.z, a, b);
        private void OnPostRender() { if (_prepared == Time.frameCount) RenderSequence++; }
        private void Release()
        {
            _commands?.Clear(); _prepared = -1; SubmittedSurfaces = 0;
            _lighting.Dispose();
            foreach (var material in _materials) if (material != null) Destroy(material); _materials.Clear();
        }
        private void OnDisable()
        {
            if (_camera != null && _commands != null) _camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _commands);
            Release(); _commands?.Dispose(); _commands = null; UnavailableReason = "Disabled";
        }
    }
}
