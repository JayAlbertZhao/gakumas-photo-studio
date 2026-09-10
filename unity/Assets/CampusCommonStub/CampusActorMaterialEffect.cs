using System;
using UnityEngine;

namespace Campus.Common
{
    // Serialized data only. Playback is implemented independently by Photo Studio.
    public sealed class CampusActorMaterialEffect : MonoBehaviour
    {
        public ActorTextureOverride overrideProperty;
    }

    [Serializable]
    public sealed class ActorTextureOverride
    {
        public string materialName;
        public Texture2D col;
        public Texture2D sdw;
        public Texture2D def;
        public int tileX = 1;
        public int tileY = 1;
        public int tileFPS = 1;
    }
}
