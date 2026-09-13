using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum LensFlareShape { Disc, Ring, Polygon, Star, Texture }
    public enum LensFlareResolution { Full = 1, Half = 2, Quarter = 4 }

    /// <summary>Independent authored lens artifacts. No original or third-party flare data.</summary>
    [Serializable]
    public sealed class LensFlareSettings
    {
        public const int MaximumEmitters = 32, MaximumElements = 1024;
        public bool enabled;
        public LensFlareResolution resolution = LensFlareResolution.Half;
        // Stratified disk: 1, 2, 4 or 8 cells per axis, not random per-frame samples.
        public int occlusionSamplesPerAxis = 4;
        public Texture2D atlas;
        public LensFlareEmitter[] emitters = Array.Empty<LensFlareEmitter>();

        internal bool Validate(out string reason)
        {
            reason = null;
            int scale = (int)resolution, grid = occlusionSamplesPerAxis;
            if ((scale != 1 && scale != 2 && scale != 4) ||
                (grid != 1 && grid != 2 && grid != 4 && grid != 8) || emitters == null || emitters.Length > MaximumEmitters)
            { reason = "Invalid flare resolution, disk sampling or emitter budget"; return false; }
            int elements = 0;
            foreach (var emitter in emitters)
            {
                if (emitter == null || !emitter.enabled) continue;
                if (!emitter.Validate(atlas, out reason)) return false;
                elements += emitter.elements.Length;
            }
            if (elements > MaximumElements) { reason = "Flare element budget exceeded"; return false; }
            return true;
        }
    }

    [Serializable]
    public sealed class LensFlareEmitter
    {
        public bool enabled = true;
        public Vector3 position;
        public Vector3 linearRadiance = Vector3.one;
        public float intensity = 1, scale = 1;
        public bool occlusion = true;
        public float occlusionRadius = .1f, depthBias = .02f;
        // Missing offscreen geometry is unknown. Default conservatively counts it as blocked.
        public float outsideScreenVisibility;
        public float offscreenMargin;
        public float fadeStartDistance = 100, fadeEndDistance = 200;
        public bool directionalAttenuation;
        public Quaternion rotation = Quaternion.identity;
        public float innerAngle = 30, outerAngle = 60;
        public float pulseAmplitude, pulseFrequency = 1, pulsePhaseDegrees;
        public LensFlareElement[] elements = Array.Empty<LensFlareElement>();

        internal bool Validate(Texture2D atlas, out string reason)
        {
            reason = null;
            if (!FogVolumeSettings.Position(position) || !Range(intensity, 0, 16) || !Range(scale, .0001f, 16) ||
                !Range(occlusionRadius, 0, 10000) || !Range(depthBias, 0, 10000) ||
                !Range(outsideScreenVisibility, 0, 1) || !Range(offscreenMargin, 0, 1) ||
                !Range(fadeStartDistance, 0, 1e6f) || !Range(fadeEndDistance, fadeStartDistance + .0001f, 1e6f) ||
                !Range(pulseAmplitude, 0, 1) || !Range(pulseFrequency, 0, 1000) || !Range(pulsePhaseDegrees, -1e6f, 1e6f) ||
                elements == null || elements.Length > LensFlareSettings.MaximumElements)
            { reason = "Invalid flare emitter, fade, animation or elements"; return false; }
            for (int i = 0; i < 3; i++) if (!Range(linearRadiance[i], 0, 65504))
            { reason = "Flare radiance must be finite nonnegative linear RGB"; return false; }
            if (directionalAttenuation && (!Range(Quaternion.Dot(rotation, rotation), 1e-8f, 1e8f) ||
                !Range(outerAngle, .1f, 179) || !Range(innerAngle, 0, outerAngle)))
            { reason = "Invalid flare emitting cone"; return false; }
            foreach (var element in elements)
                if (element != null && element.enabled && !element.Validate(atlas, out reason)) return false;
            return true;
        }
        internal static bool Range(float value, float low, float high) => FogVolumeSettings.Range(value, low, high);
    }

    [Serializable]
    public sealed class LensFlareElement
    {
        public bool enabled = true;
        public LensFlareShape shape;
        // 0 is the source, 1 is the optical center, 2 is the mirrored ghost.
        public float axisPosition;
        // Both dimensions/offsets are fractions of viewport HEIGHT, preserving aspect ratio.
        public Vector2 halfSize = new Vector2(.08f, .08f), offset;
        public Vector3 linearTint = Vector3.one;
        public float intensity = 1, rotationDegrees, rotationSpeed;
        public bool alignToAxis;
        public float softness = .5f, falloffExponent = 1;
        public float ringRadius = .65f, ringWidth = .2f;
        public int sides = 6;
        public RectInt atlasRect = new RectInt(0, 0, 1, 1);

        internal bool Validate(Texture2D atlas, out string reason)
        {
            reason = null;
            if ((int)shape < 0 || (int)shape > 4 || !LensFlareEmitter.Range(axisPosition, -8, 8) ||
                !LensFlareEmitter.Range(intensity, 0, 16) || !LensFlareEmitter.Range(rotationDegrees, -1e6f, 1e6f) ||
                !LensFlareEmitter.Range(rotationSpeed, -10000, 10000) || !LensFlareEmitter.Range(softness, 0, 1) ||
                !LensFlareEmitter.Range(falloffExponent, .25f, 16) || !LensFlareEmitter.Range(ringRadius, 0, 1) ||
                !LensFlareEmitter.Range(ringWidth, .001f, 1) || ringRadius + ringWidth > 1 || sides < 3 || sides > 16)
            { reason = "Invalid flare element shape, axis or animation"; return false; }
            for (int i = 0; i < 2; i++) if (!LensFlareEmitter.Range(halfSize[i], .0001f, 4) || !LensFlareEmitter.Range(offset[i], -4, 4))
            { reason = "Invalid flare element screen size or offset"; return false; }
            for (int i = 0; i < 3; i++) if (!LensFlareEmitter.Range(linearTint[i], 0, 16))
            { reason = "Invalid flare linear tint"; return false; }
            if (shape == LensFlareShape.Texture && (atlas == null || atlasRect.x < 0 || atlasRect.y < 0 ||
                atlasRect.width < 1 || atlasRect.height < 1 || (long)atlasRect.x + atlasRect.width > atlas.width || (long)atlasRect.y + atlasRect.height > atlas.height))
            { reason = "Textured flare requires an in-bounds pixel rectangle in the caller's atlas"; return false; }
            return true;
        }
    }
}
