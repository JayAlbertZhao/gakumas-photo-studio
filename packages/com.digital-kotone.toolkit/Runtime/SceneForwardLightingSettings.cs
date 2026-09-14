using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public enum SceneForwardLightBackend { Auto, Tiled, BruteForce }

    /// <summary>Explicit full-resolution, back-to-front surface. Host owns inputs and ordering.</summary>
    [Serializable]
    public sealed class SceneForwardSurface
    {
        public bool enabled = true;
        public Renderer renderer;
        public int submesh;
        public CullMode cull = CullMode.Back;
        public Vector3 vertexScale = Vector3.one;
        public SceneDeferredCamera.MaterialInputs inputs = new SceneDeferredCamera.MaterialInputs();
        [Range(0, 1)] public float alphaCutoff;
        [Range(0, 255)] public int receiverGroup = 1;
        public SceneGiInput gi = new SceneGiInput();
        public SceneBakedShadowInput bakedShadow = new SceneBakedShadowInput();
        public VegetationLeafMaterial leaf;
        // Alpha uses premultiplied over; additive does not change destination alpha.
        public bool additive;
    }

    [Serializable]
    public class SceneForwardLightSettings
    {
        public bool enabled;
        public SceneForwardLightBackend backend = SceneForwardLightBackend.Auto;
        public bool allowBruteForceFallback = true;
        // 8, 16 or 32. Bitsets reserve every submitted light; no per-tile overflow truncation.
        public int tileSize = 16;
        [Range(1, 128)] public int maximumGridMiB = 32;
        public SceneDecalLightSettings localLights = new SceneDecalLightSettings();
        public Vector3 lightDirection = new Vector3(0, 0, -1);
        public Vector3 lightRadiance = Vector3.one;
        public Vector3 ambientIrradiance = Vector3.one * .1f;
        [Range(0, 4)] public float diffuseScale = 1, specularScale = 1, backlightScale, giBaseScale = 1;
        [Range(0, 1)] public float directionalGiWeight;
        public SceneDirectionalShadowSettings mainLightShadow = new SceneDirectionalShadowSettings();
        public SceneBakedShadowChannel mainBakedShadowChannel;
    }

    [Serializable]
    public sealed class SceneForwardLightingSettings : SceneForwardLightSettings
    {
        public const int MaximumSurfaces = 256;
        public SceneForwardSurface[] surfaces = Array.Empty<SceneForwardSurface>();
    }
}
