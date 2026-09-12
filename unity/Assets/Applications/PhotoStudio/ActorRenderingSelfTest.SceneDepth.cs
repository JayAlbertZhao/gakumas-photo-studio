using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private Color[] ReadSceneTarget(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                texture.Apply();
                return texture.GetPixels();
            }
            finally { RenderTexture.active = previous; Destroy(texture); }
        }

        private static bool ScenePixelsEqual(Color[] a, Color[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
            return true;
        }

        private void VerifySceneDepthData(Report report)
        {
            const int width = 33, height = 25;
            var host = Own(new GameObject("Scene depth data contract camera"));
            var camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.renderingPath = RenderingPath.Forward;
            camera.allowMSAA = false;
            camera.orthographic = true;
            camera.orthographicSize = 1.5f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            camera.cullingMask = (1 << 26) | (1 << 25);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.13f, .27f, .39f, 1);
            var target = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            target.Create();
            camera.targetTexture = target;
            var flat = Own(new Material(Resources.Load<Shader>("StudioAccent")));
            flat.SetColor("_Color", new Color(.8f, .2f, .1f, 1));
            var background = Own(GameObject.CreatePrimitive(PrimitiveType.Quad));
            background.layer = 26;
            background.transform.position = new Vector3(0, 0, 3);
            background.transform.localScale = new Vector3(3, 2.4f, 1);
            var backRenderer = background.GetComponent<Renderer>();
            backRenderer.sharedMaterial = flat;
            var foreground = Own(GameObject.CreatePrimitive(PrimitiveType.Quad));
            foreground.layer = 25;
            foreground.transform.position = new Vector3(0, 0, 2);
            foreground.transform.localScale = Vector3.one * .8f;
            var frontRenderer = foreground.GetComponent<Renderer>();
            frontRenderer.sharedMaterial = flat;
            camera.Render();
            Color[] ordinary = ReadSceneTarget(target);
            DepthTextureMode originalDepthMode = camera.depthTextureMode;
            var data = host.AddComponent<SceneDepthData>();
            SceneDepthData.Frame frame;
            FrameworkCheck(report, "scene-depth-no-data-before-render", !data.TryGetFrame(camera, width, height, out frame));
            camera.Render();
            FrameworkCheck(report, "scene-depth-empty-noop", !data.TryGetFrame(camera, width, height, out frame) &&
                ScenePixelsEqual(ordinary, ReadSceneTarget(target)));
            var backSurface = new SceneDepthData.Surface { renderer = backRenderer };
            var frontSurface = new SceneDepthData.Surface { renderer = frontRenderer, receiveReflections = false };
            data.surfaces = new[] { backSurface, frontSurface };
            bool availableInsidePost = false;
            var postProbe = host.AddComponent<SphereFogDepthProbe>();
            postProbe.sample = source => { SceneDepthData.Frame inside; availableInsidePost = data.TryGetFrame(camera, width, height, out inside); };
            camera.Render();
            bool ready = data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-ready-after-render", ready && data.SubmittedSurfaces == 2);
            FrameworkCheck(report, "scene-depth-ready-in-image-effect", availableInsidePost);
            if (!ready) throw new InvalidOperationException("Scene depth unavailable: " + data.UnavailableReason);
            FrameworkCheck(report, "scene-depth-main-color-and-depth-mode-unchanged", ScenePixelsEqual(ordinary, ReadSceneTarget(target)) && camera.depthTextureMode == originalDepthMode);
            FrameworkCheck(report, "scene-depth-source-material-unchanged", backRenderer.sharedMaterial == flat && frontRenderer.sharedMaterial == flat);
            Color[] normals = ReadSceneTarget(frame.normalMask), depths = ReadSceneTarget(frame.linearDepth);
            int center = 12 * width + 16, side = 12 * width + 24;
            FrameworkCheck(report, "scene-depth-linear-occlusion", Mathf.Abs(depths[center].r - 2) < 1e-5f && Mathf.Abs(depths[side].r - 3) < 1e-5f);
            FrameworkCheck(report, "scene-depth-world-normal-unorm", Mathf.Abs(normals[side].r - .5f) < .003f &&
                Mathf.Abs(normals[side].g - .5f) < .003f && normals[side].b == 0);
            FrameworkCheck(report, "scene-depth-nonreflector-still-occludes", normals[center].a == 0 && normals[side].a == 1);
            FrameworkCheck(report, "scene-depth-clear-sentinel", depths[0].r == 20 && normals[0].Equals(Color.clear));
            FrameworkCheck(report, "scene-depth-format-contract", frame.normalMask.format == RenderTextureFormat.ARGB32 &&
                !frame.normalMask.sRGB && frame.linearDepth.format == RenderTextureFormat.RFloat &&
                frame.normalMask.filterMode == FilterMode.Point && frame.DepthLevelCount == 7);
            VerifySceneDepthHierarchy(report, frame, "scene");
            var preview = Own(new Texture2D(width, height, TextureFormat.RGBA32, false, true));
            var displayNormals = (Color[])normals.Clone();
            for (int i = 0; i < displayNormals.Length; i++) displayNormals[i].a = 1;
            preview.SetPixels(displayNormals); preview.Apply();
            File.WriteAllBytes(Path.Combine(_directory, "scene-depth-normals-preview.png"), preview.EncodeToPNG());
            data.excludedLayers = 1 << 25;
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-excluded-actor-reveals-background", data.SubmittedSurfaces == 1 &&
                Mathf.Abs(ReadSceneTarget(frame.linearDepth)[center].r - 3) < 1e-5f && ReadSceneTarget(frame.normalMask)[center].a == 1);
            FrameworkCheck(report, "scene-depth-separate-from-main-occluder", ScenePixelsEqual(ordinary, ReadSceneTarget(target)));
            data.excludedLayers = 0;
            data.surfaces = new[] { frontSurface, backSurface };
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-registration-order-independent", ScenePixelsEqual(depths, ReadSceneTarget(frame.linearDepth)) && ScenePixelsEqual(normals, ReadSceneTarget(frame.normalMask)));
            foreground.SetActive(false);
            data.surfaces = new[] { backSurface };
            foreach (bool orthographic in new[] { false, true })
            {
                camera.orthographic = orthographic;
                camera.Render(); data.TryGetFrame(camera, width, height, out frame);
                FrameworkCheck(report, "scene-depth-projection-" + orthographic, Mathf.Abs(ReadSceneTarget(frame.linearDepth)[center].r - 3) < 1e-5f);
            }
            foreach (float value in new[] { .49f, .5f, .51f })
            {
                backSurface.smoothness = value;
                camera.Render(); data.TryGetFrame(camera, width, height, out frame);
                FrameworkCheck(report, "scene-depth-smoothness-threshold-" + value, ReadSceneTarget(frame.normalMask)[center].a == (value >= .5f ? 1 : 0));
            }
            var map = Own(new Texture2D(2, 1, TextureFormat.RGBA32, false, true));
            map.filterMode = FilterMode.Point; map.wrapMode = TextureWrapMode.Clamp;
            map.SetPixels(new[] { new Color(0, 1, 1, 0), new Color(1, 0, 0, 1) }); map.Apply();
            backSurface.smoothness = 1; backSurface.smoothnessMap = map;
            backSurface.smoothnessMapST = new Vector4(0, 0, .25f, .5f);
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-smoothness-red-channel", ReadSceneTarget(frame.normalMask)[center].a == 0);
            backSurface.smoothnessMapST.z = .75f;
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-smoothness-uv-transform", ReadSceneTarget(frame.normalMask)[center].a == 1);
            backSurface.smoothnessMap = null;
            backSurface.alphaMask = map; backSurface.alphaCutoff = .5f;
            backSurface.alphaMaskST = new Vector4(0, 0, .25f, .5f);
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-cutout-clears-both-attachments", ReadSceneTarget(frame.linearDepth)[center].r == 20 && ReadSceneTarget(frame.normalMask)[center].Equals(Color.clear));
            backSurface.alphaMaskST.z = .75f;
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-cutout-uv-transform", Mathf.Abs(ReadSceneTarget(frame.linearDepth)[center].r - 3) < 1e-5f);
            backSurface.alphaMask = null; backSurface.alphaCutoff = 0;
            background.transform.rotation = Quaternion.Euler(0, 25, 0);
            background.transform.localScale = new Vector3(3, 2.4f, .4f);
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            Color normal = ReadSceneTarget(frame.normalMask)[center];
            Vector3 decoded = new Vector3(normal.r, normal.g, normal.b) * 2 - Vector3.one;
            Vector3 expectedNormal = background.transform.localToWorldMatrix.inverse.transpose.MultiplyVector(Vector3.back).normalized;
            FrameworkCheck(report, "scene-depth-rotated-nonuniform-world-normal", Vector3.Distance(decoded, expectedNormal) < .007f);
            background.transform.rotation = Quaternion.identity;
            background.transform.localScale = new Vector3(3, 2.4f, 1);
            backSurface.vertexScale = new Vector3(.1f, .1f, 1);
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-explicit-vertex-scale", ReadSceneTarget(frame.linearDepth)[side].r == 20 && Mathf.Abs(ReadSceneTarget(frame.linearDepth)[center].r - 3) < 1e-5f);
            backSurface.vertexScale = Vector3.one;
            backSurface.cull = CullMode.Front;
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-front-cull", ReadSceneTarget(frame.linearDepth)[center].r == 20);
            backSurface.cull = CullMode.Back;
            backSurface.smoothness = float.NaN;
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-invalid-surface-no-stale-pixels", data.SubmittedSurfaces == 0 && ReadSceneTarget(frame.linearDepth)[center].r == 20);
            backSurface.smoothness = 1;
            backSurface.materialIndex = 100;
            camera.Render();
            FrameworkCheck(report, "scene-depth-invalid-submesh-skipped", data.SubmittedSurfaces == 0);
            backSurface.materialIndex = 0;
            backRenderer.sharedMaterials = new[] { flat, flat }; backSurface.materialIndex = 1;
            camera.Render();
            FrameworkCheck(report, "scene-depth-material-without-submesh-skipped", data.SubmittedSurfaces == 0);
            backRenderer.sharedMaterials = new[] { flat }; backSurface.materialIndex = 0;
            backRenderer.forceRenderingOff = true; camera.Render();
            FrameworkCheck(report, "scene-depth-force-off-skipped", data.SubmittedSurfaces == 0);
            backRenderer.forceRenderingOff = false;
            camera.cullingMask = 1 << 25; camera.Render();
            FrameworkCheck(report, "scene-depth-camera-layer-respected", data.SubmittedSurfaces == 0);
            camera.cullingMask = (1 << 25) | (1 << 26);
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-other-camera-rejected", !data.TryGetFrame(_camera, width, height, out _));
            FrameworkCheck(report, "scene-depth-other-size-rejected", !data.TryGetFrame(camera, width + 1, height, out _));
            var secondHost = Own(new GameObject("Second independent scene depth camera"));
            var secondCamera = secondHost.AddComponent<Camera>();
            secondCamera.CopyFrom(camera); secondCamera.enabled = false;
            var secondData = secondHost.AddComponent<SceneDepthData>();
            secondData.surfaces = new[] { backSurface };
            secondCamera.Render(); secondData.TryGetFrame(secondCamera, width, height, out var secondFrame);
            FrameworkCheck(report, "scene-depth-two-camera-isolation", secondFrame.normalMask != frame.normalMask && secondFrame.linearDepth != frame.linearDepth &&
                data.TryGetFrame(camera, width, height, out _) && !secondData.TryGetFrame(camera, width, height, out _));
            secondData.enabled = false; secondCamera.targetTexture = null;
            data.buildDepthHierarchy = false;
            camera.Render(); data.TryGetFrame(camera, width, height, out frame);
            FrameworkCheck(report, "scene-depth-hierarchy-opt-out", frame.DepthLevelCount == 1);
            data.buildDepthHierarchy = true;
            var resized = Own(new RenderTexture(17, 9, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            resized.Create(); camera.targetTexture = resized;
            FrameworkCheck(report, "scene-depth-target-change-invalidates-frame", !data.TryGetFrame(camera, width, height, out _));
            camera.Render(); data.TryGetFrame(camera, 17, 9, out frame);
            FrameworkCheck(report, "scene-depth-resize", frame.normalMask.width == 17 && frame.linearDepth.height == 9 && frame.DepthLevelCount == 6);
            VerifySceneDepthHierarchy(report, frame, "resized");
            frame.linearDepth.Release();
            camera.Render();
            FrameworkCheck(report, "scene-depth-recreates-lost-depth-target", data.TryGetFrame(camera, 17, 9, out frame) &&
                frame.linearDepth.IsCreated() && Mathf.Abs(ReadSceneTarget(frame.linearDepth)[4 * 17 + 8].r - 3) < 1e-5f);
            var msaa = Own(new RenderTexture(17, 9, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            msaa.antiAliasing = 2; msaa.Create();
            camera.targetTexture = msaa; camera.Render();
            FrameworkCheck(report, "scene-depth-rejects-msaa-target", msaa.antiAliasing > 1 && !data.TryGetFrame(camera, 17, 9, out _));
            camera.targetTexture = resized; msaa.Release();
            camera.rect = new Rect(0, 0, .5f, 1); camera.Render();
            FrameworkCheck(report, "scene-depth-rejects-viewport-atlas", !data.TryGetFrame(camera, 17, 9, out _) && data.UnavailableReason != null);
            camera.rect = new Rect(0, 0, 1, 1);
            data.smoothnessThreshold = float.NaN; camera.Render();
            FrameworkCheck(report, "scene-depth-rejects-nan-threshold", !data.TryGetFrame(camera, 17, 9, out _));
            data.smoothnessThreshold = .5f;
            data.surfaces = Array.Empty<SceneDepthData.Surface>(); camera.Render();
            FrameworkCheck(report, "scene-depth-empty-releases-targets", !data.TryGetFrame(camera, 17, 9, out _) &&
                typeof(SceneDepthData).GetField("_normalMask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(data) == null);
            data.enabled = false;
            FrameworkCheck(report, "scene-depth-disable-detaches-buffer", camera.GetCommandBuffers(CameraEvent.BeforeForwardOpaque).Length == 0);
            data.surfaces = new[] { backSurface }; data.enabled = true;
            camera.Render();
            FrameworkCheck(report, "scene-depth-reenable", data.TryGetFrame(camera, 17, 9, out _) && camera.GetCommandBuffers(CameraEvent.BeforeForwardOpaque).Length == 1);
            data.enabled = false;
            postProbe.sample = null;
            VerifySceneDepthSkinned(report, camera, data, background.GetComponent<MeshFilter>().sharedMesh, flat);
            camera.targetTexture = null; resized.Release(); target.Release();
            VerifySceneDepthOddEdge(report);
        }

        private void VerifySceneDepthHierarchy(Report report, SceneDepthData.Frame frame, string label)
        {
            Color[] previous = ReadSceneTarget(frame.linearDepth);
            int previousWidth = frame.linearDepth.width, previousHeight = frame.linearDepth.height;
            for (int level = 1; level < frame.DepthLevelCount; level++)
            {
                RenderTexture target = frame.GetDepthLevel(level);
                Color[] actual = ReadSceneTarget(target);
                float error = 0;
                for (int y = 0; y < target.height; y++)
                for (int x = 0; x < target.width; x++)
                {
                    float expected = float.PositiveInfinity;
                    for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        expected = Mathf.Min(expected, previous[Mathf.Min(y * 2 + dy, previousHeight - 1) * previousWidth + Mathf.Min(x * 2 + dx, previousWidth - 1)].r);
                    error = Mathf.Max(error, Mathf.Abs(expected - actual[y * target.width + x].r));
                }
                FrameworkCheck(report, "scene-depth-hiz-" + label + "-level-" + level, error < 1e-5f, error);
                previous = actual; previousWidth = target.width; previousHeight = target.height;
            }
        }

        private void VerifySceneDepthOddEdge(Report report)
        {
            var source = Own(new Texture2D(5, 3, TextureFormat.RGBAFloat, false, true));
            source.filterMode = FilterMode.Point; source.wrapMode = TextureWrapMode.Clamp;
            var pixels = new Color[15];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(10 + i, 0, 0, 0);
            pixels[14].r = .125f;
            source.SetPixels(pixels); source.Apply();
            var material = Own(new Material(Resources.Load<Shader>("SceneDepthData")));
            Texture input = source;
            foreach (Vector2Int size in new[] { new Vector2Int(3, 2), new Vector2Int(2, 1), new Vector2Int(1, 1) })
            {
                var target = Own(new RenderTexture(size.x, size.y, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear));
                target.filterMode = FilterMode.Point; target.wrapMode = TextureWrapMode.Clamp; target.Create();
                Graphics.Blit(input, target, material, 1);
                input = target;
                float last = ReadSceneTarget(target)[size.x * size.y - 1].r;
                FrameworkCheck(report, "scene-depth-hiz-preserves-odd-edge-" + size.x + "x" + size.y, last == .125f, Mathf.Abs(last - .125f));
            }
        }

        private void VerifySceneDepthSkinned(Report report, Camera camera, SceneDepthData data, Mesh original, Material material)
        {
            var host = Own(new GameObject("Generated scene depth skinned quad"));
            host.layer = 26; host.transform.position = new Vector3(0, 0, 4);
            var bone = Own(new GameObject("Scene depth test bone"));
            bone.transform.SetParent(host.transform, false);
            var mesh = Own(Instantiate(original));
            var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { Matrix4x4.identity };
            var skin = host.AddComponent<SkinnedMeshRenderer>();
            skin.sharedMesh = mesh; skin.sharedMaterial = material;
            skin.bones = new[] { bone.transform }; skin.rootBone = bone.transform;
            skin.updateWhenOffscreen = true;
            skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 20);
            data.surfaces = new[] { new SceneDepthData.Surface { renderer = skin } };
            data.enabled = true;
            camera.Render();
            bool ready = data.TryGetFrame(camera, 17, 9, out var first);
            float depth = ready ? ReadSceneTarget(first.linearDepth)[4 * 17 + 8].r : -1;
            FrameworkCheck(report, "scene-depth-skinned-mesh", ready && Mathf.Abs(depth - 4) < 1e-5f, Mathf.Abs(depth - 4));
            // A second generated bone pose must be reflected in the GPU prepass.
            bone.transform.localPosition = Vector3.forward;
            camera.Render();
            ready = data.TryGetFrame(camera, 17, 9, out var moved);
            depth = ready ? ReadSceneTarget(moved.linearDepth)[4 * 17 + 8].r : -1;
            FrameworkCheck(report, "scene-depth-skinned-bone-motion", ready && Mathf.Abs(depth - 5) < 1e-5f, Mathf.Abs(depth - 5));
            data.enabled = false;
        }
    }
}
