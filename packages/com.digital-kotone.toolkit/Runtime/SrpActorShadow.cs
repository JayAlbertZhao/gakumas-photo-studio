using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit directional Actor self-shadow, independent of toon and scene lights.
    /// Background drop shadows consume ActorShadowInputs through the scene's own light settings.
    /// No camera render, global writes or Submit. Host owns GPU completion before reuse/disposal.</summary>
    public sealed class SrpActorShadow : IDisposable
    {
        public readonly struct Frame
        {
            private readonly SrpActorShadow owner;
            public readonly ulong sequence;
            public readonly RenderTexture depth;
            internal Frame(SrpActorShadow value) { owner = value; sequence = value.sequence; depth = value.atlas.Atlas; }
            public bool IsCurrent => owner != null && owner.Current(sequence);
            public void Bind(Material ownedMaterial)
            {
                if (ownedMaterial == null) throw new ArgumentNullException(nameof(ownedMaterial));
                if (!IsCurrent) throw new InvalidOperationException("Actor shadow ticket is no longer current");
                owner.atlas.BindActor(ownedMaterial, owner.recordedPlaneBias);
            }
        }
        private readonly SceneLightShadowAtlas atlas = new SceneLightShadowAtlas("Toolkit independent Actor self shadow");
        private readonly List<ActorForwardDrawSet.RendererState> states = new List<ActorForwardDrawSet.RendererState>();
        private readonly List<ShadowCastingMode> modes = new List<ShadowCastingMode>();
        private readonly HashSet<Texture> textures = new HashSet<Texture>();
        private CommandBuffer commands;
        private ulong sequence;
        private bool disposed, ready, hadDepth;
        private bool recordedPlaneBias;
        // Evaluate depth at each shadow texel's centre on the actual receiver
        // triangle plane. Disable only for comparison with constant-bias sampling.
        public bool ReceiverPlaneBias { get; set; } = true;
        public int CasterDrawCalls => atlas.CasterDrawCalls;
        private bool Current(ulong value)
        {
            if (disposed || !ready || value != sequence || (hadDepth && (atlas.Atlas == null || !atlas.Atlas.IsCreated()))) return false;
            for (int i = 0; i < states.Count; i++)
                if (!states[i].IsCurrent || states[i].renderer.shadowCastingMode != modes[i]) return false;
            foreach (var texture in textures) if (texture == null || (texture is RenderTexture rt && !rt.IsCreated())) return false;
            return true;
        }
        public bool TryRecord(ScriptableRenderContext context, Vector3 direction, SceneDirectionalShadowSettings settings,
            ulong value, out Frame frame, out string error)
        {
            frame = default; error = null;
            if (disposed || settings == null || !settings.enabled || value == 0 || value <= sequence || GraphicsSettings.currentRenderPipeline == null)
            { error = "Requires enabled SRP shadow settings and a positive monotonic sequence"; return false; }
            // A failed preparation retires prior tickets; no partial command buffer is submitted.
            ready = false; states.Clear(); modes.Clear(); textures.Clear();
            try
            {
                if (settings.casters == null) throw new ArgumentException("Missing explicit shadow casters");
                foreach (var caster in settings.casters)
                {
                    var invalid = SceneLightShadowAtlas.ValidateCaster(caster);
                    if (invalid != null) throw new ArgumentException(invalid);
                    if (caster.alphaMap != null && caster.alphaMap == atlas.Atlas) throw new ArgumentException("Actor shadow output feedback");
                    if (caster.alphaMap != null) textures.Add(caster.alphaMap);
                    var r = caster.renderer;
                    var mesh = r is SkinnedMeshRenderer skin ? skin.sharedMesh : r.GetComponent<MeshFilter>().sharedMesh;
                    states.Add(new ActorForwardDrawSet.RendererState {
                        renderer = r, enabled = r.enabled, forceOff = r.forceRenderingOff, active = r.gameObject.activeInHierarchy,
                        layer = r.gameObject.layer, staticBatch = r.isPartOfStaticBatch, included = true,
                        mesh = mesh, vertices = mesh.vertexCount, submeshes = mesh.subMeshCount, transform = r.localToWorldMatrix
                    });
                    modes.Add(r.shadowCastingMode);
                }
                if (settings.crowds != null && settings.crowds.Length != 0) throw new ArgumentException("Actor self-shadow accepts explicit renderer casters; use scene lights for crowd shadows");
                if (!atlas.PrepareDirectional(direction, settings, true, out error)) return false;
                if (commands == null) commands = new CommandBuffer { name = "Toolkit independent Actor self shadow" };
                commands.Clear(); atlas.Record(commands); context.ExecuteCommandBuffer(commands); commands.Clear();
                sequence = value; hadDepth = atlas.Atlas != null; recordedPlaneBias = ReceiverPlaneBias; ready = true; frame = new Frame(this); return true;
            }
            catch (Exception exception) { commands?.Clear(); error = "Actor shadow recording failed: " + exception.Message; return false; }
        }
        public void Dispose()
        { if (disposed) return; disposed = true; ready = false; commands?.Dispose(); commands = null; atlas.Dispose(); states.Clear(); modes.Clear(); textures.Clear(); }
    }
}
