using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyPerformanceAuthoring(Report report)
        {
            yield return null;
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "performance-" + name, ok, error);
            string path = Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_PERFORMANCE_BUNDLE");
            if (string.IsNullOrEmpty(path)) { Check("external-dcc-bundle-not-supplied", true); yield break; }
            var others = FindObjectsOfType<Renderer>(); var flags = others.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in others) r.forceRenderingOff = true;
            var active = RenderTexture.active; AssetBundle bundle = null; PerformancePlayer player = null;
            VertexLocatorRig rig = null; FaceExpressionRenderer.VertexSource source = null; FaceExpressionRenderer face = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(path); Check("actual-imported-bundle", bundle != null);
                var asset = bundle.LoadAllAssets<PerformanceClipAsset>().Single();
                var imported = bundle.LoadAllAssets<GameObject>().SelectMany(g => g.GetComponentsInChildren<ImportedPerformance>(true)).Single();
                Check("actual-sidecar-and-fbx-data", asset.clip != null && imported.clip != null && JsonUtility.ToJson(asset.clip) == JsonUtility.ToJson(imported.clip));
                Check("dcc-baked-keys", asset.clip.morphs[0].keys.Length == 31 && asset.clip.decals[0].tracks[0].keys.Length == 31);
                Check("json-roundtrip", PerformanceClip.TryParse(JsonUtility.ToJson(asset.clip), out var definition, out _));
                var invalid = JsonUtility.FromJson<PerformanceClip>(JsonUtility.ToJson(definition)); invalid.schema = "unknown";
                Check("unknown-schema-rejected", !invalid.TryCopy(out _, out _));
                invalid.schema = PerformanceClip.Schema; invalid.morphs[0].keys[1].seconds = 0;
                Check("unordered-key-rejected", !invalid.TryCopy(out _, out _));
                Check("oversized-json-rejected", !PerformanceClip.TryParse(new string(' ', PerformanceClip.MaxJsonBytes + 1), out _, out _));
                Check("null-json-rejected", !PerformanceClip.TryParse(null, out _, out _));
                invalid = JsonUtility.FromJson<PerformanceClip>(JsonUtility.ToJson(definition)); invalid.effects[0].prefab = "../not-a-path";
                Check("resource-path-rejected", !invalid.TryCopy(out _, out _));
                invalid = JsonUtility.FromJson<PerformanceClip>(JsonUtility.ToJson(definition)); invalid.effects[0].duration = 61;
                Check("interval-capacity-rejected", !invalid.TryCopy(out _, out _));

                var camera = Own(new GameObject("Performance acceptance camera")).AddComponent<Camera>(); camera.enabled = false; camera.cullingMask = (1 << 24) | (1 << 25);
                camera.orthographic = true; camera.orthographicSize = 1; camera.aspect = 143f / 103; camera.nearClipPlane = .1f; camera.farClipPlane = 10;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.11f, .21f, .31f, .61f); camera.allowMSAA = false; camera.allowHDR = true;
                var target = Own(new RenderTexture(143, 103, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var owner = Own(new GameObject("Independent authored face")); owner.layer = 24;
                var driver = owner.AddComponent<VL.FaceSystem.VLActorFaceModel>(); var filter = owner.AddComponent<MeshFilter>(); var receiver = owner.AddComponent<MeshRenderer>();
                var mesh = Own(new Mesh { name = "Independent authoring triangle" }); mesh.vertices = new[] { new Vector3(-.55f, -.35f, 3), new Vector3(.4f, -.35f, 3), new Vector3(-.5f, .5f, 3) };
                mesh.normals = Enumerable.Repeat(Vector3.back, 3).ToArray(); mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 3).ToArray();
                mesh.uv = new[] { new Vector2(.25f, .5f), new Vector2(.25f, .5f), new Vector2(.25f, .5f) }; mesh.triangles = new[] { 0, 2, 1 }; mesh.RecalculateBounds(); filter.sharedMesh = mesh;
                var material = Own(new Material(MaterialRepairer.FallbackShader())); material.SetFloat("_Cull", 0); material.SetFloat("_OutlineEnabled", 0); material.SetFloat("_VertexColor", 0); receiver.sharedMaterial = material;
                var atlas = Own(new Texture2D(2, 1, TextureFormat.RGBAFloat, false, true)); atlas.filterMode = FilterMode.Point; atlas.wrapMode = TextureWrapMode.Clamp;
                atlas.SetPixels(new[] { new Color(.9f, .2f, .1f, 1), new Color(.1f, .4f, .9f, 1) }); atlas.Apply();
                var decalAtlas = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)); decalAtlas.SetPixel(0, 0, new Color(1, 1, 1, .7f)); decalAtlas.Apply();
                var bone = Own(new GameObject("Authored face bone")).transform; driver.bones = new[] { bone }; driver.bindposes = new[] { Matrix4x4.identity };
                driver.boneWeightAndIndices = new uint[12]; for (int i = 0; i < 3; i++) driver.boneWeightAndIndices[i * 4] = 65535u << 16;
                driver.blendShapes.Add(new VL.FaceSystem.VLFaceBlendShape { blendShapeName = "smile" });
                driver.blendShapes[0].blendShapeVertices.Add(new VL.FaceSystem.VLFaceBlendShapeVertex { vertIndex = 0, position = new Vector3(.3f, .2f, 0) });
                face = owner.AddComponent<FaceExpressionRenderer>(); Check("face-initialize", face.Initialize(driver, driver)); face.SetAutomaticBlinkEnabled(false); face.InitializeGaze(null, null, camera);
                var decal = Own(new GameObject("Explicit authored decal")).AddComponent<FaceDecalProjector>(); decal.opacity = 0;
                var layer = camera.gameObject.AddComponent<FaceDecalLayer>(); layer.decalsEnabled = true; layer.atlas = decalAtlas; layer.projectors = new[] { decal };
                layer.receivers = new[] { new FaceDecalRenderer.Receiver { surface = new SceneDepthData.Surface { renderer = receiver, cull = CullMode.Off } } };
                Check("authored-locators-create", VertexLocatorRig.TryCreate(definition.locators, out rig, out _));
                Check("current-face-source", face.TryCreateVertexSource(rig.GetVertexIndices(), out source, out _));
                var marker = Own(new GameObject("Authored marker template")); marker.SetActive(false); marker.layer = 25;
                marker.AddComponent<MeshFilter>().sharedMesh = Own(new Mesh { vertices = new[] { new Vector3(-.045f, -.045f, 0), new Vector3(.045f, -.045f, 0), new Vector3(0, .06f, 0) }, triangles = new[] { 0, 2, 1 } });
                var markerMaterial = Own(new Material(Shader.Find("Hidden/PhotoStudio/FaceDecalProbe"))); markerMaterial.SetVector("_ProbeTint", new Vector4(.1f, .95f, .2f, 1)); marker.AddComponent<MeshRenderer>().sharedMaterial = markerMaterial;
                var bindings = new PerformanceBindings(); bindings.morphs.Add("face.smile", new PerformanceBindings.Morph { faceDriver = driver, index = 0 });
                bindings.decals.Add("cheek", decal); bindings.prefabs.Add("my.marker", marker); bindings.attachments.Add("tear", rig.GetAttachment("tear"));
                bindings.textures.Add("my.atlas", atlas); bindings.materials.Add("face.surface", new PerformanceBindings.MaterialSlot { renderer = receiver, slot = 0 });
                Color[] Render() { camera.Render(); Check("decal-layer-" + report.checks.Count, layer.UnavailableReason == null); return ReadSceneTarget(target); }
                bool Exact(Color[] a, Color[] b) => a.Length == b.Length && a.Zip(b, (x, y) => x.Equals(y)).All(x => x);
                driver.SetWeight(0, 0); face.ApplyCurrentWeights(); source.TryUpdate(rig, Vector3.one, out _); var off = Render();
                Check("bind-sidecar", PerformancePlayer.TryCreate(definition, bindings, out player, out _));
                Check("effects-require-pose-phase", !player.TrySampleEffects() && player.ActiveEffectCount == 0);
                var inputs = new[] { 0.0, .1, .3, .5, .7, .85, .3, 1.3, -.1, 2.3 };
                var expectedImages = new Color[inputs.Length][]; Color[] reference = null;
                for (int backend = 0; backend < 2; backend++)
                {
                    face.GpuDeformationEnabled = backend == 1;
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        double time = inputs[i], local = time >= 0 ? time % 1 : 0;
                        Check(backend + "-" + i + "-pose", player.TrySamplePose(time));
                        float error = Mathf.Abs(driver.GetWeight(0) - (float)local); Check(backend + "-" + i + "-independent-morph", error <= .000001f, error);
                        error = Mathf.Abs(decal.opacity - (float)(local * .8)); Check(backend + "-" + i + "-independent-decal", error <= .000001f, error);
                        face.ApplyCurrentWeights(); Check(backend + "-" + i + "-current-locator", source.TryUpdate(rig, Vector3.one, out _));
                        Check(backend + "-" + i + "-effects-after-geometry", player.TrySampleEffects());
                        bool effectActive = time >= 0 && local >= definition.effects[0].start && local < definition.effects[0].start + definition.effects[0].duration;
                        Check(backend + "-" + i + "-keyed-effect-interval", player.ActiveEffectCount == (effectActive ? 1 : 0));
                        if (time >= 0)
                        {
                            var frame = receiver.sharedMaterial.GetVector("_ActorTextureFrame");
                            Check(backend + "-" + i + "-independent-atlas", frame == new Vector4(.5f, 1, Math.Floor(local * 2) % 2 == 0 ? 0 : .5f, 0));
                        }
                        var image = Render();
                        if (backend == 0) expectedImages[i] = image; else Check("cpu-gpu-" + i + "-whole-rgba-exact", Exact(expectedImages[i], image));
                        if (i == 2) { if (backend == 0) reference = image; Check(backend + "-positive-authored-image", !Exact(off, image)); SaveSsrPreview("performance-" + backend + "-authored", image, 143, 103, false); }
                        if (i == 6 || i == 7 || i == 9) Check(backend + "-seek-" + i + "-whole-rgba-exact", Exact(reference, image));
                        if (i == 8) Check(backend + "-negative-restored-whole-image", Exact(off, image));
                    }
                }
                var complete = Render();
                Check("actual-decal-receiver-submitted", layer.SubmittedReceivers == 1);
                layer.decalsEnabled = false; Check("positive-decal-rendered-contribution", !Exact(complete, Render())); layer.decalsEnabled = true;
                camera.cullingMask = 1 << 24; Check("positive-prefab-rendered-contribution", !Exact(complete, Render())); camera.cullingMask |= 1 << 25;
                var replacement = receiver.sharedMaterial; receiver.sharedMaterial = material;
                Check("positive-material-rendered-contribution", !Exact(complete, Render())); receiver.sharedMaterial = replacement;
                player.Dispose(); player = null; face.GpuDeformationEnabled = false; face.ApplyCurrentWeights();
                Check("stop-restores-borrowed-targets", driver.GetWeight(0) == 0 && decal.opacity == 0 && receiver.sharedMaterial == material);
                Check("stop-whole-image-exact", Exact(off, Render()));
                var direct = new PerformanceClip { duration = 1, loop = true,
                    morphs = new[] { new PerformanceClip.Morph { target = "face.smile", keys = new[] { new FaceDecalAnimation.Key(0, 0), new FaceDecalAnimation.Key(1, 1) } } },
                    decals = new[] { new PerformanceClip.Decal { target = "cheek", baseline = new FaceDecalPose { positionOffset = new Vector3(0, 0, 3), size = Vector3.one * 2, tint = new Vector4(.9f, .1f, .2f, 1), opacity = 0 },
                        tracks = new[] { new FaceDecalAnimation.Track { channel = FaceDecalChannel.Opacity, keys = new[] { new FaceDecalAnimation.Key(0, 0), new FaceDecalAnimation.Key(1, .8f) } } } } },
                    effects = new[] { new PerformanceClip.Effect { id = "direct-marker", prefab = "my.marker", attachment = "tear", start = .2, duration = .6, seed = 713 } },
                    materials = new[] { new PerformanceClip.MaterialEffect { id = "direct-atlas", target = "face.surface", colorTexture = "my.atlas", start = 0, duration = 1, columns = 2, rows = 1, fps = 2 } } };
                Check("independent-typed-direct-bind", PerformancePlayer.TryCreate(direct, bindings, out player, out _));
                Check("independent-typed-direct-pose", player.TrySamplePose(.5)); face.ApplyCurrentWeights(); source.TryUpdate(rig, Vector3.one, out _); player.TrySampleEffects();
                Check("typed-direct-imported-whole-rgba-exact", Exact(expectedImages[3], Render())); player.Dispose(); player = null;
                Check("bind-actual-fbx-owner", PerformancePlayer.TryCreate(imported.clip, bindings, out player, out _));
                Check("fbx-owner-pose", player.TrySamplePose(.3)); face.ApplyCurrentWeights(); source.TryUpdate(rig, Vector3.one, out _); player.TrySampleEffects();
                Check("actual-fbx-sidecar-whole-rgba-exact", Exact(reference, Render()));
                player.Stop();
                Check("clip-input-is-copied", player.TrySamplePose(.3));
                float before = driver.GetWeight(0); imported.clip.morphs[0].keys[0].value = 10;
                Check("mutated-source-does-not-change-player", player.TrySamplePose(.3) && driver.GetWeight(0) == before);
                Check("nan-clock-fails-restores", !player.TrySamplePose(double.NaN) && driver.GetWeight(0) == 0 && decal.opacity == 0 && receiver.sharedMaterial == material && player.ActiveEffectCount == 0);
                player.TrySamplePose(.3); var foreign = Own(new Material(material)); receiver.sharedMaterial = foreign;
                Check("foreign-material-fails-without-overwrite", !player.TrySamplePose(.4) && receiver.sharedMaterial == foreign); receiver.sharedMaterial = material;
                player.Dispose(); Check("disposed-player-terminal", !player.TrySamplePose(.3)); player = null;
                Check("missing-binding-fails", !PerformancePlayer.TryCreate(definition, new PerformanceBindings(), out _, out _));
                var scalar = new PerformanceClip { duration = 1, morphs = new[] { new PerformanceClip.Morph { target = "face.smile", keys = new[] {
                    new FaceDecalAnimation.Key(0, 0, FaceDecalInterpolation.Hermite), new FaceDecalAnimation.Key(1, 1) } } } };
                Check("hermite-bind", PerformancePlayer.TryCreate(scalar, bindings, out player, out _));
                Check("independent-hermite", player.TrySamplePose(.25) && driver.GetWeight(0) == .15625f); player.Dispose(); player = null;
                scalar.morphs[0].keys[0].interpolation = FaceDecalInterpolation.Hold;
                Check("hold-bind", PerformancePlayer.TryCreate(scalar, bindings, out player, out _));
                Check("independent-hold-boundary", player.TrySamplePose(.999) && driver.GetWeight(0) == 0 && player.TrySamplePose(1) && driver.GetWeight(0) == 1);
                player.Dispose(); player = null;
                var overshoot = JsonUtility.FromJson<PerformanceClip>(JsonUtility.ToJson(definition));
                overshoot.decals[0].tracks[0].keys = new[] { new FaceDecalAnimation.Key(0, 0, FaceDecalInterpolation.Hermite) { outTangent = 8 }, new FaceDecalAnimation.Key(1, 0) { inTangent = -8 } };
                Check("overshoot-clip-bind", PerformancePlayer.TryCreate(overshoot, bindings, out player, out _));
                Check("overshoot-fails-before-partial-pose", !player.TrySamplePose(.5) && driver.GetWeight(0) == 0 && decal.opacity == 0 && receiver.sharedMaterial == material && player.ActiveEffectCount == 0);
                player.Dispose(); player = null;
                var alias = JsonUtility.FromJson<PerformanceClip>(JsonUtility.ToJson(definition));
                alias.morphs = new[] { alias.morphs[0], new PerformanceClip.Morph { target = "alias", keys = alias.morphs[0].keys } };
                bindings.morphs.Add("alias", new PerformanceBindings.Morph { faceDriver = driver, index = 0 });
                Check("aliased-physical-morph-rejected", !PerformancePlayer.TryCreate(alias, bindings, out _, out _));
                var skinObject = Own(new GameObject("Explicit Unity blendshape binding")); skinObject.SetActive(false);
                var skin = skinObject.AddComponent<SkinnedMeshRenderer>(); var skinMesh = Own(Instantiate(mesh));
                skinMesh.AddBlendShapeFrame("independent", 100, Enumerable.Repeat(Vector3.up, 3).ToArray(), new Vector3[3], new Vector3[3]); skin.sharedMesh = skinMesh;
                var skinBindings = new PerformanceBindings(); skinBindings.morphs.Add("face.smile", new PerformanceBindings.Morph { renderer = skin, index = 0 });
                scalar.morphs[0].keys[0].interpolation = FaceDecalInterpolation.Linear;
                Check("unity-blendshape-bind", PerformancePlayer.TryCreate(scalar, skinBindings, out player, out _));
                Check("unity-percentage-weight-conversion", player.TrySamplePose(.25) && skin.GetBlendShapeWeight(0) == 25);
                var replacementMesh = Own(Instantiate(skinMesh)); skin.sharedMesh = replacementMesh; skin.SetBlendShapeWeight(0, 7); DestroyImmediate(skinMesh);
                Check("destroyed-topology-fails-without-overwrite", !player.TrySamplePose(.5) && skin.GetBlendShapeWeight(0) == 7);
                player.Dispose(); player = null;
                Check("no-template-mutation", marker.activeSelf == false && marker.GetComponent<MeshRenderer>().sharedMaterial == markerMaterial && material.GetTexture("_MainTex") != atlas);
            }
            finally
            {
                player?.Dispose(); rig?.Dispose(); source?.Dispose(); if (face != null) face.GpuDeformationEnabled = false;
                RenderTexture.active = active; foreach (var item in _owned) if (item != null) Destroy(item); _owned.Clear();
                if (bundle != null) bundle.Unload(true);
                for (int i = 0; i < others.Length; i++) if (others[i] != null) others[i].forceRenderingOff = flags[i];
            }
            yield return null;
        }
    }
}
