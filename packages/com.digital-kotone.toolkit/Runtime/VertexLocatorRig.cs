using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Owns locator transforms, consumes explicitly ordered current positions. No geometry or clock discovery.</summary>
    public sealed class VertexLocatorRig : IDisposable
    {
        [Serializable]
        public sealed class Locator
        {
            public string id;
            public int a, b, c;
            public Vector3 barycentric = new Vector3(1, 0, 0);
            public bool alignToTriangle = true;
            public Vector3 positionOffset, rotationDegrees;
            internal Locator Copy() => (Locator)MemberwiseClone();
        }
        private readonly Locator[] definitions;
        private readonly int[] indices;
        private readonly Dictionary<int, int> lookup = new Dictionary<int, int>();
        private readonly Transform[] targets;
        private GameObject root;
        private bool disposed;
        public bool IsCurrent { get; private set; }
        public string LastError { get; private set; }

        private VertexLocatorRig(Locator[] locators)
        {
            Require(locators != null && locators.Length > 0 && locators.Length <= 256, "Require 1..256 locators.");
            var names = new HashSet<string>(StringComparer.Ordinal); var selected = new List<int>();
            definitions = new Locator[locators.Length]; targets = new Transform[locators.Length];
            for (int i = 0; i < locators.Length; i++)
            {
                var d = locators[i]; Require(d != null && !string.IsNullOrWhiteSpace(d.id) && d.id.Length <= 128 && names.Add(d.id), "Missing or duplicate locator ID.");
                Require(Finite(d.barycentric) && d.barycentric.x >= 0 && d.barycentric.y >= 0 && d.barycentric.z >= 0 &&
                    Mathf.Abs(d.barycentric.x + d.barycentric.y + d.barycentric.z - 1) <= 1e-6f, "Barycentric weights must be nonnegative and sum to one.");
                Require(Finite(d.positionOffset) && Finite(d.rotationDegrees), "Invalid locator offset.");
                foreach (int index in new[] { d.a, d.b, d.c })
                {
                    Require(index >= 0 && index < 262144, "Invalid vertex index.");
                    if (!lookup.ContainsKey(index)) { lookup.Add(index, selected.Count); selected.Add(index); }
                }
                definitions[i] = d.Copy();
            }
            indices = selected.ToArray();
            root = new GameObject("Vertex locator rig") { hideFlags = HideFlags.DontSave }; root.SetActive(false);
            for (int i = 0; i < targets.Length; i++)
            {
                targets[i] = new GameObject(definitions[i].id) { hideFlags = HideFlags.DontSave }.transform;
                targets[i].SetParent(root.transform, false);
            }
        }
        public static bool TryCreate(Locator[] definitions, out VertexLocatorRig rig, out string reason)
        {
            rig = null; reason = null;
            try { rig = new VertexLocatorRig(definitions); return true; }
            catch (ArgumentException error) { reason = error.Message; return false; }
        }
        public int[] GetVertexIndices() => (int[])indices.Clone();
        /// <summary>Borrowed attachment. Do not mutate/reparent it. Dispose effects before their locator rig.</summary>
        public Transform GetAttachment(string id)
        {
            for (int i = 0; i < targets.Length; i++) if (definitions[i].id == id) return targets[i];
            return null;
        }
        public bool TryUpdate(Vector3[] positions, Matrix4x4 localToWorld)
        {
            Hide();
            try
            {
                Require(!disposed && root != null, "Locator owner disposed or destroyed.");
                Require(positions != null && positions.Length == indices.Length, "Position order/count must match GetVertexIndices.");
                foreach (var point in positions) Require(Finite(point), "Nonfinite current vertex.");
                for (int i = 0; i < 16; i++) Require(Finite(localToWorld[i]), "Nonfinite surface matrix.");
                Require(localToWorld.m30 == 0 && localToWorld.m31 == 0 && localToWorld.m32 == 0 && localToWorld.m33 == 1, "Affine surface transform required.");
                var worldPositions = new Vector3[targets.Length]; var rotations = new Quaternion[targets.Length];
                for (int i = 0; i < targets.Length; i++)
                {
                    Require(targets[i] != null && targets[i].parent == root.transform, "Owned locator hierarchy changed.");
                    var d = definitions[i]; var a = localToWorld.MultiplyPoint3x4(positions[lookup[d.a]]);
                    var b = localToWorld.MultiplyPoint3x4(positions[lookup[d.b]]); var c = localToWorld.MultiplyPoint3x4(positions[lookup[d.c]]);
                    var forward = (Vector3)localToWorld.GetColumn(2); var up = (Vector3)localToWorld.GetColumn(1);
                    if (d.alignToTriangle)
                    {
                        var edge = b - a; forward = Vector3.Cross(edge, c - a); up = Vector3.Cross(forward, edge);
                    }
                    Require(Finite(forward) && Finite(up) && forward.sqrMagnitude > 1e-20f && up.sqrMagnitude > 1e-20f, "Degenerate locator frame.");
                    forward.Normalize(); up.Normalize();
                    Require(Vector3.Cross(forward, up).sqrMagnitude > 1e-10f, "Parallel locator frame axes.");
                    var frame = Quaternion.LookRotation(forward, up);
                    worldPositions[i] = a * d.barycentric.x + b * d.barycentric.y + c * d.barycentric.z + frame * d.positionOffset;
                    rotations[i] = frame * Quaternion.Euler(d.rotationDegrees);
                    Require(Finite(worldPositions[i]), "Locator position overflow.");
                }
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); root.transform.localScale = Vector3.one;
                for (int i = 0; i < targets.Length; i++)
                { targets[i].SetPositionAndRotation(worldPositions[i], rotations[i]); targets[i].localScale = Vector3.one; }
                root.SetActive(true); IsCurrent = true; LastError = null; return true;
            }
            catch (ArgumentException error) { LastError = error.Message; return false; }
        }
        public void Hide() { if (root != null) root.SetActive(false); IsCurrent = false; }
        public void Dispose()
        {
            Hide(); disposed = true;
            if (root != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(root); else UnityEngine.Object.DestroyImmediate(root); }
            root = null;
        }
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static void Require(bool ok, string reason) { if (!ok) throw new ArgumentException(reason); }
    }
}
