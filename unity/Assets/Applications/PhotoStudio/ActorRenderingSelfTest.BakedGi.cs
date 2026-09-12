using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyRealGiBake(Report report, string bundlePath)
        {
            var original = SceneManager.GetActiveScene();
            int oldMapCount = LightmapSettings.lightmaps.Length;
            var bundle = AssetBundle.LoadFromFile(Path.GetFullPath(bundlePath));
            if (bundle == null) throw new IOException("Cannot load requested real GI fixture bundle.");
            Scene loaded = default; Camera camera = null;
            void Check(string label, bool ok, float error = 0) => FrameworkCheck(report, "real-baked-gi-" + label, ok, error);
            try
            {
                string[] paths = bundle.GetAllScenePaths();
                if (paths.Length != 1) throw new InvalidOperationException("Expected one baked reference scene.");
                yield return SceneManager.LoadSceneAsync(paths[0], LoadSceneMode.Additive);
                loaded = SceneManager.GetSceneByPath(paths[0]);
                if (!loaded.IsValid() || !loaded.isLoaded) throw new InvalidOperationException("Baked scene did not load.");
                SceneManager.SetActiveScene(loaded);
                LightProbes.Tetrahedralize();
                var probes = LightmapSettings.lightProbes;
                Check("actual-scene-has-45-baked-probes", probes != null && probes.count == 45);
                if (probes == null || probes.count < 4) throw new InvalidOperationException("Missing actual baked probe volume.");
                MeshRenderer floor = null; int lights = 0;
                foreach (var root in loaded.GetRootGameObjects())
                {
                    foreach (var r in root.GetComponentsInChildren<Renderer>())
                    { r.gameObject.layer = 25; if (r.name == "Floor") floor = r as MeshRenderer; }
                    foreach (var light in root.GetComponentsInChildren<Light>())
                    {
                        Check("reference-light-white-baked-" + lights++, light.color == Color.white && light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked);
                        light.enabled = false;
                    }
                }
                if (floor == null || lights != 1) throw new InvalidOperationException("Unexpected self-authored bake fixture.");
                int index = floor.lightmapIndex;
                Check("floor-keeps-actual-lightmap-index", index >= 0 && index < LightmapSettings.lightmaps.Length);
                var map = LightmapSettings.lightmaps[index].lightmapColor;
                Check("desktop-map-linear-hdr-half-nondirectional", map != null && map.format == TextureFormat.RGBAHalf && LightmapSettings.lightmapsMode == LightmapsMode.NonDirectional);
                if (map == null || map.format != TextureFormat.RGBAHalf) throw new InvalidOperationException("Fixture requires verified linear HDR lightmap encoding.");
                var host = Own(new GameObject("Actual baked GI host")); camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
                camera.orthographic = true; camera.orthographicSize = 3.2f; camera.aspect = 1;
                camera.nearClipPlane = .1f; camera.farClipPlane = 30; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black; camera.cullingMask = 1 << 26;
                camera.transform.SetPositionAndRotation(new Vector3(0, 6, 0), Quaternion.Euler(90, 0, 0));
                var target = Own(new RenderTexture(129, 129, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var stage = host.AddComponent<SceneDeferredCamera>(); stage.sceneEnabled = true; stage.sceneLayers = 1 << 25;
                stage.lightRadiance = Vector3.zero; stage.ambientIrradiance = Vector3.zero;
                var surface = new SceneDeferredCamera.Surface { renderer = floor, cull = CullMode.Off,
                    gi = new SceneGiInput { source = SceneGiSource.RendererLightmap, encoding = SceneGiEncoding.LinearRgb } };
                surface.inputs.albedo = new Vector3(.4f, .6f, .2f); surface.inputs.mos = new Vector3(0, 1, .2f);
                surface.inputs.emission = new Vector3(.1f, .2f, .3f); stage.surfaces = new[] { surface };
                SceneDeferredCamera.Frame Render()
                { camera.Render(); if (!stage.TryGetFrame(out var f)) throw new InvalidOperationException(stage.UnavailableReason); return f; }
                Color Pixel(RenderTexture t) => ReadSceneTarget(t)[t.width * (t.height / 2) + t.width / 2];
                Vector3 Rgb(Color c) => new Vector3(c.r, c.g, c.b);
                float Error(Vector3 a, Vector3 b) => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z));
                var mapTarget = Own(new RenderTexture(map.width, map.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); mapTarget.Create();
                Graphics.Blit(map, mapTarget); var texels = ReadSceneTarget(mapTarget);
                var mesh = floor.GetComponent<MeshFilter>().sharedMesh;
                Vector3[] samples = { new Vector3(-2.2f, 0, .8f), new Vector3(-1, 0, -.6f), new Vector3(1.4f, 0, .8f), new Vector3(2.2f, 0, 2) };
                var responses = new Vector3[samples.Length]; int sampleIndex = 0;
                foreach (var p in samples)
                {
                    camera.transform.position = p + Vector3.up * 6;
                    Vector2 uv = BakedGiRayUv(mesh, floor.transform, camera.ViewportPointToRay(new Vector3(.5f, .5f, 0)));
                    var st = floor.lightmapScaleOffset; uv = Vector2.Scale(uv, new Vector2(st.x, st.y)) + new Vector2(st.z, st.w);
                    var expected = Rgb(BakedGiBilinear(texels, map.width, map.height, uv)); var frame = Render();
                    var actual = Rgb(Pixel(frame.bakedDiffuseGi)); float error = Error(expected, actual);
                    responses[sampleIndex++] = actual;
                    Check("actual-atlas-uv2-st-cpu-" + p.x, expected.sqrMagnitude > .00001f && Pixel(frame.bakedDiffuseGi).a == 1 && error < .012f, error);
                    var albedo = Rgb(Pixel(frame.albedoCoverage)); var emission = Rgb(Pixel(frame.emission));
                    error = Error(Rgb(Pixel(target)), emission + Vector3.Scale(albedo, actual));
                    Check("actual-bake-base-no-extra-pi-" + p.x, error < .004f, error);
                }
                Check("baked-occluder-region-darker-with-runtime-lights-disabled", responses[2].magnitude < responses[1].magnitude * .1f);
                camera.transform.position = new Vector3(0, 6, 0); var floorFrame = Render();
                SaveSsrPreview("real-baked-gi-floor", ReadSceneTarget(floorFrame.bakedDiffuseGi), 129, 129, false);
                SaveSsrPreview("real-baked-gi-floor-base", ReadSceneTarget(target), 129, 129, false);
                var exposed = ReadSceneTarget(floorFrame.bakedDiffuseGi);
                for (int i = 0; i < exposed.Length; i++) { exposed[i].r *= .125f; exposed[i].g *= .125f; exposed[i].b *= .125f; }
                SaveSsrPreview("real-baked-gi-floor-exposure-minus3", exposed, 129, 129, false);

                var receiver = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); receiver.name = "Moving probe receiver"; receiver.layer = 25;
                receiver.transform.rotation = Quaternion.Euler(90, 0, 0);
                var renderer = receiver.GetComponent<Renderer>(); surface.renderer = renderer; surface.gi.source = SceneGiSource.SceneProbe;
                Vector3 Probe(Vector3 position, Renderer r, Vector3 normal)
                { LightProbes.GetInterpolatedProbe(position, r, out var sh); var colors = new Color[1]; sh.Evaluate(new[] { normal }, colors); return Vector3.Max(Rgb(colors[0]), Vector3.zero); }
                void View(Vector3 p) { camera.transform.position = p + Vector3.up * 6; camera.orthographicSize = .8f; }
                Vector3 first = default, last = default;
                for (int i = 0; i < samples.Length; i++)
                {
                    var p = samples[i] + Vector3.up; receiver.transform.position = p; View(p); var f = Render();
                    var expected = Probe(renderer.bounds.center, renderer, Vector3.up); var actual = Rgb(Pixel(f.bakedDiffuseGi));
                    float error = Error(expected, actual); Check("spatial-probe-cpu-" + i, expected.sqrMagnitude > .00001f && error < .008f, error);
                    if (i == 0) first = actual; last = actual;
                    SaveSsrPreview("real-baked-gi-probe-" + i, ReadSceneTarget(target), 129, 129, false);
                }
                Check("real-volume-spatial-response-not-ambient-constant", Error(first, last) > .05f, Error(first, last));
                Check("red-wall-color-bounce-in-baked-probe", first.x > first.y * 1.03f && first.x > first.z * 1.03f);
                var anchor = Own(new GameObject("Explicit scene probe anchor")).transform; anchor.position = samples[0] + Vector3.up;
                surface.gi.probeAnchor = anchor; var anchored = Render();
                Check("input-anchor-overrides-moving-bounds", Error(Rgb(Pixel(anchored.bakedDiffuseGi)), Probe(anchor.position, renderer, Vector3.up)) < .008f);
                renderer.probeAnchor = anchor; surface.gi.probeAnchor = null; anchored = Render();
                Check("renderer-anchor-fallback", Error(Rgb(Pixel(anchored.bakedDiffuseGi)), Probe(anchor.position, renderer, Vector3.up)) < .008f);
                var otherAnchor = Own(new GameObject("Priority anchor")).transform; otherAnchor.position = samples[2] + Vector3.up; surface.gi.probeAnchor = otherAnchor;
                anchored = Render(); Check("input-anchor-precedes-renderer-anchor", Error(Rgb(Pixel(anchored.bakedDiffuseGi)), Probe(otherAnchor.position, renderer, Vector3.up)) < .008f);
                otherAnchor.position = samples[1] + Vector3.up; anchored = Render();
                Check("moving-anchor-refresh", Error(Rgb(Pixel(anchored.bakedDiffuseGi)), Probe(otherAnchor.position, renderer, Vector3.up)) < .008f);

                var skinHost = Own(new GameObject("Actual probe skinned receiver")); skinHost.layer = 25; skinHost.transform.rotation = receiver.transform.rotation;
                var bone = Own(new GameObject("GI test bone")).transform; bone.SetParent(skinHost.transform, false);
                var skin = skinHost.AddComponent<SkinnedMeshRenderer>(); var skinMesh = Own(Instantiate(receiver.GetComponent<MeshFilter>().sharedMesh));
                var weights = new BoneWeight[skinMesh.vertexCount]; for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
                skinMesh.boneWeights = weights; skinMesh.bindposes = new[] { Matrix4x4.identity }; skin.sharedMesh = skinMesh; skin.bones = new[] { bone }; skin.rootBone = bone;
                skin.sharedMaterial = renderer.sharedMaterial; skin.updateWhenOffscreen = true; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 4);
                receiver.SetActive(false); surface.renderer = skin; surface.gi.probeAnchor = null;
                for (int i = 0; i < 3; i++)
                {
                    skinHost.transform.position = samples[i] + Vector3.up; bone.localRotation = Quaternion.Euler(0, i * 25, 0); View(skinHost.transform.position);
                    // Unity updates skinning at the frame boundary, not when setting a bone transform.
                    yield return null;
                    var f = Render(); var normal = bone.TransformDirection(Vector3.back).normalized;
                    var expected = Probe(skin.bounds.center, skin, normal); var actual = Rgb(Pixel(f.bakedDiffuseGi));
                    float error = Error(expected, actual); Check("moving-skinned-normal-and-probe-cpu-" + i, Pixel(f.bakedDiffuseGi).a == 1 && error < .012f, error);
                    var n = Pixel(f.normalGroup); var observedNormal = new Vector3(n.r, n.g, n.b);
                    Check("skinned-normal-actually-deformed-" + i, Error(normal, observedNormal) < .015f, Error(normal, observedNormal));
                    SaveSsrPreview("real-baked-gi-skin-" + i, ReadSceneTarget(target), 129, 129, false);
                }
                surface.gi.probeAnchor = anchor; stage.giBaseScale = 0; stage.lightDirection = Vector3.up; stage.lightRadiance = new Vector3(2, 3, 1);
                bone.localRotation = Quaternion.identity; yield return null;
                stage.directionalGiWeight = 0; var neutral = Render(); var noGi = Rgb(Pixel(target)); var emitted = Rgb(Pixel(neutral.emission));
                stage.directionalGiWeight = 1; var weighted = Render(); var reference = Rgb(Pixel(weighted.bakedDiffuseGi));
                var wanted = emitted + Vector3.Scale(noGi - emitted, reference);
                Check("real-probe-multiplies-dynamic-light-once-not-emission", Error(wanted, Rgb(Pixel(target))) < .012f && (noGi - emitted).sqrMagnitude > .001f, Error(wanted, Rgb(Pixel(target))));
                SaveSsrPreview("real-baked-gi-skin-dynamic", ReadSceneTarget(target), 129, 129, false);
                camera.targetTexture = null; host.SetActive(false);
                foreach (var value in _owned) if (value != null) Destroy(value); _owned.Clear();
                SceneManager.SetActiveScene(original); yield return SceneManager.UnloadSceneAsync(loaded); loaded = default;
                Check("scene-unload-restores-original-lightmap-count", LightmapSettings.lightmaps.Length == oldMapCount);
            }
            finally
            {
                if (camera != null) camera.targetTexture = null;
                foreach (var value in _owned) if (value != null) Destroy(value); _owned.Clear();
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                if (loaded.IsValid() && loaded.isLoaded) SceneManager.UnloadSceneAsync(loaded);
                bundle.Unload(false);
            }
        }

        private static Vector2 BakedGiRayUv(Mesh mesh, Transform transform, Ray ray)
        {
            var vertices = mesh.vertices; var triangles = mesh.triangles; var uv = mesh.uv2;
            float nearest = float.PositiveInfinity; Vector2 result = default;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                var v = transform.TransformPoint(vertices[a]); var e1 = transform.TransformPoint(vertices[b]) - v; var e2 = transform.TransformPoint(vertices[c]) - v;
                var cross = Vector3.Cross(ray.direction, e2); float det = Vector3.Dot(e1, cross); if (Mathf.Abs(det) < 1e-8f) continue;
                var delta = ray.origin - v; float u = Vector3.Dot(delta, cross) / det; var q = Vector3.Cross(delta, e1); float w = Vector3.Dot(ray.direction, q) / det;
                float t = Vector3.Dot(e2, q) / det;
                if (u < 0 || w < 0 || u + w > 1 || t < 0 || t >= nearest) continue;
                nearest = t; result = uv[a] * (1 - u - w) + uv[b] * u + uv[c] * w;
            }
            if (float.IsInfinity(nearest)) throw new InvalidOperationException("CPU reference ray missed baked receiver.");
            return result;
        }
        private static Color BakedGiBilinear(Color[] pixels, int width, int height, Vector2 uv)
        {
            float x = uv.x * width - .5f, y = uv.y * height - .5f; int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            Color At(int a, int b) => pixels[Mathf.Clamp(b, 0, height - 1) * width + Mathf.Clamp(a, 0, width - 1)];
            return Color.Lerp(Color.Lerp(At(x0, y0), At(x0 + 1, y0), x - x0), Color.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), x - x0), y - y0);
        }
    }
}
