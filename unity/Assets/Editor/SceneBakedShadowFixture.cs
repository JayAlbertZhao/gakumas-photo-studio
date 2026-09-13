using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GakumasPhotoMode.Editor
{
    /// <summary>Opt-in authored real Mixed-light bake and an occluder-removal control.</summary>
    public static class SceneBakedShadowFixture
    {
        [Serializable] private sealed class Report { public bool accepted, bakesAccepted; public SceneGiBaker.Result occluded, clear; }
        public static void Build()
        {
            string name = Environment.GetEnvironmentVariable("GAKUMAS_BAKED_MASK_NAME"), output = Environment.GetEnvironmentVariable("GAKUMAS_BAKED_MASK_OUTPUT");
            if (string.IsNullOrEmpty(name) || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9-]+$") || string.IsNullOrEmpty(output)) throw new ArgumentException("Explicit fresh baked-mask name/output required");
            output = Path.GetFullPath(output); string folder = "Assets/LocalGiFixtures/" + name;
            if (Directory.Exists(output) || Directory.Exists(folder)) throw new IOException("Fresh output and fixture names required");
            var setup = EditorSceneManager.GetSceneManagerSetup();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var caller = SceneManager.GetSceneAt(i);
                if (caller.isDirty || string.IsNullOrEmpty(caller.path) && caller.rootCount > 0) throw new InvalidOperationException("Save all caller scenes first");
            }
            try
            {
                Directory.CreateDirectory(folder); Directory.CreateDirectory(output); AssetDatabase.Refresh();
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var material = new Material(Shader.Find("Standard")) { name = "Own shadowmask gray" }; material.color = Color.gray; material.SetFloat("_Glossiness", 0);
                AssetDatabase.CreateAsset(material, folder + "/Gray.mat");
                var floor = GameObject.CreatePrimitive(PrimitiveType.Plane); floor.name = "BakedMaskFloor"; floor.transform.localScale = Vector3.one * .4f; floor.GetComponent<Renderer>().sharedMaterial = material;
                var blocker = GameObject.CreatePrimitive(PrimitiveType.Cube); blocker.name = "BakedMaskOccluder"; blocker.transform.position = new Vector3(.15f, .65f, .1f); blocker.transform.localScale = new Vector3(.7f, 1.3f, .8f); blocker.GetComponent<Renderer>().sharedMaterial = material;
                for (int i = 0; i < 4; i++)
                {
                    var light = new GameObject("BakedMaskLamp" + i).AddComponent<Light>(); light.type = LightType.Point; light.range = 9; light.intensity = 1; light.shadows = LightShadows.Soft;
                    light.color = Color.blue; light.transform.position = new Vector3(i % 2 == 0 ? -1.5f : 1.5f, 2.5f, i < 2 ? -1.5f : 1.5f);
                }
                string source = folder + "/Source.unity"; EditorSceneManager.SaveScene(scene, source); AssetDatabase.SaveAssets();
                SceneGiBaker.Options Settings(string sceneName) => new SceneGiBaker.Options { sceneName = sceneName, shadowMask = true, texelsPerUnit = 16, atlasSize = 256, directSamples = 128, indirectSamples = 64, environmentSamples = 32, bounces = 1 };
                var report = new Report { occluded = SceneGiBaker.BakeSceneCopy(source, folder + "/Occluded", Settings("Occluded")) };
                string control = folder + "/NoOccluder.unity";
                if (!AssetDatabase.CopyAsset(source, control)) throw new IOException("Control copy failed");
                var clearScene = EditorSceneManager.OpenScene(control, OpenSceneMode.Single); UnityEngine.Object.DestroyImmediate(GameObject.Find("BakedMaskOccluder")); EditorSceneManager.SaveScene(clearScene);
                report.clear = SceneGiBaker.BakeSceneCopy(control, folder + "/Clear", Settings("Clear"));
                report.bakesAccepted = report.occluded.accepted && report.clear.accepted && new[] { report.occluded, report.clear }.All(r =>
                    r.shadowMaps.Length > 0 && r.shadowLights.Length == 4 && r.shadowLights.Select(l => l.channel).Distinct().Count() == 4 && r.shadowLights.All(l => l.channel >= 0 && l.channel < 4));
                File.WriteAllText(Path.Combine(output, "bake.json"), JsonUtility.ToJson(report, true));
                if (!report.bakesAccepted) throw new InvalidOperationException("Actual bake did not allocate four distinct shadow channels");
                var manifest = ToolkitBuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild { assetBundleName = "reference-baked-mask", assetNames = new[] { report.occluded.scene, report.clear.scene } } }, BuildAssetBundleOptions.UncompressedAssetBundle, BuildTarget.StandaloneWindows64);
                string bundlePath = Path.Combine(output, "reference-baked-mask");
                if (manifest == null || !File.Exists(bundlePath) || new FileInfo(bundlePath).Length < 1024) throw new InvalidOperationException("Baked mask scene bundle payload missing despite build manifest");
                var verify = AssetBundle.LoadFromFile(bundlePath);
                if (verify == null) throw new InvalidOperationException("Generated scene bundle cannot be opened");
                try { if (verify.GetAllScenePaths().Length != 2) throw new InvalidOperationException("Generated bundle lacks both actual scenes"); }
                finally { verify.Unload(true); }
                report.accepted = true;
                File.WriteAllText(Path.Combine(output, "bake.json"), JsonUtility.ToJson(report, true));
                Debug.Log("[SceneBakedShadowFixture] Bundle ready: " + output);
            }
            finally
            {
                if (setup.Length > 0 && !string.IsNullOrEmpty(setup[0].path)) EditorSceneManager.RestoreSceneManagerSetup(setup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
    }
}
