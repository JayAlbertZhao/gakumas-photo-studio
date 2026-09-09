using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum CapturedLookAtMode
    {
        Off,
        Natural,
        Gaze,
        Cocchi,
    }

    /// <summary>
    /// The two CampusCustomLookAtEffector endpoints captured in pass192.
    /// Gaze is not a third authored profile: the original controller linearly
    /// mixes these endpoints with its current eye weight.
    /// </summary>
    [Serializable]
    public struct CapturedLookAtProfile
    {
        public float controllerEyeWeight;
        public float weight;
        public float eyesWeight;
        public float headWeight;
        public float bodyWeight;
        public float eyeInOutLimit;
        public float eyeUpLimit;
        public float eyeDownLimit;
        public float headLeftRightLimit;
        public float headFrontLimit;
        public float headBackLimit;
        public float headRollLeftRightLimit;
        public float neckLeftRightLimit;
        public float neckFrontLimit;
        public float neckBackLimit;
        public float neckRollLeftRightLimit;
        public float bodyLeftRightLimit;
        public float bodyFrontLimit;
        public float bodyBackLimit;
        public float bodyTwistLeftRightLimit;

        // Campus.Common.LookAt.LookAtDefine in the matching current build.
        public const float LookOutAngle = 80f;
        public const float LookAgainAngle = 60f;
        public const float EaseDuration = 1f;

        // CampusActorController.get_LookAtEyeSideCorrectionDeFactoWeight in
        // the matching build returns this literal. pass192 also records
        // customLookAtEyeCorrectionType=0 (AlwaysSideCorrection), so the
        // horizontal eye result is reduced to one quarter after humanoid IK.
        public const float EyeSideCorrectionWeight = 0.25f;

        public static readonly CapturedLookAtProfile Natural = new CapturedLookAtProfile
        {
            controllerEyeWeight = 1f,
            weight = 1f,
            eyesWeight = 0.649999976f,
            headWeight = 0.349000007f,
            bodyWeight = 0.00100000005f,
            eyeInOutLimit = 0.800000012f,
            eyeUpLimit = 0.0799999982f,
            eyeDownLimit = -0.0799999982f,
            headLeftRightLimit = 1f,
            headFrontLimit = 1f,
            headBackLimit = -1f,
            headRollLeftRightLimit = 0.699999988f,
            neckLeftRightLimit = 1f,
            neckFrontLimit = 1f,
            neckBackLimit = -1f,
            neckRollLeftRightLimit = 0.699999988f,
            bodyLeftRightLimit = 1.89999998f,
            bodyFrontLimit = 1.89999998f,
            bodyBackLimit = -1.89999998f,
            bodyTwistLeftRightLimit = 1.89999998f,
        };

        public static readonly CapturedLookAtProfile Cocchi = new CapturedLookAtProfile
        {
            controllerEyeWeight = 0f,
            weight = 1f,
            eyesWeight = 0.300000012f,
            headWeight = 0.600000024f,
            bodyWeight = 0.100000001f,
            eyeInOutLimit = 0.180000007f,
            eyeUpLimit = 0.300000012f,
            eyeDownLimit = -0.300000012f,
            headLeftRightLimit = 0.899999976f,
            headFrontLimit = 0.899999976f,
            headBackLimit = -0.899999976f,
            headRollLeftRightLimit = 0.899999976f,
            neckLeftRightLimit = 0.899999976f,
            neckFrontLimit = 0.899999976f,
            neckBackLimit = -0.899999976f,
            neckRollLeftRightLimit = 0.899999976f,
            bodyLeftRightLimit = 1.89999998f,
            bodyFrontLimit = 1.89999998f,
            bodyBackLimit = -1.89999998f,
            bodyTwistLeftRightLimit = 1.89999998f,
        };

        public static CapturedLookAtProfile Evaluate(float controllerEyeWeight)
        {
            float t = Mathf.Clamp01(controllerEyeWeight);
            CapturedLookAtProfile result = new CapturedLookAtProfile
            {
                controllerEyeWeight = t,
                weight = Mathf.Lerp(Cocchi.weight, Natural.weight, t),
                eyesWeight = Mathf.Lerp(Cocchi.eyesWeight, Natural.eyesWeight, t),
                headWeight = Mathf.Lerp(Cocchi.headWeight, Natural.headWeight, t),
                bodyWeight = Mathf.Lerp(Cocchi.bodyWeight, Natural.bodyWeight, t),
                eyeInOutLimit = Mathf.Lerp(Cocchi.eyeInOutLimit, Natural.eyeInOutLimit, t),
                eyeUpLimit = Mathf.Lerp(Cocchi.eyeUpLimit, Natural.eyeUpLimit, t),
                eyeDownLimit = Mathf.Lerp(Cocchi.eyeDownLimit, Natural.eyeDownLimit, t),
                headLeftRightLimit = Mathf.Lerp(Cocchi.headLeftRightLimit, Natural.headLeftRightLimit, t),
                headFrontLimit = Mathf.Lerp(Cocchi.headFrontLimit, Natural.headFrontLimit, t),
                headBackLimit = Mathf.Lerp(Cocchi.headBackLimit, Natural.headBackLimit, t),
                headRollLeftRightLimit = Mathf.Lerp(Cocchi.headRollLeftRightLimit, Natural.headRollLeftRightLimit, t),
                neckLeftRightLimit = Mathf.Lerp(Cocchi.neckLeftRightLimit, Natural.neckLeftRightLimit, t),
                neckFrontLimit = Mathf.Lerp(Cocchi.neckFrontLimit, Natural.neckFrontLimit, t),
                neckBackLimit = Mathf.Lerp(Cocchi.neckBackLimit, Natural.neckBackLimit, t),
                neckRollLeftRightLimit = Mathf.Lerp(Cocchi.neckRollLeftRightLimit, Natural.neckRollLeftRightLimit, t),
                bodyLeftRightLimit = Mathf.Lerp(Cocchi.bodyLeftRightLimit, Natural.bodyLeftRightLimit, t),
                bodyFrontLimit = Mathf.Lerp(Cocchi.bodyFrontLimit, Natural.bodyFrontLimit, t),
                bodyBackLimit = Mathf.Lerp(Cocchi.bodyBackLimit, Natural.bodyBackLimit, t),
                bodyTwistLeftRightLimit = Mathf.Lerp(Cocchi.bodyTwistLeftRightLimit, Natural.bodyTwistLeftRightLimit, t),
            };
            return result;
        }
    }
}
