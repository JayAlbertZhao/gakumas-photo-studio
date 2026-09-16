using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit SSR/Probe consumer of a successfully recorded Tile scene.
    /// Record before actors/transparency and after closing the Tile pass. The host owns
    /// Submit, source-content lifetime and GPU completion before reuse/resize/Dispose.</summary>
    public sealed class SrpTileReflection : IDisposable
    {
        public sealed class Settings
        {
            public bool enabled;
            // Explicit host promise: only scene surfaces were drawn; no indirect specular
            // or actors were added to Color. An exclusion mask alone cannot guarantee this.
            public bool sceneOnlyInput;
            public SceneShaderBackend backend;
            public bool allowComputeFallback = true, useHierarchy = true;
            public float maximumDistance=30, thickness=.12f, normalBias=.025f;
            public int maximumSteps=128, maximumMiB=256;
            public float edgeFade=.04f, historyDepthTolerance=.15f;
            public float cameraCutDistance=2, cameraCutAngle=35, smoothnessThreshold=.5f;
            public SsrRoughnessSettings roughness = new SsrRoughnessSettings();
            public Cubemap probe;
            public bool decodeProbeHdr;
            public Vector4 probeDecode = new Vector4(1,1,0,0);
            public float probeMaximumMip=6, specularScale=1;
            public Vector2 normalDistortion = new Vector2(.1f,-.1f);
        }
        public readonly struct Frame
        {
            private readonly SrpTileReflection owner;
            public readonly ulong sequence;
            public readonly RenderTexture color, rawReflection, reflection, response, radiance, visibility;
            public bool IsCurrent => owner!=null && owner.Current(sequence);
            internal bool Matches(TileSceneRenderer.PreparedFrame scene,ulong value)=>IsCurrent&&sequence==value&&ReferenceEquals(owner.source,scene);
            public int DepthLevelCount => IsCurrent ? owner.depth.Count : 0;
            public RenderTexture GetDepthLevel(int level) => IsCurrent && level>=0 && level<owner.depth.Count ? owner.depth[level] : null;
            internal Frame(SrpTileReflection value)
            {
                owner=value; sequence=value.lastSequence; color=value.output; rawReflection=value.raw;
                reflection=value.filteredActive?value.filtered:value.raw; response=value.response;
                radiance=value.radiance; visibility=value.visibility;
            }
        }
        public Camera Camera { get; }
        public Settings Configuration { get; }
        public SceneShaderBackend ActiveBackend { get; private set; }
        public string BackendFallbackReason { get; private set; }
        public bool UsedHistory { get; private set; }
        public long NominalTextureBytes { get; private set; }
        private readonly List<RenderTexture> depth = new List<RenderTexture>();
        private RenderTexture visibility,raw,horizontal,filtered,response,radiance,output,historyColor,historyDepth;
        private Material utility,trace,reduce,filterX,filterY;
        private ComputeShader compute;
        private CommandBuffer commands;
        private TileSceneRenderer.PreparedFrame source;
        private SrpTilePlanarReflection.Frame? planarSource;
        private ulong lastSequence,revision;
        private Matrix4x4 historyView,historyProjection;
        private bool disposed,ready,history,historyOrtho,filteredActive;
        private int width,height;
        public SrpTileReflection(Camera camera,Settings settings) { Camera=camera; Configuration=settings; }
        public void ResetHistory() { history=false; ready=false; UsedHistory=false; }
        private bool Current(ulong sequence) => !disposed && ready && sequence==lastSequence &&
            Configuration!=null && Configuration.enabled && source!=null && source.IsRecorded && TargetsAlive() &&
            (!planarSource.HasValue || planarSource.Value.Matches(source,sequence));

        /// <summary>One monotonic positive sequence and one fresh Tile ticket per call. Change
        /// sceneRevision on discontinuous topology/content changes; skipped sequences/camera
        /// cuts/resize reset history. This returns recorded work, not completed GPU work.</summary>
        public bool TryRecord(ScriptableRenderContext context,TileSceneRenderer.PreparedFrame scene,
            ulong sequence,ulong sceneRevision,out Frame frame,out string error)
            => Record(context,scene,sequence,sceneRevision,null,out frame,out error);

        /// <summary>Accepts only a current real Planar producer ticket for this exact Tile
        /// scene and sequence. An invalid supplied ticket is rejected, not silently ignored.</summary>
        public bool TryRecord(ScriptableRenderContext context,TileSceneRenderer.PreparedFrame scene,
            ulong sequence,ulong sceneRevision,SrpTilePlanarReflection.Frame planar,out Frame frame,out string error)
            => Record(context,scene,sequence,sceneRevision,planar,out frame,out error);

        private bool Record(ScriptableRenderContext context,TileSceneRenderer.PreparedFrame scene,
            ulong sequence,ulong sceneRevision,SrpTilePlanarReflection.Frame? planar,out Frame frame,out string error)
        {
            frame=default; error=Validate(scene,sequence);
            if(error!=null)return false;
            if(planar.HasValue && !planar.Value.Matches(scene,sequence)) { error="Tile reflection requires the matching current Planar ticket";return false; }
            try
            {
                if(!Allocate(scene.Color.width,scene.Color.height,out error))return false;
                var s=Configuration;
                filteredActive=s.roughness!=null && s.roughness.IsActive;
                Matrix4x4 view=scene.WorldToCamera, projection=scene.GpuProjection;
                var inverse=view.inverse; var oldInverse=historyView.inverse;
                UsedHistory=history && sequence==lastSequence+1 && sceneRevision==revision && historyOrtho==scene.Orthographic &&
                    Vector3.Distance(inverse.MultiplyPoint(Vector3.zero),oldInverse.MultiplyPoint(Vector3.zero))<=s.cameraCutDistance &&
                    Quaternion.Angle(Rotation(inverse),Rotation(oldInverse))<=s.cameraCutAngle && MatrixDistance(projection,historyProjection)<.1f;
                ready=false;
                if(commands==null)commands=new CommandBuffer { name="Toolkit Tile SSR and Probe resolve" };
                commands.Clear();
                utility.SetTexture("_TileDepth",scene.EyeDepth); utility.SetTexture("_TileGeometry",scene.GeometryDepthId);
                utility.SetTexture("_TileNormal",scene.NormalIdentity); utility.SetTexture("_TileBase",scene.MaterialBase);
                utility.SetTexture("_TileMos",scene.MaterialMos);
                utility.SetVector("_TileSize",new Vector4(width,height,1f/width,1f/height));
                utility.SetVector("_TileOptions",new Vector4(scene.FarClip,s.smoothnessThreshold,scene.Orthographic?1:0,s.specularScale));
                utility.SetMatrix("_TileInverseProjection",projection.inverse); utility.SetMatrix("_TileInverseView",inverse);
                utility.SetMatrix("_TileView",view);
                utility.SetVector("_TileDistortion",s.normalDistortion);
                utility.SetTexture("_TileProbe",s.probe);
                utility.SetVector("_TileProbeOptions",new Vector4(s.probe!=null?1:0,s.decodeProbeHdr?1:0,
                    s.probe!=null?Mathf.Min(s.probeMaximumMip,s.probe.mipmapCount-1):0,0));
                utility.SetVector("_TileProbeDecode",s.probeDecode);
                Texture planarTexture=planar.HasValue?(Texture)planar.Value.reflection:Texture2D.blackTexture;
                utility.SetTexture("_TilePlanar",planarTexture);utility.SetFloat("_TilePlanarAvailable",planar.HasValue?1:0);
                // Tile background is zero eye depth; the SSR min hierarchy requires far.
                commands.Blit(scene.EyeDepth,depth[0],utility,0);
                for(int level=1;level<depth.Count;level++)commands.Blit(depth[level-1],depth[level],reduce,1);
                commands.Blit(scene.MaterialMos,visibility,utility,1);
                trace.SetTexture("_SsrNormalMask",scene.GeometryDepthId); trace.SetTexture("_SsrVisibility",visibility);
                trace.SetTexture("_SsrPlanarCoverage",planarTexture); trace.SetFloat("_SsrPlanarAvailable",planar.HasValue?1:0);
                trace.SetTexture("_SsrHistoryColor",historyColor); trace.SetTexture("_SsrHistoryDepth",historyDepth);
                for(int level=0;level<15;level++)trace.SetTexture("_SsrDepth"+level,depth[Mathf.Min(level,depth.Count-1)]);
                trace.SetMatrix("_SsrInverseProjection",projection.inverse); trace.SetMatrix("_SsrProjection",projection);
                trace.SetMatrix("_SsrView",view); trace.SetMatrix("_SsrInverseView",inverse);
                trace.SetMatrix("_SsrHistoryView",historyView); trace.SetMatrix("_SsrHistoryViewProjection",historyProjection*historyView);
                trace.SetVector("_SsrSize",new Vector4(width,height,1f/width,1f/height));
                trace.SetVector("_SsrTrace",new Vector4(s.maximumDistance,s.thickness,s.normalBias,s.maximumSteps));
                trace.SetVector("_SsrFrame",new Vector4(scene.FarClip,scene.NearClip,scene.Orthographic?1:0,s.useHierarchy?depth.Count-1:0));
                trace.SetVector("_SsrHistory",new Vector4(UsedHistory?1:0,s.historyDepthTolerance,s.edgeFade,1));
                if(ActiveBackend==SceneShaderBackend.Compute) Dispatch("TraceReflections",raw);
                else commands.Blit(scene.Color,raw,trace,1);
                if(filteredActive)
                {
                    var r=s.roughness; trace.SetVector("_SsrFilterOptions",new Vector4(r.RadiusAtHeight(height),r.planeTolerance,r.normalThreshold,r.smoothnessTolerance));
                    Filter(raw,horizontal,new Vector4(1,0,0,0)); Filter(horizontal,filtered,new Vector4(0,1,0,0));
                }
                utility.SetTexture("_TileReflection",filteredActive?filtered:raw);
                commands.Blit(scene.Color,response,utility,2);
                commands.Blit(scene.Color,radiance,utility,3);
                utility.SetTexture("_TileResponse",response); utility.SetTexture("_TileRadiance",radiance);
                commands.Blit(scene.Color,output,utility,4);
                // Never copy the resolved output: no recursive reflections or character history.
                commands.Blit(scene.Color,historyColor); commands.CopyTexture(depth[0],historyDepth);
                context.ExecuteCommandBuffer(commands); commands.Clear();
                source=scene; planarSource=planar; lastSequence=sequence; revision=sceneRevision; history=true; ready=true;
                historyView=view; historyProjection=projection; historyOrtho=scene.Orthographic;
                frame=new Frame(this); return true;
            }
            catch(Exception exception)
            {
                // Keep resources alive if work partially entered the caller's context.
                ResetHistory(); error="Tile reflections failed: "+exception.Message; return false;
            }
            finally { commands?.Clear(); }
        }
        private void Filter(RenderTexture input,RenderTexture target,Vector4 axis)
        {
            trace.SetTexture("_SsrFilterInput",input); trace.SetVector("_SsrFilterAxis",axis);
            if(ActiveBackend==SceneShaderBackend.Compute)Dispatch("FilterRoughness",target);
            else
            {
                // Commands retain material references. Two queued raster passes must not
                // see the second pass's axis/source (or sample their own destination).
                var material=axis.x>0?filterX:filterY;
                material.CopyPropertiesFromMaterial(trace); commands.Blit(input,target,material,3);
            }
        }
        private void Dispatch(string name,RenderTexture target)
        {
            int kernel=compute.FindKernel(name);
            foreach(string texture in new[]{"_SsrNormalMask","_SsrVisibility","_SsrPlanarCoverage","_SsrHistoryColor","_SsrHistoryDepth","_SsrFilterInput"})
                commands.SetComputeTextureParam(compute,kernel,texture,trace.GetTexture(texture)??Texture2D.blackTexture);
            for(int level=0;level<15;level++)commands.SetComputeTextureParam(compute,kernel,"_SsrDepth"+level,depth[Mathf.Min(level,depth.Count-1)]);
            foreach(string matrix in new[]{"_SsrInverseProjection","_SsrProjection","_SsrView","_SsrInverseView","_SsrHistoryView","_SsrHistoryViewProjection"})
                commands.SetComputeMatrixParam(compute,matrix,trace.GetMatrix(matrix));
            var vectors=name=="FilterRoughness"?new[]{"_SsrSize","_SsrFilterOptions","_SsrFilterAxis"}:
                new[]{"_SsrSize","_SsrTrace","_SsrFrame","_SsrHistory"};
            foreach(string vector in vectors)
                commands.SetComputeVectorParam(compute,vector,trace.GetVector(vector));
            commands.SetComputeFloatParam(compute,"_SsrPlanarAvailable",trace.GetFloat("_SsrPlanarAvailable"));
            commands.SetComputeTextureParam(compute,kernel,"_SsrOutput",target);
            commands.DispatchCompute(compute,kernel,(width+7)/8,(height+7)/8,1);
        }
        private string Validate(TileSceneRenderer.PreparedFrame scene,ulong sequence)
        {
            var s=Configuration;
            if(disposed || s==null || !s.enabled)return "Tile reflections disabled or disposed";
            if(!s.sceneOnlyInput)return "Declare scene-only input before actors and indirect specular";
            if(GraphicsSettings.currentRenderPipeline==null || Camera==null || scene==null || !scene.SceneContentAvailable || scene.Camera!=Camera)
                return "Tile reflections require the matching successfully recorded SRP scene";
            if(QualitySettings.activeColorSpace!=ColorSpace.Linear || Camera.stereoEnabled || Camera.allowDynamicResolution || Camera.rect!=new Rect(0,0,1,1))
                return "Tile reflections require Linear, fixed-size full viewport without XR";
            if(sequence==0 || sequence<=lastSequence || ReferenceEquals(scene,source))return "Tile reflection requires a fresh scene and monotonic positive sequence";
            if(!Range(scene.NearClip,.001f,10000) || !Range(scene.FarClip,scene.NearClip+.001f,1000000))return "Invalid reflection clipping planes";
            if(!Range(s.maximumDistance,.01f,10000) || !Range(s.thickness,.001f,100) || !Range(s.normalBias,.001f,10) ||
                s.maximumSteps<8 || s.maximumSteps>512 || !Range(s.edgeFade,0,.25f) || !Range(s.historyDepthTolerance,.001f,100) ||
                !Range(s.cameraCutDistance,.01f,10000) || !Range(s.cameraCutAngle,1,180) || !Range(s.smoothnessThreshold,0,1) ||
                !Range(s.specularScale,0,4) || !Range(s.probeMaximumMip,0,16) || !Range(s.normalDistortion.x,-1,1) ||
                !Range(s.normalDistortion.y,-1,1) || (s.roughness!=null && !s.roughness.IsValid) || s.maximumMiB<1 || s.maximumMiB>512)
                return "Invalid Tile reflection settings";
            for(int c=0;c<4;c++)if(!Range(s.probeDecode[c],-65504,65504))return "Invalid reflection probe decode";
            int w=scene.Color.width,h=scene.Color.height;
            if(w<1 || h<1 || w>4096 || h>4096 || EstimateBytes(w,h)>(long)s.maximumMiB*1048576)return "Tile reflection texture budget exceeded";
            var inputs=new[]{scene.Color,scene.EyeDepth,scene.GeometryDepthId,scene.NormalIdentity,scene.MaterialBase,scene.MaterialMos};
            var formats=new[]{GraphicsFormat.B10G11R11_UFloatPack32,GraphicsFormat.R32_SFloat,GraphicsFormat.R8G8B8A8_UNorm,
                GraphicsFormat.R16G16B16A16_SFloat,GraphicsFormat.R8G8B8A8_SRGB,GraphicsFormat.R8G8B8A8_UNorm};
            for(int i=0;i<inputs.Length;i++)
            {
                var t=inputs[i];
                if(t==null || !t.IsCreated() || t.width!=w || t.height!=h || t.graphicsFormat!=formats[i] || t.antiAliasing!=1 ||
                    t.dimension!=TextureDimension.Tex2D || t.useDynamicScale || Owns(t))return "Tile reflections require separate stored current depth/geometry/normal/base/MOS targets";
                for(int j=0;j<i;j++)if(t==inputs[j])return "Tile reflection input alias";
            }
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11 && SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Vulkan)
                return "Tile reflection currently supports desktop D3D11/Vulkan";
            if(!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat) ||
                (SystemInfo.copyTextureSupport&CopyTextureSupport.Basic)==0)return "Tile reflection target/copy capability unavailable";
            return null;
        }
        private bool Allocate(int w,int h,out string error)
        {
            error=null;
            if(utility==null)utility=Material("TileReflection");
            if(trace==null)trace=Material("ScreenSpaceReflection");
            if(reduce==null)reduce=Material("SceneDepthData");
            if(filterX==null)filterX=Material("ScreenSpaceReflection");
            if(filterY==null)filterY=Material("ScreenSpaceReflection");
            if(utility==null || trace==null || reduce==null || filterX==null || filterY==null) { error="Tile reflection shaders unavailable";return false; }
            if(compute==null) { var asset=Resources.Load<ComputeShader>("ScreenSpaceReflection"); if(asset!=null)compute=UnityEngine.Object.Instantiate(asset); }
            var s=Configuration;
            if(!SceneComputeSupport.Select(s.backend,s.allowComputeFallback,compute,"TraceReflections",RenderTextureFormat.ARGBHalf,
                out var selected,out _,out var fallback,out error))return false;
            BackendFallbackReason=fallback;
            if(selected==SceneShaderBackend.Compute && (s.roughness!=null && s.roughness.IsActive) &&
                !SceneComputeSupport.Select(selected,s.allowComputeFallback,compute,"FilterRoughness",RenderTextureFormat.ARGBHalf,out selected,out _,out fallback,out error))return false;
            if(fallback!=null)BackendFallbackReason=fallback;
            if(width==w && height==h && selected==ActiveBackend && TargetsAlive())return true;
            ReleaseTargets(); width=w; height=h; ActiveBackend=selected;
            for(int x=w,y=h;;x=(x+1)/2,y=(y+1)/2)
            { depth.Add(Target(x,y,"min depth "+depth.Count,RenderTextureFormat.RFloat)); if(x==1&&y==1)break; }
            visibility=Target(w,h,"receiver metadata"); response=Target(w,h,"material response"); radiance=Target(w,h,"resolved radiance");
            output=Target(w,h,"composite"); historyColor=Target(w,h,"actor-free history"); historyDepth=Target(w,h,"history depth",RenderTextureFormat.RFloat);
            raw=Target(w,h,"raw trace",RenderTextureFormat.ARGBHalf,selected==SceneShaderBackend.Compute);
            horizontal=Target(w,h,"horizontal filter",RenderTextureFormat.ARGBHalf,selected==SceneShaderBackend.Compute);
            filtered=Target(w,h,"roughness filter",RenderTextureFormat.ARGBHalf,selected==SceneShaderBackend.Compute);
            NominalTextureBytes=EstimateBytes(w,h); return true;
        }
        private static long EstimateBytes(int w,int h)
        {
            long bytes=(long)w*h*(8*8+4);
            for(int x=w,y=h;;x=(x+1)/2,y=(y+1)/2) { bytes+=(long)x*y*4; if(x==1&&y==1)return bytes; }
        }
        private IEnumerable<RenderTexture> Targets()
        {
            foreach(var t in depth)yield return t;
            foreach(var t in new[]{visibility,raw,horizontal,filtered,response,radiance,output,historyColor,historyDepth})yield return t;
        }
        private bool TargetsAlive() { if(depth.Count==0)return false; foreach(var t in Targets())if(t==null || !t.IsCreated())return false; return true; }
        private bool Owns(Texture value) { foreach(var t in Targets())if(value!=null && value==t)return true; return false; }
        private static RenderTexture Target(int w,int h,string name,RenderTextureFormat format=RenderTextureFormat.ARGBHalf,bool random=false)
        {
            var t=new RenderTexture(w,h,0,format,RenderTextureReadWrite.Linear) { name="Toolkit Tile reflection "+name,
                hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,enableRandomWrite=random };
            if(t.Create())return t; Destroy(t); throw new InvalidOperationException("Reflection target allocation failed");
        }
        private static Material Material(string resource)
        { var shader=Resources.Load<Shader>(resource); return shader!=null&&shader.isSupported?new Material(shader) { hideFlags=HideFlags.HideAndDontSave }:null; }
        private void ReleaseTargets()
        {
            ResetHistory(); foreach(var t in Targets())if(t!=null) { t.Release(); Destroy(t); }
            depth.Clear(); visibility=raw=horizontal=filtered=response=radiance=output=historyColor=historyDepth=null; NominalTextureBytes=0;
        }
        public void Dispose()
        { if(disposed)return; disposed=true; ReleaseTargets(); commands?.Release(); commands=null; Destroy(utility); Destroy(trace); Destroy(reduce); Destroy(filterX); Destroy(filterY); Destroy(compute); source=null; }
        private static void Destroy(UnityEngine.Object value) { if(value==null)return; if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value); }
        private static bool Range(float v,float min,float max) => !float.IsNaN(v) && v>=min && v<=max;
        private static Quaternion Rotation(Matrix4x4 inverse) => Quaternion.LookRotation(inverse.MultiplyVector(Vector3.back),inverse.MultiplyVector(Vector3.up));
        private static float MatrixDistance(Matrix4x4 a,Matrix4x4 b) { float d=0;for(int i=0;i<16;i++)d=Mathf.Max(d,Mathf.Abs(a[i]-b[i]));return d; }
    }
}
