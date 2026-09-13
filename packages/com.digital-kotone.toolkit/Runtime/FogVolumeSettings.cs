using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Independent additive extinction media; colors are linear radiance, not sRGB.</summary>
    [Serializable]
    public sealed class FogVolumeSettings
    {
        public const int MaximumSpheres = 8;
        public bool enabled;
        [Range(1,32)] public int stepsPerInterval = 8;
        [Range(0,1)] public float maximumOpacity = 1;
        public DistanceMedium distance = new DistanceMedium();
        public SphereMedium[] spheres = Array.Empty<SphereMedium>();

        [Serializable] public sealed class DistanceMedium
        {
            public bool enabled;
            [Min(0)] public float density = .01f;
            // Distances along the ray, starting at its near-plane point (world units).
            [Min(0)] public float startDistance;
            [Min(0)] public float endDistance = 1000;
            public Color linearColor = new Color(.5f,.6f,.7f,1);
            public bool affectSky = true;
        }
        [Serializable] public sealed class SphereMedium
        {
            public bool enabled = true;
            public Vector3 center;
            [Min(.0001f)] public float radius = 10;
            [Min(0)] public float density = .1f;
            public Color linearColor = new Color(.5f,.6f,.7f,1);
            public bool affectSky = true;
        }
        internal static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        internal static bool Range(float x,float lo,float hi) => Finite(x) && x>=lo && x<=hi;
        internal static bool Position(Vector3 p) => Range(p.x,-1e6f,1e6f)&&Range(p.y,-1e6f,1e6f)&&Range(p.z,-1e6f,1e6f);
        internal static bool Radiance(Color c) => Range(c.r,0,65504)&&Range(c.g,0,65504)&&Range(c.b,0,65504);
    }
}
