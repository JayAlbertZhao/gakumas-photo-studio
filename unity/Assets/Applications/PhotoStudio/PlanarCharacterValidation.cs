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
    /// <summary>Explicit offscreen validation with the user's local character. Never active by default.</summary>
    public sealed class PlanarCharacterValidation : MonoBehaviour
    {
        [Serializable] private sealed class Check { public string name; public bool accepted; public float value; }
        [Serializable] private sealed class Report
        {
            public string schema = "photo-studio.planar-character.v1";
            public string graphicsDevice, error, costume;
            public bool accepted;
            public int renderers, draws, materials;
            public int[] materialTypes;
            public List<Check> checks = new List<Check>();
        }
        private PhotoModeApp _app;
        private string _directory;
        public static bool TryStart(PhotoModeApp app)
        {
            string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "--validate-planar-character");
            if (index < 0) return false;
            try
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) || !args.Contains("--photo-mode"))
                    throw new ArgumentException("--validate-planar-character requires --photo-mode and an output directory");
                var validation = app.gameObject.AddComponent<PlanarCharacterValidation>();
                validation._app = app; validation._directory = Path.GetFullPath(args[index + 1]);
            }
            catch (Exception error) { Debug.LogError("[PlanarCharacterValidation] " + error.Message); Application.Quit(3); }
            return true;
        }
        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(2);
            var report = new Report { graphicsDevice = SystemInfo.graphicsDeviceVersion, costume = _app.CurrentCostume };
            GameObject host = null, mirror = null; RenderTexture target = null; Material mirrorMaterial = null;
            var set = new ActorPlanarCaptureSet(); var previousOffscreen = new Dictionary<SkinnedMeshRenderer, bool>();
            bool wasPaused = _app.IsPlaybackPaused;
            try
            {
                Directory.CreateDirectory(_directory);
                if (GraphicsSettings.currentRenderPipeline != null || _app.CharacterRoot == null) throw new InvalidOperationException("Requires initialized Built-in local character");
                _app.SetPlaybackPaused(true); _app.EvaluateMotion(0);
                var renderers = _app.CharacterRoot.GetComponentsInChildren<Renderer>().Where(r => r.enabled && !r.forceRenderingOff).ToArray();
                if (renderers.Length == 0) throw new InvalidOperationException("No character renderers");
                foreach (var skin in renderers.OfType<SkinnedMeshRenderer>()) { previousOffscreen.Add(skin, skin.updateWhenOffscreen); skin.updateWhenOffscreen = true; }
                Bounds bounds = renderers[0].bounds; foreach (var r in renderers) bounds.Encapsulate(r.bounds);
                Vector3 center = bounds.center; float distance = Mathf.Max(1, bounds.size.y) * 2;
                var headDriver = _app.CharacterRoot.GetComponent<ActorHeadLightingDriver>();
                Transform head = headDriver != null ? (Transform)typeof(ActorHeadLightingDriver).GetField("_head", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(headDriver) : null;
                var lighting = new ActorPlanarLighting { lightDirection = new Vector3(.3f, .4f, 1), lightColor = Vector3.one * .8f,
                    ambientColor = Vector3.one * .2f, head = head };
                host = new GameObject("Offscreen character mirror validation"); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowMSAA = false; camera.allowHDR = true; camera.renderingPath = RenderingPath.Forward;
                camera.fieldOfView = 40; camera.nearClipPlane = .03f; camera.farClipPlane = distance * 5;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear; camera.cullingMask = 1 << 29;
                target = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear); target.Create(); camera.targetTexture = target;
                mirror = GameObject.CreatePrimitive(PrimitiveType.Quad); mirror.layer = 29; mirror.transform.localScale = Vector3.one * distance * 3;
                mirrorMaterial = new Material(Resources.Load<Shader>("StudioAccent")); mirror.GetComponent<Renderer>().sharedMaterial = mirrorMaterial;
                var planar = host.AddComponent<PlanarReflection>(); planar.reflectionsEnabled = true; planar.reflectedLayers = ~0;
                planar.resolutionScale = 1; planar.maximumRoughnessMip = 0;
                planar.receivers = new[] { new PlanarReflection.Receiver { surface = new SceneDepthData.Surface { renderer = mirror.GetComponent<Renderer>() } } };
                void Check(string name, bool accepted, float value = 0) => report.checks.Add(new Check { name = name, accepted = accepted, value = value });
                void View(float angle)
                {
                    Vector3 normal = Quaternion.Euler(0, angle, 0) * Vector3.back;
                    planar.planePoint = center - normal * distance * .6f; planar.planeNormal = normal;
                    mirror.transform.SetPositionAndRotation(planar.planePoint, Quaternion.LookRotation(-normal));
                    camera.transform.position = center - normal * distance * .3f;
                    camera.transform.LookAt(center - normal * distance * 1.2f);
                }
                Color[] Render(string name)
                {
                    if (!set.TryRefresh(renderers, camera.worldToCameraMatrix * PlanarReflection.ReflectionMatrix(planar.planePoint, planar.planeNormal), lighting, out var error)) throw new InvalidOperationException(error);
                    planar.reflectedSurfaces = set.Draws; camera.Render();
                    if (!planar.TryGetReflection(camera, 512, 512, out _)) throw new InvalidOperationException(planar.UnavailableReason);
                    var capture = (RenderTexture)typeof(PlanarReflection).GetField("_capture", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(planar);
                    Color[] pixels = Read(capture);
                    if (name != null) Save(name, pixels);
                    return pixels;
                }
                string sourceBefore = Snapshot(renderers);
                Vector4 globalBefore = Shader.GetGlobalVector("_CapturedLightDirection");
                Color[] front = null;
                foreach (int angle in new[] { 0, 90, 180, 270 })
                {
                    View(angle); Color[] pixels = Render("mirror-" + angle);
                    int coverage = pixels.Count(p => p.a > .01f), colored = pixels.Count(p => p.a > .01f && Mathf.Max(p.r, p.g, p.b) > .03f);
                    Check("actual-character-view-" + angle, coverage > 1000 && colored > 1000, coverage);
                    Check("finite-hdr-and-bounded-coverage-" + angle, pixels.All(p => !float.IsNaN(p.r + p.g + p.b + p.a) && !float.IsInfinity(p.r + p.g + p.b + p.a) && p.a >= 0 && p.a <= 1));
                    if (angle == 0) front = pixels;
                }
                report.renderers = renderers.Length; report.draws = set.Draws.Length; report.materials = set.MaterialCount;
                report.materialTypes = renderers.SelectMany(r => r.sharedMaterials).Select(m => (int)m.GetFloat("_ShaderType")).Distinct().OrderBy(x => x).ToArray();
                Check("main-materials-and-global-light-not-mutated", Snapshot(renderers) == sourceBefore && Shader.GetGlobalVector("_CapturedLightDirection") == globalBefore);
                View(0); Check("front-view-revisit-identical", Difference(front, Render(null)) == 0);
                _app.EvaluateMotion(.7f); Color[] moved = Render("mirror-motion-070");
                int movedPixels = Difference(front, moved); Check("actual-skinned-motion-changes-capture", movedPixels > 100, movedPixels);
                _app.EvaluateMotion(0); Color[] restored = Render("mirror-motion-restored");
                Check("motion-seek-restores-capture", Difference(front, restored) == 0, Difference(front, restored));
                foreach (var draw in set.Draws) { draw.material.SetTexture("_CapShade", Texture2D.whiteTexture); draw.material.SetTexture("_CapRampAdd", Texture2D.blackTexture); draw.material.SetTexture("_CapHighlight", Texture2D.blackTexture); }
                camera.Render(); var raw = (RenderTexture)typeof(PlanarReflection).GetField("_capture", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(planar);
                Color[] detailOff = Read(raw); Save("mirror-detail-negative-control", detailOff);
                Check("authored-shade-hair-material-details-affect-capture", Difference(restored, detailOff) > 100, Difference(restored, detailOff));
                Check("refresh-recovers-owned-material-overrides", Difference(restored, Render(null)) == 0);
                set.TryRefresh(Array.Empty<Renderer>(), Matrix4x4.identity, lighting, out _); planar.reflectedSurfaces = set.Draws; camera.Render();
                Check("empty-character-releases-capture-and-materials", set.MaterialCount == 0 && !planar.TryGetReflection(camera, 512, 512, out _));
                Check("rebind-real-character-recovers", Difference(restored, Render(null)) == 0);
                set.Dispose(); Check("dispose-removes-borrowed-draws", set.MaterialCount == 0 && set.Draws.Length == 0);
                report.accepted = report.checks.All(c => c.accepted);
            }
            catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
            finally
            {
                set.Dispose(); foreach (var pair in previousOffscreen) if (pair.Key != null) pair.Key.updateWhenOffscreen = pair.Value;
                _app.SetPlaybackPaused(wasPaused);
                if (host != null) { host.GetComponent<Camera>().targetTexture = null; Destroy(host); }
                if (mirror != null) Destroy(mirror);
                if (mirrorMaterial != null) Destroy(mirrorMaterial);
                if (target != null) { target.Release(); Destroy(target); }
            }
            try { File.WriteAllText(Path.Combine(_directory, "planar-character.json"), JsonUtility.ToJson(report, true)); }
            catch (Exception error) { report.accepted = false; Debug.LogException(error); }
            Debug.Log("[PlanarCharacterValidation] accepted=" + report.accepted + "; checks=" + report.checks.Count);
            Application.Quit(report.accepted ? 0 : 2);
        }
        private static Color[] Read(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active; var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            try { RenderTexture.active = texture; copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); copy.Apply(); return copy.GetPixels(); }
            finally { RenderTexture.active = previous; Destroy(copy); }
        }
        private void Save(string name, Color[] pixels)
        {
            var image = new Texture2D(512, 512, TextureFormat.RGBA32, false, true);
            // PNG is a display preview; acceptance always compares the unclamped
            // linear HDR readback, not these sRGB/8-bit pixels.
            try { image.SetPixels(QualitySettings.activeColorSpace == ColorSpace.Linear ? pixels.Select(c => c.gamma).ToArray() : pixels); image.Apply(); File.WriteAllBytes(Path.Combine(_directory, name + ".png"), image.EncodeToPNG()); }
            finally { Destroy(image); }
        }
        private static int Difference(Color[] a, Color[] b) { int n = 0; for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) n++; return n; }
        private static string Snapshot(Renderer[] renderers)
        {
            var parts = new List<string>();
            foreach (Renderer r in renderers) foreach (Material m in r.sharedMaterials)
            {
                parts.Add(r.GetInstanceID() + "/" + m.GetInstanceID() + "/" + m.shader.name);
                foreach (string p in new[] { "_MainTex", "_ShadeTex", "_DefTex", "_RampTex", "_HighlightTex", "_LayerTex" }) parts.Add(p + ":" + (m.GetTexture(p) != null ? m.GetTexture(p).GetInstanceID() : 0));
                foreach (string p in new[] { "_Color", "_ActorColor", "_ActorTextureFrame", "_WardrobeScaleCorrection" }) parts.Add(p + ":" + m.GetVector(p).ToString("R"));
                foreach (string p in new[] { "_ShaderType", "_UseAlphaClip", "_Cutoff", "_LayerWeight", "_Cull", "_SrcBlend", "_DstBlend", "_ZWrite", "_StencilComp", "_StencilRef" }) parts.Add(p + ":" + m.GetFloat(p).ToString("R"));
            }
            return string.Join("|", parts);
        }
    }
}
