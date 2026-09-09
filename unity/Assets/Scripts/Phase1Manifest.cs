using System;

namespace GakumasPhotoMode
{
    [Serializable]
    public sealed class Phase1Manifest
    {
        public string schema_version;
        public string character_id;
        public string unity_version;
        public string output_root;
        public BundleRecord[] bundles;
        public MissingBundleRecord[] missing_bundles;
        public VoiceRecord[] voices;
    }

    [Serializable]
    public sealed class BundleRecord
    {
        public string name;
        public string role;
        public string label;
        public bool requested;
        public string output_relative_path;
        public string[] dependencies;
    }

    [Serializable]
    public sealed class MissingBundleRecord
    {
        public string name;
        public string role;
        public bool requested;
        public string reason;
    }

    [Serializable]
    public sealed class VoiceRecord
    {
        public string label;
        public string output_relative_path;
    }
}

