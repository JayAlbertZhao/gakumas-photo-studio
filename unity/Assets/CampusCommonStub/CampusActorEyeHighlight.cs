using System;
using UnityEngine;
using VL.FaceSystem;

namespace Campus.Common
{
    // Serialization-compatible shell for the original eye-highlight correction.
    public sealed class CampusActorEyeHighlight : MonoBehaviour
    {
        public VLActorFaceModel target;
        public EyeHighlightBlendShape[] blendShapes;

        public float GetHighlightOffset()
        {
            if (target == null || blendShapes == null) return 0f;
            float result = 0f;
            foreach (EyeHighlightBlendShape value in blendShapes)
            {
                if (value == null || value.index < 0) continue;
                result += target.GetWeight(value.index) * value.value;
            }
            return result;
        }
    }

    [Serializable]
    public sealed class EyeHighlightBlendShape
    {
        public int index;
        public float value;
    }
}
