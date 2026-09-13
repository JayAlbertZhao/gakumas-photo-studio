using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class FaceExpressionRenderer
    {
        private bool _gpuDeformationEnabled, _gpuAttemptFailed, _cpuBlendNeedsRefresh;
        private GpuFaceDeformer _gpuFace;
        /// <summary>Opt in per character. CPU remains the default and the current-frame
        /// fallback. Toggle off/on to retry a failed capability/data initialization.</summary>
        public bool GpuDeformationEnabled
        {
            get { return _gpuDeformationEnabled; }
            set
            {
                if (_gpuDeformationEnabled == value) return;
                _gpuDeformationEnabled = value; _gpuAttemptFailed = false;
                ReleaseGpuDeformation(); ForceRefresh();
            }
        }
        public bool IsGpuDeformationActive { get; private set; }
        public string GpuDeformationUnavailableReason { get; private set; }
        public long GpuDeformationBufferBytes { get { return _gpuFace == null ? 0 : _gpuFace.BufferBytes; } }
        // GPU execution intentionally does not stall for diagnostic-only reduction/readback.
        public bool CpuBoneDisplacementIsCurrent { get { return !IsGpuDeformationActive; } }

        private bool TryApplyGpuDeformation(float[] weights, bool changed)
        {
            if (!_gpuDeformationEnabled || _gpuAttemptFailed) return false;
            try
            {
                if (_gpuFace == null)
                {
                    var deltas = new List<GpuFaceDeformer.Delta>();
                    for (int s = 0; s < _shapeSource.blendShapes.Count; s++)
                    {
                        var shape = _shapeSource.blendShapes[s];
                        if (shape == null || shape.blendShapeVertices == null) continue;
                        foreach (var v in shape.blendShapeVertices)
                            if (v != null && v.vertIndex >= 0 && v.vertIndex < _baseVertices.Length)
                                deltas.Add(new GpuFaceDeformer.Delta(s,v.vertIndex,v.position));
                    }
                    var rest = Instantiate(_deformedMesh);
                    try
                    {
                        rest.vertices = _baseVertices;
                        if (_workingNormals != null) rest.normals = _baseNormals;
                        if (_workingTangents != null) rest.tangents = _baseTangents;
                        if (!GpuFaceDeformer.TryCreate(rest,weights.Length,deltas.ToArray(),
                            _shapeSource.boneWeightAndIndices,_skinMatrices.Length,out _gpuFace,out string reason))
                            throw new InvalidOperationException(reason);
                    }
                    finally { DestroyImmediate(rest); }
                }
                if (_boneSkinningReady) PrepareSkinMatrices();
                // Preserve the CPU path's >1e-4 weight-change latch, including its blink
                // ownership and repeated sub-threshold updates; do not accumulate drift.
                if (!_gpuFace.TryDispatch(changed ? weights : _lastWeights,_skinMatrices))
                    throw new InvalidOperationException(_gpuFace.UnavailableReason);
                if (changed) Array.Copy(weights,_lastWeights,weights.Length);
                _filter.sharedMesh = _gpuFace.Mesh;
                _cpuBlendNeedsRefresh = true; IsGpuDeformationActive = true;
                GpuDeformationUnavailableReason = null; return true;
            }
            catch (Exception error)
            {
                GpuDeformationUnavailableReason = error.Message; _gpuAttemptFailed = true;
                ReleaseGpuDeformation();
                Debug.LogWarning("[PhotoMode] GPU face unavailable; current CPU fallback: " + error.Message);
                return false;
            }
        }

        private void ReleaseGpuDeformation()
        {
            if (_gpuFace != null)
            {
                // Restore before destroying the borrowed GPU mesh, even after dispatch failure.
                if (_filter != null) _filter.sharedMesh = _deformedMesh;
                _gpuFace.Dispose(); _gpuFace = null; _cpuBlendNeedsRefresh = true;
            }
            IsGpuDeformationActive = false;
        }
    }
}
