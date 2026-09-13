using UnityEngine;

namespace GakumasPhotoMode
{
    [CreateAssetMenu(menuName = "Character Toolkit/Performance Clip")]
    public sealed class PerformanceClipAsset : ScriptableObject
    {
        public PerformanceClip clip = new PerformanceClip();
        public string importError;
        public bool TryCreatePlayer(PerformanceBindings bindings, out PerformancePlayer player, out string reason) =>
            PerformancePlayer.TryCreate(clip, bindings, out player, out reason);
    }
}
