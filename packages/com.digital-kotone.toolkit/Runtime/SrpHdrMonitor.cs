using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit HDR UI/unlit camera producer for caller-owned SRP contexts.
    /// The host owns Submit, ordering and GPU completion; the default Built-in producer is unchanged.</summary>
    public sealed class SrpHdrMonitor : IDisposable
    {
        public sealed class Settings
        {
            public bool enabled;
            public int width=512, height=512;
            public MonitorUpdateMode updateMode=MonitorUpdateMode.WhenDirty;
            public float updatesPerSecond=30;
        }
        public readonly struct Frame
        {
            private readonly SrpHdrMonitor owner;
            public readonly RenderTexture texture;
            public readonly ulong sequence, contentVersion;
            public readonly double seconds;
            public bool IsCurrent => owner!=null && owner.Current(this);
            internal Frame(SrpHdrMonitor value)
            { owner=value;texture=value.output;sequence=value.RenderSequence;contentVersion=value.version;seconds=value.renderTime; }
        }
        public Camera Camera { get; }
        public Settings Configuration { get; }
        public ulong RenderSequence { get; private set; }
        public bool DidRecord { get; private set; }
        public string UnavailableReason { get; private set; }
        public long NominalColorBytes => capture!=null && output!=null ? (long)output.width*output.height*16 : 0;
        // Only called for a due capture, after installing its target. Content/layout work only.
        public event Action<double> PrepareCapture;

        private RenderTexture capture,output,originalTarget;
        private CommandBuffer commands;
        private bool ready,requested=true,recording,disposed,prepared,needsCapture;
        private double preparedTime;
        private ulong preparedVersion;
        private Matrix4x4 preparedView,preparedProjection;
        private int preparedLayers,preparedWidth,preparedHeight;
        private Color preparedBackground;
        private RenderTexture preparedTarget;
        private MonitorUpdateMode preparedMode;
        private float preparedRate;
        private RenderPipelineAsset preparedPipeline;
        private double renderTime,observedTime;
        private ulong version;
        private Matrix4x4 view,projection;
        private int layers;
        private Color background;
        private MonitorUpdateMode mode;
        private float rate;
        private RenderPipelineAsset pipeline;

        public SrpHdrMonitor(Camera camera,Settings settings)
        { Camera=camera;Configuration=settings; }
        public void RequestUpdate() { requested=true; }
        public bool TryGetFrame(out Frame frame)
        {
            frame=default;
            if(!ready || recording || Validate(renderTime)!=null || !Matches())return false;
            frame=new Frame(this);return true;
        }
        private bool Current(Frame frame) => frame.sequence==RenderSequence && frame.texture==output && TryGetFrame(out _);

        /// <summary>Call BEFORE entering the SRP render request/frame. UGUI batches can be
        /// snapshotted by the engine before the SRP callback; changing UI geometry inside that
        /// callback can lag one capture. This method performs due content/layout work first.</summary>
        public bool TryPrepare(double seconds,ulong contentVersion)
        {
            DidRecord=false;
            if(recording) { UnavailableReason="Recursive SRP monitor recording";return false; }
            bool pendingCapture=prepared&&needsCapture;prepared=false;
            string error=Validate(seconds);
            if(error!=null) { UnavailableReason=error;ready=false;return false; }
            bool changed=!Matches();
            bool due=pendingCapture || !ready || changed || requested || version!=contentVersion || seconds<observedTime ||
                Configuration.updateMode==MonitorUpdateMode.EveryCall ||
                (Configuration.updateMode==MonitorUpdateMode.FixedRate && seconds-renderTime>=1.0/Configuration.updatesPerSecond);
            observedTime=seconds;
            needsCapture=due;
            if(due&&!Allocate()) { ready=false;UnavailableReason="SRP monitor HDR allocation failed";return false; }
            var oldTarget=Camera.targetTexture;
            bool oldHdr=Camera.allowHDR,oldMsaa=Camera.allowMSAA;
            if(due)
            {
                recording=true;requested=false;
                try
                {
                    Camera.targetTexture=capture;Camera.allowHDR=true;Camera.allowMSAA=false;
                    PrepareCapture?.Invoke(seconds);
                    if(Validate(seconds)!=null || Camera.targetTexture!=capture ||
                        Configuration.width!=capture.width || Configuration.height!=capture.height)
                        throw new InvalidOperationException("SRP monitor ownership changed during preparation");
                }
                catch(Exception exception)
                { ready=false;requested=true;UnavailableReason="SRP monitor preparation failed: "+exception.Message;return false; }
                finally
                {
                    if(Camera!=null) { Camera.targetTexture=oldTarget;Camera.allowHDR=oldHdr;Camera.allowMSAA=oldMsaa; }
                    recording=false;
                }
            }
            preparedView=Camera.worldToCameraMatrix;preparedProjection=Camera.projectionMatrix;
            preparedLayers=Camera.cullingMask;preparedBackground=Camera.backgroundColor;preparedTarget=Camera.targetTexture;
            preparedWidth=Configuration.width;preparedHeight=Configuration.height;preparedMode=Configuration.updateMode;
            preparedRate=Configuration.updatesPerSecond;preparedPipeline=GraphicsSettings.currentRenderPipeline;
            preparedTime=seconds;preparedVersion=contentVersion;prepared=true;UnavailableReason=null;return true;
        }

        /// <summary>Consumes the prepared ticket: records SRPDefaultUnlit opaque/transparent
        /// and camera UI, then same-format copies into the stable published texture. Returned
        /// Frame is not a completion fence. No open render pass; restore host camera setup after.</summary>
        public bool TryRecord(ScriptableRenderContext context,out Frame frame)
        {
            frame=default;DidRecord=false;
            if(recording) { UnavailableReason="Recursive SRP monitor recording";return false; }
            if(!prepared || Validate(preparedTime)!=null || !PreparedMatches())
            {
                // A rejected ticket must not swallow an explicit content refresh after the
                // host restores its camera settings and prepares again.
                requested|=prepared&&needsCapture;prepared=false;
                UnavailableReason="Prepare SRP monitor before entering the render request; keep camera/settings unchanged";return false;
            }
            prepared=false;
            if(!needsCapture)return TryGetFrame(out frame);
            var oldTarget=Camera.targetTexture;bool oldHdr=Camera.allowHDR,oldMsaa=Camera.allowMSAA;
            recording=true;
            try
            {
                Camera.targetTexture=capture;Camera.allowHDR=true;Camera.allowMSAA=false;
                if(!Camera.TryGetCullingParameters(false,out var culling))throw new InvalidOperationException("SRP monitor culling unavailable");
                culling.cullingOptions &= ~CullingOptions.OcclusionCull;
                context.SetupCameraProperties(Camera);
                // Offscreen requests must explicitly emit the dedicated camera's UI geometry.
                ScriptableRenderContext.EmitGeometryForCamera(Camera);
                var results=context.Cull(ref culling);
                if(commands==null)commands=new CommandBuffer { name="Toolkit SRP HDR monitor" };
                commands.Clear();commands.SetRenderTarget(capture);
                commands.ClearRenderTarget(true,true,Camera.backgroundColor);
                context.ExecuteCommandBuffer(commands);commands.Clear();
                Draw(context,results,RenderQueueRange.opaque,SortingCriteria.CommonOpaque);
                Draw(context,results,RenderQueueRange.transparent,SortingCriteria.CommonTransparent);
                // Same-format copy preserves HDR/alpha and permits reads of the prior published
                // texture while rendering into the distinct capture target.
                commands.CopyTexture(capture,0,0,output,0,0);context.ExecuteCommandBuffer(commands);commands.Clear();
                view=preparedView;projection=preparedProjection;layers=Camera.cullingMask;background=Camera.backgroundColor;
                originalTarget=oldTarget;mode=Configuration.updateMode;rate=Configuration.updatesPerSecond;
                pipeline=GraphicsSettings.currentRenderPipeline;version=preparedVersion;renderTime=preparedTime;
                ready=true;RenderSequence++;DidRecord=true;UnavailableReason=null;
            }
            catch(Exception exception)
            {
                // Retain resources: partial commands may already be queued in the host context.
                // The host must finish/discard that work before another update or Dispose.
                ready=false;requested=true;UnavailableReason="SRP monitor recording failed: "+exception.Message;
            }
            finally
            {
                if(Camera!=null) { Camera.targetTexture=oldTarget;Camera.allowHDR=oldHdr;Camera.allowMSAA=oldMsaa; }
                commands?.Clear();recording=false;
            }
            return TryGetFrame(out frame);
        }
        private bool PreparedMatches() => Camera.targetTexture==preparedTarget && Camera.worldToCameraMatrix==preparedView &&
            Camera.projectionMatrix==preparedProjection && Camera.cullingMask==preparedLayers && Camera.backgroundColor==preparedBackground &&
            Configuration.width==preparedWidth && Configuration.height==preparedHeight && Configuration.updateMode==preparedMode &&
            Configuration.updatesPerSecond==preparedRate && GraphicsSettings.currentRenderPipeline==preparedPipeline &&
            capture!=null && output!=null && capture.IsCreated() && output.IsCreated();
        private void Draw(ScriptableRenderContext context,CullingResults results,RenderQueueRange queue,SortingCriteria sort)
        {
            var drawing=new DrawingSettings(new ShaderTagId("SRPDefaultUnlit"),new SortingSettings(Camera) { criteria=sort })
                { enableDynamicBatching=false,enableInstancing=true,perObjectData=PerObjectData.None };
            var filtering=new FilteringSettings(queue,Camera.cullingMask);
            context.DrawRenderers(results,ref drawing,ref filtering);
        }
        private string Validate(double seconds)
        {
            if(disposed)return "SRP monitor disposed";
            var s=Configuration;
            if(s==null || !s.enabled)return "SRP monitor disabled";
            if(Camera==null || Camera.enabled)return "SRP monitor requires a dedicated disabled camera";
            if(GraphicsSettings.currentRenderPipeline==null || Camera.stereoEnabled || Camera.allowDynamicResolution || Camera.rect!=new Rect(0,0,1,1))
                return "SRP monitor requires an explicit SRP, full viewport and fixed non-XR camera";
            if(Camera.clearFlags!=CameraClearFlags.SolidColor)return "SRP monitor requires SolidColor clear";
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11 && SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Vulkan)
                return "SRP monitor currently supports desktop D3D11/Vulkan";
            if(!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) || (SystemInfo.copyTextureSupport&CopyTextureSupport.Basic)==0)
                return "SRP monitor requires HDR Half targets and basic texture copy";
            if(s.width<1 || s.height<1 || s.width>Mathf.Min(4096,SystemInfo.maxTextureSize) || s.height>Mathf.Min(4096,SystemInfo.maxTextureSize) ||
                double.IsNaN(seconds) || double.IsInfinity(seconds) || Math.Abs(seconds)>1e12 ||
                !HdrMonitor.Range(s.updatesPerSecond,1,240) || !Enum.IsDefined(typeof(MonitorUpdateMode),s.updateMode))return "Invalid SRP monitor schedule or dimensions";
            var color=Camera.backgroundColor;
            if(!HdrMonitor.Range(color.r,0,65504) || !HdrMonitor.Range(color.g,0,65504) || !HdrMonitor.Range(color.b,0,65504) || !HdrMonitor.Range(color.a,0,1) ||
                !SceneDeferredCamera.Matrix(Camera.worldToCameraMatrix) || !SceneDeferredCamera.Matrix(Camera.projectionMatrix))return "Invalid SRP monitor camera inputs";
            return null;
        }
        private bool Matches() => Camera!=null && capture!=null && output!=null && capture.IsCreated() && output.IsCreated() &&
            output.width==Configuration.width && output.height==Configuration.height && Camera.targetTexture==originalTarget &&
            Camera.worldToCameraMatrix==view && Camera.projectionMatrix==projection && Camera.cullingMask==layers &&
            Camera.backgroundColor==background && Configuration.updateMode==mode && Configuration.updatesPerSecond==rate && GraphicsSettings.currentRenderPipeline==pipeline;
        private bool Allocate()
        {
            if(capture!=null && output!=null && capture.IsCreated() && output.IsCreated() && output.width==Configuration.width && output.height==Configuration.height)return true;
            Release();
            try
            {
                capture=Target("Toolkit SRP monitor capture",24);output=Target("Toolkit SRP monitor published HDR",0);
                return capture.IsCreated() && output.IsCreated();
            }
            catch { Release();return false; }
        }
        private RenderTexture Target(string name,int depth)
        {
            var target=new RenderTexture(Configuration.width,Configuration.height,depth,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear)
                { name=name,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,antiAliasing=1,useMipMap=false };
            try
            {
                if(!target.Create())throw new InvalidOperationException("SRP monitor target creation failed");
                return target;
            }
            catch
            {
                target.Release();
                if(Application.isPlaying)UnityEngine.Object.Destroy(target);else UnityEngine.Object.DestroyImmediate(target);
                throw;
            }
        }
        private void Release()
        {
            ready=false;
            foreach(var texture in new[]{capture,output})if(texture!=null)
            { texture.Release();if(Application.isPlaying)UnityEngine.Object.Destroy(texture);else UnityEngine.Object.DestroyImmediate(texture); }
            capture=null;output=null;
        }
        public void Dispose()
        {
            if(recording)throw new InvalidOperationException("Cannot dispose SRP monitor while recording");
            if(disposed)return;disposed=true;Release();commands?.Dispose();commands=null;
        }
    }
}
