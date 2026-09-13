using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyVolumetricReconstruction(Report report)
        {
            yield return null;
            var oldRenderers=FindObjectsOfType<Renderer>();var forced=new bool[oldRenderers.Length];
            for(int i=0;i<oldRenderers.Length;i++){forced[i]=oldRenderers[i].forceRenderingOff;oldRenderers[i].forceRenderingOff=true;}
            var renderer=new VolumetricLightingRenderer();var reference=new VolumetricLightingRenderer();var saved=RenderTexture.active;var oldAnisotropy=QualitySettings.anisotropicFiltering;
            try
            {
                void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"volumetric-reconstruction-"+name,ok,error);
                RenderTexture Target(int w,int h,RenderTextureFormat format,int bits=0)
                {var t=Own(new RenderTexture(w,h,bits,format,RenderTextureReadWrite.Linear){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});t.Create();return t;}
                void Upload(RenderTexture t,Func<int,int,Color> pixel)
                {
                    var data=new Color[t.width*t.height];for(int y=0;y<t.height;y++)for(int x=0;x<t.width;x++)data[y*t.width+x]=pixel(x,y);
                    var image=new Texture2D(t.width,t.height,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point};
                    try{image.SetPixels(data);image.Apply();Graphics.Blit(image,t);}finally{Destroy(image);}
                }
                var host=Own(new GameObject("Reduced volumetric camera"));var camera=host.AddComponent<Camera>();camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;
                camera.nearClipPlane=.3f;camera.farClipPlane=18;camera.fieldOfView=55;camera.orthographicSize=3.5f;camera.aspect=57f/39;camera.cullingMask=1<<25;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.08f,.16f,.3f,.37f);camera.renderingPath=RenderingPath.Forward;
                var source=Target(57,39,RenderTextureFormat.ARGBFloat);var depth=Target(57,39,RenderTextureFormat.RFloat);
                Upload(source,(x,y)=>new Color(.07f+x*.003f,.16f+y*.004f,.3f,.12f+x*.011f));Upload(depth,(x,y)=>new Color(12,0,0,0));
                var settings=new VolumetricLightingSettings{enabled=true,mediumCenter=new Vector3(0,0,6),mediumHalfSize=new Vector3(5,4,5),extinction=.13f,samplesPerLight=256,resolution=VolumetricResolution.Half};
                var light=new VolumetricSpotLight{position=new Vector3(-2,2,1),rotation=Quaternion.LookRotation(new Vector3(.1f,-.1f,1)),range=14,innerAngle=50,outerAngle=80,linearRadiance=new Vector3(12,5,2)};
                settings.lights=new[]{light};int stableBright=0,repairedPixels=0;float lastDense=0;
                VolumetricLightingRenderer.Frame Render(string label,bool dense=true,RenderTexture protection=null,FogDepthEncoding encoding=FogDepthEncoding.LinearEye)
                {
                    if(!renderer.TryRender(source,new FogVolumeDepth(depth,encoding),camera,settings,out var frame,protection))throw new InvalidOperationException(renderer.UnavailableReason);
                    int width=source.width,height=source.height,lowW=(width+(int)settings.resolution-1)/(int)settings.resolution,lowH=(height+(int)settings.resolution-1)/(int)settings.resolution;
                    var selected=settings.resolution;settings.resolution=VolumetricResolution.Full;
                    VolumetricLightingRenderer.Frame full;
                    try{if(!reference.TryRender(source,new FogVolumeDepth(depth,encoding),camera,settings,out full,protection))throw new InvalidOperationException(reference.UnavailableReason);}
                    finally{settings.resolution=selected;}
                    var input=ReadSceneTarget(source);var rawDepth=ReadSceneTarget(depth);var mask=protection!=null?ReadSceneTarget(protection):null;
                    var range=ReadSceneTarget(frame.depthRange);var low=ReadSceneTarget(frame.lowResolutionScattering);var repair=ReadSceneTarget(frame.reconstructionMask);
                    var actual=ReadSceneTarget(frame.scattering);var output=ReadSceneTarget(frame.color);var exact=ReadSceneTarget(full.scattering);
                    var atlas=frame.shadowAtlas!=null?ReadSceneTarget(frame.shadowAtlas):null;int atlasSize=frame.shadowAtlas!=null?frame.shadowAtlas.width:0;
                    var eye=new float[width*height];var sky=new bool[eye.Length];
                    for(int i=0;i<eye.Length;i++)
                    {
                        float value=rawDepth[i].r;sky[i]=encoding==FogDepthEncoding.LinearEye?value==0:SystemInfo.usesReversedZBuffer?value==0:value==1;
                        bool invalid=float.IsNaN(value)||float.IsInfinity(value)||value<0||(mask!=null&&(mask[i].r>0||float.IsNaN(mask[i].r)||float.IsInfinity(mask[i].r)));
                        if(encoding==FogDepthEncoding.LinearEye){invalid|=value!=0&&value<camera.nearClipPlane;eye[i]=sky[i]?camera.farClipPlane:Mathf.Min(value,camera.farClipPlane);}
                        else
                        {
                            invalid|=value>1;float z=SystemInfo.usesReversedZBuffer?value:1-value;
                            eye[i]=camera.orthographic?camera.farClipPlane-z*(camera.farClipPlane-camera.nearClipPlane):camera.nearClipPlane*camera.farClipPlane/(camera.nearClipPlane+z*(camera.farClipPlane-camera.nearClipPlane));
                        }
                        if(invalid||(sky[i]&&!settings.affectSky))eye[i]=-1;
                    }
                    float rangeError=0,lowError=0,denseError=0,denseSum=0,reconstructionError=0,alpha=0,fullDenseError=0,fullDifference=0;int badMask=0,worst=-1;Color worstExpected=Color.clear;stableBright=repairedPixels=0;bool finite=true;
                    for(int y=0;y<lowH;y++)for(int x=0;x<lowW;x++)
                    {
                        int i=y*lowW+x;float nearest=camera.farClipPlane,furthest=0;bool invalid=false;
                        for(int v=y*height/lowH;v<((y+1)*height+lowH-1)/lowH;v++)for(int u=x*width/lowW;u<((x+1)*width+lowW-1)/lowW;u++)
                        {float z=eye[v*width+u];invalid|=z<0;nearest=Mathf.Min(nearest,z);furthest=Mathf.Max(furthest,z);}
                        if(invalid)nearest=furthest=-1;rangeError=Mathf.Max(rangeError,Mathf.Abs(range[i].r-nearest),Mathf.Abs(range[i].g-furthest));
                        if(dense&&nearest>=0)
                        {
                            var a=camera.ViewportToWorldPoint(new Vector3((x+.5f)/lowW,(y+.5f)/lowH,camera.nearClipPlane));var b=camera.ViewportToWorldPoint(new Vector3((x+.5f)/lowW,(y+.5f)/lowH,nearest));
                            var expected=VolumeDenseReference(settings,a,b,false,atlas,atlasSize,2048);for(int c=0;c<3;c++)lowError=Mathf.Max(lowError,Mathf.Abs(expected[c]-low[i][c]));
                        }
                        if(nearest<0)for(int c=0;c<4;c++)lowError=Mathf.Max(lowError,Mathf.Abs(low[i][c]));
                    }
                    for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                    {
                        int i=y*width+x,nx=(2*x+1)*lowW-width,ny=(2*y+1)*lowH-height;double px=nx/(2.0*width),py=ny/(2.0*height);int bx=(int)Math.Floor(px),by=(int)Math.Floor(py);
                        float fx=(nx-bx*2*width)/(2f*width),fy=(ny-by*2*height)/(2f*height);
                        var minimum=Vector3.one*1e30f;var maximum=Vector3.one*-1e30f;var sum=Color.clear;bool needs=px<0||py<0||px>lowW-1||py>lowH-1;float tol=settings.reconstruction.depthAbsoluteTolerance+eye[i]*settings.reconstruction.depthRelativeTolerance;
                        for(int v=0;v<2;v++)for(int u=0;u<2;u++)
                        {
                            float weight=(u==0?1-fx:fx)*(v==0?1-fy:fy);if(weight<=0)continue;
                            int tap=Mathf.Clamp(by+v,0,lowH-1)*lowW+Mathf.Clamp(bx+u,0,lowW-1);var r=range[tap];sum+=low[tap]*weight;
                            needs|=r.r<0||r.g-r.r>tol||Mathf.Abs(eye[i]-r.r)>tol||Mathf.Abs(eye[i]-r.g)>tol;
                            for(int c=0;c<3;c++){minimum[c]=Mathf.Min(minimum[c],low[tap][c]);maximum[c]=Mathf.Max(maximum[c],low[tap][c]);}
                        }
                        for(int c=0;c<3;c++)needs|=maximum[c]-minimum[c]>settings.reconstruction.radianceAbsoluteTolerance+maximum[c]*settings.reconstruction.radianceRelativeTolerance;
                        needs&=eye[i]>=0&&settings.extinction>0;if((repair[i].r>.5f)!=needs)badMask++;
                        if(needs)repairedPixels++;else if(eye[i]>=0&&actual[i].r>1e-4f)stableBright++;
                        var expectedScatter=eye[i]<0?Color.clear:needs?exact[i]:sum;
                        for(int c=0;c<3;c++){reconstructionError=Mathf.Max(reconstructionError,Mathf.Abs(actual[i][c]-expectedScatter[c]));fullDifference=Mathf.Max(fullDifference,Mathf.Abs(actual[i][c]-exact[i][c]));}
                        alpha=Mathf.Max(alpha,Mathf.Abs(output[i].a-input[i].a),Mathf.Abs(actual[i].a));
                        for(int c=0;c<4;c++)finite&=!float.IsNaN(output[i][c])&&!float.IsInfinity(output[i][c]);
                        if(dense)
                        {
                            var a=camera.ViewportToWorldPoint(new Vector3((x+.5f)/width,(y+.5f)/height,camera.nearClipPlane));var b=camera.ViewportToWorldPoint(new Vector3((x+.5f)/width,(y+.5f)/height,eye[i]));
                            var expected=eye[i]<0?new Color(0,0,0,1):VolumeDenseReference(settings,a,b,sky[i],atlas,atlasSize,2048);
                            for(int c=0;c<3;c++){float error=Mathf.Abs(output[i][c]-(input[i][c]*expected.a+expected[c]));if(error>denseError){denseError=error;worst=i;worstExpected=expected;}denseSum+=error;fullDenseError=Mathf.Max(fullDenseError,Mathf.Abs(exact[i][c]-expected[c]));}
                        }
                    }
                    Check(label+"-current-full-output-and-low-grid",frame.IsCurrent&&frame.scattering.width==width&&frame.scattering.height==height&&frame.lowResolutionScattering.width==lowW&&frame.lowResolutionScattering.height==lowH&&renderer.DrawCalls==2*renderer.LightCount+4&&renderer.ReintegrationDrawCalls==renderer.LightCount);
                    Check(label+"-opaque-range-whole-image",rangeError<.00003f,rangeError);Check(label+"-mask-exact-all-pixels",badMask==0,badMask);
                    Check(label+"-full-reconstruction-whole-image",reconstructionError<.00002f,reconstructionError);Check(label+"-finite-exact-alpha",finite&&alpha==0,alpha);
                    Check(label+"-complete-full-resolution-reference",fullDifference<.004f,fullDifference);
                    if(dense){Check(label+"-reduced-world-integral",lowError<.004f,lowError);Check(label+"-full-dense-world-quality",denseError<.004f,denseError);Check(label+"-full-dense-mean-quality",denseSum/(width*height*3)<.0003f,denseSum/(width*height*3));
                        Debug.Log("[ReducedVolumeDiagnostic] "+label+" dense="+denseError.ToString("R")+" full="+fullDenseError.ToString("R")+" worst="+(worst%width)+","+(worst/width)+" repair="+(worst>=0?repair[worst].r:0)+" scatter="+(worst>=0?actual[worst]:Color.clear)+" expected="+worstExpected+" stableLit="+stableBright+" replay="+repairedPixels);}
                    lastDense=denseError;return frame;
                }
                var first=Render("half-broad-spot");Check("nonempty-lit-low-reconstruction",stableBright>20,stableBright);
                SaveSsrPreview("volumetric-reduced-half",ReadSceneTarget(first.color),57,39,false);
                foreach(var resolution in new[]{VolumetricResolution.Half,VolumetricResolution.Quarter})
                {
                    settings.resolution=resolution;Upload(depth,(x,y)=>new Color(x==27||y==17?2.7f:x>42?0:12,0,0,0));
                    var edge=Render("thin-cross-"+resolution);Check("thin-cross-nonempty-reintegration-"+resolution,repairedPixels>30,repairedPixels);
                    SaveSsrPreview("volumetric-reduced-"+resolution+"-mask",ReadSceneTarget(edge.reconstructionMask),57,39,false);
                }
                var protection=Target(57,39,RenderTextureFormat.R8);Upload(protection,(x,y)=>new Color(x<5||y==20?1:0,0,0,0));Render("protected-thin-cross",true,protection);
                settings.affectSky=false;Render("unaffected-sky");settings.affectSky=true;
                Upload(depth,(x,y)=>new Color(x<7?-1:x<14?float.NaN:x<21?.1f:12,0,0,0));Render("invalid-depth-is-not-sky");Upload(depth,(x,y)=>new Color(12,0,0,0));
                foreach(int projection in new[]{1,2})
                {
                    camera.orthographic=projection==1;camera.ResetProjectionMatrix();if(projection==2){var p=camera.projectionMatrix;p.m02=.21f;p.m12=-.19f;camera.projectionMatrix=p;}
                    Render("projection-"+projection);
                }
                camera.orthographic=false;camera.ResetProjectionMatrix();camera.transform.SetPositionAndRotation(new Vector3(.2f,-.1f,3),Quaternion.Euler(4,-7,2));Render("inside-moving-camera");camera.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                var second=new VolumetricSpotLight{position=new Vector3(1,-1,2),rotation=Quaternion.LookRotation(new Vector3(-.2f,.1f,1)),range=12,innerAngle=45,outerAngle=75,linearRadiance=new Vector3(2,7,10)};
                settings.lights=new[]{light,second};Render("two-colored-ordered-lights");settings.lights=new[]{light};
                var blocker=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));blocker.layer=25;blocker.transform.SetPositionAndRotation(light.position+light.rotation*new Vector3(.2f,-.1f,3),light.rotation);blocker.transform.localScale=new Vector3(1.5f,1.3f,1);
                blocker.GetComponent<Renderer>().sharedMaterial=Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback")));
                light.shadow.enabled=true;light.shadow.depthBias=0;light.shadow.normalBias=0;settings.shadows.tileResolution=256;settings.shadows.casters=new[]{new SceneShadowCaster{renderer=blocker.GetComponent<Renderer>(),cull=CullMode.Off}};
                settings.resolution=VolumetricResolution.Half;var shadow=Render("actual-shadowed-spot");var before=ReadSceneTarget(shadow.color);
                blocker.transform.position+=light.rotation*new Vector3(.65f,.3f,0);var moved=Render("moved-shadow-caster");Check("moving-shadow-changes-reconstructed-volume",!ScenePixelsEqual(before,ReadSceneTarget(moved.color)));
                light.shadow.filter=SceneShadowFilter.Pcf3x3;Render("pcf-shadow");light.shadow.filter=SceneShadowFilter.Hard;
                var samplerBase=ReadSceneTarget(Render("sampler-baseline",false).color);source.filterMode=depth.filterMode=FilterMode.Trilinear;source.wrapMode=depth.wrapMode=TextureWrapMode.Repeat;source.anisoLevel=depth.anisoLevel=16;QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;
                Check("sampler-independent",ScenePixelsEqual(samplerBase,ReadSceneTarget(Render("sampler-forced",false).color)));QualitySettings.anisotropicFiltering=oldAnisotropy;
                settings.attenuateBackground=false;Render("additive-only");settings.attenuateBackground=true;
                settings.extinction=0;var vacuum=Render("zero-extinction");Check("vacuum-input-exact",ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(vacuum.color)));settings.extinction=.13f;
                for(int targetIndex=0;targetIndex<3;targetIndex++)
                {
                    var lease=Render("before-target-loss-"+targetIndex,false);var targets=new[]{lease.lowResolutionScattering,lease.depthRange,lease.reconstructionMask};targets[targetIndex].Release();
                    Check("lost-owned-guide-invalidates-"+targetIndex,!lease.IsCurrent&&!renderer.TryGetFrame(out _));Render("recreated-target-"+targetIndex,false);
                }
                var leaseBeforeFull=Render("before-full-restore",false);settings.resolution=VolumetricResolution.Full;
                Check("full-mode-releases-added-targets",renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out var restored)&&!leaseBeforeFull.IsCurrent&&restored.lowResolutionScattering==null&&restored.depthRange==null&&restored.reconstructionMask==null&&renderer.ReintegrationDrawCalls==0&&renderer.TargetCount==3&&ScenePixelsEqual(ReadSceneTarget(restored.color),ReadSceneTarget(reference.TryGetFrame(out var referenceFrame)?referenceFrame.color:null)));
                settings.resolution=(VolumetricResolution)3;Check("invalid-resolution-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _)&&renderer.TargetCount==0);settings.resolution=VolumetricResolution.Half;
                settings.reconstruction.depthAbsoluteTolerance=float.NaN;Check("invalid-reconstruction-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _));settings.reconstruction.depthAbsoluteTolerance=.005f;
                foreach(var size in new[]{new Vector2Int(1,1),new Vector2Int(1,7),new Vector2Int(63,41)})
                {
                    source=Target(size.x,size.y,RenderTextureFormat.ARGBHalf);depth=Target(size.x,size.y,RenderTextureFormat.RHalf);camera.aspect=size.x/(float)size.y;Upload(source,(x,y)=>new Color(.3f,2,.8f,.375f));Upload(depth,(x,y)=>new Color(12,0,0,0));Render("half-hdr-odd-size-"+size.x+"x"+size.y);
                }
                light.shadow.enabled=false;
                source=Target(47,29,RenderTextureFormat.RGB111110Float);depth=Target(47,29,RenderTextureFormat.RFloat);camera.aspect=47f/29;
                Upload(source,(x,y)=>new Color(.3f,2,.8f,1));Upload(depth,(x,y)=>new Color(12,0,0,0));Render("packed-r11g11b10-hdr");
                foreach(var size in new[]{new Vector2Int(171,117),new Vector2Int(342,234)})
                {
                    source=Target(size.x,size.y,RenderTextureFormat.ARGBFloat);depth=Target(size.x,size.y,RenderTextureFormat.RFloat);camera.aspect=size.x/(float)size.y;
                    Upload(source,(x,y)=>new Color(.3f,2,.8f,.375f));Upload(depth,(x,y)=>new Color(12,0,0,0));var scaled=Render("same-view-spatial-scale-"+size.x,false);
                    long rays=(long)scaled.lowResolutionScattering.width*scaled.lowResolutionScattering.height+repairedPixels;
                    Check("same-view-lit-reconstruction-"+size.x,stableBright>size.x,stableBright);Check("same-view-fewer-integrated-rays-"+size.x,rays<(long)size.x*size.y,(float)rays);
                }
                light.shadow.enabled=true;
                var budgetSource=Target(256,256,RenderTextureFormat.ARGBFloat);var budgetDepth=Target(256,256,RenderTextureFormat.RFloat);settings.reconstruction.maximumTargetMiB=1;
                Check("budget-rejected-before-owned-allocation",!renderer.TryRender(budgetSource,new FogVolumeDepth(budgetDepth),camera,settings,out _)&&renderer.TargetCount==0&&renderer.TargetBytes==0&&budgetSource.IsCreated()&&budgetDepth.IsCreated());settings.reconstruction.maximumTargetMiB=512;
                var aliasFrame=Render("before-owned-alias",false);Check("borrowed-output-alias-rejected",!renderer.TryRender(aliasFrame.color,new FogVolumeDepth(depth),camera,settings,out _)&&!aliasFrame.IsCurrent);
                settings.enabled=false;Check("disabled-no-targets",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _)&&renderer.TargetCount==0);settings.enabled=true;
                var final=Render("before-dispose",false);renderer.Dispose();Check("dispose-all-added-targets",!final.IsCurrent&&!final.lowResolutionScattering.IsCreated()&&!final.depthRange.IsCreated()&&!final.reconstructionMask.IsCreated());
                // Current actual opaque geometry and the production post bridge, not a supplied screen image.
                const int actualWidth=97,actualHeight=65;camera.aspect=actualWidth/(float)actualHeight;camera.depthTextureMode=DepthTextureMode.Depth;
                var target=Target(actualWidth,actualHeight,RenderTextureFormat.ARGBFloat,24);var actualDepth=Target(actualWidth,actualHeight,RenderTextureFormat.RFloat);camera.targetTexture=target;
                var wall=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));wall.layer=25;wall.transform.position=new Vector3(.35f,-.1f,3);wall.transform.localScale=new Vector3(.45f,2.5f,1);wall.GetComponent<Renderer>().sharedMaterial=blocker.GetComponent<Renderer>().sharedMaterial;
                var post=host.AddComponent<OriginalStyleRenderPipeline>();post.volumetricLighting=settings;Color[] actualInput=null;
                post.volumetricDepthProvider=(view,input)=>{actualInput=ReadSceneTarget(input);Graphics.Blit(Shader.GetGlobalTexture("_CameraDepthTexture"),actualDepth);return new FogVolumeDepth(actualDepth,FogDepthEncoding.Device);};
                bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_VOLUMETRIC_REDUCED")=="1",started=false,ended=false;
                try
                {
                    if(capture)started=RenderDocCaptureBridge.BeginOffscreenCapture();
                    for(int step=0;step<2;step++)
                    {
                        settings.resolution=step==0?VolumetricResolution.Half:VolumetricResolution.Quarter;camera.orthographic=step==1;camera.ResetProjectionMatrix();
                        if(step==0){var p=camera.projectionMatrix;p.m02=.17f;p.m12=-.21f;camera.projectionMatrix=p;}
                        camera.transform.position=new Vector3(step*.1f,-step*.06f,0);blocker.transform.position+=light.rotation*new Vector3(-.3f,.1f,0);camera.Render();
                        if(!post.TryGetVolumetricLightingFrame(out var actual))throw new InvalidOperationException("Actual reduced volume frame missing: "+post.VolumetricLightingUnavailableReason);
                        var z=ReadSceneTarget(actualDepth);var output=ReadSceneTarget(actual.color);var atlas=ReadSceneTarget(actual.shadowAtlas);var repair=ReadSceneTarget(actual.reconstructionMask);var scatter=ReadSceneTarget(actual.scattering);
                        float maximum=0,mean=0,alpha=0;int surfaces=0,skyPixels=0,repairCount=0,lowLit=0;
                        for(int y=0;y<actualHeight;y++)for(int x=0;x<actualWidth;x++)
                        {
                            int i=y*actualWidth+x;bool sky=z[i].r==0;float eye=sky?camera.farClipPlane:camera.orthographic?camera.farClipPlane-z[i].r*(camera.farClipPlane-camera.nearClipPlane):camera.nearClipPlane*camera.farClipPlane/(camera.nearClipPlane+z[i].r*(camera.farClipPlane-camera.nearClipPlane));
                            if(sky)skyPixels++;else surfaces++;if(repair[i].r>.5f)repairCount++;else if(scatter[i].r>1e-4f)lowLit++;
                            var a=camera.ViewportToWorldPoint(new Vector3((x+.5f)/actualWidth,(y+.5f)/actualHeight,camera.nearClipPlane));var b=camera.ViewportToWorldPoint(new Vector3((x+.5f)/actualWidth,(y+.5f)/actualHeight,eye));
                            var expected=VolumeDenseReference(settings,a,b,sky,atlas,actual.shadowAtlas.width,2048);
                            for(int c=0;c<3;c++){float error=Mathf.Abs(output[i][c]-(actualInput[i][c]*expected.a+expected[c]));maximum=Mathf.Max(maximum,error);mean+=error;}
                            alpha=Mathf.Max(alpha,Mathf.Abs(output[i].a-actualInput[i].a));
                        }
                        Check("actual-camera-dense-whole-image-"+step,maximum<.004f,maximum);Check("actual-camera-dense-mean-"+step,mean/(actualWidth*actualHeight*3)<.0003f,mean/(actualWidth*actualHeight*3));
                        Check("actual-camera-opaque-and-repair-"+step,alpha==0&&surfaces>20&&skyPixels>20&&repairCount>20,repairCount);Check("actual-camera-lit-low-path-"+step,lowLit>20,lowLit);
                        SaveSsrPreview("volumetric-reduced-actual-camera-"+step,ReadSceneTarget(target),actualWidth,actualHeight,false);SaveSsrPreview("volumetric-reduced-actual-mask-"+step,repair,actualWidth,actualHeight,false);
                    }
                }
                finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                if(capture)Check("requested-native-capture",started&&ended);
                post.volumetricDepthProvider=null;camera.Render();Check("missing-depth-invalidates-all-guides",!post.TryGetVolumetricLightingFrame(out _)&&post.VolumetricLightingUnavailableReason!=null);
                settings.enabled=false;camera.Render();var legacy=ReadSceneTarget(target);settings.enabled=true;camera.Render();settings.enabled=false;camera.Render();
                Check("post-default-restored-exact",ScenePixelsEqual(legacy,ReadSceneTarget(target)));post.enabled=false;camera.targetTexture=null;host.SetActive(false);
            }
            finally
            {
                renderer.Dispose();reference.Dispose();QualitySettings.anisotropicFiltering=oldAnisotropy;RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;
                for(int i=0;i<oldRenderers.Length;i++)if(oldRenderers[i]!=null)oldRenderers[i].forceRenderingOff=forced[i];foreach(var obj in _owned)if(obj!=null)Destroy(obj);_owned.Clear();
            }
        }
    }
}
