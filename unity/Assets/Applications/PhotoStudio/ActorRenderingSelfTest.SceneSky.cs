using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifySceneSky(Report report)
        {
            yield return null;
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "scene-sky-" + name, ok, error);
            var previous = FindObjectsOfType<Renderer>(); var forced = previous.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in previous) r.forceRenderingOff = true;
            var active = RenderTexture.active; var globalSky = RenderSettings.skybox; var globalAmbient = RenderSettings.ambientMode;
            using var capture = new SceneSkyCapture(); using var water = new SceneWaterRenderer(); using var standalone = new SceneSkyMaterial();
            try
            {
                const int width = 65, height = 49;
                var host = Own(new GameObject("Authored sky independent camera")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
                camera.cullingMask = 0; camera.fieldOfView = 62; camera.aspect = (float)width / height;
                camera.nearClipPlane = .1f; camera.farClipPlane = 20; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.03f, .05f, .07f, .61f); camera.transform.position = new Vector3(0, 0, -3);
                var target = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
                target.name = "Authored sky current camera color"; target.Create(); camera.targetTexture = target;
                Color[] Draw() { camera.Render(); return ReadSceneTarget(target); }
                bool native = Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SCENE_SKY") == "1";
                void NativeDraw(string name)
                {
                    if (!native) return;
                    FsrCaptureDrain(target); bool began = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                    try { Draw(); FsrCaptureDrain(target); } finally { if (began) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                    Check("native-" + name, began && ended);
                }
                var baseline = Draw(); var binding = host.AddComponent<SceneSkyCamera>(); var settings = binding.settings;
                Check("default-disabled-exact", PixelError(baseline, Draw()) == 0 && !binding.IsBound && host.GetComponent<Skybox>() == null);
                settings.enabled = true;
                Check("host-clear-flags-not-silently-changed", !binding.TryApply() && camera.clearFlags == CameraClearFlags.SolidColor && PixelError(baseline, Draw()) == 0);
                camera.clearFlags = CameraClearFlags.Skybox;
                settings.zenith = new Vector3(2, .25f, .5f); settings.horizon = new Vector3(.125f, .75f, .25f); settings.ground = new Vector3(.25f, .125f, 3);
                settings.gradientPower = 1.7f;
                // Independent double-precision angular reference. Native camera
                // rays, not shader interpolants or the public material constants.
                Color Gradient(Vector3 world)
                {
                    var direction = Quaternion.Inverse(settings.rotation.normalized) * world.normalized;
                    double t = Math.Pow(Math.Abs(direction.y), settings.gradientPower);
                    var end = direction.y >= 0 ? settings.zenith : settings.ground;
                    var c = new Color(0, 0, 0, 1);
                    double sun = 0;
                    if (settings.sunEnabled)
                    {
                        double chord = (direction - settings.sunDirection.normalized).magnitude;
                        double angle = 2 * Math.Asin(Math.Min(1, chord / 2));
                        double outer = settings.sunRadiusDegrees * Math.PI / 180, inner = outer * (1 - settings.sunSoftness);
                        if (angle <= inner) sun = 1;
                        else if (angle < outer)
                        {
                            double outer2 = Math.Pow(2 * Math.Sin(outer / 2), 2), inner2 = Math.Pow(2 * Math.Sin(inner / 2), 2);
                            double v = (outer2 - chord * chord) / (outer2 - inner2); sun = v * v * (3 - 2 * v);
                        }
                    }
                    for (int k = 0; k < 3; k++) c[k] = (float)Math.Min(65504, (settings.horizon[k] + (end[k] - settings.horizon[k]) * t + settings.sunRadiance[k] * sun) * settings.tint[k] * Math.Pow(2, settings.exposure));
                    return c;
                }
                var rayHost = Own(new GameObject("Sky origin-relative CPU camera rays")); var rayCamera = rayHost.AddComponent<Camera>(); rayCamera.enabled = false;
                Vector3 Ray(int x, int y)
                {
                    // ViewportPointToRay otherwise subtracts large world-space
                    // points. Keep this independent native CPU reference at the
                    // origin so its cancellation is not blamed on the renderer.
                    rayCamera.transform.rotation = camera.transform.rotation; rayCamera.projectionMatrix = camera.projectionMatrix;
                    return rayCamera.ViewportPointToRay(new Vector3((x + .5f) / width, (y + .5f) / height)).direction;
                }
                void CameraOracle(string name)
                {
                    var pixels = Draw(); float error = 0;
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    { var c = Gradient(Ray(x, y)); for (int k = 0; k < 4; k++) error = Mathf.Max(error, Mathf.Abs(c[k] - pixels[y * width + x][k])); }
                    Check(name, error < .0001f && binding.IsBound, error);
                    SaveSsrPreview("scene-sky-" + name, pixels, width, height, false);
                    if (name == "sun-17-0.4") NativeDraw("angular-precision");
                }
                foreach (var angle in new[] { Vector3.zero, new Vector3(-63, 37, 21), new Vector3(71, -123, -13) })
                {
                    camera.transform.rotation = Quaternion.Euler(angle); CameraOracle("gradient-pose-" + angle);
                    var before = Draw(); camera.transform.position += new Vector3(107, -53, 211);
                    Check("translation-invariant-" + angle, PixelError(before, Draw()) < .0001f);
                }
                camera.transform.SetPositionAndRotation(new Vector3(0, 0, -3), Quaternion.identity);
                settings.rotation = Quaternion.Euler(31, 47, -17); settings.tint = new Vector3(.5f, 1.25f, .75f); settings.exposure = .7f;
                CameraOracle("authored-rotation-tint-exposure");
                var projection = camera.projectionMatrix; var offAxis = projection; offAxis.m02 = .17f; offAxis.m12 = -.23f;
                camera.projectionMatrix = offAxis; CameraOracle("off-axis-projection"); camera.projectionMatrix = projection;
                settings.rotation = Quaternion.identity; settings.tint = Vector3.one; settings.exposure = 0;
                settings.sunEnabled = true; settings.sunDirection = Vector3.forward; settings.sunRadiance = new Vector3(7, 3, .5f);
                foreach (float radius in new[] { .001f, .27f, 17f }) foreach (float softness in new[] { 0f, .4f, 1f })
                { settings.sunRadiusDegrees = radius; settings.sunSoftness = softness; CameraOracle("sun-" + radius + "-" + softness); }
                settings.sunEnabled = false;

                // Every cubemap face has a distinct HDR constant, including its
                // explicit mip chain; engine face selection cannot pass on gray.
                var faceColors = new[] { new Color(2, .125f, .25f, 1), new Color(.25f, 3, .5f, 1), new Color(.5f, .25f, 4, 1),
                    new Color(1.5f, 2.5f, .75f, 1), new Color(.75f, 1.25f, 3.5f, 1), new Color(2.75f, .5f, 1.25f, 1) };
                var cube = Own(new Cubemap(16, TextureFormat.RGBAHalf, true)); cube.filterMode = FilterMode.Point;
                for (int face = 0; face < 6; face++) for (int mip = 0; mip < cube.mipmapCount; mip++)
                    cube.SetPixels(Enumerable.Repeat(faceColors[face] * Mathf.Pow(.5f, mip), Math.Max(1, 16 >> mip) * Math.Max(1, 16 >> mip)).ToArray(), (CubemapFace)face, mip);
                cube.Apply(false); settings.source = SceneSkySource.Cubemap; settings.texture = cube;
                var directions = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
                for (int face = 0; face < 6; face++) foreach (int mip in new[] { 0, 2, 4 })
                {
                    camera.transform.rotation = Quaternion.LookRotation(directions[face], face == 2 || face == 3 ? Vector3.forward : Vector3.up); settings.sourceMip = mip;
                    var pixels = Draw(); var expected = faceColors[face] * Mathf.Pow(.5f, mip); expected.a = 1;
                    float error = PixelError(pixels, Enumerable.Repeat(expected, pixels.Length).ToArray()); Check("borrowed-cube-face-mip-" + face + "-" + mip, error < .0001f, error);
                }
                settings.sourceMip = 0; settings.rotation = Quaternion.Euler(0, 90, 0); camera.transform.rotation = Quaternion.identity;
                Check("cube-rotation-current", PixelError(Draw(), Enumerable.Repeat(faceColors[1], width * height).ToArray()) < .0001f);
                settings.rotation = Quaternion.identity;
                var panorama = Own(new Texture2D(128, 64, TextureFormat.RGBAFloat, false, true)); panorama.filterMode = FilterMode.Bilinear;
                panorama.wrapModeU = TextureWrapMode.Repeat; panorama.wrapModeV = TextureWrapMode.Clamp;
                for (int y = 0; y < 64; y++) for (int x = 0; x < 128; x++) panorama.SetPixel(x, y, new Color((x + .5f) / 128, (y + .5f) / 64, 2, 1)); panorama.Apply();
                settings.source = SceneSkySource.Equirectangular; settings.texture = panorama;
                camera.transform.rotation = Quaternion.Euler(0, 180, 0); var sawSeam = Draw()[height / 2 * width + width / 2];
                Check("sawtooth-seam-exact-center-wraps-both-edge-texels", Mathf.Abs(sawSeam.r - .5f) < .0001f);
                // Periodic HDR texture for the full seam/pole image. A sawtooth
                // jump magnifies a one-bin sampler-weight boundary into 1/256
                // radiance; retain the exact seam stress above instead of
                // treating an intrinsic atan rounding boundary as a wrong face.
                Color PanoramaTexel(int x, int y)
                {
                    double angle = ((x % 128 + 128) % 128 + .5) / 128 * Math.PI * 2;
                    return new Color((float)(.5 + .25 * Math.Cos(angle)), (Mathf.Clamp(y, 0, 63) + .5f) / 64, (float)(2 + .5 * Math.Sin(angle)), 1);
                }
                for (int y = 0; y < 64; y++) for (int x = 0; x < 128; x++) panorama.SetPixel(x, y, PanoramaTexel(x, y)); panorama.Apply();
                foreach (var angle in new[] { Vector3.zero, new Vector3(-83, 179, 0), new Vector3(83, -179, 13) })
                {
                    camera.transform.rotation = Quaternion.Euler(angle); var pixels = Draw(); float error = 0;
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    {
                        var d = Ray(x, y); float u = (float)(Math.Atan2(d.x, d.z) / (2 * Math.PI) + .5), v = (float)(Math.Asin(d.y) / Math.PI + .5);
                        float tx = u * 128 - .5f; int left = (int)Math.Floor(tx); float f = tx - left;
                        float ty = v * 64 - .5f; int bottom = (int)Math.Floor(ty); float fy = ty - bottom;
                        // Actual D3D11 sampler fractional weights, independently
                        // verified against native texels, not an error allowance.
                        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11) { f = Mathf.Round(f * 256) / 256; fy = Mathf.Round(fy * 256) / 256; }
                        var expected = Color.LerpUnclamped(Color.LerpUnclamped(PanoramaTexel(left, bottom), PanoramaTexel(left + 1, bottom), f),
                            Color.LerpUnclamped(PanoramaTexel(left, bottom + 1), PanoramaTexel(left + 1, bottom + 1), f), fy);
                        for (int k = 0; k < 4; k++) error = Mathf.Max(error, Mathf.Abs(expected[k] - pixels[y * width + x][k]));
                    }
                    Check("latlong-entire-image-seam-pole-" + angle, error < .00015f, error);
                    SaveSsrPreview("scene-sky-latlong-" + angle, pixels, width, height, false);
                    if (angle.z == 13) NativeDraw("latlong-seam-pole");
                }
                panorama.wrapModeU = TextureWrapMode.Clamp;
                Check("reject-panorama-sampler-without-mutating", !binding.TryApply() && panorama.wrapModeU == TextureWrapMode.Clamp);
                panorama.wrapModeU = TextureWrapMode.Repeat; yield return null;
                settings.source = SceneSkySource.Gradient; settings.texture = null; camera.transform.rotation = Quaternion.identity;
                CameraOracle("recover-after-source-rejection");

                var quad = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); quad.name = "Sky opaque cutout foreground"; quad.layer = 25;
                quad.transform.localScale = new Vector3(2.3f, 1.7f, 1); var renderer = quad.GetComponent<Renderer>();
                var flat = Own(new Material(Resources.Load<Shader>("PlanarCapture"))); flat.SetColor("_Color", new Color(.75f, .125f, .25f, 1)); flat.SetFloat("_Cutoff", .5f);
                var mask = Own(new Texture2D(2, 1, TextureFormat.RGBAFloat, false, true)); mask.filterMode = FilterMode.Point; mask.wrapMode = TextureWrapMode.Clamp;
                mask.SetPixels(new[] { Color.clear, Color.white }); mask.Apply(); flat.SetTexture("_BaseMap", mask);
                // Keep the authored point discontinuity away from exact pixel
                // centers without excluding any pixels from the depth oracle.
                flat.SetTextureOffset("_BaseMap", new Vector2(.071f, 0)); renderer.sharedMaterial = flat;
                var skyOnly = Draw(); camera.cullingMask = 1 << 25;
                camera.clearFlags = CameraClearFlags.SolidColor; var foregroundOnly = Draw(); camera.clearFlags = CameraClearFlags.Skybox;
                var foreground = Draw(); int covered = 0, clear = 0; float foregroundError = 0;
                var plane = new Plane(Vector3.back, Vector3.zero);
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int i = y * width + x; var ray = camera.ViewportPointToRay(new Vector3((x + .5f) / width, (y + .5f) / height));
                    plane.Raycast(ray, out float distance); var p = quad.transform.InverseTransformPoint(ray.GetPoint(distance));
                    bool opaque = p.x >= -.071f && p.x < .5f && Math.Abs(p.y) < .5f;
                    var expected = opaque ? foregroundOnly[i] : skyOnly[i]; if (opaque) covered++; else clear++;
                    for (int k = 0; k < 4; k++) foregroundError = Mathf.Max(foregroundError, Mathf.Abs(expected[k] - foreground[i][k]));
                }
                Check("native-opaque-cutout-depth-entire-image", covered > 100 && clear > 100 && foregroundError < .0001f, foregroundError);
                SaveSsrPreview("scene-sky-opaque-cutout-depth", foreground, width, height, false); camera.cullingMask = 0;

                // Native six-face HDR producer: sample every face through the
                // real public skybox consumer; no CPU-uploaded fake output cube.
                SceneSkyCapture.Frame frame = default;
                void Capture(int size = 64, bool mips = true)
                { if (!capture.TryCapture(settings, size, mips, out frame)) throw new InvalidOperationException(capture.UnavailableReason); }
                settings.gradientPower = 1; settings.sunEnabled = false;
                Capture(); var savedFrame = frame;
                Check("capture-current-hdr-six-face-descriptor", frame.IsCurrent && frame.radiance.dimension == TextureDimension.Cube && frame.radiance.width == 64 && frame.radiance.mipmapCount == 7 && capture.TargetCount == 1);
                binding.settings = new SceneSkySettings { enabled = true, source = SceneSkySource.Cubemap, texture = frame.radiance };
                Color CapturedReference(Vector3 d)
                {
                    // Independently evaluate the actual64x64 texel centers and
                    // bilinear reconstruction, including the gradient's cusp at
                    // the horizon. A finite cube cannot equal a continuous sky.
                    int axis = Math.Abs(d.x) > Math.Abs(d.y) && Math.Abs(d.x) > Math.Abs(d.z) ? 0 : Math.Abs(d.y) > Math.Abs(d.z) ? 1 : 2;
                    int uAxis = axis == 0 ? 2 : 0, vAxis = axis == 1 ? 2 : 1; float major = Math.Abs(d[axis]);
                    float tx = (d[uAxis] / major + 1) * 32 - .5f, ty = (d[vAxis] / major + 1) * 32 - .5f;
                    int ix = (int)Math.Floor(tx), iy = (int)Math.Floor(ty); float fx = tx - ix, fy = ty - iy;
                    Color Texel(int x, int y)
                    {
                        var direction = Vector3.zero; direction[axis] = Math.Sign(d[axis]); direction[uAxis] = (x + .5f) / 32 - 1; direction[vAxis] = (y + .5f) / 32 - 1;
                        var c = Gradient(direction); for (int k = 0; k < 3; k++) c[k] = Mathf.HalfToFloat(Mathf.FloatToHalf(c[k])); return c;
                    }
                    return Color.LerpUnclamped(Color.LerpUnclamped(Texel(ix, iy), Texel(ix + 1, iy), fx), Color.LerpUnclamped(Texel(ix, iy + 1), Texel(ix + 1, iy + 1), fx), fy);
                }
                for (int face = 0; face < 6; face++)
                {
                    camera.transform.rotation = Quaternion.LookRotation(directions[face], face == 2 || face == 3 ? Vector3.forward : Vector3.up); var pixels = Draw(); float error = 0;
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    { var c = CapturedReference(Ray(x, y)); for (int k = 0; k < 4; k++) error = Mathf.Max(error, Mathf.Abs(c[k] - pixels[y * width + x][k])); }
                    Check("real-capture-face-full-image-" + face, error < .003f, error); SaveSsrPreview("scene-sky-captured-face-" + face, pixels, width, height, false);
                }
                settings.zenith = settings.ground = settings.horizon = new Vector3(2, .5f, 1); Capture();
                Check("capture-update-invalidates-old-frame", !savedFrame.IsCurrent && frame.IsCurrent);
                binding.settings.texture = frame.radiance; camera.transform.rotation = Quaternion.identity;
                for (int mip = 0; mip < frame.radiance.mipmapCount; mip++)
                {
                    binding.settings.sourceMip = mip; var pixels = Draw(); var expected = new Color(2, .5f, 1, 1);
                    Check("native-generated-mip-" + mip, PixelError(pixels, Enumerable.Repeat(expected, pixels.Length).ToArray()) < .0001f);
                }
                binding.settings.sourceMip = 0;
                // Real indirect-specular and water consumers use current output,
                // not a copied constant cube or an unconnected public property.
                camera.clearFlags = CameraClearFlags.SolidColor; binding.enabled = false; camera.cullingMask = 1 << 25;
                flat.SetTexture("_BaseMap", Texture2D.whiteTexture); flat.SetColor("_Color", Color.black); flat.SetFloat("_Cutoff", 0);
                var resolve = host.AddComponent<SceneReflectionResolve>(); resolve.reflectionsEnabled = true; resolve.inputExcludesIndirectSpecular = true;
                var receiver = new SceneReflectionResolve.Receiver { surface = new SceneDepthData.Surface { renderer = renderer, smoothness = 1 }, f0 = Vector3.one, probeMaximumMip = 0, probe = frame.radiance };
                resolve.receivers = new[] { receiver }; var hook = host.AddComponent<ReflectionResolveHostProbe>(); hook.resolver = resolve;
                var firstReflection = Draw();
                Check("real-resolve-current-cube-hdr", hook.consumed && firstReflection[(height / 2) * width + width / 2].r > 1.99f);
                var source = Own(new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); source.Create();
                var depth = Own(new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)); depth.Create();
                var upload = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); upload.SetPixel(0, 0, new Color(15, 0, 0, 0)); upload.Apply(); Graphics.Blit(upload, depth);
                var waterSettings = new SceneWaterSettings { enabled = true }; var waterSurface = new SceneWaterSurface(); waterSettings.surfaces = new[] { waterSurface };
                waterSurface.surface.renderer = renderer; waterSurface.surface.cull = CullMode.Off; waterSurface.surface.inputs.albedo = Vector3.zero; waterSurface.surface.inputs.alpha = 1;
                waterSurface.waveA = waterSurface.waveB = Vector4.zero; waterSurface.refractionPixelsPerUnit = 0; waterSurface.absorption = waterSurface.scatteringRadiance = Vector3.zero;
                waterSurface.reflectionCapture = frame.radiance; waterSurface.indexOfRefraction = 2; waterSettings.lighting.lightRadiance = waterSettings.lighting.ambientIrradiance = Vector3.zero;
                var legacyCube = ResolveCube(new Color(.5f, .25f, .75f, 1)); waterSurface.reflectionProbe = legacyCube;
                Cubemap legacyRead = waterSurface.reflectionProbe; Check("water-legacy-Cubemap-field-type-preserved", legacyRead == legacyCube);
                Color[] Water()
                {
                    hook.enabled = false; resolve.enabled = false; camera.cullingMask = 0; camera.Render(); Graphics.Blit(target, source);
                    if (!water.TryRender(source, new FogVolumeDepth(depth), camera, waterSettings, out var w)) throw new InvalidOperationException(water.UnavailableReason);
                    return ReadSceneTarget(w.color);
                }
                var firstWater = Water(); var center = (height / 2) * width + width / 2;
                float firstWaterError = (float)Math.Abs(firstWater[center].r - (2.0 / 9 + baseline[center].r * 8.0 / 9));
                Check("real-water-current-cube-independent-Schlick", firstWaterError < .0001, firstWaterError);
                settings.zenith = settings.ground = settings.horizon = new Vector3(.25f, 3, .125f); Capture();
                receiver.probe = waterSurface.reflectionCapture = frame.radiance; hook.enabled = resolve.enabled = true; camera.cullingMask = 1 << 25;
                var secondReflection = Draw(); var secondWater = Water();
                Check("producer-update-actual-resolve-consumption", hook.consumed && PixelError(firstReflection, secondReflection) > 1 && Math.Abs(secondReflection[center].g - 3) < .001);
                float secondWaterError = (float)Math.Abs(secondWater[center].g - (1.0 / 3 + baseline[center].g * 8.0 / 9));
                Check("producer-update-actual-water-consumption", PixelError(firstWater, secondWater) > .2f && secondWaterError < .0001, secondWaterError);
                SaveSsrPreview("scene-sky-resolve-current", secondReflection, width, height, false); SaveSsrPreview("scene-sky-water-current", secondWater, width, height, false);
                if (Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SCENE_SKY") == "1")
                {
                    FsrCaptureDrain(target); bool began = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                    try { Capture(); receiver.probe = waterSurface.reflectionCapture = frame.radiance; hook.enabled = resolve.enabled = true; camera.cullingMask = 1 << 25; Draw(); Water(); FsrCaptureDrain(target); }
                    finally { if (began) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                    Check("native-capture-six-faces-mips-resolve-water", began && ended);
                }
                frame.radiance.Release(); Check("loss-invalidates-frame", !frame.IsCurrent);
                hook.enabled = resolve.enabled = true; camera.cullingMask = 1 << 25; Draw(); Check("resolve-rejects-lost-cube", !hook.consumed); camera.cullingMask = 0;
                Check("water-rejects-lost-cube", !water.TryRender(source, new FogVolumeDepth(depth), camera, waterSettings, out _));
                waterSurface.reflectionCapture = null; var legacyWater = Water();
                Check("water-explicit-clear-restores-legacy-cube", Math.Abs(legacyWater[center].r - (.5 / 9 + baseline[center].r * 8.0 / 9)) < .0001);
                waterSurface.reflectionCapture = target;
                Check("water-rejects-2d-capture-without-legacy-fallback", !water.TryRender(source, new FogVolumeDepth(depth), camera, waterSettings, out _) && target.IsCreated() && waterSurface.reflectionProbe == legacyCube);
                waterSurface.reflectionCapture = null;
                Capture(); Check("capture-recovers-loss", frame.IsCurrent); savedFrame = frame; Capture(32, false);
                Check("resize-and-mips-change-current", !savedFrame.IsCurrent && frame.IsCurrent && frame.radiance.width == 32 && !frame.radiance.useMipMap);
                settings.source = SceneSkySource.Cubemap; settings.texture = frame.radiance;
                Check("reject-output-feedback-releases", !capture.TryCapture(settings, 32, false, out _) && !frame.IsCurrent && capture.TargetCount == 0);
                settings.source = SceneSkySource.Gradient; settings.texture = null; Capture(); savedFrame = frame;
                Check("reject-size-releases", !capture.TryCapture(settings, 17, false, out _) && !savedFrame.IsCurrent && capture.TargetCount == 0);
                foreach (var bad in new[] { float.NaN, float.PositiveInfinity, -1f, 65505f })
                { settings.tint = new Vector3(bad, 1, 1); Check("reject-radiance-" + bad, !standalone.TryUpdate(settings, out _) && standalone.MaterialCount == 0); }
                settings.tint = Vector3.one; settings.rotation = new Quaternion(0, 0, 0, 0); Check("reject-zero-rotation", !standalone.TryUpdate(settings, out _));
                settings.rotation = Quaternion.identity; settings.sunEnabled = true; settings.sunDirection = Vector3.zero;
                Check("reject-zero-sun-direction", !standalone.TryUpdate(settings, out _)); settings.sunEnabled = false;
                Check("disabled-sun-ignores-unused-direction", standalone.TryUpdate(settings, out _));
                settings.enabled = false; Check("disabled-material-releases", !standalone.TryUpdate(settings, out _) && standalone.MaterialCount == 0);
                Check("disabled-capture-releases", !capture.TryCapture(settings, 32, false, out _) && capture.TargetCount == 0);
                settings.enabled = true; settings.source = SceneSkySource.Cubemap; settings.texture = cube; settings.sourceMip = cube.mipmapCount;
                Check("reject-unavailable-source-mip-preserves-borrowed-cube", !standalone.TryUpdate(settings, out _) && cube != null && cube.width == 16);
                settings.sourceMip = 0; settings.texture = panorama; Check("reject-2d-as-cube", !standalone.TryUpdate(settings, out _));
                var srgb = Own(new Texture2D(4, 2, TextureFormat.RGBA32, false, false)); srgb.wrapModeU = TextureWrapMode.Repeat; srgb.wrapModeV = TextureWrapMode.Clamp;
                settings.source = SceneSkySource.Equirectangular; settings.texture = srgb;
                Check("reject-srgb-panorama", !standalone.TryUpdate(settings, out _) && srgb != null);
                hook.enabled = resolve.enabled = false; camera.cullingMask = 0; camera.clearFlags = CameraClearFlags.Skybox;
                settings.source = SceneSkySource.Gradient; settings.texture = null; settings.zenith = settings.ground = settings.horizon = Vector3.one * 65504;
                settings.tint = Vector3.one * 65504; settings.exposure = 16; binding.settings = settings; binding.enabled = true;
                Check("actual-HDR-overflow-clamped-alpha-one", PixelError(Draw(), Enumerable.Repeat(new Color(65504, 65504, 65504, 1), width * height).ToArray()) == 0);
                settings.tint = Vector3.zero; Check("zero-tint-finite-black-alpha-one", PixelError(Draw(), Enumerable.Repeat(new Color(0, 0, 0, 1), width * height).ToArray()) == 0);
                binding.enabled = false;
                var ownershipHost = Own(new GameObject("Sky borrowed component ownership")); var other = ownershipHost.AddComponent<Camera>();
                other.enabled = false; other.cullingMask = 0; other.targetTexture = target; other.clearFlags = CameraClearFlags.Skybox;
                var borrowed = ownershipHost.AddComponent<Skybox>(); borrowed.material = flat; borrowed.enabled = false;
                var owner = ownershipHost.AddComponent<SceneSkyCamera>(); owner.settings.enabled = true;
                Check("borrowed-disabled-component-bind", owner.TryApply() && owner.IsBound && borrowed.enabled && borrowed.material != flat);
                owner.enabled = false; Check("borrowed-component-restores-disabled-and-material", !borrowed.enabled && borrowed.material == flat);
                owner.enabled = true; Check("borrowed-same-frame-reenable", owner.TryApply() && owner.IsBound);
                borrowed.material = flat; Check("external-material-not-overwritten-on-detection", !owner.TryApply() && borrowed.material == flat);
                other.Render(); other.Render(); Check("external-material-not-reclaimed-on-next-camera-render", borrowed.material == flat && !owner.IsBound);
                owner.enabled = false; other.targetTexture = null;
                var emptyHost = Own(new GameObject("Sky owned component same-frame cycle")); var emptyCamera = emptyHost.AddComponent<Camera>();
                emptyCamera.enabled = false; emptyCamera.cullingMask = 0; emptyCamera.targetTexture = target; emptyCamera.clearFlags = CameraClearFlags.Skybox;
                var emptyOwner = emptyHost.AddComponent<SceneSkyCamera>(); emptyOwner.settings.enabled = true; emptyOwner.TryApply(); emptyOwner.enabled = false; emptyOwner.enabled = true; emptyOwner.TryApply();
                yield return null; Check("owned-component-same-frame-cycle-survives-destruction-boundary", emptyOwner.IsBound);
                emptyCamera.orthographic = true; emptyCamera.transform.rotation = Quaternion.Euler(-30, 0, 0); emptyCamera.Render();
                var ortho = ReadSceneTarget(target); var orthoColor = new Color(.34f, .45f, .7f, 1);
                float orthoError = PixelError(ortho, Enumerable.Repeat(orthoColor, ortho.Length).ToArray());
                Check("orthographic-infinite-sky-parallel-rays", emptyOwner.IsBound && orthoError < .0001f, orthoError);
                emptyCamera.rect = new Rect(0, 0, .5f, 1); Check("partial-viewport-explicitly-rejected", !emptyOwner.TryApply() && !emptyOwner.IsBound);
                emptyCamera.rect = new Rect(0, 0, 1, 1); Check("full-viewport-recovers", emptyOwner.TryApply() && emptyOwner.IsBound);
                emptyOwner.enabled = false; emptyCamera.targetTexture = null;
                hook.enabled = resolve.enabled = false; camera.cullingMask = 0; camera.clearFlags = CameraClearFlags.SolidColor;
                Check("default-camera-restored", PixelError(baseline, Draw()) == 0 && RenderSettings.skybox == globalSky && RenderSettings.ambientMode == globalAmbient);
                camera.targetTexture = null; RenderTexture.active = active != null && active.IsCreated() ? active : null;
                target.Release(); source.Release(); depth.Release();
            }
            finally
            {
                RenderTexture.active = active != null && active.IsCreated() ? active : null;
                foreach (var value in _owned) if (value is GameObject go && go != null) go.SetActive(false);
                foreach (var value in _owned) if (value != null) Destroy(value);
                for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i];
            }
        }
    }
}
