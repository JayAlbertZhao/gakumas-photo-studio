using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Owned reduced reflection materials for explicitly selected ActorToon
    /// renderers. Refresh after animation and before Camera.Render, then assign
    /// Draws to PlanarReflection. Never changes source materials/property blocks.
    /// </summary>
    public sealed class ActorPlanarCaptureSet : IDisposable
    {
        private sealed class Entry
        {
            public Material color, cover;
            public bool used;
        }
        private readonly Dictionary<long, Entry> _entries = new Dictionary<long, Entry>();
        private readonly MaterialPropertyBlock _rendererBlock = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _materialBlock = new MaterialPropertyBlock();
        private Shader _shader;
        private bool _disposed;
        public PlanarReflection.Draw[] Draws { get; private set; } = Array.Empty<PlanarReflection.Draw>();
        public int MaterialCount { get; private set; }

        public bool TryRefresh(Renderer[] renderers, Matrix4x4 reflectedView, ActorPlanarLighting lighting, out string error)
        {
            error = null;
            if (_disposed) { error = "Capture set disposed"; return false; }
            if (renderers == null || lighting == null || !lighting.IsValid) return Fail("Invalid capture inputs", out error);
            for (int i = 0; i < 16; i++) if (!ActorPlanarLighting.Range(reflectedView[i], -1e8f, 1e8f)) return Fail("Invalid reflected view", out error);
            if (_shader == null) _shader = Resources.Load<Shader>("ActorPlanarCapture");
            if (_shader == null || !_shader.isSupported) return Fail("Reduced character shader unavailable", out error);
            foreach (Entry entry in _entries.Values) entry.used = false;
            var draws = new List<PlanarReflection.Draw>(); var covers = new List<PlanarReflection.Draw>();
            var queues = new Dictionary<Material, int>();
            var seen = new HashSet<int>();
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy) continue;
                if (!seen.Add(renderer.GetInstanceID())) return Fail("Duplicate capture renderer", out error);
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) return Fail("Requires mesh or skinned renderer", out error);
                renderer.GetPropertyBlock(_rendererBlock);
                Material[] materials = renderer.sharedMaterials;
                for (int index = 0; index < materials.Length; index++)
                {
                    Material source = materials[index];
                    if (source == null || source.shader == null || source.shader.name != "GakumasPhotoMode/ActorToon")
                        return Fail("Requires this toolkit's ActorToon source materials", out error);
                    renderer.GetPropertyBlock(_materialBlock, index);
                    // Unity uses the per-material block instead of the per-renderer
                    // block when both exist, not a property-by-property merge.
                    MaterialPropertyBlock block = _materialBlock.isEmpty ? _rendererBlock : _materialBlock;
                    float F(string name, float fallback) => block.HasProperty(name) ? block.GetFloat(name) :
                        source.HasProperty(name) ? source.GetFloat(name) : fallback;
                    Vector4 V(string name, Vector4 fallback) => block.HasProperty(name) ? block.GetVector(name) :
                        source.HasProperty(name) ? source.GetVector(name) : fallback;
                    Texture T(string name, Texture fallback) => (block.HasProperty(name) ? block.GetTexture(name) :
                        source.HasProperty(name) ? source.GetTexture(name) : null) ?? fallback;
                    float kind = F("_ShaderType", 0);
                    if (Array.IndexOf(new[] { 0f, 1f, 2f, 3f, 4f, 5f, 6f, 8f, 9f }, kind) < 0)
                        return Fail("Unsupported ActorToon type", out error);
                    float src = source.GetFloat("_SrcBlend"), dst = source.GetFloat("_DstBlend");
                    float colorMask = source.GetFloat("_ColorMask");
                    if (colorMask != 14 && colorMask != 15) return Fail("Requires all RGB color channels", out error);
                    if (!((src == 1 && (dst == 0 || dst == 1 || dst == 10)) || (src == 5 && (dst == 1 || dst == 10))))
                        return Fail("Unsupported character blend equation", out error);
                    var scale = V("_WardrobeScaleCorrection", Vector4.one);
                    if (!ActorPlanarLighting.Finite(scale) || Mathf.Min(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)) < .0001f)
                        return Fail("Invalid character vertex scale", out error);
                    var surface = new SceneDepthData.Surface { renderer = renderer, materialIndex = index,
                        vertexScale = scale, cull = (CullMode)(int)source.GetFloat("_Cull") };
                    if (!SceneDepthData.ValidSurface(surface)) return Fail("Invalid character submesh", out error);
                    long key = ((long)renderer.GetInstanceID() << 32) | (uint)index;
                    if (!_entries.TryGetValue(key, out var entry)) { entry = new Entry(); _entries.Add(key, entry); }
                    entry.used = true;
                    if (entry.color == null) entry.color = NewMaterial();
                    Material target = entry.color;
                    string[] inputs = { "_MainTex", "_ShadeTex", "_DefTex", "_RampTex", "_LayerTex", "_HighlightTex", "_RampAddTex", "_EmissionMap" };
                    string[] outputs = { "_CapMain", "_CapShade", "_CapDef", "_CapRamp", "_CapLayer", "_CapHighlight", "_CapRampAdd", "_CapEmission" };
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        Texture texture = T(inputs[i], i < 4 ? Texture2D.whiteTexture : Texture2D.blackTexture);
                        if (texture.dimension != TextureDimension.Tex2D) return Fail("Character maps must be 2D", out error);
                        target.SetTexture(outputs[i], texture);
                    }
                    Vector4 mainST = new Vector4(source.GetTextureScale("_MainTex").x, source.GetTextureScale("_MainTex").y,
                        source.GetTextureOffset("_MainTex").x, source.GetTextureOffset("_MainTex").y);
                    target.SetVector("_CapMainST", V("_MainTex_ST", mainST));
                    target.SetVector("_CapBaseST", V("_BaseMap_ST", new Vector4(1, 1, 0, 0)));
                    target.SetVector("_CapAtlas", V("_ActorTextureFrame", Vector4.zero));
                    target.SetVector("_CapTint", V("_Color", Vector4.one));
                    target.SetVector("_CapActorTint", V("_ActorColor", Vector4.one));
                    target.SetVector("_CapDefValue", V("_DefValue", new Vector4(.5f, 0, 1, 0)));
                    target.SetVector("_CapThreshold", V("_SpecularThreshold", new Vector4(.6f, .05f, 0, 0)));
                    target.SetVector("_CapHairFade", V("_HairFadeParameters", new Vector4(.75f, 2, .4f, 4)));
                    foreach (var pair in new[] { new[] { "_RampAddColor", "_CapRampAddTint" }, new[] { "_EmissionColor", "_CapEmissionTint" } })
                    {
                        // Blocks already store the shader-space value, including
                        // SetColor conversion. GetVector also preserves an explicit
                        // linear SetVector override without another gamma transform.
                        Color value = source.GetColor(pair[0]);
                        target.SetVector(pair[1], block.HasProperty(pair[0]) ? block.GetVector(pair[0]) :
                            (Vector4)(QualitySettings.activeColorSpace == ColorSpace.Linear ? value.linear : value));
                    }
                    target.SetVector("_CapScale", scale);
                    target.SetFloat("_CapType", kind); target.SetFloat("_CapVariant", F("_CapturedType1Variant", 0));
                    target.SetFloat("_CapUseDef", F("_DisableDefMap", 0) > .5f ? 0 : 1);
                    target.SetFloat("_CapLayerEnabled", F("_EnableLayer", 0)); target.SetFloat("_CapLayerWeight", F("_LayerWeight", 0));
                    target.SetFloat("_CapUseVertex", F("_VertexColor", 1)); target.SetFloat("_CapAlphaClip", F("_UseAlphaClip", 0));
                    target.SetFloat("_CapCutoff", F("_Cutoff", .33f)); target.SetFloat("_CapEmissionEnabled", F("_UseEmission", 0));
                    target.SetFloat("_CapHairCover", 0);
                    target.SetFloat("_CapOpacityMode", src == 5 || kind == 4 || (kind == 8 && src == 1 && dst == 10) ? 1 : 0);
                    target.SetFloat("_CapPremultiply", kind == 4 || (kind == 8 && src == 1 && dst == 10) ? 1 : 0);
                    foreach (var pair in new[] { new[] { "_Cull", "_CapCull" }, new[] { "_ZWrite", "_CapZWrite" },
                        new[] { "_SrcBlend", "_CapSrcBlend" }, new[] { "_DstBlend", "_CapDstBlend" },
                        new[] { "_StencilRef", "_CapStencilRef" }, new[] { "_StencilReadMask", "_CapStencilRead" },
                        new[] { "_StencilWriteMask", "_CapStencilWrite" }, new[] { "_StencilComp", "_CapStencilComp" }, new[] { "_StencilPass", "_CapStencilPass" } })
                        target.SetFloat(pair[1], source.GetFloat(pair[0]));
                    target.SetVector("_CapKey", lighting.lightColor); target.SetVector("_CapAmbient", lighting.ambientColor);
                    target.SetVector("_CapLight", lighting.lightDirection.normalized); target.SetVector("_CapShadeTint", lighting.shadeTint);
                    target.SetVector("_CapShadeAdditive", lighting.shadeAdditive); target.SetFloat("_CapRampOffset", lighting.rampOffset);
                    target.SetFloat("_CapLodBias", lighting.textureLodBias); target.SetFloat("_CapSkinSaturation", lighting.skinSaturation);
                    Transform head = lighting.head;
                    target.SetVector("_CapHeadRight", head != null ? -head.right : Vector3.left);
                    target.SetVector("_CapHeadUp", head != null ? head.up : Vector3.up);
                    target.SetVector("_CapHeadForward", head != null ? head.forward : Vector3.forward);
                    foreach (string name in new[] { "_CapMainST", "_CapBaseST", "_CapAtlas", "_CapTint", "_CapActorTint", "_CapDefValue",
                        "_CapThreshold", "_CapHairFade", "_CapRampAddTint", "_CapEmissionTint", "_CapHeadRight", "_CapHeadUp", "_CapHeadForward" })
                    {
                        Vector4 value = target.GetVector(name);
                        for (int c = 0; c < 4; c++) if (!ActorPlanarLighting.Range(value[c], -1e8f, 1e8f)) return Fail("Nonfinite character input", out error);
                    }
                    foreach (string name in new[] { "_CapVariant", "_CapLayerEnabled", "_CapLayerWeight", "_CapUseVertex", "_CapAlphaClip", "_CapCutoff", "_CapEmissionEnabled" })
                        if (!ActorPlanarLighting.Range(target.GetFloat(name), 0, name == "_CapVariant" ? 2 : 1)) return Fail("Invalid character scalar", out error);
                    foreach (string name in new[] { "_CapCull", "_CapZWrite", "_CapStencilRef", "_CapStencilRead", "_CapStencilWrite", "_CapStencilComp", "_CapStencilPass" })
                    {
                        float value = target.GetFloat(name);
                        float maximum = name == "_CapCull" ? 2 : name == "_CapZWrite" ? 1 : name == "_CapStencilComp" ? 8 : name == "_CapStencilPass" ? 7 : 255;
                        if (!ActorPlanarLighting.Range(value, 0, maximum) || value != Mathf.Round(value)) return Fail("Invalid character render state", out error);
                    }
                    draws.Add(Draw(surface, target)); queues.Add(target, source.renderQueue);
                    if (kind == 8 && source.GetShaderPassEnabled("ActorHairCover"))
                    {
                        if (entry.cover == null) entry.cover = NewMaterial();
                        entry.cover.CopyPropertiesFromMaterial(target);
                        entry.cover.SetFloat("_CapHairCover", 1); entry.cover.SetFloat("_CapPremultiply", 0);
                        entry.cover.SetFloat("_CapSrcBlend", 5); entry.cover.SetFloat("_CapDstBlend", 10);
                        entry.cover.SetFloat("_CapStencilWrite", 0); entry.cover.SetFloat("_CapStencilComp", 2); entry.cover.SetFloat("_CapStencilPass", 0);
                        covers.Add(Draw(surface, entry.cover));
                    }
                    else Release(ref entry.cover);
                }
            }
            draws.Sort((a, b) => {
                int order = queues[a.material].CompareTo(queues[b.material]); if (order != 0) return order;
                if (queues[a.material] >= 2500)
                {
                    order = reflectedView.MultiplyPoint(a.surface.renderer.bounds.center).z.CompareTo(reflectedView.MultiplyPoint(b.surface.renderer.bounds.center).z);
                    if (order != 0) return order;
                }
                order = a.surface.renderer.GetInstanceID().CompareTo(b.surface.renderer.GetInstanceID());
                return order != 0 ? order : a.surface.materialIndex.CompareTo(b.surface.materialIndex);
            });
            draws.AddRange(covers); Draws = draws.ToArray();
            var unused = new List<long>(); MaterialCount = 0;
            foreach (var pair in _entries)
            {
                if (!pair.Value.used) { Release(ref pair.Value.color); Release(ref pair.Value.cover); unused.Add(pair.Key); }
                else MaterialCount += pair.Value.cover != null ? 2 : 1;
            }
            foreach (long key in unused) _entries.Remove(key);
            return true;
        }

        private Material NewMaterial() => new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
        private static PlanarReflection.Draw Draw(SceneDepthData.Surface surface, Material material) =>
            new PlanarReflection.Draw { surface = surface, material = material, shaderPass = 0, coverageMaterial = material, coverageShaderPass = 1 };
        private bool Fail(string message, out string error) { Clear(); error = message; return false; }
        private void Clear()
        {
            foreach (Entry entry in _entries.Values) { Release(ref entry.color); Release(ref entry.cover); }
            _entries.Clear(); Draws = Array.Empty<PlanarReflection.Draw>(); MaterialCount = 0;
        }
        private static void Release(ref Material material)
        { if (material != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(material); else UnityEngine.Object.DestroyImmediate(material); material = null; } }
        public void Dispose() { Clear(); _disposed = true; }
    }
}
