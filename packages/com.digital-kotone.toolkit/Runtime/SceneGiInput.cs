using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public enum SceneGiSource { None, Lightmap, RendererLightmap, Probe, SceneProbe }
    public enum SceneGiEncoding { LinearRgb, Rgbm, DoubleLdr }

    /// <summary>Borrowed precomputed unit-albedo diffuse response, without surface color or AO.</summary>
    [Serializable]
    public sealed class SceneGiInput
    {
        public SceneGiSource source;
        public Texture lightmap, directionality;
        public Vector4 lightmapST = new Vector4(1, 1, 0, 0);
        // Explicit decode contract, including RendererLightmap. Never guess from alpha/brightness.
        public SceneGiEncoding encoding;
        [Min(0)] public float decodeMultiplier = 1;
        [Min(.001f)] public float decodeExponent = 1;
        public SphericalHarmonicsL2 probe;
        public Transform probeAnchor;

        internal bool UsesLightmap => source == SceneGiSource.Lightmap || source == SceneGiSource.RendererLightmap;
        internal bool Validate(Renderer renderer, Mesh mesh, out string error)
        {
            error = null;
            if ((int)source < 0 || (int)source > 4) { error = "Invalid GI source"; return false; }
            if (source == SceneGiSource.None) return true;
            if (QualitySettings.activeColorSpace != ColorSpace.Linear) { error = "Scene GI requires a Linear project"; return false; }
            if (UsesLightmap)
            {
                if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) { error = "GI lightmap requires UV2"; return false; }
                if (!ResolveLightmap(renderer, out _, out _, out _, out error)) return false;
            }
            else if (source == SceneGiSource.SceneProbe)
            {
                if (LightmapSettings.lightProbes == null || LightmapSettings.lightProbes.count == 0)
                { error = "Scene GI probe requires baked scene probe data; no ambient fallback"; return false; }
                var anchor = probeAnchor != null ? probeAnchor : renderer.probeAnchor;
                var p = anchor != null ? anchor.position : renderer.bounds.center;
                if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) { error = "Invalid GI probe position"; return false; }
            }
            else if (!ValidProbe(probe)) { error = "Invalid explicit GI probe coefficients"; return false; }
            return true;
        }

        internal bool Bind(Material material, Renderer renderer, out string error)
        {
            try { return BindCore(material, renderer, out error); }
            catch (Exception exception) { error = "GI binding failed: " + exception.GetType().Name; return false; }
        }
        private bool BindCore(Material material, Renderer renderer, out string error)
        {
            error = null; material.SetFloat("_SceneGiMode", 0);
            if (source == SceneGiSource.None) return true;
            if (UsesLightmap)
            {
                if (!ResolveLightmap(renderer, out var color, out var direction, out var st, out error)) return false;
                material.SetFloat("_SceneGiMode", 1); material.SetTexture("_SceneGiLightmap", color);
                material.SetTexture("_SceneGiDirection", direction != null ? direction : Texture2D.grayTexture);
                material.SetFloat("_SceneGiDirectional", direction != null ? 1 : 0);
                material.SetVector("_SceneGiST", st);
                material.SetVector("_SceneGiDecode", new Vector4((int)encoding, decodeMultiplier, decodeExponent, 0));
            }
            else
            {
                var value = probe;
                if (source == SceneGiSource.SceneProbe)
                {
                    var anchor = probeAnchor != null ? probeAnchor : renderer.probeAnchor;
                    LightProbes.GetInterpolatedProbe(anchor != null ? anchor.position : renderer.bounds.center, renderer, out value);
                }
                if (!ValidProbe(value)) { error = "Invalid sampled GI probe coefficients"; return false; }
                // Unity packs its SH convention, but each owned material gets private names.
                // Command-buffer draws cannot inherit a previous renderer's unity_SH globals.
                var block = new MaterialPropertyBlock(); block.CopySHCoefficientArraysFrom(new[] { value });
                foreach (string suffix in new[] { "Ar", "Ag", "Ab", "Br", "Bg", "Bb", "C" })
                    material.SetVector("_SceneGiSH" + suffix, block.GetVectorArray("unity_SH" + suffix)[0]);
                material.SetFloat("_SceneGiMode", 2);
            }
            return true;
        }

        private bool ResolveLightmap(Renderer renderer, out Texture color, out Texture direction, out Vector4 st, out string error)
        {
            color = lightmap; direction = directionality; st = lightmapST; error = null;
            if (source == SceneGiSource.RendererLightmap)
            {
                int index = renderer.lightmapIndex; var maps = LightmapSettings.lightmaps;
                if (maps == null || index < 0 || index >= maps.Length || maps[index] == null)
                { error = "Renderer GI lightmap index is not bound"; return false; }
                color = maps[index].lightmapColor; st = renderer.lightmapScaleOffset;
                direction = LightmapSettings.lightmapsMode == LightmapsMode.CombinedDirectional ? maps[index].lightmapDir : null;
                if (LightmapSettings.lightmapsMode == LightmapsMode.CombinedDirectional && direction == null)
                { error = "Directional scene GI requires its directionality texture"; return false; }
            }
            if ((int)encoding < 0 || (int)encoding > 2 || !InRange(decodeMultiplier, 0, 65504) || !InRange(decodeExponent, .001f, 16) ||
                !TextureReady(color) || (direction != null && !TextureReady(direction)))
            { error = "Invalid GI lightmap texture or decode contract"; return false; }
            for (int i = 0; i < 4; i++) if (!Finite(st[i])) { error = "Invalid GI lightmap UV transform"; return false; }
            return true;
        }
        private static bool TextureReady(Texture t) => t != null && t.dimension == TextureDimension.Tex2D &&
            (!(t is RenderTexture r) || (r.IsCreated() && r.antiAliasing == 1));
        private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        private static bool InRange(float x, float min, float max) => Finite(x) && x >= min && x <= max;
        private static bool ValidProbe(SphericalHarmonicsL2 p)
        { for (int c = 0; c < 3; c++) for (int i = 0; i < 9; i++) if (!InRange(p[c, i], -65504, 65504)) return false; return true; }
    }
}
