using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Opt-in scene motion metadata. Geometry-shader snapshots require a compatible GPU; disabled by default.</summary>
    [Serializable]
    public sealed class SceneMotionSettings
    {
        public bool enabled;
        [Min(1)] public int maximumTrackedVertices = 1000000;
        [Min(0)] public float cameraCutDistance = 1;
        [Range(0,180)] public float cameraCutAngle = 30;
    }
}
