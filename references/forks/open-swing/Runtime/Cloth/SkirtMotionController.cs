using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenSwing
{
    /// <summary>
    /// Small, asset-independent adapter for skirt root drivers and the shared swing solver.
    /// The caller controls animation sampling and the order of each simulation step.
    /// </summary>
    public sealed class SkirtMotionController : MonoBehaviour
    {
        [Serializable]
        private sealed class DriverConfiguration
        {
            public SkirtRootMath.Setting[] drivers = Array.Empty<SkirtRootMath.Setting>();
        }

        private struct DriverBinding
        {
            public Transform bone;
            public Transform reference;
            public SkirtRootMath.Setting setting;
        }

        private DriverBinding[] drivers = Array.Empty<DriverBinding>();
        private GameObject solverHost;
        private ActorAnimationSwingSolver solver;
        private FixedStepSwingClock clock;

        public ActorAnimationSwingSolver Solver => solver;
        public bool IsBound { get; private set; }

        /// <summary>
        /// Bind a rig and a JSON TextAsset. The same JSON contains "drivers" for skirt roots
        /// and "nodes" / "chains" / "staticColliders" for ActorAnimationSwingSolver.
        /// Bind before the first simulation frame and again after replacing the rig.
        /// </summary>
        public void Bind(Transform rigRoot, TextAsset configuration,
            Transform rootMotionSource, bool enableDynamics = true)
        {
            if (!rigRoot) throw new ArgumentNullException(nameof(rigRoot));
            if (!configuration) throw new ArgumentNullException(nameof(configuration));

            Release();
            var namedBones = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var bone in rigRoot.GetComponentsInChildren<Transform>(true))
            {
                if (!namedBones.TryAdd(bone.name, bone))
                    throw new InvalidOperationException("Ambiguous bone name: " + bone.name);
            }

            var parsed = JsonUtility.FromJson<DriverConfiguration>(configuration.text);
            if (parsed == null) throw new InvalidOperationException("Invalid skirt configuration JSON.");
            var settings = parsed.drivers ?? Array.Empty<SkirtRootMath.Setting>();
            var nextDrivers = new DriverBinding[settings.Length];
            for (int i = 0; i < settings.Length; i++)
            {
                var setting = settings[i];
                if (setting == null || setting.rotationOrder != 0)
                    throw new InvalidOperationException("Unsupported skirt root driver at index " + i);
                if (string.IsNullOrEmpty(setting.bone) ||
                    !namedBones.TryGetValue(setting.bone, out var driven))
                    throw new InvalidOperationException("Missing driven bone: " + setting.bone);
                if (string.IsNullOrEmpty(setting.reference) ||
                    !namedBones.TryGetValue(setting.reference, out var reference))
                    throw new InvalidOperationException("Missing reference bone: " + setting.reference);
                nextDrivers[i] = new DriverBinding
                {
                    bone = driven,
                    reference = reference,
                    setting = setting
                };
            }

            if (enableDynamics)
            {
                // Initialize on an inactive host so the solver receives every binding
                // before its Awake callback can run.
                solverHost = new GameObject("Skirt swing solver");
                solverHost.SetActive(false);
                solverHost.transform.SetParent(rigRoot, false);
                try
                {
                    solver = solverHost.AddComponent<ActorAnimationSwingSolver>();
                    solver.configuration = configuration;
                    solver.bindings = namedBones;
                    solver.rootMotionSource = rootMotionSource ? rootMotionSource : rigRoot;
                    solver.manualSimulation = true;
                    solver.enableQuartzHair = false;
                    solver.enableWind = false;
                    solverHost.SetActive(true);
                    solver.Initialize();
                    if (solver.SimulatedNodes == 0)
                        throw new InvalidOperationException("No skirt segments were bound.");
                    clock = new FixedStepSwingClock(solver, rigRoot);
                }
                catch
                {
                    if (solverHost)
                    {
                        solverHost.SetActive(false);
                        solverHost.transform.SetParent(null, false);
                        Destroy(solverHost);
                    }
                    solverHost = null;
                    solver = null;
                    clock = null;
                    throw;
                }
            }

            drivers = nextDrivers;
            IsBound = true;
        }

        /// <summary>Call before sampling a new animation pose.</summary>
        public void RestorePose()
        {
            if (solver) solver.RestorePose();
        }

        /// <summary>Call after animation and clothing-bone synchronization.</summary>
        public void Step(float deltaTime)
        {
            if (!IsBound) throw new InvalidOperationException("Bind a rig before stepping.");
            foreach (var driver in drivers)
                driver.bone.localRotation = SkirtRootMath.Calculate(
                    driver.setting.initialReferenceRotation,
                    driver.reference.localRotation,
                    driver.setting);

            if (!solver) return;
            clock.Advance(deltaTime);
        }

        /// <summary>Call after a teleport or other discontinuity in the rig root.</summary>
        public void RequestReset()
        {
            if (solver) solver.RequestReset();
            if (clock != null) clock.Reset();
        }

        /// <summary>Release the previous rig before replacing or destroying it.</summary>
        public void Release()
        {
            if (solver) solver.RestorePose();
            if (solverHost)
            {
                solverHost.SetActive(false);
                solverHost.transform.SetParent(null, false);
                Destroy(solverHost);
            }
            solverHost = null;
            solver = null;
            clock = null;
            drivers = Array.Empty<DriverBinding>();
            IsBound = false;
        }

        private void OnDestroy()
        {
            Release();
        }
    }
}
