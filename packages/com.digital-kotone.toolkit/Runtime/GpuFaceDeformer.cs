using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Independent sparse position morphs followed by four-influence skinning.
    /// Owns a cloned mesh; only its GPU vertex streams change. No runtime readback.
    /// Caller must stop drawing the borrowed Mesh on failure and before Dispose.</summary>
    public sealed class GpuFaceDeformer : IDisposable
    {
        [Serializable]
        public struct Delta
        {
            public int shape, vertex;
            public Vector3 position;
            public Delta(int shape, int vertex, Vector3 position)
            { this.shape = shape; this.vertex = vertex; this.position = position; }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Rest { public Vector3 position, normal; public Vector4 tangent; public float inverseWeight; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Entry { public uint shape; public Vector3 position; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Bone { public Vector4 x, y, z, state; }

        private Mesh mesh;
        private ComputeShader shader;
        private int kernel, vertexCount, shapeCount, boneCount;
        private readonly List<GraphicsBuffer> owned = new List<GraphicsBuffer>();
        private readonly GraphicsBuffer[] vertexBuffers = new GraphicsBuffer[4];
        private readonly GraphicsBuffer[] dummy = new GraphicsBuffer[4];
        private GraphicsBuffer restBuffer, entriesBuffer, startsBuffer, influenceBuffer, weightsBuffer, bonesBuffer;
        private Bone[] boneUpload;
        private Vector3[] shapeRadius;
        private Bounds restBounds;
        private int streamCount;
        private readonly int[] strides = new int[4];
        private readonly int[] streams = new int[3], offsets = new int[3];
        private static readonly VertexAttribute[] Attributes = { VertexAttribute.Position, VertexAttribute.Normal, VertexAttribute.Tangent };
        private bool disposed;

        public Mesh Mesh { get { return LastDispatchSucceeded && mesh != null ? mesh : null; } }
        public bool LastDispatchSucceeded { get; private set; }
        public string UnavailableReason { get; private set; }
        public long BufferBytes { get; private set; }
        public int DispatchCount { get; private set; }
        public int VertexCount { get { return vertexCount; } }

        /// <summary>Deltas must be in nondecreasing shape order; duplicates accumulate in
        /// supplied order. Packed influences are four uints per vertex: high 16 bits weight,
        /// low 16 bits bone index. Invalid indices and zero influences are skipped.</summary>
        public static bool TryCreate(Mesh source, int shapes, Delta[] deltas, uint[] influences,
            int bones, out GpuFaceDeformer result, out string reason)
        {
            result = null; reason = null;
            var value = new GpuFaceDeformer();
            try { value.Initialize(source, shapes, deltas, influences, bones); result = value; return true; }
            catch (Exception error) { reason = error.Message; value.Dispose(); return false; }
        }

        private void Initialize(Mesh source, int shapes, Delta[] deltas, uint[] influences, int bones)
        {
            if (!SystemInfo.supportsComputeShaders) throw new NotSupportedException("Compute shaders unavailable.");
            if (source == null || !source.isReadable || source.vertexCount < 1 || source.vertexCount > 262144)
                throw new ArgumentException("A readable mesh with 1..262144 vertices is required.");
            if (shapes < 0 || shapes > 4096 || bones < 0 || bones > 65536 || deltas == null || deltas.Length > 4194304)
                throw new ArgumentException("Face shape/bone/delta capacity exceeded or deltas absent.");
            vertexCount = source.vertexCount; shapeCount = shapes; boneCount = bones;
            if (bones != 0 && (influences == null || influences.Length != vertexCount * 4))
                throw new ArgumentException("Four packed influences per vertex are required.");
            var asset = Resources.Load<ComputeShader>("GpuFaceDeformer");
            if (asset == null) throw new NotSupportedException("GpuFaceDeformer compute resource unavailable.");
            shader = UnityEngine.Object.Instantiate(asset);
            kernel = shader.FindKernel("DeformFace");
            if (!shader.IsSupported(kernel)) throw new NotSupportedException("Face kernel unsupported.");
            // Never add Raw or MarkDynamic to a shared caller mesh.
            mesh = UnityEngine.Object.Instantiate(source);
            mesh.name = source.name + "__gpu_face";
            // GPU-owned writes need a GPU-writable vertex allocation. MarkDynamic is
            // a CPU-upload hint and breaks Raw UAV binding on this D3D11 path
            // (Unity UUM-9352). The lecture's Android-specific workaround is not
            // portable to this desktop backend.
            mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            streamCount = mesh.vertexBufferCount;
            if (streamCount < 1 || streamCount > 4) throw new NotSupportedException("Expected 1..4 vertex streams.");
            for (int s = 0; s < streamCount; s++) strides[s] = mesh.GetVertexBufferStride(s);
            for (int a = 0; a < Attributes.Length; a++)
            {
                var attr = Attributes[a];
                streams[a] = offsets[a] = -1;
                if (!mesh.HasVertexAttribute(attr))
                { if (a == 0) throw new ArgumentException("Position required."); continue; }
                if (mesh.GetVertexAttributeFormat(attr) != VertexAttributeFormat.Float32 ||
                    mesh.GetVertexAttributeDimension(attr) != (a == 2 ? 4 : 3))
                    throw new NotSupportedException("Position/normal/tangent must be Float32 with dimensions 3/3/4.");
                streams[a] = mesh.GetVertexAttributeStream(attr); offsets[a] = mesh.GetVertexAttributeOffset(attr);
                if ((offsets[a] & 3) != 0 || (strides[streams[a]] & 3) != 0)
                    throw new NotSupportedException("Deformation channels must be word aligned.");
            }
            long bytes = vertexCount * 44L + (vertexCount + 1L) * 4 + Math.Max(1, deltas.Length) * 16L +
                Math.Max(1, vertexCount * 4) * 4L + Math.Max(1, shapes) * 4L + Math.Max(1, bones) * 64L + 64;
            for (int s = 0; s < streamCount; s++) bytes += (long)vertexCount * strides[s];
            if (bytes > 256L * 1024 * 1024) throw new ArgumentException("Face vertex/data buffer budget exceeds 256 MiB.");
            BufferBytes = bytes;
            Vector3[] positions = source.vertices, normals = source.normals; Vector4[] tangents = source.tangents;
            var rest = new Rest[vertexCount]; var starts = new uint[vertexCount + 1];
            restBounds = new Bounds(positions[0], Vector3.zero);
            for (int v = 0; v < vertexCount; v++)
            {
                rest[v] = new Rest { position = positions[v], normal = streams[1] >= 0 ? normals[v] : Vector3.zero,
                    tangent = streams[2] >= 0 ? tangents[v] : Vector4.zero };
                RequireFinite(rest[v].position); RequireFinite(rest[v].normal); RequireFinite(rest[v].tangent);
                // Topology weights are immutable. Compute this scalar once with the same
                // float32 division as the CPU path, instead of a GPU approximate reciprocal
                // each frame: a sub-ULP position shift can flip a silhouette sample.
                float total = 0;
                if (bones > 0) for (int k = 0; k < 4; k++)
                {
                    uint packed = influences[v * 4 + k];
                    if ((packed & 65535u) < bones)
                        total = Float32(total + Float32((packed >> 16) * (1f / 65535f)));
                }
                rest[v].inverseWeight = total > 0 ? 1f / total : 0;
                restBounds.Encapsulate(positions[v]);
            }
            shapeRadius = new Vector3[shapes];
            var perVertex = new Dictionary<int, Vector3>(); int previousShape = -1;
            foreach (var delta in deltas)
            {
                if (delta.shape < previousShape || delta.shape < 0 || delta.shape >= shapes || delta.vertex < 0 || delta.vertex >= vertexCount)
                    throw new ArgumentException("Invalid delta index or non-shape-major order.");
                RequireFinite(delta.position);
                if (delta.shape != previousShape) perVertex.Clear();
                perVertex.TryGetValue(delta.vertex, out Vector3 radius);
                radius += Abs(delta.position); perVertex[delta.vertex] = radius;
                shapeRadius[delta.shape] = Vector3.Max(shapeRadius[delta.shape], radius);
                starts[delta.vertex + 1]++; previousShape = delta.shape;
            }
            for (int v = 1; v <= vertexCount; v++) starts[v] += starts[v - 1];
            var cursor = (uint[])starts.Clone(); var entries = new Entry[Math.Max(1, deltas.Length)];
            foreach (var delta in deltas) entries[cursor[delta.vertex]++] = new Entry { shape = (uint)delta.shape, position = delta.position };
            restBuffer = Buffer(rest, 44); entriesBuffer = Buffer(entries, 16); startsBuffer = Buffer(starts, 4);
            influenceBuffer = Buffer(bones == 0 ? new uint[vertexCount * 4] : influences, 4);
            weightsBuffer = Buffer(new float[Math.Max(1, shapes)], 4);
            boneUpload = new Bone[Math.Max(1, bones)]; bonesBuffer = Buffer(boneUpload, 64);
            for (int s = 0; s < 4; s++) { dummy[s] = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 4, 4); owned.Add(dummy[s]); }
        }

        private GraphicsBuffer Buffer(Array values, int stride)
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, values.Length, stride);
            owned.Add(buffer); buffer.SetData(values); return buffer;
        }

        /// <summary>Submit before any draw. Matrices map authored mesh coordinates to output
        /// mesh coordinates and must be finite affine transforms. Arrays are borrowed only
        /// during the call. No blending history or time is retained.</summary>
        public bool TryDispatch(float[] weights, Matrix4x4[] matrices)
        {
            LastDispatchSucceeded = false; UnavailableReason = null;
            try
            {
                if (disposed || mesh == null || shader == null) throw new InvalidOperationException("Face owner disposed or output destroyed.");
                if (weights == null || weights.Length != shapeCount || matrices == null || matrices.Length != boneCount)
                    throw new ArgumentException("Current weight/matrix counts must match initialized data.");
                if (mesh.vertexCount != vertexCount || mesh.vertexBufferCount != streamCount)
                    throw new InvalidOperationException("Owned mesh vertex layout changed.");
                for (int s = 0; s < streamCount; s++) if (mesh.GetVertexBufferStride(s) != strides[s]) throw new InvalidOperationException("Owned mesh stride changed.");
                for (int a = 0; a < Attributes.Length; a++)
                {
                    bool present = mesh.HasVertexAttribute(Attributes[a]);
                    if (present != (streams[a] >= 0) || (present && (mesh.GetVertexAttributeStream(Attributes[a]) != streams[a] ||
                        mesh.GetVertexAttributeOffset(Attributes[a]) != offsets[a] || mesh.GetVertexAttributeFormat(Attributes[a]) != VertexAttributeFormat.Float32 ||
                        mesh.GetVertexAttributeDimension(Attributes[a]) != (a == 2 ? 4 : 3)))) throw new InvalidOperationException("Owned mesh attribute layout changed.");
                }
                foreach (var buffer in owned) if (buffer == null || !buffer.IsValid()) throw new InvalidOperationException("Face buffer lost.");
                Vector3 expansion = Vector3.zero;
                for (int i = 0; i < weights.Length; i++)
                {
                    if (!Finite(weights[i]) || Mathf.Abs(weights[i]) > 64) throw new ArgumentException("Weights must be finite and within [-64,64].");
                    if (Mathf.Abs(weights[i]) >= .0001f) expansion += shapeRadius[i] * Mathf.Abs(weights[i]);
                }
                var expanded = restBounds; expanded.extents += expansion;
                var bounds = expanded;
                for (int b = 0; b < boneCount; b++)
                {
                    var m = matrices[b]; bool identity = true;
                    for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++)
                    { RequireFinite(m[r,c]); if (Mathf.Abs(m[r,c] - (r == c ? 1 : 0)) > 1e-5f) identity = false; }
                    if (m.m30 != 0 || m.m31 != 0 || m.m32 != 0 || m.m33 != 1) throw new ArgumentException("Affine bone matrices required.");
                    if (identity) m = Matrix4x4.identity;
                    boneUpload[b] = new Bone { x = m.GetRow(0), y = m.GetRow(1), z = m.GetRow(2), state = new Vector4(identity ? 0 : 1,0,0,0) };
                    var center = m.MultiplyPoint3x4(expanded.center); var e = expanded.extents;
                    var radius = Abs(new Vector3(m.m00,m.m10,m.m20))*e.x + Abs(new Vector3(m.m01,m.m11,m.m21))*e.y + Abs(new Vector3(m.m02,m.m12,m.m22))*e.z;
                    bounds.Encapsulate(center - radius); bounds.Encapsulate(center + radius);
                }
                bounds.Expand(2 * (Mathf.Max(bounds.center.magnitude, bounds.extents.magnitude) * .00001f + .0001f));
                if (!Finite(bounds.center.x) || !Finite(bounds.center.y) || !Finite(bounds.center.z) ||
                    !Finite(bounds.extents.x) || !Finite(bounds.extents.y) || !Finite(bounds.extents.z)) throw new ArgumentException("Deformed bounds overflow.");
                // Finish ALL CPU-side mesh mutations before acquiring its GPU streams.
                // Do not rely on which CPU setters each engine version treats as dirty.
                mesh.bounds = bounds;
                if (shapeCount > 0) weightsBuffer.SetData(weights);
                if (boneCount > 0) bonesBuffer.SetData(boneUpload);
                shader.SetBuffer(kernel,"_FaceRest",restBuffer); shader.SetBuffer(kernel,"_FaceEntries",entriesBuffer);
                shader.SetBuffer(kernel,"_FaceStarts",startsBuffer); shader.SetBuffer(kernel,"_FaceInfluences",influenceBuffer);
                shader.SetBuffer(kernel,"_FaceWeights",weightsBuffer); shader.SetBuffer(kernel,"_FaceBones",bonesBuffer);
                shader.SetInt("_FaceVertexCount",vertexCount); shader.SetInt("_FaceBoneCount",boneCount);
                shader.SetInts("_FaceStrides",strides);
                shader.SetInts("_FaceChannels",streams[0],streams[1],streams[2],0);
                shader.SetInts("_FaceOffsets",offsets[0],offsets[1],offsets[2],0);
                // Reacquire every dispatch: CPU mesh uploads can replace Unity's vertex buffers.
                for (int s = 0; s < 4; s++)
                {
                    if (s < streamCount)
                    {
                        vertexBuffers[s] = mesh.GetVertexBuffer(s);
                        if (vertexBuffers[s] == null || !vertexBuffers[s].IsValid() ||
                            (vertexBuffers[s].target & GraphicsBuffer.Target.Raw) == 0)
                            throw new NotSupportedException("Mesh GPU stream lacks a valid Raw target.");
                    }
                    shader.SetBuffer(kernel,"_FaceOutput" + s, s < streamCount ? vertexBuffers[s] : dummy[s]);
                }
                shader.Dispatch(kernel,(vertexCount + 63) / 64,1,1);
                DispatchCount++; LastDispatchSucceeded = true; return true;
            }
            catch (Exception error) { UnavailableReason = error.Message; return false; }
            finally { for (int s = 0; s < 4; s++) { vertexBuffers[s]?.Dispose(); vertexBuffers[s] = null; } }
        }

        private static Vector3 Abs(Vector3 v) { return new Vector3(Mathf.Abs(v.x),Mathf.Abs(v.y),Mathf.Abs(v.z)); }
        // Mono can retain wider scalar locals; force the same float32 weight and
        // accumulated sum that the CPU skinning calls materialize. Initialization only.
        private static float Float32(float v) { return BitConverter.ToSingle(BitConverter.GetBytes(v),0); }
        private static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        private static void RequireFinite(float v) { if (!Finite(v) || Mathf.Abs(v) > 1e6f) throw new ArgumentException("Face coordinates/directions/matrix components must be finite within +/-1e6."); }
        private static void RequireFinite(Vector3 v) { RequireFinite(v.x); RequireFinite(v.y); RequireFinite(v.z); }
        private static void RequireFinite(Vector4 v) { RequireFinite(v.x); RequireFinite(v.y); RequireFinite(v.z); RequireFinite(v.w); }
        public void Dispose()
        {
            LastDispatchSucceeded = false; disposed = true;
            foreach (var buffer in owned) buffer?.Dispose(); owned.Clear(); BufferBytes = 0;
            if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            if (shader != null) UnityEngine.Object.DestroyImmediate(shader);
            mesh = null; shader = null;
        }
    }
}
