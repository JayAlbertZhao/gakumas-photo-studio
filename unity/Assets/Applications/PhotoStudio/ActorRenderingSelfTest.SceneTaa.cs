using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifySceneTaa(Report report)
        {
            yield return null;var previous=FindObjectsOfType<Renderer>();var forced=new bool[previous.Length];
            for(int i=0;i<previous.Length;i++){forced[i]=previous[i].forceRenderingOff;previous[i].forceRenderingOff=true;}
            try
            {
                void Check(string n,bool ok,float d=0)=>FrameworkCheck(report,"scene-taa-"+n,ok,d);
                RenderTexture Target(int width,int height){var t=Own(new RenderTexture(width,height,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));t.Create();return t;}
                var host=Own(new GameObject("Scene TAA actual motion host"));var camera=host.AddComponent<Camera>();camera.enabled=false;
                camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;camera.cullingMask=1<<26;
                camera.orthographic=true;camera.orthographicSize=2;camera.aspect=1;camera.nearClipPlane=.1f;camera.farClipPlane=40;camera.transform.position=new Vector3(0,0,-4);
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.03f,.05f,.07f,.4f);var target=Target(97,97);camera.targetTexture=target;
                var scene=host.AddComponent<SceneDeferredCamera>();scene.sceneEnabled=true;scene.sceneLayers=1<<25;scene.lightRadiance=Vector3.zero;scene.ambientIrradiance=Vector3.zero;
                var plane=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));plane.layer=25;plane.transform.localScale=Vector3.one*3.8f;
                var floor=new SceneDeferredCamera.Surface{renderer=plane.GetComponent<Renderer>(),cull=CullMode.Off};floor.inputs.emission=new Vector3(.6f,.2f,.1f);scene.surfaces=new[]{floor};
                SceneDeferredCamera.Frame Render(){camera.Render();if(!scene.TryGetFrame(out var f))throw new InvalidOperationException(scene.UnavailableReason);return f;}
                var baseline=ReadSceneTarget(Render().mosDepth);Check("default-no-color-history",scene.TemporalColorTargetCount==0&&!scene.TryGetTemporalColorFrame(out _));
                scene.temporalAntialiasing.enabled=true;camera.Render();Check("requires-explicit-motion",!scene.TryGetFrame(out _)&&scene.TemporalColorTargetCount==0&&scene.UnavailableReason.Contains("motion"));scene.motion.enabled=true;
                Color[] raw=null,result=null,geometry=null,metadata=null;TemporalClassification classification=null;
                int Used(){int count=0;foreach(var pixel in metadata)if(pixel.a>0)count++;return count;}
                SceneDeferredCamera.TemporalColorFrame Resolve(string name)
                {
                    Render();var source=camera.targetTexture;raw=ReadSceneTarget(source);var active=RenderTexture.active;
                    if(!scene.TryResolveTemporalColor(camera,source,classification,out var resolved))throw new InvalidOperationException(name+": "+scene.TemporalColorUnavailableReason);
                    Check(name+"-preserves-caller-active-target",RenderTexture.active==active);
                    if(!scene.TryGetTemporalColorFrame(out var frame))throw new InvalidOperationException("No completed temporal frame");
                    result=ReadSceneTarget(resolved);geometry=ReadSceneTarget(frame.visibleGeometry);metadata=ReadSceneTarget(frame.identityAgeFlagsWeight);
                    int visible=0,reused=0;float alphaError=0;bool finite=true;
                    for(int i=0;i<result.Length;i++)
                    {
                        if(geometry[i].a>0)visible++;if(metadata[i].a>0)reused++;
                        if(scene.temporalAntialiasing.jitterUv==Vector2.zero)alphaError=Mathf.Max(alphaError,Mathf.Abs(raw[i].a-result[i].a));
                        for(int c=0;c<4;c++)finite&=!float.IsNaN(result[i][c])&&!float.IsInfinity(result[i][c]);
                    }
                    Check(name+"-completed-owned-eight-targets-visible-geometry",frame.IsCurrent&&visible>Math.Min(500,source.width*source.height/2)&&scene.TemporalColorTargetCount==8&&scene.TemporalColorResolveDrawCalls==1,visible);
                    Check(name+"-finite-hdr-and-current-alpha",finite&&alphaError<1e-6f,alphaError);
                    return frame;
                }
                var first=Resolve("first");int firstUsed=0;foreach(var p in metadata)if(p.a>0)firstUsed++;Check("first-has-no-history",firstUsed==0&&PixelError(raw,result)<1e-6f,firstUsed);
                Resolve("stationary");int reusedCount=0;float stationaryError=PixelError(raw,result);foreach(var p in metadata)if(p.a>0)reusedCount++;
                Check("stationary-history-nonvacuous-and-color-stable",reusedCount>6000&&stationaryError<1e-5f,stationaryError);
                Check("previous-result-invalid-after-next-render",!first.IsCurrent);
                Check("same-render-double-consume-rejected",!scene.TryResolveTemporalColor(camera,target,null,out _)&&scene.TemporalColorUnavailableReason.Contains("already"));
                floor.inputs.emission=new Vector3(64000,32000,8000);scene.ResetTemporalColorHistory();Resolve("large-hdr-first");Resolve("large-hdr-stationary");
                float highError=0;for(int i=0;i<result.Length;i++)for(int c=0;c<3;c++)highError=Mathf.Max(highError,Mathf.Abs(result[i][c]-raw[i][c])/Mathf.Max(1,raw[i][c]));
                Check("hdr-compression-does-not-cap-bright-constant",result[result.Length/2].r>63000&&highError<2e-5f,highError);

                var checker=Own(new Texture2D(128,128,TextureFormat.RGBAFloat,false,true));checker.filterMode=FilterMode.Point;checker.wrapMode=TextureWrapMode.Clamp;var checkerPixels=new Color[128*128];
                for(int y=0;y<128;y++)for(int x=0;x<128;x++)checkerPixels[y*128+x]=((x+y)%2)==0?new Color(1.4f,.4f,.1f,1):new Color(.1f,.5f,1.2f,1);
                checker.SetPixels(checkerPixels);checker.Apply();floor.inputs.emission=Vector3.one;floor.inputs.emissionMap=checker;
                var refHost=Own(new GameObject("Scene TAA spatial reference"));var refCamera=refHost.AddComponent<Camera>();refCamera.CopyFrom(camera);refCamera.enabled=false;refCamera.transform.SetPositionAndRotation(camera.transform.position,camera.transform.rotation);var referenceTarget=Target(388,388);refCamera.targetTexture=referenceTarget;
                var refScene=refHost.AddComponent<SceneDeferredCamera>();refScene.sceneEnabled=true;refScene.sceneLayers=scene.sceneLayers;refScene.surfaces=scene.surfaces;refScene.lightRadiance=refScene.ambientIrradiance=Vector3.zero;
                refCamera.Render();var reference=ReadSceneTarget(referenceTarget);var expected=new Color[97*97];
                for(int y=0;y<97;y++)for(int x=0;x<97;x++)for(int dy=0;dy<4;dy++)for(int dx=0;dx<4;dx++)expected[y*97+x]+=reference[(y*4+dy)*388+x*4+dx]/16;
                var projection=camera.projectionMatrix;scene.temporalAntialiasing.reactiveThreshold=1;scene.ResetTemporalColorHistory();double rawError=0,filteredError=0,dejitterError=0,dejitterVariation=0,filteredVariation=0;int compared=0;
                Color[] priorDejitter=null,priorFiltered=null;
                for(int frame=0;frame<48;frame++)
                {
                    int phase=frame%16;var jitter=new Vector2(((phase%4)+.5f)/4-.5f,((phase/4)+.5f)/4-.5f);
                    var p=projection;p.m03+=2*jitter.x/97;p.m13+=2*jitter.y/97;camera.projectionMatrix=p;scene.temporalAntialiasing.jitterUv=-jitter/97;
                    Resolve("jittered-checker-"+frame);
                    var dejitter=new Color[raw.Length];for(int y=0;y<97;y++)for(int x=0;x<97;x++)dejitter[y*97+x]=TaaCurrent(raw,97,97,x+jitter.x,y+jitter.y);
                    if(frame>=32)for(int y=6;y<91;y++)for(int x=6;x<91;x++)for(int c=0;c<3;c++)
                    {
                        int i=y*97+x;rawError+=Math.Abs(raw[i][c]-expected[i][c]);filteredError+=Math.Abs(result[i][c]-expected[i][c]);dejitterError+=Math.Abs(dejitter[i][c]-expected[i][c]);compared++;
                        if(priorDejitter!=null){dejitterVariation+=Math.Abs(dejitter[i][c]-priorDejitter[i][c]);filteredVariation+=Math.Abs(result[i][c]-priorFiltered[i][c]);}
                    }
                    priorDejitter=dejitter;priorFiltered=result;
                }
                Check("actual-jittered-texture-alias-error-reduces",compared>10000&&rawError>1&&filteredError<rawError*.85,(float)(filteredError/rawError));
                Check("temporal-improves-over-dejitter-only-bilinear-control",dejitterError>1&&filteredError<dejitterError*.85,(float)(filteredError/dejitterError));
                Check("temporal-reduces-dejittered-adjacent-frame-variation",dejitterVariation>1&&filteredVariation<dejitterVariation*.85,(float)(filteredVariation/dejitterVariation));
                SaveSsrPreview("scene-taa-jittered-current",raw,97,97,true);SaveSsrPreview("scene-taa-jittered-resolved",result,97,97,true);
                SaveSsrPreview("scene-taa-jittered-dejitter-only",priorDejitter,97,97,true);SaveSsrPreview("scene-taa-jittered-spatial-reference",expected,97,97,true);
                scene.temporalAntialiasing.historyWeight=0;Resolve("zero-history-jittered-control");var zeroHistory=new Color[raw.Length];
                for(int y=0;y<97;y++)for(int x=0;x<97;x++)zeroHistory[y*97+x]=TaaCurrent(raw,97,97,x-scene.temporalAntialiasing.jitterUv.x*97,y-scene.temporalAntialiasing.jitterUv.y*97);
                Check("zero-history-is-only-explicit-current-resampling",PixelError(zeroHistory,result)<1e-5f&&Used()==0,PixelError(zeroHistory,result));scene.temporalAntialiasing.historyWeight=.95f;
                camera.projectionMatrix=projection;scene.temporalAntialiasing.jitterUv=Vector2.zero;scene.temporalAntialiasing.reactiveThreshold=.2f;scene.ResetTemporalColorHistory();Resolve("projection-return");
                var foreground=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));foreground.layer=26;foreground.transform.position=new Vector3(0,0,-.5f);foreground.transform.localScale=Vector3.one*1.2f;
                var flat=Own(new Material(Resources.Load<Shader>("StudioAccent")));flat.SetColor("_Color",new Color(.12f,.8f,.2f,1));foreground.GetComponent<Renderer>().sharedMaterial=flat;
                Resolve("foreign-forward-foreground");int opaque=0;float foregroundError=0;
                for(int y=38;y<59;y++)for(int x=38;x<59;x++){int i=y*97+x;if(geometry[i].a==0&&metadata[i].r==0)opaque++;foregroundError=Mathf.Max(foregroundError,Mathf.Abs(result[i].g-raw[i].g));}
                Check("unregistered-forward-depth-never-borrows-scene-history",opaque==441&&foregroundError<1e-6f,opaque);foreground.SetActive(false);Resolve("forward-disocclusion");
                foreach(var flags in new[]{TemporalPixelFlags.ExcludeTaa,TemporalPixelFlags.NoJitter,(TemporalPixelFlags)6})
                {
                    floor.temporalFlags=flags;Resolve("flags-"+(int)flags);Resolve("flags-seeded-"+(int)flags);int used=0;foreach(var p in metadata)if(p.a>0)used++;
                    Check("flags-bypass-history-"+(int)flags,used==0&&PixelError(raw,result)<1e-6f,PixelError(raw,result));
                }
                floor.temporalFlags=TemporalPixelFlags.Normal;

                // Applied subpixel jitter distinguishes ExcludeTaa from NoJitter.
                var flagsProjection=projection;flagsProjection.m03+=.5f/97;flagsProjection.m13-=.75f/97;camera.projectionMatrix=flagsProjection;scene.temporalAntialiasing.jitterUv=new Vector2(-.25f,.375f)/97;
                foreach(var flags in new[]{TemporalPixelFlags.ExcludeTaa,TemporalPixelFlags.NoJitter,(TemporalPixelFlags)6})
                {
                    floor.temporalFlags=flags;Resolve("jittered-flags-"+(int)flags);float error=0,distinguishable=0;
                    for(int y=6;y<91;y++)for(int x=6;x<91;x++)
                    {
                        int i=y*97+x;var dejitter=TaaCurrent(raw,97,97,x+.25f,y-.375f);var value=(((int)flags&4)!=0)?raw[i]:dejitter;
                        for(int c=0;c<4;c++){error=Mathf.Max(error,Mathf.Abs(result[i][c]-value[c]));distinguishable=Mathf.Max(distinguishable,Mathf.Abs(dejitter[c]-raw[i][c]));}
                    }
                    Check("jittered-flag-semantics-"+(int)flags,Used()==0&&error<1e-5f&&distinguishable>.1f,error);
                }
                floor.temporalFlags=TemporalPixelFlags.Normal;camera.projectionMatrix=projection;scene.temporalAntialiasing.jitterUv=Vector2.zero;

                floor.inputs.emissionMap=null;floor.inputs.emission=new Vector3(.08f,.1f,.12f);
                var box=Own(GameObject.CreatePrimitive(PrimitiveType.Cube));box.layer=25;box.transform.position=new Vector3(.45f,0,-.65f);box.transform.localScale=new Vector3(.8f,2,1);
                var wall=new SceneDeferredCamera.Surface{renderer=box.GetComponent<Renderer>(),cull=CullMode.Off};wall.inputs.emission=new Vector3(1.2f,.15f,.08f);scene.surfaces=new[]{floor,wall};
                Resolve("moving-box-first");Resolve("moving-box-seed");int movedReused=0,disoccluded=0;float ghost=0;
                for(int step=0;step<5;step++)
                {
                    var priorMetadata=metadata;box.transform.position+=new Vector3(-.16f,.02f,0);box.transform.rotation=Quaternion.Euler(4*step,11*step,2*step);
                    var frame=Resolve("moving-self-occluding-box-"+step);scene.TryGetFrame(out var geometryFrame);var motionPixels=ReadSceneTarget(geometryFrame.motionVectors);
                    for(int i=0;i<metadata.Length;i++)
                    {
                        if(Mathf.Abs(motionPixels[i].r)+Mathf.Abs(motionPixels[i].g)>.001f&&metadata[i].a>0)movedReused++;
                        if(metadata[i].r==1&&priorMetadata[i].r==2&&metadata[i].a==0)disoccluded++;
                        for(int c=0;c<3;c++)ghost=Mathf.Max(ghost,Mathf.Abs(result[i][c]-raw[i][c]));
                    }
                }
                Check("actual-rigid-motion-reuses-color-history",movedReused>300,movedReused);
                Check("newly-visible-different-identity-rejects-history",disoccluded>50,disoccluded);
                Check("constant-material-disocclusion-has-no-color-trail",ghost<.0001f,ghost);
                var alpha=Own(new Texture2D(2,2,TextureFormat.RGBA32,false,true));alpha.filterMode=FilterMode.Point;alpha.SetPixels(new[]{Color.clear,Color.white,Color.white,Color.clear});alpha.Apply();wall.inputs.albedoMap=alpha;wall.alphaCutoff=.5f;
                Resolve("moving-alpha-cutout-first");Resolve("moving-alpha-cutout-seed");alpha.SetPixels(new[]{Color.white,Color.clear,Color.clear,Color.white});alpha.Apply();Resolve("alpha-content-revision");int alphaRejected=0;
                for(int i=0;i<metadata.Length;i++)if(metadata[i].r==2&&metadata[i].a==0)alphaRejected++;
                Check("updated-alpha-content-rejects-old-correspondence",alphaRejected>100,alphaRejected);wall.inputs.albedoMap=null;wall.alphaCutoff=0;

                // The same actual skin stream must feed geometry, motion and color history.
                var skinHost=Own(new GameObject("TAA two-bone surface"));skinHost.layer=25;skinHost.transform.SetPositionAndRotation(box.transform.position,box.transform.rotation);skinHost.transform.localScale=box.transform.localScale;
                var bone0=Own(new GameObject("TAA bone0")).transform;bone0.SetParent(skinHost.transform,false);var bone1=Own(new GameObject("TAA bone1")).transform;bone1.SetParent(skinHost.transform,false);
                var skin=skinHost.AddComponent<SkinnedMeshRenderer>();var mesh=Own(Instantiate(box.GetComponent<MeshFilter>().sharedMesh));var vertices=mesh.vertices;var weights=new BoneWeight[vertices.Length];var deltas=new Vector3[vertices.Length];
                for(int i=0;i<vertices.Length;i++){float t=(vertices[i].y+.5f)*.8f;weights[i]=new BoneWeight{boneIndex0=0,weight0=1-t,boneIndex1=1,weight1=t};deltas[i]=new Vector3(.08f*(vertices[i].y+.5f),0,-.06f*(vertices[i].x+.5f));}
                mesh.boneWeights=weights;mesh.bindposes=new[]{Matrix4x4.identity,Matrix4x4.identity};mesh.AddBlendShapeFrame("TAA shape",100,deltas,new Vector3[vertices.Length],new Vector3[vertices.Length]);skin.sharedMesh=mesh;skin.bones=new[]{bone0,bone1};skin.rootBone=bone0;skin.sharedMaterial=wall.renderer.sharedMaterial;skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*10);
                var rigid=wall.renderer;wall.renderer=skin;box.SetActive(false);scene.temporalAntialiasing.maximumFrameGap=3;yield return null;Resolve("skin-first");Resolve("skin-seeded");
                for(int pose=1;pose<=3;pose++)
                {
                    bone1.localPosition=new Vector3(.08f*pose,.015f*pose,-.04f*pose);bone1.localRotation=Quaternion.Euler(3*pose,5*pose,-4*pose);skin.SetBlendShapeWeight(0,20*pose);
                    yield return null;yield return null;Resolve("skin-deform-"+pose);scene.TryGetFrame(out var frame);var motionPixels=ReadSceneTarget(frame.motionVectors);int moving=0,reused=0;
                    for(int i=0;i<metadata.Length;i++)if(Mathf.Abs(motionPixels[i].r)+Mathf.Abs(motionPixels[i].g)>.001f){moving++;if(metadata[i].a>0)reused++;}
                    Check("skin-bone-blendshape-color-history-nonvacuous-"+pose,moving>50&&reused>20,reused);
                }
                skinHost.SetActive(false);box.SetActive(true);wall.renderer=rigid;scene.temporalAntialiasing.maximumFrameGap=1;Resolve("skin-return");
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SCENE_TAA")=="1")
                {
                    floor.inputs.emissionMap=checker;floor.inputs.emission=Vector3.one;scene.temporalAntialiasing.reactiveThreshold=1;
                    Resolve("native-color-prime");Resolve("native-color-seed");
                    bool started=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try
                    {
                        if(started)
                        {
                            Resolve("native-prior");camera.transform.position+=new Vector3(.08f,0,0);box.transform.position+=new Vector3(-.08f,.04f,0);
                            var nativeProjection=projection;nativeProjection.m03+=.5f/97;nativeProjection.m13-=.25f/97;camera.projectionMatrix=nativeProjection;scene.temporalAntialiasing.jitterUv=new Vector2(-.25f,.125f)/97;
                            Resolve("native-current");
                            var capturedPost=host.AddComponent<OriginalStyleRenderPipeline>();capturedPost.sceneTemporalSource=scene;
                            Render();Check("native-production-post-resolve",scene.TryGetTemporalColorFrame(out var postFrame)&&postFrame.IsCurrent&&scene.TemporalColorResolveDrawCalls==1);
                            capturedPost.enabled=false;
                        }
                    }
                    finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("requested-native-capture",started&&ended);
                    floor.inputs.emissionMap=null;floor.inputs.emission=new Vector3(.08f,.1f,.12f);scene.temporalAntialiasing.reactiveThreshold=.2f;scene.temporalAntialiasing.jitterUv=Vector2.zero;camera.projectionMatrix=projection;
                }
                box.SetActive(false);scene.surfaces=new[]{floor};camera.transform.position=new Vector3(0,0,-4);Resolve("single-floor-return");
                floor.inputs.emission=new Vector3(4,.7f,.3f);Resolve("reactive-emission-change");Check("large-lighting-change-rejects-history",Used()==0&&PixelError(raw,result)<1e-5f,Used());
                Resolve("reactive-emission-seeded");Check("unchanged-lighting-resumes-history",Used()>6000,Used());floor.inputs.emission=new Vector3(.6f,.2f,.1f);

                foreach(float slope in new[]{35f,65f})
                {
                    plane.transform.rotation=Quaternion.Euler(8,slope,0);Resolve("sloped-first-"+slope);Resolve("sloped-seeded-"+slope);
                    for(int phase=0;phase<4;phase++)
                    {
                        var j=new Vector2((phase%2)*.75f-.375f,(phase/2)*.75f-.375f);var p=projection;p.m03+=2*j.x/97;p.m13+=2*j.y/97;camera.projectionMatrix=p;scene.temporalAntialiasing.jitterUv=-j/97;
                        Resolve("sloped-jitter-"+slope+"-"+phase);Check("sloped-jitter-history-nonvacuous-"+slope+"-"+phase,Used()>100,Used());
                    }
                    scene.temporalAntialiasing.jitterUv=Vector2.zero;camera.projectionMatrix=projection;
                }
                plane.transform.rotation=Quaternion.identity;Resolve("sloped-return");

                var isolated=Resolve("camera-isolation-seed");var isolatedPixels=ReadSceneTarget(isolated.color);
                refScene.motion.enabled=true;refScene.temporalAntialiasing.enabled=true;refCamera.Render();refScene.TryResolveTemporalColor(refCamera,referenceTarget,null,out var otherColor);refCamera.Render();refScene.TryResolveTemporalColor(refCamera,referenceTarget,null,out otherColor);
                Check("two-camera-independent-color-history",otherColor!=null&&otherColor!=isolated.color&&isolated.IsCurrent&&ScenePixelsEqual(isolatedPixels,ReadSceneTarget(isolated.color)));
                Render();Render();Check("missed-resolve-can-consume-current",scene.TryResolveTemporalColor(camera,target,null,out var missed));scene.TryGetTemporalColorFrame(out var missedFrame);metadata=ReadSceneTarget(missedFrame.identityAgeFlagsWeight);Check("missed-resolve-invalidates-sequence-history",Used()==0,Used());
                Resolve("after-missed-resolve");scene.temporalAntialiasing.contentRevision++;Resolve("color-space-revision");Check("color-space-revision-resets-history",Used()==0,Used());
                Resolve("before-skipped-tick");yield return null;yield return null;Resolve("skipped-tick");Check("frame-gap-rejects-old-color",Used()==0,Used());
                foreach(var size in new[]{new Vector2Int(65,49),new Vector2Int(1,1),new Vector2Int(9,1),new Vector2Int(1,9)})
                {
                    camera.targetTexture=Target(size.x,size.y);Resolve("resize-"+size.x+"x"+size.y);Check("resize-rejects-history-"+size.x+"x"+size.y,Used()==0,Used());Resolve("resized-seed-"+size.x+"x"+size.y);
                }
                camera.targetTexture=target;Resolve("size-return");
                for(int kind=0;kind<5;kind++)
                {
                    var frame=Resolve("before-loss-"+kind);var lost=kind==0?frame.color:kind==1?frame.geometry:kind==2?frame.identityAgeFlagsWeight:kind==3?frame.visibleGeometry:frame.visibleIdentityFlags;lost.Release();
                    Check("released-attachment-invalidates-lease-"+kind,!frame.IsCurrent&&!scene.TryGetTemporalColorFrame(out _));Resolve("lost-attachment-rebuild-"+kind);Check("lost-attachment-rejects-history-"+kind,Used()==0,Used());
                }
                Render();Check("foreign-camera-source-rejected",!scene.TryResolveTemporalColor(refCamera,target,null,out _));
                var invalidSource=Own(new RenderTexture(97,97,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear));invalidSource.Create();
                Check("non-hdr-source-rejected",!scene.TryResolveTemporalColor(camera,invalidSource,null,out _));
                Resolve("after-source-rejection");Check("source-rejection-clears-history",Used()==0,Used());
                var alias=Resolve("before-source-alias");Render();Check("owned-history-alias-rejected",!scene.TryResolveTemporalColor(camera,alias.color,null,out _));

                classification=host.AddComponent<TemporalClassification>();classification.surfaces=new[]{new TemporalClassification.Surface{renderer=foreground.GetComponent<Renderer>(),flags=TemporalPixelFlags.ExcludeTaa,cull=CullMode.Off}};foreground.SetActive(true);
                Resolve("external-classification-current");classification.jitterUv=Vector2.one*.001f;Render();Check("external-classification-jitter-mismatch-rejected",!scene.TryResolveTemporalColor(camera,target,classification,out _)&&scene.TemporalColorUnavailableReason.Contains("jitter"));classification.jitterUv=Vector2.zero;
                classification.enabled=false;Render();Check("unavailable-explicit-classification-rejected",!scene.TryResolveTemporalColor(camera,target,classification,out _));classification=null;foreground.SetActive(false);
                var pipeline=host.GetComponent<OriginalStyleRenderPipeline>();if(pipeline==null)pipeline=host.AddComponent<OriginalStyleRenderPipeline>();pipeline.enabled=true;pipeline.sceneTemporalSource=scene;Render();
                Check("actual-production-post-consumes-completed-scene",scene.TryGetTemporalColorFrame(out var production)&&production.IsCurrent&&scene.TemporalColorResolveDrawCalls==1,scene.TemporalColorResolveDrawCalls);
                pipeline.ResetTemporalHistory();Check("production-reset-invalidates-scene-color-history",!production.IsCurrent);pipeline.enabled=false;
                Resolve("post-disabled-reseed");var old=Resolve("lifecycle-seeded");scene.ResetTemporalColorHistory();Check("reset-invalidates-borrowed-result",!old.IsCurrent&&!scene.TryGetTemporalColorFrame(out _));
                Resolve("after-reset");scene.temporalAntialiasing.enabled=false;Render();Check("disabled-releases-color-history-only",scene.TemporalColorTargetCount==0&&scene.MotionTargetCount==2&&!scene.TryGetTemporalColorFrame(out _));
                floor.inputs.emissionMap=null;floor.inputs.emission=new Vector3(.6f,.2f,.1f);scene.motion.enabled=false;Check("default-geometry-unchanged-after-temporal",ScenePixelsEqual(baseline,ReadSceneTarget(Render().mosDepth)));
                void Reject(string name){camera.Render();Check(name,!scene.TryGetFrame(out _)&&scene.TemporalColorTargetCount==0&&!string.IsNullOrEmpty(scene.UnavailableReason));}
                scene.motion.enabled=true;scene.temporalAntialiasing.enabled=true;scene.temporalAntialiasing.historyWeight=float.NaN;Reject("nan-weight-rejected");scene.temporalAntialiasing.historyWeight=.95f;
                scene.temporalAntialiasing.maximumHistory=1;Reject("invalid-age-budget-rejected");scene.temporalAntialiasing.maximumHistory=32;
                scene.temporalAntialiasing.maximumFrameGap=0;Reject("invalid-frame-gap-rejected");scene.temporalAntialiasing.maximumFrameGap=1;
                scene.temporalAntialiasing.jitterUv=new Vector2(float.PositiveInfinity,0);Reject("nonfinite-jitter-rejected");scene.temporalAntialiasing.jitterUv=Vector2.zero;
                floor.temporalFlags=(TemporalPixelFlags)1;Reject("unknown-flags-rejected");floor.temporalFlags=TemporalPixelFlags.Normal;
                scene.temporalAntialiasing.enabled=false;scene.temporalAntialiasing.historyWeight=float.NaN;Render();Check("disabled-invalid-settings-ignored",scene.TemporalColorTargetCount==0);scene.temporalAntialiasing.historyWeight=.95f;scene.temporalAntialiasing.enabled=true;
                var end=Resolve("final-reseed");scene.enabled=false;Check("component-disable-releases-color-history",!end.IsCurrent&&!end.color.IsCreated()&&!end.geometry.IsCreated()&&!end.identityAgeFlagsWeight.IsCreated()&&!end.visibleGeometry.IsCreated()&&!end.visibleIdentityFlags.IsCreated()&&scene.TemporalColorTargetCount==0);
                camera.targetTexture=null;refCamera.targetTexture=null;host.SetActive(false);refHost.SetActive(false);
            }
            finally
            {
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var item in _owned)if(item!=null)Destroy(item);_owned.Clear();
            }
        }

        private static Color TaaCurrent(Color[] pixels,int width,int height,float x,float y)
        {
            int ix=Mathf.FloorToInt(x),iy=Mathf.FloorToInt(y);float fx=x-ix,fy=y-iy;
            Color Point(int px,int py)=>pixels[Mathf.Clamp(py,0,height-1)*width+Mathf.Clamp(px,0,width-1)];
            return Color.LerpUnclamped(Color.LerpUnclamped(Point(ix,iy),Point(ix+1,iy),fx),Color.LerpUnclamped(Point(ix,iy+1),Point(ix+1,iy+1),fx),fy);
        }
    }
}
