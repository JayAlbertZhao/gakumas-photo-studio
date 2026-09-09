using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Loads the original riverbed photography environment from the current DMM
    /// Octo extraction.  Geometry is mapped into the local viewer basis by the
    /// authored layout-001 actor transform, so the local identity-root actor
    /// stands at the same point and faces the same direction as the original.
    /// </summary>
    public sealed class OriginalRiverbedEnvironment : IDisposable
    {
        private const string SceneBundleName = "env_3d_adv_riverbed-00-00-morning";
        private const string PhotoGroupBundleName = "mdl_env_grp_photo_riverbed-00-00";
        private const string DefaultRoot = @"D:\digital-kotone-assets\experiments\20260814-gakumas-photo-mode-fkotone\research\octo-705100-riverbed";

        // scl_photo_riverbed-00-00 / layout-001 / Actor_00.
        private static readonly Vector3 AuthoredActorPosition =
            new Vector3(30.01000023f, 0f, 4.71999979f);
        private const float AuthoredActorYaw = -95.06024f;

        [Serializable]
        private sealed class ExtractionManifest
        {
            public ExtractionBundleRecord[] bundles;
        }

        [Serializable]
        private sealed class ExtractionBundleRecord
        {
            public string name;
            public bool requested;
        }

        private readonly List<AssetBundle> _ownedBundles = new List<AssetBundle>();
        private readonly List<Scene> _loadedScenes = new List<Scene>();
        private GameObject _root;

        public GameObject Root { get { return _root; } }
        public bool IsLoaded { get { return _root != null; } }
        public int RendererCount { get; private set; }
        public int UnsupportedMaterialCount { get; private set; }

        public bool TryLoad()
        {
            string rootPath = Environment.GetEnvironmentVariable("GAKUMAS_RIVERBED_STAGING");
            if (string.IsNullOrWhiteSpace(rootPath)) rootPath = DefaultRoot;
            rootPath = Path.GetFullPath(rootPath);
            string manifestPath = Path.Combine(rootPath, "extraction-manifest.json");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning("[Riverbed] Extraction manifest not found: " + manifestPath);
                return false;
            }

            string unityCnKey = Environment.GetEnvironmentVariable("GAKUMAS_UNITYCN_KEY");
            if (string.IsNullOrEmpty(unityCnKey))
            {
                Debug.LogWarning(
                    "[Riverbed] GAKUMAS_UNITYCN_KEY is not set; refusing to load " +
                    "ArchiveStorage-protected bundles. The studio fallback remains active.");
                return false;
            }

            MethodInfo setDecryptKey = typeof(AssetBundle).GetMethod(
                "SetAssetBundleDecryptKey",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            if (setDecryptKey == null)
            {
                Debug.LogWarning(
                    "[Riverbed] This player runtime exposes no AssetBundle.SetAssetBundleDecryptKey API.");
                return false;
            }

            try
            {
                setDecryptKey.Invoke(null, new object[] { unityCnKey });
                Debug.Log("[Riverbed] Installed the captured ArchiveStorage decrypt key.");
                ExtractionManifest manifest = JsonUtility.FromJson<ExtractionManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || manifest.bundles == null)
                    throw new InvalidDataException("Invalid riverbed extraction manifest: " + manifestPath);

                Dictionary<string, AssetBundle> loaded = AssetBundle.GetAllLoadedAssetBundles()
                    .Where(value => value != null)
                    .GroupBy(value => value.name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(value => value.Key, value => value.First(), StringComparer.OrdinalIgnoreCase);
                foreach (ExtractionBundleRecord record in manifest.bundles
                             .OrderBy(value => value.requested)
                             .ThenBy(value => value.name, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(record.name) || loaded.ContainsKey(record.name)) continue;
                    string path = Path.Combine(rootPath, record.name);
                    if (!File.Exists(path)) continue;
                    AssetBundle bundle = AssetBundle.LoadFromFile(path);
                    if (bundle == null)
                    {
                        Debug.LogWarning("[Riverbed] AssetBundle.LoadFromFile failed: " + path);
                        continue;
                    }
                    loaded[bundle.name] = bundle;
                    _ownedBundles.Add(bundle);
                }

                AssetBundle sceneBundle;
                if (!loaded.TryGetValue(SceneBundleName, out sceneBundle))
                    throw new InvalidDataException("Riverbed scene bundle was not loaded: " + SceneBundleName);

                _root = new GameObject("OriginalRiverbedEnvironment");
                Quaternion toLocalActor = Quaternion.Euler(0f, -AuthoredActorYaw, 0f);
                _root.transform.rotation = toLocalActor;
                _root.transform.position = -(toLocalActor * AuthoredActorPosition);

                int instantiatedRoots = InstantiateBundleContent(sceneBundle, _root.transform);
                AssetBundle photoGroupBundle;
                if (loaded.TryGetValue(PhotoGroupBundleName, out photoGroupBundle))
                    instantiatedRoots += InstantiateBundleContent(photoGroupBundle, _root.transform);
                if (instantiatedRoots == 0)
                    throw new InvalidDataException("Riverbed bundles exposed no scene or prefab roots.");

                Sanitize(_root);
                Debug.Log(string.Format(
                    "[Riverbed] Ready: roots={0} renderers={1} unsupportedMaterials={2} position={3} rotation={4}",
                    instantiatedRoots, RendererCount, UnsupportedMaterialCount,
                    _root.transform.position, _root.transform.eulerAngles));
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Dispose();
                return false;
            }
        }

        public void SetActive(bool active)
        {
            if (_root != null) _root.SetActive(active);
        }

        private int InstantiateBundleContent(AssetBundle bundle, Transform parent)
        {
            string[] scenePaths = bundle.GetAllScenePaths();
            if (scenePaths != null && scenePaths.Length > 0)
            {
                string path = scenePaths[0];
                SceneManager.LoadScene(path, LoadSceneMode.Additive);
                Scene scene = SceneManager.GetSceneByPath(path);
                if (!scene.IsValid() || !scene.isLoaded)
                    scene = SceneManager.GetSceneByName(Path.GetFileNameWithoutExtension(path));
                if (!scene.IsValid() || !scene.isLoaded)
                    throw new InvalidDataException("Loaded riverbed scene was not found: " + path);
                _loadedScenes.Add(scene);
                GameObject[] roots = scene.GetRootGameObjects();
                foreach (GameObject value in roots) value.transform.SetParent(parent, false);
                Debug.Log(string.Format("[Riverbed] Loaded additive scene {0}: roots={1}", path, roots.Length));
                return roots.Length;
            }

            GameObject[] prefabs = bundle.LoadAllAssets<GameObject>();
            GameObject[] rootsOnly = prefabs.Where(value => value != null && value.transform.parent == null).ToArray();
            foreach (GameObject prefab in rootsOnly)
            {
                GameObject instance = UnityEngine.Object.Instantiate(prefab, parent, false);
                instance.name = prefab.name;
            }
            Debug.Log(string.Format("[Riverbed] Instantiated bundle {0}: roots={1}", bundle.name, rootsOnly.Length));
            return rootsOnly.Length;
        }

        private void Sanitize(GameObject root)
        {
            foreach (Behaviour behaviour in root.GetComponentsInChildren<Behaviour>(true))
                behaviour.enabled = false;
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            HashSet<Material> materials = new HashSet<Material>();
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null) materials.Add(material);
                }
            }
            RendererCount = renderers.Length;
            UnsupportedMaterialCount = materials.Count(value => value.shader == null || !value.shader.isSupported);
            foreach (IGrouping<string, Material> group in materials.GroupBy(
                         value => value.shader == null ? "<null>" : value.shader.name))
            {
                bool supported = group.All(value => value.shader != null && value.shader.isSupported);
                Debug.Log(string.Format("[Riverbed] Shader {0}: materials={1} supported={2}",
                    group.Key, group.Count(), supported));
            }
        }

        public void Dispose()
        {
            if (_root != null)
            {
                UnityEngine.Object.DestroyImmediate(_root);
                _root = null;
            }
            foreach (Scene scene in _loadedScenes)
            {
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
            _loadedScenes.Clear();
            foreach (AssetBundle bundle in _ownedBundles.AsEnumerable().Reverse())
            {
                if (bundle != null) bundle.Unload(false);
            }
            _ownedBundles.Clear();
            RendererCount = 0;
            UnsupportedMaterialCount = 0;
        }
    }
}
