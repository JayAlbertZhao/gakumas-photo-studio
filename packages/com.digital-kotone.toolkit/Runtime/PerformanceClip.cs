using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Independent DCC interchange. IDs resolve only through explicit host bindings, never filesystem paths.</summary>
    [Serializable]
    public sealed class PerformanceClip
    {
        public const string Schema = "photo-studio.performance.v1";
        public const int MaxJsonBytes = 1048576;
        [Serializable] public sealed class Morph { public string target; public FaceDecalAnimation.Key[] keys = Array.Empty<FaceDecalAnimation.Key>(); }
        [Serializable] public sealed class Decal
        {
            public string target; public FaceDecalPose baseline = new FaceDecalPose();
            public FaceDecalAnimation.Track[] tracks = Array.Empty<FaceDecalAnimation.Track>();
        }
        [Serializable] public sealed class Effect
        {
            public string id, prefab, attachment; public double start, duration = 1; public uint seed = 1;
            public Vector3 position, rotation, scale = Vector3.one;
        }
        [Serializable] public sealed class MaterialEffect
        {
            public string id, target, colorTexture, shadeTexture, defTexture;
            public double start, duration = 1; public int columns = 1, rows = 1, fps = 1;
        }
        public string schema = Schema;
        public float duration = 1; public bool loop;
        public Morph[] morphs = Array.Empty<Morph>();
        public Decal[] decals = Array.Empty<Decal>();
        public Effect[] effects = Array.Empty<Effect>();
        public MaterialEffect[] materials = Array.Empty<MaterialEffect>();
        public VertexLocatorRig.Locator[] locators = Array.Empty<VertexLocatorRig.Locator>();

        public static bool TryParse(string json, out PerformanceClip clip, out string reason)
        {
            clip = null; reason = null;
            try
            {
                Require(json != null && json.Length <= MaxJsonBytes && Encoding.UTF8.GetByteCount(json) <= MaxJsonBytes && json.TrimStart().StartsWith("{", StringComparison.Ordinal), "Missing/oversized JSON object.");
                var value = JsonUtility.FromJson<PerformanceClip>(json); Validate(value); clip = value; return true;
            }
            catch (ArgumentException error) { reason = error.Message; return false; }
        }
        public bool TryCopy(out PerformanceClip copy, out string reason)
        {
            copy = null; reason = null;
            try { Validate(this); return TryParse(JsonUtility.ToJson(this), out copy, out reason); }
            catch (ArgumentException error) { reason = error.Message; return false; }
        }
        public static void Validate(PerformanceClip clip)
        {
            Require(clip != null && clip.schema == Schema && Range(clip.duration, .000001, 3600), "Unknown schema or invalid duration.");
            Require(clip.morphs != null && clip.morphs.Length <= 256 && clip.decals != null && clip.decals.Length <= 8 &&
                clip.effects != null && clip.effects.Length <= 32 && clip.materials != null && clip.materials.Length <= 64 &&
                clip.locators != null && clip.locators.Length <= 256, "Missing collection or capacity exceeded.");
            int totalKeys = 0; var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in clip.morphs)
            {
                Require(item != null && Id(item.target) && names.Add(item.target), "Missing/duplicate morph target.");
                ValidateKeys(item.keys, clip.duration, ref totalKeys);
                foreach (var key in item.keys) Require(Range(key.value, -64, 64), "Morph weight outside [-64,64].");
            }
            names.Clear();
            foreach (var item in clip.decals)
            {
                Require(item != null && Id(item.target) && names.Add(item.target) && item.tracks != null, "Missing/duplicate decal target.");
                foreach (var track in item.tracks) { Require(track != null, "Null decal track."); ValidateKeys(track.keys, clip.duration, ref totalKeys); }
                var animation = new FaceDecalAnimation { duration = clip.duration, tracks = item.tracks };
                Require(animation.TrySample(item.baseline, 0, out _, out var reason), "Invalid decal: " + reason);
            }
            names.Clear();
            foreach (var item in clip.effects)
            {
                Require(item != null && Id(item.id) && names.Add(item.id) && Id(item.prefab) && Id(item.attachment), "Invalid/duplicate effect ID or binding.");
                Interval(item.start, item.duration, clip.duration, 60);
                Require(item.seed != 0 && Finite(item.position) && Finite(item.rotation) && Finite(item.scale) &&
                    item.scale.x != 0 && item.scale.y != 0 && item.scale.z != 0, "Invalid effect seed/TRS.");
            }
            names.Clear();
            foreach (var item in clip.materials)
            {
                Require(item != null && Id(item.id) && names.Add(item.id) && Id(item.target), "Invalid/duplicate material effect.");
                Interval(item.start, item.duration, clip.duration, 3600);
                Require(OptionalId(item.colorTexture) && OptionalId(item.shadeTexture) && OptionalId(item.defTexture) &&
                    item.columns >= 1 && item.columns <= 64 && item.rows >= 1 && item.rows <= 64 && item.fps >= 1 && item.fps <= 240, "Invalid texture IDs or atlas.");
            }
            names.Clear();
            foreach (var d in clip.locators)
                Require(d != null && Id(d.id) && names.Add(d.id) && d.a >= 0 && d.a < 262144 && d.b >= 0 && d.b < 262144 && d.c >= 0 && d.c < 262144 &&
                    Finite(d.barycentric) && d.barycentric.x >= 0 && d.barycentric.y >= 0 && d.barycentric.z >= 0 && Mathf.Abs(d.barycentric.x + d.barycentric.y + d.barycentric.z - 1) <= 1e-6f &&
                    Finite(d.positionOffset) && Finite(d.rotationDegrees), "Invalid locator definition.");
        }
        internal static void ValidateKeys(FaceDecalAnimation.Key[] keys, float duration, ref int total)
        {
            Require(keys != null && keys.Length > 0 && keys.Length <= 8192 && (total += keys.Length) <= 16384, "Key capacity exceeded or empty track.");
            float last = -1;
            foreach (var key in keys)
            {
                Require(Range(key.seconds, 0, duration) && key.seconds > last && Range(key.value, -1e6, 1e6) &&
                    Range(key.inTangent, -1e6, 1e6) && Range(key.outTangent, -1e6, 1e6) && (int)key.interpolation >= 0 && (int)key.interpolation <= 2, "Invalid key/order/interpolation.");
                last = key.seconds;
            }
        }
        internal static float Sample(FaceDecalAnimation.Key[] keys, double time)
        {
            if (time <= keys[0].seconds) return keys[0].value;
            for (int i = 0; i + 1 < keys.Length; i++)
            {
                var a = keys[i]; var b = keys[i + 1]; if (time >= b.seconds) continue;
                double dt = b.seconds - (double)a.seconds, t = (time - a.seconds) / dt;
                if (a.interpolation == FaceDecalInterpolation.Hold) return a.value;
                if (a.interpolation == FaceDecalInterpolation.Linear) return (float)(a.value + (b.value - (double)a.value) * t);
                return (float)((2*t*t*t-3*t*t+1)*a.value + (t*t*t-2*t*t+t)*dt*a.outTangent + (-2*t*t*t+3*t*t)*b.value + (t*t*t-t*t)*dt*b.inTangent);
            }
            return keys[keys.Length - 1].value;
        }
        internal static bool Id(string value) => value != null && Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]{1,128}\z");
        private static bool OptionalId(string value) => string.IsNullOrEmpty(value) || Id(value);
        private static void Interval(double start, double duration, float end, double limit) => Require(Range(start, 0, end) && Range(duration, .000001, limit) && start + duration <= end, "Invalid effect interval.");
        internal static bool Range(double value, double lo, double hi) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= lo && value <= hi;
        private static bool Finite(Vector3 value) => Range(value.x, -1e6, 1e6) && Range(value.y, -1e6, 1e6) && Range(value.z, -1e6, 1e6);
        internal static void Require(bool condition, string reason) { if (!condition) throw new ArgumentException(reason); }
    }
}
