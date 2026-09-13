using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class CrowdPrototypeGeometry : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Vertex
        { public Vector4 position, normal, tangent, uv; }
        [StructLayout(LayoutKind.Sequential)] private struct Influence
        { public uint a, b, c, d; public Vector4 weight; }
        [StructLayout(LayoutKind.Sequential)] private struct Bone
        { public Vector4 x, y, z; }
        [StructLayout(LayoutKind.Sequential)] private struct Delta
        { public Vector4 position, normal, tangent; }
        public readonly Mesh Mesh;
        public readonly int VertexCount, BoneCount, ShapeCount;
        public readonly long BufferBytes;
        public ComputeBuffer Current { get; private set; }
        public Bounds PoseBounds { get; private set; }
        private ComputeBuffer restBuffer, influenceBuffer, boneBuffer, deltaBuffer, weightBuffer;
        private readonly Vertex[] rest, cpu;
        private readonly Influence[] influences;
        private readonly Matrix4x4[] bindposes, matrices;
        private readonly Bone[] bones;
        private readonly Delta[] deltas;
        private readonly float[] frameWeights, weights;
        private readonly Vector3[] shapeExtent;
        private readonly Bounds restBounds;
        private ComputeShader compute;
        public bool IsCreated => Current != null && Current.IsValid() && restBuffer != null && restBuffer.IsValid() &&
            influenceBuffer != null && influenceBuffer.IsValid() && boneBuffer != null && boneBuffer.IsValid() &&
            deltaBuffer != null && deltaBuffer.IsValid() && weightBuffer != null && weightBuffer.IsValid();

        internal static long EstimateBytes(Mesh mesh)
        {
            Require(mesh != null && mesh.isReadable && mesh.vertexCount > 0 && mesh.vertexCount <= 262144, "Readable crowd geometry requires1..262144 vertices");
            int bones = mesh.bindposes.Length, shapes = mesh.blendShapeCount;
            Require(bones <= 256 && shapes <= 32, "Crowd prototype supports at most256 bones and32 single-frame shapes");
            long deltas = Math.Max(1L, (long)mesh.vertexCount * shapes) * 48;
            Require(deltas <= 256L * 1024 * 1024, "Crowd morph topology exceeds256MiB");
            return (long)mesh.vertexCount * 160 + Math.Max(1, bones) * 48L + deltas + Math.Max(1, shapes) * 4L;
        }

        public CrowdPrototypeGeometry(Mesh mesh)
        {
            EstimateBytes(mesh);
            Require(mesh != null && mesh.isReadable && mesh.vertexCount > 0 && mesh.vertexCount <= 262144, "Readable crowd geometry requires1..262144 vertices");
            Require(mesh.HasVertexAttribute(VertexAttribute.Normal), "Crowd geometry requires normals");
            Mesh = mesh; VertexCount = mesh.vertexCount; bindposes = mesh.bindposes; BoneCount = bindposes.Length; ShapeCount = mesh.blendShapeCount;
            Require(BoneCount <= 256 && ShapeCount <= 32, "Crowd prototype supports at most256 bones and32 single-frame shapes");
            rest = new Vertex[VertexCount]; cpu = new Vertex[VertexCount]; influences = new Influence[VertexCount];
            var positions = mesh.vertices; var normals = mesh.normals; var tangents = mesh.tangents; var uv = mesh.uv; var uv2 = mesh.uv2;
            var sourceWeights = mesh.boneWeights; var bounds = new Bounds(positions[0], Vector3.zero);
            using (var counts = mesh.GetBonesPerVertex())
            {
                for (int i = 0; i < counts.Length; i++) Require(counts[i] <= 4 && (BoneCount > 0 || counts[i] == 0), "Crowd skin requires bindposes and does not silently truncate more than four influences");
                // Modern serialized meshes need not expose a legacy BoneWeight array.
                // Read their explicit variable stream without mutating or truncating it.
                if (BoneCount > 0 && counts.Length > 0)
                {
                    Require(counts.Length == VertexCount, "Crowd variable skin vertex count mismatch");
                    using (var flat = mesh.GetAllBoneWeights())
                    {
                        sourceWeights = new BoneWeight[VertexCount]; int cursor = 0;
                        for (int i = 0; i < VertexCount; i++)
                        {
                            var w = new BoneWeight();
                            for (int k = 0; k < counts[i]; k++)
                            {
                                Require(cursor < flat.Length, "Crowd variable skin influence count mismatch"); var influence = flat[cursor++];
                                if (k == 0) { w.boneIndex0 = influence.boneIndex; w.weight0 = influence.weight; }
                                if (k == 1) { w.boneIndex1 = influence.boneIndex; w.weight1 = influence.weight; }
                                if (k == 2) { w.boneIndex2 = influence.boneIndex; w.weight2 = influence.weight; }
                                if (k == 3) { w.boneIndex3 = influence.boneIndex; w.weight3 = influence.weight; }
                            }
                            sourceWeights[i] = w;
                        }
                        Require(cursor == flat.Length, "Crowd variable skin has unmatched influences");
                    }
                }
            }
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                foreach (int index in mesh.GetIndices(submesh)) Require(index >= 0 && index < VertexCount, "Crowd source index is outside authored vertex storage");
            Require(BoneCount > 0 || sourceWeights.Length == 0, "Crowd skin weights require matching bindposes");
            Require(BoneCount == 0 || sourceWeights.Length == VertexCount, "Crowd skin requires four explicit influences per vertex");
            for (int i = 0; i < VertexCount; i++)
            {
                Require(Finite(positions[i]) && Finite(normals[i]) && (tangents.Length == 0 || Finite(tangents[i])) &&
                    (uv.Length == 0 || Finite(uv[i])) && (uv2.Length == 0 || Finite(uv2[i])), "Nonfinite crowd vertex attributes");
                var p = positions[i]; bounds.Encapsulate(p);
                rest[i] = new Vertex { position = new Vector4(p.x, p.y, p.z, 1), normal = normals[i],
                    tangent = tangents.Length == VertexCount ? tangents[i] : new Vector4(1, 0, 0, 1),
                    uv = new Vector4(uv.Length == VertexCount ? uv[i].x : 0, uv.Length == VertexCount ? uv[i].y : 0,
                        uv2.Length == VertexCount ? uv2[i].x : 0, uv2.Length == VertexCount ? uv2[i].y : 0) };
                if (BoneCount == 0) continue;
                var w = sourceWeights[i]; var indices = new[] { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
                var value = new Vector4(w.weight0, w.weight1, w.weight2, w.weight3); float sum = value.x + value.y + value.z + value.w;
                Require(Finite(value) && sum > .000001f && Mathf.Abs(sum - 1) < .0001f, "Crowd skin weights must sum to one");
                for (int j = 0; j < 4; j++) Require(value[j] >= 0 && (value[j] == 0 || indices[j] >= 0 && indices[j] < BoneCount), "Invalid crowd bone influence");
                influences[i] = new Influence { a = (uint)Mathf.Max(0, indices[0]), b = (uint)Mathf.Max(0, indices[1]), c = (uint)Mathf.Max(0, indices[2]), d = (uint)Mathf.Max(0, indices[3]), weight = value };
            }
            restBounds = bounds; matrices = new Matrix4x4[BoneCount]; bones = new Bone[Mathf.Max(1, BoneCount)];
            frameWeights = new float[ShapeCount]; weights = new float[Mathf.Max(1, ShapeCount)]; shapeExtent = new Vector3[ShapeCount];
            // This bound is checked before the potentially large delta array, not after GPU allocation.
            Require((long)VertexCount * Math.Max(1, ShapeCount) * 48 <= 256L * 1024 * 1024, "Crowd morph topology exceeds256MiB");
            deltas = new Delta[Mathf.Max(1, VertexCount * ShapeCount)];
            for (int s = 0; s < ShapeCount; s++)
            {
                Require(mesh.GetBlendShapeFrameCount(s) == 1, "Crowd morphs require one explicit authored frame per shape");
                float frame = mesh.GetBlendShapeFrameWeight(s, 0); Require(Finite(frame) && frame > .001f, "Invalid crowd morph reference weight"); frameWeights[s] = frame;
                var dp = new Vector3[VertexCount]; var dn = new Vector3[VertexCount]; var dt = new Vector3[VertexCount]; mesh.GetBlendShapeFrameVertices(s, 0, dp, dn, dt);
                for (int i = 0; i < VertexCount; i++)
                {
                    Require(Finite(dp[i]) && Finite(dn[i]) && Finite(dt[i]), "Nonfinite crowd morph delta");
                    deltas[s * VertexCount + i] = new Delta { position = dp[i], normal = dn[i], tangent = dt[i] };
                    shapeExtent[s] = Vector3.Max(shapeExtent[s], new Vector3(Mathf.Abs(dp[i].x), Mathf.Abs(dp[i].y), Mathf.Abs(dp[i].z)));
                }
            }
            foreach (var bind in bindposes) Require(Affine(bind), "Invalid crowd bindpose");
            BufferBytes = EstimateBytes(mesh);
        }
        public void Allocate()
        {
            if (IsCreated) return; ReleaseBuffers();
            Current = New(VertexCount, 64, "current prototype vertices"); restBuffer = New(VertexCount, 64, "rest prototype vertices");
            influenceBuffer = New(VertexCount, 32, "four influences"); boneBuffer = New(bones.Length, 48, "current bone rows");
            deltaBuffer = New(deltas.Length, 48, "shape deltas"); weightBuffer = New(weights.Length, 4, "current shape weights");
            restBuffer.SetData(rest); influenceBuffer.SetData(influences); deltaBuffer.SetData(deltas);
        }
        public void Snapshot(CrowdPose pose)
        {
            Require(BoneCount == 0 || pose != null && pose.root != null && pose.bones != null && pose.bones.Length == BoneCount, "Crowd current rig bone count/root mismatch");
            Require(ShapeCount == 0 || pose != null && pose.blendShapeWeights != null && pose.blendShapeWeights.Length == ShapeCount, "Crowd current morph count mismatch");
            var inflated = restBounds; var grow = Vector3.zero;
            for (int s = 0; s < ShapeCount; s++)
            {
                weights[s] = pose.blendShapeWeights[s] / frameWeights[s]; Require(Finite(weights[s]) && Mathf.Abs(weights[s]) <= 64, "Invalid current crowd morph weight");
                grow += shapeExtent[s] * Mathf.Abs(weights[s]);
            }
            inflated.extents += grow; var result = inflated;
            if (BoneCount > 0)
            {
                var toLocal = pose.root.worldToLocalMatrix; Require(Affine(toLocal), "Invalid crowd root transform");
                bool first = true;
                for (int b = 0; b < BoneCount; b++)
                {
                    Require(pose.bones[b] != null, "Missing current crowd bone"); var m = toLocal * pose.bones[b].localToWorldMatrix * bindposes[b];
                    Require(Affine(m), "Invalid current crowd bone matrix"); matrices[b] = m;
                    bones[b] = new Bone { x = m.GetRow(0), y = m.GetRow(1), z = m.GetRow(2) };
                    for (int corner = 0; corner < 8; corner++)
                    {
                        var p = inflated.center + Vector3.Scale(inflated.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                        p = m.MultiplyPoint3x4(p); if (first) { result = new Bounds(p, Vector3.zero); first = false; } else result.Encapsulate(p);
                    }
                }
            }
            result.Expand(Mathf.Max(.0001f, result.size.magnitude * .0001f)); PoseBounds = result;
            Require(Finite(result.center) && Finite(result.extents) && result.extents.magnitude > 1e-6f && result.extents.magnitude < 1e5f, "Invalid current crowd conservative bounds");
        }
        public void Record(CommandBuffer commands, bool gpu)
        {
            if (!gpu) { DeformCpu(); return; }
            if (compute == null) compute = Resources.Load<ComputeShader>("CrowdGeometry");
            if (compute == null) throw new InvalidOperationException("Crowd deformation compute unavailable");
            boneBuffer.SetData(bones); weightBuffer.SetData(weights); int kernel = compute.FindKernel("Deform");
            commands.BeginSample("Toolkit crowd current shared prototype pose");
            commands.SetComputeIntParam(compute, "_CrowdVertexCount", VertexCount); commands.SetComputeIntParam(compute, "_CrowdBoneCount", BoneCount); commands.SetComputeIntParam(compute, "_CrowdShapeCount", ShapeCount);
            commands.SetComputeBufferParam(compute, kernel, "_CrowdRest", restBuffer); commands.SetComputeBufferParam(compute, kernel, "_CrowdCurrent", Current);
            commands.SetComputeBufferParam(compute, kernel, "_CrowdInfluences", influenceBuffer); commands.SetComputeBufferParam(compute, kernel, "_CrowdBones", boneBuffer);
            commands.SetComputeBufferParam(compute, kernel, "_CrowdDeltas", deltaBuffer); commands.SetComputeBufferParam(compute, kernel, "_CrowdWeights", weightBuffer);
            commands.DispatchCompute(compute, kernel, (VertexCount + 127) / 128, 1, 1); commands.EndSample("Toolkit crowd current shared prototype pose");
        }
        private void DeformCpu()
        {
            for (int i = 0; i < VertexCount; i++)
            {
                var v = rest[i]; Vector3 p = v.position, n = v.normal, t = v.tangent;
                for (int s = 0; s < ShapeCount; s++) { var d = deltas[s * VertexCount + i]; p += (Vector3)d.position * weights[s]; n += (Vector3)d.normal * weights[s]; t += (Vector3)d.tangent * weights[s]; }
                if (BoneCount > 0)
                {
                    var w = influences[i]; Vector3 sp = Vector3.zero, sn = Vector3.zero, st = Vector3.zero;
                    void Add(uint index, float weight) { if (weight <= 0) return; var m = matrices[index]; sp += m.MultiplyPoint3x4(p) * weight; sn += m.MultiplyVector(n) * weight; st += m.MultiplyVector(t) * weight; }
                    Add(w.a, w.weight.x); Add(w.b, w.weight.y); Add(w.c, w.weight.z); Add(w.d, w.weight.w); p = sp; n = sn; t = st;
                }
                v.position = new Vector4(p.x, p.y, p.z, 1); v.normal = n.normalized; t.Normalize(); v.tangent = new Vector4(t.x, t.y, t.z, v.tangent.w); cpu[i] = v;
            }
            Current.SetData(cpu);
        }
        internal static bool Affine(Matrix4x4 m)
        { for (int i = 0; i < 16; i++) if (!Finite(m[i]) || Mathf.Abs(m[i]) > 1e8f) return false; return m.m30 == 0 && m.m31 == 0 && m.m32 == 0 && m.m33 == 1 && Mathf.Abs(m.determinant) > 1e-12f; }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector2 v) => Finite(v.x) && Finite(v.y);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool Finite(Vector4 v) => Finite(v.x) && Finite(v.y) && Finite(v.z) && Finite(v.w);
        private static void Require(bool value, string reason) { if (!value) throw new ArgumentException(reason); }
        private static ComputeBuffer New(int count, int stride, string name) => new ComputeBuffer(count, stride) { name = "Toolkit crowd " + name };
        private void ReleaseBuffers()
        { Current?.Dispose(); restBuffer?.Dispose(); influenceBuffer?.Dispose(); boneBuffer?.Dispose(); deltaBuffer?.Dispose(); weightBuffer?.Dispose(); Current = restBuffer = influenceBuffer = boneBuffer = deltaBuffer = weightBuffer = null; }
        public void Dispose() => ReleaseBuffers();
    }
}
