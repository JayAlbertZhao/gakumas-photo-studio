using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Camera-local, independently reconstructed spherical fog. No game assets
    /// or global Volume state are required. Distances are in world units.
    /// </summary>
    [Serializable]
    public sealed class SphereFogSettings
    {
        public bool enabled;
        public Vector3 center;
        [Min(0.0001f)] public float radius = 10f;
        [Min(0f)] public float density = 0.1f;
        [ColorUsage(false, true)] public Color color = new Color(0.5f, 0.6f, 0.7f, 1f);
        [Range(0f, 1f)] public float maximumOpacity = 0.6f;
        public bool affectSky = true;

        public bool IsActive
        {
            get
            {
                return enabled && Finite(radius) && radius >= 0.0001f &&
                    Finite(density) && density > 0f && Finite(maximumOpacity) && maximumOpacity > 0f &&
                    Finite(center.x) && Finite(center.y) && Finite(center.z) &&
                    Finite(color.r) && Finite(color.g) && Finite(color.b) &&
                    color.r >= 0f && color.g >= 0f && color.b >= 0f;
            }
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        /// <summary>
        /// Integral of density * max(1 - squaredDistance/radius^2, 0) on a
        /// finite segment. A full central chord has optical depth 4*r*d/3.
        /// Opacity is min(1-exp(-opticalDepth), maximumOpacity).
        /// </summary>
        public float EvaluateOpticalDepth(Vector3 start, Vector3 end)
        {
            if (!IsActive) return 0f;
            Vector3 segment = end - start;
            double length = segment.magnitude;
            if (length <= 0.000001 || double.IsNaN(length) || double.IsInfinity(length)) return 0f;
            Vector3 direction = segment / (float)length;
            Vector3 origin = (start - center) / radius;
            double closest = -Vector3.Dot(origin, direction);
            Vector3 perpendicular = origin + direction * (float)closest;
            double halfSquared = 1.0 - perpendicular.sqrMagnitude;
            if (halfSquared <= 0.0) return 0f;
            double half = Math.Sqrt(halfSquared);
            double entry = Math.Max(0.0, closest - half);
            double exit = Math.Min(length / radius, closest + half);
            double span = Math.Max(0.0, exit - entry);
            double midpoint = (entry + exit) * 0.5 - closest;
            // Chord-centered integration avoids subtracting two large cubes.
            double integral = span * Math.Max(0.0, halfSquared - midpoint * midpoint - span * span / 12.0);
            return (float)Math.Min(float.MaxValue, integral * radius * density);
        }
    }
}
