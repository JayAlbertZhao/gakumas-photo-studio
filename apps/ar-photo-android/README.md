# AR Photo for Android

This is an independent Android test application for AR photography. It ships
with a small, original, code-generated stand-in character and room, **not**
game assets. You can import a self-contained GLB that you own or have permission
to use. The native application and the [Unity toolkit AR sample](../../packages/com.digital-kotone.toolkit/Samples~/ARPhoto/README.md)
are separate implementations today; the native application does not embed the
Unity renderer or consume its C# API.

## Build

- Open this directory as a project in Android Studio. Use its JDK 17 runtime and
  an installed Android SDK Platform 36. Gradle 9.2.1 is the version used for
  verification; this source checkout currently has no Gradle wrapper, so select
  a local Gradle 9.2.1 installation in Android Studio's Gradle settings.
- Sync the project, then run the `app` configuration, or run
  `gradle :app:assembleDebug` from this directory with the SDK available.
- The debug APK is written to `app/build/outputs/apk/debug/app-debug.apk`.
  The APK, downloaded dependencies, SDK path, and imported models are not
  published in this repository.
- CI builds the same debug APK and runs Android lint on Linux; it does not
  upload an APK or include imported assets. Emulator and device checks remain
  separate from this compile-time gate.

Android 10 (API 29) is the minimum. The manifest marks ARCore as optional so
the synthetic preview also runs on a device without ARCore. For real AR, use an
[ARCore-supported device](https://developers.google.com/ar/devices) with
Google Play Services for AR installed and grant camera permission. A dedicated
depth camera is not required for the current plane-placement flow.
The app requests camera permission only when you enter real AR or dataset
playback. Granting it starts the selected mode automatically; denying it keeps
the synthetic preview available. A previously imported dataset remains private
to the app until its data is cleared or replaced.

## Offline test without a phone

Start an Android Studio emulator with API 36 and hardware graphics. The
synthetic room and stand-in character require no camera or original assets.
Use **合成预览** to test placement, stand-in animation, and saving a PNG.
For ARCore dataset playback on this host, the API 36.1 Google Play image only
worked when launched with `-camera-back emulated`: its default `virtualscene`
camera exposed IDs `1` and `10`, while ARCore expected camera `0`. Google Play
Services for AR must also be installed in the emulator. This camera override
is a development workaround, not a substitute for real-device tracking.

To test the import path, generate an original, tiny GLB with Python's standard
library outside the checkout, then drag it into the emulator's Downloads folder
or use `adb push`:

```powershell
python -I tools/generate_sample_glb.py "$env:TEMP\ar-photo-sample.glb"
adb push "$env:TEMP\ar-photo-sample.glb" /sdcard/Download/ar-photo-sample.glb
```

Tap **导入 GLB** and select the file. The app copies and validates a binary
glTF 2.0 GLB (at most 80 MB) into private app storage; it rejects external
buffer/image references and retains the previous model when import fails. Only
one imported model is retained. A model may contain glTF animations, which
appear in the **上一段 / 下一段** controls. The procedural character is a fallback
when no model is loaded.

Tap the preview to move the subject; use the size and rotation sliders, then
tap **拍照**. A PNG without the control cards is saved to `Pictures/AR Photo/`.
The emulator rendered both our generated GLB and Khronos' public Box.glb.
Preview placement, size, rotation, and reset visibly changed the Box; the app
reloads its model instance after imported-model gestures because SceneView
4.25 did not visibly apply later transform updates to GLB renderables in this
emulator. Rapid changes are coalesced before reloading. The app explicitly
clears the Filament render target each frame so old pixels do not linger when a
model shrinks or moves; otherwise SceneView 4.25's no-skybox path can look like
multiple models stacked on top of each other (see the
[upstream diagnosis](https://github.com/sceneview/sceneview/pull/2424)).

## Real AR and dataset recording/playback

Switch to **真实 AR**, scan a well-lit, textured horizontal surface, and tap
the detected plane to place the subject. Tap again to move it. This path uses
ARCore's camera tracking and plane hit testing; it cannot be validated by a
static camera image alone. Some emulator/ARCore combinations fail to open the
emulated camera; test real tracking on a supported phone.
Use **收起控件** to expose the full camera view when the bottom controls cover
the floor; **显示控件** restores them. A tap without a plane hit reports that no
horizontal surface was found rather than silently failing.

The **拍照** button saves the composited view to `Pictures/AR Photo/`. The
separate **录制 AR 会话** button starts an ARCore Recording & Playback dataset;
after scanning the desired scene, tap **停止录制会话**. It saves an MP4 under
`Movies/AR Photo/`. That file contains camera/sensor data for later ARCore
replay; it is **not** a final video with the virtual subject composited in.
Recording may capture sensitive surroundings and motion metadata. The app
stores the file locally and does not upload it. To replay one, tap **数据回放**
and select an ARCore-recorded MP4 (or select **导入会话 MP4** while in AR mode).
The app keeps one copy, limited to 256 MB, in private storage; a normal camera
video is not a valid ARCore dataset. The app will ask ARCore to validate it
when playback begins. This lets you reuse a session from a different phone.
The dataset is finite. After it ends, tap **重新播放会话** to restart the ARCore
session and retry plane placement or capture; you can also restart early with
**从头播放会话**. A plain MP4 video cannot be used here.

An externally published recording in SceneView's Android demo is suitable
for a local test: [Pixel 9 ARCore session](https://github.com/sceneview/sceneview/blob/main/samples/android-demo/src/debug/assets/ar-recordings/bundled-pixel9-sample.mp4).
Download it only into your own test directory, not into this public source
checkout. It includes a real-world camera recording and is not an app asset.
Its SHA-256 in our test was
`DB7371F42A515451B7FF7D0425B31059BA43A5AB800AB532FFFBD25D830932E7`.

The debug APK has been built. On the API 36.1 emulator, the synthetic
preview/import/photo flow worked; the public ARCore recording also replayed,
detected a floor, accepted a tap-to-place anchor, resized the stand-in, and
saved a composited PNG. With Khronos' Box GLB imported, playback accepted a
tracking floor hit and rendered the scaled Box at the captured world position.
Imported models use that hit pose directly because attaching a loaded GLB under
the library's `AnchorNode` did not visibly place its renderables in this
emulator; the procedural character still uses `AnchorNode`. The emulator also
reported the dataset's finished status and started the MP4 player again when
**重新播放会话** was tapped. Switching out of playback requires pausing its ARCore
session before SceneView destroys it; this was tested in the emulator. Live
camera tracking, long-term anchor stability, and session recording are **not
yet phone-verified**. The emulator's flat `emulated` camera caused ARCore's
native feature tracker to abort, so use a supported phone for that acceptance
test. Do not use this as a production AR capture tool until those checks pass.
