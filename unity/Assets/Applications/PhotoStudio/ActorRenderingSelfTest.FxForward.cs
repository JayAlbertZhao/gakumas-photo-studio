using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyFxForward(Report report)
        {
            yield return null;
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "fx-forward-" + name, ok, error);
            var previous = FindObjectsOfType<Renderer>(); var forced = previous.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in previous) r.forceRenderingOff = true;
            var active = RenderTexture.active;
            using var low = new LowResolutionFxRenderer(); using var heavy = new HeavyFxRenderer();
            try
            {
                const int width = 97, height = 65;
                var host = Own(new GameObject("Current lit mixed-resolution FX acceptance")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.renderingPath = RenderingPath.Forward; camera.allowHDR = true; camera.allowMSAA = false;
                camera.transform.position = new Vector3(0, 0, -4); camera.orthographic = true; camera.orthographicSize = 1.6f;
                camera.aspect = (float)width / height; camera.nearClipPlane = .1f; camera.farClipPlane = 25;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.13f, .21f, .33f, 1); camera.cullingMask = 0;
                RenderTexture Target(int w, int h, RenderTextureFormat format, int depthBits = 0)
                {
                    var t = Own(new RenderTexture(w, h, depthBits, format, RenderTextureReadWrite.Linear)); t.Create(); return t;
                }
                void Upload(RenderTexture t, Color[] pixels)
                {
                    var upload = Own(new Texture2D(t.width, t.height, TextureFormat.RGBAFloat, false, true));
                    upload.SetPixels(pixels); upload.Apply(); Graphics.Blit(upload, t);
                }
                float Difference(Color[] a, Color[] b)
                {
                    if (a.Length != b.Length) return float.PositiveInfinity;
                    float max = 0; for (int i = 0; i < a.Length; i++) for (int c = 0; c < 4; c++)
                    { float d = Mathf.Abs(a[i][c] - b[i][c]); if (float.IsNaN(d) || float.IsInfinity(d)) return float.PositiveInfinity; max = Mathf.Max(max, d); }
                    return max;
                }
                var source = Target(width, height, RenderTextureFormat.ARGBFloat);
                var depth = Target(width, height, RenderTextureFormat.RFloat);
                var cameraTarget = Target(width, height, RenderTextureFormat.ARGBFloat, 24); camera.targetTexture = cameraTarget;
                var background = Enumerable.Repeat(camera.backgroundColor, width * height).ToArray(); Upload(source, background);
                Upload(depth, Enumerable.Repeat(new Color(24, 0, 0, 0), width * height).ToArray());
                LowResolutionFxSurface Surface(string name, Vector3 position, Vector3 scale)
                {
                    var go = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.name = name; go.layer = 25;
                    go.transform.position = position; go.transform.localScale = scale;
                    var mesh = Own(Instantiate(go.GetComponent<MeshFilter>().sharedMesh)); mesh.uv2 = mesh.uv;
                    mesh.colors = Enumerable.Repeat(Color.white, mesh.vertexCount).ToArray(); go.GetComponent<MeshFilter>().sharedMesh = mesh;
                    return new LowResolutionFxSurface { renderer = go.GetComponent<Renderer>(), opacity = 1, fog = false, resolution = FxResolution.Full,
                        lighting = new FxSurfaceLighting { enabled = true, inputs = new SceneDeferredCamera.MaterialInputs {
                            albedo = new Vector3(.37f, .53f, .29f), mos = new Vector3(.17f, .73f, .36f), alpha = .63f,
                            emission = new Vector3(.031f, .052f, .017f) } } };
                }
                var back = Surface("Lit rear FX", Vector3.zero, new Vector3(3.73f, 2.81f, 1));
                var front = Surface("Lit front FX", new Vector3(.27f, -.13f, -.63f), new Vector3(2.13f, 1.71f, 1));
                front.lighting.inputs.albedo = new Vector3(.61f, .24f, .13f); front.lighting.inputs.alpha = .37f;
                var geometry = new LowResolutionFxSettings { enabled = true, surfaces = new[] { back, front } };
                var joint = new HeavyFxSettings { enabled = true, geometry = geometry };
                var settings = geometry.lighting; settings.enabled = true; settings.allowBruteForceFallback = false;
                settings.lightRadiance = new Vector3(.23f, .17f, .11f); settings.ambientIrradiance = new Vector3(.07f, .13f, .09f);
                var locals = settings.localLights; locals.enabled = true;
                locals.lights = Enumerable.Range(0, 97).Select(i => new SceneDecalLight {
                    shape = (SceneDecalLightShape)(i % 4), position = new Vector3((i % 11 - 5) * .91f, (i / 11 - 4) * .81f, -1.2f - i % 3 * .31f),
                    rotation = Quaternion.Euler((i % 3 - 1) * 11, (i % 5 - 2) * 7, i * 13), range = 1.67f + i % 5 * .13f,
                    halfLength = .37f, halfSize = new Vector2(.31f, .43f), areaSpread = new Vector2(.33f, .21f),
                    spotInnerAngle = 59, spotOuterAngle = 113, radiance = new Vector3(.31f, .17f + i % 3 * .1f, .23f),
                    falloffExponent = 1 + i % 3, diffuseScale = .9f, specularScale = .7f, backlightScale = .2f,
                    receiverGroup = i % 7 == 0 ? 2 : 0 }).ToArray();
                bool isHeavy = false; var depthEncoding = FogDepthEncoding.LinearEye;
                LowResolutionFxRenderer.Frame lowFrame = default; HeavyFxRenderer.Frame heavyFrame = default;
                Color[] Render()
                {
                    var before = RenderTexture.active;
                    bool ok = isHeavy ? heavy.TryRender(source, new FogVolumeDepth(depth, depthEncoding), camera, joint, 2.3, out heavyFrame)
                        : low.TryRender(source, new FogVolumeDepth(depth, depthEncoding), camera, geometry, out lowFrame);
                    if (!ok) throw new InvalidOperationException(isHeavy ? heavy.UnavailableReason : low.UnavailableReason);
                    if (before != RenderTexture.active) throw new InvalidOperationException("Lit FX changed caller's active target");
                    return ReadSceneTarget(isHeavy ? heavyFrame.color : lowFrame.color);
                }
                Color[] Pair(string name)
                {
                    settings.backend = SceneForwardLightBackend.BruteForce; var brute = Render();
                    Check(name + "-brute-no-grid", (isHeavy ? heavy.LightingTileCount : low.LightingTileCount) == 0);
                    settings.backend = SceneForwardLightBackend.Tiled; var tiled = Render(); float error = Difference(brute, tiled);
                    Check(name + "-tiled-brute-whole-rgba", error <= .00002f, error); return tiled;
                }
                void Resolution(FxResolution value) { foreach (var s in geometry.surfaces) s.resolution = value; }
                // Full-resolution oracle is the separately validated camera producer, not an FX shader reading back its own intermediate.
                var forward = host.AddComponent<SceneForwardLightingCamera>(); forward.surfaceLayers = 1 << 25;
                var reference = forward.settings; reference.enabled = true; reference.localLights = locals;
                reference.lightRadiance = settings.lightRadiance; reference.ambientIrradiance = settings.ambientIrradiance;
                reference.surfaces = geometry.surfaces.Select(s => new SceneForwardSurface { renderer = s.renderer, cull = s.cull, inputs = s.lighting.inputs, gi = s.lighting.gi }).ToArray();
                reference.enabled = false; camera.Render(); background = ReadSceneTarget(cameraTarget); Upload(source, background);
                reference.enabled = true;
                camera.Render(); if (forward.UnavailableReason != null) throw new InvalidOperationException(forward.UnavailableReason);
                var expected = ReadSceneTarget(cameraTarget); forward.enabled = false;
                foreach (bool producer in new[] { false, true })
                {
                    isHeavy = producer; string prefix = producer ? "joint" : "standalone";
                    foreach (var res in new[] { FxResolution.Full, FxResolution.Half, FxResolution.Quarter })
                    {
                        Resolution(res); var actual = Pair(prefix + "-" + res); float error = Difference(expected, actual);
                        if (res == FxResolution.Full) Check(prefix + "-independent-camera-full-rgba", error <= .0002f, error);
                        if (res != FxResolution.Full)
                        {
                            RenderTexture effect, range;
                            bool found = producer ? heavyFrame.TryGetLastBatch(res, out effect, out range) : lowFrame.TryGetLastBatch(res, out effect, out range);
                            Check(prefix + "-" + res + "-actual-reduced-target", found && effect.width == (width + (int)res - 1) / (int)res && effect.height == (height + (int)res - 1) / (int)res);
                            Check(prefix + "-" + res + "-bounded-reconstruction", error < .15f, error);
                        }
                        Check(prefix + "-" + res + "-positive-grid", (producer ? heavy.LightingTileCount : low.LightingTileCount) == 7 * 5);
                    }
                    Resolution(FxResolution.Full); camera.targetTexture = null; var detached = Pair(prefix + "-explicit-size-no-camera-target");
                    Check(prefix + "-explicit-source-dimensions", Difference(expected, detached) <= .0002f, Difference(expected, detached)); camera.targetTexture = cameraTarget;
                    for (int i = 0; i < background.Length; i++) background[i].a = .1f + (i % 7) * .1f;
                    Upload(source, background); var alphaImage = Render(); float alphaError = 0;
                    for (int i = 0; i < background.Length; i++) alphaError = Mathf.Max(alphaError, Mathf.Abs(alphaImage[i].a - background[i].a));
                    Check(prefix + "-preserves-source-alpha", alphaError == 0, alphaError);
                    for (int i = 0; i < background.Length; i++) background[i].a = 1; Upload(source, background);
                    foreach (bool perspective in new[] { false, true }) foreach (int tile in new[] { 8, 16, 32 })
                    {
                        settings.tileSize = tile; camera.orthographic = !perspective; camera.transform.position = new Vector3(.13f, -.07f, -4);
                        camera.ResetProjectionMatrix(); var p = camera.projectionMatrix; p.m02 += .09f; p.m12 -= .07f; camera.projectionMatrix = p;
                        Resolution(FxResolution.Quarter); Pair(prefix + "-moving-" + perspective + "-tile" + tile);
                    }
                    camera.transform.position = new Vector3(0, 0, -4); camera.orthographic = true; camera.ResetProjectionMatrix(); settings.tileSize = 16;
                    Resolution(FxResolution.Full);
                    var original = Render(); back.lighting.enabled = false; front.lighting.enabled = false; var unlit = Render();
                    Check(prefix + "-opt-in-contributes", Difference(original, unlit) > .1f);
                    Check(prefix + "-unlit-no-light-resources", (producer ? heavy.LightingBufferCount : low.LightingBufferCount) == 0);
                    back.lighting.enabled = front.lighting.enabled = true;
                    bool Rejected()
                    {
                        bool ok = producer ? heavy.TryRender(source, new FogVolumeDepth(depth, FogDepthEncoding.LinearEye), camera, joint, 2.3, out _)
                            : low.TryRender(source, new FogVolumeDepth(depth, FogDepthEncoding.LinearEye), camera, geometry, out _);
                        return !ok && (producer ? heavy.LightingBufferCount : low.LightingBufferCount) == 0 && (producer ? heavy.UnavailableReason : low.UnavailableReason) != null;
                    }
                    back.blend = FxBlend.Distortion; Check(prefix + "-reject-lit-distortion", Rejected()); back.blend = FxBlend.Alpha;
                    settings.enabled = false; Check(prefix + "-reject-missing-shared-light-settings", Rejected()); settings.enabled = true;
                    back.lighting.inputs.alpha = float.NaN; Check(prefix + "-reject-nan-material", Rejected()); back.lighting.inputs.alpha = .63f;
                    var block = new MaterialPropertyBlock(); block.SetFloat("_Unrelated", 1); back.renderer.SetPropertyBlock(block);
                    Check(prefix + "-reject-property-block", Rejected()); back.renderer.SetPropertyBlock(null);
                    back.lighting.gi.source = SceneGiSource.Lightmap; Check(prefix + "-reject-missing-gi", Rejected()); back.lighting.gi.source = SceneGiSource.None;
                    var borrowedMesh = back.renderer.GetComponent<MeshFilter>().sharedMesh; var meshOnly = Own(Instantiate(borrowedMesh));
                    var borrowedRenderer = back.renderer; back.mesh = meshOnly; back.localToWorld = borrowedRenderer.localToWorldMatrix; back.renderer = null;
                    Check(prefix + "-mesh-matrix-current-equivalent", Difference(original, Render()) <= .0002f, Difference(original, Render()));
                    meshOnly.normals = Array.Empty<Vector3>(); Check(prefix + "-reject-no-normal", Rejected()); meshOnly.normals = borrowedMesh.normals;
                    back.lighting.gi.source = SceneGiSource.RendererLightmap; Check(prefix + "-reject-mesh-renderer-gi-lookup", Rejected()); back.lighting.gi.source = SceneGiSource.None;
                    back.renderer = borrowedRenderer; back.mesh = null; back.localToWorld = Matrix4x4.identity;
                    Render(); var leaseLow = lowFrame; var leaseHeavy = heavyFrame;
                    if (producer) heavy.Dispose(); else low.Dispose();
                    Check(prefix + "-dispose-invalidates-frame", producer ? !leaseHeavy.IsCurrent && heavy.LightingBufferCount == 0 : !leaseLow.IsCurrent && low.LightingBufferCount == 0);
                    Check(prefix + "-recover-after-dispose", Difference(original, Render()) <= .0002f, Difference(original, Render()));
                }
                Resolution(FxResolution.Full); isHeavy = false; var lowImage = Render(); isHeavy = true; var heavyImage = Render();
                Check("producer-full-consistency", Difference(lowImage, heavyImage) <= .0002f, Difference(lowImage, heavyImage));
                SaveSsrPreview("fx-forward-current-full", heavyImage, width, height, false);
                SaveSsrPreview("fx-forward-reference-camera", expected, width, height, false);
                Texture2D Constant(Color value)
                { var t = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); t.SetPixel(0, 0, value); t.Apply(); return t; }
                var many = locals.lights;
                foreach (bool producer in new[] { false, true })
                {
                    isHeavy = producer; string prefix = producer ? "joint" : "standalone";
                    Resolution(FxResolution.Full); var plain = Render();
                    back.lighting.inputs.normalMap = Constant(new Color(.75f, .55f, .93f, 1));
                    Check(prefix + "-current-normal-positive", Difference(plain, Pair(prefix + "-normal")) > .01f);
                    back.lighting.inputs.albedoMap = Constant(new Color(.3f, .8f, .6f, .7f));
                    back.lighting.inputs.mosMap = Constant(new Color(.2f, .4f, .7f, 1));
                    back.lighting.inputs.emissionMap = Constant(new Color(2, .5f, 4, 1));
                    var preGi = Render(); back.lighting.gi.source = SceneGiSource.Lightmap;
                    back.lighting.gi.lightmap = Constant(new Color(3.2f, 1.2f, 2, 1));
                    Check(prefix + "-current-gi-positive", Difference(preGi, Pair(prefix + "-material-gi")) > .02f);
                    back.lighting.inputs.normalMap = back.lighting.inputs.albedoMap = back.lighting.inputs.mosMap = back.lighting.inputs.emissionMap = null;
                    back.lighting.gi.source = SceneGiSource.None;
                    back.texture = Constant(new Color(.7f, .4f, .9f, .6f)); back.opacity = .7f; back.linearRadiance = new Vector3(.3f, 1.7f, .8f);
                    back.vertexColor = true; front.blend = FxBlend.Additive; Pair(prefix + "-fx-multiplier-alpha-additive");
                    back.texture = null; back.opacity = 1; back.linearRadiance = Vector3.one; back.vertexColor = false; front.blend = FxBlend.Alpha;
                    geometry.effectEdgeThreshold = joint.effectEdgeThreshold = 0;
                    foreach (var res in new[] { FxResolution.Half, FxResolution.Quarter })
                    {
                        Resolution(res); var repaired = Pair(prefix + "-forced-repair-" + res); var error = Difference(plain, repaired);
                        Check(prefix + "-forced-repair-" + res + "-full-image", error <= .0002f, error);
                        if (producer)
                        {
                            var mask = ReadSceneTarget(heavyFrame.repairMask); int count = mask.Count(c => c.r > .5f);
                            Check(prefix + "-forced-repair-" + res + "-real-mask", count > 0 && count < mask.Length);
                        }
                    }
                    geometry.effectEdgeThreshold = .125f; joint.effectEdgeThreshold = .001f;
                }
                Resolution(FxResolution.Full);
                // A real producer refreshes the light atlas; stale leases fail closed in both consumers.
                var monitorHost = Own(new GameObject("Lit FX current monitor")); var monitorCamera = monitorHost.AddComponent<Camera>();
                monitorCamera.CopyFrom(camera); monitorCamera.enabled = false; monitorCamera.targetTexture = null; monitorCamera.cullingMask = 1 << 22;
                monitorCamera.transform.position = new Vector3(0, 0, -3); monitorCamera.orthographicSize = 1;
                var panel = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); panel.layer = 22; panel.transform.localScale = new Vector3(5, 3, 1);
                var panelMaterial = Own(new Material(Resources.Load<Shader>("MonitorEmission"))); panelMaterial.SetTexture("_MonitorTex", Texture2D.whiteTexture);
                panel.GetComponent<Renderer>().sharedMaterial = panelMaterial;
                var monitor = monitorHost.AddComponent<HdrMonitor>(); monitor.width = 65; monitor.height = 33; monitor.monitorEnabled = true;
                foreach (bool producer in new[] { false, true })
                {
                    isHeavy = producer; string prefix = producer ? "joint" : "standalone"; monitor.enabled = true;
                    panelMaterial.SetVector("_MonitorTint", new Vector3(4, .5f, .1f)); Check(prefix + "-monitor-publish-red", monitor.TryUpdate(0, 0, out _)); locals.monitor = monitor;
                    var red = Pair(prefix + "-monitor-red"); panelMaterial.SetVector("_MonitorTint", new Vector3(.1f, .5f, 4));
                    Check(prefix + "-monitor-publish-blue", monitor.TryUpdate(0, 1, out _));
                    Check(prefix + "-current-atlas-positive", Difference(red, Pair(prefix + "-monitor-blue")) > .05f);
                    monitor.enabled = false;
                    bool ok = producer ? heavy.TryRender(source, new FogVolumeDepth(depth), camera, joint, 2.3, out _)
                        : low.TryRender(source, new FogVolumeDepth(depth), camera, geometry, out _);
                    Check(prefix + "-stale-monitor-rejected", !ok && (producer ? heavy.LightingBufferCount : low.LightingBufferCount) == 0); locals.monitor = null;
                }
                monitorHost.SetActive(false); panel.SetActive(false);
                // Native deformation is compared to independently edited vertices and object transform.
                var frontRenderer = front.renderer; var referenceMesh = frontRenderer.GetComponent<MeshFilter>().sharedMesh;
                var rest = referenceMesh.vertices; var delta = new Vector3[rest.Length]; delta[0] = new Vector3(.23f, .17f, 0);
                var skinHost = Own(new GameObject("Lit FX native receiver")); skinHost.layer = 25; var skin = skinHost.AddComponent<SkinnedMeshRenderer>();
                skin.sharedMaterial = frontRenderer.sharedMaterial; var skinMesh = Own(Instantiate(referenceMesh));
                skinMesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, rest.Length).ToArray(); skinMesh.bindposes = new[] { Matrix4x4.identity };
                skinMesh.AddBlendShapeFrame("current", 100, delta, new Vector3[rest.Length], new Vector3[rest.Length]);
                var bone = Own(new GameObject("Lit FX native bone")).transform; skin.sharedMesh = skinMesh; skin.bones = new[] { bone }; skin.rootBone = bone;
                skin.updateWhenOffscreen = true; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 20);
                var frontPosition = frontRenderer.transform.position; var frontScale = frontRenderer.transform.localScale;
                var firstPose = new Color[2][];
                for (int pose = 0; pose < 3; pose++)
                {
                    bone.SetPositionAndRotation(new Vector3(-.2f + pose * .23f, .1f - pose * .13f, -.63f), Quaternion.Euler(pose * 8, pose * 11, pose * 17));
                    skin.SetBlendShapeWeight(0, pose * 40); frontRenderer.transform.SetPositionAndRotation(bone.position, bone.rotation); frontRenderer.transform.localScale = Vector3.one;
                    referenceMesh.vertices = rest.Select((p, i) => p + delta[i] * pose * .4f).ToArray(); referenceMesh.RecalculateBounds();
                    yield return null; yield return null;
                    foreach (bool producer in new[] { false, true }) foreach (var res in new[] { FxResolution.Full, FxResolution.Half, FxResolution.Quarter })
                    {
                        isHeavy = producer; Resolution(res); string name = (producer ? "joint" : "standalone") + "-native-" + pose + "-" + res;
                        front.renderer = skin; var native = Pair(name); front.renderer = frontRenderer; var independent = Render(); float error = Difference(native, independent);
                        Check(name + "-independent-deformation-whole-rgba", error <= .00002f, error);
                        if (res == FxResolution.Full)
                        {
                            if (pose == 0) firstPose[producer ? 1 : 0] = native;
                            else Check(name + "-positive-pose", Difference(firstPose[producer ? 1 : 0], native) > .02f);
                        }
                    }
                }
                referenceMesh.vertices = rest; referenceMesh.RecalculateBounds(); frontRenderer.transform.SetPositionAndRotation(frontPosition, Quaternion.identity); frontRenderer.transform.localScale = frontScale;
                skinHost.SetActive(false); Resolution(FxResolution.Full);
                // Grow/shrink resources and retain the final light in a fully overlapping 4096-light bitset.
                var savedSource = source; var savedDepth = depth;
                source = Target(33, 25, RenderTextureFormat.ARGBFloat); depth = Target(33, 25, RenderTextureFormat.RFloat); camera.aspect = 33f / 25;
                Upload(source, Enumerable.Repeat(camera.backgroundColor, 33 * 25).ToArray()); Upload(depth, Enumerable.Repeat(new Color(24, 0, 0, 0), 33 * 25).ToArray());
                locals.lights = Enumerable.Range(0, 4096).Select(i => new SceneDecalLight { position = new Vector3(0, 0, -1.5f), range = 15,
                    radiance = new Vector3(.0001f, .0002f, .0003f), specularScale = 0 }).ToArray();
                settings.tileSize = 8;
                foreach (bool producer in new[] { false, true }) foreach (var res in new[] { FxResolution.Full, FxResolution.Half, FxResolution.Quarter })
                {
                    isHeavy = producer; Resolution(res); string name = (producer ? "joint" : "standalone") + "-4096-" + res;
                    locals.lights[4095].radiance = new Vector3(.0001f, .0002f, .0003f); var overlap = Pair(name);
                    Check(name + "-no-dropped-light", (producer ? heavy.LightingSubmittedLights : low.LightingSubmittedLights) == 4096);
                    locals.lights[4095].radiance = Vector3.right; Check(name + "-tail-visible", Difference(overlap, Pair(name + "-tail")) > .03f);
                }
                var overlapLights = locals.lights;
                foreach (bool producer in new[] { false, true })
                {
                    isHeavy = producer; string name = producer ? "joint" : "standalone";
                    locals.lights = new[] { overlapLights[0] }; Pair(name + "-shrink-one");
                    locals.lights = Array.Empty<SceneDecalLight>(); Pair(name + "-zero");
                    Check(name + "-zero-no-grid", (producer ? heavy.LightingTileCount : low.LightingTileCount) == 0);
                }
                source = Target(385, 387, RenderTextureFormat.ARGBFloat); depth = Target(385, 387, RenderTextureFormat.RFloat); camera.aspect = 385f / 387;
                Upload(source, Enumerable.Repeat(camera.backgroundColor, 385 * 387).ToArray()); Upload(depth, Enumerable.Repeat(new Color(24, 0, 0, 0), 385 * 387).ToArray());
                var backScale = back.renderer.transform.localScale; back.renderer.transform.localScale = frontRenderer.transform.localScale = Vector3.one * .01f;
                locals.lights = overlapLights; settings.maximumGridMiB = 1;
                foreach (bool producer in new[] { false, true })
                {
                    isHeavy = producer; string name = producer ? "joint" : "standalone"; settings.allowBruteForceFallback = false; settings.backend = SceneForwardLightBackend.Tiled;
                    bool ok = producer ? heavy.TryRender(source, new FogVolumeDepth(depth), camera, joint, 2.3, out _)
                        : low.TryRender(source, new FogVolumeDepth(depth), camera, geometry, out _);
                    Check(name + "-grid-budget-fail-closed", !ok && (producer ? heavy.LightingBufferCount : low.LightingBufferCount) == 0);
                    settings.allowBruteForceFallback = true; Render();
                    Check(name + "-explicit-brute-budget-fallback", (producer ? heavy.LightingBackend : low.LightingBackend) == SceneForwardLightBackend.BruteForce &&
                        (producer ? heavy.LightingFallbackReason : low.LightingFallbackReason) != null);
                }
                settings.maximumGridMiB = 32; settings.allowBruteForceFallback = false; settings.tileSize = 16; locals.lights = many;
                source = savedSource; depth = savedDepth; camera.aspect = (float)width / height;
                back.renderer.transform.localScale = backScale; frontRenderer.transform.localScale = frontScale; Resolution(FxResolution.Full);
                isHeavy = false; var isolatedLow = Pair("standalone-restored-size"); isHeavy = true; var isolatedHeavy = Pair("joint-restored-size");
                isHeavy = false; Check("separate-consumer-state-low", Difference(isolatedLow, Render()) == 0);
                isHeavy = true; Check("separate-consumer-state-joint", Difference(isolatedHeavy, Render()) == 0);
                var blocker = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); blocker.layer = 24;
                blocker.transform.position = new Vector3(-.4f, .3f, -1); blocker.transform.localScale = new Vector3(.61f, 1.13f, 1);
                blocker.GetComponent<Renderer>().sharedMaterial = Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback")));
                var caster = new SceneShadowCaster { renderer = blocker.GetComponent<Renderer>(), cull = CullMode.Off };
                locals.lights = new[] { new SceneDecalLight { shape = SceneDecalLightShape.Point, position = new Vector3(0, 0, -2), range = 6, radiance = Vector3.one * 2 } };
                locals.shadows.casters = new[] { caster }; locals.shadows.tileResolution = 128;
                settings.mainLightShadow.origin = new Vector3(0, 0, -3); settings.mainLightShadow.halfSize = Vector2.one * 3;
                settings.mainLightShadow.resolution = 128; settings.mainLightShadow.farPlane = 12; settings.mainLightShadow.casters = new[] { caster };
                settings.lightRadiance = Vector3.one * 2;
                foreach (bool producer in new[] { false, true })
                {
                    isHeavy = producer; string name = producer ? "joint" : "standalone";
                    locals.lights[0].shadow.enabled = false; settings.mainLightShadow.enabled = false; var clear = Render();
                    locals.lights[0].shadow.enabled = true; var localShadow = Pair(name + "-point-shadow");
                    Check(name + "-point-shadow-positive", Difference(clear, localShadow) > .02f && (producer ? heavy.LightingShadowMapCount : low.LightingShadowMapCount) == 6);
                    settings.mainLightShadow.enabled = true; var both = Pair(name + "-main-local-shadow");
                    Check(name + "-main-shadow-positive", Difference(localShadow, both) > .01f && (producer ? heavy.LightingShadowMapCount : low.LightingShadowMapCount) == 7);
                }
                // Two shadowed medium lights use a different atlas from both surface atlases.
                isHeavy = true; back.fog = front.fog = true;
                var medium1 = new VolumetricSpotLight { position = new Vector3(-1, 1, -3), rotation = Quaternion.LookRotation(new Vector3(.2f, -.1f, 1)),
                    range = 9, innerAngle = 28, outerAngle = 64, linearRadiance = new Vector3(6, 3, 2) };
                var medium2 = new VolumetricSpotLight { position = new Vector3(1, -.8f, -2.5f), rotation = Quaternion.LookRotation(new Vector3(-.15f, .2f, 1)),
                    range = 8, innerAngle = 25, outerAngle = 60, linearRadiance = new Vector3(1, 3, 5) };
                medium1.shadow.enabled = medium2.shadow.enabled = true;
                joint.medium = new VolumetricLightingSettings { enabled = true, mediumCenter = Vector3.zero, mediumHalfSize = new Vector3(4, 3, 4),
                    extinction = .13f, samplesPerLight = 64, lights = new[] { medium1, medium2 } };
                joint.medium.shadows.tileResolution = 128; joint.medium.shadows.casters = new[] { caster };
                foreach (var res in new[] { FxResolution.Full, FxResolution.Half, FxResolution.Quarter })
                {
                    Resolution(res); joint.medium.resolution = (VolumetricResolution)res;
                    var dual = Pair("joint-dual-shadow-" + res);
                    Check("joint-dual-shadow-" + res + "-distinct-depth-producers", heavy.LightingShadowMapCount == 7 && heavyFrame.shadowAtlas != null && heavyFrame.shadowAtlas.width == 256 && heavy.ShadowCasterDrawCalls == 2);
                    medium1.shadow.enabled = medium2.shadow.enabled = false;
                    Check("joint-dual-shadow-" + res + "-medium-shadow-positive", Difference(dual, Render()) > .001f);
                    medium1.shadow.enabled = medium2.shadow.enabled = true;
                    settings.mainLightShadow.enabled = false; locals.lights[0].shadow.enabled = false;
                    Check("joint-dual-shadow-" + res + "-surface-shadow-positive", Difference(dual, Render()) > .01f);
                    settings.mainLightShadow.enabled = true; locals.lights[0].shadow.enabled = true;
                }
                // Emission-only material is numerically equivalent to the pre-existing unlit producer.
                // This isolates material-alpha transport, including the SECOND medium-scatter draw.
                settings.mainLightShadow.enabled = false; locals.lights = Array.Empty<SceneDecalLight>(); settings.lightRadiance = settings.ambientIrradiance = Vector3.zero;
                var backInput = back.lighting.inputs; var frontInput = front.lighting.inputs;
                back.lighting.inputs = new SceneDeferredCamera.MaterialInputs { albedo = Vector3.zero, emission = new Vector3(1.2f, .3f, .2f), alpha = .37f };
                front.lighting.inputs = new SceneDeferredCamera.MaterialInputs { albedo = Vector3.zero, emission = new Vector3(.1f, .4f, 1.6f), alpha = .61f };
                back.opacity = .7f; front.opacity = .8f;
                var barrier = new LowResolutionFxSurface { renderer = frontRenderer, blend = FxBlend.Distortion, opacity = .35f,
                    distortionOffset = new Vector2(.061f, -.043f), fog = false };
                var additive = new LowResolutionFxSurface { renderer = back.renderer, blend = FxBlend.Additive, opacity = .2f, linearRadiance = new Vector3(.2f, 1.3f, .3f) };
                geometry.surfaces = new[] { back, additive, barrier, front };
                joint.optics.enabled = true; joint.optics.emitters = new[] { new LensFlareEmitter { position = new Vector3(.5f, .2f, -.3f),
                    linearRadiance = new Vector3(.2f, .3f, .1f), elements = new[] { new LensFlareElement { halfSize = new Vector2(.31f, .17f), softness = .9f } } } };
                using (var oldProducer = new HeavyFxRenderer())
                foreach (var res in new[] { FxResolution.Full, FxResolution.Half, FxResolution.Quarter })
                foreach (bool shadowed in new[] { false, true })
                {
                    Resolution(res); joint.medium.resolution = (VolumetricResolution)res; joint.optics.resolution = (LensFlareResolution)res;
                    medium1.shadow.enabled = medium2.shadow.enabled = shadowed;
                    string name = "joint-medium-material-alpha-" + res + "-shadow" + shadowed;
                    var lit = Pair(name);
                    Check("joint-mixed-" + res + "-shadow" + shadowed + "-ordered-shared-batches", heavy.BatchCount == 3 && heavyFrame.TryGetBatchInfo(0, out var info) && info.medium && info.surfaceCount == 2 &&
                        heavyFrame.TryGetBatchInfo(1, out info) && info.distortion && heavyFrame.TryGetBatchInfo(2, out info) && info.optics && info.surfaceCount == 1);
                    back.lighting.enabled = front.lighting.enabled = false;
                    back.linearRadiance = back.lighting.inputs.emission; front.linearRadiance = front.lighting.inputs.emission;
                    back.opacity *= back.lighting.inputs.alpha; front.opacity *= front.lighting.inputs.alpha;
                    if (!oldProducer.TryRender(source, new FogVolumeDepth(depth), camera, joint, 2.3, out var oldFrame)) throw new InvalidOperationException(oldProducer.UnavailableReason);
                    var unlit = ReadSceneTarget(oldFrame.color); float error = Difference(lit, unlit);
                    Check(name + "-independent-unlit-whole-rgba", error <= .0002f, error);
                    if (error > .0002f)
                    {
                        int worst = 0; float value = 0;
                        for (int i = 0; i < lit.Length; i++) for (int c = 0; c < 4; c++) if (Mathf.Abs(lit[i][c] - unlit[i][c]) > value) { worst = i; value = Mathf.Abs(lit[i][c] - unlit[i][c]); }
                        Debug.Log("[FxForwardDiagnostic] " + name + " worst=" + worst % width + "," + worst / width + " lit=" + lit[worst].ToString("R") + " unlit=" + unlit[worst].ToString("R"));
                        SaveSsrPreview("fx-forward-" + name + "-lit", lit, width, height, false); SaveSsrPreview("fx-forward-" + name + "-unlit", unlit, width, height, false);
                    }
                    back.lighting.enabled = front.lighting.enabled = true; back.linearRadiance = front.linearRadiance = Vector3.one; back.opacity = .7f; front.opacity = .8f;
                }
                // Actual camera bridge, current device depth, explicit source captured before post-processing.
                Resolution(FxResolution.Half); joint.medium.resolution = VolumetricResolution.Half; joint.optics.resolution = LensFlareResolution.Half;
                camera.cullingMask = 1 << 24; camera.depthTextureMode = DepthTextureMode.Depth;
                var actualDepth = Target(width, height, RenderTextureFormat.RFloat); Color[] inputPixels = null;
                var post = host.AddComponent<OriginalStyleRenderPipeline>(); post.heavyFx = joint; post.heavyFxTimeSeconds = 2.3;
                post.heavyFxDepthProvider = (view, input) => { inputPixels = ReadSceneTarget(input); Graphics.Blit(Shader.GetGlobalTexture("_CameraDepthTexture"), actualDepth); return new FogVolumeDepth(actualDepth, FogDepthEncoding.Device); };
                camera.Render();
                if (!post.TryGetHeavyFxFrame(out var bridge)) throw new InvalidOperationException(post.HeavyFxUnavailableReason);
                var bridgePixels = ReadSceneTarget(bridge.color); Check("actual-camera-bridge-current", bridge.IsCurrent && inputPixels != null);
                Upload(source, inputPixels); depth = actualDepth; depthEncoding = FogDepthEncoding.Device;
                float bridgeError = Difference(bridgePixels, Render());
                Check("actual-camera-bridge-direct-consumer-whole-rgba", bridgeError <= .0002f, bridgeError);
                SaveSsrPreview("fx-forward-current-mixed-camera", bridgePixels, width, height, false);
                post.enabled = false; camera.cullingMask = 0; depth = savedDepth; depthEncoding = FogDepthEncoding.LinearEye;
                geometry.surfaces = new[] { back, front }; back.lighting.inputs = backInput; front.lighting.inputs = frontInput;
                back.opacity = front.opacity = 1; joint.medium.enabled = joint.optics.enabled = false;
                // Fog transport is also checked against the unchanged unlit emitter shader.
                geometry.fog.enabled = geometry.fog.distance.enabled = true; geometry.fog.distance.density = .13f;
                geometry.fog.distance.linearColor = new Color(.31f, .47f, .19f, 1);
                back.lighting.inputs = new SceneDeferredCamera.MaterialInputs { albedo = Vector3.zero, emission = new Vector3(1.2f, .3f, .2f), alpha = .37f };
                front.lighting.inputs = new SceneDeferredCamera.MaterialInputs { albedo = Vector3.zero, emission = new Vector3(.1f, .4f, 1.6f), alpha = .61f };
                foreach (bool producer in new[] { false, true }) foreach (var res in new[] { FxResolution.Full, FxResolution.Half, FxResolution.Quarter })
                {
                    isHeavy = producer; Resolution(res); string name = (producer ? "joint" : "standalone") + "-fog-alpha-" + res;
                    front.blend = FxBlend.Additive; var lit = Pair(name);
                    back.lighting.enabled = front.lighting.enabled = false;
                    back.linearRadiance = back.lighting.inputs.emission; front.linearRadiance = front.lighting.inputs.emission;
                    back.opacity = back.lighting.inputs.alpha; front.opacity = front.lighting.inputs.alpha;
                    float error = Difference(lit, Render()); Check(name + "-independent-unlit-whole-rgba", error <= .0002f, error);
                    back.lighting.enabled = front.lighting.enabled = true; back.linearRadiance = front.linearRadiance = Vector3.one; back.opacity = front.opacity = 1;
                }
                geometry.fog.enabled = false; front.blend = FxBlend.Alpha; back.lighting.inputs = backInput; front.lighting.inputs = frontInput;
                Resolution(FxResolution.Full); locals.lights = many;
                var serialized = JsonUtility.FromJson<SceneForwardLightingSettings>(JsonUtility.ToJson(reference));
                Check("inherited-camera-settings-serialization", serialized.localLights.lights.Length == reference.localLights.lights.Length && serialized.lightRadiance == reference.lightRadiance &&
                    serialized.tileSize == reference.tileSize && serialized.surfaces.Length == 2);
                foreach (bool producer in new[] { false, true })
                {
                    isHeavy = producer; string name = producer ? "joint" : "standalone";
                    bool Rejected()
                    {
                        bool ok = producer ? heavy.TryRender(source, new FogVolumeDepth(depth), camera, joint, 2.3, out _)
                            : low.TryRender(source, new FogVolumeDepth(depth), camera, geometry, out _);
                        return !ok && (producer ? heavy.LightingBufferCount : low.LightingBufferCount) == 0 && (producer ? heavy.UnavailableReason : low.UnavailableReason) != null;
                    }
                    var mesh = Own(Instantiate(back.renderer.GetComponent<MeshFilter>().sharedMesh)); var renderer = back.renderer;
                    back.mesh = mesh; back.renderer = null; back.localToWorld = renderer.localToWorldMatrix;
                    var tangents = mesh.tangents; mesh.tangents = Array.Empty<Vector4>(); back.lighting.inputs.normalMap = Constant(new Color(.5f, .5f, 1, 1));
                    Check(name + "-reject-normal-map-no-tangent", Rejected()); mesh.tangents = tangents; back.lighting.inputs.normalMap = null;
                    var uv = mesh.uv; mesh.uv = Array.Empty<Vector2>(); back.texture = Constant(Color.white);
                    Check(name + "-reject-texture-no-uv", Rejected()); mesh.uv = uv; back.texture = null;
                    var colors = mesh.colors; mesh.colors = Array.Empty<Color>(); back.vertexColor = true;
                    Check(name + "-reject-missing-vertex-color", Rejected()); mesh.colors = colors; back.vertexColor = false;
                    back.localToWorld = Matrix4x4.Scale(new Vector3(0, 1, 1)); Check(name + "-reject-singular-current-transform", Rejected());
                    back.renderer = renderer; back.mesh = null; back.localToWorld = Matrix4x4.identity;
                    var dead = Target(3, 3, RenderTextureFormat.ARGBFloat); dead.Release(); back.lighting.inputs.albedoMap = dead;
                    Check(name + "-reject-released-material-texture", Rejected()); back.lighting.inputs.albedoMap = null;
                    locals.lights = new SceneDecalLight[4097]; Check(name + "-reject-light-capacity", Rejected()); locals.lights = many;
                    camera.rect = new Rect(0, 0, .5f, 1); Check(name + "-reject-partial-viewport", Rejected()); camera.rect = new Rect(0, 0, 1, 1);
                    geometry.lighting = null; Check(name + "-reject-null-light-settings", Rejected());
                    back.lighting.enabled = front.lighting.enabled = false; Render();
                    Check(name + "-unlit-accepts-no-light-settings", (producer ? heavy.LightingBufferCount : low.LightingBufferCount) == 0);
                    geometry.lighting = settings; back.lighting.enabled = front.lighting.enabled = true; Pair(name + "-recovered-adversarial-inputs");
                }
            }
            finally
            {
                low.Dispose(); heavy.Dispose(); RenderTexture.active = active;
                foreach (var item in _owned) { if (item is RenderTexture rt) rt.Release(); if (item != null) Destroy(item); }
                _owned.Clear(); for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i];
            }
            yield return null;
        }
    }
}
