using System.Collections.Generic;
using UnityEngine;

namespace VL
{
    // Data-only compatibility shell for facial motion bundles.
    public sealed class MotionDefine : MonoBehaviour
    {
        public string layerName;
        public bool isAdditive;
        public bool ignoreAdditive;
        public bool applyPlayableIK;
        public bool applyFootIK;
        public List<MotionEffect> effects = new List<MotionEffect>();
        public bool isLoopMotionEffect;
        public MotionAnimation baseAnimation;
    }
}
