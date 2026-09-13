using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Independent, explicitly sampled prefab effects. No catalog or application clock.</summary>
    public sealed class MotionEffectSequence : IDisposable
    {
        [Serializable]
        public sealed class Entry
        {
            public string id;
            public GameObject prefab;
            public Transform attachment;
            public double startSeconds;
            public double durationSeconds = 1;
            public uint seed = 1;
            public Vector3 positionOffset;
            public Vector3 rotationDegrees;
            public Vector3 scale = Vector3.one;
            internal Entry Copy() => (Entry)MemberwiseClone();
        }

        private sealed class Slot
        {
            public Entry entry;
            public GameObject instance;
            public ParticleSystem[] particles;
        }

        public const int MaxEntries = 32;
        public const int MaxSystemsPerEntry = 16;
        public const int MaxSimulationSteps = 60000;
        public const double StepSeconds = 1.0 / 60;
        private Slot[] slots = Array.Empty<Slot>();
        private double duration;
        private bool loop, disposed;
        public int ActiveCount { get; private set; }
        public string LastError { get; private set; }

        /// <summary>Copies entries, borrows templates/attachments. Failure unbinds and clears old effects.</summary>
        public bool TryBind(Entry[] entries, double durationSeconds, bool repeat = false)
        {
            Clear();
            if (disposed) return Fail("Sequence is disposed.");
            try
            {
                Require(Finite(durationSeconds) && durationSeconds > 0 && durationSeconds <= 3600, "Duration must be in (0,3600].");
                Require(entries != null && entries.Length <= MaxEntries, "Missing entries or capacity exceeded.");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var next = new Slot[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    Require(entry != null && !string.IsNullOrWhiteSpace(entry.id) && entry.id.Length <= 128 && ids.Add(entry.id), "Missing or duplicate effect ID.");
                    Require(Finite(entry.startSeconds) && entry.startSeconds >= 0 && Finite(entry.durationSeconds) &&
                        entry.durationSeconds > 0 && entry.durationSeconds <= 60 && entry.startSeconds + entry.durationSeconds <= durationSeconds, "Invalid effect interval.");
                    Require(entry.seed != 0 && Finite(entry.positionOffset) && Finite(entry.rotationDegrees) &&
                        Finite(entry.scale) && entry.scale.x != 0 && entry.scale.y != 0 && entry.scale.z != 0, "Invalid seed or offset.");
                    Require(entry.attachment != null, "Missing explicit attachment.");
                    ValidateTemplate(entry.prefab);
                    Require(!entry.attachment.IsChildOf(entry.prefab.transform), "Attachment must be outside the template hierarchy.");
                    next[i] = new Slot { entry = entry.Copy() };
                }
                slots = next; duration = durationSeconds; loop = repeat; LastError = null;
                return true;
            }
            catch (ArgumentException error) { return Fail(error.Message); }
        }

        /// <summary>Absolute time. Intervals are [start,end); negative time is inactive, repeat wraps nonnegative time.</summary>
        public bool TrySample(double seconds)
        {
            if (disposed) return Fail("Sequence is disposed.");
            try
            {
                Require(Finite(seconds) && Math.Abs(seconds) <= 1e12, "Invalid sample time.");
                double time = loop && seconds >= 0 ? seconds % duration : seconds;
                long work = 0;
                // Validate before activating any slot. An invalid current resource removes all owned instances.
                foreach (var slot in slots)
                {
                    var e = slot.entry;
                    Require(e.attachment != null && e.prefab != null, "Attachment or template was destroyed.");
                    Require(!e.attachment.IsChildOf(e.prefab.transform), "Attachment must be outside the template hierarchy.");
                    Require(Finite(e.attachment.localToWorldMatrix), "Nonfinite attachment matrix.");
                    if (!Active(e, time)) continue;
                    int systems = ValidateTemplate(e.prefab);
                    work += (long)(Math.Ceiling((time - e.startSeconds) / StepSeconds) + 1) * systems;
                }
                Require(work <= MaxSimulationSteps, "Simulation step budget exceeded.");
                ActiveCount = 0;
                foreach (var slot in slots)
                {
                    var e = slot.entry;
                    if (!Active(e, time)) { Release(slot); continue; }
                    if (slot.instance == null) Spawn(slot);
                    var transform = slot.instance.transform;
                    transform.SetParent(e.attachment, false);
                    transform.localPosition = e.positionOffset;
                    transform.localRotation = Quaternion.Euler(e.rotationDegrees);
                    transform.localScale = e.scale;
                    slot.instance.SetActive(true);
                    double elapsed = time - e.startSeconds;
                    foreach (var particle in slot.particles)
                    {
                        Require(particle != null, "Owned particle hierarchy was modified.");
                        if (!particle.gameObject.activeInHierarchy) continue;
                        particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                        particle.Simulate(0, false, true, false);
                        int steps = (int)Math.Floor(elapsed / StepSeconds);
                        for (int step = 0; step < steps; step++) particle.Simulate((float)StepSeconds, false, false, false);
                        double remainder = elapsed - steps * StepSeconds;
                        if (remainder > 0) particle.Simulate((float)remainder, false, false, false);
                        particle.Pause(false);
                    }
                    ActiveCount++;
                }
                LastError = null;
                return true;
            }
            catch (ArgumentException error) { return Fail(error.Message); }
        }

        private static bool Active(Entry e, double time) => e.attachment.gameObject.activeInHierarchy &&
            time >= e.startSeconds && time < e.startSeconds + e.durationSeconds;

        /// <summary>Borrowed, read-only instance for rendering/inspection. Do not mutate it or parent foreign objects under it.</summary>
        public GameObject GetInstance(string id)
        {
            foreach (var slot in slots) if (slot.entry.id == id) return slot.instance;
            return null;
        }

        /// <summary>Stable across owners, seeks, object IDs and sequence ordering. Zero is never returned.</summary>
        public static uint SystemSeed(uint seed, int hierarchyOrdinal)
        {
            unchecked
            {
                uint value = seed + 0x9e3779b9u * ((uint)hierarchyOrdinal + 1);
                value = (value ^ (value >> 16)) * 0x85ebca6bu;
                value = (value ^ (value >> 13)) * 0xc2b2ae35u;
                value ^= value >> 16;
                return value == 0 ? 1u : value;
            }
        }

        private static void Spawn(Slot slot)
        {
            // A disabled staging parent prevents play-on-awake before the clone's private settings are installed.
            var staging = new GameObject("MotionEffect staging") { hideFlags = HideFlags.DontSave };
            staging.SetActive(false);
            try
            {
                slot.instance = UnityEngine.Object.Instantiate(slot.entry.prefab, staging.transform, false);
                slot.instance.name = "MotionEffect " + slot.entry.id;
                slot.instance.hideFlags = HideFlags.DontSave;
                slot.instance.SetActive(false);
                slot.particles = slot.instance.GetComponentsInChildren<ParticleSystem>(true);
                for (int i = 0; i < slot.particles.Length; i++)
                {
                    var ps = slot.particles[i]; var main = ps.main;
                    main.playOnAwake = false; main.stopAction = ParticleSystemStopAction.None;
                    main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                    ps.useAutoRandomSeed = false; ps.randomSeed = SystemSeed(slot.entry.seed, i);
                }
                slot.instance.transform.SetParent(slot.entry.attachment, false);
            }
            finally { DestroyOwned(staging); }
        }

        private static int ValidateTemplate(GameObject template)
        {
            Require(template != null, "Missing template.");
            int systems = 0; long particles = 0;
            foreach (var component in template.GetComponentsInChildren<Component>(true))
            {
                Require(component != null, "Missing script in template.");
                Require(component is Transform || component is MeshFilter || component is MeshRenderer ||
                    component is ParticleSystemRenderer || component is ParticleSystem, "Only passive meshes and local particle systems are supported.");
                if (component is Transform transform) Require(Finite(transform.localToWorldMatrix), "Nonfinite template transform.");
                if (!(component is ParticleSystem ps)) continue;
                systems++; var main = ps.main; particles += main.maxParticles;
                Require(main.simulationSpace == ParticleSystemSimulationSpace.Local && !main.prewarm &&
                    Zero(main.gravityModifier), "Local, non-prewarmed, gravity-free particles are required.");
                Require(!ps.collision.enabled && !ps.trigger.enabled && !ps.externalForces.enabled && !ps.subEmitters.enabled &&
                    !ps.inheritVelocity.enabled, "Physics, external forces, inherited velocity and subemitters require a history provider.");
                Require(Zero(ps.emission.rateOverDistance), "Distance emission requires emitter history.");
                Require(!ps.trails.enabled || !ps.trails.worldSpace, "World-space trails require emitter history.");
                Require(!ps.velocityOverLifetime.enabled || ps.velocityOverLifetime.space == ParticleSystemSimulationSpace.Local, "Velocity must use local space.");
                Require(!ps.forceOverLifetime.enabled || ps.forceOverLifetime.space == ParticleSystemSimulationSpace.Local, "Force must use local space.");
                Require(!ps.shape.enabled || (ps.shape.shapeType != ParticleSystemShapeType.MeshRenderer &&
                    ps.shape.shapeType != ParticleSystemShapeType.SkinnedMeshRenderer), "Animated emission geometry requires a history provider.");
            }
            Require(systems <= MaxSystemsPerEntry && particles <= 65536, "Particle capacity exceeded.");
            return systems;
        }

        /// <summary>Hides and destroys instances, retains the copied binding for a later seek.</summary>
        public void Stop()
        {
            foreach (var slot in slots) Release(slot);
            ActiveCount = 0;
        }

        public void Clear() { Stop(); slots = Array.Empty<Slot>(); duration = 0; loop = false; LastError = null; }
        public void Dispose() { Clear(); disposed = true; }
        private bool Fail(string reason) { Stop(); LastError = reason; return false; }
        private static void Release(Slot slot)
        {
            if (slot.instance != null) { slot.instance.SetActive(false); DestroyOwned(slot.instance); }
            slot.instance = null; slot.particles = null;
        }
        private static void DestroyOwned(GameObject value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value);
        }
        private static void Require(bool ok, string message) { if (!ok) throw new ArgumentException(message); }
        private static bool Zero(ParticleSystem.MinMaxCurve curve) => curve.mode == ParticleSystemCurveMode.Constant
            ? curve.constant == 0
            : curve.mode == ParticleSystemCurveMode.TwoConstants && curve.constantMin == 0 && curve.constantMax == 0;
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool Finite(Matrix4x4 m) { for (int i = 0; i < 16; i++) if (!Finite(m[i])) return false; return true; }
    }
}
