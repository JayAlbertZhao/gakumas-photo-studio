using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct CrowdShadowFixturePlacement { public Vector4 positionScale, rotationType, tint; }
        private IEnumerator VerifyCrowdShadows(Report report)
        {
            yield return null;
            void Check(string n, bool ok, float error = 0) => FrameworkCheck(report, "crowd-shadow-" + n, ok, error);
            var previous = FindObjectsOfType<Renderer>(); var forced = previous.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in previous) r.forceRenderingOff = true; var active = RenderTexture.active;
            using var source = new CrowdShadowSource();
            try
            {
                const int size = 65;
                var host = Own(new GameObject("Independent current crowd shadow fixture")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward; camera.cullingMask = 0;
                camera.orthographic = true; camera.orthographicSize = 1.7f; camera.aspect = 1; camera.transform.position = new Vector3(0, 0, -4);
                camera.nearClipPlane = .1f; camera.farClipPlane = 20; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                var target = Own(new RenderTexture(size, size, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var stage = host.AddComponent<SceneDeferredCamera>(); stage.sceneEnabled = true; stage.sceneLayers = 1 << 25;
                stage.lightRadiance = stage.ambientIrradiance = Vector3.zero; stage.giBaseScale = 0;
                var receiver = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); receiver.layer = 25; receiver.transform.localScale = Vector3.one * 3.8f;
                var surface = new SceneDeferredCamera.Surface { renderer = receiver.GetComponent<Renderer>(), cull = CullMode.Off };
                surface.inputs.albedo = new Vector3(.5f, .375f, .25f); surface.inputs.mos = new Vector3(0, .75f, .25f); surface.inputs.emission = new Vector3(.03125f, .0625f, .015625f); stage.surfaces = new[] { surface };
                var light = new SceneDecalLight { shape = SceneDecalLightShape.Spot, position = new Vector3(.17f, -.13f, -2), range = 5,
                    halfLength = .8f, halfSize = new Vector2(.7f, .6f), areaSpread = new Vector2(.35f, .25f), radiance = new Vector3(2, 3, 1), specularScale = 0, spotOuterAngle = 130 };
                light.shadow.enabled = true; light.shadow.extendedSourceCoverage = true; light.shadow.extendedSamplesPerAxis = 2;
                Check("point-legacy-addressing-default", !light.shadow.stablePointTexels); light.shadow.stablePointTexels = true;
                var options = stage.decalLighting; options.enabled = true; options.backend = SceneDecalLightBackend.Instanced; options.allowInstancingFallback = false;
                options.lights = new[] { light }; options.shadows.tileResolution = 64;
                stage.mainLightShadow.resolution = 64; stage.mainLightShadow.origin = new Vector3(.17f, -.13f, -2); stage.mainLightShadow.halfSize = Vector2.one * 2;
                var forward = host.AddComponent<SceneForwardLightingCamera>(); forward.surfaceLayers = 1 << 25;
                forward.settings.surfaces = new[] { new SceneForwardSurface { renderer = surface.renderer, cull = CullMode.Off, inputs = surface.inputs } };
                forward.settings.lightRadiance = forward.settings.ambientIrradiance = Vector3.zero; forward.settings.giBaseScale = 0; forward.settings.localLights = options;
                forward.settings.mainLightShadow = stage.mainLightShadow;
                var definition = Own(ScriptableObject.CreateInstance<CrowdDefinition>());
                var sourceMesh = Own(Instantiate(receiver.GetComponent<MeshFilter>().sharedMesh));
                var points = sourceMesh.vertices; for (int i = 0; i < points.Length; i++) points[i] = new Vector3(points[i].x * (.55f + .2f * points[i].y), points[i].y * .65f, points[i].x * .13f);
                sourceMesh.vertices = points; sourceMesh.RecalculateBounds(); sourceMesh.RecalculateNormals();
                var highMesh = Own(Instantiate(sourceMesh)); var highPoints = highMesh.vertices; for (int i = 0; i < highPoints.Length; i++) highPoints[i].x *= 1.3f; highMesh.vertices = highPoints; highMesh.RecalculateBounds();
                definition.prototypes = Enumerable.Range(0, 3).Select(i => new CrowdPrototype { lowMesh = sourceMesh, highMesh = highMesh, cull = CullMode.Off }).ToArray();
                definition.instances = Enumerable.Range(0, 6).Select(i => new CrowdInstance { prototype = i % 3, position = new Vector3((i % 3 - 1) * .65f, i / 3 * .6f - .4f, -.9f), yawDegrees = i * 7, scale = .7f + i * .05f }).ToArray();
                var config = new CrowdShadowSettings { enabled = true, backend = CrowdBackend.Gpu, allowCpuFallback = false };
                var references = Enumerable.Range(0, 6).Select(i => { var g = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); g.layer = 24; return g; }).ToArray();
                CrowdPose[] poses = null; Mesh referencePose = null;
                void Prepare() { if (!source.TryPrepare(definition, config, poses)) throw new InvalidOperationException(source.UnavailableReason); }
                SceneShadowCaster[] Reference()
                {
                    var result = new List<SceneShadowCaster>();
                    for (int i = 0; i < definition.instances.Length; i++)
                    {
                        var item = definition.instances[i]; var p = definition.prototypes[item.prototype]; var g = references[i];
                        g.GetComponent<MeshFilter>().sharedMesh = referencePose != null ? referencePose : config.meshQuality == CrowdMeshQuality.High ? p.highMesh : p.lowMesh;
                        g.transform.SetPositionAndRotation(item.position, Quaternion.Euler(0, item.yawDegrees, 0)); g.transform.localScale = Vector3.one * item.scale;
                        g.GetComponent<Renderer>().enabled = !item.hidden;
                        result.Add(new SceneShadowCaster { renderer = g.GetComponent<Renderer>(), cull = p.cull, alpha = p.material.alpha, alphaMap = p.material.albedoMap, cutoff = p.alphaCutoff, uvST = p.material.uvST });
                    }
                    return result.ToArray();
                }
                SceneDeferredCamera.Frame Render() { camera.Render(); if (!stage.TryGetFrame(out var f)) throw new InvalidOperationException(stage.UnavailableReason); return f; }
                float Difference(Color[] a, Color[] b)
                {
                    if (a.Length != b.Length) return float.MaxValue; float max = 0;
                    for (int i = 0; i < a.Length; i++) for (int c = 0; c < 4; c++) { float d = Mathf.Abs(a[i][c] - b[i][c]); if (float.IsNaN(d) || float.IsInfinity(d)) return float.MaxValue; max = Mathf.Max(max, d); } return max;
                }
                void Raw(string name, Color[] data)
                { using var w = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(_directory, "crowd-shadow-" + name + ".raw"))); foreach (var p in data) for (int c = 0; c < 4; c++) w.Write(p[c]); }
                var coordinateProbe = Own(new Material(Resources.Load<Shader>("CrowdShadowReceiverReference")));
                var coordinateTarget = Own(new RenderTexture(size, size, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); coordinateTarget.Create();
                var coordinateQuad = Own(Instantiate(receiver.GetComponent<MeshFilter>().sharedMesh)); coordinateQuad.vertices = coordinateQuad.vertices.Select(v => v * 2).ToArray(); coordinateQuad.RecalculateBounds();
                Color[][] Coordinates(string name, SceneDeferredCamera.Frame frame)
                {
                    var result = new Color[2][];
                    var view = camera.worldToCameraMatrix; var vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * view;
                    coordinateProbe.SetMatrix("_ViewProjection", vp); coordinateProbe.SetVector("_VertexScale", Vector3.one);
                    coordinateProbe.SetMatrix("_LightInverseViewProjection", vp.inverse); coordinateProbe.SetMatrix("_LightView", view); coordinateProbe.SetTexture("_G2", frame.mosDepth);
                    using var snapshot = new SceneDecalLightRenderer(); if (!snapshot.PrepareSnapshot(camera, options, out var error)) throw new InvalidOperationException(error);
                    coordinateProbe.SetVector("_SingleClipRect", snapshot.PreparedLights[0].clipRect);
                    for (int pass = 0; pass < 2; pass++)
                    {
                        using var commands = new CommandBuffer(); commands.SetRenderTarget(coordinateTarget); commands.SetViewport(new Rect(0, 0, size, size)); commands.ClearRenderTarget(true, true, Color.clear);
                        if (pass == 0) commands.DrawRenderer(surface.renderer, coordinateProbe, 0, 0);
                        else commands.DrawMesh(coordinateQuad, Matrix4x4.identity, coordinateProbe, 0, 1);
                        Graphics.ExecuteCommandBuffer(commands); var pixels = ReadSceneTarget(coordinateTarget);
                        Check(name + "-coordinate-probe-coverage-" + pass, pixels.All(p => p.a == 1));
                        result[pass] = pixels;
                        Raw(name + (pass == 0 ? "-receiver-forward-world" : "-receiver-deferred-world"), pixels);
                    }
                    Raw(name + "-receiver-atlas", ReadSceneTarget(frame.lightShadowAtlas)); Raw(name + "-receiver-lit", ReadSceneTarget(target));
                    return result;
                }
                // Independent Point PCF and diffuse/Schlick evaluation for this authored
                // plane. Each backend supplies its actual receiver coordinates, not the
                // other's slightly different raster/reconstruction samples.
                Color[] PointOracle(Color[] coordinates, Color[] atlas)
                {
                    int resolution = options.shadows.tileResolution, width = resolution * 3;
                    Vector3 Face(Vector3 d)
                    {
                        var a = new Vector3(Mathf.Abs(d.x), Mathf.Abs(d.y), Mathf.Abs(d.z)); float threshold = Mathf.Max(a.x, Mathf.Max(a.y, a.z)) * (1 - .00001f);
                        if (a.x >= threshold) return new Vector3((d.x >= 0 ? -d.z : d.z) / a.x, d.y / a.x, d.x >= 0 ? 0 : 1);
                        if (a.y >= threshold) return new Vector3((d.y >= 0 ? -d.x : d.x) / a.y, d.z / a.y, d.y >= 0 ? 2 : 3);
                        return new Vector3((d.z >= 0 ? d.x : -d.x) / a.z, d.y / a.z, d.z >= 0 ? 4 : 5);
                    }
                    Vector3 Direction(Vector2 p, int face)
                    {
                        switch (face) { case 0: return new Vector3(1, p.y, -p.x); case 1: return new Vector3(-1, p.y, p.x); case 2: return new Vector3(-p.x, 1, p.y);
                            case 3: return new Vector3(p.x, -1, p.y); case 4: return new Vector3(p.x, p.y, 1); default: return new Vector3(-p.x, p.y, -1); }
                    }
                    var expected = new Color[coordinates.Length];
                    for (int i = 0; i < expected.Length; i++)
                    {
                        var world = new Vector3(coordinates[i].r, coordinates[i].g, coordinates[i].b); var d = world - light.position;
                        float distance = d.magnitude, receiverDepth = (distance - light.shadow.depthBias) / light.range, visibility = 0; var baseFace = Face(d);
                        for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
                        {
                            var p = new Vector2(baseFace.x, baseFace.y) + new Vector2(x, y) * (2f / resolution); var face = Face(Direction(p, (int)baseFace.z));
                            int Texel(float value) { float coordinate = (value * .5f + .5f) * resolution, edge = Mathf.Round(coordinate);
                                if (Mathf.Abs(coordinate - edge) <= resolution / 1048576f) coordinate = edge; return Mathf.Clamp(Mathf.FloorToInt(coordinate), 0, resolution - 1); }
                            int tile = (int)face.z, u = Texel(face.x), v = Texel(face.y);
                            if (receiverDepth <= atlas[(tile / 3 * resolution + v) * width + tile % 3 * resolution + u].r) visibility += 1f / 9;
                        }
                        var direction = -d / distance; var half = (Vector3.back + direction).normalized;
                        float fresnel = .04f + .96f * Mathf.Pow(1 - Mathf.Clamp01(Vector3.Dot(Vector3.back, half)), 5);
                        float scale = (1 - fresnel) * Mathf.Clamp01(-direction.z) / Mathf.PI * Mathf.Pow(Mathf.Clamp01(1 - distance / light.range), light.falloffExponent) * visibility;
                        var rgb = surface.inputs.emission + Vector3.Scale(surface.inputs.albedo, light.radiance) * scale;
                        expected[i] = new Color(rgb.x, rgb.y, rgb.z, 1);
                    }
                    return expected;
                }
                bool main = false; int cases = 0;
                Color[] Case(string name, bool positive = true, bool capture = false)
                {
                    Prepare(); stage.mainLightShadow.enabled = main; stage.lightRadiance = main ? new Vector3(2, 3, 1) : Vector3.zero;
                    light.shadow.enabled = !main; light.radiance = main ? Vector3.zero : new Vector3(2, 3, 1);
                    options.shadows.casters = stage.mainLightShadow.casters = Array.Empty<SceneShadowCaster>();
                    options.shadows.crowds = stage.mainLightShadow.crowds = Array.Empty<CrowdShadowSource>(); Render(); var clear = ReadSceneTarget(target);
                    options.shadows.crowds = stage.mainLightShadow.crowds = new[] { source };
                    bool native = capture && Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_CROWD_SHADOW") == "1";
                    void Captured(string kind, Action draw)
                    {
                        if (!native) { draw(); return; }
                        target.name = "Crowd shadow final " + name + "-" + kind; FsrCaptureDrain(target);
                        bool started = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                        try { draw(); FsrCaptureDrain(target); } finally { if (started) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                        Check(name + "-native-" + kind, started && ended); Raw(name + "-" + kind, ReadSceneTarget(target));
                    }
                    SceneDeferredCamera.Frame frame = default; Captured("deferred", () => frame = Render());
                    var actual = ReadSceneTarget(target); var atlas = ReadSceneTarget(main ? frame.mainLightShadowDepth : frame.lightShadowAtlas);
                    var worlds = name.StartsWith("offscreen-point-") || name == "camera-offscreen-still-casts" ? Coordinates(name, frame) : null;
                    Color[] forwardOracle = null, deferredOracle = null;
                    if (worlds != null)
                    {
                        forwardOracle = PointOracle(worlds[0], atlas); deferredOracle = PointOracle(worlds[1], atlas);
                        float oracleError = Difference(actual, deferredOracle); Check(name + "-whole-deferred-coordinate-oracle", oracleError <= .0003f, oracleError);
                    }
                    if (native) Raw(name + "-atlas", atlas);
                    int maps = main ? 1 : stage.LightShadowMapCount;
                    Check(name + "-indirect-draw-budget", (main ? stage.MainShadowCasterDrawCalls : stage.LightShadowCasterDrawCalls) == maps * source.DrawCount);
                    options.shadows.crowds = stage.mainLightShadow.crowds = Array.Empty<CrowdShadowSource>();
                    options.shadows.casters = stage.mainLightShadow.casters = Reference(); var referenceFrame = Render(); var expected = ReadSceneTarget(target);
                    float depthError = Difference(atlas, ReadSceneTarget(main ? referenceFrame.mainLightShadowDepth : referenceFrame.lightShadowAtlas));
                    float error = Difference(actual, expected); Check(name + "-whole-authored-depth", depthError <= .00002f, depthError); Check(name + "-whole-authored-lit", error <= .0003f, error);
                    if (depthError > .00002f || error > .0003f) { Raw(name + "-failed-depth", atlas); Raw(name + "-expected-depth", ReadSceneTarget(main ? referenceFrame.mainLightShadowDepth : referenceFrame.lightShadowAtlas)); }
                    float change = Difference(actual, clear); Check(name + "-visibility-control", positive ? change > .0001f : change == 0, change);
                    options.shadows.casters = stage.mainLightShadow.casters = Array.Empty<SceneShadowCaster>(); options.shadows.crowds = stage.mainLightShadow.crowds = new[] { source };
                    stage.sceneEnabled = false; forward.settings.enabled = true; forward.settings.lightRadiance = stage.lightRadiance;
                    foreach (var backend in new[] { SceneForwardLightBackend.BruteForce, SceneForwardLightBackend.Tiled })
                    {
                        forward.settings.backend = backend; Captured("forward-" + backend, () => camera.Render());
                        var forwardActual = ReadSceneTarget(target); float delta = Difference(forwardActual, actual);
                        if (worlds == null) Check(name + "-whole-forward-" + backend, delta <= .003f, delta);
                        else
                        {
                            float oracleError = Difference(forwardActual, forwardOracle); Check(name + "-whole-forward-coordinate-oracle-" + backend, oracleError <= .0003f, oracleError);
                            var observed = forwardActual.Zip(actual, (a, b) => a - b).ToArray(); var predicted = forwardOracle.Zip(deferredOracle, (a, b) => a - b).ToArray();
                            float residual = Difference(observed, predicted); Check(name + "-whole-backend-difference-predicted-" + backend, residual <= .0003f, residual);
                        }
                        options.shadows.crowds = stage.mainLightShadow.crowds = Array.Empty<CrowdShadowSource>(); options.shadows.casters = stage.mainLightShadow.casters = Reference(); camera.Render();
                        float authored = Difference(forwardActual, ReadSceneTarget(target)); Check(name + "-whole-forward-authored-" + backend, authored <= .0003f, authored);
                        if (delta > .003f) { Raw(name + "-forward-" + backend, forwardActual); Raw(name + "-deferred", actual); Raw(name + "-forward-authored-" + backend, ReadSceneTarget(target)); }
                        options.shadows.casters = stage.mainLightShadow.casters = Array.Empty<SceneShadowCaster>(); options.shadows.crowds = stage.mainLightShadow.crowds = new[] { source };
                    }
                    forward.settings.enabled = false; stage.sceneEnabled = true;
                    SaveSsrPreview("crowd-shadow-" + name, actual, size, size, false); cases++; return actual;
                }
                foreach (var shape in new[] { SceneDecalLightShape.Spot, SceneDecalLightShape.Point, SceneDecalLightShape.Capsule, SceneDecalLightShape.Area })
                {
                    light.shape = shape;
                    foreach (var filter in new[] { SceneShadowFilter.Hard, SceneShadowFilter.Pcf3x3 })
                    { light.shadow.filter = filter; Case(shape + "-" + filter, true, filter == SceneShadowFilter.Hard && (shape == SceneDecalLightShape.Spot || shape == SceneDecalLightShape.Point)); }
                }
                main = true; Case("directional-current", true, true); stage.mainLightShadow.filter = SceneShadowFilter.Pcf3x3; Case("directional-pcf"); main = false;
                light.shape = SceneDecalLightShape.Point; var original = Case("before-placement-change");
                definition.instances[1].position += new Vector3(.24f, -.15f, .1f); definition.instances[2].yawDegrees = -31; definition.instances[3].scale = 1.2f;
                Check("moving-current-placement", Difference(original, Case("moved-scaled-rotated")) > .001f);
                definition.instances[0].hidden = true; Case("hidden-one"); definition.instances[0].hidden = false;
                foreach (var i in definition.instances) i.hidden = true; Case("hidden-all", false); foreach (var i in definition.instances) i.hidden = false;
                config.backend = CrowdBackend.Cpu; var cpu = Case("explicit-cpu"); config.backend = CrowdBackend.Gpu;
                Check("gpu-cpu-whole-frame", Difference(cpu, Case("current-gpu")) <= .0003f, Difference(cpu, ReadSceneTarget(target)));
                config.meshQuality = CrowdMeshQuality.High; Case("explicit-high"); config.meshQuality = CrowdMeshQuality.Low;
                var cutout = Own(new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true)); cutout.filterMode = FilterMode.Point; cutout.wrapMode = TextureWrapMode.Repeat;
                cutout.SetPixels(Enumerable.Range(0, 16).Select(i => new Color(1, 1, 1, (i % 4 + i / 4) % 2)).ToArray()); cutout.Apply();
                definition.prototypes[1].material.albedoMap = cutout; Case("current-cutout"); definition.prototypes[1].material.uvST = new Vector4(1.3f, .7f, .23f, -.1f); Case("current-cutout-uv");
                definition.prototypes[1].material.alpha = .2f; Case("alpha-removed-prototype"); definition.prototypes[1].material.alpha = 1; definition.prototypes[1].material.albedoMap = null;
                options.backend = SceneDecalLightBackend.Scalar; Case("scalar-light-consumer"); options.backend = SceneDecalLightBackend.Instanced;
                var placements = definition.instances; definition.instances = new[] { new CrowdInstance { position = new Vector3(1.5f, 0, -1) } };
                camera.orthographicSize = .7f; light.position = new Vector3(3, 0, -2); Case("camera-offscreen-still-casts");
                // Sweep the light at fixed receiver rasterization. Moving the camera by
                // less than a D3D11 subpixel changes Deferred reconstruction while the
                // snapped Forward triangle attributes can stay fixed: different inputs.
                foreach (float offset in new[] { -.00001f, -.000001f, .000001f, .00001f })
                { light.position = new Vector3(3 + offset, 0, -2); Case("offscreen-point-light-edge-" + offset.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); }
                camera.orthographicSize = 1.7f; light.position = new Vector3(.17f, -.13f, -2); definition.instances = placements;
                // Single authored bone and shape: independent reference vertices use the known affine pose.
                var rig = Own(new GameObject("Authored crowd shadow rig")); var bone = Own(new GameObject("One explicit shadow bone")); bone.transform.SetParent(rig.transform, false);
                var skinMesh = Own(Instantiate(sourceMesh)); skinMesh.bindposes = new[] { Matrix4x4.identity };
                skinMesh.boneWeights = Enumerable.Range(0, skinMesh.vertexCount).Select(i => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
                var deltas = points.Select(p => new Vector3(.12f * (p.y + .5f), .08f * p.x, .07f * p.y)).ToArray();
                skinMesh.AddBlendShapeFrame("Independent current wave", 100, deltas, new Vector3[points.Length], new Vector3[points.Length]);
                foreach (var p in definition.prototypes) p.lowMesh = skinMesh;
                poses = Enumerable.Range(0, 3).Select(i => new CrowdPose { root = rig.transform, bones = new[] { bone.transform }, blendShapeWeights = new[] { 0f } }).ToArray();
                referencePose = Own(Instantiate(sourceMesh));
                for (int pose = 0; pose < 3; pose++)
                {
                    bone.transform.localPosition = new Vector3(.06f * pose, .08f * pose, -.05f * pose); bone.transform.localRotation = Quaternion.Euler(7 * pose, -11 * pose, 13 * pose);
                    foreach (var p in poses) p.blendShapeWeights[0] = 50 * pose;
                    referencePose.vertices = points.Select((p, i) => bone.transform.localToWorldMatrix.MultiplyPoint3x4(p + deltas[i] * (.5f * pose))).ToArray(); referencePose.RecalculateBounds();
                    var deformed = Case("current-bone-shape-" + pose, true, pose == 2); var deformedDepth = ReadSceneTarget(Render().lightShadowAtlas);
                    var nativeCasters = new List<SceneShadowCaster>(); var nativeHosts = new List<GameObject>();
                    foreach (var item in definition.instances)
                    {
                        var nativeHost = Own(new GameObject("Native crowd shadow reference")); nativeHost.layer = 24; nativeHosts.Add(nativeHost);
                        var nativeBone = Own(new GameObject("Native current reference bone")).transform;
                        var placement = Matrix4x4.TRS(item.position, Quaternion.Euler(0, item.yawDegrees, 0), Vector3.one * item.scale);
                        nativeBone.SetPositionAndRotation(placement.MultiplyPoint3x4(bone.transform.localPosition), Quaternion.Euler(0, item.yawDegrees, 0) * bone.transform.localRotation); nativeBone.localScale = Vector3.one * item.scale;
                        var skin = nativeHost.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = skinMesh; skin.sharedMaterial = surface.renderer.sharedMaterial;
                        skin.bones = new[] { nativeBone }; skin.rootBone = nativeBone; skin.updateWhenOffscreen = true; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 20);
                        skin.SetBlendShapeWeight(0, 50 * pose); nativeCasters.Add(new SceneShadowCaster { renderer = skin, cull = CullMode.Off });
                    }
                    yield return null;
                    options.shadows.crowds = Array.Empty<CrowdShadowSource>(); options.shadows.casters = nativeCasters.ToArray();
                    var nativeFrame = Render(); float nativeDepthError = Difference(deformedDepth, ReadSceneTarget(nativeFrame.lightShadowAtlas)); float nativeImageError = Difference(deformed, ReadSceneTarget(target));
                    Check("native-skin-whole-depth-" + pose, nativeDepthError <= .00002f, nativeDepthError); Check("native-skin-whole-lit-" + pose, nativeImageError <= .0003f, nativeImageError);
                    foreach (var nativeHost in nativeHosts) nativeHost.SetActive(false);
                }
                foreach (var p in definition.prototypes) p.lowMesh = sourceMesh; poses = null; referencePose = null; Prepare();
                using (var atlas = new SceneLightShadowAtlas())
                {
                    var lights = new List<SceneDecalLight> { light }; var settings = new SceneLightShadowSettings { tileResolution = 32, crowds = new[] { source } };
                    Check("explicit-adapter-prepare", atlas.Prepare(lights, settings, true, out _));
                    Prepare(); using var commands = new CommandBuffer(); bool rejected = false; try { atlas.Record(commands); } catch (InvalidOperationException) { rejected = true; }
                    Check("changed-generation-rejected-before-record", rejected && commands.sizeInBytes == 0);
                    settings.maxCrowdShadowTriangles = 1; atlas.Dispose(); Check("triangle-work-budget-before-allocation", !atlas.Prepare(lights, settings, true, out _) && atlas.Atlas == null);
                    settings.maxCrowdShadowTriangles = 4194304; settings.crowds = new[] { source, source }; Check("duplicate-source-rejected", !atlas.Prepare(lights, settings, true, out _));
                    settings.crowds = new[] { source }; light.shape = SceneDecalLightShape.Capsule; settings.maxExtendedCasterDraws = 1; Check("extended-draw-budget-includes-crowd", !atlas.Prepare(lights, settings, true, out _));
                    light.shape = SceneDecalLightShape.Point; settings.maxExtendedCasterDraws = 32768; source.Dispose(); Check("disposed-source-rejected", !atlas.Prepare(lights, settings, true, out _));
                }
                config.enabled = false; Check("disabled-releases-owned-buffers", !source.TryPrepare(definition, config) && source.AllocatedBuffers == 0); config.enabled = true;
                var saved = definition.instances[0].scale; definition.instances[0].scale = float.NaN; Check("nonfinite-releases", !source.TryPrepare(definition, config) && source.AllocatedBuffers == 0); definition.instances[0].scale = saved;
                definition.instances = Array.Empty<CrowdInstance>(); Check("empty-releases", !source.TryPrepare(definition, config) && source.AllocatedBuffers == 0); definition.instances = placements;
                Case("recovered-after-invalid"); target.Release(); target.Create(); Case("caller-target-recreated");
                // Bounded stress: all live placements except the last lie outside every source face.
                // Compare the entire atlas against one explicit authored caster, not an edge mask.
                foreach (int count in new[] { 10000, 65536 })
                {
                    definition.instances = Enumerable.Range(0, count).Select(i => new CrowdInstance { prototype = i % 3,
                        position = i == count - 1 ? new Vector3(.1f, .17f, -.9f) : new Vector3(100 + i % 10, 20, -.9f), hidden = i != count - 1 && i % 17 == 0 }).ToArray();
                    Prepare(); using var atlas = new SceneLightShadowAtlas(); var limits = new SceneLightShadowSettings { tileResolution = 32, crowds = new[] { source } };
                    if (!atlas.Prepare(new List<SceneDecalLight> { light }, limits, true, out var error)) throw new InvalidOperationException(error);
                    using (var commands = new CommandBuffer()) { atlas.Record(commands); Graphics.ExecuteCommandBuffer(commands); }
                    var actual = ReadSceneTarget(atlas.Atlas); var words = new uint[15]; source.IndirectArguments.GetData(words);
                    int total = 0; bool argsMatch = true;
                    for (int type = 0; type < 3; type++)
                    {
                        int expected = definition.instances.Count(i => !i.hidden && i.prototype == type); total += expected;
                        argsMatch &= words[type * 5] == 6 && words[type * 5 + 1] == expected && words[type * 5 + 2] == 0 && words[type * 5 + 3] == 0 && words[type * 5 + 4] == 0;
                    }
                    Check("count-" + count + "-all-native-argument-words", argsMatch && total == source.InstanceCount && source.TriangleCount == total * 2L);
                    var nativePlacements = new CrowdShadowFixturePlacement[count]; source.Placements.GetData(nativePlacements); int cursor = 0; bool placementMatch = true;
                    for (int type = 0; type < 3; type++) foreach (var item in definition.instances.Where(i => !i.hidden && i.prototype == type))
                    {
                        var p = nativePlacements[cursor++]; placementMatch &= p.positionScale == new Vector4(item.position.x, item.position.y, item.position.z, item.scale) &&
                            p.rotationType == new Vector4(0, 1, type, 0) && p.tint == Vector4.one;
                    }
                    for (; cursor < count; cursor++) placementMatch &= nativePlacements[cursor].positionScale == Vector4.zero && nativePlacements[cursor].rotationType == Vector4.zero && nativePlacements[cursor].tint == Vector4.zero;
                    Check("count-" + count + "-every-ordered-placement-and-zero-tail", placementMatch);
                    var last = definition.instances[count - 1]; definition.instances = new[] { last }; limits.crowds = Array.Empty<CrowdShadowSource>(); limits.casters = Reference();
                    if (!atlas.Prepare(new List<SceneDecalLight> { light }, limits, true, out error)) throw new InvalidOperationException(error);
                    using (var commands = new CommandBuffer()) { atlas.Record(commands); Graphics.ExecuteCommandBuffer(commands); }
                    float depthError = Difference(actual, ReadSceneTarget(atlas.Atlas)); Check("count-" + count + "-whole-last-visible-depth", depthError <= .00002f && actual.Any(p => p.r < 1), depthError);
                }
                definition.instances = placements;
                Check("borrowed-mesh-and-material-unchanged", sourceMesh.vertices.SequenceEqual(points) && definition.prototypes.All(p => p.lowMesh == sourceMesh));
                Check("all-whole-cases", cases == 31, cases);
            }
            finally
            {
                RenderTexture.active = active;
                for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i];
                foreach (var item in _owned) if (item != null) Destroy(item); _owned.Clear();
            }
        }
    }
}
