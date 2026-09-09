using System;
using System.Collections.Generic;
using System.Linq;
using ActorAnimation;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Source-readable port of ActorAnimationSwingJobSkeleton.ProcessBreastBones.
    ///
    /// The production component owns one bilateral job rather than two ordinary
    /// ActorSwingDynamicBone chains.  Both endpoints use the same fixed-step
    /// spring/pendulum integration, optional forearm-driven up/side correction,
    /// a shared Euler limit and a final cross-side average.  Only the BreastSkin
    /// root rotation is written; the End transform remains its authored child.
    /// </summary>
    [DefaultExecutionOrder(905)]
    public sealed class BreastDynamicsSystem : MonoBehaviour
    {
        private const float FixedDt = 0.01667f;
        private const float DampFactor = 40f;
        private const float BilateralStepScale = 0.5f;
        private const int PrewarmSteps = 30;
        private const int MaxSubsteps = 4;
        private const float ResetDistance = 0.5f;

        [Range(0f, 1.5f)] public float strength = 1f;
        [Range(0f, 1f)] public float correctionStrength = 1f;

        private readonly List<JobState> _jobs = new List<JobState>();
        private Transform _hips;
        private Vector3 _lastHipsPosition;
        private float _accumulator;
        private bool _initialized;
        private bool _dumpStateForDiagnostics;
        private float _manageRootWeight = 1f;
        private float _manageRootHorizontalWeight = 1f;
        private float _manageRootVerticalWeight = 1f;

        public int JobCount { get { return _jobs.Count; } }
        public int DrivenBoneCount { get { return _jobs.Count * 2; } }
        public float MeanEndpointDisplacementMillimeters { get; private set; }
        public float PeakEndpointDisplacementMillimeters { get; private set; }

        public void SetManagerRootWeights(
            float rootWeight,
            float horizontalWeight,
            float verticalWeight)
        {
            _manageRootWeight = Mathf.Clamp01(rootWeight);
            _manageRootHorizontalWeight = Mathf.Clamp01(horizontalWeight);
            _manageRootVerticalWeight = Mathf.Clamp01(verticalWeight);
        }

        public void Initialize(Transform modelRoot)
        {
            _jobs.Clear();
            _dumpStateForDiagnostics = Environment.GetCommandLineArgs()
                .Contains("--dump-dynamics-state");
            if (Environment.GetCommandLineArgs().Contains("--disable-breast-dynamics"))
            {
                Debug.Log("[ActorSwingBreast] Disabled by diagnostic command line");
                _initialized = false;
                return;
            }

            _hips = FindDescendant(modelRoot, "Hips");
            foreach (ActorSwingBreastBone component in
                     modelRoot.GetComponentsInChildren<ActorSwingBreastBone>(true))
            {
                component.enabled = false;
                if (!IsValid(component, modelRoot))
                {
                    Debug.LogWarning("[ActorSwingBreast] Ignored incomplete component on " +
                        component.transform.name);
                    continue;
                }
                _jobs.Add(new JobState(component));
            }

            _initialized = _jobs.Count > 0;
            _lastHipsPosition = _hips == null ? modelRoot.position : _hips.position;
            ResetSimulation();
            Debug.Log(string.Format(
                "[ActorSwingBreast] Bilateral original-structure replay ready: " +
                "jobs={0} drivenBones={1} profiles={2}",
                JobCount, DrivenBoneCount,
                _jobs.Count == 0 ? "-" : string.Join(";", _jobs.Select(job =>
                    string.Format(
                        "damp:{0:R}/stiff:{1:R}/spring:{2:R}/pend:{3:R}/range:{4:R}/avg:{5:R}/root:{6:R}/arm:{7}",
                        job.setting.damping, job.setting.stiffness,
                        job.setting.spring, job.setting.pendulum,
                        job.setting.pendulumRange, job.setting.average,
                        job.setting.rootWeight,
                        job.setting.useArmCorrection ? 1 : 0)).ToArray())));
        }

        public void ResetSimulation()
        {
            foreach (JobState job in _jobs)
            {
                RestoreAuthoredPose(job.left);
                RestoreAuthoredPose(job.right);
                CaptureAuthoredPose(job.left);
                CaptureAuthoredPose(job.right);
                ResetSide(job.left);
                ResetSide(job.right);
            }

            for (int index = 0; index < PrewarmSteps; index++)
                SimulateStep(FixedDt, true);
            SimulateStep(FixedDt, false);
            ApplyRuntimePose();
            _accumulator = 0f;
            CollectMetrics();
        }

        private void LateUpdate()
        {
            if (!_initialized || _jobs.Count == 0) return;

            foreach (JobState job in _jobs)
            {
                RestoreAuthoredPose(job.left);
                RestoreAuthoredPose(job.right);
                CaptureAuthoredPose(job.left);
                CaptureAuthoredPose(job.right);
            }

            Vector3 hipsPosition = _hips == null ? transform.position : _hips.position;
            Vector3 hipsTranslation = hipsPosition - _lastHipsPosition;
            _lastHipsPosition = hipsPosition;
            foreach (JobState job in _jobs)
            {
                Vector3 cancellation = GetRootCorrectionCancelPosition(
                    hipsTranslation, job.setting.rootWeight);
                job.left.endpointPosition += cancellation;
                job.right.endpointPosition += cancellation;
                job.lastRootTranslationCancellation = cancellation;
                if (!IsFinite(job.left.endpointPosition) ||
                    Vector3.Distance(job.left.endpointPosition,
                        job.left.authoredEndPosition) > ResetDistance)
                    ResetSide(job.left);
                if (!IsFinite(job.right.endpointPosition) ||
                    Vector3.Distance(job.right.endpointPosition,
                        job.right.authoredEndPosition) > ResetDistance)
                    ResetSide(job.right);
            }

            if (strength <= 0.001f)
            {
                foreach (JobState job in _jobs)
                {
                    ResetSide(job.left);
                    ResetSide(job.right);
                }
                ApplyRuntimePose();
                CollectMetrics();
                return;
            }

            _accumulator = Mathf.Min(
                _accumulator + Mathf.Min(Time.deltaTime, 0.10f),
                FixedDt * MaxSubsteps);
            int steps = 0;
            while (_accumulator >= FixedDt && steps < MaxSubsteps)
            {
                SimulateStep(FixedDt, false);
                _accumulator -= FixedDt;
                steps++;
            }
            ApplyRuntimePose();
            CollectMetrics();
        }

        private void SimulateStep(float dt, bool prewarming)
        {
            float integrationScale = dt * DampFactor * BilateralStepScale *
                Mathf.Max(0f, strength);
            foreach (JobState job in _jobs)
            {
                Quaternion leftSolved = SimulateSide(
                    job, job.left, integrationScale, prewarming, 1f);
                Quaternion rightSolved = SimulateSide(
                    job, job.right, integrationScale, prewarming, -1f);

                float average = Mathf.Clamp01(job.setting.average);
                // ProcessBreastBones computes both blends from the unmodified
                // pair.  Do not feed the already averaged left result into the
                // right side.
                job.left.solvedRotation = Quaternion.SlerpUnclamped(
                    leftSolved, rightSolved, average);
                job.right.solvedRotation = Quaternion.SlerpUnclamped(
                    rightSolved, leftSolved, average);
            }
        }

        private Quaternion SimulateSide(
            JobState job,
            SideState side,
            float integrationScale,
            bool prewarming,
            float sideSign)
        {
            Vector3 origin = side.bone.position;
            Quaternion defaultRotation = side.authoredRotation;
            Vector3 authoredDirection = SafeDirection(
                defaultRotation * side.boneAxis,
                side.authoredEndPosition - origin);
            float length = side.boneLength;
            if (length <= 0.00001f)
            {
                side.endpointPosition = side.authoredEndPosition;
                side.childSpeed = Vector3.zero;
                return defaultRotation;
            }

            Vector3 currentDirection = SafeDirection(
                side.endpointPosition - origin, authoredDirection);
            float damping = Mathf.Clamp01(job.setting.damping);
            float velocityRetention = (1f - damping) * (1f - damping);
            Vector3 poseFollowing =
                (side.authoredEndPosition - side.endpointPosition) *
                velocityRetention;
            float stiffness = EffectiveSwingStiffness(
                job.setting, authoredDirection, currentDirection);
            Vector3 acceleration = authoredDirection * stiffness + poseFollowing;
            if (!prewarming)
                acceleration += side.childSpeed * Mathf.Max(0f, job.setting.spring);
            if (job.setting.useArmCorrection && correctionStrength > 0.0001f)
                acceleration += ComputeArmCorrection(
                    job, side, authoredDirection, sideSign) * correctionStrength;

            Vector3 rawDelta = acceleration * integrationScale;
            Vector3 candidate = side.endpointPosition + rawDelta;
            candidate = origin + SafeDirection(
                candidate - origin, authoredDirection) * length;
            side.childSpeed = rawDelta;
            side.endpointPosition = candidate;

            Quaternion swing = Quaternion.FromToRotation(
                authoredDirection,
                SafeDirection(candidate - origin, authoredDirection));
            Quaternion solved = swing * defaultRotation;
            solved = ApplyLimit(job.setting.limitInfo, defaultRotation, solved);
            side.solvedRotation = solved;
            return solved;
        }

        private static Vector3 ComputeArmCorrection(
            JobState job,
            SideState side,
            Vector3 authoredDirection,
            float sideSign)
        {
            if (side.lowerArm == null) return Vector3.zero;
            Vector3 armDirection = side.lowerArm.position - side.bone.position;
            if (armDirection.sqrMagnitude <= 0.00000001f) return Vector3.zero;
            armDirection.Normalize();
            Vector3 localArm = Quaternion.Inverse(side.authoredRotation) * armDirection;
            float up = job.setting.upCurve == null
                ? 0f
                : job.setting.upCurve.Evaluate(Mathf.Clamp(localArm.y, -1f, 1f));
            float angleRadians = Mathf.Acos(Mathf.Clamp(
                Vector3.Dot(authoredDirection, armDirection), -1f, 1f));
            float lateral = job.setting.sideCurve == null
                ? 0f
                : job.setting.sideCurve.Evaluate(angleRadians) * sideSign;
            return side.authoredRotation * new Vector3(0f, up, lateral);
        }

        private static float EffectiveSwingStiffness(
            ActorSwingBreastBone setting,
            Vector3 authoredDirection,
            Vector3 currentDirection)
        {
            float stiffness = setting.stiffness;
            float pendulum = setting.pendulum;
            float range = setting.pendulumRange;
            if (pendulum <= 0f || range <= 0.00001f) return stiffness;
            float alignment = Mathf.Abs(Vector3.Dot(
                authoredDirection, currentDirection));
            float active = Mathf.Max(0f, alignment - (1f - range));
            return stiffness - active / range * pendulum;
        }

        private static Quaternion ApplyLimit(
            SwingLimitInfo limit,
            Quaternion defaultRotation,
            Quaternion solvedRotation)
        {
            if (limit == null || limit.useLimit != 1) return solvedRotation;
            Quaternion relative = Quaternion.Inverse(defaultRotation) * solvedRotation;
            Vector3 euler = ToSignedEuler(relative.eulerAngles);
            euler.x = Mathf.Clamp(euler.x, limit.axisX.x, limit.axisX.y);
            euler.y = Mathf.Clamp(euler.y, limit.axisY.x, limit.axisY.y);
            euler.z = Mathf.Clamp(euler.z, limit.axisZ.x, limit.axisZ.y);
            return defaultRotation * Quaternion.Euler(euler);
        }

        private Vector3 GetRootCorrectionCancelPosition(
            Vector3 hipsTranslation,
            float boneRootWeight)
        {
            float boneWeight = Mathf.Clamp01(boneRootWeight);
            float horizontalCancel = 1f - _manageRootWeight *
                _manageRootHorizontalWeight * boneWeight;
            float verticalCancel = 1f - _manageRootWeight *
                _manageRootVerticalWeight * boneWeight;
            return new Vector3(
                hipsTranslation.x * horizontalCancel,
                hipsTranslation.y * verticalCancel,
                hipsTranslation.z * horizontalCancel);
        }

        private void ApplyRuntimePose()
        {
            foreach (JobState job in _jobs)
            {
                job.left.bone.rotation = job.left.solvedRotation;
                job.right.bone.rotation = job.right.solvedRotation;
            }
        }

        private void CollectMetrics()
        {
            float sum = 0f;
            float peak = 0f;
            int count = 0;
            foreach (JobState job in _jobs)
            {
                foreach (SideState side in new[] { job.left, job.right })
                {
                    float millimeters = Vector3.Distance(
                        side.endpointPosition, side.authoredEndPosition) * 1000f;
                    sum += millimeters;
                    peak = Mathf.Max(peak, millimeters);
                    count++;
                }
            }
            MeanEndpointDisplacementMillimeters = sum / Mathf.Max(1, count);
            PeakEndpointDisplacementMillimeters = peak;
        }

        public void LogCurrentState(string label)
        {
            if (!_dumpStateForDiagnostics) return;
            foreach (JobState job in _jobs)
            {
                Debug.Log(string.Format(
                    "[ActorSwingBreastState] label={0} owner={1} " +
                    "leftDeltaMm={2} rightDeltaMm={3} leftSpeed={4} rightSpeed={5} " +
                    "rootCancellation={6}",
                    label, job.setting.transform.name,
                    ((job.left.endpointPosition - job.left.authoredEndPosition) *
                        1000f).ToString("F4"),
                    ((job.right.endpointPosition - job.right.authoredEndPosition) *
                        1000f).ToString("F4"),
                    job.left.childSpeed.ToString("F6"),
                    job.right.childSpeed.ToString("F6"),
                    job.lastRootTranslationCancellation.ToString("F6")));
            }
            Debug.Log(string.Format(
                "[ActorSwingBreastSummary] label={0} jobs={1} driven={2} " +
                "meanEndpointMm={3:0.0000} peakEndpointMm={4:0.0000}",
                label, JobCount, DrivenBoneCount,
                MeanEndpointDisplacementMillimeters,
                PeakEndpointDisplacementMillimeters));
        }

        private static void RestoreAuthoredPose(SideState side)
        {
            side.bone.localPosition = side.boneRestLocalPosition;
            side.bone.localRotation = side.boneRestLocalRotation;
            side.end.localPosition = side.endRestLocalPosition;
            side.end.localRotation = side.endRestLocalRotation;
        }

        private static void CaptureAuthoredPose(SideState side)
        {
            side.authoredPosition = side.bone.position;
            side.authoredRotation = side.bone.rotation;
            side.authoredEndPosition = side.end.position;
        }

        private static void ResetSide(SideState side)
        {
            side.endpointPosition = side.authoredEndPosition;
            side.childSpeed = Vector3.zero;
            side.solvedRotation = side.authoredRotation;
        }

        private static bool IsValid(ActorSwingBreastBone value, Transform root)
        {
            return value != null && value.leftBreast != null &&
                value.rightBreast != null && value.leftBreastEnd != null &&
                value.rightBreastEnd != null &&
                value.leftBreast.IsChildOf(root) && value.rightBreast.IsChildOf(root) &&
                value.leftBreastEnd.IsChildOf(root) &&
                value.rightBreastEnd.IsChildOf(root);
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root == null) return null;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            return transforms.FirstOrDefault(value =>
                string.Equals(value.name, name, StringComparison.Ordinal));
        }

        private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
        {
            if (value.sqrMagnitude > 0.00000001f) return value.normalized;
            if (fallback.sqrMagnitude > 0.00000001f) return fallback.normalized;
            return Vector3.forward;
        }

        private static Vector3 ToSignedEuler(Vector3 value)
        {
            return new Vector3(
                value.x > 180f ? value.x - 360f : value.x,
                value.y > 180f ? value.y - 360f : value.y,
                value.z > 180f ? value.z - 360f : value.z);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private sealed class JobState
        {
            public readonly ActorSwingBreastBone setting;
            public readonly SideState left;
            public readonly SideState right;
            public Vector3 lastRootTranslationCancellation;

            public JobState(ActorSwingBreastBone value)
            {
                setting = value;
                left = new SideState(
                    value.leftBreast, value.leftBreastEnd, value.leftLowerArm);
                right = new SideState(
                    value.rightBreast, value.rightBreastEnd, value.rightLowerArm);
            }
        }

        private sealed class SideState
        {
            public readonly Transform bone;
            public readonly Transform end;
            public readonly Transform lowerArm;
            public readonly Vector3 boneRestLocalPosition;
            public readonly Quaternion boneRestLocalRotation;
            public readonly Vector3 endRestLocalPosition;
            public readonly Quaternion endRestLocalRotation;
            public readonly Vector3 boneAxis;
            public readonly float boneLength;
            public Vector3 authoredPosition;
            public Quaternion authoredRotation;
            public Vector3 authoredEndPosition;
            public Vector3 endpointPosition;
            public Vector3 childSpeed;
            public Quaternion solvedRotation;

            public SideState(Transform boneValue, Transform endValue, Transform armValue)
            {
                bone = boneValue;
                end = endValue;
                lowerArm = armValue;
                boneRestLocalPosition = bone.localPosition;
                boneRestLocalRotation = bone.localRotation;
                endRestLocalPosition = end.localPosition;
                endRestLocalRotation = end.localRotation;
                boneLength = endRestLocalPosition.magnitude;
                boneAxis = boneLength > 0.00001f
                    ? endRestLocalPosition / boneLength
                    : Vector3.forward;
            }
        }
    }
}
