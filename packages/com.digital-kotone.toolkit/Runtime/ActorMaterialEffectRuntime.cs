using System;
using System.Collections.Generic;
using Campus.Common;
using UnityEngine;
using VL;

namespace GakumasPhotoMode
{
    /// <summary>Facial-motion texture overrides sampled from the owning motion clock.</summary>
    public sealed class ActorMaterialEffectRuntime : IDisposable
    {
        private readonly BundleCatalog _catalog;
        private readonly Renderer[] _renderers;
        private readonly Dictionary<string, MotionDefine> _definitions =
            new Dictionary<string, MotionDefine>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Binding> _bindings = new List<Binding>();
        private string _motion;
        private MotionDefine _definition;
        public int ActiveMaterialCount { get; private set; }

        private sealed class Binding
        {
            public Renderer renderer;
            public int slot;
            public readonly List<MotionEffect> effects = new List<MotionEffect>();
            public ActorTextureOverride appliedProperty;
            public Material original, replacement;
        }

        public ActorMaterialEffectRuntime(Renderer[] renderers, BundleCatalog catalog)
        {
            _renderers = renderers ?? Array.Empty<Renderer>();
            _catalog = catalog;
        }

        public void Sample(string motion, double seconds, bool repeat)
        {
            if (!string.Equals(_motion, motion, StringComparison.OrdinalIgnoreCase))
            {
                Clear();
                _motion = motion;
                _definition = null;
                if (!string.IsNullOrEmpty(motion) && _catalog != null)
                {
                    if (!_definitions.TryGetValue(motion, out _definition))
                    {
                        // Some body-only motions have no facial MotionDefine.
                        try
                        {
                            MotionDefine[] values = _catalog.LoadAll<MotionDefine>(motion);
                            if (values.Length == 1) _definition = values[0];
                            else if (values.Length > 1)
                                Debug.LogWarning("[MaterialEffect] Ambiguous facial definition: " + motion);
                        }
                        catch (KeyNotFoundException)
                        {
                            Debug.LogWarning("[MaterialEffect] Motion bundle unavailable: " + motion);
                        }
                        _definitions[motion] = _definition;
                    }
                    Bind(_definition == null ? null : _definition.effects);
                }
            }
            double time = seconds;
            AnimationClip clip = _definition == null || _definition.baseAnimation == null
                ? null : _definition.baseAnimation.clip;
            if (repeat && clip != null && clip.length > 0 && IsFinite(time) && time >= 0)
                time %= clip.length;
            Apply(time);
        }

        // Also usable with authored effects without requiring an asset catalog.
        public void Bind(IEnumerable<MotionEffect> effects)
        {
            Clear();
            if (effects == null) return;
            foreach (MotionEffect timing in effects)
            {
                var effect = timing == null ? null : timing.effect as CampusActorMaterialEffect;
                ActorTextureOverride property = effect == null ? null : effect.overrideProperty;
                if (property == null || string.IsNullOrEmpty(property.materialName) ||
                    property.tileX < 1 || property.tileX > 64 || property.tileY < 1 || property.tileY > 64 ||
                    property.tileFPS < 1 || property.tileFPS > 240 ||
                    !IsFinite(timing.startTime) || !IsFinite(timing.duration) ||
                    timing.startTime < 0 || timing.duration < 0) continue;
                foreach (Renderer renderer in _renderers)
                {
                    if (renderer == null) continue;
                    Material[] materials = renderer.sharedMaterials;
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        Material material = materials[slot];
                        if (material == null || material.shader != MaterialRepairer.FallbackShader() ||
                            !MatchesMaterial(material.name, property.materialName)) continue;
                        Binding binding = _bindings.Find(value => value.renderer == renderer && value.slot == slot);
                        if (binding == null)
                        {
                            binding = new Binding {renderer=renderer, slot=slot, original=material};
                            _bindings.Add(binding);
                        }
                        binding.effects.Add(timing);
                    }
                }
            }
        }

        public static bool MatchesMaterial(string actual, string requested)
        {
            return !string.IsNullOrEmpty(actual) && !string.IsNullOrEmpty(requested) &&
                (string.Equals(actual, requested, StringComparison.Ordinal) ||
                actual.StartsWith(requested + "_", StringComparison.Ordinal));
        }

        public static Vector4 TextureFrame(int columns, int rows, int fps, double seconds)
        {
            if (columns < 1 || columns > 64 || rows < 1 || rows > 64 || fps < 1 || fps > 240 ||
                !IsFinite(seconds) || seconds < 0) return Vector4.zero;
            int count = columns * rows;
            double elapsedFrames = Math.Floor(seconds * fps);
            if (!IsFinite(elapsedFrames)) return Vector4.zero;
            int frame = (int)(elapsedFrames % count);
            return new Vector4(1f / columns, 1f / rows,
                (float)(frame % columns) / columns, (float)(rows - 1 - frame / columns) / rows);
        }

        public void Apply(double time)
        {
            ActiveMaterialCount = 0;
            foreach (Binding binding in _bindings)
            {
                MotionEffect selected = null;
                foreach (MotionEffect timing in binding.effects)
                    if (IsFinite(time) && time >= timing.startTime &&
                        (timing.duration == 0 || time < (double)timing.startTime + timing.duration))
                        selected = timing; // Last active serialized entry owns an overlapping slot.
                if (selected == null) { Release(binding); continue; }
                ActorTextureOverride property = ((CampusActorMaterialEffect)selected.effect).overrideProperty;
                if (property != binding.appliedProperty) Release(binding);
                if (binding.renderer == null) continue;
                Material[] materials = binding.renderer.sharedMaterials;
                if (binding.slot >= materials.Length) { Release(binding); continue; }
                if (binding.replacement == null)
                {
                    binding.original = materials[binding.slot];
                    if (binding.original == null) continue;
                    binding.replacement = new Material(binding.original) { name=binding.original.name + "__sequence" };
                    if (property.col != null) binding.replacement.SetTexture("_MainTex", property.col);
                    if (property.sdw != null) binding.replacement.SetTexture("_ShadeTex", property.sdw);
                    if (property.def != null) binding.replacement.SetTexture("_DefTex", property.def);
                    binding.appliedProperty = property;
                    materials[binding.slot] = binding.replacement;
                    binding.renderer.sharedMaterials = materials;
                }
                // A costume/script that replaced the slot retains ownership.
                if (materials[binding.slot] != binding.replacement) continue;
                binding.replacement.SetVector("_ActorTextureFrame", TextureFrame(property.tileX,
                    property.tileY, property.tileFPS, time - selected.startTime));
                ActiveMaterialCount++;
            }
        }

        private static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        private static void Release(Binding binding)
        {
            if (binding.replacement == null) return;
            if (binding.renderer != null)
            {
                Material[] materials = binding.renderer.sharedMaterials;
                if (binding.slot < materials.Length && materials[binding.slot] == binding.replacement)
                {
                    materials[binding.slot] = binding.original;
                    binding.renderer.sharedMaterials = materials;
                }
            }
            UnityEngine.Object.Destroy(binding.replacement);
            binding.replacement = null;
            binding.appliedProperty = null;
        }

        public void Clear()
        {
            foreach (Binding binding in _bindings) Release(binding);
            _bindings.Clear();
            ActiveMaterialCount = 0;
        }

        public void Dispose() { Clear(); _definitions.Clear(); }
    }
}
