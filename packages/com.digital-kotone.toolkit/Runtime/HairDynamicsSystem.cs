using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ActorAnimation;
using UnityEngine;

namespace GakumasPhotoMode
{
    // Source-readable port of the recovered ActorAnimation swing data flow.
    // Serialized ActorSwing components remain authoritative.  Code paths which
    // are not yet instruction/trace verified stay explicitly diagnostic.
    [DefaultExecutionOrder(900)]
    public sealed class HairDynamicsSystem : MonoBehaviour
    {
        private enum DynamicSubset
        {
            All,
            GarmentOnly,
            SkirtOnly,
        }

        // Recovered from ActorAnimationSwingJobSkeleton.ProcessDynamicBones.
        private const float FixedDt = 0.01667f;
        private const float DampFactor = 40f;
        // Current-build .rdata 0x82CF0 and ProcessDynamicBones agree on 0.01.
        // The multiplication happens once before the fixed-step loop.
        private const float MassScale = 0.01f;
        // ActorAnimationManageData::.ctor writes 0x1e into propertyData.prewarmCount.
        private const int PrewarmSteps = 30;
        private const int MaxSubsteps = 4;

        [Range(0f, 1.5f)] public float strength = 1f;
        [Range(0f, 2f)] public float windStrength;
        // Explicit opt-in. Null keeps the previous diagnostic wind unchanged.
        public NaturalWindSettings naturalWind;
        public double? naturalWindTimeOverride { get; set; }
        [Range(0f, 2f)] public float gravityStrength = 1f;
        [Range(0f, 1f)] public float collisionStrength = 1f;

        private readonly List<Node> _nodes = new List<Node>();
        private readonly List<QuartzDriverState> _quartzDrivers = new List<QuartzDriverState>();
        private readonly List<QuartzSkirtDriverState> _quartzSkirtDrivers =
            new List<QuartzSkirtDriverState>();
        private readonly List<ColliderState> _staticColliders = new List<ColliderState>();
        private readonly List<ChainLayerState> _chainLayers = new List<ChainLayerState>();
        private readonly Dictionary<Transform, Quaternion> _quartzBaseRotations =
            new Dictionary<Transform, Quaternion>();
        private Transform _head;
        private Transform _chest;
        private Vector3 _lastRootPosition;
        private float _accumulator;
        private bool _initialized;
        private bool _disableHardLimitsForDiagnostics;
        private bool _disableCollisionsForDiagnostics;
        private bool _legacyPostChainLayersForDiagnostics;
        private float _chainSmoothingOverrideForDiagnostics = -1f;
        private bool _dumpStateForDiagnostics;
        private string _systemLabel = "Hair";
        private bool _enableSeatedDynamicCorrection;
        private float _manageRootWeight = 1f;
        private float _manageRootHorizontalWeight = 1f;
        private float _manageRootVerticalWeight = 1f;

        public int SimulatedBoneCount { get { return _nodes.Count(node => node.child != null); } }
        public int DynamicEntryCount { get { return _nodes.Count; } }
        public int TerminalEntryCount { get { return _nodes.Count(node => node.child == null); } }
        public int StaticColliderCount { get { return _staticColliders.Count; } }
        public int QuartzDriverCount
        {
            get { return _quartzDrivers.Count + _quartzSkirtDrivers.Count; }
        }
        public int ChainLayerCount { get { return _chainLayers.Count; } }
        public int ChainCollisionCorrections { get; private set; }
        public int PeakChainCollisionCorrections { get; private set; }
        public int ChainSmoothingApplications { get; private set; }
        public int ChainLoopLengthRestorations { get; private set; }
        public float MaxRestoredChainLoopLengthError { get; private set; }
        public float MeanAngularOffset { get; private set; }
        public float MaxAngularOffset { get; private set; }
        public float BraidMeanAngularOffset { get; private set; }
        public float BraidMaxAngularOffset { get; private set; }
        public float BraidAngularVariation { get; private set; }

        public void SetMotionRuntime(bool enableSeatedDynamicCorrection)
        {
            _enableSeatedDynamicCorrection = enableSeatedDynamicCorrection;
            int weightedEntries = _nodes.Count(node => node.child != null &&
                node.child.setting.seatDynamicCorrection > 0.000001f);
            Debug.Log(string.Format(
                "[ActorSwing] {0} motion contract: seated={1} weightedEntries={2} implementation={3}",
                _systemLabel, _enableSeatedDynamicCorrection, weightedEntries,
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

        public void Initialize(Transform head, Transform chest, Transform sharedColliderRoot = null)
        {
            InitializeInternal(head, chest, sharedColliderRoot, null,
                DynamicSubset.All, "Hair");
        }

        public void InitializeGarment(
            Transform head,
            Transform chest,
            Transform sharedColliderRoot,
            ICollection<Transform> activeBones)
        {
            InitializeInternal(head, chest, sharedColliderRoot, activeBones,
                DynamicSubset.GarmentOnly, "Garment");
        }

        public void InitializeSkirt(
            Transform head,
            Transform chest,
            Transform sharedColliderRoot,
            ICollection<Transform> activeBones)
        {
            InitializeInternal(head, chest, sharedColliderRoot, activeBones,
                DynamicSubset.SkirtOnly, "Skirt");
        }

        private void InitializeInternal(
            Transform head,
            Transform chest,
            Transform sharedColliderRoot,
            ICollection<Transform> activeBones,
            DynamicSubset subset,
            string systemLabel)
        {
            _head = head;
            _chest = chest;
            _systemLabel = systemLabel;
            _nodes.Clear();
            _quartzDrivers.Clear();
            _quartzSkirtDrivers.Clear();
            _staticColliders.Clear();
            _chainLayers.Clear();
            _quartzBaseRotations.Clear();
            string[] commandLine = Environment.GetCommandLineArgs();
            _disableHardLimitsForDiagnostics =
                commandLine.Contains("--hair-disable-hard-limits");
            _disableCollisionsForDiagnostics =
                commandLine.Contains("--hair-disable-collisions");
            _legacyPostChainLayersForDiagnostics =
                commandLine.Contains("--hair-legacy-post-chain-layers");
            for (int index = 0; index + 1 < commandLine.Length; index++)
            {
                if (!string.Equals(commandLine[index],
                        "--hair-chain-smoothing-diagnostic",
                        StringComparison.Ordinal)) continue;
                float parsed;
                if (float.TryParse(commandLine[index + 1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    _chainSmoothingOverrideForDiagnostics = Mathf.Max(0f, parsed);
            }
            _dumpStateForDiagnostics = commandLine.Contains("--dump-dynamics-state");

            ActorAnimationQuartzDriverHairBone[] quartzDrivers =
                GetComponentsInChildren<ActorAnimationQuartzDriverHairBone>(true)
                    .Where(value => activeBones == null || activeBones.Contains(value.transform))
                    .ToArray();
            foreach (ActorAnimationQuartzDriverHairBone driver in quartzDrivers)
            {
                driver.enabled = false;
                if (driver.setting == null) continue;
                _quartzDrivers.Add(new QuartzDriverState(driver, _head));
                Debug.Log(string.Format(
                    "[PhotoMode] Quartz hair {0}: headRot={1} neckRot={2} headRef={3} neckRef={4}",
                    driver.transform.name,
                    driver.setting.headRotateCoefficient,
                    driver.setting.neckRotateCoefficient,
                    driver.setting.referenceHeadBone == null ? "-" : driver.setting.referenceHeadBone.name,
                    driver.setting.referenceNeckBone == null ? "-" : driver.setting.referenceNeckBone.name));
            }

            if (subset == DynamicSubset.SkirtOnly)
            {
                Transform referenceRoot = sharedColliderRoot == null
                    ? transform
                    : sharedColliderRoot;
                Transform[] referenceBones =
                    referenceRoot.GetComponentsInChildren<Transform>(true);
                foreach (ActorAnimationQuartzDriverSkirtBone driver in
                         GetComponentsInChildren<ActorAnimationQuartzDriverSkirtBone>(true))
                {
                    if (activeBones != null && !activeBones.Contains(driver.transform))
                        continue;
                    driver.enabled = false;
                    if (driver.setting == null) continue;
                    string referenceName = driver.transform.name.StartsWith(
                        "Left", StringComparison.Ordinal)
                        ? "LeftUpLeg"
                        : "RightUpLeg";
                    Transform referenceBone = referenceBones.FirstOrDefault(
                        value => value.name == referenceName);
                    if (referenceBone == null) continue;
                    _quartzSkirtDrivers.Add(new QuartzSkirtDriverState(
                        driver, referenceBone));
                    Debug.Log(string.Format(
                        "[PhotoMode] Quartz skirt {0}: reference={1} axis={2} inner={3} outer={4}",
                        driver.transform.name,
                        referenceBone.name,
                        driver.setting.connectionAxis,
                        driver.setting.innerCoefficient,
                        driver.setting.outerCoefficient));
                }
            }

            ActorSwingDynamicBone[] settings = GetComponentsInChildren<ActorSwingDynamicBone>(true)
                .Where(value => activeBones == null || activeBones.Contains(value.transform))
                .Where(value => MatchesSubset(value.transform.name, subset))
                .ToArray();
            Dictionary<Transform, Node> nodesByTransform = new Dictionary<Transform, Node>();
            foreach (ActorSwingDynamicBone setting in settings)
            {
                setting.enabled = false;
                Node node = new Node(setting, HierarchyDepth(setting.transform));
                _nodes.Add(node);
                nodesByTransform[setting.transform] = node;
            }
            _nodes.Sort((left, right) => left.depth.CompareTo(right.depth));
            foreach (Node node in _nodes)
            {
                node.parent = FindDynamicAncestor(node.bone.parent, nodesByTransform);
                node.child = FindDynamicChild(node.bone, nodesByTransform);
                SwingReferenceLimitInfo reference = node.setting.referenceLimitInfo;
                if (reference != null && reference.bone != null)
                    nodesByTransform.TryGetValue(reference.bone, out node.referenceNode);
            }

            // The production CampusActorAnimationRigData aggregates swing components
            // from every assembled model part. Hair dynamic colliders therefore hit
            // the body-owned static colliders; they are not expected to find copies
            // inside the standalone hair prefab. The former head/chest fallback was
            // consequently active for all hair and pushed roots out of an oversized
            // synthetic sphere, which is the source of the visibly upright locks.
            ActorSwingStaticBone[] staticBones = sharedColliderRoot == null
                ? GetComponentsInChildren<ActorSwingStaticBone>(true)
                : sharedColliderRoot.GetComponentsInChildren<ActorSwingStaticBone>(true);
            foreach (ActorSwingStaticBone staticBone in staticBones)
            {
                staticBone.enabled = false;
                if (staticBone.staticCollider == null || staticBone.staticCollider.type == 4) continue;
                _staticColliders.Add(new ColliderState(staticBone.transform, staticBone.staticCollider));
            }

            // ActorSwingBreastBone implements IActorSwingBone and contributes
            // its breastCollider to the same production static-bone list.  It
            // is not represented by a separate ActorSwingStaticBone component,
            // so omitting it leaves a pair of 50 mm torso collision volumes out
            // of hair/garment collision despite the serialized mask being -1.
            ActorSwingBreastBone[] breastColliders = sharedColliderRoot == null
                ? GetComponentsInChildren<ActorSwingBreastBone>(true)
                : sharedColliderRoot.GetComponentsInChildren<ActorSwingBreastBone>(true);
            foreach (ActorSwingBreastBone breastBone in breastColliders)
            {
                if (breastBone.breastCollider == null ||
                    breastBone.breastCollider.type == 4) continue;
                _staticColliders.Add(new ColliderState(
                    breastBone.transform, breastBone.breastCollider));
            }

            BuildChainLayers(subset);

            _lastRootPosition = transform.position;
            _accumulator = 0f;
            _initialized = true;
            ResetSimulation();
            if (Environment.GetCommandLineArgs().Contains("--dump-dynamics-settings"))
                LogNodeSettings();
            int braidCount = _nodes.Count(node => node.isBraid);
            Debug.Log(string.Format(
                "[PhotoMode] Recovered ActorSwing {0} data flow ready: entries={1}, edges={2}, terminals={3}, braidEntries={4}, QuartzDrivers={5}, staticColliders={6}, chainLayers={7}, chainSchedule={8}, chainDepths={9}, chainProfiles={10}",
                _systemLabel,
                DynamicEntryCount,
                SimulatedBoneCount,
                TerminalEntryCount,
                braidCount,
                QuartzDriverCount,
                _staticColliders.Count,
                _chainLayers.Count,
                _legacyPostChainLayersForDiagnostics ? "legacy-post" : "native-interleaved",
                _chainLayers.Count == 0
                    ? "-"
                    : string.Join(",", _chainLayers
                        .GroupBy(value => value.depth)
                        .Select(group => string.Format("{0}:{1}", group.Key, group.Count()))
                        .ToArray()),
                _chainLayers.Count == 0
                    ? "-"
                    : string.Join(";", _chainLayers.Select(value => string.Format(
                        CultureInfo.InvariantCulture,
                        "d{0}/n{1}/around{2}/smooth{3:R}/loop{4:R}",
                        value.depth, value.points.Count, value.around ? 1 : 0,
                        value.smoothing, value.initialLoopLength)).ToArray())));
        }

        private static bool MatchesSubset(string boneName, DynamicSubset subset)
        {
            if (subset == DynamicSubset.All) return true;
            bool skirt = boneName.IndexOf(
                "Skirt", StringComparison.OrdinalIgnoreCase) >= 0;
            if (subset == DynamicSubset.SkirtOnly) return skirt;
            bool softTissue = IsBodySoftTissueBone(boneName);
            return !softTissue && !skirt;
        }

        private static bool IsBodySoftTissueBone(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return false;
            string part = boneName;
            if (part.StartsWith("Left", StringComparison.Ordinal)) part = part.Substring(4);
            else if (part.StartsWith("Right", StringComparison.Ordinal)) part = part.Substring(5);
            return part.StartsWith("UpLegSkin", StringComparison.Ordinal) ||
                part.StartsWith("LegSkin", StringComparison.Ordinal) ||
                part.StartsWith("HipSkin", StringComparison.Ordinal);
        }

        private void BuildChainLayers(DynamicSubset subset)
        {
            // ActorSwingChain layers reference the dynamic CHILD at a given
            // depth.  The controller which receives the collision-adjusted
            // rotation is therefore that child's dynamic parent, matching
            // ProcessChainBoneCollider -> CheckChainCollision.
            Dictionary<Transform, Node> controllerByPoint = _nodes
                .Where(value => value.child != null)
                .GroupBy(value => value.child.bone)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (ActorSwingChain chain in
                     GetComponentsInChildren<ActorSwingChain>(true))
            {
                if (chain == null || chain.chains == null ||
                    chain.chains.layers == null) continue;
                foreach (SwingChainLayer layer in chain.chains.layers)
                {
                    if (layer == null || !layer.active || layer.bones == null ||
                        layer.bones.Length < 2) continue;
                    List<ChainPointState> points = new List<ChainPointState>();
                    bool accepted = true;
                    foreach (ActorSwingDynamicBone setting in layer.bones)
                    {
                        Node controller;
                        if (setting == null ||
                            !MatchesSubset(setting.transform.name, subset) ||
                            !controllerByPoint.TryGetValue(setting.transform,
                                out controller))
                        {
                            accepted = false;
                            break;
                        }
                        points.Add(new ChainPointState(setting, controller));
                    }
                    if (!accepted || points.Count < 2) continue;
                    float smoothing = _chainSmoothingOverrideForDiagnostics >= 0f
                        ? _chainSmoothingOverrideForDiagnostics
                        : layer.smoothing;
                    ChainLayerState state = new ChainLayerState(
                        points, layer.radius, smoothing, layer.around);
                    _chainLayers.Add(state);
                }
            }
            _chainLayers.Sort((left, right) => left.depth.CompareTo(right.depth));
        }

        private void LogNodeSettings()
        {
            foreach (Node node in _nodes)
            {
                if (node.child == null) continue;
                // A segment rotates `node`, but the serialized parameters live on
                // its dynamic child. ProcessDynamicBoneLayer calls
                // CalcDynamicSwing(pDynamic = child) and writes the returned swing
                // into the parent affine. Log the same owner that the solver uses.
                ActorSwingDynamicBone segmentSetting = node.child.setting;
                SwingLimitInfo limit = segmentSetting.limitInfo;
                SwingReferenceLimitInfo reference = segmentSetting.referenceLimitInfo;
                Vector3 restWorldDirection = node.bone.rotation * node.child.boneAxis;
                Debug.Log(string.Format(
                    "[HairSetting] name={0} parent={1} child={2} depth={3} axis={4} worldAxis={5} " +
                    "type={6} damp={7:R} stiff={8:R} spring={9:R} pend={10:R} range={11:R} " +
                    "mass={12:R} axisXY={13:R} axisXZ={14:R} wind={15:R} rootWeight={16:R} " +
                    "limit={17}:{18}/{19}/{20} reference={21}:{22}/{23} " +
                    "modelingLocal={24}/{25} restLocal={26}/{27} restWorld={28} " +
                    "collider={29}",
                    node.bone.name,
                    node.bone.parent == null ? "-" : node.bone.parent.name,
                    node.child.bone.name,
                    node.depth,
                    node.child.boneAxis,
                    restWorldDirection,
                    segmentSetting.dynamicType,
                    segmentSetting.damping,
                    segmentSetting.stiffness,
                    segmentSetting.spring,
                    segmentSetting.pendulum,
                    segmentSetting.pendulumRange,
                    segmentSetting.mass,
                    segmentSetting.axisAddXToY,
                    segmentSetting.axisAddXToZ,
                    segmentSetting.wind,
                    segmentSetting.rootWeight,
                    limit == null ? 0 : limit.useLimit,
                    limit == null ? Vector2Int.zero : limit.axisX,
                    limit == null ? Vector2Int.zero : limit.axisY,
                    limit == null ? Vector2Int.zero : limit.axisZ,
                    reference == null || reference.bone == null ? "-" : reference.bone.name,
                    reference == null ? "---" : Bool3Text(reference.min),
                    reference == null ? "---" : Bool3Text(reference.max),
                    segmentSetting.modelingTransform.localPosition,
                    segmentSetting.modelingTransform.localRotation.ToQuaternion(),
                    node.restLocalPosition,
                    node.restLocalRotation,
                    node.authoredRotation,
                    ColliderText(segmentSetting.dynamicCollider)));
            }
            foreach (ColliderState state in _staticColliders)
                Debug.Log(string.Format("[HairStaticCollider] bone={0} world={1} setting={2}",
                    state.transform.name,
                    state.transform.position,
                    ColliderText(state.collider)));
        }

        private static string ColliderText(SwingCollider collider)
        {
            if (collider == null) return "null";
            return string.Format("type={0},mask={1},a={2},b={3},fa={4:R},fb={5:R}",
                collider.type,
                collider.collisionMask,
                collider.vector3_A,
                collider.vector3_B,
                collider.float_A,
                collider.float_B);
        }

        private static string Bool3Text(SerializableBool3 value)
        {
            return string.Concat(value.x ? "1" : "0", value.y ? "1" : "0", value.z ? "1" : "0");
        }

        public void ResetSimulation()
        {
            if (!_initialized) return;
            ChainCollisionCorrections = 0;
            PeakChainCollisionCorrections = 0;
            ChainSmoothingApplications = 0;
            ChainLoopLengthRestorations = 0;
            MaxRestoredChainLoopLengthError = 0f;
            ApplyQuartzDrivers();
            ResetNodesToBasePose();
            CaptureAuthoredPose();
            foreach (Node node in _nodes) ResetNode(node);

            // The native prewarm loop integrates positions before
            // ProcessDynamicBoneLayer calls CalcDynamicSwing.  Hard/reference
            // limits therefore run once after prewarm, not once per warm-up step.
            // It also zeros the history spring and axis-add terms while prewarming.
            // Applying the angular clamp inside every warm-up step feeds the
            // clamped pose back into the braid 24 times and creates the persistent
            // sideways "telekinetic" equilibrium seen in the old substitute.
            for (int step = 0; step < PrewarmSteps; step++)
            {
                SimulateStep(FixedDt, false, false, true);
            }
            SimulateStep(FixedDt, false, true, false);
            ApplyRuntimePose();
            CollectMetrics();
            _accumulator = 0f;
        }

        private void LateUpdate()
        {
            if (!_initialized || _nodes.Count == 0) return;
            if ((transform.position - _lastRootPosition).sqrMagnitude > 0.20f)
            {
                _lastRootPosition = transform.position;
                ResetSimulation();
                return;
            }
            _lastRootPosition = transform.position;

            if (strength <= 0.001f)
            {
                ApplyQuartzDrivers();
                ResetNodesToBasePose();
                CaptureAuthoredPose();
                foreach (Node node in _nodes) ResetNode(node);
                CollectMetrics();
                return;
            }

            ApplyQuartzDrivers();
            ResetNodesToBasePose();
            CaptureAuthoredPose();
            _accumulator = Mathf.Min(_accumulator + Mathf.Min(Time.deltaTime, 0.10f),
                FixedDt * MaxSubsteps);
            int steps = 0;
            while (_accumulator >= FixedDt && steps < MaxSubsteps)
            {
                SimulateStep(FixedDt, true, true, false);
                _accumulator -= FixedDt;
                steps++;
            }

            if (steps == 0) ApplyRuntimePose();
            CollectMetrics();
        }

        private void SimulateStep(
            float dt,
            bool allowWind,
            bool applyAuthoredLimits,
            bool prewarming)
        {
            ChainSmoothingApplications = 0;
            ChainLoopLengthRestorations = 0;
            MaxRestoredChainLoopLengthError = 0f;
            float integrationScale = dt * DampFactor * Mathf.Max(0f, strength);
            float time = Time.time;
            Vector3 naturalForce = allowWind && !prewarming && naturalWind != null
                ? naturalWind.Sample(naturalWindTimeOverride ?? Time.timeAsDouble)
                : Vector3.zero;
            bool useAnimatedAttachmentFrame =
                string.Equals(_systemLabel, "Garment", StringComparison.Ordinal) ||
                string.Equals(_systemLabel, "Skirt", StringComparison.Ordinal);

            // Root entries are kinematic anchors.  Every dynamic segment uses the
            // CHILD entry's parameters; this is the key ownership rule visible in
            // ProcessDynamicBones (lVar12/childIndex), and differs from the former
            // per-component Verlet substitute.
            foreach (Node node in _nodes)
            {
                if (!node.ready) ResetNode(node);
                if (node.parent == null)
                {
                    if (useAnimatedAttachmentFrame && node.child != null)
                    {
                        // ActorSwingUtility.GetRootCorrectionCancelPosition
                        // cancels most hips/root translation before a dynamic
                        // garment chain is integrated:
                        //   cancel = (1 - manageWeight * axisWeight * boneWeight)
                        //            * hipsTranslation
                        // The standalone player has no manager override yet, so
                        // its neutral weights are 1.  The serialized child entry
                        // owns the segment's rootWeight, just like its other
                        // integration settings.  Advecting the simulated chain
                        // by that cancelled attachment delta keeps a loose coat
                        // attached to the moving torso while preserving the
                        // authored rootWeight fraction as inertia.  Without this
                        // step, the animated torso moves through a world-space
                        // coat and the front panels settle near 90 degrees—the
                        // apparent air-filled jacket seen in pass 223.
                        Vector3 attachmentDelta = node.authoredPosition - node.position;
                        Vector3 cancellation = GetRootCorrectionCancelPosition(
                            attachmentDelta, node.child.setting.rootWeight);
                        TranslateDynamicChain(node.child, cancellation);
                        node.lastRootTranslationCancellation = cancellation;
                    }
                    // A root dynamic entry follows the animated attachment in
                    // translation, but its self rotation is persistent solver
                    // state.  Native pass202 frame N after_self_rotation is bit
                    // identical to frame N+1 before_self_rotation.  Resetting it
                    // to the authored rotation every fixed step makes the child
                    // default tip sit roughly 12 mm below its current tip; the
                    // (1-damping)^2 term then becomes a large artificial bind-pose
                    // force.  Keep self rotation and update only the independent
                    // authored/default frame used by CalcDynamicSwing.
                    node.position = node.authoredPosition;
                    node.defaultRotation = node.authoredRotation;
                    node.defaultWorldRotation = node.authoredRotation;
                }
            }

            int chainCorrections = 0;
            int activeDynamicDepth = int.MinValue;
            foreach (Node parent in _nodes)
            {
                Node child = parent.child;
                if (child == null || !parent.ready || !child.ready) continue;
                // ProcessDynamicBoneLayer invokes ProcessChainBone(depth + 1)
                // before integrating each contiguous dynamic-depth group.  Chain
                // collision therefore consumes the previous-step spring positions,
                // then the ordinary layer below it propagates that correction.  The
                // former standalone path ran every chain after every dynamic layer,
                // reversing this dependency and letting wide sleeves/coat panels
                // retain an inflated equilibrium.
                // The native group depth belongs to the current/controller job
                // entry.  The child entry still owns the segment parameters used
                // by this source-readable port, but using the child's hierarchy
                // depth here skips the shallowest chain layer (and advances every
                // following layer one group too early).
                int dynamicDepth = parent.depth;
                if (!_legacyPostChainLayersForDiagnostics &&
                    dynamicDepth != activeDynamicDepth)
                {
                    chainCorrections += ApplyChainLayersAtDepth(dynamicDepth + 1);
                    activeDynamicDepth = dynamicDepth;
                }
                Vector3 origin = parent.position;
                // ProcessDynamicBones and CalcDynamicSwing use two different
                // rotation frames.  The integration target is propagated from the
                // parent's current simulated selfTx.  The final FromTo/limit frame
                // is the parent's defaultRotation: for a root this is the authored
                // world rotation, while for deeper bones it was propagated by the
                // preceding edge as parentSelfRotation * childLocalRotation.
                // pass202 proves that relation for every non-terminal braid link.
                // Using the hierarchy's static authored world rotation here drops
                // the upstream swing from the limit basis and clamps a healthy
                // downward candidate into a sideways pose.
                Quaternion simulationWorldRotation = parent.rotation;
                Quaternion defaultWorldRotation = parent.defaultRotation;
                // Garment roots are attached directly to animated arm/torso
                // helpers.  Their free swing rotation is persistent solver
                // state, but the force target must stay in the newly animated
                // attachment frame.  Using the persistent self rotation as its
                // own target makes a sleeve keep its old world direction when
                // the arm moves; there is then literally no restoring error and
                // a bind-pose horizontal panel survives into an arms-down pose.
                // Hair keeps the pass202-verified propagation path.  Garment
                // chains use the authored/default frame at the root, then the
                // solved upstream rotation is propagated to deeper links below.
                Quaternion targetWorldRotation = useAnimatedAttachmentFrame
                    ? defaultWorldRotation
                    : simulationWorldRotation;
                Vector3 authoredTip = origin +
                    targetWorldRotation * child.restLocalPosition;
                Quaternion authoredChildRotation =
                    targetWorldRotation * child.restLocalRotation;
                float length = child.restLocalPosition.magnitude;
                if (length < 0.0001f) continue;
                if ((child.position - authoredTip).sqrMagnitude > 0.25f)
                {
                    child.position = authoredTip;
                    child.rotation = authoredChildRotation;
                    parent.childSpeed = Vector3.zero;
                }

                parent.defaultWorldRotation = defaultWorldRotation;
                child.defaultPosition = authoredTip;
                child.defaultRotation = authoredChildRotation;
                Vector3 authoredDirection = SafeDirection(
                    simulationWorldRotation * child.boneAxis,
                    authoredTip - origin);
                Vector3 currentDirection = SafeDirection(
                    child.position - origin,
                    authoredDirection);

                float damping = Mathf.Clamp01(child.setting.damping);
                float velocityRetention = (1f - damping) * (1f - damping);
                Vector3 poseFollowing = (authoredTip - child.position) * velocityRetention;
                float stiffness = CalcEffectiveStiffness(
                    child,
                    authoredDirection,
                    currentDirection,
                    authoredTip,
                    origin);
                Vector3 stiffnessDelta = child.setting.dynamicType == 0
                    ? authoredDirection * stiffness
                    : (authoredTip - child.position) * stiffness;
                Vector3 massDelta = Vector3.down * Mathf.Max(0f, child.setting.mass) *
                    MassScale * gravityStrength;
                Vector3 acceleration = poseFollowing + stiffnessDelta + massDelta +
                    parent.childSpeed * (prewarming
                        ? 0f
                        : Mathf.Max(0f, child.setting.spring));

                // The production force comes from ActorAnimationManagePropertyData.
                // A non-zero UI value is retained only as an explicit diagnostic
                // source until that manager is connected to the standalone player.
                if (allowWind && windStrength > 0.0001f && child.setting.wind != 0f)
                {
                    float phase = child.phase + time * (1.15f + child.depth * 0.008f);
                    Vector3 wind = new Vector3(
                        Mathf.Sin(phase),
                        0.35f * Mathf.Sin(phase * 0.71f),
                        Mathf.Cos(phase * 0.83f));
                    acceleration += wind * child.setting.wind * windStrength;
                }
                // Child wind coefficients remain authoritative for hair, jacket
                // and skirt. Do not add environmental wind to anatomy helpers.
                if (!naturalForce.Equals(Vector3.zero) && child.setting.useWindGlobalForce && child.setting.wind != 0f)
                    acceleration += naturalForce * child.setting.wind;

                if (!prewarming)
                    acceleration = ApplyAxisAdd(
                        parent, child, simulationWorldRotation, acceleration);
                Vector3 rawDelta = acceleration * integrationScale;
                Vector3 candidate = child.position + rawDelta;
                candidate = ConstrainLength(origin, candidate, authoredDirection, length);
                candidate = ResolveCollision(child, origin, candidate, length);
                candidate = ConstrainLength(origin, candidate, authoredDirection, length);
                if (applyAuthoredLimits && !_disableHardLimitsForDiagnostics)
                {
                    Vector3 defaultDirection = SafeDirection(
                        defaultWorldRotation * child.boneAxis,
                        authoredDirection);
                    candidate = ApplyAuthoredHardLimit(parent, child, child.boneAxis, origin,
                        defaultWorldRotation, defaultDirection, candidate, length);
                }

                parent.childSpeed = rawDelta;
                Vector3 solvedDefaultDirection = SafeDirection(
                    defaultWorldRotation * child.boneAxis,
                    authoredDirection);
                ApplySegmentRotation(parent, child, defaultWorldRotation,
                    solvedDefaultDirection, candidate);
                if (useAnimatedAttachmentFrame)
                {
                    // A downstream garment link inherits the already-solved
                    // upstream swing.  Only the chain root is targeted directly
                    // by the animated anatomy helper.
                    child.defaultRotation = child.rotation;
                    child.defaultWorldRotation = child.rotation;
                }
            }
            if (_legacyPostChainLayersForDiagnostics)
                chainCorrections += ApplyAllChainLayers();
            ChainCollisionCorrections = chainCorrections;
            PeakChainCollisionCorrections = Mathf.Max(
                PeakChainCollisionCorrections, ChainCollisionCorrections);
            ApplyRuntimePose();
        }

        private static void TranslateDynamicChain(Node node, Vector3 translation)
        {
            if (translation.sqrMagnitude <= 0.0000000001f) return;
            while (node != null)
            {
                node.position += translation;
                node.defaultPosition += translation;
                node = node.child;
            }
        }

        private Vector3 GetRootCorrectionCancelPosition(
            Vector3 hipsTranslation,
            float boneRootWeight)
        {
            // Exact current-build ActorSwingUtility formula. The serialized
            // dynamic record exposes one rootWeight, passed for both the bone
            // horizontal and vertical terms by ProcessDynamicBones.
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

        private float CalcEffectiveStiffness(
            Node child,
            Vector3 authoredDirection,
            Vector3 currentDirection,
            Vector3 authoredTip,
            Vector3 origin)
        {
            float stiffness = child.setting.stiffness;
            float pendulum = child.setting.pendulum;
            float range = child.setting.pendulumRange;
            if (pendulum <= 0f || range <= 0.00001f) return stiffness;

            float reduction;
            if (child.setting.dynamicType == 0)
            {
                float alignment = Mathf.Abs(Vector3.Dot(authoredDirection, currentDirection));
                float active = Mathf.Max(0f, alignment - (1f - range));
                reduction = active / range * pendulum;
            }
            else
            {
                float distance = Mathf.Min(range,
                    Vector3.Distance(child.position, authoredTip) * 10f);
                reduction = (1f - distance / range) * pendulum;
            }
            return stiffness - reduction;
        }

        private static Vector3 ApplyAxisAdd(
            Node parent,
            Node child,
            Quaternion baseWorldRotation,
            Vector3 worldAcceleration)
        {
            if (child.setting.dynamicType != 1 ||
                (Mathf.Abs(child.setting.axisAddXToY) < 0.00001f &&
                 Mathf.Abs(child.setting.axisAddXToZ) < 0.00001f))
                return worldAcceleration;

            Vector3 local = Quaternion.Inverse(baseWorldRotation) * worldAcceleration;
            float x = local.x;
            local.y += Mathf.Sign(local.y == 0f ? x : local.y) * Mathf.Abs(x) *
                child.setting.axisAddXToY;
            local.z += Mathf.Sign(local.z == 0f ? x : local.z) * Mathf.Abs(x) *
                child.setting.axisAddXToZ;
            return baseWorldRotation * local;
        }

        private void ApplyRuntimePose()
        {
            ResetNodesToBasePose();
            foreach (Node node in _nodes)
            {
                if (!node.ready) continue;
                // A rendered frame may contain no fixed simulation step. Keep
                // the held chain attached to the current animated root in that
                // frame, without advancing its solver positions or velocities.
                // One translation for the entire chain preserves segment lengths
                // and persistent world rotations; moving only its root stretches
                // the first segment. After a step this offset is exactly zero.
                Node root = node;
                while (root.parent != null) root = root.parent;
                Vector3 presentationOffset = root.authoredPosition - root.position;
                node.bone.position = node.position + presentationOffset;
                node.bone.rotation = node.rotation;
            }
        }

        private void ApplySegmentRotation(
            Node parent,
            Node child,
            Quaternion baseWorldRotation,
            Vector3 authoredDirection,
            Vector3 candidate)
        {
            Vector3 desiredDirection = SafeDirection(candidate - parent.position,
                authoredDirection);
            Quaternion solvedRotation =
                Quaternion.FromToRotation(authoredDirection, desiredDirection) *
                baseWorldRotation;
            // swingPowerWeight already scales the integration delta.  The native
            // CalcDynamicSwing result is written at full modeling-pose weight in
            // this runtime path; blending it by strength a second time creates a
            // spurious return-to-default term.
            parent.rotation = solvedRotation;
            child.position = parent.position + parent.rotation * child.restLocalPosition;
            child.rotation = parent.rotation * child.restLocalRotation;
            child.ready = true;
        }

        private Vector3 ApplyAuthoredHardLimit(
            Node poseNode,
            Node settingNode,
            Vector3 localAxis,
            Vector3 origin,
            Quaternion baseWorldRotation,
            Vector3 authoredDirection,
            Vector3 candidate,
            float length)
        {
            // In the native array an entry describes the segment from its
            // dynamic parent to itself. CalcDynamicSwing therefore reads the
            // child entry's limit at offsets 0x344..0x375, then
            // ProcessDynamicBoneLayer writes the resulting rotation into the
            // parent affine. Keeping those two roles separate is essential for
            // sleeve roots: Sleeve1 has no limit, while Sleeve2 owns the narrow
            // +/-15/5/5 degree limit that constrains Sleeve1's panel.
            SwingLimitInfo limit = settingNode.setting.limitInfo;
            if (limit == null || limit.useLimit == 0) return candidate;

            Vector3 desired = SafeDirection(candidate - origin, authoredDirection);
            poseNode.lastPreLimitDirection = desired;
            // The native job's confusingly named parentTx is this dynamic bone's
            // authored/default world frame, assembled before swing processing. Thus
            // inverse(parentTx.rotation) * candidateWorldRotation is the conjugated
            // world delta below, not a Transform-parent-local absolute rotation.
            Quaternion worldDelta = Quaternion.FromToRotation(authoredDirection, desired);
            Quaternion localDelta = Quaternion.Inverse(baseWorldRotation) *
                worldDelta * baseWorldRotation;
            Vector3 euler = SignedEuler(localDelta);
            poseNode.lastLimitEuler = euler;
            euler.x = ClampAuthoredAngle(euler.x, limit.axisX);
            euler.y = ClampAuthoredAngle(euler.y, limit.axisY);
            euler.z = ClampAuthoredAngle(euler.z, limit.axisZ);
            poseNode.lastClampedLimitEuler = euler;
            Quaternion limitedWorldRotation = baseWorldRotation * Quaternion.Euler(euler);

            // CalcDynamicSwing applies the optional reference clamp after the
            // ordinary local hard limit.  It compares signed ZXY world Euler
            // components against the referenced dynamic bone's self rotation;
            // max flags run first, then min flags.  The six serialized booleans
            // at 0x370..0x375 therefore are live constraints, not metadata.
            SwingReferenceLimitInfo reference = settingNode.setting.referenceLimitInfo;
            if (reference != null && settingNode.referenceNode != null)
            {
                Vector3 candidateEuler = SignedEuler(limitedWorldRotation);
                Vector3 referenceEuler = SignedEuler(settingNode.referenceNode.rotation);
                if (reference.max.x) candidateEuler.x = Mathf.Min(candidateEuler.x, referenceEuler.x);
                if (reference.max.y) candidateEuler.y = Mathf.Min(candidateEuler.y, referenceEuler.y);
                if (reference.max.z) candidateEuler.z = Mathf.Min(candidateEuler.z, referenceEuler.z);
                if (reference.min.x) candidateEuler.x = Mathf.Max(candidateEuler.x, referenceEuler.x);
                if (reference.min.y) candidateEuler.y = Mathf.Max(candidateEuler.y, referenceEuler.y);
                if (reference.min.z) candidateEuler.z = Mathf.Max(candidateEuler.z, referenceEuler.z);
                limitedWorldRotation = Quaternion.Euler(candidateEuler);
            }

            Vector3 limited = limitedWorldRotation * localAxis;
            limited = SafeDirection(limited, authoredDirection);
            poseNode.lastLimitedDirection = limited;
            return origin + limited * length;
        }

        private static float ClampAuthoredAngle(float value, Vector2Int interval)
        {
            float minimum = Mathf.Min(interval.x, interval.y);
            float maximum = Mathf.Max(interval.x, interval.y);
            return Mathf.Clamp(value, minimum, maximum);
        }

        private int ApplyChainLayersAtDepth(int depth)
        {
            if (_chainLayers.Count == 0 || _disableCollisionsForDiagnostics ||
                collisionStrength <= 0.001f) return 0;

            int corrections = 0;
            foreach (ChainLayerState layer in _chainLayers)
            {
                if (layer.depth != depth) continue;
                corrections += ApplyChainLayer(layer);
            }
            return corrections;
        }

        private int ApplyAllChainLayers()
        {
            if (_chainLayers.Count == 0 || _disableCollisionsForDiagnostics ||
                collisionStrength <= 0.001f) return 0;
            int corrections = 0;
            foreach (ChainLayerState layer in _chainLayers)
                corrections += ApplyChainLayer(layer);
            return corrections;
        }

        private int ApplyChainLayer(ChainLayerState layer)
        {
            int corrections = ApplyChainCollisions(layer);
            // ProcessChainBone runs collider -> smoothing -> collider.  A second
            // collider pass is made only when smoothing actually handled the
            // layer.  All currently loaded solo1 layers serialize smoothing=0,
            // so this branch is dormant there rather than changing their shape.
            if (ApplyChainSmoothing(layer))
                corrections += ApplyChainCollisions(layer);
            return corrections;
        }

        private int ApplyChainCollisions(ChainLayerState layer)
        {
            int pointCount = layer.points.Count;
            int edgeCount = layer.around ? pointCount : pointCount - 1;
            if (edgeCount <= 0) return 0;

            int corrections = 0;
            // CheckChainCollision walks static colliders and chain edges in array
            // order.  A hit translates both spring endpoints by the component of
            // the rejection perpendicular to the chain edge, then calls
            // UpdateSpringPosition independently for both bones.  Those writes
            // are immediately visible to the next collider/edge.  Accumulating
            // barycentric endpoint offsets (the old path) was neither the native
            // order nor the native geometry.
            for (int edge = 0; edge < edgeCount; edge++)
            {
                int nextIndex = (edge + 1) % pointCount;
                ChainPointState point = layer.points[edge];
                ChainPointState next = layer.points[nextIndex];
                int mask = point.collisionMask | next.collisionMask;
                foreach (ColliderState collider in _staticColliders)
                {
                    Node pointChild = point.node.child;
                    Node nextChild = next.node.child;
                    if (pointChild == null || nextChild == null) continue;
                    Vector3 start = pointChild.position;
                    Vector3 end = nextChild.position;
                    if (mask != -1 && collider.collider.collisionMask != -1 &&
                        (mask & collider.collider.collisionMask) == 0) continue;
                    float unusedEdgeT;
                    Vector3 correction;
                    if (!collider.TryResolveChainCollision(
                            start, end, layer.radius, collisionStrength,
                            out unusedEdgeT, out correction))
                        continue;
                    Vector3 edgeDirection = end - start;
                    if (edgeDirection.sqrMagnitude > 0.00000001f)
                    {
                        edgeDirection.Normalize();
                        correction -= edgeDirection *
                            Vector3.Dot(correction, edgeDirection);
                    }
                    if (correction.sqrMagnitude <= 0.0000000001f) continue;
                    ApplyChainPointTranslation(point, correction);
                    ApplyChainPointTranslation(next, correction);
                    corrections++;
                }
            }
            return corrections;
        }

        private bool ApplyChainSmoothing(ChainLayerState layer)
        {
            // The recovered ProcessChainBoneSmoothing early-outs for open layers
            // and for zero smoothing.  It applies one simultaneous cyclic
            // Laplacian step, then restores the authored initial loop length only
            // when smoothing shortened the loop.
            if (!layer.around || layer.smoothing <= 0.000001f ||
                layer.points.Count < 3) return false;

            int count = layer.points.Count;
            Vector3[] source = new Vector3[count];
            Vector3[] smoothed = new Vector3[count];
            for (int index = 0; index < count; index++)
            {
                Node child = layer.points[index].node.child;
                if (child == null) return false;
                source[index] = child.position;
            }

            float smoothing = layer.smoothing;
            for (int index = 0; index < count; index++)
            {
                Vector3 previous = source[(index + count - 1) % count];
                Vector3 current = source[index];
                Vector3 next = source[(index + 1) % count];
                smoothed[index] = current + smoothing *
                    ((previous - current) + (next - current)) * 0.5f;
            }

            float smoothedLoopLength = ClosedLoopLength(smoothed);
            if (smoothedLoopLength > 0.000001f &&
                smoothedLoopLength < layer.initialLoopLength)
            {
                Vector3 centroid = Vector3.zero;
                foreach (Vector3 position in smoothed) centroid += position;
                centroid /= count;
                float scale = layer.initialLoopLength / smoothedLoopLength;
                for (int index = 0; index < count; index++)
                    smoothed[index] = centroid +
                        (smoothed[index] - centroid) * scale;
                ChainLoopLengthRestorations++;
                MaxRestoredChainLoopLengthError = Mathf.Max(
                    MaxRestoredChainLoopLengthError,
                    Mathf.Abs(ClosedLoopLength(smoothed) -
                        layer.initialLoopLength));
            }

            // The native routine writes only selfTx.position (0xE4..0xEC),
            // preserving each affine rotation.  Do the same here; the following
            // dynamic layer consumes these positions and derives its rotations.
            for (int index = 0; index < count; index++)
                layer.points[index].node.child.position = smoothed[index];
            ChainSmoothingApplications++;
            return true;
        }

        private static float ClosedLoopLength(IList<Vector3> positions)
        {
            if (positions == null || positions.Count < 2) return 0f;
            float length = 0f;
            for (int index = 0; index < positions.Count; index++)
                length += Vector3.Distance(
                    positions[index], positions[(index + 1) % positions.Count]);
            return length;
        }

        private void ApplyChainPointTranslation(
            ChainPointState point,
            Vector3 translation)
        {
            Node parent = point.node;
            Node child = parent.child;
            if (child == null) return;
            Vector3 origin = parent.position;
            float length = child.restLocalPosition.magnitude;
            if (length < 0.0001f) return;
            Quaternion defaultWorldRotation = parent.defaultWorldRotation;
            Vector3 defaultDirection = SafeDirection(
                defaultWorldRotation * child.boneAxis,
                child.position - origin);
            Vector3 candidate = ConstrainLength(
                origin, child.position + translation,
                defaultDirection, length);
            // Native CheckChainCollision calls UpdateSpringPosition here and
            // writes only selfTx.position.  It preserves the existing affine
            // rotation and does not rerun either FromTo or the angular hard-limit
            // stage; the interleaved dynamic layer consumes the corrected spring
            // position immediately afterwards.
            child.position = candidate;
            child.ready = true;
        }

        private static void ClosestPointsOnSegments(
            Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
            out float s, out float t, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);
            if (a <= 0.0000001f && e <= 0.0000001f)
            {
                s = t = 0f;
            }
            else if (a <= 0.0000001f)
            {
                s = 0f;
                t = Mathf.Clamp01(f / Mathf.Max(e, 0.0000001f));
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 0.0000001f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denominator = a * e - b * b;
                    s = denominator != 0f
                        ? Mathf.Clamp01((b * f - c * e) / denominator)
                        : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }

        private Vector3 ResolveCollision(Node node, Vector3 origin, Vector3 point, float boneLength)
        {
            if (_disableCollisionsForDiagnostics || collisionStrength <= 0.001f) return point;
            SwingCollider dynamicCollider = node.setting.dynamicCollider;
            float dynamicRadius;
            if (dynamicCollider == null)
            {
                dynamicRadius = Mathf.Max(0.003f, boneLength * 0.035f);
            }
            else
            {
                // ActorSwing's Sphere path reads jobCollider.float_A (offset
                // 0x34) as the radius; float_B is unused.  The imported hair
                // spheres commonly store A=0.01/0.015 and B=0.05.  Taking max(A,
                // B) inflated every hair collider by 3-5x and made the torso
                // capsule falsely eject both braids sideways.
                float authoredRadius = dynamicCollider.type == 0
                    ? dynamicCollider.float_A
                    : Mathf.Max(dynamicCollider.float_A, dynamicCollider.float_B);
                dynamicRadius = Mathf.Max(0.001f,
                    authoredRadius * MinAbsScale(node.bone.lossyScale));
            }
            int mask = dynamicCollider == null ? -1 : dynamicCollider.collisionMask;
            Vector3 result = point;
            bool usedAuthoredCollider = false;
            node.lastCollisionBone = "-";
            node.lastCollisionType = -1;
            node.lastCollisionCorrection = 0f;

            foreach (ColliderState state in _staticColliders)
            {
                if (mask != -1 && state.collider.collisionMask != -1 &&
                    (mask & state.collider.collisionMask) == 0) continue;
                usedAuthoredCollider = true;
                Vector3 before = result;
                result = state.PushOutside(result, dynamicRadius, collisionStrength);
                float correction = Vector3.Distance(before, result);
                if (correction > node.lastCollisionCorrection)
                {
                    node.lastCollisionBone = state.transform.name;
                    node.lastCollisionType = state.collider.type;
                    node.lastCollisionCorrection = correction;
                }
                if (correction > 0.000001f)
                {
                    node.collisionHitCount++;
                    if (correction > node.maximumCollisionCorrection)
                    {
                        node.maximumCollisionBone = state.transform.name;
                        node.maximumCollisionType = state.collider.type;
                        node.maximumCollisionCorrection = correction;
                    }
                }
            }

            if (!usedAuthoredCollider)
            {
                if (_head != null)
                {
                    Vector3 center = _head.position + _head.rotation * new Vector3(0f, 0.025f, 0.005f);
                    result = PushOutsideSphere(result, center, 0.135f + dynamicRadius,
                        collisionStrength);
                }
                if (_chest != null)
                {
                    Vector3 top = _chest.position + Vector3.up * 0.10f;
                    Vector3 bottom = _chest.position - Vector3.up * 0.24f;
                    result = PushOutsideCapsule(result, top, bottom, 0.16f + dynamicRadius,
                        collisionStrength);
                }
            }
            return result;
        }

        private void ApplyQuartzDrivers()
        {
            _quartzBaseRotations.Clear();
            foreach (QuartzDriverState driver in _quartzDrivers)
            {
                Vector3 headEuler = driver.HeadDeltaEuler;
                Vector3 neckEuler = driver.NeckDeltaEuler;
                QuartzHairSetting setting = driver.setting;
                Vector3 headCompensation = ClampComponents(
                    Vector3.Scale(headEuler, setting.headRotateCoefficient),
                    setting.headRotateLimitMin,
                    setting.headRotateLimitMax);
                Vector3 neckCompensation = ClampComponents(
                    Vector3.Scale(neckEuler, setting.neckRotateCoefficient),
                    setting.neckRotateLimitMin,
                    setting.neckRotateLimitMax);
                Quaternion compensation = Quaternion.Euler(headCompensation + neckCompensation);
                Quaternion baseRotation = setting.composeType == 1
                    ? compensation * driver.restLocalRotation
                    : driver.restLocalRotation * compensation;
                driver.transform.localRotation = baseRotation;
                _quartzBaseRotations[driver.transform] = baseRotation;
            }
            foreach (QuartzSkirtDriverState driver in _quartzSkirtDrivers)
            {
                QuartzSkirtSetting setting = driver.setting;
                Vector3 delta = SignedEuler(
                    Quaternion.Inverse(driver.referenceRestLocalRotation) *
                    driver.referenceBone.localRotation);
                float connectionValue = setting.connectionAxis == 1
                    ? delta.y
                    : setting.connectionAxis == 2 ? delta.z : delta.x;
                bool left = driver.transform.name.StartsWith(
                    "Left", StringComparison.Ordinal);
                bool outward = left
                    ? connectionValue >= 0f
                    : connectionValue <= 0f;
                Vector3 coefficient = outward
                    ? setting.outerCoefficient
                    : setting.innerCoefficient;
                Vector3 rotation = ClampComponents(
                    Vector3.Scale(delta, coefficient),
                    setting.limitMin,
                    setting.limitMax);
                Quaternion baseRotation = driver.restLocalRotation *
                    Quaternion.Euler(rotation);
                driver.transform.localRotation = baseRotation;
                _quartzBaseRotations[driver.transform] = baseRotation;
            }
        }

        private void ResetNodesToBasePose()
        {
            foreach (Node node in _nodes)
            {
                Quaternion rotation;
                node.bone.localPosition = node.restLocalPosition;
                node.bone.localRotation = _quartzBaseRotations.TryGetValue(node.bone, out rotation)
                    ? rotation
                    : node.restLocalRotation;
            }
        }

        private void CaptureAuthoredPose()
        {
            foreach (Node node in _nodes)
            {
                node.authoredPosition = node.bone.position;
                node.authoredRotation = node.bone.rotation;
                if (!node.ready) ResetNode(node);
            }
        }

        private void CollectMetrics()
        {
            float angleSum = 0f;
            float maxAngle = 0f;
            float braidSum = 0f;
            float braidSquared = 0f;
            float braidMax = 0f;
            int braidCount = 0;
            int count = 0;
            foreach (Node node in _nodes)
            {
                Node child = node.child;
                if (!node.ready || child == null || !child.ready) continue;
                Vector3 origin = node.position;
                Quaternion baseWorldRotation = node.defaultWorldRotation;
                Vector3 authored = baseWorldRotation * child.boneAxis;
                Vector3 current = SafeDirection(child.position - origin, authored);
                float angle = Vector3.Angle(authored, current);
                angleSum += angle;
                maxAngle = Mathf.Max(maxAngle, angle);
                count++;
                if (!node.isBraid) continue;
                braidSum += angle;
                braidSquared += angle * angle;
                braidMax = Mathf.Max(braidMax, angle);
                braidCount++;
            }
            MeanAngularOffset = angleSum / Mathf.Max(1, count);
            MaxAngularOffset = maxAngle;
            BraidMeanAngularOffset = braidSum / Mathf.Max(1, braidCount);
            BraidMaxAngularOffset = braidMax;
            float meanSquare = braidSquared / Mathf.Max(1, braidCount);
            BraidAngularVariation = Mathf.Sqrt(Mathf.Max(0f,
                meanSquare - BraidMeanAngularOffset * BraidMeanAngularOffset));
        }

        public void LogCurrentState(string label)
        {
            if (!_dumpStateForDiagnostics) return;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[ChainState] label={0} system={1} schedule={2} layers={3} corrections={4} smoothing={5} restorations={6} maxRestoredLoopError={7:R}",
                label, _systemLabel,
                _legacyPostChainLayersForDiagnostics ? "legacy-post" : "native-interleaved",
                _chainLayers.Count, ChainCollisionCorrections,
                ChainSmoothingApplications, ChainLoopLengthRestorations,
                MaxRestoredChainLoopLengthError));
            foreach (Node node in _nodes)
            {
                Node child = node.child;
                if (child == null || !node.ready || !child.ready) continue;
                Vector3 origin = node.position;
                Quaternion baseWorldRotation = node.defaultWorldRotation;
                Vector3 authored = SafeDirection(
                    baseWorldRotation * child.boneAxis,
                    Vector3.down);
                Vector3 current = SafeDirection(child.position - origin, authored);
                Debug.Log(string.Format(
                    "[HairState] label={0} name={1} origin={2} authored={3} current={4} " +
                    "angle={5:0.000} tip={6} childSpeed={7} defaultTip={8} braid={9} " +
                    "preLimit={10} limitEuler={11} clamped={12} limited={13} " +
                    "collision={14}:{15}/{16:0.000000} cumulative={17}/{18}:{19}/{20:0.000000} " +
                    "rootCancel={21}",
                    label, node.bone.name, origin.ToString("F5"), authored.ToString("F5"),
                    current.ToString("F5"), Vector3.Angle(authored, current),
                    child.position.ToString("F5"), node.childSpeed.ToString("F5"),
                    child.defaultPosition.ToString("F5"), node.isBraid,
                    node.lastPreLimitDirection.ToString("F5"),
                    node.lastLimitEuler.ToString("F3"),
                    node.lastClampedLimitEuler.ToString("F3"),
                    node.lastLimitedDirection.ToString("F5"),
                    node.lastCollisionBone,
                    node.lastCollisionType,
                    node.lastCollisionCorrection,
                    node.collisionHitCount,
                    node.maximumCollisionBone,
                    node.maximumCollisionType,
                    node.maximumCollisionCorrection,
                    node.lastRootTranslationCancellation.ToString("F6")));
            }
        }

        private static void ResetNode(Node node)
        {
            node.position = node.authoredPosition;
            node.rotation = node.authoredRotation;
            node.defaultPosition = node.authoredPosition;
            node.defaultRotation = node.authoredRotation;
            node.defaultWorldRotation = node.authoredRotation;
            node.childSpeed = Vector3.zero;
            node.lastCollisionBone = "-";
            node.lastCollisionType = -1;
            node.lastCollisionCorrection = 0f;
            node.collisionHitCount = 0;
            node.maximumCollisionBone = "-";
            node.maximumCollisionType = -1;
            node.maximumCollisionCorrection = 0f;
            node.lastRootTranslationCancellation = Vector3.zero;
            node.ready = true;
        }

        private static Node FindDynamicAncestor(
            Transform transform,
            Dictionary<Transform, Node> nodesByTransform)
        {
            while (transform != null)
            {
                Node result;
                if (nodesByTransform.TryGetValue(transform, out result)) return result;
                transform = transform.parent;
            }
            return null;
        }

        private static Node FindDynamicChild(
            Transform bone,
            Dictionary<Transform, Node> nodesByTransform)
        {
            Queue<Transform> pending = new Queue<Transform>();
            for (int index = 0; index < bone.childCount; index++)
                pending.Enqueue(bone.GetChild(index));
            while (pending.Count > 0)
            {
                Transform candidate = pending.Dequeue();
                Node result;
                if (nodesByTransform.TryGetValue(candidate, out result)) return result;
                for (int index = 0; index < candidate.childCount; index++)
                    pending.Enqueue(candidate.GetChild(index));
            }
            return null;
        }

        private static Vector3 ConstrainLength(
            Vector3 origin,
            Vector3 point,
            Vector3 fallbackDirection,
            float length)
        {
            return origin + SafeDirection(point - origin, fallbackDirection) * length;
        }

        private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
        {
            if (value.sqrMagnitude > 0.00000001f) return value.normalized;
            if (fallback.sqrMagnitude > 0.00000001f) return fallback.normalized;
            return Vector3.down;
        }

        private static Vector3 ClampComponents(Vector3 value, Vector3 minimum, Vector3 maximum)
        {
            return new Vector3(
                Mathf.Clamp(value.x, minimum.x, maximum.x),
                Mathf.Clamp(value.y, minimum.y, maximum.y),
                Mathf.Clamp(value.z, minimum.z, maximum.z));
        }

        private static Vector3 SignedEuler(Quaternion rotation)
        {
            // ActorSwingUtility.ToUnityEuler, recovered from RVA 0x9894c0.
            // Keep the signed atan/asin result: Quaternion.eulerAngles adds a
            // 0..360 remap and differs near the limit frames used by the braids.
            float x = rotation.x;
            float y = rotation.y;
            float z = rotation.z;
            float w = rotation.w;
            float eulerX = Mathf.Asin(Mathf.Clamp(2f * (x * w - y * z), -1f, 1f));
            float eulerY = Mathf.Atan2(
                2f * (x * z + y * w),
                w * w - x * x - y * y + z * z);
            float eulerZ = Mathf.Atan2(
                2f * (x * y + z * w),
                w * w - x * x + y * y - z * z);
            return new Vector3(eulerX, eulerY, eulerZ) * Mathf.Rad2Deg;
        }

        private static Vector3 PushOutsideSphere(
            Vector3 point,
            Vector3 center,
            float radius,
            float weight)
        {
            Vector3 delta = point - center;
            float distance = delta.magnitude;
            if (distance >= radius) return point;
            Vector3 normal = distance > 0.00001f ? delta / distance : Vector3.up;
            return Vector3.Lerp(point, center + normal * radius, Mathf.Clamp01(weight));
        }

        private static Vector3 PushOutsideCapsule(
            Vector3 point,
            Vector3 top,
            Vector3 bottom,
            float radius,
            float weight)
        {
            Vector3 axis = bottom - top;
            float t = Mathf.Clamp01(Vector3.Dot(point - top, axis) /
                Mathf.Max(axis.sqrMagnitude, 0.0000001f));
            return PushOutsideSphere(point, top + axis * t, radius, weight);
        }

        private static float MinAbsScale(Vector3 value)
        {
            return Mathf.Min(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }

        private static int HierarchyDepth(Transform value)
        {
            int result = 0;
            while (value != null && value.parent != null)
            {
                result++;
                value = value.parent;
            }
            return result;
        }

        private sealed class Node
        {
            public readonly ActorSwingDynamicBone setting;
            public readonly Transform bone;
            public readonly Vector3 restLocalPosition;
            public readonly Quaternion restLocalRotation;
            public readonly Vector3 boneAxis;
            public readonly int depth;
            public readonly float phase;
            public readonly bool isBraid;
            public Node parent;
            public Node child;
            public Node referenceNode;
            public Vector3 authoredPosition;
            public Quaternion authoredRotation;
            public Vector3 defaultPosition;
            public Quaternion defaultRotation;
            public Quaternion defaultWorldRotation;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 childSpeed;
            public Vector3 lastPreLimitDirection;
            public Vector3 lastLimitEuler;
            public Vector3 lastClampedLimitEuler;
            public Vector3 lastLimitedDirection;
            public string lastCollisionBone = "-";
            public int lastCollisionType = -1;
            public float lastCollisionCorrection;
            public int collisionHitCount;
            public string maximumCollisionBone = "-";
            public int maximumCollisionType = -1;
            public float maximumCollisionCorrection;
            public Vector3 lastRootTranslationCancellation;
            public bool ready;

            public Node(ActorSwingDynamicBone value, int hierarchyDepth)
            {
                setting = value;
                bone = value.transform;
                restLocalPosition = bone.localPosition;
                restLocalRotation = bone.localRotation;
                boneAxis = SafeDirection(restLocalPosition, Vector3.down);
                depth = hierarchyDepth;
                phase = Mathf.Abs(bone.name.GetHashCode() % 1000) * 0.017f;
                isBraid = bone.name.StartsWith("LeftHair", StringComparison.Ordinal) ||
                    bone.name.StartsWith("RightHair", StringComparison.Ordinal);
            }
        }

        private sealed class QuartzDriverState
        {
            public readonly Transform transform;
            public readonly QuartzHairSetting setting;
            public readonly Quaternion restLocalRotation;
            private readonly Transform _headReference;
            private readonly Transform _neckReference;
            private readonly Quaternion _headRestWorldRotation;
            private readonly Quaternion _neckRestWorldRotation;

            public QuartzDriverState(ActorAnimationQuartzDriverHairBone driver, Transform fallbackHead)
            {
                transform = driver.transform;
                setting = driver.setting;
                restLocalRotation = transform.localRotation;
                _headReference = setting.referenceHeadBone == null
                    ? fallbackHead
                    : setting.referenceHeadBone;
                _neckReference = setting.referenceNeckBone == null
                    ? (_headReference == null ? null : _headReference.parent)
                    : setting.referenceNeckBone;
                _headRestWorldRotation = _headReference == null
                    ? Quaternion.identity
                    : _headReference.rotation;
                _neckRestWorldRotation = _neckReference == null
                    ? Quaternion.identity
                    : _neckReference.rotation;
            }

            public Vector3 HeadDeltaEuler
            {
                get
                {
                    if (_headReference == null) return Vector3.zero;
                    return SignedEuler(Quaternion.Inverse(_headRestWorldRotation) *
                        _headReference.rotation);
                }
            }

            public Vector3 NeckDeltaEuler
            {
                get
                {
                    if (_neckReference == null) return Vector3.zero;
                    return SignedEuler(Quaternion.Inverse(_neckRestWorldRotation) *
                        _neckReference.rotation);
                }
            }
        }

        private sealed class QuartzSkirtDriverState
        {
            public readonly Transform transform;
            public readonly QuartzSkirtSetting setting;
            public readonly Quaternion restLocalRotation;
            public readonly Quaternion referenceRestLocalRotation;
            public readonly Transform referenceBone;

            public QuartzSkirtDriverState(
                ActorAnimationQuartzDriverSkirtBone driver,
                Transform resolvedReferenceBone)
            {
                transform = driver.transform;
                setting = driver.setting;
                restLocalRotation = transform.localRotation;
                referenceBone = resolvedReferenceBone;
                referenceRestLocalRotation = referenceBone.localRotation;
            }
        }

        private sealed class ChainPointState
        {
            public readonly ActorSwingDynamicBone setting;
            public readonly Node node;
            public readonly int collisionMask;

            public ChainPointState(ActorSwingDynamicBone value, Node controller)
            {
                setting = value;
                node = controller;
                collisionMask = value.dynamicCollider == null
                    ? -1
                    : value.dynamicCollider.collisionMask;
            }
        }

        private sealed class ChainLayerState
        {
            public readonly List<ChainPointState> points;
            public readonly float radius;
            public readonly float smoothing;
            public readonly bool around;
            public readonly int depth;
            public readonly float initialLoopLength;

            public ChainLayerState(
                List<ChainPointState> values,
                float layerRadius,
                float layerSmoothing,
                bool loop)
            {
                points = values;
                radius = Mathf.Max(0f, layerRadius);
                smoothing = Mathf.Max(0f, layerSmoothing);
                around = loop;
                depth = values.Min(value => value.node.child.depth);
                Vector3[] modelingPositions = values
                    .Select(value => value.node.child.bone.position)
                    .ToArray();
                initialLoopLength = around
                    ? ClosedLoopLength(modelingPositions)
                    : 0f;
            }
        }

        private sealed class ColliderState
        {
            public readonly Transform transform;
            public readonly SwingCollider collider;

            public ColliderState(Transform owner, SwingCollider value)
            {
                transform = owner;
                collider = value;
            }

            public bool TryResolveChainCollision(
                Vector3 chainStart,
                Vector3 chainEnd,
                float chainRadius,
                float weight,
                out float chainT,
                out Vector3 correction)
            {
                chainT = 0f;
                correction = Vector3.zero;
                float scale = MinAbsScale(transform.lossyScale);
                if (collider.type == 3)
                {
                    Vector3 planePoint = transform.position +
                        transform.rotation * (collider.vector3_A * scale);
                    Vector3 normal = transform.TransformDirection(
                        collider.vector3_B).normalized;
                    if (normal.sqrMagnitude < 0.000001f) normal = transform.up;
                    float startDistance = Vector3.Dot(
                        chainStart - planePoint, normal);
                    float endDistance = Vector3.Dot(
                        chainEnd - planePoint, normal);
                    if (startDistance <= endDistance)
                    {
                        chainT = 0f;
                        if (startDistance >= chainRadius) return false;
                        correction = normal * (chainRadius - startDistance) *
                            Mathf.Clamp01(weight);
                    }
                    else
                    {
                        chainT = 1f;
                        if (endDistance >= chainRadius) return false;
                        correction = normal * (chainRadius - endDistance) *
                            Mathf.Clamp01(weight);
                    }
                    return correction.sqrMagnitude > 0.0000000001f;
                }

                Vector3 staticStart;
                Vector3 staticEnd;
                float radiusStart;
                float radiusEnd;
                if (!TryGetSegmentGeometry(
                        scale, out staticStart, out staticEnd,
                        out radiusStart, out radiusEnd)) return false;
                float staticT;
                Vector3 chainPoint;
                Vector3 staticPoint;
                ClosestPointsOnSegments(
                    chainStart, chainEnd, staticStart, staticEnd,
                    out chainT, out staticT, out chainPoint, out staticPoint);
                Vector3 separation = chainPoint - staticPoint;
                float distance = separation.magnitude;
                float combinedRadius = chainRadius +
                    Mathf.Lerp(radiusStart, radiusEnd, staticT);
                if (distance >= combinedRadius) return false;
                Vector3 normalDirection = distance > 0.00001f
                    ? separation / distance
                    : transform.forward;
                correction = normalDirection * (combinedRadius - distance) *
                    Mathf.Clamp01(weight);
                return correction.sqrMagnitude > 0.0000000001f;
            }

            private bool TryGetSegmentGeometry(
                float scale,
                out Vector3 start,
                out Vector3 end,
                out float radiusStart,
                out float radiusEnd)
            {
                radiusStart = Mathf.Max(0f, collider.float_A) * scale;
                radiusEnd = Mathf.Max(0f, collider.float_B) * scale;
                switch (collider.type)
                {
                    case 0:
                        start = end = transform.position +
                            transform.rotation * (collider.vector3_A * scale);
                        radiusEnd = radiusStart;
                        return true;
                    case 1:
                        Vector3 localCenter = collider.vector3_A * scale;
                        int axisIndex = Mathf.Clamp(
                            Mathf.RoundToInt(collider.vector3_B.x), 0, 2);
                        Vector3 localOffset = Vector3.zero;
                        localOffset[axisIndex] =
                            collider.vector3_B.y * 0.5f * scale - radiusStart;
                        start = transform.position +
                            transform.rotation * (localCenter - localOffset);
                        end = transform.position +
                            transform.rotation * (localCenter + localOffset);
                        return true;
                    case 2:
                        if (transform.parent == null)
                        {
                            start = end = Vector3.zero;
                            return false;
                        }
                        start = transform.position;
                        end = transform.parent.position;
                        Vector3 line = end - start;
                        float length = line.magnitude;
                        if (length > 0.00001f)
                        {
                            Vector3 direction = line / length;
                            float radiusSum = radiusStart + radiusEnd;
                            if (length < radiusSum && radiusSum > 0.00001f)
                            {
                                float factor = length / radiusSum;
                                radiusStart *= factor;
                                radiusEnd *= factor;
                            }
                            start += direction * radiusStart;
                            end -= direction * radiusEnd;
                        }
                        return true;
                    default:
                        start = end = Vector3.zero;
                        return false;
                }
            }

            public Vector3 PushOutside(Vector3 point, float dynamicRadius, float weight)
            {
                // ConvertJobCollider (RVA 0x9A75B0) uses the minimum lossy-scale
                // component as a uniform collider scale.  The serialized fields
                // are not two generic endpoints:
                //   Sphere  : A is its local centre.
                //   Capsule : A is its local centre; B.x selects the local axis
                //             and B.y is the authored full height.  The native
                //             converter builds symmetric endpoints after
                //             subtracting the end-sphere radius.
                //   Line    : the endpoints are the driven transform and its
                //             Transform parent.  Serialized A/B are not line
                //             endpoints. UpdateCollider then shortens the line by
                //             the endpoint radii.
                // Treating Capsule.B as a position made the Neck collider one
                // metre wide; treating zero-valued Line fields as endpoints made
                // arm lines into shoulder spheres. Both falsely ejected braids.
                float scale = MinAbsScale(transform.lossyScale);
                float radiusA = Mathf.Max(0f, collider.float_A) * scale + dynamicRadius;
                float radiusB = Mathf.Max(0f, collider.float_B) * scale + dynamicRadius;
                switch (collider.type)
                {
                    case 0: // Sphere
                        Vector3 sphereCenter = transform.position +
                            transform.rotation * (collider.vector3_A * scale);
                        return PushOutsideSphere(point, sphereCenter, radiusA, weight);
                    case 1: // Capsule with linearly interpolated endpoint radii.
                        Vector3 localCenter = collider.vector3_A * scale;
                        int axisIndex = Mathf.Clamp(Mathf.RoundToInt(collider.vector3_B.x), 0, 2);
                        Vector3 localOffset = Vector3.zero;
                        localOffset[axisIndex] = collider.vector3_B.y * 0.5f * scale -
                            Mathf.Max(0f, collider.float_A) * scale;
                        Vector3 capsuleA = transform.position +
                            transform.rotation * (localCenter - localOffset);
                        Vector3 capsuleB = transform.position +
                            transform.rotation * (localCenter + localOffset);
                        return PushOutsideTaperedCapsule(point, capsuleA, capsuleB,
                            radiusA, radiusB, weight);
                    case 2: // Line joins this transform to its hierarchy parent.
                        if (transform.parent == null) return point;
                        Vector3 lineA = transform.position;
                        Vector3 lineB = transform.parent.position;
                        Vector3 line = lineB - lineA;
                        float lineLength = line.magnitude;
                        if (lineLength > 0.00001f)
                        {
                            Vector3 direction = line / lineLength;
                            float authoredRadiusA = Mathf.Max(0f, collider.float_A) * scale;
                            float authoredRadiusB = Mathf.Max(0f, collider.float_B) * scale;
                            float radiusSum = authoredRadiusA + authoredRadiusB;
                            if (lineLength < radiusSum && radiusSum > 0.00001f)
                            {
                                float factor = lineLength / radiusSum;
                                authoredRadiusA *= factor;
                                authoredRadiusB *= factor;
                            }
                            lineA += direction * authoredRadiusA;
                            lineB -= direction * authoredRadiusB;
                        }
                        return PushOutsideTaperedCapsule(point, lineA, lineB,
                            radiusA, radiusB, weight);
                    case 3: // Plane: A is a point and B is its local normal
                        Vector3 a = transform.position +
                            transform.rotation * (collider.vector3_A * scale);
                        Vector3 normal = transform.TransformDirection(collider.vector3_B).normalized;
                        if (normal.sqrMagnitude < 0.000001f) normal = transform.up;
                        float distance = Vector3.Dot(point - a, normal);
                        if (distance >= dynamicRadius) return point;
                        return Vector3.Lerp(point,
                            point + normal * (dynamicRadius - distance),
                            Mathf.Clamp01(weight));
                    default:
                        return point;
                }
            }

            private static Vector3 PushOutsideTaperedCapsule(
                Vector3 point,
                Vector3 a,
                Vector3 b,
                float radiusA,
                float radiusB,
                float weight)
            {
                Vector3 axis = b - a;
                float axisLengthSq = axis.sqrMagnitude;
                float t = axisLengthSq > 0.0000001f
                    ? Mathf.Clamp01(Vector3.Dot(point - a, axis) / axisLengthSq)
                    : 0f;
                Vector3 center = a + axis * t;
                float radius = Mathf.Lerp(radiusA, radiusB, t);
                return PushOutsideSphere(point, center, radius, weight);
            }
        }
    }
}
