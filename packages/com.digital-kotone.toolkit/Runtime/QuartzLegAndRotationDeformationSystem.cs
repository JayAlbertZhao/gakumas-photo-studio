using System;
using System.Collections.Generic;
using System.Linq;
using ActorAnimation;
using Unity.Mathematics;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Runtime replay of the production HumanoidUpLeg and Rotation Quartz jobs.
    /// These jobs run after the Animator and before ActorSwing.  They distribute
    /// leg/forearm deformation into helper bones and are particularly visible in
    /// the hmsz cstm-0000 sleeve receiver.
    /// </summary>
    [DefaultExecutionOrder(710)]
    public sealed class QuartzLegAndRotationDeformationSystem : MonoBehaviour
    {
        // ActorAnimationQuartzDriverHumanoidUpLegSetting::.cctor writes -60f.
        private const float UpLegMuscleConvertCoefficient = -60f;

        private readonly List<UpLegDriver> _upLegDrivers = new List<UpLegDriver>();
        private readonly List<RotationDriver> _rotationDrivers = new List<RotationDriver>();
        private HumanPoseHandler _poseHandler;
        private HumanPose _pose;
        private bool _initialized;
        private bool _dumpState;

        public int UpLegDriverCount { get { return _upLegDrivers.Count; } }
        public int RotationDriverCount { get { return _rotationDrivers.Count; } }

        public void Initialize(Animator animator)
        {
            DisposePoseHandler();
            _upLegDrivers.Clear();
            _rotationDrivers.Clear();
            _initialized = false;

            if (Environment.GetCommandLineArgs().Contains("--disable-quartz-leg-rotation"))
            {
                Debug.Log("[QuartzLegRotation] Disabled by diagnostic command line");
                return;
            }

            Transform[] hierarchy = GetComponentsInChildren<Transform>(true);
            foreach (ActorAnimationQuartzDriverHumanoidUpLegBone component in
                     GetComponentsInChildren<ActorAnimationQuartzDriverHumanoidUpLegBone>(true))
            {
                component.enabled = false;
                if (component.setting == null) continue;
                int muscle = ResolveUpLegMuscle(component.setting.humanPartDof);
                if (muscle < 0) continue;
                _upLegDrivers.Add(new UpLegDriver(
                    component.transform, muscle, component.setting.coefficient));
            }

            foreach (ActorAnimationQuartzDriverRotationBone component in
                     GetComponentsInChildren<ActorAnimationQuartzDriverRotationBone>(true))
            {
                component.enabled = false;
                if (component.setting == null) continue;
                if (Environment.GetCommandLineArgs().Contains("--disable-quartz-receive") &&
                    component.transform.name.IndexOf("Receive", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                Transform reference = ResolveReference(component, hierarchy);
                if (reference == null)
                {
                    Debug.LogWarning("[QuartzLegRotation] Reference unavailable for " +
                        component.transform.name);
                    continue;
                }
                _rotationDrivers.Add(new RotationDriver(
                    component.transform, reference, component.setting));
            }

            if (_upLegDrivers.Count > 0)
            {
                if (animator == null || animator.avatar == null ||
                    !animator.avatar.isValid || !animator.avatar.isHuman)
                {
                    Debug.LogWarning(
                        "[QuartzLegRotation] Humanoid avatar unavailable; upper-leg drivers disabled");
                    _upLegDrivers.Clear();
                }
                else
                {
                    _poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
                    _pose = new HumanPose();
                }
            }

            _dumpState = Environment.GetCommandLineArgs()
                .Contains("--dump-quartz-leg-rotation-state");
            _initialized = _upLegDrivers.Count > 0 || _rotationDrivers.Count > 0;
            Debug.Log(string.Format(
                "[QuartzLegRotation] Native pose drivers ready: upLeg={0} rotation={1} " +
                "euler={2} stereoBendRoll={3}",
                _upLegDrivers.Count, _rotationDrivers.Count,
                _rotationDrivers.Count(value => value.setting.decomposeType == 0 &&
                    value.setting.composeType == 3),
                _rotationDrivers.Count(value => value.setting.decomposeType == 2 &&
                    value.setting.composeType == 0)));
        }

        private void LateUpdate()
        {
            if (!_initialized) return;
            if (_upLegDrivers.Count > 0 && _poseHandler != null)
            {
                _poseHandler.GetHumanPose(ref _pose);
                if (_pose.muscles != null)
                {
                    foreach (UpLegDriver driver in _upLegDrivers)
                    {
                        if (driver.transform == null || driver.muscleIndex < 0 ||
                            driver.muscleIndex >= _pose.muscles.Length)
                            continue;
                        float muscle = _pose.muscles[driver.muscleIndex];
                        float angle = muscle * UpLegMuscleConvertCoefficient * driver.coefficient;
                        Vector3 currentEuler = driver.transform.localRotation.eulerAngles;
                        driver.transform.localRotation = Quaternion.Euler(
                            angle, currentEuler.y, currentEuler.z);
                        driver.lastMuscle = muscle;
                        driver.lastAngle = angle;
                    }
                }
            }

            foreach (RotationDriver driver in _rotationDrivers)
            {
                if (driver.transform == null || driver.reference == null) continue;
                driver.lastReference = driver.reference.localRotation;
                driver.lastOutput = CalculateRotation(
                    driver.lastReference, driver.setting, out driver.lastDecomposed,
                    out driver.lastConverted);
                driver.transform.localRotation = driver.lastOutput;
            }
        }

        private static Quaternion CalculateRotation(
            Quaternion referenceRotation,
            QuartzRotationSetting setting,
            out Vector3 decomposed,
            out Vector3 converted)
        {
            quaternion reference = new quaternion(
                referenceRotation.x, referenceRotation.y,
                referenceRotation.z, referenceRotation.w);
            math.RotationOrder order = ToRotationOrder(setting.rotationOrder);
            float3 euler = math.Euler(math.normalize(reference), order);
            decomposed = new Vector3(euler.x, euler.y, euler.z);

            if (setting.decomposeType == 1)
                decomposed = EulerToBendRoll(decomposed);
            else if (setting.decomposeType == 2)
                decomposed = EulerToBendRollQuaternionInverse(decomposed);

            decomposed.x = FormatRange(decomposed.x);
            decomposed.y = FormatRange(decomposed.y);
            decomposed.z = FormatRange(decomposed.z);
            converted = new Vector3(
                ClampThenScale(decomposed.x, setting.limitMin.x,
                    setting.limitMax.x, setting.coefficient.x),
                ClampThenScale(decomposed.y, setting.limitMin.y,
                    setting.limitMax.y, setting.coefficient.y),
                ClampThenScale(decomposed.z, setting.limitMin.z,
                    setting.limitMax.z, setting.coefficient.z));

            // RotationBone.Calc negates all three converted axes before calling
            // the BendRoll/RollBend helpers.  The helpers also contain their
            // own authored axis signs, so folding this call-site negation into
            // the helper would invert the hmsz ForeArm_Receive_A bend.
            if (setting.composeType == 0)
                return AxisRotationToQuaternionWithBendRoll(-converted);
            if (setting.composeType == 1)
                return AxisRotationToQuaternionWithRollBend(-converted);
            if (setting.composeType == 2)
                return AxisRotationToQuaternionWithBendRoll(converted);

            Vector3 connected = ExchangeAxisConnection(converted, setting.connectionAxis);
            return Quaternion.Euler(connected * Mathf.Rad2Deg);
        }

        // Stereo-projection decomposition used by hmsz's *_ForeArm_Receive_A
        // nodes.  This is a source-level transcription of
        // ActorAnimationMathUtility.EulerToBendrollQuaternionInverse.
        private static Vector3 EulerToBendRollQuaternionInverse(Vector3 euler)
        {
            // The native helper feeds its input through EulerZXY after applying
            // Deg2Rad, even though ToEuler's output is already in radians. Keep
            // that literal unit path: it is part of the shipped sleeve response.
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
            // No current fktn/hmsz solo1 driver selects this mode.  Preserve a
            // stable representation for future content rather than dropping it.
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
                scale - 1f,
                scale * yawTangent,
                scale * pitchTangent);
            Quaternion bend = Quaternion.FromToRotation(Vector3.right, way);
            Quaternion roll = Quaternion.AngleAxis(-value.x * Mathf.Rad2Deg, Vector3.right);
            return Normalize(roll * bend);
        }

        private static Quaternion AxisRotationToQuaternionWithRollBend(Vector3 value)
        {
            float pitchTangent = Mathf.Tan(value.y * 0.5f);
            float yawTangent = Mathf.Tan(-value.z * 0.5f);
            float scale = 2f / (yawTangent * yawTangent +
                                pitchTangent * pitchTangent + 1f);
            Vector3 way = new Vector3(
                scale - 1f,
                scale * yawTangent,
                scale * pitchTangent);
            Quaternion bend = Quaternion.FromToRotation(Vector3.right, way);
            Quaternion roll = Quaternion.AngleAxis(-value.x * Mathf.Rad2Deg, Vector3.right);
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

        private static float ClampThenScale(
            float value, float minimum, float maximum, float coefficient)
        {
            return Mathf.Clamp(value, minimum, maximum) * coefficient;
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

        private Transform ResolveReference(
            ActorAnimationQuartzDriverRotationBone component,
            IEnumerable<Transform> hierarchy)
        {
            if (component.setting.referenceBone != null)
            {
                Transform candidate = component.setting.referenceBone.transform;
                if (candidate == transform || candidate.IsChildOf(transform)) return candidate;
            }

            string driven = component.transform.name;
            string referenceName;
            if (driven.StartsWith("LeftForeArm", StringComparison.Ordinal))
                referenceName = "LeftForeArm";
            else if (driven.StartsWith("RightForeArm", StringComparison.Ordinal))
                referenceName = "RightForeArm";
            else if (driven.StartsWith("LeftLeg", StringComparison.Ordinal))
                referenceName = "LeftLeg";
            else if (driven.StartsWith("RightLeg", StringComparison.Ordinal))
                referenceName = "RightLeg";
            else
                return null;
            return hierarchy.FirstOrDefault(value => value.name == referenceName);
        }

        private static int ResolveUpLegMuscle(int humanPartDof)
        {
            if (humanPartDof == 2)
                return HumanMuscleResolver.Find("Left", "UpperLeg", "FrontBack");
            if (humanPartDof == 3)
                return HumanMuscleResolver.Find("Right", "UpperLeg", "FrontBack");
            Debug.LogWarning("[QuartzLegRotation] Unsupported humanPartDof: " + humanPartDof);
            return -1;
        }

        public void LogCurrentState(string label)
        {
            if (!_dumpState) return;
            foreach (UpLegDriver driver in _upLegDrivers)
            {
                Debug.Log(string.Format(
                    "[QuartzLegState] label={0} bone={1} muscleIndex={2} muscle={3:0.000000} " +
                    "coefficient={4:0.000000} angle={5:0.000000}",
                    label, driver.transform == null ? "-" : driver.transform.name,
                    driver.muscleIndex, driver.lastMuscle, driver.coefficient,
                    driver.lastAngle));
            }
            foreach (RotationDriver driver in _rotationDrivers)
            {
                Debug.Log(string.Format(
                    "[QuartzRotationState] label={0} bone={1} reference={2} order={3} " +
                    "decompose={4} compose={5} raw={6} converted={7} output={8}",
                    label, driver.transform == null ? "-" : driver.transform.name,
                    driver.reference == null ? "-" : driver.reference.name,
                    driver.setting.rotationOrder, driver.setting.decomposeType,
                    driver.setting.composeType, driver.lastDecomposed.ToString("F6"),
                    driver.lastConverted.ToString("F6"),
                    driver.lastOutput.eulerAngles.ToString("F3")));
            }
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

        private sealed class UpLegDriver
        {
            public readonly Transform transform;
            public readonly int muscleIndex;
            public readonly float coefficient;
            public float lastMuscle;
            public float lastAngle;

            public UpLegDriver(Transform target, int muscle, float coefficientValue)
            {
                transform = target;
                muscleIndex = muscle;
                coefficient = coefficientValue;
            }
        }

        private sealed class RotationDriver
        {
            public readonly Transform transform;
            public readonly Transform reference;
            public readonly QuartzRotationSetting setting;
            public Quaternion lastReference;
            public Quaternion lastOutput;
            public Vector3 lastDecomposed;
            public Vector3 lastConverted;

            public RotationDriver(
                Transform target, Transform referenceTransform,
                QuartzRotationSetting source)
            {
                transform = target;
                reference = referenceTransform;
                setting = source;
            }
        }
    }
}
