using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public enum SceneShadowFilter { Hard, Pcf3x3 }

    /// <summary>Opt-in source visibility. World-unit bias and near distance (Spot axial, other shapes radial per sample).</summary>
    [Serializable]
    public sealed class SceneLightShadowInput
    {
        public bool enabled;
        [Range(0, 1)] public float strength = 1;
        [Min(.001f)] public float nearPlane = .05f;
        [Min(0)] public float depthBias = .002f;
        [Min(0)] public float normalBias;
        public SceneShadowFilter filter = SceneShadowFilter.Hard;
        // Explicitly select the independently authored finite-source coverage model.
        // Default false preserves old unsupported Capsule/Area shadow rejection.
        public bool extendedSourceCoverage;
        // Capsule: N midpoint sources. Area: N*N. Only read for these two shapes.
        [Range(1, 4)] public int extendedSamplesPerAxis = 2;
    }

    /// <summary>Explicit borrowed geometry; no inference from the renderer's material shader.</summary>
    [Serializable]
    public sealed class SceneShadowCaster
    {
        public Renderer renderer;
        public int materialIndex;
        public CullMode cull = CullMode.Back;
        public Vector3 vertexScale = Vector3.one;
        public Texture alphaMap;
        public Vector4 uvST = new Vector4(1, 1, 0, 0);
        [Range(0, 1)] public float alpha = 1, cutoff;
    }

    [Serializable]
    public sealed class SceneLightShadowSettings
    {
        // Power of two, 32..2048; atlas side is also capped at 4096.
        public int tileResolution = 256;
        // Counts light sources, not faces. A Point consumes six atlas tiles, a Spot one.
        [Range(1, 16)] public int maxShadowedLights = 16;
        // Checked only when an active Capsule/Area shadow is requested.
        [Range(1, 256)] public int maxExtendedSourceSamples = 64;
        [Range(1, 262144)] public int maxExtendedCasterDraws = 32768;
        public SceneShadowCaster[] casters = Array.Empty<SceneShadowCaster>();
    }
}
