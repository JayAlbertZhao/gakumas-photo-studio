using System;
using System.Collections.Generic;
using System.Linq;
using ActorAnimation;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Positional ActorSwing replay for the thigh/calf flesh-helper chains.
    ///
    /// The shipped *LegSkin chains use dynamicType=1. In ProcessDynamicBones
    /// that branch integrates independent self translation and deliberately
    /// skips the length-constrained FromTo rotation used by hair/cloth. The old
    /// substitute converted limb acceleration into a root rotation; that was
    /// the wrong state and the wrong driven transforms. Skin1 remains the
    /// animated anchor while Skin2/Skin3 use child-owned authored parameters.
    /// </summary>
    [DefaultExecutionOrder(910)]
    public sealed class BodySoftTissueDynamicsSystem : MonoBehaviour
    {
        private const float FixedDt = 0.01667f;
        private const float DampFactor = 40f;
        private const float MassScale = 0.01f;
        private const int PrewarmSteps = 30;
        private const int MaxSubsteps = 4;
        // Native dynamicType=1 deliberately compresses the LegSkin helpers by
        // several centimetres from their authored/default positions.  The old
        // 30 mm guard therefore clipped valid thigh/calf deformation.  Keep a
        // much wider numerical guard that can only catch a genuinely detached
        // chain; it is not a presentation clamp.
        private const float CorruptStateGuardMeters = 0.25f;

        [Range(0f, 1.5f)] public float strength = 1f;

        private readonly List<ChainState> _chains = new List<ChainState>();
        private float _accumulator;
        private bool _initialized;
        private bool _dumpStateForDiagnostics;
        private bool _enableSeatedDynamicCorrection;
        private float _manageRootWeight = 1f;
        private float _manageRootHorizontalWeight = 1f;
        private float _manageRootVerticalWeight = 1f;
        private double _sumSquaredDisplacementMm;
        private int _displacementSampleCount;

        public int SimulatedBoneCount { get { return _chains.Count; } }
        public int SimulatedNodeCount { get { return _chains.Count * 2; } }
        public float MeanDisplacementMillimeters { get; private set; }
        public float MaxDisplacementMillimeters { get; private set; }
        public float ObservedRmsDisplacementMillimeters
        {
            get
            {
                return _displacementSampleCount == 0
                    ? 0f
                    : Mathf.Sqrt((float)(_sumSquaredDisplacementMm /
                        _displacementSampleCount));
            }
        }
        public float ObservedPeakDisplacementMillimeters { get; private set; }

        public void SetMotionRuntime(bool enableSeatedDynamicCorrection)
        {
            _enableSeatedDynamicCorrection = enableSeatedDynamicCorrection;
            int weightedEntries = _chains.Count(chain =>
                chain.middleSetting.seatDynamicCorrection > 0.000001f ||
                chain.terminalSetting.seatDynamicCorrection > 0.000001f);
            Debug.Log(string.Format(
                "[ActorSwing] SoftTissue motion contract: seated={0} weightedChains={1} implementation={2}",
                _enableSeatedDynamicCorrection, weightedEntries,
                _enableSeatedDynamicCorrection && weightedEntries > 0
                    ? "pending-exact-cap-constant"
                    : "inactive"));
        }

        public void SetManagerRootWeights(
            float rootWeight,
            float horizontalWeight,
            float verticalWeight)
        {
            _manageRootWeight = Mathf.Clamp01(rootWeight);
            _manageRootHorizontalWeight = Mathf.Clamp01(horizontalWeight);
            _manageRootVerticalWeight = Mathf.Clamp01(verticalWeight);
        }

        public void Initialize(Transform modelRoot, ICollection<Transform> activeBones)
        {
            _chains.Clear();
            _sumSquaredDisplacementMm = 0d;
            _displacementSampleCount = 0;
            ObservedPeakDisplacementMillimeters = 0f;
            _dumpStateForDiagnostics = Environment.GetCommandLineArgs()
                .Contains("--dump-dynamics-state");

            if (Environment.GetCommandLineArgs().Contains("--disable-body-soft-tissue"))
            {
                Debug.Log("[SoftTissue] Disabled by diagnostic command line");
                _initialized = false;
                return;
            }

            ActorSwingDynamicBone[] settings =
                GetComponentsInChildren<ActorSwingDynamicBone>(true)
                    .Where(value => activeBones == null || activeBones.Contains(value.transform))
                    .Where(value => IsSoftTissueBone(value.transform.name))
                    .ToArray();
            Dictionary<string, ActorSwingDynamicBone> byName = settings
                .GroupBy(value => value.transform.name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(),
                    StringComparer.Ordinal);

            foreach (string prefix in new[]
            {
                "LeftUpLegSkin", "RightUpLegSkin",
                "LeftLegSkin", "RightLegSkin",
            })
            {
                ActorSwingDynamicBone root;
                ActorSwingDynamicBone middle;
                ActorSwingDynamicBone terminal;
                if (!byName.TryGetValue(prefix + "1_S", out root) ||
                    !byName.TryGetValue(prefix + "2_S", out middle) ||
                    !byName.TryGetValue(prefix + "3_S_End", out terminal))
                    continue;

                root.enabled = false;
                middle.enabled = false;
                terminal.enabled = false;
                _chains.Add(new ChainState(root, middle, terminal));
            }

            _initialized = _chains.Count > 0;
            ResetSimulation();
            Debug.Log(string.Format(
                "[SoftTissue] Native dynamicType=1 positional replay ready: " +
                "chains={0} simulatedNodes={1} ({2})",
                _chains.Count, SimulatedNodeCount,
                string.Join(", ", _chains.Select(value => value.root.name).ToArray())));
        }

        public void ResetSimulation()
        {
            foreach (ChainState chain in _chains)
            {
                RestoreAuthoredPose(chain);
                CaptureAuthoredPose(chain);
                chain.rootState.Reset(chain.rootAuthoredPosition,
                    chain.rootAuthoredRotation);
                chain.middleState.Reset(chain.middleAuthoredPosition,
                    chain.middleAuthoredRotation);
                chain.terminalState.Reset(chain.terminalAuthoredPosition,
                    chain.terminalAuthoredRotation);
            }

            for (int step = 0; step < PrewarmSteps; step++)
                SimulateStep(FixedDt, true);
            // Native reset/prewarm suppresses history and axis-add during the
            // warm-up loop, then evaluates one ordinary step before presenting.
            SimulateStep(FixedDt, false);
            ApplyRuntimePose();
            _accumulator = 0f;
            CollectMetrics();
        }

        private void LateUpdate()
        {
            if (!_initialized || _chains.Count == 0) return;

            foreach (ChainState chain in _chains)
            {
                RestoreAuthoredPose(chain);
                CaptureAuthoredPose(chain);
                if (!chain.rootState.ready ||
                    Vector3.Distance(chain.rootState.position,
                        chain.rootAuthoredPosition) > 0.5f)
                {
                    chain.rootState.Reset(chain.rootAuthoredPosition,
                        chain.rootAuthoredRotation);
                    chain.middleState.Reset(chain.middleAuthoredPosition,
                        chain.middleAuthoredRotation);
                    chain.terminalState.Reset(chain.terminalAuthoredPosition,
                        chain.terminalAuthoredRotation);
                }

                // ActorSwingUtility.GetRootCorrectionCancelPosition cancels
                // (1 - rootWeight) of hips/root translation before integration.
                // Preserve the remaining rootWeight fraction as inertia instead
                // of letting an animated limb move through world-space helper
                // points.  The segment child owns rootWeight, matching all other
                // ProcessDynamicBones parameters.
                Vector3 attachmentDelta = chain.rootAuthoredPosition -
                    chain.rootState.position;
                Vector3 cancellation = GetRootCorrectionCancelPosition(
                    attachmentDelta, chain.middleSetting.rootWeight);
                chain.middleState.position += cancellation;
                chain.terminalState.position += cancellation;
                chain.lastRootTranslationCancellation = cancellation;
                // Skin1 is an animated/kinematic anchor (parentIndex=-1).
                chain.rootState.position = chain.rootAuthoredPosition;
                chain.rootState.rotation = chain.rootAuthoredRotation;
            }

            if (strength <= 0.001f)
            {
                foreach (ChainState chain in _chains)
                {
                    chain.middleState.Reset(chain.middleAuthoredPosition,
                        chain.middleAuthoredRotation);
                    chain.terminalState.Reset(chain.terminalAuthoredPosition,
                        chain.terminalAuthoredRotation);
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
            float integrationScale = dt * DampFactor * Mathf.Max(0f, strength);
            foreach (ChainState chain in _chains)
            {
                chain.rootState.position = chain.rootAuthoredPosition;
                chain.rootState.rotation = chain.rootAuthoredRotation;

                SimulateEdge(chain.rootState, chain.middleState,
                    chain.middleSetting, chain.middleRestLocalPosition,
                    chain.middleRestLocalRotation, integrationScale, prewarming);
                SimulateEdge(chain.middleState, chain.terminalState,
                    chain.terminalSetting, chain.terminalRestLocalPosition,
                    chain.terminalRestLocalRotation, integrationScale, prewarming);
            }
        }

        private static void SimulateEdge(
            NodeState parent,
            NodeState child,
            ActorSwingDynamicBone setting,
            Vector3 restLocalPosition,
            Quaternion restLocalRotation,
            float integrationScale,
            bool prewarming)
        {
            Vector3 target = parent.position + parent.rotation * restLocalPosition;
            Quaternion targetRotation = parent.rotation * restLocalRotation;
            if (!child.ready || (child.position - target).sqrMagnitude > 0.25f)
                child.Reset(target, targetRotation);

            child.authoredPosition = target;
            child.rotation = targetRotation;
            Vector3 defaultError = target - child.position;
            float damping = Mathf.Clamp01(setting.damping);
            float velocityRetention = (1f - damping) * (1f - damping);
            float stiffness = EffectiveTranslationStiffness(
                setting, defaultError.magnitude);
            // CalcStiffnessPendulum(dynamicType=1) transforms
            // boneAxis * boneLength through the parent's self transform.  The
            // LegSkin middle/end records have a production boneLength of zero,
            // so that point is exactly parent.position.  This second force is
            // intentionally distinct from the authored/default follow force;
            // conflating the two made the helpers converge to zero displacement
            // and removed the flesh deformation entirely.
            Vector3 acceleration = defaultError * velocityRetention +
                (parent.position - child.position) * stiffness;
            acceleration += Vector3.down * Mathf.Max(0f, setting.mass) * MassScale;
            if (!prewarming)
                acceleration += parent.childSpeed * Mathf.Max(0f, setting.spring);
            if (!prewarming)
                acceleration = ApplyAxisAdd(setting, parent.rotation, acceleration);

            Vector3 rawDelta = acceleration * integrationScale;
            Vector3 candidate = child.position + rawDelta;
            Vector3 displacement = candidate - target;
            if (displacement.sqrMagnitude >
                CorruptStateGuardMeters * CorruptStateGuardMeters)
            {
                candidate = target + Vector3.ClampMagnitude(
                    displacement, CorruptStateGuardMeters);
                rawDelta = candidate - child.position;
                child.guardHitCount++;
            }

            parent.childSpeed = rawDelta;
            child.position = candidate;
            child.ready = true;
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

        private static float EffectiveTranslationStiffness(
            ActorSwingDynamicBone setting,
            float distance)
        {
            float stiffness = setting.stiffness;
            if (setting.pendulum <= 0f || setting.pendulumRange <= 0.00001f)
                return stiffness;
            float limited = Mathf.Min(setting.pendulumRange, distance * 10f);
            float reduction = (1f - limited / setting.pendulumRange) *
                setting.pendulum;
            return stiffness - reduction;
        }

        private static Vector3 ApplyAxisAdd(
            ActorSwingDynamicBone setting,
            Quaternion baseWorldRotation,
            Vector3 worldAcceleration)
        {
            if (Mathf.Abs(setting.axisAddXToY) < 0.00001f &&
                Mathf.Abs(setting.axisAddXToZ) < 0.00001f)
                return worldAcceleration;
            Vector3 local = Quaternion.Inverse(baseWorldRotation) * worldAcceleration;
            float x = local.x;
            local.y += Mathf.Sign(local.y == 0f ? x : local.y) * Mathf.Abs(x) *
                setting.axisAddXToY;
            local.z += Mathf.Sign(local.z == 0f ? x : local.z) * Mathf.Abs(x) *
                setting.axisAddXToZ;
            return baseWorldRotation * local;
        }

        private static void RestoreAuthoredPose(ChainState chain)
        {
            chain.root.localPosition = chain.rootRestLocalPosition;
            chain.root.localRotation = chain.rootRestLocalRotation;
            chain.middle.localPosition = chain.middleRestLocalPosition;
            chain.middle.localRotation = chain.middleRestLocalRotation;
            chain.terminal.localPosition = chain.terminalRestLocalPosition;
            chain.terminal.localRotation = chain.terminalRestLocalRotation;
        }

        private static void CaptureAuthoredPose(ChainState chain)
        {
            chain.rootAuthoredPosition = chain.root.position;
            chain.rootAuthoredRotation = chain.root.rotation;
            chain.middleAuthoredPosition = chain.middle.position;
            chain.middleAuthoredRotation = chain.middle.rotation;
            chain.terminalAuthoredPosition = chain.terminal.position;
            chain.terminalAuthoredRotation = chain.terminal.rotation;
        }

        private void ApplyRuntimePose()
        {
            foreach (ChainState chain in _chains)
            {
                // Root remains in the Animator-authored attachment pose.
                chain.middle.position = chain.middleState.position;
                chain.middle.rotation = chain.middleState.rotation;
                chain.terminal.position = chain.terminalState.position;
                chain.terminal.rotation = chain.terminalState.rotation;
            }
        }

        private void CollectMetrics()
        {
            float sum = 0f;
            float maximum = 0f;
            int count = 0;
            foreach (ChainState chain in _chains)
            {
                foreach (NodeState node in new[]
                {
                    chain.middleState, chain.terminalState,
                })
                {
                    float millimeters = Vector3.Distance(
                        node.position, node.authoredPosition) * 1000f;
                    sum += millimeters;
                    maximum = Mathf.Max(maximum, millimeters);
                    _sumSquaredDisplacementMm += millimeters * millimeters;
                    _displacementSampleCount++;
                    ObservedPeakDisplacementMillimeters = Mathf.Max(
                        ObservedPeakDisplacementMillimeters, millimeters);
                    count++;
                }
            }
            MeanDisplacementMillimeters = sum / Mathf.Max(1, count);
            MaxDisplacementMillimeters = maximum;
        }

        public void LogCurrentState(string label)
        {
            if (!_dumpStateForDiagnostics) return;
            foreach (ChainState chain in _chains)
            {
                Debug.Log(string.Format(
                    "[SoftTissueState] label={0} name={1} " +
                    "middleDeltaMm={2} terminalDeltaMm={3} " +
                    "rootSpeed={4} middleSpeed={5} " +
                    "middleResponse=damp:{6:R}/stiff:{7:R}/spring:{8:R} " +
                    "terminalResponse=damp:{9:R}/stiff:{10:R}/spring:{11:R}",
                    label, chain.root.name,
                    ((chain.middleState.position - chain.middleState.authoredPosition) *
                        1000f).ToString("F4"),
                    ((chain.terminalState.position - chain.terminalState.authoredPosition) *
                        1000f).ToString("F4"),
                    chain.rootState.childSpeed.ToString("F6"),
                    chain.middleState.childSpeed.ToString("F6"),
                    chain.middleSetting.damping, chain.middleSetting.stiffness,
                    chain.middleSetting.spring, chain.terminalSetting.damping,
                    chain.terminalSetting.stiffness, chain.terminalSetting.spring));
                Debug.Log(string.Format(
                    "[SoftTissueTransport] label={0} name={1} " +
                    "rootCancellation={2} middleGuardHits={3} terminalGuardHits={4}",
                    label, chain.root.name,
                    chain.lastRootTranslationCancellation.ToString("F6"),
                    chain.middleState.guardHitCount,
                    chain.terminalState.guardHitCount));
            }
            Debug.Log(string.Format(
                "[SoftTissueSummary] label={0} chains={1} nodes={2} " +
                "currentMeanMm={3:0.0000} currentMaxMm={4:0.0000} " +
                "observedRmsMm={5:0.0000} observedPeakMm={6:0.0000} samples={7}",
                label, _chains.Count, SimulatedNodeCount,
                MeanDisplacementMillimeters, MaxDisplacementMillimeters,
                ObservedRmsDisplacementMillimeters,
                ObservedPeakDisplacementMillimeters,
                _displacementSampleCount));
        }

        private static bool IsSoftTissueBone(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                (name.StartsWith("LeftUpLegSkin", StringComparison.Ordinal) ||
                 name.StartsWith("RightUpLegSkin", StringComparison.Ordinal) ||
                 name.StartsWith("LeftLegSkin", StringComparison.Ordinal) ||
                 name.StartsWith("RightLegSkin", StringComparison.Ordinal));
        }

        private sealed class NodeState
        {
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 authoredPosition;
            public Vector3 childSpeed;
            public bool ready;
            public int guardHitCount;

            public void Reset(Vector3 value, Quaternion rotationValue)
            {
                position = value;
                authoredPosition = value;
                rotation = rotationValue;
                childSpeed = Vector3.zero;
                guardHitCount = 0;
                ready = true;
            }
        }

        private sealed class ChainState
        {
            public readonly ActorSwingDynamicBone rootSetting;
            public readonly ActorSwingDynamicBone middleSetting;
            public readonly ActorSwingDynamicBone terminalSetting;
            public readonly Transform root;
            public readonly Transform middle;
            public readonly Transform terminal;
            public readonly Vector3 rootRestLocalPosition;
            public readonly Quaternion rootRestLocalRotation;
            public readonly Vector3 middleRestLocalPosition;
            public readonly Quaternion middleRestLocalRotation;
            public readonly Vector3 terminalRestLocalPosition;
            public readonly Quaternion terminalRestLocalRotation;
            public readonly NodeState rootState = new NodeState();
            public readonly NodeState middleState = new NodeState();
            public readonly NodeState terminalState = new NodeState();
            public Vector3 rootAuthoredPosition;
            public Quaternion rootAuthoredRotation;
            public Vector3 middleAuthoredPosition;
            public Quaternion middleAuthoredRotation;
            public Vector3 terminalAuthoredPosition;
            public Quaternion terminalAuthoredRotation;
            public Vector3 lastRootTranslationCancellation;

            public ChainState(
                ActorSwingDynamicBone rootValue,
                ActorSwingDynamicBone middleValue,
                ActorSwingDynamicBone terminalValue)
            {
                rootSetting = rootValue;
                middleSetting = middleValue;
                terminalSetting = terminalValue;
                root = rootValue.transform;
                middle = middleValue.transform;
                terminal = terminalValue.transform;
                rootRestLocalPosition = root.localPosition;
                rootRestLocalRotation = root.localRotation;
                middleRestLocalPosition = middle.localPosition;
                middleRestLocalRotation = middle.localRotation;
                terminalRestLocalPosition = terminal.localPosition;
                terminalRestLocalRotation = terminal.localRotation;
            }
        }
    }
}
