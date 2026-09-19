# AR Photo prototype (Android-first)

This AR Foundation **application sample** can either place an ordinary prefab or
reuse the toolkit's reconstructed character renderer. It lets the user pinch to
scale / twist to rotate and saves a composite screenshot to the app's private
`Application.persistentDataPath`. It ships no original game assets.

## Host project setup

1. In a compatible Tuanjie host, import this sample through Package Manager.
   Install AR Foundation
   5.x and the device provider (ARCore XR Plugin on Android, ARKit XR Plugin on
   iOS), enable the provider in XR Plug-in Management, and install the target
   build module.
2. Use the AR Foundation scene setup for your render pipeline: `AR Session`,
   `XR Origin (AR)`, `AR Camera` with `AR Camera Manager` and
   `AR Camera Background`, plus `AR Plane Manager`, `AR Raycast Manager`, and
   `AR Anchor Manager` on the XR Origin. In URP, add the AR Background Renderer
   Feature to the renderer asset. Follow the provider's camera-permission and
   platform requirements.
3. Add `ARPhotoController` to an object. Assign its three managers. Assign an
   animated **user-owned** prefab as `Subject Prefab`, or leave it empty for a
   capsule placement diagnostic. Put its feet at prefab local origin and make
   its Animator Controller available on the target platform.
4. Set Active Input Handling to `Both` or `Input Manager` for this touch-input
   sample. Build to a compatible phone. Tap a detected plane to place/reposition.
   Two fingers scale and rotate. Wire UI buttons to `Capture`, `Clear`, or
   `SetAnimationTrigger`. If you assign the UI canvas to `Capture UI`, it is
   hidden for the captured frame.
5. To use the reconstructed renderer, add `ARCharacterSceneHost`, assign the AR
   camera and `ARPhotoController`, and stage compatible data at
   `Application.persistentDataPath/character-data`. The host keeps AR camera
   projection/clear settings intact, disables the studio backdrop, and passes
   the runtime-owned character to the placement controller.

`Capture` writes a PNG to app-private storage. It does **not** add the photo to
the system gallery or request gallery permissions. Export/share and real-world
depth occlusion are separate follow-up work.

## Toolkit integration boundary

`ARCharacterSceneHost` instantiates `CharacterSceneRuntime` against the live AR
camera and keeps ownership of its character. Compatible data stays outside the
package and app build. The adapter currently targets the existing Built-in
camera image-effect path; mobile performance, XR composition and the captured
shadow path still require on-device validation before parity can be claimed.
Do not import original game assets into a public project. The capsule path only
verifies placement, gestures and camera capture; it is not a renderer test.

The repository also includes a [separate native Android AR test app](../../../../apps/ar-photo-android/README.md)
with a build module and emulator-tested dataset playback. That app does not
embed this Unity sample or the toolkit renderer. This Unity sample still needs
an actual on-device build and visual check before its rendering or performance
can be called verified.
