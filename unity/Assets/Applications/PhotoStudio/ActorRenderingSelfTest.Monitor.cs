using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace GakumasPhotoMode
{
    internal sealed class MonitorCallbackProbe : MonoBehaviour
    {
        public Action callback;
        private void OnPreRender() { callback?.Invoke(); }
    }

    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifyHdrMonitor(Report report)
        {
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "monitor-" + name, ok, error);
            var sourceHost = Own(new GameObject("Dedicated HDR monitor camera")); var source = sourceHost.AddComponent<Camera>();
            source.enabled = false; source.allowHDR = false; source.allowMSAA = true; source.renderingPath = RenderingPath.UsePlayerSettings;
            source.orthographic = true; source.orthographicSize = .5f; source.aspect = 2;
            source.nearClipPlane = .01f; source.farClipPlane = 10; source.transform.position = new Vector3(0, 0, -2);
            source.clearFlags = CameraClearFlags.SolidColor; source.backgroundColor = Color.clear; source.cullingMask = 1 << 24;
            var externalTarget = Own(new RenderTexture(32, 32, 24, RenderTextureFormat.ARGBHalf)); externalTarget.Create(); source.targetTexture = externalTarget;
            Matrix4x4 originalProjection = source.projectionMatrix, originalView = source.worldToCameraMatrix;
            var monitor = sourceHost.AddComponent<HdrMonitor>(); monitor.width = 129; monitor.height = 65;
            Check("default-disabled-no-output", !monitor.TryUpdate(0, 0, out _) && !monitor.TryGetFrame(out _) && monitor.RenderSequence == 0);
            var canvasHost = Own(new GameObject("Self-authored HDR UI atlas", typeof(RectTransform), typeof(Canvas))); canvasHost.layer = 24;
            var canvas = canvasHost.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = source;
            canvasHost.GetComponent<RectTransform>().sizeDelta = new Vector2(2, 1);
            RawImage Panel(string name, Vector2 position, Vector2 size, Vector3 radiance)
            {
                var host = Own(new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage))); host.layer = 24;
                var rect = host.GetComponent<RectTransform>(); rect.SetParent(canvasHost.transform, false); rect.anchoredPosition = position; rect.sizeDelta = size;
                var ui = host.GetComponent<RawImage>(); ui.texture = Texture2D.whiteTexture;
                ui.material = Own(new Material(Resources.Load<Shader>("MonitorCanvas"))); ui.material.SetVector("_Radiance", radiance); return ui;
            }
            var left = Panel("HDR left content", new Vector2(-.5f, 0), Vector2.one, new Vector3(4, .25f, .125f));
            var right = Panel("HDR right content", new Vector2(.5f, 0), Vector2.one, new Vector3(.125f, 3, .25f));
            Canvas.ForceUpdateCanvases(); monitor.monitorEnabled = true;
            int preparations = 0;
            Action<double> prepare = time => { preparations++; Canvas.ForceUpdateCanvases(); };
            monitor.PrepareCapture += prepare;
            HdrMonitor.Frame Update(double time, ulong version = 0)
            { if (!monitor.TryUpdate(time, version, out var frame)) throw new InvalidOperationException(monitor.UnavailableReason); return frame; }
            Color At(Color[] pixels, int w, int h, float u, float v) => pixels[Mathf.Clamp((int)(v * h), 0, h - 1) * w + Mathf.Clamp((int)(u * w), 0, w - 1)];
            Color[] Pixels(HdrMonitor.Frame frame) => ReadSceneTarget(frame.texture);
            Color l = new Color(4, .25f, .125f, 1), r = new Color(.125f, 3, .25f, 1);
            var first = Update(0); var pixels = Pixels(first);
            Check("actual-canvas-left-hdr-not-clamped", PixelError(new[] { At(pixels, 129, 65, .25f, .5f) }, new[] { l }) < .002f);
            Check("actual-canvas-right-atlas-region", PixelError(new[] { At(pixels, 129, 65, .75f, .5f) }, new[] { r }) < .002f);
            Check("dedicated-camera-state-restored", source.targetTexture == externalTarget && !source.allowHDR && source.allowMSAA && source.renderingPath == RenderingPath.UsePlayerSettings &&
                !source.enabled && source.projectionMatrix == originalProjection && source.worldToCameraMatrix == originalView);
            Check("published-hdr-format-and-filter", first.texture.format == RenderTextureFormat.ARGBHalf && !first.texture.sRGB && first.texture.antiAliasing == 1 && !first.texture.useMipMap && first.texture.wrapMode == TextureWrapMode.Clamp);
            uint sequence = monitor.RenderSequence; var repeated = Update(100);
            Check("dirty-static-skips-render-and-retains-output", !monitor.DidRender && monitor.RenderSequence == sequence && repeated.texture == first.texture && ScenePixelsEqual(pixels, Pixels(repeated)));
            Check("skipped-call-does-not-run-ui-preparation", preparations == 1);
            left.material.SetVector("_Radiance", new Vector3(8, .25f, .125f)); Update(101);
            Check("explicit-revision-not-implicit-content-scan", monitor.RenderSequence == sequence && ScenePixelsEqual(pixels, Pixels(repeated)));
            var changed = Update(102, 1); Check("revision-updates-ui-brightness", monitor.DidRender && monitor.RenderSequence == sequence + 1 && At(Pixels(changed), 129, 65, .25f, .5f).r == 8);
            Check("borrowed-old-sequence-invalidated-stable-texture", !first.IsCurrent && changed.IsCurrent && first.texture == changed.texture);
            left.material.SetVector("_Radiance", new Vector3(4, .25f, .125f)); monitor.RequestUpdate(); var restored = Update(102, 1);
            Check("request-overrides-time-and-recovers-content", monitor.DidRender && ScenePixelsEqual(pixels, Pixels(restored)));
            monitor.updateMode = MonitorUpdateMode.FixedRate; monitor.updatesPerSecond = 10; Update(200, 1); sequence = monitor.RenderSequence;
            Update(200.05, 1); Check("fixed-rate-suppresses-early-call", !monitor.DidRender && monitor.RenderSequence == sequence);
            Update(200.11, 1); Check("fixed-rate-renders-due-content", monitor.DidRender && monitor.RenderSequence == sequence + 1);
            Update(200.105, 1); Check("backward-seek-forces-refresh", monitor.DidRender && monitor.RenderSequence == sequence + 2);
            monitor.RequestUpdate(); Update(200.105, 1); Check("explicit-request-bypasses-rate-limit", monitor.DidRender && monitor.RenderSequence == sequence + 3);
            monitor.updateMode = MonitorUpdateMode.EveryCall; Update(200.105, 1); sequence = monitor.RenderSequence; Update(200.105, 1);
            Check("every-call-updates-at-same-time", monitor.RenderSequence == sequence + 1);
            monitor.updateMode = MonitorUpdateMode.WhenDirty; Update(200.105, 1); sequence = monitor.RenderSequence;
            source.transform.position += new Vector3(.05f, 0, 0); Update(200.105, 1); Check("camera-view-change-forces-refresh", monitor.RenderSequence == sequence + 1);
            source.transform.position -= new Vector3(.05f, 0, 0); Update(200.105, 1);
            var maskHost = Own(new GameObject("Monitor rectangular clipping", typeof(RectTransform), typeof(RectMask2D))); maskHost.layer = 24;
            var maskRect = maskHost.GetComponent<RectTransform>(); maskRect.SetParent(canvasHost.transform, false); maskRect.anchoredPosition = new Vector2(.5f, 0); maskRect.sizeDelta = new Vector2(.4f, .4f);
            right.rectTransform.SetParent(maskRect, false); right.rectTransform.anchoredPosition = Vector2.zero;
            Canvas.ForceUpdateCanvases(); var clipped = Update(201, 2); var clippedPixels = Pixels(clipped);
            Check("rect-mask-clips-canvas-region", At(clippedPixels, 129, 65, .9f, .5f).Equals(Color.clear) && At(clippedPixels, 129, 65, .75f, .5f).g == 3);
            maskHost.GetComponent<RectMask2D>().enabled = false;
            var maskImage = maskHost.AddComponent<Image>(); maskImage.material = Own(new Material(Resources.Load<Shader>("MonitorCanvas")));
            maskHost.AddComponent<Mask>().showMaskGraphic = false; Canvas.ForceUpdateCanvases(); clipped = Update(201, 3); clippedPixels = Pixels(clipped);
            Check("stencil-mask-clips-canvas-region", At(clippedPixels, 129, 65, .9f, .5f).Equals(Color.clear) && At(clippedPixels, 129, 65, .75f, .5f).g == 3);
            right.rectTransform.SetParent(canvasHost.transform, false); right.rectTransform.anchoredPosition = new Vector2(.5f, 0); maskHost.SetActive(false); Canvas.ForceUpdateCanvases();
            var unmasked = Update(201, 4); Check("removing-ui-masks-recovers-atlas", ScenePixelsEqual(pixels, Pixels(unmasked)));
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.planeDistance = 1; canvas.scaleFactor = 65; Canvas.ForceUpdateCanvases();
            var screenSpace = Update(201, 5); var screenPixels = Pixels(screenSpace);
            Check("screen-space-camera-canvas-hdr", At(screenPixels, 129, 65, .25f, .5f).r == 4 && At(screenPixels, 129, 65, .75f, .5f).g == 3);
            canvas.renderMode = RenderMode.WorldSpace; canvasHost.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); canvasHost.transform.localScale = Vector3.one;
            canvasHost.GetComponent<RectTransform>().sizeDelta = new Vector2(2, 1); Canvas.ForceUpdateCanvases();
            Check("world-canvas-mode-restores-atlas", ScenePixelsEqual(pixels, Pixels(Update(201, 6))));
            source.ResetAspect(); Matrix4x4 automaticProjection = source.projectionMatrix;
            var automaticFrame = Update(201, 6);
            Check("implicit-projection-survives-target-swap", automaticFrame.IsCurrent && source.projectionMatrix == automaticProjection && source.targetTexture == externalTarget);
            source.orthographicSize = .6f; automaticFrame = Update(201, 6);
            Check("implicit-projection-not-latched-as-custom", automaticFrame.IsCurrent && source.projectionMatrix != automaticProjection);
            source.orthographicSize = .5f; source.aspect = 2; Update(201, 6);

            var secondHost = Own(new GameObject("Second independent monitor camera")); var secondCamera = secondHost.AddComponent<Camera>(); secondCamera.CopyFrom(source);
            secondCamera.enabled = false; secondCamera.cullingMask = 0; secondCamera.backgroundColor = Color.blue;
            var secondMonitor = secondHost.AddComponent<HdrMonitor>(); secondMonitor.width = 33; secondMonitor.height = 17; secondMonitor.monitorEnabled = true;
            Check("second-monitor-independent-target-and-content", secondMonitor.TryUpdate(0, 0, out var secondFrame) && secondFrame.texture != unmasked.texture && PixelError(new[] { Pixels(secondFrame)[200] }, new[] { Color.blue }) < .002f);
            Check("interleaved-monitor-does-not-change-first-atlas", ScenePixelsEqual(pixels, Pixels(Update(201, 6))));
            secondHost.SetActive(false);
            foreach (bool linearTexture in new[] { false, true })
            {
                var texture = Own(new Texture2D(1, 1, TextureFormat.RGBA32, false, linearTexture)); texture.SetPixel(0, 0, new Color(.5f, .25f, .75f, 1)); texture.Apply(); left.texture = texture;
                Color expectedTexture = texture.GetPixel(0, 0); if (!linearTexture && QualitySettings.activeColorSpace == ColorSpace.Linear) expectedTexture = expectedTexture.linear;
                expectedTexture *= l; monitor.RequestUpdate(); var textured = Update(201, 6);
                Check("ui-texture-color-space-" + (linearTexture ? "linear" : "srgb"), PixelError(new[] { At(Pixels(textured), 129, 65, .25f, .5f) }, new[] { expectedTexture }) < .004f);
            }
            left.texture = Texture2D.whiteTexture; monitor.RequestUpdate(); Update(201, 6);
            monitor.TryGetFrame(out var feedbackFrame); left.texture = feedbackFrame.texture; left.uvRect = new Rect(.1f, .1f, .1f, .1f); left.material.SetVector("_Radiance", Vector3.one * .5f);
            monitor.RequestUpdate(); var feedbackA = Update(201, 6);
            Check("feedback-reads-previous-published-frame", At(Pixels(feedbackA), 129, 65, .25f, .5f).r == 2);
            monitor.RequestUpdate(); var feedbackB = Update(201, 6);
            Check("feedback-advances-once-per-request", At(Pixels(feedbackB), 129, 65, .25f, .5f).r == 1);
            left.texture = Texture2D.whiteTexture; left.uvRect = new Rect(0, 0, 1, 1); left.material.SetVector("_Radiance", new Vector3(4, .25f, .125f)); monitor.RequestUpdate();
            var oldActive = RenderTexture.active; bool oldSrgb = GL.sRGBWrite;
            try { RenderTexture.active = externalTarget; GL.sRGBWrite = true; Update(201, 6); Check("active-target-and-srgb-write-restored", RenderTexture.active == externalTarget && GL.sRGBWrite); }
            finally { RenderTexture.active = oldActive; GL.sRGBWrite = oldSrgb; }

            // Real mesh consumers: independent UV atlas regions, no baked colors.
            var viewerHost = Own(new GameObject("Monitor emissive mesh camera")); var viewer = viewerHost.AddComponent<Camera>();
            viewer.enabled = false; viewer.allowHDR = true; viewer.allowMSAA = false; viewer.renderingPath = RenderingPath.Forward;
            viewer.orthographic = true; viewer.orthographicSize = .6f; viewer.aspect = 3;
            viewer.transform.position = new Vector3(0, 0, -3); viewer.cullingMask = 1 << 25; viewer.clearFlags = CameraClearFlags.SolidColor; viewer.backgroundColor = Color.clear;
            var target = Own(new RenderTexture(192, 64, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create(); viewer.targetTexture = target;
            var surfaces = new List<GameObject>(); var bindings = new List<MonitorEmissionMaterial>();
            try
            {
                var settings = new[] { new MonitorEmissionSettings { monitorUV = new Vector4(0, 0, .25f, .5f) },
                    new MonitorEmissionSettings { monitorUV = new Vector4(0, 0, .75f, .5f) }, new MonitorEmissionSettings() };
                var frame = Update(201, 1);
                for (int i = 0; i < 3; i++)
                {
                    var surface = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); surface.layer = 25; surface.transform.position = new Vector3((i - 1) * 1.1f, 0, 0); surface.transform.localScale = new Vector3(.9f, .9f, 1);
                    var binding = new MonitorEmissionMaterial(); bindings.Add(binding); surfaces.Add(surface);
                    if (!binding.TryBind(frame, settings[i], out var error)) throw new InvalidOperationException(error);
                    surface.GetComponent<Renderer>().sharedMaterial = binding.Material;
                }
                Color[] View() { viewer.Render(); return ReadSceneTarget(target); }
                Color Surface(Color[] p, int i) => At(p, 192, 64, (i - 1) * 1.1f / 3.6f + .5f, .5f);
                var displayed = View();
                Check("emissive-left-mesh-reads-live-hdr", PixelError(new[] { Surface(displayed, 0) }, new[] { l }) < .002f);
                Check("emissive-right-mesh-selects-other-region", PixelError(new[] { Surface(displayed, 1) }, new[] { r }) < .002f);
                var material = bindings[0].Material;
                left.material.SetVector("_Radiance", new Vector3(8, .25f, .125f)); frame = Update(202, 2);
                foreach (var binding in bindings) Check("material-instance-owned-" + bindings.IndexOf(binding), binding.Material != left.material && binding.Material != right.material);
                bindings[0].TryBind(frame, settings[0], out _); displayed = View();
                Check("ui-animation-reaches-emission-not-handle-only", Surface(displayed, 0).r == 8 && PixelError(new[] { Surface(displayed, 1) }, new[] { r }) < .002f && bindings[0].Material == material);
                left.material.SetVector("_Radiance", new Vector3(4, .25f, .125f)); frame = Update(203, 3);
                settings[0].linearTint = new Vector3(.5f, 2, 1); settings[0].intensity = 2; bindings[0].TryBind(frame, settings[0], out _);
                Check("emission-linear-tint-intensity-once", PixelError(new[] { Surface(View(), 0) }, new[] { new Color(4, 1, .25f, 1) }) < .002f);
                settings[0].linearTint = Vector3.one; settings[0].intensity = 1; settings[0].ledStrength = 1;
                settings[0].ledPattern = ResolveTexture(new[] { new Color(.5f, .25f, 1, 1) }, 1, 1); bindings[0].TryBind(frame, settings[0], out _);
                Check("led-pattern-modulates-not-replaces-radiance", PixelError(new[] { Surface(View(), 0) }, new[] { new Color(2, .0625f, .125f, 1) }) < .002f);
                settings[0].ledPattern = ResolveTexture(new[] { Color.white, Color.black }, 2, 1); settings[0].ledTiling = Vector2.one; bindings[0].TryBind(frame, settings[0], out _);
                Check("led-spatial-pattern-first-texel", PixelError(new[] { Surface(View(), 0) }, new[] { l }) < .002f);
                settings[0].ledTiling = new Vector2(3, 1); bindings[0].TryBind(frame, settings[0], out _);
                Check("led-spatial-tiling-changes-sampled-texel", Surface(View(), 0).r == 0 && Surface(View(), 0).g == 0);
                settings[0].ledStrength = 0; bindings[0].TryBind(frame, settings[0], out _); Check("led-disabled-restores-radiance", PixelError(new[] { Surface(View(), 0) }, new[] { l }) < .002f);
                var mesh = Own(Instantiate(surfaces[2].GetComponent<MeshFilter>().sharedMesh)); surfaces[2].GetComponent<MeshFilter>().sharedMesh = mesh;
                for (int channel = 0; channel < 4; channel++)
                {
                    var uv = new List<Vector2>(); for (int vertex = 0; vertex < mesh.vertexCount; vertex++) uv.Add(new Vector2(channel % 2 == 0 ? .25f : .75f, .5f));
                    mesh.SetUVs(channel, uv);
                }
                for (int channel = 0; channel < 4; channel++)
                {
                    settings[2].uvChannel = channel; bindings[2].TryBind(frame, settings[2], out _);
                    Check("mesh-uv-channel-" + channel, PixelError(new[] { Surface(View(), 2) }, new[] { channel % 2 == 0 ? l : r }) < .002f);
                }
                // Actual overlapping transparent UI: opacity must be applied only
                // by the capture, not again in its emissive consumer.
                var overlay = Panel("Half-alpha HDR overlay", new Vector2(-.5f, 0), Vector2.one, new Vector3(0, 0, 6)); overlay.color = new Color(1, 1, 1, .5f);
                Canvas.ForceUpdateCanvases(); frame = Update(204, 4); var composite = At(Pixels(frame), 129, 65, .25f, .5f);
                float alpha = ((Color32)overlay.color).a / 255f;
                var opacityOracle = new Color(4 * (1 - alpha), .25f * (1 - alpha), .125f * (1 - alpha) + 6 * alpha, 1);
                Check("canvas-alpha-composition-hdr", PixelError(new[] { composite }, new[] { opacityOracle }) < .004f);
                bindings[0].TryBind(frame, settings[0], out _); Check("emission-does-not-double-alpha-ui", PixelError(new[] { Surface(View(), 0) }, new[] { composite }) < .002f);
                left.gameObject.SetActive(false); monitor.RequestUpdate(); frame = Update(205, 4); composite = At(Pixels(frame), 129, 65, .25f, .5f);
                bindings[0].TryBind(frame, settings[0], out _);
                Check("transparent-only-region-remains-premultiplied", composite.a > .49f && composite.a < .51f && Mathf.Abs(Surface(View(), 0).b - composite.b) < .002f && composite.b > 2.9f);
                left.gameObject.SetActive(true); overlay.gameObject.SetActive(false); monitor.RequestUpdate(); frame = Update(206, 4);
                bindings[0].TryBind(frame, settings[0], out _); SaveSsrPreview("monitor-live-emission-panels", View(), 192, 64, false);
                SaveSsrPreview("monitor-hdr-canvas-atlas", Pixels(frame), 129, 65, false);
                var unobstructed = View(); var foreground = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); foreground.layer = 25; foreground.transform.position = new Vector3(-1.1f, 0, -.5f); foreground.transform.localScale = Vector3.one;
                var foregroundMaterial = Own(new Material(Resources.Load<Shader>("PlanarCapture"))); foregroundMaterial.SetColor("_Color", Color.black); foreground.GetComponent<Renderer>().sharedMaterial = foregroundMaterial;
                Check("emission-obeys-main-scene-foreground-depth", Surface(View(), 0).r == 0 && Surface(View(), 1).g == 3);
                foreground.SetActive(false); Check("removing-foreground-recovers-emission", ScenePixelsEqual(unobstructed, View()));
                bindings[0].Unbind(); Check("explicit-unbind-removes-emission", Surface(View(), 0).r == 0 && Surface(View(), 0).g == 0);
                Check("binding-current-frame-recovers", bindings[0].TryBind(frame, settings[0], out _) && Surface(View(), 0).r == 4);
                settings[0].uvChannel = 4;
                Check("invalid-binding-blacks-out-not-stale", !bindings[0].TryBind(frame, settings[0], out _) && Surface(View(), 0).r == 0);
                settings[0].uvChannel = 0;
                settings[0].monitorUV.w = float.NaN;
                Check("nonfinite-uv-offset-rejected", !bindings[0].TryBind(frame, settings[0], out _)); settings[0].monitorUV.w = .5f;
                monitor.width = 97; monitor.height = 73;
                Check("resize-invalidates-borrowed-frame-before-update", !frame.IsCurrent && !monitor.TryGetFrame(out _));
                var resized = Update(207, 4);
                Check("resize-reallocates-with-new-dimensions", resized.texture.width == 97 && resized.texture.height == 73 && resized.texture != frame.texture && resized.IsCurrent);
                resized.texture.Release(); Check("lost-target-not-readable", !resized.IsCurrent);
                var recreated = Update(207, 4); Check("lost-target-recreated-and-rendered", recreated.IsCurrent && monitor.DidRender && recreated.texture.IsCreated());
                monitor.width = 0; Check("invalid-dimensions-release-output", !monitor.TryUpdate(207, 4, out _) && !recreated.IsCurrent);
                monitor.width = 129; monitor.height = 65; frame = Update(208, 4);
                Check("invalid-input-recovery-renders", frame.IsCurrent && At(Pixels(frame), 129, 65, .25f, .5f).r == 4);
                source.enabled = true; Check("active-camera-not-hijacked", !monitor.TryUpdate(208, 4, out _) && source.enabled && source.targetTexture == externalTarget);
                source.enabled = false; frame = Update(209, 4);
                source.clearFlags = CameraClearFlags.Depth; Check("uncleared-capture-rejected", !monitor.TryUpdate(209, 4, out _)); source.clearFlags = CameraClearFlags.SolidColor;
                frame = Update(210, 4); Check("invalid-clock-rejected", !monitor.TryUpdate(double.NaN, 4, out _) && !frame.IsCurrent);
                frame = Update(211, 4);
                source.rect = new Rect(0, 0, .5f, 1); Check("partial-viewport-invalidates-and-rejects", !frame.IsCurrent && !monitor.TryUpdate(211, 4, out _)); source.rect = new Rect(0, 0, 1, 1);
                source.allowDynamicResolution = true; bool dynamicRequested = source.allowDynamicResolution;
                bool dynamicAccepted = monitor.TryUpdate(211, 4, out _);
                // D3D11 on this host reads the requested flag back as false;
                // record that limited observation, not an active-DR rejection.
                if (dynamicRequested) Check("active-dynamic-resolution-rejected", !dynamicAccepted);
                else Check("dynamic-resolution-request-inactive-on-this-backend", dynamicAccepted && monitor.TryGetFrame(out var fixedFrame) && fixedFrame.texture.width == 129 && fixedFrame.texture.height == 65);
                source.allowDynamicResolution = false;
                monitor.updatesPerSecond = float.NaN; Check("nonfinite-rate-rejected", !monitor.TryUpdate(211, 4, out _)); monitor.updatesPerSecond = 30;
                monitor.updateMode = (MonitorUpdateMode)99; Check("unknown-update-policy-rejected", !monitor.TryUpdate(211, 4, out _)); monitor.updateMode = MonitorUpdateMode.WhenDirty;
                source.backgroundColor = new Color(float.NaN, 0, 0, 1); Check("nonfinite-clear-color-rejected", !monitor.TryUpdate(211, 4, out _)); source.backgroundColor = Color.clear;
                Action<double> badPrepare = time => throw new InvalidOperationException("Expected monitor preparation fault"); monitor.PrepareCapture += badPrepare;
                Check("preparation-error-restores-camera-and-releases", !monitor.TryUpdate(211, 4, out _) && monitor.UnavailableReason == "Expected monitor preparation fault" && source.targetTexture == externalTarget && !source.allowHDR);
                monitor.PrepareCapture -= badPrepare; frame = Update(211, 4);
                var callback = sourceHost.AddComponent<MonitorCallbackProbe>(); bool recursiveRejected = false;
                callback.callback = () => recursiveRejected = !monitor.TryUpdate(211, 4, out _);
                monitor.RequestUpdate(); frame = Update(211, 4); callback.callback = null;
                Check("recursive-update-rejected-with-outer-frame-preserved", recursiveRejected && frame.IsCurrent && monitor.DidRender);
                callback.callback = () => monitor.RequestUpdate(); monitor.RequestUpdate(); frame = Update(211, 4); callback.callback = null;
                sequence = monitor.RenderSequence; frame = Update(211, 4);
                Check("request-during-render-is-not-dropped", monitor.DidRender && monitor.RenderSequence == sequence + 1);
                callback.callback = () => monitor.enabled = false;
                monitor.RequestUpdate(); bool canceled = !monitor.TryUpdate(212, 4, out _); callback.callback = null;
                Check("disable-during-capture-defers-release-and-restores-camera", canceled && !frame.IsCurrent && source.targetTexture == externalTarget && !source.allowHDR && source.allowMSAA);
                monitor.enabled = true; frame = Update(213, 4);
                canvasHost.SetActive(false); monitor.RequestUpdate(); frame = Update(214, 4);
                Check("empty-ui-clears-old-frame", ScenePixelsEqual(Pixels(frame), new Color[129 * 65]));
                canvasHost.SetActive(true); Canvas.ForceUpdateCanvases(); monitor.RequestUpdate(); frame = Update(215, 4);
                Check("reactivated-ui-recovers", At(Pixels(frame), 129, 65, .25f, .5f).r == 4);
                monitor.enabled = false; Check("disable-invalidates-and-releases-frame", !frame.IsCurrent && !monitor.TryGetFrame(out _));
                Check("stale-binding-rejected-and-cleared", !bindings[0].TryBind(frame, settings[0], out _) && Surface(View(), 0).r == 0);
                surfaces[0].GetComponent<Renderer>().sharedMaterial = null; bindings[0].Dispose();
                Check("disposed-emission-material-rejects-binding", !bindings[0].TryBind(frame, settings[0], out _) && bindings[0].Material == null);
            }
            finally
            {
                foreach (var surface in surfaces) { surface.GetComponent<Renderer>().sharedMaterial = null; surface.SetActive(false); }
                foreach (var binding in bindings) binding.Dispose();
                viewer.targetTexture = null; target.Release(); viewerHost.SetActive(false);
            }
            monitor.enabled = false; source.targetTexture = null; externalTarget.Release(); sourceHost.SetActive(false); canvasHost.SetActive(false);
        }
    }
}
