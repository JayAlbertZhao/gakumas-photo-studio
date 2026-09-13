using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public enum SceneSkySource { Gradient, Cubemap, Equirectangular }

    /// <summary>Authored linear sky radiance. No physical atmosphere or original-game parameters.</summary>
    [Serializable]
    public sealed class SceneSkySettings
    {
        public bool enabled;
        public SceneSkySource source;
        public Vector3 zenith = new Vector3(.08f, .2f, .5f), horizon = new Vector3(.6f, .7f, .9f), ground = new Vector3(.06f, .04f, .03f);
        [Range(.01f, 16)] public float gradientPower = 1;
        public Texture texture;
        [Range(0, 12)] public float sourceMip;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 tint = Vector3.one;
        [Range(-16, 16)] public float exposure;
        public bool sunEnabled;
        public Vector3 sunDirection = new Vector3(.3f, .8f, .2f), sunRadiance = new Vector3(4, 3, 2);
        [Range(.001f, 20)] public float sunRadiusDegrees = .27f;
        [Range(0, 1)] public float sunSoftness = .2f;

        internal string Validate()
        {
            if (!enabled) return "Disabled";
            if ((int)source < 0 || (int)source > 2 || !Radiance(tint) || !Range(exposure, -16, 16)) return "Invalid sky source, tint or exposure";
            float norm = Quaternion.Dot(rotation, rotation);
            if (!Range(norm, 1e-8f, 1e8f)) return "Sky rotation must be finite and nonzero";
            if (source == SceneSkySource.Gradient)
            {
                if (!Radiance(zenith) || !Radiance(horizon) || !Radiance(ground) || !Range(gradientPower, .01f, 16)) return "Invalid authored sky gradient";
            }
            else
            {
                var dimension = source == SceneSkySource.Cubemap ? TextureDimension.Cube : TextureDimension.Tex2D;
                if (!LinearTexture(texture, dimension) || !Range(sourceMip, 0, Mathf.Min(12, texture.mipmapCount - 1))) return "Sky requires a created linear texture and an existing mip";
                if (source == SceneSkySource.Equirectangular && (texture.wrapModeU != TextureWrapMode.Repeat || texture.wrapModeV != TextureWrapMode.Clamp))
                    return "Equirectangular sky requires Repeat U and Clamp V; input sampler is borrowed";
            }
            if (sunEnabled && (!Radiance(sunRadiance) || !Finite(sunDirection) || sunDirection.sqrMagnitude < 1e-8f ||
                !Range(sunRadiusDegrees, .001f, 20) || !Range(sunSoftness, 0, 1))) return "Invalid authored sky sun";
            return null;
        }
        internal static bool LinearTexture(Texture texture, TextureDimension dimension) => texture != null && texture.dimension == dimension &&
            !GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) &&
            (!(texture is RenderTexture rt) || rt.IsCreated() && rt.antiAliasing == 1 && !rt.useDynamicScale);
        private static bool Radiance(Vector3 v) => Range(v.x, 0, 65504) && Range(v.y, 0, 65504) && Range(v.z, 0, 65504);
        private static bool Finite(Vector3 v) => Range(v.x, -1e6f, 1e6f) && Range(v.y, -1e6f, 1e6f) && Range(v.z, -1e6f, 1e6f);
        private static bool Range(float v, float low, float high) => !float.IsNaN(v) && !float.IsInfinity(v) && v >= low && v <= high;
    }
}
