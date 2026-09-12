using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private static void SetComputeField(object target, string name, object value) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private RenderTexture ComputeTarget(int width, int height, RenderTextureFormat format, bool randomWrite)
        {
            var result = Own(new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear) {
                enableRandomWrite = randomWrite, filterMode = FilterMode.Point
            });
            result.Create(); return result;
        }

        private static float PixelError(Color[] a, Color[] b)
        {
            if (a.Length != b.Length) return float.PositiveInfinity;
            float error = 0;
            for (int i = 0; i < a.Length; i++) for (int c = 0; c < 4; c++)
            {
                if (float.IsNaN(a[i][c]) || float.IsNaN(b[i][c])) return float.PositiveInfinity;
                error = Mathf.Max(error, Mathf.Abs(a[i][c] - b[i][c]));
            }
            return error;
        }

        private void VerifyComputeDepthOracle(Report report)
        {
            var compute = Own(Instantiate(Resources.Load<ComputeShader>("SceneDepthHierarchy")));
            int kernel = compute.FindKernel("ReduceMinDepth");
            var material = Own(new Material(Resources.Load<Shader>("SceneDepthData")));
            foreach (var size in new[] { new Vector2Int(17, 9), new Vector2Int(1, 19), new Vector2Int(23, 1), new Vector2Int(1, 1), new Vector2Int(64, 48) })
            {
                int width = size.x, height = size.y, level = 0;
                var expected = new float[width * height]; var values = new Color[expected.Length];
                for (int i = 0; i < values.Length; i++)
                { expected[i] = .25f + ((i * 37 + 11) % 503) * .125f; values[i] = new Color(expected[i], 0, 0, 0); }
                // A unique closest sample at the odd final edge must reach the root.
                expected[expected.Length - 1] = .0625f; values[values.Length - 1].r = .0625f;
                Texture input = ResolveTexture(values, width, height);
                do
                {
                    int nextWidth = (width + 1) / 2, nextHeight = (height + 1) / 2;
                    var output = ComputeTarget(nextWidth, nextHeight, RenderTextureFormat.RFloat, true);
                    var raster = ComputeTarget(nextWidth, nextHeight, RenderTextureFormat.RFloat, false);
                    // Poison both outputs first so unwritten border threads cannot pass.
                    Graphics.Blit(Texture2D.whiteTexture, output); Graphics.Blit(Texture2D.whiteTexture, raster);
                    compute.SetVector("_DepthSize", new Vector4(width, height, nextWidth, nextHeight));
                    compute.SetTexture(kernel, "_DepthInput", input); compute.SetTexture(kernel, "_DepthOutput", output);
                    compute.Dispatch(kernel, (nextWidth + 7) / 8, (nextHeight + 7) / 8, 1);
                    Graphics.Blit(input, raster, material, 1);
                    var next = new float[nextWidth * nextHeight]; float error = 0;
                    Color[] actual = ReadSceneTarget(output), reference = ReadSceneTarget(raster);
                    for (int y = 0; y < nextHeight; y++) for (int x = 0; x < nextWidth; x++)
                    {
                        float minimum = float.PositiveInfinity;
                        for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                            minimum = Mathf.Min(minimum, expected[Mathf.Min(2 * y + dy, height - 1) * width + Mathf.Min(2 * x + dx, width - 1)]);
                        int i = y * nextWidth + x; next[i] = minimum;
                        error = Mathf.Max(error, Mathf.Abs(actual[i].r - minimum), Mathf.Abs(reference[i].r - minimum));
                    }
                    FrameworkCheck(report, "compute-depth-oracle-" + size.x + "x" + size.y + "-level-" + level,
                        output.enableRandomWrite && error == 0, error);
                    expected = next; input = output; width = nextWidth; height = nextHeight; level++;
                    RenderTexture.active = null; raster.Release();
                } while (width > 1 || height > 1);
                FrameworkCheck(report, "compute-depth-preserves-final-edge-" + size.x + "x" + size.y, expected[0] == .0625f);
            }
        }

        private void VerifySsrComputeEquivalence(Report report, ScreenSpaceReflection ssr, Camera camera, Color[] computePixels, RenderTexture source,
            Color[] oldHistoryColor, Color[] oldHistoryDepth)
        {
            var material = SsrField<Material>(ssr, "_material");
            var compute = SsrField<ComputeShader>(ssr, "_compute");
            FrameworkCheck(report, "ssr-compute-equivalence-input-color-identical", ScenePixelsEqual(oldHistoryColor, ReadSceneTarget(SsrField<RenderTexture>(ssr, "_historyColor"))));
            FrameworkCheck(report, "ssr-compute-equivalence-input-depth-identical", ScenePixelsEqual(oldHistoryDepth, ReadSceneTarget(SsrField<RenderTexture>(ssr, "_historyDepth"))));
            FrameworkCheck(report, "ssr-compute-real-kernel-and-uav-used", ssr.ActiveBackend == SceneShaderBackend.Compute &&
                ssr.ActiveHierarchyBackend == SceneShaderBackend.Compute && ssr.ComputeDispatchCount == 1 &&
                SsrField<RenderTexture>(ssr, "_reflection").enableRandomWrite && compute != null &&
                compute != Resources.Load<ComputeShader>("ScreenSpaceReflection"));
            var raster = ComputeTarget(source.width, source.height, RenderTextureFormat.ARGBHalf, false);
            // Static second capture: color/depth history is identical before and
            // after consumption; evaluate the reference shader on those inputs.
            Graphics.Blit(source, raster, material, 1);
            var rasterPixels = ReadSceneTarget(raster);
            float error = PixelError(computePixels, rasterPixels);
            int mismatchedHits = 0, wrongColors = 0;
            for (int i = 0; i < computePixels.Length; i++)
            {
                if ((computePixels[i].a > .01f) != (rasterPixels[i].a > .01f))
                {
                    mismatchedHits++;
                    Debug.Log("[ComputeTraceBoundary] pixel=" + (i % source.width) + "," + (i / source.width) + "; compute=" + computePixels[i] + "; raster=" + rasterPixels[i]);
                }
                if (computePixels[i].a > .01f && rasterPixels[i].a > .01f &&
                    Mathf.Max(Mathf.Abs(computePixels[i].r - rasterPixels[i].r), Mathf.Abs(computePixels[i].g - rasterPixels[i].g)) > .001f) wrongColors++;
            }
            Debug.Log("[ComputeTraceEquivalence] rasterHits=" + SsrHitCount(rasterPixels) + "; computeHits=" + SsrHitCount(computePixels) +
                "; mismatchedHits=" + mismatchedHits + "; wrongCommonColors=" + wrongColors + "; maxError=" + error);
            SaveSsrPreview("compute-trace-raster-reference", rasterPixels, source.width, source.height, true);
            FrameworkCheck(report, "ssr-compute-raster-same-input-hdr-confidence", error < .001f && SsrHitCount(computePixels) > 30, error);
            var geometry = SsrField<SceneDepthData>(ssr, "_geometry");
            var sceneCamera = SsrField<Camera>(ssr, "_sceneCamera");
            geometry.TryGetFrame(sceneCamera, source.width, source.height, out var frame);
            FrameworkCheck(report, "ssr-compute-hierarchy-dispatched-per-level", geometry.ComputeDispatchCount == frame.DepthLevelCount - 1 && geometry.ComputeDispatchCount > 0);
            VerifySceneDepthHierarchy(report, frame, "compute-real-geometry");
            // All coverage must suppress all rays; removing it recovers the
            // exact prior result, so a permanently zero compute kernel fails.
            var originalMask = material.GetTexture("_SsrPlanarCoverage");
            var fullCoverage = ComputeTarget(source.width, source.height, RenderTextureFormat.ARGBHalf, false);
            Graphics.Blit(Texture2D.whiteTexture, fullCoverage);
            material.SetTexture("_SsrPlanarCoverage", fullCoverage); material.SetFloat("_SsrPlanarAvailable", 1);
            typeof(ScreenSpaceReflection).GetMethod("DispatchTrace", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ssr, null);
            FrameworkCheck(report, "ssr-compute-planar-mask-suppresses-trace", SsrHitCount(SsrPixels(ssr, camera)) == 0);
            material.SetTexture("_SsrPlanarCoverage", originalMask); material.SetFloat("_SsrPlanarAvailable", 0);
            typeof(ScreenSpaceReflection).GetMethod("DispatchTrace", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ssr, null);
            FrameworkCheck(report, "ssr-compute-planar-negative-control-recovers", ScenePixelsEqual(computePixels, SsrPixels(ssr, camera)));
            RenderTexture.active = null; raster.Release(); fullCoverage.Release();
        }

        private void VerifySsrComputeLifecycle(Report report, ScreenSpaceReflection ssr, Camera camera, RenderTexture source)
        {
            var originalAsset = SsrField<ComputeShader>(ssr, "_computeAsset");
            var oldOutput = SsrField<RenderTexture>(ssr, "_reflection");
            oldOutput.Release();
            FrameworkCheck(report, "ssr-compute-lost-output-rejected", !ssr.TryGetReflection(camera, out _));
            camera.Render(); camera.Render();
            FrameworkCheck(report, "ssr-compute-lost-output-recreated", SsrField<RenderTexture>(ssr, "_reflection").IsCreated() && SsrHitCount(SsrPixels(ssr, camera)) > 10);
            var replacement = Own(new RenderTexture(source.width, source.height, 24, RenderTextureFormat.ARGBHalf)); replacement.Create();
            camera.targetTexture = replacement;
            FrameworkCheck(report, "ssr-compute-same-size-target-identity-guard", !ssr.TryGetReflection(camera, out _));
            camera.targetTexture = source;
            SetComputeField(ssr, "_computeAsset", null); ssr.allowComputeFallback = true; camera.Render();
            FrameworkCheck(report, "ssr-compute-missing-resource-raster-fallback-cold", ssr.ActiveBackend == SceneShaderBackend.Raster &&
                ssr.ComputeFallbackReason != null && !SsrField<RenderTexture>(ssr, "_reflection").enableRandomWrite && SsrHitCount(SsrPixels(ssr, camera)) == 0);
            camera.Render();
            FrameworkCheck(report, "ssr-compute-fallback-produces-real-hits", SsrHitCount(SsrPixels(ssr, camera)) > 10 && SsrField<ComputeShader>(ssr, "_compute") == null);
            ssr.allowComputeFallback = false; camera.Render();
            FrameworkCheck(report, "ssr-compute-strict-missing-resource-fails-closed", ssr.UnavailableReason != null && !ssr.TryGetReflection(camera, out _) && SsrField<RenderTexture>(ssr, "_reflection") == null);
            SetComputeField(ssr, "_computeAsset", originalAsset); camera.Render(); camera.Render();
            FrameworkCheck(report, "ssr-compute-resource-restored", ssr.ActiveBackend == SceneShaderBackend.Compute && ssr.ComputeFallbackReason == null && SsrHitCount(SsrPixels(ssr, camera)) > 10);
            var geometry = SsrField<SceneDepthData>(ssr, "_geometry");
            var depthAsset = typeof(SceneDepthData).GetField("_computeAsset", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(geometry);
            SetComputeField(geometry, "_computeAsset", null); ssr.allowComputeFallback = true; camera.Render();
            FrameworkCheck(report, "ssr-compute-depth-resource-independent-fallback", ssr.ActiveBackend == SceneShaderBackend.Compute &&
                ssr.ActiveHierarchyBackend == SceneShaderBackend.Raster && geometry.ComputeDispatchCount == 0 && ssr.ComputeFallbackReason != null && SsrHitCount(SsrPixels(ssr, camera)) > 10);
            ssr.allowComputeFallback = false; camera.Render();
            FrameworkCheck(report, "ssr-compute-strict-depth-kernel-missing", !ssr.TryGetReflection(camera, out _) && !ssr.HistoryAvailable);
            SetComputeField(geometry, "_computeAsset", depthAsset); camera.Render(); camera.Render();
            FrameworkCheck(report, "ssr-compute-depth-kernel-restored", ssr.ActiveHierarchyBackend == SceneShaderBackend.Compute && SsrHitCount(SsrPixels(ssr, camera)) > 10);
            ssr.backend = (SceneShaderBackend)123; camera.Render();
            FrameworkCheck(report, "ssr-compute-invalid-backend-rejected", ssr.UnavailableReason != null && SsrField<ComputeShader>(ssr, "_compute") == null);
            ssr.backend = SceneShaderBackend.Compute; camera.Render(); camera.Render();
            VerifySsrComputeCameraIsolation(report, ssr, camera, source);
            var recoveredGeometry = SsrField<SceneDepthData>(ssr, "_geometry");
            var recoveredCamera = SsrField<Camera>(ssr, "_sceneCamera");
            recoveredGeometry.TryGetFrame(recoveredCamera, source.width, source.height, out var recoveredFrame);
            recoveredFrame.GetDepthLevel(1).Release();
            FrameworkCheck(report, "ssr-compute-lost-hierarchy-level-rejected", !recoveredGeometry.TryGetFrame(recoveredCamera, source.width, source.height, out _));
            camera.Render(); camera.Render();
            FrameworkCheck(report, "ssr-compute-lost-hierarchy-level-recreated", recoveredGeometry.TryGetFrame(recoveredCamera, source.width, source.height, out recoveredFrame) &&
                recoveredFrame.GetDepthLevel(1).IsCreated() && recoveredFrame.GetDepthLevel(1).enableRandomWrite && SsrHitCount(SsrPixels(ssr, camera)) > 10);
            VerifySsrComputeResolutionEquivalence(report, ssr, camera, source);
            ssr.enabled = false;
            FrameworkCheck(report, "ssr-compute-disable-releases-kernel-instance", SsrField<ComputeShader>(ssr, "_compute") == null && ssr.ComputeDispatchCount == 0);
            ssr.enabled = true; camera.Render(); camera.Render();
            FrameworkCheck(report, "ssr-compute-reenable-restores-backend", ssr.ActiveBackend == SceneShaderBackend.Compute && ssr.ComputeDispatchCount == 1 && SsrHitCount(SsrPixels(ssr, camera)) > 10);
            replacement.Release();
        }

        private void VerifySsrComputeCameraIsolation(Report report, ScreenSpaceReflection first, Camera firstCamera, RenderTexture firstTarget)
        {
            var before = SsrPixels(first, firstCamera);
            var host = Own(new GameObject("Second compute camera")); var camera = host.AddComponent<Camera>();
            camera.CopyFrom(firstCamera); camera.enabled = false;
            camera.transform.SetPositionAndRotation(firstCamera.transform.position + new Vector3(.7f, 0, 0), firstCamera.transform.rotation);
            var target = Own(new RenderTexture(83, 63, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create();
            camera.targetTexture = target;
            var second = host.AddComponent<ScreenSpaceReflection>(); second.reflectionsEnabled = true;
            second.backend = SceneShaderBackend.Compute; second.allowComputeFallback = false;
            second.sceneLayers = first.sceneLayers; second.surfaces = first.surfaces; second.maximumSteps = 512;
            var hook = host.AddComponent<SsrHostProbe>(); hook.reflection = second;
            camera.Render(); camera.Render();
            FrameworkCheck(report, "ssr-compute-two-camera-kernel-and-target-isolation", SsrHitCount(SsrPixels(second, camera)) > 10 &&
                SsrField<ComputeShader>(second, "_compute") != SsrField<ComputeShader>(first, "_compute") &&
                SsrField<RenderTexture>(second, "_reflection") != SsrField<RenderTexture>(first, "_reflection") &&
                ScenePixelsEqual(before, SsrPixels(first, firstCamera)) && !second.TryGetReflection(firstCamera, out _));
            firstCamera.Render();
            FrameworkCheck(report, "ssr-compute-interleaved-camera-restores-identical-output", ScenePixelsEqual(before, SsrPixels(first, firstCamera)));
            second.enabled = false; camera.targetTexture = null; host.SetActive(false); target.Release();
        }

        private void VerifySsrComputeResolutionEquivalence(Report report, ScreenSpaceReflection ssr, Camera camera, RenderTexture originalTarget)
        {
            float aspect = camera.aspect;
            foreach (var size in new[] { new Vector2Int(161, 119), new Vector2Int(1025, 769) })
            {
                var target = Own(new RenderTexture(size.x, size.y, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create();
                camera.targetTexture = target; camera.aspect = (float)size.x / size.y; camera.Render(); camera.Render();
                var computePixels = SsrPixels(ssr, camera);
                var raster = ComputeTarget(size.x, size.y, RenderTextureFormat.ARGBHalf, false);
                Graphics.Blit(target, raster, SsrField<Material>(ssr, "_material"), 1);
                var rasterPixels = ReadSceneTarget(raster); float error = PixelError(computePixels, rasterPixels);
                int differentHits = 0;
                for (int i = 0; i < computePixels.Length; i++) if ((computePixels[i].a > .01f) != (rasterPixels[i].a > .01f)) differentHits++;
                Debug.Log("[ComputeTraceResolution] size=" + size + "; hits=" + SsrHitCount(computePixels) + "; differentHits=" + differentHits + "; maxError=" + error);
                FrameworkCheck(report, "ssr-compute-resolution-equivalence-" + size.x + "x" + size.y,
                    error < .001f && differentHits == 0 && SsrHitCount(computePixels) > 50, error);
                RenderTexture.active = null; raster.Release(); camera.targetTexture = originalTarget; target.Release();
            }
            camera.aspect = aspect; camera.Render(); camera.Render();
        }
    }
}
