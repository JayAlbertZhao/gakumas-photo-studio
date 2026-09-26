using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenSwing
{
    /// <summary>
    /// Binds a user supplied transform hierarchy and configuration to the swing solver.
    /// Restore occurs before Animator evaluation; simulation occurs after it.
    /// For manual AnimationClip.SampleAnimation calls, invoke RestoreBeforeAnimation
    /// and SimulateAfterAnimation around the sample instead of using automaticUpdate.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class HairSwingAdapter : MonoBehaviour
    {
        [Serializable]
        public struct Binding
        {
            public string name;
            public Transform transform;
        }

        [SerializeField] private Transform animatedRoot = null;
        [SerializeField] private TextAsset configuration = null;
        [SerializeField] private Transform motionSource = null;
        [SerializeField] private Transform head = null;
        [SerializeField] private Transform neck = null;
        [SerializeField] private Binding[] explicitBindings = Array.Empty<Binding>();
        [SerializeField] private bool automaticUpdate = true;
        [SerializeField] private bool fixedStep = true;
        [SerializeField] private bool enableCollision = true;
        [SerializeField] private bool enableWind = false;
        [SerializeField, Min(0f)] private float resetDistance = 2f;

        private ActorAnimationSwingSolver solver;
        private FixedStepSwingClock clock;
        private Vector3 lastPosition;
        private bool hasLastPosition;

        public ActorAnimationSwingSolver Solver => solver;

        private void Awake()
        {
            if (!animatedRoot || !configuration)
            {
                Debug.LogError("Assign an animated root and a swing configuration.", this);
                enabled = false;
                return;
            }

            var bindings = BuildBindings();
            var host = new GameObject("Swing solver");
            host.SetActive(false);
            host.transform.SetParent(animatedRoot, false);
            solver = host.AddComponent<ActorAnimationSwingSolver>();
            solver.configuration = configuration;
            solver.bindings = bindings;
            solver.rootMotionSource = motionSource ? motionSource : animatedRoot;
            solver.head = head;
            solver.neck = neck;
            solver.manualSimulation = true;
            solver.enableCollision = enableCollision;
            solver.enableWind = enableWind;
            host.SetActive(true);
            solver.Initialize();
            if (solver.SimulatedNodes == 0)
                Debug.LogWarning("No simulated segments were bound. Check names and child joints.", this);
            if (fixedStep) clock = new FixedStepSwingClock(solver, animatedRoot);
        }

        private Dictionary<string, Transform> BuildBindings()
        {
            var result = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (explicitBindings != null && explicitBindings.Length > 0)
            {
                foreach (var item in explicitBindings)
                {
                    if (string.IsNullOrEmpty(item.name) || !item.transform) continue;
                    if (!result.TryAdd(item.name, item.transform))
                        throw new InvalidOperationException("Duplicate swing binding: " + item.name);
                }
            }
            else
            {
                foreach (var bone in animatedRoot.GetComponentsInChildren<Transform>(true))
                    if (!result.TryAdd(bone.name, bone))
                        throw new InvalidOperationException("Duplicate transform name: " + bone.name + ". Supply explicit bindings.");
            }
            return result;
        }

        private void Update()
        {
            if (automaticUpdate) RestoreBeforeAnimation();
        }

        private void LateUpdate()
        {
            if (automaticUpdate) SimulateAfterAnimation(Time.unscaledDeltaTime);
        }

        public void RestoreBeforeAnimation()
        {
            if (solver) solver.RestorePose();
        }

        public void SimulateAfterAnimation(float deltaTime)
        {
            if (!solver) return;
            var position = animatedRoot.position;
            if (hasLastPosition && resetDistance > 0f &&
                (position - lastPosition).sqrMagnitude > resetDistance * resetDistance)
            {
                solver.RequestReset();
                if (clock != null) clock.Reset();
            }
            lastPosition = position;
            hasLastPosition = true;
            solver.ApplyQuartzHairDrivers();
            solver.CapturePose();
            if (clock != null) clock.Advance(deltaTime);
            else solver.Step(deltaTime);
        }

        private void OnDisable()
        {
            if (solver) solver.RestorePose();
            if (clock != null) clock.Reset();
            hasLastPosition = false;
        }

        private void OnDestroy()
        {
            if (solver) Destroy(solver.gameObject);
        }
    }
}
