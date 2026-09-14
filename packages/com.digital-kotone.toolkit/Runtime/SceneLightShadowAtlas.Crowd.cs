using System;
using System.Collections.Generic;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed partial class SceneLightShadowAtlas
    {
        private CrowdShadowSource[] _crowds = Array.Empty<CrowdShadowSource>();
        private ulong[] _crowdRevisions = Array.Empty<ulong>();
        private int CrowdDrawCount { get { int count = 0; foreach (var c in _crowds) count += c.DrawCount; return count; } }
        private bool ValidateCrowds(SceneLightShadowSettings settings, int maps, out string error)
        {
            error = null; _crowds = Array.Empty<CrowdShadowSource>(); _crowdRevisions = Array.Empty<ulong>();
            // Unity deserialization can leave a NonSerialized field null; null means no registration.
            if (settings?.crowds == null || settings.crowds.Length == 0) return true;
            if (settings.crowds.Length > 16 || settings.maxCrowdShadowTriangles < 1 || settings.maxCrowdShadowTriangles > 268435456)
            { error = "Invalid crowd shadow source/triangle budget"; return false; }
            long triangles = 0; var unique = new HashSet<CrowdShadowSource>();
            foreach (var source in settings.crowds)
            {
                if (source == null || !source.IsPrepared || !unique.Add(source))
                { error = "Crowd shadow sources must be distinct and currently prepared"; return false; }
                triangles += source.TriangleCount;
            }
            if (triangles * maps > settings.maxCrowdShadowTriangles)
            { error = "Crowd shadow submitted triangle budget exceeded before atlas allocation"; return false; }
            _crowds = (CrowdShadowSource[])settings.crowds.Clone(); _crowdRevisions = new ulong[_crowds.Length];
            for (int i = 0; i < _crowds.Length; i++) _crowdRevisions[i] = _crowds[i].Revision;
            return true;
        }
        private void RecordCrowdGeometry(CommandBuffer commands)
        {
            // Validate all sources first; do not record a partial frame with stale ownership.
            for (int i = 0; i < _crowds.Length; i++)
                if (!_crowds[i].IsPrepared || _crowds[i].Revision != _crowdRevisions[i])
                    throw new InvalidOperationException("Crowd shadow source changed after atlas preparation");
            foreach (var source in _crowds) source.RecordGeometry(commands);
        }
    }
}
