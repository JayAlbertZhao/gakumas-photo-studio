using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public enum FxResolution { Full = 1, Half = 2, Quarter = 4 }
    public enum FxBlend { Alpha, Additive, Distortion }

    /// <summary>A submitted surface, in explicit back-to-front compositing order. Inputs remain caller-owned.</summary>
    [Serializable]
    public sealed class LowResolutionFxSurface
    {
        public bool enabled = true;
        // Exactly one geometry source. Renderer supports current Unity deformation;
        // mesh/matrix also permits an independently generated particle mesh.
        public Renderer renderer;
        public Mesh mesh;
        public Matrix4x4 localToWorld = Matrix4x4.identity;
        public int submesh;
        public CullMode cull = CullMode.Off;
        public FxResolution resolution = FxResolution.Half;
        public FxBlend blend;
        public Vector3 linearRadiance = Vector3.one;
        [Range(0, 1)] public float opacity = .5f;
        public Texture2D texture;
        public Vector4 textureST = new Vector4(1, 1, 0, 0);
        public bool vertexColor;
        // Smoothly fades a UV-space disc. Zero uses the supplied mesh/texture only.
        [Range(0, 1)] public float radialSoftness;
        [Min(0)] public float softIntersectionDistance;
        // Both components use viewport HEIGHT, independent of working resolution.
        public Vector2 distortionOffset;
        public Vector2 distortionTextureScale;
        public bool fog = true;
    }

    [Serializable]
    public sealed class LowResolutionFxSettings
    {
        public const int MaximumSurfaces = 256;
        public bool enabled;
        public LowResolutionFxSurface[] surfaces = Array.Empty<LowResolutionFxSurface>();
        [Min(0)] public float depthBias = .001f;
        [Min(0)] public float depthAbsoluteTolerance = .001f;
        [Min(0)] public float depthRelativeTolerance = .0001f;
        [Min(0)] public float effectEdgeThreshold = .125f;
        [Range(1, 2048)] public int maximumTargetMiB = 512;
        // Per-surface fog, after the host has fogged its opaque background.
        public FogVolumeSettings fog = new FogVolumeSettings();
    }
}
