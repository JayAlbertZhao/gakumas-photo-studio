# Asset-free Desktop Host

The implementation is compiled from `Examples/DesktopHost` in the package, not
from an excluded sample-code snippet. No character model, game data, AssetBundle,
private LUT, or other external asset is required.

1. Use the repository's supported desktop editor/project and **Linear** color space.
2. In an empty scene, create an empty GameObject.
3. Add **Character Toolkit → Examples → Desktop Host**, then press Play.

The component creates its camera, animated primitive character, receiving floor,
background, toon ramp, reflection probe, transparent effect and authored color LUT.
It temporarily selects a small explicit SRP; disabling it releases its own objects
and restores the previously selected pipeline, provided another host has not
changed that selection in the meantime. Use only one instance at a time.

This is an ownership/ordering example for desktop D3D11 and Vulkan. It intentionally
performs a one-pixel synchronous GPU readback each frame before retiring resources.
That makes lifetime management easy to inspect, at the cost of CPU/GPU overlap.
Do not use its frame time as a toolkit or mobile-performance measurement.

`DesktopHostExample.RenderOffscreen(seconds)` runs the same implementation with
explicit time, without backbuffer presentation. Its `Display` texture is owned by
the example and remains valid until shutdown. The reusable `DesktopFrameRenderer`
does not activate a pipeline, present, synchronize the GPU, or load assets.

See the repository's `docs/desktop-host.md` for core integration and limitations.
