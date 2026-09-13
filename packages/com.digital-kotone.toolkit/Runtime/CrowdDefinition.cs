using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public enum CrowdBackend { Auto, Gpu, Cpu }
    public enum CrowdMeshQuality { Low, High }
    public enum CrowdLighting { Pbr, Toon }

    /// <summary>Authored inputs only. Runtime poses and owned GPU state do not live in this asset.</summary>
    [CreateAssetMenu(menuName = "Digital Kotone/Crowd Definition")]
    public sealed class CrowdDefinition : ScriptableObject
    {
        public const int MaximumPrototypes = 8, MaximumInstances = 65536;
        public CrowdPrototype[] prototypes = Array.Empty<CrowdPrototype>();
        public CrowdInstance[] instances = Array.Empty<CrowdInstance>();
        // Advance after editing mesh vertex/index/shape data in place. Replacement objects
        // and per-frame material/instance/pose values are detected without this token.
        public ulong contentVersion;
    }

    [Serializable]
    public sealed class CrowdPrototype
    {
        public Mesh lowMesh, highMesh;
        public int submesh;
        public CullMode cull = CullMode.Back;
        public SceneDeferredCamera.MaterialInputs material = new SceneDeferredCamera.MaterialInputs();
        public SceneGiInput gi = new SceneGiInput();
        public CrowdLightstickSurface lightstick = new CrowdLightstickSurface();
        public CrowdLighting lighting;
        [Range(0, 1)] public float alphaCutoff = .5f;
        [Range(0, 255)] public int receiverGroup = 1;
        // Independent two-band diffuse model; not recovered original character ramps.
        [Range(0, 1)] public float toonThreshold = .5f, toonShadow = .3f;
        [Range(.001f, .5f)] public float toonSoftness = .05f;
    }

    [Serializable]
    public sealed class CrowdInstance
    {
        public Vector3 position;
        public float yawDegrees;
        public float scale = 1;
        public int prototype;
        public Vector3 tint = Vector3.one;
        public Vector3 lightstickTint = Vector3.one;
        public double lightstickPhaseCycles;
        public bool hidden;
    }

    /// <summary>Explicit current rig pose in the prototype's authored coordinate space.</summary>
    [Serializable]
    public sealed class CrowdPose
    {
        public Transform root;
        public Transform[] bones = Array.Empty<Transform>();
        public float[] blendShapeWeights = Array.Empty<float>();
    }

    [Serializable]
    public sealed class CrowdSettings
    {
        public bool enabled;
        public CrowdBackend backend = CrowdBackend.Auto;
        public bool allowCpuFallback = true;
        public CrowdMeshQuality meshQuality;
        [Range(0, CrowdDefinition.MaximumInstances)] public int meshBudget = 256;
        [Range(16, 512)] public int captureResolution = 128;
        [Range(1, 2048)] public int maximumResourceMiB = 256;
        // Explicit absolute time; no automatic Time.time or frame accumulation.
        public double lightstickTimeSeconds;
        public SceneForwardLightSettings lighting = new SceneForwardLightSettings { enabled = true };
    }
}
