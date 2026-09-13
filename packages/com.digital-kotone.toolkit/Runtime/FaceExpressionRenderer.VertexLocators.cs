using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class FaceExpressionRenderer
    {
        /// <summary>Explicit read-only view of selected vertices from the last applied face pose.</summary>
        public sealed class VertexSource : IDisposable
        {
            private FaceExpressionRenderer face;
            private Vector3[] topologyIdentity;
            private VertexDeformationSampler sampler;
            internal VertexSource(FaceExpressionRenderer owner, VertexDeformationSampler evaluator)
            { face = owner; topologyIdentity = owner._baseVertices; sampler = evaluator; }
            public int[] GetVertexIndices() => sampler == null ? Array.Empty<int>() : sampler.GetVertexIndices();
            public bool TrySample(out Vector3[] positions, out Matrix4x4 localToWorld, out string reason)
            {
                positions = null; localToWorld = Matrix4x4.identity; reason = null;
                if (face == null || !face._ready || !ReferenceEquals(topologyIdentity, face._baseVertices) || face._filter == null ||
                    face._renderer == null || (!face.IsGpuDeformationActive && face._cpuBlendNeedsRefresh))
                { reason = "Face source disposed, replaced or not yet applied."; return false; }
                Mesh expected = face.IsGpuDeformationActive ? face._gpuFace?.Mesh : face._deformedMesh;
                if (expected == null || face._filter.sharedMesh != expected)
                { reason = "Current face output unavailable or replaced."; return false; }
                if (!sampler.TrySample(face._lastWeights, face._skinMatrices, out positions, out reason)) return false;
                localToWorld = face._renderer.localToWorldMatrix;
                for (int i = 0; i < 16; i++) if (float.IsNaN(localToWorld[i]) || float.IsInfinity(localToWorld[i]))
                { positions = null; localToWorld = Matrix4x4.identity; reason = "Current surface matrix is nonfinite."; return false; }
                return true;
            }
            /// <summary>Samples then updates matching locator definitions. Shader vertex scale must be explicit.</summary>
            public bool TryUpdate(VertexLocatorRig rig, Vector3 vertexScale, out string reason)
            {
                reason = null;
                if (rig == null) { reason = "Missing locator rig."; return false; }
                if (face == null || sampler == null) { rig.Hide(); reason = "Face source disposed or destroyed."; return false; }
                var expected = sampler.GetVertexIndices(); var requested = rig.GetVertexIndices();
                bool match = expected.Length == requested.Length;
                for (int i = 0; match && i < expected.Length; i++) match &= expected[i] == requested[i];
                if (!match) { rig.Hide(); reason = "Locator indices differ from this source."; return false; }
                if (!TrySample(out var positions, out var matrix, out reason)) { rig.Hide(); return false; }
                if (!rig.TryUpdate(positions, matrix * Matrix4x4.Scale(vertexScale))) { reason = rig.LastError; return false; }
                return true;
            }
            public void Dispose() { face = null; topologyIdentity = null; sampler = null; }
        }

        public bool TryCreateVertexSource(int[] indices, out VertexSource source, out string reason)
        {
            source = null; reason = null;
            if (!_ready || _shapeSource == null || _shapeSource.blendShapes == null || _skinMatrices == null)
            { reason = "Face topology is not ready."; return false; }
            var deltas = new List<GpuFaceDeformer.Delta>();
            for (int shape = 0; shape < _shapeSource.blendShapes.Count; shape++)
            {
                var record = _shapeSource.blendShapes[shape];
                if (record == null || record.blendShapeVertices == null) continue;
                foreach (var vertex in record.blendShapeVertices)
                    if (vertex != null && vertex.vertIndex >= 0 && vertex.vertIndex < _baseVertices.Length)
                        deltas.Add(new GpuFaceDeformer.Delta(shape, vertex.vertIndex, vertex.position));
            }
            if (!VertexDeformationSampler.TryCreate(_baseVertices, _lastWeights.Length, deltas.ToArray(),
                _shapeSource.boneWeightAndIndices, _skinMatrices.Length, indices, out var sampler, out reason)) return false;
            source = new VertexSource(this, sampler); return true;
        }
    }
}
