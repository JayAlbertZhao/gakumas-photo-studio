using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    [Serializable]
    public sealed class SceneRefractionSurface
    {
        public bool enabled=true;
        // Created explicitly from caller geometry, independently of a renderer or material database.
        [NonSerialized] public ConvexRefractionShape shape;
        public Matrix4x4 localToWorld=Matrix4x4.identity;
        public Vector3 indexOfRefraction=new Vector3(2.4f,2.4f,2.4f);
        [Range(1,4)] public float exteriorIndexOfRefraction=1;
        // Inverse world units; RGB IOR is an authored three-channel dispersion approximation.
        public Vector3 absorption;
        public Texture environment;
        public Quaternion environmentRotation=Quaternion.identity;
        public Vector3 radianceScale=Vector3.one;
        [Range(0,12)] public float environmentMip;
        [Range(1,32)] public int internalInterfaces=8;
    }

    [Serializable]
    public sealed class SceneRefractionSettings
    {
        public const int MaximumSurfaces=32;
        public bool enabled;
        public SceneRefractionSurface[] surfaces=Array.Empty<SceneRefractionSurface>();
        [Range(1,2048)] public int maximumTargetMiB=256;
    }
}
