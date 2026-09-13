using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Independent material response, before existing FX tint, opacity and medium transport.</summary>
    [Serializable]
    public sealed class FxSurfaceLighting
    {
        public bool enabled;
        public SceneDeferredCamera.MaterialInputs inputs = new SceneDeferredCamera.MaterialInputs();
        public SceneGiInput gi = new SceneGiInput();
        public SceneBakedShadowInput bakedShadow = new SceneBakedShadowInput();
        [Range(0, 255)] public int receiverGroup = 1;
        [Range(0, 1)] public float alphaCutoff;
    }
}
