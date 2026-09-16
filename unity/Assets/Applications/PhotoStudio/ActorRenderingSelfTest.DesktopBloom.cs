using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        // Independent double-precision image reference, no shader or producer
        // intermediates. Sampling is reconstructed from coordinates and weights.
        private sealed class BloomReferenceImage
        {
            public readonly int width,height;public readonly double[] rgb;
            public BloomReferenceImage(int w,int h){width=w;height=h;rgb=new double[w*h*3];}
            public double Load(int x,int y,int c)=>rgb[(Math.Max(0,Math.Min(height-1,y))*width+Math.Max(0,Math.Min(width-1,x)))*3+c];
            public double Sample(double u,double v,int c)
            {
                double x=u*width-.5,y=v*height-.5;int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);double dx=x-ix,dy=y-iy;
                return Load(ix,iy,c)*(1-dx)*(1-dy)+Load(ix+1,iy,c)*dx*(1-dy)+Load(ix,iy+1,c)*(1-dx)*dy+Load(ix+1,iy+1,c)*dx*dy;
            }
            public double Four(int x,int y,int w,int h,int c)
            {
                double u=(x+.5)/w,v=(y+.5)/h,dx=.5/width,dy=.5/height;
                return (Sample(u-dx,v-dy,c)+Sample(u+dx,v-dy,c)+Sample(u-dx,v+dy,c)+Sample(u+dx,v+dy,c))*.25;
            }
        }
        private static Color[] BloomReference(Color[] source,int width,int height,BloomRenderer.Settings settings,out Color[] glow,out int levels,out long bytes)
        {
            var image=new BloomReferenceImage(width,height);
            for(int p=0;p<source.Length;p++)for(int c=0;c<3;c++)image.rgb[p*3+c]=Math.Max(0,Math.Min(65504,source[p][c]));
            var pyramid=new List<BloomReferenceImage>();bytes=(long)width*height*16;
            for(int level=0;level<settings.maximumLevels;level++)
            {
                var next=new BloomReferenceImage(Math.Max(1,(image.width+1)/2),Math.Max(1,(image.height+1)/2));
                for(int y=0;y<next.height;y++)for(int x=0;x<next.width;x++)
                {
                    double[] color={image.Four(x,y,next.width,next.height,0),image.Four(x,y,next.width,next.height,1),image.Four(x,y,next.width,next.height,2)};
                    double weight=1;
                    if(level==0)
                    {
                        double brightness=Math.Max(color[0],Math.Max(color[1],color[2])),k=(double)settings.threshold*settings.softKnee;
                        double soft=Math.Max(0,Math.Min(2*k,brightness-settings.threshold+k));
                        weight=Math.Max(brightness-settings.threshold,k>0?soft*soft/(4*k):0)/Math.Max(brightness,1e-20);
                    }
                    for(int c=0;c<3;c++)next.rgb[(y*next.width+x)*3+c]=color[c]*weight;
                }
                pyramid.Add(next);bytes+=(long)next.width*next.height*16;image=next;if(image.width==1&&image.height==1)break;
            }
            levels=pyramid.Count;
            for(int i=pyramid.Count-2;i>=0;i--)
            {
                var high=pyramid[i];var result=new BloomReferenceImage(high.width,high.height);
                for(int y=0;y<result.height;y++)for(int x=0;x<result.width;x++)for(int c=0;c<3;c++)
                    result.rgb[(y*result.width+x)*3+c]=(1-settings.scatter)*high.Load(x,y,c)+settings.scatter*image.Sample((x+.5)/result.width,(y+.5)/result.height,c);
                image=result;bytes+=(long)image.width*image.height*16;
            }
            glow=new Color[image.width*image.height];for(int p=0;p<glow.Length;p++)glow[p]=new Color((float)image.rgb[p*3],(float)image.rgb[p*3+1],(float)image.rgb[p*3+2],1);
            var final=new Color[source.Length];Array.Copy(source,final,source.Length);
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)for(int c=0;c<3;c++)final[y*width+x][c]=(float)(source[y*width+x][c]+settings.intensity*image.Sample((x+.5)/width,(y+.5)/height,c));
            return final;
        }
        private void VerifyDesktopBloom(Report report,DesktopFrameRenderer host,Camera camera,DesktopFrameRenderer.Settings s,ref ulong sequence)
        {
            void Check(string n,bool ok,float error=0)=>FrameworkCheck(report,"desktop-bloom-"+n,ok,error);
            float Relative(Color[] a,Color[] b){float e=0;for(int i=0;i<a.Length;i++)for(int c=0;c<3;c++)e=Mathf.Max(e,Mathf.Abs(a[i][c]-b[i][c])/(1+Mathf.Abs(b[i][c])));return e;}
            float Maximum(Color[] a,Color[] b){float e=0;for(int i=0;i<a.Length;i++)for(int c=0;c<4;c++)e=Mathf.Max(e,Mathf.Abs(a[i][c]-b[i][c]));return e;}
            void Save(string name,Color[] pixels,int w,int h){SaveSsrPreview("desktop-bloom-"+name,pixels,w,h,false);using var file=new BinaryWriter(File.Create(Path.Combine(_directory,"desktop-bloom-"+name+".raw")));foreach(var p in pixels)for(int c=0;c<4;c++)file.Write(p[c]);}
            var copy=Own(new Material(Resources.Load<Shader>("ActorForwardDepth")));var quad=Own(new Mesh());
            quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};quad.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};quad.triangles=new[]{0,1,2,0,2,3};
            RenderTexture Input(int w,int h,GraphicsFormat format,Color[] colors)
            {
                var texture=Own(new Texture2D(w,h,TextureFormat.RGBAFloat,false,true));texture.SetPixels(colors);texture.Apply();
                var target=Own(new RenderTexture(new RenderTextureDescriptor(w,h,format,0)){filterMode=FilterMode.Trilinear,wrapMode=TextureWrapMode.Repeat,anisoLevel=8});if(!target.Create())throw new InvalidOperationException("Bloom fixture input");
                copy.SetTexture("_ActorSourceColor",texture);copy.SetVector("_ActorTargetSize",new Vector4(w,h,0,0));
                using var command=new CommandBuffer();command.SetRenderTarget(target);command.SetViewport(new Rect(0,0,w,h));command.DrawMesh(quad,Matrix4x4.identity,copy,0,2);Graphics.ExecuteCommandBuffer(command);return target;
            }
            using var renderer=new BloomRenderer();BloomRenderer.Frame prior=default;
            foreach(var size in new[]{new Vector2Int(1,1),new Vector2Int(1,17),new Vector2Int(19,1),new Vector2Int(17,13),new Vector2Int(64,48)})
            foreach(var format in new[]{GraphicsFormat.R32G32B32A32_SFloat,GraphicsFormat.R16G16B16A16_SFloat,GraphicsFormat.B10G11R11_UFloatPack32})
            {
                var colors=new Color[size.x*size.y];for(int p=0;p<colors.Length;p++)colors[p]=new Color((p%11)*.4f,(p%5)*.3f,(p%7)*.6f,(p%9)/8f);
                var source=Input(size.x,size.y,format,colors);var actualSource=ReadSceneTarget(source);
                foreach(int variant in new[]{0,1,2,3})
                {
                    var settings=new BloomRenderer.Settings {enabled=true,maximumLevels=variant==0?1:variant==1?4:10,threshold=variant==2?0:1,
                        softKnee=variant==0?0:.5f,intensity=variant==3?0:.75f,scatter=variant==0?0:variant==2?1:.7f};
                    string name=size.x+"x"+size.y+"-"+format+"-"+variant;
                    if(!renderer.TryRender(source,settings,out var frame))throw new InvalidOperationException(renderer.UnavailableReason);
                    var result=ReadSceneTarget(frame.color);var expected=BloomReference(actualSource,size.x,size.y,settings,out var glow,out int levels,out long bytes);
                    float error=Relative(result,expected),glowError=Relative(ReadSceneTarget(frame.bloom),glow);bool alpha=true;
                    for(int p=0;p<result.Length;p++)alpha&=result[p].a==actualSource[p].a;
                    Check(name+"-independent-double-whole-hdr",error<.00002f,error);Check(name+"-independent-double-glow",glowError<.00002f,glowError);
                    Check(name+"-alpha-exact",alpha);Check(name+"-stops-at-one-texel",renderer.LevelCount==levels);
                    Check(name+"-one-composition-draw-budget",renderer.DrawCalls==2*levels);Check(name+"-nominal-owned-budget",renderer.NominalTextureBytes==bytes,renderer.NominalTextureBytes);
                    Check(name+"-prior-ticket-retired",!prior.IsCurrent&&frame.IsCurrent);prior=frame;
                    Check(name+"-input-sampler-unmodified",source.filterMode==FilterMode.Trilinear&&source.wrapMode==TextureWrapMode.Repeat&&source.anisoLevel==8);
                    Check(name+"-source-content-exact",Maximum(actualSource,ReadSceneTarget(source))==0);
                    if(settings.intensity==0)Check(name+"-zero-intensity-exact",Maximum(actualSource,result)==0);
                    Save(name,result,size.x,size.y);
                }
            }
            var extreme=Input(9,7,GraphicsFormat.R32G32B32A32_SFloat,Array.ConvertAll(new int[63],_=>new Color(65504,32752,-2,.3f)));
            var config=new BloomRenderer.Settings {enabled=true,threshold=0,softKnee=0,intensity=64,maximumLevels=10};
            Check("hdr-extreme-accepted",renderer.TryRender(extreme,config,out var extremeFrame));var extremePixels=ReadSceneTarget(extremeFrame.color);
            var extremeReference=BloomReference(ReadSceneTarget(extreme),9,7,config,out _,out _,out _);
            Check("hdr-extreme-no-half-overflow",Relative(extremePixels,extremeReference)<.00002f,Relative(extremePixels,extremeReference));
            Check("reject-owned-input",!renderer.TryRender(extremeFrame.color,config,out _)&&!extremeFrame.IsCurrent);
            config.maximumMiB=0;Check("reject-invalid-budget",!renderer.TryRender(extreme,config,out _));config.maximumMiB=128;
            Check("recover-after-rejection",renderer.TryRender(extreme,config,out var recovered));recovered.bloom.Release();Check("lost-pyramid-invalidates-ticket",!recovered.IsCurrent);
            Check("recreate-lost-pyramid",renderer.TryRender(extreme,config,out _));
            var large=Input(256,256,GraphicsFormat.R32G32B32A32_SFloat,new Color[256*256]);
            config.maximumMiB=1;long retained=renderer.NominalTextureBytes;
            Check("computed-budget-rejected-before-reallocation",!renderer.TryRender(large,config,out _)&&renderer.NominalTextureBytes==retained&&renderer.UnavailableReason.Contains("budget"));
            config.maximumMiB=128;config.enabled=false;Check("disabled-settings-rejected",!renderer.TryRender(extreme,config,out _));config.enabled=true;
            var ldr=Input(1,1,GraphicsFormat.R8G8B8A8_UNorm,new[]{Color.white});Check("non-hdr-format-rejected",!renderer.TryRender(ldr,config,out _));
            Check("owner-recovered",renderer.TryRender(extreme,config,out var ownerFrame));var ownerColor=ReadSceneTarget(ownerFrame.color);
            using(var other=new BloomRenderer())
            {
                var zero=new BloomRenderer.Settings {enabled=true,intensity=0};Check("second-owner-render",other.TryRender(extreme,zero,out var otherFrame));
                Check("independent-owner-storage-and-parameters",ownerFrame.IsCurrent&&otherFrame.IsCurrent&&ownerFrame.color!=otherFrame.color&&
                    Maximum(ReadSceneTarget(ownerFrame.color),ownerColor)==0&&Maximum(ReadSceneTarget(otherFrame.color),ReadSceneTarget(extreme))==0);
            }
            Check("other-disposal-preserves-owner",ownerFrame.IsCurrent&&extreme.IsCreated());

            s.bloom.enabled=true;s.bloom.threshold=.5f;s.bloom.intensity=.25f;s.bloom.maximumLevels=5;s.motionBlur.enabled=true;
            host.ResetHistoryAfterGpuCompletion();ulong serial=sequence;
            void Run(string name,double time)
            {
                bool requested=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_STORAGE_CASE")=="bloom-"+name;
                bool began=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();
                DesktopFrameRenderer.OpaqueFrame opaque=default;Exception failure=null;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{try{if(!host.TryRecord(context,++serial,1,out opaque,out var why))throw new InvalidOperationException(why);}catch(Exception e){failure=e;}}});
                if(failure!=null)throw failure;if(!host.TryFinishAfterSubmission(opaque,time,out var frame,out var error))throw new InvalidOperationException(error);
                if(!frame.bloom.HasValue||!frame.motionBlur.HasValue)throw new InvalidOperationException("Missing joined post ticket");
                var source=ReadSceneTarget(frame.motionBlur.Value.color);var result=ReadSceneTarget(frame.color);
                var expected=BloomReference(source,frame.color.width,frame.color.height,s.bloom,out _,out _,out long bytes);float e=Relative(result,expected);
                Check(name+"-after-motion-blur-independent-whole-hdr",e<.00002f,e);
                Check(name+"-visible-bloom-response",Maximum(source,result)>.01f,Maximum(source,result));
                Check(name+"-host-budget",host.BloomNominalTextureBytes==bytes);
                Save(name,result,frame.color.width,frame.color.height);
                var child=frame.bloom.Value;host.RetireAfterGpuCompletion();Check(name+"-retirement-invalidates-bloom",!frame.IsCurrent&&!child.IsCurrent);
                if(requested)Check(name+"-native-capture",began&&RenderDocCaptureBridge.EndOffscreenCapture());
            }
            Run("cold",6);s.actors.renderers[0].transform.position+=new Vector3(.3f,0,0);Run("moving",6.02);
            s.bloom.enabled=false;s.motionBlur.enabled=false;sequence=serial;
        }
    }
}
