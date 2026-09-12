using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifySceneDeferred(Report report)
        {
            // Earlier synthetic fixtures intentionally retain owned objects until suite teardown.
            // Isolate this host from their Forward geometry, restoring every preexisting flag.
            var previousRenderers = FindObjectsOfType<Renderer>();
            var previousForced = new bool[previousRenderers.Length];
            for (int i = 0; i < previousRenderers.Length; i++) { previousForced[i] = previousRenderers[i].forceRenderingOff; previousRenderers[i].forceRenderingOff = true; }
            try
            {
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "deferred-" + name, ok, error);
            var host = Own(new GameObject("Explicit deferred scene host")); var camera = host.AddComponent<Camera>();
            camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
            camera.orthographic = true; camera.orthographicSize = 1; camera.aspect = 1; camera.nearClipPlane = .1f; camera.farClipPlane = 20;
            camera.transform.position = new Vector3(0, 0, -3); camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black; camera.cullingMask = 1 << 26;
            var target = Own(new RenderTexture(129, 129, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
            var stage = host.AddComponent<SceneDeferredCamera>(); stage.sceneLayers = 1 << 25;
            var surfaceHost = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); surfaceHost.name = "Self-authored PBR receiver"; surfaceHost.layer = 25;
            surfaceHost.transform.localScale = new Vector3(1.5f, 1.5f, 1);
            var renderer = surfaceHost.GetComponent<Renderer>(); Material shared = renderer.sharedMaterial;
            var surface = new SceneDeferredCamera.Surface { renderer = renderer, cull = CullMode.Off };
            surface.inputs.albedo = new Vector3(.4f, .2f, .1f); surface.inputs.mos = new Vector3(.1f, .8f, .5f);
            surface.inputs.emission = new Vector3(.1f, .2f, .3f); stage.surfaces = new[] { surface };
            Color At(RenderTexture rt, float u = .5f, float v = .5f) { var p = ReadSceneTarget(rt); return p[Mathf.Clamp((int)(v * rt.height), 0, rt.height - 1) * rt.width + Mathf.Clamp((int)(u * rt.width), 0, rt.width - 1)]; }
            float Error(Color a, Vector3 b) => Mathf.Max(Mathf.Abs(a.r - b.x), Mathf.Abs(a.g - b.y), Mathf.Abs(a.b - b.z));
            SceneDeferredCamera.Frame Render()
            { camera.Render(); if (!stage.TryGetFrame(out var f)) throw new InvalidOperationException(stage.UnavailableReason); return f; }
            camera.Render(); var disabled = ReadSceneTarget(target);
            Check("isolated-forward-host-clear", Error(disabled[0], Vector3.zero) == 0 && Error(disabled[disabled.Length / 2], Vector3.zero) == 0);
            Check("default-off-no-targets", stage.AllocatedTargets == 0 && !stage.TryGetFrame(out _));
            stage.sceneEnabled = true; var frame = Render();
            Check("geometry-actually-submitted", stage.SubmittedSurfaces == 1 && stage.SubmittedDecals == 0 && stage.AllocatedTargets == 4);
            Check("unlit-albedo-data", Error(At(frame.albedoCoverage), surface.inputs.albedo) < .001f);
            Check("world-normal-and-receiver-id", Error(At(frame.normalGroup), Vector3.back) < .001f && At(frame.normalGroup).a == 1);
            Check("mos-and-positive-camera-depth", Error(At(frame.mosDepth), surface.inputs.mos) < .001f && Mathf.Abs(At(frame.mosDepth).a - 3) < .001f);
            Check("unlit-emission-data", Error(At(frame.emission), surface.inputs.emission) < .001f);
            Check("uncovered-pixels-preserve-host-clear", PixelError(new[] { At(target, .01f, .01f) }, new[] { disabled[0] }) == 0);
            var baseline = ReadSceneTarget(target); var initialFrame = frame; Render();
            Check("no-decal-repeat-identical", ScenePixelsEqual(baseline, ReadSceneTarget(target)) && !initialFrame.IsCurrent);
            Vector3 CpuLight(Vector3 albedo, Vector3 mos, Vector3 emission, Vector3 normal, Vector3 direction)
            {
                Vector3 n = normal.normalized, v = Vector3.back, l = direction.normalized, h = (v + l).normalized;
                float nl = Mathf.Clamp01(Vector3.Dot(n, l)), nv = Mathf.Clamp01(Vector3.Dot(n, v));
                float nh = Mathf.Clamp01(Vector3.Dot(n, h)), vh = Mathf.Clamp01(Vector3.Dot(v, h));
                float a2 = Mathf.Pow(Mathf.Max(1 - mos.z, .045f), 4), den = nh * nh * (a2 - 1) + 1;
                float d = a2 / Mathf.Max(Mathf.PI * den * den, 1e-8f);
                float vis = .5f / Mathf.Max(nl * Mathf.Sqrt(nv * nv * (1 - a2) + a2) + nv * Mathf.Sqrt(nl * nl * (1 - a2) + a2), 1e-6f);
                var f0 = Vector3.Lerp(Vector3.one * .04f, albedo, mos.x); var f = f0 + (Vector3.one - f0) * Mathf.Pow(1 - vh, 5);
                var diffuse = albedo * ((1 - mos.x) / Mathf.PI);
                return Vector3.Scale(Vector3.Scale(Vector3.one - f, diffuse) + f * d * vis, stage.lightRadiance) * nl +
                    Vector3.Scale(diffuse, stage.ambientIrradiance) * mos.y + emission;
            }
            var expected = CpuLight(surface.inputs.albedo, surface.inputs.mos, surface.inputs.emission, Vector3.back, stage.lightDirection);
            Check("lighting-consumes-unlit-channels-once-cpu-control", Error(At(target), expected) < .003f, Error(At(target), expected));
            stage.lightRadiance = Vector3.zero; stage.ambientIrradiance = Vector3.zero; Render();
            Check("emission-is-not-lit-twice", Error(At(target), surface.inputs.emission) < .001f);
            stage.lightRadiance = Vector3.one; stage.ambientIrradiance = Vector3.one * .1f;

            var decal = new SceneDeferredCamera.Decal { localToWorld = Matrix4x4.Scale(new Vector3(1, 1, 2)) };
            decal.inputs.albedo = new Vector3(.1f, .6f, .2f); decal.inputs.alpha = .5f; stage.decals = new[] { decal };
            frame = Render(); var blended = Vector3.Lerp(surface.inputs.albedo, decal.inputs.albedo, .5f);
            Check("projected-alpha-blends-albedo", Error(At(frame.albedoCoverage), blended) < .001f);
            Check("projection-box-excludes-outside", Error(At(frame.albedoCoverage, .8f), surface.inputs.albedo) < .001f);
            Check("albedo-only-preserves-mos-normal-emission", Error(At(frame.mosDepth), surface.inputs.mos) < .001f && Error(At(frame.normalGroup), Vector3.back) < .001f && Error(At(frame.emission), surface.inputs.emission) < .001f);
            Check("decal-result-is-actually-lit", Error(At(target), CpuLight(blended, surface.inputs.mos, surface.inputs.emission, Vector3.back, stage.lightDirection)) < .003f);
            Check("decal-uses-separate-owned-targets", stage.AllocatedTargets == 8 && stage.SubmittedDecals == 1);
            SaveSsrPreview("deferred-albedo-decal", ReadSceneTarget(frame.albedoCoverage), 129, 129, false);
            SaveSsrPreview("deferred-lit-decal", ReadSceneTarget(target), 129, 129, false);
            decal.receiverGroup = 2; frame = Render(); Check("receiver-group-prevents-bleed", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            decal.receiverGroup = 1; surface.receiverGroup = 0; frame = Render(); Check("zero-group-is-not-a-receiver", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            surface.receiverGroup = 1;
            decal.localToWorld = Matrix4x4.Translate(new Vector3(0, 0, 2)); Render(); Check("projection-depth-rejection", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            decal.localToWorld = Matrix4x4.identity; decal.minimumFacing = .9f; Render(); Check("front-facing-projector-accepted", !ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            decal.localToWorld = Matrix4x4.Rotate(Quaternion.Euler(0, 180, 0)); Render(); Check("back-facing-projector-rejected", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            decal.localToWorld = Matrix4x4.identity; decal.minimumFacing = -1;

            decal.albedoWeight = 0; decal.inputs.mos = new Vector3(.8f, .2f, .9f); decal.mosWeight = new Vector3(.25f, .5f, 1);
            frame = Render(); Vector3 mixedMos = surface.inputs.mos + Vector3.Scale(decal.inputs.mos - surface.inputs.mos, decal.mosWeight) * .5f;
            Check("independent-metallic-occlusion-smoothness-alpha", Error(At(frame.mosDepth), mixedMos) < .001f);
            Check("mos-only-preserves-albedo", Error(At(frame.albedoCoverage), surface.inputs.albedo) < .001f);
            Check("mos-lighting-cpu-control", Error(At(target), CpuLight(surface.inputs.albedo, mixedMos, surface.inputs.emission, Vector3.back, stage.lightDirection)) < .004f, Error(At(target), CpuLight(surface.inputs.albedo, mixedMos, surface.inputs.emission, Vector3.back, stage.lightDirection)));
            decal.mosWeight = Vector3.zero; decal.emissionWeight = .5f; decal.inputs.emission = new Vector3(8, 4, 2);
            frame = Render(); var emissive = Vector3.Lerp(surface.inputs.emission, decal.inputs.emission, .25f);
            Check("hdr-emission-decal-alpha-once", Error(At(frame.emission), emissive) < .003f && At(target).r > 1);
            Check("emission-decal-independent-of-mos", Error(At(frame.mosDepth), surface.inputs.mos) < .001f);
            decal.emissionWeight = 0; decal.mosWeight = Vector3.up; decal.inputs.alpha = 1; decal.inputs.mos = Vector3.zero; decal.heightOcclusion = true; decal.height = 1; decal.heightFade = 1;
            frame = Render(); Check("height-ao-center-cpu-control", Mathf.Abs(At(frame.mosDepth).g - .4f) < .001f);
            surfaceHost.transform.position = new Vector3(0, 0, .25f); frame = Render(); Check("moving-receiver-height-ao", Mathf.Abs(At(frame.mosDepth).g - .6f) < .001f);
            surfaceHost.transform.position = new Vector3(0, 0, -.25f); frame = Render(); Check("lower-receiver-height-ao", Mathf.Abs(At(frame.mosDepth).g - .2f) < .001f);
            surfaceHost.transform.position = Vector3.zero; decal.height = .25f; frame = Render(); Check("above-height-field-rejected", Mathf.Abs(At(frame.mosDepth).g - .8f) < .001f);
            decal.heightOcclusion = false; decal.mosWeight = Vector3.zero; decal.normalWeight = 1;
            var normalTexture = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); normalTexture.SetPixel(0, 0, new Color(.8f, .5f, .9f, 1)); normalTexture.Apply();
            decal.inputs.normalMap = normalTexture; frame = Render(); var tilted = new Vector3(.6f, 0, -.8f);
            Check("projected-normal-is-world-space-unit-vector", Error(At(frame.normalGroup), tilted) < .001f);
            Check("normal-channel-actually-changes-lighting", Error(At(target), CpuLight(surface.inputs.albedo, surface.inputs.mos, surface.inputs.emission, tilted, stage.lightDirection)) < .003f);
            decal.localToWorld = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0, 0, 90), new Vector3(2, .5f, 1)); frame = Render();
            Check("rotated-nonuniform-projector-normal-basis", Error(At(frame.normalGroup), new Vector3(0, .6f, -.8f)) < .001f);
            decal.normalWeight = 0; decal.albedoWeight = 1; decal.localToWorld = Matrix4x4.identity; decal.inputs.normalMap = null;
            var second = new SceneDeferredCamera.Decal { inputs = new SceneDeferredCamera.MaterialInputs { albedo = new Vector3(.7f, .1f, .5f), alpha = .25f } };
            stage.decals = new[] { decal, second }; frame = Render();
            Check("ordered-overlap-two-ping-pong-passes", Error(At(frame.albedoCoverage), Vector3.Lerp(decal.inputs.albedo, second.inputs.albedo, .25f)) < .001f && stage.SubmittedDecals == 2);
            stage.decals = new[] { second, decal }; frame = Render(); Check("reordered-overlap-is-explicit", Error(At(frame.albedoCoverage), decal.inputs.albedo) < .001f);
            stage.decals = new[] { decal }; decal.inputs.alpha = 0; Render(); Check("zero-alpha-identical-baseline", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            decal.inputs.alpha = 1; decal.enabled = false; Render(); Check("disabled-decals-release-scratch", stage.AllocatedTargets == 4 && stage.SubmittedDecals == 0 && ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            decal.enabled = true; decal.albedoWeight = 0; Render(); Check("zero-channel-weights-no-decal-work", stage.AllocatedTargets == 4 && stage.SubmittedDecals == 0 && ScenePixelsEqual(baseline, ReadSceneTarget(target)));

            // Main Forward objects are never sampled as scene albedo or material receivers.
            var actor = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); actor.name = "Forward occlusion control"; actor.layer = 26;
            actor.transform.localScale = Vector3.one * .4f; actor.transform.position = new Vector3(0, 0, -1);
            var actorMaterial = Own(new Material(Resources.Load<Shader>("MonitorEmission")));
            actorMaterial.SetTexture("_MonitorTex", Texture2D.whiteTexture); actorMaterial.SetVector("_MonitorTint", new Vector4(1, 0, 1, 1));
            actor.GetComponent<Renderer>().sharedMaterial = actorMaterial;
            frame = Render(); Check("forward-foreground-draws-after-scene", Error(At(target), new Vector3(1, 0, 1)) < .002f);
            Check("forward-actor-absent-from-material-buffers", Error(At(frame.albedoCoverage), surface.inputs.albedo) < .001f);
            actor.transform.position = new Vector3(0, 0, 1); Render(); Check("resolved-scene-depth-occludes-forward-object", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            actor.SetActive(false);
            var cutout = Own(new Texture2D(2, 1, TextureFormat.RGBAFloat, false, true)); cutout.filterMode = FilterMode.Point;
            cutout.SetPixels(new[] { Color.clear, Color.white }); cutout.Apply(); surface.inputs.albedoMap = cutout; surface.alphaCutoff = .5f;
            frame = Render(); Check("alpha-cutout-shares-material-uv", At(frame.albedoCoverage, .3f).a == 0 && At(frame.albedoCoverage, .7f).a == 1);
            surface.inputs.uvST = new Vector4(-1, 1, 1, 0); frame = Render(); Check("mirrored-material-uv-cutout", At(frame.albedoCoverage, .3f).a == 1 && At(frame.albedoCoverage, .7f).a == 0);
            actor.SetActive(true); actor.transform.position = new Vector3(-.3f, 0, 1); surface.inputs.uvST = new Vector4(1, 1, 0, 0); Render();
            Check("cutout-preserves-forward-depth-hole", Error(At(target, .35f), new Vector3(1, 0, 1)) < .002f); actor.SetActive(false);
            surface.inputs.albedoMap = null; surface.alphaCutoff = 0;
            surface.inputs.normalMap = normalTexture; surfaceHost.transform.rotation = Quaternion.Euler(0, 30, 0); surfaceHost.transform.localScale = new Vector3(1.5f, .7f, 2);
            frame = Render(); Check("transformed-geometry-tangent-normal", Error(At(frame.normalGroup), surfaceHost.transform.rotation * tilted) < .002f);
            surface.inputs.normalMap = null; surfaceHost.transform.rotation = Quaternion.identity; surfaceHost.transform.localScale = new Vector3(1.5f, 1.5f, 1);
            surface.vertexScale = new Vector3(.5f, 1, 1); frame = Render(); Check("explicit-vertex-scale-changes-coverage", At(frame.albedoCoverage, .75f).a == 0 && At(frame.albedoCoverage).a == 1);
            surface.vertexScale = Vector3.one;
            camera.orthographic = false; camera.fieldOfView = 40; decal.albedoWeight = 1; frame = Render();
            Check("perspective-depth-reconstruction-and-projection", Error(At(frame.albedoCoverage), decal.inputs.albedo) < .001f && Mathf.Abs(At(frame.mosDepth).a - 3) < .001f);
            camera.orthographic = true; decal.enabled = false; frame = Render();
            Check("orthographic-restores-original-output", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            Check("shared-renderer-state-unchanged", renderer.sharedMaterial == shared && renderer.enabled && renderer.gameObject.layer == 25 && camera.cullingMask == 1 << 26);
            var block = new MaterialPropertyBlock(); block.SetVector("_Albedo", new Vector4(1, 0, 0, 1)); renderer.SetPropertyBlock(block); camera.Render();
            Check("source-property-block-cannot-override-explicit-inputs", !stage.TryGetFrame(out _) && stage.AllocatedTargets == 0 && renderer.HasPropertyBlock());
            renderer.SetPropertyBlock(null); frame = Render();
            surface.cull = CullMode.Front; frame = Render(); Check("explicit-front-cull-removes-geometry", At(frame.albedoCoverage).a == 0);
            surface.cull = CullMode.Back; frame = Render(); Check("explicit-back-cull-keeps-front", At(frame.albedoCoverage).a == 1); surface.cull = CullMode.Off;
            surfaceHost.transform.localScale = new Vector3(-1.5f, .7f, 2); surface.inputs.normalMap = normalTexture; frame = Render();
            Check("mirrored-transform-normal-handedness", Error(At(frame.normalGroup), new Vector3(-.6f, 0, -.8f)) < .002f);
            surface.inputs.normalMap = null; surfaceHost.transform.localScale = new Vector3(1.5f, 1.5f, 1);
            foreach (bool linear in new[] { false, true })
            {
                var colorTexture = Own(new Texture2D(1, 1, TextureFormat.RGBA32, false, linear)); colorTexture.SetPixel(0, 0, new Color(.5f, .25f, .75f, 1)); colorTexture.Apply();
                surface.inputs.albedoMap = colorTexture; frame = Render(); Color texel = colorTexture.GetPixel(0, 0);
                if (!linear && QualitySettings.activeColorSpace == ColorSpace.Linear) texel = texel.linear;
                Check("albedo-texture-" + (linear ? "linear" : "srgb"), Error(At(frame.albedoCoverage), Vector3.Scale(new Vector3(texel.r, texel.g, texel.b), surface.inputs.albedo)) < .002f);
            }
            surface.inputs.albedoMap = null; decal.enabled = true; decal.albedoWeight = 1; decal.localToWorld = Matrix4x4.TRS(new Vector3(.35f, .35f, 0), Quaternion.Euler(0, 0, 25), Vector3.one * .4f);
            frame = Render(); Check("offcenter-translated-rotated-projection", Error(At(frame.albedoCoverage, .675f, .675f), decal.inputs.albedo) < .001f && Error(At(frame.albedoCoverage, .325f, .325f), surface.inputs.albedo) < .001f);
            camera.orthographic = false; camera.fieldOfView = 40; frame = Render(); var location = camera.WorldToViewportPoint(new Vector3(.35f, .35f, 0));
            Check("perspective-offcenter-projector-world-reconstruction", Error(At(frame.albedoCoverage, location.x, location.y), decal.inputs.albedo) < .001f);
            camera.orthographic = true; decal.localToWorld = Matrix4x4.identity; decal.enabled = false;
            // An interleaved second host has private attachments and does not change this camera's state.
            var otherHost = Own(new GameObject("Second scene host")); var otherCamera = otherHost.AddComponent<Camera>(); otherCamera.CopyFrom(camera); otherCamera.enabled = false;
            var otherTarget = Own(new RenderTexture(33, 17, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); otherTarget.Create(); otherCamera.targetTexture = otherTarget;
            var otherStage = otherHost.AddComponent<SceneDeferredCamera>(); otherStage.sceneLayers = 1 << 25; otherStage.surfaces = new[] { surface }; otherStage.sceneEnabled = true;
            otherStage.lightRadiance = Vector3.zero; otherStage.ambientIrradiance = Vector3.zero; frame = Render(); otherCamera.Render();
            Check("second-camera-distinct-buffers-and-lighting", otherStage.TryGetFrame(out var otherFrame) && otherFrame.albedoCoverage != frame.albedoCoverage && Error(At(otherTarget), surface.inputs.emission) < .001f && ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            otherHost.SetActive(false);
            // Previous stage producer now drives actual scene material emission rather than a handle-only binding.
            var monitorHost = Own(new GameObject("Scene decal HDR source")); var monitorCamera = monitorHost.AddComponent<Camera>(); monitorCamera.CopyFrom(camera);
            monitorCamera.enabled = false; monitorCamera.cullingMask = 1 << 24; monitorCamera.orthographicSize = .5f;
            var sourceObject = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); sourceObject.layer = 24; sourceObject.transform.localScale = Vector3.one * 2;
            var sourceMaterial = Own(new Material(Resources.Load<Shader>("MonitorEmission"))); sourceMaterial.SetTexture("_MonitorTex", Texture2D.whiteTexture);
            sourceMaterial.SetVector("_MonitorTint", new Vector4(4, 2, 1, 1)); sourceObject.GetComponent<Renderer>().sharedMaterial = sourceMaterial;
            var monitor = monitorHost.AddComponent<HdrMonitor>(); monitor.width = 17; monitor.height = 17; monitor.monitorEnabled = true;
            Check("monitor-source-capture-for-scene-decal", monitor.TryUpdate(0, 0, out var monitorFrame));
            decal.enabled = true; decal.albedoWeight = 0; decal.emissionWeight = 1; decal.inputs.emission = Vector3.one; decal.inputs.emissionMap = monitorFrame.texture;
            frame = Render(); Check("monitor-hdr-reaches-gbuffer-and-lighting", Error(At(frame.emission), new Vector3(4, 2, 1)) < .003f && At(target).r > 4);
            sourceMaterial.SetVector("_MonitorTint", new Vector4(8, 2, 1, 1)); monitor.TryUpdate(0, 1, out monitorFrame); frame = Render();
            Check("monitor-revision-updates-scene-emission", Error(At(frame.emission), new Vector3(8, 2, 1)) < .003f && At(target).r > 8);
            SaveSsrPreview("deferred-monitor-emission", ReadSceneTarget(target), 129, 129, false);
            monitorHost.SetActive(false); sourceObject.SetActive(false); decal.inputs.emissionMap = null; decal.emissionWeight = 0; decal.enabled = false;
            // Real one-bone skinning with an excluded source layer and explicit offscreen updates.
            var skinHost = Own(new GameObject("Skinned scene receiver")); skinHost.layer = 25;
            var bone = Own(new GameObject("Scene receiver bone")); bone.transform.SetParent(skinHost.transform, false);
            var mesh = Own(new Mesh()); mesh.vertices = new[] { new Vector3(-.75f, -.75f, 0), new Vector3(-.75f, .75f, 0), new Vector3(.75f, .75f, 0), new Vector3(.75f, -.75f, 0) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 }; mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            var weight = new BoneWeight { boneIndex0 = 0, weight0 = 1 }; mesh.boneWeights = new[] { weight, weight, weight, weight }; mesh.bindposes = new[] { Matrix4x4.identity }; mesh.RecalculateBounds();
            var skin = skinHost.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.sharedMaterial = shared; skin.bones = new[] { bone.transform }; skin.rootBone = bone.transform;
            skin.updateWhenOffscreen = true; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 4);
            surfaceHost.SetActive(false); stage.surfaces = new[] { new SceneDeferredCamera.Surface { renderer = skin, cull = CullMode.Off, inputs = surface.inputs } };
            frame = Render(); Check("skinned-scene-geometry-first-pose", At(frame.albedoCoverage, .2f).a == 1 && Error(At(frame.normalGroup), Vector3.back) < .001f);
            bone.transform.localPosition = new Vector3(.4f, 0, 0); frame = Render(); Check("skinned-scene-bone-motion-reaches-gbuffer", At(frame.albedoCoverage, .2f).a == 0 && At(frame.albedoCoverage, .8f).a == 1);
            bone.transform.localPosition = Vector3.zero; frame = Render(); Check("skinned-scene-pose-rewind", At(frame.albedoCoverage, .2f).a == 1);
            skinHost.SetActive(false); surfaceHost.SetActive(true); stage.surfaces = new[] { surface };
            frame = Render();
            var oldFrame = frame; frame.albedoCoverage.Release(); frame = Render(); Check("lost-target-recreated", frame.IsCurrent && !oldFrame.IsCurrent && frame.albedoCoverage != oldFrame.albedoCoverage);
            var resized = Own(new RenderTexture(65, 33, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); resized.Create(); camera.targetTexture = resized;
            frame = Render(); Check("resize-releases-and-rebuilds", frame.albedoCoverage.width == 65 && frame.albedoCoverage.height == 33 && !oldFrame.IsCurrent);
            camera.targetTexture = target; Render();
            camera.cullingMask |= 1 << 25; camera.Render(); Check("overlapping-host-layer-rejected", !stage.TryGetFrame(out _) && stage.AllocatedTargets == 0 && stage.UnavailableReason.Contains("double lighting"));
            camera.cullingMask = 1 << 26; Render();
            target.Release(); Check("released-host-target-invalidates-borrow", !stage.TryGetFrame(out _)); target.Create(); Render();
            decal.enabled = true; decal.albedoWeight = 1; var projective = Matrix4x4.identity; projective.m30 = .2f; decal.localToWorld = projective; camera.Render();
            Check("nonaffine-projector-rejected", !stage.TryGetFrame(out _) && stage.AllocatedTargets == 0);
            decal.enabled = true; decal.albedoWeight = 1; decal.localToWorld = Matrix4x4.Scale(Vector3.zero); camera.Render();
            Check("singular-projector-fails-closed", !stage.TryGetFrame(out _) && stage.AllocatedTargets == 0);
            decal.localToWorld = Matrix4x4.identity; Render(); stage.surfaces = new[] { surface, surface }; camera.Render();
            Check("duplicate-surface-fails-closed", !stage.TryGetFrame(out _) && stage.AllocatedTargets == 0);
            stage.surfaces = new[] { surface }; Render(); stage.lightRadiance = new Vector3(float.NaN, 0, 0); camera.Render();
            Check("nonfinite-light-rejected", !stage.TryGetFrame(out _) && stage.AllocatedTargets == 0); stage.lightRadiance = Vector3.one;
            Render(); stage.sceneEnabled = false; camera.Render(); Check("disable-exact-host-restoration", ScenePixelsEqual(disabled, ReadSceneTarget(target)) && stage.AllocatedTargets == 0);
            stage.sceneEnabled = true; Render(); stage.enabled = false;
            Check("component-disable-releases-command-buffer", stage.AllocatedTargets == 0 && !stage.TryGetFrame(out _) && camera.GetCommandBuffers(CameraEvent.BeforeForwardOpaque).Length == 0);
            stage.enabled = true; Render(); Check("component-reenable-recovers", stage.TryGetFrame(out _));
            host.SetActive(false); surfaceHost.SetActive(false);
            }
            finally
            {
                for (int i = 0; i < previousRenderers.Length; i++) if (previousRenderers[i] != null) previousRenderers[i].forceRenderingOff = previousForced[i];
            }
        }
    }
}
