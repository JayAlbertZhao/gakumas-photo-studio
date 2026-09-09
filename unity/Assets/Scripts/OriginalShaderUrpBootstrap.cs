using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Activates a small URP asset only for original-shader research captures. The normal photo
    /// studio remains on its stable built-in/fallback renderer.
    /// </summary>
    internal static class OriginalShaderUrpBootstrap
    {
        private const string ResourceName = "ResearchOriginalShaderPipeline";

        public static bool IsRequested
        {
            get
            {
                string[] args = Environment.GetCommandLineArgs();
                return args.Contains("--original-shader") && args.Contains("--original-urp");
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void EnableForResearchCapture()
        {
            if (!IsRequested)
            {
                return;
            }

            UniversalRenderPipelineAsset pipeline = Resources.Load<UniversalRenderPipelineAsset>(ResourceName);
            if (pipeline == null)
            {
                Debug.LogError("[OriginalShader] Research URP asset is missing from Resources");
                return;
            }

            QualitySettings.renderPipeline = pipeline;
            GraphicsSettings.renderPipelineAsset = pipeline;
            Debug.Log("[OriginalShader] Activated UniversalPipeline for GPU shader research");
        }
    }
}
