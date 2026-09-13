using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyCrowdLightsticks(Report report)
        {
            yield return null;
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "crowd-lightsticks-" + name, ok, error);
            var previous = FindObjectsOfType<Renderer>(); var forced = previous.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in previous) r.forceRenderingOff = true;
            var active = RenderTexture.active;
            using var crowd = new CrowdRenderer();
            try
            {
                const int width = 129, height = 97;
                var host = Own(new GameObject("Authored audience lightsticks")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.renderingPath = RenderingPath.Forward; camera.allowHDR = true; camera.allowMSAA = false;
                camera.transform.position = new Vector3(0, 0, -6); camera.orthographic = true; camera.orthographicSize = 3.7f;
                camera.aspect = (float)width / height; camera.nearClipPlane = .1f; camera.farClipPlane = 30;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.cullingMask = 0;
                var target = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var model = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); model.GetComponent<Renderer>().forceRenderingOff = true;
                var mesh = Own(Instantiate(model.GetComponent<MeshFilter>().sharedMesh)); var positions = mesh.vertices;
                for (int i = 0; i < positions.Length; i++) positions[i] = Vector3.Scale(positions[i], new Vector3(.8f, 1.7f, .6f));
                mesh.vertices = positions; mesh.RecalculateBounds();
                var mask = Own(new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true)); mask.filterMode = FilterMode.Point; mask.wrapMode = TextureWrapMode.Repeat;
                var maskPixels = Enumerable.Range(0, 16).Select(i => { float v = (i % 4) / 3f; return new Color(v, v, v, 1); }).ToArray();
                mask.SetPixels(maskPixels); mask.Apply(false);
                var p = new CrowdPrototype { lowMesh = mesh, highMesh = mesh, material = new SceneDeferredCamera.MaterialInputs {
                    albedo = new Vector3(.31f, .43f, .59f), emission = new Vector3(.02f, .04f, .03f), uvST = new Vector4(.83f, .71f, .071f, .113f) } };
                p.lightstick.mask = mask; p.lightstick.radiance = new Vector3(3, 2, 4); p.lightstick.minimum = .25f; p.lightstick.frequencyHz = 2;
                var definition = Own(ScriptableObject.CreateInstance<CrowdDefinition>()); definition.prototypes = new[] { p };
                definition.instances = Enumerable.Range(0, 4).Select(i => new CrowdInstance { position = new Vector3((i - 1.5f) * 2.1f, 0, 0),
                    tint = new Vector3(.3f + i * .15f, .8f, .6f), lightstickTint = new Vector3(.25f + i * .2f, .8f - i * .1f, .5f), lightstickPhaseCycles = i * .25 }).ToArray();
                var settings = new CrowdSettings { enabled = true, backend = CrowdBackend.Gpu, allowCpuFallback = false, captureResolution = 64, meshBudget = 4 };
                settings.lighting.backend = SceneForwardLightBackend.BruteForce;
                settings.lighting.lightRadiance = new Vector3(.7f, .5f, .3f); settings.lighting.ambientIrradiance = new Vector3(.11f, .13f, .17f);
                CrowdPose[] poses = null; CrowdRenderer.Frame frame = default;
                Color[] Draw()
                {
                    camera.Render();
                    if (!crowd.TryRender(target, camera, definition, settings, poses, out frame)) throw new InvalidOperationException(crowd.UnavailableReason);
                    return ReadSceneTarget(target);
                }
                var baseline = Draw(); long baselineBytes = frame.crowdResourceBytes; int baselineBuffers = crowd.AllocatedBuffers;
                settings.lightstickTimeSeconds = double.NaN; definition.instances[0].lightstickPhaseCycles = double.NaN;
                Check("disabled-invalid-dormant-input-exact", PixelError(baseline, Draw()) == 0 && crowd.CurrentLightstickRadiance == null);
                settings.lightstickTimeSeconds = 0; definition.instances[0].lightstickPhaseCycles = 0;

                // Independent old ordinary-emission path is the mask oracle. It does
                // not read the new mask atlas alpha or new instance emission buffer.
                // Placements do not overlap and use all-near/all-far for this algebra.
                Color[] Expected()
                {
                    var enabled = definition.prototypes.Select(t => t.lightstick.enabled).ToArray();
                    foreach (var prototype in definition.prototypes) prototype.lightstick.enabled = false;
                    var expected = Draw();
                    var materials = definition.prototypes.Select(t => t.material).ToArray();
                    var tints = definition.instances.Select(t => t.tint).ToArray(); var hidden = definition.instances.Select(t => t.hidden).ToArray();
                    var direct = settings.lighting.lightRadiance; var ambient = settings.lighting.ambientIrradiance;
                    try
                    {
                        settings.lighting.lightRadiance = settings.lighting.ambientIrradiance = Vector3.zero;
                        for (int j = 0; j < definition.prototypes.Length; j++)
                        {
                            var prototype = definition.prototypes[j]; var original = materials[j];
                            prototype.material = new SceneDeferredCamera.MaterialInputs { albedo = Vector3.zero, mos = Vector3.zero,
                                emission = Vector3.one, emissionMap = prototype.lightstick.mask, albedoMap = original.albedoMap, alpha = original.alpha, uvST = original.uvST };
                        }
                        for (int i = 0; i < definition.instances.Length; i++)
                        {
                            var item = definition.instances[i]; var s = definition.prototypes[item.prototype].lightstick;
                            if (!enabled[item.prototype] || hidden[i]) continue;
                            for (int j = 0; j < definition.instances.Length; j++) { definition.instances[j].hidden = j != i; definition.instances[j].tint = Vector3.one; }
                            var coverage = Draw();
                            // Independent sin^2 identity with an explicit reduced clock.
                            double cycles = settings.lightstickTimeSeconds * s.frequencyHz + item.lightstickPhaseCycles;
                            double phase = cycles % 1; double wave = Math.Sin(Math.PI * phase);
                            double pulse = s.minimum + (1 - s.minimum) * wave * wave;
                            for (int v = 0; v < expected.Length; v++) for (int c = 0; c < 3; c++)
                            {
                                double radiance = Math.Min(65504, s.radiance[c] * (double)item.lightstickTint[c] * pulse);
                                expected[v][c] = (float)Math.Min(65504, expected[v][c] + coverage[v].r * radiance);
                            }
                        }
                        return expected;
                    }
                    finally
                    {
                        settings.lighting.lightRadiance = direct; settings.lighting.ambientIrradiance = ambient;
                        for (int j = 0; j < definition.prototypes.Length; j++) { definition.prototypes[j].material = materials[j]; definition.prototypes[j].lightstick.enabled = enabled[j]; }
                        for (int j = 0; j < definition.instances.Length; j++) { definition.instances[j].tint = tints[j]; definition.instances[j].hidden = hidden[j]; }
                    }
                }
                void Oracle(string name)
                {
                    var expected = Expected(); var actual = Draw(); float error = PixelError(expected, actual);
                    Check(name + "-whole-hdr-independent-mask", error <= .0002f, error);
                    Check(name + "-positive-emission", actual.Any(c => Mathf.Max(c.r, c.g, c.b) > 1));
                    var gpu = actual; settings.backend = CrowdBackend.Cpu; var cpu = Draw(); settings.backend = CrowdBackend.Gpu;
                    Check(name + "-current-gpu-cpu-whole-hdr", PixelError(gpu, cpu) <= .0002f, PixelError(gpu, cpu));
                    SaveSsrPreview("crowd-lightsticks-" + name, actual, width, height, false);
                }
                p.lightstick.enabled = true;
                foreach (int budget in new[] { 4, 0 }) foreach (double time in new[] { 0.0, .125, -.25, 1000000.125 })
                { settings.meshBudget = budget; settings.lightstickTimeSeconds = time; Oracle("budget-" + budget + "-clock-" + time); }
                settings.meshBudget = 4; settings.lightstickTimeSeconds = .125; Draw();
                Check("optional-stream-accounted-before-allocation", frame.crowdResourceBytes == baselineBytes + 64 && crowd.AllocatedBuffers == baselineBuffers + 1 && crowd.CurrentLightstickRadiance.stride == 16);
                var paused = Draw(); yield return null; yield return null;
                Check("caller-clock-pause-exact", PixelError(paused, Draw()) == 0);
                p.lightstick.enabled = false; settings.lightstickTimeSeconds = 0;
                Check("disable-releases-optional-stream-exact", PixelError(baseline, Draw()) == 0 && frame.crowdResourceBytes == baselineBytes && crowd.CurrentLightstickRadiance == null);
                p.lightstick.enabled = true; settings.lightstickTimeSeconds = .125;

                // A current mask must enter every material-view capture, not a baked
                // per-instance color. Compare its alpha against the old RGB path.
                Draw(); var capturedMask = ReadSceneTarget(crowd.Atlas(3));
                var oldMaterial = p.material; p.lightstick.enabled = false;
                p.material = new SceneDeferredCamera.MaterialInputs { emissionMap = mask, emission = Vector3.one, uvST = oldMaterial.uvST };
                Draw(); var oldEmission = ReadSceneTarget(crowd.Atlas(3)); float atlasError = 0;
                for (int i = 0; i < capturedMask.Length; i++) atlasError = Mathf.Max(atlasError, Mathf.Abs(capturedMask[i].a - oldEmission[i].r));
                Check("all-four-mask-atlas-texels-independent-rgb", atlasError <= .0002f, atlasError);
                p.material = oldMaterial; p.lightstick.enabled = true;
                var beforeMask = Draw(); mask.SetPixels(maskPixels.Reverse().ToArray()); mask.Apply(false);
                Oracle("current-mask-replacement"); Check("current-mask-positive-no-version-token", PixelError(beforeMask, Draw()) > .1f);
                definition.instances[0].tint = Vector3.zero; Oracle("emission-independent-of-black-body-tint"); definition.instances[0].tint = new Vector3(.3f, .8f, .6f);
                foreach (float yaw in new[] { 0f, 90f, 180f, 270f })
                { foreach (var item in definition.instances) item.yawDegrees = yaw; settings.meshBudget = 0; Oracle("current-yaw-" + yaw); }
                foreach (var item in definition.instances) item.yawDegrees = 0;

                var rig = Own(new GameObject("Current lightstick rig")); var bone = Own(new GameObject("Authored hand influence")); bone.transform.SetParent(rig.transform, false);
                var skinMesh = Own(Instantiate(mesh)); skinMesh.bindposes = new[] { Matrix4x4.identity };
                skinMesh.boneWeights = positions.Select(v => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
                skinMesh.AddBlendShapeFrame("AuthoredAudienceShape", 100, positions.Select(v => new Vector3(.13f * v.y, .09f * v.x, 0)).ToArray(), new Vector3[positions.Length], new Vector3[positions.Length]);
                p.lowMesh = skinMesh; poses = new[] { new CrowdPose { root = rig.transform, bones = new[] { bone.transform }, blendShapeWeights = new float[1] } };
                for (int pose = 0; pose < 3; pose++)
                {
                    bone.transform.localRotation = Quaternion.Euler(pose * 7, pose * 13, pose * 9); poses[0].blendShapeWeights[0] = pose * 37;
                    yield return null; yield return null;
                    foreach (int budget in new[] { 4, 0 }) { settings.meshBudget = budget; Oracle("current-bone-shape-" + pose + "-budget-" + budget); }
                }
                p.lowMesh = mesh; poses = null;
                settings.meshBudget = 2; var mixed = Draw(); settings.backend = CrowdBackend.Cpu; var mixedCpu = Draw(); settings.backend = CrowdBackend.Gpu;
                Check("mixed-budget-current-cpu-gpu-whole-hdr", PixelError(mixed, mixedCpu) <= .0002f, PixelError(mixed, mixedCpu));

                var savedInstances = definition.instances; float savedSize = camera.orthographicSize;
                definition.prototypes = Enumerable.Range(0, 8).Select(i => new CrowdPrototype { lowMesh = mesh, highMesh = mesh, material = oldMaterial,
                    lightstick = new CrowdLightstickSurface { enabled = i != 7, mask = mask, radiance = new Vector3(2 + i * .2f, 3, 4), minimum = .5f, frequencyHz = i } }).ToArray();
                definition.instances = Enumerable.Range(0, 8).Select(i => new CrowdInstance { prototype = i, position = new Vector3((i - 3.5f) * 2.1f, 0, 0),
                    lightstickPhaseCycles = i * .17, lightstickTint = new Vector3(.5f, .7f, .9f) }).ToArray();
                camera.orthographicSize = 6;
                foreach (int budget in new[] { 8, 0 }) { settings.meshBudget = budget; Oracle("eight-prototypes-budget-" + budget); }
                Draw(); var stream = new Vector4[8]; crowd.CurrentLightstickRadiance.GetData(stream);
                Check("disabled-prototype-stream-zero", stream[7] == Vector4.zero && stream.Take(7).All(v => v.x > 0));
                Check("eight-prototypes-no-fifth-atlas", crowd.AllocatedTargets == 5 && crowd.CurrentLightstickRadiance.count == 8);
                definition.prototypes = new[] { p }; definition.instances = savedInstances; camera.orthographicSize = savedSize; settings.meshBudget = 2;
                Draw(); Check("resize-optional-stream-recovery", crowd.CurrentLightstickRadiance.count == 4);

                bool native = Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_CROWD_LIGHTSTICKS") == "1";
                if (native)
                {
                    FsrCaptureDrain(target); bool began = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                    try { Draw(); FsrCaptureDrain(target); } finally { if (began) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                    Check("native-current-mixed-indirect", began && ended);
                }
                // 4096 placements with49px tiles fit1MiB without the optional
                // stream, but exceed it with the additional65536bytes. Reject
                // before allocation, then explicitly raise the caller's budget.
                settings.captureResolution = 49; settings.maximumResourceMiB = 1;
                definition.instances = Enumerable.Range(0, 4096).Select(i => new CrowdInstance { hidden = true }).ToArray();
                p.lightstick.enabled = false; Draw(); long withoutStream = frame.crowdResourceBytes;
                p.lightstick.enabled = true;
                Check("aggregate-budget-includes-optional-stream", withoutStream <= 1048576 && withoutStream + 65536 > 1048576 &&
                    !crowd.TryRender(target, camera, definition, settings, poses, out _) && crowd.AllocatedBuffers == 0 && crowd.AllocatedTargets == 0);
                settings.maximumResourceMiB = 2; Draw();
                Check("aggregate-budget-explicit-recovery", frame.crowdResourceBytes == withoutStream + 65536 && crowd.CurrentLightstickRadiance.count == 4096);
                definition.instances = Array.Empty<CrowdInstance>();
                Check("empty-releases-optional-stream", !crowd.TryRender(target, camera, definition, settings, poses, out _) && crowd.AllocatedBuffers == 0 && crowd.CurrentLightstickRadiance == null);
                definition.instances = savedInstances; settings.captureResolution = 64; settings.maximumResourceMiB = 256;
                void Reject(string name, Action change, Action restore)
                {
                    Draw(); change();
                    bool accepted = crowd.TryRender(target, camera, definition, settings, poses, out _);
                    Check("reject-" + name + "-releases", !accepted && crowd.AllocatedBuffers == 0 && crowd.AllocatedTargets == 0 && crowd.CurrentLightstickRadiance == null && !string.IsNullOrEmpty(crowd.UnavailableReason));
                    restore(); Draw(); Check("recover-" + name, crowd.CurrentLightstickRadiance != null);
                }
                Reject("missing-mask", () => p.lightstick.mask = null, () => p.lightstick.mask = mask);
                Reject("nan-clock", () => settings.lightstickTimeSeconds = double.NaN, () => settings.lightstickTimeSeconds = .125);
                Reject("infinite-phase", () => definition.instances[0].lightstickPhaseCycles = double.PositiveInfinity, () => definition.instances[0].lightstickPhaseCycles = 0);
                Reject("negative-radiance", () => p.lightstick.radiance.x = -1, () => p.lightstick.radiance.x = 3);
                Reject("negative-tint", () => definition.instances[0].lightstickTint.y = -1, () => definition.instances[0].lightstickTint.y = .8f);
                Reject("frequency", () => p.lightstick.frequencyHz = 101, () => p.lightstick.frequencyHz = 2);
                Reject("minimum", () => p.lightstick.minimum = 1.01f, () => p.lightstick.minimum = .25f);
                Reject("output-feedback", () => p.lightstick.mask = target, () => p.lightstick.mask = mask);
                Reject("atlas-feedback", () => p.lightstick.mask = crowd.Atlas(3), () => p.lightstick.mask = mask);
                var srgb = Own(new Texture2D(4, 4, TextureFormat.RGBA32, false, false));
                Reject("srgb-mask", () => p.lightstick.mask = srgb, () => p.lightstick.mask = mask);
                var liveMask = Own(new RenderTexture(4, 4, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); liveMask.Create(); Graphics.Blit(mask, liveMask);
                p.lightstick.mask = liveMask; Draw(); RenderTexture.active = active; liveMask.Release();
                Check("reject-lost-borrowed-mask", !crowd.TryRender(target, camera, definition, settings, poses, out _) && crowd.CurrentLightstickRadiance == null);
                liveMask.Create(); Graphics.Blit(mask, liveMask); Draw(); Check("recover-recreated-borrowed-mask", crowd.CurrentLightstickRadiance != null);
                p.lightstick.mask = mask; settings.meshBudget = 4; p.lightstick.minimum = 1; p.lightstick.radiance = Vector3.one * 65504;
                foreach (var item in definition.instances) item.lightstickTint = Vector3.one * 65504;
                var hdr = Draw(); Check("finite-hdr-range-clamp", hdr.All(c => Enumerable.Range(0, 4).All(k => !float.IsNaN(c[k]) && !float.IsInfinity(c[k]) && c[k] >= 0 && c[k] <= 65504)) && hdr.Any(c => c.r == 65504));
                RenderTexture.active = active; crowd.Dispose(); Check("dispose-only-owned-resources", crowd.AllocatedBuffers == 0 && crowd.AllocatedTargets == 0 && mask != null && liveMask.IsCreated() && target.IsCreated());
            }
            finally
            {
                RenderTexture.active = active; crowd.Dispose();
                for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i];
                foreach (var value in _owned) if (value != null) Destroy(value); _owned.Clear();
            }
        }
    }
}
