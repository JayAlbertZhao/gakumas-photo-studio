using System;
using System.Collections.Generic;
using System.Linq;
using ActorAnimation;
using Unity.Mathematics;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Runtime replay of the non-temporal Quartz garment jobs.  These jobs are
    /// evaluated after the Animator and before ActorSwing: Frill maps a
    /// reference rotation through authored piecewise coefficients, Furisode
    /// follows the hand/forearm pose in the sleeve-offset frame, Waist places
    /// a helper between the waist and thigh references, and Poncho aims each
    /// authored panel between arm/body guide points while applying its
    /// arm-angle position offset. HumanoidSleeve removes the authored forearm
    /// twist contribution from its reference bone, decomposes the remaining
    /// rotation to the native roll/pitch/yaw basis, and applies the same
    /// piecewise coefficient mapper used by Frill.
    /// </summary>
    [DefaultExecutionOrder(720)]
    public sealed class QuartzGarmentDeformationSystem : MonoBehaviour
    {
        private readonly List<FrillDriver> _frills = new List<FrillDriver>();
        private readonly List<FurisodeDriver> _furisodes = new List<FurisodeDriver>();
        private readonly List<WaistDriver> _waists = new List<WaistDriver>();
        private readonly List<PonchoDriver> _ponchos = new List<PonchoDriver>();
        private readonly List<HumanoidSleeveDriver> _sleeves =
            new List<HumanoidSleeveDriver>();
        private HumanPoseHandler _poseHandler;
        private HumanPose _pose;
        private bool _initialized;
        private bool _dumpState;

        public int FrillCount { get { return _frills.Count; } }
        public int FurisodeCount { get { return _furisodes.Count; } }
        public int WaistCount { get { return _waists.Count; } }
        public int PonchoCount { get { return _ponchos.Count; } }
        public int HumanoidSleeveCount { get { return _sleeves.Count; } }

        public void Initialize(Animator animator)
        {
            DisposePoseHandler();
            _frills.Clear();
            _furisodes.Clear();
            _waists.Clear();
            _ponchos.Clear();
            _sleeves.Clear();
            _initialized = false;

            string[] arguments = Environment.GetCommandLineArgs();
            if (arguments.Contains("--disable-quartz-garment"))
            {
                Debug.Log("[QuartzGarment] Disabled by diagnostic command line");
                return;
            }

            foreach (ActorAnimationQuartzDriverFrillBone component in
                     GetComponentsInChildren<ActorAnimationQuartzDriverFrillBone>(true))
            {
                component.enabled = false;
                ActorAnimationQuartzDriverFrillSetting setting = component.setting;
                if (setting == null || setting.referenceBone == null) continue;
                _frills.Add(new FrillDriver(
                    component.transform, setting.referenceBone.transform, setting));
            }

            foreach (ActorAnimationQuartzDriverFurisodeBone component in
                     GetComponentsInChildren<ActorAnimationQuartzDriverFurisodeBone>(true))
            {
                component.enabled = false;
                ActorAnimationQuartzDriverFurisodeSetting setting = component.setting;
                if (setting == null || setting.referenceFurisodeOffsetBone == null ||
                    setting.referenceSpineBone == null ||
                    setting.referenceForearmBone == null ||
                    setting.referenceHandBone == null)
                    continue;
                _furisodes.Add(new FurisodeDriver(
                    component.transform,
                    setting.referenceFurisodeOffsetBone.transform,
                    setting.referenceSpineBone.transform,
                    setting.referenceForearmBone.transform,
                    setting.referenceHandBone.transform,
                    setting));
            }

            foreach (ActorAnimationQuartzDriverWaistBone component in
                     GetComponentsInChildren<ActorAnimationQuartzDriverWaistBone>(true))
            {
                component.enabled = false;
                ActorAnimationQuartzDriverWaistSetting setting = component.setting;
                if (setting == null || setting.referenceWaistOffsetBone == null ||
                    setting.referenceThighOffsetBone == null)
                    continue;
                _waists.Add(new WaistDriver(
                    component.transform,
                    setting.referenceWaistOffsetBone.transform,
                    setting.referenceThighOffsetBone.transform,
                    setting));
            }

            foreach (ActorAnimationQuartzDriverPonchoBone component in
                     GetComponentsInChildren<ActorAnimationQuartzDriverPonchoBone>(true))
            {
                component.enabled = false;
                ActorAnimationQuartzDriverPonchoSetting setting = component.setting;
                if (setting == null || setting.referenceArmBone == null ||
                    setting.referencePonchoOffsetBone == null ||
                    setting.referenceArmOffsetBone == null ||
                    setting.referenceBodyOffsetBone == null ||
                    setting.referenceInnerOffsetBone == null ||
                    setting.referenceOuterOffsetBone == null)
                    continue;
                _ponchos.Add(new PonchoDriver(
                    component.transform,
                    setting.referenceArmBone.transform,
                    setting.referencePonchoOffsetBone.transform,
                    setting.referenceArmOffsetBone.transform,
                    setting.referenceBodyOffsetBone.transform,
                    setting.referenceInnerOffsetBone.transform,
                    setting.referenceOuterOffsetBone.transform,
                    setting));
            }

            foreach (ActorAnimationQuartzDriverHumanoidSleeveBone component in
                     GetComponentsInChildren<ActorAnimationQuartzDriverHumanoidSleeveBone>(true))
            {
                component.enabled = false;
                ActorAnimationQuartzDriverHumanoidSleeveSetting setting = component.setting;
                if (setting == null || setting.referenceBone == null) continue;
                int muscle = ResolveForearmTwistMuscle(setting.humanPartDof);
                if (muscle < 0) continue;
                _sleeves.Add(new HumanoidSleeveDriver(
                    component.transform, setting.referenceBone.transform,
                    setting, muscle));
            }

            if (_sleeves.Count > 0)
            {
                if (animator != null && animator.avatar != null &&
                    animator.avatar.isValid && animator.avatar.isHuman)
                {
                    _poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
                    _pose = new HumanPose();
                }
                else
                {
                    Debug.LogWarning(
                        "[QuartzGarment] HumanoidSleeve requires a valid humanoid Animator");
                    _sleeves.Clear();
                }
            }

            _dumpState = arguments.Contains("--dump-quartz-garment-state");
            _initialized = _frills.Count > 0 || _furisodes.Count > 0 ||
                           _waists.Count > 0 || _ponchos.Count > 0 ||
                           _sleeves.Count > 0;
            Debug.Log(string.Format(
                "[QuartzGarment] Native garment pose drivers ready: " +
                "frill={0} furisode={1} waist={2} poncho={3} humanoidSleeve={4}",
                _frills.Count, _furisodes.Count, _waists.Count, _ponchos.Count,
                _sleeves.Count));
        }

        private void LateUpdate()
        {
            if (!_initialized) return;

            foreach (FrillDriver driver in _frills)
            {
                if (!driver.IsValid) continue;
                driver.lastReference = driver.reference.localRotation;
                driver.lastOutput = CalculateFrill(
                    driver.lastReference, driver.setting, out driver.lastEuler,
                    out driver.lastMapped);
                driver.target.localRotation = driver.lastOutput;
            }

            foreach (FurisodeDriver driver in _furisodes)
            {
                if (!driver.IsValid) continue;
                driver.lastOutput = CalculateFurisode(driver, out driver.lastApexWeight,
                    out driver.lastSignedAngle);
                driver.target.localRotation = driver.lastOutput;
            }

            foreach (WaistDriver driver in _waists)
            {
                if (!driver.IsValid) continue;
                Vector3 direction = driver.thigh.position - driver.waist.position;
                Vector3 localDirection = direction.sqrMagnitude > 0.00000001f
                    ? Quaternion.Inverse(driver.waist.rotation) * direction.normalized
                    : Vector3.down;
                driver.lastDirection = localDirection;
                driver.lastRotation = Quaternion.FromToRotation(Vector3.down, localDirection);
                driver.lastPosition = Vector3.LerpUnclamped(
                    driver.waist.position, driver.thigh.position, driver.setting.weight);
                driver.target.localRotation = driver.lastRotation;
                driver.target.position = driver.lastPosition;
            }

            foreach (PonchoDriver driver in _ponchos)
            {
                if (!driver.IsValid) continue;
                CalculatePoncho(driver, out driver.lastDirection,
                    out driver.lastArmAngle, out driver.lastRotation,
                    out driver.lastPosition);
                driver.target.localRotation = driver.lastRotation;
                driver.target.localPosition = driver.lastPosition;
            }

            if (_sleeves.Count > 0 && _poseHandler != null)
            {
                _poseHandler.GetHumanPose(ref _pose);
                if (_pose.muscles != null)
                {
                    foreach (HumanoidSleeveDriver driver in _sleeves)
                    {
                        if (!driver.IsValid || driver.muscleIndex < 0 ||
                            driver.muscleIndex >= _pose.muscles.Length)
                            continue;
                        driver.lastMuscle = _pose.muscles[driver.muscleIndex];
                        driver.lastOutput = CalculateHumanoidSleeve(
                            driver, out driver.lastCorrectedReference,
                            out driver.lastUnityEuler, out driver.lastRollPitchYaw,
                            out driver.lastMapped);
                        driver.target.localRotation = driver.lastOutput;
                    }
                }
            }
        }

        // ActorAnimationQuartzDriverHumanoidSleeveJobBone.Execute and
        // ActorAnimationQuartzDriverHumanoidSleeveBone.Calc
        // (current runtime 0x7fff01ff2760 / 0x7fff01fe0060).
        // ArmDof.ForeArmRollInOut (6) is converted with the production -90 deg
        // coefficient, premultiplied into the authored reference rotation, and
        // then passed through ToUnityEuler + EulerToBendRoll before mapping.
        private static Quaternion CalculateHumanoidSleeve(
            HumanoidSleeveDriver driver,
            out Quaternion correctedReference,
            out Vector3 unityEuler,
            out Vector3 rollPitchYaw,
            out Vector3 mapped)
        {
            Quaternion muscleCorrection = Quaternion.AngleAxis(
                driver.lastMuscle * -90f, Vector3.right);
            correctedReference = Normalize(
                muscleCorrection * driver.reference.localRotation);
            unityEuler = ToUnityEulerDegrees(correctedReference);
            rollPitchYaw = EulerDegreesToBendRoll(unityEuler);

            ActorAnimationQuartzDriverHumanoidSleeveSetting setting = driver.setting;
            rollPitchYaw = new Vector3(
                FormatRange(rollPitchYaw.x),
                FormatRange(rollPitchYaw.y),
                FormatRange(rollPitchYaw.z));
            mapped = new Vector3(
                SwitchCoefficient(rollPitchYaw.x,
                    setting.outerMinusCoefficient.x, setting.innerCoefficient.x,
                    setting.outerPlusCoefficient.x,
                    setting.switchMinus.x, setting.switchPlus.x),
                SwitchCoefficient(rollPitchYaw.y,
                    setting.outerMinusCoefficient.y, setting.innerCoefficient.y,
                    setting.outerPlusCoefficient.y,
                    setting.switchMinus.y, setting.switchPlus.y),
                SwitchCoefficient(rollPitchYaw.z,
                    setting.outerMinusCoefficient.z, setting.innerCoefficient.z,
                    setting.outerPlusCoefficient.z,
                    setting.switchMinus.z, setting.switchPlus.z)) * Mathf.Deg2Rad;

            if (setting.composeType == 0)
                return AxisRotationToQuaternionWithBendRoll(mapped);
            if (setting.composeType == 1)
                return AxisRotationToQuaternionWithRollBend(mapped);
            if (setting.composeType == 2)
                return AxisRotationToQuaternionWithBendRoll(-mapped);
            return AxisRotationToQuaternionFallback(mapped);
        }

        // Direct source transcription of ActorAnimationMathUtility.ToUnityEuler
        // (0x7fff01fc94c0). The native helper emits signed degrees.
        private static Vector3 ToUnityEulerDegrees(Quaternion value)
        {
            Quaternion q = Normalize(value);
            float x = q.x;
            float y = q.y;
            float z = q.z;
            float w = q.w;
            float asinInput = Mathf.Clamp(2f * (y * z - x * w), -1f, 1f);
            return new Vector3(
                -Mathf.Asin(asinInput) * Mathf.Rad2Deg,
                -Mathf.Atan2(2f * (x * z + y * w),
                    w * w - x * x - y * y + z * z) * Mathf.Rad2Deg,
                -Mathf.Atan2(2f * (x * y + z * w),
                    w * w - x * x + y * y - z * z) * Mathf.Rad2Deg);
        }

        // ActorAnimationMathUtility.EulerToBendRoll used at 0x7fff01feefc0.
        // Input and output are degrees; Y intentionally carries the positive
        // sign used by the production HumanoidSleeve/Poncho path.
        private static Vector3 EulerDegreesToBendRoll(Vector3 euler)
        {
            Quaternion rebuilt = Quaternion.Euler(euler);
            Vector3 way = rebuilt * Vector3.right;
            float denominator = way.x + 1f;
            return new Vector3(
                euler.x,
                2f * Mathf.Atan2(way.z, denominator) * Mathf.Rad2Deg,
                2f * Mathf.Atan2(way.y, denominator) * Mathf.Rad2Deg);
        }

        // The fourth production compose branch uses the generic axis-rotation
        // fallback. No current Sleeve asset selects it, but keep it finite and
        // deterministic rather than silently returning identity.
        private static Quaternion AxisRotationToQuaternionFallback(Vector3 value)
        {
            return Normalize(Quaternion.Euler(value * Mathf.Rad2Deg));
        }

        private static int ResolveForearmTwistMuscle(int humanPartDof)
        {
            if (humanPartDof == 4)
                return HumanMuscleResolver.FindExact(
                    "Left Forearm Twist In-Out", "Left ForeArm Twist In-Out");
            if (humanPartDof == 5)
                return HumanMuscleResolver.FindExact(
                    "Right Forearm Twist In-Out", "Right ForeArm Twist In-Out");
            Debug.LogWarning(
                "[QuartzGarment] Unsupported HumanoidSleeve humanPartDof: " +
                humanPartDof);
            return -1;
        }

        // ActorAnimationQuartzDriverPonchoBone.CalcRotation / CalcPosition
        // (current runtime 0x7fff01fe1140 / 0x7fff01fe0e00).  Rotation aims the
        // authored axis from the poncho guide toward the weighted arm/body
        // guide. Position uses the reference arm's bend/roll Y angle and the
        // signed inner/outer limits to blend from zero to the authored offsets.
        private static void CalculatePoncho(
            PonchoDriver driver, out Vector3 localDirection, out float armAngle,
            out Quaternion rotation, out Vector3 position)
        {
            float armWeight = driver.setting.lerpCoefficient;
            Vector3 weightedGuide =
                driver.armOffset.position * armWeight +
                driver.bodyOffset.position * (1f - armWeight);
            Vector3 worldDirection = weightedGuide - driver.ponchoOffset.position;
            if (worldDirection.sqrMagnitude > 0.00000001f)
                worldDirection.Normalize();
            else
                worldDirection = driver.ponchoOffset.rotation * driver.setting.axisWay;

            localDirection = Quaternion.Inverse(driver.ponchoOffset.rotation) *
                             worldDirection;
            Vector3 axis = driver.setting.axisWay;
            if (axis.sqrMagnitude < 0.00000001f) axis = Vector3.right;
            if (localDirection.sqrMagnitude < 0.00000001f) localDirection = axis;
            rotation = Normalize(Quaternion.FromToRotation(
                axis.normalized, localDirection.normalized));

            Vector3 bendRoll = PonchoArmRotationToBendRollDegrees(
                driver.arm.localRotation);
            armAngle = bendRoll.y;

            if (armAngle > 0f)
            {
                float divisor = driver.setting.innerLimit;
                float weight = Mathf.Abs(divisor) > 0.000001f
                    ? Mathf.Clamp01(armAngle / divisor)
                    : 1f;
                position = Vector3.LerpUnclamped(
                    Vector3.zero, driver.innerOffset.localPosition, weight);
            }
            else if (armAngle < 0f)
            {
                float divisor = driver.setting.outerLimit;
                float weight = Mathf.Abs(divisor) > 0.000001f
                    ? Mathf.Clamp01(armAngle / divisor)
                    : 1f;
                position = Vector3.LerpUnclamped(
                    Vector3.zero, driver.outerOffset.localPosition, weight);
            }
            else
            {
                position = Vector3.zero;
            }
        }

        // Direct transcription of the helper at 0x7fff01feefc0.  Its Y sign
        // intentionally differs from the generic Frill bend/roll helper:
        // Poncho uses +2*atan2(rightWay.z, rightWay.x+1), in degrees.
        private static Vector3 PonchoArmRotationToBendRollDegrees(Quaternion rotation)
        {
            quaternion source = math.normalize(new quaternion(
                rotation.x, rotation.y, rotation.z, rotation.w));
            float3 rawEuler = math.Euler(source, math.RotationOrder.Default);
            Quaternion rebuilt = Quaternion.Euler(new Vector3(
                rawEuler.x, rawEuler.y, rawEuler.z) * Mathf.Rad2Deg);
            Vector3 way = rebuilt * Vector3.right;
            float denominator = way.x + 1f;
            return new Vector3(
                rawEuler.x * Mathf.Rad2Deg,
                2f * Mathf.Atan2(way.z, denominator) * Mathf.Rad2Deg,
                2f * Mathf.Atan2(way.y, denominator) * Mathf.Rad2Deg);
        }

        // Source-level transcription of ActorAnimationQuartzDriverFrillBone.Calc
        // (current runtime 0x7fff01fddde0).  SwitchCoefficient is recovered
        // exactly from 0x7fff01ff02c0 rather than approximated as a branch.
        private static Quaternion CalculateFrill(
            Quaternion referenceRotation,
            ActorAnimationQuartzDriverFrillSetting setting,
            out Vector3 euler,
            out Vector3 mapped)
        {
            quaternion reference = math.normalize(new quaternion(
                referenceRotation.x, referenceRotation.y,
                referenceRotation.z, referenceRotation.w));
            float3 raw = math.Euler(reference, ToRotationOrder(setting.rotationOrder));
            euler = new Vector3(raw.x, raw.y, raw.z);
            if (setting.decomposeType == 1)
                euler = EulerToBendRoll(euler);
            else if (setting.decomposeType == 2)
                euler = EulerToBendRollQuaternionInverse(euler);

            euler = new Vector3(
                FormatRange(euler.x), FormatRange(euler.y), FormatRange(euler.z));
            mapped = new Vector3(
                SwitchCoefficient(euler.x,
                    setting.outerMinusCoefficient.x, setting.innerCoefficient.x,
                    setting.outerPlusCoefficient.x,
                    setting.switchMinus.x, setting.switchPlus.x),
                SwitchCoefficient(euler.y,
                    setting.outerMinusCoefficient.y, setting.innerCoefficient.y,
                    setting.outerPlusCoefficient.y,
                    setting.switchMinus.y, setting.switchPlus.y),
                SwitchCoefficient(euler.z,
                    setting.outerMinusCoefficient.z, setting.innerCoefficient.z,
                    setting.outerPlusCoefficient.z,
                    setting.switchMinus.z, setting.switchPlus.z)) * Mathf.Deg2Rad;

            if (setting.composeType == 0)
                return AxisRotationToQuaternionWithBendRoll(mapped);
            if (setting.composeType == 1)
                return AxisRotationToQuaternionWithRollBend(mapped);
            if (setting.composeType == 2)
                return AxisRotationToQuaternionWithBendRoll(-mapped);
            return Quaternion.Euler(
                ExchangeAxisConnection(mapped, setting.connectionAxis) * Mathf.Rad2Deg);
        }

        private static float SwitchCoefficient(
            float value, float outerMinus, float inner, float outerPlus,
            float switchMinus, float switchPlus)
        {
            return Mathf.Min(value - switchMinus, 0f) * outerMinus +
                   value * inner +
                   Mathf.Max(value - switchPlus, 0f) * outerPlus;
        }

        // ActorAnimationQuartzDriverFurisodeBone.Calc (0x7fff01fde590).
        // The native path measures the downward hand/forearm apex, expresses the
        // spine-authored back vector in the furisode-offset frame, extracts the
        // bend/roll Z response, then emits a local Z rotation.
        private static Quaternion CalculateFurisode(
            FurisodeDriver driver, out float apexWeight, out float signedAngle)
        {
            Vector3 handWay = driver.hand.position - driver.forearm.position;
            if (handWay.sqrMagnitude > 0.00000001f) handWay.Normalize();
            else handWay = Vector3.down;
            float apex = Mathf.Max(Mathf.Abs(driver.setting.apexAngle), 0.0001f);
            apexWeight = Mathf.Clamp01(
                1f - Vector3.Angle(handWay, Vector3.down) / apex);

            Vector3 worldBackWay = driver.spine.rotation * driver.setting.backWay;
            Vector3 offsetBackWay = Quaternion.Inverse(driver.offset.rotation) * worldBackWay;
            if (offsetBackWay.sqrMagnitude < 0.00000001f)
                offsetBackWay = Vector3.left;
            Quaternion backRotation = Quaternion.FromToRotation(
                Vector3.left, offsetBackWay.normalized);
            Vector3 euler = SignedEulerRadians(backRotation);
            Vector3 bendRoll = EulerToBendRoll(euler);
            signedAngle = Mathf.Clamp(
                apexWeight * driver.setting.coefficient * bendRoll.z * Mathf.Rad2Deg,
                driver.setting.min, driver.setting.max);
            return Quaternion.AngleAxis(signedAngle, Vector3.forward);
        }

        private static Vector3 SignedEulerRadians(Quaternion rotation)
        {
            Vector3 value = rotation.eulerAngles;
            if (value.x > 180f) value.x -= 360f;
            if (value.y > 180f) value.y -= 360f;
            if (value.z > 180f) value.z -= 360f;
            return value * Mathf.Deg2Rad;
        }

        private static Vector3 EulerToBendRollQuaternionInverse(Vector3 euler)
        {
            quaternion qMath = quaternion.EulerZXY(new float3(
                euler.x * Mathf.Deg2Rad,
                euler.y * Mathf.Deg2Rad,
                euler.z * Mathf.Deg2Rad));
            Quaternion q = new Quaternion(
                qMath.value.x, qMath.value.y, qMath.value.z, qMath.value.w);
            Vector3 way = q * Vector3.right;
            float denominator = way.x + 1f;
            float second = -2f * Mathf.Atan2(way.z, denominator) * Mathf.Rad2Deg;
            float third = 2f * Mathf.Atan2(way.y, denominator) * Mathf.Rad2Deg;

            Quaternion bend = Quaternion.FromToRotation(Vector3.right, way);
            Quaternion residual = Quaternion.Inverse(bend) * q;
            residual.ToAngleAxis(out float roll, out Vector3 axis);
            if (roll > 180f) roll -= 360f;
            if (Vector3.Dot(axis, Vector3.right) < 0f) roll = -roll;
            return new Vector3(roll, second, third);
        }

        private static Vector3 EulerToBendRoll(Vector3 euler)
        {
            Quaternion q = Quaternion.Euler(euler * Mathf.Rad2Deg);
            Vector3 way = q * Vector3.right;
            float denominator = way.x + 1f;
            return new Vector3(
                euler.x,
                -2f * Mathf.Atan2(way.z, denominator),
                2f * Mathf.Atan2(way.y, denominator));
        }

        private static Quaternion AxisRotationToQuaternionWithBendRoll(Vector3 value)
        {
            float pitchTangent = Mathf.Tan(value.y * 0.5f);
            float yawTangent = Mathf.Tan(-value.z * 0.5f);
            float scale = 2f / (yawTangent * yawTangent +
                                pitchTangent * pitchTangent + 1f);
            Vector3 way = new Vector3(
                scale - 1f, scale * yawTangent, scale * pitchTangent);
            Quaternion bend = Quaternion.FromToRotation(Vector3.right, way);
            Quaternion roll = Quaternion.AngleAxis(
                -value.x * Mathf.Rad2Deg, Vector3.right);
            return Normalize(roll * bend);
        }

        private static Quaternion AxisRotationToQuaternionWithRollBend(Vector3 value)
        {
            float pitchTangent = Mathf.Tan(value.y * 0.5f);
            float yawTangent = Mathf.Tan(-value.z * 0.5f);
            float scale = 2f / (yawTangent * yawTangent +
                                pitchTangent * pitchTangent + 1f);
            Vector3 way = new Vector3(
                scale - 1f, scale * yawTangent, scale * pitchTangent);
            Quaternion bend = Quaternion.FromToRotation(Vector3.right, way);
            Quaternion roll = Quaternion.AngleAxis(
                -value.x * Mathf.Rad2Deg, Vector3.right);
            return Normalize(bend * roll);
        }

        private static Quaternion Normalize(Quaternion value)
        {
            float length = Mathf.Sqrt(value.x * value.x + value.y * value.y +
                                      value.z * value.z + value.w * value.w);
            if (length < 0.000001f) return Quaternion.identity;
            return new Quaternion(
                value.x / length, value.y / length,
                value.z / length, value.w / length);
        }

        private static float FormatRange(float value)
        {
            if (value > 180f) return value - 360f;
            if (value < -180f) return value + 360f;
            return value;
        }

        private static math.RotationOrder ToRotationOrder(int value)
        {
            return value >= 0 && value <= 5
                ? (math.RotationOrder)value
                : math.RotationOrder.Default;
        }

        private static Vector3 ExchangeAxisConnection(Vector3 value, int order)
        {
            switch (order)
            {
                case 1: return new Vector3(value.x, value.z, value.y);
                case 2: return new Vector3(value.y, value.x, value.z);
                case 3: return new Vector3(value.y, value.z, value.x);
                case 4: return new Vector3(value.z, value.x, value.y);
                case 5: return new Vector3(value.z, value.y, value.x);
                default: return value;
            }
        }

        public void LogCurrentState(string label)
        {
            if (!_dumpState) return;
            foreach (FrillDriver driver in _frills)
                Debug.Log(string.Format(
                    "[QuartzGarmentState] label={0} kind=Frill target={1} reference={2} " +
                    "order={3} decompose={4} compose={5} euler={6} mapped={7} output={8}",
                    label, driver.Name, driver.ReferenceName,
                    driver.setting.rotationOrder, driver.setting.decomposeType,
                    driver.setting.composeType, driver.lastEuler.ToString("F6"),
                    driver.lastMapped.ToString("F6"),
                    driver.lastOutput.eulerAngles.ToString("F3")));
            foreach (FurisodeDriver driver in _furisodes)
                Debug.Log(string.Format(
                    "[QuartzGarmentState] label={0} kind=Furisode target={1} " +
                    "apexWeight={2:0.000000} angle={3:0.000000} output={4}",
                    label, driver.Name, driver.lastApexWeight,
                    driver.lastSignedAngle, driver.lastOutput.eulerAngles.ToString("F3")));
            foreach (WaistDriver driver in _waists)
                Debug.Log(string.Format(
                    "[QuartzGarmentState] label={0} kind=Waist target={1} weight={2:0.000000} " +
                    "direction={3} position={4} output={5}",
                    label, driver.Name, driver.setting.weight,
                    driver.lastDirection.ToString("F6"),
                    driver.lastPosition.ToString("F6"),
                    driver.lastRotation.eulerAngles.ToString("F3")));
            foreach (PonchoDriver driver in _ponchos)
                Debug.Log(string.Format(
                    "[QuartzGarmentState] label={0} kind=Poncho target={1} " +
                    "arm={2} lerp={3:0.000000} limits=({4:0.000},{5:0.000}) " +
                    "armAngle={6:0.000000} direction={7} position={8} output={9}",
                    label, driver.Name, driver.ArmName,
                    driver.setting.lerpCoefficient,
                    driver.setting.innerLimit, driver.setting.outerLimit,
                    driver.lastArmAngle, driver.lastDirection.ToString("F6"),
                    driver.lastPosition.ToString("F6"),
                    driver.lastRotation.eulerAngles.ToString("F3")));
            foreach (HumanoidSleeveDriver driver in _sleeves)
                Debug.Log(string.Format(
                    "[QuartzGarmentState] label={0} kind=HumanoidSleeve target={1} " +
                    "reference={2} muscleIndex={3} muscle={4:0.000000} compose={5} " +
                    "corrected={6} unityEuler={7} rollPitchYaw={8} mapped={9} output={10}",
                    label, driver.Name, driver.ReferenceName,
                    driver.muscleIndex, driver.lastMuscle,
                    driver.setting.composeType,
                    driver.lastCorrectedReference.eulerAngles.ToString("F3"),
                    driver.lastUnityEuler.ToString("F6"),
                    driver.lastRollPitchYaw.ToString("F6"),
                    driver.lastMapped.ToString("F6"),
                    driver.lastOutput.eulerAngles.ToString("F3")));
        }

        private void OnDestroy()
        {
            DisposePoseHandler();
        }

        private void DisposePoseHandler()
        {
            if (_poseHandler == null) return;
            _poseHandler.Dispose();
            _poseHandler = null;
        }

        private sealed class FrillDriver
        {
            public readonly Transform target;
            public readonly Transform reference;
            public readonly ActorAnimationQuartzDriverFrillSetting setting;
            public Quaternion lastReference;
            public Quaternion lastOutput;
            public Vector3 lastEuler;
            public Vector3 lastMapped;
            public bool IsValid { get { return target != null && reference != null; } }
            public string Name { get { return target == null ? "-" : target.name; } }
            public string ReferenceName { get { return reference == null ? "-" : reference.name; } }

            public FrillDriver(Transform driven, Transform source,
                ActorAnimationQuartzDriverFrillSetting value)
            {
                target = driven;
                reference = source;
                setting = value;
            }
        }

        private sealed class FurisodeDriver
        {
            public readonly Transform target;
            public readonly Transform offset;
            public readonly Transform spine;
            public readonly Transform forearm;
            public readonly Transform hand;
            public readonly ActorAnimationQuartzDriverFurisodeSetting setting;
            public Quaternion lastOutput;
            public float lastApexWeight;
            public float lastSignedAngle;
            public bool IsValid { get { return target != null && offset != null &&
                spine != null && forearm != null && hand != null; } }
            public string Name { get { return target == null ? "-" : target.name; } }

            public FurisodeDriver(
                Transform driven, Transform offsetReference, Transform spineReference,
                Transform forearmReference, Transform handReference,
                ActorAnimationQuartzDriverFurisodeSetting value)
            {
                target = driven;
                offset = offsetReference;
                spine = spineReference;
                forearm = forearmReference;
                hand = handReference;
                setting = value;
            }
        }

        private sealed class WaistDriver
        {
            public readonly Transform target;
            public readonly Transform waist;
            public readonly Transform thigh;
            public readonly ActorAnimationQuartzDriverWaistSetting setting;
            public Vector3 lastDirection;
            public Vector3 lastPosition;
            public Quaternion lastRotation;
            public bool IsValid { get { return target != null && waist != null && thigh != null; } }
            public string Name { get { return target == null ? "-" : target.name; } }

            public WaistDriver(Transform driven, Transform waistReference,
                Transform thighReference, ActorAnimationQuartzDriverWaistSetting value)
            {
                target = driven;
                waist = waistReference;
                thigh = thighReference;
                setting = value;
            }
        }

        private sealed class PonchoDriver
        {
            public readonly Transform target;
            public readonly Transform arm;
            public readonly Transform ponchoOffset;
            public readonly Transform armOffset;
            public readonly Transform bodyOffset;
            public readonly Transform innerOffset;
            public readonly Transform outerOffset;
            public readonly ActorAnimationQuartzDriverPonchoSetting setting;
            public Vector3 lastDirection;
            public float lastArmAngle;
            public Quaternion lastRotation;
            public Vector3 lastPosition;
            public bool IsValid
            {
                get
                {
                    return target != null && arm != null && ponchoOffset != null &&
                           armOffset != null && bodyOffset != null &&
                           innerOffset != null && outerOffset != null;
                }
            }
            public string Name { get { return target == null ? "-" : target.name; } }
            public string ArmName { get { return arm == null ? "-" : arm.name; } }

            public PonchoDriver(
                Transform driven, Transform armReference,
                Transform ponchoOffsetReference, Transform armOffsetReference,
                Transform bodyOffsetReference, Transform innerOffsetReference,
                Transform outerOffsetReference,
                ActorAnimationQuartzDriverPonchoSetting value)
            {
                target = driven;
                arm = armReference;
                ponchoOffset = ponchoOffsetReference;
                armOffset = armOffsetReference;
                bodyOffset = bodyOffsetReference;
                innerOffset = innerOffsetReference;
                outerOffset = outerOffsetReference;
                setting = value;
            }
        }

        private sealed class HumanoidSleeveDriver
        {
            public readonly Transform target;
            public readonly Transform reference;
            public readonly ActorAnimationQuartzDriverHumanoidSleeveSetting setting;
            public readonly int muscleIndex;
            public float lastMuscle;
            public Quaternion lastCorrectedReference;
            public Vector3 lastUnityEuler;
            public Vector3 lastRollPitchYaw;
            public Vector3 lastMapped;
            public Quaternion lastOutput;
            public bool IsValid { get { return target != null && reference != null; } }
            public string Name { get { return target == null ? "-" : target.name; } }
            public string ReferenceName
            {
                get { return reference == null ? "-" : reference.name; }
            }

            public HumanoidSleeveDriver(
                Transform driven, Transform source,
                ActorAnimationQuartzDriverHumanoidSleeveSetting value,
                int muscle)
            {
                target = driven;
                reference = source;
                setting = value;
                muscleIndex = muscle;
            }
        }
    }
}
