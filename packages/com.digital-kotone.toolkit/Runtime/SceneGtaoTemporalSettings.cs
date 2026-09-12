using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    [Serializable]
    public sealed class SceneGtaoTemporalSettings
    {
        public bool enabled;
        public bool rotateSamples = true;
        [Range(0, .97f)] public float historyWeight = .85f;
        [Range(2, 64)] public int maximumHistory = 16;
        [Range(1, 60)] public int maximumFrameGap = 1;
        [Min(.000001f)] public float depthTolerance = .02f;
        [Range(0, .9999f)] public float normalThreshold = .9f;
        [Range(0, 4)] public float varianceGamma = 1.5f;
        [Range(0, 1)] public float clampPadding = .02f;
        [Range(.000001f, 1)] public float reactiveThreshold = .2f;
        [Range(0, 1)] public float maximumHistoryDeviation = .1f;

        internal bool IsValid => Range(historyWeight,0,.97f) && maximumHistory>=2 && maximumHistory<=64 &&
            maximumFrameGap>=1 && maximumFrameGap<=60 && Range(depthTolerance,.000001f,10000) &&
            Range(normalThreshold,0,.9999f) && Range(varianceGamma,0,4) && Range(clampPadding,0,1) &&
            Range(reactiveThreshold,.000001f,1) && Range(maximumHistoryDeviation,0,1);
        private static bool Range(float v,float lo,float hi)=>!float.IsNaN(v)&&!float.IsInfinity(v)&&v>=lo&&v<=hi;
    }
}
