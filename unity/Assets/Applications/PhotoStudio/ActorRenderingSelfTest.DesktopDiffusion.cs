using System;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        // Double precision reference operates on the caller's source, never on
        // producer blur buffers. Sampling helper is shared with the Bloom oracle.
        private static Color[] DiffusionReference(Color[] source,int width,int height,DiffusionRenderer.Settings s,out Color[] blur,bool wrongLowRadius=false)
        {
            var image=new BloomReferenceImage(width,height);
            for(int p=0;p<source.Length;p++)for(int c=0;c<3;c++)image.rgb[p*3+c]=Math.Max(0,Math.Min(65504,source[p][c]));
            int w=(width+s.downsample-1)/s.downsample,h=(height+s.downsample-1)/s.downsample;
            var reduced=new BloomReferenceImage(w,h);
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)for(int c=0;c<3;c++)
            {
                double u=(x+.5)/w,v=(y+.5)/h,dx=.25/w,dy=.25/h;
                reduced.rgb[(y*w+x)*3+c]=w==width&&h==height?image.Load(x,y,c):
                    (image.Sample(u-dx,v-dy,c)+image.Sample(u+dx,v-dy,c)+image.Sample(u-dx,v+dy,c)+image.Sample(u+dx,v+dy,c))*.25;
            }
            image=reduced;int[] weights={1,4,6,4,1};
            for(int axis=0;axis<2;axis++)
            {
                var next=new BloomReferenceImage(w,h);double step=(double)s.radiusPixels/(axis==0?(wrongLowRadius?w:width):(wrongLowRadius?h:height));
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)for(int c=0;c<3;c++)for(int k=-2;k<=2;k++)
                    next.rgb[(y*w+x)*3+c]+=weights[k+2]/16.0*image.Sample((x+.5)/w+(axis==0?k*step:0),(y+.5)/h+(axis==1?k*step:0),c);
                image=next;
            }
            blur=new Color[w*h];for(int p=0;p<blur.Length;p++)blur[p]=new Color((float)image.rgb[p*3],(float)image.rgb[p*3+1],(float)image.rgb[p*3+2],1);
            var result=(Color[])source.Clone();
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)for(int c=0;c<3;c++)
                result[y*width+x][c]=(float)(source[y*width+x][c]+s.intensity*Math.Max(0,image.Sample((x+.5)/width,(y+.5)/height,c)-Math.Max(0,source[y*width+x][c])));
            return result;
        }
        private void VerifyDesktopDiffusion(Report report,DesktopFrameRenderer host,Camera camera,DesktopFrameRenderer.Settings s,ref ulong sequence)
        {
            void Check(string name,bool ok,float metric=0)=>FrameworkCheck(report,"desktop-diffusion-"+name,ok,metric);
            float Relative(Color[] a,Color[] b){float e=0;for(int p=0;p<a.Length;p++)for(int c=0;c<4;c++)e=Mathf.Max(e,Mathf.Abs(a[p][c]-b[p][c])/(1+Mathf.Abs(b[p][c])));return e;}
            void Save(string name,Color[] pixels,int w,int h)
            {
                SaveSsrPreview("desktop-diffusion-"+name,pixels,w,h,false);
                using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"desktop-diffusion-"+name+".raw")));
                foreach(var pixel in pixels)for(int c=0;c<4;c++)writer.Write(pixel[c]);
            }
            var copy=Own(new Material(Resources.Load<Shader>("ActorForwardDepth")));var quad=Own(new Mesh());
            quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};quad.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};quad.triangles=new[]{0,1,2,0,2,3};
            RenderTexture Input(int w,int h,GraphicsFormat format,Color[] colors)
            {
                var texture=Own(new Texture2D(w,h,TextureFormat.RGBAFloat,false,true));texture.SetPixels(colors);texture.Apply();
                var target=Own(new RenderTexture(new RenderTextureDescriptor(w,h,format,0)){filterMode=FilterMode.Trilinear,wrapMode=TextureWrapMode.Repeat,anisoLevel=8});if(!target.Create())throw new InvalidOperationException("Diffusion fixture input");
                copy.SetTexture("_ActorSourceColor",texture);copy.SetVector("_ActorTargetSize",new Vector4(w,h,0,0));
                using var command=new CommandBuffer();command.SetRenderTarget(target);command.SetViewport(new Rect(0,0,w,h));command.DrawMesh(quad,Matrix4x4.identity,copy,0,2);Graphics.ExecuteCommandBuffer(command);return target;
            }
            using var renderer=new DiffusionRenderer();DiffusionRenderer.Frame prior=default;
            foreach(var size in new[]{new Vector2Int(1,1),new Vector2Int(1,17),new Vector2Int(19,1),new Vector2Int(17,13),new Vector2Int(64,48)})
            foreach(var format in new[]{GraphicsFormat.R32G32B32A32_SFloat,GraphicsFormat.R16G16B16A16_SFloat,GraphicsFormat.B10G11R11_UFloatPack32})
            {
                var colors=new Color[size.x*size.y];for(int p=0;p<colors.Length;p++)colors[p]=new Color((p%11)*.4f,(p%5)*.3f,(p%7)*.6f,(p%9)/8f);
                var input=Input(size.x,size.y,format,colors);var source=ReadSceneTarget(input);
                foreach(int variant in new[]{0,1,2,3,4})
                {
                    var config=new DiffusionRenderer.Settings {enabled=true,downsample=variant==0?1:variant==1?2:variant==2?3:4,
                        radiusPixels=variant==0?0:variant==1?.75f:variant==2?4:64,intensity=variant==4?0:.6f};
                    string name=size.x+"x"+size.y+"-"+format+"-"+variant;
                    if(!renderer.TryRender(input,config,out var frame))throw new InvalidOperationException(renderer.UnavailableReason);
                    var actual=ReadSceneTarget(frame.color);var expected=DiffusionReference(source,size.x,size.y,config,out var blur);
                    float error=Relative(actual,expected),blurError=Relative(ReadSceneTarget(frame.blurred),blur);bool alpha=true;
                    for(int p=0;p<actual.Length;p++)alpha&=actual[p].a==source[p].a;
                    Check(name+"-independent-double-whole-hdr",error<.00002f,error);Check(name+"-independent-double-blur",blurError<.00002f,blurError);
                    Check(name+"-alpha-exact",alpha);Check(name+"-four-draws",renderer.DrawCalls==4);
                    Check(name+"-nominal-budget",renderer.NominalTextureBytes==config.EstimateTargetBytes(size.x,size.y),renderer.NominalTextureBytes);
                    Check(name+"-prior-ticket-retired",!prior.IsCurrent&&frame.IsCurrent);prior=frame;
                    Check(name+"-source-and-sampler-unmodified",PixelError(source,ReadSceneTarget(input))==0&&input.filterMode==FilterMode.Trilinear&&input.wrapMode==TextureWrapMode.Repeat&&input.anisoLevel==8);
                    if(variant==0||variant==4)Check(name+"-no-op-exact",PixelError(actual,source)==0,PixelError(actual,source));
                    Save(name,actual,size.x,size.y);
                }
            }
            var constant=Input(9,7,GraphicsFormat.R32G32B32A32_SFloat,Array.ConvertAll(new int[63],_=>new Color(65504,32752,-2,.3f)));
            var settings=new DiffusionRenderer.Settings {enabled=true,intensity=1,radiusPixels=64,downsample=3};
            Check("constant-hdr-accepted",renderer.TryRender(constant,settings,out var extreme));
            Check("constant-hdr-and-negative-base-exact",PixelError(ReadSceneTarget(constant),ReadSceneTarget(extreme.color))==0);
            Check("reject-owned-input",!renderer.TryRender(extreme.color,settings,out _)&&!extreme.IsCurrent);
            settings.radiusPixels=float.NaN;Check("reject-nonfinite-radius",!renderer.TryRender(constant,settings,out _));settings.radiusPixels=4;
            settings.downsample=0;Check("reject-invalid-reduction",!renderer.TryRender(constant,settings,out _));settings.downsample=2;
            Check("recover-after-rejection",renderer.TryRender(constant,settings,out var recovered));recovered.blurred.Release();Check("lost-blur-invalidates-ticket",!recovered.IsCurrent);
            Check("recreate-lost-blur",renderer.TryRender(constant,settings,out _));
            var large=Input(256,256,GraphicsFormat.R32G32B32A32_SFloat,new Color[256*256]);settings.maximumMiB=1;long retained=renderer.NominalTextureBytes;
            Check("budget-before-reallocation",!renderer.TryRender(large,settings,out _)&&renderer.NominalTextureBytes==retained&&renderer.UnavailableReason.Contains("budget"));settings.maximumMiB=128;
            settings.enabled=false;Check("disabled-rejected",!renderer.TryRender(constant,settings,out _));settings.enabled=true;
            var ldr=Input(1,1,GraphicsFormat.R8G8B8A8_UNorm,new[]{Color.white});Check("ldr-rejected",!renderer.TryRender(ldr,settings,out _));
            Check("owner-recovered",renderer.TryRender(constant,settings,out var owner));
            using(var sibling=new DiffusionRenderer())
            {
                Check("independent-owner",sibling.TryRender(constant,settings,out var child)&&child.color!=owner.color&&owner.IsCurrent);
            }
            Check("sibling-disposal-preserves-owner",owner.IsCurrent&&constant.IsCreated());
            renderer.Dispose();Check("terminal-dispose",!owner.IsCurrent&&!renderer.TryRender(constant,settings,out _)&&renderer.NominalTextureBytes==0);

            // Actual low-size scene + temporal/DOF/blur/Bloom + FSR, followed by
            // full-size diffusion and nonlinear grading. Never resize geometry back.
            var originalOutput=s.scene.output;var originalDepth=s.scene.depthStencil;var originalNormal=s.scene.normalIdentity;
            var originalBase=s.scene.materialBase;var originalMos=s.scene.materialMos;var originalTarget=camera.targetTexture;
            float originalAspect=camera.aspect;var originalPosition=s.actors.renderers[0].transform.position;ulong serial=sequence;
            using var gradeControl=new ColorGradingRenderer();
            using var lut=ColorGradingLut.Bake(new ColorGradingProfile {size=16,domain=ColorLutDomain.Linear,maximumInput=8,toneMapping=ColorToneMapping.Clip,colorFilter=new Vector3(.8f,.9f,1.1f)});
            RenderTexture Target(Vector2Int size,GraphicsFormat color,GraphicsFormat depth=GraphicsFormat.None)
            {var t=Own(new RenderTexture(new RenderTextureDescriptor(size.x,size.y,color,0){depthStencilFormat=depth}));if(!t.Create())throw new InvalidOperationException("Diffusion scene target");return t;}
            void Reject(string name)
            {
                bool rejected=false;string reason=null;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>rejected=!host.TryRecord(context,++serial,1,out _,out reason)});
                Check(name,rejected&&!host.HasPendingWork&&!string.IsNullOrEmpty(reason));
            }
            try
            {
                Check("host-disabled-default-no-allocation",!s.diffusion.enabled&&host.DiffusionNominalTextureBytes==0);
                s.fsr.enabled=true;s.fsr.encoding=FsrInputEncoding.LinearHdr;s.fsr.quality=FsrQuality.Quality;s.fsr.stabilizeLumaGradients=true;s.fsrOutputSize=new Vector2Int(257,193);
                if(!s.fsr.TryGetRenderSize(s.fsrOutputSize,out var low))throw new InvalidOperationException("Diffusion FSR size");
                s.scene.output=Target(low,GraphicsFormat.B10G11R11_UFloatPack32);s.scene.depthStencil=Target(low,GraphicsFormat.None,GraphicsFormat.D32_SFloat_S8_UInt);
                s.scene.normalIdentity=Target(low,GraphicsFormat.R16G16B16A16_SFloat);s.scene.materialBase=Target(low,GraphicsFormat.R8G8B8A8_SRGB);s.scene.materialMos=Target(low,GraphicsFormat.R8G8B8A8_UNorm);
                camera.targetTexture=s.scene.output;camera.aspect=257f/193;
                s.actorMotion.enabled=s.temporal.enabled=s.effects.enabled=s.depthOfField.enabled=s.motionBlur.enabled=s.bloom.enabled=s.diffusion.enabled=true;
                s.diffusion.radiusPixels=3.5f;s.diffusion.intensity=.6f;s.diffusion.downsample=3;s.colorGrade=lut;
                foreach(var backend in new[]{FsrBackend.Compute,FsrBackend.Raster})
                foreach(bool moving in new[]{false,true})
                {
                    s.fsr.backend=backend;if(!moving)host.ResetHistoryAfterGpuCompletion();else s.actors.renderers[0].transform.position+=new Vector3(.1f,0,0);
                    string name=backend+"-"+(moving?"moving":"cold");
                    bool requested=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_STORAGE_CASE")=="diffusion-"+name;
                    bool began=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();DesktopFrameRenderer.OpaqueFrame opaque=default;Exception failure=null;
                    RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{try{if(!host.TryRecord(context,++serial,1,out opaque,out var reason))throw new InvalidOperationException(reason);}catch(Exception e){failure=e;}}});
                    if(failure!=null)throw failure;if(!host.TryFinishAfterSubmission(opaque,30+serial*.02,out var frame,out var error))throw new InvalidOperationException(error);
                    if(!frame.diffusion.HasValue||!frame.fsr.HasValue||!frame.bloom.HasValue||!frame.temporal.HasValue||!frame.motionBlur.HasValue)throw new InvalidOperationException("Missing diffusion chain ticket");
                    var child=frame.diffusion.Value;var source=ReadSceneTarget(frame.fsr.Value.color);var result=ReadSceneTarget(child.color);
                    var expected=DiffusionReference(source,257,193,s.diffusion,out var blur);float e=Relative(expected,result);
                    Check(name+"-after-fsr-independent-whole-hdr",e<.00002f,e);
                    float b=Relative(blur,ReadSceneTarget(child.blurred));Check(name+"-independent-whole-blur",b<.00002f,b);
                    var wrong=DiffusionReference(source,257,193,s.diffusion,out _,true);float delta=PixelError(wrong,expected);
                    Check(name+"-low-resolution-radius-countermodel-rejected",delta>.001f,delta);
                    Check(name+"-visible-response",PixelError(source,result)>.001f,PixelError(source,result));
                    Check(name+"-full-color-low-depth",frame.OutputSize==s.fsrOutputSize&&frame.RenderSize==low&&child.blurred.width==86&&child.blurred.height==65);
                    Check(name+"-host-budget",host.DiffusionNominalTextureBytes==s.diffusion.EstimateTargetBytes(257,193),host.DiffusionNominalTextureBytes);
                    if(!gradeControl.TryRender(child.color,lut,out var grade))throw new InvalidOperationException(gradeControl.UnavailableReason);
                    var final=ReadSceneTarget(frame.color);Check(name+"-grade-after-diffusion-exact",PixelError(final,ReadSceneTarget(grade.color))==0);
                    Check(name+"-fsr-input-preserved",PixelError(source,ReadSceneTarget(frame.fsr.Value.color))==0);
                    Save(name+"-source",source,257,193);Save(name+"-blur",ReadSceneTarget(child.blurred),86,65);Save(name+"-diffusion",result,257,193);Save(name+"-final",final,257,193);
                    if(requested)Check(name+"-native-capture",began&&RenderDocCaptureBridge.EndOffscreenCapture());
                    if(backend==FsrBackend.Raster&&moving){child.blurred.Release();Check("lost-child-invalidates-host",!child.IsCurrent&&!frame.IsCurrent);}
                    host.RetireAfterGpuCompletion();Check(name+"-retirement-invalidates-child",!child.IsCurrent&&!frame.IsCurrent&&s.scene.output.IsCreated());
                }
                s.diffusion.intensity=float.PositiveInfinity;Reject("host-invalid-before-record");s.diffusion.intensity=.6f;
                s.diffusion.maximumMiB=1;Reject("host-budget-before-record");s.diffusion.maximumMiB=128;
                s.diffusion.enabled=false;DesktopFrameRenderer.OpaqueFrame bare=default;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{if(!host.TryRecord(context,++serial,1,out bare,out var why))throw new InvalidOperationException(why);}});
                if(!host.TryFinishAfterSubmission(bare,40,out var disabled,out var reasonDisabled))throw new InvalidOperationException(reasonDisabled);
                if(!gradeControl.TryRender(disabled.fsr.Value.color,lut,out var control))throw new InvalidOperationException(gradeControl.UnavailableReason);
                Check("host-disabled-grade-from-fsr-exact",!disabled.diffusion.HasValue&&PixelError(ReadSceneTarget(disabled.color),ReadSceneTarget(control.color))==0);
                host.RetireAfterGpuCompletion();
            }
            finally
            {
                host.ResetHistoryAfterGpuCompletion();s.fsr.enabled=s.diffusion.enabled=s.effects.enabled=s.depthOfField.enabled=s.motionBlur.enabled=s.temporal.enabled=s.bloom.enabled=false;s.colorGrade=null;
                s.scene.output=originalOutput;s.scene.depthStencil=originalDepth;s.scene.normalIdentity=originalNormal;s.scene.materialBase=originalBase;s.scene.materialMos=originalMos;
                camera.targetTexture=originalTarget;camera.aspect=originalAspect;s.actors.renderers[0].transform.position=originalPosition;sequence=serial;
            }
        }
    }
}
