using System;
using System.Collections;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyFsr(Report report)
        {
            yield return null;
            var savedActive = RenderTexture.active; bool savedSrgb = GL.sRGBWrite;
            var compute = new FsrRenderer(); var raster = new FsrRenderer();
            try
            {
                void Check(string name, bool accepted, float value = 0) => FrameworkCheck(report, "fsr-" + name, accepted, value);
                int scenario = 0;
                foreach (var output in new[] { new Vector2Int(65, 49), new Vector2Int(1, 1), new Vector2Int(2, 7), new Vector2Int(13, 2) })
                foreach (FsrQuality quality in Enum.GetValues(typeof(FsrQuality)))
                foreach (FsrInputEncoding encoding in Enum.GetValues(typeof(FsrInputEncoding)))
                foreach (bool sharpen in new[] { false, true })
                {
                    var settings = new FsrSettings { enabled = true, quality = quality, encoding = encoding, sharpen = sharpen, backend = FsrBackend.Compute, allowRasterFallback = false };
                    settings.TryGetRenderSize(output, out var input);
                    // Padding contains deliberately unrelated color. Crop must clamp to
                    // viewport, including all boundary taps, rather than reading padding.
                    int width = input.x + 5, height = input.y + 3;
                    var pixels = new Color[width * height];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(17, 3, 49, .8f);
                    for (int y = 0; y < input.y; y++) for (int x = 0; x < input.x; x++)
                    {
                        float u = x / (float)Mathf.Max(1, input.x - 1), v = y / (float)Mathf.Max(1, input.y - 1);
                        var color = new Color(.05f + .8f * u, .1f + .7f * v, x * 3 > y * 2 ? .85f : .12f, .1f + .7f * u * v);
                        if (encoding == FsrInputEncoding.LinearHdr) { color.r *= 8; color.g *= 4; color.b *= 2; }
                        pixels[(y + 1) * width + x + 2] = color;
                    }
                    var texture = Own(new Texture2D(width, height, TextureFormat.RGBAFloat, false, true));
                    texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Repeat;
                    texture.SetPixels(pixels); texture.Apply();
                    var viewport = new RectInt(2, 1, input.x, input.y); string name = "scalar-" + scenario++;
                    var expected = FsrScalar(pixels, width, viewport, output, settings, out var prepared, out var expanded);
                    var active = RenderTexture.active; bool srgb = GL.sRGBWrite;
                    bool ok = compute.TryRender(texture, viewport, output, settings, out var gpu);
                    Check(name + "-actual-compute", ok && compute.Dispatches == 3 && compute.DrawCalls == 0 && gpu.IsCurrent);
                    if (!ok) throw new InvalidOperationException(compute.UnavailableReason);
                    var actual = ReadSceneTarget(gpu.color);
                    var gpuPrepared = ReadSceneTarget(gpu.prepared); var gpuExpanded = ReadSceneTarget(gpu.expanded);
                    float preparation = PixelError(prepared, gpuPrepared), expansion = PixelError(expanded, gpuExpanded);
                    float error = FsrRelativeError(expected, actual, encoding == FsrInputEncoding.LinearHdr);
                    Check(name + "-whole-prepared-rgba", preparation <= .00001f, preparation);
                    Check(name + "-whole-easu-rgba", expansion <= .0005f, expansion);
                    Check(name + "-whole-decoded-rgba", error <= (encoding == FsrInputEncoding.LinearHdr ? .02f : .0005f), error);
                    if (preparation > .00001f || expansion > .0005f || error > (encoding == FsrInputEncoding.LinearHdr ? .02f : .0005f))
                    {
                        FsrDump(name + "-prepared", gpuPrepared); FsrDump(name + "-easu", gpuExpanded); FsrDump(name + "-easu-scalar", expanded);
                        FsrDump(name + "-actual", actual); FsrDump(name + "-expected", expected);
                    }
                    settings.backend = FsrBackend.Raster;
                    ok = raster.TryRender(texture, viewport, output, settings, out var fallback);
                    Check(name + "-actual-raster", ok && raster.DrawCalls == 3 && raster.Dispatches == 0 && fallback.IsCurrent);
                    if (!ok) throw new InvalidOperationException(raster.UnavailableReason);
                    float backends = FsrRelativeError(actual, ReadSceneTarget(fallback.color), encoding == FsrInputEncoding.LinearHdr);
                    Check(name + "-whole-backends", backends <= .00005f, backends);
                    Check(name + "-source-and-global-state", texture.filterMode == FilterMode.Point && texture.wrapMode == TextureWrapMode.Repeat && RenderTexture.active == active && GL.sRGBWrite == srgb);
                    Check(name + "-target-budget", compute.TargetCount == 3 && compute.EstimatedTargetBytes == FsrSettings.EstimateTargetBytes(input, output));
                    if (output.x == 65 && quality == FsrQuality.Quality && encoding == FsrInputEncoding.LinearLdr)
                    {
                        SaveSsrPreview("fsr-" + name + "-actual", actual, output.x, output.y, true);
                        SaveSsrPreview("fsr-" + name + "-scalar", expected, output.x, output.y, true);
                    }
                    Destroy(texture); _owned.Remove(texture);
                }
                VerifyFsrConstants(report, compute);
                VerifyFsrPatterns(report, compute, raster);
                VerifyFsrSourceFormats(report, compute, raster);
                VerifyFsrLifetime(report, compute, raster);
            }
            finally
            {
                RenderTexture.active = null;
                compute.Dispose(); raster.Dispose();
                foreach (var item in _owned) { if (item is RenderTexture target) target.Release(); if (item != null) Destroy(item); }
                _owned.Clear();
                RenderTexture.active = savedActive != null && savedActive.IsCreated() ? savedActive : null; GL.sRGBWrite = savedSrgb;
            }
        }
        private static float FsrRelativeError(Color[] expected, Color[] actual, bool relative)
        {
            if (expected.Length != actual.Length) return float.PositiveInfinity;
            float result = 0;
            for (int i = 0; i < expected.Length; i++) for (int c = 0; c < 4; c++)
            {
                if (float.IsNaN(actual[i][c]) || float.IsInfinity(actual[i][c])) return float.PositiveInfinity;
                float error = Mathf.Abs(actual[i][c] - expected[i][c]);
                if (relative && c < 3) error /= Mathf.Max(1, Mathf.Abs(expected[i][c]));
                result = Mathf.Max(result, error);
            }
            return result;
        }
        private void VerifyFsrSourceFormats(Report report, FsrRenderer compute, FsrRenderer raster)
        {
            var texture = Own(new Texture2D(17, 13, TextureFormat.RGBAFloat, false, true));
            var pixels = new Color[17 * 13];
            for (int y = 0; y < 13; y++) for (int x = 0; x < 17; x++) pixels[y * 17 + x] = new Color(.05f + x * .045f, .1f + y * .057f, (x + y) % 3 == 0 ? .15f : .85f, .2f + x * .04f);
            texture.SetPixels(pixels); texture.Apply();
            foreach (var format in new[] { RenderTextureFormat.ARGBFloat, RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGB32, RenderTextureFormat.RGB111110Float })
            {
                var source = Own(new RenderTexture(17, 13, 0, format, RenderTextureReadWrite.Linear)); source.Create(); Graphics.Blit(texture, source);
                var settings = new FsrSettings { enabled = true, backend = FsrBackend.Compute, allowRasterFallback = false };
                var output = new Vector2Int(31, 23); var viewport = new RectInt(0, 0, 17, 13);
                var reference = FsrScalar(ReadSceneTarget(source), 17, viewport, output, settings, out _, out _);
                bool ok = compute.TryRender(source, viewport, output, settings, out var frame);
                FrameworkCheck(report, "fsr-source-format-" + format + "-actual-compute", ok && frame.IsCurrent);
                if (!ok) throw new InvalidOperationException(compute.UnavailableReason);
                float error = PixelError(reference, ReadSceneTarget(frame.color));
                FrameworkCheck(report, "fsr-source-format-" + format + "-whole-scalar", error <= .0005f, error);
                settings.backend = FsrBackend.Raster; ok = raster.TryRender(source, viewport, output, settings, out var other);
                FrameworkCheck(report, "fsr-source-format-" + format + "-actual-raster", ok && other.IsCurrent);
                if (!ok) throw new InvalidOperationException(raster.UnavailableReason);
                float pair = PixelError(ReadSceneTarget(frame.color), ReadSceneTarget(other.color));
                FrameworkCheck(report, "fsr-source-format-" + format + "-whole-backends", pair <= .00005f, pair);
            }
        }
        private void VerifyFsrConstants(Report report, FsrRenderer renderer)
        {
            int index = 0;
            foreach (FsrInputEncoding encoding in Enum.GetValues(typeof(FsrInputEncoding)))
            foreach (bool sharpen in new[] { false, true })
            foreach (float level in new[] { 0f, .25f, 1f, 8f, 64000f })
            {
                var settings = new FsrSettings { enabled = true, backend = FsrBackend.Compute, allowRasterFallback = false, encoding = encoding, sharpen = sharpen };
                var value = new Color(level, level * .5f, level * .125f, .3f);
                var source = new Color[9 * 7]; for (int i = 0; i < source.Length; i++) source[i] = value;
                var texture = Own(new Texture2D(9, 7, TextureFormat.RGBAFloat, false, true)); texture.SetPixels(source); texture.Apply();
                var output = new Vector2Int(17, 13); string name = "fsr-constant-" + index++;
                var reference = FsrScalar(source, 9, new RectInt(0, 0, 9, 7), output, settings, out _, out _);
                bool ok = renderer.TryRender(texture, new RectInt(0, 0, 9, 7), output, settings, out var frame);
                FrameworkCheck(report, name + "-rendered", ok && frame.IsCurrent);
                if (!ok) throw new InvalidOperationException(renderer.UnavailableReason);
                var actual = ReadSceneTarget(frame.color); bool hdr = encoding == FsrInputEncoding.LinearHdr;
                float oracleError = FsrRelativeError(reference, actual, hdr);
                FrameworkCheck(report, name + "-whole-scalar-rgba", oracleError <= (hdr ? .02f : .0005f), oracleError);
                var ideal = new Color[actual.Length];
                if (!hdr) { value.r = Mathf.Clamp01(value.r); value.g = Mathf.Clamp01(value.g); value.b = Mathf.Clamp01(value.b); }
                for (int i = 0; i < ideal.Length; i++) ideal[i] = value;
                float constantError = FsrRelativeError(ideal, actual, hdr);
                FrameworkCheck(report, name + "-constant-signal-preserved", constantError <= (hdr ? .02f : .0005f), constantError);
                if (constantError > (hdr ? .02f : .0005f)) { FsrDump(name + "-actual", actual); FsrDump(name + "-expected-constant", ideal); }
                Destroy(texture); _owned.Remove(texture);
            }
        }
        private void FsrDump(string name, Color[] values)
        {
            // Test-only raw GPU evidence. Never called by the production renderer.
            using (var stream = System.IO.File.Create(System.IO.Path.Combine(_directory, "fsr-" + name + ".rgba32f")))
            using (var writer = new System.IO.BinaryWriter(stream))
                foreach (var value in values) for (int c = 0; c < 4; c++) writer.Write(value[c]);
        }
        private void VerifyFsrLifetime(Report report, FsrRenderer renderer, FsrRenderer sibling)
        {
            void Check(string name, bool value, float error = 0) => FrameworkCheck(report, "fsr-lifetime-" + name, value, error);
            var texture = Own(new Texture2D(9, 7, TextureFormat.RGBAFloat, false, true));
            var pixels = new Color[63];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(.1f + (i % 9) * .08f, .1f + (i / 9) * .1f, .25f, .7f);
            texture.SetPixels(pixels); texture.Apply();
            var settings = new FsrSettings { enabled = true, backend = FsrBackend.Compute, allowRasterFallback = false };
            var viewport = new RectInt(0, 0, 9, 7); var output = new Vector2Int(17, 13);
            FsrRenderer.Frame Seed()
            {
                if (!renderer.TryRender(texture, viewport, output, settings, out var frame)) throw new InvalidOperationException(renderer.UnavailableReason);
                return frame;
            }
            void Reject(string name, Func<bool> attempt)
            {
                var old = Seed(); bool accepted = attempt();
                Check(name, !accepted && !old.IsCurrent && renderer.TargetCount == 0 && renderer.EstimatedTargetBytes == 0 &&
                    !renderer.TryGetFrame(out _) && !string.IsNullOrEmpty(renderer.UnavailableReason) && texture != null);
                var restored = Seed(); Check(name + "-recovery", restored.IsCurrent && renderer.TargetCount == 3);
            }
            var first = Seed(); var original = ReadSceneTarget(first.color);
            var rasterSettings = new FsrSettings { enabled = true, backend = FsrBackend.Raster, encoding = FsrInputEncoding.PerceptualGamma2Ldr, sharpen = false };
            bool siblingOk = sibling.TryRender(texture, new RectInt(1, 1, 7, 5), new Vector2Int(13, 9), rasterSettings, out var siblingFrame);
            float siblingError = PixelError(original, ReadSceneTarget(first.color));
            Check("interleaved-owner-isolation", siblingOk && siblingFrame.IsCurrent && first.IsCurrent && siblingError == 0, siblingError);
            var second = Seed();
            Check("next-call-invalidates-only-its-own-frame", !first.IsCurrent && second.IsCurrent && siblingFrame.IsCurrent);
            var full = new Vector2Int(18, 14);
            Check("grow", renderer.TryRender(texture, viewport, full, settings, out var grown) && grown.IsCurrent && !second.IsCurrent && grown.color.width == 18 && grown.color.height == 14);
            Check("shrink-one-to-one", renderer.TryRender(texture, viewport, new Vector2Int(9, 7), settings, out var shrunk) && shrunk.IsCurrent && !grown.IsCurrent && shrunk.color.width == 9);
            var lost = Seed(); lost.prepared.Release();
            Check("lost-attachment-invalidates-frame", !lost.IsCurrent && !renderer.TryGetFrame(out _));
            Check("lost-attachment-recreated", Seed().IsCurrent && !lost.IsCurrent);
            Reject("null-source", () => renderer.TryRender(null, viewport, output, settings, out _));
            Reject("negative-viewport", () => renderer.TryRender(texture, new RectInt(-1, 0, 9, 7), output, settings, out _));
            Reject("empty-viewport", () => renderer.TryRender(texture, new RectInt(0, 0, 0, 7), output, settings, out _));
            Reject("outside-viewport", () => renderer.TryRender(texture, new RectInt(1, 0, 9, 7), output, settings, out _));
            Reject("integer-overflow-viewport", () => renderer.TryRender(texture, new RectInt(int.MaxValue, 0, 9, 7), output, settings, out _));
            Reject("downscale", () => renderer.TryRender(texture, viewport, new Vector2Int(8, 7), settings, out _));
            Reject("over-two-times", () => renderer.TryRender(texture, viewport, new Vector2Int(19, 13), settings, out _));
            Reject("zero-output", () => renderer.TryRender(texture, viewport, Vector2Int.zero, settings, out _));
            Reject("oversize-output", () => renderer.TryRender(texture, viewport, new Vector2Int(4097, 13), settings, out _));
            foreach (var name in new[] { "quality", "backend", "encoding", "nan-sharpness", "infinite-sharpness", "negative-sharpness", "excess-sharpness", "zero-budget", "excess-budget" })
            {
                var invalid = new FsrSettings { enabled = true };
                if (name == "quality") invalid.quality = (FsrQuality)99;
                if (name == "backend") invalid.backend = (FsrBackend)99;
                if (name == "encoding") invalid.encoding = (FsrInputEncoding)99;
                if (name == "nan-sharpness") invalid.sharpnessStops = float.NaN;
                if (name == "infinite-sharpness") invalid.sharpnessStops = float.PositiveInfinity;
                if (name == "negative-sharpness") invalid.sharpnessStops = -.1f;
                if (name == "excess-sharpness") invalid.sharpnessStops = 2.6f;
                if (name == "zero-budget") invalid.memoryBudgetMiB = 0;
                if (name == "excess-budget") invalid.memoryBudgetMiB = 1025;
                Reject(name, () => renderer.TryRender(texture, viewport, output, invalid, out _));
            }
            var large = Own(new Texture2D(256, 256, TextureFormat.RGBAFloat, false, true)); large.Apply();
            var budget = new FsrSettings { enabled = true, memoryBudgetMiB = 1 };
            Reject("budget-before-allocation", () => renderer.TryRender(large, new RectInt(0, 0, 256, 256), new Vector2Int(512, 512), budget, out _));
            var srgb = Own(new Texture2D(9, 7, TextureFormat.RGBA32, false, false)); srgb.Apply();
            Reject("srgb-metadata", () => renderer.TryRender(srgb, viewport, output, settings, out _));
            var mip = Own(new Texture2D(9, 7, TextureFormat.RGBAFloat, true, true)); mip.Apply();
            Reject("mip-input", () => renderer.TryRender(mip, viewport, output, settings, out _));
            var scalar = Own(new Texture2D(9, 7, TextureFormat.RFloat, false, true)); scalar.Apply();
            Reject("unsupported-scalar-format", () => renderer.TryRender(scalar, viewport, output, settings, out _));
            var lostSource = Own(new RenderTexture(9, 7, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); lostSource.Create(); lostSource.Release();
            Reject("released-source", () => renderer.TryRender(lostSource, viewport, output, settings, out _));
            var msaa = Own(new RenderTexture(9, 7, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { antiAliasing = 2 });
            msaa.Create(); Reject("msaa-source", () => renderer.TryRender(msaa, viewport, output, settings, out _));
            var alias = Seed();
            Check("own-output-alias-rejected", !renderer.TryRender(alias.color, new RectInt(0, 0, 17, 13), output, settings, out _) && !alias.IsCurrent && renderer.TargetCount == 0);
            var disabled = Seed();
            Check("disabled-release", !renderer.TryRender(texture, viewport, output, new FsrSettings(), out _) && !disabled.IsCurrent && renderer.TargetCount == 0 && renderer.UnavailableReason == null);
            var missing = Seed();
            Check("null-settings-release", !renderer.TryRender(texture, viewport, output, null, out _) && !missing.IsCurrent && renderer.TargetCount == 0);
            var disposed = Seed(); renderer.Dispose(); renderer.Dispose();
            Check("idempotent-dispose", !disposed.IsCurrent && renderer.TargetCount == 0 && !renderer.TryGetFrame(out _) && siblingFrame.IsCurrent);
            Check("reuse-after-dispose", Seed().IsCurrent);
            var callerActive = Own(new RenderTexture(3, 2, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); callerActive.Create();
            var saved = RenderTexture.active; bool savedSrgb = GL.sRGBWrite;
            try
            {
                RenderTexture.active = callerActive; GL.sRGBWrite = true; Seed();
                Check("restore-nondefault-active-and-srgb", RenderTexture.active == callerActive && GL.sRGBWrite);
            }
            finally { RenderTexture.active = saved; GL.sRGBWrite = savedSrgb; }
        }
        private void VerifyFsrPatterns(Report report, FsrRenderer compute, FsrRenderer raster)
        {
            int index = 0;
            foreach (int pattern in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (FsrInputEncoding encoding in Enum.GetValues(typeof(FsrInputEncoding)))
            foreach (bool accurate in new[] { true, false })
            foreach (float stops in new[] { 0f, .2f, 1f, 2.5f })
            {
                const int width = 33, height = 25; var pixels = new Color[width * height];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    Color value;
                    switch (pattern)
                    {
                        case 0: value = ((x + y) & 1) == 0 ? Color.white : Color.black; break;
                        case 1: value = x * 3 > y * 2 ? Color.white : Color.black; break;
                        case 2: value = x == 16 && y == 12 ? Color.white : Color.black; break;
                        case 3: value = new Color((x % 8) / 7f, (y % 8) / 7f, ((x + y) % 8) / 7f, 1); break;
                        case 4: value = new Color(x % 3 == 0 ? float.NaN : -.2f, x % 3 == 1 ? float.PositiveInfinity : .3f,
                            x % 3 == 2 ? float.NegativeInfinity : .6f, y % 2 == 0 ? float.NaN : 2); break;
                        default: value = new Color(8 * (x % 4), 4 * (y % 4), 2 * ((x + y) % 4), 1); break;
                    }
                    if (pattern != 4) value.a = .2f + .7f * ((x + y) % 7) / 6;
                    pixels[y * width + x] = value;
                }
                var texture = Own(new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)); texture.SetPixels(pixels); texture.Apply();
                var output = new Vector2Int(65, 49); var viewport = new RectInt(0, 0, width, height);
                var settings = new FsrSettings { enabled = true, backend = FsrBackend.Compute, allowRasterFallback = false,
                    encoding = encoding, accurateRcasNormalization = accurate, sharpnessStops = stops };
                string name = "fsr-pattern-" + index++;
                var expected = FsrScalar(pixels, width, viewport, output, settings, out var prepared, out var expanded);
                bool ok = compute.TryRender(texture, viewport, output, settings, out var gpu);
                FrameworkCheck(report, name + "-actual-compute", ok && gpu.IsCurrent && compute.Dispatches == 3);
                if (!ok) throw new InvalidOperationException(compute.UnavailableReason);
                var actual = ReadSceneTarget(gpu.color); var gpuExpanded = ReadSceneTarget(gpu.expanded); bool hdr = encoding == FsrInputEncoding.LinearHdr;
                float prepareError = PixelError(prepared, ReadSceneTarget(gpu.prepared)), easuError = PixelError(expanded, gpuExpanded), error = FsrRelativeError(expected, actual, hdr);
                FrameworkCheck(report, name + "-whole-prepared-rgba", prepareError <= .00001f, prepareError);
                FrameworkCheck(report, name + "-whole-easu-rgba", easuError <= .0005f, easuError);
                FrameworkCheck(report, name + "-whole-final-rgba", error <= (hdr ? .02f : .0005f), error);
                if (easuError > .0005f || error > (hdr ? .02f : .0005f))
                {
                    FsrDump(name + "-prepared", ReadSceneTarget(gpu.prepared)); FsrDump(name + "-easu", gpuExpanded);
                    FsrDump(name + "-easu-scalar", expanded); FsrDump(name + "-actual", actual); FsrDump(name + "-expected", expected);
                }
                settings.backend = FsrBackend.Raster;
                ok = raster.TryRender(texture, viewport, output, settings, out var nativeRaster);
                FrameworkCheck(report, name + "-actual-raster", ok && nativeRaster.IsCurrent && raster.DrawCalls == 3);
                if (!ok) throw new InvalidOperationException(raster.UnavailableReason);
                float backendError = FsrRelativeError(actual, ReadSceneTarget(nativeRaster.color), hdr);
                FrameworkCheck(report, name + "-whole-backends", backendError <= .00005f, backendError);
                Destroy(texture); _owned.Remove(texture);
            }
        }
    }
}
