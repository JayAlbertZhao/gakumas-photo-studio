# Capsule and Area source visibility

`SceneLightShadowInput.extendedSourceCoverage` explicitly selects an independent finite-source geometric visibility model for Capsule and Area lights. Both it and `shadow.enabled` must be true. Existing configurations still reject an unsupported extended-shape shadow unless this model is selected. Point, Spot, directional lights and unshadowed light response retain their existing contracts.

```csharp
light.shadow.enabled = true;
light.shadow.extendedSourceCoverage = true;
light.shadow.extendedSamplesPerAxis = 2;
scene.decalLighting.shadows.tileResolution = 128;
scene.decalLighting.shadows.casters = yourCurrentCasters;
```

Capsule uses N midpoint samples along its current local-X segment. Area uses N by N midpoint samples across its current local-XY rectangle. N is1..4. Each source position renders six current radial depth views and consumes the same cross-face Hard/PCF comparison as a Point shadow. The final factor is `lerp(1, mean(sourceVisibility), strength)`. Increasing N changes the source quadrature, while increasing tile resolution changes directional depth sampling. Neither implies continuous or temporally stable soft shadows.

Extended-source taps address integer texels inside each face and sample their centers, including non-power-of-two atlas grids. Coordinates within `2^-20` in normalized face UV of an integer texel edge snap to that edge before flooring; the edge belongs to the next texel, clamped at the face border. This explicit numerical boundary convention stabilizes tiny world-coordinate differences between Deferred reconstruction and Forward interpolation. It does not widen a shadow filter. Existing Point/Spot addressing is unchanged.

This factor measures equal-length or equal-area source coverage. It multiplies the existing artistic light response: Capsule closest-segment direction and line Monitor mapping, Area trapezoid projection and plane Monitor mapping, linear falloff, BRDF, GI modulation and backlight remain unchanged. A nonuniform HDR Monitor does not reweight the source samples. This model does not integrate radiance-weighted area-light BRDFs, perform stochastic temporal accumulation, or reproduce an undisclosed original shader.

When a baked shadow channel is selected, the existing [mixed visibility contract](scene-baked-shadows.md) remains `min(realtimeVisibility, bakedVisibility)`, applied once to the direct-light response. The two masks are not multiplied together. Base GI, material emission and other lights remain independent.

Near clipping is a radial sphere around each sample. Bias is in world units as for Point shadows. Each source captures a conservative far sphere containing the light's full artistic support. For Capsule the radius is `range + 2*halfLength`. For Area it is the length of `(range, 2*halfSize.x + areaSpread.x*range, 2*halfSize.y + areaSpread.y*range)`. Using only `range` around each source would omit valid side receivers and blockers. Light attenuation/culling still uses the original capsule/trapezoid support. Near-clipped occluders do not contribute.

The shared input limits remain: `nearPlane` is at least 0.001 and strictly below `range`; both bias values are within `0..range`. The conservative capture radius does not enlarge those input limits.

All sources are refreshed each render from explicit current transforms and casters. Static and native skinned caster rules, cutout inputs and ownership are shared with [light-source shadows](scene-light-shadows.md). No screen-depth shortcut, shadow cache, original assets, shader globals or runtime GPU readback is introduced. Deferred Scalar/Instanced and shared Forward lighting consumers read the same independent shadow metadata.

The existing144-byte light and112-byte per-light shadow layouts remain unchanged. For extended sources only, negative shadow `options.w` encodes the first tile, the matrix holds the center and source bases, and atlas metadata holds the sample grid. Per-map producer metadata is separate, ensuring every native depth draw uses that sample's actual position. Other shapes retain their previous bytes and semantics.

Each sample costs six depth views. N=2 means12 views for Capsule or24 for Area; N=4 means24 or96. `maxShadowedLights` still counts lights, not samples. When an active extended light is present, `maxExtendedSourceSamples` limits their total samples (default64, range1..256), and `maxExtendedCasterDraws` limits all atlas faces times registered casters (default32768, range1..262144). Disabled casters are conservatively included in that allocation budget. These checks run before extended atlas/material allocation. Existing4096-pixel/device atlas limits and caster limits also apply. Budget failure rejects the frame rather than silently dropping source samples.

Finite source sampling can miss a narrow visible or blocked source region, particularly with small N. Fixed PCF introduces its own depth-texel discretization. Actual desktop correctness, production-scene quality and mobile cost require separate evidence; this interface is not a claim of physical area-light integration or mobile performance parity.

## Desktop verification scope

`ActorRenderingSelfTest.ExtendedShadow` exercises 50 complete-image cases on the current desktop D3D11 Player. Coverage includes both shapes at N=1..4, cross-face PCF, current rotated sources and casters, bias and near clipping, perspective views, zero-length Capsule, cutout, leaf/backlight, GI, four baked channels and actual Monitor content updates. Eight mixed-mask counterexamples distinguish `min` from multiplication. Both sides of the numerical texel-edge band are tested, and large finite sources explicitly test blockers beyond an individual sample's old `range` sphere.

The independent depth reference evaluates every atlas texel using authored triangles, clipping, top-left coverage, perspective interpolation and [D3D11's eight-bit subpixel snapping](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm#3.4.1%20Coordinate%20Snapping). There are no excluded edge pixels. Forty-seven complete depth comparisons use a fixed `2e-5` normalized-depth limit; final independent visibility uses `3e-4` RGBA. Three native one-bone poses are additionally compared with independently transformed static geometry. This does not validate arbitrary production rigs or custom vertex displacement.

Mixed Spot/Capsule/Point/Area lighting checks a 43-map atlas, individual-light addition, Scalar/Instanced batch sizes 1/2/256 and Forward BruteForce/Tiled. Seven native captures verify per-source origins, current geometry and viewport order, the separate 112/144-byte buffers or equivalent Scalar uniforms, and exact native/runtime atlas and final-image bytes. `GAKUMAS_SELFTEST_CAPTURE_EXTENDED_SHADOW=1` requests these captures only in a dedicated self-test Player with RenderDoc already injected; do not combine capture flags.

The previous complete self-test records and preview bytes remain unchanged. Full production scenes, joint FX/water content, temporal quality and non-D3D11/mobile bandwidth or frame time remain separate work.
