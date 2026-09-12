using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifySceneDecalLights(Report report)
        {
            var previous = FindObjectsOfType<Renderer>(); var forced = new bool[previous.Length];
            for (int i = 0; i < previous.Length; i++) { forced[i] = previous[i].forceRenderingOff; previous[i].forceRenderingOff = true; }
            try
            {
                void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "decal-light-" + name, ok, error);
                var host = Own(new GameObject("Decal-light scene host")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
                camera.transform.position = new Vector3(0, 0, -4); camera.orthographic = true; camera.orthographicSize = 2; camera.aspect = 1;
                camera.nearClipPlane = .1f; camera.farClipPlane = 40; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.cullingMask = 1 << 26;
                var target = Own(new RenderTexture(129, 129, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var stage = host.AddComponent<SceneDeferredCamera>(); stage.sceneEnabled = true; stage.sceneLayers = 1 << 25; stage.lightRadiance = stage.ambientIrradiance = Vector3.zero;
                var surfaceObject = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); surfaceObject.layer = 25; surfaceObject.transform.localScale = new Vector3(3.8f, 3.8f, 1);
                var surface = new SceneDeferredCamera.Surface { renderer = surfaceObject.GetComponent<Renderer>(), cull = CullMode.Off };
                surface.inputs.albedo = new Vector3(.4f, .6f, .2f); surface.inputs.mos = new Vector3(.1f, .7f, .4f); surface.inputs.emission = new Vector3(.1f, .2f, .3f); stage.surfaces = new[] { surface };
                SceneDeferredCamera.Frame Render() { camera.Render(); if (!stage.TryGetFrame(out var frame)) throw new InvalidOperationException(stage.UnavailableReason); return frame; }
                Color Pixel(RenderTexture rt, float u = .5f, float v = .5f) => ReadSceneTarget(rt)[Mathf.Clamp((int)(v * rt.height), 0, rt.height - 1) * rt.width + Mathf.Clamp((int)(u * rt.width), 0, rt.width - 1)];
                Vector3 Rgb(Color c) => new Vector3(c.r, c.g, c.b);
                float Error(Vector3 a, Vector3 b) => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z));
                var initial = Render(); var baseline = ReadSceneTarget(target);
                Vector3 albedo = Rgb(Pixel(initial.albedoCoverage)), mos = Rgb(Pixel(initial.mosDepth)), emission = Rgb(Pixel(initial.emission));
                Check("default-disabled-no-light-resources", stage.SubmittedLights == 0 && stage.LightDrawCalls == 0 && stage.LightTargetCount == 0 && stage.LightBufferCapacity == 0);
                var atlas = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); atlas.SetPixel(0, 0, new Color(2, 3, 4, 0)); atlas.Apply();
                var options = stage.decalLighting; options.enabled = true; options.atlas = atlas;
                var light = new SceneDecalLight { position = new Vector3(0, 0, -1), range = 2, radiance = new Vector3(.5f, .4f, .3f) };
                options.lights = new[] { light };
                Vector3 CpuLight(SceneDecalLight value, Vector3 world, Vector3 sample, Vector3 n)
                {
                    var rot = value.rotation.normalized; Vector3 x = rot * Vector3.right, y = rot * Vector3.up, z = rot * Vector3.forward;
                    Vector3 delta = world - value.position, source = value.position; float distance;
                    if (value.shape == SceneDecalLightShape.Point) distance = delta.magnitude;
                    else if (value.shape == SceneDecalLightShape.Capsule) { source += x * Mathf.Clamp(Vector3.Dot(delta, x), -value.halfLength, value.halfLength); distance = (world - source).magnitude; }
                    else
                    {
                        distance = Vector3.Dot(delta, z); if (distance <= 0 || distance >= value.range) return Vector3.zero;
                        float px = Vector3.Dot(delta, x) / (value.halfSize.x + value.areaSpread.x * distance), py = Vector3.Dot(delta, y) / (value.halfSize.y + value.areaSpread.y * distance);
                        if (Mathf.Abs(px) > 1 || Mathf.Abs(py) > 1) return Vector3.zero;
                        source += x * px * value.halfSize.x + y * py * value.halfSize.y;
                    }
                    Vector3 l = (source - world).normalized, v = camera.orthographic ? Vector3.back : (camera.transform.position - world).normalized, h = (v + l).normalized;
                    float nl = Mathf.Clamp01(Vector3.Dot(n, l)), nv = Mathf.Clamp01(Vector3.Dot(n, v)), nh = Mathf.Clamp01(Vector3.Dot(n, h)), vh = Mathf.Clamp01(Vector3.Dot(v, h));
                    float a2 = Mathf.Pow(Mathf.Max(1 - mos.z, .045f), 4), den = nh * nh * (a2 - 1) + 1;
                    float d = a2 / Mathf.Max(Mathf.PI * den * den, 1e-8f), visibility = .5f / Mathf.Max(nl * Mathf.Sqrt(nv * nv * (1 - a2) + a2) + nv * Mathf.Sqrt(nl * nl * (1 - a2) + a2), 1e-6f);
                    Vector3 f0 = Vector3.Lerp(Vector3.one * .04f, albedo, mos.x), f = f0 + (Vector3.one - f0) * Mathf.Pow(1 - vh, 5);
                    var brdf = Vector3.Scale(Vector3.one - f, albedo) * ((1 - mos.x) / Mathf.PI) * value.diffuseScale + f * d * visibility * value.specularScale;
                    return Vector3.Scale(brdf, Vector3.Scale(sample, value.radiance)) * (nl * Mathf.Pow(Mathf.Clamp01(1 - distance / value.range), value.falloffExponent));
                }
                foreach (var backend in new[] { SceneDecalLightBackend.Scalar, SceneDecalLightBackend.Instanced })
                {
                    options.backend = backend; options.allowInstancingFallback = false;
                    foreach (var shape in new[] { SceneDecalLightShape.Point, SceneDecalLightShape.Capsule, SceneDecalLightShape.Area })
                    {
                        light.shape = shape; light.areaSpread = Vector2.one * .5f;
                        var frame = Render(); var expected = emission + CpuLight(light, Vector3.zero, new Vector3(2, 3, 4), Vector3.back);
                        float error = Error(Rgb(Pixel(target)), expected);
                        Check(backend + "-" + shape + "-cpu-brdf-and-attenuation", error < .003f, error);
                        Check(backend + "-" + shape + "-does-not-modify-material-emission", Error(Rgb(Pixel(frame.emission)), emission) == 0 && Error(Rgb(Pixel(frame.albedoCoverage)), albedo) == 0);
                        Check(backend + "-" + shape + "-submitted-real-light-work", stage.SubmittedLights == 1 && stage.LightDrawCalls == 1 && stage.ActiveLightBackend == backend && stage.LightTargetCount == 1);
                    }
                }
                light.shape = SceneDecalLightShape.Point; options.backend = SceneDecalLightBackend.Instanced;
                light.receiverGroup = 2; Render(); Check("receiver-group-rejection", ScenePixelsEqual(baseline, ReadSceneTarget(target))); light.receiverGroup = 1;
                Render(); Check("matching-group-lighting", !ScenePixelsEqual(baseline, ReadSceneTarget(target))); light.receiverGroup = 0;
                surface.receiverGroup = 0; Render(); Check("zero-light-group-reaches-nondecal-surface", !ScenePixelsEqual(baseline, ReadSceneTarget(target))); surface.receiverGroup = 1;
                light.position = new Vector3(0, 0, 1); Render(); Check("back-normal-rejects-direct-light", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
                light.position = new Vector3(0, 0, -3); Render(); Check("outside-radial-range", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
                light.position = Vector3.zero; Render(); Check("point-singularity-is-finite-and-dark", Error(Rgb(Pixel(target)), emission) == 0);
                light.position = new Vector3(0, 0, -1); light.diffuseScale = 0; Render();
                Check("specular-only-response-cpu", Error(Rgb(Pixel(target)), emission + CpuLight(light, Vector3.zero, new Vector3(2, 3, 4), Vector3.back)) < .003f);
                light.diffuseScale = 1; light.specularScale = 0; Render();
                Check("diffuse-only-response-cpu", Error(Rgb(Pixel(target)), emission + CpuLight(light, Vector3.zero, new Vector3(2, 3, 4), Vector3.back)) < .003f);
                light.specularScale = 1; light.falloffExponent = 2; Render(); Check("falloff-exponent-cpu", Error(Rgb(Pixel(target)), emission + CpuLight(light, Vector3.zero, new Vector3(2, 3, 4), Vector3.back)) < .003f); light.falloffExponent = 1;
                light.position = new Vector3(100, 0, -1); Render(); Check("offscreen-volume-culled-no-targets", stage.CulledLights == 1 && stage.SubmittedLights == 0 && stage.LightDrawCalls == 0 && stage.LightTargetCount == 0 && stage.LightBufferCapacity == 0);
                light.position = new Vector3(0, 0, -3.9f); light.range = 5; Render(); Check("camera-inside-volume-conservative-bound", stage.SubmittedLights == 1 && Error(Rgb(Pixel(target)), emission + CpuLight(light, Vector3.zero, new Vector3(2, 3, 4), Vector3.back)) < .003f);
                light.position = new Vector3(0, 0, -1); light.range = 2;

                var patterned = Own(new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true)); patterned.filterMode = FilterMode.Point; patterned.wrapMode = TextureWrapMode.Clamp;
                for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) patterned.SetPixel(x, y, new Color(x + 1, y + 1, .5f, 0)); patterned.Apply(); options.atlas = patterned;
                Vector3 WorldAt(float u, float v) => new Vector3(((int)(u * 129) + .5f) / 129 * 4 - 2, ((int)(v * 129) + .5f) / 129 * 4 - 2, 0);
                light.monitorUV = new Vector4(1, 1, .125f, .125f); Render();
                Check("point-samples-one-atlas-texel-ignoring-alpha", Error(Rgb(Pixel(target)), emission + CpuLight(light, Vector3.zero, new Vector3(1, 1, .5f), Vector3.back)) < .003f);
                light.shape = SceneDecalLightShape.Capsule; light.halfLength = 1; light.monitorUV = new Vector4(.75f, 0, .125f, .125f); Render();
                foreach (float u in new[] { .35f, .65f })
                {
                    var world = WorldAt(u, .5f); float along = Mathf.Clamp(world.x, -1, 1) * .5f + .5f;
                    var texel = Rgb(patterned.GetPixel(Mathf.Clamp((int)((.125f + .75f * along) * 4), 0, 3), 0));
                    Check("capsule-line-atlas-" + u, Error(Rgb(Pixel(target, u)), emission + CpuLight(light, world, texel, Vector3.back)) < .004f);
                }
                SaveSsrPreview("decal-light-capsule-line", ReadSceneTarget(target), 129, 129, false);
                light.rotation = Quaternion.Euler(0, 0, 90); Render(); Check("capsule-rotation-rotates-light-footprint", Rgb(Pixel(target, .5f, .65f)).x > Rgb(Pixel(target, .5f, .35f)).x); light.rotation = Quaternion.identity;
                light.shape = SceneDecalLightShape.Area; light.halfSize = Vector2.one * .5f; light.areaSpread = Vector2.one * .5f; light.monitorUV = new Vector4(1, 1, 0, 0); Render();
                foreach (var uv in new[] { new Vector2(.35f, .35f), new Vector2(.65f, .65f) })
                {
                    var world = WorldAt(uv.x, uv.y); var sample = Rgb(patterned.GetPixel(Mathf.Clamp((int)((world.x * .5f + .5f) * 4), 0, 3), Mathf.Clamp((int)((world.y * .5f + .5f) * 4), 0, 3)));
                    Check("area-plane-atlas-" + uv.x, Error(Rgb(Pixel(target, uv.x, uv.y)), emission + CpuLight(light, world, sample, Vector3.back)) < .004f);
                }
                Check("area-trapezoid-bounds", Error(Rgb(Pixel(target, .8f)), emission) == 0 && Error(Rgb(Pixel(target, .7f)), emission) > .001f);
                SaveSsrPreview("decal-light-area-atlas", ReadSceneTarget(target), 129, 129, false);
                light.rotation = Quaternion.Euler(0, 180, 0); Render(); Check("area-one-sided-emission", ScenePixelsEqual(baseline, ReadSceneTarget(target))); light.rotation = Quaternion.identity;
                light.areaSpread = Vector2.zero; Render(); Check("area-zero-spread-prism", Error(Rgb(Pixel(target, .7f)), emission) == 0 && Error(Rgb(Pixel(target, .55f)), emission) > .001f);

                options.atlas = atlas;
                var many = new SceneDecalLight[110];
                for (int i = 0; i < many.Length; i++) many[i] = new SceneDecalLight {
                    shape = (SceneDecalLightShape)(i % 3), position = new Vector3((i % 11 - 5) * .16f, (i / 11 - 5) * .14f, -.8f - i % 4 * .1f), range = 2,
                    radiance = new Vector3(.015f + i % 3 * .002f, .012f, .01f), halfLength = .4f, areaSpread = Vector2.one * .5f
                };
                options.lights = many; options.backend = SceneDecalLightBackend.Scalar; Render(); var scalar = ReadSceneTarget(target);
                Check("110-scalar-light-draws", stage.SubmittedLights == 110 && stage.LightDrawCalls == 110 && stage.LightBufferCapacity == 0);
                options.backend = SceneDecalLightBackend.Instanced; Render(); var instanced = ReadSceneTarget(target);
                float instancedError = PixelError(scalar, instanced); Check("110-independent-instances-match-scalar-image", instancedError < .003f, instancedError);
                Check("110-lights-one-gpu-instanced-draw", stage.SubmittedLights == 110 && stage.LightDrawCalls == 1 && stage.LightBufferCapacity == 128);
                if (Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DECAL_LIGHTS") == "1")
                {
                    bool started = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                    try { if (started) { Render(); ReadSceneTarget(target); } }
                    finally { if (started) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                    Check("requested-native-capture-of-110-instances", started && ended);
                }
                Vector3 sum = emission; foreach (var value in many) sum += CpuLight(value, Vector3.zero, new Vector3(2, 3, 4), Vector3.back);
                Check("110-mixed-shape-lights-cpu-sum", Error(Rgb(Pixel(target)), sum) < .005f, Error(Rgb(Pixel(target)), sum));
                options.batchSize = 32; Render(); Check("batch-offsets-preserve-all-110-lights", stage.LightDrawCalls == 4 && PixelError(instanced, ReadSceneTarget(target)) < .003f); options.batchSize = 256;
                SaveSsrPreview("decal-light-110-instanced", instanced, 129, 129, false);
                camera.orthographic = false; camera.fieldOfView = 50; options.backend = SceneDecalLightBackend.Scalar; Render(); scalar = ReadSceneTarget(target);
                options.backend = SceneDecalLightBackend.Instanced; Render(); Check("perspective-scalar-instanced-equivalence", PixelError(scalar, ReadSceneTarget(target)) < .003f); camera.orthographic = true;
                foreach (var value in many) value.radiance = Vector3.one * 65504; Render();
                var high = Pixel(target); Check("overlapping-hdr-lights-saturate-without-infinity", high.r == 65504 && high.g == 65504 && high.b == 65504);
                options.lights = new[] { light }; light.shape = SceneDecalLightShape.Point; light.areaSpread = Vector2.zero; light.radiance = new Vector3(.5f, .4f, .3f);
                light.monitorUV = new Vector4(0, 0, .25f, .5f);

                // Actual dedicated producer and distinct emissive meshes share atlas content with scene lighting.
                var sourceHost = Own(new GameObject("Light monitor source camera")); var sourceCamera = sourceHost.AddComponent<Camera>(); sourceCamera.CopyFrom(camera); sourceCamera.enabled = false;
                sourceCamera.transform.position = new Vector3(0, 0, -3); sourceCamera.orthographicSize = 1; sourceCamera.targetTexture = null; sourceCamera.cullingMask = 1 << 24;
                GameObject Panel(float x, Vector3 color)
                {
                    var panel = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); panel.layer = 24; panel.transform.position = new Vector3(x, 0, 0); panel.transform.localScale = new Vector3(1, 2, 1);
                    var material = Own(new Material(Resources.Load<Shader>("MonitorEmission"))); material.SetTexture("_MonitorTex", Texture2D.whiteTexture); material.SetVector("_MonitorTint", color); panel.GetComponent<Renderer>().sharedMaterial = material; return panel;
                }
                var left = Panel(-.5f, new Vector3(4, 0, 0)); var right = Panel(.5f, new Vector3(0, 2, 0));
                var monitor = sourceHost.AddComponent<HdrMonitor>(); monitor.width = 65; monitor.height = 33; monitor.monitorEnabled = true;
                Check("real-monitor-published-for-lighting", monitor.TryUpdate(0, 0, out _)); options.monitor = monitor; Render(); var monitorFirst = Rgb(Pixel(target));
                Check("monitor-red-content-lights-other-surface", Error(monitorFirst, emission + CpuLight(light, Vector3.zero, new Vector3(4, 0, 0), Vector3.back)) < .004f);
                left.GetComponent<Renderer>().sharedMaterial.SetVector("_MonitorTint", new Vector3(8, 0, 0)); monitor.TryUpdate(0, 1, out _); var frameWithMonitor = Render();
                Check("monitor-animation-doubles-only-direct-red", Mathf.Abs((Pixel(target).r - emission.x) - 2 * (monitorFirst.x - emission.x)) < .004f && Error(Rgb(Pixel(frameWithMonitor.emission)), emission) == 0);
                light.monitorUV.z = .75f; Render(); Check("monitor-uv-selects-different-light-color", Error(Rgb(Pixel(target)), emission + CpuLight(light, Vector3.zero, new Vector3(0, 2, 0), Vector3.back)) < .004f);
                SaveSsrPreview("decal-light-live-monitor", ReadSceneTarget(target), 129, 129, false);
                monitor.enabled = false; camera.Render(); Check("stale-monitor-rejected-and-resources-released", !stage.TryGetFrame(out _) && stage.LightTargetCount == 0 && stage.LightBufferCapacity == 0);
                options.monitor = null; sourceHost.SetActive(false); left.SetActive(false); right.SetActive(false); Render();
                options.atlas = null; Render(); Check("null-atlas-is-unit-radiance", Error(Rgb(Pixel(target)), emission + CpuLight(light, Vector3.zero, Vector3.one, Vector3.back)) < .003f);
                options.atlas = atlas; Render(); var beforeMaterialDecal = ReadSceneTarget(target);
                var materialDecal = new SceneDeferredCamera.Decal(); materialDecal.inputs.albedo = new Vector3(.8f, .1f, .2f); stage.decals = new[] { materialDecal };
                var modifiedMaterial = Render(); albedo = Rgb(Pixel(modifiedMaterial.albedoCoverage));
                Check("material-decal-albedo-feeds-direct-light-brdf", Error(Rgb(Pixel(target)), emission + CpuLight(light, Vector3.zero, new Vector3(2, 3, 4), Vector3.back)) < .003f && !ScenePixelsEqual(beforeMaterialDecal, ReadSceneTarget(target)));
                var normalMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); normalMap.SetPixel(0, 0, new Color(.8f, .5f, .9f, 1)); normalMap.Apply();
                materialDecal.inputs.normalMap = normalMap; materialDecal.normalWeight = 1; Render();
                Check("material-decal-normal-feeds-direct-light-brdf", Error(Rgb(Pixel(target)), emission + CpuLight(light, Vector3.zero, new Vector3(2, 3, 4), new Vector3(.6f, 0, -.8f))) < .003f);
                stage.decals = Array.Empty<SceneDeferredCamera.Decal>(); var restoredMaterial = Render(); albedo = Rgb(Pixel(restoredMaterial.albedoCoverage));
                Check("removing-material-decals-recovers-lighting", ScenePixelsEqual(beforeMaterialDecal, ReadSceneTarget(target)));
                surface.inputs.mos.y = 0; Render(); Check("ao-does-not-multiply-direct-light-twice", ScenePixelsEqual(beforeMaterialDecal, ReadSceneTarget(target))); surface.inputs.mos.y = .7f;
                RenderTexture Accumulator(SceneDeferredCamera owner)
                {
                    var renderer = typeof(SceneDeferredCamera).GetField("_decalLights", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(owner);
                    return (RenderTexture)renderer.GetType().GetProperty("Accumulation").GetValue(renderer);
                }
                Render(); var oldAccumulation = Accumulator(stage); oldAccumulation.Release(); Render();
                Check("lost-light-target-recreated", Accumulator(stage).IsCreated() && Accumulator(stage) != oldAccumulation && ScenePixelsEqual(beforeMaterialDecal, ReadSceneTarget(target)));
                var smaller = Own(new RenderTexture(65, 33, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); smaller.Create(); camera.targetTexture = smaller; Render();
                Check("light-target-resize-follows-host", Accumulator(stage).width == 65 && Accumulator(stage).height == 33 && Error(Rgb(Pixel(smaller)), emission + CpuLight(light, Vector3.zero, new Vector3(2, 3, 4), Vector3.back)) < .003f);
                camera.targetTexture = target; Render();
                var otherHost = Own(new GameObject("Independent light camera")); var otherCamera = otherHost.AddComponent<Camera>(); otherCamera.CopyFrom(camera); otherCamera.enabled = false; otherCamera.targetTexture = smaller;
                var otherStage = otherHost.AddComponent<SceneDeferredCamera>(); otherStage.sceneEnabled = true; otherStage.sceneLayers = 1 << 25; otherStage.surfaces = new[] { surface }; otherStage.lightRadiance = otherStage.ambientIrradiance = Vector3.zero;
                otherStage.decalLighting.enabled = true; otherStage.decalLighting.lights = new[] { new SceneDecalLight { position = new Vector3(0, 0, -1), radiance = new Vector3(0, 5, 0), range = 2 } };
                otherCamera.Render(); Check("two-cameras-own-separate-light-targets-and-buffers", Accumulator(otherStage) != Accumulator(stage) && otherStage.LightBufferCapacity == 1 && stage.LightBufferCapacity == 1 && Pixel(smaller).g > Pixel(target).g && ScenePixelsEqual(beforeMaterialDecal, ReadSceneTarget(target)));
                otherStage.enabled = false; Render(); Check("disabling-other-camera-preserves-lighting", ScenePixelsEqual(beforeMaterialDecal, ReadSceneTarget(target))); otherHost.SetActive(false);
                options.backend = SceneDecalLightBackend.Auto; Render(); Check("auto-selects-tested-instanced-backend", stage.ActiveLightBackend == SceneDecalLightBackend.Instanced && stage.LightFallbackReason == null); options.backend = SceneDecalLightBackend.Instanced;
                camera.nearClipPlane = 5; camera.projectionMatrix = Matrix4x4.Ortho(-2, 2, -2, 2, .1f, 40);
                light.position = new Vector3(0, 0, -.1f); light.range = .2f; Render();
                Check("custom-projection-overrides-near-property-for-culling", stage.SubmittedLights == 1 && Error(Rgb(Pixel(target)), emission + CpuLight(light, Vector3.zero, new Vector3(2, 3, 4), Vector3.back)) < .004f);
                camera.nearClipPlane = .1f; camera.ResetProjectionMatrix(); light.position = new Vector3(0, 0, -1); light.range = 2;
                var actor = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); actor.layer = 26; actor.transform.position = new Vector3(0, 0, -1.5f); actor.transform.localScale = Vector3.one * .5f;
                var actorMaterial = Own(new Material(Resources.Load<Shader>("MonitorEmission"))); actorMaterial.SetTexture("_MonitorTex", Texture2D.whiteTexture); actorMaterial.SetVector("_MonitorTint", new Vector3(0, 0, 1)); actor.GetComponent<Renderer>().sharedMaterial = actorMaterial;
                Render(); Check("forward-foreground-does-not-receive-scene-lights", Error(Rgb(Pixel(target)), Vector3.forward) < .001f); actor.transform.position = Vector3.forward; Render();
                Check("scene-depth-still-occludes-forward-behind", Error(Rgb(Pixel(target)), Vector3.forward) > .01f); actor.SetActive(false);
                options.lights = Array.Empty<SceneDecalLight>(); Render(); Check("empty-light-set-releases-all-extra-gpu-work", stage.LightDrawCalls == 0 && stage.LightTargetCount == 0 && stage.LightBufferCapacity == 0 && ScenePixelsEqual(baseline, ReadSceneTarget(target)));
                options.lights = new[] { light }; light.radiance = Vector3.zero; Render(); Check("zero-radiance-no-light-work", stage.LightDrawCalls == 0 && stage.LightTargetCount == 0);
                light.radiance = Vector3.one; Render(); options.enabled = false; Render(); Check("lighting-disable-is-exact-noop", ScenePixelsEqual(baseline, ReadSceneTarget(target)) && stage.LightBufferCapacity == 0);
                options.enabled = true; Render(); light.range = 0; camera.Render(); Check("invalid-range-fails-closed", !stage.TryGetFrame(out _) && stage.LightTargetCount == 0); light.range = 2;
                Render(); light.rotation = new Quaternion(0, 0, 0, 0); camera.Render(); Check("zero-quaternion-rejected", !stage.TryGetFrame(out _)); light.rotation = Quaternion.identity;
                Render(); light.radiance = new Vector3(float.NaN, 0, 0); camera.Render(); Check("nonfinite-radiance-rejected", !stage.TryGetFrame(out _)); light.radiance = Vector3.one;
                Render(); options.batchSize = 0; camera.Render(); Check("invalid-batch-size-rejected", !stage.TryGetFrame(out _)); options.batchSize = 256;
                Render(); stage.enabled = false; Check("component-disable-releases-light-resources", stage.LightBufferCapacity == 0 && stage.LightTargetCount == 0 && camera.GetCommandBuffers(CameraEvent.BeforeForwardOpaque).Length == 0);
                stage.enabled = true; Render(); Check("component-reenable-rebuilds-light-resources", stage.LightBufferCapacity == 1 && stage.LightTargetCount == 1);
                host.SetActive(false); surfaceObject.SetActive(false);
            }
            finally { for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i]; }
        }
    }
}
