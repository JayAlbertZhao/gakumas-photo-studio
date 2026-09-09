using System;
using System.Globalization;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Replays the captured Campus look-at contract inside the humanoid
    /// AnimationStream, before Quartz/hair LateUpdate consumers run.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class CapturedLookAtRuntime : MonoBehaviour
    {
        private Animator _animator;
        private FaceExpressionRenderer _face;
        private Camera _camera;
        private Transform _actorRoot;
        private Transform _head;
        private AnimationScriptPlayable _playable;
        private CapturedLookAtAnimationJob _job;
        private CapturedLookAtMode _mode;
        private CapturedLookAtProfile _profile;
        private float _controllerEyeWeight = 1f;
        private float _transitionStartWeight = 1f;
        private float _transitionTargetWeight = 1f;
        private float _transitionStartTime;
        private float _transitionDuration;
        private bool _transitionActive;
        private float? _diagnosticYaw;
        private float? _diagnosticPitch;
        private float _virtualAngle;
        private float _horizontalAngle;
        private float _verticalAngle;
        private Vector3 _targetPosition;
        private NativeArray<Vector3> _preIkHeadState;
        private float _actorEyeHeight;
        private bool _storyOverrideActive;
        private Vector3 _storyTargetPosition;
        private float _storyEyesWeight;
        private float _storyHeadWeight;
        private float _storyBodyWeight;
        private int _storyDiagnosticRevision;
        private bool _thresholdDiagnostic;
        private float _thresholdDiagnosticStartTime;
        private int _thresholdDiagnosticPhase = -1;

        public CapturedLookAtMode Mode { get { return _mode; } }
        public CapturedLookAtProfile Profile { get { return _profile; } }
        public float ControllerEyeWeight { get { return _controllerEyeWeight; } }
        public float VirtualAngle { get { return _virtualAngle; } }
        public Vector3 TargetPosition { get { return _targetPosition; } }
        public bool StoryOverrideActive { get { return _storyOverrideActive; } }
        public Vector3 StoryTargetPosition { get { return _storyTargetPosition; } }
        public Vector3 StoryEffectiveWeights
        {
            get { return new Vector3(_storyEyesWeight, _storyHeadWeight, _storyBodyWeight); }
        }

        public void Initialize(
            Animator animator,
            FaceExpressionRenderer face,
            Camera camera,
            Transform actorRoot,
            Transform head)
        {
            _animator = animator;
            _face = face;
            _camera = camera;
            _actorRoot = actorRoot;
            _head = head;
            _job = CapturedLookAtAnimationJob.Create();
            _preIkHeadState = new NativeArray<Vector3>(
                2, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _job.preIkHeadState = _preIkHeadState;
            if (_animator != null && _head != null)
                _job.head = _animator.BindStreamTransform(_head);
            // ActorManager.Actor.EyeHeight is captured once when the actor is
            // created. Direction LookTargetData adds that fixed height to the
            // actor layout position; it does not feed the already-solved head
            // position back into the next target.
            _actorEyeHeight = EyeCenter().y -
                (_actorRoot == null ? 0f : _actorRoot.position.y);
            ParseCommandLine();
            _thresholdDiagnosticStartTime = Time.time;
            SetMode(ModeFromCommandLine());
            UpdateTargetAndJob(true);
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[CapturedLookAt] ready mode={0}; eyes-mapped={1}/{2}; out/again={3:0}/{4:0}; target-yaw={5}; target-pitch={6}",
                _mode,
                HasHumanBone(HumanBodyBones.LeftEye),
                HasHumanBone(HumanBodyBones.RightEye),
                CapturedLookAtProfile.LookOutAngle,
                CapturedLookAtProfile.LookAgainAngle,
                _diagnosticYaw.HasValue ? _diagnosticYaw.Value.ToString("0.###", CultureInfo.InvariantCulture) : "camera",
                _diagnosticPitch.HasValue ? _diagnosticPitch.Value.ToString("0.###", CultureInfo.InvariantCulture) : "eye-plane"));
        }

        public AnimationScriptPlayable CreatePlayable(
            PlayableGraph graph,
            Playable input)
        {
            _playable = AnimationScriptPlayable.Create(graph, _job, 1);
            graph.Connect(input, 0, _playable, 0);
            _playable.SetInputWeight(0, 1f);
            UpdateTargetAndJob(true);
            return _playable;
        }

        /// <summary>
        /// Campus.ADV.ActorManager.Actor.GetLookTargetDirection composes the
        /// two world-space axes in this order, then GetTargetPosition places a
        /// point ten metres from Actor layout position + fixed EyeHeight.
        /// The 10.0 literal is recovered from live rdata VA 0x7fff098e7d24.
        /// </summary>
        public Vector3 ResolveStoryDirectionTarget(float azimuth, float elevation)
        {
            if (_actorRoot == null) return Vector3.zero;
            Quaternion actorRotation = _actorRoot.rotation;
            Quaternion elevationRotation = Quaternion.AngleAxis(
                elevation, actorRotation * Vector3.right);
            Quaternion azimuthRotation = Quaternion.AngleAxis(
                azimuth, actorRotation * Vector3.up);
            Vector3 direction = elevationRotation * azimuthRotation * _actorRoot.forward;
            Vector3 basePosition = _actorRoot.position;
            basePosition.y += _actorEyeHeight;
            return basePosition + direction.normalized * 10f;
        }

        public void SetStoryLookTarget(
            Vector3 targetPosition,
            float eyesWeight,
            float headWeight,
            float bodyWeight)
        {
            _storyOverrideActive = true;
            _storyTargetPosition = targetPosition;
            _storyEyesWeight = Mathf.Max(0f, eyesWeight);
            _storyHeadWeight = Mathf.Max(0f, headWeight);
            _storyBodyWeight = Mathf.Max(0f, bodyWeight);
            _storyDiagnosticRevision++;
            UpdateTargetAndJob(true);
        }

        public void ClearStoryLookTarget()
        {
            if (!_storyOverrideActive) return;
            _storyOverrideActive = false;
            _storyEyesWeight = 0f;
            _storyHeadWeight = 0f;
            _storyBodyWeight = 0f;
            _storyDiagnosticRevision++;
            UpdateTargetAndJob(true);
        }

        public string StoryDiagnosticJson()
        {
            CultureInfo culture = CultureInfo.InvariantCulture;
            return "{\"active\":" + (_storyOverrideActive ? "true" : "false") +
                ",\"revision\":" + _storyDiagnosticRevision.ToString(culture) +
                ",\"eyeHeight\":" + _actorEyeHeight.ToString("R", culture) +
                ",\"target\":[" +
                _storyTargetPosition.x.ToString("R", culture) + "," +
                _storyTargetPosition.y.ToString("R", culture) + "," +
                _storyTargetPosition.z.ToString("R", culture) + "]," +
                "\"effectiveWeights\":{\"eyes\":" +
                _storyEyesWeight.ToString("R", culture) +
                ",\"head\":" + _storyHeadWeight.ToString("R", culture) +
                ",\"body\":" + _storyBodyWeight.ToString("R", culture) + "}}";
        }

        public void SetMode(CapturedLookAtMode mode)
        {
            _mode = mode;
            _transitionActive = false;
            if (mode == CapturedLookAtMode.Natural)
                _controllerEyeWeight = 1f;
            else if (mode == CapturedLookAtMode.Cocchi)
                _controllerEyeWeight = 0f;
            else if (mode == CapturedLookAtMode.Gaze)
                _controllerEyeWeight = _virtualAngle >= CapturedLookAtProfile.LookOutAngle ? 0f : 1f;
            _profile = CapturedLookAtProfile.Evaluate(_controllerEyeWeight);
            UpdateTargetAndJob(true);
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[CapturedLookAt] mode={0}; controllerEye={1:0.000}; effector eyes/head/body={2:0.000}/{3:0.000}/{4:0.000}",
                _mode, _controllerEyeWeight, _profile.eyesWeight, _profile.headWeight, _profile.bodyWeight));
        }

        public void CycleMode()
        {
            CapturedLookAtMode next = _mode == CapturedLookAtMode.Natural
                ? CapturedLookAtMode.Gaze
                : _mode == CapturedLookAtMode.Gaze
                    ? CapturedLookAtMode.Cocchi
                    : CapturedLookAtMode.Natural;
            SetMode(next);
        }

        private void Update()
        {
            UpdateTargetAndJob(false);
        }

        private void OnDisable()
        {
            if (_face != null) _face.SetCapturedLookAtOwned(false);
        }

        private void OnDestroy()
        {
            if (_preIkHeadState.IsCreated) _preIkHeadState.Dispose();
        }

        private void UpdateTargetAndJob(bool force)
        {
            if (_camera == null || _actorRoot == null || _head == null) return;
            // LookAtUtility uses GetFullBodyIkHeadOriginalPosition/Forward: the
            // animated head pose before humanoid IK, not the actor root and not
            // last frame's already-solved head. The animation job publishes that
            // pre-IK pose; consuming it one Update later is stable and differs by
            // only one rendered frame while avoiding solver feedback.
            Vector3 origin = EyeCenter();
            Vector3 referenceForward = _actorRoot.forward;
            if (_preIkHeadState.IsCreated &&
                _preIkHeadState[1].sqrMagnitude > 0.25f)
            {
                origin = _preIkHeadState[0];
                referenceForward = _preIkHeadState[1].normalized;
            }
            Vector3 horizontalForward = Vector3.ProjectOnPlane(
                referenceForward, Vector3.up).normalized;
            if (horizontalForward.sqrMagnitude < 0.001f)
                horizontalForward = Vector3.ProjectOnPlane(
                    _actorRoot.forward, Vector3.up).normalized;
            if (horizontalForward.sqrMagnitude < 0.001f)
                horizontalForward = Vector3.forward;

            float? diagnosticYaw = _diagnosticYaw;
            if (_thresholdDiagnostic)
            {
                float elapsed = Time.time - _thresholdDiagnosticStartTime;
                int phase = elapsed < 1.5f ? 0 : elapsed < 3.75f ? 1 : 2;
                diagnosticYaw = phase == 0 ? 0f : phase == 1 ? 85f : 55f;
                if (phase != _thresholdDiagnosticPhase)
                {
                    _thresholdDiagnosticPhase = phase;
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[CapturedLookAt] threshold-diagnostic phase={0}; yaw={1:0.0}; elapsed={2:0.000}",
                        phase, diagnosticYaw.Value, elapsed));
                }
            }

            if (diagnosticYaw.HasValue || _diagnosticPitch.HasValue)
            {
                float yaw = diagnosticYaw ?? 0f;
                float pitch = _diagnosticPitch ?? 0f;
                Vector3 direction = Quaternion.AngleAxis(yaw, Vector3.up) * horizontalForward;
                Vector3 right = Vector3.Cross(Vector3.up, direction).normalized;
                direction = Quaternion.AngleAxis(-pitch, right) * direction;
                _targetPosition = origin + direction.normalized * 3f;
            }
            else
            {
                // pass192 target Y was stable at the actor eye-height plane,
                // rather than using the raw camera Y coordinate.
                _targetPosition = _camera.transform.position;
                _targetPosition.y = origin.y;
            }

            Vector3 directionToTarget = _targetPosition - origin;
            Vector3 flatDirection = Vector3.ProjectOnPlane(directionToTarget, Vector3.up);
            _virtualAngle = Vector3.Angle(referenceForward, directionToTarget.normalized);
            _horizontalAngle = flatDirection.sqrMagnitude < 0.0001f
                ? 0f
                : Vector3.SignedAngle(horizontalForward, flatDirection.normalized, Vector3.up);
            float referencePitch = Mathf.Atan2(
                referenceForward.y,
                Mathf.Max(0.0001f,
                    Vector3.ProjectOnPlane(referenceForward, Vector3.up).magnitude)) * Mathf.Rad2Deg;
            _verticalAngle = Mathf.Atan2(directionToTarget.y,
                Mathf.Max(0.0001f, flatDirection.magnitude)) * Mathf.Rad2Deg - referencePitch;

            bool storyGazeMode = _face != null && _face.StoryGazeActive;
            bool active = _storyOverrideActive ||
                (_mode != CapturedLookAtMode.Off && !storyGazeMode);
            bool photoLookAtActive = active && !_storyOverrideActive;
            if (_mode == CapturedLookAtMode.Gaze && photoLookAtActive)
            {
                if (!_transitionActive && _controllerEyeWeight >= 0.9999f &&
                    _virtualAngle >= CapturedLookAtProfile.LookOutAngle)
                    BeginTransition(0f);
                else if (!_transitionActive && _controllerEyeWeight <= 0.0001f &&
                    _virtualAngle <= CapturedLookAtProfile.LookAgainAngle)
                    BeginTransition(1f);
                UpdateTransition();
            }

            _profile = CapturedLookAtProfile.Evaluate(_controllerEyeWeight);
            _job.enabled = active ? 1 : 0;
            _job.targetPosition = _storyOverrideActive
                ? _storyTargetPosition
                : _targetPosition;
            _job.weight = _storyOverrideActive ? 1f : _profile.weight;
            _job.eyesWeight = _storyOverrideActive
                ? _storyEyesWeight
                : _profile.eyesWeight;
            _job.headWeight = _storyOverrideActive
                ? _storyHeadWeight
                : _profile.headWeight;
            _job.bodyWeight = _storyOverrideActive
                ? _storyBodyWeight
                : _profile.bodyWeight;
            _job.eyeInOutLimit = _profile.eyeInOutLimit;
            _job.eyeUpLimit = _profile.eyeUpLimit;
            _job.eyeDownLimit = _profile.eyeDownLimit;
            _job.eyeSideCorrectionWeight = CapturedLookAtProfile.EyeSideCorrectionWeight;
            _job.headLeftRightLimit = _profile.headLeftRightLimit;
            _job.headFrontLimit = _profile.headFrontLimit;
            _job.headBackLimit = _profile.headBackLimit;
            _job.headRollLimit = _profile.headRollLeftRightLimit;
            _job.neckLeftRightLimit = _profile.neckLeftRightLimit;
            _job.neckFrontLimit = _profile.neckFrontLimit;
            _job.neckBackLimit = _profile.neckBackLimit;
            _job.neckRollLimit = _profile.neckRollLeftRightLimit;
            _job.bodyLeftRightLimit = _profile.bodyLeftRightLimit;
            _job.bodyFrontLimit = _profile.bodyFrontLimit;
            _job.bodyBackLimit = _profile.bodyBackLimit;
            _job.bodyTwistLimit = _profile.bodyTwistLeftRightLimit;
            if (_playable.IsValid()) _playable.SetJobData(_job);
            if (_face != null) _face.SetCapturedLookAtOwned(active);
        }

        private void BeginTransition(float targetEyeWeight)
        {
            _transitionStartWeight = _controllerEyeWeight;
            _transitionTargetWeight = targetEyeWeight;
            _transitionStartTime = Time.time;
            _transitionDuration = CapturedLookAtProfile.EaseDuration *
                Mathf.Max(0.0001f, Mathf.Abs(_transitionTargetWeight - _transitionStartWeight));
            _transitionActive = true;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[CapturedLookAt] hysteresis {0}: virtual={1:0.000}, horizontal={2:0.000}, vertical={3:0.000}, duration={4:0.000}",
                targetEyeWeight < 0.5f ? "look-out" : "look-again",
                _virtualAngle, _horizontalAngle, _verticalAngle, _transitionDuration));
        }

        private void UpdateTransition()
        {
            if (!_transitionActive) return;
            float t = Mathf.Clamp01((Time.time - _transitionStartTime) /
                Mathf.Max(0.0001f, _transitionDuration));
            // Matching DOTween Ease numeric constants from LookAtDefine:
            // to-body=3 (OutSine), to-eye=9 (OutCubic).
            float eased = _transitionTargetWeight < _transitionStartWeight
                ? Mathf.Sin(t * Mathf.PI * 0.5f)
                : 1f - Mathf.Pow(1f - t, 3f);
            _controllerEyeWeight = Mathf.Lerp(
                _transitionStartWeight, _transitionTargetWeight, eased);
            if (t >= 1f)
            {
                _controllerEyeWeight = _transitionTargetWeight;
                _transitionActive = false;
                Debug.Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "[CapturedLookAt] hysteresis complete: controllerEye={0:0.000}; elapsed={1:0.000}",
                    _controllerEyeWeight, Time.time - _transitionStartTime));
            }
        }

        private Vector3 EyeCenter()
        {
            if (_face != null && _face.HasEyeBones) return _face.EyeCenterPosition;
            return _head.position;
        }

        private bool HasHumanBone(HumanBodyBones bone)
        {
            return _animator != null && _animator.isHuman && _animator.GetBoneTransform(bone) != null;
        }

        private CapturedLookAtMode ModeFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains("--lookat-off") || args.Contains("--ambient-wallpaper-demo"))
                return CapturedLookAtMode.Off;
            if (args.Contains("--lookat-natural")) return CapturedLookAtMode.Natural;
            if (args.Contains("--lookat-cocchi")) return CapturedLookAtMode.Cocchi;
            if (args.Contains("--lookat-gaze")) return CapturedLookAtMode.Gaze;
            return args.Contains("--photo-mode") ? CapturedLookAtMode.Gaze : CapturedLookAtMode.Off;
        }

        private void ParseCommandLine()
        {
            _diagnosticYaw = CommandLineFloat("--lookat-target-yaw");
            _diagnosticPitch = CommandLineFloat("--lookat-target-pitch");
            _thresholdDiagnostic = Environment.GetCommandLineArgs()
                .Contains("--lookat-threshold-diagnostic");
        }

        private static float? CommandLineFloat(string option)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < args.Length; index++)
            {
                float value;
                if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) &&
                    float.TryParse(args[index + 1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value))
                    return value;
            }
            return null;
        }
    }

    public struct CapturedLookAtAnimationJob : IAnimationJob
    {
        public int enabled;
        public Vector3 targetPosition;
        public float weight;
        public float eyesWeight;
        public float headWeight;
        public float bodyWeight;
        public float eyeInOutLimit;
        public float eyeUpLimit;
        public float eyeDownLimit;
        public float eyeSideCorrectionWeight;
        public float headLeftRightLimit;
        public float headFrontLimit;
        public float headBackLimit;
        public float headRollLimit;
        public float neckLeftRightLimit;
        public float neckFrontLimit;
        public float neckBackLimit;
        public float neckRollLimit;
        public float bodyLeftRightLimit;
        public float bodyFrontLimit;
        public float bodyBackLimit;
        public float bodyTwistLimit;
        public TransformStreamHandle head;
        public NativeArray<Vector3> preIkHeadState;

        private MuscleHandle _leftEyeDownUp;
        private MuscleHandle _leftEyeInOut;
        private MuscleHandle _rightEyeDownUp;
        private MuscleHandle _rightEyeInOut;
        private MuscleHandle _headFrontBack;
        private MuscleHandle _headLeftRight;
        private MuscleHandle _headRoll;
        private MuscleHandle _neckFrontBack;
        private MuscleHandle _neckLeftRight;
        private MuscleHandle _neckRoll;
        private MuscleHandle _spineFrontBack;
        private MuscleHandle _spineLeftRight;
        private MuscleHandle _spineRoll;
        private MuscleHandle _chestFrontBack;
        private MuscleHandle _chestLeftRight;
        private MuscleHandle _chestRoll;
        private MuscleHandle _upperChestFrontBack;
        private MuscleHandle _upperChestLeftRight;
        private MuscleHandle _upperChestRoll;

        public static CapturedLookAtAnimationJob Create()
        {
            return new CapturedLookAtAnimationJob
            {
                _leftEyeDownUp = new MuscleHandle(HeadDof.LeftEyeDownUp),
                _leftEyeInOut = new MuscleHandle(HeadDof.LeftEyeInOut),
                _rightEyeDownUp = new MuscleHandle(HeadDof.RightEyeDownUp),
                _rightEyeInOut = new MuscleHandle(HeadDof.RightEyeInOut),
                _headFrontBack = new MuscleHandle(HeadDof.HeadFrontBack),
                _headLeftRight = new MuscleHandle(HeadDof.HeadLeftRight),
                _headRoll = new MuscleHandle(HeadDof.HeadRollLeftRight),
                _neckFrontBack = new MuscleHandle(HeadDof.NeckFrontBack),
                _neckLeftRight = new MuscleHandle(HeadDof.NeckLeftRight),
                _neckRoll = new MuscleHandle(HeadDof.NeckRollLeftRight),
                _spineFrontBack = new MuscleHandle(BodyDof.SpineFrontBack),
                _spineLeftRight = new MuscleHandle(BodyDof.SpineLeftRight),
                _spineRoll = new MuscleHandle(BodyDof.SpineRollLeftRight),
                _chestFrontBack = new MuscleHandle(BodyDof.ChestFrontBack),
                _chestLeftRight = new MuscleHandle(BodyDof.ChestLeftRight),
                _chestRoll = new MuscleHandle(BodyDof.ChestRollLeftRight),
                _upperChestFrontBack = new MuscleHandle(BodyDof.UpperChestFrontBack),
                _upperChestLeftRight = new MuscleHandle(BodyDof.UpperChestLeftRight),
                _upperChestRoll = new MuscleHandle(BodyDof.UpperChestRollLeftRight),
            };
        }

        public void ProcessRootMotion(AnimationStream stream)
        {
        }

        public void ProcessAnimation(AnimationStream stream)
        {
            if (enabled == 0 || !stream.isHumanStream) return;
            if (preIkHeadState.IsCreated && head.IsValid(stream))
            {
                preIkHeadState[0] = head.GetPosition(stream);
                preIkHeadState[1] = head.GetRotation(stream) * Vector3.forward;
            }
            AnimationHumanStream human = stream.AsHuman();
            human.SetLookAtPosition(targetPosition);
            human.SetLookAtClampWeight(0f);
            human.SetLookAtBodyWeight(bodyWeight * weight);
            human.SetLookAtHeadWeight(headWeight * weight);
            human.SetLookAtEyesWeight(eyesWeight * weight);
            human.SolveIK();

            Clamp(ref human, _leftEyeInOut, -eyeInOutLimit, eyeInOutLimit);
            Clamp(ref human, _rightEyeInOut, -eyeInOutLimit, eyeInOutLimit);
            Clamp(ref human, _leftEyeDownUp, eyeDownLimit, eyeUpLimit);
            Clamp(ref human, _rightEyeDownUp, eyeDownLimit, eyeUpLimit);

            // The original CampusActorAnimation job solves the humanoid look-at
            // first. CampusActorController.LateUpdate then applies
            // AlwaysSideCorrection to LeftEye/RightEye and scales only their
            // horizontal local Euler component by the de-facto 0.25 weight.
            // Scaling the corresponding humanoid muscle here is the equivalent
            // stream-space operation and avoids the four-times-too-wide stare.
            Scale(ref human, _leftEyeInOut, eyeSideCorrectionWeight);
            Scale(ref human, _rightEyeInOut, eyeSideCorrectionWeight);
            Clamp(ref human, _headLeftRight, -headLeftRightLimit, headLeftRightLimit);
            Clamp(ref human, _headFrontBack, headBackLimit, headFrontLimit);
            Clamp(ref human, _headRoll, -headRollLimit, headRollLimit);
            Clamp(ref human, _neckLeftRight, -neckLeftRightLimit, neckLeftRightLimit);
            Clamp(ref human, _neckFrontBack, neckBackLimit, neckFrontLimit);
            Clamp(ref human, _neckRoll, -neckRollLimit, neckRollLimit);
            ClampBody(ref human, _spineFrontBack, _spineLeftRight, _spineRoll);
            ClampBody(ref human, _chestFrontBack, _chestLeftRight, _chestRoll);
            ClampBody(ref human, _upperChestFrontBack, _upperChestLeftRight, _upperChestRoll);
        }

        private void ClampBody(
            ref AnimationHumanStream human,
            MuscleHandle frontBack,
            MuscleHandle leftRight,
            MuscleHandle roll)
        {
            Clamp(ref human, frontBack, bodyBackLimit, bodyFrontLimit);
            Clamp(ref human, leftRight, -bodyLeftRightLimit, bodyLeftRightLimit);
            Clamp(ref human, roll, -bodyTwistLimit, bodyTwistLimit);
        }

        private static void Clamp(
            ref AnimationHumanStream human,
            MuscleHandle handle,
            float minimum,
            float maximum)
        {
            human.SetMuscle(handle, Mathf.Clamp(human.GetMuscle(handle), minimum, maximum));
        }

        private static void Scale(
            ref AnimationHumanStream human,
            MuscleHandle handle,
            float scale)
        {
            human.SetMuscle(handle, human.GetMuscle(handle) * scale);
        }
    }
}
