# Gakumas AR Photo — Unity host

This asset-free Unity 2022.3 Android application reuses the toolkit's
`CharacterSceneRuntime`, actor shaders, animation, facial expression and captured
shadow pipeline against an AR Foundation camera. It is separate from the native
Android diagnostic app. No compatible character bundles are included.

## Build

Install Unity 2022.3.62f3 with Android Build Support and a Java 11/Android SDK toolchain. From
the repository root:

```powershell
python -I tools/prepare_unity_ar_package.py
& '<Unity>\Editor\Unity.exe' -batchmode -quit `
  -projectPath apps/ar-photo-unity `
  -executeMethod DigitalKotone.ARPhoto.Editor.ARPhotoBuild.BuildAndroid `
  -logFile Builds/ar-photo-unity-build.log
```

The preparation step makes an ignored local package mirror and removes only
Tuanjie's encrypted `.meta` files. Standard Unity then generates its own local
metadata; the reviewed source and the Tuanjie project stay unchanged.

The default output is `apps/ar-photo-unity/Builds/gakumas-ar-photo.apk`.

## Private data staging

Install and start the development APK once so Android creates its private files
directory. Stage a compatible toolkit data set at the path printed by the app,
under `Application.persistentDataPath/character-data`, then press **Reload data**.
For a debuggable build this can be scripted with `adb shell run-as`; production
distribution needs its own licensed-data import flow. Never commit those assets.

For the currently reconstructed FKTN profile, create the minimal private set
from an existing lawful staging directory:

```powershell
python -I tools/prepare_ar_character_data.py `
  '.\private-staging' '.\ar-photo-character-data'
adb push '.\ar-photo-character-data' `
  '/sdcard/Android/data/org.digital_kotone.arphoto.unity/files/'
```

The preparation tool follows AssetBundle dependencies and currently emits seven
bundles (about 21 MiB), rather than copying the full desktop research corpus.
Original game data remains private and is never part of this repository.

Tap a detected plane to place the character. Pinch to scale, twist to rotate,
and use **Capture** to write a composite PNG to app-private storage.
