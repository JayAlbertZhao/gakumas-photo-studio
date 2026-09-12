using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifyTemporalClassification(Report report)
        {
            const int width = 32, height = 24;
            var color = Own(new Texture2D(width, height, TextureFormat.RGBAFloat, false, true));
            color.filterMode = FilterMode.Point;
            color.wrapMode = TextureWrapMode.Clamp;
            var colors = new Color[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float value = 0.1f + x * 0.02f + y * 0.01f;
                colors[y * width + x] = new Color(value, value * 0.5f, value * 0.25f, 1f);
            }
            color.SetPixels(colors);
            color.Apply();
            var flags = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            flags.filterMode = FilterMode.Point;
            var destination = Own(new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
            destination.Create();
            var readback = Own(new Texture2D(width, height, TextureFormat.RGBAFloat, false, true));
            var post = Own(new Material(Resources.Load<Shader>("OriginalStylePost")));
            post.SetTexture("_TemporalFlagsTex", flags);
            post.SetTexture("_HistoryTex", Texture2D.whiteTexture);
            post.SetTexture("_CameraDepthTexture", Texture2D.blackTexture);
            post.SetTexture("_CameraMotionVectorsTexture", Texture2D.blackTexture);
            post.SetFloat("_TemporalBlend", 0.95f);
            post.SetVector("_TemporalJitterUv", new Vector4(1f / width, 0, 0, 0));
            RenderTexture previous = RenderTexture.active;
            try
            {
                foreach (bool validHistory in new[] { false, true })
                foreach (int value in new[] { 0, 2, 4, 6 })
                {
                    flags.SetPixel(0, 0, new Color(value / 255f, 0, 0, 0));
                    flags.Apply();
                    post.SetFloat("_HistoryValid", validHistory ? 1f : 0f);
                    post.SetFloat("_TemporalMaskEnabled", 0f);
                    Graphics.Blit(color, destination, post, 7);
                    RenderTexture.active = destination;
                    readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    readback.Apply();
                    Color legacy = readback.GetPixel(16, 12);
                    post.SetFloat("_TemporalMaskEnabled", 1f);
                    Graphics.Blit(color, destination, post, 7);
                    RenderTexture.active = destination;
                    readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    readback.Apply();
                    Color actual = readback.GetPixel(16, 12);
                    Color expected = value == 0 ? legacy : colors[12 * width + (value == 2 ? 15 : 16)];
                    float error = Mathf.Max(Mathf.Abs(actual.r - expected.r), Mathf.Abs(actual.g - expected.g), Mathf.Abs(actual.b - expected.b));
                    FrameworkCheck(report, "taa-flags-gpu-" + value + "-history-" + validHistory, error < 1e-6f, error);
                    if (validHistory && value == 4)
                        FrameworkCheck(report, "taa-bypass-positive-control", Mathf.Abs(legacy.r - actual.r) > 0.0001f);
                }
                VerifyTemporalMaskCamera(report, destination, readback, width, height);
            }
            finally
            {
                RenderTexture.active = previous;
                destination.Release();
            }
        }

        private void VerifyTemporalMaskCamera(Report report, RenderTexture destination, Texture2D readback, int width, int height)
        {
            var host = Own(new GameObject("Temporal mask camera"));
            var camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.allowMSAA = false;
            camera.orthographic = true;
            camera.orthographicSize = 1.5f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 10f;
            camera.cullingMask = 1 << 27;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            var target = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            target.Create();
            camera.targetTexture = target;
            var mask = host.AddComponent<TemporalClassification>();
            var pipeline = host.AddComponent<OriginalStyleRenderPipeline>();
            pipeline.temporalClassification = mask;
            var flat = Own(new Material(Resources.Load<Shader>("StudioAccent")));
            flat.SetColor("_Color", Color.white);
            var surface = Own(GameObject.CreatePrimitive(PrimitiveType.Quad));
            surface.layer = 27;
            surface.transform.position = new Vector3(0, 0, 3);
            surface.transform.localScale = Vector3.one * 2;
            var renderer = surface.GetComponent<Renderer>();
            renderer.sharedMaterial = flat;
            var blocker = Own(GameObject.CreatePrimitive(PrimitiveType.Quad));
            blocker.layer = 27;
            blocker.transform.position = new Vector3(0, 0, 2);
            blocker.transform.localScale = Vector3.one * 0.7f;
            blocker.GetComponent<Renderer>().sharedMaterial = flat;
            mask.surfaces = new[] { new TemporalClassification.Surface { renderer = renderer, flags = TemporalPixelFlags.ExcludeTaa } };
            camera.Render();
            Texture texture;
            bool bound = mask.TryGetMask(camera, width, height, out texture);
            FrameworkCheck(report, "taa-mask-camera-bound", bound && mask.SubmittedSurfaces == 1);
            if (!bound) throw new InvalidOperationException("Temporal mask did not bind to its camera");
            Graphics.Blit(texture, destination);
            RenderTexture.active = destination;
            readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            readback.Apply();
            float center = readback.GetPixel(16, 12).r, side = readback.GetPixel(23, 12).r;
            FrameworkCheck(report, "taa-mask-real-depth-occlusion", center == 0f && Mathf.Abs(side * 255f - 2f) < 0.01f);
            File.WriteAllBytes(Path.Combine(_directory, "taa-classification-depth.png"), readback.EncodeToPNG());
            var postMaterial = (Material)typeof(OriginalStyleRenderPipeline).GetField("_postMaterial", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(pipeline);
            FrameworkCheck(report, "taa-mask-production-pipeline-binding", postMaterial.GetFloat("_TemporalMaskEnabled") == 1f && postMaterial.GetTexture("_TemporalFlagsTex") == texture);
            foreach (bool orthographic in new[] { true, false })
            foreach (TemporalPixelFlags value in new[] { TemporalPixelFlags.ExcludeTaa, TemporalPixelFlags.NoJitter, (TemporalPixelFlags)6 })
            {
                camera.orthographic = orthographic;
                mask.surfaces[0].flags = value;
                camera.Render();
                mask.TryGetMask(camera, width, height, out texture);
                Graphics.Blit(texture, destination);
                RenderTexture.active = destination;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readback.Apply();
                FrameworkCheck(report, "taa-mask-projection-" + orthographic + "-flags-" + (int)value,
                    readback.GetPixel(16, 12).r == 0f && Mathf.Abs(readback.GetPixel(21, 12).r * 255f - (int)value) < 0.01f);
            }
            var transparentMask = Own(new Texture2D(1, 1, TextureFormat.RGBA32, false, true));
            transparentMask.SetPixel(0, 0, Color.clear);
            transparentMask.Apply();
            mask.surfaces[0].alphaMask = transparentMask;
            mask.surfaces[0].alphaCutoff = 0.5f;
            camera.Render();
            mask.TryGetMask(camera, width, height, out texture);
            Graphics.Blit(texture, destination);
            RenderTexture.active = destination;
            readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            readback.Apply();
            bool alphaClear = true;
            foreach (Color pixel in readback.GetPixels()) alphaClear &= pixel.r == 0f;
            FrameworkCheck(report, "taa-mask-alpha-cutout", alphaClear);
            mask.surfaces[0].alphaMask = null;
            mask.surfaces[0].alphaCutoff = 0f;
            Texture unused;
            FrameworkCheck(report, "taa-mask-rejects-other-camera", !mask.TryGetMask(_camera, width, height, out unused));
            FrameworkCheck(report, "taa-mask-rejects-other-size", !mask.TryGetMask(camera, width + 1, height, out unused));
            renderer.enabled = false;
            camera.Render();
            FrameworkCheck(report, "taa-mask-hidden-renderer-not-stale", !mask.TryGetMask(camera, width, height, out unused));
            renderer.enabled = true;
            camera.Render();
            FrameworkCheck(report, "taa-mask-reenable", mask.TryGetMask(camera, width, height, out unused));
            mask.surfaces[0].alphaCutoff = float.NaN;
            camera.Render();
            FrameworkCheck(report, "taa-mask-rejects-invalid-surface", !mask.TryGetMask(camera, width, height, out unused));
            mask.surfaces[0].alphaCutoff = 0f;
            mask.surfaces = Array.Empty<TemporalClassification.Surface>();
            camera.Render();
            FrameworkCheck(report, "taa-mask-empty-releases-target", !mask.TryGetMask(camera, width, height, out unused) &&
                typeof(TemporalClassification).GetField("_mask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mask) == null);
            mask.enabled = false;
            camera.Render();
            FrameworkCheck(report, "taa-mask-disable-detaches-buffer", camera.GetCommandBuffers(CameraEvent.BeforeImageEffects).Length == 0 && postMaterial.GetFloat("_TemporalMaskEnabled") == 0f);
            camera.targetTexture = null;
            target.Release();
        }
    }
}
