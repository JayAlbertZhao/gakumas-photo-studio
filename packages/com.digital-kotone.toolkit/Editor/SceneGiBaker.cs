using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GakumasPhotoMode.Editor
{
    /// <summary>Opt-in reference bake into a fresh scene copy; never changes source scene assets.</summary>
    public static class SceneGiBaker
    {
        [Serializable] public sealed class Options
        {
            public float texelsPerUnit = 8;
            public int atlasSize = 256, directSamples = 64, indirectSamples = 256, environmentSamples = 64, bounces = 2;
            public bool directional;
            public LightingSettings.Lightmapper lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
        }
        [Serializable] public sealed class MapRecord
        {
            public string color, direction, sha256, format;
            public int width, height;
        }
        [Serializable] public sealed class ReceiverRecord
        {
            public string path, mesh;
            public int lightmapIndex;
            public Vector4 scaleOffset;
        }
        [Serializable] public sealed class ProbeRecord { public Vector3 position; public float[] coefficients; }
        [Serializable] public sealed class SourceRecord { public string path, sha256, sha256After; }
        [Serializable] public sealed class Result
        {
            public string schema = "photo-studio.reference-gi-bake.v1", engine, scene, lightingData, sourceSceneSha256, sourceSceneSha256After, actualLightmapper;
            public bool accepted;
            public double seconds;
            public int whiteLights;
            public Options settings;
            public MapRecord[] maps;
            public ReceiverRecord[] receivers;
            public ProbeRecord[] probes;
            public SourceRecord[] sourceAssets;
        }

        public static Result BakeSceneCopy(string sourceScene, string destinationFolder, Options options = null)
        {
            options = options ?? new Options();
            ValidatePaths(sourceScene, destinationFolder);
            if (EditorApplication.isPlayingOrWillChangePlaymode || Lightmapping.isRunning)
                throw new InvalidOperationException("Cannot start GI bake while playing or another bake is running.");
            if (QualitySettings.activeColorSpace != ColorSpace.Linear || GraphicsSettings.currentRenderPipeline != null)
                throw new InvalidOperationException("Reference bake currently requires a Linear Built-in project.");
            if (options.texelsPerUnit < 1 || options.texelsPerUnit > 128 || float.IsNaN(options.texelsPerUnit) ||
                options.atlasSize < 32 || options.atlasSize > 4096 || !Mathf.IsPowerOfTwo(options.atlasSize) ||
                options.directSamples < 1 || options.directSamples > 4096 || options.indirectSamples < 1 || options.indirectSamples > 8192 ||
                options.environmentSamples < 1 || options.environmentSamples > 8192 || options.bounces < 1 || options.bounces > 8)
                throw new ArgumentException("Invalid GI bake settings.");
            if (options.lightmapper != LightingSettings.Lightmapper.ProgressiveCPU && options.lightmapper != LightingSettings.Lightmapper.ProgressiveGPU)
                throw new ArgumentException("Use a supported Progressive lightmapper explicitly.");
            var setup = EditorSceneManager.GetSceneManagerSetup();
            // RestoreSceneManagerSetup cannot recover unsaved additive scenes.
            // A single clean empty scene is safe to recreate; mixed sets must be saved.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.sceneCount > 1 && string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                    throw new InvalidOperationException("Save all additive scenes before baking a copy.");
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save or discard dirty scenes explicitly before baking a copy.");
            }
            string before = HashFile(sourceScene), destination = destinationFolder + "/Reference.unity";
            var originals = new List<SourceRecord>();
            foreach (string path in AssetDatabase.GetDependencies(sourceScene, true))
            {
                if (File.Exists(path)) originals.Add(new SourceRecord { path = path, sha256 = HashFile(path) });
                if (File.Exists(path + ".meta")) originals.Add(new SourceRecord { path = path + ".meta", sha256 = HashFile(path + ".meta") });
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(destinationFolder); AssetDatabase.Refresh();
                if (!AssetDatabase.CopyAsset(sourceScene, destination)) throw new IOException("Scene copy failed.");
                var scene = EditorSceneManager.OpenScene(destination, OpenSceneMode.Single);
                Lightmapping.lightingDataAsset = null;
                Lightmapping.Clear();
                var settings = new LightingSettings {
                    name = "Toolkit white reference GI", autoGenerate = false, bakedGI = true, realtimeGI = false,
                    lightmapper = options.lightmapper,
                    lightmapResolution = options.texelsPerUnit, lightmapMaxSize = options.atlasSize,
                    directSampleCount = options.directSamples, indirectSampleCount = options.indirectSamples,
                    environmentSampleCount = options.environmentSamples, minBounces = options.bounces, maxBounces = options.bounces,
                    directionalityMode = options.directional ? LightmapsMode.CombinedDirectional : LightmapsMode.NonDirectional,
                    lightmapCompression = LightmapCompression.None, prioritizeView = false
                };
                if (settings.lightmapper != options.lightmapper) throw new InvalidOperationException("Requested lightmapper is unavailable; select an installed backend explicitly.");
                AssetDatabase.CreateAsset(settings, destinationFolder + "/ReferenceLighting.asset"); Lightmapping.lightingSettings = settings;
                // No ambient fallback or colored sky contribution in the reference bake.
                RenderSettings.skybox = null; RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.black;
                RenderSettings.ambientIntensity = 0; RenderSettings.reflectionIntensity = 0;
                int whiteLights = 0, meshCount = 0, materialCount = 0;
                var materials = new Dictionary<Material, Material>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var light in root.GetComponentsInChildren<Light>(true))
                    {
                        if (!light.isActiveAndEnabled) continue;
                        light.color = Color.white; light.useColorTemperature = false; light.lightmapBakeType = LightmapBakeType.Baked; whiteLights++;
                    }
                    foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                        var filter = r.GetComponent<MeshFilter>();
                        if (filter == null || filter.sharedMesh == null) throw new InvalidOperationException("GI receiver lacks a mesh: " + r.name);
                        // Clone before unwrapping, even if the source already contains UV2.
                        var mesh = UnityEngine.Object.Instantiate(filter.sharedMesh); mesh.name = r.name + " reference UV2";
                        if (!Unwrapping.GenerateSecondaryUVSet(mesh)) throw new InvalidOperationException("UV2 generation failed: " + r.name);
                        AssetDatabase.CreateAsset(mesh, destinationFolder + "/Mesh-" + meshCount++ + ".asset"); filter.sharedMesh = mesh;
                        var bound = r.sharedMaterials;
                        for (int i = 0; i < bound.Length; i++)
                        {
                            if (bound[i] == null) throw new InvalidOperationException("GI receiver has a missing material.");
                            if (!materials.TryGetValue(bound[i], out var copy))
                            {
                                copy = new Material(bound[i]) { name = bound[i].name + " reference bake" };
                                copy.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                                if (copy.HasProperty("_EmissionColor")) copy.SetColor("_EmissionColor", Color.black);
                                AssetDatabase.CreateAsset(copy, destinationFolder + "/Material-" + materialCount++ + ".mat"); materials.Add(bound[i], copy);
                            }
                            bound[i] = copy;
                        }
                        r.sharedMaterials = bound;
                        GameObjectUtility.SetStaticEditorFlags(r.gameObject, GameObjectUtility.GetStaticEditorFlags(r.gameObject) | StaticEditorFlags.ContributeGI);
                        r.receiveGI = ReceiveGI.Lightmaps;
                    }
                }
                if (whiteLights == 0 || meshCount == 0) throw new InvalidOperationException("Reference bake requires active authored lights and static mesh receivers.");
                AssetDatabase.SaveAssets(); if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Cannot save reference scene.");
                Debug.Log("[SceneGiBake] Starting actual " + settings.lightmapper + " bake: " + destination);
                if (!Lightmapping.Bake()) throw new InvalidOperationException("Lightmapping.Bake did not finish successfully; inspect the Editor log.");
                if (Lightmapping.lightingDataAsset == null || LightmapSettings.lightmaps.Length == 0)
                    throw new InvalidOperationException("Bake returned without lightmap/lighting data outputs.");
                EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
                var result = Describe(scene, options); result.whiteLights = whiteLights; result.seconds = clock.Elapsed.TotalSeconds;
                result.actualLightmapper = Lightmapping.lightingSettings.lightmapper.ToString();
                result.sourceSceneSha256 = before; result.sourceSceneSha256After = HashFile(sourceScene);
                result.accepted = before == result.sourceSceneSha256After && result.maps.Length > 0;
                result.sourceAssets = originals.ToArray();
                foreach (var asset in result.sourceAssets)
                { asset.sha256After = File.Exists(asset.path) ? HashFile(asset.path) : null; result.accepted &= asset.sha256 == asset.sha256After; }
                File.WriteAllText(destinationFolder + "/bake.json", JsonUtility.ToJson(result, true));
                Debug.Log("[SceneGiBake] Complete: maps=" + result.maps.Length + "; probes=" + result.probes.Length + "; seconds=" + result.seconds);
                if (!result.accepted) throw new InvalidOperationException("Source scene or dependency changed during reference bake.");
                return result;
            }
            finally
            {
                // Restore the caller's scene set; generated outputs remain for inspection, including failures.
                if (setup.Length > 0 && !string.IsNullOrEmpty(setup[0].path)) EditorSceneManager.RestoreSceneManagerSetup(setup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        private static Result Describe(Scene scene, Options settings)
        {
            var result = new Result { engine = Application.unityVersion, scene = scene.path, settings = settings,
                lightingData = AssetDatabase.GetAssetPath(Lightmapping.lightingDataAsset) };
            var maps = new List<MapRecord>();
            foreach (var map in LightmapSettings.lightmaps)
            {
                if (map.lightmapColor == null) throw new InvalidOperationException("Missing generated lightmap.");
                string path = AssetDatabase.GetAssetPath(map.lightmapColor);
                maps.Add(new MapRecord { color = path, direction = AssetDatabase.GetAssetPath(map.lightmapDir), sha256 = HashFile(path),
                    width = map.lightmapColor.width, height = map.lightmapColor.height, format = map.lightmapColor.format.ToString() });
            }
            result.maps = maps.ToArray(); var receivers = new List<ReceiverRecord>();
            foreach (var root in scene.GetRootGameObjects()) foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r.lightmapIndex < 0 || r.lightmapIndex >= result.maps.Length) throw new InvalidOperationException("Receiver missing baked index: " + r.name);
                receivers.Add(new ReceiverRecord { path = AnimationUtility.CalculateTransformPath(r.transform, null), lightmapIndex = r.lightmapIndex,
                    scaleOffset = r.lightmapScaleOffset, mesh = AssetDatabase.GetAssetPath(r.GetComponent<MeshFilter>().sharedMesh) });
            }
            result.receivers = receivers.ToArray(); var probes = LightmapSettings.lightProbes;
            result.probes = new ProbeRecord[probes == null ? 0 : probes.count];
            if (probes != null)
            {
                var positions = probes.positions; var coefficients = probes.bakedProbes;
                for (int i = 0; i < probes.count; i++)
                {
                    var row = new ProbeRecord { position = positions[i], coefficients = new float[27] };
                    for (int c = 0; c < 3; c++) for (int j = 0; j < 9; j++) row.coefficients[c * 9 + j] = coefficients[i][c, j];
                    result.probes[i] = row;
                }
            }
            return result;
        }
        private static void ValidatePaths(string source, string folder)
        {
            if (string.IsNullOrWhiteSpace(source) || !source.StartsWith("Assets/", StringComparison.Ordinal) || !source.EndsWith(".unity", StringComparison.Ordinal) || !File.Exists(source))
                throw new ArgumentException("Source must be a saved Assets scene.");
            if (string.IsNullOrWhiteSpace(folder) || !folder.StartsWith("Assets/", StringComparison.Ordinal) || folder.Contains("..") || folder.Contains("\\") || source.Contains("..") || source.Contains("\\"))
                throw new ArgumentException("Use canonical project-relative Assets paths.");
            var assets = Path.GetFullPath("Assets") + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(folder).StartsWith(assets, StringComparison.OrdinalIgnoreCase) || Directory.Exists(folder) || File.Exists(folder))
                throw new ArgumentException("Destination must be a fresh directory within Assets; outputs are never overwritten.");
        }
        private static string HashFile(string path)
        { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    }
}
