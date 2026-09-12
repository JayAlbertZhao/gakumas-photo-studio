using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifySceneGi(Report report)
        {
            var previous = FindObjectsOfType<Renderer>(); var forced = new bool[previous.Length];
            for (int i = 0; i < previous.Length; i++) { forced[i] = previous[i].forceRenderingOff; previous[i].forceRenderingOff = true; }
            var oldMaps = LightmapSettings.lightmaps; var oldMode = LightmapSettings.lightmapsMode;
            try
            {
                void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "scene-gi-" + name, ok, error);
                var host = Own(new GameObject("Baked GI scene host")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
                camera.transform.position = new Vector3(0, 0, -4); camera.orthographic = true; camera.orthographicSize = 2; camera.aspect = 1;
                camera.nearClipPlane = .1f; camera.farClipPlane = 30; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.cullingMask = 1 << 26;
                var target = Own(new RenderTexture(129, 129, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var stage = host.AddComponent<SceneDeferredCamera>(); stage.sceneEnabled = true; stage.sceneLayers = 1 << 25;
                stage.lightRadiance = Vector3.zero; stage.ambientIrradiance = new Vector3(.2f, .1f, .3f);
                var receiver = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); receiver.layer = 25; receiver.transform.localScale = new Vector3(3.8f, 3.8f, 1);
                var renderer = receiver.GetComponent<Renderer>(); var shared = renderer.sharedMaterial;
                var mesh = Own(Instantiate(receiver.GetComponent<MeshFilter>().sharedMesh)); receiver.GetComponent<MeshFilter>().sharedMesh = mesh;
                var uv2 = new Vector2[mesh.vertexCount]; for (int i = 0; i < uv2.Length; i++) uv2[i] = new Vector2(.1f, .7f); mesh.uv2 = uv2;
                var surface = new SceneDeferredCamera.Surface { renderer = renderer, cull = CullMode.Off };
                surface.inputs.albedo = new Vector3(.4f, .6f, .2f); surface.inputs.mos = new Vector3(.25f, .5f, .4f); surface.inputs.emission = new Vector3(.1f, .2f, .3f); stage.surfaces = new[] { surface };
                SceneDeferredCamera.Frame Render() { camera.Render(); if (!stage.TryGetFrame(out var f)) throw new InvalidOperationException(stage.UnavailableReason); return f; }
                Color Pixel(RenderTexture rt, float u = .5f, float v = .5f) => ReadSceneTarget(rt)[Mathf.Clamp((int)(v * rt.height), 0, rt.height - 1) * rt.width + Mathf.Clamp((int)(u * rt.width), 0, rt.width - 1)];
                Vector3 Rgb(Color c) => new Vector3(c.r, c.g, c.b);
                float Error(Vector3 a, Vector3 b) => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z));
                var initial = Render(); var baseline = ReadSceneTarget(target);
                var albedo = Rgb(Pixel(initial.albedoCoverage)); var mos = Rgb(Pixel(initial.mosDepth)); var emission = Rgb(Pixel(initial.emission));
                Check("default-no-fifth-target", stage.GiTargetCount == 0 && initial.bakedDiffuseGi == null);
                Vector3 Base(Vector3 gi) => Vector3.Scale(albedo, gi) * ((1 - mos.x) * mos.y * stage.giBaseScale);
                Vector3 Front(Vector3 incident, float diffuse = 1, float specular = 1)
                {
                    Vector3 f0 = Vector3.Lerp(Vector3.one * .04f, albedo, mos.x);
                    float a2 = Mathf.Pow(Mathf.Max(1 - mos.z, .045f), 4);
                    return Vector3.Scale(Vector3.Scale(Vector3.one - f0, albedo) * ((1 - mos.x) / Mathf.PI) * diffuse + f0 * (specular / (4 * Mathf.PI * a2)), incident);
                }
                Vector3 Back(Vector3 incident, float scale) => Vector3.Scale(Vector3.Scale(Vector3.one - Vector3.Lerp(Vector3.one * .04f, albedo, mos.x), albedo), incident) * ((1 - mos.x) / Mathf.PI * scale);
                var map = Own(new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true)); map.filterMode = FilterMode.Point; map.wrapMode = TextureWrapMode.Clamp;
                map.SetPixels(new[] { new Color(1, 2, 3, 0), new Color(2, 1, .5f, .25f), new Color(.25f, .5f, 2, .5f), new Color(3, 2, 1, 1) }); map.Apply();
                var gi = surface.gi; gi.source = SceneGiSource.Lightmap; gi.lightmap = map;
                var frame = Render(); Vector3 bake = new Vector3(.25f, .5f, 2);
                Check("lightmap-uses-uv2-not-material-uv", Error(Rgb(Pixel(frame.bakedDiffuseGi)), bake) == 0 && Pixel(frame.bakedDiffuseGi).a == 1 && stage.GiTargetCount == 1);
                Check("baked-base-replaces-ambient-without-extra-pi", Error(Rgb(Pixel(target)), emission + Base(bake)) < .003f);
                Check("gi-target-uncovered-pixels-invalid", Pixel(frame.bakedDiffuseGi, 0, 0).a == 0);
                gi.lightmapST = new Vector4(1, 1, .5f, -.5f); frame = Render(); bake = new Vector3(2, 1, .5f);
                Check("lightmap-independent-scale-offset", Error(Rgb(Pixel(frame.bakedDiffuseGi)), bake) == 0 && Error(Rgb(Pixel(target)), emission + Base(bake)) < .003f);
                surface.inputs.uvST = new Vector4(3, 4, .2f, .1f); Render(); Check("material-uv-does-not-move-lightmap", Error(Rgb(Pixel(target)), emission + Base(bake)) < .003f);
                foreach (float scale in new[] { 0f, .1f, 1f, 2f }) { stage.giBaseScale = scale; Render(); Check("base-gi-scale-" + scale, Error(Rgb(Pixel(target)), emission + Base(bake)) < .004f); }
                stage.giBaseScale = 0; stage.lightRadiance = new Vector3(2, 3, 4);
                foreach (float weight in new[] { 0f, .5f, 1f })
                {
                    stage.directionalGiWeight = weight; Render();
                    Check("directional-gi-multiplier-" + weight, Error(Rgb(Pixel(target)), emission + Vector3.Scale(Front(stage.lightRadiance), Vector3.Lerp(Vector3.one, bake, weight))) < .005f);
                }
                stage.directionalDiffuseScale = 0; Render(); Check("directional-specular-only-multiplied", Error(Rgb(Pixel(target)), emission + Vector3.Scale(Front(stage.lightRadiance, 0, 1), bake)) < .004f);
                stage.directionalDiffuseScale = 1; stage.directionalSpecularScale = 0; Render(); Check("directional-diffuse-only-multiplied", Error(Rgb(Pixel(target)), emission + Vector3.Scale(Front(stage.lightRadiance, 1, 0), bake)) < .004f); stage.directionalSpecularScale = 1;
                stage.lightDirection = Vector3.forward; Render(); Check("backlight-zero-is-dark", Error(Rgb(Pixel(target)), emission) == 0);
                stage.directionalBacklight = .4f; frame = Render(); var backPixels = ReadSceneTarget(target);
                Check("inverse-vector-diffuse-cpu", Error(Rgb(Pixel(target)), emission + Vector3.Scale(Back(stage.lightRadiance, .4f), bake)) < .004f);
                stage.directionalSpecularScale = 0; Render(); Check("backlight-does-not-create-specular", ScenePixelsEqual(backPixels, ReadSceneTarget(target))); stage.directionalSpecularScale = 1;
                Check("gi-never-modifies-emission-buffer", Error(Rgb(Pixel(frame.emission)), emission) == 0);
                SaveSsrPreview("scene-gi-backlight", ReadSceneTarget(target), 129, 129, false);
                stage.directionalBacklight = 0; stage.lightDirection = Vector3.back; stage.lightRadiance = Vector3.zero;
                var light = new SceneDecalLight { position = new Vector3(0, 0, -1), range = 2, radiance = new Vector3(2, 3, 4), giWeight = 1 };
                stage.decalLighting.enabled = true; stage.decalLighting.lights = new[] { light };
                foreach (var backend in new[] { SceneDecalLightBackend.Scalar, SceneDecalLightBackend.Instanced })
                {
                    stage.decalLighting.backend = backend;
                    foreach (var shape in new[] { SceneDecalLightShape.Point, SceneDecalLightShape.Capsule, SceneDecalLightShape.Area })
                    {
                        light.shape = shape; Render(); Check(backend + "-" + shape + "-baked-gi-direct-cpu", Error(Rgb(Pixel(target)), emission + Vector3.Scale(Front(light.radiance) * .5f, bake)) < .004f);
                    }
                    light.shape = SceneDecalLightShape.Point; light.position = Vector3.forward; light.backlightScale = .5f; Render();
                    Check(backend + "-inverse-diffuse-cpu", Error(Rgb(Pixel(target)), emission + Vector3.Scale(Back(light.radiance, .5f) * .5f, bake)) < .004f);
                    light.diffuseScale = 0; Render(); Check(backend + "-backlight-off-when-diffuse-disabled", Error(Rgb(Pixel(target)), emission) == 0);
                    light.diffuseScale = 1; light.backlightScale = 0; light.position = Vector3.back;
                }
                var monitorHost = Own(new GameObject("GI monitor producer")); var producer = monitorHost.AddComponent<Camera>(); producer.CopyFrom(camera); producer.enabled = false;
                producer.targetTexture = null; producer.transform.position = new Vector3(0, 0, -3); producer.cullingMask = 1 << 24; producer.orthographicSize = 1;
                var panel = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); panel.layer = 24; panel.transform.localScale = Vector3.one * 2;
                var panelMaterial = Own(new Material(Resources.Load<Shader>("MonitorEmission"))); panelMaterial.SetTexture("_MonitorTex", Texture2D.whiteTexture); panelMaterial.SetVector("_MonitorTint", new Vector3(4, 2, 1)); panel.GetComponent<Renderer>().sharedMaterial = panelMaterial;
                var monitor = monitorHost.AddComponent<HdrMonitor>(); monitor.monitorEnabled = true; monitor.width = monitor.height = 33;
                stage.decalLighting.monitor = monitor; Check("monitor-gi-content-published", monitor.TryUpdate(0, 0, out _)); frame = Render();
                Check("monitor-direct-light-times-baked-gi-cpu", Error(Rgb(Pixel(target)), emission + Vector3.Scale(Front(Vector3.Scale(light.radiance, new Vector3(4, 2, 1))) * .5f, bake)) < .008f);
                float firstRed = Pixel(target).r; panelMaterial.SetVector("_MonitorTint", new Vector3(8, 2, 1)); monitor.TryUpdate(0, 1, out _); frame = Render();
                Check("monitor-revision-retains-gi-and-emission", Mathf.Abs(Pixel(target).r - emission.x - 2 * (firstRed - emission.x)) < .008f && Error(Rgb(Pixel(frame.bakedDiffuseGi)), bake) == 0 && Error(Rgb(Pixel(frame.emission)), emission) == 0);
                SaveSsrPreview("scene-gi-monitor", ReadSceneTarget(target), 129, 129, false);
                stage.decalLighting.monitor = null; monitorHost.SetActive(false); panel.SetActive(false);
                var many = new SceneDecalLight[110]; for (int i = 0; i < many.Length; i++) many[i] = new SceneDecalLight {
                    shape = (SceneDecalLightShape)(i % 3), position = new Vector3((i % 11 - 5) * .12f, (i / 11 - 5) * .1f, -1),
                    range = 2, radiance = new Vector3(.02f, .03f, .01f), giWeight = 1, backlightScale = .2f
                };
                stage.decalLighting.lights = many; stage.decalLighting.backend = SceneDecalLightBackend.Scalar; Render(); var scalarGi = ReadSceneTarget(target);
                stage.decalLighting.backend = SceneDecalLightBackend.Instanced; Render();
                Check("110-gi-mixed-shapes-instanced-matches-scalar", stage.SubmittedLights == 110 && stage.LightDrawCalls == 1 && PixelError(scalarGi, ReadSceneTarget(target)) < .003f);
                stage.decalLighting.batchSize = 32; Render(); Check("gi-instance-offsets-match-full-image", stage.LightDrawCalls == 4 && PixelError(scalarGi, ReadSceneTarget(target)) < .003f); stage.decalLighting.batchSize = 256;
                if (Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SCENE_GI") == "1")
                {
                    bool started = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                    try { if (started) { Render(); ReadSceneTarget(target); } }
                    finally { if (started) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                    Check("requested-native-gi-and-instancing-capture", started && ended);
                }
                stage.decalLighting.lights = new[] { light }; gi.lightmap = Texture2D.blackTexture; Render();
                Check("valid-black-gi-suppresses-dynamic-light", Error(Rgb(Pixel(target)), emission) == 0);
                gi.lightmap = Texture2D.whiteTexture; Render(); Check("white-gi-is-neutral-direct-multiplier", Error(Rgb(Pixel(target)), emission + Front(light.radiance) * .5f) < .004f); gi.lightmap = map;
                gi.source = SceneGiSource.None; Render(); Check("missing-gi-is-unit-multiplier-not-black-alpha", Error(Rgb(Pixel(target)), emission + Front(light.radiance) * .5f + Vector3.Scale(albedo, stage.ambientIrradiance) * ((1 - mos.x) * mos.y / Mathf.PI)) < .004f && stage.GiTargetCount == 0);
                var extra = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); extra.layer = 25; extra.transform.position = new Vector3(1.3f, 0, -.1f); extra.transform.localScale = Vector3.one * .4f;
                var extraMesh = Own(Instantiate(mesh)); extra.GetComponent<MeshFilter>().sharedMesh = extraMesh;
                var extraSurface = new SceneDeferredCamera.Surface { renderer = extra.GetComponent<Renderer>(), gi = new SceneGiInput { source = SceneGiSource.Lightmap, lightmap = map } };
                stage.surfaces = new[] { surface, extraSurface }; frame = Render();
                Check("mixed-receiver-missing-gi-stays-invalid-in-allocated-target", stage.GiTargetCount == 1 && Pixel(frame.bakedDiffuseGi).a == 0 && Error(Rgb(Pixel(target)), emission + Front(light.radiance) * .5f + Vector3.Scale(albedo, stage.ambientIrradiance) * ((1 - mos.x) * mos.y / Mathf.PI)) < .004f);
                stage.surfaces = new[] { surface }; extra.SetActive(false);
                gi.source = SceneGiSource.Lightmap; stage.decalLighting.enabled = false;
                gi.encoding = SceneGiEncoding.Rgbm; gi.decodeMultiplier = 8; gi.decodeExponent = 2; frame = Render();
                Check("explicit-rgbm-alpha-exponent-decode", Error(Rgb(Pixel(frame.bakedDiffuseGi)), bake * .5f) == 0);
                gi.encoding = SceneGiEncoding.DoubleLdr; gi.decodeMultiplier = 4; frame = Render(); Check("explicit-dldr-decode", Error(Rgb(Pixel(frame.bakedDiffuseGi)), bake * 4) == 0);
                gi.encoding = SceneGiEncoding.LinearRgb;
                var direction = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); direction.SetPixel(0, 0, new Color(.5f, .5f, 0, .5f)); direction.Apply(); gi.directionality = direction;
                frame = Render(); Check("directional-lightmap-front-normal", Error(Rgb(Pixel(frame.bakedDiffuseGi)), bake * 2) == 0);
                receiver.transform.rotation = Quaternion.Euler(0, 180, 0); frame = Render(); Check("directional-lightmap-back-normal", Rgb(Pixel(frame.bakedDiffuseGi)).sqrMagnitude < .00001f); receiver.transform.rotation = Quaternion.identity; gi.directionality = null;
                LightmapSettings.lightmapsMode = LightmapsMode.NonDirectional; LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = map } };
                renderer.lightmapIndex = 0; renderer.lightmapScaleOffset = gi.lightmapST; gi.source = SceneGiSource.RendererLightmap; frame = Render();
                Check("renderer-bound-lightmap-index-and-st", Error(Rgb(Pixel(frame.bakedDiffuseGi)), bake) == 0);
                renderer.lightmapScaleOffset = new Vector4(1, 1, 0, 0); frame = Render(); Check("renderer-lightmap-transform-refresh", Error(Rgb(Pixel(frame.bakedDiffuseGi)), new Vector3(.25f, .5f, 2)) == 0);
                renderer.lightmapIndex = -1; camera.Render(); Check("missing-renderer-lightmap-fails-closed", !stage.TryGetFrame(out _) && stage.GiTargetCount == 0); renderer.lightmapIndex = 0;
                gi.source = SceneGiSource.Probe; var sh = new SphericalHarmonicsL2();
                sh.AddAmbientLight(new Color(.4f, .6f, .8f)); sh.AddDirectionalLight(Vector3.back, new Color(.3f, .2f, .1f), 2); gi.probe = sh;
                var normals = new[] { Vector3.back, Vector3.forward, Vector3.right, Vector3.up };
                foreach (var normal in normals)
                {
                    // Edge-on geometry cannot cover center. Keep geometry facing camera and supply a normal stream instead.
                    receiver.transform.rotation = Quaternion.identity; var ns = new Vector3[mesh.vertexCount]; for (int i = 0; i < ns.Length; i++) ns[i] = normal; mesh.normals = ns;
                    var colors = new Color[1]; sh.Evaluate(new[] { normal }, colors); frame = Render(); var expected = Vector3.Max(Rgb(colors[0]), Vector3.zero);
                    Check("probe-sh-direction-" + normal, Error(Rgb(Pixel(frame.bakedDiffuseGi)), expected) < .003f);
                }
                var backs = new Vector3[mesh.vertexCount]; for (int i = 0; i < backs.Length; i++) backs[i] = Vector3.back; mesh.normals = backs;
                gi.probe = default; frame = Render(); Check("black-probe-is-valid-dark-gi-not-missing", Pixel(frame.bakedDiffuseGi).a == 1 && Rgb(Pixel(frame.bakedDiffuseGi)) == Vector3.zero);
                sh[0, 0] = float.NaN; gi.probe = sh; camera.Render(); Check("nonfinite-sh-rejected", !stage.TryGetFrame(out _) && stage.GiTargetCount == 0);
                gi.source = SceneGiSource.SceneProbe;
                if (LightmapSettings.lightProbes == null || LightmapSettings.lightProbes.count == 0)
                { camera.Render(); Check("missing-baked-scene-probe-rejects-ambient-fallback", !stage.TryGetFrame(out _) && stage.GiTargetCount == 0); }
                gi.source = SceneGiSource.Lightmap; gi.lightmap = null; camera.Render(); Check("missing-map-rejected", !stage.TryGetFrame(out _)); gi.lightmap = map;
                mesh.uv2 = Array.Empty<Vector2>(); camera.Render(); Check("missing-uv2-rejected", !stage.TryGetFrame(out _)); mesh.uv2 = uv2;
                gi.lightmapST = new Vector4(float.NaN, 1, 0, 0); camera.Render(); Check("invalid-uv-transform-rejected", !stage.TryGetFrame(out _)); gi.lightmapST = new Vector4(1, 1, .5f, -.5f);
                gi.decodeExponent = 0; camera.Render(); Check("invalid-decode-rejected", !stage.TryGetFrame(out _)); gi.decodeExponent = 1;
                stage.giBaseScale = 1; frame = Render(); var oldGi = frame.bakedDiffuseGi; oldGi.Release(); Render();
                Check("lost-gi-target-recreated", stage.TryGetFrame(out frame) && frame.bakedDiffuseGi != oldGi && frame.bakedDiffuseGi.IsCreated());
                var small = Own(new RenderTexture(65, 33, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); small.Create(); camera.targetTexture = small; frame = Render();
                Check("gi-target-resizes-with-host", frame.bakedDiffuseGi.width == 65 && frame.bakedDiffuseGi.height == 33 && Error(Rgb(Pixel(small)), emission + Base(bake)) < .004f);
                camera.targetTexture = target; frame = Render(); var beforeDecalGi = ReadSceneTarget(frame.bakedDiffuseGi);
                var decal = new SceneDeferredCamera.Decal(); decal.inputs.albedo = new Vector3(.8f, .3f, .1f); stage.decals = new[] { decal }; frame = Render(); albedo = Rgb(Pixel(frame.albedoCoverage));
                Check("material-decal-colors-baked-gi-once", Error(Rgb(Pixel(target)), emission + Base(bake)) < .004f && ScenePixelsEqual(beforeDecalGi, ReadSceneTarget(frame.bakedDiffuseGi)));
                SaveSsrPreview("scene-gi-material-decal", ReadSceneTarget(target), 129, 129, false);
                stage.decals = Array.Empty<SceneDeferredCamera.Decal>(); Render();
                var otherHost = Own(new GameObject("Second GI host")); var otherCamera = otherHost.AddComponent<Camera>(); otherCamera.CopyFrom(camera); otherCamera.enabled = false; otherCamera.transform.position = camera.transform.position; otherCamera.targetTexture = small;
                var otherStage = otherHost.AddComponent<SceneDeferredCamera>(); otherStage.sceneEnabled = true; otherStage.sceneLayers = 1 << 25; otherStage.surfaces = new[] { surface }; otherStage.lightRadiance = Vector3.zero; otherStage.giBaseScale = .2f;
                otherCamera.Render(); Check("two-camera-gi-targets-independent", otherStage.TryGetFrame(out var otherFrame) && otherFrame.bakedDiffuseGi != frame.bakedDiffuseGi && Pixel(small).r < Pixel(target).r);
                otherStage.enabled = false; frame = Render(); Check("other-disable-preserves-borrowed-map-and-host-gi", map != null && stage.GiTargetCount == 1 && frame.bakedDiffuseGi.IsCreated()); otherHost.SetActive(false);
                stage.enabled = false; Check("component-disable-releases-gi", stage.GiTargetCount == 0 && !frame.IsCurrent && target.IsCreated()); stage.enabled = true; Render(); Check("component-reenable-recreates-gi", stage.GiTargetCount == 1);
                gi.source = SceneGiSource.None; stage.directionalGiWeight = 0; frame = Render(); Check("no-gi-restores-all-old-pixels", ScenePixelsEqual(baseline, ReadSceneTarget(target)) && frame.bakedDiffuseGi == null);
                Check("borrowed-renderer-material-and-property-block-unchanged", renderer.sharedMaterial == shared && !renderer.HasPropertyBlock());
                host.SetActive(false); receiver.SetActive(false);
            }
            finally
            {
                LightmapSettings.lightmaps = oldMaps; LightmapSettings.lightmapsMode = oldMode;
                for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i];
            }
        }
    }
}
