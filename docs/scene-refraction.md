# Convex faceted refraction

`ConvexRefractionShape` and `SceneRefractionRenderer` are opt-in independent tools for a closed convex glass or diamond-like solid in an explicit distant cubemap environment. PPT109 names a diamond refraction shader but omits its algorithm. This implementation uses independently authored geometric optics. General Snell/Fresnel and dielectric radiance transport are described in [PBRT4 section9.3](https://pbr-book.org/4ed/Reflection_Models/Specular_Reflection_and_Transmission) and [section9.5](https://pbr-book.org/4ed/Reflection_Models/Dielectric_BSDF). No original asset, shader or PBRT implementation is bundled.

```csharp
using GakumasPhotoMode;
using UnityEngine;

if (!ConvexRefractionShape.TryCreate(yourReadableMesh, 0, 1,
                                    out var shape, out var reason))
    throw new System.InvalidOperationException(reason);
var optics = new SceneRefractionRenderer();
var material = new SceneRefractionSurface {
    shape = shape,
    localToWorld = yourTransform.localToWorldMatrix,
    environment = yourLinearHdrCube,
    indexOfRefraction = new Vector3(2.4f, 2.4f, 2.4f)
};
var settings = new SceneRefractionSettings {
    enabled = true, surfaces = new[] { material }
};
// Host produces matching current opaque HDR and linear-eye depth first.
// Do not also render this optical shape through the host's ordinary materials.
if (optics.TryRender(opaqueHdr, opaqueEyeDepth, camera, settings, out var frame))
    Graphics.Blit(frame.color, destination);
// Dispose optics and shape when the host no longer needs them.
```

The shape snapshots one readable triangular submesh, welds exact duplicate positions, validates oriented closed edges and convex supporting planes, then owns an independent mesh. Limit128 triangles. It does not infer a convex hull for a concave model or silently freeze current skinned/deformed geometry. Recreate the snapshot explicitly when geometry changes. Transforms remain explicit per invocation and include mirror/nonuniform affine transforms. Caller source meshes are not modified.

The selected source has at most65536 vertices before welding. Local bounds diagonal is1e-4..1e4, with a relative2e-6 supporting-plane tolerance for float geometry. Transformed unit-plane offsets must remain finite within±1e8. These numerical domains are explicit; normalize very large or tiny author coordinates and use the instance transform rather than relying on cancellation between huge vertex coordinates.

Entry and each internal boundary use unpolarized Fresnel splitting and Snell directions. Each transmitted escape samples the supplied environment while the reflected part continues inside, including total internal reflection. RGB absorption is per world unit of internal path. RGB IOR is an authored three-channel dispersion approximation, not a sampled physical spectrum. The default2.4 is an example author value, not an original game parameter. Exterior IOR is explicit. For a sensor inside the solid, radiance IOR scaling remains present.

`internalInterfaces` bounds internal boundary visits to1..32. The renderer does not pretend a finite loop converged: `Frame.unresolvedThroughput.rgb` contains the remaining escaping coefficient in exterior-environment radiance units; A marks primary solid coverage. Multiplying each component by the corresponding upper bound of scaled environment radiance bounds the omitted nonnegative contribution. No residual is reassigned to a convenient direction or diffuse lobe. A higher interface budget can still leave a nonzero trapped path. No stochastic sampling or implicit animation clock is used.

Environment inputs are linear floating Cubemap or created cube RenderTexture, optionally the current result of `SceneSkyCapture`. Rotation, mip and radiance scaling are explicit. A mip is an author-selected cube mip, not a rough-interface BSDF or GGX prefilter. Invalid supplied inputs fail rather than silently falling back. Outgoing rays use a distant environment; there is no local probe-box parallax, screen-space ray hit, caustic projection, rough/multiple volume scattering, nested media or tracing between different solids.

Native geometry draws composite the nearest primary solid against matching current opaque depth. Untouched pixels preserve source RGBA. Covered pixels have alpha1 because optical transmission already contributes radiance, not alpha coverage. Multiple shapes resolve primary depth regardless of submission order, but their optical paths do not refract through each other. `firstEyeDepth` is primary/clipped surface depth, not the depth of the refracted background; downstream fog/DOF adapters must not confuse those meanings. Near-plane clipping does not move optical entry points.

This standalone stage does not install a camera component, modify shared materials or shader globals, or activate itself in PhotoStudio. Inputs must be matching full-size fixed linear non-MSAA2D HDR and RFloat/RHalf eye depth from the same full-viewport Built-in Forward camera. Per-axis limit4096, no XR. Three targets use40 logical bytes per pixel including the color target's depth allocation; driver overhead and borrowed cube/shape resources are separate. The budget is checked before target allocation. Each call invalidates old frames. Disable, failure, loss, resize and disposal release/invalidate owned outputs; do not feed these outputs into the same instance.

Supported views are canonical perspective (including offcenter/jitter and oblique clipping) and affine orthographic (including shear). Arbitrary projective transforms with a displaced perspective sensor or noncanonical homogeneous W fail explicitly. The host supplies valid current opaque color/depth contents. Primary ray directions are formed in view space before world translation to avoid subtracting nearly coincident world points near an internal critical angle.

Enabled optical/native execution and production/platform acceptance are separate from public source-contract tests. Full production scenes, other transparent/refractive consumers and mobile cost remain in the framework inventory.
