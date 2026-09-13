using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyMotionBlur(Report report)
        {
            yield return null;
            var previous=FindObjectsOfType<Renderer>();var forced=new bool[previous.Length];
            for(int i=0;i<previous.Length;i++){forced[i]=previous[i].forceRenderingOff;previous[i].forceRenderingOff=true;}
            var savedActive=RenderTexture.active;var savedAnisotropy=QualitySettings.anisotropicFiltering;
            var renderer=new MotionBlurRenderer();var second=new MotionBlurRenderer();
            try
            {
                void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"motion-blur-"+name,ok,error);
                RenderTexture Target(int w,int h,RenderTextureFormat format=RenderTextureFormat.ARGBFloat,int depth=0)
                {
                    var t=Own(new RenderTexture(w,h,depth,format,RenderTextureReadWrite.Linear){name="Motion blur fixture input "+w+"x"+h,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});t.Create();return t;
                }
                void Upload(RenderTexture target,Func<int,int,Color> pixel)
                {
                    var tex=Own(new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point});var colors=new Color[target.width*target.height];
                    for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)colors[y*target.width+x]=pixel(x,y);
                    tex.SetPixels(colors);tex.Apply();var active=RenderTexture.active;Graphics.Blit(tex,target);RenderTexture.active=active;
                }
                var settings=new MotionBlurSettings{enabled=true,maximumRadiusPixels=12,sampleJitter=.83f};
                var source=Target(67,43);var guide=Target(67,43);RenderTexture flags=null;
                float dt=1f/60;Vector2 jitter=Vector2.zero,mapping=Vector2.zero;
                void Pattern()
                {
                    Upload(source,(x,y)=>new Color(.05f+(x*19+y*7)%41/10f,.03f+(x+y*23)%37/20f,.1f+(x*11+y*13)%31/15f,.1f+(x+3*y)%17/20f));
                    Upload(guide,(x,y)=>new Color(x<31?.33f:0,x<31?0:-.26f,x<31?3:3.3f,(x+y)%23==0?0:1));
                }
                MotionBlurRenderer.Frame Run(string name,bool oracle=true)
                {
                    var active=RenderTexture.active;var input=new MotionBlurInput(source,guide,dt,jitter,mapping,flags);
                    if(!renderer.TryRender(input,settings,out var frame))throw new InvalidOperationException(name+": "+renderer.UnavailableReason);
                    Check(name+"-three-draws-three-live-targets",frame.IsCurrent&&renderer.DrawCalls==3&&renderer.TargetCount==3);
                    Check(name+"-caller-active-restored",RenderTexture.active==active);
                    var raw=ReadSceneTarget(source);var result=ReadSceneTarget(frame.color);
                    bool finite=true;float alpha=0;
                    for(int i=0;i<result.Length;i++){alpha=Mathf.Max(alpha,Mathf.Abs(result[i].a-raw[i].a));for(int c=0;c<4;c++)finite&=!float.IsNaN(result[i][c])&&!float.IsInfinity(result[i][c]);}
                    Check(name+"-finite-hdr-source-alpha-exact",finite&&alpha==0,alpha);
                    if(oracle)
                    {
                        MotionBlurReference(raw,ReadSceneTarget(guide),flags!=null?ReadSceneTarget(flags):null,source.width,source.height,settings,dt,jitter,mapping,out var expected,out var tiles,out var neighbors);
                        float te=PixelError(tiles,ReadSceneTarget(frame.tileMaximum)),ne=PixelError(neighbors,ReadSceneTarget(frame.neighborhoodMaximum)),ce=PixelError(expected,result);
                        Check(name+"-whole-image-cpu-tile-maximum",te<.00002f,te);
                        Check(name+"-whole-image-cpu-neighborhood-maximum",ne<.00002f,ne);
                        Check(name+"-whole-image-cpu-reconstruction",ce<.00008f,ce);
                        if(ce>=.00008f)DumpMotionBlurPixels(name,source.width,source.height,raw,ReadSceneTarget(guide),expected,result);
                        if(name=="mixed-directions-depth-protection")
                        {
                            MotionBlurReference(raw,ReadSceneTarget(guide),null,source.width,source.height,settings,dt,jitter,mapping,out var alternate,out _,out _,true);
                            Check(name+"-flipped-readback-negative-control",PixelError(alternate,result)>.01f,PixelError(alternate,result));
                        }
                    }
                    return frame;
                }
                Check("default-valid-and-disabled",new MotionBlurSettings().IsValid&&!new MotionBlurSettings().enabled);
                Check("angle-half-exposure",Mathf.Abs(settings.HalfDisplacementScale(dt)-.25f)<1e-7f);
                settings.exposure=MotionBlurExposure.Seconds;settings.exposureSeconds=1f/120;
                Check("seconds-interval-normalization",Mathf.Abs(settings.HalfDisplacementScale(dt)-.25f)<1e-7f&&Mathf.Abs(settings.HalfDisplacementScale(dt*2)-.125f)<1e-7f);
                settings.exposure=MotionBlurExposure.ShutterAngle;
                foreach(Action<MotionBlurSettings> corrupt in new Action<MotionBlurSettings>[] {s=>s.samples=5,s=>s.samples=66,s=>s.maximumRadiusPixels=0,s=>s.maximumRadiusPixels=129,s=>s.shutterAngle=float.NaN,s=>s.exposureSeconds=float.PositiveInfinity,s=>s.exposure=(MotionBlurExposure)7,s=>s.softDepthExtent=0,s=>s.maximumSampleInterval=0,s=>s.sampleJitter=-1,s=>s.centerWeightDenominator=0})
                {var bad=new MotionBlurSettings{enabled=true};corrupt(bad);Check("invalid-settings-"+report.checks.Count,!bad.IsValid&&!renderer.TryRender(new MotionBlurInput(source,guide,dt),bad,out _)&&renderer.TargetCount==0);}
                Pattern();var first=Run("mixed-directions-depth-protection");var baseline=ReadSceneTarget(first.color);
                Check("nontrivial-filter-changes-current-image",PixelError(ReadSceneTarget(source),baseline)>.1f);
                SaveSsrPreview("motion-blur-authored-input",ReadSceneTarget(source),source.width,source.height,false);SaveSsrPreview("motion-blur-authored-output",baseline,source.width,source.height,false);
                foreach(int samples in new[]{4,8,16,32,64})foreach(bool dual in new[]{false,true})
                {settings.samples=samples;settings.dualDirections=dual;Run("budget-"+samples+"-dual-"+dual);}
                settings.samples=32;settings.dualDirections=true;
                var repeat=Run("repeat-original-settings");Check("deterministic-same-current-input",ScenePixelsEqual(baseline,ReadSceneTarget(repeat.color))&&!first.IsCurrent);
                source.filterMode=guide.filterMode=FilterMode.Trilinear;source.wrapMode=guide.wrapMode=TextureWrapMode.Repeat;source.anisoLevel=guide.anisoLevel=16;QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;
                Check("integer-load-independent-of-caller-sampler",ScenePixelsEqual(baseline,ReadSceneTarget(Run("caller-sampler-state").color)));QualitySettings.anisotropicFiltering=savedAnisotropy;
                settings.noiseSeed=0xf1379021;Run("full-32-bit-seed");settings.sampleJitter=0;Run("unjittered-strata");settings.noiseSeed=17;settings.sampleJitter=.83f;
                settings.shutterAngle=0;Check("zero-shutter-exact",ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(Run("zero-shutter").color)));settings.shutterAngle=180;
                foreach(float interval in new[]{0f,1e-8f,.5f}){dt=interval;Check("paused-or-stale-exact-"+interval,ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(Run("interval-"+interval).color)));}dt=1f/60;
                settings.exposure=MotionBlurExposure.Seconds;Run("explicit-exposure-seconds");settings.exposure=MotionBlurExposure.ShutterAngle;
                jitter=new Vector2(.013f,-.021f);mapping=new Vector2(2f/source.width,-3f/source.height);Run("jitter-removal-and-guide-remapping");jitter=mapping=Vector2.zero;
                flags=Target(source.width,source.height,RenderTextureFormat.R8);Upload(flags,(x,y)=>new Color(x<20?4f/255:0,0,0,1));Run("mixed-no-jitter-protected-mask");flags=null;
                foreach(float a in new[]{0f,.5f,2f})
                {Upload(guide,(x,y)=>new Color(.3f,.2f,4,a));Check("non-unit-correspondence-protected-"+a,ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(Run("invalid-correspondence-"+a).color)));}
                Upload(guide,(x,y)=>new Color(.3f,.2f,0,1));Check("nonpositive-depth-protected",ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(Run("invalid-depth").color)));
                foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})foreach(int component in new[]{0,1,2})
                {Upload(guide,(x,y)=>{var g=new Color(.3f,.2f,4,1);g[component]=invalid;return g;});Check("nonfinite-guide-protected-"+component+"-"+invalid,ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(Run("nonfinite-guide-"+component+"-"+invalid).color)));}
                Upload(guide,(x,y)=>new Color(float.MaxValue,1e30f,4,1));Check("finite-guide-conversion-overflow-protected",ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(Run("finite-guide-overflow").color)));
                foreach(var size in new[]{new Vector2Int(1,1),new Vector2Int(1,17),new Vector2Int(19,1),new Vector2Int(71,47)})
                foreach(var format in new[]{RenderTextureFormat.ARGBFloat,RenderTextureFormat.ARGBHalf,RenderTextureFormat.RGB111110Float})
                {
                    source=Target(size.x,size.y,format);guide=Target(size.x,size.y);Upload(source,(x,y)=>new Color(.1f+(x%3),.2f+y%5,8,.37f));Upload(guide,(x,y)=>new Color(.47f,-.21f,3,1));Run("shape-"+size.x+"x"+size.y+"-"+format);
                }
                source=Target(67,43);guide=Target(67,43);Pattern();
                foreach(int radius in new[]{1,3,16,128}){settings.maximumRadiusPixels=radius;Run("radius-clamp-"+radius);}settings.maximumRadiusPixels=12;
                Upload(source,(x,y)=>new Color(128,32,8,.37f));Check("large-constant-hdr-retained",PixelError(ReadSceneTarget(source),ReadSceneTarget(Run("large-constant-hdr").color))<.00002f);Pattern();
                second.TryRender(new MotionBlurInput(source,guide,dt),settings,out var other);Check("independent-instance-live",other.IsCurrent);
                for(int targetIndex=0;targetIndex<3;targetIndex++)
                {var lease=Run("before-loss-"+targetIndex);(targetIndex==0?lease.color:targetIndex==1?lease.tileMaximum:lease.neighborhoodMaximum).Release();Check("any-target-loss-invalidates-"+targetIndex,!lease.IsCurrent&&!renderer.TryGetFrame(out _));Run("recover-target-loss-"+targetIndex);}
                var alias=Run("before-output-alias");Check("output-alias-rejected",!renderer.TryRender(new MotionBlurInput(alias.color,guide,dt),settings,out _)&&!alias.IsCurrent&&renderer.TargetCount==0);
                Check("same-input-texture-rejected",!renderer.TryRender(new MotionBlurInput(source,source,dt),settings,out _));
                Check("null-guide-rejected",!renderer.TryRender(new MotionBlurInput(source,null,dt),settings,out _));
                Check("nonfinite-interval-rejected",!renderer.TryRender(new MotionBlurInput(source,guide,float.NaN),settings,out _));
                Check("bad-uv-rejected",!renderer.TryRender(new MotionBlurInput(source,guide,dt,new Vector2(float.NaN,0)),settings,out _));
                settings.enabled=false;Check("explicit-disabled-releases",!renderer.TryRender(new MotionBlurInput(source,guide,dt),settings,out _)&&renderer.TargetCount==0);settings.enabled=true;
                var final=Run("before-dispose");renderer.Dispose();Check("dispose-invalidates-only-owner",!final.IsCurrent&&!final.color.IsCreated()&&other.IsCurrent);second.Dispose();

                // Actual current geometry motion and framebuffer occlusion, no manufactured motion texture.
                var host=Own(new GameObject("Motion blur actual geometry camera"));host.SetActive(false);var camera=host.AddComponent<Camera>();camera.enabled=false;
                camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;camera.cullingMask=1<<26;camera.orthographic=true;camera.orthographicSize=2;camera.aspect=129f/97;
                camera.nearClipPlane=.1f;camera.farClipPlane=40;camera.transform.position=new Vector3(0,0,-4);camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                var target=Target(129,97,RenderTextureFormat.ARGBFloat,24);camera.targetTexture=target;
                var scene=host.AddComponent<SceneDeferredCamera>();scene.sceneEnabled=true;scene.sceneLayers=1<<25;scene.lightRadiance=scene.ambientIrradiance=Vector3.zero;
                SceneDeferredCamera.Surface Panel(string name,Vector3 position,Vector3 scale,Vector3 emission)
                {
                    var panel=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));panel.name=name;panel.layer=25;panel.transform.position=position;panel.transform.localScale=scale;
                    var surface=new SceneDeferredCamera.Surface{renderer=panel.GetComponent<Renderer>(),cull=CullMode.Off};surface.inputs.emission=emission;return surface;
                }
                var background=Panel("Motion blur registered background",new Vector3(0,0,1),new Vector3(8,6,1),new Vector3(.05f,.1f,.15f));
                var foreground=Panel("Motion blur moving foreground",new Vector3(-.55f,0,0),new Vector3(.7f,2.4f,1),new Vector3(2,.4f,.08f));
                scene.surfaces=new[]{background,foreground};scene.motionBlurTime=0;host.SetActive(true);camera.Render();var disabledImage=ReadSceneTarget(target);
                Check("scene-default-no-extra-guide-or-resolve",scene.MotionBlurTargetCount==0&&scene.MotionBlurVisibilityDrawCalls==0&&!scene.TryGetMotionBlurFrame(out _));
                scene.motionBlur.enabled=true;camera.Render();Check("scene-missing-motion-fails-locally",scene.TryGetFrame(out _)&&scene.MotionBlurTargetCount==0&&scene.MotionBlurUnavailableReason.Contains("motion")&&ScenePixelsEqual(disabledImage,ReadSceneTarget(target)));
                scene.motion.enabled=true;scene.motionBlur.maximumRadiusPixels=12;scene.motionBlur.samples=64;scene.motionBlur.sampleJitter=.83f;scene.motionBlur.shutterAngle=360;
                Color[] current=null,filtered=null,visible=null;SceneDeferredCamera.MotionBlurFrame sceneFrame=default;
                SceneDeferredCamera.MotionBlurFrame Render(string name,double time,bool dejitter=false,Vector2 delta=default)
                {
                    scene.motionBlurTime=time;camera.Render();if(!scene.TryGetFrame(out var geometry))throw new InvalidOperationException(scene.UnavailableReason);
                    RenderTexture input=target;if(dejitter&&!scene.TryResolveTemporalColor(camera,target,null,out input))throw new InvalidOperationException(scene.TemporalColorUnavailableReason);
                    current=ReadSceneTarget(input);
                    if(!scene.TryResolveMotionBlur(camera,input,dejitter,null,out var output)||!scene.TryGetMotionBlurFrame(out sceneFrame))throw new InvalidOperationException(name+": "+scene.MotionBlurUnavailableReason);
                    filtered=ReadSceneTarget(output);visible=ReadSceneTarget(sceneFrame.visibleMotionDepth);
                    Check(name+"-actual-four-live-targets",sceneFrame.IsCurrent&&scene.MotionBlurTargetCount==4&&scene.MotionBlurVisibilityDrawCalls==2&&scene.MotionBlurResolveDrawCalls==3);
                    int valid=0;float protection=0,depth=0,motionDifference=0;var motions=ReadSceneTarget(geometry.motionVectors);
                    for(int i=0;i<visible.Length;i++)
                    {
                        if(visible[i].a==1){valid++;depth=Mathf.Max(depth,Mathf.Min(Mathf.Abs(visible[i].b-4),Mathf.Abs(visible[i].b-5)));motionDifference=Mathf.Max(motionDifference,Mathf.Abs(visible[i].r-motions[i].r),Mathf.Abs(visible[i].g-motions[i].g));}
                        else if(!dejitter)for(int c=0;c<4;c++)protection=Mathf.Max(protection,Mathf.Abs(current[i][c]-filtered[i][c]));
                    }
                    Check(name+"-actual-visible-current-depth",depth<.00002f,depth);Check(name+"-actual-visible-motion-identical",motionDifference==0,motionDifference);Check(name+"-protected-pixels-exact",protection==0,protection);
                    // Current guide carries invalid A on a discontinuity. Its zero interval is independently tested above.
                    MotionBlurReference(current,visible,null,target.width,target.height,scene.motionBlur,1f/60,delta,dejitter?-scene.temporalAntialiasing.jitterUv:Vector2.zero,out var expected,out var expectedTiles,out var expectedNeighbors);
                    float error=PixelError(expected,filtered);Check(name+"-actual-whole-image-cpu-filter",error<.00008f,error);
                    if(error>=.00008f)
                    {
                        DumpMotionBlurPixels(name,target.width,target.height,current,visible,expected,filtered);
                        DumpMotionBlurPixels(name+"-maxima",expectedTiles.Length,1,expectedTiles,expectedNeighbors,ReadSceneTarget(sceneFrame.tileMaximum),ReadSceneTarget(sceneFrame.neighborhoodMaximum));
                    }
                    return sceneFrame;
                }
                Render("scene-first-sample",0);Check("scene-first-exact",ScenePixelsEqual(current,filtered));
                Render("scene-stationary",1.0/60);Check("scene-stationary-exact",ScenePixelsEqual(current,filtered));
                foreground.renderer.transform.position=new Vector3(0,0,0);var moving=Render("scene-rigid-motion",2.0/60);
                Check("scene-rigid-motion-changes-image",PixelError(current,filtered)>.1f);
                var movingCurrent=(Color[])current.Clone();var movingFiltered=(Color[])filtered.Clone();
                SaveSsrPreview("motion-blur-actual-current",current,target.width,target.height,false);SaveSsrPreview("motion-blur-actual-filtered",filtered,target.width,target.height,false);
                int trails=0;for(int i=0;i<current.Length;i++)if(current[i].r<.1f&&filtered[i].r>current[i].r+.02f)trails++;Check("moving-foreground-spreads-into-visible-background",trails>40,trails);
                Check("scene-double-consume-rejected",!scene.TryResolveMotionBlur(camera,target,false,null,out _));
                Render("scene-paused-time",2.0/60);Check("scene-paused-source-exact",ScenePixelsEqual(current,filtered)&&!moving.IsCurrent);
                foreground.renderer.transform.position=new Vector3(.15f,0,0);Render("scene-time-seek",1.0/60);Check("scene-seek-source-exact",ScenePixelsEqual(current,filtered));
                Render("scene-long-gap",1);Check("scene-long-gap-source-exact",ScenePixelsEqual(current,filtered));
                scene.ResetMotionBlurHistory();Render("scene-explicit-reset",1+1.0/60);Check("scene-reset-source-exact",ScenePixelsEqual(current,filtered));
                camera.transform.position=new Vector3(1.1f,0,-4);Render("scene-camera-cut",1+2.0/60);Check("scene-cut-source-exact",ScenePixelsEqual(current,filtered));
                camera.transform.position=new Vector3(0,0,-4);Render("scene-camera-return-cut",1+3.0/60);
                foreground.excludeMotionBlur=true;foreground.renderer.transform.position=Vector3.zero;Render("scene-surface-exclusion",1+4.0/60);
                int excluded=0;bool exclusionExact=true;for(int i=0;i<current.Length;i++)if(current[i].r>1){excluded++;exclusionExact&=visible[i].a==0&&current[i].Equals(filtered[i]);}Check("scene-foreground-exclusion-nonvacuous",excluded>500&&exclusionExact,excluded);foreground.excludeMotionBlur=false;
                // Unknown forward opaque object writes the actual framebuffer depth and must protect its pixels.
                var occluder=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));occluder.layer=26;occluder.transform.position=new Vector3(-.12f,0,-.3f);occluder.transform.localScale=new Vector3(.3f,3,1);
                var occluderMaterial=Own(new Material(Resources.Load<Shader>("StudioAccent")));occluderMaterial.SetColor("_Color",new Color(.1f,2,.1f,1));occluder.GetComponent<Renderer>().sharedMaterial=occluderMaterial;
                foreground.renderer.transform.position=new Vector3(.35f,0,0);Render("scene-forward-opaque-protection",1+5.0/60);
                int protectedOpaque=0;bool opaqueExact=true;for(int i=0;i<current.Length;i++)if(current[i].g>1){protectedOpaque++;opaqueExact&=visible[i].a==0&&current[i].Equals(filtered[i]);}Check("scene-forward-occluder-nonvacuous-exact",protectedOpaque>300&&opaqueExact,protectedOpaque);occluder.SetActive(false);
                camera.transform.position=new Vector3(.15f,0,-4);Render("scene-camera-only-motion",1+6.0/60);Check("scene-camera-only-nonzero",PixelError(current,filtered)>.01f);camera.transform.position=new Vector3(0,0,-4);

                // Dense actual Camera.Render exposure reference for constant-speed foreground motion.
                var referenceHost=Own(new GameObject("Motion blur dense temporal reference"));var referenceCamera=referenceHost.AddComponent<Camera>();referenceCamera.CopyFrom(camera);referenceCamera.enabled=false;referenceCamera.transform.SetPositionAndRotation(camera.transform.position,camera.transform.rotation);var referenceTarget=Target(129,97,RenderTextureFormat.ARGBFloat,24);referenceCamera.targetTexture=referenceTarget;
                var referenceScene=referenceHost.AddComponent<SceneDeferredCamera>();referenceScene.sceneEnabled=true;referenceScene.sceneLayers=scene.sceneLayers;referenceScene.surfaces=scene.surfaces;referenceScene.lightRadiance=referenceScene.ambientIrradiance=Vector3.zero;
                var temporalReference=new Color[target.width*target.height];const int temporalSamples=64;
                for(int sample=0;sample<temporalSamples;sample++)
                {
                    foreground.renderer.transform.position=new Vector3(((sample+.5f)/temporalSamples-.5f)*.55f,0,0);referenceCamera.Render();var pixels=ReadSceneTarget(referenceTarget);
                    for(int i=0;i<pixels.Length;i++)temporalReference[i]+=pixels[i]/temporalSamples;
                }
                double rawMae=0,blurMae=0;for(int i=0;i<temporalReference.Length;i++)for(int c=0;c<3;c++){rawMae+=Math.Abs(movingCurrent[i][c]-temporalReference[i][c]);blurMae+=Math.Abs(movingFiltered[i][c]-temporalReference[i][c]);}
                Check("64-actual-exposure-reference-raw-mae",rawMae>0,(float)(rawMae/(3*temporalReference.Length)));Check("64-actual-exposure-reference-blur-mae",blurMae<rawMae,(float)(blurMae/(3*temporalReference.Length)));
                SaveSsrPreview("motion-blur-actual-64-exposure-reference",temporalReference,target.width,target.height,false);referenceHost.SetActive(false);referenceCamera.targetTexture=null;

                foreground.renderer.transform.position=Vector3.zero;scene.ResetMotionHistory();Render("scene-before-deformation",1.5);
                foreground.renderer.transform.rotation=Quaternion.Euler(0,0,19);Render("scene-rigid-rotation",1.5+1.0/60);Check("scene-rigid-rotation-nonzero",PixelError(current,filtered)>.01f);
                foreground.renderer.transform.rotation=Quaternion.identity;
                var rigidRenderer=foreground.renderer;var rigidObject=rigidRenderer.gameObject;var mutable=Own(Instantiate(rigidObject.GetComponent<MeshFilter>().sharedMesh));rigidObject.GetComponent<MeshFilter>().sharedMesh=mutable;
                Render("scene-new-mesh-seed",1.5+2.0/60);var vertices=mutable.vertices;vertices[2]+=new Vector3(.5f,.2f,0);mutable.vertices=vertices;mutable.RecalculateBounds();
                Render("scene-readable-vertex-deformation",1.5+3.0/60);Check("scene-readable-vertex-deformation-nonzero",PixelError(current,filtered)>.01f);
                var skinHost=Own(new GameObject("Motion blur two-bone deforming surface"));skinHost.layer=25;skinHost.transform.localScale=new Vector3(.7f,2.4f,1);
                var bone0=Own(new GameObject("Motion blur root bone")).transform;bone0.SetParent(skinHost.transform,false);var bone1=Own(new GameObject("Motion blur second bone")).transform;bone1.SetParent(skinHost.transform,false);
                var skin=skinHost.AddComponent<SkinnedMeshRenderer>();var skinMesh=Own(Instantiate(mutable));var weights=new BoneWeight[skinMesh.vertexCount];var blend=new Vector3[skinMesh.vertexCount];
                for(int i=0;i<weights.Length;i++){float fraction=Mathf.Clamp01(skinMesh.vertices[i].x+.5f);weights[i]=new BoneWeight{boneIndex0=0,weight0=1-fraction,boneIndex1=1,weight1=fraction};blend[i]=new Vector3(.35f*fraction,.05f*fraction,0);}
                skinMesh.boneWeights=weights;skinMesh.bindposes=new[]{Matrix4x4.identity,Matrix4x4.identity};skinMesh.AddBlendShapeFrame("Independent moving shape",100,blend,new Vector3[blend.Length],new Vector3[blend.Length]);
                skin.sharedMesh=skinMesh;skin.bones=new[]{bone0,bone1};skin.rootBone=bone0;skin.sharedMaterial=rigidRenderer.sharedMaterial;skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*10);
                foreground.renderer=skin;rigidObject.SetActive(false);yield return null;yield return null;Render("scene-skin-first-seed",1.7);
                bone1.localPosition=new Vector3(.4f,.12f,0);bone1.localRotation=Quaternion.Euler(0,0,14);yield return null;yield return null;
                Render("scene-two-bone-actual-motion",1.7+1.0/60);Check("scene-two-bone-actual-motion-nonzero",PixelError(current,filtered)>.01f);
                skin.SetBlendShapeWeight(0,85);yield return null;yield return null;Render("scene-blendshape-actual-motion",1.7+2.0/60);Check("scene-blendshape-actual-motion-nonzero",PixelError(current,filtered)>.01f);
                SaveSsrPreview("motion-blur-actual-skin-blendshape",filtered,target.width,target.height,false);skinHost.SetActive(false);foreground.renderer=rigidRenderer;rigidObject.SetActive(true);
                foreground.renderer.transform.position=Vector3.zero;scene.ResetMotionHistory();Render("scene-before-jitter",2);
                var projection=camera.projectionMatrix;Vector2 oldJitter=Vector2.zero;
                for(int sample=0;sample<4;sample++)
                {
                    var shift=new Vector2(sample%2==0?.37f:-.29f,sample<2?.21f:-.41f);var p=projection;p.m03+=2*shift.x/target.width;p.m13+=2*shift.y/target.height;camera.projectionMatrix=p;
                    var now=new Vector2(-shift.x/target.width,-shift.y/target.height);scene.motionBlurJitterUv=now;Render("scene-jitter-only-"+sample,2+(sample+1)/60.0,false,now-oldJitter);Check("scene-jitter-only-no-physical-blur-"+sample,ScenePixelsEqual(current,filtered));oldJitter=now;
                }
                camera.projectionMatrix=projection;scene.motionBlurJitterUv=Vector2.zero;scene.ResetMotionHistory();scene.temporalAntialiasing.enabled=true;Render("scene-taa-first",3,true);
                for(int sample=0;sample<3;sample++)
                {
                    var shift=new Vector2(sample%2==0?.31f:-.27f,.19f);var p=projection;p.m03+=2*shift.x/target.width;p.m13+=2*shift.y/target.height;camera.projectionMatrix=p;
                    var now=new Vector2(-shift.x/target.width,-shift.y/target.height);var before=scene.temporalAntialiasing.jitterUv;scene.temporalAntialiasing.jitterUv=now;
                    foreground.renderer.transform.position=new Vector3((sample+1)*.22f,0,0);Render("scene-taa-actual-dejittered-moving-"+sample,3+(sample+1)/60.0,true,now-before);
                }
                scene.temporalAntialiasing.enabled=false;scene.temporalAntialiasing.jitterUv=Vector2.zero;camera.projectionMatrix=projection;scene.ResetMotionHistory();
                Render("scene-before-classification",3.2);
                foreground.temporalFlags=TemporalPixelFlags.NoJitter;foreground.renderer.transform.position+=new Vector3(.2f,0,0);Render("scene-no-jitter-surface",3.2+1.0/60);
                int noJitter=0;bool noJitterExact=true;for(int i=0;i<current.Length;i++)if(current[i].r>1){noJitter++;noJitterExact&=visible[i].a==0&&current[i].Equals(filtered[i]);}Check("scene-no-jitter-surface-protected",noJitter>500&&noJitterExact,noJitter);
                foreground.temporalFlags=TemporalPixelFlags.ExcludeTaa;foreground.renderer.transform.position+=new Vector3(.2f,0,0);Render("scene-exclude-taa-is-not-blur-exclusion",3.2+2.0/60);
                Check("scene-exclude-taa-still-moves",PixelError(current,filtered)>.01f);foreground.temporalFlags=TemporalPixelFlags.Normal;
                var alpha=Own(new Texture2D(4,1,TextureFormat.RGBA32,false,true){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});alpha.SetPixels(new[]{Color.white,Color.clear,Color.white,Color.clear});alpha.Apply();foreground.inputs.albedoMap=alpha;foreground.alphaCutoff=.5f;
                Render("scene-cutout-first-seed",3.3);foreground.renderer.transform.position+=new Vector3(-.4f,0,0);Render("scene-cutout-alpha-visible-motion",3.3+1.0/60);
                int cutoutForeground=0;for(int i=0;i<current.Length;i++)if(current[i].r>1&&visible[i].b<4.1f&&visible[i].a==1)cutoutForeground++;Check("scene-cutout-live-fragments",cutoutForeground>200&&PixelError(current,filtered)>.01f,cutoutForeground);foreground.inputs.albedoMap=null;foreground.alphaCutoff=0;
                var beforeOther=Render("scene-before-second-camera",3.4);var beforeOtherBytes=(Color[])filtered.Clone();referenceHost.SetActive(true);referenceCamera.targetTexture=referenceTarget;referenceScene.motion.enabled=true;referenceScene.motionBlur.enabled=true;referenceScene.motionBlurTime=0;referenceCamera.Render();referenceScene.motionBlurTime=1.0/60;referenceCamera.Render();
                Check("scene-two-camera-independent-result",referenceScene.TryResolveMotionBlur(referenceCamera,referenceTarget,false,null,out var otherOutput)&&referenceScene.TryGetMotionBlurFrame(out var otherFrame)&&otherFrame.IsCurrent&&otherOutput!=beforeOther.color&&beforeOther.IsCurrent&&ScenePixelsEqual(beforeOtherBytes,ReadSceneTarget(beforeOther.color)));
                Check("scene-wrong-camera-cannot-consume",!scene.TryResolveMotionBlur(referenceCamera,target,false,null,out _));referenceHost.SetActive(false);referenceCamera.targetTexture=null;
                for(int lost=0;lost<4;lost++)
                {
                    var lease=Render("scene-before-loss-"+lost,3.5+lost/60.0);(lost==0?lease.visibleMotionDepth:lost==1?lease.color:lost==2?lease.tileMaximum:lease.neighborhoodMaximum).Release();
                    Check("scene-any-target-loss-invalidates-"+lost,!lease.IsCurrent&&!scene.TryGetMotionBlurFrame(out _));Render("scene-recover-loss-"+lost,3.5+(lost+.5)/60.0);
                }
                var originalTarget=target;target=Target(83,61,RenderTextureFormat.ARGBFloat,24);camera.targetTexture=target;Render("scene-odd-resize",3.7);Check("scene-resize-reseeds",ScenePixelsEqual(current,filtered));target=originalTarget;camera.targetTexture=target;Render("scene-return-size-reseed",3.8);
                var post=host.AddComponent<OriginalStyleRenderPipeline>();post.sceneMotionBlurSource=scene;
                bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_MOTION_BLUR")=="1",started=false,ended=false;
                scene.motionBlurTime=4;camera.Render();if(capture)started=RenderDocCaptureBridge.BeginOffscreenCapture();
                try
                {
                    foreach(float position in new[]{.15f,-.15f})
                    {
                        foreground.renderer.transform.position=new Vector3(position,0,0);scene.motionBlurTime+=1.0/60;camera.Render();
                        Check("production-actual-post-consumes-current-motion-"+position,scene.TryGetMotionBlurFrame(out var frame)&&frame.IsCurrent&&scene.MotionBlurUnavailableReason==null);
                        SaveSsrPreview("motion-blur-production-"+position,ReadSceneTarget(target),target.width,target.height,false);
                    }
                }
                finally {if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                if(capture)Check("requested-native-capture",started&&ended);
                scene.motionBlur.enabled=false;camera.Render();var legacy=ReadSceneTarget(target);Check("production-disable-releases",scene.MotionBlurTargetCount==0&&!scene.TryGetMotionBlurFrame(out _));
                scene.motionBlur.enabled=true;scene.motionBlurTime+=1.0/60;camera.Render();scene.motionBlur.enabled=false;camera.Render();Check("production-disable-default-byte-restored",ScenePixelsEqual(legacy,ReadSceneTarget(target)));
                post.enabled=false;camera.targetTexture=null;host.SetActive(false);
            }
            finally
            {
                renderer.Dispose();second.Dispose();QualitySettings.anisotropicFiltering=savedAnisotropy;RenderTexture.active=savedActive!=null&&savedActive.IsCreated()?savedActive:null;
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];foreach(var item in _owned)if(item!=null)Destroy(item);_owned.Clear();
            }
        }

        // Separate scalar CPU oracle. This offscreen ReadPixels path already has native raster row order on D3D11.
        // graphicsUVStartsAtTop is not a request to flip ReadPixels. The asymmetric noise negative control checks this.
        // It verifies all output pixels, not a sample-point tolerance or comparison against another GPU invocation.
        private static void MotionBlurReference(Color[] source,Color[] guide,Color[] flags,int width,int height,MotionBlurSettings settings,float interval,Vector2 jitter,Vector2 mapping,out Color[] output,out Color[] tiles,out Color[] neighbors,bool flip=false)
        {
            Color[] Raster(Color[] value,int w,int h)
            {
                if(value==null)return null;var result=new Color[value.Length];for(int y=0;y<h;y++)Array.Copy(value,y*w,result,(flip?h-1-y:y)*w,w);return result;
            }
            var colors=Raster(source,width,height);var raw=Raster(guide,width,height);var mask=Raster(flags,width,height);int radius=settings.maximumRadiusPixels,tw=(width+radius-1)/radius,th=(height+radius-1)/radius;
            var velocities=new Vector4[width*height];
            // No use of the production settings math helper in the numerical reference.
            double scale=interval>=1e-6&&interval<=settings.maximumSampleInterval?(settings.exposure==MotionBlurExposure.ShutterAngle?settings.shutterAngle/720.0:settings.exposureSeconds/(2.0*interval)):0;
            bool Protect(int i)=>mask!=null&&(((int)Math.Round(mask[i].r*255))&4)!=0;
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int i=y*width+x,gx=(int)Math.Floor(x+.5+mapping.x*width),gy=(int)Math.Floor(y+.5+mapping.y*height);if(Protect(i)||gx<0||gy<0||gx>=width||gy>=height)continue;
                int gi=gy*width+gx;var g=raw[gi];if(Protect(gi)||g.a!=1||g.b<=0||float.IsNaN(g.r)||float.IsNaN(g.g)||float.IsNaN(g.b)||float.IsInfinity(g.r)||float.IsInfinity(g.g)||float.IsInfinity(g.b))continue;
                float vx=(g.r+jitter.x)*width*(float)scale,vy=(g.g+jitter.y)*height*(float)scale,speed=Mathf.Sqrt(vx*vx+vy*vy),clamp=Mathf.Min(1,radius/Mathf.Max(speed,1e-12f));
                if(float.IsNaN(speed)||float.IsInfinity(speed))continue;
                velocities[i]=new Vector4(speed<.5?0:vx*clamp,speed<.5?0:vy*clamp,g.b,1);
            }
            var tile=new Color[tw*th];var neighborhood=new Color[tw*th];
            // Mono may keep float locals in wider registers. Materialize each float32
            // product/sum: otherwise nearly horizontal vectors break the native tie rule.
            double R32(double value)=>BitConverter.ToSingle(BitConverter.GetBytes((float)value),0);
            double Score(float x,float y)=>R32(R32((double)x*x)+R32((double)y*y));
            for(int ty=0;ty<th;ty++)for(int tx=0;tx<tw;tx++)
            {
                double square=0;Vector2 best=Vector2.zero;
                for(int y=ty*radius;y<Math.Min(height,(ty+1)*radius);y++)for(int x=tx*radius;x<Math.Min(width,(tx+1)*radius);x++)
                {var v=velocities[y*width+x];double q=Score(v.x,v.y);if(q>square){square=q;best=new Vector2(v.x,v.y);}}
                tile[ty*tw+tx]=new Color(best.x,best.y,(float)Math.Sqrt(square),1);
            }
            for(int y=0;y<th;y++)for(int x=0;x<tw;x++)
            {
                double square=0;Color best=new Color(0,0,0,1);
                for(int yy=Math.Max(0,y-1);yy<=Math.Min(th-1,y+1);yy++)for(int xx=Math.Max(0,x-1);xx<=Math.Min(tw-1,x+1);xx++)
                {var v=tile[yy*tw+xx];double q=Score(v.r,v.g);if(q>square){square=q;best=v;}}
                neighborhood[y*tw+x]=best;
            }
            double Length(double x,double y)=>Math.Sqrt(x*x+y*y);
            float RasterLength(float x,float y)=>Mathf.Sqrt(x*x+y*y);
            // The D3D11 capture has fused MAD address formation. Integer point loads are
            // discontinuous: evaluating their address in double can choose a different pixel.
            float RasterMad(float a,float b,float c)=>(float)((double)a*b+c);
            double Clamp(double x)=>Math.Max(0,Math.Min(1,x));
            double Cone(double d,double r)=>r>1e-5?Math.Max(0,1-d/r):0;
            double Cylinder(double d,double r){if(r<=1e-5)return 0;double t=Clamp((d-r*.95)/(r*.1));return 1-t*t*(3-2*t);}
            var resultPixels=new Color[source.Length];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int index=y*width+x;var center=colors[index];var local=velocities[index];var dominant=neighborhood[(y/radius)*tw+x/radius];double r=Length(dominant.r,dominant.g),lr=Length(local.x,local.y);
                if(local.w!=1||r<.5){resultPixels[index]=center;continue;}
                float rasterRadius=RasterLength(dominant.r,dominant.g),localRasterRadius=RasterLength(local.x,local.y);
                float mx=dominant.r/rasterRadius,my=dominant.g/rasterRadius,sx=lr>=.5?local.x/localRasterRadius:-my,sy=lr>=.5?local.y/localRasterRadius:mx;
                int directions=settings.dualDirections?2:1,count=settings.samples/directions;double weight=settings.samples/(settings.centerWeightDenominator*Math.Max(lr,.5));var sum=new double[]{center.r*weight,center.g*weight,center.b*weight};
                uint h;unchecked{h=(uint)x*1664525u+(uint)y*1013904223u+settings.noiseSeed;h^=h>>16;h*=2246822519u;h^=h>>13;}
                float noise=(h&65535u)/65536f-.5f;
                for(int dir=0;dir<directions;dir++)for(int sample=0;sample<count;sample++)
                {
                    float ax=dir==0?mx:sx,ay=dir==0?my:sy,t=(RasterMad(noise,settings.sampleJitter,sample)+.5f)/count;t=RasterMad(t,2,-1)*rasterRadius;
                    int px=(int)Math.Floor(RasterMad(ax,t,x+.5f)),py=(int)Math.Floor(RasterMad(ay,t,y+.5f));if(px<0||py<0||px>=width||py>=height)continue;
                    int pi=py*width+px;var other=velocities[pi];if(other.w!=1)continue;
                    double or=Length(other.x,other.y),d=Length(px-x,py-y),a=Math.Abs((local.x*ax+local.y*ay)/Math.Max(lr,1e-12)),b=Math.Abs((other.x*ax+other.y*ay)/Math.Max(or,1e-12));
                    double nearer=Clamp(1-(other.z-local.z)/settings.softDepthExtent),farther=Clamp(1-(local.z-other.z)/settings.softDepthExtent);
                    double w=nearer*Cone(d,or)*b+farther*Cone(d,lr)*a+2*Cylinder(d,Math.Min(lr,or))*Math.Max(a,b);weight+=w;
                    for(int c=0;c<3;c++)sum[c]+=colors[pi][c]*w;
                }
                resultPixels[index]=new Color((float)(sum[0]/weight),(float)(sum[1]/weight),(float)(sum[2]/weight),center.a);
            }
            output=Raster(resultPixels,width,height);tiles=Raster(tile,tw,th);neighbors=Raster(neighborhood,tw,th);
        }

        private void DumpMotionBlurPixels(string name,int width,int height,params Color[][] images)
        {
            using(var writer=new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(_directory,"motion-blur-diagnostic-"+name+".raw"))))
            {writer.Write(width);writer.Write(height);writer.Write(images.Length);foreach(var image in images)foreach(var color in image)for(int channel=0;channel<4;channel++)writer.Write(color[channel]);}
        }
    }
}
