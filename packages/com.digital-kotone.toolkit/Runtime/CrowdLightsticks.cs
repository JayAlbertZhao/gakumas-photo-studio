using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Independent authored lightstick emission, not an original game shader ABI.</summary>
    [Serializable]
    public sealed class CrowdLightstickSurface
    {
        public bool enabled;
        // Required linear red-channel mask in the prototype's existing transformed UV.
        // Geometry (including a held lightstick) belongs to the current posed prototype.
        public Texture mask;
        public Vector3 radiance = Vector3.one;
        [Range(0, 100)] public float frequencyHz;
        [Range(0, 1)] public float minimum = 1;
    }

    /// <summary>Optional owned instance emission stream; the old placement ABI stays48bytes.</summary>
    internal sealed class CrowdLightsticks : IDisposable
    {
        public ComputeBuffer Radiance { get; private set; }
        private Vector4[] values;
        public int BufferCount => Radiance == null ? 0 : 1;
        public static bool Enabled(CrowdDefinition source)
        {
            foreach (var p in source.prototypes) if (p.lightstick != null && p.lightstick.enabled) return true;
            return false;
        }
        public static long Bytes(CrowdDefinition source) => Enabled(source) ? (long)Mathf.NextPowerOfTwo(source.instances.Length) * 16 : 0;
        internal static bool ValidMask(Texture mask)
        {
            if (mask == null || mask.dimension != UnityEngine.Rendering.TextureDimension.Tex2D ||
                GraphicsFormatUtility.IsSRGBFormat(mask.graphicsFormat)) return false;
            return !(mask is RenderTexture rt) || rt.IsCreated() && rt.antiAliasing == 1 && !rt.useDynamicScale && !rt.sRGB;
        }
        internal static bool Range(double value, double low, double high) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= low && value <= high;
        internal static bool Tint(Vector3 value) => Range(value.x, 0, 65504) && Range(value.y, 0, 65504) && Range(value.z, 0, 65504);

        public void Prepare(CrowdDefinition source, double timeSeconds)
        {
            if (!Enabled(source)) { Dispose(); return; }
            int capacity = Mathf.NextPowerOfTwo(source.instances.Length);
            if (Radiance == null || !Radiance.IsValid() || Radiance.count != capacity)
            {
                Dispose(); values = new Vector4[capacity];
                Radiance = new ComputeBuffer(capacity, 16) { name = "Toolkit crowd current lightstick radiance" };
            }
            Array.Clear(values, 0, values.Length);
            for (int i = 0; i < source.instances.Length; i++)
            {
                var item = source.instances[i]; var surface = source.prototypes[item.prototype].lightstick;
                if (surface == null || !surface.enabled) continue;
                // Reduce in double precision before trig; no Unity time, random seed
                // or accumulated frame delta. Seek/pause/replay use the caller's clock.
                double cycles = timeSeconds * surface.frequencyHz + item.lightstickPhaseCycles;
                double phase = cycles - Math.Floor(cycles);
                double pulse = surface.minimum + (1 - surface.minimum) * (.5 - .5 * Math.Cos(phase * Math.PI * 2));
                var value = Vector4.zero;
                for (int c = 0; c < 3; c++) value[c] = (float)Math.Min(65504, (double)surface.radiance[c] * item.lightstickTint[c] * pulse);
                values[i] = value;
            }
            Radiance.SetData(values);
        }
        public void Dispose() { Radiance?.Dispose(); Radiance = null; values = null; }
    }
}
