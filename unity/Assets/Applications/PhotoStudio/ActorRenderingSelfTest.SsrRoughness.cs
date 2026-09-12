using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        // Independent scalar oracle for the documented separable tent, including
        // receiver barriers and straight-radiance/confidence normalization.
        private static Color[] RoughnessOracle(Color[] input, Color[] receivers, Color[] normals, Color[] depths,
            Color[] planar, int width, int height, Vector4 options, bool vertical)
        {
            var output = new Color[input.Length];
            Vector3 Position(int i) => new Vector3((i % width + .5f) * 2 / width - 1,
                ((i / width + .5f) * 2 / height - 1) * (SystemInfo.graphicsUVStartsAtTop ? -1 : 1), -depths[i].r);
            Vector3 Normal(int i) => new Vector3(normals[i].r * 2 - 1, normals[i].g * 2 - 1, normals[i].b * 2 - 1).normalized;
            for (int i = 0; i < input.Length; i++)
            {
                if (receivers[i].r < .5f || receivers[i].b < .5f || input[i].a <= 1e-5f || planar[i].a > 1e-5f) continue;
                float radius = options.x * Mathf.Pow(1 - receivers[i].g, 2);
                if (radius <= 1e-5f) { output[i] = input[i]; continue; }
                Vector3 color = Vector3.zero, normal = Normal(i), position = Position(i);
                float weightSum = 0, confidenceSum = 0;
                for (int offset = -12; offset <= 12; offset++)
                {
                    int x = i % width + (vertical ? 0 : offset), y = i / width + (vertical ? offset : 0);
                    float weight = Mathf.Max(0, radius + 1 - Mathf.Abs(offset));
                    if (weight == 0 || x < 0 || x >= width || y < 0 || y >= height) continue;
                    int j = y * width + x;
                    if (receivers[j].r < .5f || Mathf.Abs(receivers[j].b - receivers[i].b) > .25f ||
                        Mathf.Abs(receivers[j].g - receivers[i].g) > options.w || planar[j].a > 1e-5f) continue;
                    Vector3 otherNormal = Normal(j), delta = Position(j) - position;
                    if (Vector3.Dot(normal, otherNormal) < options.z ||
                        Mathf.Max(Mathf.Abs(Vector3.Dot(delta, normal)), Mathf.Abs(Vector3.Dot(delta, otherNormal))) > options.y) continue;
                    float trust = Mathf.Clamp01(input[j].a);
                    color += new Vector3(input[j].r, input[j].g, input[j].b) * (weight * trust);
                    weightSum += weight; confidenceSum += weight * trust;
                }
                if (confidenceSum <= 1e-5f || weightSum <= 1e-5f) continue;
                color /= confidenceSum;
                output[i] = new Color(color.x, color.y, color.z, Mathf.Min(input[i].a, confidenceSum / weightSum));
            }
            return output;
        }

        private void VerifySsrRoughnessOracle(Report report)
        {
            var material = Own(new Material(Resources.Load<Shader>("ScreenSpaceReflection")));
            var compute = Own(Instantiate(Resources.Load<ComputeShader>("ScreenSpaceReflection")));
            int kernel = compute.FindKernel("FilterRoughness");
            foreach (var size in new[] { new Vector2Int(17, 9), new Vector2Int(1, 19), new Vector2Int(23, 1), new Vector2Int(1, 1) })
            foreach (string mode in new[] { "frequency", "smooth", "zero", "constant-hdr", "miss", "receiver", "visibility", "normal", "plane", "smoothness", "planar", "sloped-plane" })
            {
                int width = size.x, height = size.y, count = width * height;
                var input = new Color[count]; var receivers = new Color[count]; var normals = new Color[count];
                var depths = new Color[count]; var planar = new Color[count];
                var options = new Vector4(mode == "zero" ? 0 : 8, .03f, .95f, .2f);
                for (int i = 0; i < count; i++)
                {
                    int x = i % width, y = i / width;
                    bool right = width > 1 ? x >= width / 2 : y >= height / 2;
                    input[i] = new Color((x + y) % 2 == 0 ? 4 : 0, .5f, 0, 1);
                    receivers[i] = new Color(1, .25f, 1, 0);
                    normals[i] = new Color(.5f, .5f, 1, 1); depths[i].r = 4;
                    if (mode == "smooth") receivers[i].g = 1;
                    if (mode == "constant-hdr" || mode == "miss") input[i] = new Color(4, 2, .5f, 1);
                    if (mode == "miss" && i % 3 == 0) input[i] = Color.clear;
                    if (mode == "miss" && i % 3 == 1) input[i].a = .25f;
                    if (mode == "receiver" || mode == "visibility" || mode == "normal" || mode == "plane" || mode == "smoothness" || mode == "planar")
                        input[i] = right ? new Color(0, 0, 4, 1) : new Color(4, 0, 0, 1);
                    if (right)
                    {
                        if (mode == "receiver") receivers[i].b = 1024;
                        if (mode == "visibility") receivers[i].r = 0;
                        if (mode == "normal") normals[i] = new Color(1, .5f, .5f, 1);
                        if (mode == "plane") depths[i].r = 5;
                        if (mode == "smoothness") receivers[i].g = .75f;
                        if (mode == "planar") planar[i].a = 1;
                    }
                    if (mode == "sloped-plane")
                    {
                        Vector3 n = new Vector3(.3f, 0, 1).normalized;
                        normals[i] = new Color(n.x * .5f + .5f, .5f, n.z * .5f + .5f, 1);
                        depths[i].r += .3f * ((x + .5f) * 2 / width - 1);
                    }
                }
                Texture inputTexture = ResolveTexture(input, width, height);
                Texture[] textures = { ResolveTexture(receivers, width, height), ResolveTexture(normals, width, height),
                    ResolveTexture(depths, width, height), ResolveTexture(planar, width, height) };
                string[] names = { "_SsrVisibility", "_SsrNormalMask", "_SsrDepth0", "_SsrPlanarCoverage" };
                for (int i = 0; i < names.Length; i++) { material.SetTexture(names[i], textures[i]); compute.SetTexture(kernel, names[i], textures[i]); }
                foreach (string name in new[] { "_SsrInverseProjection", "_SsrView" })
                { material.SetMatrix(name, Matrix4x4.identity); compute.SetMatrix(name, Matrix4x4.identity); }
                Vector4 sizeVector = new Vector4(width, height, 1f / width, 1f / height);
                material.SetVector("_SsrSize", sizeVector); compute.SetVector("_SsrSize", sizeVector);
                material.SetVector("_SsrFilterOptions", options); compute.SetVector("_SsrFilterOptions", options);
                material.SetFloat("_SsrPlanarAvailable", 1); compute.SetFloat("_SsrPlanarAvailable", 1);
                var rasterH = ComputeTarget(width, height, RenderTextureFormat.ARGBFloat, false);
                var rasterV = ComputeTarget(width, height, RenderTextureFormat.ARGBFloat, false);
                var computeH = ComputeTarget(width, height, RenderTextureFormat.ARGBFloat, true);
                var computeV = ComputeTarget(width, height, RenderTextureFormat.ARGBFloat, true);
                for (int pass = 0; pass < 2; pass++)
                {
                    Vector4 axis = pass == 0 ? new Vector4(1, 0, 0, 0) : new Vector4(0, 1, 0, 0);
                    material.SetVector("_SsrFilterAxis", axis); compute.SetVector("_SsrFilterAxis", axis);
                    material.SetTexture("_SsrFilterInput", pass == 0 ? inputTexture : rasterH);
                    compute.SetTexture(kernel, "_SsrFilterInput", pass == 0 ? inputTexture : computeH);
                    var rasterOutput = pass == 0 ? rasterH : rasterV; var computeOutput = pass == 0 ? computeH : computeV;
                    Graphics.Blit(Texture2D.whiteTexture, rasterOutput); Graphics.Blit(Texture2D.whiteTexture, computeOutput);
                    Graphics.Blit(inputTexture, rasterOutput, material, 3);
                    compute.SetTexture(kernel, "_SsrOutput", computeOutput); compute.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1);
                }
                Color[] expected = RoughnessOracle(RoughnessOracle(input, receivers, normals, depths, planar, width, height, options, false),
                    receivers, normals, depths, planar, width, height, options, true);
                Color[] raster = ReadSceneTarget(rasterV), actual = ReadSceneTarget(computeV);
                float oracleError = Mathf.Max(PixelError(expected, raster), PixelError(expected, actual));
                float backendError = PixelError(raster, actual);
                string label = "ssr-roughness-oracle-" + width + "x" + height + "-" + mode;
                FrameworkCheck(report, label, oracleError < .0001f && backendError < .00001f, oracleError);
                bool contract = true;
                for (int i = 0; i < count; i++)
                {
                    if (mode == "zero" || mode == "smooth" || mode == "constant-hdr") contract &= PixelError(new[] { input[i] }, new[] { actual[i] }) < .00001f;
                    if (mode == "miss") contract &= input[i].a == 0 ? actual[i].Equals(Color.clear) :
                        Mathf.Abs(actual[i].r - 4) < .00001f && actual[i].a <= input[i].a && actual[i].a > 0;
                    if (mode == "receiver" || mode == "visibility" || mode == "normal" || mode == "plane" || mode == "smoothness" || mode == "planar")
                        contract &= input[i].r > 0 ? actual[i].b == 0 : actual[i].r == 0;
                }
                if ((mode == "frequency" || mode == "sloped-plane") && count > 1)
                    contract &= actual[count / 2].r > .1f && actual[count / 2].r < 3.9f;
                FrameworkCheck(report, label + "-behavior", contract);
                if (width == 17 && mode == "frequency")
                { SaveSsrPreview("roughness-frequency-input", input, width, height, false); SaveSsrPreview("roughness-frequency-filtered", actual, width, height, false); }
                RenderTexture.active = null;
                foreach (var rt in new[] { rasterH, rasterV, computeH, computeV }) rt.Release();
            }
        }

        private void VerifySsrRoughnessHost(Report report, ScreenSpaceReflection ssr, Camera camera, SsrHostProbe hook)
        {
            string prefix = "ssr-roughness-host-" + ssr.backend + "-";
            void Check(string name, bool ok, float value = 0) => FrameworkCheck(report, prefix + name, ok, value);
            var target = camera.targetTexture;
            // The legacy fixture ends with constant-color walls separated by
            // foreground coverage. Add actual frequency geometry so dimming a
            // constant reflection cannot masquerade as radiance broadening.
            var panels = new GameObject[12];
            var extended = new SceneDepthData.Surface[ssr.surfaces.Length + panels.Length];
            Array.Copy(ssr.surfaces, extended, ssr.surfaces.Length);
            for (int i = 0; i < panels.Length; i++)
            {
                var panel = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); panels[i] = panel;
                panel.layer = ssr.surfaces[0].renderer.gameObject.layer;
                panel.transform.position = new Vector3(-2.75f + i * .5f, 2, 6.8f); panel.transform.localScale = new Vector3(.5f, 4, 1);
                var mat = Own(new Material(Resources.Load<Shader>("StudioAccent")));
                mat.SetColor("_Color", i % 2 == 0 ? new Color(.9f, .05f, .05f, 1) : new Color(.05f, .9f, .05f, 1));
                panel.GetComponent<Renderer>().sharedMaterial = mat;
                extended[ssr.surfaces.Length + i] = new SceneDepthData.Surface { renderer = panel.GetComponent<Renderer>(), receiveReflections = false };
            }
            ssr.surfaces = extended;
            ssr.surfaces[0].smoothness = .55f;
            camera.Render(); camera.Render();
            Color[] raw = SsrPixels(ssr, camera), main = ReadSceneTarget(target);
            Check("default-no-extra-targets", SsrField<RenderTexture>(ssr, "_filtered") == null && SsrField<RenderTexture>(ssr, "_filterHorizontal") == null);
            var history = SsrField<RenderTexture>(ssr, "_historyColor");
            ssr.roughness.enabled = true; ssr.roughness.referenceHeight = target.height; ssr.roughness.maximumRadiusPixels = 12;
            camera.Render();
            Color[] filtered = SsrPixels(ssr, camera);
            ssr.TryGetRawReflection(camera, out var rawTexture);
            Check("toggle-preserves-history-and-raw-trace", history == SsrField<RenderTexture>(ssr, "_historyColor") &&
                ScenePixelsEqual(raw, ReadSceneTarget((RenderTexture)rawTexture)) && SsrHitCount(filtered) > 10);
            int changed = 0; float confidenceInflation = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i].a > .01f && Mathf.Abs(raw[i].r - filtered[i].r) + Mathf.Abs(raw[i].g - filtered[i].g) > .002f) changed++;
                confidenceInflation = Mathf.Max(confidenceInflation, filtered[i].a - raw[i].a);
            }
            Check("real-radiance-broadening-not-only-dimming", changed > 2, changed);
            Check("misses-not-resurrected-confidence-not-inflated", confidenceInflation <= .00001f, confidenceInflation);
            Check("actual-backend-two-filter-passes", SsrField<RenderTexture>(ssr, "_visibility").format == RenderTextureFormat.ARGBHalf &&
                SsrField<RenderTexture>(ssr, "_filtered").enableRandomWrite == (ssr.backend == SceneShaderBackend.Compute) &&
                ssr.ComputeDispatchCount == (ssr.backend == SceneShaderBackend.Compute ? 3 : 0));
            Check("composite-consumes-filtered-output", !ScenePixelsEqual(main, ReadSceneTarget(target)) &&
                SsrField<Material>(ssr, "_material").GetTexture("_SsrReflection") == SsrField<RenderTexture>(ssr, "_filtered"));
            int foreground = 0, leaked = 0;
            for (int i = 0; i < main.Length; i++) if (main[i].b > .7f && main[i].r < .2f)
            { foreground++; if (filtered[i].a > 0) leaked++; }
            Check("real-foreground-depth-never-receives-filtered-reflection", foreground > 5 && leaked == 0, leaked);
            SaveSsrPreview("roughness-host-" + ssr.backend + "-raw", raw, target.width, target.height, true);
            SaveSsrPreview("roughness-host-" + ssr.backend + "-filtered", filtered, target.width, target.height, true);
            var visibility = ReadSceneTarget(SsrField<RenderTexture>(ssr, "_visibility")); int metadata = 0;
            foreach (Color value in visibility) if (value.r > .5f && Mathf.Abs(value.g - .55f) < .001f && value.b == 1) metadata++;
            Check("real-surface-smoothness-and-id", metadata > 20, metadata);
            var map = ResolveTexture(new[] { new Color(.5f, 1, 1, 1), Color.white }, 2, 1);
            ssr.surfaces[0].smoothnessMap = map; ssr.surfaces[0].smoothnessMapST = new Vector4(0, 0, .25f, .5f);
            camera.Render(); Check("smoothness-map-red-channel-rejects-receiver", SsrHitCount(SsrPixels(ssr, camera)) == 0);
            ssr.surfaces[0].smoothnessMapST.z = .75f; camera.Render();
            Check("smoothness-map-uv-recovers", SsrHitCount(SsrPixels(ssr, camera)) > 10);
            ssr.surfaces[0].smoothnessMap = null;
            ssr.surfaces[0].alphaMask = ResolveTexture(new[] { Color.clear }, 1, 1);
            ssr.surfaces[0].alphaCutoff = .5f; camera.Render();
            Check("metadata-respects-alpha-cutout", SsrHitCount(SsrPixels(ssr, camera)) == 0);
            ssr.surfaces[0].alphaMask = null; camera.Render(); camera.Render();
            Check("cutout-restoration-recovers", SsrHitCount(SsrPixels(ssr, camera)) > 10);
            hook.traceOnly = true; camera.Render();
            Check("trace-only-returns-filtered-for-unified-hosts", hook.consumed && hook.traceResult == SsrField<RenderTexture>(ssr, "_filtered"));
            hook.traceOnly = false;
            ssr.roughness.maximumRadiusPixels = 0; camera.Render();
            Check("zero-radius-exact-raw-no-extra-targets", ScenePixelsEqual(raw, SsrPixels(ssr, camera)) &&
                SsrField<RenderTexture>(ssr, "_filtered") == null && history == SsrField<RenderTexture>(ssr, "_historyColor"));
            ssr.roughness.maximumRadiusPixels = 12; ssr.roughness.enabled = false; camera.Render();
            Check("disable-exact-main-and-raw-restoration", ScenePixelsEqual(main, ReadSceneTarget(target)) && ScenePixelsEqual(raw, SsrPixels(ssr, camera)));
            ssr.roughness.enabled = true; camera.Render();
            SsrField<RenderTexture>(ssr, "_filtered").Release();
            Check("lost-filter-output-rejected", !ssr.TryGetReflection(camera, out _) && ssr.TryGetRawReflection(camera, out _));
            camera.Render(); Check("lost-filter-recreated-with-history", SsrHitCount(SsrPixels(ssr, camera)) > 10 && history == SsrField<RenderTexture>(ssr, "_historyColor"));
            ssr.roughness.referenceHeight = float.NaN; camera.Render();
            Check("invalid-enabled-settings-fail-closed", !hook.consumed && ssr.UnavailableReason == "Invalid SSR roughness settings" && SsrField<RenderTexture>(ssr, "_filtered") == null);
            ssr.roughness.enabled = false; camera.Render(); camera.Render();
            Check("disabled-invalid-settings-ignored", SsrHitCount(SsrPixels(ssr, camera)) > 10);
            ssr.roughness.referenceHeight = target.height; ssr.roughness.enabled = true; camera.Render();
            var savedSurfaces = ssr.surfaces;
            ssr.surfaces = new SceneDepthData.Surface[1025]; camera.Render();
            Check("receiver-id-cap-fails-closed", !hook.consumed && ssr.UnavailableReason == "Roughness filtering supports at most 1024 surface entries");
            ssr.surfaces = savedSurfaces; camera.Render(); camera.Render();
            if (ssr.backend == SceneShaderBackend.Compute)
            {
                var asset = SsrField<ComputeShader>(ssr, "_computeAsset");
                SetComputeField(ssr, "_computeAsset", null); ssr.allowComputeFallback = true; camera.Render(); camera.Render();
                Check("missing-compute-resource-raster-filter-fallback", ssr.ActiveBackend == SceneShaderBackend.Raster &&
                    ssr.ComputeFallbackReason != null && SsrHitCount(SsrPixels(ssr, camera)) > 10 &&
                    !SsrField<RenderTexture>(ssr, "_filtered").enableRandomWrite);
                ssr.allowComputeFallback = false; camera.Render();
                Check("strict-compute-failure-releases-filter", !hook.consumed && SsrField<RenderTexture>(ssr, "_filtered") == null);
                SetComputeField(ssr, "_computeAsset", asset); camera.Render(); camera.Render();
                Check("compute-filter-recovery", ssr.ActiveBackend == SceneShaderBackend.Compute && ssr.ComputeDispatchCount == 3 && SsrHitCount(SsrPixels(ssr, camera)) > 10);
            }
            var resized = Own(new RenderTexture(83, 61, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); resized.Create();
            camera.targetTexture = resized;
            Check("target-identity-guard", !ssr.TryGetReflection(camera, out _) && !ssr.TryGetRawReflection(camera, out _));
            camera.Render(); Check("resize-cold-zero", SsrHitCount(SsrPixels(ssr, camera)) == 0);
            camera.Render(); Check("odd-resize-recovery", SsrField<RenderTexture>(ssr, "_filtered").width == 83 && SsrHitCount(SsrPixels(ssr, camera)) > 10);
            camera.targetTexture = target; resized.Release();
            ssr.enabled = false;
            Check("disable-releases-both-filter-targets", SsrField<RenderTexture>(ssr, "_filtered") == null && SsrField<RenderTexture>(ssr, "_filterHorizontal") == null);
            foreach (var panel in panels) panel.SetActive(false);
        }
    }
}
