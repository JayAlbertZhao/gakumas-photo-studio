using System;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifyDesktopMotionBlur(Report report,DesktopFrameRenderer host,Camera camera,DesktopFrameRenderer.Settings s,ref ulong sequence)
        {
            void Check(string n,bool ok,float value=0)=>FrameworkCheck(report,"desktop-motion-blur-"+n,ok,value);
            bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
            float Difference(Color[] a,Color[] b){float error=0;for(int p=0;p<a.Length;p++)for(int c=0;c<4;c++)error=Mathf.Max(error,Mathf.Abs(a[p][c]-b[p][c]));return error;}
            int w=s.scene.output.width,h=s.scene.output.height;ulong serial=sequence;
            camera.orthographic=true;camera.orthographicSize=2;camera.transform.SetPositionAndRotation(new Vector3(0,0,-4),Quaternion.identity);camera.ResetProjectionMatrix();
            var baseProjection=camera.projectionMatrix;
            var go=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));go.name="Independent frame motion blur striped Actor";go.layer=22;go.transform.localScale=new Vector3(2,2,1);
            var actor=go.GetComponent<Renderer>();var m=Own(new Material(Resources.Load<Shader>("PhotoModeFallback")));actor.sharedMaterial=m;
            m.SetFloat("_OutlineEnabled",0);m.SetFloat("_VertexColor",0);m.SetFloat("_Cull",0);m.SetColor("_Color",Color.white);
            var stripes=Own(new Texture2D(32,2,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});
            var texels=new Color[64];for(int i=0;i<64;i++)texels[i]=((i%32)/2%2)==0?new Color(.125f,.25f,.5f,1):new Color(2,1,.25f,1);
            stripes.SetPixels(texels);stripes.Apply();m.SetTexture("_MainTex",stripes);
            s.actors.renderers=new[]{actor};s.actors.configureMaterial=(r,i,material)=>material.SetFloat("_FaceDebugMode",14);
            s.actors.outlines=s.actors.hairCover=false;s.actors.temporalFlags=null;s.actors.excludeMotionBlur=null;
            s.actorMotion.enabled=s.includeSceneMotion=true;s.reuseSceneMotionStorage=false;s.actorStorage=SrpActorForward.Storage.SeparateHalf;
            s.effects.enabled=s.temporal.enabled=s.depthOfField.enabled=s.reflections.enabled=s.planar.enabled=s.selfShadow.enabled=false;s.colorGrade=null;
            s.motionBlur.enabled=true;s.motionBlur.shutterAngle=360;s.motionBlur.maximumRadiusPixels=8;s.motionBlur.samples=32;
            host.ResetHistoryAfterGpuCompletion();
            RenderTexture Target(GraphicsFormat f,string name){var t=Own(new RenderTexture(new RenderTextureDescriptor(w,h,f,0)){name=name,filterMode=FilterMode.Point});if(!t.Create())throw new InvalidOperationException(name);return t;}
            var independentGuide=Target(GraphicsFormat.R32G32B32A32_SFloat,"Independent CPU authored motion blur guide");
            var independentProtection=Target(GraphicsFormat.R8_UNorm,"Independent CPU authored blur exclusions");
            var upload=Own(new Material(Resources.Load<Shader>("ActorForwardDepth")));
            var quad=Own(new Mesh());quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};quad.triangles=new[]{0,1,2,0,2,3};
            quad.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};
            void Upload(Color[] pixels,RenderTexture target)
            {
                var texture=Own(new Texture2D(w,h,TextureFormat.RGBAFloat,false,true));texture.SetPixels(pixels);texture.Apply();
                upload.SetTexture("_ActorSourceColor",texture);upload.SetVector("_ActorTargetSize",new Vector4(w,h,0,0));
                using var command=new CommandBuffer();command.SetRenderTarget(target);command.SetViewport(new Rect(0,0,w,h));command.DrawMesh(quad,Matrix4x4.identity,upload,0,2);Graphics.ExecuteCommandBuffer(command);
            }
            void Save(string name,Color[] pixels){SaveSsrPreview("desktop-motion-blur-"+name,pixels,w,h,false);using var file=new BinaryWriter(File.Create(Path.Combine(_directory,"desktop-motion-blur-"+name+".raw")));foreach(var p in pixels)for(int c=0;c<4;c++)file.Write(p[c]);}
            using var control=new MotionBlurRenderer();using var dof=new BokehDepthOfFieldRenderer();using var fx=new HeavyFxRenderer();
            bool excludeActor=false,excludeScene=false;Vector2 priorJitter=Vector2.zero;Color[] lastOutput=null,lastInput=null,lastGuide=null,lastMask=null;
            float lastResponse=0;
            void Run(string name,double time,float expectedInterval,bool reset=false)
            {
                if(reset)host.ResetHistoryAfterGpuCompletion();
                bool requested=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_STORAGE_CASE")=="motion-blur-"+name;
                bool began=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();
                DesktopFrameRenderer.OpaqueFrame opaque=default;Exception failure=null;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{
                    try{if(!host.TryRecord(context,++serial,1,out opaque,out var why))throw new InvalidOperationException(why);}catch(Exception e){failure=e;}
                }});
                if(failure!=null)throw failure;
                var raw=ReadSceneTarget(opaque.actors.motionDepthIdentity);var opaqueColor=ReadSceneTarget(opaque.actors.color);
                if(!host.TryFinishAfterSubmission(opaque,time,out var frame,out var error))throw new InvalidOperationException(error);
                if(!frame.motionBlur.HasValue)throw new InvalidOperationException("Missing enabled blur frame");
                var b=frame.motionBlur.Value;
                var preTemporal=opaque.actors.color;
                if(s.effects.enabled&&fx.TryRender(preTemporal,new FogVolumeDepth(opaque.actors.eyeDepth),camera,s.effects,time,out var f))preTemporal=f.color;
                var postFx=ReadSceneTarget(preTemporal);
                var input=frame.temporal.HasValue?frame.temporal.Value.color:preTemporal;
                if(s.depthOfField.enabled){if(!dof.TryRender(input,opaque.actors.eyeDepth,s.depthOfField,out var d))throw new InvalidOperationException(dof.UnavailableReason);input=d.color;}
                var expectedGuide=new Color[w*h];var expectedMask=new Color[w*h];int valid=0,protectedCount=0;
                for(int p=0;p<raw.Length;p++)
                {
                    int packed=(int)raw[p].a;bool isActor=(packed&1)!=0;
                    bool changed=false;for(int c=0;c<4;c++)changed|=postFx[p][c]!=opaqueColor[p][c];
                    bool protect=(packed&4)!=0||(packed>>4)>0&&(isActor?excludeActor:excludeScene)||changed;
                    if(protect){expectedMask[p].r=4f/255;protectedCount++;}
                    if(!protect&&(packed>>4)>0&&(packed&8)!=0&&raw[p].b>0){expectedGuide[p]=new Color(raw[p].r,raw[p].g,raw[p].b,1);valid++;}
                }
                Upload(expectedGuide,independentGuide);Upload(expectedMask,independentProtection);
                Check(name+"-independent-guide-upload-exact",Difference(expectedGuide,ReadSceneTarget(independentGuide))==0,Difference(expectedGuide,ReadSceneTarget(independentGuide)));
                var uploadedMask=ReadSceneTarget(independentProtection);float maskUploadError=0;
                for(int p=0;p<uploadedMask.Length;p++)maskUploadError=Mathf.Max(maskUploadError,Mathf.Abs(uploadedMask[p].r-expectedMask[p].r));
                Check(name+"-independent-mask-upload-exact",maskUploadError==0,maskUploadError);
                var jitter=s.temporal.enabled?s.temporal.jitterUv:s.motionBlurJitterUv;
                var ci=new MotionBlurInput(input,independentGuide,expectedInterval,reset?Vector2.zero:jitter-priorJitter,s.temporal.enabled?-jitter:Vector2.zero,independentProtection);
                if(!control.TryRender(ci,s.motionBlur,out var reference))throw new InvalidOperationException(control.UnavailableReason);
                lastOutput=ReadSceneTarget(frame.color);lastInput=ReadSceneTarget(input);lastGuide=ReadSceneTarget(b.guide);lastMask=ReadSceneTarget(b.protection);
                var expected=ReadSceneTarget(reference.color);lastResponse=Difference(lastOutput,lastInput);
                Check(name+"-explicit-sample-interval",Mathf.Abs(b.sampleInterval-expectedInterval)<.000001f,b.sampleInterval);
                Check(name+"-independent-half4-guide",Difference(lastGuide,ReadSceneTarget(independentGuide))==0,Difference(lastGuide,ReadSceneTarget(independentGuide)));
                Check(name+"-independent-protection",Difference(lastMask,ReadSceneTarget(independentProtection))==0,Difference(lastMask,ReadSceneTarget(independentProtection)));
                Check(name+"-existing-filter-independent-guide-whole-color",Difference(lastOutput,expected)<.000002f,Difference(lastOutput,expected));
                Check(name+"-current-tickets",frame.IsCurrent&&b.IsCurrent&&opaque.IsCurrent);
                Check(name+"-finite-color",Array.TrueForAll(lastOutput,p=>Finite(p.r)&&Finite(p.g)&&Finite(p.b)&&Finite(p.a)));
                if(s.effects.enabled)Check(name+"-fx-positive-protection",protectedCount>100,protectedCount);
                long bytes=33L*w*h+32L*((w+7)/8)*((h+7)/8);Check(name+"-nominal-owned-budget",host.MotionBlurNominalTextureBytes==bytes,host.MotionBlurNominalTextureBytes);
                Save(name,lastOutput);Save(name+"-guide",lastGuide);Save(name+"-protection",lastMask);
                priorJitter=jitter;host.RetireAfterGpuCompletion();Check(name+"-retirement-invalidates-ticket",!frame.IsCurrent&&!b.IsCurrent&&s.scene.output.IsCreated());
                if(requested)Check(name+"-native-capture",began&&RenderDocCaptureBridge.EndOffscreenCapture());
            }
            Run("cold",0,0,true);Check("cold-exact-current",lastResponse==0,lastResponse);
            Run("stationary",.02,.02f);Check("stationary-exact-current",lastResponse==0,lastResponse);
            actor.transform.position=new Vector3(.3f,0,0);Run("moving",.04,.02f);Check("moving-striped-actor-positive",lastResponse>.05f,lastResponse);
            Run("paused",.04,0);Check("paused-exact-current",lastResponse==0,lastResponse);
            actor.transform.position+=new Vector3(.3f,0,0);Run("rewind",.01,0);Check("rewind-exact-current",lastResponse==0,lastResponse);
            actor.transform.position+=new Vector3(.2f,0,0);Run("long-gap",2,0);Check("long-gap-exact-current",lastResponse==0,lastResponse);
            actor.transform.position+=new Vector3(-.3f,0,0);Run("seek-reset",2.02,0,true);Check("seek-exact-current",lastResponse==0,lastResponse);
            serial++;actor.transform.position+=new Vector3(-.2f,0,0);Run("sequence-gap",2.04,0);Check("sequence-gap-exact-current",lastResponse==0,lastResponse);
            s.actors.temporalFlags=(r,i)=>TemporalPixelFlags.ExcludeTaa;actor.transform.position+=new Vector3(-.3f,0,0);
            Run("exclude-taa-still-blurs",2.06,.02f);Check("exclude-taa-not-blur-exclusion",lastResponse>.05f,lastResponse);
            s.actors.temporalFlags=(r,i)=>TemporalPixelFlags.NoJitter;actor.transform.position+=new Vector3(.3f,0,0);
            Run("no-jitter-protected",2.08,.02f);Check("no-jitter-preserves-current",lastResponse==0,lastResponse);
            s.actors.temporalFlags=null;s.actors.excludeMotionBlur=(r,i)=>true;excludeActor=true;actor.transform.position+=new Vector3(-.3f,0,0);
            Run("authored-actor-exclusion",2.10,.02f);Check("authored-actor-preserves-current",lastResponse==0,lastResponse);
            s.actors.excludeMotionBlur=null;m.renderQueue=3000;actor.transform.position+=new Vector3(.3f,0,0);
            Run("blended-draw-protected",2.12,.02f);Check("blended-preserves-current",lastResponse==0,lastResponse);m.renderQueue=2000;excludeActor=false;
            foreach(var surface in s.scene.surfaces)surface.excludeMotionBlur=true;excludeScene=true;
            actor.transform.position+=new Vector3(-.3f,0,0);Run("scene-exclusions",2.14,.02f);
            foreach(var surface in s.scene.surfaces)surface.excludeMotionBlur=false;excludeScene=false;
            s.temporal.enabled=true;s.temporal.jitterUv=new Vector2(.25f/w,-.25f/h);var projection=baseProjection;
            projection.m03+=2*s.temporal.jitterUv.x;projection.m13-=2*s.temporal.jitterUv.y;camera.projectionMatrix=projection;
            Run("taa-jitter-cold",3,0,true);actor.transform.position+=new Vector3(.3f,0,0);Run("taa-jitter-moving",3.02,.02f);
            Check("taa-moving-positive",lastResponse>.01f,lastResponse);
            s.depthOfField.enabled=true;actor.transform.position+=new Vector3(-.3f,0,0);Run("taa-dof-moving",3.04,.02f);
            s.effects.enabled=true;actor.transform.position+=new Vector3(.3f,0,0);Run("fx-protection",3.06,.02f);
            s.effects.enabled=s.depthOfField.enabled=s.temporal.enabled=false;s.temporal.jitterUv=Vector2.zero;camera.projectionMatrix=baseProjection;
            s.reuseSceneMotionStorage=true;s.actorStorage=SrpActorForward.Storage.ReuseScenePacked;
            Run("reuse-cold",4,0,true);actor.transform.position+=new Vector3(-.3f,0,0);Run("reuse-moving",4.02,.02f);
            Check("reuse-moving-positive",lastResponse>.05f,lastResponse);
            s.motionBlur.enabled=false;
            DesktopFrameRenderer.OpaqueFrame disabled=default;
            RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{if(!host.TryRecord(context,++serial,1,out disabled,out var why))throw new InvalidOperationException(why);}});
            if(!host.TryFinishAfterSubmission(disabled,5,out var disabledFrame,out var disabledError))throw new InvalidOperationException(disabledError);
            Check("disabled-exact-passthrough",!disabledFrame.motionBlur.HasValue&&disabledFrame.color==disabled.actors.color);
            ReadSceneTarget(disabledFrame.color);host.RetireAfterGpuCompletion();
            s.motionBlur.enabled=true;actor.transform.position+=new Vector3(.3f,0,0);Run("reenabled-cold",5.02,0);Check("reenabled-exact-current",lastResponse==0,lastResponse);
            s.motionBlurMaximumMiB=0;
            RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>Check("invalid-budget-before-record",!host.TryRecord(context,++serial,1,out _,out var why)&&why!=null&&!host.HasPendingWork)});
            s.motionBlurMaximumMiB=256;s.motionBlur.enabled=false;sequence=serial;
        }

        private void VerifyShortExposureReconstruction(Report report)
        {
            const int w=17,h=13;const float interval=.02f;
            using var blur=new MotionBlurRenderer();
            RenderTexture Target(RenderTextureFormat format)
            {var t=Own(new RenderTexture(w,h,0,format,RenderTextureReadWrite.Linear){filterMode=FilterMode.Point});t.Create();return t;}
            var source=Target(RenderTextureFormat.ARGBFloat);var guide=Target(RenderTextureFormat.ARGBFloat);var flags=Target(RenderTextureFormat.R8);
            var upload=Own(new Texture2D(w,h,TextureFormat.RGBAFloat,false,true));
            void Upload(RenderTexture target,Color[] data){upload.SetPixels(data);upload.Apply();Graphics.Blit(upload,target);}
            void Check(string name,bool ok,float value=0)=>FrameworkCheck(report,"motion-blur-short-"+name,ok,value);
            var colors=new Color[w*h];var guides=new Color[colors.Length];var masks=new Color[colors.Length];
            for(int p=0;p<colors.Length;p++)colors[p]=new Color((p%7)/7f,(p%11)/11f,(p%13)/13f,.1f+(p%9)/10f);
            Upload(source,colors);colors=ReadSceneTarget(source);
            var settings=new MotionBlurSettings {enabled=true,subpixelReconstruction=true,samples=64,maximumRadiusPixels=12};
            Check("default-opt-in-only",!new MotionBlurSettings().subpixelReconstruction);
            Color[] Render(float dt=interval)
            {
                if(!blur.TryRender(new MotionBlurInput(source,guide,dt,default,default,flags),settings,out var frame))throw new InvalidOperationException(blur.UnavailableReason);
                var result=ReadSceneTarget(frame.color);float alpha=0;for(int p=0;p<result.Length;p++)alpha=Mathf.Max(alpha,Mathf.Abs(result[p].a-colors[p].a));
                Check("alpha-exact-"+report.checks.Count,alpha==0,alpha);return result;
            }
            // Analytic integral of a piecewise-bilinear image along a centered
            // constant short translation, not a copy of the shader's sample loop.
            Color[] Integral(Vector2 velocity)
            {
                var result=new Color[colors.Length];
                for(int p=0;p<result.Length;p++)
                {
                    var v=new Vector2(guides[p].r*w*.25f,guides[p].g*h*.25f);double a=Math.Abs(v.x),b=Math.Abs(v.y);int sx=v.x<0?-1:1,sy=v.y<0?-1:1;
                    int[] dx={0,sx,-sx,0,0,sx,-sx},dy={0,0,0,sy,-sy,sy,-sy};
                    double[] weights={1-(a+b)/2+a*b/3,a/4-a*b/6,a/4-a*b/6,b/4-a*b/6,b/4-a*b/6,a*b/6,a*b/6};
                    bool centerValid=guides[p].a==1&&masks[p].r<.01f;
                    for(int c=0;c<3;c++)
                    {
                        double sum=0;
                        for(int k=0;k<weights.Length;k++)
                        {
                            int x=p%w+dx[k],y=p/w+dy[k],q=x+y*w;
                            bool valid=centerValid&&x>=0&&x<w&&y>=0&&y<h&&guides[q].a==1&&masks[q].r<.01f&&Math.Abs(guides[q].b-guides[p].b)<=settings.softDepthExtent;
                            if(valid)valid=Vector2.Distance(v,new Vector2(guides[q].r*w*.25f,guides[q].g*h*.25f))<=.5f;
                            sum+=(valid?colors[q][c]:colors[p][c])*weights[k];
                        }
                        result[p][c]=(float)sum;
                    }
                    result[p].a=colors[p].a;
                }
                return result;
            }
            foreach(var velocity in new[]{new Vector2(.47f,.45f),new Vector2(-.47f,.45f),new Vector2(.31f,-.23f),new Vector2(-.1f,-.12f)})
            foreach(string condition in new[]{"uniform","protected","depth-edge","velocity-edge","invalid-guide"})
            {
                for(int p=0;p<guides.Length;p++)
                {
                    guides[p]=new Color(velocity.x/(w*.25f)*(condition=="velocity-edge"&&p%w>8?-1:1),velocity.y/(h*.25f),condition=="depth-edge"&&p%w>8?7:3,condition=="invalid-guide"&&p%w==8?0:1);
                    masks[p]=new Color(condition=="protected"&&p%w==8?4f/255:0,0,0,1);
                }
                Upload(guide,guides);Upload(flags,masks);guides=ReadSceneTarget(guide);masks=ReadSceneTarget(flags);
                var expected=Integral(velocity);settings.samples=16;var coarse=Render();settings.samples=64;var fine=Render();
                float coarseError=PixelError(expected,coarse),fineError=PixelError(expected,fine);
                string label=condition+"-"+velocity.x+"-"+velocity.y;
                Check(label+"-analytic-whole-image",fineError<.00004f,fineError);
                Check(label+"-quadrature-converges",fineError<coarseError,coarseError==0?0:fineError/coarseError);
                Check(label+"-short-exposure-response",PixelError(fine,colors)>.001f,PixelError(fine,colors));
                Check(label+"-zero-interval-exact",PixelError(Render(0),colors)==0);
                var repeat=Render();Check(label+"-repeat-exact",PixelError(fine,repeat)==0);
                settings.subpixelReconstruction=false;var legacy=Render();settings.subpixelReconstruction=true;
                Check(label+"-retains-integer-baseline",PixelError(legacy,colors)<.000002f,PixelError(legacy,colors));
                if(condition=="protected"||condition=="invalid-guide")
                {float e=0;for(int y=0;y<h;y++)e=Mathf.Max(e,Vector4.Distance(fine[y*w+8],colors[y*w+8]));Check(label+"-protected-center-exact",e==0,e);}
            }
            int nearAxisCase=0;
            foreach(var velocity in new[]{new Vector2(.47f,1e-7f),new Vector2(.47f,-1e-7f),new Vector2(1e-7f,.47f),new Vector2(-1e-7f,.47f)})
            {
                string label=(nearAxisCase++).ToString();
                void Set(Vector2 v)
                {for(int p=0;p<guides.Length;p++){guides[p]=new Color(v.x/(w*.25f),v.y/(h*.25f),3,1);masks[p]=Color.clear;}Upload(guide,guides);Upload(flags,masks);guides=ReadSceneTarget(guide);}
                Set(velocity);var expected=Integral(velocity);var tiny=Render();
                Check("near-axis-analytic-"+label,PixelError(expected,tiny)<.000004f,PixelError(expected,tiny));
                Set(Mathf.Abs(velocity.x)<1e-6f?new Vector2(0,velocity.y):new Vector2(velocity.x,0));var axis=Render();
                Check("near-axis-continuity-"+label,PixelError(tiny,axis)<.000004f,PixelError(tiny,axis));
            }
        }
    }
}
