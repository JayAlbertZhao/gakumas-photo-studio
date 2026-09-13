using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GakumasPhotoMode.Editor
{
    /// <summary>Explicit generated-DCC transport probe, not part of normal model import or build.</summary>
    public static class PerformanceAuthoringValidation
    {
        [Serializable] private sealed class Check { public string name; public bool accepted; }
        [Serializable] private sealed class Report { public bool accepted; public string error, bundle; public List<Check> checks = new List<Check>(); }
        public static void Run()
        {
            string input = Environment.GetEnvironmentVariable("GAKUMAS_PERFORMANCE_INPUT");
            string output = Environment.GetEnvironmentVariable("GAKUMAS_PERFORMANCE_OUTPUT");
            string fixture = Environment.GetEnvironmentVariable("GAKUMAS_PERFORMANCE_FIXTURE");
            var report = new Report();
            void Check(string name, bool accepted) { report.checks.Add(new Check { name = name, accepted = accepted }); if (!accepted) throw new InvalidOperationException(name); }
            try
            {
                if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output) || fixture == null || !System.Text.RegularExpressions.Regex.IsMatch(fixture, @"\AAssets/PerformanceAuthoringProbe/v[0-9]+\z") || Directory.Exists(fixture))
                    throw new ArgumentException("Explicit input/output and fresh scoped fixture directory are required.");
                Directory.CreateDirectory(output); Directory.CreateDirectory(fixture);
                foreach (string name in new[] { "performance.performance", "model.fbx", "plain.fbx" })
                    File.Copy(Path.Combine(input, name), fixture + "/" + name, false);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadAssetAtPath<PerformanceClipAsset>(fixture + "/performance.performance");
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(fixture + "/model.fbx");
                var plain = AssetDatabase.LoadAssetAtPath<GameObject>(fixture + "/plain.fbx");
                Check("actual-scripted-importer", asset != null && asset.clip != null && string.IsNullOrEmpty(asset.importError));
                Check("actual-fbx-custom-property-callback", model != null && model.GetComponentsInChildren<ImportedPerformance>(true).Length == 1);
                Check("actual-fbx-geometry", model.GetComponentsInChildren<MeshFilter>(true).Any(m => m.sharedMesh != null && m.sharedMesh.vertexCount >= 3));
                Check("unmarked-model-untouched", plain != null && plain.GetComponentsInChildren<ImportedPerformance>(true).Length == 0);
                string canonical = JsonUtility.ToJson(asset.clip);
                Check("sidecar-fbx-conversion-identical", canonical == JsonUtility.ToJson(model.GetComponentInChildren<ImportedPerformance>(true).clip));
                Check("dcc-baked-shared-channels", asset.clip.morphs.Length == 1 && asset.clip.morphs[0].keys.Length == 31 && asset.clip.decals[0].tracks[0].keys.Length == 31 &&
                    asset.clip.effects.Length == 1 && asset.clip.effects[0].id == "marker.0" && asset.clip.materials[0].id == "atlas.0");
                AssetDatabase.ImportAsset(fixture + "/model.fbx", ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                model = AssetDatabase.LoadAssetAtPath<GameObject>(fixture + "/model.fbx");
                Check("reimport-one-component", model.GetComponentsInChildren<ImportedPerformance>(true).Length == 1 && canonical == JsonUtility.ToJson(model.GetComponentInChildren<ImportedPerformance>(true).clip));
                string sidecarPath = fixture + "/performance.performance", original = File.ReadAllText(sidecarPath);
                var changed = JsonUtility.FromJson<PerformanceClip>(original); changed.duration = 2;
                File.WriteAllText(sidecarPath, JsonUtility.ToJson(changed)); AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Check("sidecar-reimport-updates-data", AssetDatabase.LoadAssetAtPath<PerformanceClipAsset>(sidecarPath).clip.duration == 2);
                File.WriteAllText(sidecarPath, original); AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Check("sidecar-reimport-restored", JsonUtility.ToJson(AssetDatabase.LoadAssetAtPath<PerformanceClipAsset>(sidecarPath).clip) == canonical);
                string bundleDir = Path.Combine(output, "bundle"); Directory.CreateDirectory(bundleDir);
                var manifest = ToolkitBuildPipeline.BuildAssetBundles(bundleDir, new[] { new AssetBundleBuild {
                    assetBundleName = "performance-reference", assetNames = new[] { sidecarPath, fixture + "/model.fbx", fixture + "/plain.fbx" }
                } }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
                report.bundle = Path.Combine(bundleDir, "performance-reference");
                Check("actual-imported-assets-bundle", manifest != null && File.Exists(report.bundle) && new FileInfo(report.bundle).Length > 0);
                report.accepted = report.checks.All(c => c.accepted);
            }
            catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
            if (!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "import-validation.json"), JsonUtility.ToJson(report, true)); }
            Debug.Log("[PerformanceAuthoringValidation] accepted=" + report.accepted + "; checks=" + report.checks.Count);
            if (!report.accepted) throw new InvalidOperationException(report.error ?? "Performance import validation failed.");
        }
    }
}
