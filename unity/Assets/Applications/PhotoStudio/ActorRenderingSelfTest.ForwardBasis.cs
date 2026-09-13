using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyForwardBasis(Report report)
        {
            yield return null;
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "forward-basis-" + name, ok, error);
            var previous = FindObjectsOfType<Renderer>(); var forced = previous.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in previous) r.forceRenderingOff = true;
            var active = RenderTexture.active;
            using var low = new LowResolutionFxRenderer(); using var heavy = new HeavyFxRenderer();
            try
            {
                const int width = 97, height = 65;
                var host = Own(new GameObject("Independent shared Forward normal basis")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.renderingPath = RenderingPath.Forward; camera.allowHDR = true; camera.allowMSAA = false;
                camera.transform.position = new Vector3(0, 0, -4); camera.orthographic = true; camera.orthographicSize = 1.8f; camera.aspect = (float)width / height;
                camera.nearClipPlane = .1f; camera.farClipPlane = 30; camera.cullingMask = 0; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.19f, .27f, .35f, .61f);
                RenderTexture Target(RenderTextureFormat format, int depthBits = 0)
                { var t = Own(new RenderTexture(width, height, depthBits, format, RenderTextureReadWrite.Linear)); t.Create(); return t; }
                var target = Target(RenderTextureFormat.ARGBFloat, 24); target.name = "Shared Forward mapped normal current output"; camera.targetTexture = target;
                var source = Target(RenderTextureFormat.ARGBFloat); var depth = Target(RenderTextureFormat.RFloat);
                var depthUpload = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); depthUpload.SetPixel(0, 0, new Color(25, 0, 0, 0)); depthUpload.Apply(); Graphics.Blit(depthUpload, depth);
                var stage = host.AddComponent<SceneForwardLightingCamera>(); stage.surfaceLayers = 1 << 25;
                var settings = stage.settings; settings.enabled = true; settings.lightDirection = new Vector3(.23f, .73f, -1).normalized;
                settings.lightRadiance = new Vector3(1.31f, .79f, .57f); settings.ambientIrradiance = new Vector3(.07f, .13f, .09f);
                settings.localLights.enabled = true; settings.localLights.lights = Enumerable.Range(0, 4).Select(i => new SceneDecalLight {
                    shape = (SceneDecalLightShape)i, position = new Vector3((i - 1.5f) * .31f, .43f, -1.7f), range = 5,
                    radiance = new Vector3(.31f, .27f, .41f), halfLength = .4f, halfSize = Vector2.one * .4f, areaSpread = Vector2.one,
                    spotInnerAngle = 90, spotOuterAngle = 130 }).ToArray();
                var input = new SceneDeferredCamera.MaterialInputs { albedo = new Vector3(.37f, .53f, .29f), mos = new Vector3(.17f, .73f, .36f), alpha = .63f, emission = new Vector3(.03f, .01f, .02f) };
                var normalMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
                var go = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.name = "Mapped shared Forward receiver"; go.layer = 25;
                var mesh = Own(Instantiate(go.GetComponent<MeshFilter>().sharedMesh)); go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var original = mesh.vertices; var originalMaterial = go.GetComponent<Renderer>().sharedMaterial;
                var objectNormal = new Vector3(.13f, .21f, -.9f).normalized; var objectTangent = new Vector3(1, .17f, .13f);
                mesh.normals = Enumerable.Repeat(objectNormal, 4).ToArray(); mesh.uv2 = mesh.uv;
                var renderer = go.GetComponent<Renderer>();
                var referenceGo = Own(new GameObject("Independent world-normal geometry")); referenceGo.layer = 25;
                var referenceMesh = Own(Instantiate(mesh)); referenceGo.AddComponent<MeshFilter>().sharedMesh = referenceMesh;
                var referenceRenderer = referenceGo.AddComponent<MeshRenderer>(); referenceRenderer.sharedMaterial = originalMaterial;
                var surface = new SceneForwardSurface { renderer = renderer, inputs = input, cull = CullMode.Off }; settings.surfaces = new[] { surface };
                var fx = new LowResolutionFxSurface { renderer = renderer, opacity = 1, fog = false, resolution = FxResolution.Full, cull = CullMode.Off,
                    lighting = new FxSurfaceLighting { enabled = true, inputs = input } };
                var geometry = new LowResolutionFxSettings { enabled = true, surfaces = new[] { fx }, lighting = settings };
                var joint = new HeavyFxSettings { enabled = true, geometry = geometry };
                // The oracle supplies world-space shading normals as NORMAL,
                // so it never takes the normal-map/TANGENT branch under test.
                void Reference(Matrix4x4 combined, Vector3 n, Vector3 t, float w, Vector3 map, bool omitBitangent = false, Vector3[] positions = null)
                {
                    var worldN = combined.inverse.transpose.MultiplyVector(n).normalized;
                    var transformedT = combined.MultiplyVector(t); var worldT = (transformedT - worldN * Vector3.Dot(worldN, transformedT)).normalized;
                    var worldB = Vector3.Cross(worldN, worldT) * w * (combined.determinant < 0 ? -1 : 1);
                    var mapped = (worldT * map.x + (omitBitangent ? Vector3.zero : worldB * map.y) + worldN * map.z).normalized;
                    referenceMesh.vertices = (positions ?? original).Select(combined.MultiplyPoint3x4).ToArray(); referenceMesh.normals = Enumerable.Repeat(mapped, 4).ToArray(); referenceMesh.RecalculateBounds();
                }
                Color[] CameraDraw(bool reference, Vector3 vertexScale)
                {
                    stage.enabled = true; surface.renderer = reference ? referenceRenderer : renderer; surface.vertexScale = reference ? Vector3.one : vertexScale;
                    input.normalMap = reference ? null : normalMap; camera.Render();
                    if (stage.UnavailableReason != null) throw new InvalidOperationException(stage.UnavailableReason);
                    var pixels = ReadSceneTarget(target); stage.enabled = false; return pixels;
                }
                Color[] FxDraw(bool reference, bool explicitMesh, bool heavyMode, Matrix4x4 matrix, FxResolution resolution, out Color[] batch)
                {
                    stage.enabled = false; fx.resolution = resolution; input.normalMap = reference ? null : normalMap;
                    fx.renderer = explicitMesh ? null : reference ? referenceRenderer : renderer;
                    fx.mesh = explicitMesh ? reference ? referenceMesh : mesh : null; fx.localToWorld = reference ? Matrix4x4.identity : matrix;
                    RenderTexture output; RenderTexture work = null, range;
                    if (heavyMode)
                    {
                        if (!heavy.TryRender(source, new FogVolumeDepth(depth), camera, joint, 0, out var frame)) throw new InvalidOperationException(heavy.UnavailableReason);
                        output = frame.color; if (resolution != FxResolution.Full) frame.TryGetLastBatch(resolution, out work, out range);
                    }
                    else
                    {
                        if (!low.TryRender(source, new FogVolumeDepth(depth), camera, geometry, out var frame)) throw new InvalidOperationException(low.UnavailableReason);
                        output = frame.color; if (resolution != FxResolution.Full) frame.TryGetLastBatch(resolution, out work, out range);
                    }
                    batch = work == null ? null : ReadSceneTarget(work); return ReadSceneTarget(output);
                }
                stage.enabled = false; camera.Render(); Graphics.Blit(target, source); var sourcePixels = ReadSceneTarget(source);
                for (int transform = 0; transform < 8; transform++)
                foreach (float tangentSign in new[] { -1f, 1f })
                foreach (float mapY in new[] { -.65f, 0f, .65f })
                {
                    var map = new Vector3(.23f, mapY, .83f).normalized;
                    normalMap.SetPixel(0, 0, new Color(map.x * .5f + .5f, map.y * .5f + .5f, map.z * .5f + .5f, 1)); normalMap.Apply();
                    mesh.tangents = Enumerable.Repeat(new Vector4(objectTangent.x, objectTangent.y, objectTangent.z, tangentSign), 4).ToArray();
                    var objectScale = new Vector3(transform % 2 == 0 ? 2.7f : -2.7f, 2.1f, .9f);
                    var vertexScale = new Vector3((transform & 2) == 0 ? 1.1f : -1.1f, (transform & 4) == 0 ? .9f : -.9f, 1.3f);
                    go.transform.SetPositionAndRotation(new Vector3(.17f, -.11f, .2f), Quaternion.Euler(13, 17, transform * 7)); go.transform.localScale = objectScale;
                    var combined = go.transform.localToWorldMatrix * Matrix4x4.Scale(vertexScale); Reference(combined, objectNormal, objectTangent, tangentSign, map);
                    settings.backend = SceneForwardLightBackend.Tiled;
                    string name = $"transform-{transform}-sign-{tangentSign}-mapY-{mapY}";
                    var actual = CameraDraw(false, vertexScale); var expected = CameraDraw(true, vertexScale); float error = PixelError(actual, expected);
                    Check("camera-independent-" + name, error <= .00003f, error);
                    Check("camera-positive-" + name, PixelError(expected, sourcePixels) > .01f);
                    settings.backend = SceneForwardLightBackend.BruteForce; var brute = CameraDraw(false, vertexScale);
                    Check("camera-brute-tiled-" + name, PixelError(brute, actual) <= .00002f); settings.backend = SceneForwardLightBackend.Tiled;
                    if (transform == 0 && tangentSign == 1 && mapY > 0)
                    {
                        SaveSsrPreview("forward-basis-camera-actual", actual, width, height, false); SaveSsrPreview("forward-basis-camera-independent", expected, width, height, false);
                        Reference(combined, objectNormal, objectTangent, tangentSign, map, true); var missing = CameraDraw(true, vertexScale);
                        Check("independent-missing-bitangent-negative-control", PixelError(expected, missing) > .02f);
                        Check("current-rejects-missing-bitangent", PixelError(actual, missing) > .02f);
                        Reference(combined, objectNormal, objectTangent, tangentSign, map);
                        if (Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_BASIS") == "1")
                        {
                            FsrCaptureDrain(target); bool began = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                            try { CameraDraw(false, vertexScale); FsrCaptureDrain(target); } finally { if (began) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                            Check("requested-native-camera-capture", began && ended);
                        }
                    }
                    // FX's public mesh/matrix contract has no separate vertexScale.
                    // Compose that scale into the renderer/matrix instead of inventing one.
                    go.transform.localScale = Vector3.Scale(objectScale, vertexScale);
                    foreach (bool heavyMode in new[] { false, true })
                    foreach (bool explicitMesh in new[] { false, true })
                    foreach (FxResolution resolution in new[] { FxResolution.Full, FxResolution.Half, FxResolution.Quarter })
                    {
                        string label = $"{(heavyMode ? "heavy" : "low")}-{(explicitMesh ? "mesh" : "renderer")}-{resolution}-" + name;
                        actual = FxDraw(false, explicitMesh, heavyMode, combined, resolution, out var actualBatch);
                        expected = FxDraw(true, explicitMesh, heavyMode, combined, resolution, out var expectedBatch);
                        error = PixelError(actual, expected); Check("fx-independent-" + label, error <= .00005f, error);
                        if (resolution != FxResolution.Full) Check("fx-working-normal-" + label, actualBatch != null && expectedBatch != null && PixelError(actualBatch, expectedBatch) <= .00005f, actualBatch == null || expectedBatch == null ? 1 : PixelError(actualBatch, expectedBatch));
                        settings.backend = SceneForwardLightBackend.BruteForce; brute = FxDraw(false, explicitMesh, heavyMode, combined, resolution, out _);
                        Check("fx-brute-tiled-" + label, PixelError(actual, brute) <= .00002f); settings.backend = SceneForwardLightBackend.Tiled;
                        if (transform == 0 && tangentSign == 1 && mapY > 0 && resolution == FxResolution.Quarter && !explicitMesh)
                        {
                            SaveSsrPreview("forward-basis-" + (heavyMode ? "heavy" : "low") + "-actual", actual, width, height, false);
                            SaveSsrPreview("forward-basis-" + (heavyMode ? "heavy" : "low") + "-independent", expected, width, height, false);
                        }
                        bool captureCase = tangentSign == 1 && mapY > 0 &&
                            (transform == 0 && !explicitMesh && resolution == FxResolution.Quarter || transform == 1 && explicitMesh && resolution == FxResolution.Half);
                        if (captureCase && Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_BASIS") == "1")
                        {
                            FsrCaptureDrain(target); bool began = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                            try { FxDraw(false, explicitMesh, heavyMode, combined, resolution, out _); FsrCaptureDrain(target); }
                            finally { if (began) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                            Check("requested-native-fx-" + label, began && ended);
                        }
                    }
                }
                // Direction-sensitive GI exercises a second downstream consumer
                // of the corrected normal, independently of direct-light response.
                var gi = new SceneGiInput { source = SceneGiSource.Probe };
                gi.probe.AddAmbientLight(new Color(.3f, .2f, .1f));
                gi.probe.AddDirectionalLight(new Vector3(.2f, .8f, -.7f).normalized, new Color(.7f, .9f, .5f), 2);
                surface.gi = fx.lighting.gi = gi;
                settings.lightRadiance = settings.ambientIrradiance = Vector3.zero; settings.localLights.enabled = false;
                var probeMap = new Vector3(.23f, .65f, .83f).normalized;
                normalMap.SetPixel(0, 0, new Color(probeMap.x * .5f + .5f, probeMap.y * .5f + .5f, probeMap.z * .5f + .5f, 1)); normalMap.Apply();
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(11, 17, 23)); go.transform.localScale = new Vector3(2.7f, 2.1f, .9f);
                Reference(go.transform.localToWorldMatrix, objectNormal, objectTangent, 1, probeMap);
                foreach (int consumer in new[] { 0, 1, 2 })
                {
                    Color[] Draw(bool reference) => consumer == 0 ? CameraDraw(reference, Vector3.one) : FxDraw(reference, false, consumer == 2, go.transform.localToWorldMatrix, FxResolution.Full, out _);
                    var actual = Draw(false); var expected = Draw(true); float error = PixelError(actual, expected);
                    Check("probe-independent-" + consumer, error <= .00005f, error);
                    gi.source = SceneGiSource.None; var none = Draw(false); gi.source = SceneGiSource.Probe;
                    Check("probe-positive-" + consumer, PixelError(actual, none) > .01f);
                }
                // Normal-biased point/main shadow lookup uses the same corrected
                // normal. Compare against the independently authored normal mesh.
                var blocker = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); blocker.layer = 24;
                blocker.transform.position = new Vector3(-.1f, .1f, -1); blocker.transform.localScale = new Vector3(.53f, .81f, 1);
                var caster = new SceneShadowCaster { renderer = blocker.GetComponent<Renderer>(), cull = CullMode.Off };
                settings.localLights.enabled = true;
                var lamp = new SceneDecalLight { shape = SceneDecalLightShape.Point, position = new Vector3(0, 0, -2), range = 6, radiance = Vector3.one * 2 };
                settings.localLights.lights = new[] { lamp }; settings.localLights.shadows.tileResolution = 128; settings.localLights.shadows.casters = new[] { caster };
                lamp.shadow.normalBias = .013f;
                settings.mainLightShadow.origin = new Vector3(0, 0, -3); settings.mainLightShadow.halfSize = Vector2.one * 3;
                settings.mainLightShadow.resolution = 128; settings.mainLightShadow.farPlane = 12; settings.mainLightShadow.casters = new[] { caster }; settings.mainLightShadow.normalBias = .017f;
                settings.lightRadiance = Vector3.one;
                foreach (int consumer in new[] { 0, 1, 2 })
                {
                    Color[] Draw(bool reference) => consumer == 0 ? CameraDraw(reference, Vector3.one) : FxDraw(reference, false, consumer == 2, go.transform.localToWorldMatrix, FxResolution.Full, out _);
                    lamp.shadow.enabled = settings.mainLightShadow.enabled = false; var clear = Draw(false);
                    lamp.shadow.enabled = settings.mainLightShadow.enabled = true;
                    var actual = Draw(false); var expected = Draw(true); float error = PixelError(actual, expected);
                    Check("normal-biased-shadows-independent-" + consumer, error <= .00005f, error);
                    Check("normal-biased-shadows-positive-" + consumer, PixelError(actual, clear) > .01f);
                }
                lamp.shadow.enabled = settings.mainLightShadow.enabled = false;
                // Current native skin/blendshape, not a baked replacement. The
                // reference applies explicit bone rotation to its own mesh data.
                var skinGo = Own(new GameObject("Shared Forward current skin")); skinGo.layer = 25;
                var skin = skinGo.AddComponent<SkinnedMeshRenderer>(); var skinMesh = Own(Instantiate(mesh));
                skinMesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray(); skinMesh.bindposes = new[] { Matrix4x4.identity };
                var delta = new[] { new Vector3(.21f, .13f, .07f), Vector3.zero, new Vector3(-.07f, .11f, .03f), Vector3.zero };
                skinMesh.AddBlendShapeFrame("Current mapped receiver", 100, delta, new Vector3[4], new Vector3[4]);
                // Author immutable parity variants before binding. Mutating the
                // bound mesh's tangent.w does not refresh native skin input on
                // every backend; do not confuse stale input with TBN output.
                var negativeSkinMesh = Own(Instantiate(skinMesh));
                negativeSkinMesh.tangents = Enumerable.Repeat(new Vector4(objectTangent.x, objectTangent.y, objectTangent.z, -1), 4).ToArray();
                var bone = Own(new GameObject("Independent mapped receiver bone")).transform; skin.sharedMesh = skinMesh; skin.bones = new[] { bone }; skin.rootBone = bone;
                skin.sharedMaterial = originalMaterial; skin.updateWhenOffscreen = true; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 20); renderer = skin;
                Color[] first = null;
                for (int pose = 0; pose < 3; pose++)
                {
                    float sign = pose == 1 ? -1 : 1; skin.sharedMesh = sign < 0 ? negativeSkinMesh : skinMesh;
                    Check("native-bound-mesh-sign-" + pose, skin.sharedMesh.tangents.All(t => t.w == sign));
                    bone.SetPositionAndRotation(new Vector3(pose * .13f, pose * -.07f, 0), Quaternion.Euler(pose * 11, pose * 17, pose * 23)); skin.SetBlendShapeWeight(0, pose * 40);
                    Reference(bone.localToWorldMatrix, objectNormal, objectTangent, sign, probeMap, false, original.Select((p, i) => p + delta[i] * (pose * .4f)).ToArray());
                    yield return null; yield return null;
                    foreach (int consumer in new[] { 0, 1, 2 })
                    {
                        var actual = consumer == 0 ? CameraDraw(false, Vector3.one) : FxDraw(false, false, consumer == 2, Matrix4x4.identity, FxResolution.Full, out _);
                        var expected = consumer == 0 ? CameraDraw(true, Vector3.one) : FxDraw(true, false, consumer == 2, Matrix4x4.identity, FxResolution.Full, out _);
                        float error = PixelError(actual, expected); Check($"current-native-skin-pose-{pose}-consumer-{consumer}", error <= .00005f, error);
                        if (consumer == 0 && Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_BASIS") == "1")
                        {
                            FsrCaptureDrain(target); bool began = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                            try { CameraDraw(false, Vector3.one); FsrCaptureDrain(target); }
                            finally { if (began) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                            Check("requested-native-skin-capture-" + pose, began && ended);
                        }
                        if (consumer == 0) { if (first == null) first = actual; else Check("current-native-pose-change-" + pose, PixelError(first, actual) > .01f); }
                    }
                }
                Check("source-and-shared-material-unchanged", PixelError(ReadSceneTarget(source), sourcePixels) == 0 && renderer.sharedMaterial == originalMaterial);
            }
            finally
            {
                RenderTexture.active = active;
                for (int i = 0; i < previous.Length; i++) if (previous[i]) previous[i].forceRenderingOff = forced[i];
                foreach (var item in _owned) if (item) DestroyImmediate(item); _owned.Clear();
            }
        }
    }
}
