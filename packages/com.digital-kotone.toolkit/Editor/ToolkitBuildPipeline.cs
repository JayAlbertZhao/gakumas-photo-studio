using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GakumasPhotoMode.Editor
{
    /// <summary>Original scoped Built-in/URP build compatibility helper, shared by hosts.</summary>
    public static class ToolkitBuildPipeline
    {
        public static BuildReport BuildPlayer(BuildPlayerOptions options, bool originalShaderReference = false)
        {
            if (originalShaderReference) return BuildPipeline.BuildPlayer(options);
            var settings = GraphicsSettings.GetSettingsForRenderPipeline<UniversalRenderPipeline>();
            if (settings == null) return BuildPipeline.BuildPlayer(options);
            var serialized = new SerializedObject(settings);
            var stripUnused = serialized.FindProperty("m_StripUnusedVariants");
            if (stripUnused == null)
                throw new InvalidOperationException("URP global settings lack the unused-variant option.");
            bool previousStripUnused = stripUnused.boolValue;
            try
            {
                // Tuanjie 2022.3.62t15's URP preprocessor rejects an empty URP
                // asset list even for Built-in builds. Retain variants instead
                // of activating URP or changing the ordinary render pipeline.
                stripUnused.boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                return BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                serialized.Update();
                stripUnused.boolValue = previousStripUnused;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssetIfDirty(settings);
            }
        }
    }
}
