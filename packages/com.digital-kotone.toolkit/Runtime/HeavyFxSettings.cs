using System;

namespace GakumasPhotoMode
{
    /// <summary>One opaque medium, ordered surfaces, then camera optics. Inputs remain caller-owned.</summary>
    [Serializable]
    public sealed class HeavyFxSettings
    {
        public bool enabled;
        public LowResolutionFxSettings geometry = new LowResolutionFxSettings();
        public VolumetricLightingSettings medium = new VolumetricLightingSettings();
        public LensFlareSettings optics = new LensFlareSettings();
        // Joint reconstruction is deliberately stricter than independent geometry defaults.
        public float depthAbsoluteTolerance = .001f;
        public float depthRelativeTolerance = .0001f;
        public float effectEdgeThreshold = .001f;
        public int maximumTargetMiB = 512;
    }

    public readonly struct HeavyFxBatchInfo
    {
        public readonly FxResolution resolution;
        public readonly bool medium, distortion, optics;
        public readonly int surfaceCount;
        internal HeavyFxBatchInfo(FxResolution size, bool volume, bool refract, bool flare, int count)
        { resolution=size; medium=volume; distortion=refract; optics=flare; surfaceCount=count; }
    }
}
