using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyBakedMask(Report report)
        {
            yield return null;
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "baked-mask-" + name, ok, error);
            var previous = FindObjectsOfType<Renderer>(); var forced = previous.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in previous) r.forceRenderingOff = true;
            var active = RenderTexture.active; var oldMaps = LightmapSettings.lightmaps;
            using var low = new LowResolutionFxRenderer(); using var heavy = new HeavyFxRenderer();
            try
            {
                const int width = 37, height = 29;
                var host = Own(new GameObject("Independent packed baked shadow host")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
                camera.orthographic = true; camera.orthographicSize = 1; camera.aspect = (float)width / height;
                camera.nearClipPlane = .1f; camera.farClipPlane = 20; camera.transform.position = new Vector3(0, 0, -3);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.03f, .05f, .07f, .61f); camera.cullingMask = 0;
                RenderTexture Target(RenderTextureFormat format, int bits = 0)
                { var rt = Own(new RenderTexture(width, height, bits, format, RenderTextureReadWrite.Linear)); rt.Create(); return rt; }
                var target = Target(RenderTextureFormat.ARGBFloat, 24); target.name = "Baked shadow current final output"; camera.targetTexture = target;
                var source = Target(RenderTextureFormat.ARGBFloat); var depth = Target(RenderTextureFormat.RFloat);
                var depthUpload = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); depthUpload.SetPixel(0, 0, new Color(15, 0, 0, 0)); depthUpload.Apply(); Graphics.Blit(depthUpload, depth);
                camera.Render(); Graphics.Blit(target, source);
                var go = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.layer = 25; go.transform.localScale = new Vector3(camera.aspect * 2, 2, 1);
                var renderer = go.GetComponent<Renderer>(); var mesh = Own(Instantiate(go.GetComponent<MeshFilter>().sharedMesh));
                // Avoid exact point-filter discontinuities at odd-target pixel
                // centers. Every output pixel is still checked, without an edge exclusion.
                mesh.uv2 = mesh.uv.Select(uv => new Vector2(.013f + .913f * uv.x, .017f + .937f * uv.y)).ToArray();
                go.GetComponent<MeshFilter>().sharedMesh = mesh; var originalMaterial = renderer.sharedMaterial;
                var material = new SceneDeferredCamera.MaterialInputs { albedo = new Vector3(.5f, .25f, .75f), mos = new Vector3(.125f, .75f, .5f) };
                var baked = new SceneBakedShadowInput();
                var gi = new SceneGiInput(); gi.probe.AddAmbientLight(new Color(.125f, .25f, .375f));
                var surface = new SceneDeferredCamera.Surface { renderer = renderer, cull = CullMode.Off, inputs = material, bakedShadow = baked, gi = gi };
                var deferred = host.AddComponent<SceneDeferredCamera>(); deferred.sceneLayers = 1 << 25; deferred.surfaces = new[] { surface };
                var forward = host.AddComponent<SceneForwardLightingCamera>(); forward.surfaceLayers = 1 << 25;
                var forwardSurface = new SceneForwardSurface { renderer = renderer, inputs = material, bakedShadow = baked, gi = gi, cull = CullMode.Off };
                forward.settings.surfaces = new[] { forwardSurface };
                var fx = new LowResolutionFxSurface { renderer = renderer, fog = false, opacity = 1, cull = CullMode.Off,
                    lighting = new FxSurfaceLighting { enabled = true, inputs = material, bakedShadow = baked, gi = gi } };
                var geometry = new LowResolutionFxSettings { enabled = true, surfaces = new[] { fx }, lighting = forward.settings };
                var joint = new HeavyFxSettings { enabled = true, geometry = geometry };
                SceneDeferredCamera.Frame frame = default;
                Color[] Deferred()
                {
                    forward.enabled = false; deferred.enabled = true; deferred.sceneEnabled = true; camera.Render();
                    if (!deferred.TryGetFrame(out frame)) throw new InvalidOperationException(deferred.UnavailableReason);
                    return ReadSceneTarget(target);
                }
                Color[] Draw(int consumer, FxResolution resolution = FxResolution.Full, bool explicitMesh = false)
                {
                    if (consumer == 0) return Deferred();
                    deferred.enabled = false; forward.enabled = consumer == 1; forward.settings.enabled = true;
                    if (consumer == 1) { camera.Render(); if (forward.UnavailableReason != null) throw new InvalidOperationException(forward.UnavailableReason); return ReadSceneTarget(target); }
                    fx.resolution = resolution; fx.renderer = explicitMesh ? null : renderer; fx.mesh = explicitMesh ? mesh : null; fx.localToWorld = go.transform.localToWorldMatrix;
                    if (consumer == 2)
                    {
                        if (!low.TryRender(source, new FogVolumeDepth(depth), camera, geometry, out var f)) throw new InvalidOperationException(low.UnavailableReason);
                        return ReadSceneTarget(f.color);
                    }
                    if (!heavy.TryRender(source, new FogVolumeDepth(depth), camera, joint, 0, out var h)) throw new InvalidOperationException(heavy.UnavailableReason);
                    return ReadSceneTarget(h.color);
                }
                // Independent recursive2x2 rank, rather than calling the public
                // pack helper or copying the shader's16-entry table.
                int Rank2(int x, int y) => y == 0 ? x * 2 : x == 0 ? 3 : 1;
                int Rank(int x, int y) => 4 * Rank2(x & 1, y & 1) + Rank2((x >> 1) & 1, (y >> 1) & 1);
                int Quantize(float value, int levels, float threshold)
                {
                    double scaled = (double)value * levels; int whole = (int)Math.Floor(scaled);
                    return Math.Min(levels, whole + (scaled - whole >= 1 - threshold ? 1 : 0));
                }
                Vector4 Expected(Vector4 value, int x, int y, bool dither)
                {
                    // Current camera GPU projection already accounts for RT Y.
                    // The readback row is the shader pixel row; do not flip twice.
                    int shaderY = y;
                    float threshold = dither ? (Rank(x, shaderY) + .5f) / 16 : .5f;
                    return new Vector4(Quantize(value.x, 255, .5f) / 255f, Quantize(value.y, 7, threshold) / 7f, Quantize(value.z, 7, threshold) / 7f, Quantize(value.w, 3, threshold) / 3f);
                }
                int Code(Vector4 q) => Mathf.RoundToInt(q.y * 7) * 32 + Mathf.RoundToInt(q.z * 7) * 4 + Mathf.RoundToInt(q.w * 3);
                Color[] disabled = Deferred(); Check("default-no-packed-target", deferred.BakedShadowTargetCount == 0 && frame.bakedShadowMask == null);
                baked.source = SceneBakedShadowSource.Constant;
                for (int code = 0; code < 256; code++)
                {
                    var q = new Vector3(code / 32 / 7f, (code / 4 % 8) / 7f, (code % 4) / 3f);
                    Check("public-byte-roundtrip-" + code, SceneBakedShadowEncoding.PackGba(q, -3, 9) == code && SceneBakedShadowEncoding.UnpackGba((byte)code) == q);
                    baked.visibility = new Vector4((255 - code) / 255f, q.x, q.y, q.z); Deferred();
                    var pixels = ReadSceneTarget(frame.bakedShadowMask);
                    Check("actual-all-pixels-byte-" + code, pixels.All(p => Mathf.RoundToInt(p.r * 255) == 255 - code && Mathf.RoundToInt(p.g * 255) == code));
                }
                Check("actual-packed-format-and-mrt", frame.bakedShadowMask.graphicsFormat == GraphicsFormat.R8G8_UNorm && frame.bakedShadowMask.filterMode == FilterMode.Point && deferred.GiTargetCount == 0 && deferred.BakedShadowTargetCount == 1);
                baked.visibility = new Vector4(.37f, .43f, .74f, .38f);
                foreach (bool dither in new[] { false, true })
                {
                    baked.dither = dither; Deferred(); var pixels = ReadSceneTarget(frame.bakedShadowMask); int differences = 0;
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    {
                        var q = Expected(baked.visibility, x, y, dither); var p = pixels[y * width + x];
                        if (Mathf.RoundToInt(p.r * 255) != Mathf.RoundToInt(q.x * 255) || Mathf.RoundToInt(p.g * 255) != Code(q)) differences++;
                    }
                    Check("independent-dither-entire-target-" + dither, differences == 0, differences);
                    if (dither) Debug.Log("[BakedMaskDither] current first4x4 packed bytes=" + string.Join(",", Enumerable.Range(0, 16).Select(i => Mathf.RoundToInt(pixels[(i / 4) * width + i % 4].g * 255))));
                    Deferred(); Check("dither-repeat-no-time-seed-" + dither, PixelError(pixels, ReadSceneTarget(frame.bakedShadowMask)) == 0);
                }
                var map = Own(new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true)); map.filterMode = FilterMode.Point; map.wrapMode = TextureWrapMode.Clamp;
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) map.SetPixel(x, y, new Color(x / 7f, y / 7f, (x ^ y) / 7f, ((x + y) % 4) / 3f)); map.Apply();
                baked.source = SceneBakedShadowSource.Texture; baked.texture = map;
                foreach (var st in new[] { new Vector4(1, 1, 0, 0), new Vector4(.75f, .5f, .125f, .25f), new Vector4(-1, 1, 1, 0) })
                {
                    baked.uvST = st; Deferred(); var pixels = ReadSceneTarget(frame.bakedShadowMask); int differences = 0;
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    {
                        int tx = Mathf.Clamp((int)Math.Floor(((.013 + .913 * (x + .5) / width) * st.x + st.z) * 8), 0, 7);
                        int ty = Mathf.Clamp((int)Math.Floor(((.017 + .937 * (y + .5) / height) * st.y + st.w) * 8), 0, 7);
                        Color c = map.GetPixel(tx, ty); var q = Expected(new Vector4(c.r, c.g, c.b, c.a), x, y, baked.dither); var p = pixels[y * width + x];
                        if (Mathf.RoundToInt(p.r * 255) != Mathf.RoundToInt(q.x * 255) || Mathf.RoundToInt(p.g * 255) != Code(q)) differences++;
                    }
                    Check("uv2-current-ST-" + st, differences == 0, differences);
                }
                var explicitMap = ReadSceneTarget(frame.bakedShadowMask);
                LightmapSettings.lightmaps = new[] { new LightmapData(), new LightmapData { shadowMask = map } };
                renderer.lightmapIndex = 1; renderer.lightmapScaleOffset = baked.uvST; baked.source = SceneBakedShadowSource.RendererLightmap;
                Deferred(); Check("actual-renderer-lightmap-binding", PixelError(explicitMap, ReadSceneTarget(frame.bakedShadowMask)) == 0);
                baked.source = SceneBakedShadowSource.Constant; baked.dither = false;
                var lights = Enumerable.Range(0, 4).Select(i => new SceneDecalLight { shape = (SceneDecalLightShape)i,
                    position = new Vector3((i - 1.5f) * .2f, .15f, -1.5f), range = 5, halfLength = .4f,
                    halfSize = Vector2.one * 2, areaSpread = Vector2.one, spotInnerAngle = 80, spotOuterAngle = 130,
                    radiance = Vector3.zero, diffuseScale = .7f, specularScale = .4f, backlightScale = .2f, giWeight = .3f }).ToArray();
                var settings = forward.settings; settings.localLights = deferred.decalLighting = new SceneDecalLightSettings { enabled = true, lights = lights };
                deferred.ambientIrradiance = settings.ambientIrradiance = Vector3.zero;
                deferred.lightRadiance = settings.lightRadiance = Vector3.zero;
                gi.source = SceneGiSource.Probe; material.emission = new Vector3(.125f, .0625f, .03125f);
                var value = new Vector4(.2f, .43f, .74f, .38f); baked.visibility = value;
                foreach (int consumer in new[] { 0, 1, 2, 3 })
                foreach (var resolution in consumer < 2 ? new[] { FxResolution.Full } : new[] { FxResolution.Full, FxResolution.Half, FxResolution.Quarter })
                foreach (bool explicitMesh in consumer < 2 ? new[] { false } : new[] { false, true })
                {
                    if (consumer == 0) deferred.decalLighting.backend = SceneDecalLightBackend.Instanced;
                    string label = consumer + "-" + resolution + "-mesh-" + explicitMesh;
                    Color[] Render() => Draw(consumer, resolution, explicitMesh);
                    foreach (var light in lights) { light.radiance = Vector3.zero; light.bakedShadowChannel = SceneBakedShadowChannel.None; }
                    deferred.lightRadiance = settings.lightRadiance = Vector3.zero;
                    deferred.mainBakedShadowChannel = settings.mainBakedShadowChannel = SceneBakedShadowChannel.None;
                    var indirect = Render(); var contributions = new Color[5][];
                    deferred.lightRadiance = settings.lightRadiance = new Vector3(.8f, .6f, .4f); contributions[0] = Render();
                    deferred.lightRadiance = settings.lightRadiance = Vector3.zero;
                    for (int light = 0; light < 4; light++)
                    {
                        lights[light].radiance = new Vector3(.5f, .75f, .25f); contributions[light + 1] = Render(); lights[light].radiance = Vector3.zero;
                    }
                    deferred.lightRadiance = settings.lightRadiance = new Vector3(.8f, .6f, .4f);
                    deferred.mainBakedShadowChannel = settings.mainBakedShadowChannel = SceneBakedShadowChannel.R;
                    for (int light = 0; light < 4; light++) { lights[light].radiance = new Vector3(.5f, .75f, .25f); lights[light].bakedShadowChannel = (SceneBakedShadowChannel)(light + 1); }
                    var actual = Render(); var expected = new Color[actual.Length];
                    Vector4 visibility = consumer == 0 ? Expected(value, 0, 0, false) : value;
                    for (int p = 0; p < actual.Length; p++)
                    {
                        expected[p] = indirect[p];
                        for (int channel = 0; channel < 3; channel++)
                        {
                            expected[p][channel] += (contributions[0][p][channel] - indirect[p][channel]) * visibility.x;
                            for (int light = 0; light < 4; light++) expected[p][channel] += (contributions[light + 1][p][channel] - indirect[p][channel]) * visibility[light];
                        }
                    }
                    // Joint FX's color-edge repair decision is nonlinear. The
                    // independent reference must scale the unmasked light inputs
                    // BEFORE that decision, not sum differently repaired images.
                    if (consumer == 3 && resolution != FxResolution.Full)
                    {
                        var savedSource = baked.source; baked.source = SceneBakedShadowSource.None;
                        settings.mainBakedShadowChannel = SceneBakedShadowChannel.None; settings.lightRadiance *= visibility.x;
                        for (int light = 0; light < 4; light++) { lights[light].bakedShadowChannel = SceneBakedShadowChannel.None; lights[light].radiance *= visibility[light]; }
                        expected = Render(); baked.source = savedSource;
                        settings.mainBakedShadowChannel = SceneBakedShadowChannel.R; settings.lightRadiance = new Vector3(.8f, .6f, .4f);
                        for (int light = 0; light < 4; light++) { lights[light].bakedShadowChannel = (SceneBakedShadowChannel)(light + 1); lights[light].radiance = new Vector3(.5f, .75f, .25f); }
                    }
                    float error = PixelError(actual, expected); Check("independent-five-lights-GI-emission-" + label, error <= .00005f, error);
                    Check("masked-light-positive-" + label, PixelError(actual, indirect) > .01f);
                    if (consumer == 0) deferred.decalLighting.backend = SceneDecalLightBackend.Scalar; else settings.backend = SceneForwardLightBackend.BruteForce;
                    var other = Render(); Check("scalar-instanced-or-tiled-brute-" + label, PixelError(actual, other) <= .00002f, PixelError(actual, other));
                    deferred.decalLighting.backend = SceneDecalLightBackend.Instanced; settings.backend = SceneForwardLightBackend.Tiled;
                    bool capture = consumer < 2 || consumer == 2 && resolution == FxResolution.Quarter && !explicitMesh || consumer == 3 && resolution == FxResolution.Half && explicitMesh;
                    if (capture && Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_BAKED_MASK") == "1")
                    {
                        FsrCaptureDrain(target); bool began = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                        try { Render(); FsrCaptureDrain(target); } finally { if (began) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                        Check("requested-native-capture-" + label, began && ended);
                    }
                    if (consumer < 2 || resolution == FxResolution.Quarter && !explicitMesh)
                    { SaveSsrPreview("baked-mask-actual-" + consumer, actual, width, height, false); SaveSsrPreview("baked-mask-independent-" + consumer, expected, width, height, false); }
                }
                // PCF's fractional visibility distinguishes min-union from an
                // accidental second multiplication. Derive visibility from an
                // independently rendered unmasked/current-shadow pair.
                gi.source = SceneGiSource.None; material.emission = Vector3.zero;
                var blocker = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); blocker.layer = 24;
                blocker.transform.position = new Vector3(.07f, .11f, -.65f); blocker.transform.localScale = new Vector3(.55f, .65f, 1);
                var caster = new SceneShadowCaster { renderer = blocker.GetComponent<Renderer>(), cull = CullMode.Off };
                var point = lights[0]; point.position = new Vector3(0, 0, -1.5f); point.shadow.filter = SceneShadowFilter.Pcf3x3;
                settings.localLights.lights = new[] { point }; settings.localLights.shadows.tileResolution = 64; settings.localLights.shadows.casters = new[] { caster };
                var main = new SceneDirectionalShadowSettings { origin = new Vector3(0, 0, -3), halfSize = Vector2.one * 2,
                    nearPlane = .05f, farPlane = 6, resolution = 64, filter = SceneShadowFilter.Pcf3x3, casters = new[] { caster } };
                deferred.mainLightShadow = settings.mainLightShadow = main;
                foreach (int shadowKind in new[] { 0, 1, 2 })
                foreach (int consumer in new[] { 0, 1, 2, 3 })
                {
                    bool directional = shadowKind == 2; point.shape = shadowKind == 1 ? SceneDecalLightShape.Spot : SceneDecalLightShape.Point;
                    point.radiance = directional ? Vector3.zero : Vector3.one;
                    settings.lightRadiance = deferred.lightRadiance = directional ? Vector3.one : Vector3.zero;
                    point.bakedShadowChannel = SceneBakedShadowChannel.R; settings.mainBakedShadowChannel = deferred.mainBakedShadowChannel = SceneBakedShadowChannel.R;
                    baked.source = SceneBakedShadowSource.None; point.shadow.enabled = main.enabled = false;
                    var clear = Draw(consumer); if (directional) main.enabled = true; else point.shadow.enabled = true;
                    var dynamic = Draw(consumer); baked.source = SceneBakedShadowSource.Constant; baked.visibility = Vector4.one * .43f;
                    var actual = Draw(consumer); var expected = new Color[actual.Length]; var wrongProduct = new Color[actual.Length]; int fractional = 0;
                    float mask = consumer == 0 ? Expected(baked.visibility, 0, 0, false).x : .43f;
                    for (int p = 0; p < actual.Length; p++)
                    {
                        int best = clear[p].r >= clear[p].g && clear[p].r >= clear[p].b ? 0 : clear[p].g >= clear[p].b ? 1 : 2;
                        float visibility = clear[p][best] > .00001f ? Mathf.Clamp01(dynamic[p][best] / clear[p][best]) : 1;
                        if (visibility > .05f && visibility < .95f) fractional++;
                        expected[p] = wrongProduct[p] = clear[p];
                        for (int c = 0; c < 3; c++) { expected[p][c] *= Mathf.Min(mask, visibility); wrongProduct[p][c] *= mask * visibility; }
                    }
                    float error = PixelError(actual, expected);
                    Check("dynamic-PCF-min-union-" + shadowKind + "-" + consumer, error <= .00005f, error);
                    Check("fractional-PCF-reject-double-multiply-" + shadowKind + "-" + consumer, fractional > 0 && PixelError(actual, wrongProduct) > .01f);
                }
                point.shadow.enabled = main.enabled = false;
                forward.enabled = false; deferred.enabled = true; deferred.sceneEnabled = true;
                void Reject(string label)
                {
                    camera.Render(); Check("reject-" + label, deferred.UnavailableReason != null && !deferred.TryGetFrame(out _) && deferred.BakedShadowTargetCount == 0);
                }
                baked.source = (SceneBakedShadowSource)99; Reject("unknown-source");
                baked.source = SceneBakedShadowSource.Constant; baked.visibility = new Vector4(float.NaN, 1, 1, 1); Reject("nonfinite-constant");
                baked.visibility = Vector4.one; baked.source = SceneBakedShadowSource.Texture; baked.texture = null; Reject("missing-texture");
                var nonlinear = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, false)); baked.texture = nonlinear; Reject("srgb-mask");
                baked.texture = map; var uv2 = mesh.uv2; mesh.uv2 = Array.Empty<Vector2>(); Reject("missing-UV2"); mesh.uv2 = uv2;
                baked.source = SceneBakedShadowSource.RendererLightmap; renderer.lightmapIndex = 99; Reject("missing-current-lightmap-index"); renderer.lightmapIndex = 1;
                baked.source = SceneBakedShadowSource.Constant; Deferred(); var beforeAlias = frame;
                baked.source = SceneBakedShadowSource.Texture; baked.texture = frame.bakedShadowMask; Reject("owned-target-feedback");
                Check("feedback-rejection-invalidates-borrowed-frame", !beforeAlias.IsCurrent);
                baked.source = SceneBakedShadowSource.None; baked.visibility = new Vector4(float.NaN, 0, 0, 0); baked.texture = nonlinear;
                Deferred(); Check("disabled-ignores-unused-invalid-inputs", deferred.UnavailableReason == null && frame.bakedShadowMask == null);
                baked.source = SceneBakedShadowSource.Constant; baked.visibility = Vector4.one; Deferred(); var beforeResize = frame;
                var resized = Own(new RenderTexture(53, 31, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); resized.Create(); camera.targetTexture = resized;
                Deferred(); Check("resize-current-packed-attachment", !beforeResize.IsCurrent && frame.bakedShadowMask.width == 53 && frame.bakedShadowMask.height == 31);
                frame.bakedShadowMask.Release(); Check("lost-packed-target-invalidates-frame", !frame.IsCurrent); Deferred(); Check("lost-packed-target-recreated", frame.bakedShadowMask.IsCreated());
                camera.targetTexture = target;
                baked.source = SceneBakedShadowSource.None; gi.source = SceneGiSource.None; material.emission = Vector3.zero;
                settings.localLights.enabled = false; deferred.lightRadiance = Vector3.one; deferred.ambientIrradiance = Vector3.one * .1f;
                var beforeDisable = frame; var restored = Deferred();
                Check("disabled-restores-default-pixels-and-releases", PixelError(disabled, restored) == 0 && deferred.BakedShadowTargetCount == 0 && !beforeDisable.IsCurrent);

                // A nearer unmasked cutout must write white in the optional MRT;
                // rejected fragments must retain the farther receiver's own mask.
                baked.source = SceneBakedShadowSource.Constant; baked.visibility = new Vector4(.2f, 2 / 7f, 5 / 7f, 1 / 3f);
                var rear = Deferred(); var rearMask = ReadSceneTarget(frame.bakedShadowMask);
                var front = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); front.layer = 25;
                front.transform.position = new Vector3(0, 0, -.2f); front.transform.localScale = new Vector3(camera.aspect * 2, 2, 1);
                var cutout = Own(new Texture2D(2, 1, TextureFormat.RGBAFloat, false, true)); cutout.filterMode = FilterMode.Point; cutout.wrapMode = TextureWrapMode.Clamp;
                cutout.SetPixels(new[] { new Color(1, 1, 1, 0), Color.white }); cutout.Apply();
                var unmasked = new SceneDeferredCamera.Surface { renderer = front.GetComponent<Renderer>(), cull = CullMode.Off, alphaCutoff = .5f };
                unmasked.inputs.albedoMap = cutout; deferred.surfaces = new[] { surface, unmasked }; Deferred(); var overlap = ReadSceneTarget(frame.bakedShadowMask);
                Check("near-unmasked-cutout-writes-white", overlap[width * (height / 2) + width * 3 / 4].r == 1 && overlap[width * (height / 2) + width * 3 / 4].g == 1);
                Check("cutout-rejection-retains-rear-mask", overlap[width * (height / 2) + width / 4] == rearMask[width * (height / 2) + width / 4]);
                deferred.surfaces = new[] { unmasked, surface }; Deferred(); Check("current-depth-not-submission-order", PixelError(overlap, ReadSceneTarget(frame.bakedShadowMask)) == 0);
                deferred.surfaces = new[] { surface }; front.SetActive(false);

                // Compare current native bone/blendshape UV2 to an independently
                // transformed static reference; do not use BakeMesh as an oracle.
                var originalRenderer = renderer; var originalVertices = mesh.vertices;
                var skinHost = Own(new GameObject("Current baked mask skin")); skinHost.layer = 25; skinHost.transform.localScale = go.transform.localScale;
                var skin = skinHost.AddComponent<SkinnedMeshRenderer>(); var skinMesh = Own(Instantiate(mesh));
                skinMesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray(); skinMesh.bindposes = new[] { Matrix4x4.identity };
                var delta = new[] { new Vector3(.17f, .07f, 0), Vector3.zero, new Vector3(-.06f, .11f, 0), Vector3.zero };
                skinMesh.AddBlendShapeFrame("Baked mask current deformation", 100, delta, new Vector3[4], new Vector3[4]);
                var bone = Own(new GameObject("Baked mask current bone")).transform; bone.SetParent(skinHost.transform, false);
                skin.sharedMesh = skinMesh; skin.bones = new[] { bone }; skin.rootBone = bone; skin.sharedMaterial = originalMaterial;
                skin.updateWhenOffscreen = true; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 20);
                void BindReceiver(Renderer r) { renderer = r; surface.renderer = r; forwardSurface.renderer = r; fx.renderer = r; }
                baked.source = SceneBakedShadowSource.Texture; baked.texture = map; baked.uvST = new Vector4(1, 1, 0, 0); map.filterMode = FilterMode.Bilinear;
                settings.mainBakedShadowChannel = deferred.mainBakedShadowChannel = SceneBakedShadowChannel.G;
                settings.ambientIrradiance = deferred.ambientIrradiance = Vector3.zero; settings.lightRadiance = deferred.lightRadiance = Vector3.one;
                Color[] firstSkin = null;
                for (int pose = 0; pose < 3; pose++)
                {
                    bone.localPosition = new Vector3(pose * .05f, pose * -.03f, 0); bone.localRotation = Quaternion.Euler(0, 0, pose * 13); skin.SetBlendShapeWeight(0, pose * 40);
                    var transform = Matrix4x4.TRS(bone.localPosition, bone.localRotation, Vector3.one);
                    mesh.vertices = originalVertices.Select((p, i) => transform.MultiplyPoint3x4(p + delta[i] * (pose * .4f))).ToArray(); mesh.RecalculateBounds();
                    yield return null; yield return null;
                    foreach (int consumer in new[] { 0, 1, 2, 3 })
                    {
                        BindReceiver(skin); var actual = Draw(consumer); BindReceiver(originalRenderer); var expected = Draw(consumer);
                        float error = PixelError(actual, expected); Check("current-native-UV2-skin-" + pose + "-" + consumer, error <= .00005f, error);
                        if (consumer == 1) { if (firstSkin == null) firstSkin = actual; else Check("native-pose-changes-current-mask-" + pose, PixelError(firstSkin, actual) > .01f); }
                        if (pose == 2 && Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_BAKED_MASK") == "1")
                        {
                            BindReceiver(skin); FsrCaptureDrain(target); bool began = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                            try { Draw(consumer); FsrCaptureDrain(target); } finally { if (began) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                            Check("requested-native-UV2-skin-capture-" + consumer, began && ended);
                        }
                    }
                }
                BindReceiver(originalRenderer); mesh.vertices = originalVertices; mesh.RecalculateBounds(); skinHost.SetActive(false);
                Check("borrowed-material-and-input-texture-preserved", renderer.sharedMaterial == originalMaterial && map != null && map.GetPixel(7, 7).r == 1);
            }
            finally
            {
                LightmapSettings.lightmaps = oldMaps; RenderTexture.active = active;
                for (int i = 0; i < previous.Length; i++) if (previous[i]) previous[i].forceRenderingOff = forced[i];
                foreach (var item in _owned) if (item) DestroyImmediate(item); _owned.Clear();
            }
            string bundle = Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_BAKED_MASK_BUNDLE");
            if (!string.IsNullOrEmpty(bundle))
            {
                // Drive the child here so the owning report catches failures;
                // yielding a nested IEnumerator lets Unity swallow exceptions.
                var fixture = VerifyRealBakedMask(report, bundle);
                try { while (fixture.MoveNext()) yield return fixture.Current; }
                finally { (fixture as IDisposable)?.Dispose(); }
            }
        }

        private IEnumerator VerifyRealBakedMask(Report report, string bundlePath)
        {
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "real-baked-mask-" + name, ok, error);
            var original = SceneManager.GetActiveScene(); int oldMapCount = LightmapSettings.lightmaps.Length;
            var active = RenderTexture.active; var bundle = AssetBundle.LoadFromFile(System.IO.Path.GetFullPath(bundlePath));
            if (bundle == null) throw new InvalidOperationException("Requested actual shadowmask bundle is missing");
            Scene loaded = default; var means = new float[2, 4];
            try
            {
                bool Occluded(string path) => path.IndexOf("/Occluded/", StringComparison.OrdinalIgnoreCase) >= 0;
                var paths = bundle.GetAllScenePaths().OrderBy(p => Occluded(p) ? 0 : 1).ToArray();
                if (paths.Length != 2 || !Occluded(paths[0]) || paths[1].IndexOf("/Clear/", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("Expected authored occluded and clear bake scenes");
                for (int variant = 0; variant < paths.Length; variant++)
                {
                    yield return SceneManager.LoadSceneAsync(paths[variant], LoadSceneMode.Additive); loaded = SceneManager.GetSceneByPath(paths[variant]);
                    SceneManager.SetActiveScene(loaded);
                    var renderers = loaded.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Renderer>()).ToArray();
                    var floor = renderers.Single(r => r.name == "BakedMaskFloor"); foreach (var r in renderers) r.gameObject.layer = 25;
                    var lamps = loaded.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Light>()).OrderBy(l => l.name).ToArray();
                    Check("actual-four-distinct-mixed-channels-" + variant, lamps.Length == 4 && lamps.All(l => l.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed && l.color == Color.white) &&
                        lamps.Select(l => l.bakingOutput.occlusionMaskChannel).Distinct().Count() == 4 && lamps.All(l => l.bakingOutput.occlusionMaskChannel >= 0 && l.bakingOutput.occlusionMaskChannel < 4));
                    foreach (var lamp in lamps) lamp.enabled = false;
                    var map = LightmapSettings.lightmaps[floor.lightmapIndex].shadowMask;
                    Check("actual-linear-shadowmap-" + variant, map != null && map.graphicsFormat == GraphicsFormat.R8G8B8A8_UNorm);
                    if (map == null) throw new InvalidOperationException("Baked scene lost its real shadowmask");
                    const int width = 65, height = 57;
                    var host = Own(new GameObject("Actual Mixed Shadowmask consumer")); var camera = host.AddComponent<Camera>();
                    camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward; camera.cullingMask = 0;
                    camera.orthographic = true; camera.orthographicSize = 1.55f; camera.aspect = (float)width / height; camera.nearClipPlane = .1f; camera.farClipPlane = 20;
                    camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                    camera.transform.SetPositionAndRotation(new Vector3(0, 6, 0), Quaternion.Euler(90, 0, 0));
                    var target = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                    var source = Own(new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); source.Create(); camera.Render(); Graphics.Blit(target, source);
                    var depth = Own(new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)); depth.Create();
                    var upload = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); upload.SetPixel(0, 0, new Color(15, 0, 0, 0)); upload.Apply(); Graphics.Blit(upload, depth);
                    var readback = Own(new RenderTexture(map.width, map.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); readback.Create(); Graphics.Blit(map, readback);
                    var texels = ReadSceneTarget(readback); var mesh = floor.GetComponent<MeshFilter>().sharedMesh; var cpu = new Color[width * height]; var st = floor.lightmapScaleOffset;
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    {
                        var uv = BakedGiRayUv(mesh, floor.transform, camera.ViewportPointToRay(new Vector3((x + .5f) / width, (y + .5f) / height, 0)));
                        cpu[y * width + x] = BakedGiBilinear(texels, map.width, map.height, Vector2.Scale(uv, new Vector2(st.x, st.y)) + new Vector2(st.z, st.w));
                    }
                    var baked = new SceneBakedShadowInput { source = SceneBakedShadowSource.RendererLightmap, dither = false };
                    var material = new SceneDeferredCamera.MaterialInputs { albedo = new Vector3(.4f, .6f, .2f), mos = new Vector3(.1f, 1, .3f) };
                    var deferred = host.AddComponent<SceneDeferredCamera>(); deferred.sceneEnabled = true; deferred.sceneLayers = 1 << 25; deferred.lightRadiance = deferred.ambientIrradiance = Vector3.zero;
                    deferred.surfaces = new[] { new SceneDeferredCamera.Surface { renderer = floor, cull = CullMode.Off, bakedShadow = baked, inputs = material } };
                    var forward = host.AddComponent<SceneForwardLightingCamera>(); forward.surfaceLayers = 1 << 25; forward.settings.enabled = true;
                    var settings = forward.settings; settings.lightRadiance = settings.ambientIrradiance = Vector3.zero;
                    settings.surfaces = new[] { new SceneForwardSurface { renderer = floor, cull = CullMode.Off, bakedShadow = baked, inputs = material } };
                    var light = new SceneDecalLight { shape = SceneDecalLightShape.Point, range = 9, radiance = Vector3.one };
                    settings.localLights = deferred.decalLighting = new SceneDecalLightSettings { enabled = true, lights = new[] { light } };
                    var fx = new LowResolutionFxSettings { enabled = true, lighting = settings, surfaces = new[] { new LowResolutionFxSurface {
                        renderer = floor, cull = CullMode.Off, opacity = 1, fog = false, resolution = FxResolution.Full,
                        lighting = new FxSurfaceLighting { enabled = true, inputs = material, bakedShadow = baked } } } };
                    var joint = new HeavyFxSettings { enabled = true, geometry = fx };
                    using var low = new LowResolutionFxRenderer(); using var heavy = new HeavyFxRenderer(); SceneDeferredCamera.Frame frame = default;
                    Color[] Draw(int consumer)
                    {
                        deferred.enabled = consumer == 0; forward.enabled = consumer == 1;
                        if (consumer < 2)
                        {
                            camera.Render();
                            if (consumer == 0 && !deferred.TryGetFrame(out frame)) throw new InvalidOperationException(deferred.UnavailableReason);
                            if (consumer == 1 && forward.UnavailableReason != null) throw new InvalidOperationException(forward.UnavailableReason);
                            return ReadSceneTarget(target);
                        }
                        if (consumer == 2) { if (!low.TryRender(source, new FogVolumeDepth(depth), camera, fx, out var f)) throw new InvalidOperationException(low.UnavailableReason); return ReadSceneTarget(f.color); }
                        if (!heavy.TryRender(source, new FogVolumeDepth(depth), camera, joint, 0, out var h)) throw new InvalidOperationException(heavy.UnavailableReason); return ReadSceneTarget(h.color);
                    }
                    for (int lamp = 0; lamp < 4; lamp++)
                    {
                        int channel = lamps[lamp].bakingOutput.occlusionMaskChannel; light.position = lamps[lamp].transform.position; light.bakedShadowChannel = (SceneBakedShadowChannel)(channel + 1);
                        means[variant, lamp] = cpu.Average(p => p[channel]);
                        Check("authored-occluder-causal-input-" + variant + "-" + lamp, variant == 0 ? cpu.Min(p => p[channel]) < .1f && means[variant, lamp] < .98f : cpu.Min(p => p[channel]) > .98f);
                        foreach (int consumer in new[] { 0, 1, 2, 3 })
                        {
                            string label = variant + "-" + lamp + "-" + consumer;
                            baked.source = SceneBakedShadowSource.None; var clear = Draw(consumer);
                            baked.source = SceneBakedShadowSource.RendererLightmap; var actual = Draw(consumer); Color[] packed = consumer == 0 ? ReadSceneTarget(frame.bakedShadowMask) : null;
                            float error = 0, maxLight = 0;
                            for (int p = 0; p < actual.Length; p++)
                            {
                                float visibility;
                                if (consumer == 0)
                                {
                                    int code = Mathf.RoundToInt(packed[p].g * 255);
                                    visibility = channel == 0 ? packed[p].r : channel == 1 ? (code / 32) / 7f : channel == 2 ? ((code / 4) % 8) / 7f : (code % 4) / 3f;
                                }
                                else visibility = cpu[p][channel];
                                for (int c = 0; c < 3; c++) { error = Mathf.Max(error, Mathf.Abs(actual[p][c] - clear[p][c] * visibility)); maxLight = Mathf.Max(maxLight, clear[p][c]); }
                            }
                            // UNorm source bilinear filtering has finite subtexel
                            // precision. Bound visibility by1/256 plus arithmetic;
                            // compact-output consumption itself stays5e-5.
                            float bound = consumer == 0 ? .00005f : maxLight / 256 + .00005f;
                            Check("actual-light-consumer-entire-target-" + label, maxLight > .01f && error <= bound, error);
                            baked.source = SceneBakedShadowSource.Texture; baked.texture = map; baked.uvST = st;
                            var explicitMap = Draw(consumer); Check("renderer-index-ST-equals-explicit-" + label, PixelError(actual, explicitMap) == 0);
                            baked.source = SceneBakedShadowSource.RendererLightmap;
                            if (variant == 0 && lamp == 0 && consumer < 2 && Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_BAKED_MASK") == "1")
                            {
                                FsrCaptureDrain(target); bool began = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                                try { Draw(consumer); FsrCaptureDrain(target); } finally { if (began) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                                Check("requested-native-actual-bake-capture-" + consumer, began && ended);
                            }
                            SaveSsrPreview("real-baked-mask-" + label, actual, width, height, false);
                        }
                    }
                    camera.targetTexture = null; host.SetActive(false); low.Dispose(); heavy.Dispose();
                    RenderTexture.active = active;
                    foreach (var item in _owned) if (item) DestroyImmediate(item); _owned.Clear();
                    SceneManager.SetActiveScene(original); yield return SceneManager.UnloadSceneAsync(loaded); loaded = default;
                    Check("unload-restores-original-map-count-" + variant, LightmapSettings.lightmaps.Length == oldMapCount);
                }
                for (int lamp = 0; lamp < 4; lamp++) Check("remove-authored-occluder-restores-light-" + lamp, means[1, lamp] - means[0, lamp] > .02f);
            }
            finally
            {
                RenderTexture.active = active; foreach (var item in _owned) if (item) DestroyImmediate(item); _owned.Clear();
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                if (loaded.IsValid() && loaded.isLoaded) SceneManager.UnloadSceneAsync(loaded);
                bundle.Unload(false);
            }
        }
    }
}
