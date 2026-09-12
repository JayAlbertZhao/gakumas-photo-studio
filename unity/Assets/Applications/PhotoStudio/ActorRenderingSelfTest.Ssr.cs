using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class SsrHostProbe : MonoBehaviour
    {
        public ScreenSpaceReflection reflection;
        public bool consumed;
        public Action beforeConsume;
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            beforeConsume?.Invoke();
            consumed = reflection.TryComposite(GetComponent<Camera>(), source, out RenderTexture result);
            Graphics.Blit(consumed ? result : source, destination);
        }
    }

    public sealed partial class ActorRenderingSelfTest
    {
        private T SsrField<T>(ScreenSpaceReflection ssr, string name) => (T)typeof(ScreenSpaceReflection)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ssr);

        private Color[] SsrPixels(ScreenSpaceReflection ssr, Camera camera)
        {
            if (!ssr.TryGetReflection(camera, out Texture texture)) throw new InvalidOperationException("SSR output unavailable: " + ssr.UnavailableReason);
            return ReadSceneTarget((RenderTexture)texture);
        }

        private static int SsrHitCount(Color[] pixels)
        {
            int count = 0;
            foreach (Color pixel in pixels) if (pixel.a > .01f) count++;
            return count;
        }

        private void SaveSsrPreview(string name, Color[] pixels, int width, int height, bool radiance)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                var copy = (Color[])pixels.Clone();
                for (int i = 0; i < copy.Length; i++)
                {
                    if (radiance) copy[i] *= copy[i].a;
                    copy[i].a = 1;
                }
                texture.SetPixels(copy); texture.Apply();
                File.WriteAllBytes(Path.Combine(_directory, name + ".png"), texture.EncodeToPNG());
            }
            finally { Destroy(texture); }
        }

        private void VerifyScreenSpaceReflection(Report report, SceneShaderBackend backend = SceneShaderBackend.Raster)
        {
            int width = backend == SceneShaderBackend.Compute ? 97 : 96, height = backend == SceneShaderBackend.Compute ? 73 : 72;
            string prefix = backend == SceneShaderBackend.Compute ? "ssr-compute-" : "ssr-";
            void Check(Report r, string name, bool accepted, float value = 0) => FrameworkCheck(r, prefix + name.Substring(4), accepted, value);
            var host = Own(new GameObject("SSR synthetic host"));
            var camera = host.AddComponent<Camera>();
            camera.enabled = false; camera.allowMSAA = false; camera.allowHDR = true;
            camera.renderingPath = RenderingPath.Forward;
            camera.nearClipPlane = .1f; camera.farClipPlane = 30;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.02f, .03f, .04f, 1);
            camera.fieldOfView = 55; camera.aspect = (float)width / height;
            camera.cullingMask = (1 << 22) | (1 << 23);
            host.transform.position = new Vector3(0, 2.5f, -4);
            host.transform.LookAt(new Vector3(0, .5f, 3));
            var target = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            target.Create(); camera.targetTexture = target;
            Material Flat(Color color)
            {
                var material = Own(new Material(Resources.Load<Shader>("StudioAccent")));
                material.SetColor("_Color", color); return material;
            }
            GameObject Quad(string name, Vector3 position, Vector3 scale, Quaternion rotation, Material material)
            {
                var go = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.name = name; go.layer = 22;
                go.transform.SetPositionAndRotation(position, rotation); go.transform.localScale = scale;
                go.GetComponent<Renderer>().sharedMaterial = material; return go;
            }
            var floorMaterial = Flat(new Color(.05f, .05f, .05f, 1));
            var red = Flat(new Color(.8f, .1f, .05f, 1));
            var green = Flat(new Color(.05f, .8f, .1f, 1));
            var floor = Quad("SSR floor", new Vector3(0, 0, 3), new Vector3(12, 12, 1), Quaternion.Euler(90, 0, 0), floorMaterial);
            var wallLeft = Quad("SSR red wall", new Vector3(-1.5f, 2, 6), new Vector3(3, 4, 1), Quaternion.identity, red);
            var wallRight = Quad("SSR green wall", new Vector3(1.5f, 2, 6), new Vector3(3, 4, 1), Quaternion.identity, green);
            var actor = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); actor.name = "SSR excluded blue actor";
            actor.layer = 23; actor.transform.position = new Vector3(0, .8f, 2); actor.transform.localScale = new Vector3(.8f, 1.6f, .8f);
            actor.GetComponent<Renderer>().sharedMaterial = Flat(new Color(.05f, .1f, .9f, 1));
            camera.Render();
            Color[] baseline = ReadSceneTarget(target);
            var ssr = host.AddComponent<ScreenSpaceReflection>();
            ssr.backend = backend; ssr.allowComputeFallback = false;
            var hook = host.AddComponent<SsrHostProbe>(); hook.reflection = ssr;
            ssr.sceneLayers = 1 << 22;
            ssr.surfaces = new[] {
                new SceneDepthData.Surface { renderer = floor.GetComponent<Renderer>() },
                new SceneDepthData.Surface { renderer = wallLeft.GetComponent<Renderer>(), receiveReflections = false },
                new SceneDepthData.Surface { renderer = wallRight.GetComponent<Renderer>(), receiveReflections = false }
            };
            ssr.maximumSteps = 512; ssr.intensity = .7f;
            camera.Render();
            Check(report, "ssr-default-off-exact-noop", !hook.consumed && ScenePixelsEqual(baseline, ReadSceneTarget(target)) && SsrField<RenderTexture>(ssr, "_sceneColor") == null);
            ssr.reflectionsEnabled = true;
            camera.Render();
            Check(report, "ssr-cold-frame-initializes-history", hook.consumed && ssr.HistoryAvailable && SsrHitCount(SsrPixels(ssr, camera)) == 0);
            Check(report, "ssr-cold-frame-preserves-main-color", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            Color[] oldHistoryColor = null, oldHistoryDepth = null;
            if (backend == SceneShaderBackend.Compute) hook.beforeConsume = () => {
                oldHistoryColor = ReadSceneTarget(SsrField<RenderTexture>(ssr, "_historyColor"));
                oldHistoryDepth = ReadSceneTarget(SsrField<RenderTexture>(ssr, "_historyDepth"));
            };
            camera.Render();
            hook.beforeConsume = null;
            Color[] reflections = SsrPixels(ssr, camera), composite = ReadSceneTarget(target);
            int hits = SsrHitCount(reflections);
            Check(report, "ssr-warm-real-scene-hit-positive-control", hits > 30, hits);
            SaveSsrPreview(prefix + "main-composite", composite, width, height, false);
            SaveSsrPreview(prefix + "radiance-confidence-preview", reflections, width, height, true);
            VerifySsrAnalyticWall(report, camera, reflections, width, height, "perspective", prefix);
            if (backend == SceneShaderBackend.Compute) VerifySsrComputeEquivalence(report, ssr, camera, reflections, target, oldHistoryColor, oldHistoryDepth);
            int actorPixels = 0, changedActor = 0;
            for (int i = 0; i < baseline.Length; i++)
                if (baseline[i].b > .7f && baseline[i].r < .2f)
                { actorPixels++; if (!baseline[i].Equals(composite[i])) changedActor++; }
            Check(report, "ssr-main-actor-occludes-reflector-with-real-depth", actorPixels > 20 && changedActor == 0, changedActor);
            int historyActorPixels = 0;
            foreach (Color pixel in ReadSceneTarget(SsrField<RenderTexture>(ssr, "_historyColor")))
                if (pixel.b > .7f && pixel.r < .2f) historyActorPixels++;
            Check(report, "ssr-color-history-excludes-actor", historyActorPixels == 0, historyActorPixels);
            var scene = SsrField<SceneDepthData>(ssr, "_geometry");
            var sceneCamera = SsrField<Camera>(ssr, "_sceneCamera");
            scene.TryGetFrame(sceneCamera, width, height, out var geometry);
            int reflectiveWallPixels = 0;
            var normals = ReadSceneTarget(geometry.normalMask);
            SaveSsrPreview(prefix + "input-world-normals", normals, width, height, false);
            SaveSsrPreview(prefix + "input-visibility", ReadSceneTarget(SsrField<RenderTexture>(ssr, "_visibility")), width, height, false);
            SaveSsrPreview(prefix + "input-history-color", ReadSceneTarget(SsrField<RenderTexture>(ssr, "_historyColor")), width, height, false);
            var confidence = new Color[reflections.Length];
            for (int i = 0; i < confidence.Length; i++) confidence[i] = new Color(reflections[i].a, reflections[i].a, reflections[i].a, 1);
            SaveSsrPreview(prefix + "confidence", confidence, width, height, false);
            for (int i = 0; i < normals.Length; i++) if (normals[i].a < .5f && reflections[i].a > 0) reflectiveWallPixels++;
            Check(report, "ssr-only-qualified-receivers", reflectiveWallPixels == 0);
            Check(report, "ssr-rejects-other-camera-and-double-consume", !ssr.TryComposite(_camera, target, out _) && !ssr.TryComposite(camera, target, out _));
            host.transform.position += Vector3.right * .35f;
            camera.Render();
            VerifySsrAnalyticWall(report, camera, SsrPixels(ssr, camera), width, height, "camera-pan-history-reprojection", prefix);
            // Compare tracing algorithms with the same stationary history, not
            // the pan's disoccluded history versus an already updated capture.
            camera.Render();
            Color[] hierarchical = SsrPixels(ssr, camera);
            ssr.useHierarchy = false; camera.Render();
            Color[] linear = SsrPixels(ssr, camera);
            int maskDifferences = 0; float radianceError = 0;
            for (int i = 0; i < linear.Length; i++)
            {
                if ((linear[i].a > .01f) != (hierarchical[i].a > .01f)) maskDifferences++;
                if (linear[i].a > .01f && hierarchical[i].a > .01f)
                    radianceError = Mathf.Max(radianceError, Mathf.Abs(linear[i].r - hierarchical[i].r), Mathf.Abs(linear[i].g - hierarchical[i].g));
            }
            Check(report, "ssr-hierarchy-vs-full-resolution-trace", maskDifferences <= 2 && radianceError < .001f && SsrHitCount(linear) > 30, maskDifferences);
            ssr.useHierarchy = true; camera.Render();
            int highBudgetHits = SsrHitCount(SsrPixels(ssr, camera));
            ssr.maximumSteps = 128; camera.Render();
            int defaultBudgetHits = SsrHitCount(SsrPixels(ssr, camera));
            Check(report, "ssr-default-128-step-budget-renders-reflections", defaultBudgetHits > 30, defaultBudgetHits);
            ssr.maximumSteps = 8; camera.Render();
            Check(report, "ssr-trace-budget-negative-control", SsrHitCount(SsrPixels(ssr, camera)) < highBudgetHits);
            ssr.maximumSteps = 512;
            ssr.ResetHistory();
            Check(report, "ssr-reset-invalidates-borrowed-output", !ssr.TryGetReflection(camera, out _));
            camera.Render();
            Check(report, "ssr-explicit-history-reset", SsrHitCount(SsrPixels(ssr, camera)) == 0);
            camera.Render();
            Check(report, "ssr-history-reset-recovers", SsrHitCount(SsrPixels(ssr, camera)) > 30);
            // Change both wall colors without changing geometry. The next
            // reflection must still sample the preceding actor-free color.
            Color[] priorColor = SsrPixels(ssr, camera);
            red.SetColor("_Color", new Color(.8f, .8f, .05f, 1));
            green.SetColor("_Color", new Color(.8f, .8f, .05f, 1));
            camera.Render(); Color[] lagged = SsrPixels(ssr, camera);
            int lagMatched = 0;
            for (int i = 0; i < lagged.Length; i++) if (lagged[i].a > .01f && priorColor[i].a > .01f && lagged[i].r == priorColor[i].r && lagged[i].g == priorColor[i].g) lagMatched++;
            Check(report, "ssr-uses-previous-color-not-current", lagMatched > 30);
            camera.Render(); Color[] updated = SsrPixels(ssr, camera);
            int newColor = 0;
            Color yellowLinear = new Color(.8f, .8f, .05f, 1).linear;
            foreach (Color p in updated) if (p.a > .01f && Mathf.Abs(p.r - yellowLinear.r) < .002f && Mathf.Abs(p.g - yellowLinear.g) < .002f) newColor++;
            Check(report, "ssr-history-color-advances-without-feedback", newColor > 30);
            wallLeft.transform.position += Vector3.forward; wallRight.transform.position += Vector3.forward;
            camera.Render();
            Check(report, "ssr-depth-disocclusion-rejects-history", SsrHitCount(SsrPixels(ssr, camera)) == 0);
            camera.Render();
            Check(report, "ssr-disocclusion-recovers-next-capture", SsrHitCount(SsrPixels(ssr, camera)) > 20);
            host.transform.position += Vector3.right * 3;
            camera.Render();
            Check(report, "ssr-camera-cut-invalidates-history", SsrHitCount(SsrPixels(ssr, camera)) == 0);
            host.transform.position -= Vector3.right * 3;
            camera.orthographic = true; camera.orthographicSize = 3;
            camera.Render(); camera.Render();
            Check(report, "ssr-orthographic-hit-positive-control", SsrHitCount(SsrPixels(ssr, camera)) > 20);
            ssr.intensity = 0; camera.Render();
            Check(report, "ssr-zero-intensity-releases-resources", !hook.consumed && !ssr.HistoryAvailable && SsrField<RenderTexture>(ssr, "_historyColor") == null);
            ssr.intensity = .7f; ssr.thickness = float.NaN; camera.Render();
            Check(report, "ssr-invalid-settings-fail-closed", !hook.consumed && ssr.UnavailableReason != null);
            ssr.thickness = .12f;
            camera.rect = new Rect(0, 0, .5f, 1); camera.Render();
            Check(report, "ssr-viewport-atlas-rejected", !hook.consumed);
            camera.rect = new Rect(0, 0, 1, 1);
            camera.Render(); camera.Render();
            var resized = Own(new RenderTexture(80, 60, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            resized.Create(); camera.targetTexture = resized; camera.Render();
            Check(report, "ssr-resize-invalidates-history", SsrHitCount(SsrPixels(ssr, camera)) == 0 && SsrField<RenderTexture>(ssr, "_historyColor").width == 80);
            camera.Render();
            Check(report, "ssr-resize-recovers", SsrHitCount(SsrPixels(ssr, camera)) > 10);
            ssr.enabled = false;
            Check(report, "ssr-disable-detaches-and-releases", camera.GetCommandBuffers(CameraEvent.BeforeImageEffects).Length == 0 && !ssr.HistoryAvailable && SsrField<Camera>(ssr, "_sceneCamera") == null);
            ssr.enabled = true; camera.Render(); camera.Render();
            Check(report, "ssr-reenable-single-buffer", camera.GetCommandBuffers(CameraEvent.BeforeImageEffects).Length == 1 && SsrHitCount(SsrPixels(ssr, camera)) > 10);
            Matrix4x4 authoredView = camera.worldToCameraMatrix * Matrix4x4.Translate(new Vector3(-.2f, 0, 0));
            Matrix4x4 authoredProjection = camera.projectionMatrix; authoredProjection.m02 += .04f;
            camera.worldToCameraMatrix = authoredView; camera.projectionMatrix = authoredProjection;
            camera.Render(); camera.Render();
            var authoredCamera = SsrField<Camera>(ssr, "_sceneCamera");
            var authoredGeometry = SsrField<SceneDepthData>(ssr, "_geometry");
            authoredGeometry.TryGetFrame(authoredCamera, 80, 60, out var authoredFrame);
            float matrixError = 0;
            Matrix4x4 expectedProjection = GL.GetGPUProjectionMatrix(authoredProjection, true);
            for (int i = 0; i < 16; i++) matrixError = Mathf.Max(matrixError,
                Mathf.Abs(authoredFrame.worldToCamera[i] - authoredView[i]), Mathf.Abs(authoredFrame.gpuProjection[i] - expectedProjection[i]));
            Check(report, "ssr-custom-view-and-projection-captured", matrixError < 1e-6f && SsrHitCount(SsrPixels(ssr, camera)) > 10, matrixError);
            camera.ResetWorldToCameraMatrix(); camera.ResetProjectionMatrix();
            hook.enabled = false;
            var pipeline = host.AddComponent<OriginalStyleRenderPipeline>();
            pipeline.screenSpaceReflection = ssr;
            camera.Render(); camera.Render();
            Check(report, "ssr-production-hdr-pipeline-consumes", ssr.TryGetReflection(camera, out _) && !ssr.TryComposite(camera, resized, out _) && SsrHitCount(SsrPixels(ssr, camera)) > 10);
            if (backend == SceneShaderBackend.Compute) VerifySsrComputeLifecycle(report, ssr, camera, resized);
            ssr.enabled = false; pipeline.enabled = false;
            camera.targetTexture = null; resized.Release(); target.Release();
            foreach (var go in new[] { host, floor, wallLeft, wallRight, actor }) go.SetActive(false);
        }

        private void VerifySsrAnalyticWall(Report report, Camera camera, Color[] reflections, int width, int height, string label, string prefix = "ssr-")
        {
            int compared = 0, wrong = 0;
            var floor = new Plane(Vector3.up, Vector3.zero);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                Color actual = reflections[y * width + x];
                if (actual.a <= .01f) continue;
                Ray primary = camera.ScreenPointToRay(new Vector3(x + .5f, y + .5f));
                if (!floor.Raycast(primary, out float distance)) continue;
                Vector3 point = primary.GetPoint(distance);
                Vector3 direction = Vector3.Reflect(primary.direction, Vector3.up);
                if (direction.z <= .0001f) continue;
                Vector3 hit = point + direction * ((6 - point.z) / direction.z);
                // Exclude quantized-normal and raster boundary ambiguity, not
                // failed rays. Geometry/color misses have separate controls.
                if (hit.y < .2f || hit.y > 3.8f || Mathf.Abs(hit.x) < .2f || Mathf.Abs(hit.x) > 2.8f) continue;
                compared++;
                // Color material properties are authored in sRGB; the HDR
                // capture and reflection texture hold linear scene radiance.
                Color expected = (hit.x < 0 ? new Color(.8f, .1f, .05f, 1) : new Color(.05f, .8f, .1f, 1)).linear;
                if (Mathf.Max(Mathf.Abs(actual.r - expected.r), Mathf.Abs(actual.g - expected.g), Mathf.Abs(actual.b - expected.b)) > .002f) wrong++;
            }
            FrameworkCheck(report, prefix + "analytic-two-color-wall-" + label, compared > 20 && wrong == 0, wrong);
        }
    }
}
