using System;
using System.IO;
using UnityEngine;

namespace GakumasPhotoMode.Samples.ARPhoto
{
    /// <summary>
    /// Connects the reconstructed character renderer to the AR camera and placement
    /// controller. Compatible data stays outside the package and application build.
    /// </summary>
    public sealed class ARCharacterSceneHost : MonoBehaviour
    {
        [SerializeField] private Camera arCamera;
        [SerializeField] private ARPhotoController placement;
        [SerializeField] private string dataDirectoryName = "character-data";
        [SerializeField] private string characterId = "fktn";
        [SerializeField] private string costumeLabel = "cstm-0000";
        [SerializeField] private string motionLabel = "photo-idle-001";

        public CharacterSceneRuntime Runtime { get; private set; }
        public string DataRoot { get; private set; }
        public string Error { get; private set; }

        private void Start()
        {
            if (arCamera == null || placement == null)
            {
                Error = "AR camera and placement controller are required";
                Debug.LogError("[AR Photo] " + Error);
                return;
            }

            DataRoot = Path.Combine(Application.persistentDataPath, dataDirectoryName);
            if (!Directory.Exists(DataRoot))
            {
                Error = "Compatible character data is missing: " + DataRoot;
                Debug.LogError("[AR Photo] " + Error);
                return;
            }

            try
            {
                Runtime = gameObject.AddComponent<CharacterSceneRuntime>();
                Runtime.Initialize(new CharacterSceneOptions
                {
                    DataRoot = DataRoot,
                    CharacterId = characterId,
                    CostumeLabel = costumeLabel,
                    MotionLabel = motionLabel,
                    StartStory = false,
                    EnableOrbitInput = false,
                    HostCamera = arCamera,
                    DisableDefaultEnvironment = true,
                });
                placement.SetExternalSubject(Runtime.CharacterRoot);
                Debug.Log("[AR Photo] Character renderer ready: " + Runtime.CurrentCharacter);
            }
            catch (Exception exception)
            {
                Error = exception.Message;
                Debug.LogException(exception);
            }
        }
    }
}
