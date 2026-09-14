using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit same-pixel render-pass scheduling for caller-owned SRP hosts.
    /// This does not enable URP NativeRenderPass or replace the Built-in scene renderer.</summary>
    public sealed class TileRenderPass : IDisposable
    {
        public enum BackendPolicy { RequireNative, AllowEmulation }
        public enum InitialContents { Clear, Load }

        [Serializable]
        public sealed class Attachment
        {
            public string name;
            public GraphicsFormat format = GraphicsFormat.R8G8B8A8_UNorm;
            // Null requests a transient attachment, memoryless only where supported.
            public RenderTexture target;
            public InitialContents initialContents;
            public Color clearColor = Color.clear;
            [Range(0, 1)] public float clearDepth = 1;
            public bool store;
        }

        public sealed class Draw
        {
            // Exactly one geometry source. All objects and property blocks are borrowed.
            public Mesh mesh;
            public Renderer renderer;
            public Matrix4x4 localToWorld = Matrix4x4.identity;
            public int submesh;
            public Material material;
            public int shaderPass;
            public MaterialPropertyBlock properties;
        }

        public sealed class Subpass
        {
            public string name;
            // Order defines SV_Target and framebuffer input indices, respectively.
            public int[] colors = Array.Empty<int>();
            public int[] inputs = Array.Empty<int>();
            public bool depthReadOnly;
            public Draw[] draws = Array.Empty<Draw>();
        }

        public sealed class Plan
        {
            public bool enabled;
            public int width, height;
            public BackendPolicy backend = BackendPolicy.RequireNative;
            public Attachment[] attachments = Array.Empty<Attachment>();
            public int depthAttachment = -1;
            public Subpass[] subpasses = Array.Empty<Subpass>();
            // Color tile budget is separate from depth. Packed HDR can occupy 64 tile bits.
            public bool chargePackedHdrAs64Bits = true;
            public int maximumColorTileBits = 256;
            public int maximumAttachmentMiB = 128;
            public int maximumDraws = 4096;
        }

        public readonly struct Submission
        {
            public readonly uint sequence;
            public readonly int subpasses, draws, colorTileBits, depthBits;
            public readonly long nominalBytes, transientBytes, storedBytes, loadedBytes;
            // API backend classification, not a claim about tile residency or measured cost.
            public readonly bool nativeApi;
            internal Submission(uint sequence, int subpasses, int draws, int colorBits, int depthBits,
                long bytes, long transient, long stored, long loaded, bool native)
            {
                this.sequence = sequence; this.subpasses = subpasses; this.draws = draws;
                colorTileBits = colorBits; this.depthBits = depthBits; nominalBytes = bytes;
                transientBytes = transient; storedBytes = stored; loadedBytes = loaded; nativeApi = native;
            }
        }

        private CommandBuffer _commands;
        private bool _disposed, _recording;
        private uint _sequence;

        public static bool HasNativeApi => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan ||
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal;

        /// <summary>Validates before recording anything. The caller owns a valid SRP context,
        /// camera setup, surrounding frame, output textures and the final context.Submit().</summary>
        public bool TryRecord(ScriptableRenderContext context, Plan plan, out Submission submission, out string error)
        {
            submission = default;
            if (_disposed || _recording) { error = "Disposed or reentrant tile renderer"; return false; }
            if (!Validate(plan, out var budget, out error)) return false;
            if (GraphicsSettings.currentRenderPipeline == null)
            { error = "Tile render passes require an explicitly selected SRP host"; return false; }
            if (_commands == null) _commands = new CommandBuffer { name = "Toolkit explicit tile render pass" };
            _recording = true;
            bool passOpen = false, subpassOpen = false;
            try
            {
                var attachments = new NativeArray<AttachmentDescriptor>(plan.attachments.Length, Allocator.Temp);
                try
                {
                    for (int i = 0; i < attachments.Length; i++)
                    {
                        var input = plan.attachments[i]; var descriptor = new AttachmentDescriptor(input.format);
                        if (input.target != null)
                            descriptor.ConfigureTarget(input.target, input.initialContents == InitialContents.Load, input.store);
                        if (input.initialContents == InitialContents.Clear)
                            descriptor.ConfigureClear(input.clearColor, input.clearDepth, 0);
                        attachments[i] = descriptor;
                    }
                    context.BeginRenderPass(plan.width, plan.height, 1, attachments, plan.depthAttachment);
                    passOpen = true;
                }
                finally { attachments.Dispose(); }
                foreach (var subpass in plan.subpasses)
                {
                    using (var colors = new NativeArray<int>(subpass.colors, Allocator.Temp))
                    using (var inputs = new NativeArray<int>(subpass.inputs, Allocator.Temp))
                    {
                        context.BeginSubPass(colors, inputs, subpass.depthReadOnly, subpass.depthReadOnly);
                        subpassOpen = true;
                    }
                    _commands.Clear();
                    _commands.BeginSample(subpass.name ?? "Toolkit tile subpass");
                    foreach (var draw in subpass.draws)
                    {
                        if (draw.renderer != null)
                            _commands.DrawRenderer(draw.renderer, draw.material, draw.submesh, draw.shaderPass);
                        else _commands.DrawMesh(draw.mesh, draw.localToWorld, draw.material, draw.submesh, draw.shaderPass, draw.properties);
                    }
                    _commands.EndSample(subpass.name ?? "Toolkit tile subpass");
                    context.ExecuteCommandBuffer(_commands);
                    context.EndSubPass(); subpassOpen = false;
                }
                context.EndRenderPass(); passOpen = false;
                submission = new Submission(++_sequence, plan.subpasses.Length, budget.draws, budget.colorTileBits,
                    budget.depthBits, budget.nominalBytes, budget.transientBytes, budget.storedBytes, budget.loadedBytes, HasNativeApi);
                return true;
            }
            finally
            {
                try { if (subpassOpen) context.EndSubPass(); }
                finally
                {
                    try { if (passOpen) context.EndRenderPass(); }
                    finally { _commands.Clear(); _recording = false; }
                }
            }
        }

        /// <summary>Descriptor estimates only. A valid plan has not rendered or saved bandwidth.</summary>
        public static bool Validate(Plan plan, out Submission budget, out string error)
        {
            budget = default; error = null;
            if (plan == null || !plan.enabled) { error = "Tile plan disabled"; return false; }
            if (plan.width < 1 || plan.height < 1 || plan.width > 4096 || plan.height > 4096 ||
                plan.attachments == null || plan.attachments.Length < 1 || plan.attachments.Length > 9 ||
                plan.subpasses == null || plan.subpasses.Length < 1 || plan.subpasses.Length > 16 ||
                plan.depthAttachment < -1 || plan.depthAttachment >= plan.attachments.Length ||
                plan.maximumColorTileBits < 32 || plan.maximumColorTileBits > 1024 ||
                plan.maximumAttachmentMiB < 1 || plan.maximumAttachmentMiB > 512 || plan.maximumDraws < 1 || plan.maximumDraws > 65536 ||
                (plan.backend != BackendPolicy.RequireNative && plan.backend != BackendPolicy.AllowEmulation))
            { error = "Invalid tile layout, dimensions or declared limits"; return false; }
            if (plan.backend == BackendPolicy.RequireNative && !HasNativeApi)
            { error = "Native render-pass policy requires Vulkan or Metal; emulation must be explicit"; return false; }
            var targets = new HashSet<Texture>();
            var initialized = new bool[plan.attachments.Length]; var used = new bool[plan.attachments.Length];
            int colorBits = 0, depthBits = 0, colorCount = 0, draws = 0;
            long total = 0, transient = 0, stored = 0, loaded = 0;
            for (int i = 0; i < plan.attachments.Length; i++)
            {
                var a = plan.attachments[i];
                if (a == null || a.format == GraphicsFormat.None || GraphicsFormatUtility.IsCompressedFormat(a.format) ||
                    !SystemInfo.IsFormatSupported(a.format, FormatUsage.Render) ||
                    GraphicsFormatUtility.IsDepthStencilFormat(a.format) != (i == plan.depthAttachment) ||
                    (a.initialContents != InitialContents.Clear && a.initialContents != InitialContents.Load) ||
                    !Finite(a.clearColor) || !Finite(a.clearDepth) || a.clearDepth < 0 || a.clearDepth > 1)
                { error = "Invalid or unsupported attachment descriptor"; return false; }
                int bits = checked((int)GraphicsFormatUtility.GetBlockSize(a.format) * 8);
                if (bits == 0) { error = "Attachment has no defined storage size"; return false; }
                if (i == plan.depthAttachment) depthBits = bits;
                else
                {
                    colorCount++;
                    colorBits += a.format == GraphicsFormat.B10G11R11_UFloatPack32 && plan.chargePackedHdrAs64Bits ? 64 : bits;
                }
                long bytes = (long)plan.width * plan.height * bits / 8; total += bytes;
                if (a.target == null)
                {
                    if (a.store || a.initialContents == InitialContents.Load)
                    { error = "Transient attachments cannot load or store external contents"; return false; }
                    transient += bytes;
                }
                else
                {
                    var target = a.target;
                    var format = i == plan.depthAttachment ? target.descriptor.depthStencilFormat : target.graphicsFormat;
                    if (!target.IsCreated() || target.width != plan.width || target.height != plan.height ||
                        target.dimension != TextureDimension.Tex2D || target.volumeDepth != 1 || target.antiAliasing != 1 ||
                        target.useDynamicScale || target.useMipMap || target.enableRandomWrite || target.memorylessMode != RenderTextureMemoryless.None ||
                        format != a.format || !targets.Add(target))
                    { error = "External attachment must be unique, created, exact-format fixed 2D without MSAA/mips/random-write/memoryless"; return false; }
                    if (a.store) stored += bytes;
                    if (a.initialContents == InitialContents.Load) loaded += bytes;
                }
                initialized[i] = true; // Explicit clear or valid external Load; no undefined holes.
            }
            if (colorCount > 8 || colorBits > plan.maximumColorTileBits || total > (long)plan.maximumAttachmentMiB * 1048576)
            { error = "Attachment count, color tile budget or nominal byte budget exceeded"; return false; }
            foreach (var subpass in plan.subpasses)
            {
                if (subpass == null || subpass.colors == null || subpass.inputs == null || subpass.draws == null ||
                    subpass.colors.Length > SystemInfo.supportedRenderTargetCount || subpass.inputs.Length > 8)
                { error = "Invalid subpass or unsupported color MRT count"; return false; }
                var colors = new HashSet<int>(); var inputs = new HashSet<int>();
                foreach (int index in subpass.colors)
                {
                    if (index < 0 || index >= initialized.Length || index == plan.depthAttachment || !colors.Add(index))
                    { error = "Invalid or duplicate subpass color index"; return false; }
                    used[index] = true;
                }
                foreach (int index in subpass.inputs)
                {
                    if (index < 0 || index >= initialized.Length || !inputs.Add(index) || colors.Contains(index) || !initialized[index])
                    { error = "Invalid, duplicate, uninitialized or simultaneous read/write input attachment"; return false; }
                    if (index == plan.depthAttachment && (!subpass.depthReadOnly || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan))
                    { error = "Depth input requires read-only Vulkan; D3D11 emulation is not validated; Metal needs a color depth attachment"; return false; }
                    used[index] = true;
                }
                if (plan.depthAttachment >= 0) used[plan.depthAttachment] = true;
                foreach (var draw in subpass.draws)
                {
                    if (++draws > plan.maximumDraws || draw == null || (draw.mesh == null) == (draw.renderer == null) ||
                        draw.material == null || draw.material.shader == null || !draw.material.shader.isSupported ||
                        draw.shaderPass < 0 || draw.shaderPass >= draw.material.passCount || draw.submesh < 0)
                    { error = "Invalid draw source, material pass or draw budget"; return false; }
                    Mesh mesh = draw.mesh;
                    if (draw.renderer != null)
                    {
                        var skin = draw.renderer as SkinnedMeshRenderer; var filter = draw.renderer.GetComponent<MeshFilter>();
                        mesh = skin != null ? skin.sharedMesh : draw.renderer is MeshRenderer && filter != null ? filter.sharedMesh : null;
                        if (draw.properties != null || draw.renderer.HasPropertyBlock() || !Matrix(draw.renderer.localToWorldMatrix))
                        { error = "Renderer draws require a finite transform and no implicit property block"; return false; }
                    }
                    else if (!Matrix(draw.localToWorld)) { error = "Invalid mesh transform"; return false; }
                    if (mesh == null || draw.submesh >= mesh.subMeshCount || mesh.GetTopology(draw.submesh) != MeshTopology.Triangles)
                    { error = "Draw requires a valid triangle submesh"; return false; }
                    foreach (string property in draw.material.GetTexturePropertyNames())
                    {
                        if (targets.Contains(draw.material.GetTexture(property)) ||
                            (draw.properties != null && targets.Contains(draw.properties.GetTexture(property))))
                        { error = "Attachment textures cannot also be ordinary sampled material inputs inside the render pass"; return false; }
                    }
                }
            }
            for (int i = 0; i < used.Length; i++) if (!used[i]) { error = "Unused attachment in declared tile budget"; return false; }
            budget = new Submission(0, plan.subpasses.Length, draws, colorBits, depthBits, total, transient, stored, loaded, HasNativeApi);
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Color value) => Finite(value.r) && Finite(value.g) && Finite(value.b) && Finite(value.a);
        private static bool Matrix(Matrix4x4 value)
        { for (int i = 0; i < 16; i++) if (!Finite(value[i])) return false; return Finite(value.determinant) && Mathf.Abs(value.determinant) > 1e-12f; }

        public void Dispose()
        {
            if (_recording) throw new InvalidOperationException("Cannot dispose a recording tile renderer");
            if (_disposed) return;
            _disposed = true; _commands?.Release(); _commands = null;
        }
    }
}
