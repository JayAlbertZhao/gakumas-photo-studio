# Thin-leaf material

`VegetationLeafMaterial` is an opt-in, independently authored thin-sheet diffuse material for `SceneDeferredCamera.Surface.leaf` and `SceneForwardSurface.leaf`. It can consume the current mesh from `VegetationWindDeformer`; no original vegetation art or shader is distributed. The source presentation names specialized vegetation but does not disclose its algorithm.

Set `enabled=true`, supply an optional linear R thickness texture, and author `thickness`, RGB `absorption`, `transmissionTint` and `strength`. Texture sampling uses the owning surface's albedo UV transform. Transmission is `strength * tint * exp(-absorption * thickness * saturate(texture.r))`; a missing texture means thickness multiplier1. Thickness units are authored, with absorption in the inverse of those units. There is no inferred real-world leaf thickness, refraction or volumetric multiple scattering.

```csharp
surface.leaf = new GakumasPhotoMode.VegetationLeafMaterial {
    enabled = true,
    thicknessMap = yourLinearThicknessTexture,
    thickness = 1,
    strength = .7f
};
surface.cull = UnityEngine.Rendering.CullMode.Off;
// Update your wind mesh before rendering; leaf shading uses its current streams.
```

The material divides diffuse energy between reflected and opposite-hemisphere transmitted light, while retaining the existing specular lobe. Metallic diffuse suppression applies to both portions. Existing artistic backlight scales the reflected portion, not transmitted energy a second time. Optional `twoSided` orients the current shading normal toward the viewing hemisphere using the authored geometric normal; it does not change host culling. Use `CullMode.Off` to render both sides. Tangent normals, cutout and vertex transforms retain their existing inputs.

Deferred uses an optional dedicated linear RGBAHalf attachment, RGB transmission and A presence. Existing GBuffer/GI/baked-shadow lanes remain unchanged. It needs five MRTs, six with GI or baked shadows, seven with both. `maximumLeafResourceMiB` bounds this attachment's eight bytes per pixel before allocation; it excludes other scene resources and driver overhead. A disabled material on every surface allocates no leaf attachment. `Frame.leafTransmission` is borrowed and current only while the frame remains valid; loss, resize, disable and disposal invalidate old handles. Do not sample current camera/owned attachments as thickness input.

Current main and local light evaluation consumes leaf transmission. GI mixes visible/opposite-hemisphere diffuse responses per channel. Backlit shadow sampling biases toward the light rather than into the thin caster itself. When screen shadows are enabled, leaf pixels can sample the current raw main-light atlas with that bias while retaining screen AO. Forward+ evaluates the same material directly without the Half attachment; its metadata precision and transparent ordering remain distinct from Deferred. Thin-sheet transmission does not change alpha into a new transparency mode.

Deferred projected decals change the final base color, normal and MOS response without overwriting the independently authored leaf thickness attachment. This does not add a projected-decal bridge to Forward+. Input domains are thickness0–16, absorption components0–64, tint/strength0–1 and finite values. Disabled leaf settings do not validate or allocate inactive inputs. Materials and textures remain caller-owned.

Default PhotoStudio does not enable this feature. Specialized water, Low/Heavy FX and Crowd inputs are not silently reinterpreted as leaf materials. Complete production vegetation, complex transparent intersections, refractive media, platform performance and original appearance need separate evidence. The Player fixture covers enabled material behavior; Python source tests alone are not image or performance evidence.
