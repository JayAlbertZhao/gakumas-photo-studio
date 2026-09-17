using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Reprojects an unmixed current opaque color/depth at one explicit
    /// shutter phase. The visible motion and expected previous depth refer to the
    /// SAME current surface. No previous color, hidden-surface recovery or clock.
    /// The caller must compose transparent layers at that same phase before averaging.</summary>
    public sealed class OpaqueExposureRenderer : IDisposable
    {
        public readonly struct Frame
        {
            private readonly OpaqueExposureRenderer owner;
            private readonly uint generation;
            public readonly RenderTexture color,eyeDepth;
            internal Frame(OpaqueExposureRenderer value)
            {owner=value;generation=value.generation;color=value.color;eyeDepth=value.depth;}
            public bool IsCurrent=>owner!=null&&owner.ready&&owner.generation==generation&&owner.Created;
        }
        private RenderTexture color,depth;
        private Material material;
        private Mesh quad;
        private uint generation;
        private bool ready;
        public string UnavailableReason {get;private set;}
        public long TargetBytes {get;private set;}
        public int DrawCalls {get;private set;}
        public int TargetCount=>(color!=null?1:0)+(depth!=null?1:0);
        private bool Created=>color!=null&&depth!=null&&color.IsCreated()&&depth.IsCreated();
        public bool Owns(RenderTexture texture)=>texture!=null&&(texture==color||texture==depth);

        /// <param name="phase">Signed fraction of the endpoint sample interval;
        /// a centered 180 degree shutter spans -0.25 to +0.25.</param>
        /// <param name="currentEyeDepth">Full precision current visible depth.</param>
        /// <param name="expectedPreviousEyeDepth">Previous view depth of the current
        /// surface, NOT a previous-frame screen-space depth texture.</param>
        public bool TryRender(MotionBlurInput input,RenderTexture currentEyeDepth,RenderTexture expectedPreviousEyeDepth,
            float phase,float depthTolerance,bool orthographic,int maximumMiB,out Frame frame)
        {
            frame=default;ready=false;generation++;DrawCalls=0;UnavailableReason=null;
            var source=input.color;var guide=input.motionDepth;
            if(!Valid(source)||source.sRGB||!Valid(guide)||guide.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat||
                (source.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat&&source.graphicsFormat!=GraphicsFormat.R16G16B16A16_SFloat&&source.graphicsFormat!=GraphicsFormat.B10G11R11_UFloatPack32))
                return Fail("Opaque shutter requires separate stored linear HDR and float4 visible motion");
            if(!MotionBlurSettings.Range(phase,-.5f,.5f)||!MotionBlurSettings.Range(depthTolerance,.000001f,10000)||
                !MotionBlurSettings.Range(input.sampleInterval,0,10)||input.jitterDeltaUv!=Vector2.zero||input.colorToGuideUv!=Vector2.zero)
                return Fail("Opaque shutter requires finite phase/depth tolerance and unjittered current coordinates");
            foreach(var t in new[]{guide,currentEyeDepth,expectedPreviousEyeDepth})
                if(!Valid(t)||t.sRGB||t.width!=source.width||t.height!=source.height||Owns(t)||t==source)
                    return Fail("Opaque shutter requires matching nonaliased current motion and endpoint depths");
            if(Owns(source)||currentEyeDepth==expectedPreviousEyeDepth||guide==currentEyeDepth||guide==expectedPreviousEyeDepth||
                currentEyeDepth.graphicsFormat!=GraphicsFormat.R32_SFloat||expectedPreviousEyeDepth.graphicsFormat!=GraphicsFormat.R32_SFloat)
                return Fail("Opaque shutter endpoint depths must be distinct float32 inputs");
            var flags=input.noJitterFlags;
            if(flags!=null&&(!Valid(flags)||flags.width!=source.width||flags.height!=source.height||flags.graphicsFormat!=GraphicsFormat.R8_UNorm||Owns(flags)))
                return Fail("Opaque shutter exclusions require matching stored R8 flags");
            long bytes=(long)source.width*source.height*20;
            if(maximumMiB<1||maximumMiB>2048||bytes>(long)maximumMiB*1048576)return Fail("Opaque shutter owned target budget exceeded");
            var active=RenderTexture.active;
            try
            {
                if(SystemInfo.supportedRenderTargetCount<2)throw new InvalidOperationException("Two MRTs required");
                if(material==null)
                {
                    var shader=Resources.Load<Shader>("OpaqueExposure");
                    if(shader==null||!shader.isSupported)throw new InvalidOperationException("Opaque shutter shader unavailable");
                    material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                    quad=new Mesh {name="Toolkit opaque shutter sample quad",hideFlags=HideFlags.HideAndDontSave};
                    quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
                    quad.triangles=new[]{0,1,2,0,2,3};
                }
                if(!Created||color.width!=source.width||color.height!=source.height)
                {
                    Release(color);Release(depth);color=depth=null;
                    color=Target(source.width,source.height,GraphicsFormat.R32G32B32A32_SFloat,"color");
                    depth=Target(source.width,source.height,GraphicsFormat.R32_SFloat,"eye depth");
                }
                material.SetTexture("_OpaqueColor",source);material.SetTexture("_OpaqueMotion",guide);
                material.SetTexture("_OpaqueDepth",currentEyeDepth);material.SetTexture("_OpaquePreviousDepth",expectedPreviousEyeDepth);
                material.SetTexture("_OpaqueFlags",flags);
                material.SetVector("_OpaqueSample",new Vector4(source.width,source.height,input.sampleInterval>=.000001f?phase:0,depthTolerance));
                material.SetFloat("_OpaqueHasFlags",flags!=null?1:0);
                material.SetFloat("_OpaqueOrthographic",orthographic?1:0);
                using(var commands=new CommandBuffer {name="Toolkit unmixed opaque color and depth shutter sample"})
                {
                    commands.SetRenderTarget(new[]{new RenderTargetIdentifier(color),new RenderTargetIdentifier(depth)},BuiltinRenderTextureType.None);
                    commands.SetViewport(new Rect(0,0,source.width,source.height));commands.DrawMesh(quad,Matrix4x4.identity,material,0,0);
                    Graphics.ExecuteCommandBuffer(commands);
                }
                TargetBytes=bytes;DrawCalls=1;ready=true;frame=new Frame(this);return true;
            }
            catch(Exception e){return Fail("Opaque shutter sample failed: "+e.Message);}
            finally {RenderTexture.active=active!=null&&active.IsCreated()?active:null;}
        }
        private static bool Valid(RenderTexture t)=>t!=null&&t.IsCreated()&&t.dimension==TextureDimension.Tex2D&&t.volumeDepth==1&&
            t.antiAliasing==1&&!t.useMipMap&&!t.useDynamicScale&&t.memorylessMode==RenderTextureMemoryless.None;
        private static RenderTexture Target(int w,int h,GraphicsFormat format,string label)
        {
            if(!SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))
                throw new InvalidOperationException("Opaque shutter target format unavailable");
            var t=new RenderTexture(new RenderTextureDescriptor(w,h,format,0)){name="Toolkit opaque sub-time "+label,filterMode=FilterMode.Point,hideFlags=HideFlags.HideAndDontSave};
            if(t.Create()&&t.graphicsFormat==format)return t;Release(t);throw new InvalidOperationException("Opaque shutter target allocation failed");
        }
        private static void Release(RenderTexture t){if(t==null)return;t.Release();UnityEngine.Object.Destroy(t);}
        private bool Fail(string reason){Dispose();UnavailableReason=reason;return false;}
        public void Dispose()
        {
            ready=false;Release(color);Release(depth);color=depth=null;
            if(material!=null)UnityEngine.Object.Destroy(material);if(quad!=null)UnityEngine.Object.Destroy(quad);material=null;quad=null;
            TargetBytes=0;DrawCalls=0;
        }
    }
}
