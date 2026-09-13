using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyFsrCamera(Report report)
        {
            yield return null;
            var savedActive = RenderTexture.active; bool savedSrgb = GL.sRGBWrite;
            var previous = FindObjectsOfType<Renderer>(); var forced = new bool[previous.Length];
            for (int i = 0; i < previous.Length; i++) { forced[i] = previous[i].forceRenderingOff; previous[i].forceRenderingOff = true; }
            var renderer = new FsrCameraRenderer(); var zeroRenderer = new FsrCameraRenderer(); var oracle = new FsrRenderer();
            Camera observed = null; Camera.CameraCallback observe = null;
            try
            {
                void Check(string name, bool value, float error = 0) => FrameworkCheck(report, "fsr-camera-" + name, value, error);
                RenderTexture Target(int w, int h, int depth = 0)
                {
                    var t = Own(new RenderTexture(w, h, depth, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); t.Create(); return t;
                }
                Camera CameraHost(string name)
                {
                    var host = Own(new GameObject(name)); var c = host.AddComponent<Camera>(); c.enabled = false;
                    c.allowHDR = true; c.allowMSAA = false; c.cullingMask = 1 << 26; c.renderingPath = RenderingPath.Forward;
                    c.orthographic = true; c.orthographicSize = 2; c.aspect = 193f / 145; c.nearClipPlane = .1f; c.farClipPlane = 30;
                    c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = new Color(.07f, .09f, .12f, 1);
                    c.transform.position = new Vector3(0, 0, -4); c.targetTexture = Target(19, 17, 24); return c;
                }
                SceneDeferredCamera.Surface Surface(string name, Vector3 position, Vector3 scale, float angle, Vector3 emission)
                {
                    var go = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.name = name; go.layer = 25;
                    go.transform.position = position; go.transform.localScale = scale; go.transform.rotation = Quaternion.Euler(0, 0, angle);
                    var surface = new SceneDeferredCamera.Surface { renderer = go.GetComponent<Renderer>(), cull = CullMode.Off };
                    surface.inputs.emission = emission; return surface;
                }
                var surfaces = new[] {
                    Surface("FSR textured background", new Vector3(0, 0, .4f), new Vector3(5.8f, 4.5f, 1), 0, new Vector3(.6f, .45f, .3f)),
                    Surface("FSR diagonal narrow red geometry", new Vector3(-.55f, -.15f, 0), new Vector3(.075f, 3.2f, 1), -24, new Vector3(.75f, .08f, .03f)),
                    Surface("FSR short blue geometry", new Vector3(.65f, .3f, -.2f), new Vector3(.18f, 1.3f, 1), 37, new Vector3(.05f, .22f, .7f))
                };
                SceneDeferredCamera Scene(Camera c)
                {
                    var s = c.gameObject.AddComponent<SceneDeferredCamera>(); s.sceneEnabled = true; s.sceneLayers = 1 << 25;
                    s.surfaces = surfaces; s.lightRadiance = s.ambientIrradiance = Vector3.zero; return s;
                }
                var camera = CameraHost("FSR actual lower-resolution camera"); var scene = Scene(camera);
                var zeroCamera = CameraHost("FSR de-jitter-only control"); var zeroScene = Scene(zeroCamera);
                var referenceCamera = CameraHost("FSR independent native reference"); var referenceScene = Scene(referenceCamera);
                var outputSize = new Vector2Int(193, 145); var output = Target(outputSize.x, outputSize.y);
                var zeroOutput = Target(outputSize.x, outputSize.y); var bilinearOutput = Target(outputSize.x, outputSize.y);
                var settings = new FsrSettings { enabled = true, backend = FsrBackend.Compute, allowRasterFallback = false };
                int observedRenders = 0; Vector2Int observedSize = default; Matrix4x4 observedProjection = default;
                observed = camera;
                observe = c => { if (c == observed) { observedRenders++; observedSize = new Vector2Int(c.targetTexture.width, c.targetTexture.height); observedProjection = c.projectionMatrix; } };
                Camera.onPreRender += observe;
                var originalTarget = camera.targetTexture; float originalAspect = camera.aspect;
                var baseProjection = camera.projectionMatrix; int originalMask = camera.cullingMask;
                FsrCameraRenderer.Frame Render(string name)
                {
                    var projection = camera.projectionMatrix; int before = observedRenders;
                    bool ok = renderer.TryRender(camera, output, settings, out var frame, temporalSource: scene);
                    Check(name + "-actual-manual-render", ok && frame.IsCurrent && observedRenders == before + 1);
                    if (!ok) throw new InvalidOperationException(name + ": " + renderer.UnavailableReason);
                    settings.TryGetRenderSize(new Vector2Int(output.width, output.height), out var size);
                    var gbuffer = FsrPrivateField<RenderTexture[]>(scene, "_output"); var low = FsrPrivateField<RenderTexture>(renderer, "_input");
                    Check(name + "-actual-low-resolution-attachments", observedSize == size && frame.renderSize == size && low.width == size.x &&
                        low.height == size.y && gbuffer[0].width == size.x && gbuffer[0].height == size.y && scene.SubmittedSurfaces == 3);
                    Check(name + "-camera-state-restored", camera.targetTexture == originalTarget && camera.aspect == originalAspect &&
                        camera.projectionMatrix == projection && observedProjection == projection && camera.cullingMask == originalMask && !camera.enabled);
                    Check(name + "-depth-format-and-total-budget", low.depthStencilFormat == GraphicsFormat.D24_UNorm_S8_UInt &&
                        renderer.EstimatedTargetBytes == FsrSettings.EstimateTargetBytes(size, new Vector2Int(output.width, output.height)) + 20L * size.x * size.y);
                    return frame;
                }
                var first = Render("without-taa");
                Check("default-no-temporal-allocation", scene.TemporalColorTargetCount == 0 && !first.beforeDiffusion && renderer.TargetCount == 4);
                scene.motion.enabled = true; scene.temporalAntialiasing.enabled = true;
                foreach (FsrQuality quality in Enum.GetValues(typeof(FsrQuality)))
                {
                    settings.quality = quality; var frame = Render("quality-" + quality);
                    var taa = FsrTemporalColor(scene); var metadata = FsrTemporalTexture(scene, "Metadata");
                    Check("quality-" + quality + "-actual-taa-size", taa.width == frame.renderSize.x && taa.height == frame.renderSize.y && scene.TemporalColorTargetCount == 8);
                    Check("quality-" + quality + "-resize-resets-history", FsrHistoryUse(metadata) == 0);
                    Render("quality-" + quality + "-history");
                    Check("quality-" + quality + "-history-reused", FsrHistoryUse(FsrTemporalTexture(scene, "Metadata")) > 100);
                    bool ok = oracle.TryRender(FsrTemporalColor(scene), new RectInt(0, 0, frame.renderSize.x, frame.renderSize.y), outputSize, settings, out var expected);
                    float error = ok ? PixelError(ReadSceneTarget(expected.color), ReadSceneTarget(output)) : float.PositiveInfinity;
                    Check("quality-" + quality + "-actual-taa-to-fsr-whole-output", ok && error <= .00005f, error);
                }
                Check("old-camera-result-invalidated", !first.IsCurrent);
                var custom = baseProjection; custom.m03 += .037f; custom.m13 -= .021f; camera.projectionMatrix = custom;
                Render("custom-projection"); camera.projectionMatrix = baseProjection;
                settings.quality = FsrQuality.Quality; camera.orthographic = false; camera.ResetProjectionMatrix();
                Render("perspective"); camera.orthographic = true; camera.projectionMatrix = baseProjection;
                var oldOutput = output; output = Target(129, 101); Render("odd-output-resize"); output = oldOutput;

                // A separate real camera uses zero history, not a CPU-prepared image
                // or repeated mutation of the accumulated camera's history state.
                zeroScene.motion.enabled = true; zeroScene.temporalAntialiasing.enabled = true; zeroScene.temporalAntialiasing.historyWeight = 0;
                var checker = Own(new Texture2D(256, 256, TextureFormat.RGBAFloat, false, true)); checker.filterMode = FilterMode.Point; checker.wrapMode = TextureWrapMode.Clamp;
                var texturePixels = new Color[256 * 256];
                for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
                    texturePixels[y * 256 + x] = ((x / 2 + y / 2) & 1) == 0 ? new Color(.7f, .16f, .08f, 1) : new Color(.08f, .52f, .67f, 1);
                checker.SetPixels(texturePixels); checker.Apply(); surfaces[0].inputs.emissionMap = checker; surfaces[0].inputs.emission = Vector3.one;
                var highTarget = Target(outputSize.x * 4, outputSize.y * 4, 24); referenceCamera.targetTexture = highTarget; referenceCamera.Render();
                var high = ReadSceneTarget(highTarget); var reference = new Color[outputSize.x * outputSize.y];
                for (int y = 0; y < outputSize.y; y++) for (int x = 0; x < outputSize.x; x++)
                    for (int dy = 0; dy < 4; dy++) for (int dx = 0; dx < 4; dx++) reference[y * outputSize.x + x] += high[(y * 4 + dy) * highTarget.width + x * 4 + dx] / 16;
                var nativeTarget = Target(outputSize.x, outputSize.y, 24); referenceCamera.targetTexture = nativeTarget; referenceCamera.Render(); var native = ReadSceneTarget(nativeTarget);
                SaveSsrPreview("fsr-camera-quality-reference-4x", reference, outputSize.x, outputSize.y, false);
                SaveSsrPreview("fsr-camera-quality-reference-native", native, outputSize.x, outputSize.y, false);
                double allError = 0, allControlError = 0, allVariation = 0, allControlVariation = 0;
                foreach (FsrQuality quality in Enum.GetValues(typeof(FsrQuality)))
                {
                    settings.quality = quality; settings.TryGetRenderSize(outputSize, out var lowSize);
                    scene.ResetTemporalColorHistory(); zeroScene.ResetTemporalColorHistory();
                    scene.temporalAntialiasing.reactiveThreshold = zeroScene.temporalAntialiasing.reactiveThreshold = 1;
                    double error = 0, controlError = 0, variation = 0, controlVariation = 0, bilinearError = 0; long samples = 0;
                    Color[] prior = null, priorControl = null, accumulated = null, control = null, bilinear = null;
                    for (int step = 0; step < 48; step++)
                    {
                        int phase = step % 16; var jitter = new Vector2(((phase % 4) + .5f) / 4 - .5f, ((phase / 4) + .5f) / 4 - .5f);
                        var projection = baseProjection; projection.m03 += 2 * jitter.x / lowSize.x; projection.m13 += 2 * jitter.y / lowSize.y;
                        camera.projectionMatrix = zeroCamera.projectionMatrix = projection;
                        scene.temporalAntialiasing.jitterUv = zeroScene.temporalAntialiasing.jitterUv = new Vector2(-jitter.x / lowSize.x, -jitter.y / lowSize.y);
                        var result = Render("quality-sequence-" + quality + "-" + step);
                        bool zeroOk = zeroRenderer.TryRender(zeroCamera, zeroOutput, settings, out var zeroResult, temporalSource: zeroScene);
                        Check("quality-sequence-" + quality + "-" + step + "-independent-zero-history", zeroOk && zeroResult.IsCurrent && result.IsCurrent && FsrHistoryUse(FsrTemporalTexture(zeroScene, "Metadata")) == 0);
                        if (!zeroOk) throw new InvalidOperationException(zeroRenderer.UnavailableReason);
                        accumulated = ReadSceneTarget(output); control = ReadSceneTarget(zeroOutput);
                        Graphics.Blit(FsrTemporalColor(scene), bilinearOutput); bilinear = ReadSceneTarget(bilinearOutput);
                        if (step >= 32)
                        {
                            for (int i = 0; i < accumulated.Length; i++) for (int c = 0; c < 3; c++)
                            {
                                error += Math.Abs(accumulated[i][c] - reference[i][c]); controlError += Math.Abs(control[i][c] - reference[i][c]);
                                bilinearError += Math.Abs(bilinear[i][c] - reference[i][c]); samples++;
                                variation += Math.Abs(accumulated[i][c] - prior[i][c]); controlVariation += Math.Abs(control[i][c] - priorControl[i][c]);
                            }
                        }
                        prior = accumulated; priorControl = control;
                    }
                    Check("quality-" + quality + "-finite-error-ratio", samples > 100000 && controlError > 1 && !double.IsNaN(error / controlError), (float)(error / controlError));
                    Check("quality-" + quality + "-finite-variation-ratio", controlVariation > 1 && !double.IsNaN(variation / controlVariation), (float)(variation / controlVariation));
                    Check("quality-" + quality + "-bilinear-mae-measured", samples > 0 && !double.IsNaN(bilinearError), (float)(bilinearError / samples));
                    Check("quality-" + quality + "-fsr-mae-measured", samples > 0 && !double.IsNaN(error), (float)(error / samples));
                    allError += error; allControlError += controlError; allVariation += variation; allControlVariation += controlVariation;
                    SaveSsrPreview("fsr-camera-quality-" + quality + "-taa-fsr", accumulated, outputSize.x, outputSize.y, false);
                    SaveSsrPreview("fsr-camera-quality-" + quality + "-dejitter-fsr", control, outputSize.x, outputSize.y, false);
                    SaveSsrPreview("fsr-camera-quality-" + quality + "-taa-bilinear", bilinear, outputSize.x, outputSize.y, false);
                }
                Check("all-quality-taa-reduces-dejitter-fsr-reference-error", allControlError > 1 && allError <= allControlError * .90, (float)(allError / allControlError));
                Check("all-quality-taa-reduces-dejitter-fsr-temporal-variation", allControlVariation > 1 && allVariation <= allControlVariation * .85, (float)(allVariation / allControlVariation));
                camera.projectionMatrix = baseProjection; scene.temporalAntialiasing.jitterUv = Vector2.zero;
                VerifyFsrPostBridge(report, camera, scene, surfaces, output, renderer, settings);
                VerifyFsrCameraOwnership(report, camera, scene, zeroScene, output, renderer, settings);
                VerifyFsrUi(report, output, renderer, settings);
                VerifyFsrNativeCostCapture(report, camera, scene, renderer);
            }
            finally
            {
                if (observe != null) Camera.onPreRender -= observe;
                RenderTexture.active = null;
                renderer.Dispose(); zeroRenderer.Dispose(); oracle.Dispose();
                foreach (var item in _owned) if (item is GameObject go && go.GetComponent<Camera>() != null) go.SetActive(false);
                foreach (var item in _owned) { if (item is RenderTexture target) target.Release(); if (item != null) Destroy(item); }
                _owned.Clear();
                for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i];
                RenderTexture.active = savedActive != null && savedActive.IsCreated() ? savedActive : null; GL.sRGBWrite = savedSrgb;
            }
        }

        private static RenderTexture FsrTemporalColor(SceneDeferredCamera scene) => FsrTemporalTexture(scene, "Color");
        private void VerifyFsrCameraOwnership(Report report, Camera camera, SceneDeferredCamera scene,
            SceneDeferredCamera foreignScene, RenderTexture output, FsrCameraRenderer renderer, FsrSettings settings)
        {
            void Check(string name, bool value) => FrameworkCheck(report, "fsr-camera-ownership-" + name, value);
            var target = camera.targetTexture; var projection = camera.projectionMatrix; float aspect = camera.aspect;
            settings.enabled = true; settings.encoding = FsrInputEncoding.LinearLdr;
            FsrCameraRenderer.Frame Prime()
            {
                bool ok = renderer.TryRender(camera, output, settings, out var frame);
                if (!ok) throw new InvalidOperationException(renderer.UnavailableReason);
                return frame;
            }
            void Invalid(string name, Func<bool> attempt)
            {
                var prior = Prime(); var before = ReadSceneTarget(output);
                Check(name + "-rejected", !attempt() && renderer.UnavailableReason != null && renderer.TargetCount == 0 && !prior.IsCurrent);
                Check(name + "-external-output-unchanged", output.IsCreated() && PixelError(before, ReadSceneTarget(output)) == 0);
                Check(name + "-camera-restored", camera.targetTexture == target && camera.projectionMatrix == projection && camera.aspect == aspect);
            }
            Invalid("null-camera", () => renderer.TryRender(null, output, settings, out _));
            Invalid("null-output", () => renderer.TryRender(camera, null, settings, out _));
            Invalid("enabled-camera", () => { camera.enabled = true; try { return renderer.TryRender(camera, output, settings, out _); } finally { camera.enabled = false; } });
            Invalid("msaa-camera", () => { camera.allowMSAA = true; try { return renderer.TryRender(camera, output, settings, out _); } finally { camera.allowMSAA = false; } });
            Invalid("partial-viewport", () => { camera.rect = new Rect(0, 0, .5f, 1); try { return renderer.TryRender(camera, output, settings, out _); } finally { camera.rect = new Rect(0, 0, 1, 1); } });
            Invalid("foreign-temporal-camera", () => renderer.TryRender(camera, output, settings, out _, temporalSource: foreignScene));
            Invalid("invalid-quality", () => { var q = settings.quality; settings.quality = (FsrQuality)100; try { return renderer.TryRender(camera, output, settings, out _); } finally { settings.quality = q; } });
            Invalid("budget-before-allocation", () => { int budget = settings.memoryBudgetMiB; settings.memoryBudgetMiB = 1; try { return renderer.TryRender(camera, output, settings, out _); } finally { settings.memoryBudgetMiB = budget; } });
            Invalid("own-input-alias", () => renderer.TryRender(camera, FsrPrivateField<RenderTexture>(renderer, "_input"), settings, out _));
            var priorFrame = Prime(); settings.enabled = false;
            Check("disable-releases-without-error", !renderer.TryRender(camera, output, settings, out _) && renderer.TargetCount == 0 && !priorFrame.IsCurrent && renderer.UnavailableReason == null);
            settings.enabled = true;
            var sibling = new FsrCameraRenderer(); int callbacks = 0; bool nestedSame = false, nestedSibling = false, disposeRejected = false;
            Camera.CameraCallback nested = c => {
                if (c != camera) return; callbacks++;
                nestedSame = !renderer.TryRender(camera, output, settings, out _);
                nestedSibling = !sibling.TryRender(camera, output, settings, out _) && sibling.TargetCount == 0;
                try { renderer.Dispose(); } catch (InvalidOperationException) { disposeRejected = true; }
            };
            Camera.onPreRender += nested;
            try
            {
                var frame = Prime();
                Check("same-and-other-owner-reentry-blocked", callbacks == 1 && nestedSame && nestedSibling && disposeRejected && frame.IsCurrent && renderer.UnavailableReason == null);
            }
            finally { Camera.onPreRender -= nested; sibling.Dispose(); }
            var current = Prime(); renderer.Dispose(); renderer.Dispose();
            Check("idempotent-dispose-and-external-survival", !current.IsCurrent && renderer.TargetCount == 0 && output.IsCreated() && target.IsCreated());
            Check("reuse-after-dispose", Prime().IsCurrent);
        }

        private void VerifyFsrUi(Report report, RenderTexture sceneOutput, FsrCameraRenderer cameraRenderer, FsrSettings settings)
        {
            void Check(string name, bool value, float error = 0) => FrameworkCheck(report, "fsr-ui-" + name, value, error);
            RenderTexture Target(int width, int height) { var t = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); t.Create(); return t; }
            var full = Target(sceneOutput.width, sceneOutput.height); var native = Target(full.width, full.height);
            var host = Own(new GameObject("FSR full-resolution UI camera")); var uiCamera = host.AddComponent<Camera>();
            uiCamera.enabled = false; uiCamera.allowMSAA = false; uiCamera.allowHDR = true; uiCamera.orthographic = true;
            uiCamera.orthographicSize = full.height * .5f; uiCamera.aspect = full.width / (float)full.height;
            uiCamera.transform.position = new Vector3(0, 0, -10); uiCamera.cullingMask = 1 << 24; uiCamera.backgroundColor = Color.clear;
            var canvasHost = Own(new GameObject("FSR native authored UI", typeof(RectTransform), typeof(Canvas))); canvasHost.layer = 24;
            var canvas = canvasHost.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = uiCamera;
            canvasHost.GetComponent<RectTransform>().sizeDelta = new Vector2(full.width, full.height);
            var texture = Own(new Texture2D(33, 17, TextureFormat.RGBAFloat, false, true)); texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Clamp;
            var pixels = new Color[33 * 17]; for (int y = 0; y < 17; y++) for (int x = 0; x < 33; x++) pixels[y * 33 + x] = (x + y) % 2 == 0 ? Color.white : new Color(0, 1, 0, 1);
            texture.SetPixels(pixels); texture.Apply();
            var imageHost = Own(new GameObject("One-native-pixel UI checks", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.RawImage))); imageHost.layer = 24;
            var image = imageHost.GetComponent<UnityEngine.UI.RawImage>(); image.texture = texture;
            image.rectTransform.SetParent(canvasHost.transform, false); image.rectTransform.sizeDelta = new Vector2(33, 17); image.rectTransform.anchoredPosition = new Vector2(41, 27);
            try
            {
                Canvas.ForceUpdateCanvases(); uiCamera.clearFlags = CameraClearFlags.SolidColor; uiCamera.targetTexture = native; uiCamera.Render();
                var overlay = ReadSceneTarget(native); var background = ReadSceneTarget(sceneOutput); int opaque = 0;
                Graphics.Blit(sceneOutput, full); uiCamera.clearFlags = CameraClearFlags.Depth; uiCamera.targetTexture = full; uiCamera.Render();
                var actual = ReadSceneTarget(full); var expected = new Color[actual.Length];
                for (int i = 0; i < expected.Length; i++) { bool covered = overlay[i].a > .99f; if (covered) opaque++; expected[i] = covered ? overlay[i] : background[i]; }
                float error = PixelError(expected, actual);
                Check("real-ugui-native-pixel-coverage", opaque == 33 * 17);
                Check("after-fsr-full-image-equals-native-overlay", error <= .00005f, error);
                // Deliberately wrong order: render this same native-sized canvas
                // at the 3D resolution, then FSR. It must not pass the UI oracle.
                settings.TryGetRenderSize(new Vector2Int(full.width, full.height), out var size); var low = Target(size.x, size.y);
                uiCamera.clearFlags = CameraClearFlags.SolidColor; uiCamera.targetTexture = low; uiCamera.Render();
                using (var upscaler = new FsrRenderer())
                {
                    bool ok = upscaler.TryRender(low, new RectInt(0, 0, size.x, size.y), new Vector2Int(full.width, full.height), settings, out var wrong);
                    Check("pre-fsr-ui-negative-control-renders", ok && wrong.IsCurrent);
                    if (ok) { float difference = PixelError(overlay, ReadSceneTarget(wrong.color)); Check("pre-fsr-ui-loses-native-pixel-detail", difference > .1f, difference); }
                }
                SaveSsrPreview("fsr-full-resolution-ui-after-reconstruction", actual, full.width, full.height, false);
            }
            finally { canvasHost.SetActive(false); uiCamera.targetTexture = null; host.SetActive(false); }
        }
        private void VerifyFsrPostBridge(Report report, Camera camera, SceneDeferredCamera scene,
            SceneDeferredCamera.Surface[] surfaces, RenderTexture output, FsrCameraRenderer renderer, FsrSettings settings)
        {
            void Check(string name, bool value, float error = 0) => FrameworkCheck(report, "fsr-post-" + name, value, error);
            StoryFloatParameter Number(float value) => new StoryFloatParameter { overrideState = true, value = value };
            var profile = new StoryPostProcessProfile {
                active = true,
                bloom = new StoryBloomProfile { active = true, threshold = Number(0), intensity = Number(0), diffusion = new StoryIntParameter { overrideState = true, value = 3 } },
                diffusion = new StoryDiffusionProfile { active = true, diffusion = Number(.36f), contrastThreshold = Number(.5f), contrastPower = Number(0), blend = Number(0) },
                chromaticAberration = new StoryChromaticAberrationProfile { active = false }
            };
            var lut = ColorGradingLut.Bake(new ColorGradingProfile { size = 32, domain = ColorLutDomain.Linear, maximumInput = 1, toneMapping = ColorToneMapping.Clip });
            var context = OriginalStyleRenderPipeline.CurrentPresentationContext;
            OriginalStyleRenderPipeline.SetPresentationContext(OriginalStyleRenderPipeline.PresentationContext.StudioLocal);
            OriginalStyleRenderPipeline post = null;
            try
            {
                camera.gameObject.SetActive(false);
                post = camera.gameObject.AddComponent<OriginalStyleRenderPipeline>(); post.useAuthoredColorGrading = true; post.authoredColorLut = lut;
                post.sceneTemporalSource = scene; post.sceneMotionBlurSource = scene; post.occlusionStrength = post.outlineStrength = 0;
                camera.gameObject.SetActive(true);
                var originalTarget = camera.targetTexture; var originalProjection = camera.projectionMatrix; float originalAspect = camera.aspect;
                settings.encoding = FsrInputEncoding.LinearHdr; settings.quality = FsrQuality.Quality;
                settings.TryGetRenderSize(new Vector2Int(output.width, output.height), out var input);
                var constant = new Vector3(.03125f, .0625f, .125f);
                foreach (var surface in surfaces) { surface.inputs.emissionMap = null; surface.inputs.emission = constant; }
                FsrCameraRenderer.Frame Render(string name)
                {
                    string dump = System.IO.Path.Combine(_directory, "fsr-post-input-" + name);
                    OriginalStyleRenderPipeline.RequestPostInputDump(dump);
                    bool ok = renderer.TryRender(camera, output, settings, out var frame);
                    Check(name + "-completed-before-diffusion", ok && frame.IsCurrent && frame.beforeDiffusion);
                    if (!ok) throw new InvalidOperationException(name + ": " + renderer.UnavailableReason);
                    Check(name + "-camera-restored", camera.targetTexture == originalTarget && camera.aspect == originalAspect && camera.projectionMatrix == originalProjection);
                    Check(name + "-real-source-and-bloom-budget", frame.renderSize == input && renderer.TargetCount == 5 &&
                        renderer.EstimatedTargetBytes == FsrSettings.EstimateTargetBytes(input, new Vector2Int(output.width, output.height)) + 36L * input.x * input.y);
                    // Read the actual temporary before it is released through
                    // the existing diagnostic dump. Material.GetTexture returns
                    // null for these non-Properties shader uniforms in Player.
                    var blurPixels = FsrReadHalfDump(dump + "-t1-blur", out var blurSize);
                    int blurWidth = Mathf.Max(1, Mathf.RoundToInt(output.width * (3f / 16f))), blurHeight = Mathf.Max(1, Mathf.RoundToInt(output.height * (3f / 16f)));
                    Check(name + "-full-size-diffusion-and-grade", blurSize.x == blurWidth && blurSize.y == blurHeight &&
                        post.TryGetAuthoredColorFrame(out var graded) && graded.IsCurrent && graded.color.width == output.width && graded.color.height == output.height);
                    var sharpPixels = ReadSceneTarget(frame.upscale.color); var actualPost = ReadSceneTarget(output);
                    var expectedPost = FsrPostReference(sharpPixels, new Vector2Int(output.width, output.height), blurPixels, blurSize,
                        profile.diffusion.blend.value, profile.diffusion.contrastPower.value);
                    float postError = PixelError(expectedPost, actualPost);
                    Check(name + "-independent-whole-image-no-second-bloom", postError <= .002f, postError);
                    if (name == "dof-motion-3")
                    {
                        float missingDiffusion = PixelError(actualPost, FsrPostReference(sharpPixels, new Vector2Int(output.width, output.height), blurPixels, blurSize, 0, 0));
                        Check(name + "-missing-diffusion-negative-control-detected", missingDiffusion > .01f, missingDiffusion);
                    }
                    if (postError > .002f) { FsrDump("post-" + name + "-expected", expectedPost); FsrDump("post-" + name + "-actual", actualPost); }
                    return frame;
                }
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    profile.bloom.intensity.value = mode == 0 ? 0 : 10; profile.diffusion.blend.value = mode == 2 ? .5f : 0;
                    post.SetStoryPostProcessProfile(profile); scene.ResetTemporalColorHistory();
                    var frame = Render("constant-mode-" + mode);
                    var expectedColor = constant * (mode == 0 ? 1 : 4); var actual = ReadSceneTarget(output);
                    var expected = new Color[actual.Length]; for (int i = 0; i < expected.Length; i++) expected[i] = new Color(expectedColor.x, expectedColor.y, expectedColor.z, 1);
                    float error = PixelError(expected, actual);
                    Check("constant-mode-" + mode + "-independent-full-image-single-bloom", error <= .0005f, error);
                    var composed = ReadSceneTarget(FsrPrivateField<RenderTexture>(renderer, "_bloomInput"));
                    var expectedLow = new Color[composed.Length]; for (int i = 0; i < expectedLow.Length; i++) expectedLow[i] = expected[0];
                    float lowError = PixelError(expectedLow, composed);
                    Check("constant-mode-" + mode + "-actual-bloom-before-fsr", lowError <= .00002f, lowError);
                }
                Check("no-private-lut-attempt", !FsrPrivateField<bool>(post, "_capturedLutAttempted"));

                // Both existing low-resolution geometry consumers must actually
                // execute before reconstruction, with complete registered depth.
                surfaces[0].inputs.emission = new Vector3(.09f, .14f, .2f);
                surfaces[1].inputs.emission = new Vector3(2f, .12f, .05f);
                surfaces[2].inputs.emission = new Vector3(.08f, .4f, 1.5f);
                profile.bloom.intensity.value = 2.5f; profile.diffusion.blend.value = .5f; profile.diffusion.contrastPower.value = .075f;
                post.SetStoryPostProcessProfile(profile);
                // SceneDepthData deliberately respects its own camera culling
                // mask. The 3D host excludes native draws for deferred surfaces,
                // so use a same-view explicit depth camera instead of an empty
                // prepass which would merely supply farClip for every pixel.
                var depthHost = Own(new GameObject("FSR explicit complete depth camera"));
                var depthCamera = depthHost.AddComponent<Camera>(); depthCamera.enabled = false;
                var depthTarget = Own(new RenderTexture(input.x, input.y, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); depthTarget.Create();
                var depth = depthHost.AddComponent<SceneDepthData>(); depth.buildDepthHierarchy = false;
                depth.surfaces = Array.ConvertAll(surfaces, s => new SceneDepthData.Surface { renderer = s.renderer, cull = CullMode.Off });
                int providers = 0;
                post.bokehDepthOfField.enabled = true; post.bokehDepthOfField.focusNear = 3.75f; post.bokehDepthOfField.focusFar = 3.85f;
                post.bokehDepthOfField.nearTransition = .4f; post.bokehDepthOfField.farTransition = .6f; post.bokehDepthOfField.maximumRadius = .025f;
                post.bokehDepthProvider = (cam, color) => {
                    providers++; Check("dof-current-source-" + providers, cam == camera && color.width == input.x && color.height == input.y);
                    depthCamera.CopyFrom(cam); depthCamera.enabled = false; depthCamera.cullingMask = 1 << 25;
                    depthCamera.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation); depthCamera.targetTexture = depthTarget; depthCamera.Render();
                    if (!depth.TryGetFrame(depthCamera, color.width, color.height, out var frame)) throw new InvalidOperationException("FSR complete current depth missing");
                    var depths = ReadSceneTarget(frame.linearDepth); int geometry = 0;
                    foreach (var value in depths) if (value.r > 3 && value.r < 5) geometry++;
                    Check("dof-real-registered-depth-" + providers, depth.SubmittedSurfaces == 3 && geometry > depths.Length * .9f);
                    return frame.linearDepth;
                };
                scene.motionBlur.enabled = true; scene.motionBlurTime = 0; scene.ResetTemporalColorHistory();
                Render("dof-motion-prime");
                for (int step = 1; step <= 3; step++)
                {
                    surfaces[1].renderer.transform.position += new Vector3(.12f, 0, 0);
                    scene.motionBlurTime = step / 60.0; Render("dof-motion-" + step);
                    Check("dof-motion-" + step + "-both-low-resolution-consumers", post.TryGetBokehDepthOfFieldFrame(out var bokeh) && bokeh.IsCurrent &&
                        bokeh.color.width == input.x && bokeh.color.height == input.y && scene.MotionBlurTargetCount > 0 && scene.MotionBlurResolveDrawCalls > 0 && depth.SubmittedSurfaces == 3);
                }
                SaveSsrPreview("fsr-post-actual-taa-dof-motion-bloom-fsr-diffusion", ReadSceneTarget(output), output.width, output.height, false);
                if (Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_FSR") == "1")
                {
                    scene.motionBlurTime += 1.0 / 60; surfaces[1].renderer.transform.position += new Vector3(.12f, 0, 0);
                    FsrCaptureDrain(output);
                    bool started = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false, rendered = false;
                    try { rendered = renderer.TryRender(camera, output, settings, out var captured) && captured.IsCurrent && captured.beforeDiffusion; FsrCaptureDrain(output); }
                    finally { if (started) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                    Check("requested-native-complete-post-chain-capture", started && ended && rendered);
                }
                scene.motion.enabled = false;
                bool missingMotion = renderer.TryRender(camera, output, settings, out _);
                Check("failed-requested-taa-is-not-silent-input", !missingMotion && renderer.TargetCount == 0 && renderer.UnavailableReason.Contains("upstream"));
                scene.motion.enabled = true; scene.motionBlurTime = 1; Render("recover-missing-motion");
                post.bokehDepthProvider = null;
                Check("failed-requested-dof-is-not-silent-input", !renderer.TryRender(camera, output, settings, out _) && renderer.TargetCount == 0 && renderer.UnavailableReason.Contains("upstream"));
                post.bokehDepthOfField.enabled = false; scene.motionBlur.enabled = false; Render("recover-missing-dof");
                post.authoredColorLut = null;
                Check("failed-requested-grade-is-not-complete-output", !renderer.TryRender(camera, output, settings, out _) && renderer.TargetCount == 0 && renderer.UnavailableReason.Contains("upstream"));
                post.authoredColorLut = lut; Render("recover-missing-grade");
                post.enabled = false; scene.temporalAntialiasing.enabled = false; settings.encoding = FsrInputEncoding.LinearLdr;
                Check("post-bridge-disabled-independent-camera-still-works", renderer.TryRender(camera, output, settings, out var plain, temporalSource: scene) && plain.IsCurrent && !plain.beforeDiffusion && renderer.TargetCount == 4);
            }
            finally
            {
                if (post != null) post.enabled = false;
                lut.Dispose(); OriginalStyleRenderPipeline.SetPresentationContext(context);
            }
        }
        private static RenderTexture FsrTemporalTexture(SceneDeferredCamera scene, string name)
        {
            // Test-only resource inspection after the manual host restores the
            // camera target. The scene's public borrowed frame intentionally
            // checks its active camera target and is no longer a valid lease.
            var temporal = FsrPrivateField<object>(scene, "_temporalAntialiasing");
            if (temporal == null) throw new InvalidOperationException("No actual scene TAA allocation");
            return (RenderTexture)temporal.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public).GetValue(temporal);
        }
        private static T FsrPrivateField<T>(object owner, string name) =>
            (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
        private int FsrHistoryUse(RenderTexture metadata)
        {
            int count = 0; foreach (var value in ReadSceneTarget(metadata)) if (value.a > 0) count++; return count;
        }
        [Serializable] private sealed class FsrDumpDimensions { public int width, height; }
        private Color[] FsrReadHalfDump(string prefix, out Vector2Int size)
        {
            var dimensions = JsonUtility.FromJson<FsrDumpDimensions>(System.IO.File.ReadAllText(prefix + ".json"));
            size = new Vector2Int(dimensions.width, dimensions.height);
            var texture = new Texture2D(size.x, size.y, TextureFormat.RGBAHalf, false, true);
            try { texture.LoadRawTextureData(System.IO.File.ReadAllBytes(prefix + ".rgba16f.raw")); texture.Apply(); return texture.GetPixels(); }
            finally { Destroy(texture); }
        }
        private static Color[] FsrPostReference(Color[] sharp, Vector2Int size, Color[] blur, Vector2Int blurSize, float blend, float power)
        {
            // Independent scalar authored composite + identity clip-LUT. Bloom
            // is already in sharp; any second bloom contribution is an error.
            Color Sample(float u, float v)
            {
                float x = u * blurSize.x - .5f, y = v * blurSize.y - .5f; int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
                Color Read(int a, int b) => blur[Mathf.Clamp(b, 0, blurSize.y - 1) * blurSize.x + Mathf.Clamp(a, 0, blurSize.x - 1)];
                return Color.LerpUnclamped(Color.LerpUnclamped(Read(ix, iy), Read(ix + 1, iy), x - ix),
                    Color.LerpUnclamped(Read(ix, iy + 1), Read(ix + 1, iy + 1), x - ix), y - iy);
            }
            var result = new Color[sharp.Length];
            for (int y = 0; y < size.y; y++) for (int x = 0; x < size.x; x++)
            {
                int i = y * size.x + x; var blurred = Sample((x + .5f) / size.x, (y + .5f) / size.y); var value = sharp[i];
                for (int c = 0; c < 3; c++)
                {
                    float shaped = Mathf.Clamp01(Mathf.Max(value[c], blurred[c]));
                    float curve = shaped >= .5f ? 1 - 2 * (1 - shaped) * (1 - shaped) : 2 * shaped * shaped;
                    shaped = Mathf.Lerp(shaped, curve, power); value[c] = Mathf.Clamp01((value[c] + shaped * blend) / (1 + blend));
                }
                result[i] = value;
            }
            return result;
        }
        private void VerifyFsrNativeCostCapture(Report report, Camera camera, SceneDeferredCamera scene, FsrCameraRenderer renderer)
        {
            if (Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_FSR") != "1") return;
            scene.motion.enabled = true; scene.temporalAntialiasing.enabled = true;
            foreach (var size in new[] { new Vector2Int(1281, 721), new Vector2Int(1920, 1080) })
            foreach (FsrQuality quality in Enum.GetValues(typeof(FsrQuality)))
            {
                var target = Own(new RenderTexture(size.x, size.y, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); target.Create();
                var settings = new FsrSettings { enabled = true, quality = quality, backend = FsrBackend.Compute, allowRasterFallback = false, encoding = FsrInputEncoding.LinearHdr };
                scene.ResetTemporalColorHistory();
                for (int warm = 0; warm < 4; warm++)
                    if (!renderer.TryRender(camera, target, settings, out _, temporalSource: scene)) throw new InvalidOperationException(renderer.UnavailableReason);
                string name = size.x + "x" + size.y + "-" + quality;
                FsrCaptureDrain(target);
                bool started = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false, rendered = false;
                try { rendered = renderer.TryRender(camera, target, settings, out var frame, temporalSource: scene) && frame.IsCurrent; FsrCaptureDrain(target); }
                finally { if (started) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                FrameworkCheck(report, "fsr-native-capture-" + name, started && ended && rendered);
                Debug.Log("[FsrNativeCapture] " + name + " source=" + renderer.RenderSize.x + "x" + renderer.RenderSize.y + " ownedTextureBytes=" + renderer.EstimatedTargetBytes);
                // Replay timestamps measure FSR dispatches only, excluding the
                // diagnostic one-pixel drain and presentation/readback overhead.
                target.Release();
            }
        }
        private static void FsrCaptureDrain(RenderTexture target)
        {
            // Diagnostic ONLY. Unity submits Camera.Render on its render thread;
            // ending RenderDoc from the main thread before that queue drains can
            // capture zero commands or overlap the next capture. A one-pixel
            // synchronous transfer brackets the actual native submission.
            var active = RenderTexture.active; var pixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            try { RenderTexture.active = target; pixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false); }
            finally { RenderTexture.active = active; Destroy(pixel); }
        }
    }
}
