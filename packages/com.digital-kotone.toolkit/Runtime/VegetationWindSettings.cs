using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum VegetationWindBackend { Auto, Gpu, Cpu }

    /// <summary>Independent rooted wind field. No inferred original vegetation parameters.</summary>
    [Serializable]
    public sealed class VegetationWindSettings
    {
        public bool enabled;
        public Vector3 rootLocal;
        public Vector3 upLocal = Vector3.up;
        [Min(.001f)] public float height = 1;
        // Displacement in world units, not a force-integrating physics solver.
        public Vector3 displacementWorld = new Vector3(.05f, 0, 0);
        public NaturalWindSettings naturalWind = new NaturalWindSettings();
        [Range(0, 100)] public float naturalWindScale = 1;
        public Vector3 flutterWorld = new Vector3(.015f, 0, .01f);
        [Range(0, 100)] public float flutterFrequencyHz = 1.5f;
        public double phaseCycles;
    }
}
