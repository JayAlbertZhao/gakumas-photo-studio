using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class ReflectionResolveHostProbe : MonoBehaviour
    {
        public SceneReflectionResolve resolver;
        public bool consumed;
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            consumed = resolver.TryComposite(GetComponent<Camera>(), source, out var result);
            Graphics.Blit(consumed ? result : source, destination);
        }
    }

    public sealed partial class ActorRenderingSelfTest
    {
        private T ResolveField<T>(SceneReflectionResolve resolver, string name) => (T)typeof(SceneReflectionResolve)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(resolver);

        private Texture2D ResolveTexture(Color[] colors, int width, int height)
        {
            var texture = Own(new Texture2D(width, height, TextureFormat.RGBAFloat, false, true));
            texture.SetPixels(colors); texture.Apply(); texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        private Cubemap ResolveCube(Color color)
        {
            var cube = Own(new Cubemap(4, TextureFormat.RGBAHalf, false));
            var pixels = new Color[16]; for (int i = 0; i < 16; i++) pixels[i] = color;
            for (int face = 0; face < 6; face++) cube.SetPixels(pixels, (CubemapFace)face);
            cube.Apply(false); cube.filterMode = FilterMode.Point; return cube;
        }

        private void VerifySceneReflectionResolve(Report report)
        {
            VerifyReflectionResolveArithmetic(report);
            const int width = 96, height = 72;
            var host = Own(new GameObject("Unified reflection host")); var camera = host.AddComponent<Camera>();
            camera.enabled = false; camera.allowMSAA = false; camera.allowHDR = true;
            camera.renderingPath = RenderingPath.Forward; camera.nearClipPlane = .1f; camera.farClipPlane = 30;
            camera.fieldOfView = 55; camera.aspect = (float)width / height;
            camera.cullingMask = (1 << 18) | (1 << 19); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            host.transform.position = new Vector3(0, 2.5f, -4); host.transform.LookAt(new Vector3(0, .5f, 3));
            var target = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            target.Create(); camera.targetTexture = target;
            Material Flat(Color color)
            {
                var material = Own(new Material(Resources.Load<Shader>("PlanarCapture")));
                material.SetColor("_Color", color); return material;
            }
            GameObject Quad(Vector3 position, Vector3 scale, Quaternion rotation, Material material)
            {
                var go = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.layer = 18;
                go.transform.SetPositionAndRotation(position, rotation); go.transform.localScale = scale;
                go.GetComponent<Renderer>().sharedMaterial = material; return go;
            }
            var floorMaterial = Flat(new Color(.1f, .12f, .15f, 1));
            var floor = Quad(new Vector3(0, 0, 3), new Vector3(12, 12, 1), Quaternion.Euler(90, 0, 0), floorMaterial);
            var left = Quad(new Vector3(-1.5f, 2, 6), new Vector3(3, 4, 1), Quaternion.identity, Flat(new Color(.8f, .1f, .05f, 1)));
            var right = Quad(new Vector3(1.5f, 2, 6), new Vector3(3, 4, 1), Quaternion.identity, Flat(new Color(.05f, .8f, .1f, 1)));
            var actor = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); actor.layer = 19;
            actor.transform.position = new Vector3(0, 1, 2); actor.transform.localScale = new Vector3(1.2f, 2, 1.2f);
            var actorMaterial = Flat(new Color(.05f, .1f, .9f, 1)); actor.GetComponent<Renderer>().sharedMaterial = actorMaterial;
            camera.Render(); var baseline = ReadSceneTarget(target);
            var ssr = host.AddComponent<ScreenSpaceReflection>(); ssr.sceneLayers = 1 << 18;
            ssr.surfaces = new[] { new SceneDepthData.Surface { renderer = floor.GetComponent<Renderer>() },
                new SceneDepthData.Surface { renderer = left.GetComponent<Renderer>(), receiveReflections = false },
                new SceneDepthData.Surface { renderer = right.GetComponent<Renderer>(), receiveReflections = false } };
            ssr.reflectionsEnabled = true; ssr.maximumSteps = 512;
            var planar = host.AddComponent<PlanarReflection>(); planar.reflectionsEnabled = true;
            planar.reflectedLayers = 1 << 19; planar.resolutionScale = 1;
            planar.reflectedSurfaces = new[] { new PlanarReflection.Draw { surface = new SceneDepthData.Surface { renderer = actor.GetComponent<Renderer>() }, material = actorMaterial } };
            planar.receivers = new[] { new PlanarReflection.Receiver { surface = new SceneDepthData.Surface { renderer = floor.GetComponent<Renderer>() } } };
            var resolve = host.AddComponent<SceneReflectionResolve>();
            var receiver = new SceneReflectionResolve.Receiver { surface = new SceneDepthData.Surface { renderer = floor.GetComponent<Renderer>() },
                f0 = Vector3.one, probe = ResolveCube(new Color(.2f, .3f, 1.6f, 1)) };
            resolve.receivers = new[] { receiver }; resolve.screenSpaceReflection = ssr; resolve.planarReflection = planar;
            var hook = host.AddComponent<ReflectionResolveHostProbe>(); hook.resolver = resolve;
            camera.Render();
            FrameworkCheck(report, "reflection-resolve-default-disabled-exact-noop", !hook.consumed && ScenePixelsEqual(baseline, ReadSceneTarget(target)) && ResolveField<RenderTexture>(resolve, "_probe") == null);
            resolve.reflectionsEnabled = true; camera.Render();
            FrameworkCheck(report, "reflection-resolve-refuses-undeclared-source-contract", !hook.consumed && resolve.UnavailableReason.Contains("exclude") && ResolveField<RenderTexture>(resolve, "_probe") == null);
            resolve.inputExcludesIndirectSpecular = true; camera.Render(); camera.Render();
            FrameworkCheck(report, "reflection-resolve-hdr-host-consumes-and-owns", hook.consumed && resolve.TryGetRadiance(camera, out _) && !resolve.TryComposite(camera, target, out _) && !resolve.TryGetRadiance(_camera, out _));
            VerifyUnifiedReflectionPixels(report, resolve, ssr, planar, camera, baseline, "all-three", true);
            SaveSsrPreview("reflection-resolve-main", ReadSceneTarget(target), width, height, false);
            resolve.TryGetRadiance(camera, out var rawRadiance);
            SaveSsrPreview("reflection-resolve-radiance", ReadSceneTarget(rawRadiance), width, height, false);
            var maskedSsr = SsrPixels(ssr, camera);
            planar.TryGetReflection(camera, width, height, out var planarTexture); var mask = ReadSceneTarget(planarTexture);
            int skipped = 0, forbidden = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i].a > 1e-5f)
            { skipped++; if (maskedSsr[i].a > 0) forbidden++; }
            FrameworkCheck(report, "reflection-resolve-planar-regions-skip-ssr-trace", skipped > 20 && forbidden == 0, forbidden);
            resolve.planarReflection = null; camera.Render(); camera.Render();
            var unmasked = SsrPixels(ssr, camera); int unsuppressed = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i].a > 1e-5f && unmasked[i].a > .01f) unsuppressed++;
            FrameworkCheck(report, "reflection-resolve-skip-negative-control-without-planar", unsuppressed > 5, unsuppressed);
            resolve.planarReflection = planar;
            // Reduced planar coverage blends toward probe, never a second SSR term.
            planar.receivers[0].strength = .25f; camera.Render();
            VerifyUnifiedReflectionPixels(report, resolve, ssr, planar, camera, baseline, "fractional-planar", false);
            planar.receivers[0].strength = 1;
            ssr.reflectionsEnabled = false; planar.reflectionsEnabled = false; camera.Render();
            var probePixels = ReadSceneTarget(ResolveField<RenderTexture>(resolve, "_probe"));
            int probeMatches = 0;
            foreach (Color p in probePixels) if (p.a > .5f && Mathf.Abs(p.r - .2f) < .001f && Mathf.Abs(p.b - 1.6f) < .002f) probeMatches++;
            FrameworkCheck(report, "reflection-resolve-linear-hdr-cubemap-fallback", probeMatches > 200, probeMatches);
            VerifyUnifiedReflectionPixels(report, resolve, ssr, planar, camera, baseline, "probe-only", false);
            VerifyDirectionalReflectionProbe(report, resolve, camera, new[] { host, floor, left, right, actor });
            receiver.f0 = new Vector3(.04f, .2f, .5f); receiver.occlusion = .7f; receiver.specularScale = .8f;
            receiver.surface.smoothness = .6f; camera.Render();
            VerifyReflectionResponse(report, resolve, camera, floor, "perspective");
            camera.orthographic = true; camera.orthographicSize = 3; camera.Render();
            VerifyReflectionResponse(report, resolve, camera, floor, "orthographic");
            camera.orthographic = false; receiver.f0 = Vector3.one; receiver.occlusion = 1; receiver.specularScale = 1; receiver.surface.smoothness = 1;
            ssr.reflectionsEnabled = true; planar.reflectionsEnabled = true; camera.Render(); camera.Render();
            var flat = ReadSceneTarget(target);
            var normal = ResolveTexture(new[] { new Color(.8f, .5f, .9f, 1) }, 1, 1);
            receiver.normalMap = normal; camera.Render(); camera.Render();
            var distorted = ReadSceneTarget(target); int changed = 0;
            for (int i = 0; i < flat.Length; i++) if (Mathf.Abs(flat[i].r - distorted[i].r) > .03f || Mathf.Abs(flat[i].b - distorted[i].b) > .03f) changed++;
            FrameworkCheck(report, "reflection-resolve-normal-difference-changes-final-reflection", changed > 30, changed);
            VerifyReflectionNormalOffset(report, resolve, camera, floor, receiver, new Vector3(.6f, 0, .8f), "tangent-x");
            VerifyUnifiedReflectionPixels(report, resolve, ssr, planar, camera, baseline, "normal-distortion", false);
            SaveSsrPreview("reflection-resolve-normal-distortion", ReadSceneTarget(target), width, height, false);
            var normalY = ResolveTexture(new[] { new Color(.5f, .8f, .9f, 1) }, 1, 1);
            receiver.normalMap = normalY; camera.Render();
            VerifyReflectionNormalOffset(report, resolve, camera, floor, receiver, new Vector3(0, .6f, .8f), "tangent-y");
            Vector3 oldScale = floor.transform.localScale;
            floor.transform.localScale = new Vector3(-oldScale.x, oldScale.y, oldScale.z);
            floorMaterial.SetFloat("_Cull", 0); receiver.surface.cull = CullMode.Off; camera.Render();
            VerifyReflectionNormalOffset(report, resolve, camera, floor, receiver, new Vector3(0, .6f, .8f), "mirrored-transform");
            floor.transform.localScale = oldScale; floorMaterial.SetFloat("_Cull", 2); receiver.surface.cull = CullMode.Back;
            receiver.normalStrength = 0; camera.Render();
            var zeroNormal = ReadSceneTarget(ResolveField<RenderTexture>(resolve, "_offset")); float offsetMax = 0;
            foreach (Color p in zeroNormal) offsetMax = Mathf.Max(offsetMax, Mathf.Abs(p.r), Mathf.Abs(p.g));
            FrameworkCheck(report, "reflection-resolve-zero-normal-strength-identity-offset", offsetMax == 0);
            receiver.normalMap = null; receiver.normalStrength = 1;
            receiver.specularScale = 0; camera.Render();
            FrameworkCheck(report, "reflection-resolve-zero-response-preserves-source", ScenePixelsEqual(baseline, ReadSceneTarget(target)));
            receiver.specularScale = 1;
            receiver.surface.receiveReflections = false; camera.Render();
            FrameworkCheck(report, "reflection-resolve-disabled-receiver-does-not-submit", !hook.consumed && ResolveField<RenderTexture>(resolve, "_probe") == null);
            receiver.surface.receiveReflections = true;
            receiver.f0 = new Vector3(float.NaN, 0, 0); camera.Render();
            FrameworkCheck(report, "reflection-resolve-invalid-material-input-releases", !hook.consumed && ResolveField<RenderTexture>(resolve, "_probe") == null);
            receiver.f0 = Vector3.one; receiver.probe = Texture2D.whiteTexture; camera.Render();
            FrameworkCheck(report, "reflection-resolve-rejects-2d-probe", !hook.consumed);
            receiver.probe = ResolveCube(new Color(.2f, .3f, 1.6f, 1)); camera.Render(); camera.Render();
            var resized = Own(new RenderTexture(81, 61, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            resized.Create(); camera.targetTexture = resized;
            FrameworkCheck(report, "reflection-resolve-target-change-invalidates", !resolve.TryGetRadiance(camera, out _));
            camera.Render(); camera.Render();
            FrameworkCheck(report, "reflection-resolve-resize-recovers", hook.consumed && resolve.TryGetRadiance(camera, out var resizedRadiance) && resizedRadiance.width == 81);
            camera.rect = new Rect(0, 0, .5f, 1); camera.Render();
            FrameworkCheck(report, "reflection-resolve-viewport-rejected", !hook.consumed);
            camera.rect = new Rect(0, 0, 1, 1); camera.Render(); camera.Render();
            int beforeBuffers = camera.GetCommandBuffers(CameraEvent.BeforeImageEffects).Length;
            resolve.enabled = false;
            FrameworkCheck(report, "reflection-resolve-disable-releases-own-resources", !resolve.TryGetRadiance(camera, out _) && ResolveField<RenderTexture>(resolve, "_probe") == null && camera.GetCommandBuffers(CameraEvent.BeforeImageEffects).Length == beforeBuffers - 1);
            resolve.enabled = true; camera.Render(); camera.Render();
            FrameworkCheck(report, "reflection-resolve-reenable-single-buffer", hook.consumed && camera.GetCommandBuffers(CameraEvent.BeforeImageEffects).Length == beforeBuffers);
            hook.enabled = false; var pipeline = host.AddComponent<OriginalStyleRenderPipeline>(); pipeline.sceneReflectionResolve = resolve;
            pipeline.screenSpaceReflection = ssr; camera.Render(); camera.Render();
            FrameworkCheck(report, "reflection-resolve-production-pipeline-prefers-unified-route", resolve.TryGetRadiance(camera, out _) && !ssr.TryTrace(camera, resized, null, out _));
            resolve.TryGetRadiance(camera, out var rasterResolved); var rasterUnified = ReadSceneTarget(rasterResolved);
            ssr.backend = SceneShaderBackend.Compute; ssr.allowComputeFallback = false; camera.Render(); camera.Render();
            resolve.TryGetRadiance(camera, out var computeResolved);
            float backendError = PixelError(rasterUnified, ReadSceneTarget(computeResolved));
            FrameworkCheck(report, "ssr-compute-unified-consumer-radiance-equivalence", ssr.ActiveBackend == SceneShaderBackend.Compute &&
                ssr.ComputeDispatchCount == 1 && backendError < .001f && SsrHitCount(SsrPixels(ssr, camera)) > 10, backendError);
            planar.TryGetReflection(camera, resized.width, resized.height, out var computePlanar);
            var computeMask = ReadSceneTarget(computePlanar); var computeSsr = SsrPixels(ssr, camera);
            int coveredPixels = 0, invalidTrace = 0;
            for (int i = 0; i < computeMask.Length; i++) if (computeMask[i].a > 1e-5f)
            { coveredPixels++; if (computeSsr[i].a > 0) invalidTrace++; }
            FrameworkCheck(report, "ssr-compute-unified-real-planar-skip", coveredPixels > 20 && invalidTrace == 0, invalidTrace);
            pipeline.enabled = false; resolve.enabled = false; ssr.enabled = false; planar.enabled = false;
            camera.targetTexture = null; target.Release(); resized.Release();
        }

        private void VerifyUnifiedReflectionPixels(Report report, SceneReflectionResolve resolve, ScreenSpaceReflection ssr,
            PlanarReflection planar, Camera camera, Color[] baseline, string label, bool requireAll)
        {
            int width = camera.targetTexture.width, height = camera.targetTexture.height;
            var zero = new Color[width * height];
            var probe = ReadSceneTarget(ResolveField<RenderTexture>(resolve, "_probe"));
            var response = ReadSceneTarget(ResolveField<RenderTexture>(resolve, "_response"));
            var offset = ReadSceneTarget(ResolveField<RenderTexture>(resolve, "_offset"));
            var a = planar != null && planar.TryGetReflection(camera, width, height, out var planarImage) ? ReadSceneTarget(planarImage) : zero;
            var b = ssr != null && ssr.TryGetReflection(camera, out var ssrImage) ? ReadSceneTarget((RenderTexture)ssrImage) : zero;
            var actual = ReadSceneTarget(camera.targetTexture);
            float error = 0, alphaError = 0; int planarCount = 0, ssrCount = 0, probeCount = 0, unchanged = 0;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                Color radiance = Color.clear;
                if (offset[i].a > .5f)
                {
                    radiance = probe[i];
                    float u = (x + .5f) / width + offset[i].r, v = (y + .5f) / height + offset[i].g;
                    int j = Mathf.FloorToInt(v * height) * width + Mathf.FloorToInt(u * width);
                    if (u >= 0 && u < 1 && v >= 0 && v < 1 && Mathf.Abs(offset[j].b - offset[i].b) < .25f)
                    {
                        if (a[j].a > 1e-5f) { radiance = Color.Lerp(probe[i], a[j], Mathf.Clamp01(a[j].a)); planarCount++; }
                        else if (b[j].a > 0) { radiance = Color.Lerp(probe[i], b[j], Mathf.Clamp01(b[j].a)); ssrCount++; }
                        else probeCount++;
                    }
                    else probeCount++;
                }
                else if (actual[i].Equals(baseline[i])) unchanged++;
                Color expected = baseline[i] + radiance * response[i];
                error = Mathf.Max(error, Mathf.Abs(actual[i].r - expected.r), Mathf.Abs(actual[i].g - expected.g), Mathf.Abs(actual[i].b - expected.b));
                alphaError = Mathf.Max(alphaError, Mathf.Abs(actual[i].a - baseline[i].a));
            }
            FrameworkCheck(report, "reflection-resolve-independent-radiance-composition-" + label, error < .004f && alphaError == 0 && unchanged > 20 &&
                (!requireAll || (planarCount > 10 && ssrCount > 30 && probeCount > 30)), error);
        }

        private void VerifyReflectionResponse(Report report, SceneReflectionResolve resolve, Camera camera, GameObject floor, string label)
        {
            var value = resolve.receivers[0]; var pixels = ReadSceneTarget(ResolveField<RenderTexture>(resolve, "_response"));
            int width = camera.targetTexture.width, height = camera.targetTexture.height, compared = 0; float error = 0;
            Vector3 normal = floor.transform.TransformDirection(Vector3.back);
            var plane = new Plane(normal, floor.transform.position);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                Color actual = pixels[y * width + x]; if (actual.a < .5f) continue;
                Ray ray = camera.ScreenPointToRay(new Vector3(x + .5f, y + .5f)); if (!plane.Raycast(ray, out float t)) continue;
                Vector3 view = camera.orthographic ? -camera.transform.forward : (camera.transform.position - ray.GetPoint(t)).normalized;
                Vector3 f = value.f0 + (Vector3.one - value.f0) * Mathf.Pow(1 - Mathf.Clamp01(Vector3.Dot(normal, view)), 5);
                f *= value.surface.smoothness * value.occlusion * value.specularScale;
                error = Mathf.Max(error, Mathf.Abs(actual.r - f.x), Mathf.Abs(actual.g - f.y), Mathf.Abs(actual.b - f.z)); compared++;
            }
            FrameworkCheck(report, "reflection-resolve-fresnel-smoothness-ao-scale-" + label, compared > 100 && error < .001f, error);
        }

        private void VerifyDirectionalReflectionProbe(Report report, SceneReflectionResolve resolve, Camera camera, GameObject[] objects)
        {
            var receiver = resolve.receivers[0]; Texture original = receiver.probe;
            var cube = Own(new Cubemap(4, TextureFormat.RGBAHalf, false));
            var colors = new Color[6];
            for (int face = 0; face < 6; face++)
            {
                colors[face] = new Color(.1f + face * .1f, .8f - face * .1f, .2f + face * .05f, 1);
                var pixels = new Color[16]; for (int i = 0; i < 16; i++) pixels[i] = colors[face];
                cube.SetPixels(pixels, (CubemapFace)face);
            }
            cube.Apply(false); cube.filterMode = FilterMode.Point; receiver.probe = cube;
            var group = Own(new GameObject("Reflection probe direction fixture"));
            foreach (GameObject go in objects) go.transform.SetParent(group.transform, true);
            var rotations = new[] { Quaternion.identity, Quaternion.Euler(0, 90, 0), Quaternion.Euler(0, -90, 0),
                Quaternion.Euler(0, 180, 0), Quaternion.Euler(90, 0, 0), Quaternion.Euler(-90, 0, 0) };
            int usedFaces = 0;
            for (int test = 0; test < rotations.Length; test++)
            {
                group.transform.rotation = rotations[test]; camera.Render();
                var actual = ReadSceneTarget(ResolveField<RenderTexture>(resolve, "_probe"));
                int width = camera.targetTexture.width, height = camera.targetTexture.height, compared = 0; float error = 0;
                Vector3 normal = group.transform.up;
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    Color p = actual[y * width + x]; if (p.a < .5f) continue;
                    Vector3 direction = Vector3.Reflect(camera.ScreenPointToRay(new Vector3(x + .5f, y + .5f)).direction, normal);
                    Vector3 magnitude = new Vector3(Mathf.Abs(direction.x), Mathf.Abs(direction.y), Mathf.Abs(direction.z));
                    int axis = magnitude.x > magnitude.y ? 0 : 1; if (magnitude.z > magnitude[axis]) axis = 2;
                    float next = Mathf.Max(magnitude[(axis + 1) % 3], magnitude[(axis + 2) % 3]);
                    if (magnitude[axis] - next < .03f) continue;
                    int face = axis * 2 + (direction[axis] >= 0 ? 0 : 1);
                    Color expected = colors[face]; usedFaces |= 1 << face; compared++;
                    error = Mathf.Max(error, Mathf.Abs(p.r - expected.r), Mathf.Abs(p.g - expected.g), Mathf.Abs(p.b - expected.b));
                }
                FrameworkCheck(report, "reflection-resolve-directional-cube-world-orientation-" + test, compared > 100 && error < .001f, error);
            }
            FrameworkCheck(report, "reflection-resolve-directional-cube-all-six-faces-observed", usedFaces == 63);
            group.transform.rotation = Quaternion.identity;
            receiver.decodeProbeHdr = true; receiver.probeHdrDecode = new Vector4(2, 1, 0, 0); camera.Render();
            float decodeError = 0; int decoded = 0;
            foreach (Color p in ReadSceneTarget(ResolveField<RenderTexture>(resolve, "_probe"))) if (p.a > .5f)
            { decodeError = Mathf.Max(decodeError, Mathf.Abs(p.r - colors[4].r * 2), Mathf.Abs(p.b - colors[4].b * 2)); decoded++; }
            FrameworkCheck(report, "reflection-resolve-probe-hdr-decode-instructions", decoded > 100 && decodeError < .002f, decodeError);
            receiver.decodeProbeHdr = false; receiver.probeHdrDecode = new Vector4(1, 1, 0, 0); receiver.probe = original;
        }

        private void VerifyReflectionNormalOffset(Report report, SceneReflectionResolve resolve, Camera camera, GameObject floor,
            SceneReflectionResolve.Receiver receiver, Vector3 mapped, string label)
        {
            Mesh mesh = floor.GetComponent<MeshFilter>().sharedMesh;
            Matrix4x4 matrix = floor.transform.localToWorldMatrix;
            Vector3 geometric = matrix.inverse.transpose.MultiplyVector(mesh.normals[0]).normalized;
            Vector4 rawTangent = mesh.tangents[0]; Vector3 tangent = matrix.MultiplyVector(new Vector3(rawTangent.x, rawTangent.y, rawTangent.z));
            tangent = (tangent - geometric * Vector3.Dot(tangent, geometric)).normalized;
            Vector3 bitangent = Vector3.Cross(geometric, tangent) * rawTangent.w * Mathf.Sign(matrix.determinant);
            Vector3 shading = (tangent * mapped.x + bitangent * mapped.y + geometric * mapped.z).normalized;
            Vector3 delta = camera.worldToCameraMatrix.MultiplyVector(shading - geometric);
            Vector2 expected = new Vector2(delta.x * receiver.distortionScale.x, delta.z * receiver.distortionScale.y);
            float error = 0; int count = 0;
            foreach (Color p in ReadSceneTarget(ResolveField<RenderTexture>(resolve, "_offset"))) if (p.a > .5f)
            { count++; error = Mathf.Max(error, Mathf.Abs(p.r - expected.x), Mathf.Abs(p.g - expected.y)); }
            FrameworkCheck(report, "reflection-resolve-high-resolution-normal-offset-analytic-" + label, count > 100 && error < .0001f, error);
        }

        private void VerifyReflectionResolveArithmetic(Report report)
        {
            const int width = 8, height = 2;
            var material = Own(new Material(Resources.Load<Shader>("SceneReflectionResolve")));
            var probe = new Color[16]; var planar = new Color[16]; var ssr = new Color[16]; var offset = new Color[16]; var basis = new Color[16]; var response = new Color[16];
            for (int i = 0; i < 16; i++)
            {
                probe[i] = new Color(.1f, .2f, .3f, 1); ssr[i] = new Color(2, 3, 4, (i % 4) / 3f);
                planar[i] = new Color(4, 2, 1, i / 4 == 0 ? 0 : (i / 4) / 3f);
                offset[i] = new Color(0, 0, 1, 1); basis[i] = new Color(.5f, .7f, .9f, .37f);
                response[i] = new Color(.2f, .3f, .4f, 1);
            }
            var output = Own(new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); output.Create();
            var composite = Own(new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); composite.Create();
            var baseTexture = ResolveTexture(basis, width, height);
            material.SetTexture("_ResolveProbe", ResolveTexture(probe, width, height)); material.SetTexture("_ResolvePlanar", ResolveTexture(planar, width, height));
            material.SetTexture("_ResolveSsr", ResolveTexture(ssr, width, height)); material.SetTexture("_ResolveOffset", ResolveTexture(offset, width, height));
            material.SetTexture("_ResolveResponse", ResolveTexture(response, width, height)); material.SetVector("_ResolveInputs", new Vector4(1, 1, 0, 0));
            Graphics.Blit(baseTexture, output, material, 1); material.SetTexture("_ResolveRadiance", output); Graphics.Blit(baseTexture, composite, material, 2);
            var actual = ReadSceneTarget(composite);
            for (int i = 0; i < 16; i++)
            {
                Color radiance = planar[i].a > 0 ? probe[i] * (1 - planar[i].a) + planar[i] * planar[i].a : probe[i] * (1 - ssr[i].a) + ssr[i] * ssr[i].a;
                Color expected = basis[i] + radiance * response[i];
                float error = Mathf.Max(Mathf.Abs(actual[i].r - expected.r), Mathf.Abs(actual[i].g - expected.g), Mathf.Abs(actual[i].b - expected.b), Mathf.Abs(actual[i].a - basis[i].a));
                FrameworkCheck(report, "reflection-resolve-analytic-priority-weight-hdr-alpha-" + i, error < .002f, error);
            }
            // Cross receiver IDs and offscreen offsets must use local Probe.
            for (int i = 0; i < 16; i++) offset[i] = new Color(i % 2 == 0 ? 2 : -1f / width, 0, i + 1, 1);
            material.SetTexture("_ResolveOffset", ResolveTexture(offset, width, height)); Graphics.Blit(baseTexture, output, material, 1);
            float fallbackError = 0; foreach (Color p in ReadSceneTarget(output)) fallbackError = Mathf.Max(fallbackError, Mathf.Abs(p.r - .1f), Mathf.Abs(p.b - .3f));
            FrameworkCheck(report, "reflection-resolve-distortion-offscreen-and-receiver-boundary-fallback", fallbackError < .001f, fallbackError);
            output.Release(); composite.Release();
        }
    }
}
