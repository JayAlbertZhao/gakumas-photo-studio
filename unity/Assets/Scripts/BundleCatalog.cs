using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VL;

namespace GakumasPhotoMode
{
    public sealed class BundleCatalog : IDisposable
    {
        private readonly Dictionary<string, AssetBundle> _loaded = new Dictionary<string, AssetBundle>();
        private readonly Dictionary<string, BundleRecord> _records = new Dictionary<string, BundleRecord>();
        private readonly Dictionary<string, MotionRuntimeMetadata> _motionMetadata =
            new Dictionary<string, MotionRuntimeMetadata>(StringComparer.OrdinalIgnoreCase);

        public string StagingRoot { get; private set; }
        public Phase1Manifest Manifest { get; private set; }

        public static string DefaultStagingRoot
        {
            get
            {
                string fromEnvironment = Environment.GetEnvironmentVariable("GAKUMAS_PHOTO_STAGING");
                return string.IsNullOrWhiteSpace(fromEnvironment)
                    ? @"D:\digital-kotone-assets\experiments\20260814-gakumas-photo-mode-fkotone"
                    : fromEnvironment;
            }
        }

        public void Load(string stagingRoot)
        {
            StagingRoot = Path.GetFullPath(stagingRoot);
            string manifestPath = Path.Combine(StagingRoot, "staging-manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException("Run stage_octo_assets.py first.", manifestPath);
            }

            Manifest = JsonUtility.FromJson<Phase1Manifest>(File.ReadAllText(manifestPath));
            if (Manifest == null || Manifest.bundles == null)
            {
                throw new InvalidDataException("Invalid staging manifest: " + manifestPath);
            }

            foreach (BundleRecord record in Manifest.bundles)
            {
                _records[record.name] = record;
            }

            foreach (BundleRecord record in Manifest.bundles.Where(value => value.role == "dependency"))
            {
                LoadBundle(record);
            }
            foreach (BundleRecord record in Manifest.bundles.Where(value => value.role != "dependency"))
            {
                LoadBundle(record);
            }
        }

        public IEnumerable<BundleRecord> RecordsForRole(string role)
        {
            return Manifest.bundles.Where(value => value.role == role);
        }

        public AssetBundle GetBundle(string name)
        {
            AssetBundle bundle;
            if (!_loaded.TryGetValue(name, out bundle))
            {
                throw new KeyNotFoundException("Bundle not loaded: " + name);
            }
            return bundle;
        }

        public T[] LoadAll<T>(string bundleName) where T : UnityEngine.Object
        {
            return GetBundle(bundleName).LoadAllAssets<T>();
        }

        public UnityEngine.Object[] LoadObjectsWithSubAssets(string bundleName)
        {
            AssetBundle bundle = GetBundle(bundleName);
            List<UnityEngine.Object> result = new List<UnityEngine.Object>(bundle.LoadAllAssets<UnityEngine.Object>());
            foreach (string assetName in bundle.GetAllAssetNames())
            {
                foreach (UnityEngine.Object value in bundle.LoadAssetWithSubAssets<UnityEngine.Object>(assetName))
                {
                    if (value != null && !result.Contains(value)) result.Add(value);
                }
            }
            return result.ToArray();
        }

        public AnimationClip[] LoadAnimationClips(string bundleName)
        {
            return LoadMotionRuntimeMetadata(bundleName).clips;
        }

        public MotionRuntimeMetadata LoadMotionRuntimeMetadata(string bundleName)
        {
            MotionRuntimeMetadata cached;
            if (_motionMetadata.TryGetValue(bundleName, out cached)) return cached;

            AssetBundle bundle = GetBundle(bundleName);
            List<AnimationClip> clips = new List<AnimationClip>(bundle.LoadAllAssets<AnimationClip>());
            List<PropConstraintData> propConstraints = new List<PropConstraintData>();
            bool enableSeatedDynamicCorrection = false;
            int definitionCount = 0;
            foreach (string assetName in bundle.GetAllAssetNames())
            {
                ActorMotionDefine definition = bundle.LoadAsset<ActorMotionDefine>(assetName);
                if (definition != null)
                {
                    definitionCount++;
                    enableSeatedDynamicCorrection |= definition.enableSeatedDynamicCorrection;
                    if (definition.propConstraints != null)
                    {
                        foreach (PropConstraintData value in definition.propConstraints)
                        {
                            if (value != null) propConstraints.Add(value);
                        }
                    }
                    AddClip(clips, definition.baseAnimation == null ? null : definition.baseAnimation.clip);
                    AddClip(clips, definition.faceAnimation == null ? null : definition.faceAnimation.clip);
                }
                UnityEngine.Object[] objects = bundle.LoadAssetWithSubAssets<UnityEngine.Object>(assetName);
                Debug.Log("[PhotoMode] Objects for " + assetName + ": " + string.Join(", ", objects.Select(value => value == null ? "null" : value.GetType().Name + ":" + value.name)));
                foreach (UnityEngine.Object value in objects)
                {
                    AnimationClip objectClip = value as AnimationClip;
                    if (objectClip != null && !clips.Contains(objectClip)) clips.Add(objectClip);
                }
                foreach (AnimationClip clip in bundle.LoadAssetWithSubAssets<AnimationClip>(assetName))
                {
                    if (clip != null && !clips.Contains(clip)) clips.Add(clip);
                }
            }
            Debug.Log("[PhotoMode] Asset names for " + bundleName + ": " + string.Join(", ", bundle.GetAllAssetNames()));
            MotionRuntimeMetadata result = new MotionRuntimeMetadata
            {
                clips = clips.ToArray(),
                definitionCount = definitionCount,
                enableSeatedDynamicCorrection = enableSeatedDynamicCorrection,
                propConstraints = propConstraints.ToArray(),
            };
            _motionMetadata[bundleName] = result;
            Debug.Log(string.Format(
                "[ActorSwing] Motion definition {0}: definitions={1} seatedCorrection={2} propConstraints={3}",
                bundleName, definitionCount, enableSeatedDynamicCorrection,
                result.propConstraints.Length));
            return result;
        }

        private static void AddClip(List<AnimationClip> clips, AnimationClip clip)
        {
            if (clip != null && !clips.Contains(clip)) clips.Add(clip);
        }

        public GameObject LoadPrefab(string bundleName)
        {
            return LoadPrefab(bundleName, null);
        }

        public GameObject LoadPrefab(string bundleName, string objectName)
        {
            AssetBundle bundle = GetBundle(bundleName);
            GameObject[] candidates = bundle.LoadAllAssets<GameObject>();
            if (!string.IsNullOrEmpty(objectName))
            {
                GameObject named = candidates.FirstOrDefault(value =>
                    string.Equals(value.name, objectName, StringComparison.Ordinal));
                if (named != null) return named;
                throw new InvalidDataException(string.Format(
                    "GameObject '{0}' not found in bundle: {1}", objectName, bundleName));
            }
            GameObject exact = candidates.FirstOrDefault(value => value.name == bundleName);
            if (exact != null)
            {
                return exact;
            }
            GameObject root = candidates.FirstOrDefault(value => value.transform.parent == null);
            if (root != null)
            {
                return root;
            }
            if (candidates.Length == 0)
            {
                throw new InvalidDataException("No GameObject prefab in bundle: " + bundleName);
            }
            return candidates[0];
        }

        public string ResolveVoicePath(VoiceRecord record)
        {
            return Path.Combine(StagingRoot, record.output_relative_path.Replace('/', Path.DirectorySeparatorChar));
        }

        private void LoadBundle(BundleRecord record)
        {
            string relative = record.output_relative_path.Replace('/', Path.DirectorySeparatorChar);
            string path = Path.Combine(StagingRoot, relative);
            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                throw new InvalidDataException("Unity failed to load AssetBundle: " + path);
            }
            _loaded.Add(record.name, bundle);
            Debug.Log(string.Format("[PhotoMode] Loaded {0} ({1})", record.name, record.role));
        }

        public void Dispose()
        {
            foreach (AssetBundle bundle in _loaded.Values.Reverse())
            {
                if (bundle != null)
                {
                    bundle.Unload(false);
                }
            }
            _loaded.Clear();
            _motionMetadata.Clear();
        }
    }

    public sealed class MotionRuntimeMetadata
    {
        public AnimationClip[] clips;
        public int definitionCount;
        public bool enableSeatedDynamicCorrection;
        public PropConstraintData[] propConstraints;
    }
}
