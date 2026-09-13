# Authored crowd lightsticks

This optional surface adds independently authored masked HDR emission to the current [crowd](crowd.md) mesh and its four generated impostor views. It is disabled by default and contains no audience art or recovered shader. The reference talk lists a lightstick shader but does not disclose its algorithm.

Author the held lightstick geometry inside each prototype's single triangle submesh, with ordinary UVs and any desired bone weights. A required **linear red-channel mask** selects emitting regions; it uses the same `material.uvST` as the other prototype textures. The module does not discover hand bones, attach separate objects or supply animation assets. Static and current shared bone/shape poses use the existing crowd path.

```csharp
prototype.lightstick.enabled = true;
prototype.lightstick.mask = yourLinearMask;
prototype.lightstick.radiance = new Vector3(4, 2, 1);
prototype.lightstick.frequencyHz = 2;
prototype.lightstick.minimum = 0.25f;
definition.instances[0].lightstickTint = new Vector3(1, 0.5f, 0.25f);
definition.instances[0].lightstickPhaseCycles = 0.125;
crowdSettings.lightstickTimeSeconds = yourTimelineSeconds;
```

The explicit clock supports pause, seeking and replay. No `Time.time`, random seeds or incremental phase accumulation are used. With `q = frac(time * frequency + phase)`, the independently chosen pulse is `minimum + (1 - minimum) * sin(pi * q)^2`. Zero frequency freezes the authored phase; `minimum = 1` gives steady emission. Radiance and per-instance tint are linear RGB values, separate from the existing body output-radiance multiplier. After ordinary shading and body tint, the shader adds `saturate(mask.r) * currentLightstickRadiance`. Per-instance radiance and the final output are each clamped to 65504.

The current mask travels in the existing emission atlas's alpha channel; RGB ordinary emission remains unchanged. The final draw reads the actual original instance ID, so colors and phases are not frozen into shared billboard captures. Existing four-view angular approximation and point-filtered atlas limits still apply.

## Ownership and limits

The mask is borrowed. It must be a non-sRGB 2D texture; a render texture must be created, fixed-size and single-sampled. The current output, owned material atlases and owned lighting targets cannot feed back as masks. In-place mask updates are consumed on the next prepare without advancing the geometry `contentVersion`. The caller must keep all borrowed inputs alive and unchanged between prepare and draw.

Enabled lightsticks allocate one additional 16-byte element per power-of-two placement capacity. That stream is included in `maximumResourceMiB` before allocation and in `Frame.crowdResourceBytes`; it is released when disabled, invalid, empty or disposed. The original 48-byte placement layout and four-atlas allocation stay unchanged. Disabled prototypes ignore their dormant lightstick inputs and allocate no lightstick stream when all prototypes are disabled.

Accepted input domains are RGB 0–65504, frequency 0–100 Hz, minimum 0–1, finite clock ±1e9 seconds and finite phase ±1e6 cycles. These bounds limit double-precision phase loss; they do not claim arbitrary-precision subframe timing. A missing mask is an error, not an all-white fallback.

HDR output can enter the host's existing bloom path. Lightstick emission does not register actual scene lights, alter global lighting or introduce automatic glow compositing. Full stage content, crowd shadow casting, temporal/reflection integration and mobile timing remain separate work.

## Verification scope

`ActorRenderingSelfTest.CrowdLightsticks.cs` runs actual current GPU/CPU near and far rendering, compares complete HDR images against an independent ordinary-emission mask reference, and checks clock, pose, texture update, resource and invalid-input behavior. Python tests guard the API/source contract only; their results do not establish image or mobile performance parity.
