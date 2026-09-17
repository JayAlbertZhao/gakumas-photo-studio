using System;
using System.Collections.Generic;
using System.Reflection;
using ActorAnimation;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        // Generated rigs only: no private model or original motion is required.
        private sealed class SecondaryFixture
        {
            public Transform root;
            public MonoBehaviour solver;
            public Action<bool> automatic;
            public Action<float, double> advance;
            public Transform[] bones;
            public Quaternion[] rotations;
            public Vector3[] positions;

            public void Pose(int frame)
            {
                root.position = new Vector3(.015f * Mathf.Sin(frame * .13f),
                    .02f * Mathf.Sin(frame * .21f), 0f);
                root.rotation = Quaternion.Euler(0, 12f * Mathf.Sin(frame * .09f), 0);
                for (int i = 1; i < bones.Length; i++)
                {
                    bones[i].localPosition = positions[i];
                    bones[i].localRotation = rotations[i];
                }
            }

            public float[] Snapshot()
            {
                var result = new float[bones.Length * 7];
                for (int i = 0; i < bones.Length; i++)
                {
                    Vector3 p = bones[i].localPosition;
                    Quaternion q = bones[i].localRotation;
                    result[i * 7] = p.x; result[i * 7 + 1] = p.y; result[i * 7 + 2] = p.z;
                    result[i * 7 + 3] = q.x; result[i * 7 + 4] = q.y;
                    result[i * 7 + 5] = q.z; result[i * 7 + 6] = q.w;
                }
                return result;
            }
        }

        private SecondaryFixture CreateSecondaryFixture(string kind)
        {
            var owner = Own(new GameObject("Generated " + kind));
            Transform Bone(string name, Transform parent, Vector3 offset)
            {
                var t = new GameObject(name).transform;
                t.SetParent(parent, false); t.localPosition = offset; return t;
            }
            var fixture = new SecondaryFixture { root = owner.transform };
            Action initialize;
            if (kind == "bilateral")
            {
                var hips = Bone("Hips", owner.transform, Vector3.zero);
                var left = Bone("Left", hips, new Vector3(-.08f, .2f, 0));
                var right = Bone("Right", hips, new Vector3(.08f, .2f, 0));
                var setting = owner.AddComponent<ActorSwingBreastBone>();
                setting.leftBreast = left; setting.rightBreast = right;
                setting.leftBreastEnd = Bone("LeftEnd", left, Vector3.forward * .08f);
                setting.rightBreastEnd = Bone("RightEnd", right, Vector3.forward * .08f);
                setting.damping = .35f; setting.stiffness = .05f;
                setting.spring = .2f; setting.pendulum = .01f; setting.pendulumRange = 1f;
                setting.rootWeight = .5f; setting.average = .2f;
                var solver = owner.AddComponent<BreastDynamicsSystem>();
                initialize = () => solver.Initialize(owner.transform); fixture.solver = solver;
                fixture.automatic = value => solver.automaticSimulation = value;
                fixture.advance = (dt, time) => solver.AdvanceSimulation(dt);
            }
            else
            {
                Transform parent = owner.transform;
                for (int i = 0; i < 4; i++)
                {
                    if (kind == "slide" && i == 3) break;
                    string name = kind == "slide" ? "LeftUpLegSkin" + (i + 1) + (i == 2 ? "_S_End" : "_S") : "Link" + i;
                    var bone = Bone(name, parent, Vector3.down * .08f);
                    var setting = bone.gameObject.AddComponent<ActorSwingDynamicBone>();
                    setting.dynamicType = kind == "slide" ? 1 : 0;
                    setting.mass = .3f; setting.stiffness = .025f; setting.spring = .15f;
                    setting.pendulum = .004f; setting.rootWeight = .5f;
                    parent = bone;
                }
                if (kind == "slide")
                {
                    var solver = owner.AddComponent<BodySoftTissueDynamicsSystem>();
                    initialize = () => solver.Initialize(owner.transform, null); fixture.solver = solver;
                    fixture.automatic = value => solver.automaticSimulation = value;
                    fixture.advance = (dt, time) => solver.AdvanceSimulation(dt);
                }
                else
                {
                    var solver = owner.AddComponent<HairDynamicsSystem>();
                    initialize = () => solver.Initialize(owner.transform, owner.transform);
                    solver.naturalWind = new NaturalWindSettings { enabled = true,
                        steadyForce = new Vector3(.01f, 0, .004f),
                        sineAmplitude = new Vector3(.012f, 0, .006f),
                        randomAmplitude = new Vector3(.008f, .002f, .004f) };
                    fixture.solver = solver;
                    fixture.automatic = value => solver.automaticSimulation = value;
                    fixture.advance = solver.AdvanceSimulation;
                }
            }
            fixture.bones = owner.GetComponentsInChildren<Transform>();
            fixture.positions = new Vector3[fixture.bones.Length];
            fixture.rotations = new Quaternion[fixture.bones.Length];
            for (int i = 0; i < fixture.bones.Length; i++)
            {
                fixture.positions[i] = fixture.bones[i].localPosition;
                fixture.rotations[i] = fixture.bones[i].localRotation;
            }
            initialize();
            return fixture;
        }

        private static float SecondaryDifference(float[] a, float[] b)
        {
            float error = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                if (float.IsNaN(a[i]) || float.IsNaN(b[i]) || float.IsInfinity(a[i]) || float.IsInfinity(b[i]))
                    return float.PositiveInfinity;
                error = Mathf.Max(error, Mathf.Abs(a[i] - b[i]));
            }
            return error;
        }

        private void VerifyAuthoredSkirtHelpers(Report report)
        {
            var type = typeof(HairDynamicsSystem);
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var evaluate = type.GetMethod("EvaluateAuthoredSkirtHelper", flags);
            var gain = type.GetMethod("SkirtAxisGain", flags);
            Quaternion Evaluate(Quaternion initial, Quaternion current, QuartzSkirtSetting setting) =>
                (Quaternion)evaluate.Invoke(null, new object[] { initial, current, setting });
            float Difference(Quaternion actual, Quaternion expected) => Mathf.Max(
                (actual * Vector3.up - expected * Vector3.up).magnitude,
                (actual * Vector3.right - expected * Vector3.right).magnitude);
            void Check(string name, bool accepted, float value = 0) =>
                FrameworkCheck(report, "skirt-helper-" + name, accepted, value);
            void Equal(string name, Quaternion actual, Quaternion expected)
            { float error = Difference(actual, expected); Check(name, error < 1e-4f, error); }
            var setting = new QuartzSkirtSetting { innerCoefficient = Vector3.one,
                outerCoefficient = Vector3.one, limitMin = Vector3.one * -10, limitMax = Vector3.one * 15 };
            // Analytic single-axis geometry, independent of the decomposition code.
            var inputAxes = new[] { Vector3.right, Vector3.up, Vector3.forward };
            var outputAxes = new[] { Vector3.forward, Vector3.right, Vector3.up };
            for (int order = 0; order < 6; order++)
            {
                setting.rotationOrder = order;
                foreach (float degrees in new[] { -70f, -20f, 0f, 20f, 70f })
                for (int axis = 0; axis < 3; axis++)
                {
                    Quaternion relative = Quaternion.AngleAxis(degrees, inputAxes[axis]);
                    Quaternion expected = Quaternion.AngleAxis(-degrees, outputAxes[axis]);
                    Equal("axial-order-" + order + "-axis-" + axis + "-angle-" + degrees,
                        Evaluate(Quaternion.identity, relative, setting), expected);
                    Quaternion initial = Quaternion.Euler(31, -27, 18);
                    Equal("parent-frame-order-" + order + "-axis-" + axis + "-angle-" + degrees,
                        Evaluate(initial, relative * initial, setting), expected);
                }
            }
            float Gain(float angle) => (float)gain.Invoke(null, new object[] { angle, .25f, 2f, -10f, 15f });
            float[] inputs = { -30, -10, -4, 0, 8, 15, 30 };
            float[] expectedGains = { -42.5f, -2.5f, -1, 0, 2, 3.75f, 33.75f };
            for (int i = 0; i < inputs.Length; i++)
                Check("continuous-dual-gain-" + inputs[i], Mathf.Abs(Gain(inputs[i]) - expectedGains[i]) < 1e-5f);
            foreach (float boundary in new[] { -10f, 15f })
                Check("gain-boundary-" + boundary, Mathf.Abs(Gain(boundary + .001f) - Gain(boundary - .001f)) < .005f);
            setting.rotationOrder = 0;
            setting.innerCoefficient = Vector3.zero; setting.outerCoefficient = Vector3.zero;
            Equal("zero-gain-identity", Evaluate(Quaternion.identity, Quaternion.Euler(15, 29, -65), setting), Quaternion.identity);
            setting.outerCoefficient = Vector3.one;
            Quaternion free = Evaluate(Quaternion.identity, Quaternion.AngleAxis(5, Vector3.right), setting);
            Equal("free-interval", free, Quaternion.identity);
            Equal("outside-interval-positive-response", Evaluate(Quaternion.identity,
                Quaternion.AngleAxis(45, Vector3.right), setting), Quaternion.AngleAxis(-30, Vector3.forward));
            for (int order = 0; order < 6; order++)
            {
                setting.rotationOrder = order;
                setting.innerCoefficient = new Vector3(.2f, .5f, .1f);
                setting.outerCoefficient = new Vector3(1, .8f, 1.2f);
                Quaternion a = Evaluate(Quaternion.identity, Quaternion.Euler(-.02f, -1.9f, -79.4f), setting);
                Quaternion b = Evaluate(Quaternion.identity, Quaternion.Euler(.02f, -1.9f, -79.4f), setting);
                float difference = Difference(a, b);
                Check("compound-boundary-order-" + order, difference < .005f, difference);
                Check("compound-nonzero-order-" + order, Difference(a, Quaternion.identity) > .1f);
            }
            setting.rotationOrder = 6;
            bool rejected = false;
            try { Evaluate(Quaternion.identity, Quaternion.identity, setting); }
            catch (TargetInvocationException e) { rejected = e.InnerException is ArgumentOutOfRangeException; }
            Check("unsupported-order-explicitly-rejected", rejected);

            // Exercise registration and output assignment as well as pure math.
            // A misleading helper name must not choose the input in opt-in mode.
            foreach (bool gameObjectReference in new[] { false, true })
            {
                var owner = Own(new GameObject("Generated explicit skirt"));
                var reference = new GameObject("Caller-assigned input").transform;
                reference.SetParent(owner.transform, false);
                var wrong = new GameObject("LeftUpLeg").transform; wrong.SetParent(owner.transform, false);
                var helper = new GameObject("Left misleading helper").transform; helper.SetParent(owner.transform, false);
                Quaternion targetRest = Quaternion.Euler(11, 14, -9); helper.localRotation = targetRest;
                var driver = helper.gameObject.AddComponent<ActorAnimationQuartzDriverSkirtBone>();
                driver.setting = new QuartzSkirtSetting { referenceBone = gameObjectReference ? (UnityEngine.Object)reference.gameObject : reference,
                    innerCoefficient = Vector3.one, outerCoefficient = Vector3.one };
                var solver = owner.AddComponent<HairDynamicsSystem>(); solver.automaticSimulation = false;
                Check("default-off-" + gameObjectReference, !solver.useAuthoredSkirtHelpers);
                solver.InitializeSkirt(owner.transform, owner.transform, owner.transform, null);
                var apply = type.GetMethod("ApplyQuartzDrivers", BindingFlags.Instance | BindingFlags.NonPublic);
                reference.localRotation = Quaternion.AngleAxis(40, Vector3.right);
                wrong.localRotation = Quaternion.Euler(0, 0, 12);
                apply.Invoke(solver, null);
                Equal("legacy-rest-and-name-" + gameObjectReference, helper.localRotation, targetRest * Quaternion.Euler(0, 0, 12));
                solver.useAuthoredSkirtHelpers = true; apply.Invoke(solver, null);
                Equal("explicit-absolute-output-" + gameObjectReference, helper.localRotation, Quaternion.AngleAxis(-40, Vector3.forward));
                solver.useAuthoredSkirtHelpers = false; apply.Invoke(solver, null);
                Equal("disable-restores-legacy-" + gameObjectReference, helper.localRotation, targetRest * Quaternion.Euler(0, 0, 12));
                // No conventional reference name exists on a fresh opt-in rig.
                wrong.name = "Unrelated"; reference.localRotation = Quaternion.identity;
                solver.useAuthoredSkirtHelpers = true;
                solver.InitializeSkirt(owner.transform, owner.transform, owner.transform, null);
                reference.localRotation = Quaternion.AngleAxis(40, Vector3.right); apply.Invoke(solver, null);
                Equal("name-free-registration-" + gameObjectReference, helper.localRotation, Quaternion.AngleAxis(-40, Vector3.forward));
            }
        }

        private void VerifyExternalReferenceLimits(Report report)
        {
            var fixture = CreateSecondaryFixture("swing"); fixture.automatic(false);
            var solver = (HairDynamicsSystem)fixture.solver;
            object parent = null;
            foreach (object node in (System.Collections.IEnumerable)DynamicField(solver, "_nodes"))
                if (DynamicField(node, "child") != null) { parent = node; break; }
            if (parent == null) throw new InvalidOperationException("Generated reference fixture has no segment.");
            object child = DynamicField(parent, "child");
            var setting = (ActorSwingDynamicBone)DynamicField(child, "setting");
            setting.limitInfo = new SwingLimitInfo { useLimit = 1,
                axisX = new Vector2Int(-180, 180), axisY = new Vector2Int(-180, 180), axisZ = new Vector2Int(-180, 180) };
            var reference = Own(new GameObject("Explicit external skirt reference")).transform;
            setting.referenceLimitInfo = new SwingReferenceLimitInfo { bone = reference };
            MethodInfo method = typeof(HairDynamicsSystem).GetMethod("ApplyAuthoredHardLimit", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo mode = typeof(HairDynamicsSystem).GetField("useExternalReferenceLimits");
            Vector3 Apply(Vector3 axis, Vector3 euler) => (Vector3)method.Invoke(solver,
                new object[] { parent, child, axis, Vector3.zero, Quaternion.identity, axis,
                    Quaternion.Euler(euler) * axis, 1f });
            void Check(string name, Vector3 actual, Vector3 expected)
            {
                float error = (actual - expected).magnitude;
                FrameworkCheck(report, "secondary-reference-" + name, error < 1e-5f, error);
            }
            FrameworkCheck(report, "secondary-reference-external-owner-not-in-local-map", DynamicField(child, "referenceNode") == null);
            reference.rotation = Quaternion.Euler(0, 0, 20);
            setting.referenceLimitInfo.max.z = true;
            Check("legacy-default-retains-own-limit-only", Apply(Vector3.down, new Vector3(0, 0, 60)),
                Quaternion.Euler(0, 0, 60) * Vector3.down);
            FrameworkCheck(report, "secondary-reference-explicit-mode-available", mode != null);
            if (mode != null) mode.SetValue(solver, true);
            for (int axis = 0; axis < 3; axis++)
            foreach (bool minimum in new[] { false, true })
            {
                var refEuler = Vector3.zero; var proposed = Vector3.zero;
                refEuler[axis] = minimum ? -20 : 20; proposed[axis] = minimum ? -60 : 60;
                reference.rotation = Quaternion.Euler(refEuler);
                setting.referenceLimitInfo.min = new SerializableBool3 { x = minimum && axis == 0, y = minimum && axis == 1, z = minimum && axis == 2 };
                setting.referenceLimitInfo.max = new SerializableBool3 { x = !minimum && axis == 0, y = !minimum && axis == 1, z = !minimum && axis == 2 };
                Vector3 direction = axis == 0 ? Vector3.forward : axis == 1 ? Vector3.right : Vector3.down;
                Check("external-" + axis + "-min-" + minimum, Apply(direction, proposed), Quaternion.Euler(refEuler) * direction);
            }
            setting.referenceLimitInfo.min = default;
            setting.referenceLimitInfo.max = new SerializableBool3 { z = true };
            foreach (float degrees in new[] { -25f, -5f, 15f, 35f })
            {
                reference.rotation = Quaternion.Euler(0, 0, degrees);
                Check("moving-reference-" + degrees, Apply(Vector3.down, new Vector3(0, 0, 70)),
                    Quaternion.Euler(0, 0, degrees) * Vector3.down);
            }
            // A mapped dynamic reference retains its solver state as authority,
            // even when the external Transform currently has a different pose.
            child.GetType().GetField("referenceNode").SetValue(child, parent);
            parent.GetType().GetField("rotation").SetValue(parent, Quaternion.Euler(0, 0, 10));
            Check("internal-state-still-authoritative", Apply(Vector3.down, new Vector3(0, 0, 70)),
                Quaternion.Euler(0, 0, 10) * Vector3.down);
            child.GetType().GetField("referenceNode").SetValue(child, null);
            var componentReference = reference.gameObject.AddComponent<ActorSwingDynamicBone>();
            setting.referenceLimitInfo.bone = componentReference;
            reference.rotation = Quaternion.Euler(0, 0, 20);
            Check("component-reference-resolves-transform-safely", Apply(Vector3.down, new Vector3(0, 0, 60)),
                Quaternion.Euler(0, 0, 20) * Vector3.down);
            child.GetType().GetField("authoredReferenceNode").SetValue(child, parent);
            Check("component-reference-prefers-local-solver-state", Apply(Vector3.down, new Vector3(0, 0, 60)),
                Quaternion.Euler(0, 0, 10) * Vector3.down);
            if (mode != null) mode.SetValue(solver, false);
            Check("disabled-component-reference-preserves-legacy", Apply(Vector3.down, new Vector3(0, 0, 60)),
                Quaternion.Euler(0, 0, 60) * Vector3.down);
            if (mode != null) mode.SetValue(solver, true);
            child.GetType().GetField("authoredReferenceNode").SetValue(child, null);
            setting.referenceLimitInfo.bone = reference.gameObject;
            Check("unsupported-object-keeps-own-limit", Apply(Vector3.down, new Vector3(0, 0, 60)),
                Quaternion.Euler(0, 0, 60) * Vector3.down);
            setting.referenceLimitInfo.bone = null;
            Check("missing-reference-keeps-own-limit", Apply(Vector3.down, new Vector3(0, 0, 60)), Quaternion.Euler(0, 0, 60) * Vector3.down);
            setting.referenceLimitInfo.bone = reference;
            if (mode != null) mode.SetValue(solver, false);
            Check("explicit-disable-restores-legacy", Apply(Vector3.down, new Vector3(0, 0, 60)), Quaternion.Euler(0, 0, 60) * Vector3.down);
        }

        private void VerifySecondaryMotionClock(Report report)
        {
            foreach (string kind in new[] { "swing", "slide", "bilateral" })
            {
                void Check(string label, bool accepted, float error = 0f) =>
                    FrameworkCheck(report, "secondary-clock-" + kind + "-" + label, accepted, error);
                var a = CreateSecondaryFixture(kind); var b = CreateSecondaryFixture(kind);
                bool owned = false;
                try { a.advance(.01667f, 1); } catch (InvalidOperationException) { owned = true; }
                Check("default-rejects-double-owner", owned);
                a.automatic(false); b.automatic(false);
                Check("nonempty-solver", kind == "swing" ? ((HairDynamicsSystem)a.solver).SimulatedBoneCount == 3 :
                    kind == "slide" ? ((BodySoftTissueDynamicsSystem)a.solver).SimulatedNodeCount == 2 :
                    ((BreastDynamicsSystem)a.solver).DrivenBoneCount == 2);
                float[] before = a.Snapshot();
                a.advance(0, 99); a.solver.SendMessage("LateUpdate");
                Check("zero-and-disabled-lateupdate-noop", SecondaryDifference(before, a.Snapshot()) == 0);
                foreach (float invalid in new[] { -.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                {
                    bool rejected = false;
                    try { a.advance(invalid, 1); } catch (ArgumentOutOfRangeException) { rejected = true; }
                    Check("invalid-delta-" + invalid, rejected && SecondaryDifference(before, a.Snapshot()) == 0);
                }
                if (kind == "swing")
                    foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e13 })
                    {
                        bool rejected = false;
                        try { a.advance(.01667f, invalid); } catch (ArgumentOutOfRangeException) { rejected = true; }
                        Check("invalid-clock-" + invalid, rejected && SecondaryDifference(before, a.Snapshot()) == 0);
                    }
                float motion = 0f, maximum = 0f;
                var trajectory = new List<float[]>();
                for (int frame = 0; frame < 360; frame++)
                {
                    a.Pose(frame); b.Pose(frame);
                    float[] authored = a.Snapshot();
                    a.advance(.01667f, frame * .01667); b.advance(.01667f, frame * .01667);
                    float[] current = a.Snapshot();
                    trajectory.Add(current);
                    maximum = Mathf.Max(maximum, SecondaryDifference(current, b.Snapshot()));
                    // Exclude the root's authored movement: compare the same frame's pose.
                    motion = Mathf.Max(motion, SecondaryDifference(current, authored));
                }
                Check("six-second-replay-exact-finite", maximum == 0f, maximum);
                Check("nonempty-secondary-response", motion > 1e-5f && !float.IsInfinity(motion), motion);
                a.Pose(0); a.solver.SendMessage("ResetSimulation");
                float resetError = 0f;
                for (int frame = 0; frame < 360; frame++)
                {
                    a.Pose(frame); a.advance(.01667f, frame * .01667);
                    resetError = Mathf.Max(resetError, SecondaryDifference(a.Snapshot(), trajectory[frame]));
                }
                Check("reset-and-full-replay-exact", resetError == 0f, resetError);

                var c = CreateSecondaryFixture(kind); var d = CreateSecondaryFixture(kind);
                c.automatic(false); d.automatic(false);
                c.advance(1f, 1); d.advance(.1f, 1);
                Check("existing-four-substep-cap", SecondaryDifference(c.Snapshot(), d.Snapshot()) == 0);
                var e = CreateSecondaryFixture(kind); var f = CreateSecondaryFixture(kind);
                e.automatic(false); f.automatic(false);
                for (int i = 0; i < 8; i++)
                {
                    e.advance(.01667f, 2);
                    f.advance(.008335f, 2); f.advance(.008335f, 2);
                }
                Check("fixed-step-partition-with-static-pose", SecondaryDifference(e.Snapshot(), f.Snapshot()) == 0,
                    SecondaryDifference(e.Snapshot(), f.Snapshot()));

                var legacy = CreateSecondaryFixture(kind); var manual = CreateSecondaryFixture(kind);
                manual.automatic(false);
                if (kind == "swing")
                {
                    ((HairDynamicsSystem)legacy.solver).naturalWindTimeOverride = Time.timeAsDouble;
                    ((HairDynamicsSystem)manual.solver).naturalWindTimeOverride = Time.timeAsDouble;
                    ((HairDynamicsSystem)legacy.solver).windStrength = .01f;
                    ((HairDynamicsSystem)manual.solver).windStrength = .01f;
                }
                for (int i = 0; i < 20; i++)
                {
                    legacy.Pose(i); manual.Pose(i);
                    legacy.solver.SendMessage("LateUpdate"); manual.advance(Time.deltaTime, Time.time);
                }
                Check("unity-default-clock-equivalence", SecondaryDifference(legacy.Snapshot(), manual.Snapshot()) == 0,
                    SecondaryDifference(legacy.Snapshot(), manual.Snapshot()));
                legacy.automatic(false);
            }
            var calm = CreateSecondaryFixture("swing"); var gust = CreateSecondaryFixture("swing");
            var overrideWind = CreateSecondaryFixture("swing"); var noWind = CreateSecondaryFixture("swing");
            foreach (var fixture in new[] { calm, gust, overrideWind, noWind }) fixture.automatic(false);
            ((HairDynamicsSystem)overrideWind.solver).naturalWindTimeOverride = 2;
            ((HairDynamicsSystem)noWind.solver).naturalWind = null;
            for (int i = 0; i < 40; i++)
            {
                calm.advance(.01667f, 5); gust.advance(.01667f, 2);
                overrideWind.advance(.01667f, 5); noWind.advance(.01667f, 5);
            }
            FrameworkCheck(report, "secondary-clock-wind-calm-exact-no-force",
                SecondaryDifference(calm.Snapshot(), noWind.Snapshot()) == 0);
            float windResponse = SecondaryDifference(calm.Snapshot(), gust.Snapshot());
            FrameworkCheck(report, "secondary-clock-explicit-gust-changes-geometry", windResponse > 1e-5f, windResponse);
            FrameworkCheck(report, "secondary-clock-explicit-wind-override-precedence",
                SecondaryDifference(gust.Snapshot(), overrideWind.Snapshot()) == 0);
        }
    }
}
