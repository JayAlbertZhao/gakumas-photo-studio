using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit full Actor Forward composition after scene-only reflection/history.
    /// Defaults to separate color/depth outputs. Explicit packed reuse consumes the
    /// scene color/depth only after scene-only readers/history have been recorded.
    /// No Camera.Render, scene search, global shader writes, pipeline switch or Submit.</summary>
    public sealed class SrpActorForward : IDisposable
    {
        public enum Storage { SeparateHalf, SeparatePacked, ReuseScenePacked }
        public sealed class Settings
        {
            public bool enabled;public int maximumMiB=256;public Storage storage;
            public readonly SceneMotionSettings motion=new SceneMotionSettings();
            public uint motionRevision;
            public int motionMaximumMiB=256;
            // GPU-only imported meshes cannot expose index contents for CPU
            // comparison. Opt in only for immutable topology; advance motionRevision
            // before any in-place index edit. Actual deformation stays GPU-tracked.
            public bool allowImmutableUnreadableMotionMeshes;
            public bool includeSceneMotion,reuseSceneMotionStorage;
            public int sceneMotionMaximumMiB=128;
        }
        public readonly struct Frame
        {
            private readonly SrpActorForward owner;
            public readonly ulong sequence;
            public readonly RenderTexture color,eyeDepth,motionDepthIdentity,expectedPreviousDepth;
            public bool IsCurrent=>owner!=null&&owner.Current(sequence);
            internal Frame(SrpActorForward value) { owner=value;sequence=value.sequence;color=value.color;eyeDepth=value.eyeDepth;
                motionDepthIdentity=value.motion?.Motion;expectedPreviousDepth=value.motion?.PreviousDepth; }
        }
        public Camera Camera { get; }
        public Settings Configuration { get; }
        public long NominalTextureBytes { get; private set; }
        private RenderTexture color,eyeDepth;
        private RenderTexture hardwareDepth;
        private bool borrowedColor;
        private Storage allocatedStorage;
        private ActorTemporalHistory motion;
        private SceneMotionHistory sceneMotion;
        private uint previousSceneRevision;
        public long MotionNominalTextureBytes => motion?.NominalTextureBytes??0;
        public long SceneMotionNominalTextureBytes=>sceneMotion?.SnapshotNominalTextureBytes??0;
        public bool MotionContinuous => motion!=null&&motion.Continuous;
        public void ResetMotionHistoryAfterGpuCompletion() { motion?.ResetHistory();sceneMotion?.ResetHistory(); }
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
        private bool Alive()=>color!=null&&color.IsCreated()&&hardwareDepth!=null&&hardwareDepth.IsCreated()&&eyeDepth!=null&&eyeDepth.IsCreated()&&
            (motion==null||motion.IsCreated)&&(sceneMotion==null||sceneMotion.IsCreated);
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
                ready=false;Allocate(scene);
                if(Configuration.motion.enabled)
                {
                    if(motion==null)motion=new ActorTemporalHistory();
                    motion.Prepare(Camera,draws,Configuration.motion,value,Configuration.motionRevision,color.width,color.height,Configuration.motionMaximumMiB,Configuration.allowImmutableUnreadableMotionMeshes,
                        Configuration.reuseSceneMotionStorage?scene.NormalIdentity:null);
                    if(Configuration.includeSceneMotion)
                    {
                        if(sceneMotion==null)sceneMotion=new SceneMotionHistory(true);
                        if(!motion.Continuous||previousSceneRevision!=Configuration.motionRevision)sceneMotion.ResetHistory();
                        if(!sceneMotion.PrepareHalf4(Configuration.motion,Camera,scene.MotionSurfaces,motion.Motion,motion.PreviousDepth,hardwareDepth,Configuration.sceneMotionMaximumMiB,out var why))
                            throw new InvalidOperationException(why);
                    }
                    else {sceneMotion?.Dispose();sceneMotion=null;}
                }
                else { sceneMotion?.Dispose();sceneMotion=null;motion?.Dispose();motion=null; }
                bool reuse=borrowedColor;
                seed.SetTexture("_ActorSourceColor",reuse&&!reflected.HasValue?Texture2D.blackTexture:(Texture)(reflected.HasValue?reflected.Value.color:scene.Color));
                if(!reuse)seed.SetTexture("_ActorSourceHardwareDepth",scene.DepthStencil,RenderTextureSubElement.Depth);
                else seed.SetTexture("_ActorSourceHardwareDepth",Texture2D.blackTexture);
                foreach(var material in new[]{seed,export})
                { material.SetVector("_ActorTargetSize",new Vector4(color.width,color.height,0,0));material.SetMatrix("_ActorInverseProjection",scene.GpuProjection.inverse); }
                export.SetTexture("_ActorHardwareDepth",hardwareDepth,RenderTextureSubElement.Depth);
                commands.Clear();motion?.RecordSnapshots(commands);
                commands.SetRenderTarget(color,hardwareDepth);commands.SetViewport(new Rect(0,0,color.width,color.height));
                if(reuse)
                {
                    // Keep raster Z exact. Clear only old scene/decal stencil; clearing
                    // depth would discard occlusion. Never sample an active attachment.
                    commands.DrawMesh(quad,Matrix4x4.identity,seed,0,3);
                    if(reflected.HasValue)commands.DrawMesh(quad,Matrix4x4.identity,seed,0,2);
                }
                else { commands.ClearRenderTarget(true,true,Color.clear);commands.DrawMesh(quad,Matrix4x4.identity,seed,0,0); }
                if(motion!=null)motion.RecordActors(commands,color,hardwareDepth,sceneMotion);
                else foreach(var draw in draws.draws)commands.DrawRenderer(draw.renderer,draw.material,draw.submesh,draw.pass);
                commands.SetRenderTarget(eyeDepth);commands.SetViewport(new Rect(0,0,color.width,color.height));commands.DrawMesh(quad,Matrix4x4.identity,export,0,1);
                context.SetupCameraProperties(Camera);
                // Consume before submission, conservatively including partial failures.
                if(reuse&&!scene.TryConsumeSceneAttachments())throw new InvalidOperationException("Scene attachments already consumed");
                if(Configuration.reuseSceneMotionStorage&&!scene.TryConsumeSceneNormals())throw new InvalidOperationException("Scene normal contents already consumed");
                context.ExecuteCommandBuffer(commands);commands.Clear();
                motion?.Complete();
                sceneMotion?.Complete();previousSceneRevision=Configuration.motionRevision;
                source=scene;actors=draws;reflection=reflected;sequence=value;ready=true;frame=new Frame(this);return true;
            }
            catch(Exception exception){ready=false;motion?.ResetHistory();sceneMotion?.ResetHistory();commands?.Clear();error="SRP Actor Forward failed: "+exception.Message;return false;}
        }
        private string Validate(TileSceneRenderer.PreparedFrame scene,ActorForwardDrawSet.PreparedFrame draws,ulong value,SrpTileReflection.Frame? reflected)
        {
            if(disposed||Configuration==null||!Configuration.enabled)return "SRP Actor Forward disabled or disposed";
            if(GraphicsSettings.currentRenderPipeline==null||Camera==null||scene==null||!scene.SceneContentAvailable||scene.Camera!=Camera||draws==null||!draws.IsValid||draws.Camera!=Camera)
                return "Requires a recorded current scene and matching full Actor draw preparation";
            if(value==0||value<=sequence||ReferenceEquals(source,scene))return "Requires a fresh scene and monotonic positive sequence";
            if(draws.selfShadow.HasValue&&draws.selfShadow.Value.sequence!=value)return "Actor shadow must match the composition sequence";
            if((color!=null&&draws.sampled.Contains(color))||(eyeDepth!=null&&draws.sampled.Contains(eyeDepth)))return "Actor materials must not sample their current output";
            if(reflected.HasValue&&!reflected.Value.Matches(scene,value))return "Reflection output must match the exact scene and sequence";
            if(!Enum.IsDefined(typeof(Storage),Configuration.storage))return "Invalid Actor storage policy";
            bool reuse=Configuration.storage==Storage.ReuseScenePacked;
            if((Configuration.includeSceneMotion||Configuration.reuseSceneMotionStorage)&&!Configuration.motion.enabled)return "Scene motion storage requires enabled motion";
            if(Configuration.reuseSceneMotionStorage)
            {
                var normal=scene.NormalIdentity;
                if(normal==null||!normal.IsCreated()||!scene.SceneNormalContentAvailable||normal.width!=scene.Color.width||normal.height!=scene.Color.height||
                    normal.graphicsFormat!=GraphicsFormat.R16G16B16A16_SFloat||normal.antiAliasing!=1||normal.memorylessMode!=RenderTextureMemoryless.None||normal.useDynamicScale||
                    normal.dimension!=TextureDimension.Tex2D||normal.sRGB||normal==scene.Color||normal==scene.DepthStencil||draws.sampled.Contains(normal))
                    return "Requires separate stored Half4 scene normals without Actor sampling feedback";
            }
            if(reuse&&(draws.sampled.Contains(scene.Color)||draws.sampled.Contains(scene.DepthStencil)))return "Actor materials must not sample reused scene attachments";
            if(QualitySettings.activeColorSpace!=ColorSpace.Linear||Camera.stereoEnabled||Camera.allowDynamicResolution||Camera.rect!=new Rect(0,0,1,1))return "Requires Linear fixed-size full viewport without XR";
            int w=scene.Color.width,h=scene.Color.height;
            long bytes=Configuration.storage==Storage.SeparateHalf?(long)w*h*20:(long)w*h*(reuse?4:16);
            if(w<1||h<1||w>4096||h>4096||Configuration.maximumMiB<1||Configuration.maximumMiB>512||bytes>(long)Configuration.maximumMiB*1048576)return "Actor Forward texture budget exceeded";
            var inputs=new[]{scene.Color,scene.DepthStencil,reflected.HasValue?reflected.Value.color:scene.Color};
            foreach(var t in inputs)if(t==null||!t.IsCreated()||t.width!=w||t.height!=h||t.antiAliasing!=1||t.dimension!=TextureDimension.Tex2D||t.useDynamicScale||t.memorylessMode!=RenderTextureMemoryless.None||(!borrowedColor&&t==color)||t==eyeDepth)return "Requires separate current stored scene inputs";
            if(scene.Color==scene.DepthStencil||(reflected.HasValue&&(reflected.Value.color==scene.Color||reflected.Value.color==scene.DepthStencil)))return "Scene/reflection attachments must be distinct";
            if(scene.DepthStencil.graphicsFormat!=GraphicsFormat.None||scene.DepthStencil.depthStencilFormat!=GraphicsFormat.D32_SFloat_S8_UInt||scene.Color.graphicsFormat!=GraphicsFormat.B10G11R11_UFloatPack32)
                return "Requires stored exact scene D32S8 depth and packed HDR color";
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11&&SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Vulkan)return "Actor Forward currently supports desktop D3D11/Vulkan";
            // Sample the Depth subelement, not the combined D32S8 format as a color
            // texture. Unity's D3D11 combined-format Sample query is false even when
            // the depth view is supported. Verify the actual allocation below.
            if(!SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat_S8_UInt,FormatUsage.Render)||!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Depth))return "D32S8 depth view unavailable";
            foreach(var format in new[]{GraphicsFormat.R16G16B16A16_SFloat,GraphicsFormat.R32_SFloat})
                if(!SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))return "Actor HDR/depth export unavailable";
            if(Configuration.storage!=Storage.SeparateHalf&&!SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32,FormatUsage.Blend))return "Packed Actor blending unavailable";
            return null;
        }
        private void Allocate(TileSceneRenderer.PreparedFrame scene)
        {
            int w=scene.Color.width,h=scene.Color.height;
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
            if(Alive()&&color.width==w&&color.height==h&&allocatedStorage==Configuration.storage&&
                (!borrowedColor||(color==scene.Color&&hardwareDepth==scene.DepthStencil)))return;
            ReleaseTargets();allocatedStorage=Configuration.storage;borrowedColor=allocatedStorage==Storage.ReuseScenePacked;
            if(borrowedColor){color=scene.Color;hardwareDepth=scene.DepthStencil;}
            else
            {
                var format=allocatedStorage==Storage.SeparateHalf?GraphicsFormat.R16G16B16A16_SFloat:GraphicsFormat.B10G11R11_UFloatPack32;
                color=Target(w,h,format,GraphicsFormat.D32_SFloat_S8_UInt,"Toolkit full Actor color and depth");hardwareDepth=color;
            }
            eyeDepth=Target(w,h,GraphicsFormat.R32_SFloat,GraphicsFormat.None,"Toolkit scene and Actor current eye depth");
            NominalTextureBytes=(long)w*h*(borrowedColor?4:allocatedStorage==Storage.SeparateHalf?20:16);
        }
        private static RenderTexture Target(int w,int h,GraphicsFormat format,GraphicsFormat depth,string name)
        {
            var value=new RenderTexture(new RenderTextureDescriptor(w,h,format,0) { depthStencilFormat=depth }) { name=name,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp };
            if(value.Create()&&value.graphicsFormat==format&&value.depthStencilFormat==depth)return value;
            value.Release();Destroy(value);throw new InvalidOperationException("Exact Actor output allocation failed");
        }
        private void ReleaseTargets() { ready=false;foreach(var t in new[]{borrowedColor?null:color,eyeDepth})if(t!=null){t.Release();Destroy(t);}color=eyeDepth=hardwareDepth=null;borrowedColor=false;NominalTextureBytes=0; }
        private static void Destroy(UnityEngine.Object value) { if(value==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value); }
        public void Dispose() { if(disposed)return;disposed=true;sceneMotion?.Dispose();sceneMotion=null;motion?.Dispose();motion=null;ReleaseTargets();commands?.Release();commands=null;Destroy(seed);Destroy(export);Destroy(quad); }
    }
}
