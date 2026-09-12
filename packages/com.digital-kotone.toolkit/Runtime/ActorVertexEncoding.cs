using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Authoring helpers for the packed inputs consumed by the toolkit's actor shaders.</summary>
    public static class ActorVertexEncoding
    {
        public struct Channels
        {
            public int outlineRed, outlineGreen, outlineBlue;
            public int materialRamp, outlineDepth, outlineWidth, rimMask, reserved;
        }

        // PPT pp67-69's four-bit channels. Integers deliberately avoid hidden
        // color-space conversion or an arbitrary float quantization convention.
        public static Color32 Pack(Channels value)
        {
            return new Color32(Pair(value.outlineRed, value.outlineGreen),
                Pair(value.outlineBlue, value.materialRamp),
                Pair(value.outlineDepth, value.outlineWidth),
                Pair(value.rimMask, value.reserved));
        }

        public static Channels Unpack(Color32 value)
        {
            return new Channels {
                outlineRed = value.r >> 4, outlineGreen = value.r & 15,
                outlineBlue = value.g >> 4, materialRamp = value.g & 15,
                outlineDepth = value.b >> 4, outlineWidth = value.b & 15,
                rimMask = value.a >> 4, reserved = value.a & 15
            };
        }

        /// <summary>
        /// Convert authored object-space outline vectors to Unity tangents.
        /// Preserve length and zero vectors: the shader uses them for extrusion.
        /// This does not generate/smooth normals or mutate the source mesh.
        /// </summary>
        public static Vector4[] OutlineTangents(IReadOnlyList<Vector3> vectors, float w = 1f)
        {
            if (vectors == null) throw new ArgumentNullException(nameof(vectors));
            if (!Finite(w)) throw new ArgumentOutOfRangeException(nameof(w));
            var result = new Vector4[vectors.Count];
            for (int i = 0; i < result.Length; i++)
            {
                Vector3 v = vectors[i];
                if (!Finite(v.x) || !Finite(v.y) || !Finite(v.z))
                    throw new ArgumentException("Outline vectors must be finite.", nameof(vectors));
                result[i] = new Vector4(v.x, v.y, v.z, w);
            }
            return result;
        }

        private static byte Pair(int high, int low)
        {
            if (high < 0 || high > 15 || low < 0 || low > 15)
                throw new ArgumentOutOfRangeException("value", "Every packed channel must be in 0..15.");
            return (byte)((high << 4) | low);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
