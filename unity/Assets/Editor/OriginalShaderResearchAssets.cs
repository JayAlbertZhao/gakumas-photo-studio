using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GakumasPhotoMode.Editor
{
    internal static class OriginalShaderResearchAssets
    {
        private const string PipelinePath = "Assets/Resources/ResearchOriginalShaderPipeline.asset";

        public static void EnsurePipelineAsset()
        {
            UniversalRenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            UniversalRendererData rendererData = AssetDatabase.LoadAllAssetsAtPath(PipelinePath)
                .OfType<UniversalRendererData>()
                .FirstOrDefault();

            if (pipeline != null && rendererData != null)
            {
                ReloadResources(pipeline, rendererData);
                return;
            }

            if (pipeline != null)
            {
                AssetDatabase.DeleteAsset(PipelinePath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PipelinePath));
            rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            rendererData.name = "ResearchOriginalShaderRenderer";
            ResourceReloader.ReloadAllNullIn(rendererData, UniversalRenderPipelineAsset.packagePath);

            pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            pipeline.name = "ResearchOriginalShaderPipeline";
            pipeline.renderScale = 1f;
            pipeline.msaaSampleCount = 1;
            pipeline.supportsCameraDepthTexture = true;
            pipeline.supportsCameraOpaqueTexture = true;

            AssetDatabase.CreateAsset(pipeline, PipelinePath);
            AssetDatabase.AddObjectToAsset(rendererData, pipeline);
            ReloadResources(pipeline, rendererData);
            Debug.Log("[OriginalShader] Created research URP asset at " + PipelinePath);
        }

        private static void ReloadResources(UniversalRenderPipelineAsset pipeline, UniversalRendererData rendererData)
        {
            ResourceReloader.ReloadAllNullIn(rendererData, UniversalRenderPipelineAsset.packagePath);
            ResourceReloader.ReloadAllNullIn(pipeline, UniversalRenderPipelineAsset.packagePath);
            EditorUtility.SetDirty(rendererData);
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(PipelinePath, ImportAssetOptions.ForceUpdate);
            Debug.Log("[OriginalShader] Reloaded research URP renderer resources");
        }
    }
}
