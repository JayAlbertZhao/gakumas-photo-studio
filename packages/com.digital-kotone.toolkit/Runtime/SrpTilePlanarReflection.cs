using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>One explicit planar capture after a closed Tile scene pass. No Camera.Render,
    /// automatic culling, main material edits or Submit. Callers retain draw resources and
    /// source contents until GPU completion, including before reuse, resize and Dispose.</summary>
    public sealed class SrpTilePlanarReflection : IDisposable
    {
        public sealed class Settings
        {
            public bool enabled;
            public Vector3 planePoint, planeNormal=Vector3.up;
            public float clipOffset=.01f, resolutionScale=.5f, maximumRoughnessMip=5;
            public float receiverPlaneTolerance=.025f, strength=1;
            public int receiverGroup, maximumMiB=128;
            public LayerMask reflectedLayers=~0;
            // Current explicitly ordered reduced draws; custom coverage must match color
            // geometry/clip/depth/stencil. ActorPlanarCaptureSet.Draws is supported.
            public PlanarReflection.Draw[] draws=Array.Empty<PlanarReflection.Draw>();
            // The caller declares its command-buffer culling state; no global GL writes.
            public bool hostInvertCulling;
        }
        public readonly struct Frame
        {
            private readonly SrpTilePlanarReflection owner;
            public readonly ulong sequence;
            public readonly RenderTexture capture, reflection;
            public readonly Matrix4x4 reflectedView, reflectedGpuProjection;
            public bool IsCurrent=>owner!=null && owner.Current(sequence);
            internal bool Matches(TileSceneRenderer.PreparedFrame scene,ulong value)=>
                IsCurrent && sequence==value && ReferenceEquals(owner.source,scene);
            internal Frame(SrpTilePlanarReflection value)
            { owner=value;sequence=value.lastSequence;capture=value.capture;reflection=value.reflection;
                reflectedView=value.mirror.worldToCameraMatrix;reflectedGpuProjection=GL.GetGPUProjectionMatrix(value.mirror.projectionMatrix,true); }
        }
        public Camera Camera { get; }
        public Settings Configuration { get; }
        public long NominalTextureBytes { get; private set; }
        private Camera mirror;
        private RenderTexture capture,reflection;
        private Material utility,project;
        private readonly List<Material> coverage=new List<Material>();
        private CommandBuffer commands;
        private TileSceneRenderer.PreparedFrame source;
        private ulong lastSequence;
        private bool disposed,ready;
        public SrpTilePlanarReflection(Camera camera,Settings settings) { Camera=camera;Configuration=settings; }
        private bool Current(ulong value)=>!disposed && ready && value==lastSequence && Configuration!=null && Configuration.enabled &&
            source!=null && source.IsRecorded && Alive();
        private bool Alive()=>capture!=null && capture.IsCreated() && reflection!=null && reflection.IsCreated();

        public bool TryRecord(ScriptableRenderContext context,TileSceneRenderer.PreparedFrame scene,ulong sequence,out Frame frame,out string error)
        {
            frame=default;error=Validate(scene,sequence);if(error!=null)return false;
            bool setup=false;
            try
            {
                var s=Configuration;ready=false;
                Allocate(scene.Color.width,scene.Color.height);
                mirror.CopyFrom(Camera);mirror.enabled=false;mirror.allowMSAA=false;mirror.allowHDR=true;
                mirror.cullingMask=0;mirror.targetTexture=capture;mirror.depthTextureMode=DepthTextureMode.None;
                mirror.clearFlags=CameraClearFlags.SolidColor;mirror.backgroundColor=Color.clear;
                var normal=s.planeNormal.normalized;
                var view=scene.WorldToCamera*PlanarReflection.ReflectionMatrix(s.planePoint,normal);
                var inverse=view.inverse;
                mirror.transform.SetPositionAndRotation(inverse.MultiplyPoint(Vector3.zero),
                    Quaternion.LookRotation(inverse.MultiplyVector(Vector3.back),inverse.MultiplyVector(Vector3.up)));
                mirror.worldToCameraMatrix=view;mirror.projectionMatrix=Camera.projectionMatrix;
                Vector3 clipPoint=s.planePoint+normal*s.clipOffset;
                var plane=new Vector4(normal.x,normal.y,normal.z,-Vector3.Dot(normal,clipPoint));
                mirror.projectionMatrix=mirror.CalculateObliqueMatrix(view.inverse.transpose*plane);
                if(!Finite(mirror.projectionMatrix))throw new InvalidOperationException("Singular oblique projection");
                commands.Clear();commands.SetRenderTarget(capture);commands.SetViewport(new Rect(0,0,capture.width,capture.height));
                commands.ClearRenderTarget(true,true,Color.clear);
                commands.SetInvertCulling(!s.hostInvertCulling);
                var active=new List<PlanarReflection.Draw>();
                foreach(var draw in s.draws)if(Active(draw,s.reflectedLayers))active.Add(draw);
                foreach(var draw in active)commands.DrawRenderer(draw.surface.renderer,draw.material,draw.surface.materialIndex,draw.shaderPass);
                // RGB blending cannot define coverage (opaque shader alpha may be zero).
                // Clear A alone, then replay depth/stencil and exact coverage in color order.
                commands.Blit(Texture2D.blackTexture,capture,utility,0);
                commands.SetRenderTarget(capture);commands.SetViewport(new Rect(0,0,capture.width,capture.height));
                commands.ClearRenderTarget(true,false,Color.clear);
                int index=0;
                foreach(var draw in active)
                {
                    Material material=draw.coverageMaterial;int pass=draw.coverageShaderPass;
                    if(material==null)
                    {
                        while(coverage.Count<=index)coverage.Add(Material("PlanarReflection"));
                        material=coverage[index++];pass=3;
                        var surface=draw.surface;
                        material.SetFloat("_Cull",(int)surface.cull);material.SetVector("_PlanarVertexScale",surface.vertexScale);
                        material.SetTexture("_PlanarAlpha",surface.alphaMask!=null?surface.alphaMask:Texture2D.whiteTexture);
                        material.SetVector("_PlanarAlphaST",surface.alphaMaskST);material.SetFloat("_PlanarCutoff",surface.alphaCutoff);
                    }
                    commands.DrawRenderer(draw.surface.renderer,material,draw.surface.materialIndex,pass);
                }
                commands.SetInvertCulling(s.hostInvertCulling);commands.GenerateMips(capture);
                // SetupCameraProperties schedules the actual reflected camera globals. Do
                // not rely on Built-in-only SetViewProjectionMatrices in a custom SRP.
                context.SetupCameraProperties(mirror);setup=true;context.ExecuteCommandBuffer(commands);commands.Clear();
                context.SetupCameraProperties(Camera);setup=false;
                project.SetTexture("_TilePlanarCapture",capture);project.SetTexture("_TilePlanarDepth",scene.EyeDepth);
                project.SetTexture("_TilePlanarNormal",scene.NormalIdentity);project.SetTexture("_TilePlanarMos",scene.MaterialMos);
                project.SetMatrix("_TilePlanarInverseProjection",scene.GpuProjection.inverse);project.SetMatrix("_TilePlanarInverseView",scene.WorldToCamera.inverse);
                project.SetMatrix("_TilePlanarCaptureVP",GL.GetGPUProjectionMatrix(mirror.projectionMatrix,true)*view);
                project.SetVector("_TilePlanarSize",new Vector4(reflection.width,reflection.height,0,0));
                project.SetVector("_TilePlanarPlane",new Vector4(normal.x,normal.y,normal.z,-Vector3.Dot(normal,s.planePoint)));
                project.SetVector("_TilePlanarOptions",new Vector4(s.receiverGroup,s.receiverPlaneTolerance,s.strength,Mathf.Min(s.maximumRoughnessMip,capture.mipmapCount-1)));
                commands.Blit(scene.Color,reflection,project,0);context.ExecuteCommandBuffer(commands);commands.Clear();
                source=scene;lastSequence=sequence;ready=true;frame=new Frame(this);return true;
            }
            catch(Exception exception) { ready=false;error="SRP Tile Planar failed: "+exception.Message;return false; }
            finally
            {
                commands?.Clear();
                if(setup)
                {
                    commands.SetInvertCulling(Configuration.hostInvertCulling);context.ExecuteCommandBuffer(commands);commands.Clear();
                    context.SetupCameraProperties(Camera);
                }
            }
        }
        private string Validate(TileSceneRenderer.PreparedFrame scene,ulong sequence)
        {
            var s=Configuration;
            if(disposed || s==null || !s.enabled)return "SRP Tile Planar disabled or disposed";
            if(GraphicsSettings.currentRenderPipeline==null || Camera==null || scene==null || !scene.IsRecorded || scene.Camera!=Camera)
                return "SRP Tile Planar requires the current successfully recorded matching camera scene";
            if(sequence==0 || sequence<=lastSequence || ReferenceEquals(source,scene))return "SRP Tile Planar requires a fresh scene and monotonic positive sequence";
            if(Camera.stereoEnabled || Camera.allowDynamicResolution || Camera.rect!=new Rect(0,0,1,1) || QualitySettings.activeColorSpace!=ColorSpace.Linear)
                return "SRP Tile Planar requires Linear, fixed-size full viewport without XR";
            if(!Finite(scene.WorldToCamera) || !Finite(scene.GpuProjection) || !Finite(s.planePoint) || !Finite(s.planeNormal) ||
                !Range(s.planeNormal.sqrMagnitude,1e-12f,1e12f) || !Range(s.clipOffset,.001f,100) || !Range(s.resolutionScale,.25f,1) ||
                !Range(s.maximumRoughnessMip,0,8) || !Range(s.receiverPlaneTolerance,.001f,1) || !Range(s.strength,0,1) ||
                s.receiverGroup<0 || s.receiverGroup>255 || s.maximumMiB<1 || s.maximumMiB>512 || s.draws==null)
                return "Invalid SRP Tile Planar settings";
            if(Vector3.Dot(s.planeNormal.normalized,scene.WorldToCamera.inverse.MultiplyPoint(Vector3.zero)-s.planePoint)<=s.clipOffset)
                return "SRP Tile Planar camera must be on the positive plane side";
            int w=scene.Color.width,h=scene.Color.height;
            if(w<1 || h<1 || w>4096 || h>4096 || Estimate(w,h,s.resolutionScale)>(long)s.maximumMiB*1048576)return "SRP Tile Planar texture budget exceeded";
            var inputs=new[]{scene.Color,scene.EyeDepth,scene.NormalIdentity,scene.MaterialMos};
            var formats=new[]{GraphicsFormat.B10G11R11_UFloatPack32,GraphicsFormat.R32_SFloat,GraphicsFormat.R16G16B16A16_SFloat,GraphicsFormat.R8G8B8A8_UNorm};
            for(int i=0;i<inputs.Length;i++)
            {
                var t=inputs[i];
                if(t==null || !t.IsCreated() || t.width!=w || t.height!=h || t.graphicsFormat!=formats[i] || t.antiAliasing!=1 ||
                    t.dimension!=TextureDimension.Tex2D || t.useDynamicScale || t==capture || t==reflection)return "SRP Tile Planar needs separate current stored depth/normal/MOS";
            }
            foreach(var draw in s.draws)
                if(draw==null || !SceneDepthData.ValidSurface(draw.surface) || !Valid(draw.material,draw.shaderPass) ||
                    (draw.coverageMaterial!=null && !Valid(draw.coverageMaterial,draw.coverageShaderPass)))return "Invalid explicit planar draw or coverage pass";
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11 && SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Vulkan)
                return "SRP Tile Planar currently supports desktop D3D11/Vulkan";
            if(!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat_S8_UInt,FormatUsage.Render))return "Planar HDR/depth-stencil capture unavailable";
            return null;
        }
        private static bool Active(PlanarReflection.Draw draw,LayerMask layers)=>draw.surface.renderer.enabled && !draw.surface.renderer.forceRenderingOff &&
            draw.surface.renderer.gameObject.activeInHierarchy && (layers.value&(1<<draw.surface.renderer.gameObject.layer))!=0;
        private static bool Valid(Material material,int pass)=>material!=null && material.shader!=null && material.shader.isSupported && pass>=0 && pass<material.passCount;
        private static bool Range(float v,float a,float b)=>!float.IsNaN(v) && v>=a && v<=b;
        private static bool Finite(Vector3 v)=>Range(v.x,-1e6f,1e6f) && Range(v.y,-1e6f,1e6f) && Range(v.z,-1e6f,1e6f);
        private static bool Finite(Matrix4x4 m) { for(int i=0;i<16;i++)if(!Range(m[i],-1e12f,1e12f))return false;return Range(Mathf.Abs(m.determinant),1e-12f,1e12f); }
        private static long Estimate(int w,int h,float scale)
        {
            int x=Mathf.CeilToInt(w*scale),y=Mathf.CeilToInt(h*scale);long bytes=(long)w*h*8+(long)x*y*8;
            for(;;x=Mathf.Max(1,x/2),y=Mathf.Max(1,y/2)) { bytes+=(long)x*y*8;if(x==1&&y==1)return bytes; }
        }
        private void Allocate(int w,int h)
        {
            if(commands==null)commands=new CommandBuffer { name="Toolkit SRP real planar capture and Tile projection" };
            if(utility==null)utility=Material("PlanarReflection");if(project==null)project=Material("TilePlanarReflection");
            if(mirror==null) { var go=new GameObject("Toolkit SRP planar camera") { hideFlags=HideFlags.HideAndDontSave };mirror=go.AddComponent<Camera>();mirror.enabled=false; }
            int cw=Mathf.CeilToInt(w*Configuration.resolutionScale),ch=Mathf.CeilToInt(h*Configuration.resolutionScale);
            if(Alive() && capture.width==cw && capture.height==ch && reflection.width==w && reflection.height==h)return;
            ReleaseTargets();capture=Target(cw,ch,true);reflection=Target(w,h,false);NominalTextureBytes=Estimate(w,h,Configuration.resolutionScale);
        }
        private static Material Material(string name)
        { var shader=Resources.Load<Shader>(name);if(shader==null || !shader.isSupported)throw new InvalidOperationException("Missing planar shader: "+name);
            return new Material(shader) { hideFlags=HideFlags.HideAndDontSave }; }
        private static RenderTexture Target(int w,int h,bool mip)
        {
            var descriptor=new RenderTextureDescriptor(w,h,GraphicsFormat.R16G16B16A16_SFloat,0) {
                depthStencilFormat=mip?GraphicsFormat.D32_SFloat_S8_UInt:GraphicsFormat.None };
            var t=new RenderTexture(descriptor) {
                name=mip?"Toolkit SRP Planar capture":"Toolkit SRP Planar current Tile projection",hideFlags=HideFlags.HideAndDontSave,
                useMipMap=mip,autoGenerateMips=false,filterMode=mip?FilterMode.Trilinear:FilterMode.Point,wrapMode=TextureWrapMode.Clamp };
            if(t.Create())return t;Destroy(t);throw new InvalidOperationException("Planar target allocation failed");
        }
        private void ReleaseTargets()
        { ready=false;if(mirror!=null)mirror.targetTexture=null;foreach(var t in new[]{capture,reflection})if(t!=null) { t.Release();Destroy(t); }
            capture=reflection=null;NominalTextureBytes=0; }
        private static void Destroy(UnityEngine.Object value) { if(value==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value); }
        public void Dispose()
        { if(disposed)return;disposed=true;ReleaseTargets();commands?.Release();commands=null;Destroy(utility);Destroy(project);
            foreach(var m in coverage)Destroy(m);coverage.Clear();if(mirror!=null)Destroy(mirror.gameObject);mirror=null; }
    }
}
