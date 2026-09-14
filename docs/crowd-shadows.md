# Current crowd shadow geometry

`CrowdShadowSource` is an opt-in, caller-owned adapter for the scene's directional, Spot, Point and finite-source Capsule/Area shadows. It uses the existing crowd definition and explicit shared prototype poses. It does not register thousands of GameObjects or reuse the viewing camera's visibility/LOD lists. Off-camera placements can cast into visible receivers.

PPT99–103 describes crowd material, model budgets and four runtime views. It does not publish a crowd shadow algorithm. This adapter is an independent integration, with no original assets or recovered code.

```csharp
using GakumasPhotoMode;

using var shadows = new CrowdShadowSource();
var settings = new CrowdShadowSettings { enabled = true };
// Refresh after changing placements, prototype material inputs or explicit current poses.
if (!shadows.TryPrepare(yourDefinition, settings, yourCurrentPoses))
    throw new System.InvalidOperationException(shadows.UnavailableReason);

sceneCamera.decalLighting.shadows.crowds = new[] { shadows };
sceneCamera.mainLightShadow.crowds = new[] { shadows };
// Enable/configure the desired source shadows separately; registration alone enables no light.
yourCamera.Render();
// Remove registration before disposing. Never change/dispose between prepare and execution.
sceneCamera.decalLighting.shadows.crowds = System.Array.Empty<CrowdShadowSource>();
sceneCamera.mainLightShadow.crowds = System.Array.Empty<CrowdShadowSource>();
```

The same settings are available through Forward lighting and its other consumers. Prepare the shadow source before rendering any consuming camera, including a `CrowdCamera` used for receiving shadows. Ownership is independent of the crowd beauty renderer. The two producers can consume the same definition/pose but own separate vertex buffers.

For Point lights, set `light.shadow.stablePointTexels = true` when deterministic Deferred/Forward face-edge sampling is required. It uses the finite-source model's integer local texels and2^-20 normalized-face edge band, without changing the112-byte shadow layout. The default remains false for historical Point image compatibility. Legacy floating atlas coordinates can pick neighboring texels at exact edges across Deferred reconstruction and Forward interpolation, independently of caster type. Capsule/Area finite sources already use deterministic addressing.

This convention does not make Deferred reconstruction and Forward triangle interpolation universally identical. Subpixel camera moves can change reconstructed world positions before native rasterizer snapping changes triangle attributes. A discontinuous shadow edge may then differ between the two receivers even though both caster paths agree exactly within each backend. Shadow texel-edge tests keep receiver rasterization fixed while moving the light; camera/raster precision is assessed separately. The snapping/attribute rule is documented in section3.4.1 of the [D3D11.3 functional specification](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm).

The adversarial Point-edge fixture observes both existing coordinate producers, then independently evaluates full-image Point PCF and the authored plane's BRDF at each producer's own coordinates. It checks each image and the predicted cross-backend difference at a maximum RGBA error of0.0003. No edge pixels are excluded. Comparing two images as though their receiver coordinates were identical would misclassify the intentionally discontinuous visibility at a texel boundary.

## Geometry and state

All nonhidden placements submit explicit Low or High triangle geometry, selected through `CrowdShadowSettings.meshQuality`. Shadow geometry does not change with the beauty camera's mesh budget or billboard quadrant. Near and far viewers therefore share a stable caster silhouette; the cost can exceed beauty impostor rendering. No automatic shadow LOD, cascade selection or light-frustum compute culling is inferred. Hardware clipping still applies.

The adapter preserves the 48-byte placement layout and reuses current shared prototype deformation. GPU compute runs before indirect depth draws. Explicit CPU fallback uses the same structured vertex output and still requires SM4.5 indirect instancing. This is shared-pose instancing, not independently sampled animation for each spectator.

Coverage consumes explicit prototype cull, alpha cutoff and material albedo-map alpha, alpha scalar and UV transform. Radiance tint, lightstick emission, GI and lighting do not change geometry coverage. Current alpha render textures must be produced before the shadow passes. Unsupported source topology/skin/morph contracts fail through the same crowd geometry validation; no native `BakeMesh` or runtime GPU readback is used.

Definitions, meshes, maps and poses are borrowed. Values are snapshotted at `TryPrepare`; advance `contentVersion` after in-place mesh/index/shape edits. Replacing meshes and changing quality invalidates the geometry cache. Prepared-source generation changes and disposal between atlas preparation and recording fail before recording any crowd geometry. The caller must also keep buffers/meshes/textures alive until submitted GPU commands finish.

An empty or disabled source fails preparation and releases its resources. Remove it from the consumer's registration list. An all-hidden, otherwise valid definition remains prepared with zero draws. Null/empty registration means no crowd casting and retains the existing default path.

## Budgets and evidence boundary

At most 16 distinct prepared sources can be registered per atlas. Each retains the existing limit of eight prototypes and65,536 placements. `maximumResourceMiB` checks proposed geometry, placement and indirect buffers before allocation. It excludes source assets, CPU caches, materials, driver overhead and the separately budgeted shadow atlas.

`maxCrowdShadowTriangles` defaults to4,194,304 submitted triangles across every source and every atlas map. Six Point faces and every finite-source sample count toward that budget. Extended-source `maxExtendedCasterDraws` also counts each nonempty crowd prototype command alongside ordinary caster draws. Empty prototypes issue no draw. No GPU-visible-count readback or performance estimate is implied by these diagnostics.

Desktop validation includes31 whole authored-geometry depth/lit cases, three native skin/shape poses,10,000/65,536 placement and indirect-argument boundaries, and independent full-image receiver-coordinate checks. Twelve D3D11 captures join actual deformation buffers, per-instance vertex outputs, indirect draw arguments, shadow-atlas resources and final lighting bytes across Deferred and both Forward backends. These are independently authored fixtures, not full production-crowd or mobile benchmarks.

Production art and platform performance remain separate. This adapter does not provide per-instance motion history, automatic SSR/Planar registration, continuous area-light integration or mobile performance guarantees. Default PhotoStudio does not enable it.
