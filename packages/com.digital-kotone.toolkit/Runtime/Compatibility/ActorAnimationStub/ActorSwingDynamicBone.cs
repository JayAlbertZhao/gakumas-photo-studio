using System;
using UnityEngine;

namespace ActorAnimation
{
    // Field-for-field serialization shell for ActorAnimation.InitialTransform.
    // Unity.Mathematics.quaternion serializes as quaternion.value.{x,y,z,w}; a
    // UnityEngine.Quaternion field only exposes {x,y,z,w} and silently read as
    // zero from the original bundles.  Keep the nested value node explicitly.
    [Serializable]
    public struct SerializedFloat4
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public Quaternion ToQuaternion()
        {
            Quaternion result = new Quaternion(x, y, z, w);
            float magnitude = Mathf.Sqrt(
                result.x * result.x + result.y * result.y +
                result.z * result.z + result.w * result.w);
            return magnitude > 0.000001f
                ? new Quaternion(
                    result.x / magnitude, result.y / magnitude,
                    result.z / magnitude, result.w / magnitude)
                : Quaternion.identity;
        }
    }

    [Serializable]
    public struct SerializedQuaternion
    {
        public SerializedFloat4 value;

        public Quaternion ToQuaternion()
        {
            return value.ToQuaternion();
        }
    }

    [Serializable]
    public struct InitialTransform
    {
        public Vector3 localPosition;
        public SerializedQuaternion localRotation;
        public Vector3 position;
        public SerializedQuaternion rotation;
    }

    // Serialization-compatible shell for the original hair-bone settings.
    // Simulation is performed centrally by GakumasPhotoMode.HairDynamicsSystem.
    public sealed class ActorSwingDynamicBone : MonoBehaviour
    {
        public SwingCollider dynamicCollider;
        public int resetType;
        public int dynamicType;
        public float damping = 0.35f;
        public float stiffness = 0.01f;
        public float spring = 0.3f;
        public float pendulum = 0.004f;
        public float pendulumRange = 1f;
        public float mass = 1f;
        public float axisAddXToY;
        public float axisAddXToZ;
        public float wind = 1f;
        public bool useWindGlobalForce = true;
        public SwingLimitInfo limitInfo;
        public float rootWeight = 0.5f;
        public float seatDynamicCorrection;
        public SwingReferenceLimitInfo referenceLimitInfo;
        public InitialTransform modelingTransform;
    }

    // Serialization-compatible shell for the original bilateral upper-torso
    // solver.  This component is separate from ActorSwingDynamicBone: one job
    // owns both left/right BreastSkin endpoints, blends their displacement,
    // optionally corrects against the forearms, and applies a shared limit.
    // Runtime evaluation is performed centrally by
    // GakumasPhotoMode.BodySoftTissueDynamicsSystem.
    public sealed class ActorSwingBreastBone : MonoBehaviour
    {
        public SwingCollider breastCollider;
        public float damping;
        public float stiffness;
        public float spring;
        public float pendulum;
        public float pendulumRange;
        public float average;
        public SwingLimitInfo limitInfo;
        public float rootWeight;
        public bool useArmCorrection;
        public Transform leftLowerArm;
        public Transform rightLowerArm;
        public Transform leftBreast;
        public Transform rightBreast;
        public Transform leftBreastEnd;
        public Transform rightBreastEnd;
        public AnimationCurve upCurve;
        public AnimationCurve sideCurve;
        public InitialTransform modelingLeftTransform;
        public InitialTransform modelingRightTransform;
        public InitialTransform modelingLeftEndTransform;
        public InitialTransform modelingRightEndTransform;
    }

    [Serializable]
    public sealed class SwingCollider
    {
        public int type;
        public int collisionMask;
        public Vector3 vector3_A;
        public Vector3 vector3_B;
        public float float_A;
        public float float_B;
        public int seatDynamicCorrectionDisableCollisionMask;
    }

    [Serializable]
    public sealed class SwingLimitInfo
    {
        // Original ActorAnimation.LimitInfo stores this as an Int32, not a bool.
        public int useLimit;
        public Vector2Int axisX;
        public Vector2Int axisY;
        public Vector2Int axisZ;
    }

    [Serializable]
    public sealed class SwingReferenceLimitInfo
    {
        public Transform bone;
        public SerializableBool3 min;
        public SerializableBool3 max;
    }

    [Serializable]
    public struct SerializableBool3
    {
        public bool x;
        public bool y;
        public bool z;
    }

    public sealed class ActorAnimationQuartzDriverHairBone : MonoBehaviour
    {
        public QuartzHairSetting setting;
    }

    [Serializable]
    public sealed class QuartzHairSetting
    {
        public int translateConnectionAxis;
        public Vector3 headTranslateCoefficient;
        public Vector3 headTranslateLimitMin;
        public Vector3 headTranslateLimitMax;
        public int rotationOrder;
        public Vector3 headRotateCoefficient;
        public Vector3 headRotateLimitMin;
        public Vector3 headRotateLimitMax;
        public int rotateConnectionAxis;
        public Vector3 neckRotateCoefficient;
        public Vector3 neckRotateLimitMin;
        public Vector3 neckRotateLimitMax;
        public Transform referenceHeadBone;
        public Transform referenceNeckBone;
        public int composeType;
    }

    public sealed class ActorSwingStaticBone : MonoBehaviour
    {
        public SwingCollider staticCollider;
    }

    public sealed class ActorAnimationQuartzDriverSkirtBone : MonoBehaviour
    {
        public QuartzSkirtSetting setting;
    }

    // The production animation job uses these two serialized helpers to spread
    // humanoid arm/forearm twist across the deformation skeleton.  They are not
    // ordinary swing bones: sleeve roots are parented below the corresponding
    // *_Roll_H transforms, so leaving these scripts unresolved makes the sleeve
    // skin stay in the bind-roll frame while the arm animates.
    public sealed class ActorAnimationQuartzDriverHumanoidArmBone : MonoBehaviour
    {
        public QuartzHumanoidArmSetting setting;
    }

    [Serializable]
    public sealed class QuartzHumanoidArmSetting
    {
        public int humanPartDof;
        public float coefficient;
    }

    public sealed class ActorAnimationQuartzDriverHumanoidHandBone : MonoBehaviour
    {
        public QuartzHumanoidHandSetting setting;
    }

    [Serializable]
    public sealed class QuartzHumanoidHandSetting
    {
        public int humanPartDof;
        public float coefficient;
    }

    // The upper-leg helpers are driven from Unity's humanoid FrontBack muscle.
    // Keep these shells separate from ActorSwing: the production component is a
    // Quartz pose driver and owns no temporal simulation state.
    public sealed class ActorAnimationQuartzDriverHumanoidUpLegBone : MonoBehaviour
    {
        public QuartzHumanoidUpLegSetting setting;
    }

    [Serializable]
    public sealed class QuartzHumanoidUpLegSetting
    {
        public int humanPartDof;
        public float coefficient;
    }

    // Serialization shell for ActorAnimationQuartzDriverRotationSetting.  The
    // source bundle stores referenceBone as a GameObject PPtr (not Transform),
    // so retaining the exact field type is required for the reference to load.
    public sealed class ActorAnimationQuartzDriverRotationBone : MonoBehaviour
    {
        public QuartzRotationSetting setting;
    }

    // Serialization shells for the garment pose jobs used by long sleeves,
    // arm frills, and the waist-to-thigh helper.  The shipped settings store
    // all references as GameObject PPtrs; using Transform here would leave the
    // references unresolved when the original prefab is instantiated.
    public sealed class ActorAnimationQuartzDriverFrillBone : MonoBehaviour
    {
        public ActorAnimationQuartzDriverFrillSetting setting;
    }

    [Serializable]
    public sealed class ActorAnimationQuartzDriverFrillSetting
    {
        public int rotationOrder;
        public Vector3 outerMinusCoefficient;
        public Vector3 innerCoefficient;
        public Vector3 outerPlusCoefficient;
        public Vector3 switchMinus;
        public Vector3 switchPlus;
        public int connectionAxis;
        public int decomposeType;
        public int composeType;
        public GameObject referenceBone;
    }

    public sealed class ActorAnimationQuartzDriverFurisodeBone : MonoBehaviour
    {
        public ActorAnimationQuartzDriverFurisodeSetting setting;
    }

    [Serializable]
    public sealed class ActorAnimationQuartzDriverFurisodeSetting
    {
        public GameObject referenceFurisodeOffsetBone;
        public GameObject referenceSpineBone;
        public GameObject referenceForearmBone;
        public GameObject referenceHandBone;
        public Vector3 backWay;
        public float apexAngle;
        public float coefficient;
        public float min;
        public float max;
    }

    public sealed class ActorAnimationQuartzDriverWaistBone : MonoBehaviour
    {
        public ActorAnimationQuartzDriverWaistSetting setting;
    }

    [Serializable]
    public sealed class ActorAnimationQuartzDriverWaistSetting
    {
        public float weight;
        public GameObject referenceWaistOffsetBone;
        public GameObject referenceThighOffsetBone;
    }

    public sealed class ActorAnimationQuartzDriverHumanoidSleeveBone : MonoBehaviour
    {
        public ActorAnimationQuartzDriverHumanoidSleeveSetting setting;
    }

    [Serializable]
    public sealed class ActorAnimationQuartzDriverHumanoidSleeveSetting
    {
        public Vector3 outerMinusCoefficient;
        public Vector3 innerCoefficient;
        public Vector3 outerPlusCoefficient;
        public Vector3 switchMinus;
        public Vector3 switchPlus;
        public GameObject referenceBone;
        public int composeType;
        public int decomposeType;
        public int humanPartDof;
    }

    public sealed class ActorAnimationQuartzDriverPonchoBone : MonoBehaviour
    {
        public ActorAnimationQuartzDriverPonchoSetting setting;
    }

    [Serializable]
    public sealed class ActorAnimationQuartzDriverPonchoSetting
    {
        public float lerpCoefficient;
        public float innerLimit;
        public float outerLimit;
        public Vector3 axisWay;
        public GameObject referenceArmBone;
        public GameObject referencePonchoOffsetBone;
        public GameObject referenceArmOffsetBone;
        public GameObject referenceBodyOffsetBone;
        public GameObject referenceInnerOffsetBone;
        public GameObject referenceOuterOffsetBone;
    }

    [Serializable]
    public sealed class QuartzRotationSetting
    {
        public int rotationOrder;
        public Vector3 limitMin = new Vector3(-180f, -180f, -180f);
        public Vector3 limitMax = new Vector3(180f, 180f, 180f);
        public Vector3 coefficient = Vector3.one;
        public int connectionAxis;
        public int decomposeType;
        public int composeType;
        public GameObject referenceBone;
    }

    [Serializable]
    public sealed class QuartzSkirtSetting
    {
        public int rotationOrder;
        public Vector3 innerCoefficient;
        public Vector3 outerCoefficient = Vector3.one;
        public Vector3 limitMin = new Vector3(-180f, -180f, -180f);
        public Vector3 limitMax = new Vector3(180f, 180f, 180f);
        public int connectionAxis;
        public Transform referenceBone;
    }

    public sealed class ActorSwingChain : MonoBehaviour
    {
        public ActorSwingDynamicBone[] rootBones;
        public SwingChainLayers chains;
    }

    [Serializable]
    public sealed class SwingChainLayers
    {
        public SwingChainLayer[] layers;
    }

    [Serializable]
    public sealed class SwingChainLayer
    {
        public bool active;
        public bool around;
        public float radius;
        public float smoothing;
        public ActorSwingDynamicBone[] bones;
    }
}
