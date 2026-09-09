using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VL.FaceSystem;

namespace GakumasPhotoMode
{
    public sealed class FaceMotionLibrary
    {
        private readonly Dictionary<string, FaceMotionClipRecord> _motions = new Dictionary<string, FaceMotionClipRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FaceMotionClipRecord> _photoExpressions = new List<FaceMotionClipRecord>();
        // Normal photo-mode playback samples every rendered frame.  Keep this
        // storage owned by the library instead of generating a 192-float GC
        // allocation for every evaluation.
        private readonly float[] _sampleBuffer = new float[192];
        private FaceMotionClipRecord _current;
        private FaceMotionClipRecord _photoExpression;
        private int _photoExpressionIndex;

        public int MotionCount { get { return _motions.Count; } }
        public string CurrentMotion { get { return _current == null ? null : _current.motion; } }
        public int PhotoExpressionCount { get { return _photoExpressions.Count + 1; } }
        public int PhotoExpressionIndex { get { return _photoExpressionIndex; } }
        public string CurrentPhotoExpressionLabel
        {
            get { return _photoExpression == null ? "FOLLOW MOTION" : "ORIGINAL PHOTO " + (_photoExpression.label ?? _photoExpression.motion); }
        }
        public string CurrentPhotoExpressionMotion
        {
            get { return _photoExpression == null ? null : _photoExpression.motion; }
        }
        public bool CurrentPhotoExpressionDisablesAutoBlink
        {
            get { return _photoExpression != null && _photoExpression.disable_auto_blink; }
        }

        public void Load(string stagingRoot)
        {
            string path = Path.Combine(stagingRoot, "face-motions.json");
            if (!File.Exists(path))
            {
                Debug.LogWarning("[PhotoMode] Face motion file missing: " + path);
                return;
            }
            FaceMotionManifest manifest = JsonUtility.FromJson<FaceMotionManifest>(File.ReadAllText(path));
            if (manifest == null || manifest.motions == null) return;
            foreach (FaceMotionClipRecord motion in manifest.motions)
            {
                if (motion != null && !string.IsNullOrEmpty(motion.motion)) _motions[motion.motion] = motion;
            }
            Debug.Log(string.Format("[PhotoMode] Face motion curves ready: {0} clips", _motions.Count));

            string photoPath = Path.Combine(stagingRoot, "photo-facial-motions.json");
            if (!File.Exists(photoPath))
            {
                Debug.LogWarning("[PhotoMode] Original photo facial catalog missing: " + photoPath);
                return;
            }
            FaceMotionManifest photoManifest = JsonUtility.FromJson<FaceMotionManifest>(File.ReadAllText(photoPath));
            if (photoManifest != null && photoManifest.motions != null)
            {
                _photoExpressions.AddRange(photoManifest.motions
                    .Where(value => value != null && !string.IsNullOrEmpty(value.motion))
                    .OrderBy(value => value.number));
            }
            Debug.Log(string.Format(
                "[PhotoMode] Original PhotoFacialMotionGroup ready: {0} expressions", _photoExpressions.Count));
        }

        public bool Select(string motionName)
        {
            return _motions.TryGetValue(motionName, out _current);
        }

        public bool SelectPhotoExpression(int index)
        {
            _photoExpressionIndex = (index % PhotoExpressionCount + PhotoExpressionCount) % PhotoExpressionCount;
            _photoExpression = _photoExpressionIndex == 0
                ? null
                : _photoExpressions[_photoExpressionIndex - 1];
            return true;
        }

        public bool SelectPhotoExpression(string motionName)
        {
            if (string.IsNullOrEmpty(motionName)) return SelectPhotoExpression(0);
            int index = _photoExpressions.FindIndex(value =>
                string.Equals(value.motion, motionName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            return SelectPhotoExpression(index + 1);
        }

        public void Sample(double seconds, VLActorFaceModel target)
        {
            Sample(seconds, target, true, true);
        }

        public void Sample(double seconds, VLActorFaceModel target, bool repeat)
        {
            Sample(seconds, target, repeat, true);
        }

        public void Sample(double seconds, VLActorFaceModel target, bool repeat, bool allowPhotoExpression)
        {
            FaceMotionClipRecord selected = allowPhotoExpression && _photoExpression != null
                ? _photoExpression
                : _current;
            if (selected == null || target == null) return;
            target.ClearWeights();
            Sample(selected, seconds, repeat, _sampleBuffer);
            for (int index = 0; index < _sampleBuffer.Length; index++)
                if (Mathf.Abs(_sampleBuffer[index]) > 0.0000001f)
                    target.SetWeight(index, _sampleBuffer[index]);
        }

        public bool Sample(string motionName, double seconds, bool repeat, float[] target)
        {
            if (target == null || target.Length < 192) return false;
            FaceMotionClipRecord selected;
            if (string.IsNullOrEmpty(motionName) || !_motions.TryGetValue(motionName, out selected))
            {
                Array.Clear(target, 0, target.Length);
                return false;
            }
            Sample(selected, seconds, repeat, target);
            return true;
        }

        private static void Sample(
            FaceMotionClipRecord selected, double seconds, bool repeat, float[] target)
        {
            Array.Clear(target, 0, target.Length);
            if (selected == null || selected.curves == null) return;
            float time = (float)seconds;
            if (selected.length > 0.001f)
                time = repeat ? Mathf.Repeat(time, selected.length) : Mathf.Clamp(time, 0f, selected.length);
            foreach (FaceMotionCurveRecord curve in selected.curves)
            {
                if (curve == null || curve.index < 0 || curve.index >= target.Length) continue;
                target[curve.index] = Evaluate(curve, time) * 0.01f;
            }
        }

        private static float Evaluate(FaceMotionCurveRecord curve, float time)
        {
            FaceMotionKeyRecord[] keys = curve.keys;
            if (keys == null || keys.Length == 0) return curve.constant;
            if (time <= keys[0].time) return keys[0].value;
            if (time >= keys[keys.Length - 1].time) return keys[keys.Length - 1].value;
            for (int index = 0; index < keys.Length - 1; index++)
            {
                FaceMotionKeyRecord left = keys[index];
                FaceMotionKeyRecord right = keys[index + 1];
                if (time > right.time) continue;
                float duration = Mathf.Max(right.time - left.time, 0.0001f);
                float t = Mathf.Clamp01((time - left.time) / duration);
                float t2 = t * t;
                float t3 = t2 * t;
                float h00 = 2f * t3 - 3f * t2 + 1f;
                float h10 = t3 - 2f * t2 + t;
                float h01 = -2f * t3 + 3f * t2;
                float h11 = t3 - t2;
                float inSlope = float.IsInfinity(right.inSlope) ? 0f : right.inSlope;
                float outSlope = float.IsInfinity(left.outSlope) ? 0f : left.outSlope;
                return h00 * left.value + h10 * duration * outSlope + h01 * right.value + h11 * duration * inSlope;
            }
            return keys[keys.Length - 1].value;
        }

        [Serializable]
        private sealed class FaceMotionManifest
        {
            public string schema_version;
            public string unity_version;
            public FaceMotionClipRecord[] motions;
        }

        [Serializable]
        private sealed class FaceMotionClipRecord
        {
            public string motion;
            public string clip;
            public float length;
            public int number;
            public string label;
            public bool disable_auto_blink;
            public FaceMotionCurveRecord[] curves;
        }

        [Serializable]
        private sealed class FaceMotionCurveRecord
        {
            public int index;
            public float constant;
            public FaceMotionKeyRecord[] keys;
        }

        [Serializable]
        private sealed class FaceMotionKeyRecord
        {
            public float time;
            public float value;
            public float inSlope;
            public float outSlope;
        }
    }
}
