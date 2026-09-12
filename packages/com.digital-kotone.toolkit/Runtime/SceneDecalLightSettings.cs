using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum SceneDecalLightShape { Point, Capsule, Area }
    public enum SceneDecalLightBackend { Auto, Scalar, Instanced }

    /// <summary>Independent direct-light volume, not a copy of the source game's light ABI.</summary>
    [Serializable]
    public sealed class SceneDecalLight
    {
        public bool enabled = true;
        public SceneDecalLightShape shape;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        [Min(.001f)] public float range = 3;
        // Capsule runs along local X. Area lies in local XY and emits toward local +Z.
        [Min(0)] public float halfLength = .5f;
        public Vector2 halfSize = Vector2.one * .5f;
        public Vector2 areaSpread = Vector2.zero;
        public Vector3 radiance = Vector3.one;
        // Point: zw; capsule: zw + t*xy; area: zw + planeUV*xy.
        public Vector4 monitorUV = new Vector4(1, 1, 0, 0);
        [Range(0, 255)] public int receiverGroup;
        [Range(1, 8)] public float falloffExponent = 1;
        [Range(0, 4)] public float diffuseScale = 1, specularScale = 1;
        [Range(0, 1)] public float giWeight;
        [Range(0, 4)] public float backlightScale;
    }

    [Serializable]
    public sealed class SceneDecalLightSettings
    {
        public bool enabled;
        public SceneDecalLightBackend backend = SceneDecalLightBackend.Auto;
        public bool allowInstancingFallback = true;
        [Range(1, 1024)] public int batchSize = 256;
        public SceneDecalLight[] lights = Array.Empty<SceneDecalLight>();
        // If assigned, a current published frame is required. The host updates it first.
        public HdrMonitor monitor;
        // Used only without monitor; null is white. Use a linear texture for literal HDR radiance.
        public Texture atlas;
    }
}
