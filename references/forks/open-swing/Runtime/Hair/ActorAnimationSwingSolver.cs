// Generic Unity swing solver for transform chains. No model, animation, or game configuration is included.
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace OpenSwing
{
[DefaultExecutionOrder(300)]
public sealed class ActorAnimationSwingSolver : MonoBehaviour
{
    public enum DynamicType { Swing = 0, Slide = 1 }
    public enum ColliderType { Sphere = 0, Capsule = 1, Line = 2, Plane = 3, None = 4 }

    [Serializable]
    public class NodeSetting
    {
        public string name;
        public string referenceBone;
        public Vector3 referenceMin, referenceMax;
        public DynamicType dynamicType;
        public float damping, stiffness, rootWeight;
        public float rootHorizontalWeight = -1f, rootVerticalWeight = -1f;
        public float spring, pendulum, pendulumRange, mass, wind, useWindGlobalForce;
        public float axisAddXToY, axisAddXToZ;
        public int collisionMask;
        public float collisionRadius;
        public Vector3 collisionOffset;
        public bool limit;
        public Vector3 minAngles = new Vector3(-180f, -180f, -180f);
        public Vector3 maxAngles = new Vector3(180f, 180f, 180f);

        public float RootHorizontal => rootHorizontalWeight >= 0f ? rootHorizontalWeight : rootWeight;
        public float RootVertical => rootVerticalWeight >= 0f ? rootVerticalWeight : rootWeight;
    }

    [Serializable]
    public class ChainLayerSetting
    {
        public bool active = true, around;
        public float radius, smoothing;
        public string[] bones = Array.Empty<string>();
    }

    [Serializable]
    public class StaticColliderSetting
    {
        public string source, anchor;
        public ColliderType type;
        public int collisionMask = -1;
        public Vector3 vector3_A, vector3_B;
        public float float_A, float_B;
        public int seatDynamicCorrectionDisableCollisionMask;
    }

    [Serializable]
    public class ManagePropertyData
    {
        public float weight = 1f;
        public float swingPowerWeight = 1f;
        public float rootWeight = 1f;
        public float rootHorizontalWeight = 1f;
        public float rootVerticalWeight = 1f;
        public Vector3 globalForce;
        public Vector3 windDirection = Vector3.forward;
        public float windPower;
        public float windFluctuation = 1f;
        public float windWaveAmplitude;
        public float windWaveFrequency = 1f;
        public float windWavePhase;
        public float windRangeMax = 10f;
        public float windRangeMin = -10f;
        public float windRangeFrequency;
        public float windRangePhase;
        public float randomPower;
        public float randomFluctuation;
    }

    [Serializable]
    public class QuartzHairSetting
    {
        public string bone;
        public int translateConnectionAxis = 3;
        public Vector3 headTranslateCoefficient;
        public Vector3 headTranslateLimitMin = new Vector3(-180f, -180f, -180f);
        public Vector3 headTranslateLimitMax = new Vector3(180f, 180f, 180f);
        public int rotationOrder;
        public Vector3 headRotateCoefficient;
        public Vector3 headRotateLimitMin;
        public Vector3 headRotateLimitMax;
        public int rotateConnectionAxis;
        public Vector3 neckRotateCoefficient;
        public Vector3 neckRotateLimitMin;
        public Vector3 neckRotateLimitMax;
        public int composeType;
    }

    [Serializable]
    public class Settings
    {
        public string source;
        public NodeSetting[] nodes = Array.Empty<NodeSetting>();
        public ChainLayerSetting[] chains = Array.Empty<ChainLayerSetting>();
        public StaticColliderSetting[] staticColliders = Array.Empty<StaticColliderSetting>();
        public ManagePropertyData manage;
        public QuartzHairSetting[] quartzHair;
    }

    [Serializable]
    public class BodyCollider
    {
        public string source;
        public ColliderType type = ColliderType.Capsule;
        public Transform anchor;
        public int collisionMask = -1;
        public Vector3 start, end;
        public float radius, endRadius = -1f;
        public Vector2 lineInsets;
        public Vector3 planeNormal = Vector3.forward;
    }

    sealed class Node
    {
        public Transform bone, child;
        public Node parentLink, referenceNode;
        public Transform referenceBone;
        public NodeSetting setting, limitSetting;
        public Vector3 axis;
        public float length;
        public int depth;
        public Quaternion poseLocalRotation;
        public Vector3 poseLocalPosition;
        public Quaternion poseRootRotation, poseParentRootRotation, selfRotation;
        public Vector3 position, previousPosition, speed;
        public Vector3 selfPosition, rootCancel, childLocalPosition;
        public Quaternion childLocalRotation, defaultRotation;
        public readonly Vector3[] prewarmCache = new Vector3[5];
        public int prewarmCacheCount;
        public Vector3 baselineTip;
        public int hitCheckCount;
        public bool frontStrand;
    }

    sealed class ChainLayer
    {
        public Node[] nodes;
        public bool around;
        public float radius, smoothing, initialLoopLength;
    }

    sealed class QuartzHairDriver
    {
        public Transform driven;
        public QuartzHairSetting setting;
    }

    struct HitInfo
    {
        public Vector3 normal, position;
        public float value;
    }

    [NonSerialized] public Dictionary<string, Transform> bindings;
    public int ActiveChainLayers => chainLayers.Count;
    public int ActiveQuartzDrivers => quartzHairDrivers.Count;
    public Transform head;
    public Transform neck;
    public Transform rootMotionSource;
    public TextAsset configuration;
    public string[] simulatedBonePrefixes = Array.Empty<string>();
    public string[] frontStrandPrefixes = Array.Empty<string>();
    public ManagePropertyData manage = new ManagePropertyData();
    public BodyCollider[] bodyColliders = Array.Empty<BodyCollider>();
    public QuartzHairSetting[] quartzHairSettings = Array.Empty<QuartzHairSetting>();
    public bool simulate = true, manualSimulation;
    public bool enableQuartzHair = true;
    public bool enableCollision = true, enableWind = true, enableChainSmoothing = true;
    [Range(0f, 1f)] public float manualWindStrength;
    [Range(0f, 1f)] public float strength = 1f;
    [Min(0)] public int prewarmSteps = 30;

    public int MatchedNodes { get; private set; }
    public int ImportedColliders { get; private set; }
    public int MissingColliderAnchors { get; private set; }
    public int SimulatedNodes => nodes.Count;
    public int ResetCount { get; private set; }
    public float MaxDeviation { get; private set; }
    public float MaxLengthError { get; private set; }
    public float MaxPenetration { get; private set; }
    public float FrontStaticPenetration { get; private set; }
    public float MaxDynamicVelocity { get; private set; }
    public float FreeHairDeviation { get; private set; }
    public float FrontHairDeviation { get; private set; }
    public float FrontRootDrift { get; private set; }
    public string MaxPenetrationNode { get; private set; }
    public string MaxPenetrationCollider { get; private set; }
    public bool IsFinite { get; private set; } = true;
    public float EffectiveWindStrength => manualWindStrength;
    public const float NativeStep = 0.01667f;

    readonly List<Node> nodes = new List<Node>();
    readonly List<ChainLayer> chainLayers = new List<ChainLayer>();
    readonly List<QuartzHairDriver> quartzHairDrivers = new List<QuartzHairDriver>();
    readonly Dictionary<Transform, Node> nodeByBone = new Dictionary<Transform, Node>();
    bool initialized, needsReset = true;
    int restoredFrame = -1;
    float simulationTime;
    Vector3 lastRootPosition;

    void Awake() { if (configuration) Initialize(); }
    void LateUpdate() { if (!manualSimulation && configuration) Step(Time.unscaledDeltaTime); }

    public void Initialize()
    {
        if (initialized) return;
        if (!configuration) throw new InvalidOperationException("ActorAnimation swing configuration is missing");

        var settings = JsonUtility.FromJson<Settings>(configuration.text);
        if (settings == null || settings.nodes == null) throw new InvalidOperationException("Invalid ActorAnimation swing configuration");
        if (settings.manage != null) manage = settings.manage;
        if (settings.quartzHair != null) quartzHairSettings = settings.quartzHair;

        var transforms = bindings ?? GetComponentsInChildren<Transform>(true)
            .GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
        var settingByName = settings.nodes
            .Where(s => s != null && !string.IsNullOrEmpty(s.name))
            .GroupBy(s => s.name).ToDictionary(g => g.Key, g => g.First());
        foreach (var setting in settings.nodes)
        {
            if (setting == null || string.IsNullOrEmpty(setting.name)) continue;
            if (simulatedBonePrefixes != null && simulatedBonePrefixes.Length > 0
                && !simulatedBonePrefixes.Any(prefix => !string.IsNullOrEmpty(prefix) && setting.name.StartsWith(prefix, StringComparison.Ordinal))) continue;
            if (!transforms.TryGetValue(setting.name, out var bone)) continue;
            MatchedNodes++;
            Transform child = null;
            for (int i = 0; i < bone.childCount; i++)
            {
                var candidate = bone.GetChild(i);
                if (candidate.localPosition.sqrMagnitude > 1e-10f) { child = candidate; break; }
            }
            if (!child) continue;

            var a = RootPoint(bone.position);
            var b = RootPoint(child.position);
            var delta = b - a;
            if (delta.sqrMagnitude <= 1e-12f) continue;
            var dynamicSetting = settingByName.TryGetValue(child.name, out var childSetting)
                ? childSetting : setting;
            var node = new Node
            {
                bone = bone,
                child = child,
                setting = dynamicSetting,
                limitSetting = setting,
                axis = child.localPosition.normalized,
                length = delta.magnitude,
                depth = Depth(bone),
                poseLocalRotation = bone.localRotation,
                poseLocalPosition = bone.localPosition,
                childLocalPosition = child.localPosition,
                childLocalRotation = child.localRotation,
                frontStrand = IsFrontStrand(setting.name) || IsFrontStrand(dynamicSetting.name)
            };
            nodes.Add(node);
            nodeByBone[bone] = node;
        }
        nodes.Sort((a, b) => a.depth.CompareTo(b.depth));
        var linkByChild = nodes.GroupBy(n => n.child).ToDictionary(g => g.Key, g => g.First());
        foreach (var node in nodes)
            if (linkByChild.TryGetValue(node.bone, out var parentLink)) node.parentLink = parentLink;

        foreach (var node in nodes) {
            var reference = node.limitSetting.referenceBone;
            if (string.IsNullOrEmpty(reference)) continue;
            if (!transforms.TryGetValue(reference, out node.referenceBone))
                throw new InvalidOperationException("Missing reference bone: " + reference);
            nodeByBone.TryGetValue(node.referenceBone, out node.referenceNode);
        }

        if (!rootMotionSource) rootMotionSource = head ? head : transform;

        if (settings.staticColliders != null && settings.staticColliders.Length > 0)
            BuildStaticColliders(settings.staticColliders, transforms);
        BuildChainLayers(settings.chains, transforms);
        if (!neck && transforms.TryGetValue("Neck", out var neckTransform)) neck = neckTransform;
        if (quartzHairSettings != null)
            foreach (var setting in quartzHairSettings)
                if (setting != null && !string.IsNullOrEmpty(setting.bone) && transforms.TryGetValue(setting.bone, out var driven))
                    quartzHairDrivers.Add(new QuartzHairDriver { driven = driven, setting = NativeConvertQuartz(setting) });
        initialized = true;
        RequestReset();
        string scope = simulatedBonePrefixes != null && simulatedBonePrefixes.Length > 0 ? string.Join("+", simulatedBonePrefixes) : "all";
        Debug.Log($"ACTOR_SWING_READY source={settings.source} scope={scope} matched={MatchedNodes}/{settings.nodes.Length} segments={nodes.Count} chains={chainLayers.Count} colliders={bodyColliders.Length} quartzHair={quartzHairDrivers.Count}");
    }

    void BuildStaticColliders(StaticColliderSetting[] settings, Dictionary<string, Transform> transforms)
    {
        var imported = new List<BodyCollider>(settings.Length);
        foreach (var setting in settings)
        {
            if (setting == null || string.IsNullOrEmpty(setting.anchor) || !transforms.TryGetValue(setting.anchor, out var anchor))
            {
                MissingColliderAnchors++;
                Debug.LogWarning($"ACTOR_SWING_COLLIDER_ANCHOR_MISSING source={setting?.source} anchor={setting?.anchor}");
                continue;
            }
            var start = setting.vector3_A;
            var end = setting.vector3_B;
            if (setting.type == ColliderType.Capsule)
                NativeCapsuleEndpoints(setting.vector3_A, setting.vector3_B,
                    setting.float_A, out start, out end);
            imported.Add(new BodyCollider
            {
                type = setting.type,
                source = setting.source,
                anchor = anchor,
                collisionMask = setting.collisionMask,
                start = start,
                end = end,
                radius = Mathf.Max(0f, setting.float_A),
                endRadius = setting.float_B,
                lineInsets = new Vector2(setting.vector3_A.x, setting.vector3_A.y),
                planeNormal = setting.vector3_B.sqrMagnitude > 1e-10f ? setting.vector3_B : Vector3.forward
            });
        }
        bodyColliders = imported.ToArray();
        ImportedColliders = bodyColliders.Length;
    }

    public static void NativeCapsuleEndpoints(Vector3 center, Vector3 shape,
        float radiusA, out Vector3 a, out Vector3 b)
    {
        int axis = (int)shape.x;
        if (axis < 0 || axis > 2) throw new InvalidOperationException("Invalid native capsule axis");
        var offset = Vector3.zero;
        offset[axis] = shape.y * .5f - radiusA;
        a = center - offset;
        b = center + offset;
    }

    public static QuartzHairSetting NativeConvertQuartz(QuartzHairSetting source)
    {
        var s = JsonUtility.FromJson<QuartzHairSetting>(JsonUtility.ToJson(source));
        s.headTranslateCoefficient *= .01f;
        s.headTranslateCoefficient.z = -s.headTranslateCoefficient.z;
        ConvertQuartzLimits(ref s.headTranslateLimitMin, ref s.headTranslateLimitMax);
        s.headRotateCoefficient = -s.headRotateCoefficient;
        ConvertQuartzLimits(ref s.headRotateLimitMin, ref s.headRotateLimitMax);
        s.neckRotateCoefficient = -s.neckRotateCoefficient;
        ConvertQuartzLimits(ref s.neckRotateLimitMin, ref s.neckRotateLimitMax);
        return s;
    }

    static void ConvertQuartzLimits(ref Vector3 minimum, ref Vector3 maximum)
    {
        var oldMinimum = minimum;
        minimum.y = -maximum.y; minimum.z = -maximum.z;
        maximum.y = -oldMinimum.y; maximum.z = -oldMinimum.z;
    }

    void BuildChainLayers(ChainLayerSetting[] settings, Dictionary<string, Transform> transforms)
    {
        if (settings == null) return;
        foreach (var setting in settings)
        {
            if (setting == null || !setting.active || setting.bones == null) continue;
            var layerNodes = new List<Node>();
            foreach (var name in setting.bones)
            {
                var node = nodes.FirstOrDefault(n => n.child.name == name);
                if (node != null) layerNodes.Add(node);
            }
            if (layerNodes.Count < (setting.around ? 3 : 2)) continue;
            var points = layerNodes.Select(n => RootPoint(n.child.position)).ToArray();
            float loop = 0f;
            if (setting.around)
                for (int i = 0; i < points.Length; i++) loop += Vector3.Distance(points[i], points[(i + 1) % points.Length]);
            chainLayers.Add(new ChainLayer { nodes = layerNodes.ToArray(), around = setting.around, radius = setting.radius, smoothing = setting.smoothing, initialLoopLength = loop });
        }
        chainLayers.Sort((a, b) => a.nodes[0].depth.CompareTo(b.nodes[0].depth));
    }

    bool IsFrontStrand(string name)
    {
        return !string.IsNullOrEmpty(name) && frontStrandPrefixes != null
            && frontStrandPrefixes.Any(prefix => !string.IsNullOrEmpty(prefix)
                && name.StartsWith(prefix, StringComparison.Ordinal));
    }

    static int Depth(Transform t) { int d = 0; while (t.parent) { d++; t = t.parent; } return d; }

    public void SetSimulation(bool value) { simulate = value; RestorePose(); RequestReset(); }
    public void RequestReset() => needsReset = true;

    public void ApplyQuartzHairDrivers()
    {
        if (!enableQuartzHair || !head || !neck || quartzHairDrivers.Count == 0) return;
        foreach (var driver in quartzHairDrivers)
        {
            NativeQuartzCalculate(head.localRotation, neck.localRotation, driver.setting, out var position, out var rotation);
            driver.driven.localPosition = position;
            driver.driven.localRotation = rotation;
        }
    }

    public static void NativeQuartzCalculate(Quaternion headQ, Quaternion neckQ, QuartzHairSetting s,
        out Vector3 position, out Quaternion rotation)
    {
        var headBend = EulerToBendrollYRoll(ToEulerNative(headQ, 2) * Mathf.Rad2Deg);
        var neckBend = EulerToBendrollYRoll(ToEulerNative(neckQ, 2) * Mathf.Rad2Deg);
        headBend = new Vector3(headBend.y, headBend.x, headBend.z);
        neckBend = new Vector3(neckBend.y, neckBend.x, neckBend.z);
        var translation = ExchangeAxis(new Vector3(
            SingleCoefficient(FormatRangeDegree(headBend.x), s.headTranslateCoefficient.x, s.headTranslateLimitMin.x, s.headTranslateLimitMax.x),
            SingleCoefficient(FormatRangeDegree(headBend.y), s.headTranslateCoefficient.y, s.headTranslateLimitMin.y, s.headTranslateLimitMax.y),
            SingleCoefficient(FormatRangeDegree(headBend.z), s.headTranslateCoefficient.z, s.headTranslateLimitMin.z, s.headTranslateLimitMax.z)), s.translateConnectionAxis);
        var headRotation = new Vector3(
            SingleCoefficient(FormatRangeDegree(headBend.x), s.headRotateCoefficient.x, s.headRotateLimitMin.x, s.headRotateLimitMax.x),
            SingleCoefficient(FormatRangeDegree(headBend.y), s.headRotateCoefficient.y, s.headRotateLimitMin.y, s.headRotateLimitMax.y),
            SingleCoefficient(FormatRangeDegree(headBend.z), s.headRotateCoefficient.z, s.headRotateLimitMin.z, s.headRotateLimitMax.z));
        var neckRotation = new Vector3(
            SingleCoefficient(FormatRangeDegree(neckBend.x), s.neckRotateCoefficient.x, s.neckRotateLimitMin.x, s.neckRotateLimitMax.x),
            SingleCoefficient(FormatRangeDegree(neckBend.y), s.neckRotateCoefficient.y, s.neckRotateLimitMin.y, s.neckRotateLimitMax.y),
            SingleCoefficient(FormatRangeDegree(neckBend.z), s.neckRotateCoefficient.z, s.neckRotateLimitMin.z, s.neckRotateLimitMax.z));

        var axisRotation = (headRotation + neckRotation) * Mathf.Deg2Rad;
        var composed = AxisrotationToQuaternionWithBendRoll(axisRotation.x, axisRotation.y, axisRotation.z);
        var euler = ExchangeAxis(ToEulerNative(composed, s.rotationOrder), s.rotateConnectionAxis) * Mathf.Rad2Deg;
        position = translation;
        rotation = Quaternion.Euler(euler);
    }

    static float SingleCoefficient(float degree, float coefficient, float minimum, float maximum)
        => Mathf.Clamp(degree, minimum, maximum) * coefficient;

    static float FormatRangeDegree(float degree)
    {
        if (degree > 180f) return degree - 360f;
        if (degree < -180f) return degree + 360f;
        return degree;
    }

    static Vector3 ExchangeAxis(Vector3 value, int order)
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

    static Vector3 ThreeAxisRot(float r11, float r12, float r21, float r31, float r32)
        => new Vector3(-Mathf.Atan2(r31, r32), -Mathf.Asin(Mathf.Clamp(r21, -1f, 1f)), -Mathf.Atan2(r11, r12));

    static Vector3 ToEulerNative(Quaternion q, int rotationOrder)
    {
        float x = q.x, y = q.y, z = q.z, w = -q.w;
        if (rotationOrder == 2)
        {
            float common = w * w - x * x + y * y - z * z;
            var t = ThreeAxisRot(
                2f * (w * y + z * x), w * w - x * x - y * y + z * z,
                2f * (w * x - y * z),
                2f * (w * z + x * y), common);
            return new Vector3(t.y, t.z, t.x);
        }

        var xyz = ThreeAxisRot(
            2f * (w * x - y * z), w * w - x * x - y * y + z * z,
            2f * (w * y + x * z),
            2f * (w * z - x * y), w * w + x * x - y * y - z * z);
        return new Vector3(xyz.z, xyz.y, xyz.x);
    }

    static Vector3 EulerToBendrollYRoll(Vector3 eulerDegrees)
    {
        var half = eulerDegrees * (Mathf.Deg2Rad * .5f);
        float sx = Mathf.Sin(half.x), cx = Mathf.Cos(half.x);
        float sy = Mathf.Sin(half.y), cy = Mathf.Cos(half.y);
        float sz = Mathf.Sin(half.z), cz = Mathf.Cos(half.z);
        float x = sx * cy * cz - sy * sz * cx;
        float y = sy * cx * cz + sx * sz * cy;
        float z = sz * cx * cy + sx * sy * cz;
        float w = cx * cy * cz - sy * sz * sx;

        float denominator = 2f - 2f * z * z - 2f * x * x;
        float bend = 2f * Mathf.Atan2(2f * z * y + 2f * x * w, denominator) * Mathf.Rad2Deg;
        float roll = -2f * Mathf.Atan2(2f * x * y - 2f * z * w, denominator) * Mathf.Rad2Deg;
        return new Vector3(eulerDegrees.y, bend, roll);
    }

    static Quaternion AxisrotationToQuaternionWithBendRoll(float roll, float pitch, float yaw)
    {
        float pitchTangent = Mathf.Tan(pitch * .5f);
        float yawTangent = Mathf.Tan(-yaw * .5f);
        float scale = 2f / (pitchTangent * pitchTangent + yawTangent * yawTangent + 1f);
        var direction = new Vector3(scale - 1f, scale * yawTangent, scale * pitchTangent);
        var bend = Quaternion.FromToRotation(Vector3.right, direction);
        var rollRotation = Quaternion.AngleAxis(-roll * Mathf.Rad2Deg, Vector3.right);
        return rollRotation * bend;
    }

    public void CapturePose()
    {
        if (!initialized) Initialize();
        foreach (var node in nodes)
        {
            node.poseLocalRotation = node.bone.localRotation;
            node.poseLocalPosition = node.bone.localPosition;
            node.childLocalPosition = node.child.localPosition;
            node.childLocalRotation = node.child.localRotation;
            node.poseRootRotation = Quaternion.Inverse(transform.rotation) * node.bone.rotation;
            node.poseParentRootRotation = node.bone.parent
                ? Quaternion.Inverse(transform.rotation) * node.bone.parent.rotation
                : Quaternion.identity;
        }
        restoredFrame = Time.frameCount;
    }

    public void RestorePose()
    {
        if (!initialized) Initialize();
        foreach (var node in nodes)
        {
            node.bone.localPosition = node.poseLocalPosition;
            node.bone.localRotation = node.poseLocalRotation;
        }
        restoredFrame = Time.frameCount;
    }

    public bool ResetPending => needsReset;
    public Transform[] SimulationBones() => nodes.Select(n=>n.bone).ToArray();

    public Vector3[] TipPositions() => nodes.Select(n => n.child.position).ToArray();



    public void Step(float deltaTime)
    {
        if (!initialized) Initialize();
        ResetMetrics();
        if (!simulate) { RestorePose(); needsReset = true; return; }
        if (deltaTime <= 0f) return;
        if (restoredFrame != Time.frameCount) CapturePose();

        foreach (var node in nodes)
        {
            node.baselineTip = RootPoint(node.child.position);
            node.hitCheckCount = 0;
        }

        var rootNow = RootPoint(rootMotionSource.position);
        bool prewarm = needsReset && prewarmSteps > 0;
        if (needsReset)
        {
            ResetState(rootNow);
            needsReset = false;
            ResetCount++;
        }
        simulationTime += deltaTime;
        int begin = 0;
        while (begin < nodes.Count)
        {
            int end = begin + 1;
            while (end < nodes.Count && nodes[end].depth == nodes[begin].depth) end++;
            for (int i = begin; i < end; i++) IntegrateNode(nodes[i], rootNow, prewarm);
            ProcessChainLayers(nodes[begin].depth);
            for (int i = begin; i < end; i++)
            {
                var n = nodes[i];
                DeriveRotation(n, n.selfPosition, n.selfRotation, n.selfRotation * n.axis);
                n.position = n.selfPosition + NativeRotate(n.selfRotation, n.childLocalPosition);
                if (nodeByBone.TryGetValue(n.child, out var downstream))
                {
                    downstream.selfPosition = n.position;
                    downstream.selfRotation = NativeMultiply(n.selfRotation, n.childLocalRotation);
                }
            }
            begin = end;
        }

        FinalizeMetrics();
        lastRootPosition = rootNow;

        if (!IsFinite)
        {
            RestorePose();
            RequestReset();
            Debug.LogError("ACTOR_SWING_NONFINITE");
        }
    }

    void ResetState(Vector3 rootPosition)
    {
        foreach (var node in nodes)
        {
            var tip = RootPoint(node.child.position);
            node.position = node.previousPosition = tip;
            node.speed = Vector3.zero;
            node.selfRotation = node.poseRootRotation;
            node.selfPosition = RootPoint(node.bone.position);
            node.prewarmCacheCount = 0;
        }
        simulationTime = 0f;
        lastRootPosition = rootPosition;
    }

    void IntegrateNode(Node node, Vector3 hipsTranslation, bool prewarm)
    {
        var rootSetting = node.limitSetting ?? node.setting;
        node.rootCancel = NativeRootCancel(hipsTranslation, manage.rootWeight,
            manage.rootHorizontalWeight, manage.rootVerticalWeight,
            rootSetting.RootHorizontal, rootSetting.RootVertical);
        if (node.parentLink == null)
        {
            node.selfPosition = RootPoint(node.bone.position) - node.rootCancel;
            node.selfRotation = node.poseRootRotation;
            node.poseParentRootRotation = node.poseRootRotation;
        }
        else
            node.poseParentRootRotation = NativeMultiply(node.parentLink.selfRotation, node.poseLocalRotation);
        node.defaultRotation = node.selfRotation;
        var anchor = node.selfPosition;
        var childDefault = NativeAdd(anchor, NativeRotate(node.selfRotation, node.childLocalPosition));
        if (prewarm) { node.position = childDefault; node.speed = Vector3.zero; }
        float remaining = NativeStep * (prewarm ? prewarmSteps * .5f : 1f);
        while (remaining > 0f)
        {
            float step = F32(Mathf.Min(remaining, NativeStep) * 40f);
            var poseWorld = PoseRootRotation(node);
            var restDirection = NativeRotate(poseWorld, node.axis);
            var current = node.position;
            float retain = F32(1f - node.setting.damping);
            var force = CalcStiffnessPendulum(node, anchor, poseWorld, current, childDefault);
            force = NativeAdd(force, NativeScale(NativeSubtract(childDefault, current), F32(retain * retain)));
            force.y = F32(force.y - F32(node.setting.mass * .01f));
            if (!prewarm)
                force = NativeAdd(force, NativeScale(node.speed, node.setting.spring));
            if (enableWind && node.setting.useWindGlobalForce != 0f)
                force += CalcWindPower(current, simulationTime,
                    node.setting.wind);

            if (!prewarm && node.setting.dynamicType == DynamicType.Slide)
                force = ApplySlideAxisCoupling(node, poseWorld, force);

            node.speed = NativeScale(NativeScale(force, step), manage.swingPowerWeight);
            var next = NativeAdd(current, node.speed);
            if (node.setting.dynamicType == DynamicType.Swing)
            {
                node.selfRotation = NativeMultiply(NativeFromToRotation(restDirection, NativeSubtract(next, anchor)), node.defaultRotation);
                poseWorld = node.selfRotation;
                next = ConstrainLength(next, anchor, restDirection, node.length);
            }
            if (enableCollision)
                next = CheckDynamicCollision(node, next + node.rootCancel,
                    anchor + node.rootCancel, poseWorld) - node.rootCancel;

            node.previousPosition = current;
            node.position = next;
            IsFinite &= Finite(node.position) && Finite(node.speed);
            remaining -= NativeStep;
            if (prewarm && CacheAlreadyStable(node)) break;
        }
    }

    static bool CacheAlreadyStable(Node node)
    {
        node.prewarmCache[node.prewarmCacheCount++ % 5] = node.position;
        if (node.prewarmCacheCount < 5) return false;
        float sum = 0f;
        for (int i = 0; i < 5; i++)
            sum += (node.prewarmCache[i] - node.prewarmCache[(i + 1) % 5]).sqrMagnitude;
        return sum < 1e-6f;
    }

    public static Vector3 NativeRootCancel(Vector3 hips, float weight,
        float horizontal, float vertical, float boneHorizontal, float boneVertical)
        => new Vector3(hips.x * (1f - weight * horizontal * boneHorizontal),
            hips.y * (1f - weight * vertical * boneVertical),
            hips.z * (1f - weight * horizontal * boneHorizontal));

    Vector3 CorrectedAnchor(Node node, Vector3 rootDelta)
    {
        return node.selfPosition;
    }

    Quaternion PoseRootRotation(Node node) => node.selfRotation;

    static Vector3 CalcStiffnessPendulum(Node node, Vector3 anchor, Quaternion selfRotation, Vector3 child, Vector3 childDefault)
    {
        var setting = node.setting;
        float p = 0f;
        Vector3 delta;
        if (setting.dynamicType == DynamicType.Swing)
        {
            delta = NativeRotate(selfRotation, node.axis);
            if (setting.pendulum > 1e-5f && setting.pendulumRange > 1e-5f)
            {
                float denominator = F32(NativeMagnitude(child) * NativeMagnitude(childDefault));
                float cosine = denominator > 1e-9f ? F32(Mathf.Abs(NativeDot(child, childDefault)) / denominator) : 0f;
                p = F32(F32(F32(1f / setting.pendulumRange) * Mathf.Max(0f, F32(cosine - F32(1f - setting.pendulumRange)))) * setting.pendulum);
            }
        }
        else
        {
            var target = anchor + NativeRotate(selfRotation, node.axis) * node.length;
            delta = target - child;
            if (setting.pendulum > 1e-5f && setting.pendulumRange > 1e-5f)
            {
                float distance = NativeMagnitude(childDefault - child);
                p = F32(F32(1f - F32(Mathf.Min(F32(distance * 10f), setting.pendulumRange) / setting.pendulumRange)) * setting.pendulum);
            }
        }
        return NativeScale(delta, F32(setting.stiffness - p));
    }

    Vector3 ApplySlideAxisCoupling(Node node, Quaternion selfRotation, Vector3 force)
    {
        var local = Quaternion.Inverse(selfRotation) * force;
        local.y += node.setting.axisAddXToY * SignZero(local.y) * Mathf.Abs(local.x);
        local.z += node.setting.axisAddXToZ * SignZero(local.z) * Mathf.Abs(local.x);
        return selfRotation * local;
    }

    static float SignZero(float value) => value > 0f ? 1f : value < 0f ? -1f : 0f;

    Vector3 CalcWindPower(Vector3 jointPosition, float time, float scale)
    {
        var world = transform.TransformPoint(jointPosition);
        var wp = new float3(world.x, world.y, world.z);
        float wave = manage.windWaveAmplitude
            * Mathf.Sin(2f * Mathf.PI * (manage.windWaveFrequency * time + manage.windWavePhase));
        float fluctuation = noise.snoise(wp * (manage.windFluctuation * time));
        float basePower = wave + manage.windPower * (fluctuation * .5f + .5f);
        var envelope = manage.globalForce + Vector3.one * basePower;

        envelope.x = Mathf.Max(0f, envelope.x - manage.windRangeMin);
        envelope.y = Mathf.Max(0f, envelope.y - manage.windRangeMin);
        envelope.z = Mathf.Max(0f, envelope.z - manage.windRangeMin);
        float range = Mathf.Sin(2f * Mathf.PI * (manage.windRangeFrequency * time + manage.windRangePhase)) + 1f;
        envelope.x = Mathf.Min(manage.windRangeMax, envelope.x * range + manage.windRangeMin);
        envelope.y = Mathf.Min(manage.windRangeMax, envelope.y * range + manage.windRangeMin);
        envelope.z = Mathf.Min(manage.windRangeMax, envelope.z * range + manage.windRangeMin);

        float random = noise.snoise(wp * (manage.randomFluctuation * time)) * manage.randomPower;
        envelope += Vector3.one * random;
        var worldWind = Vector3.Scale(envelope, manage.windDirection);
        return RootDirection(worldWind) * (scale * EffectiveWindStrength);
    }

    Vector3 CheckDynamicCollision(Node node, Vector3 childPosition, Vector3 selfPosition, Quaternion selfRotation)
    {
        float dynamicRadius = Mathf.Max(0f, node.setting.collisionRadius);
        var colliderCenter = childPosition + selfRotation * node.setting.collisionOffset;
        int hits = 0;
        foreach (var collider in bodyColliders)
        {
            if (collider == null || !collider.anchor || collider.type == ColliderType.None) continue;
            if ((node.setting.collisionMask & collider.collisionMask) == 0) continue;
            if (!CheckSphereCollision(colliderCenter, dynamicRadius, collider, out var hit)) continue;
            hits++;
            var contactCenter = collider.type == ColliderType.Plane
                ? colliderCenter + hit.normal * hit.value
                : hit.position + hit.normal * hit.value;
            childPosition = contactCenter;
            childPosition = ConstrainLength(childPosition, selfPosition, selfRotation * node.axis, node.length);
            colliderCenter = childPosition + selfRotation * node.setting.collisionOffset;
        }
        foreach (var collider in bodyColliders)
        {
            if (collider == null || !collider.anchor || collider.type == ColliderType.None) continue;
            if ((node.setting.collisionMask & collider.collisionMask) == 0) continue;
            if (!CheckSphereCollision(colliderCenter, dynamicRadius, collider, out var residual)) continue;
            float residualPenetration = HitPenetration(colliderCenter, collider, residual);
            if (residualPenetration > MaxPenetration)
            {
                MaxPenetration = residualPenetration;
                MaxPenetrationNode = node.bone.name;
                MaxPenetrationCollider = collider.source + ":" + collider.anchor.name + ":" + collider.type;
            }
            if (node.frontStrand) FrontStaticPenetration = Mathf.Max(FrontStaticPenetration, residualPenetration);
        }
        node.hitCheckCount += hits;
        return childPosition;
    }

    bool CheckSphereCollision(Vector3 center, float radius, BodyCollider collider, out HitInfo hit)
    {
        hit = default;
        var a = RootPoint(collider.anchor.TransformPoint(collider.start));
        float radiusA = Mathf.Max(0f, collider.radius);
        if (collider.type == ColliderType.Sphere)
            return SphereHit(center, radius, a, radiusA, out hit);
        if (collider.type == ColliderType.Plane)
        {
            var normal = RootDirection(collider.anchor.TransformDirection(collider.planeNormal)).normalized;
            float signed = Vector3.Dot(center - a, normal);
            if (signed >= radius) return false;
            hit.normal = normal;
            hit.position = center;
            hit.value = radius - signed;
            return true;
        }

        Vector3 b;
        if (collider.type == ColliderType.Line)
        {
            a = RootPoint(collider.anchor.position);
            b = RootPoint(collider.anchor.parent ? collider.anchor.parent.position : collider.anchor.position);
            var abFull = b - a;
            float length = abFull.magnitude;
            if (length > .01f)
            {
                var direction = abFull / length;
                float scale = RootDirection(collider.anchor.TransformVector(Vector3.right)).magnitude;
                float insetA = Mathf.Max(0f, collider.lineInsets.x) * scale;
                float insetB = Mathf.Max(0f, collider.lineInsets.y) * scale;
                float insetSum = insetA + insetB;
                if (insetSum > length && insetSum > 1e-9f)
                {
                    float fit = length / insetSum;
                    insetA *= fit;
                    insetB *= fit;
                }
                a += direction * insetA;
                b -= direction * insetB;
            }
        }
        else b = RootPoint(collider.anchor.TransformPoint(collider.end));
        float radiusB = collider.endRadius >= 0f ? collider.endRadius : radiusA;
        var ab = b - a;
        float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector3.Dot(center - a, ab) / ab.sqrMagnitude) : 0f;
        var nearest = a + ab * t;
        return SphereHit(center, radius, nearest, ((1f - t) * radiusA + t * radiusB), out hit);
    }

    static bool SphereHit(Vector3 center, float radius, Vector3 staticCenter, float staticRadius, out HitInfo hit)
    {
        hit = default;
        float sum = radius + staticRadius;
        var delta = center - staticCenter;
        float square = delta.sqrMagnitude;
        if (square > sum * sum) return false;
        hit.normal = square > 1e-10f ? NativeDivide(delta, Mathf.Sqrt(square)) : Vector3.zero;
        hit.position = staticCenter;
        hit.value = sum;
        return true;
    }

    static float HitPenetration(Vector3 center, BodyCollider collider, HitInfo hit)
    {
        return collider.type == ColliderType.Plane
            ? Mathf.Max(0f, hit.value)
            : Mathf.Max(0f, hit.value - Vector3.Distance(center, hit.position));
    }

    static Vector3 ConstrainLength(Vector3 position, Vector3 anchor, Vector3 fallbackDirection, float length)
    {
        var delta = NativeSubtract(position, anchor);
        float square = NativeDot(delta,delta);
        if (square <= 9.99999944e-11f) return anchor;
        return NativeAdd(anchor, NativeScale(NativeDivide(delta, Mathf.Sqrt(square)), length));
    }

    void ProcessDynamicBoneLayers()
    {
        foreach (var node in nodes)
        {
            var anchor = CorrectedAnchor(node, Vector3.zero);
            var selfRotation = node.selfRotation;
            DeriveRotation(node, anchor, selfRotation, selfRotation * node.axis);
        }
    }

    void DeriveRotation(Node node, Vector3 correctedAnchor, Quaternion poseWorld, Vector3 restDirection)
    {
        var direction = node.position - correctedAnchor;
        if (direction.sqrMagnitude <= 1e-12f) direction = restDirection;
        var limit = node.limitSetting ?? node.setting;
        node.selfRotation = NativeSwingRotation(poseWorld, node.defaultRotation, node.poseParentRootRotation,
            node.axis, direction, limit.limit, limit.minAngles, limit.maxAngles, node.referenceBone && limit.limit ? 1f : strength * manage.weight);
        if (node.referenceBone && limit.limit) {
            var reference = node.referenceNode != null ? node.referenceNode.selfRotation
                : Quaternion.Inverse(transform.rotation) * node.referenceBone.rotation;
            var constrained = NativeReferenceLimit(node.selfRotation, reference, limit.referenceMin, limit.referenceMax);
            var q = math.slerp(new quaternion(node.defaultRotation.x,node.defaultRotation.y,node.defaultRotation.z,node.defaultRotation.w),
                new quaternion(constrained.x,constrained.y,constrained.z,constrained.w),strength * manage.weight).value;
            node.selfRotation = new Quaternion(q.x,q.y,q.z,q.w);
        }
        var solvedWorld = transform.rotation * node.selfRotation;
        node.bone.localRotation = node.bone.parent
            ? Quaternion.Inverse(node.bone.parent.rotation) * solvedWorld
            : solvedWorld;
    }

    public static Quaternion NativeReferenceLimit(Quaternion solved, Quaternion reference, Vector3 minimum, Vector3 maximum)
    {
        var a=ToUnityEuler(solved);var b=ToUnityEuler(reference);
        for(int i=0;i<3;i++) {
            if(maximum[i]!=0)a[i]=Mathf.Min(a[i],b[i]);
            if(minimum[i]!=0)a[i]=Mathf.Max(a[i],b[i]);
        }
        return Quaternion.Euler(a);
    }

    public static Quaternion NativeSwingRotation(Quaternion poseWorld, Quaternion defaultRotation,
        Quaternion parentRotation, Vector3 axis, Vector3 direction, bool useLimit,
        Vector3 minAngles, Vector3 maxAngles, float weight)
    {
        var restDirection = NativeRotate(poseWorld, axis);
        var solvedRootRotation = NativeMultiply(NativeFromToRotation(restDirection, direction), poseWorld);
        if (useLimit)
        {
            var local = NativeMultiply(Quaternion.Inverse(parentRotation), solvedRootRotation);
            var angles = ToUnityEuler(local);
            angles.x = Mathf.Clamp(angles.x, minAngles.x, maxAngles.x);
            angles.y = Mathf.Clamp(angles.y, minAngles.y, maxAngles.y);
            angles.z = Mathf.Clamp(angles.z, minAngles.z, maxAngles.z);
            local = Quaternion.Euler(angles);
            solvedRootRotation = NativeMultiply(parentRotation, local);
        }
        var result = math.slerp(new quaternion(defaultRotation.x, defaultRotation.y, defaultRotation.z, defaultRotation.w),
            new quaternion(solvedRootRotation.x, solvedRootRotation.y, solvedRootRotation.z, solvedRootRotation.w), weight).value;
        return new Quaternion(result.x, result.y, result.z, result.w);
    }

    public static Quaternion NativeFromToRotation(Vector3 from, Vector3 to)
    {
        var a = NativeNormalize(from); var b = NativeNormalize(to);
        float dot = NativeDot(a, b);
        if (dot >= 1f) return Quaternion.identity;
        Vector3 axis;
        float angle;
        if (dot <= -1f)
        {
            axis = NativeCross(from, Vector3.right);
            if (axis.sqrMagnitude < 1e-10f) axis = NativeCross(from, Vector3.up);
            angle = Mathf.PI;
        }
        else { axis = NativeNormalize(NativeCross(from, to)); angle = Mathf.Acos(dot); }
        float sine = Mathf.Sin(angle * .5f);
        return new Quaternion(axis.x * sine, axis.y * sine, axis.z * sine, Mathf.Cos(angle * .5f));
    }

    [ThreadStatic] static float[] nativeFloatScratch;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static float F32(float value)
    {
        if (nativeFloatScratch == null) nativeFloatScratch = new float[1];
        System.Threading.Volatile.Write(ref nativeFloatScratch[0], value);
        return System.Threading.Volatile.Read(ref nativeFloatScratch[0]);
    }
    static float NativeDot(Vector3 a, Vector3 b)
        => F32(F32(F32(a.x*b.x)+F32(a.y*b.y))+F32(a.z*b.z));
    static float NativeMagnitude(Vector3 value) => Mathf.Sqrt(NativeDot(value,value));
    static Vector3 NativeCross(Vector3 a,Vector3 b)
        => new Vector3(F32(F32(a.y*b.z)-F32(a.z*b.y)),F32(F32(a.z*b.x)-F32(a.x*b.z)),F32(F32(a.x*b.y)-F32(a.y*b.x)));
    static Vector3 NativeNormalize(Vector3 value)
    {
        float square=NativeDot(value,value);
        if(square<=1.17549435e-38f)return Vector3.zero;
        float inverse=F32(1f/Mathf.Sqrt(square));
        return new Vector3(F32(value.x*inverse),F32(value.y*inverse),F32(value.z*inverse));
    }
    static Vector3 NativeDivide(Vector3 value, float divisor)
        => new Vector3(F32(value.x / divisor), F32(value.y / divisor), F32(value.z / divisor));
    static Vector3 NativeAdd(Vector3 a, Vector3 b) => new Vector3(F32(a.x+b.x), F32(a.y+b.y), F32(a.z+b.z));
    static Vector3 NativeSubtract(Vector3 a, Vector3 b) => new Vector3(F32(a.x-b.x), F32(a.y-b.y), F32(a.z-b.z));
    static Vector3 NativeScale(Vector3 a, float b) => new Vector3(F32(a.x*b), F32(a.y*b), F32(a.z*b));

    public static Vector3 NativeRotate(Quaternion q, Vector3 v)
    {
        var t = NativeCross(new Vector3(q.x,q.y,q.z), v) * 2f;
        var c = NativeCross(new Vector3(q.x,q.y,q.z), t);
        return new Vector3(F32(c.x+F32(F32(t.x*q.w)+v.x)),
            F32(c.y+F32(F32(t.y*q.w)+v.y)),F32(c.z+F32(F32(t.z*q.w)+v.z)));
    }

    public static Quaternion NativeMultiply(Quaternion a, Quaternion b)
        => new Quaternion(F32(F32(F32(F32(b.z*a.y)+F32(b.w*a.x))+F32(b.x*a.w))-F32(b.y*a.z)),
            F32(F32(F32(F32(b.x*a.z)+F32(b.w*a.y))+F32(b.y*a.w))-F32(b.z*a.x)),
            F32(F32(F32(F32(b.y*a.x)+F32(b.w*a.z))+F32(b.z*a.w))-F32(b.x*a.y)),
            F32(F32(-F32(F32(b.y*a.y)+F32(b.x*a.x))+F32(b.w*a.w))-F32(b.z*a.z)));

    static Vector3 ToUnityEuler(Quaternion q)
    {
        float x = q.x, y = q.y, z = q.z, w = q.w;
        float ex = Mathf.Asin(Mathf.Clamp(2f * (w * x - y * z), -1f, 1f));
        float ey = Mathf.Atan2(2f * (w * y + x * z), 1f - 2f * (x * x + y * y));
        float ez = Mathf.Atan2(2f * (w * z + x * y), 1f - 2f * (x * x + z * z));
        return new Vector3(ex, ey, ez) * Mathf.Rad2Deg;
    }

    void ProcessChainLayers(int depth)
    {
        foreach (var layer in chainLayers)
        {
            if (layer.nodes[0].depth != depth) continue;
            ProcessChainColliders(layer);
            if (!enableChainSmoothing || !layer.around || layer.smoothing < 1e-5f) continue;
            int count = layer.nodes.Length;
            var before = new Vector3[count];
            var after = new Vector3[count];
            for (int i = 0; i < count; i++) before[i] = layer.nodes[i].position;
            for (int i = 0; i < count; i++)
            {
                var current = before[i];
                bool hasPrevious = layer.around || i > 0;
                bool hasNext = layer.around || i + 1 < count;
                var correction = Vector3.zero;
                int neighbours = 0;
                if (hasPrevious) { correction += before[(i + count - 1) % count] - current; neighbours++; }
                if (hasNext) { correction += before[(i + 1) % count] - current; neighbours++; }
                after[i] = neighbours > 0 ? current + layer.smoothing * correction / neighbours : current;
            }
            float loop = 0f;
            int links = layer.around ? count : count - 1;
            for (int i = 0; i < links; i++) loop += Vector3.Distance(after[i], after[(i + 1) % count]);
            if (loop > 1e-9f && loop < layer.initialLoopLength)
            {
                var center = Vector3.zero;
                for (int i = 0; i < count; i++) center += after[i];
                center /= count;
                float scale = layer.initialLoopLength / loop;
                for (int i = 0; i < count; i++) after[i] = center + (after[i] - center) * scale;
            }
            for (int i = 0; i < count; i++)
            {
                var node = layer.nodes[i];
                var anchor = CorrectedAnchor(node, Vector3.zero);
                var pose = PoseRootRotation(node);
                var rest = pose * node.axis;
                node.position = ConstrainLength(after[i], anchor, rest, node.length);
                if (enableCollision) node.position = CheckDynamicCollision(node, node.position, anchor, pose);
            }
            ProcessChainColliders(layer);
        }
    }

    void ProcessChainColliders(ChainLayer layer)
    {
        if (!enableCollision || layer.nodes == null || layer.nodes.Length < 2 || layer.radius <= 0f) return;
        int count = layer.nodes.Length;
        int links = layer.around ? count : count - 1;
        for (int link = 0; link < links; link++)
        {
            int ia = link;
            int ib = (link + 1) % count;
            var a = layer.nodes[ia];
            var b = layer.nodes[ib];
            int mask = a.setting.collisionMask & b.setting.collisionMask;
            var positionA = a.position + a.rootCancel;
            var positionB = b.position + b.rootCancel;
            foreach (var collider in bodyColliders)
            {
                if (collider == null || !collider.anchor || collider.type == ColliderType.None) continue;
                if ((mask & collider.collisionMask) == 0) continue;
                if (!CheckCapsuleCollision(positionA, positionB, layer.radius, collider, out var hit)) continue;

                var target = hit.position + hit.normal * hit.value;
                var axis = positionB - positionA;
                if (axis.sqrMagnitude > 1e-10f)
                {
                    axis = NativeNormalize(axis);
                    var offset = target - positionA;
                    offset -= axis * Vector3.Dot(axis, offset);
                    positionA += offset;
                    positionB += offset;
                }
                positionA = ProjectChainEnd(a, positionA - a.rootCancel) + a.rootCancel;
                positionB = ProjectChainEnd(b, positionB - b.rootCancel) + b.rootCancel;
                a.hitCheckCount++;
                b.hitCheckCount++;
            }
            a.position = positionA - a.rootCancel;
            b.position = positionB - b.rootCancel;
        }
    }

    Vector3 ProjectChainEnd(Node node, Vector3 position)
    {
        var anchor = CorrectedAnchor(node, Vector3.zero);
        var pose = PoseRootRotation(node);
        return ConstrainLength(position, anchor, pose * node.axis, node.length);
    }

    bool CheckCapsuleCollision(Vector3 dynamicA, Vector3 dynamicB, float dynamicRadius, BodyCollider collider, out HitInfo hit)
    {
        hit = default;
        var staticA = RootPoint(collider.anchor.TransformPoint(collider.start));
        float staticRadiusA = Mathf.Max(0f, collider.radius);
        if (collider.type == ColliderType.Plane)
        {
            var normal = RootDirection(collider.anchor.TransformDirection(collider.planeNormal)).normalized;
            float da = Vector3.Dot(dynamicA - staticA, normal);
            float db = Vector3.Dot(dynamicB - staticA, normal);
            float distance = Mathf.Min(da, db);
            if (distance > dynamicRadius) return false;
            hit.normal = normal;
            hit.position = da <= db ? dynamicA : dynamicB;
            hit.value = dynamicRadius - distance;
            return true;
        }

        Vector3 staticB;
        float staticRadiusB;
        if (collider.type == ColliderType.Sphere)
        {
            staticB = staticA;
            staticRadiusB = staticRadiusA;
        }
        else if (collider.type == ColliderType.Line)
        {
            staticA = RootPoint(collider.anchor.position);
            staticB = RootPoint(collider.anchor.parent ? collider.anchor.parent.position : collider.anchor.position);
            var full = staticB - staticA;
            float length = full.magnitude;
            if (length > .01f)
            {
                var direction = full / length;
                float scale = RootDirection(collider.anchor.TransformVector(Vector3.right)).magnitude;
                float insetA = Mathf.Max(0f, collider.lineInsets.x) * scale;
                float insetB = Mathf.Max(0f, collider.lineInsets.y) * scale;
                float insetSum = insetA + insetB;
                if (insetSum > length && insetSum > 1e-9f)
                {
                    float fit = length / insetSum;
                    insetA *= fit;
                    insetB *= fit;
                }
                staticA += direction * insetA;
                staticB -= direction * insetB;
            }
            staticRadiusB = collider.endRadius >= 0f ? collider.endRadius : staticRadiusA;
        }
        else
        {
            staticB = RootPoint(collider.anchor.TransformPoint(collider.end));
            staticRadiusB = collider.endRadius >= 0f ? collider.endRadius : staticRadiusA;
        }

        ClosestSegmentPoints(dynamicA, dynamicB, staticA, staticB, out var pointDynamic, out var pointStatic, out var tDynamic, out var tStatic);
        float radiusDynamic = dynamicRadius;
        float radiusStatic = ((1f - tStatic) * staticRadiusA + tStatic * staticRadiusB);
        var delta = pointDynamic - pointStatic;
        float sum = radiusDynamic + radiusStatic;
        float square = delta.sqrMagnitude;
        if (square > sum * sum) return false;
        hit.normal = square > 1e-10f ? NativeDivide(delta, Mathf.Sqrt(square)) : Vector3.zero;
        hit.position = pointStatic;
        hit.value = sum;
        return true;
    }

    static void ClosestSegmentPoints(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
        out Vector3 c1, out Vector3 c2, out float s, out float t)
    {
        const float epsilon = 1e-5f;
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);
        if (a <= epsilon && e <= epsilon) { s = t = 0f; c1 = p1; c2 = p2; return; }
        if (a <= epsilon) { s = 0f; t = Mathf.Clamp01(f / e); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= epsilon) { t = 0f; s = Mathf.Clamp01(-c / a); }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denominator = a * e - b * b;
                s = denominator > epsilon ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
            }
        }
        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    void ResetMetrics()
    {
        MaxDeviation = MaxLengthError = MaxPenetration = FrontStaticPenetration = 0f;
        MaxDynamicVelocity = FreeHairDeviation = FrontHairDeviation = FrontRootDrift = 0f;
        MaxPenetrationNode = MaxPenetrationCollider = null;
        IsFinite = true;
    }

    void FinalizeMetrics()
    {
        foreach (var node in nodes)
        {
            var actual = RootPoint(node.child.position);
            float deviation = Vector3.Distance(actual, node.baselineTip);
            MaxDeviation = Mathf.Max(MaxDeviation, deviation);
            MaxLengthError = Mathf.Max(MaxLengthError, Mathf.Abs(Vector3.Distance(RootPoint(node.bone.position), actual) - node.length));
            MaxDynamicVelocity = Mathf.Max(MaxDynamicVelocity, node.speed.magnitude);
            if (node.frontStrand)
            {
                FrontHairDeviation = Mathf.Max(FrontHairDeviation, deviation);
                if (node.setting.mass == 0f) FrontRootDrift = Mathf.Max(FrontRootDrift, Vector3.Distance(node.bone.localPosition, node.poseLocalPosition));
            }
            else FreeHairDeviation = Mathf.Max(FreeHairDeviation, deviation);
        }
    }

    Vector3 RootPoint(Vector3 worldPoint) => transform.InverseTransformPoint(worldPoint);
    Vector3 RootDirection(Vector3 worldDirection) => transform.InverseTransformDirection(worldDirection);
    static Vector3 Signed(Vector3 angles) => new Vector3(Mathf.DeltaAngle(0f, angles.x), Mathf.DeltaAngle(0f, angles.y), Mathf.DeltaAngle(0f, angles.z));
    static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z)
        && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
}
}
