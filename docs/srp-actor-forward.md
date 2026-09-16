# Full Actor Forward in an explicit SRP host

Development integration; the default Photo Studio camera is unchanged. This uses
the toolkit's complete `ActorToon` and `ActorSupplemental` shaders, not the reduced
Planar capture material. Original game assets or shader bundles are not required.

## Frame inputs and order

The host records its scene first, captures scene-only reflection history, resolves
optional Planar / SSR / Probe reflection, and then records `SrpActorForward`.
Subsequent transparent effects and post-processing belong to the host.

`TileSceneRenderer.Settings.depthStencil` must be a separately owned, created,
depth-only `D32_SFloat_S8_UInt` render texture matching the scene output dimensions.
Setting this optional field stores the main scene pass's actual raster depth;
leaving it null preserves the existing transient scene-depth path. Color remains
`B10G11R11_UFloatPack32`.

```csharp
var depth = new RenderTexture(new RenderTextureDescriptor(width, height,
    GraphicsFormat.None, 0) { depthStencilFormat = GraphicsFormat.D32_SFloat_S8_UInt });
if (!depth.Create()) throw new InvalidOperationException("Scene depth unavailable");
sceneSettings.depthStencil = depth;

var inputs = new ActorForwardParameters();
inputs.SetVector("_CapturedLightDirection", new Vector4(0, 0, -1, 1));
inputs.SetVector("_ActorKeyColor", Vector4.one); // Literal linear values.
var actorSettings = new ActorForwardDrawSet.Settings {
    renderers = actorRenderers, parameters = inputs, outlines = true, hairCover = true
};
var forward = new SrpActorForward(camera, new SrpActorForward.Settings { enabled = true });

// Prepare after animation; retain every borrowed input through GPU completion.
if (!ActorForwardDrawSet.TryPrepare(camera, actorSettings, out var draws, out var error))
    throw new InvalidOperationException(error);
if (!TileSceneRenderer.TryPrepare(camera, sceneSettings, out var scene, out error))
    throw new InvalidOperationException(error);

// Inside the host's valid ScriptableRenderContext callback:
if (!scene.TryRecord(context, out _, out error)) throw new InvalidOperationException(error);
if (!forward.TryRecord(context, scene, draws, sequence, out var actorFrame, out error))
    throw new InvalidOperationException(error);
// actorFrame.color: RGBAHalf; actorFrame.eyeDepth: current positive R32 eye depth.
// The host records its remaining work and calls context.Submit().
```

The reflection overload additionally accepts a `SrpTileReflection.Frame` recorded
from the exact same scene preparation and sequence. An arbitrary HDR texture or a
ticket from another scene/frame is not accepted. The scene must itself remain free
of actors and indirect specular already added by the reflection resolver.

The Actor pass owns a separate RGBAHalf + D32S8 output and R32 eye-depth export,
nominally 20 bytes per pixel. Scene depth is copied without an eye-depth
unprojection/reprojection roundtrip: that roundtrip can reject coincident geometry
at `LEqual`. The destination stencil is cleared independently; scene receiver bits
do not become Actor stencil bits. Source color and depth targets remain borrowed
read inputs. A scene depth-only Store adds eight nominal stored bytes per pixel;
selecting D32S8 instead of transient D32 increases the scene attachment budget by
four bytes per pixel. These are format budgets, not measured GPU residency or
bandwidth savings.

## Materials, lighting and lifetime

Only explicit `MeshRenderer` / `SkinnedMeshRenderer` inputs with toolkit ActorToon
materials and valid submeshes are accepted. Static-batched geometry is excluded.
Queues and material fixed states are retained. Main opaque draws precede body
outlines, hair coverage, hair outlines, and transparent main draws. Transparent
draws use queue then back-to-front view depth; use distinct authored queues where
stencil dependencies require an explicit order. Coplanar same-queue order should
not be used as an implicit stencil scheduling API.

`ActorForwardParameters` binds the full shader's nonmaterial lighting, head basis,
environment, explicit shadow and face-decal inputs to owned material snapshots.
Its typed setters reject unknown inputs, nonfinite values and overflowing arrays;
light and decal arrays hold at most eight elements. New hosts start from neutral
inputs. `CaptureCurrentGlobals()` is an explicit legacy-host bridge, not automatic
scene-light discovery or a snapshot of queued command-buffer state.

Supply ambient lighting with `inputs.SetAmbientProbe(probe)`, or use
`ActorForwardParameters.BindAmbientProbe(ownedMaterial, probe)` inside the callback
below for each renderer's interpolated/authored probe. The host selects the probe
and anchor; the toolkit does not perform scene discovery. A new input set uses a
zero probe. These coefficients are material-local: setting `unity_SH*` vectors on a
material cannot reliably populate Unity's engine-owned lighting buffer during
explicit SRP draws. The optional authored captured-SH branch retains its existing
priority. Ordinary Built-in materials without the new opt-in uniform keep their
original engine-probe path.

The legacy bridge normalizes only the exact 2D black placeholder used by the old
host for a disabled environment array. Active arrays and other incompatible
texture dimensions still fail validation. A CPU global snapshot does not capture
per-renderer SH or an automatic Built-in screen-shadow map. Shadows must be supplied
through the explicit shadow texture/matrix inputs, or a current
[`SrpActorShadow.Frame`](actor-shadows.md) in `actorSettings.selfShadow`. Record
that producer before preparing Actor draws, using the same composition sequence.
Its direction/strength is independent of the Actor toon light and scene drop-shadow
light. This module does not discover scene lights or automatically fit shadows.

`configureMaterial(Renderer renderer, int submesh, Material ownedMaterial)` can
configure per-renderer/submesh shading after the common inputs are applied. It is
called for both main and any supplemental snapshot. Do not change the shader,
queue, type or fixed render states there; put those on source materials before
preparation. Do not mutate the input renderer or retain a snapshot beyond its
preparation's lifetime. Constructing another `Material` does not reliably retain
non-Properties runtime uniforms and is not a substitute for explicit binding.

Renderer/submesh property blocks retain Unity's normal precedence, including a
submesh block replacing the renderer-wide block. Property blocks, geometry, skin
pose and texture contents are borrowed, not frozen or automatically refreshed.
Keep them unchanged until the GPU finishes using a preparation. Source materials
are never replaced by the compositor.

Preparation detects subsequent camera matrix, viewport, clipping, culling-mask,
target and dynamic-resolution changes; renderer activity, layer and object
transform changes; mesh replacement and vertex/submesh count changes; and released
sampled textures. Inputs excluded by a layer or activity filter are also tracked,
so reactivating them requires preparing again. These structural checks do not hash
mesh buffers, bone poses, property blocks or texture contents. A callback that
changes the camera or registered renderer during preparation is rejected.

Sequences must be positive and strictly increasing, with a fresh scene preparation
for every recording. `Frame.IsCurrent` is a current-recording/lifetime check, **not
a GPU fence**. A later successful recording retires an older output ticket. The
host owns submission, completion, resizing and disposal order. Dispose preparations
and the compositor only after the GPU no longer needs their resources; release and
destroy the external scene targets separately.

## Validation scope and remaining work

The opt-in `--self-test-srp-actor <output-directory>` fixture currently exercises
generated geometry on desktop D3D11 and Vulkan: full material types, scene depth,
eye/hair stencil, view-faded hair owning depth, supplemental outline order,
straight/premultiplied/additive blending, cutout, actor dither fade, property blocks,
atlas inputs, explicit local/ambient lights, emission and environment cubes.
An independent probe control feeds authored L0/L1/L2 coefficients through Unity's
`CustomProvided` renderer property block and ordinary `ShadeSH9`, while the SRP uses
the new material-local path. Normal-angle and restored-frame controls compare the
entire color/depth fields; the reference does not share the new SH function.
Ordinary Built-in camera color/depth controls and selected native captures are
separate from static Python source-boundary tests. This does not establish original
game visual parity or mobile performance.

The fixture also exercises malformed inputs, callback constraints, draw budgets,
output feedback, structural lifetime checks, parameter/global isolation, and a
real scene-only SSR / Probe resolver before Actor composition. A separate history
owner records after Actor commands; a deliberately polluted history is a negative
control. Dynamic-resolution flag mutation is tested only where the backend accepts
that flag; an unavailable setter is reported explicitly rather than counted as a
successful rejection of enabled dynamic resolution.

`--photo-mode --validate-srp-actor-character <output-directory>` is a separate opt-in
fixture requiring the host's own assets. It exercises full main/supplemental draws,
front/side/rear views, motion seek/restore, material-detail negative controls and
explicit per-renderer ambient probes. Ordinary reference draws temporarily disable
automatic Built-in shadow receiving to compare the same explicitly supplied inputs;
this is not a claim of automatic-shadow equivalence. Two supplied costumes have
been exercised on desktop D3D11 and Vulkan; assets and private captures are not
distributed. All 40 actual-character color fields matched exactly in these runs.
Some skinned side/front-view eye-depth fields differed by up to `9.54e-7` world
units, below the fixture's existing `1e-5` threshold; all-character depth is not
claimed bit-identical. Selected native captures separately compare raw depth and
stencil bytes. The opt-in `--validate-actor-shadows` extension separately exercises
current geometry, moving poses and independent self-shadow controls with both
costumes/APIs; see [Actor shadows](actor-shadows.md) for its actual coverage and
negative controls. Broader characters, face-decal/normal-map combinations and
production lighting remain separate coverage work.

PDF27's motion/depth/material-ID Half4 and original
GBuffer4 storage reuse are still separate open integration work: the current R32
eye-depth export and separate HDR output do not implement them. See the complete
[technique inventory](framework-techniques.md), [Tile scene](tile-scene.md),
[Tile reflections](tile-reflections.md), and [SRP Planar](tile-planar-reflections.md).
