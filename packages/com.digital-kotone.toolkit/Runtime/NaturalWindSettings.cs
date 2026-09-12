using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Stateless, world-space wind for a host-owned clock. Independent model of
    /// the steady / periodic / random forces and gust envelope in QualiArts' 3D
    /// production presentation, slide 94; not an original asset parameter ABI.
    /// </summary>
    [Serializable]
    public sealed class NaturalWindSettings
    {
        public bool enabled;
        public int seed = 1;
        public Vector3 steadyForce = new Vector3(0.015f, 0f, 0f);
        public Vector3 sineAmplitude = new Vector3(0.005f, 0.001f, 0.004f);
        [Min(0f)] public float sineFrequency = 0.35f;
        public Vector3 randomAmplitude = new Vector3(0.004f, 0.001f, 0.004f);
        [Min(0f)] public float randomFrequency = 0.7f;
        public bool useGustEnvelope = true;
        [Min(0f)] public float gustSeconds = 4f;
        [Min(0f)] public float calmSeconds = 3f;
        [Range(0f, 0.5f)] public float fadeFraction = 0.25f;
        [Range(0f, 1f)] public float calmStrength;
        [Range(0f, 1f)] public float gustStrengthVariation = 0.3f;
        [Range(0f, 1f)] public float envelopeVariation = 0.15f;

        public bool IsValid
        {
            get
            {
                return Finite(steadyForce) && Finite(sineAmplitude) && Finite(randomAmplitude) &&
                    Nonnegative(sineFrequency) && Nonnegative(randomFrequency) &&
                    Nonnegative(gustSeconds) && Nonnegative(calmSeconds) &&
                    Unit(fadeFraction) && fadeFraction <= 0.5f && Unit(calmStrength) &&
                    Unit(gustStrengthVariation) && Unit(envelopeVariation) &&
                    (!useGustEnvelope || (gustSeconds > 0f && fadeFraction > 0f));
            }
        }

        public Vector3 Sample(double seconds)
        {
            if (!enabled || !IsValid || !ValidTime(seconds)) return Vector3.zero;
            double phase = seconds * sineFrequency;
            // Reduce before sin to retain precision during long sessions.
            float wave = (float)Math.Sin((phase - Math.Floor(phase)) * (2.0 * Math.PI));
            Vector3 random = new Vector3(
                Noise(seconds * randomFrequency, seed, 0),
                Noise(seconds * randomFrequency, seed, 1),
                Noise(seconds * randomFrequency, seed, 2));
            Vector3 force = (steadyForce + sineAmplitude * wave +
                Vector3.Scale(randomAmplitude, random)) * Envelope(seconds);
            return Finite(force) ? force : Vector3.zero;
        }

        public float Envelope(double seconds)
        {
            if (!enabled || !IsValid || !ValidTime(seconds)) return 0f;
            if (!useGustEnvelope) return 1f;
            double period = (double)gustSeconds + calmSeconds;
            double cycleValue = Math.Floor(seconds / period);
            if (Math.Abs(cycleValue) > 1e15) return 0f;
            long cycle = (long)cycleValue;
            double local = seconds - cycle * period;
            if (local >= gustSeconds) return calmStrength;
            double fade = gustSeconds * (double)fadeFraction;
            double edge = Math.Min(local / fade, (gustSeconds - local) / fade);
            float shape = Smooth((float)Math.Max(0.0, Math.Min(1.0, edge)));
            float peak = 1f - gustStrengthVariation * UnitHash(cycle, seed, 3);
            float modulation = 1f - envelopeVariation *
                (Noise(seconds * randomFrequency * 0.31, seed, 4) * 0.5f + 0.5f);
            // The varying term vanishes at either boundary. Adjacent cycles
            // may have different peaks without a force discontinuity.
            return calmStrength + (1f - calmStrength) * shape * peak * modulation;
        }

        private static float Noise(double position, int seed, uint channel)
        {
            // Avoid undefined float-to-integer conversion for extreme inputs.
            if (double.IsNaN(position) || double.IsInfinity(position) || Math.Abs(position) > 1e15)
                return 0f;
            long cell = (long)Math.Floor(position);
            float fraction = Smooth((float)(position - cell));
            return Mathf.Lerp(UnitHash(cell, seed, channel), UnitHash(cell + 1, seed, channel), fraction) * 2f - 1f;
        }

        private static float UnitHash(long cell, int seed, uint channel)
        {
            unchecked
            {
                uint value = (uint)cell ^ ((uint)(cell >> 32) * 0x9e3779b9u) ^
                    ((uint)seed * 0x85ebca6bu) ^ (channel * 0xc2b2ae35u);
                value ^= value >> 16;
                value *= 0x7feb352du;
                value ^= value >> 15;
                value *= 0x846ca68bu;
                value ^= value >> 16;
                return (value & 0x00ffffffu) / 16777215f;
            }
        }

        private static float Smooth(float t) { return t * t * (3f - 2f * t); }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static bool Finite(Vector3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z); }
        private static bool Nonnegative(float value) { return Finite(value) && value >= 0f; }
        private static bool Unit(float value) { return Finite(value) && value >= 0f && value <= 1f; }
        private static bool ValidTime(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 1e12;
        }
    }
}
