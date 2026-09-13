using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Explicit CPU authoring of dedicated object-space extrusion vectors.
    /// Independent angle-weighted model; no implicit import or renderer changes.
    /// </summary>
    public static class ActorOutlineAuthoring
    {
        public const int MaximumVertices = 1000000;
        public const int MaximumIndices = 6000000;
        public const long MaximumBlendFrameVertices = 8000000;

        public sealed class Result
        {
            public Vector3[] directions { get; internal set; }
            public int weldedVertices { get; internal set; }
            public int degenerateTriangles { get; internal set; }
            public int unresolvedVertices { get; internal set; }
        }

        // Vector3.operator== is approximate. Equals intentionally uses exact
        // component equality, including signed zero. Groups isolate touching shells.
        private readonly struct Key : IEquatable<Key>
        {
            private readonly Vector3 position;
            private readonly int group;
            public Key(Vector3 p, int g) { position = p; group = g; }
            public bool Equals(Key other) => group == other.group && position.Equals(other.position);
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => unchecked(position.GetHashCode() * 397 ^ group);
        }

        private struct Sum
        {
            public double x, y, z, weight;
            public void Add(double nx, double ny, double nz, double angle)
            { x += nx * angle; y += ny * angle; z += nz * angle; weight += angle; }
        }

        /// <summary>
        /// Smooth across exact-position duplicates in the same weld group.
        /// Null groups mean one group. Winding defines the outward direction.
        /// Degenerate triangles contribute nothing; unused/cancelled groups return zero.
        /// Returned arrays are owned by the caller. Inputs are never changed.
        /// </summary>
        public static Result Generate(IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles,
            IReadOnlyList<int> weldGroups = null)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (positions.Count == 0 || positions.Count > MaximumVertices)
                throw new ArgumentException("Vertex count must be in 1..MaximumVertices.", nameof(positions));
            if (triangles.Count > MaximumIndices || triangles.Count % 3 != 0)
                throw new ArgumentException("Expected bounded triangle indices.", nameof(triangles));
            if (weldGroups != null && weldGroups.Count != positions.Count)
                throw new ArgumentException("One weld group is required per vertex.", nameof(weldGroups));
            var lookup = new Dictionary<Key, int>();
            var membership = new int[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                Validate(positions[i]);
                var key = new Key(positions[i], weldGroups == null ? 0 : weldGroups[i]);
                if (!lookup.TryGetValue(key, out int group)) { group = lookup.Count; lookup.Add(key, group); }
                membership[i] = group;
            }
            for (int i = 0; i < triangles.Count; i++)
                if (triangles[i] < 0 || triangles[i] >= positions.Count)
                    throw new ArgumentException("Triangle index outside vertex range.", nameof(triangles));
            var sums = new Sum[lookup.Count];
            int degenerate = 0;
            for (int t = 0; t < triangles.Count; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                Vector3 pa = positions[a], pb = positions[b], pc = positions[c];
                double ux = (double)pb.x - pa.x, uy = (double)pb.y - pa.y, uz = (double)pb.z - pa.z;
                double vx = (double)pc.x - pa.x, vy = (double)pc.y - pa.y, vz = (double)pc.z - pa.z;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double area = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (area == 0) { degenerate++; continue; }
                nx /= area; ny /= area; nz /= area;
                // atan2(cross length, dot) remains defined for very thin corners.
                sums[membership[a]].Add(nx, ny, nz, Math.Atan2(area, ux * vx + uy * vy + uz * vz));
                sums[membership[b]].Add(nx, ny, nz, Math.Atan2(area, ux * (ux - vx) + uy * (uy - vy) + uz * (uz - vz)));
                sums[membership[c]].Add(nx, ny, nz, Math.Atan2(area, vx * (vx - ux) + vy * (vy - uy) + vz * (vz - uz)));
            }
            var groupDirections = new Vector3[sums.Length];
            for (int i = 0; i < sums.Length; i++)
            {
                var s = sums[i]; double length = Math.Sqrt(s.x * s.x + s.y * s.y + s.z * s.z);
                // Relative cancellation threshold, independent of mesh scale.
                if (length > s.weight * 1e-12)
                    groupDirections[i] = new Vector3((float)(s.x / length), (float)(s.y / length), (float)(s.z / length));
            }
            var result = new Result { directions = new Vector3[positions.Count],
                weldedVertices = positions.Count - sums.Length, degenerateTriangles = degenerate };
            for (int i = 0; i < result.directions.Length; i++)
            {
                result.directions[i] = groupDirections[membership[i]];
                if (result.directions[i].Equals(Vector3.zero)) result.unresolvedVertices++;
            }
            return result;
        }

        public static Result Generate(Mesh source, IReadOnlyList<int> weldGroups = null)
        {
            var indices = TriangleIndices(source);
            return Generate(source.vertices, indices, weldGroups);
        }

        /// <summary>
        /// Creates an owned clone for actor shaders whose TANGENT.xyz is an
        /// extrusion vector. Overwrites base and blend-frame tangents ONLY.
        /// Source vertices/normals/UVs/colors/weights/submeshes remain unchanged.
        /// Caller assigns and destroys the clone. No asset or shared mesh is edited.
        /// </summary>
        public static Mesh CreateMesh(Mesh source, IReadOnlyList<int> weldGroups = null)
        {
            var indices = TriangleIndices(source);
            var positions = source.vertices;
            var basis = Generate(positions, indices, weldGroups);
            long frameVertices = 0;
            for (int shape = 0; shape < source.blendShapeCount; shape++)
                frameVertices += (long)source.GetBlendShapeFrameCount(shape) * positions.Length;
            if (frameVertices > MaximumBlendFrameVertices)
                throw new ArgumentException("Blend-frame authoring budget exceeded.", nameof(source));
            Mesh result = UnityEngine.Object.Instantiate(source);
            try
            {
                result.name = source.name + " (authored outline)";
                result.tangents = ActorVertexEncoding.OutlineTangents(basis.directions);
                result.ClearBlendShapes();
                if (source.blendShapeCount > 0)
                {
                    var deltaPositions = new Vector3[positions.Length];
                    var deltaNormals = new Vector3[positions.Length];
                    var deltaTangents = new Vector3[positions.Length];
                    var deformed = new Vector3[positions.Length];
                    for (int shape = 0; shape < source.blendShapeCount; shape++)
                    for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
                    {
                        source.GetBlendShapeFrameVertices(shape, frame, deltaPositions, deltaNormals, deltaTangents);
                        for (int i = 0; i < positions.Length; i++)
                        {
                            Validate(deltaNormals[i]);
                            deformed[i] = positions[i] + deltaPositions[i];
                        }
                        var target = Generate(deformed, indices, weldGroups);
                        for (int i = 0; i < positions.Length; i++)
                            deltaTangents[i] = target.directions[i] - basis.directions[i];
                        result.AddBlendShapeFrame(source.GetBlendShapeName(shape), source.GetBlendShapeFrameWeight(shape, frame),
                            deltaPositions, deltaNormals, deltaTangents);
                    }
                }
                return result;
            }
            catch
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(result);
                else UnityEngine.Object.DestroyImmediate(result);
                throw;
            }
        }

        private static int[] TriangleIndices(Mesh source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!source.isReadable) throw new ArgumentException("Mesh must be CPU readable.", nameof(source));
            if (source.vertexCount == 0 || source.vertexCount > MaximumVertices)
                throw new ArgumentException("Vertex authoring budget exceeded.", nameof(source));
            ulong count = 0;
            for (int i = 0; i < source.subMeshCount; i++)
            {
                if (source.GetTopology(i) != MeshTopology.Triangles)
                    throw new ArgumentException("Only triangle submeshes are supported.", nameof(source));
                count += source.GetIndexCount(i);
            }
            if (count > MaximumIndices) throw new ArgumentException("Index authoring budget exceeded.", nameof(source));
            // Unity applies each submesh's baseVertex. The clone retains the
            // original submesh descriptors; this flattened array is computation only.
            var result = new int[(int)count]; int offset = 0;
            for (int i = 0; i < source.subMeshCount; i++)
            {
                int[] submesh = source.GetTriangles(i, true);
                Array.Copy(submesh, 0, result, offset, submesh.Length); offset += submesh.Length;
            }
            return result;
        }

        private static void Validate(Vector3 value)
        {
            if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z))
                throw new ArgumentException("Authoring vectors must be finite.");
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
