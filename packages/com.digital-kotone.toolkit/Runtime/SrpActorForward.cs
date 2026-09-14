using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit full Actor Forward composition after scene-only reflection/history.
    /// Owns separate color/depth outputs and preserves all borrowed Tile/reflection inputs.
    /// No Camera.Render, scene search, global shader writes, pipeline switch or Submit.</summary>
    public sealed class SrpActorForward : IDisposable
    {
        public sealed class Settings { public bool enabled;public int maximumMiB=256; }
        public readonly struct Frame
        {
            private readonly SrpActorForward owner;
            public readonly ulong sequence;
            public readonly RenderTexture color,eyeDepth;
            public bool IsCurrent=>owner!=null&&owner.Current(sequence);
            internal Frame(SrpActorForward value) { owner=value;sequence=value.sequence;color=value.color;eyeDepth=value.eyeDepth; }
        }
        public Camera Camera { get; }
        public Settings Configuration { get; }
        public long NominalTextureBytes { get; private set; }
        private RenderTexture color,eyeDepth;
        private Material seed,export;
        private Mesh quad;
        private CommandBuffer commands;
        private TileSceneRenderer.PreparedFrame source;
        private ActorForwardDrawSet.PreparedFrame actors;
        private SrpTileReflection.Frame? reflection;
        private bool disposed,ready;
        private ulong sequence;
        public SrpActorForward(Camera camera,Settings settings) { Camera=camera;Configuration=settings; }
        private bool Current(ulong value)=>!disposed&&ready&&value==sequence&&Configuration!=null&&Configuration.enabled&&Alive()&&
            source!=null&&source.IsRecorded&&actors!=null&&actors.IsValid&&(!reflection.HasValue||reflection.Value.Matches(source,value));
        private bool Alive()=>color!=null&&color.IsCreated()&&eyeDepth!=null&&eyeDepth.IsCreated();
        public bool TryRecord(ScriptableRenderContext context,TileSceneRenderer.PreparedFrame scene,ActorForwardDrawSet.PreparedFrame draws,
            ulong value,out Frame frame,out string error)=>Record(context,scene,draws,value,null,out frame,out error);
        public bool TryRecord(ScriptableRenderContext context,TileSceneRenderer.PreparedFrame scene,ActorForwardDrawSet.PreparedFrame draws,
            ulong value,SrpTileReflection.Frame reflected,out Frame frame,out string error)=>Record(context,scene,draws,value,reflected,out frame,out error);
        private bool Record(ScriptableRenderContext context,TileSceneRenderer.PreparedFrame scene,ActorForwardDrawSet.PreparedFrame draws,
            ulong value,SrpTileReflection.Frame? reflected,out Frame frame,out string error)
        {
            frame=default;error=Validate(scene,draws,value,reflected);if(error!=null)return false;
            try
            {
                ready=false;Allocate(scene.Color.width,scene.Color.height);
                seed.SetTexture("_ActorSourceColor",reflected.HasValue?reflected.Value.color:scene.Color);
                seed.SetTexture("_ActorSourceHardwareDepth",scene.DepthStencil,RenderTextureSubElement.Depth);
                foreach(var material in new[]{seed,export})
                { material.SetVector("_ActorTargetSize",new Vector4(color.width,color.height,0,0));material.SetMatrix("_ActorInverseProjection",scene.GpuProjection.inverse); }
                export.SetTexture("_ActorHardwareDepth",color,RenderTextureSubElement.Depth);
                commands.Clear();commands.SetRenderTarget(color);commands.SetViewport(new Rect(0,0,color.width,color.height));
                commands.ClearRenderTarget(true,true,Color.clear);commands.DrawMesh(quad,Matrix4x4.identity,seed,0,0);
                foreach(var draw in draws.draws)commands.DrawRenderer(draw.renderer,draw.material,draw.submesh,draw.pass);
                commands.SetRenderTarget(eyeDepth);commands.SetViewport(new Rect(0,0,color.width,color.height));commands.DrawMesh(quad,Matrix4x4.identity,export,0,1);
                context.SetupCameraProperties(Camera);context.ExecuteCommandBuffer(commands);commands.Clear();
                source=scene;actors=draws;reflection=reflected;sequence=value;ready=true;frame=new Frame(this);return true;
            }
            catch(Exception exception){ready=false;commands?.Clear();error="SRP Actor Forward failed: "+exception.Message;return false;}
        }
        private string Validate(TileSceneRenderer.PreparedFrame scene,ActorForwardDrawSet.PreparedFrame draws,ulong value,SrpTileReflection.Frame? reflected)
        {
            if(disposed||Configuration==null||!Configuration.enabled)return "SRP Actor Forward disabled or disposed";
            if(GraphicsSettings.currentRenderPipeline==null||Camera==null||scene==null||!scene.IsRecorded||scene.Camera!=Camera||draws==null||!draws.IsValid||draws.Camera!=Camera)
                return "Requires a recorded current scene and matching full Actor draw preparation";
            if(value==0||value<=sequence||ReferenceEquals(source,scene))return "Requires a fresh scene and monotonic positive sequence";
            if((color!=null&&draws.sampled.Contains(color))||(eyeDepth!=null&&draws.sampled.Contains(eyeDepth)))return "Actor materials must not sample their current output";
            if(reflected.HasValue&&!reflected.Value.Matches(scene,value))return "Reflection output must match the exact scene and sequence";
            if(QualitySettings.activeColorSpace!=ColorSpace.Linear||Camera.stereoEnabled||Camera.allowDynamicResolution||Camera.rect!=new Rect(0,0,1,1))return "Requires Linear fixed-size full viewport without XR";
            int w=scene.Color.width,h=scene.Color.height;
            if(w<1||h<1||w>4096||h>4096||Configuration.maximumMiB<1||Configuration.maximumMiB>512||(long)w*h*20>(long)Configuration.maximumMiB*1048576)return "Actor Forward texture budget exceeded";
            var inputs=new[]{scene.Color,scene.DepthStencil,reflected.HasValue?reflected.Value.color:scene.Color};
            foreach(var t in inputs)if(t==null||!t.IsCreated()||t.width!=w||t.height!=h||t.antiAliasing!=1||t.dimension!=TextureDimension.Tex2D||t.useDynamicScale||t==color||t==eyeDepth)return "Requires separate current stored scene inputs";
            if(scene.DepthStencil.graphicsFormat!=GraphicsFormat.None||scene.DepthStencil.depthStencilFormat!=GraphicsFormat.D32_SFloat_S8_UInt||scene.Color.graphicsFormat!=GraphicsFormat.B10G11R11_UFloatPack32)
                return "Requires stored exact scene D32S8 depth and packed HDR color";
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11&&SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Vulkan)return "Actor Forward currently supports desktop D3D11/Vulkan";
            // Sample the Depth subelement, not the combined D32S8 format as a color
            // texture. Unity's D3D11 combined-format Sample query is false even when
            // the depth view is supported. Verify the actual allocation below.
            if(!SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat_S8_UInt,FormatUsage.Render)||!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Depth))return "D32S8 depth view unavailable";
            foreach(var format in new[]{GraphicsFormat.R16G16B16A16_SFloat,GraphicsFormat.R32_SFloat})
                if(!SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))return "Actor HDR/depth export unavailable";
            return null;
        }
        private void Allocate(int w,int h)
        {
            if(commands==null)commands=new CommandBuffer { name="Toolkit full Actor Forward after scene history" };
            if(seed==null)
            {
                var shader=Resources.Load<Shader>("ActorForwardDepth");if(shader==null||!shader.isSupported)throw new InvalidOperationException("Actor depth bridge unavailable");
                seed=new Material(shader) { hideFlags=HideFlags.HideAndDontSave };export=new Material(shader) { hideFlags=HideFlags.HideAndDontSave };
            }
            if(quad==null)
            {
                quad=new Mesh { name="Toolkit Actor fullscreen depth bridge",hideFlags=HideFlags.HideAndDontSave };
                quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
                quad.uv=new[]{new Vector2(0,0),new Vector2(1,0),new Vector2(1,1),new Vector2(0,1)};quad.triangles=new[]{0,1,2,0,2,3};
            }
            if(Alive()&&color.width==w&&color.height==h)return;
            ReleaseTargets();color=Target(w,h,GraphicsFormat.R16G16B16A16_SFloat,GraphicsFormat.D32_SFloat_S8_UInt,"Toolkit full Actor color and depth");
            eyeDepth=Target(w,h,GraphicsFormat.R32_SFloat,GraphicsFormat.None,"Toolkit scene and Actor current eye depth");NominalTextureBytes=(long)w*h*20;
        }
        private static RenderTexture Target(int w,int h,GraphicsFormat format,GraphicsFormat depth,string name)
        {
            var value=new RenderTexture(new RenderTextureDescriptor(w,h,format,0) { depthStencilFormat=depth }) { name=name,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp };
            if(value.Create()&&value.graphicsFormat==format&&value.depthStencilFormat==depth)return value;
            value.Release();Destroy(value);throw new InvalidOperationException("Exact Actor output allocation failed");
        }
        private void ReleaseTargets() { ready=false;foreach(var t in new[]{color,eyeDepth})if(t!=null){t.Release();Destroy(t);}color=eyeDepth=null;NominalTextureBytes=0; }
        private static void Destroy(UnityEngine.Object value) { if(value==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value); }
        public void Dispose() { if(disposed)return;disposed=true;ReleaseTargets();commands?.Release();commands=null;Destroy(seed);Destroy(export);Destroy(quad); }
    }
}
