# Authored actor material inputs

The toolkit's `GakumasPhotoMode/ActorToon` shader accepts independently authored textures. Nothing in this contract requires game textures or shader bundles. `MaterialRepairer.FallbackShader()` returns the runtime shader; create a caller-owned `Material` and bind it to your renderer. Do not modify a shared imported material in place.

PPT40–47 describes the texture roles below. Face-specific Def.B is described on PPT49. The exact lighting equation and sampler settings are not fully disclosed there. The public shader is our reconstruction, with explicit compatibility controls and limitations, rather than the original implementation.

| Input | Channels and interpretation |
| --- | --- |
| `_MainTex` | RGB base color; alpha participates in the selected opacity/coverage path |
| `_ShadeTex` | RGB non-skin shadow color; A blends from non-skin shading to skin shading |
| `_DefTex` | R shifts toon shading and limits rim; G is smoothness; B is metallic except on the face branch; A masks specular/highlight visibility |
| `_RampTex` | RGB skin shading multiplier; A interpolates non-skin base/shadow color; sampled at `(lightingCoordinate, 0)` |
| `_RampAddTex` | RGB view-dependent material color; A distributes it between additive diffuse and multiplicative specular |
| `_HighlightTex` | Painted hair highlight RGB, subject to its angular gate and Def.A; accessories keep the ordinary specular path |

The original lecture gives 1024×4 for the shading ramp and 128×16 for the material ramp. The shader accepts other valid texture dimensions. The material-ramp row comes from the decoded low nibble of vertex COLOR.G, divided by 15 and interpolated over the triangle. `ActorVertexEncoding` owns the packing contract; vertex color is not ordinary albedo tint.

## Sampling and composition

Base, Shade, Def and hair Highlight use the transformed main UV and `_CapturedActorTextureLodBias`. `_ActorTextureFrame` can additionally select an atlas region. A UV2 layer is a separate opt-in path. Texture filter, wrap mode, mip contents and color-space import flags are inputs: the shader does not infer or repair them from filenames. In particular, bilinear Repeat at ramp Y=0 blends the first and last rows. Use Clamp or matching guard rows when that is not intended.

`filterMode = Bilinear` alone does not establish a bilinear sampler under forced anisotropy. Set `anisoLevel = 0` on lookup textures when that exact contract is intended; Unity documents that zero also opts out of forced anisotropic filtering. [Texture.anisoLevel](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture-anisoLevel.html). Anisotropic filtering can alter dependent ramp samples near a wrapped Def seam, even on a front-facing plane. Choose it deliberately rather than treating its output as a bilinear-equation regression.

The material ramp uses `saturate(2 * Def.R - 1 + dot(worldNormal, view))` for X. Keep the signed dot product until after the offset. Let its sampled color be T and alpha be a. The diffuse addition is `T.rgb * tint * (1-a)`, applied to both base and shade RGB before the skin/non-skin blend. The specular multiplier is `lerp(1, T.rgb * tint, a)`. Using the premultiplied diffuse addition as the specular multiplier would incorrectly erase reflections at a=1.

Skin shading multiplies base RGB by ramp RGB; non-skin shading interpolates base and shade RGB using ramp alpha. Shade alpha blends those two responses, including on mixed skin/clothing materials. The runtime's shade tint and strength affect both paths. `_Color` is a literal vector applied after authored diffuse composition, not a ShaderLab Color conversion. `_RampAddColor` is a ShaderLab Color property; color-space conversion therefore needs to be considered separately.

The face branch uses Def.B to blend an additional head-basis lighting response and has no metallic diffuse suppression. Other ordinary materials use Def.B as metallic. The hair strand region uses its painted highlight before diffuse composition; only the upper UV accessory region retains the extra ordinary specular lobe. These are explicit surface contracts, not generic PBR assumptions.

## Verification boundary

The public Player fixture `ActorRenderingSelfTest.ActorMaps.cs` contains authored maps and an independent CPU addressing/material equation. It checks full float images at intermediate diffuse/specular stages and the actual final ActorToon output, with explicit sampler and mip cases. Test execution evidence and any failures remain separate from the fixture source.

Hardware bilinear filtering need not match two floating-point CPU lerps. The fixture selects an explicit NVIDIA/D3D11 fixed-point profile before rendering: 8 fractional bits for coordinates and the cross weight, with constant/linear marginals preserved. This profile was isolated from native sampler state, exact input texels and shader sample traces; it is not fitted to the final image. Other devices use ideal bilinear expectations and require their own native precision validation. A mismatch must be diagnosed as a sampler-contract or material-equation difference before treating it as a rendering regression. Neither profile changes production sampling or chooses whichever expected image is closer to the GPU output.

This does not establish every production material, compressed/sRGB texture import, arbitrary anisotropic filtering, translucent hair overlap, all costumes or mobile driver performance. Defaults in PhotoStudio are unchanged.
