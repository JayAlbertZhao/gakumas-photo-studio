using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Independent thin water surface. No fluid simulation or game-specific material mapping.</summary>
    [Serializable]
    public sealed class SceneWaterSurface
    {
        public SceneForwardSurface surface = new SceneForwardSurface();
        [Range(1, 2.5f)] public float indexOfRefraction = 1.333f;
        public Vector3 absorption = new Vector3(.3f, .08f, .04f);
        public Vector3 scatteringRadiance = new Vector3(.03f, .12f, .16f);
        [Range(0, 1000)] public float maximumThickness = 20;
        [Range(0, 512)] public float refractionPixelsPerUnit = 8;
        [Range(0, 1)] public float reflectionStrength = 1;
        // Linear RGB, no implicit RGBM/Unity reflection-probe decoding.
        public Cubemap reflectionProbe;
        [Range(0, 12)] public float probeMaximumMip;
        // Only the current same-camera producer is consumed. Missing coverage falls back to the cube.
        public PlanarReflection planarReflection;
        public Vector2 reflectionDistortion = new Vector2(.1f, -.1f);
        // xy = angular wave vector (radians/UV); z = height amplitude in UV units;
        // w = angular speed (radians/second). These alter normals, not geometry.
        public Vector4 waveA = new Vector4(13, 3, .012f, 1.1f);
        public Vector4 waveB = new Vector4(-4, 17, .008f, -.8f);
        public Vector2 normalScroll;
        [Range(0, 2)] public float normalStrength = 1;
        [Range(0, 10)] public float shoreFadeDistance;
    }

    [Serializable]
    public sealed class SceneWaterSettings
    {
        public const int MaximumSurfaces = 64;
        public bool enabled;
        // Ordered back to front; intersecting surfaces require an explicit scene split.
        public SceneWaterSurface[] surfaces = Array.Empty<SceneWaterSurface>();
        public SceneForwardLightSettings lighting = new SceneForwardLightSettings { enabled = true };
        public double seconds;
        [Range(0, 1)] public float depthBias = .001f;
        [Range(1, 2048)] public int maximumTargetMiB = 256;
    }
}
