using System;
using System.Collections.Generic;
using UnityEngine;

namespace VL
{
    // Compatibility shell for the serialized motion definition stored in the
    // original bundle. Only fields needed to recover its AnimationClip PPtrs
    // are represented here.
    public sealed class ActorMotionDefine : MonoBehaviour
    {
        public string layerName;
        public bool isAdditive;
        public bool ignoreAdditive;
        public bool applyPlayableIK;
        public bool applyFootIK;
        public List<MotionEffect> effects = new List<MotionEffect>();
        public bool isLoopMotionEffect;
        public MotionAnimation baseAnimation;
        public MotionAnimation faceAnimation;
        public List<PropConstraintData> propConstraints = new List<PropConstraintData>();
        public bool enableSeatedDynamicCorrection;
    }

    [Serializable]
    public sealed class MotionAnimation
    {
        public AnimationClip clip;
        public AnimationClip customClip;
        public AvatarMask mask;
        public bool applyPlayableIK;
        public bool applyFootIK;
        public List<MotionBinding> bindings = new List<MotionBinding>();
    }

    [Serializable]
    public sealed class MotionBinding
    {
        public string name;
        public string path;
        public string type;
        public List<string> properties = new List<string>();
    }

    [Serializable]
    public sealed class MotionEffect
    {
        public UnityEngine.Object effect;
        public float startTime;
        public float duration;
    }

    // Exact serialized layout recovered from the current GameAssembly
    // (VL.PropConstraintData).  Keeping this shell field-for-field compatible
    // lets Unity deserialize the per-motion prop binding contract directly
    // from the original AssetBundle.
    [Serializable]
    public sealed class PropConstraintData
    {
        public string prop;
        public bool isAdaptScale;
        public bool isFieldConstraint;
        public string[] constraintTargets;
        public int executeType;
    }
}
