using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    [Serializable]
    public sealed class SceneTemporalAntialiasingSettings
    {
        public bool enabled;
        [Range(0, .99f)] public float historyWeight = .95f;
        [Range(2, 64)] public int maximumHistory = 32;
        [Range(1, 60)] public int maximumFrameGap = 1;
        [Min(.000001f)] public float depthTolerance = .02f;
        [Range(0, .9999f)] public float normalThreshold = .9f;
        [Range(0, 4)] public float varianceGamma = .9f;
        [Range(0, 1)] public float reactiveThreshold = .2f;
        // Texture-UV correction: stable output samples current at uv - jitterUv.
        // The host owns applied projection jitter. This module never changes a camera matrix.
        public Vector2 jitterUv;
        // Advance on a discontinuous change of the input color processing/exposure convention.
        public uint contentRevision;
        internal bool IsValid => Range(historyWeight,0,.99f)&&maximumHistory>=2&&maximumHistory<=64&&
            maximumFrameGap>=1&&maximumFrameGap<=60&&Range(depthTolerance,.000001f,10000)&&
            Range(normalThreshold,0,.9999f)&&Range(varianceGamma,0,4)&&Range(reactiveThreshold,0,1)&&
            Range(jitterUv.x,-.5f,.5f)&&Range(jitterUv.y,-.5f,.5f);
        private static bool Range(float x,float lo,float hi)=>!float.IsNaN(x)&&!float.IsInfinity(x)&&x>=lo&&x<=hi;
    }
}
