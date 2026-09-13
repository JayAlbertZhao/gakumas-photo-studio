using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum MotionBlurExposure { ShutterAngle = 0, Seconds = 1 }

    /// <summary>Independent centered-shutter reconstruction. Disabled until explicitly selected.</summary>
    [Serializable]
    public sealed class MotionBlurSettings
    {
        public bool enabled;
        public MotionBlurExposure exposure = MotionBlurExposure.ShutterAngle;
        [Range(0, 360)] public float shutterAngle = 180;
        [Range(0, 1)] public float exposureSeconds = 1f / 120;
        [Range(4, 64)] public int samples = 32;
        [Range(1, 128)] public int maximumRadiusPixels = 24;
        [Min(.000001f)] public float softDepthExtent = .02f;
        [Range(.001f, 10)] public float maximumSampleInterval = .25f;
        public bool dualDirections = true;
        [Range(0, 1)] public float sampleJitter = 1;
        // A stable spatial pattern. Change explicitly, never implicitly on each game-loop tick.
        public uint noiseSeed = 17;
        [Range(1, 100)] public float centerWeightDenominator = 40;

        public bool IsValid => (exposure == MotionBlurExposure.ShutterAngle || exposure == MotionBlurExposure.Seconds) &&
            Range(shutterAngle,0,360) && Range(exposureSeconds,0,1) && samples>=4 && samples<=64 && (samples&1)==0 &&
            maximumRadiusPixels>=1 && maximumRadiusPixels<=128 && Range(softDepthExtent,.000001f,10000) &&
            Range(maximumSampleInterval,.001f,10) && Range(sampleJitter,0,1) && Range(centerWeightDenominator,1,100);
        internal static bool Range(float value,float lo,float hi) => !float.IsNaN(value) && !float.IsInfinity(value) && value>=lo && value<=hi;
        internal MotionBlurSettings Snapshot() => (MotionBlurSettings)MemberwiseClone();
        // Half of the displacement during the exposure. Zero/long intervals represent a paused or stale sample.
        public float HalfDisplacementScale(float sampleInterval)
        {
            if(!IsValid || !Range(sampleInterval,0,maximumSampleInterval) || sampleInterval<.000001f) return 0;
            return exposure==MotionBlurExposure.ShutterAngle ? shutterAngle/720 : exposureSeconds/(2*sampleInterval);
        }
    }
}
