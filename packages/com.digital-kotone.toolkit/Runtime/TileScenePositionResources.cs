using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    // Current-geometry depth bridge and existing light/shadow resources, never a depth alias.
    internal sealed class TileScenePositionResources : IDisposable
    {
        private readonly TileRenderPass renderer = new TileRenderPass();
        private readonly SceneForwardLightResources lights = new SceneForwardLightResources();
        private TileRenderPass.Plan plan;
        private Material[] materials;
        private CommandBuffer commands;
        public RenderTexture EyeDepth { get; private set; }
        public RenderTexture GeometryDepthId { get; private set; }
        public Material Lighting { get; private set; }
        public TileRenderPass.Submission Budget { get; private set; }
        public int LocalLightCount => lights.SubmittedLights;
        public int ShadowMapCount => lights.LocalShadowMapCount + lights.MainShadowMapCount;
        public SceneForwardLightBackend Backend => Lighting!=null?lights.Backend:SceneForwardLightBackend.BruteForce;
        public long LightBufferBytes => lights.BufferBytes;

        public bool Prepare(Camera camera, TileSceneRenderer.Settings settings, out string error)
        {
            error = null;
            var shader = Resources.Load<Shader>("TileScenePosition");
            if (shader == null || !shader.isSupported) { error = "Tile scene position shader unavailable"; return false; }
            var descriptor = new RenderTextureDescriptor(settings.output.width, settings.output.height, GraphicsFormat.R32_SFloat, 0);
            EyeDepth = new RenderTexture(descriptor) { name = "Toolkit tile current eye depth", filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (!EyeDepth.Create()) { error = "Tile scene eye depth allocation failed"; return false; }
            bool depthId=settings.geometryDepthId||TileSceneDecals.Count(settings.decals)>0;
            if(depthId)
            {
                GeometryDepthId=new RenderTexture(new RenderTextureDescriptor(EyeDepth.width,EyeDepth.height,GraphicsFormat.R8G8B8A8_UNorm,0))
                    { name="Toolkit tile geometry DepthID",filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave };
                if(!GeometryDepthId.Create()) { error="Tile geometry DepthID allocation failed";return false; }
            }
            var depthShader=depthId?Resources.Load<Shader>("TileGeometryDepthId"):shader;
            if(depthShader==null||!depthShader.isSupported) { error="Tile geometry DepthID shader unavailable";return false; }
            Matrix4x4 view = camera.worldToCameraMatrix, vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)*view;
            materials = new Material[settings.surfaces.Length];
            var draws = new TileRenderPass.Draw[materials.Length];
            for (int i=0;i<materials.Length;i++)
            {
                var surface=settings.surfaces[i];
                var material=new Material(depthShader) { hideFlags=HideFlags.HideAndDontSave }; materials[i]=material;
                SceneDeferredCamera.BindInputs(material,surface.inputs);
                material.SetMatrix("_ViewProjection",vp); material.SetMatrix("_SceneView",view);
                material.SetVector("_VertexScale",surface.vertexScale);
                material.SetInt("_Cull",(int)surface.cull); material.SetFloat("_Cutoff",surface.alphaCutoff);
                if(depthId)
                {
                    material.SetFloat("_ReflectionThreshold",settings.reflectionSmoothnessThreshold);
                    material.SetFloat("_ReceiveReflections",(settings.reflectionExcludedLayers.value&(1<<surface.renderer.gameObject.layer))==0?1:0);
                }
                draws[i]=new TileRenderPass.Draw { renderer=surface.renderer,submesh=surface.materialIndex,material=material,shaderPass=0 };
            }
            plan=new TileRenderPass.Plan { enabled=true,backend=settings.backend,width=EyeDepth.width,height=EyeDepth.height,
                maximumAttachmentMiB=settings.maximumAttachmentMiB,depthAttachment=1,
                attachments=new[] {
                    new TileRenderPass.Attachment { name="Tile current linear eye depth",format=GraphicsFormat.R32_SFloat,target=EyeDepth,store=true },
                    new TileRenderPass.Attachment { name="Tile prepass depth test",format=GraphicsFormat.D32_SFloat }
                },subpasses=new[] { new TileRenderPass.Subpass { name="Tile current geometry depth bridge",colors=new[]{0},draws=draws } } };
            if(depthId)
            {
                plan.depthAttachment=2;
                plan.attachments=new[] { plan.attachments[0],new TileRenderPass.Attachment { name="Tile geometry normal and SSR mask",
                    format=GraphicsFormat.R8G8B8A8_UNorm,target=GeometryDepthId,store=true },plan.attachments[1] };
                plan.subpasses[0].colors=new[]{0,1};
            }
            if(!TileRenderPass.Validate(plan,out var budget,out error))return false; Budget=budget;
            if(!settings.positionLighting)return true;
            var lightSettings=new SceneForwardLightSettings { enabled=true,backend=settings.localLightBackend,
                allowBruteForceFallback=settings.allowLightFallback,localLights=settings.localLights,mainLightShadow=settings.mainLightShadow,
                lightDirection=settings.lightDirection,lightRadiance=settings.lightRadiance,ambientIrradiance=settings.ambientIrradiance,
                diffuseScale=settings.directionalDiffuseScale,specularScale=settings.directionalSpecularScale,
                backlightScale=settings.directionalBacklight,giBaseScale=settings.giBaseScale,directionalGiWeight=settings.directionalGiWeight,
                mainBakedShadowChannel=settings.mainBakedShadowChannel };
            if(!lights.Prepare(camera,lightSettings,EyeDepth.width,EyeDepth.height,out error))return false;
            Lighting=new Material(shader) { name="Toolkit tile position light snapshot",hideFlags=HideFlags.HideAndDontSave };
            lights.Bind(Lighting); Lighting.SetMatrix("_InverseViewProjection",vp.inverse); Lighting.SetMatrix("_SceneView",view);
            Lighting.SetTexture("_SceneEyeDepth",EyeDepth); return true;
        }
        public bool Validate(out string error)
        {
            if(Lighting!=null && !lights.IsCreated) { error="Tile position light resources no longer valid"; return false; }
            return TileRenderPass.Validate(plan,out _,out error);
        }
        public bool Record(ScriptableRenderContext context,out string error)
        {
            if(Lighting!=null)
            {
                if(commands==null)commands=new CommandBuffer { name="Toolkit tile current shadows and light grid" };
                commands.Clear(); lights.Record(commands); context.ExecuteCommandBuffer(commands); commands.Clear();
            }
            return renderer.TryRecord(context,plan,out _,out error);
        }
        public void Dispose()
        {
            renderer.Dispose(); lights.Dispose(); commands?.Dispose(); commands=null;
            if(materials!=null)foreach(var material in materials)Destroy(material); materials=null;
            Destroy(Lighting); Lighting=null;
            if(EyeDepth!=null) { EyeDepth.Release(); Destroy(EyeDepth); EyeDepth=null; } plan=null;
            if(GeometryDepthId!=null) { GeometryDepthId.Release();Destroy(GeometryDepthId);GeometryDepthId=null; }
        }
        private static void Destroy(UnityEngine.Object value)
        { if(value==null)return; if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value); }
    }
}
