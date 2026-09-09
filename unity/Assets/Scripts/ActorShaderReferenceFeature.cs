using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Research-only bridge for user-supplied Windows actor shaders. Standard
    /// URP does not draw their custom VLActor tag. No original shader is shipped.
    /// </summary>
    public sealed class ActorShaderReferenceFeature : ScriptableRendererFeature
    {
        private ReferencePass _pass;
        public override void Create() { _pass = new ReferencePass(); }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (OriginalShaderUrpBootstrap.IsRequested) renderer.EnqueuePass(_pass);
        }

        private sealed class ReferencePass : ScriptableRenderPass
        {
            private bool _logged;
            private bool _rejected;
            private readonly ShaderTagId _actorTag = new ShaderTagId("VLActor");
            public ReferencePass() { renderPassEvent = RenderPassEvent.AfterRenderingOpaques; }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (_rejected) return;
                if (!_logged)
                {
                    // Unity may retain the original shader's name/isSupported
                    // while selecting Hidden/InternalErrorShader's subshader.
                    // SetPass returning true also does not prove a valid original pass.
                    if (!ValidateOriginalPasses())
                    {
                        _rejected = true;
                        if (!Application.isEditor) Application.Quit(3);
                        return;
                    }
                    Debug.Log("[OriginalShader] Executing VLActor reference pass for " + renderingData.cameraData.camera.name);
                    _logged = true;
                }
                CommandBuffer commands = CommandBufferPool.Get("User shader reference: VLActor");
                commands.SetGlobalVector("_MatCapMainLight", Shader.GetGlobalVector("_CapturedLightDirection"));
                commands.SetGlobalVector("_MatCapLightColor", Shader.GetGlobalVector("_CapturedLightColor"));
                commands.SetGlobalVector("_MatCapParam", Shader.GetGlobalVector("_ActorMatcapParameters"));
                commands.SetGlobalVector("_ShadeMultiplyColor", Shader.GetGlobalVector("_CapturedShadeTint"));
                commands.SetGlobalVector("_ShadeAdditiveColor", Shader.GetGlobalVector("_CapturedShadeAdditive"));
                Vector4 rim = Shader.GetGlobalVector("_CapturedRimViewDirection");
                rim.w = Shader.GetGlobalVector("_CapturedRimParameters").z;
                commands.SetGlobalVector("_MatCapRimLight", rim);
                Vector4 color = Shader.GetGlobalVector("_ActorRimColor");
                color.w = Shader.GetGlobalVector("_CapturedRimParameters").y;
                commands.SetGlobalVector("_MatCapRimColor", color);
                commands.SetGlobalVector("_GlobalLightParameter", Shader.GetGlobalVector("_ActorLightingScales"));
                commands.SetGlobalVector("_MultiplyColor", Vector4.one);
                commands.SetGlobalVector("_SkinSaturation", Vector4.one);
                commands.SetGlobalVector("_EyeHighlightColor", Vector4.one);
                commands.SetGlobalVector("_ReflectionColor", Vector4.one);
                commands.SetGlobalVector("_EyeReflectionColor", Vector4.one);
                commands.SetGlobalVector("_GlobalMipBias", Vector4.zero);
                commands.SetGlobalVector("_HeadForwardDirection", Shader.GetGlobalVector("_HeadDirection"));
                Vector4 right = Shader.GetGlobalVector("_HeadRightDirection");
                Vector4 up = Shader.GetGlobalVector("_HeadUpDirection");
                Vector4 forward = Shader.GetGlobalVector("_HeadDirection");
                Matrix4x4 head = Matrix4x4.identity;
                head.SetColumn(0, -right);
                head.SetColumn(1, up);
                head.SetColumn(2, forward);
                commands.SetGlobalMatrix("_HeadXAxisReflectionMatrix", head);
                commands.SetGlobalVector("_ActorShadowParam", new Vector4(0f, 0f, 0f, 0f));
                context.ExecuteCommandBuffer(commands);
                CommandBufferPool.Release(commands);
                DrawingSettings drawing = CreateDrawingSettings(_actorTag, ref renderingData,
                    SortingCriteria.CommonOpaque);
                FilteringSettings filtering = new FilteringSettings(RenderQueueRange.all,
                    1 << OriginalStyleRenderPipeline.ActorLayer);
                context.DrawRenderers(renderingData.cullResults, ref drawing, ref filtering);
            }

            private static bool ValidateOriginalPasses()
            {
                var checkedMaterials = new HashSet<Material>();
                bool accepted = true;
                foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (renderer.gameObject.layer != OriginalStyleRenderPipeline.ActorLayer) continue;
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null || material.shader == null ||
                            !material.shader.name.StartsWith("Campus/Actor/", StringComparison.Ordinal) ||
                            !checkedMaterials.Add(material)) continue;
                        int forward = material.FindPass("Forward");
                        if (forward >= 0) continue;
                        accepted = false;
                        var passes = new List<string>();
                        for (int index = 0; index < material.passCount; index++) passes.Add(material.GetPassName(index));
                        Debug.LogError("[OriginalShader] Rejected fallback subshader: material=" + material.name +
                            " shader=" + material.shader.name + " isSupported=" + material.shader.isSupported +
                            " activePasses=[" + string.Join(", ", passes) + "]. Expected named Forward pass. " +
                            "No valid original-shader comparison was produced.");
                    }
                }
                if (checkedMaterials.Count == 0)
                {
                    Debug.LogError("[OriginalShader] No original actor materials found; comparison rejected.");
                    accepted = false;
                }
                return accepted;
            }
        }
    }
}
