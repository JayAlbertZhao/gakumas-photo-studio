using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VL.FaceSystem;

namespace GakumasPhotoMode
{
    /// <summary>Explicit borrowed objects. Morph/decal values require exclusive ownership while a player is bound.</summary>
    public sealed class PerformanceBindings
    {
        public sealed class Morph
        {
            public SkinnedMeshRenderer renderer; public VLActorFaceModel faceDriver; public int index;
            private Mesh topology;
            private bool captured;
            internal Morph Copy() => (Morph)MemberwiseClone();
            internal void Capture() { topology = renderer != null ? renderer.sharedMesh : null; captured = true; }
            internal void Validate()
            {
                PerformanceClip.Require((renderer != null) != (faceDriver != null) && index >= 0 &&
                    (renderer != null ? renderer.sharedMesh != null && (!captured || topology != null && renderer.sharedMesh == topology) && index < renderer.sharedMesh.blendShapeCount : index < 192), "Missing/ambiguous morph target, replaced topology or invalid index.");
            }
            internal float Read() => renderer != null ? renderer.GetBlendShapeWeight(index) * .01f : faceDriver.GetWeight(index);
            internal void Write(float value) { if (renderer != null) { if (topology != null && renderer.sharedMesh == topology && index < topology.blendShapeCount) renderer.SetBlendShapeWeight(index, value * 100); } else if (faceDriver != null) faceDriver.SetWeight(index, value); }
            internal string Identity => (renderer != null ? renderer.GetInstanceID() : faceDriver.GetInstanceID()) + ":" + index;
        }
        public sealed class MaterialSlot { public Renderer renderer; public int slot; internal MaterialSlot Copy() => (MaterialSlot)MemberwiseClone(); }
        public readonly Dictionary<string, Morph> morphs = new Dictionary<string, Morph>(StringComparer.Ordinal);
        public readonly Dictionary<string, FaceDecalProjector> decals = new Dictionary<string, FaceDecalProjector>(StringComparer.Ordinal);
        public readonly Dictionary<string, Transform> attachments = new Dictionary<string, Transform>(StringComparer.Ordinal);
        public readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        public readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        public readonly Dictionary<string, MaterialSlot> materials = new Dictionary<string, MaterialSlot>(StringComparer.Ordinal);
    }

    /// <summary>Two explicit phases: pose/materials, host geometry/locators, then effects. No default Update or file loading.</summary>
    public sealed class PerformancePlayer : IDisposable
    {
        private sealed class MorphSlot { public PerformanceBindings.Morph target; public float original; }
        private sealed class DecalSlot { public FaceDecalProjector target; public FaceDecalPose original; public FaceDecalAnimation animation; }
        private sealed class MaterialSlot
        {
            public PerformanceBindings.MaterialSlot target; public Material original, owned;
            public readonly List<PerformanceClip.MaterialEffect> entries = new List<PerformanceClip.MaterialEffect>();
            public readonly List<Texture2D[]> textures = new List<Texture2D[]>();
        }
        private PerformanceClip clip;
        private readonly List<MorphSlot> morphs = new List<MorphSlot>();
        private readonly List<DecalSlot> decals = new List<DecalSlot>();
        private readonly List<MaterialSlot> materials = new List<MaterialSlot>();
        private readonly MotionEffectSequence effects = new MotionEffectSequence();
        private bool disposed, prepared; private double preparedTime;
        public string LastError { get; private set; }
        public int ActiveEffectCount => effects.ActiveCount;
        public GameObject GetEffectInstance(string id) => effects.GetInstance(id);
        private PerformancePlayer() { }

        public static bool TryCreate(PerformanceClip definition, PerformanceBindings bindings, out PerformancePlayer player, out string reason)
        {
            player = null; reason = null; var candidate = new PerformancePlayer();
            try
            {
                PerformanceClip.Require(definition != null && bindings != null, "Missing clip or bindings.");
                PerformanceClip.Require(definition.TryCopy(out candidate.clip, out reason), reason);
                candidate.Bind(bindings); player = candidate; return true;
            }
            catch (Exception error) { reason = error.Message; candidate.Dispose(); return false; }
        }
        private static T Resolve<T>(Dictionary<string, T> values, string id) where T : class
        {
            PerformanceClip.Require(values.TryGetValue(id, out var value) && value != null, "Unbound ID: " + id); return value;
        }
        private void Bind(PerformanceBindings bindings)
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in clip.morphs)
            {
                var target = Resolve(bindings.morphs, item.target).Copy(); target.Validate(); target.Capture();
                PerformanceClip.Require(identities.Add(target.Identity), "Multiple IDs own the same morph.");
                float original = target.Read(); PerformanceClip.Require(PerformanceClip.Range(original, -64, 64), "Invalid original morph weight.");
                morphs.Add(new MorphSlot { target = target, original = original });
            }
            identities.Clear();
            foreach (var item in clip.decals)
            {
                var target = Resolve(bindings.decals, item.target);
                PerformanceClip.Require(target != null && identities.Add(target.GetInstanceID().ToString()), "Multiple IDs own the same decal or target destroyed.");
                var pose = target.Snapshot().pose; FaceDecalRenderer.Validate(new FaceDecalProjector.Data(target.transform.localToWorldMatrix, pose));
                decals.Add(new DecalSlot { target = target, original = pose, animation = new FaceDecalAnimation { duration = clip.duration, tracks = item.tracks } });
            }
            identities.Clear(); var lookup = new Dictionary<string, MaterialSlot>(StringComparer.Ordinal);
            foreach (var item in clip.materials)
            {
                if (!lookup.TryGetValue(item.target, out var slot))
                {
                    var target = Resolve(bindings.materials, item.target).Copy();
                    PerformanceClip.Require(target.renderer != null && target.slot >= 0 && target.slot < target.renderer.sharedMaterials.Length &&
                        identities.Add(target.renderer.GetInstanceID() + ":" + target.slot), "Invalid/duplicate material slot.");
                    var original = target.renderer.sharedMaterials[target.slot];
                    PerformanceClip.Require(original != null && original.HasProperty("_ActorTextureFrame") && original.HasProperty("_MainTex") &&
                        original.HasProperty("_ShadeTex") && original.HasProperty("_DefTex"), "Material requires explicit ActorToon texture-frame contract.");
                    slot = new MaterialSlot { target = target, original = original }; lookup.Add(item.target, slot); materials.Add(slot);
                }
                string[] names = { item.colorTexture, item.shadeTexture, item.defTexture }; var textures = new Texture2D[3];
                for (int i = 0; i < 3; i++) if (!string.IsNullOrEmpty(names[i]))
                { textures[i] = Resolve(bindings.textures, names[i]); PerformanceClip.Require(textures[i] != null, "Texture destroyed."); }
                slot.entries.Add(item); slot.textures.Add(textures);
            }
            var entries = clip.effects.Select(e => new MotionEffectSequence.Entry { id = e.id, prefab = Resolve(bindings.prefabs, e.prefab),
                attachment = Resolve(bindings.attachments, e.attachment), startSeconds = e.start, durationSeconds = e.duration,
                seed = e.seed, positionOffset = e.position, rotationDegrees = e.rotation, scale = e.scale }).ToArray();
            PerformanceClip.Require(effects.TryBind(entries, clip.duration, clip.loop), effects.LastError);
        }

        /// <summary>Samples copied curves and material intervals. The host applies face/bone/locator geometry next.</summary>
        public bool TrySamplePose(double seconds)
        {
            prepared = false;
            try
            {
                PerformanceClip.Require(!disposed && clip != null && PerformanceClip.Range(seconds, -1e12, 1e12), "Disposed/unbound player or invalid clock.");
                if (seconds < 0) { Stop(); preparedTime = seconds; prepared = true; LastError = null; return true; }
                double time = clip.loop ? seconds % clip.duration : seconds;
                var weights = new float[morphs.Count]; var poses = new FaceDecalPose[decals.Count]; var chosen = new int[materials.Count];
                for (int i = 0; i < morphs.Count; i++)
                {
                    morphs[i].target.Validate(); weights[i] = PerformanceClip.Sample(clip.morphs[i].keys, time);
                    PerformanceClip.Require(PerformanceClip.Range(weights[i], -64, 64), "Morph curve overshoot.");
                }
                for (int i = 0; i < decals.Count; i++)
                {
                    PerformanceClip.Require(decals[i].target != null && decals[i].animation.TrySample(clip.decals[i].baseline, time, out poses[i], out _), "Invalid current decal or sampled pose.");
                    FaceDecalRenderer.Validate(new FaceDecalProjector.Data(decals[i].target.transform.localToWorldMatrix, poses[i]));
                }
                for (int i = 0; i < materials.Count; i++)
                {
                    var slot = materials[i]; var t = slot.target;
                    PerformanceClip.Require(t.renderer != null && t.slot < t.renderer.sharedMaterials.Length && slot.original != null &&
                        t.renderer.sharedMaterials[t.slot] == (slot.owned != null ? slot.owned : slot.original), "Material owner changed or disappeared.");
                    chosen[i] = -1;
                    for (int j = 0; j < slot.entries.Count; j++)
                    {
                        var e = slot.entries[j]; string[] ids = { e.colorTexture, e.shadeTexture, e.defTexture };
                        for (int k = 0; k < 3; k++) PerformanceClip.Require(string.IsNullOrEmpty(ids[k]) || slot.textures[j][k] != null, "Borrowed texture destroyed.");
                        if (time >= e.start && time < (double)e.start + e.duration) chosen[i] = j;
                    }
                }
                // Commit after every sampled channel and borrowed target has passed validation.
                for (int i = 0; i < morphs.Count; i++) morphs[i].target.Write(weights[i]);
                for (int i = 0; i < decals.Count; i++) PerformanceClip.Require(decals[i].target.TryApplyPose(poses[i], out var reason), reason);
                for (int i = 0; i < materials.Count; i++) ApplyMaterial(materials[i], chosen[i], time);
                preparedTime = seconds; prepared = true; LastError = null; return true;
            }
            catch (Exception error) { return Fail(error.Message); }
        }
        /// <summary>Call only after host geometry and current locator update. Consumes one successful pose phase.</summary>
        public bool TrySampleEffects()
        {
            if (disposed || !prepared) return Fail("A current successful pose phase is required.");
            prepared = false;
            if (!effects.TrySample(preparedTime)) return Fail(effects.LastError);
            LastError = null; return true;
        }
        private static void ApplyMaterial(MaterialSlot slot, int selected, double time)
        {
            if (selected < 0) { ReleaseMaterial(slot); return; }
            if (slot.owned == null)
            {
                slot.owned = new Material(slot.original) { name = slot.original.name + "__performance" };
                var array = slot.target.renderer.sharedMaterials; array[slot.target.slot] = slot.owned; slot.target.renderer.sharedMaterials = array;
            }
            string[] properties = { "_MainTex", "_ShadeTex", "_DefTex" };
            for (int k = 0; k < 3; k++) slot.owned.SetTexture(properties[k], slot.textures[selected][k] != null ? slot.textures[selected][k] : slot.original.GetTexture(properties[k]));
            var e = slot.entries[selected]; slot.owned.SetVector("_ActorTextureFrame", ActorMaterialEffectRuntime.TextureFrame(e.columns, e.rows, e.fps, time - e.start));
        }
        private static void ReleaseMaterial(MaterialSlot slot)
        {
            if (slot.owned == null) return;
            var target = slot.target;
            if (target.renderer != null)
            {
                var array = target.renderer.sharedMaterials;
                if (target.slot < array.Length && array[target.slot] == slot.owned) { array[target.slot] = slot.original; target.renderer.sharedMaterials = array; }
            }
            if (Application.isPlaying) UnityEngine.Object.Destroy(slot.owned); else UnityEngine.Object.DestroyImmediate(slot.owned);
            slot.owned = null;
        }
        private bool Fail(string error) { Stop(); LastError = error; return false; }
        public void Stop()
        {
            prepared = false; effects.Stop();
            foreach (var slot in morphs) slot.target.Write(slot.original);
            foreach (var slot in decals) if (slot.target != null) slot.target.TryApplyPose(slot.original, out _);
            foreach (var slot in materials) ReleaseMaterial(slot);
        }
        public void Dispose()
        {
            Stop(); effects.Dispose(); disposed = true; clip = null; morphs.Clear(); decals.Clear(); materials.Clear();
        }
    }
}
