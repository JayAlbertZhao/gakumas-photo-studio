using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyVertexLocators(Report report)
        {
            yield return null;
            var others = FindObjectsOfType<Renderer>(); var forced = others.Select(r => r.forceRenderingOff).ToArray();
            foreach (var renderer in others) renderer.forceRenderingOff = true;
            var previous = RenderTexture.active; GpuFaceDeformer gpu = null; VertexLocatorRig rig = null, second = null;
            var effects = new MotionEffectSequence();
            try
            {
                void Check(string name, bool accepted, float value = 0) => FrameworkCheck(report, "vertex-locator-" + name, accepted, value);
                var rest = new[] { new Vector3(-.55f, -.35f, 3), new Vector3(.4f, -.35f, 3), new Vector3(-.5f, .5f, 3), new Vector3(.45f, .5f, 3) };
                var normals = Enumerable.Repeat(Vector3.back, 4).ToArray(); var tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 4).ToArray();
                var deltas = new[] { new GpuFaceDeformer.Delta(0, 0, new Vector3(.2f, .3f, -.1f)),
                    new GpuFaceDeformer.Delta(0, 2, new Vector3(.1f, -.13f, .15f)), new GpuFaceDeformer.Delta(0, 0, new Vector3(.02f, -.03f, 0)),
                    new GpuFaceDeformer.Delta(1, 1, new Vector3(-.17f, .12f, .04f)), new GpuFaceDeformer.Delta(1, 3, new Vector3(.15f, .13f, -.14f)) };
                uint[] influences = new uint[16];
                for (int v = 0; v < 4; v++) { influences[v * 4] = 31001u << 16; influences[v * 4 + 1] = (32761u << 16) | 1u; influences[v * 4 + 2] = (1200u << 16) | 60000u; }
                var definitions = new[] { new VertexLocatorRig.Locator { id = "vertex", a = 0, b = 1, c = 2, positionOffset = new Vector3(0, 0, -.04f) },
                    new VertexLocatorRig.Locator { id = "barycentric", a = 1, b = 3, c = 2, barycentric = new Vector3(.2f, .3f, .5f), positionOffset = new Vector3(0, 0, -.04f), rotationDegrees = new Vector3(0, 0, 21) } };
                Check("rig-create", VertexLocatorRig.TryCreate(definitions, out rig, out _)); var indices = rig.GetVertexIndices();
                Check("stable-unique-index-order", indices.SequenceEqual(new[] { 0, 1, 2, 3 }));
                Check("sampler-create", VertexDeformationSampler.TryCreate(rest, 2, deltas, influences, 2, indices, out var sampler, out _));
                Check("copied-indices", !ReferenceEquals(sampler.GetVertexIndices(), indices));
                var mesh = Own(new Mesh { name = "Independent locator source" }); mesh.vertices = rest; mesh.normals = normals; mesh.tangents = tangents;
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }; mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 }; mesh.RecalculateBounds();
                Check("gpu-create", GpuFaceDeformer.TryCreate(mesh, 2, deltas, influences, 2, out gpu, out _));
                var camera = Own(new GameObject("Current locator camera")).AddComponent<Camera>(); camera.enabled = false; camera.cullingMask = 1 << 24;
                camera.orthographic = true; camera.orthographicSize = 1; camera.aspect = 137f / 101; camera.nearClipPlane = .1f; camera.farClipPlane = 10;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.11f, .21f, .31f, .61f); camera.allowHDR = true; camera.allowMSAA = false;
                var target = Own(new RenderTexture(137, 101, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var faceObject = Own(new GameObject("Actual GPU face plane")); faceObject.layer = 24;
                var filter = faceObject.AddComponent<MeshFilter>(); var renderer = faceObject.AddComponent<MeshRenderer>();
                var material = Own(new Material(Shader.Find("Hidden/PhotoStudio/FaceDecalProbe"))); material.SetVector("_ProbeTint", new Vector4(.21f, .47f, .67f, .61f)); renderer.sharedMaterial = material;
                var marker = Own(new GameObject("Authored locator marker")); marker.SetActive(false); marker.layer = 24;
                marker.AddComponent<MeshFilter>().sharedMesh = Own(new Mesh { name = "Authored marker triangle", vertices = new[] { new Vector3(-.035f, -.035f, 0), new Vector3(.035f, -.035f, 0), new Vector3(0, .05f, 0) }, triangles = new[] { 0, 2, 1 } });
                var markerMaterial = Own(new Material(material)); markerMaterial.SetVector("_ProbeTint", new Vector4(.93f, .13f, .07f, .61f)); marker.AddComponent<MeshRenderer>().sharedMaterial = markerMaterial;
                Check("effects-bind-to-owned-locators", effects.TryBind(definitions.Select(d => new MotionEffectSequence.Entry { id = d.id, prefab = marker, attachment = rig.GetAttachment(d.id), durationSeconds = 4 }).ToArray(), 4));
                Color[] Render() { camera.Render(); return ReadSceneTarget(target); }
                bool Exact(Color[] a, Color[] b) => a.Zip(b, (x, y) => x.Equals(y)).All(x => x);
                Color[] referenceImage = null; Vector3[] referencePose = null;
                for (int sample = 0; sample < 24; sample++)
                {
                    float w = sample == 23 ? .4f : sample == 0 ? .4f : sample / 23f;
                    var weights = new[] { w, sample == 23 || sample == 0 ? .2f : 1 - w };
                    var matrices = new[] { Matrix4x4.Translate(new Vector3(.03f, -.02f, 0)), Matrix4x4.TRS(new Vector3(-.05f, .03f, 0), Quaternion.Euler(2, 3, 7), new Vector3(1.03f, .97f, 1)) };
                    Check("sample-" + sample + "-cpu", sampler.TrySample(weights, matrices, out var positions, out _));
                    Check("sample-" + sample + "-gpu", gpu.TryDispatch(weights, matrices)); filter.sharedMesh = gpu.Mesh;
                    FaceCpuReference(rest, normals, tangents, deltas, weights, influences, matrices, out var analytic, out _, out _);
                    var actual = ReadFaceGpu(gpu.Mesh); float cpuError = 0, gpuError = 0;
                    for (int v = 0; v < positions.Length; v++) for (int c = 0; c < 3; c++)
                    { cpuError = Mathf.Max(cpuError, Mathf.Abs(positions[v][c] - analytic[indices[v]][c])); gpuError = Mathf.Max(gpuError, Mathf.Abs(positions[v][c] - actual[indices[v]][c])); }
                    Check("sample-" + sample + "-independent-position", cpuError <= .00002f, cpuError);
                    Check("sample-" + sample + "-actual-gpu-position", gpuError <= .00002f, gpuError);
                    effects.Stop(); Check("sample-" + sample + "-update", rig.TryUpdate(positions, Matrix4x4.identity)); var off = Render();
                    Check("sample-" + sample + "-effect", effects.TrySample(.7)); var image = Render();
                    Check("sample-" + sample + "-positive-markers", image.Where((p, i) => p.r != off[i].r).Count() > 8);
                    Vector3 a = positions[0], b = positions[1], cPoint = positions[2]; Vector3 normal = Vector3.Cross(b - a, cPoint - a).normalized;
                    var expected = a - normal * .04f; float attachError = (rig.GetAttachment("vertex").position - expected).magnitude;
                    Check("sample-" + sample + "-actual-vertex-attachment", attachError <= .00002f, attachError);
                    if (sample == 0) { referenceImage = image; referencePose = positions; SaveSsrPreview("vertex-locator-attached", image, 137, 101, false); }
                    if (sample == 12) { Check("visible-current-deformation", !Exact(referenceImage, image)); SaveSsrPreview("vertex-locator-morphed", image, 137, 101, false); }
                    if (sample == 23) Check("absolute-seek-full-image-exact", Exact(referenceImage, image));
                }
                Check("gpu-cpu-copy-is-still-rest", gpu.Mesh.vertices.Zip(rest, (a, b) => a.Equals(b)).All(v => v));
                Check("source-mesh-intact", mesh.vertices.Zip(rest, (a, b) => a.Equals(b)).All(v => v) && renderer.sharedMaterial == material);
                // Analytic barycentric and frame tests include mirrored, nonuniform and sheared world transforms.
                for (int pose = 0; pose < 4; pose++)
                {
                    var matrix = Matrix4x4.TRS(new Vector3(.1f, -.2f, .3f), Quaternion.Euler(13, 29, 41), new Vector3(pose % 2 == 0 ? 1.3f : -1.3f, .8f, 1.1f));
                    if (pose > 1) matrix.m01 += .3f;
                    effects.Stop(); Check("matrix-" + pose + "-update", rig.TryUpdate(referencePose, matrix));
                    Vector3 a = matrix.MultiplyPoint3x4(referencePose[1]), b = matrix.MultiplyPoint3x4(referencePose[3]), c = matrix.MultiplyPoint3x4(referencePose[2]);
                    var z = Vector3.Cross(b - a, c - a).normalized;
                    var expected = a * .2f + b * .3f + c * .5f - z * .04f;
                    float error = (expected - rig.GetAttachment("barycentric").position).magnitude;
                    Check("matrix-" + pose + "-barycentric", error <= .00002f, error);
                    Check("matrix-" + pose + "-normal", Vector3.Dot(rig.GetAttachment("barycentric").forward, z) > .99999f);
                    var x = (b - a).normalized; var y = Vector3.Cross(z, x);
                    var rotatedX = x * Mathf.Cos(21 * Mathf.Deg2Rad) + y * Mathf.Sin(21 * Mathf.Deg2Rad);
                    Check("matrix-" + pose + "-rotation-offset", Vector3.Dot(rig.GetAttachment("barycentric").right, rotatedX) > .99999f);
                }
                Check("second-rig-create", VertexLocatorRig.TryCreate(definitions, out second, out _));
                Check("independent-rig-current", second.TryUpdate(referencePose, Matrix4x4.identity) && rig.TryUpdate(referencePose, Matrix4x4.identity) && second.GetAttachment("vertex") != rig.GetAttachment("vertex") && second.GetAttachment("vertex").position.Equals(rig.GetAttachment("vertex").position));
                effects.Stop();
                using (var secondEffects = new MotionEffectSequence())
                {
                    Check("independent-owner-bind", secondEffects.TryBind(definitions.Select(d => new MotionEffectSequence.Entry { id = d.id, prefab = marker, attachment = second.GetAttachment(d.id), durationSeconds = 4 }).ToArray(), 4));
                    Check("independent-owner-sample", secondEffects.TrySample(.7));
                    Check("independent-owner-full-image-exact", Exact(referenceImage, Render()));
                }
                Check("original-owner-restored-full-image-exact", effects.TrySample(.7) && Exact(referenceImage, Render()));
                var invalid = (Vector3[])referencePose.Clone(); invalid[1] = invalid[0];
                Check("degenerate-frame-fails-closed", !rig.TryUpdate(invalid, Matrix4x4.identity) && !rig.IsCurrent && !rig.GetAttachment("vertex").gameObject.activeInHierarchy);
                Check("degenerate-removes-effects", effects.TrySample(.7) && effects.ActiveCount == 0);
                invalid[0].x = float.NaN; Check("nonfinite-vertex", !rig.TryUpdate(invalid, Matrix4x4.identity));
                Check("wrong-position-count", !rig.TryUpdate(new Vector3[1], Matrix4x4.identity));
                Check("rig-recover", rig.TryUpdate(referencePose, Matrix4x4.identity) && effects.TrySample(.7) && effects.ActiveCount == 2);
                var wrong = Matrix4x4.identity; wrong.m30 = 1; Check("perspective-matrix-rejected", !rig.TryUpdate(referencePose, wrong));
                var fixedDefinition = new[] { new VertexLocatorRig.Locator { id = "point", a = 0, b = 0, c = 0, alignToTriangle = false } };
                second.Dispose(); Check("position-only-create", VertexLocatorRig.TryCreate(fixedDefinition, out second, out _));
                Check("position-only-no-triangle-required", second.TryUpdate(new[] { referencePose[0] }, Matrix4x4.identity) && second.GetAttachment("point").position.Equals(referencePose[0]));
                Check("sampler-rejects-nan", !sampler.TrySample(new[] { float.NaN, 0 }, new[] { Matrix4x4.identity, Matrix4x4.identity }, out var empty, out _) && empty == null);
                Check("sampler-rejects-pose-count", !sampler.TrySample(new float[0], new Matrix4x4[0], out empty, out _));
                Check("duplicate-index-rejected", !VertexDeformationSampler.TryCreate(rest, 2, deltas, influences, 2, new[] { 0, 0 }, out _, out _));
                Check("invalid-index-rejected", !VertexDeformationSampler.TryCreate(rest, 2, deltas, influences, 2, new[] { 4 }, out _, out _));
                Check("missing-influences-rejected", !VertexDeformationSampler.TryCreate(rest, 2, deltas, null, 2, new[] { 0 }, out _, out _));
                Check("shape-order-rejected", !VertexDeformationSampler.TryCreate(rest, 2, deltas.Reverse().ToArray(), influences, 2, new[] { 0 }, out _, out _));
                Check("unskinned-create", VertexDeformationSampler.TryCreate(rest, 0, new GpuFaceDeformer.Delta[0], null, 0, new[] { 3, 0 }, out var plain, out _));
                Check("unskinned-rest-exact", plain.TrySample(new float[0], new Matrix4x4[0], out var plainPose, out _) && plainPose[0].Equals(rest[3]) && plainPose[1].Equals(rest[0]));
                var restCopy = (Vector3[])rest.Clone(); var deltaCopy = (GpuFaceDeformer.Delta[])deltas.Clone();
                var influenceCopy = (uint[])influences.Clone(); var selectedCopy = (int[])indices.Clone();
                Check("snapshot-create", VertexDeformationSampler.TryCreate(restCopy, 2, deltaCopy, influenceCopy, 2, selectedCopy, out var snapshot, out _));
                restCopy[0] += Vector3.one; deltaCopy[0].position += Vector3.one; influenceCopy[0] = 0; selectedCopy[0] = 3;
                var identities = new[] { Matrix4x4.identity, Matrix4x4.identity }; var sampleWeights = new[] { .4f, .2f };
                sampler.TrySample(sampleWeights, identities, out var expectedSnapshot, out _);
                Check("topology-inputs-fully-copied", snapshot.TrySample(sampleWeights, identities, out var snapshotPose, out _) && snapshotPose.Zip(expectedSnapshot, (a, b) => a.Equals(b)).All(x => x));
                Check("tiny-morph-skipped", sampler.TrySample(new[] { .0000999f, 0 }, identities, out var tiny, out _) && tiny.Zip(rest, (a, b) => a.Equals(b)).All(x => x));
                Check("threshold-morph-applied", sampler.TrySample(new[] { .0001f, 0 }, identities, out var threshold, out _) && !threshold[0].Equals(rest[0]));
                var nearIdentity = Matrix4x4.identity; nearIdentity.m03 = .000005f;
                Check("near-identity-rest-exact", sampler.TrySample(new float[2], new[] { nearIdentity, nearIdentity }, out var nearRest, out _) && nearRest.Zip(rest, (a, b) => a.Equals(b)).All(x => x));
                var nonAffine = Matrix4x4.identity; nonAffine.m30 = .1f;
                Check("sampler-nonaffine-rejected", !sampler.TrySample(sampleWeights, new[] { nonAffine, Matrix4x4.identity }, out empty, out _) && empty == null);
                fixedDefinition[0].a = 9000;
                Check("locator-definition-copied", second.TryUpdate(new[] { referencePose[0] }, Matrix4x4.identity) && second.GetAttachment("point").position.Equals(referencePose[0]));
                Check("rig-selected-capacity", !VertexLocatorRig.TryCreate(Enumerable.Range(0, 257).Select(i => new VertexLocatorRig.Locator { id = "capacity-" + i }).ToArray(), out _, out _));
                Check("sampler-selected-capacity", !VertexDeformationSampler.TryCreate(new Vector3[769], 0, new GpuFaceDeformer.Delta[0], null, 0, Enumerable.Range(0, 769).ToArray(), out _, out _));
                Check("duplicate-locator-id-rejected", !VertexLocatorRig.TryCreate(new[] { definitions[0], definitions[0] }, out _, out _));
                var bad = new VertexLocatorRig.Locator { id = "bad", barycentric = Vector3.one }; Check("barycentric-not-renormalized", !VertexLocatorRig.TryCreate(new[] { bad }, out _, out _));
                var borrowed = rig.GetAttachment("vertex"); effects.Dispose(); rig.Dispose(); rig.Dispose();
                Check("rig-disposal-immediate-terminal", !borrowed.gameObject.activeInHierarchy && !rig.TryUpdate(referencePose, Matrix4x4.identity));
                VerifyVertexLocatorFaceBridge(report, camera, material);
            }
            finally
            {
                effects.Dispose(); rig?.Dispose(); second?.Dispose(); gpu?.Dispose(); RenderTexture.active = previous;
                for (int i = 0; i < others.Length; i++) if (others[i] != null) others[i].forceRenderingOff = forced[i];
                foreach (var value in _owned) if (value != null) Destroy(value); _owned.Clear();
            }
            yield return null;
        }

        private void VerifyVertexLocatorFaceBridge(Report report, Camera camera, Material material)
        {
            void Check(string name, bool accepted, float error = 0) => FrameworkCheck(report, "vertex-locator-face-" + name, accepted, error);
            var owner = Own(new GameObject("Selected face pose bridge")); owner.layer = 24;
            var model = owner.AddComponent<VL.FaceSystem.VLActorFaceModel>(); var filter = owner.AddComponent<MeshFilter>();
            owner.AddComponent<MeshRenderer>().sharedMaterial = material;
            var mesh = Own(new Mesh { name = "Generated sparse source" }); mesh.vertices = new[] { new Vector3(-.2f, -.2f, 3), new Vector3(.2f, -.2f, 3), new Vector3(0, .2f, 3) };
            mesh.normals = Enumerable.Repeat(Vector3.back, 3).ToArray(); mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 3).ToArray(); mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up }; mesh.triangles = new[] { 0, 2, 1 }; filter.sharedMesh = mesh;
            var bone = Own(new GameObject("Explicit source bone")).transform; model.bones = new[] { bone }; model.bindposes = new[] { Matrix4x4.identity };
            model.boneWeightAndIndices = new uint[12]; for (int i = 0; i < 3; i++) model.boneWeightAndIndices[i * 4] = 65535u << 16;
            model.blendShapes.Add(new VL.FaceSystem.VLFaceBlendShape { blendShapeName = "independent-expression" });
            model.blendShapes[0].blendShapeVertices.Add(new VL.FaceSystem.VLFaceBlendShapeVertex { vertIndex = 0, position = new Vector3(.2f, .1f, -.1f) });
            var face = owner.AddComponent<FaceExpressionRenderer>(); Check("initialize", face.Initialize(model, model));
            face.SetAutomaticBlinkEnabled(false); face.InitializeGaze(null, null, camera);
            Check("source-create", face.TryCreateVertexSource(new[] { 0, 2, 1 }, out var source, out _));
            using (source)
            {
                for (int backend = 0; backend < 2; backend++)
                {
                    face.GpuDeformationEnabled = backend == 1;
                    for (int pose = 0; pose < 4; pose++)
                    {
                        bone.SetPositionAndRotation(new Vector3(.02f * pose, .01f, 0), Quaternion.Euler(pose, pose * 3, 1));
                        model.SetWeight(0, .2f * pose); face.ApplyCurrentWeights();
                        Check(backend + "-" + pose + "-source", source.TrySample(out var sampled, out var matrix, out _));
                        var indices = source.GetVertexIndices(); float error = 0;
                        if (backend == 0)
                        {
                            var actual = filter.sharedMesh.vertices;
                            for (int i = 0; i < indices.Length; i++) error = Mathf.Max(error, (sampled[i] - actual[indices[i]]).magnitude);
                        }
                        else
                        {
                            Check(backend + "-" + pose + "-actual-gpu", face.IsGpuDeformationActive);
                            var actual = ReadFaceGpu(filter.sharedMesh);
                            for (int i = 0; i < indices.Length; i++) for (int c = 0; c < 3; c++) error = Mathf.Max(error, Mathf.Abs(sampled[i][c] - actual[indices[i]][c]));
                        }
                        Check(backend + "-" + pose + "-current-output", error <= .00002f, error);
                        Check(backend + "-" + pose + "-surface-frame", matrix == owner.transform.localToWorldMatrix);
                    }
                    model.SetWeight(0, .5f); face.ApplyCurrentWeights(); source.TrySample(out var first, out _, out _);
                    model.SetWeight(0, .9f); source.TrySample(out var unapplied, out _, out _);
                    Check(backend + "-source-does-not-advance-pose", first.Zip(unapplied, (a, b) => a.Equals(b)).All(x => x));
                    model.SetWeight(0, .50005f); face.ApplyCurrentWeights(); source.TrySample(out var latched, out _, out _);
                    Check(backend + "-subthreshold-latch", first.Zip(latched, (a, b) => a.Equals(b)).All(x => x));
                    model.SetWeight(0, .5002f); face.ApplyCurrentWeights(); source.TrySample(out var moved, out _, out _);
                    Check(backend + "-threshold-crossing-current", !first[0].Equals(moved[0]));
                }
                var current = filter.sharedMesh; filter.sharedMesh = mesh;
                Check("foreign-output-rejected", !source.TrySample(out var empty, out _, out _) && empty == null); filter.sharedMesh = current;
                source.Dispose(); Check("disposed-source-rejected", !source.TrySample(out empty, out _, out _) && empty == null);
            }
            face.GpuDeformationEnabled = false; owner.SetActive(false);
        }
    }
}
