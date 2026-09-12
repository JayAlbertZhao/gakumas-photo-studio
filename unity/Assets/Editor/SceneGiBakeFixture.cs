using System;
using System.Collections.Generic;
using System.IO;
using GakumasPhotoMode.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GakumasPhotoMode.Editor
{
    /// <summary>Self-authored, opt-in real-lightmapper fixture. Generated payload stays local.</summary>
    public static class SceneGiBakeFixture
    {
        [Serializable] private sealed class GuardReport { public bool accepted; public List<string> checks = new List<string>(); }
        [Serializable] private sealed class BakeControls
        {
            public bool accepted;
            public Color referenceOccluded, withoutOccluder, referenceBounce, grayWallBounce;
            public SceneGiBaker.Result noOccluder, grayWall;
        }
        public static void Build()
        {
            string name = Environment.GetEnvironmentVariable("GAKUMAS_GI_BAKE_NAME");
            string output = Environment.GetEnvironmentVariable("GAKUMAS_GI_BAKE_OUTPUT");
            if (string.IsNullOrEmpty(name) || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9-]+$") || string.IsNullOrEmpty(output))
                throw new ArgumentException("Explicit GAKUMAS_GI_BAKE_NAME and GAKUMAS_GI_BAKE_OUTPUT are required.");
            output = Path.GetFullPath(output);
            if (Directory.Exists(output)) throw new IOException("Use a fresh fixture output directory.");
            string folder = "Assets/LocalGiFixtures/" + name;
            if (Directory.Exists(folder)) throw new IOException("Use a fresh fixture name.");
            var setup = EditorSceneManager.GetSceneManagerSetup();
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Refusing dirty Editor scenes.");
            try
            {
                Directory.CreateDirectory(folder); Directory.CreateDirectory(output); AssetDatabase.Refresh();
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var standard = Shader.Find("Standard"); if (standard == null) throw new InvalidOperationException("Standard Meta pass required for fixture bake.");
                Material Material(string label, Color color)
                {
                    var m = new Material(standard) { name = label }; m.color = color; m.SetFloat("_Glossiness", 0); m.SetFloat("_Metallic", 0);
                    AssetDatabase.CreateAsset(m, folder + "/" + label + ".mat"); return m;
                }
                var gray = Material("Gray", new Color(.65f, .65f, .65f));
                var red = Material("RedBounce", new Color(.8f, .06f, .04f));
                void Box(string label, Vector3 position, Vector3 size, Material material)
                {
                    var o = GameObject.CreatePrimitive(PrimitiveType.Cube); o.name = label; o.transform.position = position; o.transform.localScale = size; o.GetComponent<Renderer>().sharedMaterial = material;
                }
                Box("Floor", new Vector3(0, -.1f, 0), new Vector3(6, .2f, 6), gray);
                Box("BounceWall", new Vector3(-2.9f, 1.5f, 0), new Vector3(.2f, 3, 6), red);
                Box("BackWall", new Vector3(0, 1.5f, 2.9f), new Vector3(6, 3, .2f), gray);
                Box("Occluder", new Vector3(.65f, 1, 0), new Vector3(.3f, 2, 2), gray);
                var lamp = new GameObject("ReferenceLamp").AddComponent<Light>(); lamp.type = LightType.Point; lamp.range = 12; lamp.intensity = 3;
                lamp.color = Color.blue; lamp.transform.position = new Vector3(-1.5f, 2.3f, -.6f); lamp.shadows = LightShadows.Soft;
                var positions = new List<Vector3>();
                foreach (float x in new[] { -2.2f, -1f, 0f, 1.4f, 2.2f }) foreach (float y in new[] { .35f, 1f, 2.5f }) foreach (float z in new[] { -1.8f, .8f, 2f }) positions.Add(new Vector3(x, y, z));
                new GameObject("ReferenceProbes").AddComponent<LightProbeGroup>().probePositions = positions.ToArray();
                string source = folder + "/Source.unity"; EditorSceneManager.SaveScene(scene, source); AssetDatabase.SaveAssets();
                var guards = new GuardReport();
                void Reject(string label, string from, string destination, SceneGiBaker.Options options = null)
                {
                    bool rejected = false, existed = Directory.Exists(destination);
                    try { SceneGiBaker.BakeSceneCopy(from, destination, options); }
                    catch (ArgumentException) { rejected = true; }
                    catch (InvalidOperationException) { rejected = true; }
                    if (!rejected || (!existed && Directory.Exists(destination))) throw new InvalidOperationException("Bake input guard failed: " + label);
                    guards.checks.Add(label);
                }
                Reject("missing-source", folder + "/Missing.unity", folder + "/NoMissingSource");
                Reject("existing-output-never-overwritten", source, folder);
                Reject("outside-assets", source, "Library/NoReferenceBake");
                Reject("path-traversal", source, folder + "/../NoReferenceBake");
                Reject("invalid-resolution", source, folder + "/NoInvalidResolution", new SceneGiBaker.Options { texelsPerUnit = float.NaN });
                Reject("invalid-sample-count", source, folder + "/NoInvalidSamples", new SceneGiBaker.Options { indirectSamples = int.MaxValue });
                Reject("invalid-backend", source, folder + "/NoInvalidBackend", new SceneGiBaker.Options { lightmapper = (LightingSettings.Lightmapper)999 });
                EditorSceneManager.MarkSceneDirty(scene);
                Reject("dirty-scene-preserved", source, folder + "/NoDirtyBake");
                if (!scene.isDirty) throw new InvalidOperationException("Dirty scene was silently discarded.");
                EditorSceneManager.OpenScene(source, OpenSceneMode.Single);
                var unsaved = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Reject("unsaved-additive-scene-not-discarded", source, folder + "/NoUnsavedAdditiveBake");
                if (!unsaved.IsValid() || !unsaved.isLoaded) throw new InvalidOperationException("Unsaved additive scene was discarded.");
                EditorSceneManager.CloseScene(unsaved, true); SceneManager.SetActiveScene(SceneManager.GetSceneByPath(source));
                var result = SceneGiBaker.BakeSceneCopy(source, folder + "/Baked");
                if (result.probes.Length < 4) throw new InvalidOperationException("Actual bake produced no usable probe volume.");
                var restored = SceneManager.GetActiveScene();
                if (restored.path != source || restored.isDirty) throw new InvalidOperationException("Original saved scene was not restored.");
                var sourceLamp = GameObject.Find("ReferenceLamp").GetComponent<Light>();
                if (sourceLamp.color != Color.blue || result.sourceAssets.Length < 6) throw new InvalidOperationException("Source scene/dependency isolation failed.");
                guards.checks.Add("original-scene-restored-blue-light-preserved");
                guards.checks.Add("source-scene-materials-and-metadata-sha256-unchanged"); guards.accepted = true;
                if (Environment.GetEnvironmentVariable("GAKUMAS_GI_BAKE_CONTROLS") == "1")
                {
                    SceneGiBaker.Result Control(string label, Action change)
                    {
                        string path = folder + "/" + label + ".unity";
                        if (!AssetDatabase.CopyAsset(source, path)) throw new IOException("Cannot copy control scene.");
                        var control = EditorSceneManager.OpenScene(path, OpenSceneMode.Single); change(); EditorSceneManager.SaveScene(control);
                        return SceneGiBaker.BakeSceneCopy(path, folder + "/" + label + "-Baked");
                    }
                    var controls = new BakeControls {
                        noOccluder = Control("NoOccluder", () => UnityEngine.Object.DestroyImmediate(GameObject.Find("Occluder"))),
                        grayWall = Control("GrayWall", () => GameObject.Find("BounceWall").GetComponent<Renderer>().sharedMaterial = gray)
                    };
                    controls.referenceOccluded = EvaluateProbe(result, new Vector3(1.4f, 1, .8f));
                    controls.withoutOccluder = EvaluateProbe(controls.noOccluder, new Vector3(1.4f, 1, .8f));
                    controls.referenceBounce = EvaluateProbe(result, new Vector3(-2.2f, 1, .8f));
                    controls.grayWallBounce = EvaluateProbe(controls.grayWall, new Vector3(-2.2f, 1, .8f));
                    controls.accepted = controls.noOccluder.accepted && controls.grayWall.accepted &&
                        controls.withoutOccluder.grayscale > controls.referenceOccluded.grayscale * 2 &&
                        controls.referenceBounce.r / Mathf.Max(.0001f, controls.referenceBounce.g) > 1.03f &&
                        Mathf.Abs(controls.grayWallBounce.r - controls.grayWallBounce.g) < .01f &&
                        Mathf.Abs(controls.grayWallBounce.r - controls.grayWallBounce.b) < .01f;
                    File.WriteAllText(Path.Combine(output, "controls.json"), JsonUtility.ToJson(controls, true));
                    if (!controls.accepted) throw new InvalidOperationException("Actual bake geometry/color controls failed.");
                    EditorSceneManager.OpenScene(source, OpenSceneMode.Single);
                }
                // Export the copied scene with its real LightingDataAsset, maps, probe data and geometry.
                var manifest = ToolkitBuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild {
                    assetBundleName = "reference-gi", assetNames = new[] { result.scene }
                } }, BuildAssetBundleOptions.UncompressedAssetBundle, BuildTarget.StandaloneWindows64);
                if (manifest == null) throw new InvalidOperationException("Reference scene bundle build failed.");
                File.WriteAllText(Path.Combine(output, "bake.json"), JsonUtility.ToJson(result, true));
                File.WriteAllText(Path.Combine(output, "guards.json"), JsonUtility.ToJson(guards, true));
                Debug.Log("[SceneGiBakeFixture] Bundle ready: " + output);
            }
            finally
            {
                if (setup.Length > 0 && !string.IsNullOrEmpty(setup[0].path)) EditorSceneManager.RestoreSceneManagerSetup(setup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
        private static Color EvaluateProbe(SceneGiBaker.Result bake, Vector3 position)
        {
            foreach (var record in bake.probes)
            {
                if ((record.position - position).sqrMagnitude > .00001f) continue;
                var sh = new SphericalHarmonicsL2();
                for (int c = 0; c < 3; c++) for (int i = 0; i < 9; i++) sh[c, i] = record.coefficients[c * 9 + i];
                var colors = new Color[1]; sh.Evaluate(new[] { Vector3.up }, colors); return colors[0];
            }
            throw new InvalidOperationException("Missing authored probe position in actual bake.");
        }
    }
}
