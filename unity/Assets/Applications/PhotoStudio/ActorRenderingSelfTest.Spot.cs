using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifySceneSpotLights(Report report)
        {
            var previous = FindObjectsOfType<Renderer>(); var forced = new bool[previous.Length];
            for (int i = 0; i < previous.Length; i++) { forced[i] = previous[i].forceRenderingOff; previous[i].forceRenderingOff = true; }
            try
            {
                void Check(string label, bool ok, float error = 0) => FrameworkCheck(report, "scene-spot-" + label, ok, error);
                var host = Own(new GameObject("Spot light host")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
                camera.transform.position = new Vector3(0, 0, -4); camera.orthographic = true; camera.orthographicSize = 2; camera.aspect = 1;
                camera.nearClipPlane = .1f; camera.farClipPlane = 40; camera.cullingMask = 1 << 26; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                var target = Own(new RenderTexture(129, 129, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var stage = host.AddComponent<SceneDeferredCamera>(); stage.sceneEnabled = true; stage.sceneLayers = 1 << 25; stage.lightRadiance = stage.ambientIrradiance = Vector3.zero; stage.giBaseScale = 0;
                var obj = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); obj.layer = 25; obj.transform.localScale = new Vector3(3.8f, 3.8f, 1);
                var mesh = Own(Instantiate(obj.GetComponent<MeshFilter>().sharedMesh)); mesh.uv2 = mesh.uv; obj.GetComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = obj.GetComponent<Renderer>(); var borrowed = renderer.sharedMaterial;
                var surface = new SceneDeferredCamera.Surface { renderer = renderer, cull = CullMode.Off };
                surface.inputs.albedo = new Vector3(.4f, .6f, .2f); surface.inputs.mos = new Vector3(.1f, .7f, .4f); surface.inputs.emission = new Vector3(.1f, .2f, .3f); stage.surfaces = new[] { surface };
                var options = stage.decalLighting; var light = new SceneDecalLight { shape = SceneDecalLightShape.Spot, position = Vector3.back, range = 3, radiance = new Vector3(2, 3, 1) };
                options.lights = new[] { light }; options.allowInstancingFallback = false;
                SceneDeferredCamera.Frame Render() { camera.Render(); if (!stage.TryGetFrame(out var f)) throw new InvalidOperationException(stage.UnavailableReason); return f; }
                Vector3 Rgb(Color c) => new Vector3(c.r, c.g, c.b);
                float Error(Vector3 a, Vector3 b) => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z));
                Color Center(RenderTexture t) => ReadSceneTarget(t)[t.width * (t.height / 2) + t.width / 2];
                var initial = Render(); var baseline = ReadSceneTarget(target); var g0 = ReadSceneTarget(initial.albedoCoverage); var g1 = ReadSceneTarget(initial.normalGroup); var g2 = ReadSceneTarget(initial.mosDepth); var ge = ReadSceneTarget(initial.emission);
                var emission = Rgb(ge[64 * 129 + 64]); options.enabled = true; Vector3 atlas = Vector3.one, gi = Vector3.one;
                Vector3 Cpu(SceneDecalLight value, Vector3 world, Vector3 albedo, Vector3 mos, Vector3 normal)
                {
                    if (value.receiverGroup != 0 && value.receiverGroup != surface.receiverGroup) return Vector3.zero;
                    var delta = world - value.position; float distance = delta.magnitude;
                    if (distance <= 1e-6f || distance >= value.range) return Vector3.zero;
                    var axis = value.rotation.normalized * Vector3.forward;
                    // Independent angle calculation in double precision, not packed GPU constants.
                    double theta = Math.Acos(Math.Max(-1, Math.Min(1, Vector3.Dot(delta / distance, axis))));
                    double inner = value.spotInnerAngle * Math.PI / 360, outer = value.spotOuterAngle * Math.PI / 360;
                    if (theta > outer) return Vector3.zero;
                    float cone = inner == outer ? 1 : Mathf.Clamp01((float)((Math.Cos(theta) - Math.Cos(outer)) / (Math.Cos(inner) - Math.Cos(outer))));
                    Vector3 l = -delta / distance, n = normal.normalized, v = camera.orthographic ? Vector3.back : (camera.transform.position - world).normalized, h = (v + l).normalized;
                    float nl = Mathf.Clamp01(Vector3.Dot(n, l)), nv = Mathf.Clamp01(Vector3.Dot(n, v)), nh = Mathf.Clamp01(Vector3.Dot(n, h)), vh = Mathf.Clamp01(Vector3.Dot(v, h));
                    float a2 = Mathf.Pow(Mathf.Max(1 - mos.z, .045f), 4), den = nh * nh * (a2 - 1) + 1;
                    float distribution = a2 / Mathf.Max(Mathf.PI * den * den, 1e-8f), visibility = .5f / Mathf.Max(nl * Mathf.Sqrt(nv * nv * (1 - a2) + a2) + nv * Mathf.Sqrt(nl * nl * (1 - a2) + a2), 1e-6f);
                    var f0 = Vector3.Lerp(Vector3.one * .04f, albedo, mos.x); var fresnel = f0 + (Vector3.one - f0) * Mathf.Pow(1 - vh, 5);
                    var response = (Vector3.Scale(Vector3.one - fresnel, albedo) * ((1 - mos.x) / Mathf.PI * value.diffuseScale) + fresnel * distribution * visibility * value.specularScale) * nl;
                    response += Vector3.Scale(Vector3.one - f0, albedo) * ((1 - mos.x) / Mathf.PI * value.diffuseScale * value.backlightScale * Mathf.Clamp01(-Vector3.Dot(n, l)));
                    return Vector3.Scale(Vector3.Scale(response, Vector3.Scale(atlas, value.radiance)), Vector3.Lerp(Vector3.one, gi, value.giWeight)) * (cone * Mathf.Pow(Mathf.Clamp01(1 - distance / value.range), value.falloffExponent));
                }
                int Oracle(string label, bool expectLit = true)
                {
                    var f = Render(); var pixels = ReadSceneTarget(target); g0 = ReadSceneTarget(f.albedoCoverage); g1 = ReadSceneTarget(f.normalGroup); g2 = ReadSceneTarget(f.mosDepth); ge = ReadSceneTarget(f.emission);
                    float worst = 0; int lit = 0, covered = 0;
                    for (int y = 0; y < 129; y++) for (int x = 0; x < 129; x++)
                    {
                        int i = y * 129 + x; Vector3 expected = Vector3.zero;
                        if (g0[i].a > .5f)
                        {
                            covered++; var ray = camera.ViewportPointToRay(new Vector3((x + .5f) / 129, (y + .5f) / 129, 0));
                            var plane = new Plane(Vector3.forward, Vector3.zero); if (!plane.Raycast(ray, out float distance)) throw new InvalidOperationException("Spot CPU ray missed plane.");
                            var direct = Cpu(light, ray.GetPoint(distance), Rgb(g0[i]), Rgb(g2[i]), Rgb(g1[i])); if (direct.sqrMagnitude > .00001f) lit++;
                            expected = Rgb(ge[i]) + direct;
                        }
                        worst = Mathf.Max(worst, Error(expected, Rgb(pixels[i])));
                    }
                    Check(label + "-full-image-cpu", worst < .006f && covered > 1000 && (expectLit ? lit > 0 : lit == 0), worst); return lit;
                }
                foreach (var backend in new[] { SceneDecalLightBackend.Scalar, SceneDecalLightBackend.Instanced })
                {
                    options.backend = backend; string label = backend.ToString();
                    int soft = Oracle(label + "-soft-cone"); Check(label + "-real-submitted-spot", stage.SubmittedLights == 1 && stage.LightDrawCalls == 1 && stage.ActiveLightBackend == backend);
                    var softPixels = ReadSceneTarget(target); light.shape = SceneDecalLightShape.Point; Render(); var pointPixels = ReadSceneTarget(target);
                    Check(label + "-cone-excludes-point-lit-corners", pointPixels[64 * 129 + 100].r > softPixels[64 * 129 + 100].r + .01f); light.shape = SceneDecalLightShape.Spot;
                    light.spotInnerAngle = light.spotOuterAngle; int hard = Oracle(label + "-hard-cone"); Check(label + "-hard-cone-retains-larger-full-bright-area", hard >= soft);
                    light.spotInnerAngle = 0; Oracle(label + "-zero-inner-angle");
                    light.spotOuterAngle = .1f; Oracle(label + "-minimum-cone-center-positive");
                    light.spotInnerAngle = 30; light.spotOuterAngle = 179; Oracle(label + "-wide-cone"); light.spotOuterAngle = 60;
                    light.rotation = Quaternion.Euler(0, 35, 0); Oracle(label + "-rotated-cone");
                    light.rotation = Quaternion.Euler(0, 180, 0); Oracle(label + "-backward-cone", false); light.rotation = Quaternion.identity;
                    light.position = new Vector3(.6f, -.3f, -1.2f); Oracle(label + "-translated-cone"); light.position = Vector3.back;
                    light.range = .8f; Oracle(label + "-radial-out-of-range", false); light.range = 3;
                    light.falloffExponent = 2; Oracle(label + "-squared-radial-falloff"); light.falloffExponent = 1;
                    light.diffuseScale = 0; Oracle(label + "-specular-only"); light.diffuseScale = 1; light.specularScale = 0; Oracle(label + "-diffuse-only"); light.specularScale = 1;
                    light.receiverGroup = 2; Oracle(label + "-wrong-receiver-group", false); light.receiverGroup = 0;
                }
                options.backend = SceneDecalLightBackend.Instanced; Oracle("reference"); SaveSsrPreview("scene-spot-soft", ReadSceneTarget(target), 129, 129, false);
                light.spotInnerAngle = 60; Oracle("hard-reference"); SaveSsrPreview("scene-spot-hard", ReadSceneTarget(target), 129, 129, false); light.spotInnerAngle = 30;
                camera.orthographic = false; camera.fieldOfView = 50; Oracle("perspective");
                light.position = new Vector3(0, 0, -4.5f); light.range = 6; Oracle("eye-crossing-conservative-volume");
                camera.nearClipPlane = 3; camera.projectionMatrix = Matrix4x4.Perspective(50, 1, .1f, 40); Oracle("custom-projection-not-near-property"); camera.ResetProjectionMatrix(); camera.nearClipPlane = .1f; camera.orthographic = true;
                light.position = Vector3.back; light.range = 3; light.rotation = new Quaternion(0, 0, 0, 2); Oracle("quaternion-normalization"); light.rotation = Quaternion.identity;
                var map = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); map.SetPixel(0, 0, new Color(2, 1, .5f, 1)); map.Apply();
                surface.gi = new SceneGiInput { source = SceneGiSource.Lightmap, lightmap = map }; gi = new Vector3(2, 1, .5f);
                foreach (float weight in new[] { 0f, .5f, 1f }) { light.giWeight = weight; Oracle("gi-weight-" + weight); }
                var ns = new Vector3[mesh.vertexCount]; for (int i = 0; i < ns.Length; i++) ns[i] = Vector3.forward; mesh.normals = ns;
                light.backlightScale = .4f; Oracle("inverse-diffuse-gi"); light.specularScale = 0; Oracle("inverse-diffuse-no-extra-specular"); light.specularScale = 1;
                for (int i = 0; i < ns.Length; i++) ns[i] = Vector3.back; mesh.normals = ns; light.backlightScale = 0;
                var atlasTexture = Own(new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true)); atlasTexture.filterMode = FilterMode.Point;
                atlasTexture.SetPixels(new[] { new Color(2, 3, 1, 0), Color.red, Color.blue, Color.green }); atlasTexture.Apply(); options.atlas = atlasTexture;
                light.monitorUV = new Vector4(500, 700, .25f, .25f); atlas = new Vector3(2, 3, 1); Oracle("fixed-atlas-rgb-not-cookie-or-alpha");
                options.atlas = null; atlas = Vector3.one;
                var producerHost = Own(new GameObject("Spot monitor producer")); var producer = producerHost.AddComponent<Camera>(); producer.CopyFrom(camera); producer.enabled = false; producer.targetTexture = null;
                producer.transform.position = new Vector3(0, 0, -3); producer.cullingMask = 1 << 24; producer.orthographicSize = 1;
                var panel = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); panel.layer = 24; panel.transform.localScale = Vector3.one * 2;
                var material = Own(new Material(Resources.Load<Shader>("MonitorEmission"))); material.SetTexture("_MonitorTex", Texture2D.whiteTexture); material.SetVector("_MonitorTint", new Vector3(4, 2, 1)); panel.GetComponent<Renderer>().sharedMaterial = material;
                var monitor = producerHost.AddComponent<HdrMonitor>(); monitor.monitorEnabled = true; monitor.width = monitor.height = 33;
                options.monitor = monitor; Check("monitor-real-content-published", monitor.TryUpdate(0, 0, out _)); atlas = new Vector3(4, 2, 1); Oracle("monitor-content-plus-gi");
                material.SetVector("_MonitorTint", new Vector3(8, 2, 1)); monitor.TryUpdate(0, 1, out _); atlas.x = 8; Oracle("monitor-update-plus-gi"); SaveSsrPreview("scene-spot-monitor", ReadSceneTarget(target), 129, 129, false);
                options.monitor = null; producerHost.SetActive(false); panel.SetActive(false); atlas = Vector3.one;
                var many = new SceneDecalLight[110];
                for (int i = 0; i < many.Length; i++) many[i] = new SceneDecalLight { shape = SceneDecalLightShape.Spot,
                    position = new Vector3((i % 11 - 5) * .09f, (i / 11 - 4) * .08f, -1), range = 3,
                    rotation = Quaternion.Euler(i % 5 - 2, i % 7 - 3, 0), spotInnerAngle = 15 + i % 5 * 4, spotOuterAngle = 60 + i % 7 * 5,
                    radiance = new Vector3(.02f, .03f, .01f), giWeight = 1, backlightScale = .2f };
                options.lights = many; options.backend = SceneDecalLightBackend.Scalar; Render(); var scalar = ReadSceneTarget(target);
                options.backend = SceneDecalLightBackend.Instanced; var instancedFrame = Render(); var instanced = ReadSceneTarget(target);
                Check("110-independent-spot-instances-match-scalar", stage.SubmittedLights == 110 && stage.LightDrawCalls == 1 && stage.LightBufferCapacity == 128 && PixelError(scalar, instanced) < .003f, PixelError(scalar, instanced));
                var centerAlbedo = Rgb(Center(instancedFrame.albedoCoverage)); var centerMos = Rgb(Center(instancedFrame.mosDepth)); var expectedSum = emission;
                foreach (var value in many) expectedSum += Cpu(value, Vector3.zero, centerAlbedo, centerMos, Vector3.back);
                Check("110-spots-cpu-sum-with-gi", Error(Rgb(Center(target)), expectedSum) < .005f, Error(Rgb(Center(target)), expectedSum));
                if (Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SPOT_LIGHTS") == "1")
                {
                    bool started = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                    try { if (started) { Render(); ReadSceneTarget(target); } }
                    finally { if (started) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                    Check("requested-native-spot-capture", started && ended);
                }
                options.batchSize = 32; Render(); Check("110-spot-instance-offsets", stage.LightDrawCalls == 4 && PixelError(scalar, ReadSceneTarget(target)) < .003f); options.batchSize = 256;
                SaveSsrPreview("scene-spot-110-gi", instanced, 129, 129, false);
                for (int i = 0; i < many.Length; i++) many[i].shape = (SceneDecalLightShape)(i % 4);
                options.backend = SceneDecalLightBackend.Scalar; Render(); var mixed = ReadSceneTarget(target);
                options.backend = SceneDecalLightBackend.Instanced; Render();
                Check("four-shapes-share-one-instanced-batch", stage.SubmittedLights == 110 && stage.LightDrawCalls == 1 && PixelError(mixed, ReadSceneTarget(target)) < .003f, PixelError(mixed, ReadSceneTarget(target)));
                foreach (var value in many) value.shape = SceneDecalLightShape.Spot; Render();
                Check("shape-changes-refresh-packed-cones", ScenePixelsEqual(instanced, ReadSceneTarget(target)));
                options.lights = new[] { light }; light.position = new Vector3(100, 0, -1); Render(); Check("offscreen-cone-no-gpu-resources", stage.CulledLights == 1 && stage.LightDrawCalls == 0 && stage.LightTargetCount == 0 && stage.LightBufferCapacity == 0); light.position = Vector3.back;
                foreach (var angles in new[] { new Vector2(-1, 60), new Vector2(61, 60), new Vector2(0, 0), new Vector2(0, 180), new Vector2(float.NaN, 60), new Vector2(30, float.PositiveInfinity) })
                { light.spotInnerAngle = angles.x; light.spotOuterAngle = angles.y; camera.Render(); Check("invalid-cone-rejected-" + angles, !stage.TryGetFrame(out _) && stage.LightTargetCount == 0 && stage.LightBufferCapacity == 0); }
                light.spotInnerAngle = 30; light.spotOuterAngle = 60; light.position = Vector3.zero; Render(); Check("apex-is-finite-dark", Error(Rgb(Center(target)), emission) == 0); light.position = Vector3.back;
                light.shape = (SceneDecalLightShape)999; camera.Render(); Check("unknown-shape-rejected", !stage.TryGetFrame(out _)); light.shape = SceneDecalLightShape.Point;
                light.spotOuterAngle = float.NaN; Render(); Check("unused-spot-settings-do-not-invalidate-old-shapes", stage.SubmittedLights == 1); light.shape = SceneDecalLightShape.Spot; light.spotOuterAngle = 60;
                surface.gi.source = SceneGiSource.None; gi = Vector3.one; light.giWeight = 0; options.enabled = false; Render();
                Check("disabled-restores-all-baseline-pixels", ScenePixelsEqual(baseline, ReadSceneTarget(target)) && stage.LightTargetCount == 0);
                Check("borrowed-renderer-state-unchanged", renderer.sharedMaterial == borrowed && !renderer.HasPropertyBlock()); camera.targetTexture = null; host.SetActive(false); obj.SetActive(false);
            }
            finally { for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i]; }
        }
    }
}
