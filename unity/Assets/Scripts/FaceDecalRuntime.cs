using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Replays Campus facial-decal binds in the built-in photo renderer.
    /// The source prefabs retain their exact URP DecalProjector transforms and
    /// serialized volume/atlas settings, but the default Photo Studio path is
    /// intentionally not a URP camera.  This bridge uploads the same projector
    /// volumes to ActorToon, which evaluates them against each face fragment.
    /// </summary>
    public sealed class FaceDecalRuntime
    {
        private const int MaximumShaderProjectors = 8;
        private static readonly int AtlasId = Shader.PropertyToID("_FaceDecalAtlas");
        private static readonly int CountId = Shader.PropertyToID("_FaceDecalCount");
        private static readonly int WorldToDecalId = Shader.PropertyToID("_FaceDecalWorldToDecal");
        private static readonly int UvScaleBiasId = Shader.PropertyToID("_FaceDecalUvScaleBias");
        private static readonly int FadeId = Shader.PropertyToID("_FaceDecalFade");

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Matrix4x4[] _worldToDecal = new Matrix4x4[MaximumShaderProjectors];
        private readonly Vector4[] _uvScaleBias = new Vector4[MaximumShaderProjectors];
        private readonly Vector4[] _fade = new Vector4[MaximumShaderProjectors];
        private Texture _atlas;
        private int _activeCount;
        private bool _renderEnabled = true;
        private MeshFilter[] _faceMeshes = new MeshFilter[0];
        private bool _coverageLogged;

        private sealed class Entry
        {
            public DecalProjector projector;
            public string name;
            public float weight;
        }

        public int ProjectorCount { get { return _entries.Count; } }
        public int ActiveCount { get { return _activeCount; } }

        public void Initialize(GameObject face, BundleCatalog catalog)
        {
            Clear();
            _renderEnabled = !Environment.GetCommandLineArgs()
                .Contains("--disable-face-decal-render");
            if (face == null) return;
            _faceMeshes = face.GetComponentsInChildren<MeshFilter>(true);
            foreach (DecalProjector projector in face.GetComponentsInChildren<DecalProjector>(true))
            {
                projector.fadeFactor = 0f;
                _entries.Add(new Entry
                {
                    projector = projector,
                    name = projector.gameObject.name,
                    weight = 0f,
                });
                if (_atlas == null && projector.material != null)
                    _atlas = projector.material.GetTexture("_BaseMap");
            }
            if (_atlas == null && catalog != null)
            {
                try
                {
                    _atlas = catalog.LoadAll<Texture2D>("m_fdc")
                        .FirstOrDefault(value => string.Equals(
                            value.name, "t_chr_cmmn-base-0000_dcl",
                            StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[FaceDecal] Atlas load failed: " + exception.Message);
                }
            }
            Shader.SetGlobalTexture(AtlasId, _atlas == null ? Texture2D.whiteTexture : _atlas);
            Upload();
            Debug.Log(string.Format(
                "[FaceDecal] Original projectors={0} atlas={1}",
                _entries.Count, _atlas == null ? "missing" : _atlas.name));
        }

        public void ApplyStoryOverrides(StoryFaceOverrideEvent[] overrides, float storyTime)
        {
            foreach (Entry entry in _entries) entry.weight = 0f;
            if (overrides != null)
            {
                foreach (StoryFaceOverrideEvent value in overrides)
                {
                    if (value == null || value.decals == null) continue;
                    float mixWeight = value.EvaluateMixWeight(storyTime);
                    foreach (StoryFaceDecal decal in value.decals)
                    {
                        if (decal == null || !string.Equals(
                            decal.attribute, "m_FadeFactor", StringComparison.OrdinalIgnoreCase)) continue;
                        string leaf = DecalLeafName(decal.path);
                        Entry entry = _entries.FirstOrDefault(candidate => string.Equals(
                            candidate.name, leaf, StringComparison.OrdinalIgnoreCase));
                        if (entry != null)
                            entry.weight = Mathf.Max(entry.weight, Mathf.Clamp01(decal.value * mixWeight));
                    }
                }
            }
            foreach (Entry entry in _entries)
                entry.projector.fadeFactor = entry.weight;
            Upload();
        }

        public void Clear()
        {
            foreach (Entry entry in _entries)
            {
                if (entry.projector != null) entry.projector.fadeFactor = 0f;
            }
            _entries.Clear();
            _atlas = null;
            _activeCount = 0;
            Shader.SetGlobalFloat(CountId, 0f);
        }

        public string DiagnosticJson()
        {
            StringBuilder builder = new StringBuilder(256);
            builder.Append("{\"projectors\":").Append(_entries.Count)
                .Append(",\"active\":[");
            bool first = true;
            foreach (Entry entry in _entries.Where(value => value.weight > 0.0000001f))
            {
                if (!first) builder.Append(',');
                first = false;
                builder.Append("{\"name\":\"").Append(entry.name)
                    .Append("\",\"fade\":")
                    .Append(entry.weight.ToString("R", CultureInfo.InvariantCulture))
                    .Append('}');
            }
            builder.Append("]}");
            return builder.ToString();
        }

        private void Upload()
        {
            _activeCount = 0;
            foreach (Entry entry in _entries)
            {
                if (entry.weight <= 0.0000001f || _activeCount >= MaximumShaderProjectors) continue;
                DecalProjector projector = entry.projector;
                Matrix4x4 localToWorld;
                if (projector.scaleMode == DecalScaleMode.InheritFromHierarchy)
                {
                    localToWorld = projector.transform.localToWorldMatrix *
                        Matrix4x4.Rotate(Quaternion.Euler(-90f, 0f, 0f));
                }
                else
                {
                    localToWorld = Matrix4x4.TRS(
                        projector.transform.position,
                        projector.transform.rotation * Quaternion.Euler(-90f, 0f, 0f),
                        Vector3.one);
                }
                Vector3 sourceSize = projector.size;
                Vector3 sourcePivot = projector.pivot;
                Vector3 decalSize = new Vector3(sourceSize.x, sourceSize.z, sourceSize.y);
                Vector3 decalOffset = new Vector3(sourcePivot.x, -sourcePivot.z, sourcePivot.y);
                Matrix4x4 decalToWorld = localToWorld *
                    Matrix4x4.Translate(decalOffset) * Matrix4x4.Scale(decalSize);
                _worldToDecal[_activeCount] = decalToWorld.inverse;
                Vector2 scale = projector.uvScale;
                Vector2 bias = projector.uvBias;
                _uvScaleBias[_activeCount] = new Vector4(scale.x, scale.y, bias.x, bias.y);
                _fade[_activeCount] = new Vector4(entry.weight, 0f, 0f, 0f);
                _activeCount++;
            }
            for (int index = _activeCount; index < MaximumShaderProjectors; index++)
            {
                _worldToDecal[index] = Matrix4x4.identity;
                _uvScaleBias[index] = Vector4.zero;
                _fade[index] = Vector4.zero;
            }
            Shader.SetGlobalTexture(AtlasId, _atlas == null ? Texture2D.whiteTexture : _atlas);
            Shader.SetGlobalMatrixArray(WorldToDecalId, _worldToDecal);
            Shader.SetGlobalVectorArray(UvScaleBiasId, _uvScaleBias);
            Shader.SetGlobalVectorArray(FadeId, _fade);
            Shader.SetGlobalFloat(CountId, _renderEnabled ? _activeCount : 0f);
            if (!_coverageLogged && _activeCount > 0 &&
                _entries.Any(value => value.weight >= 0.999f))
            {
                _coverageLogged = true;
                LogCoverage();
            }
        }

        private void LogCoverage()
        {
            for (int projectorIndex = 0; projectorIndex < _activeCount; projectorIndex++)
            {
                int inside = 0;
                int total = 0;
                Vector3 minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                Vector2 uvMinimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                Vector2 uvMaximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                foreach (MeshFilter filter in _faceMeshes)
                {
                    if (filter == null || filter.sharedMesh == null) continue;
                    foreach (Vector3 vertex in filter.sharedMesh.vertices)
                    {
                        Vector3 world = filter.transform.TransformPoint(vertex);
                        Vector3 decal = _worldToDecal[projectorIndex].MultiplyPoint3x4(world);
                        minimum = Vector3.Min(minimum, decal);
                        maximum = Vector3.Max(maximum, decal);
                        if (Mathf.Abs(decal.x) <= 0.5f && Mathf.Abs(decal.y) <= 0.5f &&
                            Mathf.Abs(decal.z) <= 0.5f)
                        {
                            inside++;
                            Vector4 contract = _uvScaleBias[projectorIndex];
                            Vector2 uv = new Vector2(
                                (decal.x + 0.5f) * contract.x + contract.z,
                                (decal.z + 0.5f) * contract.y + contract.w);
                            uvMinimum = Vector2.Min(uvMinimum, uv);
                            uvMaximum = Vector2.Max(uvMaximum, uv);
                        }
                        total++;
                    }
                }
                Debug.Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "[FaceDecal] Coverage index={0} name={9} inside={1}/{2} dsMin=({3:R},{4:R},{5:R}) dsMax=({6:R},{7:R},{8:R}) projectorWorld=({10:R},{11:R},{12:R}) projectorScale=({13:R},{14:R},{15:R}) volumeCenter=({16:R},{17:R},{18:R}) meshWorld=({19:R},{20:R},{21:R}) uvMin=({22:R},{23:R}) uvMax=({24:R},{25:R})",
                    projectorIndex, inside, total,
                    minimum.x, minimum.y, minimum.z,
                    maximum.x, maximum.y, maximum.z,
                    _entries.Where(value => value.weight > 0.0000001f).ElementAt(projectorIndex).name,
                    _entries.Where(value => value.weight > 0.0000001f).ElementAt(projectorIndex).projector.transform.position.x,
                    _entries.Where(value => value.weight > 0.0000001f).ElementAt(projectorIndex).projector.transform.position.y,
                    _entries.Where(value => value.weight > 0.0000001f).ElementAt(projectorIndex).projector.transform.position.z,
                    _entries.Where(value => value.weight > 0.0000001f).ElementAt(projectorIndex).projector.transform.lossyScale.x,
                    _entries.Where(value => value.weight > 0.0000001f).ElementAt(projectorIndex).projector.transform.lossyScale.y,
                    _entries.Where(value => value.weight > 0.0000001f).ElementAt(projectorIndex).projector.transform.lossyScale.z,
                    _worldToDecal[projectorIndex].inverse.MultiplyPoint3x4(Vector3.zero).x,
                    _worldToDecal[projectorIndex].inverse.MultiplyPoint3x4(Vector3.zero).y,
                    _worldToDecal[projectorIndex].inverse.MultiplyPoint3x4(Vector3.zero).z,
                    _faceMeshes.Length == 0 ? 0f : _faceMeshes[0].transform.position.x,
                    _faceMeshes.Length == 0 ? 0f : _faceMeshes[0].transform.position.y,
                    _faceMeshes.Length == 0 ? 0f : _faceMeshes[0].transform.position.z,
                    uvMinimum.x, uvMinimum.y, uvMaximum.x, uvMaximum.y));
            }
        }

        private static string DecalLeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }
    }
}
