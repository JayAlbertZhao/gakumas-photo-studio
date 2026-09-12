using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Explicit linear lighting for reduced character reflection; no global shader state is read.</summary>
    [Serializable]
    public sealed class ActorPlanarLighting
    {
        public Vector3 lightDirection = new Vector3(0, 1, -1);
        public Vector3 lightColor = Vector3.one;
        public Vector3 ambientColor = new Vector3(.2f, .2f, .2f);
        public Vector3 shadeTint = Vector3.one;
        public Vector3 shadeAdditive;
        [Range(-1, 1)] public float rampOffset = .3f;
        [Range(-4, 4)] public float textureLodBias;
        [Range(-1, 2)] public float skinSaturation;
        public Transform head;

        internal bool IsValid => Finite(lightDirection) && lightDirection.sqrMagnitude > 1e-8f &&
            Color(lightColor) && Color(ambientColor) && Color(shadeTint) && Color(shadeAdditive) &&
            Range(rampOffset, -1, 1) && Range(textureLodBias, -4, 4) && Range(skinSaturation, -1, 2);
        internal static bool Finite(Vector3 v) => Range(v.x, -1e8f, 1e8f) && Range(v.y, -1e8f, 1e8f) && Range(v.z, -1e8f, 1e8f);
        internal static bool Range(float v, float a, float b) => !float.IsNaN(v) && v >= a && v <= b;
        private static bool Color(Vector3 v) => Range(v.x, 0, 1000) && Range(v.y, 0, 1000) && Range(v.z, 0, 1000);
    }
}
