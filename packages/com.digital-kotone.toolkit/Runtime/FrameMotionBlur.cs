using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit Half4 frame-motion adapter for the existing current-image
    /// blur. Call after DOF, before Bloom. No camera mutation, submission or clock.</summary>
    public sealed class FrameMotionBlur : IDisposable
    {
        public readonly struct Frame
        {
            private readonly FrameMotionBlur owner;
            private readonly ulong sequence;
            public readonly RenderTexture color,guide,protection;
            public readonly float sampleInterval;
            internal Frame(FrameMotionBlur value)
            {owner=value;sequence=value.sequence;color=value.blurFrame.color;guide=value.guide;protection=value.protection;sampleInterval=value.interval;}
            public bool IsCurrent=>owner!=null&&owner.ready&&owner.sequence==sequence&&owner.input.IsCurrent&&owner.Created&&owner.blurFrame.IsCurrent;
        }
        private readonly MotionBlurRenderer renderer=new MotionBlurRenderer();
        private readonly float[] exclusions=new float[256];
        private Material material;
        private Mesh quad;
        private RenderTexture guide,protection;
        private SrpActorForward.Frame input;
        private MotionBlurRenderer.Frame blurFrame;
        private ulong sequence;
        private bool ready,clock,disposed;
        private double previousTime;
        private Vector2 previousJitter;
        private float interval;
        public long NominalTextureBytes {get;private set;}
        private bool Created=>guide!=null&&guide.IsCreated()&&protection!=null&&protection.IsCreated();

        public bool TryRender(SrpActorForward.Frame current,RenderTexture source,RenderTexture preTemporalColor,
            MotionBlurSettings settings,double timeSeconds,Vector2 jitterUv,bool dejittered,int maximumMiB,
            out Frame frame,out string error)
        {
            frame=default;
            if(!PrepareInput(current,source,preTemporalColor,settings,timeSeconds,jitterUv,dejittered,maximumMiB,out var prepared,out error))return false;
            try
            {
                if(!renderer.TryRender(prepared,settings,out blurFrame))return Fail(renderer.UnavailableReason,out error);
                ready=true;frame=new Frame(this);return true;
            }
            catch(Exception exception){return Fail("Frame motion blur failed: "+exception.Message,out error);}
        }

        /// <summary>Produce only the unmixed opaque guide and explicit interval,
        /// without allocating or rendering a blurred color. Borrowed targets are
        /// valid until the next preparation/render/dispose; consume immediately.</summary>
        public bool TryPrepareOpaqueInput(SrpActorForward.Frame current,MotionBlurSettings settings,double timeSeconds,
            int maximumMiB,out MotionBlurInput prepared,out string error)
            =>PrepareInput(current,current.color,current.color,settings,timeSeconds,Vector2.zero,false,maximumMiB,out prepared,out error);

        private bool PrepareInput(SrpActorForward.Frame current,RenderTexture source,RenderTexture preTemporalColor,
            MotionBlurSettings settings,double timeSeconds,Vector2 jitterUv,bool dejittered,int maximumMiB,
            out MotionBlurInput prepared,out string error)
        {
            prepared=default;error=null;ready=false;
            if(disposed||!current.IsCurrent||current.motionDepthIdentity==null||current.sequence<=sequence||
                settings==null||!settings.enabled||!settings.IsValid||double.IsNaN(timeSeconds)||double.IsInfinity(timeSeconds)||
                !MotionBlurSettings.Range(jitterUv.x,-.5f,.5f)||!MotionBlurSettings.Range(jitterUv.y,-.5f,.5f))
                return Fail("Frame motion blur requires fresh motion, explicit finite time/jitter and valid enabled settings",out error);
            int w=current.color.width,h=current.color.height,r=settings.maximumRadiusPixels;
            long bytes=(long)w*h*33+32L*((w+r-1)/r)*((h+r-1)/r);
            if(maximumMiB<1||maximumMiB>2048||bytes>(long)maximumMiB*1048576)return Fail("Frame motion blur texture budget exceeded",out error);
            foreach(var t in new[]{source,preTemporalColor,current.color,current.motionDepthIdentity})
                if(t==null||!t.IsCreated()||t.width!=w||t.height!=h||t.sRGB||t.dimension!=TextureDimension.Tex2D||t.volumeDepth!=1||
                    t.antiAliasing!=1||t.useMipMap||t.useDynamicScale||t.memorylessMode!=RenderTextureMemoryless.None||
                    t==guide||t==protection||renderer.Owns(t))return Fail("Frame motion blur requires matching separate stored linear inputs",out error);
            if(current.motionDepthIdentity.graphicsFormat!=GraphicsFormat.R16G16B16A16_SFloat)
                return Fail("Frame motion blur requires the current packed Half4 motion ABI",out error);
            var active=RenderTexture.active;
            try
            {
                bool continuity=clock&&Created&&guide.width==w&&guide.height==h&&current.SameOwner(input)&&
                    current.sequence==sequence+1&&current.MotionContinuous;
                Allocate(w,h);
                double elapsed=timeSeconds-previousTime;
                interval=continuity&&elapsed>=.000001&&elapsed<=settings.maximumSampleInterval?(float)elapsed:0;
                material.SetTexture("_FrameBlurMotion",current.motionDepthIdentity);
                material.SetTexture("_FrameBlurOpaque",current.color);material.SetTexture("_FrameBlurFx",preTemporalColor);
                material.SetFloat("_FrameBlurCheckFx",preTemporalColor==current.color?0:1);
                current.BindMotionBlurExclusions(material,exclusions);
                using(var commands=new CommandBuffer {name="Toolkit Half4 visible motion blur guide and protection"})
                {
                    commands.SetRenderTarget(new[]{new RenderTargetIdentifier(guide),new RenderTargetIdentifier(protection)},BuiltinRenderTextureType.None);
                    commands.SetViewport(new Rect(0,0,w,h));commands.DrawMesh(quad,Matrix4x4.identity,material,0,0);
                    Graphics.ExecuteCommandBuffer(commands);
                }
                prepared=new MotionBlurInput(source,guide,interval,continuity?jitterUv-previousJitter:Vector2.zero,
                    dejittered?-jitterUv:Vector2.zero,protection);
                input=current;sequence=current.sequence;previousTime=timeSeconds;previousJitter=jitterUv;
                NominalTextureBytes=bytes;clock=true;return true;
            }
            catch(Exception exception){return Fail("Frame motion blur failed: "+exception.Message,out error);}
            finally {RenderTexture.active=active!=null&&active.IsCreated()?active:null;}
        }
        public void ResetHistory(){clock=ready=false;interval=0;}
        private bool Fail(string message,out string error){error=message;ResetHistory();return false;}
        private void Allocate(int w,int h)
        {
            if(SystemInfo.supportedRenderTargetCount<2)throw new InvalidOperationException("Frame motion blur guide requires two MRTs");
            if(material==null)
            {
                var shader=Resources.Load<Shader>("FrameMotionBlurGuide");
                if(shader==null||!shader.isSupported)throw new InvalidOperationException("Frame motion blur guide shader unavailable");
                material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                quad=new Mesh {name="Toolkit explicit frame motion blur guide",hideFlags=HideFlags.HideAndDontSave};
                quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
                quad.triangles=new[]{0,1,2,0,2,3};
            }
            if(Created&&guide.width==w&&guide.height==h)return;
            Release(guide);Release(protection);guide=protection=null;
            guide=Target(w,h,GraphicsFormat.R32G32B32A32_SFloat,"Toolkit frame motion blur current depth guide");
            protection=Target(w,h,GraphicsFormat.R8_UNorm,"Toolkit frame motion blur NoJitter blended and FX protection");
        }
        private static RenderTexture Target(int w,int h,GraphicsFormat format,string name)
        {
            if(!SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))
                throw new InvalidOperationException("Frame motion blur guide format unsupported");
            var t=new RenderTexture(new RenderTextureDescriptor(w,h,format,0)){name=name,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
            if(t.Create()&&t.graphicsFormat==format)return t;Release(t);throw new InvalidOperationException("Frame motion blur guide allocation failed");
        }
        private static void Release(RenderTexture t){if(t==null)return;t.Release();Destroy(t);}
        private static void Destroy(UnityEngine.Object o){if(o==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(o);else UnityEngine.Object.DestroyImmediate(o);}
        public void Dispose()
        {
            if(disposed)return;disposed=true;ResetHistory();renderer.Dispose();Release(guide);Release(protection);
            guide=protection=null;Destroy(material);Destroy(quad);material=null;quad=null;NominalTextureBytes=0;
        }
    }
}
