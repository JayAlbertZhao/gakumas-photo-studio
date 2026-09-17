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
