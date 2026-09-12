using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum SceneAmbientCombination { Multiply, Minimum }

    /// <summary>View-axis horizon AO over registered scene geometry; no temporal history.</summary>
    [Serializable]
    public sealed class SceneGtaoSettings
    {
        public bool enabled;
        [Min(.001f)] public float radius = .7f;
        [Range(0, 1)] public float strength = 1;
        [Range(1, 16)] public int slices = 4;
        [Range(2, 32)] public int stepsPerSide = 8;
        [Range(1, 256)] public int maxRadiusPixels = 64;
        [Min(0)] public float normalBias = .002f;
        [Range(0, 1)] public float falloffStart = .8f;
        // Optional horizon decay when farther samples expose thinner geometry.
        [Range(0, 1)] public float thicknessBlend;
        public SceneAmbientCombination combineWithCapsules = SceneAmbientCombination.Multiply;
    }
}
