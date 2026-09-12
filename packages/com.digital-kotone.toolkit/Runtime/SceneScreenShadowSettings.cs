using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Explicit world-space capsule, independent of actor rig or source-game data.</summary>
    [Serializable]
    public sealed class SceneCapsuleOccluder
    {
        public bool enabled = true;
        public Vector3 start, end = Vector3.up;
        [Min(.001f)] public float radius = .1f;
    }

    /// <summary>Optional full-resolution main shadow / capsule ambient visibility buffer.</summary>
    [Serializable]
    public sealed class SceneScreenShadowSettings
    {
        public bool enabled;
        public SceneGtaoSettings gtao = new SceneGtaoSettings();
        public SceneCapsuleOccluder[] capsules = Array.Empty<SceneCapsuleOccluder>();
        // Cosine-weighted deterministic hemisphere samples: 8,16,32,64.
        public int capsuleSamples = 32;
        [Range(0, 1)] public float capsuleStrength = 1;
        [Min(.001f)] public float capsuleMaxDistance = 1;
        [Min(0)] public float capsuleNormalBias = .002f;
    }
}
