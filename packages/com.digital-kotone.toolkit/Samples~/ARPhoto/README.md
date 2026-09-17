# AR Photo prototype (Android-first)

This is an isolated AR Foundation **application sample**, not a change to the
desktop renderer. It places an animated prefab on a tracked plane, lets the user
pinch to scale / twist to rotate, and saves a composite screenshot to the app's
private `Application.persistentDataPath`. It ships no original game assets.

## Host project setup

1. In a compatible Tuanjie host, import this sample through Package Manager.
   The toolkit package currently pins Tuanjie URP `14.2.0-t1`, so a standard
   Unity project should instead copy `ARPhotoController.cs` alone into `Assets`;
   this script does not reference the toolkit assembly. Install AR Foundation
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

`Capture` writes a PNG to app-private storage. It does **not** add the photo to
the system gallery or request gallery permissions. Export/share and real-world
depth occlusion are separate follow-up work.

## Toolkit integration boundary

The subject-prefab boundary accepts independently authored models and animation
without changing the photo-studio render pipeline. It does not yet instantiate
`CharacterSceneRuntime`: that runtime creates its own desktop camera and
captured 4096 shadow map, which must be adapted and tested against the live AR
camera before claiming parity. Do not import original game assets into a public
project. The capsule path only verifies placement, gestures and camera capture;
it is not a character-rendering acceptance test.

The repository currently has no Android build module or connected test phone;
this sample therefore needs an actual on-device build and visual check before
its rendering or performance can be called verified.
