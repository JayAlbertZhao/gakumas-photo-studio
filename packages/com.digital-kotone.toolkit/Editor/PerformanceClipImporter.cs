using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace GakumasPhotoMode.Editor
{
    [ScriptedImporter(1, "performance")]
    public sealed class PerformanceClipImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            var asset = ScriptableObject.CreateInstance<PerformanceClipAsset>();
            asset.clip = null;
            try
            {
                if (new FileInfo(context.assetPath).Length > PerformanceClip.MaxJsonBytes) throw new ArgumentException("Performance JSON exceeds byte limit.");
                if (!PerformanceClip.TryParse(File.ReadAllText(context.assetPath), out asset.clip, out var reason)) throw new ArgumentException(reason);
            }
            catch (Exception error) { asset.importError = error.Message; context.LogImportWarning("Performance rejected: " + error.Message); }
            context.AddObjectToAsset("performance", asset); context.SetMainObject(asset);
        }
    }

    /// <summary>Only an exact opt-in property is consumed. Other FBX models keep their original import behavior.</summary>
    public sealed class PerformanceFbxPostprocessor : AssetPostprocessor
    {
        public const string PropertyName = "photoStudioPerformance";
        public override uint GetVersion() => 1;
        private void OnPostprocessGameObjectWithUserProperties(GameObject node, string[] names, object[] values)
        {
            if (names == null || values == null || names.Length != values.Length) return;
            int found = -1;
            for (int i = 0; i < names.Length; i++) if (names[i] == PropertyName)
            {
                if (found >= 0) { Debug.LogWarning("[PerformanceImport] Duplicate performance property rejected."); return; }
                found = i;
            }
            if (found < 0) return;
            if (!(values[found] is string json) || !PerformanceClip.TryParse(json, out var clip, out _))
            { Debug.LogWarning("[PerformanceImport] Invalid performance property rejected."); return; }
            if (node.GetComponent<ImportedPerformance>() != null)
            { Debug.LogWarning("[PerformanceImport] Duplicate performance component rejected."); return; }
            node.AddComponent<ImportedPerformance>().clip = clip;
        }
    }
}
