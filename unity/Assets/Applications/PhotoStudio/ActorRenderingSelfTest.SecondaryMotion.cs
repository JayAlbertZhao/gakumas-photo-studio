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

        private void VerifyGarmentSlideTranslation(Report report)
        {
            // A positional mode must admit translation along its rest segment,
            // including a coincident auxiliary pair. No assets or tuned forces.
            var offsets = new[] { Vector3.zero, Vector3.down * .08f, Vector3.right * .08f };
            for (int i = 0; i < offsets.Length; i++)
            {
                var owner = Own(new GameObject("Generated positional garment " + i));
                var root = new GameObject("Sleeve anchor").transform; root.SetParent(owner.transform, false);
                var tip = new GameObject("Sleeve slide output").transform; tip.SetParent(root, false);
                tip.localPosition = offsets[i]; tip.localRotation = Quaternion.Euler(5, -12, 7);
                var anchorSetting = root.gameObject.AddComponent<ActorSwingDynamicBone>();
                var setting = tip.gameObject.AddComponent<ActorSwingDynamicBone>();
                foreach (var s in new[] { anchorSetting, setting })
                { s.dynamicType = 1; s.mass = 0; s.damping = 1; s.stiffness = 0; s.spring = 0; s.pendulum = 0; s.wind = 0; }
                var solver = owner.AddComponent<HairDynamicsSystem>(); solver.automaticSimulation = false;
                // No body collider fallback in this one-force analytic fixture.
                solver.collisionStrength = 0;
                // Reflection keeps the original failing build runnable before
                // the explicit opt-in is implemented; the geometry oracle stays.
                var option = typeof(HairDynamicsSystem).GetField("useAuthoredSlideDynamics");
                if (option != null) option.SetValue(solver, true);
                solver.InitializeGarment(owner.transform, owner.transform, owner.transform, null);
                Vector3 before = tip.position; Quaternion rootRotation = root.rotation, tipRotation = tip.rotation;
                setting.mass = 1;
                solver.AdvanceSimulation(.01667f, 0);
                Vector3 expected = before + Vector3.down * (.01f * .01667f * 40f);
                float error = (tip.position - expected).magnitude;
                float response = (tip.position - before).magnitude;
                float rotationError = Mathf.Max((root.rotation * Vector3.up - rootRotation * Vector3.up).magnitude,
                    (tip.rotation * Vector3.right - tipRotation * Vector3.right).magnitude);
                FrameworkCheck(report, "garment-slide-" + i + "-nonempty-segment", solver.SimulatedBoneCount == 1, solver.SimulatedBoneCount);
                FrameworkCheck(report, "garment-slide-" + i + "-analytic-translation", error < 1e-6f, error);
                FrameworkCheck(report, "garment-slide-" + i + "-nonzero-translation", response > .006f && response < .007f, response);
                FrameworkCheck(report, "garment-slide-" + i + "-no-artificial-swing", rotationError < 1e-6f, rotationError);
            }
            VerifyGarmentSlideContracts(report);
        }

        private void VerifyGarmentSlideContracts(Report report)
        {
            void Check(string name, bool accepted, float error = 0f) =>
                FrameworkCheck(report, "garment-slide-contract-" + name, accepted, error);
            void Near(string name, Vector3 actual, Vector3 expected)
            { float error = (actual - expected).magnitude; Check(name, error < 2e-6f, error); }
            SecondaryFixture Rig(bool slide, bool enabled, Vector3 offset, Quaternion rotation)
            {
                var owner = Own(new GameObject("Generated slide contract"));
                owner.transform.rotation = rotation;
                var anchor = new GameObject("Anchor").transform; anchor.SetParent(owner.transform, false);
                var tip = new GameObject("Output").transform; tip.SetParent(anchor, false);
                tip.localPosition = offset; tip.localRotation = Quaternion.Euler(11, -7, 3);
                var a = anchor.gameObject.AddComponent<ActorSwingDynamicBone>();
                var b = tip.gameObject.AddComponent<ActorSwingDynamicBone>();
                foreach (var setting in new[] { a, b })
                {
                    setting.dynamicType = slide ? 1 : 0; setting.mass = 0; setting.damping = 1;
                    setting.stiffness = 0; setting.spring = 0; setting.pendulum = 0; setting.wind = 0;
                    setting.dynamicCollider = new SwingCollider { type = 4 };
                }
                var solver = owner.AddComponent<HairDynamicsSystem>(); solver.automaticSimulation = false;
                Check("default-off-" + slide + "-" + enabled, !solver.useAuthoredSlideDynamics);
                solver.useAuthoredSlideDynamics = enabled;
                solver.InitializeGarment(owner.transform, owner.transform, owner.transform, null);
                return new SecondaryFixture { root = owner.transform, solver = solver,
                    automatic = value => solver.automaticSimulation = value, advance = solver.AdvanceSimulation,
                    bones = new[] { owner.transform, anchor, tip },
                    positions = new[] { Vector3.zero, Vector3.zero, offset },
                    rotations = new[] { rotation, Quaternion.identity, tip.localRotation } };
            }
            ActorSwingDynamicBone Setting(SecondaryFixture f) => f.bones[2].GetComponent<ActorSwingDynamicBone>();
            const float step = .01667f;
            float distance = .01f * step * 40f;
            // Disabled authored colliders must not fall back to large body spheres.
            var disabled = Rig(true, true, Vector3.zero, Quaternion.identity);
            Setting(disabled).mass = 1; disabled.advance(step, 0);
            Near("disabled-collider-has-no-body-fallback", disabled.bones[2].position, Vector3.down * distance);
            foreach (Quaternion rotation in new[] { Quaternion.identity, Quaternion.Euler(17, 31, -26) })
            for (int axis = 0; axis < 3; axis++)
            foreach (int sign in new[] { -1, 1 })
            {
                string suffix = rotation.eulerAngles + "-" + axis + "-" + sign;
                var rig = Rig(true, true, new Vector3(.08f, -.03f, .02f), rotation);
                var solver = (HairDynamicsSystem)rig.solver;
                var setting = Setting(rig);
                setting.limitInfo = new SwingLimitInfo { useLimit = 1,
                    axisX = new Vector2Int(-2, 3), axisY = new Vector2Int(-4, 5), axisZ = new Vector2Int(-6, 7) };
                setting.wind = 1; setting.useWindGlobalForce = true;
                var force = Vector3.zero; force[axis] = sign * .1f;
                solver.naturalWind = new NaturalWindSettings { enabled = true, steadyForce = rotation * force,
                    sineAmplitude = Vector3.zero, randomAmplitude = Vector3.zero, useGustEnvelope = false };
                rig.advance(step, 0);
                Vector3 expected = rig.positions[2]; expected[axis] += (sign < 0 ? -(2 + axis * 2) : 3 + axis * 2) * .001f;
                Near("parent-frame-mm-limit-" + suffix, rig.bones[2].localPosition, expected);
                Near("translation-does-not-rotate-parent-" + suffix, rig.bones[1].localRotation * Vector3.up, Vector3.up);
                Near("translation-preserves-authored-orientation-" + suffix,
                    rig.bones[2].localRotation * Vector3.right, rig.rotations[2] * Vector3.right);
            }
            var animated = Rig(true, true, Vector3.right * .08f, Quaternion.identity);
            animated.root.rotation = Quaternion.Euler(20, 30, 40);
            animated.advance(step, 0);
            Near("animated-anchor-orientation", animated.bones[1].rotation * Vector3.up, animated.root.rotation * Vector3.up);
            Near("animated-slide-orientation", animated.bones[2].localRotation * Vector3.right, animated.rotations[2] * Vector3.right);
            foreach (int sign in new[] { -1, 1 })
            {
                Quaternion frame = Quaternion.Euler(-11, 23, 37);
                var axisRig = Rig(true, true, Vector3.zero, frame);
                var solver = (HairDynamicsSystem)axisRig.solver; var setting = Setting(axisRig);
                setting.wind = 1; setting.axisAddXToY = .5f; setting.axisAddXToZ = .25f;
                solver.naturalWind = new NaturalWindSettings { enabled = true,
                    steadyForce = frame * (Vector3.right * (sign * .02f)),
                    sineAmplitude = Vector3.zero, randomAmplitude = Vector3.zero, useGustEnvelope = false };
                axisRig.advance(step, 0);
                Near("axis-add-local-force-" + sign, axisRig.bones[2].position,
                    frame * (new Vector3(.02f, .01f, .005f) * (sign * step * 40f)));
            }
            foreach (float length in new[] { 0f, .08f })
            {
                var springRig = Rig(true, true, Vector3.right * length, Quaternion.identity);
                var solver = (HairDynamicsSystem)springRig.solver; var setting = Setting(springRig);
                // Set one initial condition directly, not by warming to a tuned
                // equilibrium: zero velocity, known 2 cm positional error.
                object child = null;
                foreach (object node in (System.Collections.IEnumerable)DynamicField(solver, "_nodes"))
                    if (DynamicField(node, "parent") != null) child = node;
                child.GetType().GetField("position").SetValue(child, springRig.bones[2].position + Vector3.up * .02f);
                setting.stiffness = .2f; setting.pendulum = .1f; setting.pendulumRange = .5f;
                springRig.advance(step, 0);
                // Distance/range = .02 * 10 / .5 = .4. Restoring gain = .14.
                Near("positional-restoring-target-" + length, springRig.bones[2].position,
                    springRig.positions[2] + Vector3.up * (.02f * (1f - .14f * step * 40f)));
                solver.strength = 0;
                springRig.advance(step, 0);
                Near("zero-strength-restores-authored-position-" + length, springRig.bones[2].localPosition, springRig.positions[2]);
            }
            // An upstream swing remains untouched by enabling positional support.
            var legacySwing = Rig(false, false, Vector3.down * .08f, Quaternion.identity);
            var optSwing = Rig(false, true, Vector3.down * .08f, Quaternion.identity);
            Setting(legacySwing).mass = Setting(optSwing).mass = .2f;
            for (int frame = 0; frame < 60; frame++)
            { legacySwing.Pose(frame); optSwing.Pose(frame); legacySwing.advance(step, frame * step); optSwing.advance(step, frame * step); }
            Check("non-slide-path-exact", SecondaryDifference(legacySwing.Snapshot(), optSwing.Snapshot()) == 0f);
            var legacy = Rig(true, false, Vector3.right * .08f, Quaternion.identity);
            var toggled = Rig(true, true, Vector3.right * .08f, Quaternion.identity);
            Setting(toggled).mass = 1; toggled.advance(step, 0);
            Setting(toggled).mass = 0; ((HairDynamicsSystem)toggled.solver).useAuthoredSlideDynamics = false;
            ((HairDynamicsSystem)toggled.solver).ResetSimulation();
            Check("disable-reset-restores-legacy", SecondaryDifference(legacy.Snapshot(), toggled.Snapshot()) == 0f);
            var replay = Rig(true, true, Vector3.zero, Quaternion.identity);
            var repeat = Rig(true, true, Vector3.zero, Quaternion.identity);
            foreach (var f in new[] { replay, repeat })
            {
                var s = Setting(f); s.mass = .4f; s.damping = .4f; s.stiffness = .2f; s.spring = .1f;
                s.limitInfo = new SwingLimitInfo { useLimit = 1,
                    axisX = new Vector2Int(-15, 15), axisY = new Vector2Int(-10, 10), axisZ = new Vector2Int(-10, 10) };
                ((HairDynamicsSystem)f.solver).ResetSimulation();
            }
            var trajectory = new List<float[]>(); float errorReplay = 0;
            for (int frame = 0; frame < 180; frame++)
            {
                replay.Pose(frame); repeat.Pose(frame);
                replay.advance(step, frame * step); repeat.advance(step, frame * step);
                trajectory.Add(replay.Snapshot());
                errorReplay = Mathf.Max(errorReplay, SecondaryDifference(replay.Snapshot(), repeat.Snapshot()));
            }
            Check("animated-three-second-exact-replay", errorReplay == 0f, errorReplay);
            replay.root.position = Vector3.zero; replay.root.rotation = Quaternion.identity;
            ((HairDynamicsSystem)replay.solver).ResetSimulation(); float errorReset = 0;
            for (int frame = 0; frame < 180; frame++)
            { replay.Pose(frame); replay.advance(step, frame * step); errorReset = Mathf.Max(errorReset, SecondaryDifference(replay.Snapshot(), trajectory[frame])); }
            Check("reset-full-trajectory-exact", errorReset == 0f, errorReset);
        }

        private void VerifyDisabledDynamicColliders(Report report)
        {
            var resolve = typeof(HairDynamicsSystem).GetMethod("ResolveCollision", BindingFlags.Instance | BindingFlags.NonPublic);
            var option = typeof(HairDynamicsSystem).GetField("respectDisabledDynamicColliders");
            foreach (bool authored in new[] { false, true })
            foreach (int mask in new[] { 0, -1, 256 })
            {
                string suffix = "-authored-" + authored + "-mask-" + mask;
                var fixture = CreateSecondaryFixture("swing"); fixture.automatic(false);
                var solver = (HairDynamicsSystem)fixture.solver;
                if (authored)
                {
                    var shape = fixture.root.gameObject.AddComponent<ActorSwingStaticBone>();
                    shape.staticCollider = new SwingCollider { type = 0, float_A = .1f, collisionMask = -1 };
                    solver.Initialize(fixture.root, fixture.root);
                }
                object node = null;
                foreach (object item in (System.Collections.IEnumerable)DynamicField(solver, "_nodes"))
                    if (DynamicField(item, "parent") != null) { node = item; break; }
                var setting = (ActorSwingDynamicBone)DynamicField(node, "setting");
                setting.dynamicCollider = new SwingCollider { type = 4, float_A = .02f, float_B = .05f, collisionMask = mask };
                Vector3 candidate = new Vector3(.025f, 0, 0);
                Vector3 Resolve() => (Vector3)resolve.Invoke(solver, new object[] { node, Vector3.zero, candidate, .08f });
                if (option != null) option.SetValue(solver, false);
                Vector3 legacy = Resolve();
                FrameworkCheck(report, "disabled-collider-legacy-counterexample" + suffix, (legacy - candidate).magnitude > .01f, (legacy - candidate).magnitude);
                if (option != null) option.SetValue(solver, true);
                float error = (Resolve() - candidate).magnitude;
                FrameworkCheck(report, "disabled-collider-no-phantom-contact" + suffix, error < 1e-6f, error);
                setting.dynamicCollider.type = 0;
                float positive = (Resolve() - candidate).magnitude;
                FrameworkCheck(report, "disabled-collider-active-sphere-positive" + suffix, positive > .01f, positive);
                setting.dynamicCollider.type = 4;
                if (option != null) option.SetValue(solver, false);
                error = (Resolve() - legacy).magnitude;
                FrameworkCheck(report, "disabled-collider-legacy-restoration" + suffix, error == 0f, error);
            }
            var fresh = Own(new GameObject("Generated collider default")).AddComponent<HairDynamicsSystem>();
            FrameworkCheck(report, "disabled-collider-default-off", option != null && !(bool)option.GetValue(fresh));
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
