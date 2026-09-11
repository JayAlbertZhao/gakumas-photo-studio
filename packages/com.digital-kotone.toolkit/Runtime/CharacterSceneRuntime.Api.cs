using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GakumasPhotoMode
{
    public partial class CharacterSceneRuntime
    {
        public bool IsInitialized { get { return _initialized; } }
        public bool IsPlaybackPaused { get { return StoryActive ? _storyPlayer.IsPaused : _paused; } }
        public GameObject CharacterRoot { get { return _characterRoot; } }
        public GameObject DefaultEnvironmentRoot { get { return _photoStudioRoot; } }
        public ActorRenderControls RenderControls { get { return _actorRenderControls; } }
        public StoryTimelinePlayer Timeline { get { return _storyPlayer; } }
        public IReadOnlyList<BundleRecord> Costumes { get { return _costumes; } }
        public IReadOnlyList<BundleRecord> Motions { get { return _motions; } }
        public IReadOnlyList<string> CharacterIds { get { return _characterIds; } }

        /// <summary>
        /// Create the existing character/scene runtime without the photography application.
        /// The host owns window settings, frame rate, project quality and application input.
        /// Supply trusted compatible data; this is not a model-format conversion API.
        /// </summary>
        public void Initialize(CharacterSceneOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (_initialized) throw new InvalidOperationException("Character scene already initialized");
            if (string.IsNullOrWhiteSpace(options.DataRoot) || !Directory.Exists(options.DataRoot))
                throw new ArgumentException("An existing compatible data directory is required", nameof(options));
            var arguments = new List<string>();
            if (!options.StartStory) arguments.Add("--photo-mode");
            AddSelection(arguments, "--character-id", options.CharacterId);
            AddSelection(arguments, "--costume-label", options.CostumeLabel);
            AddSelection(arguments, "--outfit-owner", options.OutfitOwner);
            AddSelection(arguments, "--hair-label", options.HairLabel);
            AddSelection(arguments, "--motion-label", options.MotionLabel);
            AddSelection(arguments, "--photo-expression-motion", options.FaceMotionName);
            // Reuse the existing selection path. This is an internal adapter,
            // not a public requirement to synthesize process command lines.
            RuntimeArguments = arguments.ToArray();
            ResetCapturedActorRenderGlobals();
            Shader.SetGlobalFloat("_UseCapturedAmbientSH", 0f);
            ConfigureRendererFromArguments(RuntimeArguments);
            InitializeRuntime(Path.GetFullPath(options.DataRoot));
            if (_orbit != null) _orbit.enabled = options.EnableOrbitInput;
        }

        public bool SelectCharacter(string characterId)
        {
            if (_characterIds == null) return false;
            int index = _characterIds.FindIndex(value => string.Equals(value, characterId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            SelectCharacter(index);
            return true;
        }

        public bool SelectMotion(string motionLabel)
        {
            return _motions != null && SelectMotionByLabel(motionLabel);
        }

        public void SetPlaybackPaused(bool paused)
        {
            if (IsPlaybackPaused != paused) TogglePause();
        }

        private static void AddSelection(List<string> arguments, string option, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            arguments.Add(option);
            arguments.Add(value);
        }
    }
}
