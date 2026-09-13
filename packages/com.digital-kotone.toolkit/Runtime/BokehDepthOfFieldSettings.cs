using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum BokehFocusMode { Physical = 0, FocusRange = 1 }
    public enum BokehSampleCount { Samples30 = 30, Samples43 = 43 }

    /// <summary>Independent lens/art-directed focus controls. Distances are world metres.</summary>
    [Serializable]
    public sealed class BokehDepthOfFieldSettings
    {
        public bool enabled;
        public BokehFocusMode focusMode = BokehFocusMode.FocusRange;
        public BokehSampleCount sampleCount = BokehSampleCount.Samples30;
        [Min(.001f)] public float focusDistance = 4;
        [Min(1)] public float focalLengthMillimetres = 50;
        [Min(.05f)] public float fNumber = 2;
        [Min(.1f)] public float sensorHeightMillimetres = 24;
        [Min(.001f)] public float focusNear = 2, focusFar = 5;
        [Min(.0001f)] public float nearTransition = 2, farTransition = 8;
        [Range(0,1)] public float nearBlur = 1, farBlur = 1;
        // Radius as a fraction of image height, independent of output resolution.
        [Range(.000001f,.5f)] public float maximumRadius = .02f;
        [Range(3,9)] public int bladeCount = 5;
        [Range(0,1)] public float bladeCurvature = 1;
        public float bladeRotation;

        public bool IsValid => (focusMode==BokehFocusMode.Physical||focusMode==BokehFocusMode.FocusRange)&&
            (sampleCount==BokehSampleCount.Samples30||sampleCount==BokehSampleCount.Samples43)&&
            Range(focusDistance,.001f,100000)&&Range(focalLengthMillimetres,1,1000)&&
            focusDistance>focalLengthMillimetres*.001f&&Range(fNumber,.05f,128)&&Range(sensorHeightMillimetres,.1f,1000)&&
            Range(focusNear,.001f,100000)&&Range(focusFar,focusNear,100000)&&
            Range(nearTransition,.0001f,100000)&&Range(farTransition,.0001f,100000)&&
            Range(nearBlur,0,1)&&Range(farBlur,0,1)&&Range(maximumRadius,.000001f,.5f)&&
            bladeCount>=3&&bladeCount<=9&&Range(bladeCurvature,0,1)&&Range(bladeRotation,-360000,360000);

        /// <summary>Signed CoC radius in image-height UV units; zero/invalid depth stays sharp.</summary>
        public float EvaluateRadius(float linearDepth)
        {
            if(!IsValid||!Range(linearDepth,float.Epsilon,float.MaxValue))return 0;
            float radius;
            if(focusMode==BokehFocusMode.FocusRange)
            {
                float near=Mathf.Clamp01((focusNear-linearDepth)/nearTransition);
                float far=Mathf.Clamp01((linearDepth-focusFar)/farTransition);
                radius=(far*far*(3-2*far)-near*near*(3-2*near))*maximumRadius;
            }
            else
            {
                // Thin-lens blur diameter on the sensor, converted to image-height radius.
                double focal=focalLengthMillimetres*.001;
                double diameter=focal*focal/((double)fNumber*(focusDistance-focal))*(1-focusDistance/(double)linearDepth);
                radius=(float)(diameter/(2*sensorHeightMillimetres*.001));
            }
            return Mathf.Clamp(radius,-maximumRadius,maximumRadius)*(radius<0?nearBlur:farBlur);
        }
        private static bool Range(float value,float lo,float hi)=>!float.IsNaN(value)&&!float.IsInfinity(value)&&value>=lo&&value<=hi;
    }
}
