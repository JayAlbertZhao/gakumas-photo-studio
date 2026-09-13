using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyBokeh(Report report)
        {
            yield return null;
            var previous=FindObjectsOfType<Renderer>();var forced=new bool[previous.Length];
            for(int i=0;i<previous.Length;i++){forced[i]=previous[i].forceRenderingOff;previous[i].forceRenderingOff=true;}
            var renderer=new BokehDepthOfFieldRenderer();var savedActive=RenderTexture.active;var savedAnisotropy=QualitySettings.anisotropicFiltering;
            try
            {
                void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"bokeh-"+name,ok,error);
                RenderTexture Target(int w,int h,RenderTextureFormat format)
                {
                    var t=Own(new RenderTexture(w,h,0,format,RenderTextureReadWrite.Linear){name="Bokeh fixture "+format+" "+w+"x"+h,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});t.Create();return t;
                }
                void Upload(RenderTexture t,Func<int,int,Color> pixel)
                {
                    var texture=new Texture2D(t.width,t.height,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point};var data=new Color[t.width*t.height];
                    for(int y=0;y<t.height;y++)for(int x=0;x<t.width;x++)data[y*t.width+x]=pixel(x,y);
                    var active=RenderTexture.active;
                    try{texture.SetPixels(data);texture.Apply();Graphics.Blit(texture,t);}
                    finally{RenderTexture.active=active;Destroy(texture);}
                }
                var settings=new BokehDepthOfFieldSettings();var source=Target(97,81,RenderTextureFormat.ARGBFloat);var depth=Target(97,81,RenderTextureFormat.RFloat);
                Upload(source,(x,y)=>new Color(.2f+x*.006f,.1f+y*.007f,.35f+.13f*Mathf.Sin(x*.23f),.15f+.7f*x/96));
                Upload(depth,(x,y)=>new Color(x<12?.5f:x<24?1.5f:x<36?2:x<48?3.5f:x<60?5:x<72?9:x<84?13:0,0,0,1));
                Check("default-off-no-targets",!renderer.TryRender(source,depth,settings,out _)&&renderer.TargetCount==0);
                settings.enabled=true;settings.maximumRadius=.06f;
                foreach(var budget in new[]{BokehSampleCount.Samples30,BokehSampleCount.Samples43})foreach(int blades in new[]{3,5,9})foreach(float curve in new[]{0f,.5f,1})foreach(float rotation in new[]{-160f,37f})
                {
                    settings.sampleCount=budget;settings.bladeCount=blades;settings.bladeCurvature=curve;settings.bladeRotation=rotation;
                    var kernel=new Vector4[42];int count=BokehKernel.Fill(settings,81f/97,kernel),index=0;double maximum=0;
                    for(int ring=1;ring<=3;ring++)
                    {
                        int points=budget==BokehSampleCount.Samples43?7*ring:ring==1?7:ring==2?9:13;
                        for(int p=0;p<points;p++)
                        {
                            double angle=2*Math.PI*p/points,edgeProjection=0;
                            for(int edge=0;edge<blades;edge++)edgeProjection=Math.Max(edgeProjection,Math.Cos(angle-2*Math.PI*edge/blades));
                            double boundary=Math.Cos(Math.PI/blades)/edgeProjection,r=(ring+1.0/7)/(3+1.0/7)*Math.Pow(boundary,1-curve)*settings.maximumRadius;
                            double x=r*Math.Cos(angle-rotation*Math.PI/180),y=r*Math.Sin(angle-rotation*Math.PI/180);
                            maximum=Math.Max(maximum,Math.Abs(kernel[index].x-x));maximum=Math.Max(maximum,Math.Abs(kernel[index].y-y));index++;
                        }
                    }
                    bool padding=true;for(int k=index;k<42;k++)padding&=kernel[k]==Vector4.zero;
                    Check("kernel-polygon-plane-intersection-"+(int)budget+"-blades-"+blades+"-curve-"+curve+"-rotation-"+rotation,count==index&&count==(int)budget-1&&padding&&maximum<.0000001,(float)maximum);
                }
                settings.sampleCount=BokehSampleCount.Samples30;settings.bladeCount=5;settings.bladeCurvature=1;settings.bladeRotation=0;
                BokehDepthOfFieldRenderer.Frame Render(string name,bool oracle=true)
                {
                    var active=RenderTexture.active;
                    if(!renderer.TryRender(source,depth,settings,out var frame))throw new InvalidOperationException(name+": "+renderer.UnavailableReason);
                    Check(name+"-lease-targets-draw-budget",frame.IsCurrent&&renderer.TryGetFrame(out _)&&renderer.TargetCount==(settings.nearBlur>0?11:6)&&renderer.DrawCalls==(settings.nearBlur>0?11:6)&&renderer.ColorSamplesPerGather==(int)settings.sampleCount);
                    Check(name+"-restores-active-target",RenderTexture.active==active);
                    var raw=ReadSceneTarget(source);var result=ReadSceneTarget(frame.color);float alpha=0;bool finite=true;
                    for(int i=0;i<raw.Length;i++){alpha=Mathf.Max(alpha,Mathf.Abs(raw[i].a-result[i].a));for(int c=0;c<4;c++)finite&=!float.IsNaN(result[i][c])&&!float.IsInfinity(result[i][c]);}
                    Check(name+"-finite-hdr-current-alpha",finite&&alpha<2e-5f,alpha);
                    if(oracle)
                    {
                        var actual=ReadSceneTarget(frame.encodedCoC);var input=ReadSceneTarget(depth);float error=0,cpuError=0;
                        for(int i=0;i<input.Length;i++){float expected=(float)(BokehReferenceRadius(settings,input[i].r)/settings.maximumRadius*.5+.5);error=Mathf.Max(error,Mathf.Abs(actual[i].r-expected));cpuError=Mathf.Max(cpuError,Mathf.Abs(settings.EvaluateRadius(input[i].r)/settings.maximumRadius*.5f+.5f-expected));}
                        Check(name+"-whole-image-coc-double-oracle",error<2e-5f,error);Check(name+"-public-cpu-radius-double-oracle",cpuError<2e-5f,cpuError);
                    }
                    return frame;
                }
                var first=Render("manual-slab-30");
                bool gatherCapture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_BOKEH_GATHER")=="1";
                bool gatherStarted=gatherCapture&&RenderDocCaptureBridge.BeginOffscreenCapture();
                foreach(var budget in new[]{BokehSampleCount.Samples30,BokehSampleCount.Samples43})
                {
                    settings.sampleCount=budget;var f=Render("manual-slab-"+(int)budget+"-repeat");
                    float farError=BokehGatherError(f,settings,false,out var farNearest),nearError=BokehGatherError(f,settings,true,out var nearNearest);
                    Check("far-gather-all-pixels-sampling-interval-"+(int)budget,farError<.000005f,farError);Check("near-gather-premultiplied-coverage-all-pixels-sampling-interval-"+(int)budget,nearError<.000005f,nearError);
                    Check("far-nearest-grid-reference-difference-"+(int)budget,!float.IsNaN(farNearest),farNearest);Check("near-nearest-grid-reference-difference-"+(int)budget,!float.IsNaN(nearNearest),nearNearest);
                    Check("maximum-blur-fetch-budget-"+(int)budget,renderer.MaximumBlurColorSamplesPerPixel==2*(int)budget);
                }
                if(gatherCapture)Check("requested-native-gather-capture",gatherStarted&&RenderDocCaptureBridge.EndOffscreenCapture());
                var samplerBaseline=ReadSceneTarget(Render("sampler-baseline").color);
                source.filterMode=FilterMode.Trilinear;source.wrapMode=TextureWrapMode.Repeat;source.anisoLevel=16;QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;
                Check("explicit-clamp-samplers-ignore-caller-and-forced-anisotropy",ScenePixelsEqual(samplerBaseline,ReadSceneTarget(Render("forced-anisotropy").color)));
                source.filterMode=FilterMode.Point;source.wrapMode=TextureWrapMode.Clamp;source.anisoLevel=0;QualitySettings.anisotropicFiltering=savedAnisotropy;
                Check("previous-frame-invalidated",!first.IsCurrent);
                settings.focusMode=BokehFocusMode.Physical;
                foreach(float aperture in new[]{.05f,2,128})foreach(float sensor in new[]{.1f,24,1000})
                {settings.fNumber=aperture;settings.sensorHeightMillimetres=sensor;Render("physical-aperture-"+aperture+"-sensor-"+sensor);}
                settings.focusMode=BokehFocusMode.FocusRange;settings.fNumber=2;settings.sensorHeightMillimetres=24;
                foreach(float near in new[]{0f,.4f,1})foreach(float far in new[]{0f,.3f,1})
                {settings.nearBlur=near;settings.farBlur=far;Render("independent-near-"+near+"-far-"+far);}
                settings.nearBlur=settings.farBlur=1;

                // The near prefilter packs CoC in A, never in color R. Two adversarial colors.
                Upload(source,(x,y)=>new Color(0,2,0,.37f));Upload(depth,(x,y)=>new Color(.5f,0,0,1));
                foreach(var budget in new[]{BokehSampleCount.Samples30,BokehSampleCount.Samples43})
                {
                    settings.sampleCount=budget;var f=Render("green-near-"+(int)budget);float error=0,minRadius=float.MaxValue;
                    foreach(var p in ReadSceneTarget(f.inflatedNear))minRadius=Mathf.Min(minRadius,p.r);
                    foreach(var p in ReadSceneTarget(f.blurredNear)){error=Mathf.Max(error,Mathf.Abs(p.a-1));error=Mathf.Max(error,Mathf.Abs(p.g-2));error=Mathf.Max(error,Mathf.Abs(p.r));}
                    float expectedRadius=(float)-BokehReferenceRadius(settings,.5);
                    Check("red-zero-foreground-inflates-coc-alpha-"+(int)budget,Mathf.Abs(minRadius-expectedRadius)<.000001f&&error<.0001f,Mathf.Max(error,Mathf.Abs(minRadius-expectedRadius)));
                }
                Upload(source,(x,y)=>new Color(12,0,0,.61f));Upload(depth,(x,y)=>new Color(13,0,0,1));var farOnly=Render("red-far-negative-control");float falseNear=0;
                foreach(var p in ReadSceneTarget(farOnly.inflatedNear))falseNear=Mathf.Max(falseNear,Mathf.Abs(p.r));
                foreach(var p in ReadSceneTarget(farOnly.near))falseNear=Mathf.Max(falseNear,p.maxColorComponent);
                Check("red-bright-background-does-not-create-near-coverage",falseNear==0,falseNear);
                Upload(source,(x,y)=>new Color(64000,32000,8000,.23f));var hdr=Render("large-constant-hdr");float hdrError=0;
                foreach(var p in ReadSceneTarget(hdr.color)){hdrError=Mathf.Max(hdrError,Mathf.Abs(p.r/64000-1));hdrError=Mathf.Max(hdrError,Mathf.Abs(p.g/32000-1));hdrError=Mathf.Max(hdrError,Mathf.Abs(p.b/8000-1));}
                Check("large-hdr-constant-no-brightness-loss",hdrError<.00002f,hdrError);

                // Smooth radiance has a numerical dense disk control independent of the ring kernel.
                settings.nearBlur=0;settings.maximumRadius=.06f;
                source=Target(192,128,RenderTextureFormat.ARGBFloat);depth=Target(192,128,RenderTextureFormat.RFloat);
                Upload(source,(x,y)=>new Color(.5f+.4f*Mathf.Sin((x+.5f)/192*17),.5f+.4f*Mathf.Cos((y+.5f)/128*13),.5f+.3f*Mathf.Sin((x+y)*.04f),.75f));Upload(depth,(x,y)=>new Color(20,0,0,1));
                foreach(var budget in new[]{BokehSampleCount.Samples30,BokehSampleCount.Samples43})
                {
                    settings.sampleCount=budget;var f=Render("smooth-disk-"+(int)budget);var pre=ReadSceneTarget(f.prefilterFar);var actual=ReadSceneTarget(f.blurredFar);double sum=0;int count=0;
                    int w=f.prefilterFar.width,h=f.prefilterFar.height;
                    for(int y=8;y<h-8;y+=3)for(int x=8;x<w-8;x+=3)
                    {
                        Color dense=Color.clear;
                        for(int k=0;k<1024;k++){double r=Math.Sqrt((k+.5)/1024)*settings.maximumRadius*.875,phi=k*2.399963229728653;dense+=TaaCurrent(pre,w,h,x+(float)(r*Math.Cos(phi)*h),y+(float)(r*Math.Sin(phi)*h))/1024;}
                        for(int c=0;c<3;c++){sum+=Math.Abs(actual[y*w+x][c]-dense[c]);count++;}
                    }
                    Check("dense-1024-disk-smooth-radiance-mae-"+(int)budget,count>1000&&sum/count<.03,(float)(sum/count));
                    Check("far-only-prunes-five-near-targets-"+(int)budget,f.near==null&&renderer.TargetCount==6&&renderer.MaximumBlurColorSamplesPerPixel==(int)budget);
                }

                // Focus slab preserves both separated subject depths, including a reaching hand.
                source=Target(512,256,RenderTextureFormat.ARGBFloat);depth=Target(512,256,RenderTextureFormat.RFloat);
                bool Face(int x,int y)=>x>=208&&x<304&&y>=48&&y<208;
                bool Hand(int x,int y)=>x>=128&&x<208&&y>=104&&y<152;
                Upload(source,(x,y)=>Face(x,y)?(((x/4+y/4)&1)==0?new Color(.95f,.62f,.37f,1):new Color(.18f,.23f,.34f,1)):Hand(x,y)?(((x/4+y/4)&1)==0?new Color(.98f,.74f,.5f,1):new Color(.2f,.31f,.42f,1)):(((x/3+y/3)&1)==0?new Color(.15f,.42f,.8f,1):new Color(.03f,.08f,.14f,1)));
                Upload(depth,(x,y)=>new Color(Face(x,y)?4:Hand(x,y)?2.5f:16,0,0,1));settings.sampleCount=BokehSampleCount.Samples30;settings.maximumRadius=.035f;settings.nearBlur=1;
                var focused=Render("face-and-reaching-hand");var sharp=ReadSceneTarget(source);var softened=ReadSceneTarget(focused.color);float faceError=0,handError=0;double backgroundChange=0;int backgroundCount=0;
                for(int y=0;y<256;y++)for(int x=0;x<512;x++)for(int c=0;c<3;c++)
                {
                    float e=Mathf.Abs(sharp[y*512+x][c]-softened[y*512+x][c]);
                    if(Face(x,y))faceError=Mathf.Max(faceError,e);else if(Hand(x,y))handError=Mathf.Max(handError,e);else if(x<96||x>336){backgroundChange+=e;backgroundCount++;}
                }
                Check("entire-face-depth-stays-sharp",faceError<.00002f,faceError);Check("entire-reaching-hand-depth-stays-sharp",handError<.00002f,handError);
                Check("independent-background-blur-nonvacuous",backgroundCount>10000&&backgroundChange/backgroundCount>.05,(float)(backgroundChange/backgroundCount));
                SaveSsrPreview("bokeh-manual-focus-input",sharp,512,256,false);SaveSsrPreview("bokeh-manual-focus-30",softened,512,256,false);
                settings.sampleCount=BokehSampleCount.Samples43;var fortyThree=Render("face-and-hand-43");SaveSsrPreview("bokeh-manual-focus-43",ReadSceneTarget(fortyThree.color),512,256,false);

                // Resolution/aspect contracts, including floor-halving and degenerate dimensions.
                settings.nearBlur=0;
                foreach(var size in new[]{new Vector2Int(1,1),new Vector2Int(1,17),new Vector2Int(19,1),new Vector2Int(97,81),new Vector2Int(194,162),new Vector2Int(193,81)})
                {
                    source=Target(size.x,size.y,RenderTextureFormat.ARGBFloat);depth=Target(size.x,size.y,RenderTextureFormat.RFloat);
                    Upload(source,(x,y)=>new Color(.3f,.7f,1.2f,.43f));Upload(depth,(x,y)=>new Color(20,0,0,1));var f=Render("dimension-"+size.x+"x"+size.y);
                    Check("constant-dimension-preserved-"+size.x+"x"+size.y,PixelError(ReadSceneTarget(source),ReadSceneTarget(f.color))<.00001f);
                    var kernel=new Vector4[42];BokehKernel.Fill(settings,size.y/(float)size.x,kernel);float geometryError=0;
                    foreach(var k in kernel)geometryError=Mathf.Max(geometryError,Mathf.Abs(k.w*size.x-k.x*size.y));
                    Check("kernel-image-height-pixel-isotropy-"+size.x+"x"+size.y,geometryError<.00001f,geometryError);
                }
                settings.nearBlur=1;
                // Actual GPU footprint: convolving a centered Gaussian adds the
                // kernel's second moment. Bilinear reconstruction adds at most
                // half a half-resolution pixel squared across the two axes.
                settings.nearBlur=0;
                foreach(var budget in new[]{BokehSampleCount.Samples30,BokehSampleCount.Samples43})foreach(var size in new[]{new Vector2Int(256,256),new Vector2Int(512,512),new Vector2Int(512,256),new Vector2Int(257,193)})
                {
                    settings.sampleCount=budget;source=Target(size.x,size.y,RenderTextureFormat.ARGBFloat);depth=Target(size.x,size.y,RenderTextureFormat.RFloat);float aspect=size.x/(float)size.y;
                    Upload(source,(x,y)=>{float u=((x+.5f)/size.x-.5f)*aspect,v=(y+.5f)/size.y-.5f;float value=Mathf.Exp(-(u*u+v*v)/(2*.015f*.015f));return new Color(value,value*.6f,value*.2f,1);});Upload(depth,(x,y)=>new Color(20,0,0,1));
                    var f=Render("gpu-footprint-"+(int)budget+"-"+size.x+"x"+size.y);var before=BokehMoment(ReadSceneTarget(f.prefilterFar),f.prefilterFar.width,f.prefilterFar.height,aspect);var after=BokehMoment(ReadSceneTarget(f.blurredFar),f.blurredFar.width,f.blurredFar.height,aspect);
                    var kernel=new Vector4[42];int n=BokehKernel.Fill(settings,1/aspect,kernel);double mx=0,my=0,second=0;
                    for(int k=0;k<n;k++){double x=kernel[k].x*.875,y=kernel[k].y*.875;mx+=x/(n+1);my+=y/(n+1);second+=(x*x+y*y)/(n+1);}
                    double expected=before[3]+second-mx*mx-my*my,difference=after[3]-expected,bilinearLimit=.6/(f.blurredFar.height*f.blurredFar.height);
                    Check("gpu-footprint-second-moment-height-units-"+(int)budget+"-"+size.x+"x"+size.y,Math.Abs(difference)<bilinearLimit,(float)difference);
                    Check("gpu-blur-gather-integrated-radiance-"+(int)budget+"-"+size.x+"x"+size.y,Math.Abs(after[0]/before[0]-1)<.001,(float)Math.Abs(after[0]/before[0]-1));
                    if(size.x==256&&size.y==256)SaveSsrPreview("bokeh-gaussian-footprint-"+(int)budget,ReadSceneTarget(f.color),size.x,size.y,false);
                }
                settings.nearBlur=1;
                for(int lost=0;lost<11;lost++)
                {
                    var f=Render("before-target-loss-"+lost);var targets=(RenderTexture[])typeof(BokehDepthOfFieldRenderer).GetField("_targets",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(renderer);targets[lost].Release();
                    Check("loss-invalidates-lease-"+lost,!f.IsCurrent&&!renderer.TryGetFrame(out _));Render("recover-target-loss-"+lost);
                }
                for(int alias=0;alias<9;alias++)
                {
                    var f=Render("before-alias-"+alias);var choices=new[]{f.color,f.encodedCoC,f.prefilterFar,f.blurredFar,f.far,f.prefilterNear,f.inflatedNear,f.blurredNear,f.near};
                    Check("owned-alias-rejected-"+alias,!renderer.TryRender(choices[alias],depth,settings,out _)&&!f.IsCurrent&&renderer.TargetCount==0);
                }
                var activeControl=Target(7,9,RenderTextureFormat.ARGBFloat);RenderTexture.active=activeControl;Render("caller-active-control");
                settings.focusNear=8;settings.focusFar=4;Check("invalid-slab-rejected",!renderer.TryRender(source,depth,settings,out _)&&renderer.TargetCount==0&&RenderTexture.active==activeControl);settings.focusNear=2;settings.focusFar=5;
                settings.maximumRadius=float.NaN;Check("nan-radius-rejected",!renderer.TryRender(source,depth,settings,out _));settings.maximumRadius=.035f;
                settings.sampleCount=(BokehSampleCount)29;Check("unknown-budget-rejected",!renderer.TryRender(source,depth,settings,out _));settings.sampleCount=BokehSampleCount.Samples30;
                Check("missing-depth-rejected",!renderer.TryRender(source,null,settings,out _)&&renderer.TargetCount==0);
                var wrongDepth=Target(2,2,RenderTextureFormat.RFloat);Check("mismatched-depth-rejected",!renderer.TryRender(source,wrongDepth,settings,out _));
                var final=Render("before-dispose");renderer.Dispose();Check("dispose-releases-and-invalidates",!final.IsCurrent&&!final.color.IsCreated()&&renderer.TargetCount==0&&RenderTexture.active==activeControl);
                settings.enabled=false;settings.maximumRadius=float.NaN;Check("disabled-invalid-settings-no-work",!renderer.TryRender(source,depth,settings,out _)&&renderer.TargetCount==0);settings.enabled=true;settings.maximumRadius=.035f;

                // Real Camera.Render -> actual OnRenderImage source -> caller depth -> production post.
                var host=Own(new GameObject("Bokeh production camera"));var camera=host.AddComponent<Camera>();camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;camera.cullingMask=1<<26;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.18f,.42f,.73f,1);
                camera.renderingPath=RenderingPath.Forward;camera.orthographic=true;camera.orthographicSize=2;camera.aspect=97f/81;camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.transform.position=new Vector3(0,0,-4);
                source=Own(new RenderTexture(97,81,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));source.Create();camera.targetTexture=source;
                SceneDepthData.Surface Plane(string name,Vector3 position,Vector3 scale,Color color)
                {
                    var go=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));go.name=name;go.layer=26;go.transform.position=position;go.transform.localScale=scale;
                    var material=Own(new Material(Resources.Load<Shader>("StudioAccent")));material.SetColor("_Color",color);go.GetComponent<Renderer>().sharedMaterial=material;
                    return new SceneDepthData.Surface{renderer=go.GetComponent<Renderer>(),cull=CullMode.Back};
                }
                var depthData=host.AddComponent<SceneDepthData>();depthData.buildDepthHierarchy=false;
                depthData.surfaces=new[]{Plane("Bokeh background",new Vector3(0,0,12),Vector3.one*10,new Color(.08f,.17f,.65f,1)),Plane("Bokeh face",new Vector3(.45f,.1f,0),new Vector3(1.4f,2.4f,1),new Color(.95f,.48f,.2f,1)),Plane("Bokeh hand",new Vector3(-.5f,.1f,-1.5f),new Vector3(.75f,.6f,1),new Color(.7f,.85f,.3f,1)),Plane("Bokeh near red-zero control",new Vector3(-1.55f,-.4f,-3.5f),new Vector3(.6f,1.8f,1),new Color(0,1.7f,0,1))};
                var post=host.AddComponent<OriginalStyleRenderPipeline>();camera.Render();var legacy=ReadSceneTarget(source);
                Check("production-default-no-new-dof",!post.TryGetBokehDepthOfFieldFrame(out _));post.bokehDepthOfField=settings;post.bokehDepthProvider=null;camera.Render();
                Check("production-missing-provider-passthrough",!post.TryGetBokehDepthOfFieldFrame(out _)&&ScenePixelsEqual(legacy,ReadSceneTarget(source))&&post.BokehDepthOfFieldUnavailableReason.Contains("provider"));
                int providers=0;RenderTexture providedColor=null;
                post.bokehDepthProvider=(cam,color)=>{Check("production-provider-camera-and-current-hdr-"+providers,cam==camera&&color!=source&&color.width==97&&color.height==81&&!color.sRGB);providers++;providedColor=color;if(!depthData.TryGetFrame(cam,color.width,color.height,out var geometry))throw new InvalidOperationException("Explicit complete fixture depth missing");return geometry.linearDepth;};
                bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_BOKEH")=="1",started=false,ended=false;
                if(capture)started=RenderDocCaptureBridge.BeginOffscreenCapture();
                try
                {
                    settings.sampleCount=BokehSampleCount.Samples30;camera.Render();
                    Check("production-30-resolves-in-render-callback",providers==1&&post.TryGetBokehDepthOfFieldFrame(out var production)&&production.IsCurrent&&providedColor!=null);
                    settings.sampleCount=BokehSampleCount.Samples43;camera.Render();
                    Check("production-43-resolves-in-render-callback",providers==2&&post.TryGetBokehDepthOfFieldFrame(out var alternate)&&alternate.IsCurrent);
                    Check("production-real-geometry-depth-nonvacuous",depthData.SubmittedSurfaces==4&&depthData.TryGetFrame(camera,97,81,out _));
                    if(post.TryGetBokehDepthOfFieldFrame(out var photographed))SaveSsrPreview("bokeh-actual-camera-production-43",ReadSceneTarget(photographed.color),97,81,false);
                }
                finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                if(capture)Check("requested-native-capture",started&&ended);
                post.bokehDepthProvider=(cam,color)=>throw new InvalidOperationException("Synthetic explicit provider failure");camera.Render();
                Check("production-provider-failure-invalidates-and-passthrough",!post.TryGetBokehDepthOfFieldFrame(out _)&&ScenePixelsEqual(legacy,ReadSceneTarget(source))&&post.BokehDepthOfFieldUnavailableReason.Contains("InvalidOperationException"));
                settings.enabled=false;camera.Render();Check("production-disabled-default-restored",!post.TryGetBokehDepthOfFieldFrame(out _)&&ScenePixelsEqual(legacy,ReadSceneTarget(source)));
                post.enabled=false;camera.targetTexture=null;host.SetActive(false);
            }
            finally
            {
                renderer.Dispose();QualitySettings.anisotropicFiltering=savedAnisotropy;RenderTexture.active=savedActive!=null&&savedActive.IsCreated()?savedActive:null;
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var item in _owned)if(item!=null)Destroy(item);_owned.Clear();
            }
        }

        private static double BokehReferenceRadius(BokehDepthOfFieldSettings s,double z)
        {
            if(!(z>0)||double.IsInfinity(z))return 0;
            double radius;
            if(s.focusMode==BokehFocusMode.Physical)
            {double f=s.focalLengthMillimetres/1000.0;radius=f*f*(z-s.focusDistance)/(s.fNumber*(s.focusDistance-f)*z)/(2*s.sensorHeightMillimetres/1000.0);}
            else
            {
                double Ease(double x){x=Math.Max(0,Math.Min(1,x));return x*x*(3-2*x);}
                radius=s.maximumRadius*(Ease((z-s.focusFar)/s.farTransition)-Ease((s.focusNear-z)/s.nearTransition));
            }
            return Math.Max(-s.maximumRadius,Math.Min(s.maximumRadius,radius))*(radius<0?s.nearBlur:s.farBlur);
        }

        private float BokehGatherError(BokehDepthOfFieldRenderer.Frame frame,BokehDepthOfFieldSettings settings,bool near,out float nearestDifference)
        {
            var input=near?frame.prefilterNear:frame.prefilterFar;var output=near?frame.blurredNear:frame.blurredFar;
            var pixels=ReadSceneTarget(input);var actual=ReadSceneTarget(output);var inflated=near?ReadSceneTarget(frame.inflatedNear):null;
            var kernel=new Vector4[42];int count=BokehKernel.Fill(settings,frame.color.height/(float)frame.color.width,kernel);float error=0;nearestDifference=0;
            for(int y=0;y<input.height;y++)for(int x=0;x<input.width;x++)
            {
                int i=y*input.width+x;var centre=pixels[i];Color expected=Color.clear,lower=Color.clear,upper=Color.clear;
                bool active=near?inflated[Mathf.Min(frame.inflatedNear.height-1,(int)((y+.5f)/input.height*frame.inflatedNear.height))*frame.inflatedNear.width+Mathf.Min(frame.inflatedNear.width-1,(int)((x+.5f)/input.width*frame.inflatedNear.width))].r>0:centre.a>0;
                if(active)
                {
                    Vector3 sum=new Vector3(centre.r,centre.g,centre.b);double weightSum=1;float scale=near?.4375f:centre.a/settings.maximumRadius*.875f;
                    lower=upper=new Color(centre.r,centre.g,centre.b,1);
                    for(int k=0;k<count;k++)
                    {
                        float px=x+kernel[k].w*scale*input.width,py=y+kernel[k].y*scale*input.height;
                        var sample=BokehFixedSample(pixels,input.width,input.height,px,py);
                        double coc=Math.Max(0,near?Math.Max(centre.a,sample.a):Math.Min(centre.a,sample.a));double w=Math.Max(0,Math.Min(1,(coc-kernel[k].z*scale+4.0/1080)/(4.0/1080)));
                        sum+=new Vector3(sample.r,sample.g,sample.b)*(float)w;weightSum+=w;
                        // D3D guarantees at least eight fractional address bits. At rounding
                        // boundaries, float interpolation/FMA can select either adjacent grid.
                        // D3D11.3 section 3.2.4.1 allows 0.6 ULP, rather than exact 0.5 ULP.
                        // Propagate that local addressing interval through weights and
                        // normalization; do not ignore edge pixels or use a global RGB tolerance.
                        Color lo=new Color(float.MaxValue,float.MaxValue,float.MaxValue,float.MaxValue),hi=new Color(float.MinValue,float.MinValue,float.MinValue,float.MinValue);
                        const float uncertainty=.6f/256+.00003f;
                        for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                        {var value=TaaCurrent(pixels,input.width,input.height,px+dx*uncertainty,py+dy*uncertainty);for(int c=0;c<4;c++){lo[c]=Mathf.Min(lo[c],value[c]);hi[c]=Mathf.Max(hi[c],value[c]);}}
                        float Weight(float a)=>Mathf.Clamp01((Mathf.Max(0,near?Mathf.Max(centre.a,a):Mathf.Min(centre.a,a))-kernel[k].z*scale+4f/1080)/(4f/1080));
                        float wl=Weight(lo.a),wh=Weight(hi.a);
                        for(int c=0;c<3;c++){lower[c]+=Mathf.Min(lo[c]*wl,lo[c]*wh,hi[c]*wl,hi[c]*wh);upper[c]+=Mathf.Max(lo[c]*wl,lo[c]*wh,hi[c]*wl,hi[c]*wh);}lower.a+=wl;upper.a+=wh;
                    }
                    float a=near?(float)Math.Min(1,weightSum/(count+1)):centre.a;sum/=(float)weightSum;if(near)sum*=a;expected=new Color(sum.x,sum.y,sum.z,a);
                    for(int c=0;c<3;c++){float lo=near?lower[c]/(count+1):Mathf.Min(lower[c]/lower.a,lower[c]/upper.a,upper[c]/lower.a,upper[c]/upper.a);float hi=near?upper[c]/(count+1):Mathf.Max(lower[c]/lower.a,lower[c]/upper.a,upper[c]/lower.a,upper[c]/upper.a);lower[c]=lo;upper[c]=hi;}
                    lower.a=near?lower.a/(count+1):centre.a;upper.a=near?upper.a/(count+1):centre.a;
                }
                for(int c=0;c<4;c++)
                {
                    nearestDifference=Mathf.Max(nearestDifference,Mathf.Abs(expected[c]-actual[i][c]));float outside=Mathf.Max(lower[c]-actual[i][c],actual[i][c]-upper[c]);
                    if(outside>error&&outside>.0001f)Debug.Log("[BokehOracle] near="+near+" samples="+(count+1)+" pixel="+x+","+y+" channel="+c+" centre="+centre.ToString("R")+" actual="+actual[i].ToString("R")+" expected="+expected.ToString("R")+" lower="+lower.ToString("R")+" upper="+upper.ToString("R"));
                    error=Mathf.Max(error,outside);
                }
            }
            return error;
        }

        private static Color BokehFixedSample(Color[] pixels,int width,int height,float x,float y)
        {
            int ix=Mathf.FloorToInt(x),iy=Mathf.FloorToInt(y);float fx=Mathf.Round((x-ix)*256)/256,fy=Mathf.Round((y-iy)*256)/256;
            Color Point(int px,int py)=>pixels[Mathf.Clamp(py,0,height-1)*width+Mathf.Clamp(px,0,width-1)];
            return Color.LerpUnclamped(Color.LerpUnclamped(Point(ix,iy),Point(ix+1,iy),fx),Color.LerpUnclamped(Point(ix,iy+1),Point(ix+1,iy+1),fx),fy);
        }

        private static double[] BokehMoment(Color[] pixels,int width,int height,double aspect)
        {
            double mass=0,xsum=0,ysum=0,second=0;
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {double weight=pixels[y*width+x].r,u=((x+.5)/width-.5)*aspect,v=(y+.5)/height-.5;mass+=weight;xsum+=u*weight;ysum+=v*weight;second+=(u*u+v*v)*weight;}
            return new[]{mass,xsum/mass,ysum/mass,second/mass-(xsum*xsum+ysum*ysum)/(mass*mass)};
        }
    }
}
