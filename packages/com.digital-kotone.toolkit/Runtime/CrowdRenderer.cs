using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Owned current-pose crowd renderer. Sources are borrowed; no runtime GPU readback.</summary>
    public sealed class CrowdRenderer : IDisposable
    {
        /// <summary>Borrowed output; valid only until the next prepare or disposal of this renderer.</summary>
        public readonly struct Frame
        {
            public readonly RenderTexture linearEyeDepth;
            public readonly CrowdBackend backend;
            public readonly int instances, prototypes, indirectCommands, computeDispatches;
            public readonly long crowdResourceBytes, lightingBufferBytes;
            internal Frame(CrowdRenderer owner)
            {
                linearEyeDepth = owner.eye; backend = owner.Backend; instances = owner.definition.instances.Length;
                prototypes = owner.geometry.Length; indirectCommands = prototypes * 2;
                computeDispatches = owner.selection.Dispatches + (backend == CrowdBackend.Gpu ? prototypes : 0);
                crowdResourceBytes = owner.ResourceBytes; lightingBufferBytes = owner.lights.BufferBytes;
            }
        }
        private readonly CrowdSelection selection = new CrowdSelection();
        private readonly SceneForwardLightResources lights = new SceneForwardLightResources();
        private CrowdPrototypeGeometry[] geometry = Array.Empty<CrowdPrototypeGeometry>();
        private Material[] near = Array.Empty<Material>(), far = Array.Empty<Material>(), capture = Array.Empty<Material>();
        private readonly RenderTexture[] atlas = new RenderTexture[4];
        private RenderTexture eye;
        private Mesh quad;
        private Shader drawShader, captureShader;
        private ulong version;
        private CrowdDefinition definition, geometryOwner;
        private CrowdSettings settings;
        private Camera camera;
        private RenderTexture destination;
        private Vector4[] spheres;
        public CrowdBackend Backend { get; private set; }
        public string UnavailableReason { get; private set; }
        public string FallbackReason { get; private set; }
        // Owned crowd buffers / four float atlases / atlas depth / eye target. Shared lighting has separate budgets.
        public long ResourceBytes { get; private set; }
        public int AllocatedBuffers => selection.BufferCount + geometry.Length * (selection.IsCreated ? 6 : 0) + lights.AllocatedBuffers;
        public int AllocatedTargets => (eye != null ? 1 : 0) + (atlas[0] != null ? 4 : 0) + lights.ShadowTargetCount;
        internal CrowdSelection Selection => selection;
        internal RenderTexture Atlas(int index) => atlas[index];
        internal ComputeBuffer CurrentVertices(int index) => geometry[index].Current;
        internal Vector4[] PrototypeSpheres => (Vector4[])spheres.Clone();

        public bool TryRender(RenderTexture colorDepth, Camera view, CrowdDefinition source, CrowdSettings input, CrowdPose[] poses, out Frame frame)
        {
            frame = default;
            if (!Prepare(colorDepth, view, source, input, poses)) return false;
            var commands = new CommandBuffer { name = "Toolkit current crowd" };
            try { Record(commands); Graphics.ExecuteCommandBuffer(commands); frame = new Frame(this); return true; }
            catch (Exception error) { UnavailableReason = "Crowd execution failed: " + error.Message; Release(); return false; }
            finally { commands.Release(); }
        }

        internal bool Prepare(RenderTexture colorDepth, Camera view, CrowdDefinition source, CrowdSettings input, CrowdPose[] poses)
        {
            UnavailableReason = null; FallbackReason = null;
            try
            {
                Validate(colorDepth, view, source, input, poses);
                bool gpu = SystemInfo.supportsComputeShaders && Resources.Load<ComputeShader>("CrowdGeometry") != null && Resources.Load<ComputeShader>("CrowdSelection") != null;
                Backend = input.backend == CrowdBackend.Cpu ? CrowdBackend.Cpu : CrowdBackend.Gpu;
                if (Backend == CrowdBackend.Gpu && !gpu)
                {
                    Require(input.allowCpuFallback, "Crowd compute unavailable and explicit CPU fallback disabled");
                    Backend = CrowdBackend.Cpu; FallbackReason = "Crowd compute unavailable; explicit CPU pose/selection fallback";
                }
                int types = source.prototypes.Length;
                long required = CrowdSelection.Bytes(source.instances.Length, types) +
                    (long)(input.captureResolution * 4) * (input.captureResolution * types) * 68 + (long)colorDepth.width * colorDepth.height * 4;
                for (int i = 0; i < types; i++) required += CrowdPrototypeGeometry.EstimateBytes(MeshFor(source.prototypes[i], input.meshQuality));
                Require(required <= (long)input.maximumResourceMiB * 1024 * 1024, "Crowd aggregate owned GPU resource budget exceeded before allocation");
                bool replace = geometryOwner != source || version != source.contentVersion || geometry.Length != types;
                for (int i = 0; !replace && i < types; i++) replace |= geometry[i].Mesh != MeshFor(source.prototypes[i], input.meshQuality);
                if (replace)
                {
                    ReleaseGeometry(); geometry = new CrowdPrototypeGeometry[types];
                    for (int i = 0; i < types; i++) geometry[i] = new CrowdPrototypeGeometry(MeshFor(source.prototypes[i], input.meshQuality));
                    version = source.contentVersion; geometryOwner = source;
                }
                spheres = new Vector4[types];
                for (int i = 0; i < types; i++)
                {
                    geometry[i].Snapshot(poses != null && poses.Length > 0 ? poses[i] : null);
                    var b = geometry[i].PoseBounds; spheres[i] = new Vector4(b.center.x, b.center.y, b.center.z, b.extents.magnitude);
                }
                if (!lights.Prepare(view, input.lighting, colorDepth.width, colorDepth.height, out var lightError)) throw new ArgumentException(lightError);
                ResourceBytes = required; definition = source; settings = input; camera = view; destination = colorDepth;
                for (int i = 0; i < types; i++) geometry[i].Allocate();
                EnsureTargets(input.captureResolution, types, colorDepth.width, colorDepth.height);
                EnsureMaterials(types); EnsureQuad();
                var arguments = new uint[types * 10];
                for (int i = 0; i < types; i++)
                {
                    var p = source.prototypes[i]; var mesh = geometry[i].Mesh;
                    arguments[i * 10] = mesh.GetIndexCount(p.submesh); arguments[i * 10 + 2] = mesh.GetIndexStart(p.submesh); arguments[i * 10 + 3] = mesh.GetBaseVertex(p.submesh);
                    arguments[i * 10 + 5] = quad.GetIndexCount(0);
                }
                selection.Prepare(view, source.instances, spheres, arguments, input.meshBudget);
                for (int i = 0; i < types; i++) Bind(i);
                return true;
            }
            catch (Exception error) { UnavailableReason = error.Message; Release(); return false; }
        }

        private void Validate(RenderTexture target, Camera view, CrowdDefinition source, CrowdSettings input, CrowdPose[] poses)
        {
            Require(input != null && input.enabled, "Disabled crowd");
            Require(source != null && source.prototypes != null && source.instances != null && source.prototypes.Length >= 1 && source.prototypes.Length <= CrowdDefinition.MaximumPrototypes &&
                source.instances.Length > 0 && source.instances.Length <= CrowdDefinition.MaximumInstances, "Crowd requires1..8 prototypes and1..65536 instances; empty input releases resources");
            Require(poses == null || poses.Length == 0 || poses.Length == source.prototypes.Length, "Crowd pose collection must match prototype count");
            Require(target != null && target.IsCreated() && !Owns(target) && target.depth >= 16 && target.dimension == TextureDimension.Tex2D && target.antiAliasing == 1 &&
                !target.useDynamicScale && !target.sRGB && (target.format == RenderTextureFormat.ARGBFloat || target.format == RenderTextureFormat.ARGBHalf), "Crowd requires caller-owned fixed linear HDR2D target with native depth and no MSAA");
            Require(QualitySettings.activeColorSpace == ColorSpace.Linear && SystemInfo.supportsInstancing && SystemInfo.graphicsShaderLevel >= 45 && SystemInfo.supportedRenderTargetCount >= 4 &&
                SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat), "Crowd requires Linear, SM4.5 indirect instancing and four float MRTs even on CPU fallback");
            string lightError = SceneForwardLightResources.Validate(view, input.lighting, target.width, target.height); Require(lightError == null, lightError);
            Require(Vector(view.worldToCameraMatrix.inverse.MultiplyPoint(Vector3.zero), -1e8f, 1e8f), "Crowd camera origin exceeds finite distance-key coordinate domain");
            foreach (var plane in GeometryUtility.CalculateFrustumPlanes(view.projectionMatrix * view.worldToCameraMatrix))
                Require(Vector(plane.normal, -1e8f, 1e8f) && Range(plane.distance, -1e12f, 1e12f) && plane.normal.sqrMagnitude > 1e-12f, "Crowd frustum contains a degenerate or nonfinite plane");
            Require((int)input.backend >= 0 && (int)input.backend <= 2 && (int)input.meshQuality >= 0 && (int)input.meshQuality <= 1 && input.meshBudget >= 0 && input.meshBudget <= CrowdDefinition.MaximumInstances &&
                input.captureResolution >= 16 && input.captureResolution <= 512 && input.maximumResourceMiB >= 1 && input.maximumResourceMiB <= 2048, "Invalid crowd backend/quality/capture/budget");
            if (drawShader == null) drawShader = Resources.Load<Shader>("CrowdDraw");
            if (captureShader == null) captureShader = Resources.Load<Shader>("CrowdCapture");
            Require(drawShader != null && drawShader.isSupported && captureShader != null && captureShader.isSupported, "Crowd render/capture shaders unavailable");
            foreach (var p in source.prototypes)
            {
                Require(p != null && SceneDeferredCamera.Inputs(p.material) && Range(p.alphaCutoff, .000001f, 1) && p.receiverGroup >= 0 && p.receiverGroup <= 255 &&
                    (int)p.cull >= 0 && (int)p.cull <= 2 && (int)p.lighting >= 0 && (int)p.lighting <= 1 && Range(p.toonThreshold, 0, 1) && Range(p.toonShadow, 0, 1) && Range(p.toonSoftness, .001f, .5f), "Invalid crowd material/cutoff/cull/lighting inputs");
                var mesh = MeshFor(p, input.meshQuality);
                Require(mesh != null && mesh.isReadable && p.submesh >= 0 && p.submesh < mesh.subMeshCount && mesh.GetTopology(p.submesh) == MeshTopology.Triangles && mesh.GetIndexCount(p.submesh) > 0 &&
                    mesh.HasVertexAttribute(VertexAttribute.Normal) && (p.material.normalMap == null || mesh.HasVertexAttribute(VertexAttribute.Tangent)) &&
                    ((p.material.albedoMap == null && p.material.normalMap == null && p.material.mosMap == null && p.material.emissionMap == null) || mesh.HasVertexAttribute(VertexAttribute.TexCoord0)), "Crowd requires explicit selected readable triangle mesh and material UV/normal/tangent attributes");
                foreach (var texture in new[] { p.material.albedoMap, p.material.normalMap, p.material.mosMap, p.material.emissionMap })
                    Require(texture == null || texture.dimension == TextureDimension.Tex2D && (!(texture is RenderTexture rt) || rt.IsCreated() && rt.antiAliasing == 1 && !Owns(rt)), "Crowd material requires independent created non-MSAA2D textures");
                if (p.gi != null)
                {
                    Require(p.gi.source == SceneGiSource.None || p.gi.source == SceneGiSource.Probe, "Instanced crowd requires explicit per-prototype probe GI or None; renderer/UV lightmap lookup is not inferred");
                    Require(p.gi.Validate(null, mesh, out var error), error);
                }
            }
            foreach (var item in source.instances)
                Require(item != null && item.prototype >= 0 && item.prototype < source.prototypes.Length && Vector(item.position, -1e6f, 1e6f) && Range(item.yawDegrees, -1e6f, 1e6f) &&
                    Range(item.scale, .0001f, 1000) && Vector(item.tint, 0, 65504), "Invalid crowd placement, prototype, positive uniform scale or radiance tint");
        }

        private void Bind(int index)
        {
            var p = definition.prototypes[index];
            SceneDeferredCamera.BindInputs(capture[index], p.material); capture[index].SetFloat("_Cull", (int)p.cull); capture[index].SetFloat("_Cutoff", p.alphaCutoff);
            capture[index].SetBuffer("_CrowdVertices", geometry[index].Current);
            foreach (var m in new[] { near[index], far[index] })
            {
                lights.Bind(m); m.SetMatrix("_CrowdView", camera.worldToCameraMatrix);
                m.SetVector("_CrowdViewRight", camera.worldToCameraMatrix.inverse.MultiplyVector(Vector3.right).normalized);
                m.SetFloat("_Cull", (int)p.cull); m.SetFloat("_Cutoff", p.alphaCutoff); m.SetFloat("_ReceiverGroup", p.receiverGroup); m.SetFloat("_Additive", 0);
                m.SetVector("_ForwardToon", new Vector4(p.toonThreshold, p.toonSoftness, p.toonShadow, p.lighting == CrowdLighting.Toon ? 1 : 0));
                m.SetInt("_CrowdCapacity", selection.Capacity); m.SetInt("_CrowdType", index);
                m.SetVector("_CrowdAtlasSize", new Vector4(atlas[0].width, atlas[0].height, settings.captureResolution, geometry.Length));
                m.SetBuffer("_CrowdInstances", selection.Instances); m.SetBuffer("_CrowdIndices", selection.Indices); m.SetBuffer("_CrowdBounds", selection.Bounds); m.SetBuffer("_CrowdVertices", geometry[index].Current);
                m.SetFloat("_SceneGiMode", 0); if (p.gi != null && !p.gi.Bind(m, null, out var error)) throw new ArgumentException(error);
            }
            SceneDeferredCamera.BindInputs(near[index], p.material); near[index].SetInt("_CrowdBucket", index * 2);
            var f = far[index]; f.SetInt("_CrowdBucket", index * 2 + 1);
            f.SetTexture("_AlbedoMap", atlas[0]); f.SetTexture("_CrowdNormalDepth", atlas[1]); f.SetTexture("_MosMap", atlas[2]); f.SetTexture("_EmissionMap", atlas[3]);
            f.SetVector("_Albedo", Vector3.one); f.SetVector("_Mos", Vector3.one); f.SetVector("_Emission", Vector3.one); f.SetVector("_UvST", new Vector4(1, 1, 0, 0));
            f.SetFloat("_HasNormal", 0); f.SetFloat("_Alpha", 1); f.SetFloat("_Cutoff", .5f);
        }

        internal void Record(CommandBuffer commands)
        {
            bool gpu = Backend == CrowdBackend.Gpu;
            lights.Record(commands);
            foreach (var g in geometry) g.Record(commands, gpu);
            selection.Record(commands, gpu);
            commands.BeginSample("Toolkit crowd four current material views");
            var mrt = new RenderTargetIdentifier[] { atlas[0], atlas[1], atlas[2], atlas[3] };
            commands.SetRenderTarget(mrt, atlas[0]); commands.SetViewport(new Rect(0, 0, atlas[0].width, atlas[0].height)); commands.ClearRenderTarget(true, true, Color.clear);
            var directions = new[] { Vector3.back, Vector3.right, Vector3.forward, Vector3.left };
            for (int i = 0; i < geometry.Length; i++) for (int direction = 0; direction < 4; direction++)
            {
                var b = geometry[i].PoseBounds; float radius = spheres[i].w; var forward = directions[direction];
                var pose = Matrix4x4.TRS(b.center + forward * radius * 2, Quaternion.LookRotation(-forward, Vector3.up), Vector3.one);
                var view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * pose.inverse;
                var projection = Matrix4x4.Ortho(-radius, radius, -radius, radius, radius * .1f, radius * 4);
                var block = new MaterialPropertyBlock(); block.SetMatrix("_CrowdCaptureVP", GL.GetGPUProjectionMatrix(projection, true) * view);
                block.SetVector("_CrowdCaptureCenter", b.center); block.SetVector("_CrowdCaptureDirection", forward);
                commands.SetViewport(new Rect(direction * settings.captureResolution, i * settings.captureResolution, settings.captureResolution, settings.captureResolution));
                commands.DrawMesh(geometry[i].Mesh, Matrix4x4.identity, capture[i], definition.prototypes[i].submesh, 0, block);
            }
            commands.EndSample("Toolkit crowd four current material views");
            commands.SetRenderTarget(eye); commands.SetViewport(new Rect(0, 0, eye.width, eye.height)); commands.ClearRenderTarget(false, true, Color.clear);
            commands.SetRenderTarget(new RenderTargetIdentifier[] { destination, eye }, destination);
            commands.BeginSample("Toolkit crowd current near and four-view indirect geometry");
            for (int i = 0; i < geometry.Length; i++)
            {
                commands.DrawMeshInstancedIndirect(geometry[i].Mesh, definition.prototypes[i].submesh, near[i], 0, selection.Arguments, i * 40);
                commands.DrawMeshInstancedIndirect(quad, 0, far[i], 1, selection.Arguments, i * 40 + 20);
            }
            commands.EndSample("Toolkit crowd current near and four-view indirect geometry");
            // Do not leave the private eye target bound for host transparent draws.
            commands.SetRenderTarget(destination);
        }

        private void EnsureTargets(int side, int types, int width, int height)
        {
            for (int i = 0; i < 4; i++)
                if (atlas[i] == null || !atlas[i].IsCreated() || atlas[i].width != side * 4 || atlas[i].height != side * types)
                { ReleaseTarget(ref atlas[i]); atlas[i] = Target(side * 4, side * types, i == 0 ? 24 : 0, RenderTextureFormat.ARGBFloat, "current material atlas " + i); }
            if (eye == null || !eye.IsCreated() || eye.width != width || eye.height != height)
            { ReleaseTarget(ref eye); eye = Target(width, height, 0, RenderTextureFormat.RFloat, "current crowd eye depth"); }
        }
        private void EnsureMaterials(int count)
        {
            if (near.Length != count) { ReleaseMaterials(); near = new Material[count]; far = new Material[count]; capture = new Material[count]; }
            for (int i = 0; i < count; i++)
            {
                if (near[i] == null) near[i] = Material(drawShader); if (far[i] == null) far[i] = Material(drawShader);
                if (capture[i] == null) capture[i] = Material(captureShader);
            }
        }
        private void EnsureQuad()
        {
            if (quad != null) return;
            quad = new Mesh { name = "Toolkit crowd owned impostor quad", hideFlags = HideFlags.HideAndDontSave };
            quad.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
            quad.triangles = new[] { 0, 2, 1, 0, 3, 2 }; quad.RecalculateBounds();
        }
        private static Material Material(Shader shader) => new Material(shader) { name = "Toolkit crowd owned " + shader.name, hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
        private static RenderTexture Target(int width, int height, int depth, RenderTextureFormat format, string name)
        {
            var texture = new RenderTexture(width, height, depth, format, RenderTextureReadWrite.Linear) { name = "Toolkit crowd " + name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (!texture.Create()) { Destroy(texture); throw new InvalidOperationException("Crowd render target allocation failed"); } return texture;
        }
        private bool Owns(RenderTexture target)
        { if (target == eye || lights.Owns(target)) return true; foreach (var a in atlas) if (a != null && target == a) return true; return false; }
        private static Mesh MeshFor(CrowdPrototype prototype, CrowdMeshQuality quality) => quality == CrowdMeshQuality.Low ? prototype.lowMesh : prototype.highMesh;
        private static bool Range(float v, float a, float b) => CrowdPrototypeGeometry.Finite(v) && v >= a && v <= b;
        private static bool Vector(Vector3 v, float a, float b) => Range(v.x, a, b) && Range(v.y, a, b) && Range(v.z, a, b);
        private static void Require(bool condition, string error) { if (!condition) throw new ArgumentException(error); }
        private static void Destroy(UnityEngine.Object value) { if (value != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); } }
        private static void ReleaseTarget(ref RenderTexture target) { if (target != null) { target.Release(); Destroy(target); } target = null; }
        private void ReleaseGeometry() { foreach (var g in geometry) g?.Dispose(); geometry = Array.Empty<CrowdPrototypeGeometry>(); geometryOwner = null; }
        private void ReleaseMaterials()
        { foreach (var m in near) Destroy(m); foreach (var m in far) Destroy(m); foreach (var m in capture) Destroy(m); near = far = capture = Array.Empty<Material>(); }
        private void Release()
        {
            selection.Dispose(); lights.Dispose(); ReleaseGeometry(); ReleaseMaterials();
            for (int i = 0; i < 4; i++) ReleaseTarget(ref atlas[i]); ReleaseTarget(ref eye); Destroy(quad); quad = null;
            ResourceBytes = 0; definition = null; settings = null; camera = null; destination = null; spheres = null;
        }
        public void Dispose() { Release(); UnavailableReason = "Disposed crowd"; }
    }
}
