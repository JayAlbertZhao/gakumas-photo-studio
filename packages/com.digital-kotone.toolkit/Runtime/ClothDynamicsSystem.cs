using System;
using System.Collections.Generic;
using System.Linq;
using ActorAnimation;
using UnityEngine;

namespace GakumasPhotoMode
{
    [DefaultExecutionOrder(920)]
    public sealed class ClothDynamicsSystem : MonoBehaviour
    {
        private const float CollisionSkin = 0.004f;
        private const float DampFactor = 40f;
        // Recovered ProcessDynamicBones multiplies serialized mass by 0.01 once
        // before fixed-step integration.  Keep skirt motion on the same unit
        // contract as the recovered general ActorSwing path.
        private const float MassScale = 0.01f;

        [Range(0f, 1.5f)] public float strength = 0.92f;
        [Range(0f, 2f)] public float gravityStrength = 0.78f;
        [Range(0f, 1.5f)] public float collisionStrength = 1f;

        private readonly List<Node> _nodes = new List<Node>();
        private readonly List<StaticColliderState> _colliders = new List<StaticColliderState>();
        private readonly List<QuartzSkirtState> _drivers = new List<QuartzSkirtState>();
        private readonly List<ChainLayerState> _chainLayers = new List<ChainLayerState>();
        private Vector3 _lastRootPosition;
        private bool _initialized;

        public int SimulatedBoneCount { get { return _nodes.Count; } }
        public int ColliderCount { get { return _colliders.Count; } }
        public int QuartzDriverCount { get { return _drivers.Count; } }
        public int CollisionCorrections { get; private set; }
        public int PeakCollisionCorrections { get; private set; }
        public float MeanAngularOffset { get; private set; }
        public float MaxAngularOffset { get; private set; }
        public float PeakMeanAngularOffset { get; private set; }
        public float PeakMaxAngularOffset { get; private set; }
        public float MinimumColliderClearance { get; private set; }
        public float WorstColliderClearance { get; private set; }

        public void Initialize(ICollection<Transform> activeBones = null)
        {
            _nodes.Clear();
            _colliders.Clear();
            _drivers.Clear();
            _chainLayers.Clear();

            foreach (ActorAnimationQuartzDriverSkirtBone driver in GetComponentsInChildren<ActorAnimationQuartzDriverSkirtBone>(true))
            {
                if (activeBones != null && !activeBones.Contains(driver.transform)) continue;
                driver.enabled = false;
                if (driver.setting == null) continue;
                // AssetBundle object references can point at the source prefab's
                // Transform after runtime instantiation. Resolve the leg inside this
                // instance by name rather than retaining that stale native handle.
                string referenceName = driver.transform.name.StartsWith("Left", StringComparison.Ordinal)
                    ? "LeftUpLeg"
                    : "RightUpLeg";
                Transform referenceBone = transform.Cast<Transform>()
                    .SelectMany(DescendantsAndSelf)
                    .FirstOrDefault(value => value.name == referenceName);
                if (referenceBone != null) _drivers.Add(new QuartzSkirtState(driver, referenceBone));
            }

            foreach (ActorSwingStaticBone value in GetComponentsInChildren<ActorSwingStaticBone>(true))
            {
                value.enabled = false;
                if (value.staticCollider == null) continue;
                string name = value.transform.name;
                if (name == "Hips" || name == "LeftUpLeg" || name == "RightUpLeg" || name == "LeftLeg" || name == "RightLeg")
                    _colliders.Add(new StaticColliderState(value));
            }

            foreach (ActorSwingDynamicBone setting in GetComponentsInChildren<ActorSwingDynamicBone>(true))
            {
                if (setting == null || setting.transform.name.IndexOf("Skirt", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (activeBones != null && !activeBones.Contains(setting.transform)) continue;
                setting.enabled = false;
                Transform child = setting.transform.Cast<Transform>()
                    .FirstOrDefault(value => value.name.IndexOf("Skirt", StringComparison.OrdinalIgnoreCase) >= 0);
                if (child == null || child.localPosition.sqrMagnitude < 0.0000001f) continue;
                QuartzSkirtState driver = _drivers.FirstOrDefault(value => value.transform == setting.transform);
                _nodes.Add(new Node(setting, child, HierarchyDepth(setting.transform), driver));
            }
            _nodes.Sort((left, right) => left.depth.CompareTo(right.depth));
            BuildChainLayers();
            _lastRootPosition = transform.position;
            _initialized = true;
            ResetSimulation();

            Debug.Log(string.Format(
                "[PhotoMode] Skirt dynamics ready: {0} simulated bones, {1} static colliders, {2} Quartz drivers, {3} authored chain layers",
                _nodes.Count, _colliders.Count, _drivers.Count, _chainLayers.Count));
        }

        public void ResetSimulation()
        {
            foreach (QuartzSkirtState driver in _drivers) driver.transform.localRotation = driver.restLocalRotation;
            foreach (Node node in _nodes)
            {
                // Quartz writes the authored leg-dependent base pose on each S1
                // skirt root. Swing is solved on top of that pose; resetting every
                // node to bind rotation here would silently erase all eight drivers.
                node.frameBaseLocalRotation = node.driver == null
                    ? node.restLocalRotation
                    : node.bone.localRotation;
                node.bone.localRotation = node.frameBaseLocalRotation;
                node.currentTip = node.child.position;
                node.previousTip = node.currentTip;
                node.childSpeed = Vector3.zero;
                node.lastOrigin = node.bone.position;
                node.ready = true;
            }
            CollisionCorrections = 0;
            PeakCollisionCorrections = 0;
            MeanAngularOffset = 0f;
            MaxAngularOffset = 0f;
            PeakMeanAngularOffset = 0f;
            PeakMaxAngularOffset = 0f;
            MinimumColliderClearance = float.PositiveInfinity;
            WorstColliderClearance = float.PositiveInfinity;
        }

        private void LateUpdate()
        {
            if (!_initialized || _nodes.Count == 0) return;
            float dt = Mathf.Clamp(Time.deltaTime, 1f / 120f, 1f / 30f);
            if ((transform.position - _lastRootPosition).sqrMagnitude > 0.20f) ResetSimulation();
            _lastRootPosition = transform.position;
            if (strength <= 0.001f)
            {
                ResetSimulation();
                return;
            }

            ApplyQuartzDrivers();
            int collisionCorrections = 0;
            float angleSum = 0f;
            float maxAngle = 0f;
            foreach (Node node in _nodes)
            {
                // Quartz is an authored procedural base pose and runs before Swing in
                // CampusActorAnimationJob.  Preserve its root result instead of erasing
                // it with the bind rotation at the beginning of the Swing pass.
                node.frameBaseLocalRotation = node.driver == null
                    ? node.restLocalRotation
                    : node.bone.localRotation;
                node.bone.localRotation = node.frameBaseLocalRotation;
                Vector3 origin = node.bone.position;
                Vector3 restTip = node.child.position;
                float length = Vector3.Distance(origin, restTip);
                if (length < 0.0001f) continue;
                Vector3 restDirection = (restTip - origin).normalized;
                if (!node.ready || (origin - node.lastOrigin).sqrMagnitude > 0.10f)
                {
                    node.currentTip = restTip;
                    node.previousTip = restTip;
                    node.ready = true;
                }

                float damping = Mathf.Clamp01(node.setting.damping);
                float velocityRetention = (1f - damping) * (1f - damping);
                Vector3 velocity = (node.currentTip - node.previousTip) * velocityRetention;
                float effectiveStiffness = CalcEffectiveStiffness(
                    node, restDirection, restTip);
                Vector3 stiffness = node.setting.dynamicType == 0
                    ? restDirection * effectiveStiffness
                    : (restTip - node.currentTip) * effectiveStiffness;
                Vector3 gravity = Vector3.down * Mathf.Max(0f, node.setting.mass) *
                    MassScale * gravityStrength;
                Vector3 acceleration = velocity + stiffness + gravity +
                    node.childSpeed * Mathf.Max(0f, node.setting.spring);
                Vector3 rawDelta = acceleration * (dt * DampFactor * Mathf.Max(0f, strength));
                Vector3 next = node.currentTip + rawDelta;

                float dynamicRadius = node.collisionSetting.dynamicCollider == null
                    ? 0.012f
                    : Mathf.Clamp(Mathf.Max(node.collisionSetting.dynamicCollider.float_A, 0.008f), 0.008f, 0.055f);
                Vector3 direction = (next - origin).normalized;
                next = origin + direction * length;
                direction = ApplyAuthoredLimit(node, restDirection, direction);
                next = origin + direction * length;
                float angle = Vector3.Angle(restDirection, direction);

                // Collision has higher priority than the angular spring. The old order solved
                // collision first and then projected back to the fixed bone length/angle, which
                // could push the skirt tip several millimetres back into a thigh capsule. Use a
                // short position-based solve on the length sphere after applying the authored
                // angular limit. This converges without making the skirt look inflated.
                bool collisionCorrected = false;
                for (int iteration = 0; iteration < 8; iteration++)
                {
                    Vector3 resolved = ResolveCollisions(node, next, dynamicRadius);
                    if ((resolved - next).sqrMagnitude <= 0.0000000001f) break;
                    collisionCorrected = true;
                    Vector3 resolvedDirection = resolved - origin;
                    if (resolvedDirection.sqrMagnitude < 0.0000001f) break;
                    next = origin + resolvedDirection.normalized * length;
                }
                if (collisionCorrected) collisionCorrections++;
                direction = (next - origin).normalized;
                angle = Vector3.Angle(restDirection, direction);

                Quaternion rotation = Quaternion.FromToRotation(restDirection, direction) * node.bone.rotation;
                node.bone.rotation = Quaternion.Slerp(node.bone.rotation, rotation, Mathf.Clamp01(strength));
                node.previousTip = node.currentTip;
                node.currentTip = next;
                node.childSpeed = rawDelta;
                node.lastOrigin = origin;
                angleSum += angle;
                maxAngle = Mathf.Max(maxAngle, angle);
            }
            collisionCorrections += ApplyChainLayers();
            CollisionCorrections = collisionCorrections;
            MeanAngularOffset = angleSum / Mathf.Max(1, _nodes.Count);
            MaxAngularOffset = maxAngle;
            PeakCollisionCorrections = Mathf.Max(PeakCollisionCorrections, CollisionCorrections);
            PeakMeanAngularOffset = Mathf.Max(PeakMeanAngularOffset, MeanAngularOffset);
            PeakMaxAngularOffset = Mathf.Max(PeakMaxAngularOffset, MaxAngularOffset);
            MinimumColliderClearance = ComputeMinimumColliderClearance();
            WorstColliderClearance = Mathf.Min(WorstColliderClearance, MinimumColliderClearance);
        }

        private void ApplyQuartzDrivers()
        {
            foreach (QuartzSkirtState driver in _drivers)
            {
                QuartzSkirtSetting setting = driver.setting;
                Vector3 delta = SignedEuler(Quaternion.Inverse(driver.referenceRestLocalRotation) * driver.referenceBone.localRotation);
                float connectionValue = setting.connectionAxis == 1 ? delta.y : setting.connectionAxis == 2 ? delta.z : delta.x;
                bool left = driver.transform.name.StartsWith("Left", StringComparison.Ordinal);
                bool outward = left ? connectionValue >= 0f : connectionValue <= 0f;
                Vector3 coefficient = outward ? setting.outerCoefficient : setting.innerCoefficient;
                Vector3 rotation = Vector3.Scale(delta, coefficient);
                rotation.x = Mathf.Clamp(rotation.x, setting.limitMin.x, setting.limitMax.x);
                rotation.y = Mathf.Clamp(rotation.y, setting.limitMin.y, setting.limitMax.y);
                rotation.z = Mathf.Clamp(rotation.z, setting.limitMin.z, setting.limitMax.z);
                driver.transform.localRotation = driver.restLocalRotation * Quaternion.Euler(rotation);
            }
        }

        private Vector3 ResolveCollisions(Node node, Vector3 point, float dynamicRadius)
        {
            Vector3 result = point;
            foreach (StaticColliderState collider in _colliders)
            {
                if (!MasksOverlap(node.collisionSetting.dynamicCollider, collider.setting)) continue;
                SwingCollider setting = collider.setting;
                Vector3 a = collider.transform.TransformPoint(setting.vector3_A);
                Vector3 b = collider.transform.TransformPoint(setting.vector3_B);
                if (setting.type == 0) b = a;
                Vector3 axis = b - a;
                float t = axis.sqrMagnitude < 0.0000001f
                    ? 0f
                    : Mathf.Clamp01(Vector3.Dot(result - a, axis) / axis.sqrMagnitude);
                Vector3 center = a + axis * t;
                Vector3 delta = result - center;
                float distance = delta.magnitude;
                float radius = Mathf.Lerp(setting.float_A, setting.float_B, t) + dynamicRadius + CollisionSkin;
                if (distance >= radius) continue;
                Vector3 normal = distance > 0.0001f ? delta / distance : collider.transform.forward;
                Vector3 target = center + normal * radius;
                result = Vector3.Lerp(result, target, Mathf.Clamp01(collisionStrength));
            }
            return result;
        }

        private void BuildChainLayers()
        {
            Dictionary<Transform, Node> controllerByPoint = _nodes
                .Where(value => value.child != null)
                .GroupBy(value => value.child)
                .ToDictionary(value => value.Key, value => value.First());
            foreach (ActorSwingChain chain in GetComponentsInChildren<ActorSwingChain>(true))
            {
                if (chain == null || chain.chains == null || chain.chains.layers == null) continue;
                foreach (SwingChainLayer layer in chain.chains.layers)
                {
                    if (layer == null || !layer.active || layer.bones == null || layer.bones.Length < 2) continue;
                    List<ChainPointState> points = new List<ChainPointState>();
                    bool allSkirt = true;
                    foreach (ActorSwingDynamicBone setting in layer.bones)
                    {
                        Node controller;
                        if (setting == null || !controllerByPoint.TryGetValue(setting.transform, out controller))
                        {
                            allSkirt = false;
                            break;
                        }
                        if (setting.transform.name.IndexOf("Skirt", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            allSkirt = false;
                            break;
                        }
                        points.Add(new ChainPointState(setting, controller));
                    }
                    if (allSkirt && points.Count >= 2)
                        _chainLayers.Add(new ChainLayerState(points, layer.radius, layer.smoothing, layer.around));
                }
            }
        }

        private int ApplyChainLayers()
        {
            int corrections = 0;
            foreach (ChainLayerState layer in _chainLayers)
            {
                int pointCount = layer.points.Count;
                int edgeCount = layer.around ? pointCount : pointCount - 1;
                if (edgeCount <= 0) continue;
                for (int iteration = 0; iteration < 4; iteration++)
                {
                    Vector3[] positions = layer.points.Select(value => value.transform.position).ToArray();
                    Vector3[] offsets = new Vector3[pointCount];
                    for (int edge = 0; edge < edgeCount; edge++)
                    {
                        int nextIndex = (edge + 1) % pointCount;
                        Vector3 start = positions[edge];
                        Vector3 end = positions[nextIndex];
                        Vector3 edgeVector = end - start;
                        float edgeLength = edgeVector.magnitude;
                        if (edgeLength > 0.0001f)
                        {
                            float error = edgeLength - layer.restLengths[edge];
                            // Original metadata separates ProcessChainBoneCollider
                            // from ProcessChainBoneSmoothing. A layer with smoothing=0
                            // is only a collision capsule ring; it must not become a
                            // rigid hoop. Preserve rest edge length only when authored.
                            float stiffness = Mathf.Clamp01(layer.smoothing) * 0.18f;
                            if (stiffness > 0.0001f)
                            {
                                Vector3 lengthCorrection = edgeVector / edgeLength * (error * stiffness * 0.5f);
                                offsets[edge] += lengthCorrection;
                                offsets[nextIndex] -= lengthCorrection;
                            }
                        }

                        int mask = layer.points[edge].collisionMask | layer.points[nextIndex].collisionMask;
                        foreach (StaticColliderState collider in _colliders)
                        {
                            if (!MasksOverlap(mask, collider.setting.collisionMask)) continue;
                            Vector3 colliderStart = collider.transform.TransformPoint(collider.setting.vector3_A);
                            Vector3 colliderEnd = collider.setting.type == 0
                                ? colliderStart
                                : collider.transform.TransformPoint(collider.setting.vector3_B);
                            float edgeT;
                            float colliderT;
                            Vector3 edgePoint;
                            Vector3 colliderPoint;
                            ClosestPointsOnSegments(start, end, colliderStart, colliderEnd,
                                out edgeT, out colliderT, out edgePoint, out colliderPoint);
                            Vector3 separation = edgePoint - colliderPoint;
                            float distance = separation.magnitude;
                            float radius = layer.radius + Mathf.Lerp(collider.setting.float_A, collider.setting.float_B, colliderT) + CollisionSkin;
                            if (distance >= radius) continue;
                            Vector3 normal = distance > 0.0001f ? separation / distance : collider.transform.forward;
                            Vector3 correction = normal * (radius - distance) * Mathf.Clamp01(collisionStrength);
                            offsets[edge] += correction * (1f - edgeT);
                            offsets[nextIndex] += correction * edgeT;
                            corrections++;
                        }
                    }

                    for (int index = 0; index < pointCount; index++)
                    {
                        Vector3 offset = offsets[index];
                        if (offset.sqrMagnitude < 0.0000000001f) continue;
                        ChainPointState point = layer.points[index];
                        Node node = point.controller;
                        Vector3 origin = node.bone.position;
                        Vector3 currentTip = node.child.position;
                        float length = Vector3.Distance(origin, currentTip);
                        if (length < 0.0001f) continue;
                        Vector3 currentDirection = (currentTip - origin).normalized;
                        Vector3 targetDirection = (currentTip + offset - origin).normalized;
                        Vector3 restDirection = node.bone.parent == null
                            ? node.frameBaseLocalRotation * node.child.localPosition.normalized
                            : node.bone.parent.rotation * (node.frameBaseLocalRotation * node.child.localPosition.normalized);
                        targetDirection = ApplyAuthoredLimit(node, restDirection, targetDirection);
                        Quaternion rotation = Quaternion.FromToRotation(currentDirection, targetDirection) * node.bone.rotation;
                        node.bone.rotation = Quaternion.Slerp(node.bone.rotation, rotation, 0.82f);
                        node.previousTip = node.currentTip;
                        node.currentTip = origin + targetDirection * length;
                        node.lastOrigin = origin;
                    }
                }
            }
            return corrections;
        }

        private static bool MasksOverlap(SwingCollider dynamicCollider, SwingCollider staticCollider)
        {
            if (dynamicCollider == null || staticCollider == null) return false;
            return MasksOverlap(dynamicCollider.collisionMask, staticCollider.collisionMask);
        }

        private static bool MasksOverlap(int left, int right)
        {
            if (left == -1 || right == -1) return true;
            return (left & right) != 0;
        }

        private float ComputeMinimumColliderClearance()
        {
            float minimum = float.PositiveInfinity;
            foreach (Node node in _nodes)
            {
                SwingCollider dynamicCollider = node.collisionSetting.dynamicCollider;
                if (dynamicCollider == null) continue;
                Vector3 point = node.child.position;
                foreach (StaticColliderState collider in _colliders)
                {
                    if (!MasksOverlap(dynamicCollider, collider.setting)) continue;
                    Vector3 a = collider.transform.TransformPoint(collider.setting.vector3_A);
                    Vector3 b = collider.setting.type == 0
                        ? a
                        : collider.transform.TransformPoint(collider.setting.vector3_B);
                    Vector3 axis = b - a;
                    float t = axis.sqrMagnitude < 0.0000001f
                        ? 0f
                        : Mathf.Clamp01(Vector3.Dot(point - a, axis) / axis.sqrMagnitude);
                    float radius = dynamicCollider.float_A + Mathf.Lerp(collider.setting.float_A, collider.setting.float_B, t);
                    minimum = Mathf.Min(minimum, Vector3.Distance(point, a + axis * t) - radius);
                }
            }

            foreach (ChainLayerState layer in _chainLayers)
            {
                int pointCount = layer.points.Count;
                int edgeCount = layer.around ? pointCount : pointCount - 1;
                for (int edge = 0; edge < edgeCount; edge++)
                {
                    int nextIndex = (edge + 1) % pointCount;
                    Vector3 start = layer.points[edge].transform.position;
                    Vector3 end = layer.points[nextIndex].transform.position;
                    int mask = layer.points[edge].collisionMask | layer.points[nextIndex].collisionMask;
                    foreach (StaticColliderState collider in _colliders)
                    {
                        if (!MasksOverlap(mask, collider.setting.collisionMask)) continue;
                        Vector3 colliderStart = collider.transform.TransformPoint(collider.setting.vector3_A);
                        Vector3 colliderEnd = collider.setting.type == 0
                            ? colliderStart
                            : collider.transform.TransformPoint(collider.setting.vector3_B);
                        float edgeT;
                        float colliderT;
                        Vector3 edgePoint;
                        Vector3 colliderPoint;
                        ClosestPointsOnSegments(start, end, colliderStart, colliderEnd,
                            out edgeT, out colliderT, out edgePoint, out colliderPoint);
                        float radius = layer.radius + Mathf.Lerp(collider.setting.float_A, collider.setting.float_B, colliderT);
                        minimum = Mathf.Min(minimum, Vector3.Distance(edgePoint, colliderPoint) - radius);
                    }
                }
            }
            return minimum;
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
                    s = denominator > 0.0000001f ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
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

        private static Vector3 ApplyAuthoredLimit(
            Node node,
            Vector3 authoredDirection,
            Vector3 desiredDirection)
        {
            SwingLimitInfo limit = node.setting.limitInfo;
            if (limit == null || limit.useLimit == 0) return desiredDirection.normalized;
            Quaternion parentWorldRotation = node.bone.parent == null
                ? Quaternion.identity
                : node.bone.parent.rotation;
            Quaternion baseWorldRotation = node.bone.parent == null
                ? node.frameBaseLocalRotation
                : parentWorldRotation * node.frameBaseLocalRotation;
            Quaternion worldDelta =
                Quaternion.FromToRotation(authoredDirection, desiredDirection);
            Quaternion localDelta = Quaternion.Inverse(baseWorldRotation) *
                worldDelta * baseWorldRotation;
            Vector3 euler = SignedEuler(localDelta);
            euler.x = ClampAuthoredAngle(euler.x, limit.axisX);
            euler.y = ClampAuthoredAngle(euler.y, limit.axisY);
            euler.z = ClampAuthoredAngle(euler.z, limit.axisZ);
            Vector3 localAxis = node.child.localPosition.normalized;
            Vector3 result =
                (baseWorldRotation * Quaternion.Euler(euler)) * localAxis;
            return result.sqrMagnitude < 0.0000001f ? authoredDirection : result.normalized;
        }

        private static float CalcEffectiveStiffness(
            Node node,
            Vector3 authoredDirection,
            Vector3 authoredTip)
        {
            float stiffness = node.setting.stiffness;
            float pendulum = node.setting.pendulum;
            float range = node.setting.pendulumRange;
            if (pendulum <= 0f || range <= 0.00001f) return stiffness;

            float reduction;
            if (node.setting.dynamicType == 0)
            {
                Vector3 currentDirection = node.currentTip - node.bone.position;
                currentDirection = currentDirection.sqrMagnitude < 0.0000001f
                    ? authoredDirection
                    : currentDirection.normalized;
                float alignment = Mathf.Abs(Vector3.Dot(authoredDirection, currentDirection));
                float active = Mathf.Max(0f, alignment - (1f - range));
                reduction = active / range * pendulum;
            }
            else
            {
                float distance = Mathf.Min(range,
                    Vector3.Distance(node.currentTip, authoredTip) * 10f);
                reduction = (1f - distance / range) * pendulum;
            }
            return stiffness - reduction;
        }

        private static float ClampAuthoredAngle(float value, Vector2Int interval)
        {
            return Mathf.Clamp(value,
                Mathf.Min(interval.x, interval.y),
                Mathf.Max(interval.x, interval.y));
        }

        private static Vector3 SignedEuler(Quaternion rotation)
        {
            Vector3 value = rotation.eulerAngles;
            if (value.x > 180f) value.x -= 360f;
            if (value.y > 180f) value.y -= 360f;
            if (value.z > 180f) value.z -= 360f;
            return value;
        }

        private static int HierarchyDepth(Transform value)
        {
            int result = 0;
            while (value.parent != null)
            {
                result++;
                value = value.parent;
            }
            return result;
        }

        private static IEnumerable<Transform> DescendantsAndSelf(Transform root)
        {
            yield return root;
            foreach (Transform child in root)
            {
                foreach (Transform descendant in DescendantsAndSelf(child)) yield return descendant;
            }
        }

        private sealed class Node
        {
            public readonly ActorSwingDynamicBone setting;
            public readonly ActorSwingDynamicBone collisionSetting;
            public readonly Transform bone;
            public readonly Transform child;
            public readonly Quaternion restLocalRotation;
            public readonly QuartzSkirtState driver;
            public readonly int depth;
            public Quaternion frameBaseLocalRotation;
            public Vector3 currentTip;
            public Vector3 previousTip;
            public Vector3 childSpeed;
            public Vector3 lastOrigin;
            public bool ready;

            public Node(ActorSwingDynamicBone source, Transform childValue, int depthValue, QuartzSkirtState quartzDriver)
            {
                setting = source;
                bone = source.transform;
                child = childValue;
                // ActorSwingCollisionUtility.CheckDynamicCollision receives
                // pChildDynamic in the original API. The collider at the moving
                // endpoint belongs to the child bone, not the segment root.
                collisionSetting = child.GetComponent<ActorSwingDynamicBone>() ?? source;
                restLocalRotation = bone.localRotation;
                frameBaseLocalRotation = restLocalRotation;
                driver = quartzDriver;
                depth = depthValue;
            }
        }

        private sealed class StaticColliderState
        {
            public readonly Transform transform;
            public readonly SwingCollider setting;
            public StaticColliderState(ActorSwingStaticBone source)
            {
                transform = source.transform;
                setting = source.staticCollider;
            }
        }

        private sealed class ChainPointState
        {
            public readonly Transform transform;
            public readonly Node controller;
            public readonly int collisionMask;

            public ChainPointState(ActorSwingDynamicBone setting, Node controllerNode)
            {
                transform = setting.transform;
                controller = controllerNode;
                collisionMask = setting.dynamicCollider == null ? -1 : setting.dynamicCollider.collisionMask;
            }
        }

        private sealed class ChainLayerState
        {
            public readonly List<ChainPointState> points;
            public readonly float radius;
            public readonly float smoothing;
            public readonly bool around;
            public readonly float[] restLengths;

            public ChainLayerState(List<ChainPointState> values, float layerRadius, float layerSmoothing, bool loop)
            {
                points = values;
                radius = Mathf.Max(0f, layerRadius);
                smoothing = layerSmoothing;
                around = loop;
                int edgeCount = around ? points.Count : points.Count - 1;
                restLengths = new float[Mathf.Max(0, edgeCount)];
                for (int index = 0; index < restLengths.Length; index++)
                {
                    int next = (index + 1) % points.Count;
                    restLengths[index] = Mathf.Max(0.0001f,
                        Vector3.Distance(points[index].transform.position, points[next].transform.position));
                }
            }
        }

        private sealed class QuartzSkirtState
        {
            public readonly Transform transform;
            public readonly QuartzSkirtSetting setting;
            public readonly Quaternion restLocalRotation;
            public readonly Quaternion referenceRestLocalRotation;
            public readonly Transform referenceBone;
            public QuartzSkirtState(ActorAnimationQuartzDriverSkirtBone source, Transform resolvedReferenceBone)
            {
                transform = source.transform;
                setting = source.setting;
                restLocalRotation = transform.localRotation;
                referenceBone = resolvedReferenceBone;
                referenceRestLocalRotation = referenceBone.localRotation;
            }
        }
    }
}
