using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit post-transparency temporal consumer of the current full
    /// Actor motion attachments. Untracked scene pixels pass through. No camera
    /// mutation, Submit or implicit clock. The host resets on seeks/cuts.</summary>
    public sealed class FrameTemporalAntialiasing : IDisposable
    {
        [Serializable]
        public sealed class Settings
        {
            public bool enabled;
            [Range(0,.99f)] public float historyWeight=.95f;
            [Range(2,64)] public int maximumHistory=32;
            [Min(.000001f)] public float depthTolerance=.02f;
            [Range(0,4)] public float varianceGamma=.9f;
            [Range(0,1)] public float reactiveThreshold=.2f;
            // Texture UV correction; the host owns the applied projection jitter.
            public Vector2 jitterUv;
            public uint contentRevision;
            internal bool IsValid=>Range(historyWeight,0,.99f)&&maximumHistory>=2&&maximumHistory<=64&&
                Range(depthTolerance,.000001f,10000)&&Range(varianceGamma,0,4)&&Range(reactiveThreshold,0,1)&&
                Range(jitterUv.x,-.5f,.5f)&&Range(jitterUv.y,-.5f,.5f);
            private static bool Range(float v,float lo,float hi)=>!float.IsNaN(v)&&!float.IsInfinity(v)&&v>=lo&&v<=hi;
        }
        public readonly struct Frame
        {
            private readonly FrameTemporalAntialiasing owner;
            public readonly ulong sequence;
            public readonly RenderTexture color,metadata;
            internal Frame(FrameTemporalAntialiasing value)
            {owner=value;sequence=value.sequence;color=value.colors[value.read];metadata=value.guides[value.read];}
            public bool IsCurrent=>owner!=null&&owner.Current(sequence);
        }
        private readonly RenderTexture[] colors=new RenderTexture[2],guides=new RenderTexture[2];
        private Material material;
        private Mesh quad;
        private SrpActorForward.Frame sourceFrame;
        private ulong sequence;
        private int read;
        private Vector2 previousJitter;
        private Vector4 previousSettings;
        private Vector2 previousRejection;
        private uint revision;
        private bool disposed,ready,history;
        public long NominalTextureBytes {get;private set;}
        private bool Created=>colors[0]!=null&&colors[0].IsCreated()&&colors[1]!=null&&colors[1].IsCreated()&&
            guides[0]!=null&&guides[0].IsCreated()&&guides[1]!=null&&guides[1].IsCreated();
        private bool Current(ulong value)=>!disposed&&ready&&value==sequence&&sourceFrame.IsCurrent&&Created;

        public bool TryRender(SrpActorForward.Frame input,RenderTexture source,Settings settings,
            int maximumMiB,out Frame frame,out string error)
        {
            frame=default;error=null;
            if(disposed||settings==null||!settings.enabled||!settings.IsValid||!input.IsCurrent||
                input.motionDepthIdentity==null||input.expectedPreviousDepth==null||input.sequence<=sequence)
            {error="Temporal resolve requires a fresh current motion frame and valid enabled settings";return false;}
            int width=input.color.width,height=input.color.height;
            if(maximumMiB<1||maximumMiB>2048||(long)width*height*64>(long)maximumMiB*1048576)
            {error="Temporal history texture budget exceeded";return false;}
            foreach(var texture in new[]{source,input.color,input.motionDepthIdentity,input.expectedPreviousDepth})
                if(texture==null||!texture.IsCreated()||texture.width!=width||texture.height!=height||texture.sRGB||
                    texture.antiAliasing!=1||texture.dimension!=TextureDimension.Tex2D||texture.useDynamicScale||
                    texture.memorylessMode!=RenderTextureMemoryless.None||Owns(texture))
                {error="Temporal resolve requires separate matching stored linear inputs";return false;}
            if(input.motionDepthIdentity.graphicsFormat!=GraphicsFormat.R16G16B16A16_SFloat||input.expectedPreviousDepth.graphicsFormat!=GraphicsFormat.R32_SFloat)
            {error="Temporal motion format mismatch";return false;}
            var config=new Vector4(settings.historyWeight,settings.maximumHistory,settings.varianceGamma,0);
            var rejection=new Vector2(settings.depthTolerance,settings.reactiveThreshold);
            try
            {
                Allocate(width,height);
                bool continuous=history&&input.sequence==sequence+1&&revision==settings.contentRevision&&config==previousSettings&&rejection==previousRejection;
                ready=false;int write=1-read;var m=material;
                m.SetTexture("_TemporalCurrent",source);m.SetTexture("_TemporalOpaque",input.color);
                m.SetTexture("_TemporalMotion",input.motionDepthIdentity);m.SetTexture("_TemporalPreviousDepth",input.expectedPreviousDepth);
                m.SetTexture("_TemporalHistory",colors[read]);m.SetTexture("_TemporalGuide",guides[read]);
                m.SetVector("_TemporalSize",new Vector4(width,height,1f/width,1f/height));
                m.SetVector("_TemporalHistorySettings",new Vector4(continuous?1:0,settings.historyWeight,settings.maximumHistory,settings.varianceGamma));
                m.SetVector("_TemporalRejection",new Vector4(settings.depthTolerance,settings.reactiveThreshold,source==input.color?0:1,0));
                m.SetVector("_TemporalJitter",new Vector4(settings.jitterUv.x,settings.jitterUv.y,previousJitter.x,previousJitter.y));
                var commands=new CommandBuffer {name="Toolkit current Half4 motion variance temporal resolve"};
                var active=RenderTexture.active;
                try
                {
                    commands.SetRenderTarget(new[]{new RenderTargetIdentifier(colors[write]),new RenderTargetIdentifier(guides[write])},BuiltinRenderTextureType.None);
                    commands.SetViewport(new Rect(0,0,width,height));commands.DrawMesh(quad,Matrix4x4.identity,m,0,0);
                    Graphics.ExecuteCommandBuffer(commands);
                }
                finally {RenderTexture.active=active;commands.Release();}
                read=write;sourceFrame=input;sequence=input.sequence;previousJitter=settings.jitterUv;
                previousSettings=config;previousRejection=rejection;revision=settings.contentRevision;history=ready=true;
                frame=new Frame(this);return true;
            }
            catch(Exception exception){ResetHistory();error="Temporal resolve failed: "+exception.Message;return false;}
        }
        public void ResetHistory(){ready=history=false;}
        private bool Owns(RenderTexture t){foreach(var a in new[]{colors,guides})foreach(var own in a)if(t==own)return true;return false;}
        private void Allocate(int width,int height)
        {
            if(SystemInfo.supportedRenderTargetCount<2)throw new InvalidOperationException("Temporal resolve requires two MRTs");
            if(material==null)
            {
                var shader=Resources.Load<Shader>("FrameTemporalAntialiasing");
                if(shader==null||!shader.isSupported)throw new InvalidOperationException("Temporal shader unavailable");
                material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                quad=new Mesh {name="Toolkit explicit temporal fullscreen",hideFlags=HideFlags.HideAndDontSave};
                quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
                quad.triangles=new[]{0,1,2,0,2,3};
            }
            if(Created&&colors[0].width==width&&colors[0].height==height)return;
            ReleaseTargets();
            foreach(var a in new[]{colors,guides})for(int i=0;i<2;i++)
            {
                var t=new RenderTexture(new RenderTextureDescriptor(width,height,GraphicsFormat.R32G32B32A32_SFloat,0))
                    {name=a==colors?"Toolkit temporal HDR history":"Toolkit temporal depth identity age weight",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
                a[i]=t;if(!t.Create()||t.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat)throw new InvalidOperationException("Exact temporal history format unavailable");
            }
            NominalTextureBytes=(long)width*height*64;
        }
        private void ReleaseTargets(){ResetHistory();foreach(var a in new[]{colors,guides})for(int i=0;i<2;i++){if(a[i]!=null){a[i].Release();Destroy(a[i]);a[i]=null;}}NominalTextureBytes=0;}
        private static void Destroy(UnityEngine.Object o){if(o==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(o);else UnityEngine.Object.DestroyImmediate(o);}
        public void Dispose(){if(disposed)return;disposed=true;ReleaseTargets();Destroy(material);Destroy(quad);material=null;quad=null;}
    }
}
