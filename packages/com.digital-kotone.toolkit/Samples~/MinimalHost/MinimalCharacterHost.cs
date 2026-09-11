using GakumasPhotoMode;
using UnityEngine;

/// <summary>Add to an empty GameObject and set a trusted compatible data directory.</summary>
public sealed class MinimalCharacterHost : MonoBehaviour
{
    public string dataRoot;
    public string characterId;
    public bool orbitCamera = true;
    private CharacterSceneRuntime runtime;

    private void Start()
    {
        runtime = gameObject.AddComponent<CharacterSceneRuntime>();
        runtime.Initialize(new CharacterSceneOptions
        {
            DataRoot = dataRoot,
            CharacterId = characterId,
            EnableOrbitInput = orbitCamera
        });
    }

    // An application can call runtime.SelectMotion(...),
    // runtime.SelectExpression(...), runtime.PlayVoice(...), or
    // runtime.SetPlaybackPaused(...) without loading PhotoModeApp.
}
