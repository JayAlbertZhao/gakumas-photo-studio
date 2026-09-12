using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Explicit single orthographic coverage for the scene's main directional light.
    /// Origin is a shadow-view anchor, not a finite light position. Forward is -lightDirection.</summary>
    [Serializable]
    public sealed class SceneDirectionalShadowSettings
    {
        public bool enabled;
        public Vector3 origin = new Vector3(0, 0, -10);
        public Vector3 up = Vector3.up;
        public Vector2 halfSize = Vector2.one * 5;
        [Min(.001f)] public float nearPlane = .05f;
        [Min(.002f)] public float farPlane = 20;
        [Range(0, 1)] public float strength = 1;
        [Min(0)] public float depthBias = .002f, normalBias;
        public SceneShadowFilter filter = SceneShadowFilter.Hard;
        public int resolution = 512;
        public SceneShadowCaster[] casters = Array.Empty<SceneShadowCaster>();
    }
}
