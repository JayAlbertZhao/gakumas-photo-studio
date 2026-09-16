using System;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifyDesktopFsr(Report report,DesktopFrameRenderer host,Camera camera,DesktopFrameRenderer.Settings s,ref ulong sequence)
        {
            void Check(string name,bool ok,float value=0)=>FrameworkCheck(report,"desktop-fsr-"+name,ok,value);
            void Save(string name,Color[] pixels,int w,int h)
            {
                SaveSsrPreview("desktop-fsr-"+name,pixels,w,h,false);
                using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"desktop-fsr-"+name+".raw")));
                foreach(var pixel in pixels)for(int c=0;c<4;c++)writer.Write(pixel[c]);
            }
            var originalOutput=s.scene.output;var originalDepth=s.scene.depthStencil;var originalNormal=s.scene.normalIdentity;
            var originalBase=s.scene.materialBase;var originalMos=s.scene.materialMos;var originalTarget=camera.targetTexture;
            float originalAspect=camera.aspect;var originalPosition=s.actors.renderers[0].transform.position;
            ulong serial=sequence;
            using var gradeControl=new ColorGradingRenderer();
            using var sibling=new FsrRenderer();
            float worstStableUlp=0,worstUpstreamUlp=0;
            var filter=new Vector3(.8f,.9f,1.1f);
            using var lut=ColorGradingLut.Bake(new ColorGradingProfile {size=16,domain=ColorLutDomain.Linear,maximumInput=8,toneMapping=ColorToneMapping.Clip,colorFilter=filter});
            var output=new Vector2Int(257,193);
            RenderTexture Target(Vector2Int size,GraphicsFormat color,GraphicsFormat depth=GraphicsFormat.None)
            {
                var target=Own(new RenderTexture(new RenderTextureDescriptor(size.x,size.y,color,0){depthStencilFormat=depth}));
                if(!target.Create())throw new InvalidOperationException("Desktop FSR fixture attachment unavailable");return target;
            }
            void Attach(Vector2Int size)
            {
                s.scene.output=Target(size,GraphicsFormat.B10G11R11_UFloatPack32);
                s.scene.depthStencil=Target(size,GraphicsFormat.None,GraphicsFormat.D32_SFloat_S8_UInt);
                s.scene.normalIdentity=Target(size,GraphicsFormat.R16G16B16A16_SFloat);
                s.scene.materialBase=Target(size,GraphicsFormat.R8G8B8A8_SRGB);s.scene.materialMos=Target(size,GraphicsFormat.R8G8B8A8_UNorm);
                camera.targetTexture=s.scene.output;camera.aspect=(float)output.x/output.y;
            }
            void Reject(string name)
            {
                bool rejected=false;string reason=null;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>rejected=!host.TryRecord(context,++serial,1,out _,out reason)});
                Check(name,rejected&&!host.HasPendingWork&&!string.IsNullOrEmpty(reason));
            }
            try
            {
                Check("disabled-default-no-owned-targets",!s.fsr.enabled&&host.FsrEstimatedTargetBytes==0);
                s.fsr.enabled=true;s.fsr.encoding=FsrInputEncoding.LinearHdr;s.fsr.allowRasterFallback=false;s.fsrOutputSize=output;
                s.fsr.stabilizeLumaGradients=true;
                s.actorMotion.enabled=true;s.temporal.enabled=true;s.temporal.jitterUv=Vector2.zero;
                s.effects.enabled=true;s.depthOfField.enabled=true;s.motionBlur.enabled=true;s.bloom.enabled=true;s.colorGrade=lut;
                foreach(FsrQuality quality in Enum.GetValues(typeof(FsrQuality)))
                {
                    host.ResetHistoryAfterGpuCompletion();s.fsr.quality=quality;
                    if(!s.fsr.TryGetRenderSize(output,out var input))throw new InvalidOperationException("FSR fixture render size");
                    Attach(input);
                    foreach(var backend in new[]{FsrBackend.Compute,FsrBackend.Raster})
                    {
                        s.fsr.backend=backend;
                        foreach(bool moving in new[]{false,true})
                        {
                            string name=quality+"-"+backend+"-"+(moving?"moving":"cold");
                            if(!moving)host.ResetHistoryAfterGpuCompletion();
                            else s.actors.renderers[0].transform.position+=new Vector3(.08f,0,0);
                            bool requested=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_STORAGE_CASE")=="fsr-"+name;
                            bool began=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();
                            DesktopFrameRenderer.OpaqueFrame opaque=default;Exception failure=null;
                            RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{
                                try{if(!host.TryRecord(context,++serial,1,out opaque,out var reason))throw new InvalidOperationException(reason);}catch(Exception e){failure=e;}
                            }});
                            if(failure!=null)throw failure;
                            if(!host.TryFinishAfterSubmission(opaque,10+serial*.02,out var frame,out var error))throw new InvalidOperationException(error);
                            if(!frame.fsr.HasValue||!frame.bloom.HasValue||!frame.motionBlur.HasValue||!frame.temporal.HasValue)throw new InvalidOperationException("Missing joined FSR pipeline ticket");
                            var fsr=frame.fsr.Value;var source=ReadSceneTarget(frame.bloom.Value.color);
                            var expected=FsrScalar(source,input.x,new RectInt(0,0,input.x,input.y),output,s.fsr,out var prepared,out var expanded,true);
                            var actual=ReadSceneTarget(fsr.color);var actualPrepared=ReadSceneTarget(fsr.prepared);var actualExpanded=ReadSceneTarget(fsr.expanded);
                            float p=PixelError(prepared,actualPrepared),e=PixelError(expanded,actualExpanded),f=FsrRelativeError(expected,actual,true);
                            Check(name+"-bloom-input-independent-prepared",p<=.00001f,p);
                            Check(name+"-independent-whole-easu",e<=.0005f,e);
                            Check(name+"-independent-whole-hdr",f<=.02f,f);
                            Check(name+"-actual-low-geometry-depth",opaque.actors.color.width==input.x&&opaque.actors.color.height==input.y&&
                                frame.RenderSize==input&&s.scene.output.width==input.x&&s.scene.depthStencil.height==input.y);
                            Check(name+"-full-size-color-native-depth",frame.OutputSize==output&&fsr.color.width==output.x&&frame.eyeDepth==opaque.actors.eyeDepth&&
                                frame.eyeDepth.width<frame.color.width&&frame.eyeDepth.height<frame.color.height);
                            Check(name+"-actual-backend",fsr.backend==backend);
                            Check(name+"-nominal-fsr-budget",host.FsrEstimatedTargetBytes==FsrSettings.EstimateTargetBytes(input,output),host.FsrEstimatedTargetBytes);
                            if(!gradeControl.TryRender(fsr.color,lut,out var grade))throw new InvalidOperationException(gradeControl.UnavailableReason);
                            var final=ReadSceneTarget(frame.color);float gradeError=PixelError(final,ReadSceneTarget(grade.color));
                            Check(name+"-full-resolution-grade-after-fsr",gradeError==0,gradeError);
                            Check(name+"-source-preserved",PixelError(source,ReadSceneTarget(frame.bloom.Value.color))==0);
                            Check(name+"-distinct-output-storage",fsr.color!=frame.bloom.Value.color&&frame.color!=fsr.color&&frame.IsCurrent&&fsr.IsCurrent);
                            Save(name+"-prepared",actualPrepared,input.x,input.y);Save(name+"-fsr",actual,output.x,output.y);Save(name+"-final",final,output.x,output.y);
                            Save(name+"-source",source,input.x,input.y);Save(name+"-expanded",actualExpanded,output.x,output.y);
                            Save(name+"-expected",expected,output.x,output.y);Save(name+"-expected-expanded",expanded,output.x,output.y);
                            if(requested)Check(name+"-native-capture",began&&RenderDocCaptureBridge.EndOffscreenCapture());
                            // Same source bits, independent renderer/backend; unlike
                            // the moving host cases this isolates backend arithmetic.
                            var opposite=new FsrSettings {enabled=true,encoding=FsrInputEncoding.LinearHdr,quality=quality,
                                backend=backend==FsrBackend.Compute?FsrBackend.Raster:FsrBackend.Compute,
                                allowRasterFallback=false,stabilizeLumaGradients=true};
                            var rect=new RectInt(0,0,input.x,input.y);
                            if(!sibling.TryRender(frame.bloom.Value.color,rect,output,opposite,out var same))throw new InvalidOperationException(sibling.UnavailableReason);
                            float sameError=FsrRelativeError(actual,ReadSceneTarget(same.color),true);
                            Check(name+"-identical-source-cross-backend",sameError<=.00005f,sameError);
                            var noisy=(Color[])source.Clone();
                            for(int i=0;i<noisy.Length;i++)for(int c=0;c<3;c++)if(noisy[i][c]>0&&noisy[i][c]<65504)
                                noisy[i][c]=BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(noisy[i][c])+1);
                            var noise=Own(new Texture2D(input.x,input.y,TextureFormat.RGBAFloat,false,true));noise.SetPixels(noisy);noise.Apply();
                            opposite.backend=backend;
                            if(!sibling.TryRender(noise,rect,output,opposite,out var perturbed))throw new InvalidOperationException(sibling.UnavailableReason);
                            float stableUlp=FsrRelativeError(actual,ReadSceneTarget(perturbed.color),true);worstStableUlp=Mathf.Max(worstStableUlp,stableUlp);
                            Check(name+"-one-ulp-input-stability",stableUlp<=.0005f,stableUlp);
                            opposite.stabilizeLumaGradients=false;
                            if(!sibling.TryRender(frame.bloom.Value.color,rect,output,opposite,out var upstream))throw new InvalidOperationException(sibling.UnavailableReason);
                            var upstreamColor=ReadSceneTarget(upstream.color);
                            if(!sibling.TryRender(noise,rect,output,opposite,out var upstreamNoisy))throw new InvalidOperationException(sibling.UnavailableReason);
                            float upstreamUlp=FsrRelativeError(upstreamColor,ReadSceneTarget(upstreamNoisy.color),true);worstUpstreamUlp=Mathf.Max(worstUpstreamUlp,upstreamUlp);
                            Check(name+"-unmodified-variant-ulp-response-metric",!float.IsNaN(upstreamUlp)&&!float.IsInfinity(upstreamUlp),upstreamUlp);
                            Destroy(noise);_owned.Remove(noise);
                            if(quality==FsrQuality.Performance&&backend==FsrBackend.Raster&&moving)
                            {fsr.expanded.Release();Check("lost-fsr-dependency-invalidates-host",!fsr.IsCurrent&&!frame.IsCurrent);}
                            host.RetireAfterGpuCompletion();Check(name+"-retirement-invalidates-child",!frame.IsCurrent&&!fsr.IsCurrent&&s.scene.output.IsCreated());
                        }
                    }
                }
                Check("gradient-floor-reduces-worst-ulp-amplification",worstStableUlp<.0005f&&worstUpstreamUlp>10*worstStableUlp,worstUpstreamUlp);
                // Independent chromatic signals with exactly/near-equal luma,
                // below-floor and above-floor gradients; not sampled scene images.
                foreach(float gradient in new[]{0f,1f/16777216,1f/4096,1f/256})
                foreach(var backend in new[]{FsrBackend.Compute,FsrBackend.Raster})
                {
                    const int w=9,h=7;var pixels=new Color[w*h];
                    for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                    {float red=((x+2*y)%5)*.125f+.125f;pixels[y*w+x]=new Color(red,.625f-.5f*red+y*gradient,.125f,.25f+.0625f*x);}
                    var texture=Own(new Texture2D(w,h,TextureFormat.RGBAFloat,false,true));texture.SetPixels(pixels);texture.Apply();
                    var settings=new FsrSettings {enabled=true,backend=backend,allowRasterFallback=false,encoding=FsrInputEncoding.PerceptualGamma2Ldr,stabilizeLumaGradients=true};
                    var size=new Vector2Int(17,13);var rect=new RectInt(0,0,w,h);string name="chromatic-"+gradient.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"-"+backend;
                    if(!sibling.TryRender(texture,rect,size,settings,out var stable))throw new InvalidOperationException(sibling.UnavailableReason);
                    var actual=ReadSceneTarget(stable.color);var expected=FsrScalar(pixels,w,rect,size,settings,out _,out _,true);
                    float error=PixelError(expected,actual);Check(name+"-independent-whole-perceptual",error<.0005f,error);
                    settings.stabilizeLumaGradients=false;
                    if(!sibling.TryRender(texture,rect,size,settings,out var original))throw new InvalidOperationException(sibling.UnavailableReason);
                    Check(name+"-switch-retires-stable-ticket",!stable.IsCurrent&&original.IsCurrent);
                    settings.stabilizeLumaGradients=true;
                    if(!sibling.TryRender(texture,rect,size,settings,out var restored))throw new InvalidOperationException(sibling.UnavailableReason);
                    Check(name+"-keyword-restoration-exact",PixelError(actual,ReadSceneTarget(restored.color))==0);
                    Save(name,actual,size.x,size.y);Destroy(texture);_owned.Remove(texture);
                }
                s.fsr.encoding=FsrInputEncoding.LinearLdr;Reject("reject-ldr-in-hdr-slot");s.fsr.encoding=FsrInputEncoding.LinearHdr;
                s.fsrOutputSize=Vector2Int.zero;Reject("reject-invalid-output-size");s.fsrOutputSize=output;
                s.fsr.quality=FsrQuality.UltraQuality;Reject("reject-quality-size-mismatch");s.fsr.quality=FsrQuality.Performance;
                s.fsr.memoryBudgetMiB=1;Reject("reject-budget-before-recording");s.fsr.memoryBudgetMiB=256;
                s.fsr.enabled=false;s.effects.enabled=s.depthOfField.enabled=s.motionBlur.enabled=s.temporal.enabled=s.bloom.enabled=false;s.colorGrade=null;
                DesktopFrameRenderer.OpaqueFrame bare=default;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{if(!host.TryRecord(context,++serial,1,out bare,out var why))throw new InvalidOperationException(why);}});
                var before=ReadSceneTarget(bare.actors.color);
                if(!host.TryFinishAfterSubmission(bare,20,out var disabled,out var whyDisabled))throw new InvalidOperationException(whyDisabled);
                Check("disabled-native-size-exact-passthrough",!disabled.fsr.HasValue&&disabled.color==bare.actors.color&&
                    disabled.RenderSize==disabled.OutputSize&&PixelError(before,ReadSceneTarget(disabled.color))==0);
                host.RetireAfterGpuCompletion();
            }
            finally
            {
                host.ResetHistoryAfterGpuCompletion();s.fsr.enabled=false;s.colorGrade=null;
                s.scene.output=originalOutput;s.scene.depthStencil=originalDepth;s.scene.normalIdentity=originalNormal;
                s.scene.materialBase=originalBase;s.scene.materialMos=originalMos;camera.targetTexture=originalTarget;camera.aspect=originalAspect;
                s.actors.renderers[0].transform.position=originalPosition;sequence=serial;
            }
        }
    }
}
