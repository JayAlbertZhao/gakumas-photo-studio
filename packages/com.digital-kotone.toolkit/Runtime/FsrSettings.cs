using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum FsrQuality { UltraQuality, Quality, Balanced, Performance }
    public enum FsrBackend { Auto, Compute, Raster }
    public enum FsrInputEncoding { LinearLdr, PerceptualGamma2Ldr, LinearHdr }

    /// <summary>FSR1 spatial reconstruction; antialiasing belongs to the caller, before this pass.</summary>
    [Serializable]
    public sealed class FsrSettings
    {
        public bool enabled;
        public FsrQuality quality = FsrQuality.Quality;
        public FsrBackend backend = FsrBackend.Auto;
        public FsrInputEncoding encoding = FsrInputEncoding.LinearLdr;
        public bool allowRasterFallback = true;
        public bool sharpen = true;
        // Exact RCAS normalization avoids amplifying the original approximate
        // reciprocal's constant-color bias through inverse HDR compression.
        public bool accurateRcasNormalization = true;
        [Range(0, 2.5f)] public float sharpnessStops = .2f;
        [Range(1, 1024)] public int memoryBudgetMiB = 256;

        public bool IsValid => Enum.IsDefined(typeof(FsrQuality), quality) &&
            Enum.IsDefined(typeof(FsrBackend), backend) && Enum.IsDefined(typeof(FsrInputEncoding), encoding) &&
            !float.IsNaN(sharpnessStops) && !float.IsInfinity(sharpnessStops) &&
            sharpnessStops >= 0 && sharpnessStops <= 2.5f && memoryBudgetMiB >= 1 && memoryBudgetMiB <= 1024;

        public float Scale => quality == FsrQuality.UltraQuality ? 1.3f :
            quality == FsrQuality.Quality ? 1.5f : quality == FsrQuality.Balanced ? 1.7f : 2f;

        public bool TryGetRenderSize(Vector2Int output, out Vector2Int input)
        {
            input = default;
            if (!IsValid || !ValidSize(output)) return false;
            input = new Vector2Int(Mathf.CeilToInt(output.x / Scale), Mathf.CeilToInt(output.y / Scale));
            return true;
        }

        public static bool ValidSize(Vector2Int size) => size.x >= 1 && size.y >= 1 && size.x <= 4096 && size.y <= 4096;
        public static long EstimateTargetBytes(Vector2Int input, Vector2Int output) =>
            16L * ((long)input.x * input.y + 2L * output.x * output.y);
    }
}
