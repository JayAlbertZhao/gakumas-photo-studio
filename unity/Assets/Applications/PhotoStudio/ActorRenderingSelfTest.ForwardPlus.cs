using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyForwardPlus(Report report)
        {
            yield return null;
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "forward-plus-" + name, ok, error);
            var previous = FindObjectsOfType<Renderer>(); var forced = previous.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in previous) r.forceRenderingOff = true;
            var active = RenderTexture.active;
            try
            {
                var host = Own(new GameObject("Forward+ full-resolution acceptance")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.renderingPath = RenderingPath.Forward; camera.allowHDR = true; camera.allowMSAA = false;
                camera.transform.position = new Vector3(0, 0, -4); camera.orthographic = true; camera.orthographicSize = 1.6f;
                camera.nearClipPlane = .1f; camera.farClipPlane = 25; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.13f, .21f, .33f, .61f); camera.cullingMask = 1 << 24;
                RenderTexture Target(int width, int height)
                {
                    var t = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
                    t.Create(); camera.targetTexture = t; camera.aspect = (float)width / height; return t;
                }
                var target = Target(143, 103);
                var stage = host.AddComponent<SceneForwardLightingCamera>(); stage.surfaceLayers = 1 << 25;
                var options = stage.settings; options.enabled = true; options.allowBruteForceFallback = false;
                options.lightRadiance = new Vector3(.23f, .17f, .11f); options.ambientIrradiance = new Vector3(.07f, .13f, .09f);
                var local = options.localLights; local.enabled = true;
                SceneForwardSurface Surface(string name, Vector3 position, Vector3 scale)
                {
                    var go = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.name = name; go.layer = 25; go.transform.position = position; go.transform.localScale = scale;
                    var mesh = Own(Instantiate(go.GetComponent<MeshFilter>().sharedMesh)); mesh.uv2 = mesh.uv; go.GetComponent<MeshFilter>().sharedMesh = mesh;
                    return new SceneForwardSurface { renderer = go.GetComponent<Renderer>(), cull = CullMode.Off,
                        inputs = new SceneDeferredCamera.MaterialInputs { albedo = new Vector3(.37f, .53f, .29f), mos = new Vector3(.17f, .73f, .36f), alpha = .63f, emission = new Vector3(.031f, .052f, .017f) } };
                }
                var back = Surface("Back transparent receiver", Vector3.zero, new Vector3(3.73f, 2.81f, 1));
                var front = Surface("Front transparent receiver", new Vector3(.27f, -.13f, -.63f), new Vector3(2.13f, 1.71f, 1));
                front.inputs.albedo = new Vector3(.61f, .24f, .13f); front.inputs.alpha = .37f;
                options.surfaces = new[] { back, front };
                Color[] Render(bool valid = true)
                {
                    camera.Render(); if (valid && stage.UnavailableReason != null) throw new InvalidOperationException(stage.UnavailableReason);
                    return ReadSceneTarget(camera.targetTexture);
                }
                float Difference(Color[] a, Color[] b)
                {
                    if (a.Length != b.Length) return float.PositiveInfinity;
                    float e = 0; for (int i = 0; i < a.Length; i++) for (int c = 0; c < 4; c++) e = Mathf.Max(e, Mathf.Abs(a[i][c] - b[i][c])); return e;
                }
                Color[] Pair(string name)
                {
                    options.backend = SceneForwardLightBackend.BruteForce; var brute = Render();
                    Check(name + "-brute-no-tile-dispatch", stage.TileCount == 0 && stage.GridBytes == 4);
                    options.backend = SceneForwardLightBackend.Tiled; var tiled = Render(); float error = Difference(brute, tiled);
                    Check(name + "-whole-rgba", error <= .00002f, error); return tiled;
                }
                void Grid(string name)
                {
                    var buffer = stage.TileBuffer; var actual = new uint[buffer.count]; buffer.GetData(actual);
                    int size = options.tileSize, width = camera.targetTexture.width, height = camera.targetTexture.height;
                    int nx = (width + size - 1) / size, ny = (height + size - 1) / size, words = (stage.SubmittedLights + 31) / 32;
                    int mismatches = 0, occupied = 0, full = 0, includedBits = 0;
                    for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++) for (int word = 0; word < words; word++)
                    {
                        uint expected = 0;
                        for (int bit = 0; bit < 32; bit++)
                        {
                            int light = word * 32 + bit; if (light >= stage.SubmittedLights) break;
                            var r = stage.LightSnapshot[light].clipRect;
                            double loX = (r.x + 1.0) * width / 2 - 1, hiX = (r.z + 1.0) * width / 2 + 1;
                            double loY = ((SystemInfo.graphicsUVStartsAtTop ? -r.w : r.y) + 1.0) * height / 2 - 1;
                            double hiY = ((SystemInfo.graphicsUVStartsAtTop ? -r.y : r.w) + 1.0) * height / 2 + 1;
                            if (hiX >= x * size && loX <= Math.Min((x + 1) * size, width) && hiY >= y * size && loY <= Math.Min((y + 1) * size, height)) expected |= 1u << bit;
                        }
                        uint value = actual[(y * nx + x) * words + word];
                        if (value != expected) mismatches++; if (value != 0) occupied++; if (value == uint.MaxValue) full++;
                        for (uint bits = value; bits != 0; bits &= bits - 1) includedBits++;
                    }
                    Check(name + "-gpu-grid-every-word", mismatches == 0, mismatches);
                    Check(name + "-gpu-grid-positive", occupied > 0); Check(name + "-exact-grid-capacity", buffer.count == nx * ny * words);
                    if (stage.SubmittedLights > 64 && local.lights.All(l => l.range > 10)) Check(name + "-full-word-overlap", full > 0);
                    else if (local.lights.Length == 97) Check(name + "-real-per-tile-culling", includedBits < nx * ny * stage.SubmittedLights);
                }
                options.enabled = false; var disabled = Render(false);
                Check("disabled-no-resources", stage.AllocatedBuffers == 0 && stage.SubmittedSurfaces == 0); options.enabled = true;
                local.lights = Enumerable.Range(0, 97).Select(i => new SceneDecalLight {
                    shape = (SceneDecalLightShape)(i % 4), position = new Vector3((i % 11 - 5) * .91f, (i / 11 - 4) * .81f, -1.2f - (i % 3) * .31f),
                    rotation = Quaternion.Euler((i % 3 - 1) * 11, (i % 5 - 2) * 7, i * 13), range = 1.67f + (i % 5) * .13f,
                    halfLength = .37f, halfSize = new Vector2(.31f, .43f), areaSpread = new Vector2(.33f, .21f),
                    spotInnerAngle = 59, spotOuterAngle = 113, radiance = new Vector3(.31f, .17f + i % 3 * .1f, .23f),
                    falloffExponent = 1 + i % 3, diffuseScale = .9f, specularScale = .7f, backlightScale = .2f,
                    receiverGroup = i % 7 == 0 ? 2 : 0 }).ToArray();
                foreach (bool perspective in new[] { false, true }) foreach (int size in new[] { 8, 16, 32 })
                {
                    camera.orthographic = !perspective; camera.fieldOfView = 53; options.tileSize = size;
                    string name = (perspective ? "perspective" : "ortho") + "-" + size;
                    var pixels = Pair(name); Grid(name); Check(name + "-positive-lit-surface", Difference(disabled, pixels) > .1f);
                    Check(name + "-two-depths-submitted", stage.SubmittedSurfaces == 2 && stage.SubmittedLights > 10);
                }
                camera.orthographic = true; options.tileSize = 16;
                for (int move = 0; move < 3; move++)
                {
                    camera.transform.SetPositionAndRotation(new Vector3(move * .13f, move * -.07f, -4), Quaternion.Euler(move * 2, move * -3, move * 4));
                    local.lights[10].position += Vector3.right * .6f;
                    var projection = camera.projectionMatrix; projection.m02 += .09f; projection.m12 -= .07f; camera.projectionMatrix = projection;
                    Pair("moving-asymmetric-" + move); Grid("moving-asymmetric-" + move);
                }
                camera.transform.SetPositionAndRotation(new Vector3(0, 0, -4), Quaternion.identity); camera.ResetProjectionMatrix();
                // Independent world-space formula uses all input lights, not the uploaded/camera-culled snapshot.
                options.enabled = false; var basis = Render(false); options.enabled = true;
                var oracleActual = Pair("independent-input-oracle"); var expectedImage = (Color[])basis.Clone();
                for (int y = 0; y < target.height; y++) for (int x = 0; x < target.width; x++)
                {
                    var ray = camera.ViewportPointToRay(new Vector3((x + .5f) / target.width, (y + .5f) / target.height));
                    foreach (var surface in options.surfaces)
                    {
                        var transform = surface.renderer.transform; var plane = new Plane(Vector3.forward, transform.position);
                        if (!plane.Raycast(ray, out var distance)) continue;
                        var world = ray.GetPoint(distance); var p = transform.InverseTransformPoint(world);
                        if (Mathf.Abs(p.x) > .5f || Mathf.Abs(p.y) > .5f) continue;
                        Vector3 v = Vector3.back, n = Vector3.back; var input = surface.inputs;
                        var rgb = input.emission + Vector3.Scale(input.albedo, options.ambientIrradiance) * ((1 - input.mos.x) / Mathf.PI * input.mos.y);
                        rgb += Vector3.Scale(ForwardCpuBrdf(input.albedo, input.mos, n, v, options.lightDirection.normalized, options.diffuseScale, options.specularScale, options.backlightScale), options.lightRadiance);
                        foreach (var light in local.lights) if (light.receiverGroup == 0 || light.receiverGroup == surface.receiverGroup)
                            rgb += ForwardCpuLocal(light, input.albedo, input.mos, world, n, v);
                        var dest = expectedImage[y * target.width + x];
                        expectedImage[y * target.width + x] = new Color(rgb.x * input.alpha, rgb.y * input.alpha, rgb.z * input.alpha, input.alpha) + dest * (1 - input.alpha);
                    }
                }
                float independentError = Difference(expectedImage, oracleActual);
                Check("independent-pbr-shapes-alpha-full-image", independentError <= .0002f, independentError);
                SaveSsrPreview("forward-plus-current-tiled", oracleActual, target.width, target.height, false);
                SaveSsrPreview("forward-plus-independent-cpu", expectedImage, target.width, target.height, false);
                // Each local shape must independently contribute visible radiance.
                var many = local.lights;
                foreach (SceneDecalLightShape shape in Enum.GetValues(typeof(SceneDecalLightShape)))
                {
                    var light = new SceneDecalLight { shape = shape, position = new Vector3(.2f, .1f, -1.5f), range = 4, radiance = new Vector3(2, 1, .5f), areaSpread = Vector2.one * .5f, spotOuterAngle = 130, spotInnerAngle = 80 };
                    local.lights = Array.Empty<SceneDecalLight>(); var unlit = Render(); local.lights = new[] { light };
                    Check(shape + "-visible-contribution", Difference(unlit, Pair(shape.ToString())) > .02f);
                }
                local.lights = many;
                var plain = Pair("plain-material");
                Texture2D Constant(Color value) { var t = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); t.SetPixel(0, 0, value); t.Apply(); return t; }
                back.inputs.normalMap = Constant(new Color(.75f, .55f, .93f, 1));
                Check("current-normal-map-positive", Difference(plain, Pair("normal-map")) > .01f);
                back.inputs.albedoMap = Constant(new Color(.3f, .8f, .6f, .7f)); back.inputs.mosMap = Constant(new Color(.2f, .4f, .7f));
                back.inputs.emissionMap = Constant(new Color(2, .5f, 4)); Pair("all-material-channels");
                var preGi = Render(); back.gi.source = SceneGiSource.Lightmap; back.gi.lightmap = Constant(new Color(3.2f, 1.2f, 2));
                options.directionalGiWeight = .7f; foreach (var l in local.lights) l.giWeight = .6f;
                float giDifference = Difference(preGi, Pair("surface-gi"));
                Check("current-surface-gi-positive", giDifference > .02f, giDifference);
                local.atlas = Constant(new Color(3, 1, .2f)); Check("hdr-atlas-positive", Difference(preGi, Pair("hdr-atlas")) > .02f);
                front.additive = true; Pair("mixed-alpha-additive"); front.additive = false;
                back.inputs.normalMap = back.inputs.albedoMap = back.inputs.mosMap = back.inputs.emissionMap = null;
                back.gi.source = SceneGiSource.None; local.atlas = null; options.directionalGiWeight = 0;
                foreach (var l in local.lights) l.giWeight = 0;
                var monitorHost = Own(new GameObject("Forward+ monitor camera")); var monitorCamera = monitorHost.AddComponent<Camera>();
                monitorCamera.CopyFrom(camera); monitorCamera.targetTexture = null; monitorCamera.enabled = false; monitorCamera.cullingMask = 1 << 22;
                monitorCamera.transform.position = new Vector3(0, 0, -3); monitorCamera.orthographicSize = 1;
                var panel = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); panel.layer = 22; panel.transform.localScale = new Vector3(5, 3, 1);
                var panelMaterial = Own(new Material(Resources.Load<Shader>("MonitorEmission"))); panelMaterial.SetTexture("_MonitorTex", Texture2D.whiteTexture);
                panelMaterial.SetVector("_MonitorTint", new Vector3(4, .5f, .1f)); panel.GetComponent<Renderer>().sharedMaterial = panelMaterial;
                var monitor = monitorHost.AddComponent<HdrMonitor>(); monitor.width = 65; monitor.height = 33; monitor.monitorEnabled = true;
                Check("actual-monitor-produced", monitor.TryUpdate(0, 0, out _)); local.monitor = monitor;
                var redMonitor = Pair("current-monitor-red"); panelMaterial.SetVector("_MonitorTint", new Vector3(.1f, .5f, 4));
                Check("actual-monitor-republished", monitor.TryUpdate(0, 1, out _));
                Check("current-monitor-changes-illumination", Difference(redMonitor, Pair("current-monitor-blue")) > .05f);
                monitor.enabled = false; Render(false); Check("stale-monitor-fails-closed", stage.UnavailableReason != null && stage.AllocatedBuffers == 0);
                local.monitor = null; monitorHost.SetActive(false); panel.SetActive(false); Pair("recover-monitor");
                // Actual opaque scene depth and host Forward geometry precede this alpha island.
                var occluder = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); occluder.layer = 24;
                occluder.transform.position = new Vector3(-.4f, .3f, -1); occluder.transform.localScale = new Vector3(.61f, 1.13f, 1);
                var opaqueMaterial = Own(new Material(Shader.Find("Hidden/PhotoStudio/FaceDecalProbe"))); opaqueMaterial.SetVector("_ProbeTint", new Vector4(.2f, .7f, .9f, 1));
                occluder.GetComponent<Renderer>().sharedMaterial = opaqueMaterial;
                var withOpaque = Pair("opaque-depth"); occluder.SetActive(false);
                Check("actual-opaque-depth-positive", Difference(withOpaque, Render()) > .05f);
                occluder.layer = 23; occluder.SetActive(true);
                var caster = new SceneShadowCaster { renderer = occluder.GetComponent<Renderer>(), cull = CullMode.Off };
                local.lights = new[] { new SceneDecalLight { shape = SceneDecalLightShape.Spot, position = new Vector3(0, 0, -2), range = 6, spotInnerAngle = 100, spotOuterAngle = 140, radiance = Vector3.one * 2 } };
                local.shadows.casters = new[] { caster }; local.shadows.tileResolution = 128;
                var noShadow = Render(); local.lights[0].shadow.enabled = true;
                Check("spot-shadow-visible", Difference(noShadow, Pair("spot-shadow")) > .02f && stage.LocalShadowMapCount == 1);
                local.lights[0].shape = SceneDecalLightShape.Point; Pair("point-six-face-shadow"); Check("point-six-depth-maps", stage.LocalShadowMapCount == 6);
                options.mainLightShadow.enabled = true; options.mainLightShadow.casters = new[] { caster }; options.mainLightShadow.origin = new Vector3(0, 0, -3);
                options.mainLightShadow.halfSize = Vector2.one * 3; options.mainLightShadow.resolution = 128; options.mainLightShadow.farPlane = 12;
                options.lightRadiance = Vector3.one * 2;
                var bothShadow = Pair("local-and-main-shadow"); Check("independent-shadow-atlases", stage.MainShadowMapCount == 1 && stage.LocalShadowMapCount == 6);
                options.mainLightShadow.enabled = false; Check("directional-shadow-visible", Difference(bothShadow, Render()) > .01f);
                local.lights[0].shadow.enabled = false; occluder.SetActive(false);
                var deferred = host.AddComponent<SceneDeferredCamera>(); deferred.sceneLayers = 1 << 23; deferred.sceneEnabled = true;
                deferred.lightRadiance = deferred.ambientIrradiance = Vector3.zero;
                var sceneObject = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); sceneObject.layer = 23;
                sceneObject.transform.position = new Vector3(.3f, -.2f, -.3f); sceneObject.transform.localScale = new Vector3(1.11f, .93f, 1);
                deferred.surfaces = new[] { new SceneDeferredCamera.Surface { renderer = sceneObject.GetComponent<Renderer>(), cull = CullMode.Off,
                    inputs = new SceneDeferredCamera.MaterialInputs { emission = new Vector3(.17f, .31f, .53f) } } };
                var integrated = Pair("deferred-opaque-forward-transparent");
                Check("scene-deferred-frame-current", deferred.TryGetFrame(out _) && deferred.SubmittedSurfaces == 1);
                deferred.enabled = false; sceneObject.SetActive(false);
                Check("scene-deferred-depth-positive", Difference(integrated, Render()) > .03f);
                // Independent current native skin and blendshape geometry, not a BakeMesh substitute.
                local.lights = many;
                var skinHost = Own(new GameObject("Forward+ native skin receiver")); skinHost.layer = 25;
                var skin = skinHost.AddComponent<SkinnedMeshRenderer>(); skin.sharedMaterial = front.renderer.sharedMaterial;
                var skinMesh = Own(Instantiate(front.renderer.GetComponent<MeshFilter>().sharedMesh));
                var rest = skinMesh.vertices; var delta = new Vector3[rest.Length]; delta[0] = new Vector3(.23f, .17f, 0);
                skinMesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, rest.Length).ToArray();
                skinMesh.bindposes = new[] { Matrix4x4.identity }; skinMesh.AddBlendShapeFrame("current", 100, delta, new Vector3[rest.Length], new Vector3[rest.Length]);
                var bone = Own(new GameObject("Forward+ native skin bone")).transform;
                skin.sharedMesh = skinMesh; skin.bones = new[] { bone }; skin.rootBone = bone; skin.updateWhenOffscreen = true; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 20);
                var referenceMesh = front.renderer.GetComponent<MeshFilter>().sharedMesh; var savedFrontPosition = front.renderer.transform.position; var savedFrontScale = front.renderer.transform.localScale;
                var frontRenderer = front.renderer; Color[] firstSkin = null;
                for (int pose = 0; pose < 3; pose++)
                {
                    bone.SetPositionAndRotation(new Vector3(-.2f + pose * .23f, .1f - pose * .13f, -.63f), Quaternion.Euler(pose * 8, pose * 11, pose * 17));
                    skin.SetBlendShapeWeight(0, pose * 40); frontRenderer.transform.SetPositionAndRotation(bone.position, bone.rotation); frontRenderer.transform.localScale = Vector3.one;
                    referenceMesh.vertices = rest.Select((p, i) => p + delta[i] * (pose * .4f)).ToArray(); referenceMesh.RecalculateBounds();
                    yield return null; yield return null;
                    front.renderer = skin; var native = Pair("native-skin-" + pose);
                    front.renderer = frontRenderer; var independent = Render(); float skinError = Difference(native, independent);
                    Check("native-skin-" + pose + "-independent-current-whole-rgba", skinError <= .00002f, skinError);
                    if (pose == 0) firstSkin = native; else Check("native-skin-" + pose + "-positive-current-change", Difference(firstSkin, native) > .02f);
                }
                referenceMesh.vertices = rest; referenceMesh.RecalculateBounds(); frontRenderer.transform.SetPositionAndRotation(savedFrontPosition, Quaternion.identity); frontRenderer.transform.localScale = savedFrontScale;
                skinHost.SetActive(false); Pair("restore-native-skin");
                var otherHost = Own(new GameObject("Isolated second Forward+ camera")); var otherCamera = otherHost.AddComponent<Camera>(); otherCamera.CopyFrom(camera); otherCamera.enabled = false;
                otherCamera.transform.SetPositionAndRotation(new Vector3(.4f, .1f, -3.7f), Quaternion.Euler(0, 7, 0));
                var otherTarget = Own(new RenderTexture(63, 41, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); otherTarget.Create(); otherCamera.targetTexture = otherTarget; otherCamera.aspect = 63f / 41;
                var otherStage = otherHost.AddComponent<SceneForwardLightingCamera>(); otherStage.surfaceLayers = 1 << 25;
                otherStage.settings.enabled = true; otherStage.settings.surfaces = new[] { back }; otherStage.settings.localLights.enabled = true;
                otherStage.settings.localLights.lights = new[] { new SceneDecalLight { position = new Vector3(0, 0, -1), range = 3, radiance = new Vector3(0, 3, 0) } };
                var firstCamera = Render(); otherCamera.Render();
                Check("second-camera-current-independent-grid", otherStage.UnavailableReason == null && otherStage.SubmittedLights == 1 && otherStage.TileBuffer != stage.TileBuffer);
                Check("second-camera-does-not-change-first-image", Difference(firstCamera, Render()) == 0);
                var secondCamera = ReadSceneTarget(otherTarget); otherCamera.Render();
                Check("first-camera-does-not-change-second-image", Difference(secondCamera, ReadSceneTarget(otherTarget)) == 0); otherHost.SetActive(false);
                // More than typical32/64 fixed-list capacity; final4096th light is retained.
                target = Target(33, 25); options.tileSize = 8;
                local.lights = Enumerable.Range(0, 4096).Select(i => new SceneDecalLight { position = new Vector3(0, 0, -1.5f), range = 15, radiance = new Vector3(.0001f, .0002f, .0003f), specularScale = 0 }).ToArray();
                var full = Pair("4096-overlap"); Grid("4096-overlap"); Check("no-dropped-4096th-light", stage.SubmittedLights == 4096);
                local.lights[4095].radiance = new Vector3(1, 0, 0); Check("last-light-visible", Difference(full, Pair("4096th-changed")) > .03f);
                local.lights = new[] { local.lights[0] }; Pair("shrink-4096-to-one"); Grid("shrink-4096-to-one");
                local.lights = Array.Empty<SceneDecalLight>(); Pair("zero-lights"); Check("zero-lights-no-grid", stage.TileCount == 0 && stage.GridBytes == 4);
                // Memory rejection must precede allocation of the full grid, with an explicit fallback.
                target = Target(385, 387); options.tileSize = 8; options.maximumGridMiB = 1;
                var backScale = back.renderer.transform.localScale; var frontScale = front.renderer.transform.localScale;
                back.renderer.transform.localScale = front.renderer.transform.localScale = Vector3.one * .01f;
                local.lights = Enumerable.Range(0, 4096).Select(i => new SceneDecalLight { position = new Vector3(0, 0, -1), range = 15, radiance = Vector3.one * .00001f, specularScale = 0 }).ToArray();
                Render(false); Check("budget-no-fallback-fails-closed", stage.UnavailableReason != null && stage.AllocatedBuffers == 0);
                options.allowBruteForceFallback = true; Render(); Check("budget-brute-fallback", stage.Backend == SceneForwardLightBackend.BruteForce && stage.FallbackReason != null && stage.GridBytes == 4);
                back.renderer.transform.localScale = backScale; front.renderer.transform.localScale = frontScale;
                options.maximumGridMiB = 32; local.lights = many; options.allowBruteForceFallback = false; target = Target(143, 103);
                Pair("resized-restored"); Grid("resized-restored");
                var beforeBad = Render(); var borrowed = back.renderer.sharedMaterial;
                options.surfaces = new[] { back, back }; Render(false); Check("duplicate-owner-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0);
                options.surfaces = new[] { back, front }; Pair("recover-duplicate");
                camera.cullingMask |= 1 << 25; Render(false); Check("double-draw-mask-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0); camera.cullingMask = 1 << 24;
                options.tileSize = 7; Render(false); Check("invalid-tile-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0); options.tileSize = 16;
                local.lights[0].range = float.NaN; Render(false); Check("nan-light-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0); local.lights[0].range = 1.67f;
                var oldAlpha = back.inputs.alpha; back.inputs.alpha = float.PositiveInfinity; Render(false); Check("invalid-material-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0); back.inputs.alpha = oldAlpha;
                var block = new MaterialPropertyBlock(); block.SetFloat("_Test", 1); back.renderer.SetPropertyBlock(block); Render(false);
                Check("foreign-property-block-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0); back.renderer.SetPropertyBlock(null);
                camera.rect = new Rect(0, 0, .5f, 1); Render(false); Check("partial-viewport-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0); camera.rect = new Rect(0, 0, 1, 1);
                Check("borrowed-material-unchanged", back.renderer.sharedMaterial == borrowed); Pair("recover-all-invalid-inputs");
                options.enabled = false; Check("disabled-restores-background-exact", Difference(disabled, Render(false)) == 0 && stage.AllocatedBuffers == 0);
                options.enabled = true; Render(); stage.enabled = false; Check("component-disable-releases", stage.AllocatedBuffers == 0 && camera.GetCommandBuffers(CameraEvent.BeforeForwardAlpha).Length == 0);
                stage.enabled = true; Pair("component-reenable");
                back.vertexScale = new Vector3(-.8f, 1.1f, 1); Pair("negative-vertex-scale"); back.vertexScale = Vector3.one;
                var noCutoff = Render(); back.alphaCutoff = .9f;
                Check("cutout-positive", Difference(noCutoff, Pair("alpha-cutout")) > .02f); back.alphaCutoff = 0;
                options.surfaces = Array.Empty<SceneForwardSurface>(); Render(); Check("empty-surfaces-release", stage.AllocatedBuffers == 0 && stage.SubmittedLights == 0);
                options.surfaces = new[] { back, front }; Pair("recover-empty-surfaces");
                options.surfaces = new SceneForwardSurface[257]; Render(false); Check("surface-capacity-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0); options.surfaces = new[] { back, front };
                local.lights = new SceneDecalLight[4097]; Render(false); Check("light-capacity-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0); local.lights = many;
                var disposableTexture = Own(new RenderTexture(3, 3, 0, RenderTextureFormat.ARGBFloat)); disposableTexture.Create(); disposableTexture.Release();
                back.inputs.albedoMap = disposableTexture; Render(false); Check("released-texture-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0); back.inputs.albedoMap = null;
                target.Release(); camera.Render(); Check("released-camera-target-rejected", stage.UnavailableReason != null && stage.AllocatedBuffers == 0); target.Create(); Pair("recreated-camera-target");
                local.lights = new[] { new SceneDecalLight { position = new Vector3(0, 0, -3.95f), range = 6, radiance = Vector3.one * 2 } }; camera.orthographic = false;
                var inside = Pair("perspective-eye-crossing-light"); Grid("perspective-eye-crossing-light"); local.lights = Array.Empty<SceneDecalLight>();
                Check("eye-crossing-positive-light", Difference(inside, Render()) > .02f); camera.orthographic = true; local.lights = many;
                Check("native-draws-completed", stage.RenderSequence > 20);
            }
            finally
            {
                RenderTexture.active = active;
                foreach (var value in _owned) { if (value is RenderTexture rt) rt.Release(); if (value != null) Destroy(value); }
                _owned.Clear(); for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i];
            }
            yield return null;
        }

        private static Vector3 ForwardCpuBrdf(Vector3 albedo, Vector3 mos, Vector3 n, Vector3 v, Vector3 l, float diffuse, float specular, float backlight)
        {
            var h = (v + l).normalized;
            double nl = Math.Max(0, Vector3.Dot(n, l)), nv = Math.Max(0, Vector3.Dot(n, v)), nh = Math.Max(0, Vector3.Dot(n, h)), vh = Math.Max(0, Vector3.Dot(v, h));
            double a2 = Math.Pow(Math.Max(1 - mos.z, .045), 4), denominator = nh * nh * (a2 - 1) + 1;
            double d = a2 / Math.Max(Math.PI * denominator * denominator, 1e-8);
            double visibility = .5 / Math.Max(nl * Math.Sqrt(nv * nv * (1 - a2) + a2) + nv * Math.Sqrt(nl * nl * (1 - a2) + a2), 1e-6);
            var result = Vector3.zero;
            for (int c = 0; c < 3; c++)
            {
                double f0 = .04 * (1 - mos.x) + albedo[c] * mos.x, f = f0 + (1 - f0) * Math.Pow(1 - vh, 5);
                result[c] = (float)(((1 - f) * albedo[c] * (1 - mos.x) / Math.PI * diffuse + d * visibility * f * specular) * nl +
                    (1 - f0) * albedo[c] * (1 - mos.x) / Math.PI * diffuse * backlight * Math.Max(0, -Vector3.Dot(n, l)));
            }
            return result;
        }
        private static Vector3 ForwardCpuLocal(SceneDecalLight light, Vector3 albedo, Vector3 mos, Vector3 world, Vector3 n, Vector3 v)
        {
            var rotation = light.rotation.normalized; var delta = world - light.position; var source = light.position;
            var x = rotation * Vector3.right; var y = rotation * Vector3.up; var z = rotation * Vector3.forward;
            float distance;
            if (light.shape == SceneDecalLightShape.Capsule) { source += x * Mathf.Clamp(Vector3.Dot(delta, x), -light.halfLength, light.halfLength); distance = Vector3.Distance(world, source); }
            else if (light.shape == SceneDecalLightShape.Area)
            {
                distance = Vector3.Dot(delta, z); if (distance <= 0 || distance >= light.range) return Vector3.zero;
                float px = Vector3.Dot(delta, x) / (light.halfSize.x + light.areaSpread.x * distance), py = Vector3.Dot(delta, y) / (light.halfSize.y + light.areaSpread.y * distance);
                if (Mathf.Abs(px) > 1 || Mathf.Abs(py) > 1) return Vector3.zero;
                source += x * px * light.halfSize.x + y * py * light.halfSize.y;
            }
            else distance = delta.magnitude;
            float attenuation = Mathf.Pow(Mathf.Clamp01(1 - distance / light.range), light.falloffExponent);
            if (light.shape == SceneDecalLightShape.Spot)
            {
                if (distance <= 1e-6f) return Vector3.zero;
                float cosine = Vector3.Dot(delta / distance, z), inner = Mathf.Cos(light.spotInnerAngle * Mathf.Deg2Rad / 2), outer = Mathf.Cos(light.spotOuterAngle * Mathf.Deg2Rad / 2);
                if (cosine < outer) return Vector3.zero;
                if (inner > outer) attenuation *= Mathf.Clamp01((cosine - outer) / (inner - outer));
            }
            return Vector3.Scale(ForwardCpuBrdf(albedo, mos, n, v, (source - world).normalized, light.diffuseScale, light.specularScale, light.backlightScale), light.radiance) * attenuation;
        }
    }
}
