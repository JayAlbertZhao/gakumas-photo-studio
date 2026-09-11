namespace GakumasPhotoMode
{
    /// <summary>Explicit host choices; the toolkit does not parse application shortcuts.</summary>
    public sealed class CharacterSceneOptions
    {
        public string DataRoot { get; set; }
        public string CharacterId { get; set; }
        public string CostumeLabel { get; set; }
        public string OutfitOwner { get; set; }
        public string HairLabel { get; set; }
        public string MotionLabel { get; set; }
        public string FaceMotionName { get; set; }
        public bool StartStory { get; set; }
        public bool EnableOrbitInput { get; set; }
    }
}
