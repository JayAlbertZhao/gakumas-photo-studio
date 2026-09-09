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
            private readonly ShaderTagId _actorTag = new ShaderTagId("VLActor");
            public ReferencePass() { renderPassEvent = RenderPassEvent.AfterRenderingOpaques; }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!_logged)
                {
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
        }
    }
}
