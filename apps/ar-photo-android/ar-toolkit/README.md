# Android AR toolkit

This Android library contains the reusable, asset-free operations used by the
AR Photo app. It is separate from the Unity/C# toolkit and does not reproduce
its character renderer. The host owns its UI, ARCore permission/install flow,
SceneView lifecycle, and model source.

Include `:ar-toolkit` in `settings.gradle.kts` and add
`implementation(project(":ar-toolkit"))` to an Android app. The library uses
Android API 29+, ARCore and SceneView 4.25.0. Its public Kotlin API is in
`org.digital_kotone.arphoto`:

- `SubjectImporter.importGlb(context, uri)` copies a self-contained GLB (up to
  80 MB) to app-private storage; `savedModel(context)` restores the last import.
- `DatasetImporter.importMp4(context, uri)` copies one ARCore dataset (up to
  256 MB) to app-private storage; `savedDataset(context)` restores it. ARCore
  performs the definitive dataset check when the host starts playback.
- `ArSessionRecorder(context).start(session)` / `.stop(session)` records an
  ARCore session into `Movies/AR Photo/`. This is camera and sensor data, not
  video with a virtual subject composited in.
- `PhotoStore.captureAndSave(activity)` saves the composited Android window as
  a PNG in `Pictures/AR Photo/`.
- `applyAnchorPose(node, alignment, pose, yawDegrees)` places a SceneView
  `ModelNode` at a full ARCore anchor pose, with a local feet-alignment offset
  and user yaw.

Use only models you own or have permission to use. No game assets, model
downloads, network upload, camera permission request, or ARCore installation
flow are part of this library. See the [app guide](../README.md) for the host
workflow and its remaining device-verification limits.
