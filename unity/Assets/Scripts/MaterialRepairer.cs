using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    public static class MaterialRepairer
    {
        private static readonly HashSet<string> LoggedActorMaterials = new HashSet<string>(StringComparer.Ordinal);
        private static readonly string[] BaseTextureNames =
        {
            "_BaseMap", "_MainTex", "_BaseColorMap", "_BaseTex", "_Albedo", "_ColorTex"
        };

        public static int RepairErrorMaterials(GameObject root)
        {
            Shader toon = FallbackShader();
            if (toon == null) return 0;

            int repaired = 0;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.receiveShadows = true;
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int index = 0; index < materials.Length; index++)
                {
                    Material source = materials[index];
                    if (ShouldKeepOriginal(source)) continue;
                    if (!NeedsFallback(source)) continue;

                    Material replacement = new Material(toon)
                    {
                        name = (source == null ? "missing" : source.name) + "__photo-toon"
                    };
                    ConfigureFromSource(source, replacement);
                    materials[index] = replacement;
                    changed = true;
                    repaired++;
                }
                if (changed) renderer.sharedMaterials = materials;

                // The captured 4096x4096 shadow pass contains exactly five Actor
                // draws. Their index counts map one-for-one to the two body
                // submeshes, one hair submesh and two hair-prop submeshes. The
                // nine-submesh VLSkinningRenderer face is absent, so allowing it
                // to cast in Unity adds a non-original face/eye shell to the
                // self-shadow atlas. Shader type 9 is unique to that renderer in
                // the extracted fktn set and is therefore a stable selection key.
                bool isCapturedFaceRenderer = false;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null && material.HasProperty("_ShaderType") &&
                        Mathf.Abs(material.GetFloat("_ShaderType") - 9f) < 0.25f)
                    {
                        isCapturedFaceRenderer = true;
                        break;
                    }
                }
                renderer.shadowCastingMode = isCapturedFaceRenderer
                    ? UnityEngine.Rendering.ShadowCastingMode.Off
                    : UnityEngine.Rendering.ShadowCastingMode.On;
                if (isCapturedFaceRenderer)
                    Debug.Log("[PhotoMode] Captured caster contract: disabled face renderer " + renderer.name);
            }
            return repaired;
        }

        public static Shader FallbackShader()
        {
            Shader shader = Resources.Load<Shader>("PhotoModeFallback");
            return shader != null ? shader : Shader.Find("Diffuse");
        }

        private static bool ShouldKeepOriginal(Material material)
        {
            bool requested = string.Equals(
                                 Environment.GetEnvironmentVariable("GAKUMAS_USE_ORIGINAL_SHADER"),
                                 "1",
                                 StringComparison.Ordinal) ||
                             Array.IndexOf(Environment.GetCommandLineArgs(), "--original-shader") >= 0;
            if (!requested || material == null || material.shader == null || !material.shader.isSupported)
            {
                return false;
            }
            return material.shader.name.StartsWith("Campus/Actor/", StringComparison.OrdinalIgnoreCase);
        }

        private static void ConfigureFromSource(Material source, Material target)
        {
            if (source == null) return;

            // This environment prop arrives through an unsupported Campus shader,
            // so Unity's Material.HasProperty cannot expose its serialized values.
            // The dependency bundle stores BaseColor=(0,0,0,240/255),
            // SrcAlpha/OneMinusSrcAlpha, ZWrite=0 and queue=3600 verbatim.
            bool isCityBlackRoom = source.name.StartsWith(
                "m_city_black_00_uni00", StringComparison.OrdinalIgnoreCase);

            CopyFirstTexture(source, target, BaseTextureNames, "_MainTex");
            CopyTexture(source, target, "_ShadeMap", "_ShadeTex", Texture2D.whiteTexture);
            CopyTexture(source, target, "_DefMap", "_DefTex", Texture2D.blackTexture);
            CopyTexture(source, target, "_RampMap", "_RampTex", Texture2D.whiteTexture);
            CopyTexture(source, target, "_LayerMap", "_LayerTex", Texture2D.blackTexture);
            CopyTexture(source, target, "_HighlightMap", "_HighlightTex", Texture2D.blackTexture);
            CopyTexture(source, target, "_RampAddMap", "_RampAddTex", Texture2D.blackTexture);
            bool hasBump = CopyOptionalTexture(source, target, "_BumpMap", "_BumpMap");
            bool hasAnisotropic = CopyOptionalTexture(source, target, "_AnisotropicMap", "_AnisotropicMap");
            bool hasReflection = CopyOptionalTexture(source, target, "_ReflectionSphereMap", "_ReflectionSphereMap");
            bool hasEmission = CopyOptionalTexture(source, target, "_EmissionMap", "_EmissionMap");

            // Copy the serialized float4 literally.  SetColor on a ShaderLab
            // Color property is color-space transformed in a Linear project;
            // the captured Actor CB2 stores this value without that transform
            // (notably m_ehl = 2.996078), so use a Vector property/API pair.
            target.SetVector("_Color", isCityBlackRoom
                ? new Vector4(0f, 0f, 0f, 240f / 255f)
                : source.HasProperty("_BaseColor")
                    ? source.GetVector("_BaseColor")
                    : Vector4.one);
            float shaderType = source.HasProperty("_ShaderType") ? source.GetFloat("_ShaderType") : 0f;
            target.SetFloat("_ShaderType", shaderType);
            float type1Variant = source.name.StartsWith("m_bdyco", StringComparison.OrdinalIgnoreCase) ? 1f :
                source.name.StartsWith("m_hirco", StringComparison.OrdinalIgnoreCase) ? 2f : 0f;
            target.SetFloat("_CapturedType1Variant", type1Variant);
            string materialKey = source.name + "|" + shaderType.ToString("0.###");
            bool logMaterialContract = LoggedActorMaterials.Add(materialKey);
            if (logMaterialContract)
                Debug.Log(string.Format("[PhotoMode] Actor material input: name={0} shaderType={1:0.###}", source.name, shaderType));
            float enableLayer = source.HasProperty("_EnableLayerMap") ? source.GetFloat("_EnableLayerMap") : 0f;
            float layerWeight = source.HasProperty("_LayerWeight") ? source.GetFloat("_LayerWeight") : 0f;
            target.SetFloat("_EnableLayer", enableLayer);
            target.SetFloat("_LayerWeight", layerWeight);
            if (Mathf.Abs(shaderType - 9f) < 0.25f)
            {
                Texture layerTexture = source.HasProperty("_LayerMap") ? source.GetTexture("_LayerMap") : null;
                Debug.Log(string.Format(
                    "[PhotoMode] Face material input: name={0} enableLayer={1:0.###} layerWeight={2:0.###} layer={3}",
                    source.name, enableLayer, layerWeight, layerTexture == null ? "null" : layerTexture.name));
            }
            target.SetFloat("_VertexColor", source.HasProperty("_VertexColor") ? source.GetFloat("_VertexColor") : 1f);
            target.SetFloat("_UseBump", hasBump ? 1f : 0f);
            target.SetFloat("_UseAnisotropic", hasAnisotropic ? 1f : 0f);
            target.SetFloat("_UseReflection", hasReflection ? 1f : 0f);
            target.SetFloat("_UseEmission", hasEmission && source.HasProperty("_EnableEmission") && source.GetFloat("_EnableEmission") > 0.5f ? 1f : 0f);
            CopyFloat(source, target, "_BumpScale", 1f);
            CopyFloat(source, target, "_AnisotropicScale", 0f);
            CopyVector(source, target, "_DefValue", new Vector4(0.5f, 0f, 1f, 0f));
            CopyVector(source, target, "_SpecularThreshold", new Vector4(0.6f, 0.05f, 0f, 0f));
            CopyColor(source, target, "_RampAddColor", Color.white);
            CopyColor(source, target, "_RimColor", Color.clear);
            CopyColor(source, target, "_EmissionColor", Color.clear);
            float cull = source.HasProperty("_Cull") ? source.GetFloat("_Cull") : 2f;
            target.SetFloat("_Cull", cull);
            bool alphaTestKeyword = source.IsKeywordEnabled("_ALPHATEST_ON");
            float useAlphaClip = alphaTestKeyword ||
                                 (source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip") > 0.5f)
                ? 1f
                : 0f;
            target.SetFloat("_UseAlphaClip", useAlphaClip);
            // The bound alpha-test variants compare against the literal 0.33.
            // Serialized source materials may expose a default/unused _Cutoff
            // (commonly zero); copying that value made the no-cull bdyco/hirco
            // shadow casters fill transparent card interiors that are clear in
            // the archived R16 map. Honor the active shader contract whenever
            // alpha clipping is enabled.
            target.SetFloat("_Cutoff", useAlphaClip > 0.5f
                ? 0.33f
                : (source.HasProperty("_Cutoff") ? source.GetFloat("_Cutoff") : 0.33f));

            CopyFloat(source, target, "_SrcBlend", 1f);
            CopyFloat(source, target, "_DstBlend", 0f);
            // A zero source with One/One blending preserves the destination
            // while the draw still executes its alpha clip, depth and stencil
            // writes. This mirrors the GPA discardtype1 colour suppression
            // closely enough for paired pre-post HDR contribution analysis.
            bool suppressType1Color = Mathf.Abs(shaderType - 1f) < 0.25f &&
                (Array.IndexOf(Environment.GetCommandLineArgs(), "--discard-type1-color") >= 0 ||
                 (type1Variant == 1f && Array.IndexOf(Environment.GetCommandLineArgs(), "--discard-type1-body-color") >= 0) ||
                 (type1Variant == 2f && Array.IndexOf(Environment.GetCommandLineArgs(), "--discard-type1-hair-color") >= 0));
            if (suppressType1Color)
            {
                target.SetFloat("_SrcBlend", 1f);
                target.SetFloat("_DstBlend", 1f);
            }
            // Offline GPA can replay the original type-4 PS with blending
            // disabled, exposing its premultiplied source RGB independently of
            // the eye-white destination.  Mirror that controlled probe locally;
            // the shader still premultiplies type 4 before the One/Zero write.
            if (Mathf.Abs(shaderType - 4f) < 0.25f &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "--type4-replace-blend") >= 0)
                target.SetFloat("_DstBlend", 0f);
            CopyFloat(source, target, "_SrcAlphaBlend", 1f);
            CopyFloat(source, target, "_DstAlphaBlend", 0f);
            if (suppressType1Color)
            {
                target.SetFloat("_SrcAlphaBlend", 0f);
                target.SetFloat("_DstAlphaBlend", 1f);
            }
            CopyFloat(source, target, "_ZWrite", 1f);
            CopyFloat(source, target, "_ColorMask", 15f);
            CopyFloat(source, target, "_StencilRef", 64f);
            CopyFloat(source, target, "_StencilReadMask", 108f);
            CopyFloat(source, target, "_StencilWriteMask", 96f);
            CopyFloat(source, target, "_StencilComp", 8f);
            CopyFloat(source, target, "_StencilPass", 2f);
            target.renderQueue = source.renderQueue;
            if (isCityBlackRoom)
            {
                target.SetFloat("_SrcBlend", 5f);
                target.SetFloat("_DstBlend", 10f);
                target.SetFloat("_SrcAlphaBlend", 0f);
                target.SetFloat("_DstAlphaBlend", 10f);
                target.SetFloat("_ZWrite", 0f);
                target.SetFloat("_Cull", 2f);
                target.SetFloat("_ColorMask", 15f);
                target.renderQueue = 3600;
                Debug.Log(
                    "[StoryProp] Applied serialized city-black material: " +
                    "color=(0,0,0,0.9411765) src=5 dst=10 alphaSrc=0 alphaDst=10 zwrite=0 queue=3600");
            }
            if (logMaterialContract)
            {
                Debug.Log(string.Format(
                    "[PhotoMode] Actor material contract: name={0} type={1:0.###} bump={2} anisotropic={3} reflection={4} emission={5} alphaClip={6:0} cull={7:0} src={8:0} dst={9:0} zwrite={10:0} queue={11} base={12} shade={13} def={14} ramp={15} rampAdd={16} highlight={17} color={18}",
                    source.name, shaderType, hasBump, hasAnisotropic, hasReflection, hasEmission,
                    useAlphaClip, cull, target.GetFloat("_SrcBlend"), target.GetFloat("_DstBlend"),
                    target.GetFloat("_ZWrite"), target.renderQueue,
                    TextureName(target, "_MainTex"), TextureName(target, "_ShadeTex"),
                    TextureName(target, "_DefTex"), TextureName(target, "_RampTex"),
                    TextureName(target, "_RampAddTex"), TextureName(target, "_HighlightTex"), target.GetVector("_Color")));
                Debug.Log(string.Format(
                    "[PhotoMode] Actor texture color-space: name={0} base={1} shade={2} def={3} ramp={4} rampAdd={5}",
                    source.name,
                    TextureContract(target, "_MainTex"), TextureContract(target, "_ShadeTex"),
                    TextureContract(target, "_DefTex"), TextureContract(target, "_RampTex"),
                    TextureContract(target, "_RampAddTex")));
            }
            if (Mathf.Abs(shaderType - 8f) < 0.25f || Mathf.Abs(shaderType - 4f) < 0.25f)
            {
                Debug.Log(string.Format(
                    "[PhotoMode] Actor alpha contract: name={0} type={1:0} src={2:0} dst={3:0} zwrite={4:0} queue={5}",
                    source.name, shaderType, target.GetFloat("_SrcBlend"), target.GetFloat("_DstBlend"),
                    target.GetFloat("_ZWrite"), target.renderQueue));
            }

            // The captured edge-resolve draw is identity for this frame and the Actor
            // sentinel union already explains 99.09% of Actor coverage. The visible
            // strokes therefore come from Base/Shade/Definition maps plus explicit
            // eye/eyelash geometry; do not add a geometry shell or post-process outline.
        }

        private static void CopyFirstTexture(Material source, Material target, string[] sourceNames, string targetName)
        {
            foreach (string property in sourceNames)
            {
                if (!source.HasProperty(property)) continue;
                Texture texture = source.GetTexture(property);
                if (texture == null) continue;
                target.SetTexture(targetName, texture);
                target.SetTextureScale(targetName, source.GetTextureScale(property));
                target.SetTextureOffset(targetName, source.GetTextureOffset(property));
                return;
            }
        }

        private static void CopyTexture(Material source, Material target, string sourceName, string targetName, Texture fallback)
        {
            Texture texture = source.HasProperty(sourceName) ? source.GetTexture(sourceName) : null;
            target.SetTexture(targetName, texture != null ? texture : fallback);
        }

        private static bool CopyOptionalTexture(Material source, Material target, string sourceName, string targetName)
        {
            if (!source.HasProperty(sourceName)) return false;
            Texture texture = source.GetTexture(sourceName);
            if (texture == null) return false;
            target.SetTexture(targetName, texture);
            target.SetTextureScale(targetName, source.GetTextureScale(sourceName));
            target.SetTextureOffset(targetName, source.GetTextureOffset(sourceName));
            return texture != Texture2D.blackTexture && texture != Texture2D.whiteTexture;
        }

        private static string TextureName(Material material, string propertyName)
        {
            Texture texture = material.HasProperty(propertyName) ? material.GetTexture(propertyName) : null;
            return texture == null ? "null" : texture.name;
        }

        private static string TextureContract(Material material, string propertyName)
        {
            Texture texture = material.HasProperty(propertyName) ? material.GetTexture(propertyName) : null;
            if (texture == null) return "null";
            Texture2D texture2D = texture as Texture2D;
            return string.Format(
                "{0}[{1},sRGB={2},filter={3},wrap={4}/{5},bias={6:0.###},aniso={7},mips={8}]",
                texture.name, texture.graphicsFormat, texture.isDataSRGB,
                texture.filterMode, texture.wrapModeU, texture.wrapModeV,
                texture.mipMapBias, texture.anisoLevel,
                texture2D == null ? 1 : texture2D.mipmapCount);
        }

        private static void CopyVector(Material source, Material target, string propertyName, Vector4 fallback)
        {
            target.SetVector(propertyName, source.HasProperty(propertyName) ? source.GetVector(propertyName) : fallback);
        }

        private static void CopyColor(Material source, Material target, string propertyName, Color fallback)
        {
            target.SetColor(propertyName, source.HasProperty(propertyName) ? source.GetColor(propertyName) : fallback);
        }

        private static void CopyFloat(Material source, Material target, string propertyName, float fallback)
        {
            target.SetFloat(propertyName, source.HasProperty(propertyName) ? source.GetFloat(propertyName) : fallback);
        }

        private static bool NeedsFallback(Material material)
        {
            if (material == null || material.shader == null || !material.shader.isSupported) return true;
            string shaderName = material.shader.name;
            if (shaderName.StartsWith("GakumasPhotoMode/", StringComparison.OrdinalIgnoreCase)) return false;
            return shaderName.Contains("InternalErrorShader") ||
                   shaderName.StartsWith("Campus", StringComparison.OrdinalIgnoreCase) ||
                   shaderName.IndexOf("Actor", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
