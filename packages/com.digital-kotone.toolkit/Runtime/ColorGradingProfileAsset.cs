using UnityEngine;

namespace GakumasPhotoMode
{
    [CreateAssetMenu(menuName="Digital Kotone/Color Grading Profile",fileName="ColorGradingProfile")]
    public sealed class ColorGradingProfileAsset : ScriptableObject
    {
        public ColorGradingProfile profile = new ColorGradingProfile();
        /// <summary>Caller owns the returned LUT. Re-bake explicitly after editing the profile.</summary>
        public ColorGradingLut Bake() => ColorGradingLut.Bake(profile);
    }
}
