using System;
using System.Collections.Generic;
using System.Reflection;
using ActorAnimation;
using GakumasPhotoMode;
using OpenSwing;
using UnityEditor;
using UnityEngine;

public static class RuntimeDynamicsReplayProbe
{
    private sealed class Rig
    {
        public GameObject root;
        public Transform jointB;
        public Transform tip;
        public Transform end;
    }

    public static void Run()
    {
        var reference = CreateRig("ReferenceRig", false);
        var ours = CreateRig("IndependentRig", true);

        var referenceSolverObject = new GameObject("ReferenceSolver");
        referenceSolverObject.transform.SetParent(reference.root.transform, false);
        var referenceSolver = referenceSolverObject.AddComponent<ActorAnimationSwingSolver>();
        referenceSolver.configuration = new TextAsset(
            System.IO.File.ReadAllText("Assets/ExampleChain.json"));
        referenceSolver.bindings = Bind(reference);
        referenceSolver.manualSimulation = true;
        referenceSolver.Initialize();
        referenceSolver.CapturePose();

        var independentSolver = ours.root.AddComponent<HairDynamicsSystem>();
        independentSolver.automaticSimulation = false;
        independentSolver.windStrength = 0f;
        independentSolver.naturalWind = null;
        independentSolver.includeTerminalTransformSegments = true;
        independentSolver.useReferencePrewarmSchedule = true;
        independentSolver.useReferenceResetBoundary = true;
        // The synthetic fixture has no body-owned fallback colliders. Keep
        // head/chest null so the independent solver matches the reference's
        // empty static-collider set instead of colliding with its own root.
        independentSolver.Initialize(null, null, ours.root.transform);

        for (var i = 0; i < 8; i++)
        {
            referenceSolver.Step(ActorAnimationSwingSolver.NativeStep);
            independentSolver.AdvanceSimulation(ActorAnimationSwingSolver.NativeStep,
                (i + 1) * ActorAnimationSwingSolver.NativeStep);
        }

        var capsuleResult = RunInvalidCapsuleProbe();
        var skirtAngleDelta = RunSkirtRootMathProbe();
        referenceSolver.RequestReset();
        referenceSolver.Step(ActorAnimationSwingSolver.NativeStep);
        independentSolver.ResetSimulation();
        independentSolver.AdvanceSimulation(ActorAnimationSwingSolver.NativeStep,
            9 * ActorAnimationSwingSolver.NativeStep);
        var resetReferenceTips = referenceSolver.TipPositions();
        var resetIndependentTips = new[] { ours.jointB.position, ours.tip.position, ours.end.position };
        var resetTipDelta = MaxTipDelta(resetReferenceTips, resetIndependentTips);
        var resetFinite = referenceSolver.IsFinite && IsFinite(resetIndependentTips);

        var referenceTips = referenceSolver.TipPositions();
        var oursTips = new[] { ours.jointB.position, ours.tip.position, ours.end.position };
        var maxTipDelta = 0f;
        for (var i = 0; i < Math.Min(referenceTips.Length, oursTips.Length); i++)
            maxTipDelta = Mathf.Max(maxTipDelta, Vector3.Distance(referenceTips[i], oursTips[i]));
        var tipDelta0 = TipDelta(referenceTips, oursTips, 0);
        var tipDelta1 = TipDelta(referenceTips, oursTips, 1);
        var tipDelta2 = TipDelta(referenceTips, oursTips, 2);

        if (!referenceSolver.IsFinite || !IsFinite(oursTips) || referenceSolver.SimulatedNodes != 3 ||
            independentSolver.DynamicEntryCount != 3 ||
            independentSolver.SimulatedBoneCount != 3 ||
            independentSolver.TerminalProxyCount != 1 ||
            !capsuleResult.referenceThrows ||
            !capsuleResult.independentClamps ||
            !capsuleResult.independentStrictThrows ||
            skirtAngleDelta > 0.1f || !resetFinite)
            throw new InvalidOperationException(
                $"Runtime dynamics replay failed: referenceNodes={referenceSolver.SimulatedNodes} " +
                $"oursEntries={independentSolver.DynamicEntryCount} referenceFinite={referenceSolver.IsFinite} " +
                $"capsuleReferenceThrows={capsuleResult.referenceThrows} " +
                $"capsuleIndependentClamps={capsuleResult.independentClamps} " +
                $"skirtAngleDelta={skirtAngleDelta:R} resetFinite={resetFinite}");

        Debug.Log($"RUNTIME_DYNAMICS_REPLAY_OK referenceNodes={referenceSolver.SimulatedNodes} " +
            $"oursEntries={independentSolver.DynamicEntryCount} oursSegments={independentSolver.SimulatedBoneCount} " +
            $"terminalCandidates={independentSolver.TerminalTransformSegmentCandidateCount} " +
            $"terminalProxies={independentSolver.TerminalProxyCount} " +
            $"steps=8 tipDelta0={tipDelta0:R} tipDelta1={tipDelta1:R} tipDelta2={tipDelta2:R} " +
            $"maxTipDelta={maxTipDelta:R} step={ActorAnimationSwingSolver.NativeStep:R} " +
            $"capsuleInvalidAxis=reference-throws,independent-clamps,strict-throws " +
            $"skirtRootMathAngleDelta={skirtAngleDelta:R} resetFinite={resetFinite} " +
            $"resetMaxTipDelta={resetTipDelta:R}");
        UnityEngine.Object.DestroyImmediate(referenceSolverObject);
        UnityEngine.Object.DestroyImmediate(reference.root);
        UnityEngine.Object.DestroyImmediate(ours.root);
        EditorApplication.Exit(0);
    }

    private sealed class CapsuleProbeResult
    {
        public bool referenceThrows;
        public bool independentClamps;
        public bool independentStrictThrows;
    }

    private static CapsuleProbeResult RunInvalidCapsuleProbe()
    {
        var result = new CapsuleProbeResult();
        try
        {
            Vector3 ignoredA;
            Vector3 ignoredB;
            ActorAnimationSwingSolver.NativeCapsuleEndpoints(
                Vector3.zero, new Vector3(7f, 1f, 0f), 0.02f, out ignoredA, out ignoredB);
        }
        catch (InvalidOperationException)
        {
            result.referenceThrows = true;
        }

        var root = new GameObject("IndependentInvalidCapsuleRig");
        var collider = root.AddComponent<ActorSwingStaticBone>();
        collider.staticCollider = new SwingCollider
        {
            type = 1,
            vector3_A = Vector3.zero,
            vector3_B = new Vector3(7f, 1f, 0f),
            float_A = 0.02f,
            float_B = 0.02f,
            collisionMask = -1,
        };
        var solver = root.AddComponent<HairDynamicsSystem>();
        solver.automaticSimulation = false;
        try
        {
            solver.Initialize(root.transform, root.transform, root.transform);
            result.independentClamps = solver.StaticColliderCount == 1;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
        var strictRoot = new GameObject("IndependentStrictCapsuleRig");
        var strictCollider = strictRoot.AddComponent<ActorSwingStaticBone>();
        strictCollider.staticCollider = new SwingCollider
        {
            type = 1,
            vector3_A = Vector3.zero,
            vector3_B = new Vector3(7f, 1f, 0f),
            float_A = 0.02f,
            float_B = 0.02f,
            collisionMask = -1,
        };
        var strictSolver = strictRoot.AddComponent<HairDynamicsSystem>();
        strictSolver.automaticSimulation = false;
        strictSolver.useReferenceCapsuleAxisValidation = true;
        try
        {
            strictSolver.Initialize(strictRoot.transform, strictRoot.transform, strictRoot.transform);
        }
        catch (InvalidOperationException)
        {
            result.independentStrictThrows = true;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(strictRoot);
        }
        return result;
    }

    private static float RunSkirtRootMathProbe()
    {
        var initial = Quaternion.identity;
        var current = Quaternion.Euler(17f, -11f, 23f);
        var referenceSetting = new SkirtRootMath.Setting
        {
            rotationOrder = 0,
            innerCoefficient = new Vector3(1.1f, 0.8f, 1.2f),
            outerCoefficient = new Vector3(0.6f, 1.3f, 0.7f),
            limitMin = new Vector3(-20f, -15f, -30f),
            limitMax = new Vector3(20f, 15f, 30f),
        };
        var reference = SkirtRootMath.Calculate(initial, current, referenceSetting);

        var independentSetting = new QuartzSkirtSetting
        {
            rotationOrder = 0,
            innerCoefficient = referenceSetting.innerCoefficient,
            outerCoefficient = referenceSetting.outerCoefficient,
            limitMin = referenceSetting.limitMin,
            limitMax = referenceSetting.limitMax,
        };
        var method = typeof(HairDynamicsSystem).GetMethod(
            "EvaluateAuthoredSkirtHelper", BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("Independent skirt helper is missing");
        var independent = (Quaternion)method.Invoke(null,
            new object[] { initial, current, independentSetting });
        return Quaternion.Angle(reference, independent);
    }

    private static float MaxTipDelta(Vector3[] reference, Vector3[] independent)
    {
        var result = 0f;
        for (var i = 0; i < Math.Min(reference.Length, independent.Length); i++)
            result = Mathf.Max(result, Vector3.Distance(reference[i], independent[i]));
        return result;
    }

    private static float TipDelta(Vector3[] reference, Vector3[] independent, int index)
    {
        if (index >= reference.Length || index >= independent.Length) return float.NaN;
        return Vector3.Distance(reference[index], independent[index]);
    }

    private static Rig CreateRig(string name, bool attachIndependentSettings)
    {
        var root = new GameObject(name);
        var jointA = new GameObject("JointA").transform;
        jointA.SetParent(root.transform, false);
        jointA.localPosition = Vector3.zero;
        var jointB = new GameObject("JointB").transform;
        jointB.SetParent(jointA, false);
        jointB.localPosition = new Vector3(0f, 0.1f, 0f);
        var tip = new GameObject("Tip").transform;
        tip.SetParent(jointB, false);
        tip.localPosition = new Vector3(0f, 0.1f, 0f);
        var end = new GameObject("End").transform;
        end.SetParent(tip, false);
        end.localPosition = new Vector3(0f, 0.1f, 0f);

        if (attachIndependentSettings)
        {
            AddSetting(jointA, 0.18f, 0.12f, 0.08f, 0.05f);
            AddSetting(jointB, 0.20f, 0.10f, 0.08f, 0.04f);
            AddSetting(tip, 0.22f, 0.08f, 0.06f, 0.03f);
        }

        return new Rig { root = root, jointB = jointB, tip = tip, end = end };
    }

    private static void AddSetting(Transform bone, float damping, float stiffness, float spring, float mass)
    {
        var setting = bone.gameObject.AddComponent<ActorSwingDynamicBone>();
        setting.damping = damping;
        setting.stiffness = stiffness;
        setting.spring = spring;
        setting.mass = mass;
        setting.rootWeight = 0.75f;
        setting.pendulum = 0f;
        setting.wind = 0f;
        setting.useWindGlobalForce = false;
    }

    private static Dictionary<string, Transform> Bind(Rig rig)
    {
        return new Dictionary<string, Transform>
        {
            ["JointA"] = rig.root.transform.Find("JointA"),
            ["JointB"] = rig.jointB,
            ["Tip"] = rig.tip,
            ["End"] = rig.end,
        };
    }

    private static bool IsFinite(Vector3[] values)
    {
        foreach (var value in values)
            if (float.IsNaN(value.x) || float.IsInfinity(value.x) ||
                float.IsNaN(value.y) || float.IsInfinity(value.y) ||
                float.IsNaN(value.z) || float.IsInfinity(value.z)) return false;
        return true;
    }
}
