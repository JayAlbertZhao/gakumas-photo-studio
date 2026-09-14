using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    [Serializable]
    public sealed class CrowdShadowSettings
    {
        public bool enabled;
        public CrowdBackend backend = CrowdBackend.Auto;
        public bool allowCpuFallback = true;
        public CrowdMeshQuality meshQuality;
        [Range(1, 2048)] public int maximumResourceMiB = 256;
    }

    /// <summary>Caller-owned current shared-pose shadow geometry, independent of view LOD.
    /// Prepare before the consuming camera. Inputs are borrowed until all recorded draws finish.</summary>
    public sealed class CrowdShadowSource : IDisposable
    {
        // Same48-byte placement ABI as CrowdRenderer; no tint influence on geometric coverage.
        [StructLayout(LayoutKind.Sequential)] private struct Placement { public Vector4 positionScale, rotationType, tint; }
        private CrowdPrototypeGeometry[] geometry = Array.Empty<CrowdPrototypeGeometry>();
        private Material[] materials = Array.Empty<Material>();
        private int[] starts = Array.Empty<int>(), counts = Array.Empty<int>(), submeshes = Array.Empty<int>();
        private ComputeBuffer instances, arguments;
        private CrowdDefinition owner;
        private ulong contentVersion;
        private Shader shader;
        public bool IsPrepared { get; private set; }
        public ulong Revision { get; private set; }
        public CrowdBackend Backend { get; private set; }
        public string UnavailableReason { get; private set; }
        public string FallbackReason { get; private set; }
        public long ResourceBytes { get; private set; }
        public long TriangleCount { get; private set; }
        public int DrawCount { get; private set; }
        public int InstanceCount { get; private set; }
        public int AllocatedBuffers => geometry.Length * 6 + (instances != null ? 1 : 0) + (arguments != null ? 1 : 0);
        // Fixture inspection only; production never reads these GPU buffers back.
        internal ComputeBuffer IndirectArguments => arguments;
        internal ComputeBuffer Placements => instances;

        public bool TryPrepare(CrowdDefinition source, CrowdShadowSettings settings, CrowdPose[] poses = null)
        {
            Revision++; IsPrepared = false; UnavailableReason = FallbackReason = null;
            try
            {
                Require(settings != null && settings.enabled, "Disabled crowd shadow source");
                Require(source != null && source.prototypes != null && source.prototypes.Length >= 1 && source.prototypes.Length <= 8 &&
                    source.instances != null && source.instances.Length > 0 && source.instances.Length <= 65536, "Crowd shadows require1..8 prototypes and1..65536 placements");
                Require(poses == null || poses.Length == 0 || poses.Length == source.prototypes.Length, "Crowd shadow pose count mismatch");
                Require((int)settings.backend >= 0 && (int)settings.backend <= 2 && (int)settings.meshQuality >= 0 && (int)settings.meshQuality <= 1 &&
                    settings.maximumResourceMiB >= 1 && settings.maximumResourceMiB <= 2048, "Invalid crowd shadow backend/quality/resource budget");
                Require(SystemInfo.supportsInstancing && SystemInfo.graphicsShaderLevel >= 45, "Crowd shadows require SM4.5 indirect instancing, including CPU fallback");
                bool gpu = SystemInfo.supportsComputeShaders && Resources.Load<ComputeShader>("CrowdGeometry") != null;
                Backend = settings.backend == CrowdBackend.Cpu ? CrowdBackend.Cpu : CrowdBackend.Gpu;
                if (Backend == CrowdBackend.Gpu && !gpu)
                { Require(settings.allowCpuFallback, "Crowd shadow compute unavailable and fallback disabled"); Backend = CrowdBackend.Cpu; FallbackReason = "Explicit CPU crowd shadow deformation fallback"; }
                if (shader == null) shader = Resources.Load<Shader>("CrowdShadowCaster");
                Require(shader != null && shader.isSupported, "Crowd shadow caster shader unavailable");
                int types = source.prototypes.Length; var meshes = new Mesh[types];
                long required = source.instances.Length * 48L + types * 20L;
                for (int i = 0; i < types; i++)
                {
                    var p = source.prototypes[i]; Require(p != null && p.material != null, "Missing crowd shadow prototype/material");
                    meshes[i] = settings.meshQuality == CrowdMeshQuality.High ? p.highMesh : p.lowMesh;
                    required += CrowdPrototypeGeometry.EstimateBytes(meshes[i]);
                    Require(p.submesh >= 0 && p.submesh < meshes[i].subMeshCount && meshes[i].GetTopology(p.submesh) == MeshTopology.Triangles &&
                        (int)p.cull >= 0 && (int)p.cull <= 2 && Range(p.alphaCutoff, 0, 1) && Range(p.material.alpha, 0, 1), "Invalid crowd shadow submesh/cull/alpha");
                    for (int c = 0; c < 4; c++) Require(Range(p.material.uvST[c], -1e6f, 1e6f), "Invalid crowd shadow UV transform");
                    var map = p.material.albedoMap;
                    Require(map == null || meshes[i].HasVertexAttribute(VertexAttribute.TexCoord0) && map.dimension == TextureDimension.Tex2D &&
                        (!(map is RenderTexture rt) || rt.IsCreated() && rt.antiAliasing == 1), "Crowd shadow alpha requires UV0 and a created non-MSAA2D texture");
                }
                Require(required <= settings.maximumResourceMiB * 1024L * 1024, "Crowd shadow GPU resource budget exceeded before allocation");
                var nextCounts = new int[types];
                foreach (var item in source.instances)
                {
                    Require(item != null && item.prototype >= 0 && item.prototype < types && Range(item.yawDegrees, -1e6f, 1e6f) &&
                        Range(item.scale, .0001f, 1000), "Invalid crowd shadow placement");
                    for (int c = 0; c < 3; c++) Require(Range(item.position[c], -1e6f, 1e6f), "Invalid crowd shadow position");
                    if (!item.hidden) nextCounts[item.prototype]++;
                }
                bool replace = owner != source || contentVersion != source.contentVersion || geometry.Length != types;
                for (int i = 0; !replace && i < types; i++) replace |= geometry[i].Mesh != meshes[i];
                if (replace)
                {
                    Release(); geometry = new CrowdPrototypeGeometry[types];
                    for (int i = 0; i < types; i++) geometry[i] = new CrowdPrototypeGeometry(meshes[i]);
                    owner = source; contentVersion = source.contentVersion;
                }
                for (int i = 0; i < types; i++) geometry[i].Snapshot(poses != null && poses.Length > 0 ? poses[i] : null);
                counts = nextCounts; starts = new int[types]; submeshes = new int[types];
                DrawCount = InstanceCount = 0; TriangleCount = 0;
                var args = new uint[types * 5];
                for (int i = 0; i < types; i++)
                {
                    int sub = submeshes[i] = source.prototypes[i].submesh; starts[i] = InstanceCount; InstanceCount += counts[i];
                    if (counts[i] > 0) DrawCount++;
                    uint indices = meshes[i].GetIndexCount(sub); TriangleCount += (long)(indices / 3) * counts[i];
                    args[i * 5] = indices; args[i * 5 + 1] = (uint)counts[i]; args[i * 5 + 2] = meshes[i].GetIndexStart(sub); args[i * 5 + 3] = meshes[i].GetBaseVertex(sub);
                    geometry[i].Allocate();
                }
                var data = new Placement[source.instances.Length]; var cursor = (int[])starts.Clone();
                foreach (var item in source.instances)
                {
                    if (item.hidden) continue; float yaw = item.yawDegrees * Mathf.Deg2Rad;
                    data[cursor[item.prototype]++] = new Placement { positionScale = new Vector4(item.position.x, item.position.y, item.position.z, item.scale),
                        rotationType = new Vector4(Mathf.Sin(yaw), Mathf.Cos(yaw), item.prototype, 0), tint = Vector4.one };
                }
                if (instances == null || !instances.IsValid() || instances.count != data.Length)
                { instances?.Dispose(); instances = new ComputeBuffer(data.Length, 48) { name = "Toolkit crowd shadow placements" }; }
                if (arguments == null || !arguments.IsValid() || arguments.count != args.Length)
                { arguments?.Dispose(); arguments = new ComputeBuffer(args.Length, 4, ComputeBufferType.IndirectArguments) { name = "Toolkit crowd shadow indirect arguments" }; }
                instances.SetData(data); arguments.SetData(args);
                if (materials.Length != types * 3)
                {
                    ReleaseMaterials(); materials = new Material[types * 3];
                    for (int i = 0; i < materials.Length; i++) materials[i] = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }
                for (int i = 0; i < types; i++) for (int mode = 0; mode < 3; mode++)
                {
                    var p = source.prototypes[i]; var m = materials[i * 3 + mode];
                    if (mode == 1) m.EnableKeyword("SCENE_SHADOW_POINT");
                    if (mode == 2) m.EnableKeyword("SCENE_SHADOW_ORTHOGRAPHIC");
                    m.SetFloat("_Cull", (int)p.cull); m.SetFloat("_ShadowAlpha", p.material.alpha); m.SetFloat("_ShadowCutoff", p.alphaCutoff);
                    m.SetTexture("_ShadowAlphaMap", p.material.albedoMap != null ? p.material.albedoMap : Texture2D.whiteTexture); m.SetVector("_ShadowUvST", p.material.uvST);
                    m.SetBuffer("_CrowdVertices", geometry[i].Current); m.SetBuffer("_CrowdInstances", instances); m.SetInt("_CrowdShadowStart", starts[i]);
                }
                ResourceBytes = required; IsPrepared = true; return true;
            }
            catch (Exception error) { UnavailableReason = error.Message; Release(); return false; }
        }
        internal void RecordGeometry(CommandBuffer commands)
        {
            Require(IsPrepared, "Crowd shadow source was disposed or failed preparation");
            for (int i = 0; i < geometry.Length; i++) if (counts[i] > 0) geometry[i].Record(commands, Backend == CrowdBackend.Gpu);
        }
        internal void RecordDraws(CommandBuffer commands, Matrix4x4 viewProjection, Vector4 pointOrigin, Vector4 depthPlane, float far, bool point, bool orthographic)
        {
            for (int i = 0; i < geometry.Length; i++) if (counts[i] > 0)
            {
                var block = new MaterialPropertyBlock(); block.SetMatrix("_ShadowViewProjection", viewProjection);
                block.SetVector("_ShadowPointOrigin", pointOrigin); block.SetVector("_ShadowDepthPlane", depthPlane); block.SetFloat("_ShadowFar", far);
                commands.DrawMeshInstancedIndirect(geometry[i].Mesh, submeshes[i], materials[i * 3 + (point ? 1 : orthographic ? 2 : 0)], 0, arguments, i * 20, block);
            }
        }
        private static bool Range(float v, float min, float max) => CrowdPrototypeGeometry.Finite(v) && v >= min && v <= max;
        private static void Require(bool condition, string error) { if (!condition) throw new ArgumentException(error); }
        private void ReleaseMaterials() { foreach (var m in materials) if (m != null) UnityEngine.Object.Destroy(m); materials = Array.Empty<Material>(); }
        private void Release()
        {
            foreach (var g in geometry) g?.Dispose(); geometry = Array.Empty<CrowdPrototypeGeometry>(); owner = null;
            instances?.Dispose(); arguments?.Dispose(); instances = arguments = null; ReleaseMaterials();
            IsPrepared = false; ResourceBytes = TriangleCount = 0; DrawCount = InstanceCount = 0;
        }
        public void Dispose() { Revision++; Release(); }
    }
}
