using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyLensFlares(Report report)
        {
            yield return null;
            var oldRenderers=FindObjectsOfType<Renderer>(); var forced=new bool[oldRenderers.Length];
            for(int i=0;i<oldRenderers.Length;i++){forced[i]=oldRenderers[i].forceRenderingOff;oldRenderers[i].forceRenderingOff=true;}
            var renderer=new LensFlareRenderer();var other=new LensFlareRenderer();var saved=RenderTexture.active;var oldAnisotropy=QualitySettings.anisotropicFiltering;
            try
            {
                void Check(string name,bool ok,float difference=0)=>FrameworkCheck(report,"lens-flare-"+name,ok,difference);
                RenderTexture Target(int w,int h,RenderTextureFormat format,int bits=0)
                {var t=Own(new RenderTexture(w,h,bits,format,RenderTextureReadWrite.Linear){name="Flare fixture "+w+"x"+h,filterMode=FilterMode.Point});t.Create();return t;}
                void Upload(RenderTexture target,Func<int,int,Color> pixel)
                {
                    var t=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point};var data=new Color[target.width*target.height];
                    for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)data[y*target.width+x]=pixel(x,y);
                    try{t.SetPixels(data);t.Apply();Graphics.Blit(t,target);}finally{Destroy(t);}
                }
                var host=Own(new GameObject("Actual lens flare camera"));var camera=host.AddComponent<Camera>();camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;
                camera.nearClipPlane=.3f;camera.farClipPlane=20;camera.fieldOfView=55;camera.aspect=67f/45;camera.orthographicSize=3;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.13f,.19f,.28f,.375f);camera.cullingMask=1<<26;camera.renderingPath=RenderingPath.Forward;
                var source=Target(67,45,RenderTextureFormat.ARGBFloat);var depth=Target(67,45,RenderTextureFormat.RFloat);
                Upload(source,(x,y)=>new Color(.1f+x*.02f,.23f+y*.01f,.17f,.15f+x*.01f));Upload(depth,(x,y)=>Color.clear);
                var settings=new LensFlareSettings();
                Check("default-off-no-resources",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,0,out _)&&renderer.TargetCount==0&&renderer.DrawCalls==0);
                settings.enabled=true;settings.resolution=LensFlareResolution.Full;settings.occlusionSamplesPerAxis=4;
                var emitter=new LensFlareEmitter{position=camera.ViewportToWorldPoint(new Vector3(.62f,.59f,7)),linearRadiance=new Vector3(3,1.5f,.7f),occlusionRadius=.75f,depthBias=.01f};
                var disc=new LensFlareElement{halfSize=new Vector2(.14f,.12f),softness=.8f};
                var ring=new LensFlareElement{shape=LensFlareShape.Ring,axisPosition=2.1f,halfSize=Vector2.one*.2f,linearTint=new Vector3(.1f,.9f,1.2f),rotationDegrees=21,softness=.7f};
                var polygon=new LensFlareElement{shape=LensFlareShape.Polygon,axisPosition=1.2f,halfSize=Vector2.one*.09f,linearTint=new Vector3(.3f,1,.2f),sides=7,softness=.3f};
                var star=new LensFlareElement{shape=LensFlareShape.Star,halfSize=new Vector2(.35f,.06f),sides=6,rotationDegrees=7,falloffExponent=4};
                emitter.elements=new[]{disc,ring,polygon,star};settings.emitters=new[]{emitter};double time=0;
                LensFlareRenderer.Frame Render(string name,float tolerance=.0005f,RenderTexture protection=null)
                {
                    var active=RenderTexture.active;
                    if(!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out var frame,protection))throw new InvalidOperationException(renderer.UnavailableReason);
                    Check(name+"-current-owned-three-pass",frame.IsCurrent&&renderer.TryGetFrame(out _)&&renderer.TargetCount==3&&renderer.DrawCalls==3&&RenderTexture.active==active);
                    var input=ReadSceneTarget(source);var z=ReadSceneTarget(depth);var mask=protection!=null?ReadSceneTarget(protection):null;
                    FlareReference(camera,settings,time,input,z,source.width,source.height,false,mask,out var expectedVisibility,out var expectedArtifacts,out var expectedColor);
                    var vis=ReadSceneTarget(frame.visibility);var effects=ReadSceneTarget(frame.artifacts);var result=ReadSceneTarget(frame.color);float error=0,visibilityError=0,alpha=0;
                    for(int i=0;i<vis.Length;i++)visibilityError=Mathf.Max(visibilityError,Mathf.Abs(vis[i].r-expectedVisibility[i]));
                    for(int i=0;i<effects.Length;i++)for(int c=0;c<4;c++)error=Mathf.Max(error,Mathf.Abs(effects[i][c]-expectedArtifacts[i][c]));
                    for(int i=0;i<result.Length;i++){alpha=Mathf.Max(alpha,Mathf.Abs(result[i].a-input[i].a));for(int c=0;c<3;c++)error=Mathf.Max(error,Mathf.Abs(result[i][c]-expectedColor[i][c]));}
                    Check(name+"-current-source-depth-disk",visibilityError<.00001f,visibilityError);
                    Check(name+"-whole-artifact-and-composite-images",error<tolerance,error);Check(name+"-alpha-exact",alpha==0,alpha);
                    return frame;
                }
                var first=Render("four-authored-shapes");SaveSsrPreview("flare-four-shapes",ReadSceneTarget(first.color),source.width,source.height,false);
                foreach(var resolution in new[]{LensFlareResolution.Half,LensFlareResolution.Quarter}){settings.resolution=resolution;Render("resolution-"+resolution);}
                settings.resolution=LensFlareResolution.Full;
                foreach(int grid in new[]{1,2,4,8}){settings.occlusionSamplesPerAxis=grid;Upload(depth,(x,y)=>new Color(x<42?3:0,0,0,0));Render("occlusion-grid-"+grid);}
                var partial=ReadSceneTarget(Render("partial-source",.0005f).visibility)[0].r;Check("partial-occlusion-is-fractional",partial>0&&partial<1,partial);
                Upload(depth,(x,y)=>new Color(3,0,0,0));var blocked=Render("fully-blocked");Check("blocked-beam-exact-passthrough",ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(blocked.color)));
                emitter.occlusion=false;Render("explicit-no-occlusion");emitter.occlusion=true;
                Upload(depth,(x,y)=>new Color(float.NaN,0,0,0));Render("unknown-depth-hidden");Upload(depth,(x,y)=>new Color(-1,0,0,0));Render("negative-depth-hidden");Upload(depth,(x,y)=>new Color(.1f,0,0,0));Render("before-near-hidden");Upload(depth,(x,y)=>Color.clear);
                var originalPosition=emitter.position;
                emitter.position=new Vector3(0,0,-3);var behind=Render("source-behind-camera");Check("behind-camera-no-ghost",ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(behind.color)));
                emitter.position=new Vector3(0,0,.1f);Render("source-before-near");emitter.position=new Vector3(0,0,25);Render("source-after-far");
                emitter.position=camera.ViewportToWorldPoint(new Vector3(1.1f,.6f,7));emitter.offscreenMargin=.3f;Render("unknown-offscreen-hidden");emitter.outsideScreenVisibility=.5f;Render("explicit-offscreen-visibility");
                emitter.position=originalPosition;emitter.outsideScreenVisibility=emitter.offscreenMargin=0;
                emitter.directionalAttenuation=true;emitter.rotation=Quaternion.LookRotation(-emitter.position);Render("emitting-cone-front");emitter.rotation=Quaternion.LookRotation(emitter.position);Render("emitting-cone-back");emitter.directionalAttenuation=false;
                emitter.fadeStartDistance=2;emitter.fadeEndDistance=10;Render("distance-fade");emitter.fadeStartDistance=100;emitter.fadeEndDistance=200;
                emitter.pulseAmplitude=.7f;emitter.pulseFrequency=1.3f;ring.rotationSpeed=37;ring.alignToAxis=true;
                time=.37;var animated=ReadSceneTarget(Render("explicit-time-animation").color);time=2.1;Render("seek-forward");time=.37;
                Check("seek-restores-exact-image",ScenePixelsEqual(animated,ReadSceneTarget(Render("seek-back").color)));emitter.pulseAmplitude=0;ring.rotationSpeed=0;ring.alignToAxis=false;time=0;
                foreach(int projection in new[]{1,2}){camera.orthographic=projection==1;camera.ResetProjectionMatrix();if(projection==2){var p=camera.projectionMatrix;p.m02=.21f;p.m12=-.17f;camera.projectionMatrix=p;}Render("projection-"+projection);}
                camera.orthographic=false;camera.ResetProjectionMatrix();camera.transform.SetPositionAndRotation(new Vector3(.2f,-.1f,.3f),Quaternion.Euler(3,7,2));Render("moved-rotated-camera");camera.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                emitter.position=camera.ViewportToWorldPoint(new Vector3(33.5f/67,22.5f/45,7));emitter.elements=new[]{star};Render("star-exact-pixel-center");emitter.position=originalPosition;emitter.elements=new[]{disc,ring,polygon,star};
                var atlas=Own(new Texture2D(6,4,TextureFormat.RGBAFloat,false,true));var texels=new Color[24];for(int i=0;i<texels.Length;i++)texels[i]=new Color(.2f+i*.03f,1,.5f,i%3*.5f);atlas.SetPixels(texels);atlas.Apply();
                var textured=new LensFlareElement{shape=LensFlareShape.Texture,axisPosition=.8f,halfSize=new Vector2(.18f,.1f),rotationDegrees=28,atlasRect=new RectInt(1,1,4,2)};
                settings.atlas=atlas;emitter.elements=new[]{disc,ring,polygon,star,textured};Render("custom-linear-atlas-rect");
                var second=new LensFlareEmitter{position=camera.ViewportToWorldPoint(new Vector3(.25f,.35f,8)),linearRadiance=new Vector3(.2f,2,3),elements=new[]{disc,ring}};
                settings.emitters=new[]{emitter,second};Render("two-sources-shared-elements");settings.emitters=new[]{emitter};
                second.enabled=false;settings.emitters=new[]{null,second,emitter};Render("disabled-source-does-not-shift-visibility");settings.emitters=new[]{emitter};
                var mask=Target(source.width,source.height,RenderTextureFormat.R8);Upload(mask,(x,y)=>new Color(x<source.width/2?1:0,0,0,0));Render("protected-output-pixels",.0005f,mask);
                var sampling=ReadSceneTarget(Render("sampler-baseline").color);source.filterMode=depth.filterMode=atlas.filterMode=FilterMode.Trilinear;source.anisoLevel=depth.anisoLevel=atlas.anisoLevel=16;
                source.wrapMode=depth.wrapMode=atlas.wrapMode=TextureWrapMode.Repeat;QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;
                Check("integer-loads-ignore-inherited-samplers",ScenePixelsEqual(sampling,ReadSceneTarget(Render("forced-anisotropy").color)));QualitySettings.anisotropicFiltering=oldAnisotropy;
                var lease=Render("before-target-loss");other.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out var independent);
                for(int loss=0;loss<3;loss++){var target=loss==0?lease.visibility:loss==1?lease.artifacts:lease.color;target.Release();Check("lost-target-invalidates-"+target.name,!lease.IsCurrent&&!renderer.TryGetFrame(out _));lease=Render("recreate-"+target.name);}
                Check("separate-renderer-survives",independent.IsCurrent);
                Check("owned-output-alias-rejected",!renderer.TryRender(lease.color,new FogVolumeDepth(depth),camera,settings,time,out _)&&!lease.IsCurrent);
                source=Target(71,49,RenderTextureFormat.ARGBHalf);depth=Target(71,49,RenderTextureFormat.RHalf);camera.aspect=71f/49;
                Upload(source,(x,y)=>new Color(2.5f,.125f,.5f,.375f));Upload(depth,(x,y)=>Color.clear);settings.resolution=LensFlareResolution.Quarter;Render("resize-half-hdr-and-depth");
                source=Target(71,49,RenderTextureFormat.RGB111110Float);Upload(source,(x,y)=>new Color(2.5f,.125f,.5f,1));Render("packed-hdr-input");
                foreach(int size in new[]{1,2,7}){source=Target(size,size,RenderTextureFormat.ARGBFloat);depth=Target(size,size,RenderTextureFormat.RFloat);camera.aspect=1;Upload(source,(x,y)=>new Color(.4f,.3f,.2f,.25f));Upload(depth,(x,y)=>Color.clear);Render("small-target-"+size);}
                var wrong=Target(5,3,RenderTextureFormat.RFloat);Check("wrong-depth-size-rejected",!renderer.TryRender(source,new FogVolumeDepth(wrong),camera,settings,time,out _));
                Check("nan-time-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,double.NaN,out _));
                settings.occlusionSamplesPerAxis=3;Check("invalid-grid-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out _));settings.occlusionSamplesPerAxis=4;
                emitter.linearRadiance.x=float.NaN;Check("nan-radiance-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out _));emitter.linearRadiance.x=3;
                settings.emitters=new LensFlareEmitter[33];Check("emitter-budget-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out _));settings.emitters=new[]{emitter};
                var oldElements=emitter.elements;emitter.elements=new LensFlareElement[1025];Check("element-budget-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out _));emitter.elements=oldElements;
                textured.atlasRect=new RectInt(5,0,3,2);Check("invalid-atlas-rectangle-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out _));textured.atlasRect=new RectInt(1,1,4,2);
                settings.enabled=false;Check("explicit-disable-releases",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out _)&&renderer.TargetCount==0);settings.enabled=true;
                settings.emitters=Array.Empty<LensFlareEmitter>();Check("empty-configuration-releases",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out _)&&renderer.TargetCount==0);settings.emitters=new[]{emitter};
                var final=Render("before-dispose");renderer.Dispose();Check("dispose-invalidates-and-releases",!final.IsCurrent&&!final.color.IsCreated()&&renderer.TargetCount==0);

                // Real opaque geometry depth and the unchanged production post chain.
                const int width=97,height=65;camera.aspect=width/(float)height;camera.depthTextureMode=DepthTextureMode.Depth;
                var targetColor=Target(width,height,RenderTextureFormat.ARGBFloat,24);var nativeDepth=Target(width,height,RenderTextureFormat.RFloat);camera.targetTexture=targetColor;
                var wall=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));wall.layer=26;wall.transform.localScale=new Vector3(.8f,2,1);wall.GetComponent<Renderer>().sharedMaterial=Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback")));
                var post=host.AddComponent<OriginalStyleRenderPipeline>();post.lensFlares=settings;Color[] actualInput=null;
                post.lensFlareDepthProvider=(view,input)=>{actualInput=ReadSceneTarget(input);Graphics.Blit(Shader.GetGlobalTexture("_CameraDepthTexture"),nativeDepth);return new FogVolumeDepth(nativeDepth,FogDepthEncoding.Device);};
                bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_LENS_FLARE")=="1",started=false,ended=false;
                try
                {
                    if(capture)started=RenderDocCaptureBridge.BeginOffscreenCapture();
                    for(int step=0;step<2;step++)
                    {
                        camera.orthographic=step==1;camera.ResetProjectionMatrix();if(step==0){var p=camera.projectionMatrix;p.m02=.13f;p.m12=-.19f;camera.projectionMatrix=p;}
                        var viewport=camera.WorldToViewportPoint(emitter.position);var wallPoint=camera.ViewportToWorldPoint(new Vector3(viewport.x,viewport.y,3));wall.transform.position=wallPoint+new Vector3(-.4f+step*.1f,0,0);
                        settings.resolution=step==0?LensFlareResolution.Half:LensFlareResolution.Quarter;post.lensFlareTimeSeconds=step*.3;camera.Render();
                        if(!post.TryGetLensFlareFrame(out var frame))throw new InvalidOperationException(post.LensFlareUnavailableReason);
                        var raw=ReadSceneTarget(nativeDepth);FlareReference(camera,settings,post.lensFlareTimeSeconds,actualInput,raw,width,height,true,null,out var expectedVisibility,out var expectedArtifacts,out var expectedColor);
                        var result=ReadSceneTarget(frame.color);var actualVisibility=ReadSceneTarget(frame.visibility);float error=0,alpha=0;int geometry=0;
                        for(int i=0;i<result.Length;i++){if(raw[i].r>0)geometry++;alpha=Mathf.Max(alpha,Mathf.Abs(result[i].a-actualInput[i].a));for(int c=0;c<3;c++)error=Mathf.Max(error,Mathf.Abs(result[i][c]-expectedColor[i][c]));}
                        Check("actual-camera-depth-to-hdr-post-"+step,error<.0005f&&alpha==0&&geometry>20&&geometry<width*height,error);
                        Check("actual-source-partially-occluded-"+step,actualVisibility[0].r>0&&actualVisibility[0].r<1&&Mathf.Abs(actualVisibility[0].r-expectedVisibility[0])<.00001f,actualVisibility[0].r);
                        SaveSsrPreview("flare-actual-camera-"+step,ReadSceneTarget(targetColor),width,height,false);SaveSsrPreview("flare-actual-artifacts-"+step,ReadSceneTarget(frame.artifacts),frame.artifacts.width,frame.artifacts.height,false);
                    }
                }
                finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                if(capture)Check("requested-native-capture",started&&ended);
                post.lensFlareDepthProvider=null;camera.Render();Check("missing-provider-no-stale-frame",!post.TryGetLensFlareFrame(out _)&&post.LensFlareUnavailableReason!=null);
                post.lensFlareDepthProvider=(view,input)=>throw new InvalidOperationException("Deliberate flare provider failure");camera.Render();Check("provider-error-current-passthrough",!post.TryGetLensFlareFrame(out _)&&post.LensFlareUnavailableReason!=null);
                settings.enabled=false;camera.Render();var legacy=ReadSceneTarget(targetColor);settings.enabled=true;camera.Render();settings.enabled=false;camera.Render();
                Check("default-post-exact-restored",ScenePixelsEqual(legacy,ReadSceneTarget(targetColor))&&post.LensFlareUnavailableReason==null);post.enabled=false;camera.targetTexture=null;host.SetActive(false);
            }
            finally
            {
                renderer.Dispose();other.Dispose();QualitySettings.anisotropicFiltering=oldAnisotropy;RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;
                for(int i=0;i<oldRenderers.Length;i++)if(oldRenderers[i]!=null)oldRenderers[i].forceRenderingOff=forced[i];foreach(var obj in _owned)if(obj!=null)Destroy(obj);_owned.Clear();
            }
        }

        private static void FlareReference(Camera camera,LensFlareSettings settings,double time,Color[] input,Color[] depth,int width,int height,bool device,Color[] protection,
            out float[] visibility,out Color[] artifacts,out Color[] output)
        {
            int divisor=(int)settings.resolution,w=(width+divisor-1)/divisor,h=(height+divisor-1)/divisor;
            var sources=new List<LensFlareEmitter>();foreach(var emitter in settings.emitters)if(emitter!=null&&emitter.enabled&&emitter.intensity!=0)sources.Add(emitter);
            visibility=new float[sources.Count];artifacts=new Color[w*h];output=new Color[width*height];double aspect=width/(double)height;
            double Clamp(double v)=>Math.Max(0,Math.Min(1,v));
            Color Bilinear(Color[] pixels,int tw,int th,double px,double py,int ox=0,int oy=0,int stride=0)
            {
                int x=(int)Math.Floor(px),y=(int)Math.Floor(py);float fx=(float)(px-x),fy=(float)(py-y);if(stride==0)stride=tw;
                Color At(int dx,int dy)=>pixels[(oy+Math.Max(0,Math.Min(th-1,y+dy)))*stride+ox+Math.Max(0,Math.Min(tw-1,x+dx))];
                return Color.LerpUnclamped(Color.LerpUnclamped(At(0,0),At(1,0),fx),Color.LerpUnclamped(At(0,1),At(1,1),fx),fy);
            }
            for(int index=0;index<sources.Count;index++)
            {
                var emitter=sources[index];
                var screen=camera.WorldToViewportPoint(emitter.position);double u=screen.x,v=screen.y,eye=screen.z;
                if(eye<camera.nearClipPlane||eye>camera.farClipPlane)continue;
                double edge=Math.Min(Math.Min(u,v),Math.Min(1-u,1-v));double gain=edge>=0?1:emitter.offscreenMargin>0?Clamp(1+edge/emitter.offscreenMargin):0;
                gain*=Clamp((emitter.fadeEndDistance-Vector3.Distance(camera.transform.position,emitter.position))/(emitter.fadeEndDistance-emitter.fadeStartDistance));
                if(emitter.directionalAttenuation){double cosine=Vector3.Dot(emitter.rotation.normalized*Vector3.forward,(camera.transform.position-emitter.position).normalized),a=Math.Cos(emitter.innerAngle*Math.PI/360),b=Math.Cos(emitter.outerAngle*Math.PI/360);gain*=a>b?Clamp((cosine-b)/(a-b)):cosine>=b?1:0;}
                gain*=emitter.intensity*(1+emitter.pulseAmplitude*Math.Sin(2*Math.PI*(time*emitter.pulseFrequency%1)+emitter.pulsePhaseDegrees*Math.PI/180));
                if(emitter.occlusion)
                {
                    var rx=camera.WorldToViewportPoint(emitter.position+camera.transform.right*emitter.occlusionRadius);var ry=camera.WorldToViewportPoint(emitter.position+camera.transform.up*emitter.occlusionRadius);
                    double visible=0,count=0;int grid=settings.occlusionSamplesPerAxis;
                    for(int sy=0;sy<grid;sy++)for(int sx=0;sx<grid;sx++)
                    {
                        double x=(sx+.5)*2/grid-1,y=(sy+.5)*2/grid-1;if(x*x+y*y>1)continue;count++;
                        double tx=u+x*Math.Abs(rx.x-u),ty=v+y*Math.Abs(ry.y-v);
                        if(tx<0||tx>=1||ty<0||ty>=1){visible+=emitter.outsideScreenVisibility;continue;}
                        double z=depth[(int)Math.Floor(ty*height)*width+(int)Math.Floor(tx*width)].r;
                        if(double.IsNaN(z)||double.IsInfinity(z)||z<0)continue;
                        if(device)
                        {
                            if(z>1)continue;if(z==0){visible++;continue;}
                            double near=camera.nearClipPlane,far=camera.farClipPlane;
                            z=camera.orthographic?far-z*(far-near):near*far/(near+z*(far-near));
                        }
                        else if(z==0){visible++;continue;}
                        if(z>=camera.nearClipPlane&&z>=eye-emitter.depthBias)visible++;
                    }
                    gain*=visible/count;
                }
                visibility[index]=(float)gain;if(gain==0)continue;
                foreach(var element in emitter.elements)
                {
                    if(element==null||!element.enabled||element.intensity==0)continue;
                    double cx=u+(.5-u)*element.axisPosition+element.offset.x/aspect,cy=v+(.5-v)*element.axisPosition+element.offset.y;
                    double angle=(element.rotationDegrees+element.rotationSpeed*time)%360*Math.PI/180;if(element.alignToAxis)angle+=Math.Atan2(.5-v,(.5-u)*aspect);
                    double cosine=Math.Cos(angle),sine=Math.Sin(angle);var atlas=element.shape==LensFlareShape.Texture?settings.atlas.GetPixels():null;
                    for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                    {
                        double dx=((x+.5)/w-cx)*aspect,dy=(y+.5)/h-cy;
                        double lx=(cosine*dx+sine*dy)/(element.halfSize.x*emitter.scale),ly=(-sine*dx+cosine*dy)/(element.halfSize.y*emitter.scale);
                        if(Math.Abs(lx)>1||Math.Abs(ly)>1)continue;double r=Math.Sqrt(lx*lx+ly*ly),q=r;Color profile=Color.white;
                        if(element.shape==LensFlareShape.Texture)
                        {var rect=element.atlasRect;profile=Bilinear(atlas,rect.width,rect.height,(lx*.5+.5)*rect.width-.5,(ly*.5+.5)*rect.height-.5,rect.x,rect.y,settings.atlas.width);profile*=Mathf.Clamp01(profile.a);}
                        else
                        {
                            if(element.shape==LensFlareShape.Ring)q=Math.Abs(r-element.ringRadius)/element.ringWidth;
                            if(element.shape==LensFlareShape.Polygon){double sector=2*Math.PI/element.sides,a=Math.Atan2(ly,lx),local=a-sector*Math.Floor(a/sector+.5);q=r*Math.Cos(local)/Math.Cos(Math.PI/element.sides);}
                            double t=element.softness>0?Clamp((q-1+element.softness)/element.softness):q<1?0:1,shape=Math.Pow(1-t*t*(3-2*t),element.falloffExponent);
                            if(element.shape==LensFlareShape.Star&&r>=.0001)shape*=Math.Pow(Math.Abs(Math.Cos(Math.Atan2(ly,lx)*element.sides*.5)),element.falloffExponent);
                            profile=new Color((float)shape,(float)shape,(float)shape,0);
                        }
                        int pixel=y*w+x;for(int c=0;c<3;c++)artifacts[pixel][c]+=(float)(profile[c]*element.linearTint[c]*element.intensity*emitter.linearRadiance[c]*gain);
                    }
                }
            }
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int p=y*width+x;output[p]=input[p];if(protection!=null&&protection[p].r>0)continue;
                var effect=Bilinear(artifacts,w,h,(x+.5)/width*w-.5,(y+.5)/height*h-.5);for(int c=0;c<3;c++)output[p][c]+=effect[c];
            }
        }
    }
}
