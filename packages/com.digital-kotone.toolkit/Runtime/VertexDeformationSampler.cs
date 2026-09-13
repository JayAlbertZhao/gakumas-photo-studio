using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Selected sparse position morphs and four-influence skinning on the CPU.
    /// Copies topology, consumes explicit pose inputs, never reads a GPU mesh's stale CPU copy.</summary>
    public sealed class VertexDeformationSampler
    {
        private struct Delta { public int shape; public Vector3 position; }
        private sealed class Vertex
        {
            public Vector3 rest;
            public readonly List<Delta> deltas = new List<Delta>();
            public readonly uint[] influences = new uint[4];
            public float inverseWeight;
        }
        public const int MaxSelectedVertices = 768;
        private readonly int[] indices;
        private readonly Vertex[] vertices;
        private readonly int shapeCount, boneCount;

        private VertexDeformationSampler(Vector3[] rest, int shapes, GpuFaceDeformer.Delta[] deltas,
            uint[] influences, int bones, int[] selected)
        {
            Require(rest != null && rest.Length > 0 && rest.Length <= 262144 && shapes >= 0 && shapes <= 4096 &&
                bones >= 0 && bones <= 65536 && deltas != null && deltas.Length <= 4194304, "Invalid sparse topology or capacity.");
            Require(selected != null && selected.Length > 0 && selected.Length <= MaxSelectedVertices, "Select 1..768 distinct vertices.");
            Require(bones == 0 || influences != null && influences.Length == rest.Length * 4, "Four packed influences per vertex are required.");
            indices = (int[])selected.Clone(); shapeCount = shapes; boneCount = bones; vertices = new Vertex[indices.Length];
            var lookup = new Dictionary<int, Vertex>();
            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i]; Require(index >= 0 && index < rest.Length && !lookup.ContainsKey(index), "Invalid or repeated selected index.");
                Require(Finite(rest[index]), "Nonfinite rest position.");
                var vertex = new Vertex { rest = rest[index] }; float sum = 0;
                if (bones > 0) for (int k = 0; k < 4; k++)
                {
                    uint packed = influences[index * 4 + k]; vertex.influences[k] = packed;
                    if ((packed & 65535u) < bones) sum = Float32(sum + Float32((packed >> 16) * (1f / 65535f)));
                }
                vertex.inverseWeight = sum > 0 ? 1f / sum : 0;
                vertices[i] = vertex; lookup.Add(index, vertex);
            }
            int previousShape = -1;
            foreach (var delta in deltas)
            {
                Require(delta.shape >= previousShape && delta.shape >= 0 && delta.shape < shapes &&
                    delta.vertex >= 0 && delta.vertex < rest.Length && Finite(delta.position), "Invalid delta or non-shape-major order.");
                previousShape = delta.shape;
                if (lookup.TryGetValue(delta.vertex, out var vertex)) vertex.deltas.Add(new Delta { shape = delta.shape, position = delta.position });
            }
        }

        public int[] GetVertexIndices() => (int[])indices.Clone();
        public static bool TryCreate(Vector3[] rest, int shapes, GpuFaceDeformer.Delta[] deltas, uint[] influences,
            int bones, int[] selected, out VertexDeformationSampler sampler, out string reason)
        {
            sampler = null; reason = null;
            try { sampler = new VertexDeformationSampler(rest, shapes, deltas, influences, bones, selected); return true; }
            catch (ArgumentException error) { reason = error.Message; return false; }
        }

        /// <summary>Output follows GetVertexIndices order. Weights/matrices are borrowed for this call only.</summary>
        public bool TrySample(float[] weights, Matrix4x4[] matrices, out Vector3[] positions, out string reason)
        {
            positions = null; reason = null;
            try
            {
                Require(weights != null && weights.Length == shapeCount && matrices != null && matrices.Length == boneCount, "Pose counts differ from topology.");
                foreach (float weight in weights) Require(Finite(weight) && Mathf.Abs(weight) <= 64, "Invalid morph weight.");
                var effective = new Matrix4x4[boneCount]; var moving = new bool[boneCount];
                for (int i = 0; i < boneCount; i++)
                {
                    Matrix4x4 matrix = matrices[i]; bool identity = true;
                    for (int j = 0; j < 16; j++)
                    {
                        Require(Finite(matrix[j]) && Mathf.Abs(matrix[j]) <= 1e6f, "Invalid bone matrix.");
                        if (Mathf.Abs(matrix[j] - (j % 5 == 0 ? 1 : 0)) > 1e-5f) identity = false;
                    }
                    Require(matrix.m30 == 0 && matrix.m31 == 0 && matrix.m32 == 0 && matrix.m33 == 1, "Affine bone matrix required.");
                    effective[i] = identity ? Matrix4x4.identity : matrix; moving[i] = !identity;
                }
                var result = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    var vertex = vertices[i]; var blended = vertex.rest; bool deformed = false;
                    foreach (var delta in vertex.deltas)
                        if (Mathf.Abs(weights[delta.shape]) >= .0001f) blended += delta.position * weights[delta.shape];
                    foreach (uint packed in vertex.influences)
                        if ((packed >> 16) != 0 && (packed & 65535u) < boneCount && moving[packed & 65535u]) deformed = true;
                    var position = blended;
                    if (deformed)
                    {
                        position = Vector3.zero;
                        foreach (uint packed in vertex.influences)
                        {
                            int bone = (int)(packed & 65535u); float weight = (packed >> 16) * (1f / 65535f);
                            if (weight > 0 && bone < boneCount) position += effective[bone].MultiplyPoint3x4(blended) * weight;
                        }
                        position *= vertex.inverseWeight;
                    }
                    Require(Finite(position), "Selected deformation overflow."); result[i] = position;
                }
                positions = result; return true;
            }
            catch (ArgumentException error) { reason = error.Message; return false; }
        }
        private static float Float32(float value) => BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static void Require(bool ok, string reason) { if (!ok) throw new ArgumentException(reason); }
    }
}
