using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public enum SceneBakedShadowSource { None, Constant, Texture, RendererLightmap }
    public enum SceneBakedShadowChannel { None, R, G, B, A }

    /// <summary>Borrowed per-light visibility, independent of indirect GI and material color.</summary>
    [Serializable]
    public sealed class SceneBakedShadowInput
    {
        public SceneBakedShadowSource source;
        public Vector4 visibility = Vector4.one;
        public Texture texture;
        public Vector4 uvST = new Vector4(1, 1, 0, 0);
        // Only the compact Deferred output is quantized. Forward samples directly.
        public bool dither = true;
        internal bool Enabled => source != SceneBakedShadowSource.None;
        internal bool Validate(Renderer renderer, Mesh mesh, out string error)
        {
            error = null;
            if ((int)source < 0 || (int)source > 3) { error = "Invalid baked shadow source"; return false; }
            if (!Enabled) return true;
            if (source == SceneBakedShadowSource.Constant)
            {
                for (int i = 0; i < 4; i++) if (!Unit(visibility[i])) { error = "Baked visibility must be finite0..1"; return false; }
                return true;
            }
            if (mesh == null || !mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) { error = "Baked shadow texture requires UV2"; return false; }
            return Resolve(renderer, out _, out _, out error);
        }
        internal bool Resolve(Renderer renderer, out Texture map, out Vector4 st, out string error)
        {
            map = texture; st = uvST; error = null;
            if (source == SceneBakedShadowSource.None || source == SceneBakedShadowSource.Constant) { map = null; return true; }
            if (source == SceneBakedShadowSource.RendererLightmap)
            {
                var maps = LightmapSettings.lightmaps; int index = renderer != null ? renderer.lightmapIndex : -1;
                if (maps == null || index < 0 || index >= maps.Length || maps[index] == null)
                { error = "Renderer baked shadow lightmap index is not bound; explicit mesh requires explicit inputs"; return false; }
                map = maps[index].shadowMask; st = renderer.lightmapScaleOffset;
            }
            if (map == null || map.dimension != TextureDimension.Tex2D || GraphicsFormatUtility.IsSRGBFormat(map.graphicsFormat) ||
                (map is RenderTexture rt && (!rt.IsCreated() || rt.antiAliasing != 1)))
            { error = "Baked shadow requires a created linear non-MSAA2D texture; no missing-mask fallback"; return false; }
            for (int i = 0; i < 4; i++) if (!Finite(st[i])) { error = "Invalid baked shadow UV transform"; return false; }
            return true;
        }
        internal static void Bind(Material material, Renderer renderer, SceneBakedShadowInput input, bool compactOutput = false)
        {
            bool enabled = input != null && input.Enabled;
            if (enabled || compactOutput) material.EnableKeyword("SCENE_BAKED_SHADOW_INPUT"); else material.DisableKeyword("SCENE_BAKED_SHADOW_INPUT");
            material.SetFloat("_SceneBakedShadowMode", enabled ? (input.source == SceneBakedShadowSource.Constant ? 1 : 2) : 0);
            if (!enabled) return;
            if (!input.Resolve(renderer, out var map, out var st, out var error)) throw new InvalidOperationException(error);
            material.SetTexture("_SceneBakedShadowMap", map != null ? map : Texture2D.whiteTexture);
            material.SetVector("_SceneBakedShadowST", st); material.SetVector("_SceneBakedShadowConstant", input.visibility);
            material.SetFloat("_SceneBakedShadowDither", input.dither ? 1 : 0);
        }
        internal static bool ChannelValid(SceneBakedShadowChannel value) => (int)value >= 0 && (int)value <= 4;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Unit(float value) => Finite(value) && value >= 0 && value <= 1;
    }

    /// <summary>Independent R8 + GBA3:3:2 contract. Byte order is not an original-game ABI.</summary>
    public static class SceneBakedShadowEncoding
    {
        private static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
        public static byte PackGba(Vector3 visibility, int pixelX, int pixelY, bool dither = true)
        {
            for (int i = 0; i < 3; i++) if (float.IsNaN(visibility[i]) || visibility[i] < 0 || visibility[i] > 1)
                throw new ArgumentOutOfRangeException(nameof(visibility));
            float threshold = dither ? (Bayer[(pixelY & 3) * 4 + (pixelX & 3)] + .5f) / 16 : .5f;
            int g = Mathf.FloorToInt(visibility.x * 7 + threshold), b = Mathf.FloorToInt(visibility.y * 7 + threshold), a = Mathf.FloorToInt(visibility.z * 3 + threshold);
            return (byte)(g * 32 + b * 4 + a);
        }
        public static Vector3 UnpackGba(byte value) => new Vector3((value / 32) / 7f, ((value / 4) % 8) / 7f, (value % 4) / 3f);
    }
}
