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
        private IEnumerator VerifyCrowd(Report report)
        {
            yield return null;
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "crowd-" + name, ok, error);
            var previous = FindObjectsOfType<Renderer>(); var forced = previous.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in previous) r.forceRenderingOff = true;
            var active = RenderTexture.active;
            using var crowd = new CrowdRenderer();
            try
            {
                const int width = 129, height = 97;
                var host = Own(new GameObject("Current crowd acceptance")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.renderingPath = RenderingPath.Forward; camera.allowHDR = true; camera.allowMSAA = false;
                camera.transform.position = new Vector3(0, 0, -4); camera.orthographic = true; camera.orthographicSize = 1.6f;
                camera.aspect = (float)width / height; camera.nearClipPlane = .1f; camera.farClipPlane = 25;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.07f, .11f, .17f, 1); camera.cullingMask = 0;
                RenderTexture Target(int w, int h)
                { var t = Own(new RenderTexture(w, h, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); t.Create(); return t; }
                var target = Target(width, height); camera.targetTexture = target;
                float Difference(Color[] a, Color[] b)
                {
                    if (a.Length != b.Length) return float.PositiveInfinity;
                    float max = 0; for (int i = 0; i < a.Length; i++) for (int c = 0; c < 4; c++)
                    { float d = Mathf.Abs(a[i][c] - b[i][c]); if (float.IsNaN(d) || float.IsInfinity(d)) return float.PositiveInfinity; max = Mathf.Max(max, d); }
                    return max;
                }
                var model = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); model.name = "Independently authored asymmetric crowd prototype"; model.layer = 25;
                var mesh = Own(Instantiate(model.GetComponent<MeshFilter>().sharedMesh));
                var positions = mesh.vertices;
                for (int i = 0; i < positions.Length; i++) positions[i] = new Vector3(positions[i].x * (.65f + .25f * (positions[i].y + .5f)), positions[i].y * 1.5f, positions[i].z * .5f);
                mesh.vertices = positions; mesh.RecalculateBounds(); mesh.RecalculateNormals(); model.GetComponent<MeshFilter>().sharedMesh = mesh;
                var prototype = new CrowdPrototype { lowMesh = mesh, highMesh = mesh, material = new SceneDeferredCamera.MaterialInputs {
                    albedo = new Vector3(.31f, .49f, .67f), mos = new Vector3(.13f, .81f, .27f), emission = new Vector3(.013f, .027f, .019f) } };
                var definition = Own(ScriptableObject.CreateInstance<CrowdDefinition>());
                definition.prototypes = new[] { prototype }; definition.instances = new[] { new CrowdInstance() };
                var settings = new CrowdSettings { enabled = true, backend = CrowdBackend.Gpu, allowCpuFallback = false, captureResolution = 64, meshBudget = 1 };
                var lighting = settings.lighting; lighting.backend = SceneForwardLightBackend.BruteForce;
                lighting.lightRadiance = new Vector3(.71f, .53f, .37f); lighting.ambientIrradiance = new Vector3(.13f, .17f, .11f);
                CrowdPose[] poses = null; CrowdRenderer.Frame frame = default;
                Color[] Render()
                {
                    camera.Render();
                    if (!crowd.TryRender(target, camera, definition, settings, poses, out frame)) throw new InvalidOperationException(crowd.UnavailableReason);
                    return ReadSceneTarget(target);
                }
                var forward = host.AddComponent<SceneForwardLightingCamera>(); forward.surfaceLayers = 1 << 25;
                forward.settings.enabled = true; forward.settings.lightRadiance = lighting.lightRadiance; forward.settings.ambientIrradiance = lighting.ambientIrradiance;
                forward.settings.lightDirection = lighting.lightDirection; forward.settings.backend = SceneForwardLightBackend.BruteForce;
                forward.settings.surfaces = new[] { new SceneForwardSurface { renderer = model.GetComponent<Renderer>(), inputs = prototype.material, gi = prototype.gi } };
                var nativeMaterial = Own(new Material(Resources.Load<Shader>("CrowdNativeReference")));
                // Real camera scheduling establishes Unity's native per-draw data. A bare
                // CommandBuffer.DrawRenderer does not promise lighting/builtin parameter setup.
                Color[] Native(bool depthWrites = true, int debug = 0, bool automatic = true)
                {
                    camera.Render(); using var resources = new SceneForwardLightResources();
                    if (!resources.Prepare(camera, forward.settings, camera.targetTexture.width, camera.targetTexture.height, out var error)) throw new InvalidOperationException(error);
                    var surface = forward.settings.surfaces[0]; resources.Bind(nativeMaterial); SceneDeferredCamera.BindInputs(nativeMaterial, surface.inputs);
                    nativeMaterial.SetVector("_VertexScale", surface.vertexScale); nativeMaterial.SetFloat("_Cutoff", prototype.alphaCutoff);
                    nativeMaterial.SetFloat("_Cull", (int)surface.cull); nativeMaterial.SetFloat("_ZWrite", depthWrites ? 1 : 0);
                    nativeMaterial.SetFloat("_NativeDebug", debug);
                    nativeMaterial.SetFloat("_Additive", 0); nativeMaterial.SetFloat("_ReceiverGroup", surface.receiverGroup);
                    nativeMaterial.SetVector("_ForwardToon", new Vector4(prototype.toonThreshold, prototype.toonSoftness, prototype.toonShadow, prototype.lighting == CrowdLighting.Toon ? 1 : 0));
                    nativeMaterial.SetFloat("_SceneGiMode", 0); if (surface.gi != null && !surface.gi.Bind(nativeMaterial, surface.renderer, out error)) throw new InvalidOperationException(error);
                    var commands = new CommandBuffer { name = "Independent current native crowd vertex/depth oracle" };
                    try
                    {
                        resources.Record(commands); commands.SetRenderTarget(camera.targetTexture); commands.SetViewport(new Rect(0, 0, camera.targetTexture.width, camera.targetTexture.height));
                        if (!automatic) commands.DrawRenderer(surface.renderer, nativeMaterial, surface.submesh, 0);
                        Graphics.ExecuteCommandBuffer(commands);
                        if (automatic)
                        {
                            var all = FindObjectsOfType<Renderer>(); var saved = all.Select(r => r.forceRenderingOff).ToArray();
                            var originalMaterial = surface.renderer.sharedMaterial; int mask = camera.cullingMask;
                            try
                            {
                                foreach (var r in all) r.forceRenderingOff = r != surface.renderer;
                                surface.renderer.sharedMaterial = nativeMaterial; camera.cullingMask = 1 << surface.renderer.gameObject.layer; camera.Render();
                            }
                            finally { surface.renderer.sharedMaterial = originalMaterial; camera.cullingMask = mask; for (int i = 0; i < all.Length; i++) if (all[i] != null) all[i].forceRenderingOff = saved[i]; }
                        }
                        return ReadSceneTarget(camera.targetTexture);
                    }
                    finally { commands.Release(); }
                }
                forward.enabled = false;
                camera.Render(); var background = ReadSceneTarget(target); var first = Render(); var native = Native();
                Check("static-near-positive-current-geometry", Difference(first, background) > .01f, Difference(first, background));
                Check("static-near-native-whole-rgba", Difference(first, native) <= .0002f, Difference(first, native));
                Check("gpu-path-real-buffers-dispatches", frame.backend == CrowdBackend.Gpu && frame.computeDispatches >= 5 && frame.indirectCommands == 2 && frame.crowdResourceBytes > 0);
                var firstAtlas = ReadSceneTarget(crowd.Atlas(0));
                Check("four-current-atlas-tiles-positive", Enumerable.Range(0, 4).All(d => Enumerable.Range(0, settings.captureResolution * settings.captureResolution)
                    .Any(j => firstAtlas[(j / settings.captureResolution) * settings.captureResolution * 4 + d * settings.captureResolution + j % settings.captureResolution].a > .5f)));
                settings.backend = CrowdBackend.Cpu; var cpu = Render();
                Check("static-near-explicit-cpu-whole-rgba", Difference(first, cpu) <= .00002f, Difference(first, cpu));
                Check("explicit-cpu-no-compute-dispatch", frame.backend == CrowdBackend.Cpu && frame.computeDispatches == 0);
                settings.backend = CrowdBackend.Gpu; settings.meshBudget = 0; var impostor = Render();
                Check("far-positive-generated-impostor", Difference(impostor, background) > .01f, Difference(impostor, background));
                settings.backend = CrowdBackend.Cpu; var impostorCpu = Render();
                Check("far-explicit-cpu-whole-rgba", Difference(impostor, impostorCpu) <= .00002f, Difference(impostor, impostorCpu));
                settings.backend = CrowdBackend.Gpu; settings.meshBudget = 1;

                var rig = Own(new GameObject("Crowd native skin oracle")); rig.layer = 25;
                var b0 = Own(new GameObject("Crowd oracle root influence")); b0.transform.SetParent(rig.transform, false);
                var b1 = Own(new GameObject("Crowd oracle upper influence")); b1.transform.SetParent(rig.transform, false);
                var skinMesh = Own(Instantiate(mesh)); skinMesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
                skinMesh.boneWeights = positions.Select(p => new BoneWeight { boneIndex0 = 0, boneIndex1 = 1, weight0 = .5f - p.y / 3, weight1 = .5f + p.y / 3 }).ToArray();
                var delta = positions.Select(p => new Vector3(p.y * .19f, p.x * .11f, (p.x + .31f) * .13f)).ToArray();
                skinMesh.AddBlendShapeFrame("IndependentCrowdShape", 100, delta, new Vector3[delta.Length], new Vector3[delta.Length]);
                var skin = rig.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = skinMesh; skin.bones = new[] { b0.transform, b1.transform }; skin.rootBone = rig.transform;
                skin.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                skin.sharedMaterial = model.GetComponent<Renderer>().sharedMaterial; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 10); skin.updateWhenOffscreen = true; skin.quality = SkinQuality.Bone4;
                poses = new[] { new CrowdPose { root = rig.transform, bones = skin.bones, blendShapeWeights = new float[1] } };
                prototype.lowMesh = skinMesh; forward.settings.surfaces[0].renderer = skin;
                Color[] resting = null, restingAtlas = null;
                for (int pose = 0; pose < 4; pose++)
                {
                    b0.transform.localRotation = Quaternion.Euler(0, pose * 7, -pose * 3); b1.transform.localRotation = Quaternion.Euler(pose * 5, -pose * 11, pose * 7);
                    b1.transform.localPosition = new Vector3(pose * .031f, -pose * .017f, pose * .013f);
                    poses[0].blendShapeWeights[0] = pose * 23; skin.SetBlendShapeWeight(0, pose * 23);
                    yield return null; yield return null;
                    settings.backend = CrowdBackend.Gpu; settings.meshBudget = 1; var deformed = Render(); var deformedNative = Native();
                    var noDepth = Native(false);
                    Debug.Log("[CrowdDepthAblation] pose=" + pose + "; depthOnVsOff=" + Difference(deformedNative, noDepth).ToString("R") + "; crowdVsOff=" + Difference(deformed, noDepth).ToString("R"));
                    Check("pose" + pose + "-current-native-skin-whole-rgba", Difference(deformed, deformedNative) <= .0002f, Difference(deformed, deformedNative));
                    int badPixels = 0, worstPixel = 0; float worstError = 0;
                    for (int pixel = 0; pixel < deformed.Length; pixel++)
                    {
                        float e = Enumerable.Range(0, 4).Max(c => Mathf.Abs(deformed[pixel][c] - deformedNative[pixel][c]));
                        if (e > .0002f) badPixels++; if (e > worstError) { worstError = e; worstPixel = pixel; }
                    }
                    Debug.Log("[CrowdNativeImage] pose=" + pose + "; badPixels=" + badPixels + "; worst=" + worstPixel % width + "," + worstPixel / width + "; actual=" + deformed[worstPixel].ToString("R") + "; native=" + deformedNative[worstPixel].ToString("R"));
                    var gpuVertices = new CrowdPrototypeGeometry.Vertex[skinMesh.vertexCount]; crowd.CurrentVertices(0).GetData(gpuVertices);
                    using (var nativeBuffer = skin.GetVertexBuffer())
                    {
                        if (nativeBuffer != null)
                        {
                            var data = new float[nativeBuffer.count * nativeBuffer.stride / 4]; nativeBuffer.GetData(data);
                            Debug.Log("[CrowdNativeVertices] pose=" + pose + "; count=" + nativeBuffer.count + "; stride=" + nativeBuffer.stride + "; values=" + string.Join(",", data.Select(v => v.ToString("R"))));
                            Debug.Log("[CrowdCurrentVertices] pose=" + pose + "; values=" + string.Join(";", gpuVertices.Select(v => v.position.ToString("R") + ":" + v.normal.ToString("R"))));
                            if (nativeBuffer.count == skinMesh.vertexCount && nativeBuffer.stride == 40)
                            {
                                var nativeCopy = Own(Instantiate(skinMesh)); var nativePositions = new Vector3[skinMesh.vertexCount]; var nativeNormals = new Vector3[skinMesh.vertexCount]; var nativeTangents = new Vector4[skinMesh.vertexCount];
                                for (int v = 0; v < nativePositions.Length; v++)
                                { nativePositions[v] = new Vector3(data[v * 10], data[v * 10 + 1], data[v * 10 + 2]); nativeNormals[v] = new Vector3(data[v * 10 + 3], data[v * 10 + 4], data[v * 10 + 5]); nativeTangents[v] = new Vector4(data[v * 10 + 6], data[v * 10 + 7], data[v * 10 + 8], data[v * 10 + 9]); }
                                nativeCopy.vertices = nativePositions; nativeCopy.normals = nativeNormals; nativeCopy.tangents = nativeTangents; nativeCopy.RecalculateBounds();
                                model.GetComponent<MeshFilter>().sharedMesh = nativeCopy; forward.settings.surfaces[0].renderer = model.GetComponent<Renderer>(); var copiedImage = Native();
                                Debug.Log("[CrowdNativeCopyImage] pose=" + pose + "; nativeVsCopy=" + Difference(deformedNative, copiedImage).ToString("R") + "; crowdVsCopy=" + Difference(deformed, copiedImage).ToString("R"));
                                model.GetComponent<MeshFilter>().sharedMesh = mesh; forward.settings.surfaces[0].renderer = skin;
                            }
                        }
                    }
                    var currentAtlas = ReadSceneTarget(crowd.Atlas(0));
                    settings.backend = CrowdBackend.Cpu; var deformedCpu = Render();
                    var cpuVertices = new CrowdPrototypeGeometry.Vertex[skinMesh.vertexCount]; crowd.CurrentVertices(0).GetData(cpuVertices);
                    float vertexError = 0;
                    for (int v = 0; v < gpuVertices.Length; v++) for (int c = 0; c < 4; c++)
                        vertexError = Mathf.Max(vertexError, Mathf.Abs(gpuVertices[v].position[c] - cpuVertices[v].position[c]), Mathf.Abs(gpuVertices[v].normal[c] - cpuVertices[v].normal[c]), Mathf.Abs(gpuVertices[v].tangent[c] - cpuVertices[v].tangent[c]), Mathf.Abs(gpuVertices[v].uv[c] - cpuVertices[v].uv[c]));
                    Check("pose" + pose + "-every-gpu-cpu-vertex-attribute", vertexError <= .00002f, vertexError);
                    Check("pose" + pose + "-current-cpu-skin-whole-rgba", Difference(deformed, deformedCpu) <= .0002f, Difference(deformed, deformedCpu));
                    if (pose == 0) { resting = deformed; restingAtlas = currentAtlas; }
                    else
                    {
                        Check("pose" + pose + "-positive-current-skin", Difference(resting, deformed) > .001f, Difference(resting, deformed));
                        Check("pose" + pose + "-positive-current-four-view-capture", Difference(restingAtlas, currentAtlas) > .001f, Difference(restingAtlas, currentAtlas));
                    }
                    settings.meshBudget = 0; var farCpu = Render(); settings.backend = CrowdBackend.Gpu; var farGpu = Render();
                    Check("pose" + pose + "-current-impostor-cpu-gpu-whole-rgba", Difference(farGpu, farCpu) <= .0002f, Difference(farGpu, farCpu));
                    if (pose == 3)
                    {
                        SaveSsrPreview("crowd-current-near-pose", deformed, width, height, false);
                        SaveSsrPreview("crowd-native-pose-oracle", deformedNative, width, height, false);
                        SaveSsrPreview("crowd-current-four-view-impostor", farGpu, width, height, false);
                        SaveSsrPreview("crowd-current-material-atlas", currentAtlas, crowd.Atlas(0).width, crowd.Atlas(0).height, false);
                    }
                }
                var variableMesh = Own(Instantiate(skinMesh));
                var variableWeights = new BoneWeight1[mesh.vertexCount * 2];
                for (int i = 0; i < mesh.vertexCount; i++)
                { variableWeights[i * 2] = new BoneWeight1 { boneIndex = i % 2, weight = .73f }; variableWeights[i * 2 + 1] = new BoneWeight1 { boneIndex = 1 - i % 2, weight = .27f }; }
                using (var counts = new Unity.Collections.NativeArray<byte>(Enumerable.Repeat((byte)2, mesh.vertexCount).ToArray(), Unity.Collections.Allocator.Temp))
                using (var weights = new Unity.Collections.NativeArray<BoneWeight1>(variableWeights, Unity.Collections.Allocator.Temp))
                {
                    variableMesh.SetBoneWeights(counts, weights);
                }
                skin.sharedMesh = variableMesh; prototype.lowMesh = variableMesh;
                for (int pose = 0; pose < 3; pose++)
                {
                    b0.transform.localRotation = Quaternion.Euler(pose * 3, pose * 11, 0); b1.transform.localRotation = Quaternion.Euler(-pose * 7, pose * 13, pose * 9);
                    poses[0].blendShapeWeights[0] = pose * 37; skin.SetBlendShapeWeight(0, pose * 37); yield return null; yield return null;
                    settings.meshBudget = 1; settings.backend = CrowdBackend.Gpu; var variableGpu = Render(); var variableNative = Native();
                    Check("variable-skin-pose" + pose + "-actual-native-camera-whole-rgba", Difference(variableGpu, variableNative) <= .0002f, Difference(variableGpu, variableNative));
                    settings.backend = CrowdBackend.Cpu; var variableCpu = Render(); settings.backend = CrowdBackend.Gpu;
                    Check("variable-skin-pose" + pose + "-gpu-cpu-whole-rgba", Difference(variableGpu, variableCpu) <= .0002f, Difference(variableGpu, variableCpu));
                }
                skin.sharedMesh = skinMesh;
                poses = null; prototype.lowMesh = mesh; forward.settings.surfaces[0].renderer = model.GetComponent<Renderer>();
                settings.backend = CrowdBackend.Gpu; settings.meshBudget = 1; Render();

                // Independently render actual native geometry from each authored capture camera.
                // Emission-only native output is the material albedo oracle, with no lighting baked in.
                var materialAtlas = ReadSceneTarget(crowd.Atlas(0)); var sphere = crowd.PrototypeSpheres[0];
                var tileTarget = Target(settings.captureResolution, settings.captureResolution);
                var savedInputs = forward.settings.surfaces[0].inputs;
                var captureLight = forward.settings.lightRadiance; var captureAmbient = forward.settings.ambientIrradiance;
                forward.settings.lightRadiance = Vector3.zero; forward.settings.ambientIrradiance = Vector3.zero;
                forward.settings.surfaces[0].inputs = new SceneDeferredCamera.MaterialInputs { albedo = Vector3.zero, emission = prototype.material.albedo, alpha = 1 };
                var bg = camera.backgroundColor; var size = camera.orthographicSize; var aspect = camera.aspect;
                camera.backgroundColor = Color.clear; camera.targetTexture = tileTarget; camera.aspect = 1; camera.orthographicSize = sphere.w;
                camera.nearClipPlane = sphere.w * .1f; camera.farClipPlane = sphere.w * 4;
                var directions = new[] { Vector3.back, Vector3.right, Vector3.forward, Vector3.left };
                for (int direction = 0; direction < 4; direction++)
                {
                    camera.transform.SetPositionAndRotation((Vector3)sphere + directions[direction] * sphere.w * 2, Quaternion.LookRotation(-directions[direction], Vector3.up));
                    camera.ResetProjectionMatrix(); var expectedTile = Native(); var actualTile = new Color[tileTarget.width * tileTarget.height];
                    for (int y = 0; y < tileTarget.height; y++) for (int x = 0; x < tileTarget.width; x++) actualTile[y * tileTarget.width + x] = materialAtlas[y * tileTarget.width * 4 + direction * tileTarget.width + x];
                    Check("atlas-direction" + direction + "-independent-native-capture-whole-rgba", Difference(expectedTile, actualTile) <= .0002f, Difference(expectedTile, actualTile));
                    SaveSsrPreview("crowd-native-capture-direction" + direction, expectedTile, tileTarget.width, tileTarget.height, false);
                }
                forward.settings.surfaces[0].inputs = savedInputs; camera.targetTexture = target; camera.backgroundColor = bg; camera.orthographicSize = size;
                forward.settings.lightRadiance = captureLight; forward.settings.ambientIrradiance = captureAmbient;
                camera.aspect = aspect; camera.nearClipPlane = .1f; camera.farClipPlane = 25; camera.transform.SetPositionAndRotation(new Vector3(0, 0, -4), Quaternion.identity); camera.ResetProjectionMatrix();
                Render();

                Texture2D Constant(Color color)
                { var texture = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); texture.SetPixel(0, 0, color); texture.Apply(); return texture; }
                var locals = lighting.localLights; locals.enabled = true; forward.settings.localLights = locals;
                locals.lights = Enumerable.Range(0, 73).Select(i => new SceneDecalLight {
                    shape = (SceneDecalLightShape)(i % 4), position = new Vector3((i % 9 - 4) * .5f, (i / 9 - 4) * .5f, -1.3f - i % 3 * .2f),
                    rotation = Quaternion.Euler(i % 3 * 7, i % 5 * 3, i * 11), range = 2.1f + i % 3 * .2f,
                    halfLength = .31f, halfSize = new Vector2(.21f, .29f), areaSpread = new Vector2(.23f, .17f), spotInnerAngle = 71, spotOuterAngle = 119,
                    radiance = new Vector3(.13f, .07f, .11f), specularScale = .7f, backlightScale = .1f }).ToArray();
                locals.lights[72].position = new Vector3(0, 0, -1.3f); locals.lights[72].range = 5;
                Color[] LitPair(string name)
                {
                    lighting.backend = SceneForwardLightBackend.BruteForce; var brute = Render();
                    lighting.backend = SceneForwardLightBackend.Tiled; var tiled = Render();
                    Check(name + "-current-tiled-brute-whole-rgba", Difference(brute, tiled) <= .00002f, Difference(brute, tiled)); return tiled;
                }
                foreach (int budget in new[] { 1, 0 })
                {
                    settings.meshBudget = budget; var baseline = LitPair("lit-lod" + budget);
                    if (budget == 1) { var expected = Native(); Check("lit-near-independent-native-whole-rgba", Difference(baseline, expected) <= .0002f, Difference(baseline, expected)); }
                    locals.lights[72].radiance = new Vector3(5, .1f, .1f); var last = LitPair("lit-lod" + budget + "-last-light");
                    Check("lit-lod" + budget + "-tail-light-positive", Difference(last, baseline) > .001f, Difference(last, baseline)); locals.lights[72].radiance = new Vector3(.13f, .07f, .11f);
                    prototype.gi.source = SceneGiSource.Probe; prototype.gi.probe = new SphericalHarmonicsL2(); prototype.gi.probe.AddAmbientLight(new Color(.27f, .39f, .17f));
                    var gi = LitPair("lit-lod" + budget + "-probe"); Check("lit-lod" + budget + "-probe-positive", Difference(gi, baseline) > .001f, Difference(gi, baseline));
                    if (budget == 1) { var expected = Native(); Check("probe-near-independent-native-whole-rgba", Difference(gi, expected) <= .0002f, Difference(gi, expected)); }
                    prototype.gi.source = SceneGiSource.None;
                    mesh.RecalculateTangents(); definition.contentVersion++;
                    prototype.material.normalMap = Constant(new Color(.19f, .71f, .91f, 1)); var mapped = LitPair("lit-lod" + budget + "-normal");
                    Check("lit-lod" + budget + "-normal-positive", Difference(mapped, baseline) > .001f, Difference(mapped, baseline));
                    if (budget == 1)
                    {
                        var expected = Native(); Check("mapped-near-independent-native-whole-rgba", Difference(mapped, expected) <= .0002f, Difference(mapped, expected));
                        var vertices = new CrowdPrototypeGeometry.Vertex[mesh.vertexCount]; crowd.CurrentVertices(0).GetData(vertices);
                        var copy = Own(Instantiate(mesh)); copy.vertices = vertices.Select(v => (Vector3)v.position).ToArray(); copy.normals = vertices.Select(v => (Vector3)v.normal).ToArray();
                        copy.tangents = vertices.Select(v => v.tangent).ToArray(); copy.uv = vertices.Select(v => new Vector2(v.uv.x, v.uv.y)).ToArray(); copy.RecalculateBounds();
                        model.GetComponent<MeshFilter>().sharedMesh = copy; var copied = Native(); model.GetComponent<MeshFilter>().sharedMesh = mesh;
                        var rawT = mesh.tangents; var rawN = mesh.normals; float tError = 0, nError = 0;
                        for (int v = 0; v < vertices.Length; v++) { tError = Mathf.Max(tError, Vector4.Distance(rawT[v], vertices[v].tangent)); nError = Mathf.Max(nError, Vector3.Distance(rawN[v], vertices[v].normal)); }
                        Debug.Log("[CrowdMappedInput] tangentError=" + tError.ToString("R") + "; normalError=" + nError.ToString("R") + "; copiedNativeVsOriginal=" + Difference(copied, expected).ToString("R") + "; crowdVsCopy=" + Difference(mapped, copied).ToString("R"));
                        var auto = Native(true, 0, true); var manual = Native(true, 0, false);
                        Check("mapped-real-camera-native-whole-rgba", Difference(mapped, auto) <= .0002f, Difference(mapped, auto));
                        Debug.Log("[CrowdMappedAutomatic] bareDrawRendererVsAutomatic=" + Difference(manual, auto).ToString("R") + "; crowdVsAutomatic=" + Difference(mapped, auto).ToString("R"));
                        for (int debug = 1; debug <= 4; debug++)
                        {
                            var values = Native(true, debug, false); var autoValues = Native(true, debug, true);
                            Debug.Log("[CrowdMappedBasis] mode=" + debug + "; nativeCenter=" + values[(height / 2) * width + width / 2].ToString("R") + "; automaticCenter=" + autoValues[(height / 2) * width + width / 2].ToString("R"));
                        }
                        SaveSsrPreview("crowd-current-mapped", mapped, width, height, false); SaveSsrPreview("crowd-native-mapped", expected, width, height, false);
                    }
                    prototype.material.normalMap = null;
                    prototype.lighting = CrowdLighting.Toon; var toon = LitPair("lit-lod" + budget + "-toon");
                    if (budget == 1) { var expected = Native(); Check("toon-near-independent-native-whole-rgba", Difference(toon, expected) <= .0002f, Difference(toon, expected)); }
                    Check("lit-lod" + budget + "-independent-toon-positive", Difference(toon, baseline) > .001f, Difference(toon, baseline)); prototype.lighting = CrowdLighting.Pbr;
                }
                var coverage = Own(new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point });
                coverage.SetPixels(new[] { new Color(1, .2f, .1f, 1), Color.clear, new Color(.2f, 1, .1f, 1), new Color(.1f, .2f, 1, 1) }); coverage.Apply();
                prototype.material.albedoMap = coverage;
                foreach (int budget in new[] { 1, 0 })
                {
                    settings.meshBudget = budget; var textured = LitPair("cutout-lod" + budget);
                    if (budget == 1) { var expected = Native(); Check("cutout-near-independent-native-whole-rgba", Difference(textured, expected) <= .0002f, Difference(textured, expected)); }
                    settings.backend = CrowdBackend.Cpu; var cutoutCpu = Render(); settings.backend = CrowdBackend.Gpu;
                    Check("cutout-lod" + budget + "-gpu-cpu-whole-rgba", Difference(textured, cutoutCpu) <= .0002f, Difference(textured, cutoutCpu));
                }
                prototype.material.albedoMap = null; locals.enabled = false; lighting.backend = SceneForwardLightBackend.BruteForce; settings.meshBudget = 1; Render();

                // Full independent CPU oracle inspects every actual GPU rank, all5 indirect words,
                // and every live compacted ID. No count-only acceptance, no production readback.
                void SelectionOracle(string name)
                {
                    // Independently round every operation to the shader's float32 contract.
                    // A normal Mono float expression can retain double intermediates.
                    float Single(float value) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value));
                    var packed = new CrowdSelection.InstanceData[crowd.Selection.Capacity]; crowd.Selection.Instances.GetData(packed);
                    var bounds = crowd.PrototypeSpheres;
                    var planes = GeometryUtility.CalculateFrustumPlanes(camera.projectionMatrix * camera.worldToCameraMatrix);
                    var origin = camera.worldToCameraMatrix.inverse.MultiplyPoint(Vector3.zero);
                    var keys = new List<(uint key, uint id)>();
                    for (int i = 0; i < packed.Length; i++)
                    {
                        uint key = 0x7f800000;
                        if (i < definition.instances.Length)
                        {
                            var p = packed[i]; Vector3 s = bounds[(int)p.rotationType.z]; s *= p.positionScale.w;
                            var rotated = new Vector3(Single(Single(p.rotationType.y * s.x) + Single(p.rotationType.x * s.z)), s.y, Single(Single(-p.rotationType.x * s.x) + Single(p.rotationType.y * s.z)));
                            var center = new Vector3(Single(p.positionScale.x + rotated.x), Single(p.positionScale.y + rotated.y), Single(p.positionScale.z + rotated.z));
                            var radius = Single(bounds[(int)p.rotationType.z].w * p.positionScale.w); bool inside = p.rotationType.w == 0;
                            foreach (var plane in planes)
                            {
                                var inverse = 1 / plane.normal.magnitude; var n = plane.normal * inverse; var d = Single(plane.distance * inverse);
                                inside &= Single(Single(Single(Single(n.x * center.x) + Single(n.y * center.y)) + Single(n.z * center.z)) + d) >= -radius;
                            }
                            if (inside) { var d = center - origin; key = (uint)BitConverter.SingleToInt32Bits(Single(Single(Single(d.x * d.x) + Single(d.y * d.y)) + Single(d.z * d.z))); }
                        }
                        keys.Add((key, (uint)i));
                    }
                    keys = keys.OrderBy(k => k.key).ThenBy(k => k.id).ToList();
                    var actualKeys = new CrowdSelection.Key[packed.Length]; crowd.Selection.Order.GetData(actualKeys);
                    if (name.StartsWith("capacity") && settings.backend == CrowdBackend.Gpu)
                    {
                        var expectedById = keys.ToDictionary(k => k.id, k => k.key); int separateFloatErrors = 0, doubleErrors = 0, logged = 0;
                        foreach (var key in actualKeys)
                        {
                            if (key.index >= definition.instances.Length) continue;
                            var p = packed[key.index]; Vector3 s = bounds[(int)p.rotationType.z]; s *= p.positionScale.w;
                            var center = (Vector3)p.positionScale + new Vector3(p.rotationType.y * s.x + p.rotationType.x * s.z, s.y, -p.rotationType.x * s.x + p.rotationType.y * s.z);
                            var d = center - origin;
                            uint single = (uint)BitConverter.SingleToInt32Bits(Single(Single(Single(d.x * d.x) + Single(d.y * d.y)) + Single(d.z * d.z)));
                            uint wide = (uint)BitConverter.SingleToInt32Bits((float)((double)d.x * d.x + (double)d.y * d.y + (double)d.z * d.z));
                            if (single != key.distance) separateFloatErrors++; if (wide != expectedById[key.index]) doubleErrors++;
                            if (wide != key.distance && logged++ < 4)
                                Debug.Log("[CrowdKeyPrecision] count=" + definition.instances.Length + "; id=" + key.index + "; delta=" + d.ToString("R") + "; gpu=" + key.distance + "; cpu=" + expectedById[key.index] + "; rounded32=" + single + "; wide=" + wide);
                        }
                        Debug.Log("[CrowdKeyPrecision] count=" + definition.instances.Length + "; everyRounded32VsGpu=" + separateFloatErrors + "; everyWideVsCanonicalCpu=" + doubleErrors);
                    }
                    int rankErrors = 0, keyErrors = 0;
                    for (int i = 0; i < actualKeys.Length; i++) { if (actualKeys[i].index != keys[i].id) rankErrors++; if (actualKeys[i].distance != keys[i].key) keyErrors++; }
                    Check(name + "-every-stable-gpu-rank", rankErrors == 0, rankErrors); Check(name + "-every-distance-key", keyErrors == 0, keyErrors);
                    var args = new uint[definition.prototypes.Length * 10]; crowd.Selection.Arguments.GetData(args);
                    var ids = new uint[packed.Length * definition.prototypes.Length * 2]; crowd.Selection.Indices.GetData(ids);
                    var expected = Enumerable.Range(0, definition.prototypes.Length * 2).Select(_ => new List<uint>()).ToArray();
                    for (int rank = 0; rank < definition.instances.Length && keys[rank].key != 0x7f800000; rank++)
                    { var id = keys[rank].id; expected[definition.instances[id].prototype * 2 + (rank < settings.meshBudget ? 0 : 1)].Add(id); }
                    int argErrors = 0, idErrors = 0; var unique = new HashSet<uint>();
                    for (int b = 0; b < expected.Length; b++)
                    {
                        var p = definition.prototypes[b / 2]; var selected = settings.meshQuality == CrowdMeshQuality.Low ? p.lowMesh : p.highMesh;
                        uint[] words = { b % 2 == 0 ? selected.GetIndexCount(p.submesh) : 6, (uint)expected[b].Count,
                            b % 2 == 0 ? selected.GetIndexStart(p.submesh) : 0, b % 2 == 0 ? selected.GetBaseVertex(p.submesh) : 0, 0 };
                        for (int w = 0; w < 5; w++) if (words[w] != args[b * 5 + w]) argErrors++;
                        for (int j = 0; j < expected[b].Count; j++) { uint id = ids[b * packed.Length + j]; if (id != expected[b][j] || !unique.Add(id)) idErrors++; }
                    }
                    Check(name + "-all-indirect-argument-words", argErrors == 0, argErrors);
                    Check(name + "-every-live-compact-id-no-duplicates", idErrors == 0 && unique.Count == expected.Sum(x => x.Count), idErrors);
                }
                SelectionOracle("single");
                var budgetLow = Own(CrowdBudgetMesh(10)); var budgetHigh = Own(CrowdBudgetMesh(20));
                Check("authored-budget-meshes-500-and1000-triangles", budgetLow.triangles.Length == 1500 && budgetHigh.triangles.Length == 3000);
                definition.prototypes = Enumerable.Range(0, 8).Select(i => new CrowdPrototype { lowMesh = budgetLow, highMesh = budgetHigh,
                    material = new SceneDeferredCamera.MaterialInputs { albedo = new Vector3((i + 1) / 9f, (8 - i) / 9f, (i % 3 + 1) / 4f), mos = new Vector3(.1f, .8f, .3f) } }).ToArray();
                foreach (int count in new[] { 1, 17, 255, 256, 257, 513, 10000 })
                {
                    definition.instances = Enumerable.Range(0, count).Select(i => new CrowdInstance {
                        prototype = i % 8, position = new Vector3((i % 17 - 8) * .25f, (i / 17 % 13 - 6) * .25f, i % 7 * .5f),
                        scale = .125f, hidden = count != 10000 && i % 19 == 0 && i != count - 1 }).ToArray();
                    foreach (int budget in new[] { 0, 1, Math.Min(256, count), count }.Distinct())
                    {
                        settings.meshBudget = budget; settings.backend = CrowdBackend.Gpu;
                        bool requestCapture = count == 10000 && budget == 256 && Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_CROWD") == "1";
                        bool started = requestCapture && RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                        var watch = System.Diagnostics.Stopwatch.StartNew(); Color[] result;
                        try { result = Render(); } finally { watch.Stop(); if (started) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                        if (requestCapture) Check("requested-10000-native-gpu-capture", started && ended);
                        SelectionOracle("count" + count + "-budget" + budget);
                        if (count == 10000)
                        {
                            var drawWords = new uint[16 * 5]; crowd.Selection.Arguments.GetData(drawWords);
                            long actualInstances = 0, actualTriangles = 0;
                            for (int bucket = 0; bucket < 16; bucket++) { actualInstances += drawWords[bucket * 5 + 1]; actualTriangles += (long)drawWords[bucket * 5] * drawWords[bucket * 5 + 1] / 3; }
                            Check("10000-budget" + budget + "-every-instance-actually-drawn", actualInstances == 10000);
                            Debug.Log("[CrowdCost] instances=" + count + "; meshBudget=" + budget + "; backend=" + frame.backend + "; coreGpuBytes=" + frame.crowdResourceBytes +
                                "; lightingBufferBytes=" + frame.lightingBufferBytes + "; dispatches=" + frame.computeDispatches + "; indirectCommands=" + frame.indirectCommands +
                                "; actualDrawnInstances=" + actualInstances + "; actualTriangles=" + actualTriangles + "; syncInclusiveRenderReadbackMs=" + watch.Elapsed.TotalMilliseconds.ToString("R"));
                            SaveSsrPreview("crowd-10000-budget" + budget, result, width, height, false);
                        }
                    }
                    yield return null;
                }
                foreach (bool perspective in new[] { false, true }) foreach (int pose in new[] { 0, 1, 2 })
                {
                    camera.orthographic = !perspective; camera.transform.position = new Vector3(pose * .5f, -pose * .25f, -4 + pose * .5f);
                    camera.transform.rotation = Quaternion.Euler(pose * 3, pose * 7, 0); camera.ResetProjectionMatrix();
                    var p = camera.projectionMatrix; p.m02 += pose * .125f; p.m12 -= pose * .0625f; camera.projectionMatrix = p;
                    settings.meshBudget = 256; Render(); SelectionOracle("projection" + perspective + "-pose" + pose);
                }
                settings.meshQuality = CrowdMeshQuality.High; Render(); SelectionOracle("10000-high1000-triangle-quality"); settings.meshQuality = CrowdMeshQuality.Low;
                camera.orthographic = true; camera.transform.SetPositionAndRotation(new Vector3(0, 0, -4), Quaternion.identity); camera.ResetProjectionMatrix();
                // Both sides of the maximum-capacity boundary exercise the full 256-block scan,
                // all live IDs, current indirect rasterization and the explicit CPU backend.
                foreach (int count in new[] { CrowdDefinition.MaximumInstances - 1, CrowdDefinition.MaximumInstances })
                {
                    definition.instances = Enumerable.Range(0, count).Select(i => new CrowdInstance {
                        prototype = i % 8, position = new Vector3((i % 257 - 128) * .015f, (i / 257 - 127) * .01f, i % 7 * .05f), scale = .015f }).ToArray();
                    settings.meshBudget = 256; settings.backend = CrowdBackend.Gpu; var maxGpu = Render(); SelectionOracle("capacity" + count);
                    var words = new uint[80]; crowd.Selection.Arguments.GetData(words);
                    Check("capacity" + count + "-all-actually-drawn", Enumerable.Range(0, 16).Sum(i => (long)words[i * 5 + 1]) == count);
                    settings.backend = CrowdBackend.Cpu; var maxCpu = Render(); SelectionOracle("capacity" + count + "-cpu");
                    Check("capacity" + count + "-gpu-cpu-whole-rgba", Difference(maxGpu, maxCpu) <= .0002f, Difference(maxGpu, maxCpu));
                    settings.backend = CrowdBackend.Gpu; yield return null;
                }
                definition.prototypes = new[] { prototype }; definition.instances = new[] { new CrowdInstance() }; settings.meshBudget = 1;
                Render(); SelectionOracle("shrink-back-to-single");
                void Reject(string name, Action change, Action restore)
                {
                    change(); bool ok = crowd.TryRender(target, camera, definition, settings, poses, out _);
                    Check("reject-" + name, !ok && crowd.UnavailableReason != null && crowd.AllocatedBuffers == 0 && crowd.AllocatedTargets == 0 && crowd.ResourceBytes == 0);
                    restore(); Render();
                }
                Reject("disabled", () => settings.enabled = false, () => settings.enabled = true);
                Reject("empty", () => definition.instances = Array.Empty<CrowdInstance>(), () => definition.instances = new[] { new CrowdInstance() });
                Reject("prototype-index", () => definition.instances[0].prototype = 8, () => definition.instances[0].prototype = 0);
                Reject("zero-scale", () => definition.instances[0].scale = 0, () => definition.instances[0].scale = 1);
                Reject("nan-yaw", () => definition.instances[0].yawDegrees = float.NaN, () => definition.instances[0].yawDegrees = 0);
                Reject("negative-budget", () => settings.meshBudget = -1, () => settings.meshBudget = 1);
                Reject("aggregate-budget-before-gpu-allocation", () => { settings.maximumResourceMiB = 1; settings.captureResolution = 512; }, () => { settings.maximumResourceMiB = 256; settings.captureResolution = 64; });
                Reject("missing-high-quality", () => { settings.meshQuality = CrowdMeshQuality.High; prototype.highMesh = null; }, () => { settings.meshQuality = CrowdMeshQuality.Low; prototype.highMesh = mesh; });
                Reject("invalid-pose-count", () => poses = new CrowdPose[2], () => poses = null);
                Reject("partial-viewport", () => camera.rect = new Rect(0, 0, .5f, 1), () => camera.rect = new Rect(0, 0, 1, 1));
                Reject("renderer-dependent-gi", () => prototype.gi.source = SceneGiSource.RendererLightmap, () => prototype.gi.source = SceneGiSource.None);
                Reject("negative-tint", () => definition.instances[0].tint = new Vector3(-1, 1, 1), () => definition.instances[0].tint = Vector3.one);
                Reject("nonfinite-material", () => prototype.material.albedo = new Vector3(float.NaN, .5f, .5f), () => prototype.material.albedo = new Vector3(.31f, .49f, .67f));
                Reject("too-many-types", () => definition.prototypes = Enumerable.Repeat(prototype, 9).ToArray(), () => definition.prototypes = new[] { prototype });
                Reject("over-instance-capacity", () => definition.instances = new CrowdInstance[CrowdDefinition.MaximumInstances + 1], () => definition.instances = new[] { new CrowdInstance() });
                Reject("unsupported-backend", () => settings.backend = (CrowdBackend)17, () => settings.backend = CrowdBackend.Gpu);
                Reject("released-target", () => target.Release(), () => target.Create());
                Reject("extreme-camera-origin", () => camera.transform.position = new Vector3(1e9f, 0, -4), () => camera.transform.position = new Vector3(0, 0, -4));
                var deadMap = Target(3, 3); deadMap.Release();
                Reject("released-borrowed-texture", () => prototype.material.albedoMap = deadMap, () => prototype.material.albedoMap = null);
                var ownMap = frame.linearEyeDepth;
                Reject("owned-output-material-alias", () => prototype.material.albedoMap = ownMap, () => prototype.material.albedoMap = null);
                var unreadable = Own(Instantiate(mesh)); unreadable.UploadMeshData(true);
                Reject("unreadable-selected-geometry", () => prototype.lowMesh = unreadable, () => prototype.lowMesh = mesh);
                var twoFrames = Own(Instantiate(mesh)); var zeroDelta = new Vector3[mesh.vertexCount];
                twoFrames.AddBlendShapeFrame("unsupported-multiframe", 100, zeroDelta, zeroDelta, zeroDelta); twoFrames.AddBlendShapeFrame("unsupported-multiframe", 200, zeroDelta, zeroDelta, zeroDelta);
                Reject("multiframe-shape-without-silent-truncation", () => prototype.lowMesh = twoFrames, () => prototype.lowMesh = mesh);
                var tooManyInfluences = Own(Instantiate(mesh)); tooManyInfluences.bindposes = Enumerable.Repeat(Matrix4x4.identity, 5).ToArray();
                using (var counts = new Unity.Collections.NativeArray<byte>(Enumerable.Repeat((byte)5, mesh.vertexCount).ToArray(), Unity.Collections.Allocator.Temp))
                using (var weights = new Unity.Collections.NativeArray<BoneWeight1>(Enumerable.Range(0, mesh.vertexCount * 5).Select(i => new BoneWeight1 { boneIndex = i % 5, weight = .2f }).ToArray(), Unity.Collections.Allocator.Temp))
                    tooManyInfluences.SetBoneWeights(counts, weights);
                Reject("five-influences-without-silent-truncation", () => prototype.lowMesh = tooManyInfluences, () => prototype.lowMesh = mesh);
                prototype.lowMesh = skinMesh; poses = new[] { new CrowdPose { root = rig.transform, bones = skin.bones, blendShapeWeights = new[] { 0f } } };
                Reject("missing-current-bone", () => poses[0].bones = new Transform[] { null, b1.transform }, () => poses[0].bones = skin.bones);
                Reject("nonfinite-current-shape", () => poses[0].blendShapeWeights[0] = float.NaN, () => poses[0].blendShapeWeights[0] = 0);
                prototype.lowMesh = mesh; poses = null; Render();
                var blocker = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); blocker.name = "Independent current crowd shadow caster"; blocker.layer = 27;
                blocker.transform.position = new Vector3(-.13f, .07f, -.8f); blocker.transform.localScale = new Vector3(.57f, .93f, 1);
                var caster = new SceneShadowCaster { renderer = blocker.GetComponent<Renderer>(), cull = CullMode.Off };
                var shadowPoint = new SceneDecalLight { shape = SceneDecalLightShape.Point, position = new Vector3(0, 0, -2), range = 8, radiance = new Vector3(.71f, .43f, .59f) };
                var beforeShadowLights = locals.lights; locals.enabled = true; locals.lights = new[] { shadowPoint };
                locals.shadows.casters = new[] { caster }; locals.shadows.tileResolution = 64; locals.shadows.maxShadowedLights = 1;
                var main = lighting.mainLightShadow; main.origin = new Vector3(0, 0, -3); main.halfSize = Vector2.one * 2; main.resolution = 128; main.farPlane = 8; main.casters = new[] { caster };
                forward.settings.mainLightShadow = main;
                foreach (int budget in new[] { 1, 0 })
                {
                    settings.meshBudget = budget; shadowPoint.shadow.enabled = false; main.enabled = false; var clear = LitPair("shadow-lod" + budget + "-clear");
                    shadowPoint.shadow.enabled = true; var localShadow = LitPair("shadow-lod" + budget + "-point");
                    Check("shadow-lod" + budget + "-current-point-positive", Difference(clear, localShadow) > .001f, Difference(clear, localShadow));
                    Check("shadow-lod" + budget + "-point-owned-target", crowd.AllocatedTargets == 6);
                    main.enabled = true; var both = LitPair("shadow-lod" + budget + "-point-main");
                    Check("shadow-lod" + budget + "-current-main-positive", Difference(localShadow, both) > .001f, Difference(localShadow, both));
                    Check("shadow-lod" + budget + "-independent-main-local-targets", crowd.AllocatedTargets == 7);
                    if (budget == 1) { var expected = Native(); Check("shadow-near-real-native-camera-whole-rgba", Difference(both, expected) <= .0002f, Difference(both, expected)); }
                    blocker.transform.position += new Vector3(.37f, -.11f, 0); var moved = LitPair("shadow-lod" + budget + "-current-caster");
                    Check("shadow-lod" + budget + "-caster-move-positive", Difference(both, moved) > .001f, Difference(both, moved));
                    blocker.transform.position -= new Vector3(.37f, -.11f, 0);
                }
                main.enabled = false; shadowPoint.shadow.enabled = false; locals.enabled = false; locals.lights = beforeShadowLights; blocker.SetActive(false);
                settings.meshBudget = 1; lighting.backend = SceneForwardLightBackend.BruteForce; Render();
                var adapter = host.AddComponent<CrowdCamera>(); adapter.definition = definition; adapter.settings = settings;
                camera.Render(); var adapted = ReadSceneTarget(target); var seq = adapter.RenderSequence;
                Check("adapter-actual-camera-render-and-depth", adapter.UnavailableReason == null && seq == 1 && adapter.LinearEyeDepth != null && camera.cullingMask == 0);
                adapter.enabled = false; var direct = Render();
                Check("adapter-standalone-whole-rgba", Difference(adapted, direct) <= .00002f, Difference(adapted, direct));
                Check("adapter-disabled-releases", adapter.AllocatedBuffers == 0 && adapter.LinearEyeDepth == null);
                Check("source-mesh-vertices-unchanged", mesh.vertices.SequenceEqual(positions));

                // Actual host opaque depth and ordinary transparent queue, independently
                // compared against a camera-scheduled native opaque model in the same scene.
                rig.SetActive(false);
                using var sceneLights = new SceneForwardLightResources();
                if (!sceneLights.Prepare(camera, lighting, width, height, out var sceneError)) throw new InvalidOperationException(sceneError);
                using var flatLights = new SceneForwardLightResources();
                var flatSettings = new SceneForwardLightSettings { enabled = true, lightRadiance = Vector3.zero, ambientIrradiance = Vector3.zero, backend = SceneForwardLightBackend.BruteForce };
                if (!flatLights.Prepare(camera, flatSettings, width, height, out sceneError)) throw new InvalidOperationException(sceneError);
                Material SceneMaterial(SceneDeferredCamera.MaterialInputs inputs, bool transparent)
                {
                    var material = Own(new Material(Resources.Load<Shader>(transparent ? "SceneForwardLighting" : "CrowdNativeReference")));
                    flatLights.Bind(material); SceneDeferredCamera.BindInputs(material, inputs); material.SetVector("_VertexScale", Vector3.one);
                    material.SetFloat("_SceneGiMode", 0); material.SetFloat("_Cutoff", .001f); material.SetFloat("_Cull", 2); material.SetFloat("_ZWrite", 1); material.SetFloat("_Additive", 0);
                    if (transparent) { material.renderQueue = 3000; material.SetFloat("_DestinationBlend", (int)BlendMode.OneMinusSrcAlpha); }
                    return material;
                }
                GameObject Panel(string name, Vector3 position, Vector3 scale, Vector3 emission, bool transparent = false)
                {
                    var go = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.name = name; go.layer = 26; go.transform.position = position; go.transform.localScale = scale;
                    go.GetComponent<Renderer>().sharedMaterial = SceneMaterial(new SceneDeferredCamera.MaterialInputs { albedo = Vector3.zero, emission = emission, alpha = transparent ? .43f : 1 }, transparent); return go;
                }
                var foreground = Panel("Crowd native foreground occluder", new Vector3(-.31f, 0, -.65f), new Vector3(.57f, 1.23f, 1), new Vector3(.17f, .61f, .23f));
                var backgroundPanel = Panel("Crowd native background", new Vector3(.17f, 0, .65f), new Vector3(1.73f, 1.81f, 1), new Vector3(.31f, .13f, .07f));
                var transparentPanel = Panel("Crowd ordinary transparent queue", new Vector3(.21f, 0, -.85f), new Vector3(1.19f, 1.13f, 1), new Vector3(.13f, .19f, .71f), true);
                var sourceMaterial = SceneMaterial(prototype.material, false); sceneLights.Bind(sourceMaterial); sourceMaterial.SetFloat("_ReceiverGroup", prototype.receiverGroup);
                var borrowedMaterial = model.GetComponent<Renderer>().sharedMaterial; model.GetComponent<Renderer>().sharedMaterial = sourceMaterial;
                foreach (int depthCase in new[] { 0, 1, 2 })
                {
                    transparentPanel.SetActive(depthCase != 0); transparentPanel.transform.position = new Vector3(.21f, 0, depthCase == 1 ? -.85f : .85f);
                    camera.cullingMask = (1 << 25) | (1 << 26); adapter.enabled = false; camera.Render(); var expectedScene = ReadSceneTarget(target);
                    camera.cullingMask = 1 << 26; adapter.enabled = true; camera.Render(); var actualScene = ReadSceneTarget(target);
                    Check("native-host-depth-and-transparent-case" + depthCase + "-whole-rgba", adapter.UnavailableReason == null && Difference(expectedScene, actualScene) <= .0002f, Difference(expectedScene, actualScene));
                    SaveSsrPreview("crowd-host-composition-" + depthCase, actualScene, width, height, false);
                }
                var firstCameraImage = ReadSceneTarget(target); var firstEye = ReadSceneTarget(adapter.LinearEyeDepth); var firstEyeObject = adapter.LinearEyeDepth;
                var siblingHost = Own(new GameObject("Independent crowd sibling camera")); var sibling = siblingHost.AddComponent<Camera>(); sibling.CopyFrom(camera); sibling.enabled = false;
                sibling.transform.SetPositionAndRotation(new Vector3(.37f, .19f, -4.7f), Quaternion.Euler(3, 7, 0)); sibling.targetTexture = Target(width, height);
                sibling.cullingMask = 0; // Only its own crowd; host panel materials belong to the first view.
                var siblingAdapter = siblingHost.AddComponent<CrowdCamera>(); siblingAdapter.definition = definition;
                siblingAdapter.settings = new CrowdSettings { enabled = true, meshBudget = 0, captureResolution = 32, backend = CrowdBackend.Gpu, lighting = lighting };
                sibling.Render();
                Check("sibling-owned-depth-and-target-isolation", siblingAdapter.UnavailableReason == null && siblingAdapter.LinearEyeDepth != null && siblingAdapter.LinearEyeDepth != firstEyeObject &&
                    Difference(firstCameraImage, ReadSceneTarget(target)) == 0 && Difference(firstEye, ReadSceneTarget(firstEyeObject)) == 0);
                camera.Render(); var repeated = ReadSceneTarget(target);
                Check("sibling-then-current-camera-repeat-whole-rgba", Difference(firstCameraImage, repeated) <= .00002f, Difference(firstCameraImage, repeated));
                siblingAdapter.enabled = false; siblingHost.SetActive(false); adapter.enabled = false; camera.cullingMask = 0;
                foreground.SetActive(false); backgroundPanel.SetActive(false); transparentPanel.SetActive(false); model.GetComponent<Renderer>().sharedMaterial = borrowedMaterial;
                Render();

                // Face-coded, emission-only source makes wrong quadrant selection observable
                // across the entire image, independently of PBR response and angular approximation.
                var savedUv = mesh.uv; var savedMaterial = prototype.material; var savedBackground = camera.backgroundColor;
                var savedLight = lighting.lightRadiance; var savedAmbient = lighting.ambientIrradiance;
                var savedReferenceLight = forward.settings.lightRadiance; var savedReferenceAmbient = forward.settings.ambientIrradiance;
                var faceColors = new[] { new Color(.81f, .13f, .21f, 1), new Color(.19f, .77f, .31f, 1), new Color(.23f, .37f, .89f, 1),
                    new Color(.83f, .69f, .17f, 1), new Color(.71f, .19f, .73f, 1), new Color(.13f, .73f, .79f, 1) };
                var faceMap = Own(new Texture2D(6, 1, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp }); faceMap.SetPixels(faceColors); faceMap.Apply();
                mesh.uv = mesh.normals.Select(n =>
                {
                    int face = n.z < -.5f ? 0 : n.x > .5f ? 1 : n.z > .5f ? 2 : n.x < -.5f ? 3 : n.y > 0 ? 4 : 5;
                    return new Vector2((face + .5f) / 6, .5f);
                }).ToArray(); definition.contentVersion++;
                prototype.material = new SceneDeferredCamera.MaterialInputs { albedo = Vector3.zero, emission = Vector3.one, emissionMap = faceMap };
                forward.settings.surfaces[0].inputs = prototype.material;
                lighting.lightRadiance = lighting.ambientIrradiance = forward.settings.lightRadiance = forward.settings.ambientIrradiance = Vector3.zero;
                camera.backgroundColor = Color.clear;
                foreach (float yaw in new[] { 0f, 37f }) foreach (float azimuth in new[] { -180f, -135.01f, -134.99f, -90f, -45.01f, -44.99f, 0f, 44.99f, 45.01f, 90f, 134.99f, 135.01f, 180f })
                {
                    definition.instances[0].yawDegrees = yaw; model.transform.rotation = Quaternion.Euler(0, yaw, 0);
                    var direction = Quaternion.Euler(0, azimuth, 0) * Vector3.back;
                    camera.transform.SetPositionAndRotation(direction * 4, Quaternion.LookRotation(-direction, Vector3.up)); camera.ResetProjectionMatrix();
                    settings.meshBudget = 1; var currentNear = Render(); var expectedNear = Native();
                    string name = "four-view-yaw" + yaw.ToString("R") + "-azimuth" + azimuth.ToString("R");
                    Check(name + "-near-native-whole-rgba", Difference(currentNear, expectedNear) <= .0002f, Difference(currentNear, expectedNear));
                    settings.meshBudget = 0; var currentFar = Render();
                    int quadrant = ((Mathf.FloorToInt((yaw - azimuth) / 90 + .5f) % 4) + 4) % 4;
                    var expectedColor = faceColors[quadrant]; float colorError = 0; int coverageCount = 0, intersection = 0, union = 0; double squared = 0;
                    for (int pixel = 0; pixel < currentFar.Length; pixel++)
                    {
                        bool hit = currentFar[pixel].a > .5f, nativeHit = expectedNear[pixel].a > .5f;
                        if (hit) coverageCount++; if (hit && nativeHit) intersection++; if (hit || nativeHit) union++;
                        var expected = hit ? expectedColor : Color.clear;
                        for (int c = 0; c < 4; c++)
                        { colorError = Mathf.Max(colorError, Mathf.Abs(currentFar[pixel][c] - expected[c])); double d = currentFar[pixel][c] - expectedNear[pixel][c]; squared += d * d; }
                    }
                    float iou = union == 0 ? 0 : (float)intersection / union, rms = (float)Math.Sqrt(squared / (currentFar.Length * 4));
                    Check(name + "-every-pixel-independent-quadrant", colorError <= .0002f && coverageCount > 50, colorError);
                    Check(name + "-finite-four-view-coverage-approximation", iou >= .35f, 1 - iou);
                    Check(name + "-finite-four-view-whole-rgba-rms", rms <= .30f, rms);
                    if (yaw == 0 && azimuth == 44.99f)
                    { SaveSsrPreview("crowd-four-view-oblique-native", expectedNear, width, height, false); SaveSsrPreview("crowd-four-view-oblique-impostor", currentFar, width, height, false); }
                }
                mesh.uv = savedUv; definition.contentVersion++; prototype.material = savedMaterial; forward.settings.surfaces[0].inputs = savedMaterial;
                model.transform.rotation = Quaternion.identity; camera.transform.SetPositionAndRotation(new Vector3(0, 0, -4), Quaternion.identity); camera.ResetProjectionMatrix();
                var rowColors = Enumerable.Range(0, 8).Select(i => new Color((i + 1) / 9f, (8 - i) / 9f, (i % 3 + 1) / 4f, 1)).ToArray();
                definition.prototypes = Enumerable.Range(0, 8).Select(i => new CrowdPrototype { lowMesh = budgetLow, highMesh = budgetHigh,
                    material = new SceneDeferredCamera.MaterialInputs { albedo = Vector3.zero, emission = (Vector3)(Vector4)rowColors[i] } }).ToArray();
                definition.instances = Enumerable.Range(0, 8).Select(i => new CrowdInstance { prototype = i, position = new Vector3((i % 4 - 1.5f) * 1.1f, i / 4 == 0 ? -.75f : .75f, 0), scale = .5f }).ToArray();
                camera.orthographicSize = 2; settings.meshBudget = 0; var rows = Render();
                var centers = definition.instances.Select(p => camera.WorldToViewportPoint(p.position)).ToArray(); var hitsPerRow = new int[8]; float rowError = 0;
                for (int pixel = 0; pixel < rows.Length; pixel++)
                {
                    Color expected = Color.clear;
                    if (rows[pixel].a > .5f)
                    {
                        var point = new Vector2((pixel % width + .5f) / width, (pixel / width + .5f) / height);
                        int row = Enumerable.Range(0, 8).OrderBy(i => ((Vector2)centers[i] - point).sqrMagnitude).First(); expected = rowColors[row]; hitsPerRow[row]++;
                    }
                    for (int c = 0; c < 4; c++) rowError = Mathf.Max(rowError, Mathf.Abs(rows[pixel][c] - expected[c]));
                }
                Check("eight-current-atlas-rows-every-output-pixel", rowError <= .0002f && hitsPerRow.All(n => n > 10), rowError);
                SaveSsrPreview("crowd-eight-current-atlas-rows", rows, width, height, false);
                definition.prototypes = new[] { prototype }; definition.instances = new[] { new CrowdInstance() };
                lighting.lightRadiance = savedLight; lighting.ambientIrradiance = savedAmbient; forward.settings.lightRadiance = savedReferenceLight; forward.settings.ambientIrradiance = savedReferenceAmbient;
                camera.backgroundColor = savedBackground; camera.orthographicSize = size; settings.meshBudget = 1; Render();
            }
            finally
            {
                crowd.Dispose(); RenderTexture.active = active;
                foreach (var item in _owned) { if (item is RenderTexture rt) rt.Release(); if (item != null) Destroy(item); }
                _owned.Clear(); for (int i = 0; i < previous.Length; i++) if (previous[i] != null) previous[i].forceRenderingOff = forced[i];
            }
            yield return null;
        }

        private static Mesh CrowdBudgetMesh(int sides)
        {
            const int latitudes = 26;
            var points = new List<Vector3> { new Vector3(0, .75f, 0) };
            for (int row = 1; row < latitudes; row++) for (int column = 0; column < sides; column++)
            {
                float theta = Mathf.PI * row / latitudes, phi = Mathf.PI * 2 * column / sides;
                points.Add(new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi) * .35f, Mathf.Cos(theta) * .75f, Mathf.Sin(theta) * Mathf.Sin(phi) * .25f));
            }
            int bottom = points.Count; points.Add(new Vector3(0, -.75f, 0)); var indices = new List<int>();
            void Triangle(int a, int b, int c)
            {
                if (Vector3.Dot(Vector3.Cross(points[b] - points[a], points[c] - points[a]), points[a] + points[b] + points[c]) < 0) { int swap = b; b = c; c = swap; }
                indices.Add(a); indices.Add(b); indices.Add(c);
            }
            for (int column = 0; column < sides; column++)
            {
                int next = (column + 1) % sides; Triangle(0, 1 + column, 1 + next);
                for (int row = 0; row < latitudes - 2; row++)
                {
                    int a = 1 + row * sides + column, b = 1 + row * sides + next;
                    Triangle(a, a + sides, b); Triangle(b, a + sides, b + sides);
                }
                Triangle(bottom, 1 + (latitudes - 2) * sides + column, 1 + (latitudes - 2) * sides + next);
            }
            var mesh = new Mesh { name = "Independent crowd budget ellipsoid " + sides * 50 + " triangles" };
            mesh.SetVertices(points); mesh.SetTriangles(indices, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
    }
}
