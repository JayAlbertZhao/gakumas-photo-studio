using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit current R32 depth in the temporal color's coordinates.
    /// Keeps the selected surface's full depth precision, not the Half4 guide.
    /// This does not reconstruct transparent layers or infer their depth.</summary>
    public sealed class FrameTemporalDepth : IDisposable
    {
        public readonly struct Frame
        {
            private readonly FrameTemporalDepth owner;
            private readonly ulong sequence;
            public readonly RenderTexture eyeDepth;
            internal Frame(FrameTemporalDepth value){owner=value;sequence=value.sequence;eyeDepth=value.depth;}
            public bool IsCurrent=>owner!=null&&!owner.disposed&&owner.ready&&owner.sequence==sequence&&owner.input.IsCurrent&&owner.Created;
        }
        private RenderTexture depth;
        private Material material;
        private Mesh quad;
        private SrpActorForward.Frame input;
        private ulong sequence;
        private bool disposed,ready;
        private bool Created=>depth!=null&&depth.IsCreated();
        public long NominalTextureBytes=>Created?(long)depth.width*depth.height*4:0;
        public bool TryRender(SrpActorForward.Frame current,Vector2 correctionUv,bool coverageAnchor,int maximumMiB,out Frame frame,out string error)
        {
            frame=default;error=null;ready=false;
            if(disposed||!current.IsCurrent||current.sequence<=sequence||current.motionDepthIdentity==null||
                !MotionBlurSettings.Range(correctionUv.x,-.5f,.5f)||!MotionBlurSettings.Range(correctionUv.y,-.5f,.5f))
            {error="Temporal depth requires a fresh motion frame and finite UV correction";return false;}
            int w=current.color.width,h=current.color.height;
            if(maximumMiB<1||maximumMiB>2048||(long)w*h*4>(long)maximumMiB*1048576)
            {error="Temporal depth texture budget exceeded";return false;}
            foreach(var t in new[]{current.eyeDepth,current.motionDepthIdentity})
                if(t==null||!t.IsCreated()||t.width!=w||t.height!=h||t.sRGB||t.antiAliasing!=1||t.useMipMap||t.useDynamicScale||
                    t.dimension!=TextureDimension.Tex2D||t.volumeDepth!=1||t.memorylessMode!=RenderTextureMemoryless.None||t==depth)
                {error="Temporal depth requires separate matching stored linear inputs";return false;}
            if(current.eyeDepth.graphicsFormat!=GraphicsFormat.R32_SFloat||current.motionDepthIdentity.graphicsFormat!=GraphicsFormat.R16G16B16A16_SFloat)
            {error="Temporal depth requires R32 eye depth and Half4 motion";return false;}
            var active=RenderTexture.active;
            try
            {
                Allocate(w,h);
                if(coverageAnchor)material.EnableKeyword("TOOLKIT_DEPTH_COVERAGE_ANCHOR");else material.DisableKeyword("TOOLKIT_DEPTH_COVERAGE_ANCHOR");
                material.SetTexture("_TemporalRawDepth",current.eyeDepth);material.SetTexture("_TemporalDepthMotion",current.motionDepthIdentity);
                material.SetVector("_TemporalDepthSize",new Vector4(w,h,0,0));material.SetVector("_TemporalDepthCorrection",new Vector4(correctionUv.x,correctionUv.y,0,0));
                using(var commands=new CommandBuffer {name="Toolkit current temporal-coordinate R32 depth"})
                {
                    commands.SetRenderTarget(depth);commands.SetViewport(new Rect(0,0,w,h));commands.DrawMesh(quad,Matrix4x4.identity,material,0,0);
                    Graphics.ExecuteCommandBuffer(commands);
                }
                input=current;sequence=current.sequence;ready=true;frame=new Frame(this);return true;
            }
            catch(Exception exception){error="Temporal depth failed: "+exception.Message;return false;}
            finally {RenderTexture.active=active!=null&&active.IsCreated()?active:null;}
        }
        public void RetireFrame(){ready=false;}
        private void Allocate(int w,int h)
        {
            if(material==null)
            {
                var shader=Resources.Load<Shader>("FrameTemporalDepth");
                if(shader==null||!shader.isSupported||!SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat,FormatUsage.Render)||!SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat,FormatUsage.Sample))
                    throw new InvalidOperationException("Temporal depth shader/format unavailable");
                material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                quad=new Mesh {name="Toolkit explicit temporal depth",hideFlags=HideFlags.HideAndDontSave};
                quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};quad.triangles=new[]{0,1,2,0,2,3};
            }
            if(Created&&depth.width==w&&depth.height==h)return;
            if(depth!=null){depth.Release();Destroy(depth);}
            depth=new RenderTexture(new RenderTextureDescriptor(w,h,GraphicsFormat.R32_SFloat,0))
                {name="Toolkit temporal-coordinate eye depth",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
            if(!depth.Create()||depth.graphicsFormat!=GraphicsFormat.R32_SFloat)throw new InvalidOperationException("Temporal depth allocation failed");
        }
        private static void Destroy(UnityEngine.Object o){if(o==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(o);else UnityEngine.Object.DestroyImmediate(o);}
        public void Dispose(){if(disposed)return;disposed=true;RetireFrame();if(depth!=null){depth.Release();Destroy(depth);}Destroy(material);Destroy(quad);depth=null;material=null;quad=null;}
    }
}
