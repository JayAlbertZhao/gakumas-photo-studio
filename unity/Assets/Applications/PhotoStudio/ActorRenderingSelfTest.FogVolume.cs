using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyFogVolumes(Report report)
        {
            yield return null;
            var previous=FindObjectsOfType<Renderer>();var forced=new bool[previous.Length];
            for(int i=0;i<previous.Length;i++){forced[i]=previous[i].forceRenderingOff;previous[i].forceRenderingOff=true;}
            var renderer=new FogVolumeRenderer();var other=new FogVolumeRenderer();var saved=RenderTexture.active;
            try
            {
                void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"fog-volume-"+name,ok,error);
                RenderTexture Target(int w,int h,RenderTextureFormat format,int z=0)
                {var t=Own(new RenderTexture(w,h,z,format,RenderTextureReadWrite.Linear){name="Fog fixture "+w+"x"+h,filterMode=FilterMode.Point});t.Create();return t;}
                void Upload(RenderTexture target,Func<int,int,Color> pixel)
                {
                    var t=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point};var data=new Color[target.width*target.height];
                    for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)data[y*target.width+x]=pixel(x,y);
                    try{t.SetPixels(data);t.Apply();Graphics.Blit(t,target);}finally{Destroy(t);}
                }
                var host=Own(new GameObject("Fog volume camera"));var camera=host.AddComponent<Camera>();camera.enabled=false;camera.nearClipPlane=.3f;camera.farClipPlane=20;camera.fieldOfView=52;camera.aspect=43f/31;camera.orthographicSize=2;
                camera.allowMSAA=false;camera.allowHDR=true;camera.renderingPath=RenderingPath.Forward;camera.cullingMask=1<<26;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.07f,.13f,.29f,.4f);
                var source=Target(43,31,RenderTextureFormat.ARGBFloat);var depth=Target(43,31,RenderTextureFormat.RFloat);
                Upload(source,(x,y)=>new Color(.1f+x*.05f,.2f+y*.03f,.7f,.1f+x*.015f));Upload(depth,(x,y)=>new Color(x<8?1:x<24?5:0,0,0,0));
                var settings=new FogVolumeSettings();
                Check("default-binding-off",!FogVolumeBinding.TryCreate(settings,camera,43,31,out _,out _));
                Check("default-no-draws-targets",!renderer.TryRender(source,new FogVolumeDepth(depth),null,out _)&&renderer.DrawCalls==0&&renderer.TargetCount==0);
                settings.enabled=true;settings.stepsPerInterval=32;
                settings.distance.enabled=true;settings.distance.density=.06f;settings.distance.startDistance=.8f;settings.distance.endDistance=11;settings.distance.linearColor=new Color(.15f,.28f,1.2f,1);
                settings.spheres=new[]{new FogVolumeSettings.SphereMedium{center=new Vector3(-.6f,.35f,3.5f),radius=2.1f,density=.9f,linearColor=new Color(1.7f,.13f,.08f,1)},
                    new FogVolumeSettings.SphereMedium{center=new Vector3(.8f,-.6f,4.8f),radius=2.8f,density=.4f,linearColor=new Color(.1f,1.4f,.3f,1)},
                    new FogVolumeSettings.SphereMedium{center=new Vector3(.2f,.3f,13),radius=1.5f,density=.7f,linearColor=new Color(1.2f,.8f,.1f,1)}};
                FogVolumeBinding Bind()
                {if(!FogVolumeBinding.TryCreate(settings,camera,source.width,source.height,out var binding,out var reason))throw new InvalidOperationException(reason);return binding;}
                float lastIntegralError=0;
                FogVolumeRenderer.Frame Render(string name,bool oracle=true,RenderTexture protect=null,float tolerance=.00035f)
                {
                    var active=RenderTexture.active;
                    if(!renderer.TryRender(source,new FogVolumeDepth(depth),Bind(),out var frame,protect))throw new InvalidOperationException(renderer.UnavailableReason);
                    Check(name+"-lease-budget",frame.IsCurrent&&renderer.DrawCalls==2&&renderer.TargetCount==2&&RenderTexture.active==active);
                    var input=ReadSceneTarget(source);var result=ReadSceneTarget(frame.color);float error=0,alpha=0;bool finite=true;
                    var z=ReadSceneTarget(depth);var transfers=ReadSceneTarget(frame.transfer);var protection=protect!=null?ReadSceneTarget(protect):null;
                    for(int y=0;y<source.height;y++)for(int x=0;x<source.width;x++)
                    {
                        int i=y*source.width+x;alpha=Mathf.Max(alpha,Mathf.Abs(input[i].a-result[i].a));
                        for(int c=0;c<4;c++)finite&=!float.IsNaN(result[i][c])&&!float.IsInfinity(result[i][c]);
                        if(!oracle)continue;
                        float eye=z[i].r;bool blocked=eye<camera.nearClipPlane&&eye!=0||float.IsNaN(eye)||float.IsInfinity(eye)||protection!=null&&protection[i].r>0;
                        var near=camera.ViewportToWorldPoint(new Vector3((x+.5f)/source.width,(y+.5f)/source.height,camera.nearClipPlane));
                        var end=camera.ViewportToWorldPoint(new Vector3((x+.5f)/source.width,(y+.5f)/source.height,eye==0?camera.farClipPlane:Mathf.Min(eye,camera.farClipPlane)));
                        Color transfer=blocked?new Color(0,0,0,1):FogDenseReference(settings,near,end,eye==0,2048);
                        for(int c=0;c<4;c++)error=Mathf.Max(error,Mathf.Abs(transfers[i][c]-transfer[c]));
                        for(int c=0;c<3;c++)error=Mathf.Max(error,Mathf.Abs(result[i][c]-(input[i][c]*transfer.a+transfer[c])));
                    }
                    Check(name+"-finite-exact-alpha",finite&&alpha==0,alpha);
                    lastIntegralError=error;
                    if(oracle)Check(name+"-entire-image-independent-2048-step-medium-integral",error<tolerance,error);
                    return frame;
                }
                var first=Render("overlapping-and-disjoint-distance");
                SaveSsrPreview("fog-volume-combined",ReadSceneTarget(first.color),source.width,source.height,false);
                var original=ReadSceneTarget(first.color);Array.Reverse(settings.spheres);var reversed=Render("permuted-input");
                Check("list-order-bit-identical",ScenePixelsEqual(original,ReadSceneTarget(reversed.color))&&!first.IsCurrent);
                var bindingSnapshot=Bind();float originalDensity=settings.spheres[0].density;settings.spheres[0].density*=3;
                renderer.TryRender(source,new FogVolumeDepth(depth),bindingSnapshot,out var immutable);
                Check("binding-does-not-alias-mutable-settings",ScenePixelsEqual(original,ReadSceneTarget(immutable.color)));settings.spheres[0].density=originalDensity;
                settings.stepsPerInterval=1;Render("quality-1-measured",true,null,float.PositiveInfinity);float lowError=lastIntegralError;
                settings.stepsPerInterval=8;Render("quality-8-measured",true,null,float.PositiveInfinity);float mediumError=lastIntegralError;
                settings.stepsPerInterval=32;Render("quality-32-measured");
                Check("colored-overlap-quality-converges",lowError>mediumError*2&&mediumError>lastIntegralError*2&&lastIntegralError<.00035f,lastIntegralError);
                settings.maximumOpacity=.28f;Render("art-opacity-cap");settings.maximumOpacity=1;
                settings.maximumOpacity=0;var zeroCap=Render("zero-opacity-cap");Check("zero-cap-exact-passthrough",ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(zeroCap.color)));settings.maximumOpacity=1;
                settings.distance.affectSky=false;foreach(var s in settings.spheres)s.affectSky=false;Render("sky-protected");
                settings.distance.affectSky=true;foreach(var s in settings.spheres)s.affectSky=true;
                foreach(int projection in new[]{1,2})
                {
                    camera.orthographic=projection==1;camera.ResetProjectionMatrix();if(projection==2){var p=camera.projectionMatrix;p.m02=.22f;p.m12=-.31f;camera.projectionMatrix=p;}
                    Render("projection-"+projection);
                }
                camera.orthographic=false;camera.ResetProjectionMatrix();
                camera.transform.position=new Vector3(.3f,-.4f,4);Render("camera-inside-overlap");camera.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                var protection=Target(43,31,RenderTextureFormat.R8);Upload(protection,(x,y)=>new Color(y<9?1:0,0,0,0));Render("explicit-protection",true,protection);
                Upload(depth,(x,y)=>new Color(x<7?-1:x<13?.1f:x<19?float.NaN:x<24?float.PositiveInfinity:8,0,0,0));Render("invalid-depth-passthrough");
                Upload(depth,(x,y)=>new Color(8,0,0,0));
                var media=settings.spheres;settings.spheres=Array.Empty<FogVolumeSettings.SphereMedium>();settings.distance.enabled=false;
                var empty=Render("vacuum");Check("vacuum-exact",ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(empty.color)));
                settings.spheres=new[]{new FogVolumeSettings.SphereMedium{center=new Vector3(0,0,4),radius=1,density=.7f,linearColor=new Color(.7f,1.2f,.2f,1)}};
                Render("single-sphere-analytic-transmission");settings.spheres=media;settings.distance.enabled=true;
                var savedMedia=settings.spheres;settings.spheres=new FogVolumeSettings.SphereMedium[8];
                for(int i=0;i<8;i++)settings.spheres[i]=new FogVolumeSettings.SphereMedium{center=new Vector3((i%3-1)*.2f,(i%2-.5f)*.3f,2+i*.4f),radius=1.4f+i*.1f,density=.2f,linearColor=new Color(.2f+i*.13f,.8f,.3f,1)};
                Render("maximum-eight-overlapping-spheres");settings.spheres=savedMedia;
                var valid=Render("before-lifecycle");other.TryRender(source,new FogVolumeDepth(depth),Bind(),out var independent);
                valid.transfer.Release();Check("target-loss-invalidates",!valid.IsCurrent&&!renderer.TryGetFrame(out _));var recreated=Render("recreated");
                Check("output-alias-rejected",!renderer.TryRender(recreated.color,new FogVolumeDepth(depth),Bind(),out _)&&!recreated.IsCurrent);
                source=Target(47,29,RenderTextureFormat.ARGBHalf);depth=Target(47,29,RenderTextureFormat.RHalf);camera.aspect=47f/29;
                Upload(source,(x,y)=>new Color(3,.7f,.2f,.375f));Upload(depth,(x,y)=>new Color(7,0,0,0));Render("resized-half-input");
                Check("independent-renderer-not-invalidated",independent.IsCurrent);
                settings.stepsPerInterval=0;Check("zero-quality-rejected",!FogVolumeBinding.TryCreate(settings,camera,47,29,out _,out _));settings.stepsPerInterval=32;
                settings.spheres=new FogVolumeSettings.SphereMedium[9];for(int i=0;i<9;i++)settings.spheres[i]=new FogVolumeSettings.SphereMedium();
                Check("capacity-overflow-rejected",!FogVolumeBinding.TryCreate(settings,camera,47,29,out _,out _));settings.spheres=media;
                settings.spheres[0].radius=float.NaN;Check("nan-radius-rejected",!FogVolumeBinding.TryCreate(settings,camera,47,29,out _,out _));settings.spheres[0].radius=1.5f;
                var singular=camera.projectionMatrix;camera.projectionMatrix=Matrix4x4.zero;Check("singular-camera-rejected",!FogVolumeBinding.TryCreate(settings,camera,47,29,out _,out _));camera.projectionMatrix=singular;
                var last=Render("before-dispose");renderer.Dispose();Check("dispose-invalidates-and-releases",!last.IsCurrent&&!last.color.IsCreated()&&renderer.TargetCount==0);

                // Actual raster depth and downstream post consumption. The provider copies
                // the camera-bound native depth inside OnRenderImage, not after Camera.Render.
                const int width=97,height=65;camera.aspect=width/(float)height;camera.depthTextureMode=DepthTextureMode.Depth;
                var target=Target(width,height,RenderTextureFormat.ARGBFloat,24);camera.targetTexture=target;
                var nativeDepth=Target(width,height,RenderTextureFormat.RFloat);
                var panel=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));panel.layer=26;panel.transform.position=new Vector3(0,.31f,2);panel.transform.localScale=new Vector3(1.07f,1.63f,1);
                var opaque=Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback")));panel.GetComponent<Renderer>().sharedMaterial=opaque;
                var probe=host.AddComponent<SphereFogDepthProbe>();Color[] before=null,after=null;FogVolumeBinding actualBinding=null;
                bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_FOG_VOLUME")=="1",started=false,ended=false;
                try
                {
                    if(capture)started=RenderDocCaptureBridge.BeginOffscreenCapture();
                    for(int projection=0;projection<3;projection++)
                    {
                        camera.orthographic=projection==1;camera.ResetProjectionMatrix();if(projection==2){var p=camera.projectionMatrix;p.m02=.22f;p.m12=-.31f;camera.projectionMatrix=p;}
                        probe.sample=rendered=>
                        {
                            Graphics.Blit(Shader.GetGlobalTexture("_CameraDepthTexture"),nativeDepth);before=ReadSceneTarget(rendered);
                            if(!FogVolumeBinding.TryCreate(settings,camera,width,height,out actualBinding,out var reason))throw new InvalidOperationException(reason);
                            if(!renderer.TryRender(rendered,new FogVolumeDepth(nativeDepth,FogDepthEncoding.Device),actualBinding,out var f))throw new InvalidOperationException(renderer.UnavailableReason);
                            after=ReadSceneTarget(f.color);
                        };
                        camera.Render();float error=0;int hits=0,misses=0;var raw=ReadSceneTarget(nativeDepth);
                        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                        {
                            int i=y*width+x;float u=(x+.5f)/width,v=(y+.5f)/height;var at=camera.ViewportToWorldPoint(new Vector3(u,v,2));
                            bool hit=Mathf.Abs(at.x)<.535f&&Mathf.Abs(at.y-.31f)<.815f;var near=camera.ViewportToWorldPoint(new Vector3(u,v,.3f));var end=camera.ViewportToWorldPoint(new Vector3(u,v,hit?2:20));
                            var fog=FogDenseReference(settings,near,end,!hit,2048);
                            for(int c=0;c<3;c++)error=Mathf.Max(error,Mathf.Abs(after[i][c]-(before[i][c]*fog.a+fog[c])));
                            if(hit&&raw[i].r>0)hits++;else if(!hit&&raw[i].r==0)misses++;
                        }
                        Check("actual-camera-"+projection+"-entire-visible-depth-rays",error<.00035f&&hits>20&&misses>20,error);
                        SaveSsrPreview("fog-volume-real-camera-"+projection,after,width,height,false);
                    }
                    probe.sample=null;probe.enabled=false;camera.orthographic=true;camera.ResetProjectionMatrix();
                    var post=host.AddComponent<OriginalStyleRenderPipeline>();post.fogVolumes=settings;
                    post.fogVolumeDepthProvider=(view,input)=>{Graphics.Blit(Shader.GetGlobalTexture("_CameraDepthTexture"),nativeDepth);return new FogVolumeDepth(nativeDepth,FogDepthEncoding.Device);};
                    camera.Render();Check("production-post-consumes-current-fog",post.TryGetFogVolumeFrame(out var production)&&production.IsCurrent&&post.FogVolumeUnavailableReason==null);
                    SaveSsrPreview("fog-volume-production-post",ReadSceneTarget(target),width,height,false);
                    post.fogVolumeDepthProvider=null;camera.Render();Check("production-no-provider-no-stale-frame",!post.TryGetFogVolumeFrame(out _)&&post.FogVolumeUnavailableReason!=null);
                    settings.enabled=false;camera.Render();var legacy=ReadSceneTarget(target);
                    settings.enabled=true;post.fogVolumeDepthProvider=(view,input)=>throw new InvalidOperationException("deliberate fog provider failure");camera.Render();
                    Check("production-provider-exception-passthrough",!post.TryGetFogVolumeFrame(out _)&&post.FogVolumeUnavailableReason!=null);
                    settings.enabled=false;camera.Render();Check("production-default-exact-restored",ScenePixelsEqual(legacy,ReadSceneTarget(target))&&post.FogVolumeUnavailableReason==null);settings.enabled=true;
                    post.enabled=false;
                    // A camera without image effects retains its real opaque depth
                    // attachment. Fog opaque color once, then depth-test each forward
                    // transparent surface using that attachment and its own world endpoint.
                    var opaqueHost=Own(new GameObject("Fog opaque attachment camera"));var opaqueCamera=opaqueHost.AddComponent<Camera>();opaqueCamera.CopyFrom(camera);opaqueCamera.enabled=false;
                    var copy=new CommandBuffer{name="Fog fixture native opaque depth"};copy.Blit(BuiltinRenderTextureType.Depth,nativeDepth);
                    opaqueCamera.AddCommandBuffer(CameraEvent.BeforeImageEffects,copy);
                    try{opaqueCamera.Render();}finally{opaqueCamera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects,copy);copy.Release();}
                    var opaquePixels=ReadSceneTarget(target);
                    if(!FogVolumeBinding.TryCreate(settings,opaqueCamera,width,height,out var surfaceBinding,out var surfaceReason))throw new InvalidOperationException(surfaceReason);
                    if(!renderer.TryRender(target,new FogVolumeDepth(nativeDepth,FogDepthEncoding.Device),surfaceBinding,out var foggedBackground))throw new InvalidOperationException(renderer.UnavailableReason);
                    var farColor=new Color(.9f,.2f,1.3f,.4f);var frontColor=new Color(.2f,1.5f,.3f,.65f);
                    GameObject Surface(string label,Vector3 position,Vector3 size,Color color)
                    {
                        var obj=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));obj.name=label;obj.layer=27;obj.transform.position=position;obj.transform.localScale=size;
                        var material=Own(new Material(Resources.Load<Shader>("FogVolumeSurface")));material.SetVector("_LinearColor",color);surfaceBinding.Apply(material);obj.GetComponent<Renderer>().sharedMaterial=material;return obj;
                    }
                    var farSurface=Surface("Fog rear transparent",new Vector3(.12f,-.1f,4),new Vector3(4.7f,3.7f,1),farColor);
                    var frontSurface=Surface("Fog near transparent",new Vector3(-.6f,.14f,1.4f),new Vector3(1.35f,2.7f,1),frontColor);
                    var overlayHost=Own(new GameObject("Fog forward overlay camera"));var overlay=overlayHost.AddComponent<Camera>();overlay.CopyFrom(opaqueCamera);overlay.enabled=false;overlay.depthTextureMode=DepthTextureMode.None;
                    overlay.cullingMask=1<<27;overlay.clearFlags=CameraClearFlags.Nothing;overlay.SetTargetBuffers(foggedBackground.color.colorBuffer,target.depthBuffer);overlay.Render();
                    var layered=ReadSceneTarget(foggedBackground.color);var expectedLayers=new Color[layered.Length];float layerError=0;int rejectedBehindOpaque=0,overlappingLayers=0;string worstLayer="";
                    for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                    {
                        int i=y*width+x;float u=(x+.5f)/width,v=(y+.5f)/height;var near=opaqueCamera.ViewportToWorldPoint(new Vector3(u,v,.3f));
                        var atOpaque=opaqueCamera.ViewportToWorldPoint(new Vector3(u,v,2));bool hitOpaque=Mathf.Abs(atOpaque.x)<.535f&&Mathf.Abs(atOpaque.y-.31f)<.815f;
                        var end=opaqueCamera.ViewportToWorldPoint(new Vector3(u,v,hitOpaque?2:20));var fog=FogDenseReference(settings,near,end,!hitOpaque,2048);
                        var expected=new Color(opaquePixels[i].r*fog.a+fog.r,opaquePixels[i].g*fog.a+fog.g,opaquePixels[i].b*fog.a+fog.b,opaquePixels[i].a);
                        bool inFar=false,inFront=false;
                        foreach(var obj in new[]{farSurface,frontSurface})
                        {
                            var p=opaqueCamera.ViewportToWorldPoint(new Vector3(u,v,obj.transform.position.z));var position=obj.transform.position;var size=obj.transform.localScale;
                            bool covered=Mathf.Abs(p.x-position.x)<size.x*.5f&&Mathf.Abs(p.y-position.y)<size.y*.5f;
                            if(obj==farSurface)inFar=covered;else inFront=covered;
                            if(!covered)continue;if(hitOpaque&&position.z>2){rejectedBehindOpaque++;continue;}
                            var c=obj==farSurface?farColor:frontColor;var f=FogDenseReference(settings,near,p,false,2048);
                            for(int channel=0;channel<3;channel++)expected[channel]=c.a*(c[channel]*f.a+f[channel])+(1-c.a)*expected[channel];
                            expected.a=c.a+(1-c.a)*expected.a;
                        }
                        if(inFar&&inFront&&!hitOpaque)overlappingLayers++;
                        expectedLayers[i]=expected;
                        for(int c=0;c<4;c++)if(Mathf.Abs(layered[i][c]-expected[c])>layerError)
                        {layerError=Mathf.Abs(layered[i][c]-expected[c]);worstLayer=x+","+y+" channel="+c+" expected="+expected.ToString("R")+" actual="+layered[i].ToString("R")+" opaque="+hitOpaque+" far="+inFar+" front="+inFront;}
                    }
                    Check("forward-two-layers-entire-image-medium-and-coverage",layerError<.00035f&&rejectedBehindOpaque>20&&overlappingLayers>20,layerError);
                    Debug.Log("[FogVolumeSelfTest] layers worst="+worstLayer+" rejected="+rejectedBehindOpaque+" overlap="+overlappingLayers);
                    SaveSsrPreview("fog-volume-forward-layers",layered,width,height,false);
                    SaveSsrPreview("fog-volume-forward-layers-reference",expectedLayers,width,height,false);
                    overlay.targetTexture=null;overlayHost.SetActive(false);opaqueCamera.targetTexture=null;opaqueHost.SetActive(false);
                }
                finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                if(capture)Check("requested-native-capture",started&&ended);
                camera.targetTexture=null;host.SetActive(false);
            }
            finally
            {
                renderer.Dispose();other.Dispose();RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];foreach(var item in _owned)if(item!=null)Destroy(item);_owned.Clear();
            }
        }

        // Uniform world-space midpoint integration, without the shader's chord/event
        // decomposition. Doubles and a dense grid provide an independent numerical control.
        private static Color FogDenseReference(FogVolumeSettings settings,Vector3 start,Vector3 end,bool sky,int steps)
        {
            double dx=(double)end.x-start.x,dy=(double)end.y-start.y,dz=(double)end.z-start.z,length=Math.Sqrt(dx*dx+dy*dy+dz*dz),dt=length/steps;
            double r=0,g=0,b=0,t=1;var distance=settings.distance;
            for(int j=0;j<steps;j++)
            {
                double f=(j+.5)/steps,x=start.x+dx*f,y=start.y+dy*f,z=start.z+dz*f;
                double tau=distance!=null&&distance.enabled&&(!sky||distance.affectSky)?distance.density*Math.Max(0,Math.Min((j+1)*dt,distance.endDistance)-Math.Max(j*dt,distance.startDistance)):0;
                double cr=tau*(distance==null?0:distance.linearColor.r),cg=tau*(distance==null?0:distance.linearColor.g),cb=tau*(distance==null?0:distance.linearColor.b);
                if(settings.spheres!=null)foreach(var s in settings.spheres)
                {
                    if(s==null||!s.enabled||sky&&!s.affectSky)continue;
                    double ox=x-s.center.x,oy=y-s.center.y,oz=z-s.center.z;
                    double optical=s.density*Math.Max(0,1-(ox*ox+oy*oy+oz*oz)/((double)s.radius*s.radius))*dt;
                    tau+=optical;cr+=optical*s.linearColor.r;cg+=optical*s.linearColor.g;cb+=optical*s.linearColor.b;
                }
                if(tau>0){double opacity=1-Math.Exp(-tau);r+=t*opacity*cr/tau;g+=t*opacity*cg/tau;b+=t*opacity*cb/tau;t*=1-opacity;}
            }
            double raw=1-t,limited=Math.Min(raw,settings.maximumOpacity),scale=raw>0?limited/raw:1;
            return new Color((float)(r*scale),(float)(g*scale),(float)(b*scale),(float)(1-limited));
        }
    }
}
