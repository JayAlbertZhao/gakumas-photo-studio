using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum FaceDecalChannel
    {
        PositionX, PositionY, PositionZ, RotationX, RotationY, RotationZ, ScaleX, ScaleY, ScaleZ,
        SizeX, SizeY, SizeZ, PivotX, PivotY, PivotZ, UvScaleX, UvScaleY, UvBiasX, UvBiasY,
        UvRotation, TintR, TintG, TintB, TintA, Opacity, EdgeFeather, AngleStart, AngleEnd
    }
    public enum FaceDecalInterpolation { Hold, Linear, Hermite }
    public enum FaceDecalWrap { Clamp, Loop, PingPong }

    /// <summary>Typed independent keyframes, optional JSON serialization through JsonUtility.
    /// No wall clock, retained playback state, private format or Unity Playable requirement.</summary>
    [Serializable]
    public sealed class FaceDecalAnimation
    {
        [Serializable]
        public struct Key
        {
            public float seconds, value, inTangent, outTangent;
            public FaceDecalInterpolation interpolation;
            public Key(float seconds, float value, FaceDecalInterpolation interpolation = FaceDecalInterpolation.Linear)
            { this.seconds = seconds; this.value = value; this.interpolation = interpolation; inTangent = outTangent = 0; }
        }
        [Serializable]
        public sealed class Track { public FaceDecalChannel channel; public Key[] keys = Array.Empty<Key>(); }
        public float duration = 1;
        public FaceDecalWrap wrap;
        public Track[] tracks = Array.Empty<Track>();

        public bool TrySample(FaceDecalPose baseline, double seconds, out FaceDecalPose pose, out string reason)
        {
            pose = null; reason = null;
            try
            {
                if (baseline == null || !Finite(seconds) || !Range(duration, .000001f, 1e6) || (int)wrap < 0 || (int)wrap > 2 || tracks == null || tracks.Length > 28)
                    throw new ArgumentException("Invalid animation clock, baseline or tracks.");
                double time = seconds;
                if (wrap == FaceDecalWrap.Clamp) time = Math.Max(0, Math.Min(duration, time));
                else
                {
                    double cycle = duration * (wrap == FaceDecalWrap.PingPong ? 2.0 : 1.0);
                    time %= cycle; if (time < 0) time += cycle;
                    if (time > duration) time = cycle - time;
                }
                var sampled = baseline.Copy(); var channels = new HashSet<FaceDecalChannel>(); int total = 0;
                foreach (var track in tracks)
                {
                    if (track == null || (int)track.channel < 0 || (int)track.channel > 27 || !channels.Add(track.channel) || track.keys == null || track.keys.Length == 0 || (total += track.keys.Length) > 8192)
                        throw new ArgumentException("Duplicate/invalid channel or key capacity exceeded.");
                    float previous = -1;
                    foreach (var key in track.keys)
                    {
                        if (!Range(key.seconds, 0, duration) || key.seconds <= previous || !Range(key.value, -1e6, 1e6) ||
                            !Range(key.inTangent, -1e6, 1e6) || !Range(key.outTangent, -1e6, 1e6) || (int)key.interpolation < 0 || (int)key.interpolation > 2)
                            throw new ArgumentException("Invalid key value/order/interpolation.");
                        previous = key.seconds;
                    }
                    var keys = track.keys; double value = keys[0].value;
                    for (int i = 0; i < keys.Length && time >= keys[i].seconds; i++)
                    {
                        value = keys[i].value;
                        if (i + 1 == keys.Length || time >= keys[i + 1].seconds) continue;
                        var a = keys[i]; var b = keys[i + 1]; double dt = b.seconds - (double)a.seconds, t = (time - a.seconds) / dt;
                        if (a.interpolation == FaceDecalInterpolation.Linear) value = a.value + (b.value - (double)a.value) * t;
                        else if (a.interpolation == FaceDecalInterpolation.Hermite)
                            value = (2*t*t*t-3*t*t+1)*a.value + (t*t*t-2*t*t+t)*dt*a.outTangent + (-2*t*t*t+3*t*t)*b.value + (t*t*t-t*t)*dt*b.inTangent;
                        break;
                    }
                    Set(sampled, track.channel, (float)value);
                }
                // Validate the resulting pose only after every channel is sampled. A partial
                // update must not escape when an overshooting curve creates an invalid size.
                FaceDecalRenderer.Validate(new FaceDecalProjector.Data(Matrix4x4.identity, sampled));
                pose = sampled; return true;
            }
            catch (Exception error) { reason = error.Message; return false; }
        }

        private static void Set(FaceDecalPose p, FaceDecalChannel channel, float value)
        {
            int c = (int)channel;
            if (c < 3) p.positionOffset[c] = value;
            else if (c < 6) p.rotationDegrees[c-3] = value;
            else if (c < 9) p.scale[c-6] = value;
            else if (c < 12) p.size[c-9] = value;
            else if (c < 15) p.pivot[c-12] = value;
            else if (c < 17) p.uvScale[c-15] = value;
            else if (c < 19) p.uvBias[c-17] = value;
            else if (c == 19) p.uvRotationDegrees = value;
            else if (c < 24) p.tint[c-20] = value;
            else if (c == 24) p.opacity = value;
            else if (c == 25) p.edgeFeather = value;
            else if (c == 26) p.angleFadeStart = value;
            else p.angleFadeEnd = value;
        }
        private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        private static bool Range(double x, double lo, double hi) => Finite(x) && x >= lo && x <= hi;
    }
}
