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
        private IEnumerator VerifyOutlineAuthoring(Report report)
        {
            yield return null;
            void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "outline-authoring-" + name, ok, error);
            void Reject(string name, Action operation)
            { bool rejected = false; try { operation(); } catch (ArgumentException) { rejected = true; } Check("reject-" + name, rejected); }
            var previous = FindObjectsOfType<Renderer>(); var forced = previous.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in previous) r.forceRenderingOff = true;
            var savedActive = RenderTexture.active;
            var savedParameters = Shader.GetGlobalVector("_ActorOutlineParameters");
            CommandBuffer commands = null; Camera camera = null;
            try
            {
                Mesh source = Own(OutlineCube());
                var p = source.vertices; var tri = source.triangles;
                var normals = source.normals; var oldTangents = source.tangents;
                var result = ActorOutlineAuthoring.Generate(source);
                float analyticError = 0;
                for (int i = 0; i < p.Length; i++) analyticError = Mathf.Max(analyticError, (result.directions[i] - p[i].normalized).magnitude);
                Check("split-cube-analytic-unit-corners", analyticError < 1e-6f && result.weldedVertices == 16 && result.unresolvedVertices == 0, analyticError);
                var groups = Enumerable.Range(0, p.Length).Select(i => i / 4).ToArray();
                var isolated = ActorOutlineAuthoring.Generate(p, tri, groups);
                Check("explicit-shell-isolation", OutlineVectorError(isolated.directions, normals) < 1e-6f && isolated.weldedVertices == 0);
                Check("read-only-array-and-mesh", p.SequenceEqual(source.vertices) && normals.SequenceEqual(source.normals) && oldTangents.SequenceEqual(source.tangents));
                foreach (float scale in new[] { 1e-30f, 1f, 1e30f })
                {
                    var scaled = p.Select(v => v * scale).ToArray();
                    var generated = ActorOutlineAuthoring.Generate(scaled, tri);
                    Check("scale-invariant-" + scale, OutlineVectorError(generated.directions, result.directions) < 1e-6f);
                }
                var reversed = (int[])tri.Clone();
                for (int i = 0; i < reversed.Length; i += 3) { int a = reversed[i]; reversed[i] = reversed[i + 1]; reversed[i + 1] = a; }
                Check("winding-inversion", OutlineVectorError(ActorOutlineAuthoring.Generate(p, reversed).directions, result.directions.Select(v => -v).ToArray()) < 1e-6f);
                var cancelled = ActorOutlineAuthoring.Generate(p, tri.Concat(reversed).ToArray());
                Check("opposite-winding-cancellation-zero", cancelled.unresolvedVertices == p.Length && cancelled.directions.All(v => v.Equals(Vector3.zero)));
                var loose = p.Concat(new[] { new Vector3(7, 3, 9) }).ToArray();
                var degenerate = ActorOutlineAuthoring.Generate(loose, tri.Concat(new[] { 24, 24, 24 }).ToArray());
                Check("degenerate-unused-no-invented-normal", degenerate.degenerateTriangles == 1 && degenerate.unresolvedVertices == 1 && degenerate.directions[24].Equals(Vector3.zero));
                var near = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1e-7f, 0, 0), Vector3.back, Vector3.right };
                var exact = ActorOutlineAuthoring.Generate(near, new[] { 0, 1, 2, 3, 4, 5 });
                Check("nearby-vertices-not-approximately-welded", exact.directions[0].Equals(Vector3.forward) && exact.directions[3].Equals(Vector3.down));
                var zeros = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(-0f, 0f, -0f) };
                Check("signed-zero-is-same-position", ActorOutlineAuthoring.Generate(zeros, new[] { 0, 1, 2 }).weldedVertices == 1);
                Check("empty-triangles-zero", ActorOutlineAuthoring.Generate(p, Array.Empty<int>()).unresolvedVertices == p.Length);
                Reject("null-positions", () => ActorOutlineAuthoring.Generate((Vector3[])null, tri));
                Reject("null-indices", () => ActorOutlineAuthoring.Generate(p, null));
                Reject("empty-vertices", () => ActorOutlineAuthoring.Generate(Array.Empty<Vector3>(), Array.Empty<int>()));
                Reject("incomplete-triangle", () => ActorOutlineAuthoring.Generate(p, new[] { 0, 1 }));
                Reject("negative-index", () => ActorOutlineAuthoring.Generate(p, new[] { -1, 1, 2 }));
                Reject("overflow-index", () => ActorOutlineAuthoring.Generate(p, new[] { 0, 1, p.Length }));
                Reject("group-count", () => ActorOutlineAuthoring.Generate(p, tri, new[] { 1 }));
                foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                    Reject("nonfinite-" + value, () => ActorOutlineAuthoring.Generate(new[] { new Vector3(value, 0, 0) }, Array.Empty<int>()));
                Reject("vertex-budget", () => ActorOutlineAuthoring.Generate(new Vector3[ActorOutlineAuthoring.MaximumVertices + 1], Array.Empty<int>()));
                Reject("index-budget", () => ActorOutlineAuthoring.Generate(p, new int[ActorOutlineAuthoring.MaximumIndices + 3]));
                Reject("null-mesh", () => ActorOutlineAuthoring.CreateMesh(null));
                var line = Own(new Mesh()); line.vertices = new[] { Vector3.zero, Vector3.one }; line.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
                Reject("line-submesh", () => ActorOutlineAuthoring.CreateMesh(line));
                var unreadable = Own(Instantiate(source)); unreadable.UploadMeshData(true);
                Reject("unreadable-mesh", () => ActorOutlineAuthoring.Generate(unreadable));

                // Each face can use a different diagonal. Angle weights must not
                // inherit triangulation's unequal face multiplicity at a corner.
                var alternate = new List<int>();
                for (int face = 0; face < 6; face++) alternate.AddRange(new[] { 0, 1, 3, 1, 2, 3 }.Select(i => i + face * 4));
                Check("alternate-triangulation", OutlineVectorError(ActorOutlineAuthoring.Generate(p, alternate).directions, result.directions) < 1e-6f);
                var subdivided = new List<Vector3>(p); var subdividedIndices = new List<int>();
                for (int face = 0; face < 6; face++)
                {
                    int center = subdivided.Count; subdivided.Add((p[face * 4] + p[face * 4 + 2]) * .5f);
                    for (int k = 0; k < 4; k++) subdividedIndices.AddRange(new[] { face * 4 + k, face * 4 + (k + 1) % 4, center });
                }
                Check("coplanar-face-subdivision", OutlineVectorError(ActorOutlineAuthoring.Generate(subdivided, subdividedIndices).directions.Take(p.Length).ToArray(), result.directions) < 1e-6f);
                for (int sample = 0; sample < 12; sample++)
                {
                    var warped = p.Select(v => new Vector3(v.x + .07f * sample * v.y * v.z, v.y * (1 + sample * .031f), v.z + .031f * sample * v.x)).ToArray();
                    var actual = ActorOutlineAuthoring.Generate(warped, tri, sample % 2 == 0 ? null : groups);
                    float error = OutlineVectorError(actual.directions, OutlineReference(warped, tri, sample % 2 == 0 ? null : groups));
                    Check("independent-acos-warped-reference-" + sample, error < 2e-6f, error);
                }

                var mesh = Own(ActorOutlineAuthoring.CreateMesh(source));
                Check("clone-ownership-source-unchanged", mesh != source && p.SequenceEqual(source.vertices) && normals.SequenceEqual(source.normals) && oldTangents.SequenceEqual(source.tangents));
                Check("clone-preserves-channels-and-skin", mesh.vertices.SequenceEqual(p) && mesh.normals.SequenceEqual(normals) && mesh.uv.SequenceEqual(source.uv) && mesh.uv2.SequenceEqual(source.uv2) && mesh.colors32.SequenceEqual(source.colors32) && mesh.boneWeights.SequenceEqual(source.boneWeights) && mesh.bindposes.SequenceEqual(source.bindposes) && mesh.bounds.Equals(source.bounds));
                bool submeshes = mesh.subMeshCount == source.subMeshCount;
                for (int i = 0; i < source.subMeshCount; i++) submeshes &= mesh.GetSubMesh(i).Equals(source.GetSubMesh(i)) && mesh.GetIndices(i, false).SequenceEqual(source.GetIndices(i, false));
                Check("clone-preserves-submesh-base-vertices", submeshes);
                Check("clone-explicit-dedicated-tangents", OutlineVectorError(mesh.tangents.Select(v => (Vector3)v).ToArray(), result.directions) < 1e-6f && mesh.tangents.All(v => v.w == 1));
                Check("clone-shape-names-count", mesh.blendShapeCount == source.blendShapeCount && mesh.GetBlendShapeName(0) == source.GetBlendShapeName(0) && mesh.GetBlendShapeFrameCount(0) == 2);
                var dv = new Vector3[p.Length]; var dn = new Vector3[p.Length]; var dt = new Vector3[p.Length];
                var originalDv = new Vector3[p.Length]; var originalDn = new Vector3[p.Length]; var originalDt = new Vector3[p.Length];
                for (int frame = 0; frame < 2; frame++)
                {
                    source.GetBlendShapeFrameVertices(0, frame, originalDv, originalDn, originalDt); mesh.GetBlendShapeFrameVertices(0, frame, dv, dn, dt);
                    var expected = OutlineReference(p.Select((v, i) => v + dv[i]).ToArray(), tri, null);
                    float error = OutlineVectorError(mesh.tangents.Select((v, i) => (Vector3)v + dt[i]).ToArray(), expected);
                    Check("shape-frame-preserves-position-normal-" + frame, dv.SequenceEqual(originalDv) && dn.SequenceEqual(originalDn) && mesh.GetBlendShapeFrameWeight(0, frame) == source.GetBlendShapeFrameWeight(0, frame));
                    Check("shape-frame-independent-tangent-target-" + frame, error < 2e-6f && !dt.SequenceEqual(originalDt), error);
                }

                var host = Own(new GameObject("Authored outline actual camera")); camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.cullingMask = 1 << 26; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.19f, .23f, .29f, 1); camera.allowHDR = true; camera.allowMSAA = false;
                camera.renderingPath = RenderingPath.Forward; camera.nearClipPlane = .1f; camera.farClipPlane = 30;
                var target = Own(new RenderTexture(257, 193, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); target.name = "Authored outline current geometry"; target.Create(); camera.targetTexture = target;
                var root = Own(new GameObject("Authored outline deformed actor mesh")); root.layer = 25;
                var renderer = root.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh; renderer.updateWhenOffscreen = true; renderer.quality = SkinQuality.Bone2;
                var bone0 = new GameObject("Outline bone0"); bone0.transform.SetParent(root.transform, false);
                var bone1 = new GameObject("Outline bone1"); bone1.transform.SetParent(root.transform, false);
                renderer.bones = new[] { bone0.transform, bone1.transform }; renderer.rootBone = root.transform;
                var material = Own(new Material(Resources.Load<Shader>("ActorSupplemental"))); material.SetFloat("_VertexColor", 0); material.SetFloat("_OutlineEnabled", 1);
                material.SetVector("_OutlineColor", new Vector4(.03f, .07f, .11f, 1)); material.SetVector("_ActorColor", Vector4.one); material.SetFloat("_UseAlphaClip", 0);
                renderer.sharedMaterials = Enumerable.Repeat(material, mesh.subMeshCount).ToArray();
                var reference = Own(new GameObject("Independent pre-extruded outline")); reference.layer = 25;
                var referenceMesh = Own(new Mesh()); referenceMesh.vertices = p;
                // StudioAccent culls BACK; reverse the winding of the actor
                // outline's FRONT-cull geometry. It does not read tangents.
                referenceMesh.triangles = reversed; referenceMesh.RecalculateBounds();
                var referenceRenderer = reference.AddComponent<MeshRenderer>(); reference.AddComponent<MeshFilter>().sharedMesh = referenceMesh;
                var referenceMaterial = Own(new Material(Resources.Load<Shader>("StudioAccent")));
                // StudioAccent declares a Color, while ActorSupplemental uses
                // literal Vector radiance. Feed its authored sRGB value so both
                // independent shaders output the same linear test color.
                referenceMaterial.SetColor("_Color", new Color(.03f, .07f, .11f, 1).gamma); referenceRenderer.sharedMaterial = referenceMaterial;
                commands = new CommandBuffer { name = "Authored outline current native skin" }; camera.AddCommandBuffer(CameraEvent.AfterEverything, commands);
                const float width = .18f;
                Color[] Draw(bool actual)
                {
                    commands.Clear();
                    commands.SetGlobalVector("_ActorOutlineParameters", new Vector4(width * 100, width * 100, 0, 0));
                    if (actual) for (int submesh = 0; submesh < mesh.subMeshCount; submesh++) commands.DrawRenderer(renderer, material, submesh, material.FindPass("ACTOR_OUTLINE"));
                    else commands.DrawRenderer(referenceRenderer, referenceMaterial, 0, 0);
                    camera.Render(); return ReadSceneTarget(target);
                }
                Color[] first = null;
                for (int view = 0; view < 4; view++)
                for (int pose = 0; pose < 5; pose++)
                {
                    float weight = new[] { 0f, 25f, 50f, 75f, 100f }[pose]; renderer.SetBlendShapeWeight(0, weight);
                    bone0.transform.localRotation = Quaternion.Euler(0, pose * 3.1f, pose * -2.3f);
                    bone1.transform.localRotation = Quaternion.Euler(pose * 1.7f, pose * -4.7f, pose * 5.3f);
                    root.transform.localScale = view % 2 == 0 ? Vector3.one : new Vector3(1.13f, .83f, 1.07f);
                    root.transform.localRotation = Quaternion.Euler(0, view * 71, 0); reference.transform.SetPositionAndRotation(root.transform.position, root.transform.rotation); reference.transform.localScale = root.transform.localScale;
                    camera.orthographic = view < 2; camera.orthographicSize = 1.85f; camera.fieldOfView = 43;
                    camera.transform.position = new Vector3(2.9f, 1.7f, -4.1f); camera.transform.LookAt(Vector3.zero); camera.aspect = 257f / 193;
                    // Independent authored-frame normals, followed by documented
                    // linear delta interpolation and explicit one-bone matrices.
                    var d50 = new Vector3[p.Length]; var d100 = new Vector3[p.Length];
                    source.GetBlendShapeFrameVertices(0, 0, d50, null, null); source.GetBlendShapeFrameVertices(0, 1, d100, null, null);
                    var n0 = p.Select(v => v.normalized).ToArray();
                    var n50 = OutlineReference(p.Select((v, i) => v + d50[i]).ToArray(), tri, null);
                    var n100 = OutlineReference(p.Select((v, i) => v + d100[i]).ToArray(), tri, null);
                    var vertices = new Vector3[p.Length];
                    for (int i = 0; i < p.Length; i++)
                    {
                        Vector3 delta = weight <= 50 ? d50[i] * (weight / 50) : Vector3.Lerp(d50[i], d100[i], (weight - 50) / 50);
                        Vector3 direction = weight <= 50 ? Vector3.Lerp(n0[i], n50[i], weight / 50) : Vector3.Lerp(n50[i], n100[i], (weight - 50) / 50);
                        Quaternion rotation = p[i].x > 0 ? bone1.transform.localRotation : bone0.transform.localRotation;
                        vertices[i] = rotation * (p[i] + delta + direction * width);
                    }
                    referenceMesh.vertices = vertices; referenceMesh.RecalculateBounds();
                    // Two update boundaries allow the engine's offscreen skin
                    // buffers to consume the pose before manual Camera.Render.
                    yield return null; yield return null;
                    var actual = Draw(true); var expected = Draw(false); float error = PixelError(actual, expected);
                    Check($"whole-image-native-skin-shape-view-{view}-pose-{pose}", error < .00001f && actual.Count(c => Mathf.Abs(c.g - actual[0].g) > .01f) > 100, error);
                    Check($"coverage-native-skin-shape-view-{view}-pose-{pose}", actual.Select((c, i) => (Mathf.Abs(c.g - actual[0].g) > .01f) == (Mathf.Abs(expected[i].g - expected[0].g) > .01f)).All(v => v));
                    if (error >= .00001f)
                    {
                        SaveSsrPreview($"outline-failed-actual-{view}-{pose}", actual, target.width, target.height, false);
                        SaveSsrPreview($"outline-failed-reference-{view}-{pose}", expected, target.width, target.height, false);
                    }
                    if (view == 0 && pose == 0) first = actual;
                    if (view == 2 && pose == 4)
                    {
                        SaveSsrPreview("outline-authored-native-skin", actual, target.width, target.height, false);
                        SaveSsrPreview("outline-independent-preextruded", expected, target.width, target.height, false);
                        if (Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_OUTLINE") == "1")
                        {
                            FsrCaptureDrain(target); bool began = RenderDocCaptureBridge.BeginOffscreenCapture(), ended = false;
                            try { Draw(true); FsrCaptureDrain(target); } finally { if (began) ended = RenderDocCaptureBridge.EndOffscreenCapture(); }
                            Check("requested-native-skin-draw-capture", began && ended);
                        }
                    }
                }
                Check("actual-moving-pose-positive", PixelError(first, Draw(true)) > .02f);
                // A seam-split cube with ordinary flat normals leaves a very
                // different inverted hull. Same source topology, camera and width.
                var hard = Own(Instantiate(mesh)); hard.ClearBlendShapes(); hard.tangents = ActorVertexEncoding.OutlineTangents(normals);
                renderer.sharedMesh = mesh; renderer.SetBlendShapeWeight(0, 0); bone0.transform.localRotation = bone1.transform.localRotation = Quaternion.identity;
                yield return null; yield return null;
                var smoothPixels = Draw(true); renderer.sharedMesh = hard;
                yield return null; yield return null;
                var hardPixels = Draw(true);
                Check("hard-normal-seam-negative-control", PixelError(smoothPixels, hardPixels) > .02f);
                SaveSsrPreview("outline-authored-smooth-hull", smoothPixels, target.width, target.height, false);
                SaveSsrPreview("outline-hard-normal-seam-control", hardPixels, target.width, target.height, false);
                renderer.sharedMesh = mesh;
                yield return null; yield return null;
                Check("rebind-replay-exact", PixelError(smoothPixels, Draw(true)) == 0);
                Check("source-final-tangents-untouched", source.tangents.SequenceEqual(oldTangents));
            }
            finally
            {
                if (camera && commands != null) camera.RemoveCommandBuffer(CameraEvent.AfterEverything, commands);
                commands?.Dispose(); if (camera) camera.targetTexture = null;
                Shader.SetGlobalVector("_ActorOutlineParameters", savedParameters); RenderTexture.active = savedActive;
                for (int i = 0; i < previous.Length; i++) if (previous[i]) previous[i].forceRenderingOff = forced[i];
                foreach (var item in _owned) if (item) DestroyImmediate(item); _owned.Clear();
            }
        }

        private static float OutlineVectorError(Vector3[] a, Vector3[] b)
        { if (a.Length != b.Length) return float.PositiveInfinity; float result = 0; for (int i = 0; i < a.Length; i++) result = Mathf.Max(result, (a[i] - b[i]).magnitude); return result; }

        // Independent deliberately slow oracle: visit every matching triangle
        // corner per output vertex. No production hash groups or atan2 weights.
        private static Vector3[] OutlineReference(Vector3[] vertices, int[] triangles, int[] groups)
        {
            var output = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                double sx = 0, sy = 0, sz = 0;
                for (int t = 0; t < triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = triangles[t + k], b = triangles[t + (k + 1) % 3], c = triangles[t + (k + 2) % 3];
                    if (!vertices[i].Equals(vertices[a]) || groups != null && groups[i] != groups[a]) continue;
                    Vector3 u = vertices[b] - vertices[a], v = vertices[c] - vertices[a];
                    double crossX = (double)u.y * v.z - (double)u.z * v.y, crossY = (double)u.z * v.x - (double)u.x * v.z, crossZ = (double)u.x * v.y - (double)u.y * v.x;
                    double length = Math.Sqrt(crossX * crossX + crossY * crossY + crossZ * crossZ); if (length == 0) continue;
                    double ul = Math.Sqrt((double)u.x * u.x + (double)u.y * u.y + (double)u.z * u.z), vl = Math.Sqrt((double)v.x * v.x + (double)v.y * v.y + (double)v.z * v.z);
                    double cosine = ((double)u.x * v.x + (double)u.y * v.y + (double)u.z * v.z) / (ul * vl);
                    double angle = Math.Acos(Math.Max(-1, Math.Min(1, cosine)));
                    sx += crossX / length * angle; sy += crossY / length * angle; sz += crossZ / length * angle;
                }
                double magnitude = Math.Sqrt(sx * sx + sy * sy + sz * sz);
                if (magnitude > 1e-12) output[i] = new Vector3((float)(sx / magnitude), (float)(sy / magnitude), (float)(sz / magnitude));
            }
            return output;
        }

        private static Mesh OutlineCube()
        {
            var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>();
            foreach (var n in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
            {
                var u = Vector3.Cross(n, Mathf.Abs(n.y) > .5f ? Vector3.forward : Vector3.up); var v = Vector3.Cross(n, u);
                foreach (var xy in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
                { vertices.Add((n + u * xy.x + v * xy.y) * .63f); normals.Add(n); uv.Add(xy * .5f + Vector2.one * .5f); }
            }
            var mesh = new Mesh { name = "Independent split cube actor input" }; mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0, uv); mesh.SetUVs(1, uv);
            mesh.tangents = Enumerable.Repeat(new Vector4(.1f, .2f, .3f, -1), vertices.Count).ToArray(); mesh.colors32 = Enumerable.Repeat(new Color32(0x12, 0x34, 0x56, 0x78), vertices.Count).ToArray();
            mesh.subMeshCount = 6;
            for (int i = 0; i < 6; i++) mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, i, true, i * 4);
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            mesh.boneWeights = vertices.Select(p => new BoneWeight { boneIndex0 = p.x > 0 ? 1 : 0, weight0 = 1 }).ToArray();
            foreach (float factor in new[] { .5f, 1f })
            {
                var dv = vertices.Select(p => new Vector3(.37f * p.y + .23f * p.z, -.09f * p.z, .27f * p.x * p.y) * factor).ToArray();
                mesh.AddBlendShapeFrame("Authored shear", factor * 100, dv, Enumerable.Repeat(new Vector3(.01f, .02f, .03f) * factor, vertices.Count).ToArray(), Enumerable.Repeat(Vector3.one * .2f, vertices.Count).ToArray());
            }
            mesh.RecalculateBounds(); return mesh;
        }
    }
}
