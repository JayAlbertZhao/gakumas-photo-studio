using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Authored HDR bloom with explicit sampling, normalized pyramid
    /// scatter and no captured profiles, camera, time or application dependency.</summary>
    public sealed class BloomRenderer : IDisposable
    {
        [Serializable] public sealed class Settings
        {
            public bool enabled;
            [Min(0)] public float threshold=1;
            [Range(0,1)] public float softKnee=.5f,scatter=.7f;
            [Range(0,64)] public float intensity=.1f;
            [Range(1,10)] public int maximumLevels=6;
            public int maximumMiB=128;
            public bool IsValid=>MotionBlurSettings.Range(threshold,0,65504)&&MotionBlurSettings.Range(softKnee,0,1)&&
                MotionBlurSettings.Range(scatter,0,1)&&MotionBlurSettings.Range(intensity,0,64)&&maximumLevels>=1&&maximumLevels<=10&&maximumMiB>=1&&maximumMiB<=2048;
        }
        public readonly struct Frame
        {
            private readonly BloomRenderer owner;
            private readonly uint generation;
            public readonly RenderTexture color,bloom;
            internal Frame(BloomRenderer value){owner=value;generation=value.generation;color=value.output;bloom=value.Result;}
            public bool IsCurrent=>owner!=null&&owner.ready&&owner.generation==generation&&owner.Created;
        }
        private readonly List<RenderTexture> down=new List<RenderTexture>(),up=new List<RenderTexture>();
        private RenderTexture output;
        private Material material;
        private Mesh quad;
        private uint generation;
        private bool ready,disposed;
        private RenderTexture Result=>up.Count>0?up[0]:down.Count>0?down[0]:null;
        private bool Created
        {get {if(!Live(output)||down.Count==0)return false;foreach(var t in down)if(!Live(t))return false;foreach(var t in up)if(!Live(t))return false;return true;}}
        public string UnavailableReason {get;private set;}
        public long NominalTextureBytes
        {get {long bytes=Live(output)?16L*output.width*output.height:0;foreach(var t in down)if(Live(t))bytes+=16L*t.width*t.height;foreach(var t in up)if(Live(t))bytes+=16L*t.width*t.height;return bytes;}}
        public int DrawCalls {get;private set;}
        public int LevelCount=>down.Count;
        public bool Owns(RenderTexture t)=>t!=null&&(t==output||down.Contains(t)||up.Contains(t));
        public bool TryRender(RenderTexture source,Settings settings,out Frame frame)
        {
            generation++;ready=false;DrawCalls=0;UnavailableReason=null;frame=default;
            if(disposed||settings==null||!settings.enabled||!settings.IsValid)return Fail("Enabled authored bloom settings required");
            if(!Live(source)||source.sRGB||source.antiAliasing!=1||source.volumeDepth!=1||source.dimension!=TextureDimension.Tex2D||
                source.useMipMap||source.useDynamicScale||source.memorylessMode!=RenderTextureMemoryless.None||Owns(source)||
                (source.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat&&source.graphicsFormat!=GraphicsFormat.R16G16B16A16_SFloat&&source.graphicsFormat!=GraphicsFormat.B10G11R11_UFloatPack32))
                return Fail("Bloom requires an external stored linear HDR texture without MSAA or mipmaps");
            var sizes=new List<Vector2Int>();int w=Math.Max(1,(source.width+1)/2),h=Math.Max(1,(source.height+1)/2);
            long bytes=(long)source.width*source.height*16;
            for(int level=0;level<settings.maximumLevels;level++)
            {
                sizes.Add(new Vector2Int(w,h));bytes+=(long)w*h*16;
                if(w==1&&h==1)break;w=Math.Max(1,(w+1)/2);h=Math.Max(1,(h+1)/2);
            }
            for(int level=0;level<sizes.Count-1;level++)bytes+=(long)sizes[level].x*sizes[level].y*16;
            if(bytes>(long)settings.maximumMiB*1048576)return Fail("Bloom texture budget exceeded");
            var active=RenderTexture.active;
            try
            {
                Allocate(source.width,source.height,sizes);
                material.SetVector("_BloomSettings",new Vector4(settings.threshold,settings.softKnee,settings.scatter,settings.intensity));
                using(var command=new CommandBuffer {name="Toolkit authored HDR bloom pyramid and single composition"})
                {
                    // Property blocks snapshot every draw: queued pyramid levels
                    // must not all observe the last material texture assignment.
                    void Draw(RenderTexture input,RenderTexture target,RenderTexture low,int pass)
                    {
                        var block=new MaterialPropertyBlock();block.SetTexture("_BloomSource",input);
                        block.SetVector("_BloomSourceSize",new Vector4(input.width,input.height,0,0));
                        block.SetVector("_BloomTargetSize",new Vector4(target.width,target.height,0,0));
                        if(low!=null){block.SetTexture("_BloomLow",low);block.SetVector("_BloomLowSize",new Vector4(low.width,low.height,0,0));}
                        command.SetRenderTarget(target);command.SetViewport(new Rect(0,0,target.width,target.height));
                        command.DrawMesh(quad,Matrix4x4.identity,material,0,pass,block);DrawCalls++;
                    }
                    Draw(source,down[0],null,0);
                    for(int i=1;i<down.Count;i++)Draw(down[i-1],down[i],null,1);
                    for(int i=up.Count-1;i>=0;i--)Draw(down[i],up[i],i==up.Count-1?down[i+1]:up[i+1],2);
                    Draw(source,output,Result,3);Graphics.ExecuteCommandBuffer(command);
                }
                ready=true;frame=new Frame(this);return true;
            }
            catch(Exception exception){return Fail("Bloom failed: "+exception.Message);}
            finally {RenderTexture.active=Live(active)?active:null;}
        }
        private bool Fail(string message){UnavailableReason=message;return false;}
        internal void RetireFrame(){generation++;ready=false;}
        private void Allocate(int width,int height,List<Vector2Int> sizes)
        {
            var format=GraphicsFormat.R32G32B32A32_SFloat;
            if(!SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))throw new InvalidOperationException("Bloom float targets unavailable");
            if(material==null)
            {
                var shader=Resources.Load<Shader>("AuthoredBloom");if(shader==null||!shader.isSupported)throw new InvalidOperationException("Bloom shader unavailable");
                material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};quad=new Mesh {name="Toolkit explicit bloom fullscreen",hideFlags=HideFlags.HideAndDontSave};
                quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};quad.triangles=new[]{0,1,2,0,2,3};
            }
            if(Created&&output.width==width&&output.height==height&&down.Count==sizes.Count)return;
            ReleaseTargets();output=Target(width,height,"Toolkit authored bloom composed HDR");
            for(int i=0;i<sizes.Count;i++)down.Add(Target(sizes[i].x,sizes[i].y,"Toolkit bloom down "+i));
            for(int i=0;i<sizes.Count-1;i++)up.Add(Target(sizes[i].x,sizes[i].y,"Toolkit bloom up "+i));
        }
        private static bool Live(RenderTexture t)=>t!=null&&t.IsCreated();
        private static RenderTexture Target(int w,int h,string name)
        {
            var t=new RenderTexture(new RenderTextureDescriptor(w,h,GraphicsFormat.R32G32B32A32_SFloat,0)){name=name,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave};
            if(t.Create()&&t.graphicsFormat==GraphicsFormat.R32G32B32A32_SFloat)return t;Release(t);throw new InvalidOperationException("Bloom exact target format unavailable");
        }
        private static void Release(RenderTexture t){if(t==null)return;t.Release();Destroy(t);}
        private static void Destroy(UnityEngine.Object o){if(o==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(o);else UnityEngine.Object.DestroyImmediate(o);}
        private void ReleaseTargets(){Release(output);output=null;foreach(var t in down)Release(t);foreach(var t in up)Release(t);down.Clear();up.Clear();}
        public void Dispose(){if(disposed)return;disposed=true;generation++;ready=false;ReleaseTargets();Destroy(material);Destroy(quad);material=null;quad=null;DrawCalls=0;}
    }
}
