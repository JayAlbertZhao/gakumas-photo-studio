using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit local character acceptance. Marker geometry is generated, no reference assets exported.</summary>
    public sealed class VertexLocatorCharacterValidation : MonoBehaviour
    {
        [Serializable] private sealed class Check { public string name; public bool accepted; public double value; }
        [Serializable] private sealed class Report
        {
            public string schema = "photo-studio.vertex-locator-character.v1", error, graphicsDevice, costume;
            public bool accepted; public int vertices, shapes, selectedSubmesh; public int[] selectedIndices, attachmentIndices;
            public int positionChangingShapes, positionStaticShapes;
            public List<Check> checks = new List<Check>();
        }
        private PhotoModeApp app; private string directory;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private T Own<T>(T value) where T : UnityEngine.Object { owned.Add(value); return value; }
        public static bool TryStart(PhotoModeApp app)
        {
            var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, "--validate-vertex-locator-character");
            if (i < 0) return false;
            try
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !args.Contains("--photo-mode"))
                    throw new ArgumentException("--validate-vertex-locator-character requires --photo-mode and an output directory.");
                var component = app.gameObject.AddComponent<VertexLocatorCharacterValidation>(); component.app = app; component.directory = Path.GetFullPath(args[i + 1]);
            }
            catch (Exception error) { Debug.LogException(error); Application.Quit(3); }
            return true;
        }
        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(2);
            var report = new Report { graphicsDevice = SystemInfo.graphicsDeviceVersion, costume = app.CurrentCostume };
            var fixture = Run(report);
            while (true)
            {
                bool more; object next = null;
                try { more = fixture.MoveNext(); if (more) next = fixture.Current; }
                catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); break; }
                if (!more) break;
                yield return next;
            }
            (fixture as IDisposable)?.Dispose();
            report.accepted = report.error == null && report.checks.All(c => c.accepted);
            File.WriteAllText(Path.Combine(directory, "vertex-locator-character.json"), JsonUtility.ToJson(report, true));
            Debug.Log("[VertexLocatorCharacterValidation] accepted=" + report.accepted + "; checks=" + report.checks.Count); Application.Quit(report.accepted ? 0 : 2);
        }
        private IEnumerator Run(Report report)
        {
            FaceExpressionRenderer face = null; FaceExpressionRenderer.VertexSource source = null, proofSource = null; VertexLocatorRig rig = null;
            var effects = new MotionEffectSequence(); Camera camera = null; RenderTexture oldTarget = null; bool oldGpu = false, paused = app.IsPlaybackPaused;
            Vector3 oldPosition = Vector3.zero; Quaternion oldRotation = Quaternion.identity; var active = RenderTexture.active;
            Transform characterRoot = app.CharacterRoot.transform; Vector3 oldRootPosition = characterRoot.position; Quaternion oldRootRotation = characterRoot.rotation;
            var skins = new Dictionary<SkinnedMeshRenderer, bool>();
            void Check(string name, bool ok, double value = 0) => report.checks.Add(new Check { name = name, accepted = ok, value = value });
            try
            {
                Directory.CreateDirectory(directory); app.SetPlaybackPaused(true); app.EvaluateMotion(0);
                face = app.CharacterRoot.GetComponentInChildren<FaceExpressionRenderer>();
                if (face == null) throw new InvalidOperationException("Missing face.");
                camera = (Camera)typeof(FaceExpressionRenderer).GetField("_lookCamera", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(face);
                if (camera == null) throw new InvalidOperationException("Missing camera.");
                oldTarget = camera.targetTexture; oldPosition = camera.transform.position; oldRotation = camera.transform.rotation; oldGpu = face.GpuDeformationEnabled;
                foreach (var skin in app.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>()) { skins.Add(skin, skin.updateWhenOffscreen); skin.updateWhenOffscreen = true; }
                face.GpuDeformationEnabled = false; face.SetAutomaticBlinkEnabled(false); face.SetStoryBlink(-1); face.SelectPreset(0); face.SetStoryGaze(0, 0); face.SendMessage("UpdateGaze"); face.ApplyCurrentWeights();
                var filter = face.GetComponentInChildren<MeshFilter>(); var receiver = filter.GetComponent<Renderer>(); var mesh = filter.sharedMesh;
                report.vertices = mesh.vertexCount; report.shapes = face.ShapeCount;
                Check("actual-character", report.vertices > 1000 && report.shapes > 100);
                // Explicit fixture selection from the largest depth-writing submesh. Runtime never guesses indices.
                int slot = Enumerable.Range(0, mesh.subMeshCount).Where(i => receiver.sharedMaterials[i].GetFloat("_ZWrite") > .5f).OrderByDescending(i => mesh.GetIndexCount(i)).First();
                report.selectedSubmesh = slot; var vertexScale = (Vector3)receiver.sharedMaterials[slot].GetVector("_WardrobeScaleCorrection");
                var vertices = mesh.vertices; var matrix = receiver.localToWorldMatrix * Matrix4x4.Scale(vertexScale); var triangles = mesh.GetTriangles(slot);
                var points = triangles.Distinct().Select(i => new { index = i, position = matrix.MultiplyPoint3x4(vertices[i]) }).ToArray();
                float centerX = receiver.bounds.center.x, width = receiver.bounds.size.x;
                var selected = new List<int>();
                foreach (float fraction in new[] { -.25f, 0f, .25f })
                {
                    var candidates = points.Where(p => Math.Abs(p.position.x - (centerX + fraction * width)) < width * .14f)
                        .OrderByDescending(p => p.position.z).ToArray();
                    foreach (var candidate in candidates) if (!selected.Contains(candidate.index)) { selected.Add(candidate.index); break; }
                }
                if (selected.Count != 3) throw new InvalidOperationException("Could not identify three distinct front fixture vertices.");
                var definitions = selected.Select((index, i) => new VertexLocatorRig.Locator { id = "marker-" + i, a = index, b = index, c = index,
                    alignToTriangle = false, positionOffset = new Vector3(0, 0, .012f) }).ToArray();
                Check("rig-create", VertexLocatorRig.TryCreate(definitions, out rig, out _)); report.attachmentIndices = rig.GetVertexIndices();
                Check("source-create", face.TryCreateVertexSource(report.attachmentIndices, out source, out _));
                // Probe an affected vertex from every nonempty shape, not just three visible marker points.
                var shapeSource = (VL.FaceSystem.VLActorFaceModel)typeof(FaceExpressionRenderer).GetField("_shapeSource", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(face);
                var probes = new List<int>(report.attachmentIndices); var changesPosition = new bool[face.ShapeCount]; int shapeIndex = 0;
                foreach (var shape in shapeSource.blendShapes)
                {
                    var candidate = shape?.blendShapeVertices?.Where(v => v != null && v.vertIndex >= 0 && v.vertIndex < report.vertices && v.position.sqrMagnitude > 0)
                        .OrderByDescending(v => v.position.sqrMagnitude).FirstOrDefault();
                    changesPosition[shapeIndex++] = candidate != null;
                    if (candidate == null) { report.positionStaticShapes++; continue; }
                    report.positionChangingShapes++; if (!probes.Contains(candidate.vertIndex)) probes.Add(candidate.vertIndex);
                }
                report.selectedIndices = probes.ToArray();
                Check("affected-probes-cover-nonempty-shapes", report.positionChangingShapes > 0 && probes.Count > 20 &&
                    report.positionChangingShapes + report.positionStaticShapes == face.ShapeCount, report.positionChangingShapes);
                Check("proof-source-create", face.TryCreateVertexSource(report.selectedIndices, out proofSource, out _));
                face.SetDebugShape(0, 0); face.ApplyCurrentWeights();
                Check("zero-weight-current-baseline", proofSource.TrySample(out var zeroWeightPositions, out _, out _));
                var marker = Own(new GameObject("Generated real-face locator marker")); marker.SetActive(false); marker.layer = receiver.gameObject.layer;
                marker.AddComponent<MeshFilter>().sharedMesh = Own(new Mesh { name = "Generated face marker", vertices = new[] { new Vector3(-.009f, -.009f, 0), new Vector3(.009f, -.009f, 0), new Vector3(0, .014f, 0) }, triangles = new[] { 0, 1, 2 } });
                var material = Own(new Material(Shader.Find("Hidden/PhotoStudio/FaceDecalProbe"))); material.SetVector("_ProbeTint", new Vector4(.12f, .85f, .28f, 1)); marker.AddComponent<MeshRenderer>().sharedMaterial = material;
                Check("motion-effects-bind", effects.TryBind(definitions.Select(d => new MotionEffectSequence.Entry { id = d.id, prefab = marker, attachment = rig.GetAttachment(d.id), durationSeconds = 2 }).ToArray(), 2));
                var target = Own(new RenderTexture(512, 512, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var center = receiver.bounds.center; camera.transform.SetPositionAndRotation(center + Vector3.forward * .8f, Quaternion.LookRotation(Vector3.back)); face.ApplyCurrentWeights();
                Color[] Render(string name = null)
                {
                    camera.Render(); var values = Read(target); if (name != null) Save(name, values); return values;
                }
                void Compare(string name, Color[] a, Color[] b)
                {
                    double maximum = 0; bool finite = true;
                    for (int i = 0; i < a.Length; i++) for (int c = 0; c < 4; c++)
                    { double delta = Math.Abs((double)a[i][c] - b[i][c]); maximum = Math.Max(maximum, delta); finite &= !double.IsNaN(delta) && !double.IsInfinity(delta); }
                    Check(name + "-whole-rgba-exact", finite && maximum == 0, maximum);
                }
                Vector3[] Attachments() => definitions.Select(d => rig.GetAttachment(d.id).position).ToArray();
                void Numeric(string name, bool gpu)
                {
                    bool ok = proofSource.TrySample(out var sampled, out var frame, out var reason); Check(name + "-sample", ok);
                    if (!ok) throw new InvalidOperationException(reason);
                    var actual = gpu ? ReadGpuPositions(filter.sharedMesh) : filter.sharedMesh.vertices; double error = 0;
                    for (int i = 0; i < sampled.Length; i++) for (int c = 0; c < 3; c++) error = Math.Max(error, Math.Abs((double)sampled[i][c] - actual[report.selectedIndices[i]][c]));
                    Check(name + "-actual-current-vertices", error <= .00002, error);
                    Check(name + "-update", source.TryUpdate(rig, vertexScale, out _));
                }
                for (int shape = 0; shape < face.ShapeCount; shape++)
                {
                    face.GpuDeformationEnabled = false; face.SetDebugShape(shape, .75f); face.ApplyCurrentWeights(); Numeric("shape-" + shape + "-cpu", false);
                    bool currentSample = proofSource.TrySample(out var shapePositions, out _, out _);
                    double displacement = currentSample ? shapePositions.Zip(zeroWeightPositions, (a, b) => (a - b).magnitude).Max() : double.NaN;
                    Check("shape-" + shape + "-position-change-classification", currentSample &&
                        (changesPosition[shape] ? displacement > 0 : displacement == 0), displacement);
                    effects.Stop(); var off = Render(); Check("shape-" + shape + "-effect", effects.TrySample(.5)); var cpu = Render(shape == 0 ? "shape-000-cpu" : null); var cpuPoints = Attachments();
                    Check("shape-" + shape + "-visible-markers", cpu.Where((p, i) => Math.Abs(p.g - off[i].g) > .002).Count() > 10);
                    face.GpuDeformationEnabled = true; Check("shape-" + shape + "-gpu-active", face.IsGpuDeformationActive); Numeric("shape-" + shape + "-gpu", true);
                    Check("shape-" + shape + "-gpu-effect", effects.TrySample(.5)); var gpuPoints = Attachments();
                    double difference = cpuPoints.Zip(gpuPoints, (a, b) => (a - b).magnitude).Max(); Check("shape-" + shape + "-attachment-cpu-gpu", difference <= .00002, difference);
                    Compare("shape-" + shape + "-cpu-gpu", cpu, Render(shape == 0 ? "shape-000-gpu" : null));
                    face.GpuDeformationEnabled = false; effects.Stop(); Compare("shape-" + shape + "-off-restored", off, Render());
                    if (shape % 12 == 0) Debug.Log("[VertexLocatorCharacterValidation] shapes " + shape + "/" + face.ShapeCount);
                }
                face.ClearDebugShape(); face.SelectPreset(0);
                Vector3[] firstBodyAttachments = null;
                for (int pose = 0; pose < 5; pose++)
                {
                    app.EvaluateMotion(pose * .3f);
                    characterRoot.SetPositionAndRotation(oldRootPosition + new Vector3(.015f * pose, .007f * pose, -.01f * pose), oldRootRotation * Quaternion.Euler(0, 7 * pose, pose));
                    yield return null; yield return null;
                    face.GpuDeformationEnabled = false; face.ApplyCurrentWeights(); Numeric("body-" + pose + "-cpu", false); effects.TrySample(.5); var cpu = Render("body-" + pose + "-cpu");
                    face.GpuDeformationEnabled = true; Numeric("body-" + pose + "-gpu", true); effects.TrySample(.5); Compare("body-" + pose + "-cpu-gpu", cpu, Render("body-" + pose + "-gpu"));
                    if (pose == 0) firstBodyAttachments = Attachments();
                    else Check("body-" + pose + "-positive-world-follow", Attachments().Zip(firstBodyAttachments, (a, b) => (a - b).magnitude).Max() > .01f);
                }
                face.GpuDeformationEnabled = false; effects.Stop(); var hidden = Render(); source.Dispose(); Check("source-disposed-hides-locators", !source.TryUpdate(rig, vertexScale, out _) && !rig.IsCurrent);
                Check("invalid-source-removes-effects", effects.TrySample(.5) && effects.ActiveCount == 0); Compare("disposed-source-no-stale-markers", hidden, Render());
                report.accepted = report.checks.All(c => c.accepted);
            }
            finally
            {
                effects.Dispose(); rig?.Dispose(); source?.Dispose(); proofSource?.Dispose();
                if (characterRoot != null) characterRoot.SetPositionAndRotation(oldRootPosition, oldRootRotation);
                if (face != null) { face.GpuDeformationEnabled = oldGpu; face.ClearDebugShape(); }
                if (camera != null) { camera.targetTexture = oldTarget; camera.transform.SetPositionAndRotation(oldPosition, oldRotation); }
                foreach (var pair in skins) if (pair.Key != null) pair.Key.updateWhenOffscreen = pair.Value;
                RenderTexture.active = active; foreach (var value in owned) if (value != null) Destroy(value); app.SetPlaybackPaused(paused);
            }
        }
        private static Vector3[] ReadGpuPositions(Mesh mesh)
        {
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position), stride = mesh.GetVertexBufferStride(stream) / 4, offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position) / 4;
            var data = new uint[mesh.vertexCount * stride]; using (var buffer = mesh.GetVertexBuffer(stream)) buffer.GetData(data);
            var result = new Vector3[mesh.vertexCount];
            for (int i = 0; i < result.Length; i++) for (int c = 0; c < 3; c++) result[i][c] = BitConverter.ToSingle(BitConverter.GetBytes(data[i * stride + offset + c]), 0);
            return result;
        }
        private static Color[] Read(RenderTexture target)
        {
            var old = RenderTexture.active; var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try { RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply(); return texture.GetPixels(); }
            finally { RenderTexture.active = old; DestroyImmediate(texture); }
        }
        private void Save(string name, Color[] colors)
        {
            var texture = new Texture2D(512, 512, TextureFormat.RGBA32, false, true);
            try { texture.SetPixels(colors); texture.Apply(); File.WriteAllBytes(Path.Combine(directory, name + ".png"), texture.EncodeToPNG()); }
            finally { DestroyImmediate(texture); }
        }
    }
}
