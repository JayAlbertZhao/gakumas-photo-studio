using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Camera-independent bokeh module. The caller provides corresponding linear
    /// HDR color and positive eye depth in R, including every relevant surface.
    /// No host depth texture, actor discovery, projection change or global state.
    /// </summary>
    public sealed class BokehDepthOfFieldRenderer : IDisposable
    {
        public readonly struct Frame
        {
            private readonly BokehDepthOfFieldRenderer _owner;
            private readonly uint _generation;
            public readonly RenderTexture color, encodedCoC, prefilterFar, blurredFar, far;
            public readonly RenderTexture prefilterNear, inflatedNear, blurredNear, near;
            internal Frame(BokehDepthOfFieldRenderer owner)
            {
                _owner=owner;_generation=owner._generation;
                var t=owner._targets;color=t[5];encodedCoC=t[0];prefilterFar=t[1];blurredFar=t[2];far=t[4];
                prefilterNear=t[6];inflatedNear=t[8];blurredNear=t[9];near=t[10];
            }
            public bool IsCurrent=>_owner!=null&&_owner._hasFrame&&_owner._generation==_generation&&_owner.Created;
        }
        private readonly RenderTexture[] _targets=new RenderTexture[11];
        private readonly Vector4[] _kernel=new Vector4[BokehKernel.Capacity];
        private Material _material;
        private bool _foreground,_hasFrame;
        private uint _generation;
        public int TargetCount { get; private set; }
        public int DrawCalls { get; private set; }
        public int ColorSamplesPerGather { get; private set; }
        public int MaximumBlurColorSamplesPerPixel=>ColorSamplesPerGather*(_foreground?2:1);
        public string UnavailableReason { get; private set; }
        public bool TryGetFrame(out Frame frame){frame=default;if(!_hasFrame||!Created)return false;frame=new Frame(this);return true;}
        private bool Created
        {
            get {if(TargetCount==0)return false;for(int i=0;i<TargetCount;i++)if(_targets[i]==null||!_targets[i].IsCreated())return false;return true;}
        }

        public bool TryRender(RenderTexture source,RenderTexture linearDepth,BokehDepthOfFieldSettings settings,out Frame frame)
        {
            frame=default;_generation++;_hasFrame=false;DrawCalls=ColorSamplesPerGather=0;UnavailableReason=null;
            if(settings==null||!settings.enabled){Release();return false;}
            if(!settings.IsValid)return Fail("Invalid bokeh configuration");
            if(!Valid(source)||!Valid(linearDepth)||source.width!=linearDepth.width||source.height!=linearDepth.height||
                (source.format!=RenderTextureFormat.ARGBFloat&&source.format!=RenderTextureFormat.ARGBHalf&&source.format!=RenderTextureFormat.RGB111110Float)||
                (linearDepth.format!=RenderTextureFormat.RFloat&&linearDepth.format!=RenderTextureFormat.RHalf)||Owns(source)||Owns(linearDepth))
                return Fail("Bokeh requires distinct matching linear HDR and positive RFloat/RHalf eye-depth targets");
            var shader=Resources.Load<Shader>("BokehDepthOfField");
            if(shader==null||!shader.isSupported||!Supported(GraphicsFormat.R32_SFloat)||!Supported(GraphicsFormat.R32G32B32A32_SFloat))
                return Fail("Bokeh shader and float render/sample formats unavailable");
            var active=RenderTexture.active;
            try
            {
                bool foreground=settings.nearBlur>0;
                if(!Created||_targets[0].width!=source.width||_targets[0].height!=source.height||foreground!=_foreground)
                    Allocate(source.width,source.height,foreground);
                if(_material==null)_material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                if(settings.sampleCount==BokehSampleCount.Samples30)_material.EnableKeyword("BOKEH_30");else _material.DisableKeyword("BOKEH_30");
                BokehKernel.Fill(settings,source.height/(float)source.width,_kernel);ColorSamplesPerGather=(int)settings.sampleCount;
                float focal=settings.focalLengthMillimetres*.001f;
                _material.SetTexture("_LinearDepth",linearDepth);
                _material.SetVector("_SourceSize",new Vector4(source.width,source.height,1f/source.width,1f/source.height));
                _material.SetVector("_CoCParams",new Vector4(settings.focusDistance,0,settings.maximumRadius,source.height/(float)source.width));
                _material.SetVector("_FocusRange",new Vector4(settings.focusNear,settings.focusFar,settings.nearTransition,settings.farTransition));
                _material.SetVector("_LensParameters",new Vector4(focal*focal/(settings.fNumber*(settings.focusDistance-focal)),settings.focusDistance,1/(2*settings.sensorHeightMillimetres*.001f),0));
                _material.SetVector("_FocusControl",new Vector4((int)settings.focusMode,settings.nearBlur,settings.farBlur,0));
                // Thresholds are fractions of image height, not fixed output pixels.
                _material.SetVector("_BokehConstants",new Vector4(2f/1080,4f/1080,1,1));
                _material.SetVectorArray("_BokehKernel",_kernel);
                Draw(source,0,0);_material.SetTexture("_FullCoCTexture",_targets[0]);
                _material.SetTexture("_SourceGatherTexture",source);_material.SetTexture("_CoCGatherTexture",_targets[0]);Draw(source,1,1);
                if(_foreground)
                {
                    Draw(source,6,2);_material.SetTexture("_InflateTexture",_targets[6]);Draw(_targets[6],7,3);
                    _material.SetTexture("_InflateTexture",_targets[7]);Draw(_targets[7],8,4);_material.SetTexture("_InflatedCoCTexture",_targets[8]);
                }
                Draw(_targets[1],2,5);
                if(_foreground)Draw(_targets[6],9,6);
                Draw(_targets[2],3,7);Draw(_targets[3],4,8);
                if(_foreground){Draw(_targets[9],10,9);_material.SetTexture("_DOFFrontTexture",_targets[10]);}
                _material.SetTexture("_DOFBackTexture",_targets[4]);_material.SetTexture("_DOFBackGatherTexture",_targets[4]);
                Draw(source,5,_foreground?11:10);
                _hasFrame=true;frame=new Frame(this);return true;
            }
            catch(Exception exception){return Fail("Bokeh render failed: "+exception.GetType().Name);}
            finally {RenderTexture.active=active!=null&&active.IsCreated()?active:null;}
        }
        private void Draw(RenderTexture source,int target,int pass){Graphics.Blit(source,_targets[target],_material,pass);DrawCalls++;}
        private bool Fail(string reason){Release();UnavailableReason=reason;return false;}
        private bool Owns(RenderTexture texture){foreach(var t in _targets)if(t==texture)return true;return false;}
        private static bool Valid(RenderTexture t)=>t!=null&&t.IsCreated()&&!t.sRGB&&t.antiAliasing==1&&!t.useDynamicScale&&!t.useMipMap&&t.dimension==TextureDimension.Tex2D&&t.volumeDepth==1;
        private static bool Supported(GraphicsFormat f)=>SystemInfo.IsFormatSupported(f,FormatUsage.Render)&&SystemInfo.IsFormatSupported(f,FormatUsage.Sample);
        private void Allocate(int width,int height,bool foreground)
        {
            ReleaseTargets();_foreground=foreground;int halfWidth=Mathf.Max(1,width/2),halfHeight=Mathf.Max(1,height/2);
            var names=new[]{"encoded signed CoC","prefilter far","blur far","flood far","post far","HDR output","prefilter near","inflate near first","inflate near second","blur near","post near"};
            TargetCount=foreground?11:6;
            for(int i=0;i<TargetCount;i++)
            {
                int w=i==0||i==5?width:halfWidth,h=i==0||i==5?height:halfHeight;
                if(i==7){w=Mathf.Max(1,w/2);h=Mathf.Max(1,h/2);}if(i==8){w=Mathf.Max(1,w/4);h=Mathf.Max(1,h/4);}
                bool scalar=i==0||i==7||i==8;var format=scalar?RenderTextureFormat.RFloat:RenderTextureFormat.ARGBFloat;
                var target=new RenderTexture(w,h,0,format,RenderTextureReadWrite.Linear){name="Toolkit bokeh "+names[i],hideFlags=HideFlags.HideAndDontSave,filterMode=scalar?FilterMode.Point:FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
                _targets[i]=target;target.Create();
                if(!target.IsCreated()||target.graphicsFormat!=(scalar?GraphicsFormat.R32_SFloat:GraphicsFormat.R32G32B32A32_SFloat))throw new InvalidOperationException("Bokeh float target allocation failed");
            }
        }
        private void ReleaseTargets(){if(RenderTexture.active!=null&&Owns(RenderTexture.active))RenderTexture.active=null;foreach(var t in _targets)if(t!=null){t.Release();UnityEngine.Object.Destroy(t);}Array.Clear(_targets,0,_targets.Length);TargetCount=0;_hasFrame=false;}
        private void Release(){ReleaseTargets();if(_material!=null)UnityEngine.Object.Destroy(_material);_material=null;_foreground=false;DrawCalls=ColorSamplesPerGather=0;}
        public void Dispose(){_generation++;Release();}
    }
}
