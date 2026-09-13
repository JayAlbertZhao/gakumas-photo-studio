using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum FaceDecalBlend { StraightAlpha, AlphaModulated }

    /// <summary>Independent, animatable authored values. Colors are literal linear values.</summary>
    [Serializable]
    public sealed class FaceDecalPose
    {
        public Vector3 positionOffset, rotationDegrees;
        public Vector3 scale = Vector3.one, size = Vector3.one, pivot;
        public Vector2 uvScale = Vector2.one, uvBias;
        public float uvRotationDegrees;
        public Vector4 tint = Vector4.one;
        [Range(0, 1)] public float opacity = 1;
        [Range(0, .5f)] public float edgeFeather;
        [Range(0, 180)] public float angleFadeStart = 180, angleFadeEnd = 180;
        public FaceDecalBlend blend;
        public FaceDecalPose Copy() => (FaceDecalPose)MemberwiseClone();
    }

    /// <summary>Serializable public fields support normal Animator/AnimationClip bindings.
    /// A host may instead submit Data directly, without creating any component.</summary>
    public sealed class FaceDecalProjector : MonoBehaviour
    {
        [Serializable]
        public struct Data
        {
            public Matrix4x4 parentToWorld;
            public FaceDecalPose pose;
            public Data(Matrix4x4 parentToWorld, FaceDecalPose pose)
            { this.parentToWorld = parentToWorld; this.pose = pose; }
        }
        // Keep animation bindings on the component itself. Legacy clip sampling
        // does not reliably bind fields through a nested managed pose object.
        public Vector3 positionOffset, rotationDegrees;
        public Vector3 scale = Vector3.one, size = Vector3.one, pivot;
        public Vector2 uvScale = Vector2.one, uvBias;
        public float uvRotationDegrees;
        public Vector4 tint = Vector4.one;
        [Range(0, 1)] public float opacity = 1;
        [Range(0, .5f)] public float edgeFeather;
        [Range(0, 180)] public float angleFadeStart = 180, angleFadeEnd = 180;
        public FaceDecalBlend blend;
        public Data Snapshot() => new Data(transform.localToWorldMatrix, new FaceDecalPose {
            positionOffset=positionOffset,rotationDegrees=rotationDegrees,scale=scale,size=size,pivot=pivot,
            uvScale=uvScale,uvBias=uvBias,uvRotationDegrees=uvRotationDegrees,tint=tint,opacity=opacity,
            edgeFeather=edgeFeather,angleFadeStart=angleFadeStart,angleFadeEnd=angleFadeEnd,blend=blend });
        public bool TryApplyPose(FaceDecalPose value, out string reason)
        {
            reason=null;
            try { FaceDecalRenderer.Validate(new Data(transform.localToWorldMatrix,value)); }
            catch(Exception error) { reason=error.Message;return false; }
            positionOffset=value.positionOffset;rotationDegrees=value.rotationDegrees;scale=value.scale;size=value.size;pivot=value.pivot;
            uvScale=value.uvScale;uvBias=value.uvBias;uvRotationDegrees=value.uvRotationDegrees;tint=value.tint;opacity=value.opacity;
            edgeFeather=value.edgeFeather;angleFadeStart=value.angleFadeStart;angleFadeEnd=value.angleFadeEnd;blend=value.blend;return true;
        }
    }
}
