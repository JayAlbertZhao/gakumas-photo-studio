using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifySceneLightShadows(Report report)
        {
            yield return null;
            var previous = FindObjectsOfType<Renderer>(); var forced = new bool[previous.Length];
            for (int i = 0; i < previous.Length; i++) { forced[i] = previous[i].forceRenderingOff; previous[i].forceRenderingOff = true; }
            try
            {
                void Check(string label, bool accepted, float error = 0) => FrameworkCheck(report, "scene-light-shadow-" + label, accepted, error);
                var host = Own(new GameObject("Light source shadow host")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
                camera.transform.position = new Vector3(0, 0, -4); camera.orthographic = true; camera.orthographicSize = 2; camera.aspect = 1;
                camera.nearClipPlane = .1f; camera.farClipPlane = 40; camera.cullingMask = 1 << 26;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                var target = Own(new RenderTexture(129, 129, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var stage = host.AddComponent<SceneDeferredCamera>(); stage.sceneEnabled = true; stage.sceneLayers = 1 << 25;
                stage.lightRadiance = stage.ambientIrradiance = Vector3.zero; stage.giBaseScale = 0;
                var receiver = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); receiver.layer = 25; receiver.transform.localScale = Vector3.one * 3.8f;
                var receiverMesh = Own(Instantiate(receiver.GetComponent<MeshFilter>().sharedMesh)); receiverMesh.uv2 = receiverMesh.uv; receiver.GetComponent<MeshFilter>().sharedMesh = receiverMesh;
                var surface = new SceneDeferredCamera.Surface { renderer = receiver.GetComponent<Renderer>(), cull = CullMode.Off };
                surface.inputs.albedo = new Vector3(.5f, .4f, .3f); surface.inputs.mos = new Vector3(.1f, 1, .4f);
                surface.inputs.emission = new Vector3(.12f, .08f, .04f); stage.surfaces = new[] { surface };
                var occluder = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); occluder.layer = 24;
                occluder.transform.position = new Vector3(.15f, .2f, -1); occluder.transform.localScale = new Vector3(.45f, .6f, 1);
                var caster = new SceneShadowCaster { renderer = occluder.GetComponent<Renderer>(), cull = CullMode.Off };
                var borrowed = caster.renderer.sharedMaterial; var borrowedMesh = occluder.GetComponent<MeshFilter>().sharedMesh;
                var light = new SceneDecalLight { shape = SceneDecalLightShape.Spot, position = new Vector3(0, 0, -2), range = 5,
                    spotInnerAngle = 90, spotOuterAngle = 90, radiance = new Vector3(3, 2, 1) };
                var options = stage.decalLighting; options.enabled = true; options.allowInstancingFallback = false; options.lights = new[] { light };
                options.shadows.casters = new[] { caster }; light.shadow.depthBias = .001f;
                SceneDeferredCamera.Frame Render() { camera.Render(); if (!stage.TryGetFrame(out var f)) throw new InvalidOperationException(stage.UnavailableReason); return f; }
                Color[] Pixels() => ReadSceneTarget(target);
                Vector3 Rgb(Color c) => new Vector3(c.r, c.g, c.b);
                float Error(Vector3 a, Vector3 b) => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z));
                options.enabled = false; Render(); var original = Pixels(); options.enabled = true;
                var unshadowedFrame = Render(); var unshadowed = Pixels();
                Check("default-does-not-allocate", unshadowedFrame.lightShadowAtlas == null && stage.LightShadowMapCount == 0);
                Transform deformBone = null;

                // Pair the independently tested light BRDF with a geometric CPU visibility oracle.
                // Exclude only a narrow raster edge band; every interior covered pixel is compared.
                int Oracle(string name, bool expectShadow = true)
                {
                    bool enabled = light.shadow.enabled; light.shadow.enabled = false; Render(); var lit = Pixels();
                    options.enabled = false; Render(); var basis = Pixels(); options.enabled = true; light.shadow.enabled = enabled;
                    var frame = Render(); var actual = Pixels(); var coverage = ReadSceneTarget(frame.albedoCoverage);
                    // All fixture vertices have unit weight on this bone, identity bindpose.
                    // Renderer.localToWorldMatrix may be Unity's skin draw basis; multiplying
                    // it by the bone again is not an independent skeletal geometry oracle.
                    var geometryToWorld = deformBone != null ? deformBone.localToWorldMatrix : caster.renderer.localToWorldMatrix;
                    var inverse = (geometryToWorld * Matrix4x4.Scale(caster.vertexScale)).inverse;
                    var localOrigin = inverse.MultiplyPoint(light.position); float maximum = 0; int count = 0, dark = 0, clear = 0;
                    int mismatches = 0; float worstEdgeDistance = 0; var expectedPixels = new Color[actual.Length];
                    for (int y = 0; y < 129; y++) for (int x = 0; x < 129; x++)
                    {
                        int index = y * 129 + x; if (coverage[index].a < .5f) continue;
                        var ray = camera.ViewportPointToRay(new Vector3((x + .5f) / 129, (y + .5f) / 129));
                        var plane = new Plane(Vector3.forward, Vector3.zero); if (!plane.Raycast(ray, out float distance)) continue;
                        var world = ray.GetPoint(distance); var localEnd = inverse.MultiplyPoint(world);
                        float t = -localOrigin.z / (localEnd.z - localOrigin.z); var hit = Vector3.LerpUnclamped(localOrigin, localEnd, t);
                        if (Mathf.Abs(Mathf.Abs(hit.x) - .5f) < .05f || Mathf.Abs(Mathf.Abs(hit.y) - .5f) < .05f) continue;
                        var worldHit = Vector3.LerpUnclamped(light.position, world, t);
                        float axial = Vector3.Dot(worldHit - light.position, light.rotation.normalized * Vector3.forward);
                        bool blocked = expectShadow && caster.renderer.enabled && !caster.renderer.forceRenderingOff && caster.renderer.gameObject.activeInHierarchy &&
                            t > 0 && t < 1 && Mathf.Abs(hit.x) < .5f && Mathf.Abs(hit.y) < .5f && axial >= light.shadow.nearPlane && axial <= light.range;
                        float visibility = blocked ? 1 - light.shadow.strength : 1;
                        var expected = Rgb(basis[index]) + (Rgb(lit[index]) - Rgb(basis[index])) * visibility;
                        expectedPixels[index] = new Color(expected.x, expected.y, expected.z, 1);
                        if (Error(expected, Rgb(actual[index])) > .003f)
                        { mismatches++; worstEdgeDistance = Mathf.Max(worstEdgeDistance, Mathf.Min(Mathf.Abs(Mathf.Abs(hit.x) - .5f), Mathf.Abs(Mathf.Abs(hit.y) - .5f))); }
                        maximum = Mathf.Max(maximum, Error(expected, Rgb(actual[index]))); count++;
                        if (blocked) dark++; else clear++;
                    }
                    Check(name + "-ray-visibility-interiors", maximum < .003f && count > 8000 && clear > 1000 && (!expectShadow || dark > 30), maximum);
                    if (mismatches > 0) Debug.Log("[ShadowOracle] " + name + " mismatches=" + mismatches + " maxLocalEdgeDistance=" + worstEdgeDistance + " geometryToWorld=" + geometryToWorld);
                    if (name.StartsWith("moving-skinned")) SaveSsrPreview("scene-shadow-" + name + "-expected-interiors", expectedPixels, 129, 129, false);
                    return dark;
                }
                light.shadow.enabled = true;
                foreach (var backend in new[] { SceneDecalLightBackend.Scalar, SceneDecalLightBackend.Instanced })
                {
                    options.backend = backend; string label = backend.ToString(); Oracle(label);
                    var frame = Render(); var depth = ReadSceneTarget(frame.lightShadowAtlas); int occupied = 0; float error = 0;
                    foreach (var pixel in depth) if (pixel.r < .9f) { occupied++; error = Mathf.Max(error, Mathf.Abs(pixel.r - .2f)); }
                    Check(label + "-actual-light-view-depth", occupied > 100 && error < .00001f && stage.LightShadowCasterDrawCalls == 1, error);
                    SaveSsrPreview("scene-shadow-" + label, Pixels(), 129, 129, false);
                    occluder.transform.position += new Vector3(.4f, -.25f, 0); Oracle(label + "-moving-occluder"); occluder.transform.position -= new Vector3(.4f, -.25f, 0);
                    light.position += new Vector3(-.4f, .1f, 0); Oracle(label + "-moving-light"); light.position -= new Vector3(-.4f, .1f, 0);
                    light.rotation = Quaternion.Euler(8, -12, 0); Oracle(label + "-off-axis-projection"); light.rotation = Quaternion.identity;
                    light.shadow.strength = .4f; Oracle(label + "-fractional-strength"); light.shadow.strength = 1;
                    caster.renderer.enabled = false; Oracle(label + "-disabled-caster", false); caster.renderer.enabled = true;
                    caster.renderer.forceRenderingOff = true; Oracle(label + "-force-off-caster", false); caster.renderer.forceRenderingOff = false;
                    occluder.SetActive(false); Oracle(label + "-inactive-caster", false); occluder.SetActive(true);
                    options.shadows.casters = Array.Empty<SceneShadowCaster>(); Oracle(label + "-no-casters", false); options.shadows.casters = new[] { caster };
                    light.shadow.nearPlane = 1.2f; Oracle(label + "-near-excludes-occluder", false); light.shadow.nearPlane = .05f;
                    occluder.transform.position += Vector3.forward * 7; Oracle(label + "-far-excludes-occluder", false); occluder.transform.position -= Vector3.forward * 7;
                    caster.alpha = 0; caster.cutoff = .5f; Oracle(label + "-alpha-cutout-empty", false); caster.alpha = 1; caster.cutoff = 0;
                    caster.vertexScale = new Vector3(1.7f, .7f, 1); Oracle(label + "-explicit-vertex-scale"); caster.vertexScale = Vector3.one;
                    occluder.transform.rotation = Quaternion.Euler(25, -15, 20); Oracle(label + "-sloped-caster-perspective-depth"); occluder.transform.rotation = Quaternion.identity;
                    caster.cull = CullMode.Back; Oracle(label + "-backface-culling"); caster.cull = CullMode.Front; Oracle(label + "-frontface-culling", false); caster.cull = CullMode.Off;
                    light.shadow.depthBias = 1.1f; Oracle(label + "-world-depth-bias", false); light.shadow.depthBias = .001f;
                    light.shadow.normalBias = 1.1f; Oracle(label + "-world-normal-bias", false); light.shadow.normalBias = 0;
                    light.shadow.filter = SceneShadowFilter.Pcf3x3; Oracle(label + "-pcf-interiors");
                    var soft = Pixels(); light.shadow.filter = SceneShadowFilter.Hard; Render(); var hard = Pixels();
                    Check(label + "-pcf-changes-real-shadow-edge", PixelError(soft, hard) > .01f);
                }
                options.backend = SceneDecalLightBackend.Instanced;
                var nearObject = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); nearObject.layer = 24;
                nearObject.transform.position = new Vector3(.075f, .1f, -1.5f); nearObject.transform.localScale = new Vector3(.225f, .3f, 1);
                var nearCaster = new SceneShadowCaster { renderer = nearObject.GetComponent<Renderer>(), cull = CullMode.Off };
                options.shadows.casters = new[] { caster, nearCaster }; var nearFrame = Render(); var nearDepth = ReadSceneTarget(nearFrame.lightShadowAtlas);
                float nearestError = 0; int nearestPixels = 0;
                foreach (var pixel in nearDepth) if (pixel.r < .9f) { nearestPixels++; nearestError = Mathf.Max(nearestError, Mathf.Abs(pixel.r - .1f)); }
                Check("nearest-geometry-wins-hardware-depth", nearestPixels > 100 && nearestError < .00001f, nearestError);
                options.shadows.casters = new[] { nearCaster, caster }; nearFrame = Render();
                Check("reversed-draw-order-keeps-nearest-depth", ScenePixelsEqual(nearDepth, ReadSceneTarget(nearFrame.lightShadowAtlas)));
                options.shadows.casters = new[] { caster }; nearObject.SetActive(false);
                camera.orthographic = false; camera.fieldOfView = 50; Oracle("perspective-host"); camera.orthographic = true;
                var alphaMap = Own(new Texture2D(2, 1, TextureFormat.RGBAFloat, false, true)); alphaMap.filterMode = FilterMode.Point; alphaMap.wrapMode = TextureWrapMode.Clamp;
                alphaMap.SetPixels(new[] { new Color(1,1,1,0), Color.white }); alphaMap.Apply(); caster.alphaMap = alphaMap; caster.cutoff = .5f;
                Render(); var cut = Pixels(); caster.alphaMap = null; caster.cutoff = 0; Render(); var solid = Pixels();
                int changed = 0; for (int i = 0; i < solid.Length; i++) if (cut[i].r > solid[i].r + .01f) changed++;
                Check("texture-alpha-removes-half-caster", changed > 200 && changed < 900);
                caster.alphaMap = alphaMap; caster.cutoff = .5f; caster.uvST = new Vector4(-1, 1, 1, 0); Render();
                Check("cutout-uv-transform-moves-hole", PixelError(cut, Pixels()) > .1f); caster.alphaMap = null; caster.cutoff = 0; caster.uvST = new Vector4(1, 1, 0, 0);

                var giMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); giMap.SetPixel(0, 0, new Color(2, 1, .5f, 1)); giMap.Apply();
                surface.gi = new SceneGiInput { source = SceneGiSource.Lightmap, lightmap = giMap }; stage.giBaseScale = .4f; light.giWeight = 1;
                Oracle("gi-modulated-direct-with-base-gi-and-emission-preserved"); light.diffuseScale = 0; Oracle("specular-shadow"); light.diffuseScale = 1; light.specularScale = 0; Oracle("diffuse-shadow");
                var normals = receiverMesh.normals; var inverseNormals = new Vector3[normals.Length]; for (int i = 0; i < normals.Length; i++) inverseNormals[i] = -normals[i];
                receiverMesh.normals = inverseNormals; light.backlightScale = .8f; Oracle("inverse-diffuse-shadow"); receiverMesh.normals = normals; light.backlightScale = 0; light.specularScale = 1;
                surface.gi.source = SceneGiSource.None; stage.giBaseScale = 0; light.giWeight = 0;

                var skinHost = Own(new GameObject("Moving shadow skin")); skinHost.layer = 24;
                // Uniform root scale: this oracle models bone world matrices, not Unity's
                // decomposition of rotated bones under a non-uniformly scaled parent.
                skinHost.transform.position = occluder.transform.position; skinHost.transform.localScale = Vector3.one * .6f;
                var bone = Own(new GameObject("Shadow bone")).transform; bone.SetParent(skinHost.transform, false);
                var skin = skinHost.AddComponent<SkinnedMeshRenderer>(); var skinMesh = Own(Instantiate(borrowedMesh));
                var weights = new BoneWeight[skinMesh.vertexCount]; for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
                skinMesh.boneWeights = weights; skinMesh.bindposes = new[] { Matrix4x4.identity }; skin.sharedMesh = skinMesh; skin.bones = new[] { bone }; skin.rootBone = bone;
                skin.sharedMaterial = borrowed; skin.updateWhenOffscreen = true; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 5);
                var skinControl = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); skinControl.layer = 24;
                skinControl.transform.position = skinHost.transform.position; skinControl.transform.localScale = skinHost.transform.localScale;
                var controlMesh = Own(Instantiate(borrowedMesh)); skinControl.GetComponent<MeshFilter>().sharedMesh = controlMesh;
                var originalCasterRenderer = caster.renderer; caster.renderer = skin; deformBone = bone; Color[] skinInitial = null;
                for (int i = 0; i < 3; i++)
                {
                    bone.localPosition = new Vector3(i * .45f, -i * .2f, 0); bone.localRotation = Quaternion.Euler(0, 0, i * 20);
                    yield return null; Oracle("moving-skinned-caster-" + i);
                    if (i == 0) skinInitial = Pixels(); else Check("bone-motion-changes-shadow-" + i, PixelError(skinInitial, Pixels()) > .1f);
                    var skinPixels = Pixels(); var skinDepth = ReadSceneTarget(Render().lightShadowAtlas);
                    var vertices = borrowedMesh.vertices; var deformation = Matrix4x4.TRS(bone.localPosition, bone.localRotation, bone.localScale);
                    for (int v = 0; v < vertices.Length; v++) vertices[v] = deformation.MultiplyPoint(vertices[v]); controlMesh.vertices = vertices; controlMesh.RecalculateBounds();
                    caster.renderer = skinControl.GetComponent<Renderer>(); var controlFrame = Render(); var controlDepth = ReadSceneTarget(controlFrame.lightShadowAtlas);
                    Check("skinned-depth-equals-cpu-deformed-static-mesh-" + i, PixelError(skinDepth, controlDepth) < .00001f, PixelError(skinDepth, controlDepth));
                    Check("skinned-shadow-equals-cpu-deformed-static-mesh-" + i, PixelError(skinPixels, Pixels()) < .003f, PixelError(skinPixels, Pixels()));
                    caster.renderer = skin;
                    if (i == 2 && Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_LIGHT_SHADOW_SKIN") == "1")
                    {
                        bool started = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                        try
                        {
                            if (started)
                            {
                                Render(); Pixels(); caster.renderer = skinControl.GetComponent<Renderer>(); Render(); Pixels(); caster.renderer = skin;
                                for (int v = 0; v < vertices.Length; v++) Debug.Log("[ShadowSkinVertex] " + skinControl.transform.TransformPoint(vertices[v]).ToString("F6"));
                            }
                        }
                        finally { if (started) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                        Check("requested-native-skin-capture", started && ended);
                    }
                }
                Check("borrowed-skin-state-preserved", skin.sharedMesh == skinMesh && skin.sharedMaterial == borrowed && skin.updateWhenOffscreen && !skin.HasPropertyBlock());
                SaveSsrPreview("scene-shadow-skinned", Pixels(), 129, 129, false);
                caster.renderer = originalCasterRenderer; deformBone = null; skinHost.SetActive(false); skinControl.SetActive(false);

                // Four distinct tiles, then mixed shadowed/unshadowed lights and batch offsets.
                var lights = new SceneDecalLight[5]; lights[0] = light;
                for (int i = 1; i < 5; i++) lights[i] = new SceneDecalLight { shape = SceneDecalLightShape.Spot,
                    position = new Vector3((i - 2) * .4f, (i % 2) * .6f - .3f, -2 - i * .25f), range = 5,
                    spotInnerAngle = 90, spotOuterAngle = 90, radiance = new Vector3(.3f * i, .2f, .1f),
                    shadow = new SceneLightShadowInput { enabled = i < 4, filter = SceneShadowFilter.Pcf3x3 } };
                Color[] sum = null;
                foreach (var value in lights)
                {
                    options.lights = new[] { value }; Render(); var pixels = Pixels();
                    if (sum == null) sum = pixels;
                    else for (int i = 0; i < sum.Length; i++) sum[i] += pixels[i] - original[i];
                }
                options.lights = lights;
                foreach (var backend in new[] { SceneDecalLightBackend.Scalar, SceneDecalLightBackend.Instanced })
                {
                    options.backend = backend; options.batchSize = 2; var frame = Render();
                    Check(backend + "-four-tile-atlas-equals-isolated-light-sum", stage.LightShadowMapCount == 4 && frame.lightShadowAtlas.width == 512 && PixelError(sum, Pixels()) < .004f, PixelError(sum, Pixels()));
                    Check(backend + "-mixed-shadow-state-and-batch-offsets", stage.SubmittedLights == 5 && stage.LightShadowCasterDrawCalls == 4 && stage.LightDrawCalls == (backend == SceneDecalLightBackend.Scalar ? 5 : 3));
                }
                if (Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_LIGHT_SHADOWS") == "1")
                {
                    bool started = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                    try { if (started) { Render(); Pixels(); } } finally { if (started) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                    Check("requested-native-capture", started && ended);
                }
                SaveSsrPreview("scene-shadow-four-tiles", Pixels(), 129, 129, false);
                options.lights = new[] { light }; options.batchSize = 256;
                var oldFrame = Render(); var oldAtlas = oldFrame.lightShadowAtlas; options.shadows.tileResolution = 128;
                Render(); Check("atlas-resize-invalidates-borrowed-frame", !oldFrame.IsCurrent && !oldAtlas.IsCreated()); options.shadows.tileResolution = 256;
                var lost = Render().lightShadowAtlas; lost.Release(); var recreated = Render(); Check("lost-atlas-recreated", recreated.lightShadowAtlas.IsCreated() && recreated.lightShadowAtlas != lost);
                void Reject(string label) { camera.Render(); Check(label, !stage.TryGetFrame(out _) && stage.LightShadowMapCount == 0 && stage.LightTargetCount == 0); }
                foreach (var shape in new[] { SceneDecalLightShape.Point, SceneDecalLightShape.Capsule, SceneDecalLightShape.Area })
                { light.shape = shape; Reject("unsupported-" + shape + "-shadow-fails-closed"); } light.shape = SceneDecalLightShape.Spot;
                light.position = new Vector3(100, 0, -2); light.shadow.strength = float.NaN; Reject("invalid-shadow-rejected-before-volume-cull"); light.position = new Vector3(0, 0, -2); light.shadow.strength = 1;
                light.shadow.nearPlane = light.range; Reject("near-equals-far-rejected"); light.shadow.nearPlane = .05f;
                light.shadow.depthBias = -1; Reject("negative-bias-rejected"); light.shadow.depthBias = .001f;
                light.shadow.normalBias = float.PositiveInfinity; Reject("nonfinite-normal-bias-rejected"); light.shadow.normalBias = 0;
                light.shadow.filter = (SceneShadowFilter)99; Reject("unknown-filter-rejected"); light.shadow.filter = SceneShadowFilter.Hard;
                options.shadows.tileResolution = 33; Reject("non-power-of-two-tile-rejected"); options.shadows.tileResolution = 256;
                options.lights = lights; options.shadows.maxShadowedLights = 2; Reject("shadow-budget-rejected-not-silently-dropped"); options.shadows.maxShadowedLights = 16; options.lights = new[] { light };
                var block = new MaterialPropertyBlock(); block.SetFloat("_Unrelated", 1); caster.renderer.SetPropertyBlock(block); Reject("caster-property-block-rejected"); caster.renderer.SetPropertyBlock(null);
                caster.materialIndex = 1; Reject("missing-submesh-rejected"); caster.materialIndex = 0;
                caster.vertexScale = Vector3.zero; Reject("singular-caster-scale-rejected"); caster.vertexScale = Vector3.one;
                light.shadow.strength = 0; var zero = Render(); Check("zero-strength-no-shadow-allocation", zero.lightShadowAtlas == null && ScenePixelsEqual(unshadowed, Pixels())); light.shadow.strength = 1;
                light.shadow.enabled = false; Render(); Check("shadow-disabled-restores-entire-unshadowed-frame", ScenePixelsEqual(unshadowed, Pixels()));
                options.enabled = false; Render(); Check("lighting-disabled-restores-entire-baseline", ScenePixelsEqual(original, Pixels()) && stage.LightShadowMapCount == 0);
                Check("borrowed-caster-mesh-material-and-state-unchanged", borrowed == caster.renderer.sharedMaterial && borrowedMesh == occluder.GetComponent<MeshFilter>().sharedMesh && !caster.renderer.HasPropertyBlock() && caster.renderer.enabled && !caster.renderer.forceRenderingOff);
                host.SetActive(false); receiver.SetActive(false); occluder.SetActive(false); camera.targetTexture = null;
            }
            finally
            {
                for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i];
                foreach (var value in _owned) if (value != null) Destroy(value);
                _owned.Clear();
            }
        }
    }
}
