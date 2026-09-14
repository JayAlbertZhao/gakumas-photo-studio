# Current vegetation wind geometry

`VegetationWindDeformer` produces an owned mesh whose current vertex positions, normals and tangents can be consumed by ordinary scene color, depth, shadow and motion passes. The GPU backend writes the actual mesh vertex streams before drawing; it does not hide displacement inside a color-only vertex program. PhotoStudio does not create or enable this module by default.

This independently authored wind field covers the geometry part of a vegetation surface. The reference presentation names a vegetation shader but omits its algorithm. No original vegetation art or shader is supplied. Dedicated thin-leaf transmission/material response, production trees/grass and mobile performance remain separate work.

## Integration

```csharp
// One (branch weight, flutter phase cycles, flutter weight) value per vertex.
if (!VegetationWindDeformer.TryCreate(yourReadableStaticMesh, yourCoefficients,
        VegetationWindBackend.Auto, true, 64, out var wind, out var error))
    throw new System.InvalidOperationException(error);

var original = yourMeshFilter.sharedMesh;
var settings = new VegetationWindSettings { enabled = true, height = 2 };
// Before all camera, shadow and motion renders for this simulation sample:
if (wind.TryUpdate(settings, yourTimelineSeconds,
        yourMeshFilter.transform.localToWorldMatrix))
    yourMeshFilter.sharedMesh = wind.Mesh;
else
    yourMeshFilter.sharedMesh = original; // stale output must not be drawn

// Before releasing this owner, stop all consumers from drawing its mesh:
yourMeshFilter.sharedMesh = original;
wind.Dispose();
```

The clock is explicit. There is no automatic Unity time, random state, accumulated integration or physics force solver. `displacementWorld` and `flutterWorld` are world-unit displacement vectors, transformed into the supplied mesh coordinate system. Optional enabled `naturalWind` supplies its existing stateless world-space sample multiplied by `naturalWindScale`. The root, up axis and height are authored in mesh coordinates. Objects can share the same wind/time while retaining their own transforms and leaf coefficients.

Register the resulting renderer normally with the desired scene/depth/shadow/motion modules. All consumers must use the same current mesh after `TryUpdate`; the module does not discover renderers, edit culling masks, replace materials or register shadow casters automatically. The existing motion module retains geometry across successful camera renders. Reset its history for application-defined cuts/seeks. Unrelated renderer or topology changes retain the existing motion invalidation rules.

## Independent field and normals

Let `h = saturate(dot(position - root, up) / height)` and `f = h*h*(3 - 2*h)`. Each vertex's displacement direction combines current world wind times its branch weight with sinusoidal flutter times its flutter weight. Flutter phase is the sum of the explicit clock phase and that vertex's authored phase. Position becomes `position + direction*f`. The root is pinned, and the clamped height field has continuous zero endpoint derivatives.

Coefficients are treated as constant within each leaf's analytic field. Use matching coefficients across a leaf when smooth analytic normals are desired. The module does not estimate gradients of arbitrarily painted coefficients or infer disconnected leaves. Normals use the inverse-transpose of this field's Jacobian; tangents use its forward differential and are made perpendicular to the resulting normal. Handedness is preserved. A conservative directional derivative bound rejects folding or near-singular normal transforms instead of silently returning invalid normals.

Per-leaf phase sine/cosine is snapshotted at initialization. Current phase is reduced using double precision before upload. The GPU combines those values, avoiding large unreduced trig arguments and per-vertex clock drift. A disabled field, or an exactly calm zero-displacement sample, restores the original vertex channels. An explicitly created owner keeps its resources until `Dispose`; disabling wind alone is not an allocation-release operation.

## Ownership, backends and limits

Source mesh and coefficients are read only during creation and are never modified. The owner clones the mesh and snapshots its rest data. In-place source or coefficient changes require a new owner. Input must be a readable static triangle mesh with 1–262144 vertices, no skin bindposes or blend shapes, and Float32 position/normal plus optional tangent channels. Tangent directions must be nondegenerate with handedness ±1. UVs, colors and submesh topology are retained.

GPU mode supports 1–4 word-aligned streams and requires compute plus writable Raw mesh buffers. It does not call `MarkDynamic` on the GPU mesh. Explicit CPU mode uploads current positions/normals/tangents. Creation-time compute-capability fallback requires `allowCpuFallback`; later update failures are explicit and do not silently switch owners or make a stale GPU mesh current. `Backend`, `FallbackReason`, `DispatchCount` and `GpuResourceBytes` distinguish these cases. There is no runtime GPU readback.

The GPU mesh's **CPU vertex copy remains at rest**. CPU geometry consumers must use a separate CPU owner or their own reference geometry; they must not read `Mesh.vertices` and assume it contains the latest GPU wind. Native `DrawRenderer` consumers read the actual current stream. Do not change an owned output's vertex layout, topology, UVs or lifetime. On update failure `Mesh` returns null, but the owner retains its resources for an explicit recovery attempt; stop drawing the previous borrowed handle.

The resource limit is checked before owned mesh/buffer allocation and estimates cloned GPU vertex/index storage, immutable compute rest data and padding buffers. It excludes managed snapshots, caller assets and driver overhead. Bounds conservatively include all current branch/flutter displacement without readback. Neither the estimate nor the dispatch count is an isolated GPU-time or mobile-performance measurement.

Finite input domains: source positions/normals/tangents and local root/axis within ±10000, height .001–10000, world displacement within ±1000, coefficient components 0–1, frequency 0–100 Hz, clock ±1e9 seconds, phase ±1e6 cycles. The supplied matrix must be finite nonsingular affine. Enabled natural-wind parameters and the transformed local displacement are validated separately. Large coordinates, extreme transforms and fields that violate the nonsingular derivative bound are rejected.

## Verification

The Player fixture compares every output vertex channel with an independent finite-difference field/Jacobian reference, and compares actual CPU/GPU and complete scene color, normal/depth, shadow and motion outputs. Python tests guard the public structure/ownership contract; they do not prove image quality or backend performance. Full production vegetation and thin-leaf shading are not implied by the wind geometry tests.
