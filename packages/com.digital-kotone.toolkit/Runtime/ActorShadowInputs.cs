using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Optional full Actor opacity policy for an explicit scene shadow caster.
    /// The map is still opaque coverage, not colored transmission or camera hair fading.</summary>
    [Serializable]
    public sealed class ActorShadowCoverage
    {
        public bool alphaClip;
        public float fade = 1, textureLodBias;
    }

    /// <summary>Capture material/MPB opacity and wardrobe inputs without modifying the renderer.
    /// Refresh after material animation; geometry, bones and texture pixels remain borrowed.</summary>
    public static class ActorShadowInputs
    {
        internal static readonly string[] Reserved = {
            "_ShadowViewProjection", "_ShadowVertexScale", "_ShadowUvST", "_ShadowDepthPlane",
            "_ShadowPointOrigin", "_ShadowFar", "_ShadowAlpha", "_ShadowCutoff", "_ShadowAlphaMap",
            "_ShadowActorCoverage", "_ShadowCull"
        };
        public static bool TryCapture(Renderer[] renderers, float textureLodBias,
            out SceneShadowCaster[] casters, out string error)
        {
            casters = null; error = null;
            try
            {
                if (renderers == null || renderers.Length > 1024 || !ActorForwardParameters.Finite(textureLodBias) || Mathf.Abs(textureLodBias) > 16)
                    throw new ArgumentException("Invalid actor shadow inputs or mip bias");
                var shader = Resources.Load<Shader>("PhotoModeFallback");
                if (shader == null) throw new ArgumentException("Full Actor shader unavailable");
                var output = new List<SceneShadowCaster>(); var seen = new HashSet<Renderer>();
                var common = new MaterialPropertyBlock(); var submesh = new MaterialPropertyBlock();
                foreach (var renderer in renderers)
                {
                    if (renderer == null || !seen.Add(renderer)) throw new ArgumentException("Null or duplicate actor caster");
                    if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy || renderer.shadowCastingMode == ShadowCastingMode.Off) continue;
                    if (renderer.isPartOfStaticBatch) throw new ArgumentException("Static actor batching is unsupported");
                    var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>()?.sharedMesh : null;
                    var materials = renderer.sharedMaterials;
                    if (mesh == null || mesh.subMeshCount != materials.Length) throw new ArgumentException("Actor shadow requires matching mesh/submesh materials");
                    renderer.GetPropertyBlock(common);
                    for (int i = 0; i < materials.Length; i++)
                    {
                        var material = materials[i];
                        if (material == null || material.shader != shader) throw new ArgumentException("Requires full ActorToon caster materials");
                        renderer.GetPropertyBlock(submesh, i); var block = submesh.isEmpty ? common : submesh;
                        foreach (var key in Reserved) if (block.HasProperty(key)) throw new ArgumentException("Reserved shadow property block input: " + key);
                        float Scalar(string key)
                        {
                            float value = block.HasProperty(key) ? block.GetFloat(key) : material.GetFloat(key);
                            if (!ActorForwardParameters.Finite(value)) throw new ArgumentException("Nonfinite actor shadow material input: " + key);
                            return value;
                        }
                        Vector4 Vector(string key) => block.HasProperty(key) ? block.GetVector(key) : material.GetVector(key);
                        var scale = material.GetTextureScale("_MainTex"); var offset = material.GetTextureOffset("_MainTex");
                        Vector4 uv = block.HasProperty("_MainTex_ST") ? block.GetVector("_MainTex_ST") : new Vector4(scale.x, scale.y, offset.x, offset.y);
                        Vector4 Compose(Vector4 first, Vector4 next) => new Vector4(first.x * next.x, first.y * next.y, first.z * next.x + next.z, first.w * next.y + next.w);
                        if (Mathf.Abs(Scalar("_ShaderType") - 5) < .25f) uv = Compose(uv, Vector("_BaseMap_ST"));
                        var frame = Vector("_ActorTextureFrame");
                        if (!ActorForwardParameters.Finite(frame)) throw new ArgumentException("Invalid actor texture frame");
                        if (frame.x > 0 && frame.y > 0) uv = Compose(uv, frame);
                        var coverage = new ActorShadowCoverage { alphaClip = Scalar("_UseAlphaClip") > .5f, fade = Vector("_ActorColor").w, textureLodBias = textureLodBias };
                        var caster = new SceneShadowCaster {
                            renderer = renderer, materialIndex = i,
                            cull = renderer.shadowCastingMode == ShadowCastingMode.TwoSided ? CullMode.Off : (CullMode)Scalar("_Cull"),
                            vertexScale = Vector("_WardrobeScaleCorrection"), uvST = uv,
                            alphaMap = block.HasTexture("_MainTex") ? block.GetTexture("_MainTex") : material.GetTexture("_MainTex"),
                            alpha = Vector("_Color").w, cutoff = Scalar("_Cutoff"), actorCoverage = coverage
                        };
                        var invalid = SceneLightShadowAtlas.ValidateCaster(caster);
                        if (invalid != null) throw new ArgumentException(invalid);
                        output.Add(caster);
                        if (output.Count > 1024) throw new ArgumentException("Actor shadow draw budget exceeded");
                    }
                }
                casters = output.ToArray(); return true;
            }
            catch (Exception exception) { error = "Actor shadow capture failed: " + exception.Message; return false; }
        }
    }
}
