using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Authored positive-light diffusion, not a recovered game kernel.
    /// Explicit linear HDR input; no camera, scene discovery or captured profile.</summary>
    public sealed class DiffusionRenderer : IDisposable
    {
        [Serializable] public sealed class Settings
        {
            public bool enabled;
            [Range(0,1)] public float intensity=.2f;
            // Distance between binomial taps, measured in full output pixels.
            [Range(0,64)] public float radiusPixels=4;
            [Range(1,4)] public int downsample=2;
            public int maximumMiB=128;
            public bool IsValid=>MotionBlurSettings.Range(intensity,0,1)&&MotionBlurSettings.Range(radiusPixels,0,64)&&
                downsample>=1&&downsample<=4&&maximumMiB>=1&&maximumMiB<=2048;
            public long EstimateTargetBytes(int width,int height)=>width<1||height<1||!IsValid?0:
                16L*((long)width*height+3L*(((long)width+downsample-1)/downsample)*(((long)height+downsample-1)/downsample));
        }
        public readonly struct Frame
        {
            private readonly DiffusionRenderer owner;
            private readonly uint generation;
            public readonly RenderTexture color,blurred;
            internal Frame(DiffusionRenderer value){owner=value;generation=value.generation;color=value.output;blurred=value.vertical;}
            public bool IsCurrent=>owner!=null&&owner.ready&&owner.generation==generation&&owner.Created;
        }
        private RenderTexture reduced,horizontal,vertical,output;
        private Material material;
        private Mesh quad;
        private uint generation;
        private bool ready,disposed;
        public string UnavailableReason {get;private set;}
        public int DrawCalls {get;private set;}
        public long NominalTextureBytes=>Bytes(reduced)+Bytes(horizontal)+Bytes(vertical)+Bytes(output);
        private static long Bytes(RenderTexture t)=>Live(t)?16L*t.width*t.height:0;
        private bool Created=>Live(reduced)&&Live(horizontal)&&Live(vertical)&&Live(output);
        public bool Owns(RenderTexture t)=>t!=null&&(t==reduced||t==horizontal||t==vertical||t==output);
        public bool TryRender(RenderTexture source,Settings settings,out Frame frame)
        {
            generation++;ready=false;DrawCalls=0;UnavailableReason=null;frame=default;
            if(disposed||settings==null||!settings.enabled||!settings.IsValid)return Fail("Enabled authored diffusion settings required");
            if(!Live(source)||source.sRGB||source.antiAliasing!=1||source.volumeDepth!=1||source.dimension!=TextureDimension.Tex2D||
                source.useMipMap||source.useDynamicScale||source.memorylessMode!=RenderTextureMemoryless.None||Owns(source)||
                (source.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat&&source.graphicsFormat!=GraphicsFormat.R16G16B16A16_SFloat&&source.graphicsFormat!=GraphicsFormat.B10G11R11_UFloatPack32))
                return Fail("Diffusion requires an external stored linear HDR texture without MSAA or mipmaps");
            if(settings.EstimateTargetBytes(source.width,source.height)>settings.maximumMiB*1048576L)return Fail("Diffusion texture budget exceeded");
            var active=RenderTexture.active;
            try
            {
                int w=(source.width+settings.downsample-1)/settings.downsample,h=(source.height+settings.downsample-1)/settings.downsample;
                Allocate(source.width,source.height,w,h);
                using(var command=new CommandBuffer {name="Toolkit authored HDR diffusion after reconstruction"})
                {
                    void Draw(RenderTexture input,RenderTexture target,int pass,Vector2 step)
                    {
                        var block=new MaterialPropertyBlock();block.SetTexture("_DiffusionSource",input);
                        block.SetVector("_DiffusionSourceSize",new Vector4(input.width,input.height,0,0));
                        block.SetVector("_DiffusionTargetSize",new Vector4(target.width,target.height,0,0));
                        block.SetVector("_DiffusionStep",new Vector4(step.x,step.y,0,0));
                        block.SetFloat("_DiffusionIntensity",settings.intensity);
                        if(pass==2){block.SetTexture("_DiffusionBlur",vertical);block.SetVector("_DiffusionBlurSize",new Vector4(w,h,0,0));}
                        command.SetRenderTarget(target);command.SetViewport(new Rect(0,0,target.width,target.height));
                        command.DrawMesh(quad,Matrix4x4.identity,material,0,pass,block);DrawCalls++;
                    }
                    Draw(source,reduced,0,Vector2.zero);
                    Draw(reduced,horizontal,1,new Vector2(settings.radiusPixels/source.width,0));
                    Draw(horizontal,vertical,1,new Vector2(0,settings.radiusPixels/source.height));
                    Draw(source,output,2,Vector2.zero);Graphics.ExecuteCommandBuffer(command);
                }
                ready=true;frame=new Frame(this);return true;
            }
            catch(Exception exception){return Fail("Diffusion failed: "+exception.Message);}
            finally {RenderTexture.active=Live(active)?active:null;}
        }
        private bool Fail(string message){UnavailableReason=message;return false;}
        internal void RetireFrame(){generation++;ready=false;}
        private void Allocate(int width,int height,int w,int h)
        {
            var format=GraphicsFormat.R32G32B32A32_SFloat;
            if(!SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))throw new InvalidOperationException("Diffusion float targets unavailable");
            if(material==null)
            {
                var shader=Resources.Load<Shader>("AuthoredDiffusion");if(shader==null||!shader.isSupported)throw new InvalidOperationException("Diffusion shader unavailable");
                material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};quad=new Mesh {name="Toolkit explicit diffusion fullscreen",hideFlags=HideFlags.HideAndDontSave};
                quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};quad.triangles=new[]{0,1,2,0,2,3};
            }
            if(Created&&output.width==width&&output.height==height&&reduced.width==w&&reduced.height==h)return;
            ReleaseTargets();output=Target(width,height,"Toolkit diffusion composed HDR");
            reduced=Target(w,h,"Toolkit diffusion reduced");horizontal=Target(w,h,"Toolkit diffusion horizontal");vertical=Target(w,h,"Toolkit diffusion vertical");
        }
        private static bool Live(RenderTexture t)=>t!=null&&t.IsCreated();
        private static RenderTexture Target(int w,int h,string name)
        {
            var t=new RenderTexture(new RenderTextureDescriptor(w,h,GraphicsFormat.R32G32B32A32_SFloat,0)){name=name,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave};
            if(t.Create()&&t.graphicsFormat==GraphicsFormat.R32G32B32A32_SFloat)return t;Release(t);throw new InvalidOperationException("Diffusion exact target format unavailable");
        }
        private static void Release(RenderTexture t){if(t==null)return;t.Release();Destroy(t);}
        private static void Destroy(UnityEngine.Object o){if(o==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(o);else UnityEngine.Object.DestroyImmediate(o);}
        private void ReleaseTargets(){Release(reduced);Release(horizontal);Release(vertical);Release(output);reduced=horizontal=vertical=output=null;}
        public void Dispose(){if(disposed)return;disposed=true;generation++;ready=false;ReleaseTargets();Destroy(material);Destroy(quad);material=null;quad=null;DrawCalls=0;}
    }
}
