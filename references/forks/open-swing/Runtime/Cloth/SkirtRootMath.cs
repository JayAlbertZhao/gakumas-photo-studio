using System;
using Unity.Mathematics;
using UnityEngine;

namespace OpenSwing
{
    /// <summary>
    /// Converts a reference joint's local rotation into a skirt root rotation.
    /// The coefficients and limits are rig data; no character-specific values live here.
    /// </summary>
    public static class SkirtRootMath
    {
        [Serializable]
        public sealed class Setting
        {
            public string bone;
            public string reference;
            public Quaternion initialReferenceRotation = Quaternion.identity;
            public int rotationOrder;
            public Vector3 innerCoefficient;
            public Vector3 outerCoefficient;
            public Vector3 limitMin;
            public Vector3 limitMax;
        }

        public static Quaternion Calculate(Quaternion initial, Quaternion current, Setting setting)
        {
            if (setting == null) throw new ArgumentNullException(nameof(setting));
            if (setting.rotationOrder != 0)
                throw new NotSupportedException("Only XYZ rotation order is supported.");

            var relative = current * Quaternion.Inverse(initial);
            var euler = ToEulerXYZ(relative) * Mathf.Rad2Deg;
            var q = quaternion.EulerXYZ((float3)euler * Mathf.Deg2Rad).value;
            var rotation = new Quaternion(q.x, q.y, q.z, q.w);
            var up = rotation * Vector3.up;

            float bend = 2f * Mathf.Atan2(up.z, 1f + up.y) * Mathf.Rad2Deg;
            float roll = -2f * Mathf.Atan2(up.x, 1f + up.y) * Mathf.Rad2Deg;
            var rebuilt = Quaternion.Euler(euler);
            var twist = rebuilt * Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up, up));
            double length = Math.Sqrt((double)twist.x * twist.x + (double)twist.y * twist.y
                + (double)twist.z * twist.z + (double)twist.w * twist.w);
            if (length < 1e-12) return Quaternion.identity;

            float angle = (float)(2 * Math.Acos(Math.Max(-1, Math.Min(1, twist.w / length)))
                * Mathf.Rad2Deg);
            if (Vector3.Dot(new Vector3(twist.x, twist.y, twist.z), up) < 0f) angle = -angle;

            var degrees = new Vector3(Wrap(angle), Wrap(roll), Wrap(bend));
            var result = new Vector3(
                ApplyLimit(degrees.x, setting.innerCoefficient.x, setting.outerCoefficient.x,
                    setting.limitMin.x, setting.limitMax.x),
                ApplyLimit(degrees.y, setting.innerCoefficient.y, setting.outerCoefficient.y,
                    setting.limitMin.y, setting.limitMax.y),
                ApplyLimit(degrees.z, setting.innerCoefficient.z, setting.outerCoefficient.z,
                    setting.limitMin.z, setting.limitMax.z)) * Mathf.Deg2Rad;

            float pitchTangent = Mathf.Tan(result.y * 0.5f);
            float yawTangent = Mathf.Tan(-result.z * 0.5f);
            float scale = 2f / (pitchTangent * pitchTangent + yawTangent * yawTangent + 1f);
            var direction = new Vector3(scale - 1f, scale * yawTangent, scale * pitchTangent);
            return Quaternion.AngleAxis(-result.x * Mathf.Rad2Deg, Vector3.right)
                * Quaternion.FromToRotation(Vector3.right, direction);
        }

        private static float ApplyLimit(float degrees, float inner, float outer, float min, float max)
        {
            return outer * degrees + (inner - outer) * Mathf.Clamp(degrees, min, max);
        }

        private static float Wrap(float degrees)
        {
            return degrees > 180f ? degrees - 360f : degrees < -180f ? degrees + 360f : degrees;
        }

        private static Vector3 ToEulerXYZ(Quaternion q)
        {
            float x = q.x, y = q.y, z = q.z, w = -q.w;
            return new Vector3(
                -Mathf.Atan2(2f * (w * x - y * z), w * w - x * x - y * y + z * z),
                -Mathf.Asin(Mathf.Clamp(2f * (w * y + x * z), -1f, 1f)),
                -Mathf.Atan2(2f * (w * z - x * y), w * w + x * x - y * y - z * z));
        }
    }
}
