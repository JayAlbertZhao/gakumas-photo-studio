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

        /// <summary>
        /// Optional camera owned by the host application. When supplied, the toolkit
        /// keeps its transform, projection and clear settings intact. This is intended
        /// for camera providers such as AR Foundation.
        /// </summary>
        public UnityEngine.Camera HostCamera { get; set; }

        /// <summary>
        /// Do not create the studio backdrop and floor. Lighting and the character
        /// renderer are still initialized.
        /// </summary>
        public bool DisableDefaultEnvironment { get; set; }
    }
}
