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

By default the Actor pass owns a separate RGBAHalf + D32S8 output and R32 eye-depth export,
nominally 20 bytes per pixel. Scene depth is copied without an eye-depth
unprojection/reprojection roundtrip: that roundtrip can reject coincident geometry
at `LEqual`. The destination stencil is cleared independently; scene receiver bits
do not become Actor stencil bits. Source color and depth targets remain borrowed
read inputs. A scene depth-only Store adds eight nominal stored bytes per pixel;
selecting D32S8 instead of transient D32 increases the scene attachment budget by
four bytes per pixel. These are format budgets, not measured GPU residency or
bandwidth savings.

## Optional packed scene attachment reuse

`SrpActorForward.Settings.storage` (or `DesktopFrameRenderer.Settings.actorStorage`)
selects storage explicitly. The default remains `SeparateHalf`.

| Policy | Actor color | Owned nominal bytes/pixel | Scene color/depth contents |
| --- | --- | ---: | --- |
| `SeparateHalf` | RGBAHalf | 20 | Preserved |
| `SeparatePacked` | B10G11R11 packed HDR | 16 | Preserved |
| `ReuseScenePacked` | The exact scene GBuffer4/output texture | 4 | Consumed in place |

Reuse borrows **both** scene color and its stored D32S8 depth; only the R32
current eye-depth export is allocated. Record every scene-only Planar/SSR/history
reader first. If reflections are enabled, their resolved color is copied to the
reused color without sampling either active attachment. Otherwise color stays in
place. Scene stencil is reset without changing raster depth, then the same full
Actor draws execute. Effects/DOF/grading consume current Actor color/depth as usual.

After consumption `scene.SceneContentAvailable` is false: new scene-only readers
and a second Actor consumer reject it. `scene.IsRecorded` still describes valid
recorded work, not unchanged contents. Previously recorded reflection tickets
retain independent outputs. Do not treat the reused color as a scene-only image
or refill either target until queued work completes. Disposal never releases
borrowed targets. Material/property-block sampling of either reused attachment
is rejected.

`SeparatePacked` supplies a same-format control for reuse. Packed HDR has no alpha
channel (sampled alpha is one), no negative values and less precision than RGBAHalf;
this is an explicit quality/storage choice, not a bit-identical replacement for
the default. The 12 bytes/pixel avoided versus separate packed targets are nominal
owned allocations, not measured VRAM, bandwidth, frame-time or mobile savings.
An opt-in GBuffer2 motion/depth/identity Half4 and temporal consumer are described
below. Full production-content and platform acceptance remain open.

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

The optional packed path reuses the actual GBuffer4 and stored hardware depth.
An additional opt-in motion path can reuse GBuffer2 after its scene-only readers;
its independent layout and limits are documented below. See the complete
[technique inventory](framework-techniques.md), [Tile scene](tile-scene.md),
[Tile reflections](tile-reflections.md), and [SRP Planar](tile-planar-reflections.md).

## Optional motion and temporal resolve

`Settings.motion.enabled` enables actual GPU clip-position snapshots of the full
ordered Actor draw stream, including deformation and outline vertex processing.
The default Actor shaders and disabled path remain unchanged. The motion shader
includes the same independently implemented surface/outline functions; it is not
an original-game shader or a reduced replacement for the color pass.

`Frame.motionDepthIdentity` is Half4: RG is current-minus-previous texture UV,
B is current eye depth, and A is an exactly representable packed integer:
`(identity << 4) | flags | validHistory8 | actorBit1`. Flags are `ExcludeTaa=2`
and `NoJitter=4`; the scene namespace has bit 0 clear. Each namespace reserves
127 identities. Actor identity is per renderer/submesh/pass; IDs remain reserved
until motion is disabled or its producer is recreated. Resetting history alone
does not recycle IDs. This layout is an independent contract, not an assertion
about the original game's material IDs.

An additional R32 `expectedPreviousDepth` supports disocclusion checks. The
implementation therefore does not claim identical attachment count or mobile
bandwidth to the reference pipeline. It requires desktop geometry shaders and
independently blended MRTs; desktop acceptance does not establish mobile support.

`includeSceneMotion` adds explicit readable scene geometry to the same motion
attachments. `reuseSceneMotionStorage` independently borrows the scene's stored
Half4 normal attachment after the scene-only readers have been recorded. New
scene-only consumers are rejected after destructive reuse; already recorded
commands remain valid. Borrowed textures are never released by the motion owner.

Readable topology is checked for correspondence. For imported GPU-only Actor
meshes, `allowImmutableUnreadableMotionMeshes` is an explicit host promise;
in-place topology changes require a new `motionRevision`. Scene motion still
requires readable triangle topology. Sequence gaps, geometry changes, explicit
revisions and detected camera cuts invalidate the corresponding history.

`FrameTemporalAntialiasing` is a separate opt-in post-FX HDR consumer. It validates
same-frame resources and feedback hazards, reprojects against identity and depth,
clips history to a current-inclusive variance box, and blends with luminance
weights. The current-inclusive bounds preserve stationary sparse HDR highlights.
Blended hair and current FX changes conservatively bypass history. There is no
normal-rejection claim for this Half4 layout. The host supplies projection jitter
and resets on seeks/cuts; no implicit camera mutation or clock is used.

See [desktop integration](desktop-host.md#可选整帧运动与-taa) for the complete
FX → TAA → DOF → grading order, allocation budgets and opt-in configuration.
Moving-pixel controls establish active correspondence, not an exhaustive
deformation oracle or absence of temporal artifacts in every character/stage.
The generated desktop fixture also checks two-bone skinning, blend-shape deltas,
nonuniform root scale, wardrobe scale, perspective/off-axis projection, and
outline extrusion against independent CPU correspondence. Back-facing test
geometry isolates the outline identity from the main surface; bone, blend-shape
and authored width changes must retain the previous extruded position. These
controls use authored vertices/weights, not `BakeMesh` or the motion producer's
clip atlas. A nonzero packed outline depth offset also has an independent
previous-eye-depth control. Their current scope is a small generated mesh and
one uniform packed offset, not every imported rig or outline encoding.

The temporal extension is a desktop opt-in preview, not a claim of bit-identical
shading on every backend. A bounded real-character Vulkan control has reproduced
a one-half-ULP blue-channel difference between
motion-disabled and motion-enabled shading (`1.52587890625e-5` in that sample).
The same-frame repeats of each path are exact, and native replay retains the
difference. A separate same-input Float32 control isolates a two-Float32-ULP
difference at one affected channel (`9.313225746154785e-10`); explicit Float32
texture upload/load preserves those values, and GPU conversion to Half reproduces
both complete Half images exactly in that control. CPU nearest-even conversion
does not reproduce that sample. This bounds the observed arithmetic/storage
effect on this device; it does not establish a general driver rounding rule or
identify the originating fragment operation. Neither the exact-color gate nor
the ordinary Forward tolerance has been relaxed; retained failing controls
remain failures. Keep motion disabled if exact old-path color is required.
The corresponding D3D11 control covered all 16 subpixel offsets without a color
difference; this does not establish all-device or all-character equivalence.

For local diagnosis, set `GAKUMAS_SELFTEST_ACTOR_PRECISION_SWEEP=1` when running
the character fixture. It scans at most 16 rear-view projection offsets at one
fixed pose, stops at the first difference, and saves the camera matrices,
channel/value pair, full images and repeated controls. No animation frame elapses
between a control and its motion-enabled pair. This switch is not used by the
default app and does not suppress a failing validation result.
At the first mismatch it also saves same-input Float32/Half shading controls,
repeated Float32 draws and explicit Float32 upload/load/attachment-conversion
controls. These diagnostics do not alter the default material shaders.
