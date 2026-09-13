using System;
using System.Collections;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyLowResolutionFx(Report report)
        {
            yield return null;
            var oldRenderers=FindObjectsOfType<Renderer>(); var forced=new bool[oldRenderers.Length];
            for(int i=0;i<oldRenderers.Length;i++) { forced[i]=oldRenderers[i].forceRenderingOff; oldRenderers[i].forceRenderingOff=true; }
            var renderer=new LowResolutionFxRenderer(); var other=new LowResolutionFxRenderer(); var saved=RenderTexture.active;
            try
            {
                void Check(string name,bool accepted,float difference=0)=>FrameworkCheck(report,"low-fx-"+name,accepted,difference);
                RenderTexture Target(int w,int h,RenderTextureFormat format,int bits=0)
                { var t=Own(new RenderTexture(w,h,bits,format,RenderTextureReadWrite.Linear) { name="Low FX fixture "+w+"x"+h,filterMode=FilterMode.Point }); t.Create(); return t; }
                void Upload(RenderTexture target,Func<int,int,Color> pixel)
                {
                    var t=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true); var colors=new Color[target.width*target.height];
                    for(int y=0;y<target.height;y++) for(int x=0;x<target.width;x++) colors[y*target.width+x]=pixel(x,y);
                    try { t.SetPixels(colors); t.Apply(); Graphics.Blit(t,target); } finally { Destroy(t); }
                }
                var host=Own(new GameObject("Actual mixed-resolution FX camera")); var camera=host.AddComponent<Camera>();
                camera.enabled=false; camera.allowHDR=true; camera.allowMSAA=false; camera.nearClipPlane=.3f; camera.farClipPlane=20;
                camera.orthographic=true; camera.orthographicSize=2; camera.aspect=61f/43; camera.fieldOfView=55;
                camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.13f,.21f,.31f,.375f); camera.cullingMask=1<<26;
                var mesh=Own(new Mesh { name="Self-authored FX plane" });
                mesh.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
                mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up}; mesh.triangles=new[]{0,2,1,0,3,2}; mesh.RecalculateBounds();
                LowResolutionFxSurface Surface(float z,Vector3 color,float alpha,FxBlend mode,FxResolution resolution)
                { return new LowResolutionFxSurface { mesh=mesh,localToWorld=Matrix4x4.TRS(new Vector3(0,0,z),Quaternion.identity,Vector3.one*50),linearRadiance=color,opacity=alpha,blend=mode,resolution=resolution }; }
                var rear=Surface(9,new Vector3(2,.2f,.1f),.3f,FxBlend.Alpha,FxResolution.Half);
                var light=Surface(8,new Vector3(.2f,3,.1f),.2f,FxBlend.Additive,FxResolution.Half);
                var middle=Surface(7,new Vector3(.1f,.2f,2),.4f,FxBlend.Alpha,FxResolution.Full);
                var refractor=Surface(6,Vector3.one,.6f,FxBlend.Distortion,FxResolution.Quarter); refractor.distortionOffset=new Vector2(.087f,-.053f);
                var front=Surface(5,new Vector3(1.2f,.7f,.2f),.25f,FxBlend.Alpha,FxResolution.Half);
                var settings=new LowResolutionFxSettings { surfaces=new[]{rear,light,middle,refractor,front} };
                var source=Target(61,43,RenderTextureFormat.ARGBFloat); var depth=Target(61,43,RenderTextureFormat.RFloat);
                Upload(source,(x,y)=>new Color(.1f+x*.023f,.2f+y*.031f,.3f+x*.007f+y*.009f,.15f+x*.007f)); Upload(depth,(x,y)=>Color.clear);
                Check("default-off-no-targets",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _) && renderer.TargetCount==0);
                settings.enabled=true;
                LowResolutionFxRenderer.Frame Render(string name,RenderTexture protection=null)
                {
                    var previous=RenderTexture.active;
                    if(!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out var frame,protection)) throw new InvalidOperationException(renderer.UnavailableReason);
                    var input=ReadSceneTarget(source); var z=ReadSceneTarget(depth); var mask=protection!=null?ReadSceneTarget(protection):null;
                    var expected=LowFxFlatReference(camera,settings,input,z,source.width,source.height,false,mask);
                    var result=ReadSceneTarget(frame.color); float error=0,alpha=0;
                    for(int i=0;i<result.Length;i++) { alpha=Mathf.Max(alpha,Mathf.Abs(result[i].a-input[i].a)); for(int c=0;c<3;c++) error=Mathf.Max(error,Mathf.Abs(result[i][c]-expected[i][c])); }
                    Check(name+"-whole-ordered-composition",error<.0005f,error);
                    Check(name+"-alpha-and-current-lease",alpha==0 && frame.IsCurrent && renderer.TryGetFrame(out _) && previous==RenderTexture.active,alpha);
                    return frame;
                }
                var first=Render("mixed-alpha-additive-distortion");
                Check("noncommutative-submission-order-four-batches",renderer.BatchCount==4 && renderer.EdgeReplayDraws==4);
                var baseline=ReadSceneTarget(first.color); settings.surfaces=new[]{light,rear,middle,refractor,front}; var swapped=ReadSceneTarget(Render("swapped-alpha-additive").color);
                Check("additive-behind-alpha-is-attenuated",!ScenePixelsEqual(baseline,swapped)); settings.surfaces=new[]{rear,light,middle,refractor,front};
                Upload(depth,(x,y)=>new Color(x<19?3:x<33?6.5f:0,0,0,0)); var occluded=Render("thin-foreground-and-between-layers");
                SaveSsrPreview("low-fx-depth-ordered",ReadSceneTarget(occluded.color),source.width,source.height,false);
                foreach(var scale in new[]{FxResolution.Full,FxResolution.Half,FxResolution.Quarter})
                { foreach(var s in settings.surfaces) s.resolution=scale; Render("all-"+scale); }
                rear.resolution=light.resolution=front.resolution=FxResolution.Half; middle.resolution=FxResolution.Full; refractor.resolution=FxResolution.Quarter;
                Upload(depth,(x,y)=>new Color(x%7==0?3:0,0,0,0)); Render("single-pixel-opaque-fences");
                Upload(depth,(x,y)=>new Color(x%9==0?float.NaN:x%9==1?-1:0,0,0,0)); Render("invalid-depth-is-not-sky");
                var mask=Target(61,43,RenderTextureFormat.RFloat); Upload(mask,(x,y)=>new Color(x<10?1:x==12?float.NaN:0,0,0,0)); Render("protected-source-and-output",mask);
                Upload(depth,(x,y)=>Color.clear);
                foreach(int projection in new[]{0,1,2})
                { camera.orthographic=projection==0; camera.ResetProjectionMatrix(); if(projection==2) { var p=camera.projectionMatrix;p.m02=.17f;p.m12=-.13f;camera.projectionMatrix=p; } Render("projection-"+projection); }
                camera.orthographic=true; camera.ResetProjectionMatrix();
                var lease=Render("before-resource-loss"); other.TryRender(source,new FogVolumeDepth(depth),camera,settings,out var separate);
                lease.TryGetLastBatch(FxResolution.Half,out var lowColor,out _); lowColor.Release();
                Check("lost-scratch-invalidates",!lease.IsCurrent && !renderer.TryGetFrame(out _)); lease=Render("recreate-lost-color");
                lease.TryGetLastBatch(FxResolution.Quarter,out _,out var lowDepth); lowDepth.Release();
                Check("lost-guide-invalidates",!lease.IsCurrent); lease=Render("recreate-lost-guide"); lease.color.Release();
                Check("lost-hdr-invalidates",!lease.IsCurrent); lease=Render("recreate-lost-hdr"); Check("independent-instance-current",separate.IsCurrent);
                Check("output-input-alias-rejected",!renderer.TryRender(lease.color,new FogVolumeDepth(depth),camera,settings,out _) && !lease.IsCurrent);
                settings.maximumTargetMiB=1; var big=Target(1024,1024,RenderTextureFormat.ARGBFloat); var bigDepth=Target(1024,1024,RenderTextureFormat.RFloat);
                Check("target-budget-before-allocation",!renderer.TryRender(big,new FogVolumeDepth(bigDepth),camera,settings,out _) && renderer.TargetCount==0); settings.maximumTargetMiB=512;
                rear.opacity=float.NaN; Check("invalid-material-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _)); rear.opacity=.3f;
                settings.surfaces=new LowResolutionFxSurface[257]; Check("submission-budget-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _)); settings.surfaces=new[]{rear,light,middle,refractor,front};
                foreach(int size in new[]{1,2,7})
                { source=Target(size,size,RenderTextureFormat.ARGBFloat); depth=Target(size,size,RenderTextureFormat.RFloat); camera.aspect=1; Upload(source,(x,y)=>new Color(.25f,.5f,.75f,.375f)); Upload(depth,(x,y)=>Color.clear); Render("tiny-target-"+size); }
                source=Target(71,49,RenderTextureFormat.ARGBHalf); depth=Target(71,49,RenderTextureFormat.RHalf); camera.aspect=71f/49;
                Upload(source,(x,y)=>new Color(2,.125f,.5f,.25f)); Upload(depth,(x,y)=>Color.clear); Render("half-hdr-and-eye-depth");
                source=Target(71,49,RenderTextureFormat.RGB111110Float); Upload(source,(x,y)=>new Color(2,.125f,.5f,1)); Render("packed-hdr-input");

                // Nonuniform finite geometry: strict independent full-resolution oracle,
                // then explicitly bounded low-resolution quality, without masking edges.
                source=Target(113,79,RenderTextureFormat.ARGBFloat); depth=Target(113,79,RenderTextureFormat.RFloat); camera.aspect=113f/79;
                Upload(source,(x,y)=>new Color(.12f+x*.003f,.17f+y*.002f,.22f,.375f));
                Upload(depth,(x,y)=>new Color(x>=52 && x<=55?3:0,0,0,0));
                var texture=Own(new Texture2D(8,6,TextureFormat.RGBAFloat,false,true)); var texels=new Color[48];
                for(int y=0;y<6;y++) for(int x=0;x<8;x++) texels[y*8+x]=new Color(.3f+x*.07f,.6f-y*.05f,.8f,(x+y+2)/14f);
                texture.SetPixels(texels); texture.Apply();
                var cloud=Surface(7,new Vector3(2,.7f,.2f),.65f,FxBlend.Alpha,FxResolution.Full);
                cloud.localToWorld=Matrix4x4.TRS(new Vector3(.2f,.1f,7),Quaternion.Euler(0,0,17),new Vector3(2.1f,1.4f,1));
                cloud.texture=texture; cloud.radialSoftness=.85f; cloud.textureST=new Vector4(.8f,.7f,.1f,.15f);
                settings.surfaces=new[]{cloud}; Color[] fullCloud=null;
                foreach(var resolution in new[]{FxResolution.Full,FxResolution.Half,FxResolution.Quarter})
                {
                    cloud.resolution=resolution;
                    if(!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out var cloudFrame)) throw new InvalidOperationException(renderer.UnavailableReason);
                    var result=ReadSceneTarget(cloudFrame.color); var expected=LowFxTexturedPlaneReference(camera,cloud,ReadSceneTarget(source),ReadSceneTarget(depth),settings.depthBias);
                    float maximum=0; double sum=0; int changed=0;
                    for(int i=0;i<result.Length;i++) for(int c=0;c<3;c++) { float difference=Mathf.Abs(result[i][c]-expected[i][c]); maximum=Mathf.Max(maximum,difference); sum+=difference; if(difference>1e-5f) changed++; }
                    double mean=sum/(result.Length*3);
                    Check("textured-soft-plane-"+resolution+"-whole-image-quality",resolution==FxResolution.Full?maximum<.0005f:maximum<.15f && mean<.015,maximum);
                    Check("textured-soft-plane-"+resolution+"-mean-error",mean<.015,(float)mean);
                    if(resolution==FxResolution.Full) fullCloud=result;
                    else Check("textured-soft-plane-"+resolution+"-actually-reduced",changed>20 && !ScenePixelsEqual(fullCloud,result));
                    SaveSsrPreview("low-fx-soft-plane-"+resolution,result,source.width,source.height,false);
                }
                var lowBaseline=ReadSceneTarget(renderer.TryGetFrame(out var samplingFrame)?samplingFrame.color:null);
                texture.filterMode=source.filterMode=depth.filterMode=FilterMode.Trilinear; texture.wrapMode=source.wrapMode=depth.wrapMode=TextureWrapMode.Repeat;
                texture.anisoLevel=source.anisoLevel=depth.anisoLevel=16; var oldAnisotropy=QualitySettings.anisotropicFiltering;
                try
                {
                    QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;
                    renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out var resampled);
                    Check("texture-and-depth-inherited-samplers-ignored",ScenePixelsEqual(lowBaseline,ReadSceneTarget(resampled.color)));
                }
                finally { QualitySettings.anisotropicFiltering=oldAnisotropy; }
                // MeshRenderer submission must match the explicit matrix path without
                // changing its caller-owned material or regular-camera culling mask.
                var cloudObject=Own(new GameObject("Explicit FX MeshRenderer")); cloudObject.layer=25;
                cloudObject.transform.SetPositionAndRotation(new Vector3(.2f,.1f,7),Quaternion.Euler(0,0,17)); cloudObject.transform.localScale=new Vector3(2.1f,1.4f,1);
                cloudObject.AddComponent<MeshFilter>().sharedMesh=mesh; var cloudRenderer=cloudObject.AddComponent<MeshRenderer>();
                var callerMaterial=Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback"))); cloudRenderer.sharedMaterial=callerMaterial;
                cloud.renderer=cloudRenderer; cloud.mesh=null; renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out var rendererFrame);
                Check("renderer-submission-equals-mesh-and-preserves-material",ScenePixelsEqual(lowBaseline,ReadSceneTarget(rendererFrame.color)) && cloudRenderer.sharedMaterial==callerMaterial && cloudRenderer.enabled);
                cloudObject.SetActive(false); cloud.renderer=null; cloud.mesh=mesh;
                var dynamics=VerifyLowFxDynamics(report,camera,source,depth,mesh,texture);
                while(dynamics.MoveNext()) yield return dynamics.Current;
                (dynamics as IDisposable)?.Dispose();
                settings.surfaces=new[]{rear,light,middle,refractor,front};

                const int width=97,height=65; camera.aspect=width/(float)height; camera.depthTextureMode=DepthTextureMode.Depth; camera.renderingPath=RenderingPath.Forward;
                var actualTarget=Target(width,height,RenderTextureFormat.ARGBFloat,24); var actualDepth=Target(width,height,RenderTextureFormat.RFloat); camera.targetTexture=actualTarget;
                var wall=Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); wall.layer=26; wall.transform.localScale=new Vector3(.6f,2.3f,1);
                wall.GetComponent<Renderer>().sharedMaterial=Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback")));
                var post=host.AddComponent<OriginalStyleRenderPipeline>(); post.lowResolutionFx=settings; Color[] actualInput=null;
                post.lowResolutionFxDepthProvider=(view,input)=> { actualInput=ReadSceneTarget(input); Graphics.Blit(Shader.GetGlobalTexture("_CameraDepthTexture"),actualDepth); return new FogVolumeDepth(actualDepth,FogDepthEncoding.Device); };
                bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_LOW_FX")=="1",started=false,ended=false;
                try
                {
                    if(capture) started=RenderDocCaptureBridge.BeginOffscreenCapture();
                    for(int step=0;step<2;step++)
                    {
                        camera.orthographic=step==1; camera.ResetProjectionMatrix(); if(step==0) { var p=camera.projectionMatrix;p.m02=.13f;p.m12=-.19f;camera.projectionMatrix=p; }
                        wall.transform.position=new Vector3(-.3f+step*.7f,0,3); camera.Render();
                        if(!post.TryGetLowResolutionFxFrame(out var frame)) throw new InvalidOperationException(post.LowResolutionFxUnavailableReason);
                        var z=ReadSceneTarget(actualDepth); var expected=LowFxFlatReference(camera,settings,actualInput,z,width,height,true,null); var output=ReadSceneTarget(frame.color);
                        float error=0,alpha=0; int geometry=0;
                        for(int i=0;i<output.Length;i++) { if(z[i].r>0) geometry++; alpha=Mathf.Max(alpha,Mathf.Abs(output[i].a-actualInput[i].a)); for(int c=0;c<3;c++) error=Mathf.Max(error,Mathf.Abs(output[i][c]-expected[i][c])); }
                        Check("actual-camera-current-depth-ordered-post-"+step,error<.0005f && alpha==0 && geometry>20 && geometry<width*height,error);
                        SaveSsrPreview("low-fx-actual-camera-"+step,ReadSceneTarget(actualTarget),width,height,false);
                    }
                }
                finally { if(started) ended=RenderDocCaptureBridge.EndOffscreenCapture(); }
                if(capture) Check("requested-native-capture",started && ended);
                post.lowResolutionFxDepthProvider=null; camera.Render(); Check("missing-provider-no-stale-frame",!post.TryGetLowResolutionFxFrame(out _) && post.LowResolutionFxUnavailableReason!=null);
                post.lowResolutionFxDepthProvider=(view,input)=>throw new InvalidOperationException("Deliberate FX provider failure"); camera.Render(); Check("throwing-provider-no-stale-frame",!post.TryGetLowResolutionFxFrame(out _) && post.LowResolutionFxUnavailableReason!=null);
                settings.enabled=false; camera.Render(); var disabled=ReadSceneTarget(actualTarget); settings.enabled=true; camera.Render(); settings.enabled=false; camera.Render();
                Check("default-post-exact-restored",ScenePixelsEqual(disabled,ReadSceneTarget(actualTarget)) && post.LowResolutionFxUnavailableReason==null);
                Check("disabled-releases-all",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _) && renderer.TargetCount==0);
                settings.enabled=true; settings.surfaces=Array.Empty<LowResolutionFxSurface>(); Check("empty-no-resources",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _) && renderer.TargetCount==0 && renderer.UnavailableReason==null);
                post.enabled=false; camera.targetTexture=null; host.SetActive(false);
            }
            finally
            {
                renderer.Dispose(); other.Dispose(); RenderTexture.active=saved!=null && saved.IsCreated()?saved:null;
                for(int i=0;i<oldRenderers.Length;i++) if(oldRenderers[i]!=null) oldRenderers[i].forceRenderingOff=forced[i];
                foreach(var value in _owned) if(value!=null) Destroy(value); _owned.Clear();
            }
        }

        // Independent full-resolution reference: constant, view-facing covering planes.
        // No readback of the module's low-resolution effect or depth guide.
        private static Color[] LowFxFlatReference(Camera camera,LowResolutionFxSettings settings,Color[] input,Color[] raw,int width,int height,bool device,Color[] protection)
        {
            var output=(Color[])input.Clone(); var depths=new double[input.Length];
            var inverse=(GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)*camera.worldToCameraMatrix).inverse;
            for(int y=0;y<height;y++) for(int x=0;x<width;x++)
            {
                float z=raw[y*width+x].r; double eye=z;
                if(float.IsNaN(z) || float.IsInfinity(z) || z<0 || (device && z>1)) eye=-1;
                else if(device)
                {
                    if(SystemInfo.usesReversedZBuffer?z==0:z==1) eye=camera.farClipPlane;
                    else { float sy=(y+.5f)/height*2-1; if(SystemInfo.graphicsUVStartsAtTop) sy=-sy; var world=inverse*new Vector4((x+.5f)/width*2-1,sy,z,1); eye=-camera.worldToCameraMatrix.MultiplyPoint(new Vector3(world.x,world.y,world.z)/world.w).z; }
                }
                else if(z==0) eye=camera.farClipPlane;
                if(eye<camera.nearClipPlane) eye=-1;
                depths[y*width+x]=eye;
            }
            bool Protected(int index)=>protection!=null && (float.IsNaN(protection[index].r) || float.IsInfinity(protection[index].r) || protection[index].r>0);
            foreach(var surface in settings.surfaces)
            {
                if(surface==null || !surface.enabled || surface.opacity==0) continue;
                double eye=-camera.worldToCameraMatrix.MultiplyPoint(surface.localToWorld.MultiplyPoint(Vector3.zero)).z;
                var previous=(Color[])output.Clone(); double opacity=surface.opacity;
                for(int y=0;y<height;y++) for(int x=0;x<width;x++)
                {
                    int index=y*width+x; if(Protected(index) || depths[index]<eye-settings.depthBias || eye<camera.nearClipPlane || eye>camera.farClipPlane) continue;
                    for(int channel=0;channel<3;channel++)
                    {
                        double value=surface.linearRadiance[channel];
                        if(surface.blend==FxBlend.Distortion)
                        {
                            double px=x+surface.distortionOffset.x*height,py=y+surface.distortionOffset.y*height;
                            int bx=(int)Math.Floor(px),by=(int)Math.Floor(py); double fx=px-bx,fy=py-by,sum=0,total=0;
                            for(int dy=0;dy<2;dy++) for(int dx=0;dx<2;dx++)
                            {
                                int sx=bx+dx,sy=by+dy; if(sx<0 || sx>=width || sy<0 || sy>=height) continue;
                                int at=sy*width+sx; if(Protected(at) || depths[at]<eye-settings.depthBias) continue;
                                double weight=(dx==0?1-fx:fx)*(dy==0?1-fy:fy); total+=previous[at][channel]*weight; sum+=weight;
                            }
                            value=sum>1e-6?total/sum:previous[index][channel];
                        }
                        output[index][channel]=(float)(value*opacity+previous[index][channel]*(surface.blend==FxBlend.Additive?1:1-opacity));
                    }
                }
            }
            return output;
        }

        private static Color[] LowFxTexturedPlaneReference(Camera camera,LowResolutionFxSurface surface,Color[] input,Color[] depth,float bias,LowResolutionFxSettings settings=null)
        {
            const int width=113,height=79; var output=(Color[])input.Clone(); var inverse=surface.localToWorld.inverse;
            float eye=-camera.worldToCameraMatrix.MultiplyPoint(surface.localToWorld.MultiplyPoint(Vector3.zero)).z;
            var texels=surface.texture!=null?surface.texture.GetPixels():new[]{Color.white}; int tw=surface.texture!=null?surface.texture.width:1,th=surface.texture!=null?surface.texture.height:1;
            Color vertex=surface.vertexColor?surface.mesh.colors[0]:Color.white;
            for(int y=0;y<height;y++) for(int x=0;x<width;x++)
            {
                int index=y*width+x; if(depth[index].r>0 && depth[index].r<eye-bias) continue;
                var world=camera.ViewportToWorldPoint(new Vector3((x+.5f)/width,(y+.5f)/height,eye)); var local=inverse.MultiplyPoint(world);
                if(Math.Abs(local.x)>1 || Math.Abs(local.y)>1) continue;
                double u=(local.x+1)*.5,v=(local.y+1)*.5;
                double px=Math.Max(0,Math.Min(1,u*surface.textureST.x+surface.textureST.z))*tw-.5;
                double py=Math.Max(0,Math.Min(1,v*surface.textureST.y+surface.textureST.w))*th-.5;
                int bx=(int)Math.Floor(px),by=(int)Math.Floor(py); double fx=px-bx,fy=py-by; var color=new double[4];
                for(int dy=0;dy<2;dy++) for(int dx=0;dx<2;dx++)
                {
                    var tap=texels[Math.Max(0,Math.Min(th-1,by+dy))*tw+Math.Max(0,Math.Min(tw-1,bx+dx))];
                    double weight=(dx==0?1-fx:fx)*(dy==0?1-fy:fy); for(int c=0;c<4;c++) color[c]+=tap[c]*weight;
                }
                double fade=1;
                if(surface.radialSoftness>0) { double t=Math.Max(0,Math.Min(1,(1-Math.Sqrt(local.x*local.x+local.y*local.y))/surface.radialSoftness)); fade=t*t*(3-2*t); }
                double alpha=color[3]*surface.opacity*fade*vertex.a;
                if(surface.softIntersectionDistance>0) alpha*=Math.Max(0,Math.Min(1,((depth[index].r==0?camera.farClipPlane:depth[index].r)-eye)/surface.softIntersectionDistance));
                double transmission=1; var fogColor=Color.clear;
                if(surface.fog && settings!=null && settings.fog.enabled && settings.fog.distance.enabled && surface.blend!=FxBlend.Distortion)
                {
                    var near=camera.ViewportToWorldPoint(new Vector3((x+.5f)/width,(y+.5f)/height,camera.nearClipPlane));
                    transmission=Math.Exp(-settings.fog.distance.density*Vector3.Distance(near,world)); fogColor=settings.fog.distance.linearColor;
                }
                for(int c=0;c<3;c++)
                {
                    double radiance=color[c]*vertex[c]*surface.linearRadiance[c]*transmission+(surface.blend==FxBlend.Alpha?fogColor[c]*(1-transmission):0);
                    if(surface.blend==FxBlend.Distortion)
                    {
                        double ox=surface.distortionOffset.x+(surface.texture!=null?color[0]*vertex.r*2-1:0)*surface.distortionTextureScale.x;
                        double oy=surface.distortionOffset.y+(surface.texture!=null?color[1]*vertex.g*2-1:0)*surface.distortionTextureScale.y;
                        double sx=x+ox*height,sy=y+oy*height; int ix=(int)Math.Floor(sx),iy=(int)Math.Floor(sy); double wx=sx-ix,wy=sy-iy,weighted=0,weightSum=0;
                        for(int dy=0;dy<2;dy++) for(int dx=0;dx<2;dx++)
                        {
                            int tx=ix+dx,ty=iy+dy;if(tx<0||tx>=width||ty<0||ty>=height)continue;int at=ty*width+tx;
                            if(depth[at].r>0 && depth[at].r<eye-bias)continue;
                            double weight=(dx==0?1-wx:wx)*(dy==0?1-wy:wy);weighted+=input[at][c]*weight;weightSum+=weight;
                        }
                        radiance=weightSum>1e-6?weighted/weightSum:input[index][c];
                    }
                    output[index][c]=(float)(radiance*alpha+input[index][c]*(surface.blend==FxBlend.Additive?1:1-alpha));
                }
            }
            return output;
        }
    }
}
