using System;
using System.Collections.Generic;
using System.Linq;
using ActorAnimation;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Source-readable replay of the production humanoid arm/forearm Quartz
    /// deformation drivers. These are pose drivers, not secondary physics.
    ///
    /// Recovered native constants and formula (2026-08-19 runtime image):
    ///   HumanoidArm  Calc = muscle * -90 * serializedCoefficient
    ///   HumanoidHand Calc = muscle * -66 * serializedCoefficient
    /// The two jobs read ArmRollInOut and ForeArmRollInOut respectively, then
    /// write an X-axis local rotation to the helper transform.
    /// </summary>
    [DefaultExecutionOrder(700)]
    public sealed class QuartzArmDeformationSystem : MonoBehaviour
    {
        private const float ArmMuscleToDegrees = -90f;
        private const float ForeArmMuscleToDegrees = -66f;

        private readonly List<Driver> _drivers = new List<Driver>();
        private HumanPoseHandler _poseHandler;
        private HumanPose _pose;
        private bool _initialized;
        private bool _dumpState;

        public int DriverCount { get { return _drivers.Count; } }

        public void Initialize(Animator animator)
        {
            DisposePoseHandler();
            _drivers.Clear();
            _initialized = false;
            if (animator == null || animator.avatar == null ||
                !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                Debug.LogWarning("[QuartzArm] Humanoid avatar unavailable; arm deformation disabled");
                return;
            }

            foreach (ActorAnimationQuartzDriverHumanoidArmBone component in
                GetComponentsInChildren<ActorAnimationQuartzDriverHumanoidArmBone>(true))
            {
                component.enabled = false;
                if (component.setting == null) continue;
                int muscle = ResolveMuscle(component.setting.humanPartDof, false);
                if (muscle < 0) continue;
                _drivers.Add(new Driver(
                    component.transform, component.setting.coefficient,
                    muscle, ArmMuscleToDegrees, "ArmRoll"));
            }

            foreach (ActorAnimationQuartzDriverHumanoidHandBone component in
                GetComponentsInChildren<ActorAnimationQuartzDriverHumanoidHandBone>(true))
            {
                component.enabled = false;
                if (component.setting == null) continue;
                int muscle = ResolveMuscle(component.setting.humanPartDof, true);
                if (muscle < 0) continue;
                _drivers.Add(new Driver(
                    component.transform, component.setting.coefficient,
                    muscle, ForeArmMuscleToDegrees, "ForeArmRoll"));
            }

            _poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            _pose = new HumanPose();
            _dumpState = Environment.GetCommandLineArgs().Contains("--dump-quartz-arm-state");
            _initialized = _drivers.Count > 0;
            Debug.Log(string.Format(
                "[QuartzArm] Native arm-roll deformation ready: drivers={0} arm={1} forearm={2}",
                _drivers.Count,
                _drivers.Count(value => value.label == "ArmRoll"),
                _drivers.Count(value => value.label == "ForeArmRoll")));
        }

        private void LateUpdate()
        {
            if (!_initialized || _poseHandler == null) return;
            _poseHandler.GetHumanPose(ref _pose);
            if (_pose.muscles == null) return;
            foreach (Driver driver in _drivers)
            {
                if (driver.transform == null ||
                    driver.muscleIndex < 0 ||
                    driver.muscleIndex >= _pose.muscles.Length)
                    continue;
                float muscle = _pose.muscles[driver.muscleIndex];
                float angle = muscle * driver.muscleToDegrees * driver.coefficient;
                driver.transform.localRotation = driver.restLocalRotation *
                    Quaternion.AngleAxis(angle, Vector3.right);
                driver.lastMuscle = muscle;
                driver.lastAngle = angle;
            }
        }

        public void LogCurrentState(string label)
        {
            if (!_dumpState) return;
            foreach (Driver driver in _drivers)
            {
                Debug.Log(string.Format(
                    "[QuartzArmState] label={0} bone={1} kind={2} muscleIndex={3} " +
                    "muscle={4:0.000000} coefficient={5:0.000000} angle={6:0.000000} local={7}",
                    label, driver.transform == null ? "-" : driver.transform.name,
                    driver.label, driver.muscleIndex, driver.lastMuscle,
                    driver.coefficient, driver.lastAngle,
                    driver.transform == null ? "-" : driver.transform.localRotation.eulerAngles.ToString("F3")));
            }
        }

        private static int ResolveMuscle(int humanPartDof, bool foreArm)
        {
            // HumanPartDof.LeftArm / RightArm are 4 / 5.
            if (humanPartDof == 4)
                return foreArm
                    ? HumanMuscleResolver.FindExact(
                        "Left Forearm Twist In-Out", "Left ForeArm Twist In-Out")
                    : HumanMuscleResolver.FindExact(
                        "Left Arm Twist In-Out", "Left Upper Arm Twist In-Out");
            if (humanPartDof == 5)
                return foreArm
                    ? HumanMuscleResolver.FindExact(
                        "Right Forearm Twist In-Out", "Right ForeArm Twist In-Out")
                    : HumanMuscleResolver.FindExact(
                        "Right Arm Twist In-Out", "Right Upper Arm Twist In-Out");
            Debug.LogWarning("[QuartzArm] Unsupported humanPartDof: " + humanPartDof);
            return -1;
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

        private sealed class Driver
        {
            public readonly Transform transform;
            public readonly Quaternion restLocalRotation;
            public readonly float coefficient;
            public readonly int muscleIndex;
            public readonly float muscleToDegrees;
            public readonly string label;
            public float lastMuscle;
            public float lastAngle;

            public Driver(
                Transform target,
                float coefficientValue,
                int muscle,
                float conversion,
                string kind)
            {
                transform = target;
                restLocalRotation = target.localRotation;
                coefficient = coefficientValue;
                muscleIndex = muscle;
                muscleToDegrees = conversion;
                label = kind;
            }
        }
    }
}
