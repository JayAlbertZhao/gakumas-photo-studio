# Explicit Actor shadows

The opt-in shadow producer separates three authoring controls: Actor toon light,
Actor self-shadow direction/strength, and the scene light used for the Actor's
drop shadow onto the background. It implements the separation described in the
character presentation, not an original shader or asset format.

`ActorShadowInputs.TryCapture(renderers, textureLodBias, out casters, out error)`
captures the current full ActorToon materials and effective per-submesh property
blocks into explicit `SceneShadowCaster` inputs. It includes wardrobe scale,
base/atlas UV transforms, material alpha clip and timeline fade. It skips inactive
renderers and `ShadowCastingMode.Off`; TwoSided uses no culling. No renderer or
source material is modified. Refresh these inputs after animated material changes.

Use these casters with two independently configured producers:

```csharp
// Background drop shadow: the existing scene light owns its separate atlas.
sceneSettings.positionLighting = true;
sceneSettings.mainLightShadow = backgroundShadowSettings;
backgroundShadowSettings.casters = casters;

// Actor self shadow: direction is independent of sceneSettings.lightDirection
// and ActorForwardParameters' toon light. Record before preparing Actor draws.
selfShadowSettings.casters = casters;
if (!selfShadow.TryRecord(context, selfDirection, selfShadowSettings, sequence,
                         out var shadowFrame, out var error))
    throw new InvalidOperationException(error);
actorDrawSettings.selfShadow = shadowFrame;
```

Create `selfShadow` as a caller-owned `SrpActorShadow`. Both shadow settings are
`SceneDirectionalShadowSettings`: explicit world-space origin, up, half-size,
near/far planes, map resolution, strength, depth/normal bias and Hard/PCF filtering.
Direction points toward the light. Bounds do not automatically fit the scene.
Strength zero records a neutral frame without allocating a depth map. Null
`ActorForwardDrawSet.Settings.selfShadow` preserves the old explicit-input path.

Prepare and record the scene and full Actor composition with the same positive
sequence. A supplied self-shadow ticket must remain current and match that sequence.
The full Actor material retains its per-material face-parts shadow strength.
The producer uses R32 linear axial light depth and a depth attachment; the receiver
uses integer texture loads, avoiding another sampler and a camera-distance fade.
The default receiver-plane correction evaluates each sampled texel on the current
receiver triangle's plane, including PCF offsets. `ReceiverPlaneBias = false`
retains a constant-bias comparison for diagnostics. Smoothed material normals do
not replace triangle geometry in this correction. World-unit bias is still useful
at silhouettes and curved/discontinuous surfaces. See Microsoft's discussion of
[shadow-map bias and aliasing](https://learn.microsoft.com/en-us/windows/win32/dxtecharts/common-techniques-to-improve-shadow-depth-maps).

## Ownership and limitations

- The host submits commands and waits for GPU completion before reuse/disposal.
  `Frame.IsCurrent` checks recording and structural lifetime, not GPU completion.
- Mesh/bone contents, property blocks and texture pixels remain borrowed. Preserve
  them until completion. Structural checks do not hash dynamic vertex buffers.
- A failed producer preparation retires prior tickets. Reusing the producer or
  releasing its depth map invalidates consumers. Do not sample the output as a
  caster alpha texture. Shadow-prefixed caster property blocks are rejected.
- These are opaque/cutout shadow maps with light-space dither for timeline fades.
  They do not model colored transmission or reproduce camera-dependent front-hair
  transparency in the light view. A cutout's mip/derivative footprint belongs to
  the light raster, not the camera raster.
- The optional Actor coverage path is separate from generic scene casters. Generic
  casters retain their old shader and property-block rejection.
- No automatic scene discovery, light fitting, global writes, pipeline switch,
  `Camera.Render` or `Submit` takes place inside the producer.

## Validation status

The asset-free `--self-test-actor-shadow <directory>` fixture runs on desktop
D3D11 and Vulkan. It exercises independent self/background light controls,
world-ray receiver interiors, moving geometry, map depth, opacity/atlas/property
blocks, output feedback and ticket retirement. Its sloped-plane control uses
deliberately incorrect smoothed normals: disabling receiver-plane correction
produces acne, while the corrected image matches the independent plane oracle.
Raster/PCF boundary bands are excluded from the world-ray interior comparison.

For a host's own assets, append `--validate-actor-shadows` to
`--photo-mode --validate-srp-actor-character <directory>`. Two costumes have been
exercised on both desktop APIs across front/side/rear views, motion seek/restore,
zero strength and changed shadow angle. Each run compares 13 full color fields
with ordinary Forward using the same explicit shadow map; this is not an
independent reference for the shadow algorithm. The generated ray/plane fixtures
provide that separate control. Real eye-depth comparison retains the existing
`1e-5` world-unit tolerance; not every skinned view is bit-identical.
Real-character snapshots from separate processes are not assumed deterministic:
the fixture pauses after a realtime initialization interval. Repeat runs of the
same build can differ, so their file hashes are not an old-version parity oracle.
The fixed-state generated suite supplies the exact prior-render regression check.

Selected native captures separately inspect the actual R32 shadow texture bound
to full Actor draws and compare final color, device depth, stencil and eye depth.
The shadow producer runs before those selected capture windows, so these do not
prove its complete native command schedule. Front/rear images are also checked
visually: an earlier numerically accepted version was rejected for obvious acne.

This bounded desktop integration does not establish original-game visual parity,
all costumes, complete authored stages, automatic light fitting, or mobile
quality/performance. The default Photo Studio camera stays unchanged. See the
[full technique inventory](framework-techniques.md) for remaining scope.
