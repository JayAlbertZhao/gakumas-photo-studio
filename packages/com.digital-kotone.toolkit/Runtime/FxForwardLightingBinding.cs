using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class FxForwardLightingBinding : IDisposable
    {
        private readonly SceneForwardLightResources resources = new SceneForwardLightResources();
        private readonly bool heavy;
        public Shader SurfaceShader { get; private set; }
        public bool Active { get; private set; }
        public bool IsCreated => !Active || resources.IsCreated;
        public int BufferCount => resources.AllocatedBuffers;
        public long BufferBytes => resources.BufferBytes;
        public int SubmittedLights => resources.SubmittedLights;
        public int TileCount => resources.TileCount;
        public int ShadowMapCount => resources.LocalShadowMapCount + resources.MainShadowMapCount;
        public SceneForwardLightBackend Backend => resources.Backend;
        public string FallbackReason => resources.FallbackReason;
        public FxForwardLightingBinding(bool heavy) { this.heavy = heavy; }
        public static bool Lit(LowResolutionFxSurface surface) => surface.lighting != null && surface.lighting.enabled;
        public bool Prepare(Camera camera, int width, int height, SceneForwardLightSettings settings,
            List<LowResolutionFxSurface> surfaces, out string error)
        {
            Active = false; error = null;
            foreach (var s in surfaces)
            {
                if (!Lit(s)) continue;
                if (!ValidateSurface(s, out error)) { Dispose(); return false; }
                Active = true;
            }
            if (!Active) { Dispose(); return true; }
            if (SurfaceShader == null) SurfaceShader = Resources.Load<Shader>(heavy ? "HeavyFxLit" : "LowResolutionFxLit");
            if (SurfaceShader == null || !SurfaceShader.isSupported) { error = "Lit FX surface shader unavailable"; Dispose(); return false; }
            if (!resources.Prepare(camera, settings, width, height, out error)) { Dispose(); return false; }
            var commands = new CommandBuffer { name = "Toolkit FX current Forward+ lights / grid / shadows" };
            try { resources.Record(commands); Graphics.ExecuteCommandBuffer(commands); }
            finally { commands.Release(); }
            return true;
        }
        private static bool ValidateSurface(LowResolutionFxSurface s, out string error)
        {
            error = null; var l = s.lighting;
            if (s.blend == FxBlend.Distortion) { error = "Lit distortion requires a separate transmission model; use an unlit distortion barrier"; return false; }
            if (!SceneDeferredCamera.Inputs(l.inputs) || !Range(l.alphaCutoff, 0, 1) || l.receiverGroup < 0 || l.receiverGroup > 255)
            { error = "Invalid lit FX material/cutoff/receiver group"; return false; }
            var r = s.renderer; var mesh = s.mesh;
            if (r != null) mesh = r is SkinnedMeshRenderer skin ? skin.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || !mesh.HasVertexAttribute(VertexAttribute.Normal) ||
                (l.inputs.normalMap != null && !mesh.HasVertexAttribute(VertexAttribute.Tangent)) ||
                ((l.inputs.albedoMap != null || l.inputs.normalMap != null || l.inputs.mosMap != null || l.inputs.emissionMap != null || s.texture != null || s.radialSoftness > 0) && !mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) ||
                (s.vertexColor && !mesh.HasVertexAttribute(VertexAttribute.Color)) ||
                !SceneDeferredCamera.Matrix(r != null ? r.localToWorldMatrix : s.localToWorld) ||
                (r != null && (r.HasPropertyBlock() || s.submesh >= r.sharedMaterials.Length)))
            { error = "Lit FX requires current invertible normal/UV/tangent geometry and no property block"; return false; }
            foreach (var texture in new[] { l.inputs.albedoMap, l.inputs.normalMap, l.inputs.mosMap, l.inputs.emissionMap })
                if (texture != null && (texture.dimension != TextureDimension.Tex2D || (texture is RenderTexture rt && (!rt.IsCreated() || rt.antiAliasing != 1))))
                { error = "Lit FX material requires created non-MSAA2D textures"; return false; }
            if (l.gi != null)
            {
                if (r == null && (l.gi.source == SceneGiSource.RendererLightmap || l.gi.source == SceneGiSource.SceneProbe))
                { error = "Mesh/matrix draws require explicit lightmap or probe GI, not renderer/scene-probe lookup"; return false; }
                if (!l.gi.Validate(r, mesh, out error)) return false;
            }
            return true;
        }
        public void Bind(Material material, LowResolutionFxSurface s)
        {
            // The medium producer binds its own names directly; never retrieve
            // values from another shader variant's possibly inactive uniforms.
            resources.Bind(material);
            Keyword(material, "FX_LIT_LOCAL_SHADOWS", resources.LocalShadowMapCount > 0);
            Keyword(material, "FX_LIT_MAIN_SHADOWS", resources.MainShadowMapCount > 0);
            SceneDeferredCamera.BindInputs(material, s.lighting.inputs);
            material.SetVector("_VertexScale", Vector3.one); material.SetFloat("_Additive", 0);
            material.SetFloat("_ReceiverGroup", s.lighting.receiverGroup); material.SetFloat("_Cutoff", s.lighting.alphaCutoff);
            material.SetFloat("_SceneGiMode", 0);
            if (s.lighting.gi != null && !s.lighting.gi.Bind(material, s.renderer, out var error)) throw new InvalidOperationException(error);
        }
        private static void Keyword(Material material, string name, bool enabled)
        { if (enabled) material.EnableKeyword(name); else material.DisableKeyword(name); }
        private static bool Range(float x, float low, float high) => !float.IsNaN(x) && !float.IsInfinity(x) && x >= low && x <= high;
        public bool Owns(RenderTexture texture) => resources.Owns(texture);
        public void Dispose() { resources.Dispose(); Active = false; }
    }
}
