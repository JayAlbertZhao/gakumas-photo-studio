using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace GakumasPhotoMode.Editor
{
    public static class PhotoStudioBuilder
    {
        private const string ScenePath = "Assets/Scenes/PhotoMode.unity";

        public static void RenderFaceShapeAtlas()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outputRoot = Path.Combine(projectRoot, "output", "face-shapes");
            Directory.CreateDirectory(outputRoot);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject root = new GameObject("FaceShapeAtlasApp");
            PhotoModeApp app = root.AddComponent<PhotoModeApp>();
            app.Initialize(BundleCatalog.DefaultStagingRoot);
            app.SelectMotion(0);
            app.EvaluateMotion(0f);
            app.PreviewCamera.transform.position = new Vector3(0f, 1.43f, 1.12f);
            app.PreviewCamera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            app.PreviewCamera.fieldOfView = 25f;
            List<int> indices = new List<int>();
            indices.AddRange(Enumerable.Range(0, 14));
            indices.AddRange(Enumerable.Range(20, 14));
            indices.AddRange(Enumerable.Range(41, 33));
            foreach (int index in indices)
            {
                app.SetFaceDebugShape(index, 1f);
                RenderCamera(app.PreviewCamera, Path.Combine(outputRoot, string.Format("shape-{0:000}.png", index)), 320, 320);
            }
            app.ClearFaceDebugShape();
            app.Shutdown();
            UnityEngine.Object.DestroyImmediate(root);
            Debug.Log(string.Format("[PhotoMode] Face shape atlas frames: {0} -> {1}", indices.Count, outputRoot));
        }

        public static void BuildAndValidate()
        {
            OriginalShaderResearchAssets.EnsurePipelineAsset();
            string stagingRoot = BundleCatalog.DefaultStagingRoot;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outputRoot = Path.Combine(projectRoot, "output");
            Directory.CreateDirectory(outputRoot);

            PlayerSettings.companyName = "Digital Kotone";
            PlayerSettings.productName = "Kotone Photo Studio";
            PlayerSettings.defaultScreenWidth = 1440;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.displayResolutionDialog = ResolutionDialogSetting.Disabled;
            // The captured Campus/Actor pass and its HDR post chain operate in linear space.
            // Running the reconstruction in Gamma feeds sRGB values into ACES as if they were
            // linear, producing the characteristic pale/flat image seen in earlier captures.
            PlayerSettings.colorSpace = ColorSpace.Linear;
            QualitySettings.antiAliasing = 8;
            GraphicsSettings.renderPipelineAsset = null;
            QualitySettings.renderPipeline = null;

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject root = new GameObject("PhotoModeApp");
            root.AddComponent<PhotoModeApp>();
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Scenes"));
            EditorSceneManager.SaveScene(scene, ScenePath);

            GameObject validationRoot = new GameObject("ValidationApp");
            PhotoModeApp app = validationRoot.AddComponent<PhotoModeApp>();
            app.Initialize(stagingRoot);
            app.SelectMotion(1);
            app.EvaluateMotion(1.0f);
            string previewPath = Path.Combine(outputRoot, "kotone-preview-cstm.png");
            RenderCamera(app.PreviewCamera, previewPath, 1600, 900);
            app.SelectCostume(1);
            app.SelectMotion(2);
            app.EvaluateMotion(1.5f);
            string alternatePreviewPath = Path.Combine(outputRoot, "kotone-preview-trng.png");
            RenderCamera(app.PreviewCamera, alternatePreviewPath, 1600, 900);
            app.SelectCostume(0);
            app.SelectMotion(1);

            Vector3 cameraPosition = app.PreviewCamera.transform.position;
            Quaternion cameraRotation = app.PreviewCamera.transform.rotation;
            float cameraFov = app.PreviewCamera.fieldOfView;
            List<string> expressionPreviews = new List<string>();
            app.PreviewCamera.transform.position = new Vector3(0f, 1.43f, 1.12f);
            app.PreviewCamera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            app.PreviewCamera.fieldOfView = 25f;
            for (int expressionIndex = 0; expressionIndex < app.ExpressionPresetCount; expressionIndex++)
            {
                app.SelectExpression(expressionIndex);
                string expressionPath = Path.Combine(outputRoot, string.Format("expression-{0:00}.png", expressionIndex));
                RenderCamera(app.PreviewCamera, expressionPath, 480, 480);
                expressionPreviews.Add(expressionPath);
            }
            app.SelectExpression(0);
            app.PreviewCamera.transform.position = cameraPosition;
            app.PreviewCamera.transform.rotation = cameraRotation;
            app.PreviewCamera.fieldOfView = cameraFov;

            ValidationRecord record = new ValidationRecord
            {
                timestamp_utc = DateTime.UtcNow.ToString("o"),
                staging_root = stagingRoot,
                current_costume = app.CurrentCostume,
                current_motion = app.CurrentMotion,
                renderer_count = app.RendererCount,
                repaired_material_count = app.ErrorMaterialCount,
                costume_count = app.CostumeCount,
                motion_count = app.MotionCount,
                voice_count = app.VoiceCount,
                hair_dynamic_bone_count = app.HairDynamicBoneCount,
                face_shape_count = app.FaceShapeCount,
                renderer_diagnostics = app.DescribeRenderers(),
                body_motion_bindings = DescribeBindings(app.CurrentBodyClip),
                face_motion_bindings = DescribeBindings(app.CurrentFaceClip),
                preview = previewPath,
                alternate_preview = alternatePreviewPath,
                expression_previews = expressionPreviews.ToArray(),
            };
            File.WriteAllText(Path.Combine(outputRoot, "kotone-validation.json"), JsonUtility.ToJson(record, true) + "\n");
            app.Shutdown();
            UnityEngine.Object.DestroyImmediate(validationRoot);

            string buildPath = Path.Combine(outputRoot, "KotonePhotoStudio.exe");
            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = buildPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            // Keep D3D11 as the normal default, but include Vulkan so Android-origin shader
            // bundles can be tested against their native SPIR-V variants under RenderDoc.
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[]
            {
                GraphicsDeviceType.Direct3D11,
                GraphicsDeviceType.Vulkan,
            });
            BuildReport report = BuildWithPipelineSettings(options, false);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new Exception("Build failed: " + report.summary.result);
            }
            Debug.Log(string.Format("[PhotoMode] Build succeeded: {0} bytes -> {1}", report.summary.totalSize, buildPath));
        }

        // Batch validation renders exercise a Tuanjie headless MRT path that can
        // intermittently crash inside GfxDevice::DrawBuffers after the useful
        // runtime validation has already been covered by the capture harness.
        // Keep a deterministic build-only entry point for rapid shader/diagnostic
        // iterations; release passes still use BuildAndValidate.
        public static void BuildPlayerOnly()
        {
            BuildPlayer(false);
        }

        public static void BuildOriginalShaderReferencePlayer()
        {
            BuildPlayer(true);
        }

        private static void BuildPlayer(bool originalShaderReference)
        {
            OriginalShaderResearchAssets.EnsurePipelineAsset();
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outputRoot = Path.Combine(projectRoot, "output");
            if (originalShaderReference) outputRoot = Path.Combine(outputRoot, "OriginalShaderReference");
            Directory.CreateDirectory(outputRoot);

            PlayerSettings.companyName = "Digital Kotone";
            PlayerSettings.productName = "Kotone Photo Studio";
            PlayerSettings.defaultScreenWidth = 1440;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.displayResolutionDialog = ResolutionDialogSetting.Disabled;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            int previousAntiAliasing = QualitySettings.antiAliasing;
            QualitySettings.antiAliasing = 8;
            // URP strips its ScriptableRenderPipeline variants when no URP asset
            // is active at build time. Merely including one in Resources does
            // not preserve even its blit shaders. Build the reference Player
            // separately, without replacing the normal built-in Player.
            RenderPipelineAsset previousGraphicsPipeline = GraphicsSettings.renderPipelineAsset;
            RenderPipelineAsset previousQualityPipeline = QualitySettings.renderPipeline;
            RenderPipelineAsset pipeline = originalShaderReference
                ? Resources.Load<RenderPipelineAsset>("ResearchOriginalShaderPipeline") : null;
            GraphicsSettings.renderPipelineAsset = pipeline;
            QualitySettings.renderPipeline = pipeline;
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[]
            {
                GraphicsDeviceType.Direct3D11,
                GraphicsDeviceType.Vulkan,
            });

            string buildPath = Path.Combine(outputRoot, "KotonePhotoStudio.exe");
            BuildReport report;
            try
            {
                report = BuildWithPipelineSettings(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = buildPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,
                }, originalShaderReference);
            }
            finally
            {
                if (originalShaderReference)
                {
                    GraphicsSettings.renderPipelineAsset = previousGraphicsPipeline;
                    QualitySettings.renderPipeline = previousQualityPipeline;
                    // Activating URP can overwrite this shared quality setting.
                    QualitySettings.antiAliasing = previousAntiAliasing;
                }
            }
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception("Build failed: " + report.summary.result);
            Debug.Log(string.Format("[PhotoMode] Build succeeded: {0} bytes -> {1}", report.summary.totalSize, buildPath));
        }

        private static BuildReport BuildWithPipelineSettings(BuildPlayerOptions options, bool originalShaderReference)
        {
            return GakumasPhotoMode.Editor.ToolkitBuildPipeline.BuildPlayer(options, originalShaderReference);
        }

        private static void RenderCamera(Camera camera, string output, int width, int height)
        {
            RenderTexture texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture.active = texture;
            camera.targetTexture = texture;
            camera.Render();
            Texture2D image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();
            File.WriteAllBytes(output, image.EncodeToPNG());
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            UnityEngine.Object.DestroyImmediate(image);
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static string[] DescribeBindings(AnimationClip clip)
        {
            if (clip == null) return new string[0];
            return AnimationUtility.GetCurveBindings(clip)
                .Take(80)
                .Select(binding => binding.path + " | " + binding.type.Name + "." + binding.propertyName)
                .ToArray();
        }

        [Serializable]
        private sealed class ValidationRecord
        {
            public string timestamp_utc;
            public string staging_root;
            public string current_costume;
            public string current_motion;
            public int renderer_count;
            public int repaired_material_count;
            public int costume_count;
            public int motion_count;
            public int voice_count;
            public int hair_dynamic_bone_count;
            public int face_shape_count;
            public string[] renderer_diagnostics;
            public string[] body_motion_bindings;
            public string[] face_motion_bindings;
            public string preview;
            public string alternate_preview;
            public string[] expression_previews;
        }
    }
}
