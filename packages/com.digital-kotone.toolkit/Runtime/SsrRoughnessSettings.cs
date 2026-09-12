using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Independent receiver-aware screen-space tent filter, not a GGX prefilter.</summary>
    [Serializable]
    public sealed class SsrRoughnessSettings
    {
        public bool enabled;
        [Range(0, 12)] public float maximumRadiusPixels = 8;
        [Min(1)] public float referenceHeight = 1080;
        [Min(.001f)] public float planeTolerance = .05f;
        [Range(0, 1)] public float normalThreshold = .95f;
        [Range(0, 1)] public float smoothnessTolerance = .2f;

        internal bool IsActive => enabled && maximumRadiusPixels > 0;
        internal bool IsValid => !enabled || (Range(maximumRadiusPixels, 0, 12) && Range(referenceHeight, 1, 16384) &&
            Range(planeTolerance, .001f, 100) && Range(normalThreshold, 0, 1) && Range(smoothnessTolerance, 0, 1));
        internal float RadiusAtHeight(int height) => Mathf.Min(12, maximumRadiusPixels * height / referenceHeight);
        private static bool Range(float value, float low, float high) => !float.IsNaN(value) && value >= low && value <= high;
    }
}
