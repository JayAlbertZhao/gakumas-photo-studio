# Runtime crowd module

This opt-in module combines current shared prototype poses, nearest-visible mesh budgets and four runtime-generated impostor views. It does not include crowd assets, original game code, recovered shaders or an original animation system. The PhotoStudio application's default scene and rendering path do not enable it.

Validation scope: actual desktop D3D11 rendering with independently authored meshes, current native skin/shape references, camera composition, GPU sorting/indirect execution and explicit CPU fallback. Production crowd art, full-scene temporal/reflection integration and mobile performance remain unverified.

## Inputs and ownership

`CrowdDefinition` is a reusable ScriptableObject with one to eight `CrowdPrototype` records and up to 65,536 `CrowdInstance` placements. Each placement selects a prototype, position, yaw, positive uniform scale and an RGB **output-radiance multiplier**. Tint is not a material albedo replacement. The zero-instance case is inactive and releases resources.

Each prototype supplies an explicit readable Low mesh and, when High quality is selected, an explicit High mesh. No automatic quality fallback is inferred. A prototype is one triangle submesh with an explicit material, cull mode, alpha cutoff and receiver group. Use distinct prototypes for distinct poses or material contracts. This is shared-pose instancing, not 10,000 independently sampled animation tracks.

Material inputs use the existing scene Forward PBR contract: albedo, MOS, emission, UV transform and linear RGB tangent-space normal maps. Crowd GI accepts an explicit per-prototype SH probe or None. Renderer-dependent/lightmap lookup and per-instance scene-probe interpolation are not inferred. PBR is the default; `CrowdLighting.Toon` is an independently authored two-band diffuse response with the same specular, light and shadow inputs. It does not recover a game's original character ramps.

Optional `CrowdPose` values supply a current root, bone transforms and shape weights per prototype. Supported skin topology has at most 256 bones, four influences per vertex, and 32 single-frame shapes. Shapes use their authored frame weight; current weights are explicit, not read from a native renderer. More than four influences or multi-frame shapes fail rather than being silently truncated. GPU deformation runs once per prototype, and both near geometry and all four captures use that current output.

Meshes, textures, definitions and transforms are borrowed. The module does not mutate source renderers, materials, culling masks, meshes or poses. Advance `definition.contentVersion` after in-place mesh vertex/index/shape changes; replacing the mesh object or definition and switching quality invalidates the cache. Runtime material, placement and pose values are refreshed each prepare. A caller must not modify or dispose borrowed inputs between preparation and the corresponding draw.

## Minimal integration

For optional masked HDR emission on held geometry, see [authored audience lightsticks](crowd-lightsticks.md). Per-instance color and explicit timeline phases stay current in both near geometry and four-view impostors; the original body tint and ordinary emission are unchanged when disabled.

```csharp
using GakumasPhotoMode;
using UnityEngine;

var definition = ScriptableObject.CreateInstance<CrowdDefinition>();
definition.prototypes = new[] {
    new CrowdPrototype { lowMesh = yourReadableMesh, highMesh = yourHighMesh }
};
definition.instances = new[] {
    new CrowdInstance { position = Vector3.zero, yawDegrees = 0, scale = 1 }
};

var adapter = yourCamera.gameObject.AddComponent<CrowdCamera>();
adapter.definition = definition;
adapter.settings.enabled = true;
adapter.settings.meshBudget = 256;
adapter.settings.captureResolution = 128;
```

The camera adapter requires Built-in Forward, a full viewport, a caller-owned fixed linear ARGBHalf/ARGBFloat render target with depth, no MSAA/XR/dynamic resolution, and host color/depth clearing. Keep prototype pose-host renderers outside the host camera's culling mask when they are only pose sources; this adapter deliberately does not edit that mask.

For example, create a linear `RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)`, call `Create()`, and assign it to `yourCamera.targetTexture`. The host owns presentation and must release/destroy that target and its temporary definition when finished. Disable or destroy the adapter first; it releases only its own buffers, atlases and materials. No character assets or `CharacterSceneRuntime` initialization are required.

For a custom application, own a `CrowdRenderer` and call:

```csharp
using var renderer = new CrowdRenderer();
if (!renderer.TryRender(yourHdrDepthTarget, yourCamera,
        definition, settings, currentPoses, out var frame))
    Debug.LogWarning(renderer.UnavailableReason);
```

The target's existing color and native depth are preserved, then crowd geometry is drawn into them. The caller controls ordering and must provide a meaningful current depth attachment. `Frame.linearEyeDepth` is a borrowed **crowd-only** depth texture, valid until the next prepare or disposal. A zero texel means no visible crowd fragment. It is not a complete scene depth texture and is not automatically merged into `_CameraDepthTexture`, TAA, SSR, fog, reflection or motion-history producers.

The adapter draws at `CameraEvent.AfterForwardOpaque`, before ordinary transparency. Native depth occludes crowd fragments and receives their reconstructed depth. Local/directional shadow **receiving** uses current shared Forward-light resources. Optional [current crowd shadow geometry](crowd-shadows.md) registers a separate prepared source for directional/local shadow casting without creating thousands of `SceneShadowCaster` objects. It casts explicit Low/High geometry independently of view LOD. Temporal/reflection integration remains downstream work.

## GPU path and fallback

1. Snapshot current prototype pose matrices and shape weights, validate inputs and estimate owned resources.
2. Deform each prototype into a structured GPU vertex buffer.
3. Conservatively classify instance spheres against the current camera frustum. Stable `(distance², original index)` ordering selects the nearest visible mesh budget; every other visible instance becomes an impostor.
4. Deterministic per-prototype/LOD prefix scans generate compacted live IDs and actual indirect instance counts. There is no fixed per-tile truncation or runtime GPU readback.
5. Render front, right, back and left views into owned albedo/coverage, normal/depth, MOS and emission atlases. The lighting is evaluated at the current crowd fragment, not baked into a supplied sprite sheet.
6. Issue near and far indirect draws per prototype. Select the nearest captured quadrant relative to instance yaw and camera view.

`Auto` chooses GPU compute when available; `Cpu` explicitly performs CPU pose/selection work. Missing compute only falls back when `allowCpuFallback` is enabled. The CPU backend still requires SM4.5 structured buffers, indirect instancing and four float MRTs; it is not a universal legacy-GPU fallback. Light-grid backend selection has its own explicit fallback settings.

Selection uses separate float32 multiply/add operations on both backends and breaks equal distance keys by original index. The CPU path explicitly rounds those operations: a nominal C# float expression can otherwise retain wider intermediate precision on Mono and reorder near-equal distances. It does not quantize distances into coarse bins or silently accept different GPU instance lists.

Four-view impostors approximate finite angular sampling and perspective. They are intended for distant instances. They do not reproduce arbitrary close-up viewpoints, view-dependent transparent materials or per-pixel motion history. Atlas sampling is point-filtered to preserve material coverage; resolution and the model budget are application choices. Exact GPU/CPU backend agreement and approximation against native geometry are separate acceptance conditions.

## Budgets and evidence boundaries

`maximumResourceMiB` bounds the proposed owned crowd buffers, four float atlases, atlas depth attachment and crowd-eye target **before allocation**. Shared Forward light/grid/shadow resources retain their separate budgets. It is not a measurement of total driver memory, source textures, CPU caches or the whole application's memory. Output axes are limited to 4096; capture tiles to 16–512 pixels.

`Frame` reports the submitted input count, two indirect commands per prototype, crowd compute dispatches and resource-byte estimates. These are submission/resource diagnostics, not a GPU readback of the visible count or GPU timing. The fixture's synchronization-inclusive render/readback stopwatch is not isolated GPU time. Actual device captures and mobile profiling must be assessed separately.

The implementation uses Unity's documented [command-buffer indirect draw](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.DrawMeshInstancedIndirect.html) interface. The D3D11 indirect argument UAV uses the documented [R32_UINT indirect-argument buffer layout](https://docs.unity.cn/2022.2/Documentation/ScriptReference/GraphicsBuffer.Target.IndirectArguments.html). The sorting, compaction, pose, capture and impostor code in this repository is independently authored.

## Desktop acceptance

The application fixture `ActorRenderingSelfTest.Crowd.cs` exercises actual `Camera.Render`, current native MeshRenderer/SkinnedMeshRenderer poses, entire float-RGBA comparisons and GPU buffers. The Python crowd contract suite only guards API/source structure; it does not substitute for those renders.

- All ranks, distance keys, five indirect argument words and every live compacted ID are independently checked across 1–10,000 inputs, moving/asymmetric cameras, eight types, quality switches and 65,535/65,536 capacity boundaries. Malformed inputs and resource recovery are exercised separately.
- Native/current GPU and explicit CPU images use an entire-image maximum RGBA difference limit of 0.0002, without edge masks. Coverage, material/normal/GI changes, current shadow casters, opaque depth and ordinary transparency have positive controls.
- Twenty-six yaw/view cases independently verify every quadrant-colour pixel. Their four-view/native approximation has separate, predeclared fixture limits: silhouette IoU ≥ 0.35 and entire RGBA RMS ≤ 0.30. These are not general close-up quality guarantees; view switching can visibly change a silhouette.
- A native D3D11 capture draws all 10,000 placements using eight independently authored 500-triangle prototype meshes: 256 near instances plus 9,744 two-triangle impostors, 147,488 submitted triangles, 117 compute dispatches, 32 current atlas draws and 16 indirect draw commands. The fixture uses 129×97 output and 64-pixel capture tiles; it is a workload/correctness fixture, not a full-HD production stage.

For that fixture the owned crowd GPU-resource estimate is 12,309,604 bytes, excluding separate lighting budgets, CPU/source caches and driver overhead. Desktop replay GPU timestamps and synchronization-inclusive wall times are different measurements. Neither establishes a target frame rate for another GPU, a production scene or Vulkan/Metal/mobile devices.
