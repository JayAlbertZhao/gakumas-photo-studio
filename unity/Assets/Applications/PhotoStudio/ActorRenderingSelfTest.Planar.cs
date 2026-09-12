using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class PlanarHostProbe : MonoBehaviour
    {
        public PlanarReflection reflection;
        public bool available;
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            available = reflection.TryGetReflection(GetComponent<Camera>(), source.width, source.height, out _);
            Graphics.Blit(source, destination);
        }
    }

    public sealed partial class ActorRenderingSelfTest
    {
        private T PlanarField<T>(PlanarReflection planar, string name) => (T)typeof(PlanarReflection)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(planar);

        private Color[] PlanarPixels(PlanarReflection planar, Camera camera)
        {
            var target = camera.targetTexture;
            if (!planar.TryGetReflection(camera, target.width, target.height, out var texture))
                throw new InvalidOperationException("Planar unavailable: " + planar.UnavailableReason);
            return ReadSceneTarget(texture);
        }

        private void VerifyPlanarReflection(Report report)
        {
            const int width = 128, height = 96;
            var host = Own(new GameObject("Planar synthetic host"));
            var camera = host.AddComponent<Camera>();
            camera.enabled = false; camera.allowMSAA = false; camera.allowHDR = true;
            camera.renderingPath = RenderingPath.Forward; camera.nearClipPlane = .1f; camera.farClipPlane = 40;
            camera.fieldOfView = 65; camera.aspect = (float)width / height;
            camera.cullingMask = (1 << 20) | (1 << 21);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            host.transform.position = new Vector3(0, 2.5f, -4); host.transform.LookAt(new Vector3(0, 0, 4));
            var target = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            target.Create(); camera.targetTexture = target;
            Material Flat(Color color)
            {
                var material = Own(new Material(Resources.Load<Shader>("StudioAccent")));
                material.SetColor("_Color", color); return material;
            }
            GameObject Quad(string name, Vector3 position, Vector3 scale, Quaternion rotation, Material material)
            {
                var go = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.name = name; go.layer = 20;
                go.transform.SetPositionAndRotation(position, rotation); go.transform.localScale = scale;
                go.GetComponent<Renderer>().sharedMaterial = material; return go;
            }
            var red = Flat(new Color(.8f, .1f, .05f, 0));
            var green = Flat(new Color(.05f, .8f, .1f, 1));
            var floor = Quad("Planar receiver", new Vector3(0, 0, 3), new Vector3(14, 14, 1), Quaternion.Euler(90, 0, 0), Flat(Color.black));
            var left = Quad("Planar red panel", new Vector3(-1.6f, 1.8f, 5), new Vector3(2.8f, 3.2f, 1), Quaternion.identity, red);
            var right = Quad("Planar green panel", new Vector3(1.6f, 1.8f, 5), new Vector3(2.8f, 3.2f, 1), Quaternion.identity, green);
            var actor = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); actor.layer = 21;
            actor.transform.position = new Vector3(0, .9f, 2); actor.transform.localScale = new Vector3(.8f, 1.8f, .8f);
            actor.GetComponent<Renderer>().sharedMaterial = Flat(new Color(.05f, .1f, .9f, 1));
            PlanarReflection.Draw Draw(GameObject go, Material material) => new PlanarReflection.Draw {
                surface = new SceneDepthData.Surface { renderer = go.GetComponent<Renderer>() }, material = material
            };
            var leftDraw = Draw(left, red); var rightDraw = Draw(right, green);
            var receiver = new PlanarReflection.Receiver { surface = new SceneDepthData.Surface { renderer = floor.GetComponent<Renderer>() } };
            camera.Render(); Color[] original = ReadSceneTarget(target);
            var planar = host.AddComponent<PlanarReflection>();
            planar.reflectedLayers = 1 << 20; planar.reflectedSurfaces = new[] { leftDraw, rightDraw };
            planar.receivers = new[] { receiver }; planar.resolutionScale = 1;
            camera.Render();
            FrameworkCheck(report, "planar-default-off-noop", ScenePixelsEqual(original, ReadSceneTarget(target)) && PlanarField<RenderTexture>(planar, "_capture") == null);
            planar.reflectionsEnabled = true; camera.Render();
            var pixels = PlanarPixels(planar, camera);
            int hits = SsrHitCount(pixels);
            FrameworkCheck(report, "planar-asymmetric-geometry-positive", hits > 80, hits);
            FrameworkCheck(report, "planar-main-color-depth-mode-materials-unchanged", ScenePixelsEqual(original, ReadSceneTarget(target)) &&
                camera.depthTextureMode == DepthTextureMode.None && left.GetComponent<Renderer>().sharedMaterial == red && !GL.invertCulling);
            VerifyPlanarAnalyticPanels(report, camera, pixels, width, height, "perspective");
            SaveSsrPreview("planar-main-unmodified", original, width, height, false);
            SaveSsrPreview("planar-radiance-coverage", pixels, width, height, true);
            SaveSsrPreview("planar-capture", ReadSceneTarget(PlanarField<RenderTexture>(planar, "_capture")), width, height, true);
            int redPixels = 0, actorPixels = 0, reflectedActorPixels = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a > .5f && pixels[i].r > .5f && pixels[i].g < .05f) redPixels++;
                if (original[i].b > .7f && original[i].r < .2f)
                { actorPixels++; if (pixels[i].a > .01f) reflectedActorPixels++; }
            }
            FrameworkCheck(report, "planar-coverage-independent-of-opaque-material-alpha", redPixels > 30, redPixels);
            FrameworkCheck(report, "planar-receiver-real-depth-protects-foreground", actorPixels > 20 && reflectedActorPixels == 0, reflectedActorPixels);
            FrameworkCheck(report, "planar-camera-size-ownership", !planar.TryGetReflection(_camera, width, height, out _) && !planar.TryGetReflection(camera, width - 1, height, out _));
            var hook = host.AddComponent<PlanarHostProbe>(); hook.reflection = planar; camera.Render();
            FrameworkCheck(report, "planar-available-to-hdr-image-effect-host", hook.available && ScenePixelsEqual(original, ReadSceneTarget(target)));
            hook.enabled = false;
            VerifyPlanarCutout(report, camera, planar, leftDraw, receiver);
            planar.reflectedSurfaces = new[] { leftDraw, rightDraw };
            // A reflected object can be outside the primary camera frustum.
            var emitterMaterial = Own(new Material(Resources.Load<Shader>("PlanarCapture")));
            emitterMaterial.SetColor("_Color", Color.black); emitterMaterial.SetColor("_Emission", new Color(4, .1f, 3, 1));
            var emitter = Quad("Offscreen HDR emitter", new Vector3(0, 5.5f, 4), new Vector3(3, .6f, 1), Quaternion.identity, emitterMaterial);
            var emitterDraw = Draw(emitter, emitterMaterial);
            planar.reflectedSurfaces = new[] { emitterDraw }; camera.Render();
            int hdrHits = 0, mainHdr = 0;
            foreach (Color p in PlanarPixels(planar, camera)) if (p.a > .01f && p.r > 2 && p.b > 2) hdrHits++;
            foreach (Color p in ReadSceneTarget(target)) if (p.r > 2 && p.b > 2) mainHdr++;
            FrameworkCheck(report, "planar-offscreen-hdr-emitter", hdrHits > 5 && mainHdr == 0, hdrHits);
            emitter.SetActive(false);
            // Clip the negative plane half-space, not just CPU bounds.
            left.transform.position += Vector3.down * 5; planar.reflectedSurfaces = new[] { leftDraw }; camera.Render();
            FrameworkCheck(report, "planar-oblique-clips-below-plane", SsrHitCount(PlanarPixels(planar, camera)) == 0);
            left.transform.position += Vector3.up * 5; camera.Render();
            FrameworkCheck(report, "planar-oblique-positive-side-recovers", SsrHitCount(PlanarPixels(planar, camera)) > 30);
            // Receiver registration cannot project onto a displaced surface.
            floor.transform.position += Vector3.up * .2f; camera.Render();
            FrameworkCheck(report, "planar-rejects-noncoplanar-receiver", SsrHitCount(PlanarPixels(planar, camera)) == 0);
            floor.transform.position -= Vector3.up * .2f;
            planar.reflectedSurfaces = new[] { leftDraw, rightDraw };
            receiver.strength = .25f; camera.Render();
            float maxAlpha = 0; foreach (Color p in PlanarPixels(planar, camera)) maxAlpha = Mathf.Max(maxAlpha, p.a);
            FrameworkCheck(report, "planar-receiver-strength-is-coverage", Mathf.Abs(maxAlpha - .25f) < .001f, maxAlpha);
            receiver.strength = 1; receiver.surface.smoothness = 1; camera.Render(); var sharp = PlanarPixels(planar, camera);
            receiver.surface.smoothness = 0; camera.Render(); var rough = PlanarPixels(planar, camera);
            int fractionalSharp = 0, fractionalRough = 0;
            for (int i = 0; i < rough.Length; i++)
            {
                if (sharp[i].a > .01f && sharp[i].a < .99f) fractionalSharp++;
                if (rough[i].a > .01f && rough[i].a < .99f) fractionalRough++;
            }
            FrameworkCheck(report, "planar-roughness-filters-radiance-and-coverage", fractionalRough > fractionalSharp + 30, fractionalRough - fractionalSharp);
            receiver.surface.smoothness = 1;
            planar.resolutionScale = .5f; camera.Render();
            FrameworkCheck(report, "planar-half-resolution-capture-full-resolution-output", PlanarField<RenderTexture>(planar, "_capture").width == width / 2 && PlanarPixels(planar, camera).Length == width * height && SsrHitCount(PlanarPixels(planar, camera)) > 30);
            planar.resolutionScale = 1;
            // Invalid pass/layer must not submit an old cached draw.
            leftDraw.shaderPass = 999; planar.reflectedSurfaces = new[] { leftDraw }; camera.Render();
            FrameworkCheck(report, "planar-invalid-pass-releases-stale-output", !planar.TryGetReflection(camera, width, height, out _) && PlanarField<RenderTexture>(planar, "_capture") == null);
            leftDraw.shaderPass = 0; planar.reflectedLayers = 1 << 21; camera.Render();
            FrameworkCheck(report, "planar-explicit-reflected-layer-exclusion", !planar.TryGetReflection(camera, width, height, out _));
            planar.reflectedLayers = 1 << 20; planar.reflectedSurfaces = new[] { leftDraw, rightDraw };
            camera.orthographic = true; camera.orthographicSize = 4; camera.Render();
            FrameworkCheck(report, "planar-orthographic-positive", SsrHitCount(PlanarPixels(planar, camera)) > 30);
            VerifyPlanarAnalyticPanels(report, camera, PlanarPixels(planar, camera), width, height, "orthographic");
            camera.orthographic = false;
            Matrix4x4 customView = camera.worldToCameraMatrix * Matrix4x4.Translate(new Vector3(.3f, 0, 0));
            Matrix4x4 customProjection = camera.projectionMatrix; customProjection.m02 += .07f;
            camera.worldToCameraMatrix = customView; camera.projectionMatrix = customProjection; camera.Render();
            VerifyPlanarAnalyticPanels(report, camera, PlanarPixels(planar, camera), width, height, "custom-view-projection");
            FrameworkCheck(report, "planar-custom-view-and-projection-unchanged", camera.worldToCameraMatrix == customView && camera.projectionMatrix == customProjection);
            camera.ResetWorldToCameraMatrix(); camera.ResetProjectionMatrix();
            // A rotated/translated whole fixture must preserve projected output.
            camera.Render(); var beforeRotate = PlanarPixels(planar, camera);
            var group = Own(new GameObject("Rotated planar fixture"));
            foreach (var go in new[] { host, left, right, floor, actor }) go.transform.SetParent(group.transform, true);
            group.transform.SetPositionAndRotation(new Vector3(3, 1, -2), Quaternion.Euler(13, 24, -17));
            planar.planePoint = group.transform.position; planar.planeNormal = group.transform.up;
            camera.Render(); var rotated = PlanarPixels(planar, camera);
            int changedRotation = 0;
            for (int i = 0; i < rotated.Length; i++) if (Mathf.Abs(rotated[i].a - beforeRotate[i].a) > .01f) changedRotation++;
            FrameworkCheck(report, "planar-rotated-translated-plane-invariance", changedRotation <= 4 && SsrHitCount(rotated) > 30, changedRotation);
            group.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); planar.planePoint = Vector3.zero; planar.planeNormal = Vector3.up;
            VerifyPlanarSkinned(report, camera, planar, left.GetComponent<MeshFilter>().sharedMesh, green);
            planar.reflectedSurfaces = new[] { leftDraw, rightDraw };
            camera.Render();
            var resized = Own(new RenderTexture(95, 71, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            resized.Create(); camera.targetTexture = resized;
            FrameworkCheck(report, "planar-target-change-invalidates-borrowed-output", !planar.TryGetReflection(camera, width, height, out _));
            planar.resolutionScale = .5f; camera.Render();
            FrameworkCheck(report, "planar-odd-resize-recreates-capture", PlanarField<RenderTexture>(planar, "_capture").width == 48 && PlanarField<RenderTexture>(planar, "_capture").height == 36 && SsrHitCount(PlanarPixels(planar, camera)) > 20);
            camera.rect = new Rect(0, 0, .5f, 1); camera.Render();
            FrameworkCheck(report, "planar-viewport-rejected", !planar.TryGetReflection(camera, 95, 71, out _));
            camera.rect = new Rect(0, 0, 1, 1); planar.planeNormal = Vector3.zero; camera.Render();
            FrameworkCheck(report, "planar-invalid-plane-rejected", !planar.TryGetReflection(camera, 95, 71, out _));
            planar.planeNormal = Vector3.down; camera.Render();
            FrameworkCheck(report, "planar-negative-camera-side-rejected", !planar.TryGetReflection(camera, 95, 71, out _));
            planar.planeNormal = Vector3.up; camera.Render();
            bool oldCulling = GL.invertCulling;
            try
            {
                GL.invertCulling = true; camera.Render();
                FrameworkCheck(report, "planar-restores-preexisting-inverted-culling", GL.invertCulling);
            }
            finally { GL.invertCulling = oldCulling; }
            planar.enabled = false;
            FrameworkCheck(report, "planar-disable-detaches-and-releases", camera.GetCommandBuffers(CameraEvent.BeforeImageEffects).Length == 0 && PlanarField<Camera>(planar, "_captureCamera") == null && !planar.TryGetReflection(camera, 95, 71, out _));
            planar.enabled = true; camera.Render();
            FrameworkCheck(report, "planar-reenable-single-buffer", camera.GetCommandBuffers(CameraEvent.BeforeImageEffects).Length == 1 && SsrHitCount(PlanarPixels(planar, camera)) > 20);
            planar.enabled = false; camera.targetTexture = null; target.Release(); resized.Release();
            VerifyPlanarMatrix(report);
        }

        private void VerifyPlanarCutout(Report report, Camera camera, PlanarReflection planar,
            PlanarReflection.Draw draw, PlanarReflection.Receiver receiver)
        {
            Material original = draw.material;
            var material = Own(new Material(Resources.Load<Shader>("PlanarCapture")));
            var alpha = Own(new Texture2D(2, 1, TextureFormat.RGBA32, false, true));
            alpha.SetPixels(new[] { new Color(1, 1, 1, 0), Color.white }); alpha.Apply();
            alpha.filterMode = FilterMode.Point; alpha.wrapMode = TextureWrapMode.Clamp;
            material.SetTexture("_BaseMap", alpha); material.SetFloat("_Cutoff", .5f);
            draw.material = material; draw.surface.alphaMask = alpha; draw.surface.alphaCutoff = .5f;
            planar.reflectedSurfaces = new[] { draw }; camera.Render();
            int halfCoverage = SsrHitCount(ReadSceneTarget(PlanarField<RenderTexture>(planar, "_capture")));
            draw.surface.alphaMaskST = new Vector4(0, 0, .75f, .5f);
            material.SetTextureScale("_BaseMap", Vector2.zero); material.SetTextureOffset("_BaseMap", new Vector2(.75f, .5f));
            camera.Render(); int fullCoverage = SsrHitCount(ReadSceneTarget(PlanarField<RenderTexture>(planar, "_capture")));
            FrameworkCheck(report, "planar-capture-cutout-matching-color-and-mask", halfCoverage > 20 && fullCoverage > halfCoverage * 1.8f, fullCoverage - halfCoverage);
            draw.surface.alphaMaskST = new Vector4(0, 0, .25f, .5f);
            material.SetTextureOffset("_BaseMap", new Vector2(.25f, .5f)); camera.Render();
            FrameworkCheck(report, "planar-capture-cutout-uv-rejects-both", SsrHitCount(PlanarPixels(planar, camera)) == 0 && SsrHitCount(ReadSceneTarget(PlanarField<RenderTexture>(planar, "_capture"))) == 0);
            draw.surface.alphaMask = null; draw.surface.alphaCutoff = 0; draw.surface.alphaMaskST = new Vector4(1, 1, 0, 0);
            draw.material = original;
            receiver.surface.alphaMask = alpha; receiver.surface.alphaCutoff = .5f;
            receiver.surface.alphaMaskST = new Vector4(0, 0, .25f, .5f); camera.Render();
            FrameworkCheck(report, "planar-receiver-cutout-blocks-projection", SsrHitCount(PlanarPixels(planar, camera)) == 0);
            receiver.surface.alphaMaskST = new Vector4(0, 0, .75f, .5f); camera.Render();
            FrameworkCheck(report, "planar-receiver-cutout-uv-recovers", SsrHitCount(PlanarPixels(planar, camera)) > 30);
            receiver.surface.alphaMask = null; receiver.surface.alphaCutoff = 0;
            receiver.surface.alphaMaskST = new Vector4(1, 1, 0, 0);
            // Explicit single-light capture must work without Unity light globals.
            material.SetTexture("_BaseMap", Texture2D.whiteTexture); material.SetFloat("_Cutoff", 0);
            material.SetColor("_AmbientColor", Color.black); material.SetColor("_LightColor", Color.white);
            material.SetVector("_LightDirection", Vector3.back); draw.material = material; camera.Render();
            int lit = 0; foreach (Color p in PlanarPixels(planar, camera)) if (p.a > .99f && p.r > .9f) lit++;
            material.SetVector("_LightDirection", Vector3.forward); camera.Render();
            int dark = 0; foreach (Color p in PlanarPixels(planar, camera)) if (p.a > .99f && p.r < .001f) dark++;
            FrameworkCheck(report, "planar-reduced-forward-explicit-light-direction", lit > 20 && dark == lit);
            draw.material = original;
        }

        private void VerifyPlanarAnalyticPanels(Report report, Camera camera, Color[] pixels, int width, int height, string label)
        {
            int compared = 0, wrong = 0;
            var plane = new Plane(Vector3.up, Vector3.zero);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                Color actual = pixels[y * width + x];
                if (actual.a < .99f) continue;
                Ray primary = camera.ScreenPointToRay(new Vector3(x + .5f, y + .5f));
                if (!plane.Raycast(primary, out float distance)) continue;
                Vector3 p = primary.GetPoint(distance), direction = Vector3.Reflect(primary.direction, Vector3.up);
                if (direction.z <= .0001f) continue;
                Vector3 hit = p + direction * ((5 - p.z) / direction.z);
                if (hit.y < .35f || hit.y > 3.25f || Mathf.Abs(hit.x) < .35f || Mathf.Abs(hit.x) > 2.85f) continue;
                compared++;
                Color expected = (hit.x < 0 ? new Color(.8f, .1f, .05f, 1) : new Color(.05f, .8f, .1f, 1)).linear;
                if (Mathf.Max(Mathf.Abs(actual.r - expected.r), Mathf.Abs(actual.g - expected.g), Mathf.Abs(actual.b - expected.b)) > .002f) wrong++;
            }
            FrameworkCheck(report, "planar-analytic-two-panels-" + label, compared > 20 && wrong == 0, wrong);
        }

        private void VerifyPlanarSkinned(Report report, Camera camera, PlanarReflection planar, Mesh original, Material material)
        {
            var host = Own(new GameObject("Planar skinned panel")); host.layer = 20;
            host.transform.position = new Vector3(-1, 1.5f, 4); host.transform.localScale = new Vector3(2, 2, 1);
            var bone = Own(new GameObject("Planar test bone")); bone.transform.SetParent(host.transform, false);
            var mesh = Own(Instantiate(original)); var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
            mesh.boneWeights = weights; mesh.bindposes = new[] { Matrix4x4.identity };
            var skin = host.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.sharedMaterial = material;
            skin.bones = new[] { bone.transform }; skin.rootBone = bone.transform; skin.updateWhenOffscreen = true;
            skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 20);
            planar.reflectedSurfaces = new[] { new PlanarReflection.Draw { surface = new SceneDepthData.Surface { renderer = skin }, material = material } };
            camera.Render(); var before = PlanarPixels(planar, camera);
            bone.transform.localPosition = Vector3.right; camera.Render(); var moved = PlanarPixels(planar, camera);
            FrameworkCheck(report, "planar-actual-skinned-bone-motion", SsrHitCount(before) > 20 && SsrHitCount(moved) > 20 && !ScenePixelsEqual(before, moved));
            bone.transform.localPosition = Vector3.zero; camera.Render();
            FrameworkCheck(report, "planar-skinned-bone-restore", ScenePixelsEqual(before, PlanarPixels(planar, camera)));
            host.SetActive(false);
        }

        private void VerifyPlanarMatrix(Report report)
        {
            Vector3 point = new Vector3(3, -2, 4), normal = new Vector3(.2f, .7f, -.3f).normalized;
            Matrix4x4 matrix = PlanarReflection.ReflectionMatrix(point, normal * 3);
            Vector3 input = point + normal * 2 + Vector3.Cross(normal, Vector3.right);
            Vector3 reflected = matrix.MultiplyPoint(input);
            FrameworkCheck(report, "planar-matrix-independent-plane-distance", Mathf.Abs(Vector3.Dot(reflected - point, normal) + 2) < 1e-5f);
            FrameworkCheck(report, "planar-matrix-involution-and-negative-determinant", Vector3.Distance(matrix.MultiplyPoint(reflected), input) < 1e-5f && Mathf.Abs(matrix.determinant + 1) < 1e-5f);
            bool rejected = false;
            try { PlanarReflection.ReflectionMatrix(point, Vector3.zero); } catch (ArgumentException) { rejected = true; }
            FrameworkCheck(report, "planar-matrix-invalid-input", rejected);
        }
    }
}
