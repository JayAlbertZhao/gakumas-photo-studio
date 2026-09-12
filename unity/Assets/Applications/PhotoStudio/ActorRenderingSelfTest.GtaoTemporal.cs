using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifySceneGtaoTemporal(Report report)
        {
            yield return null;
            var previous=FindObjectsOfType<Renderer>();var forced=new bool[previous.Length];
            for(int i=0;i<previous.Length;i++){forced[i]=previous[i].forceRenderingOff;previous[i].forceRenderingOff=true;}
            try
            {
                void Check(string n,bool ok,float error=0)=>FrameworkCheck(report,"scene-gtao-temporal-"+n,ok,error);
                RenderTexture Target(int w,int h){var t=Own(new RenderTexture(w,h,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));t.Create();return t;}
                var host=Own(new GameObject("Temporal GTAO camera"));var camera=host.AddComponent<Camera>();camera.enabled=false;
                camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;camera.cullingMask=1<<26;
                camera.transform.position=new Vector3(0,0,-4);camera.orthographic=true;camera.orthographicSize=2;camera.aspect=1;
                camera.nearClipPlane=.1f;camera.farClipPlane=40;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                var target=Target(97,97);camera.targetTexture=target;
                var scene=host.AddComponent<SceneDeferredCamera>();scene.sceneEnabled=true;scene.sceneLayers=1<<25;
                scene.lightRadiance=Vector3.zero;scene.ambientIrradiance=Vector3.one;scene.screenShadow.enabled=true;
                var plane=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));plane.layer=25;plane.transform.localScale=Vector3.one*3.8f;
                var box=Own(GameObject.CreatePrimitive(PrimitiveType.Cube));box.layer=25;box.transform.position=new Vector3(.25f,0,-.3f);box.transform.localScale=new Vector3(.4f,1.8f,.6f);box.transform.rotation=Quaternion.Euler(0,0,17);
                var floor=new SceneDeferredCamera.Surface{renderer=plane.GetComponent<Renderer>(),cull=CullMode.Off};
                var wall=new SceneDeferredCamera.Surface{renderer=box.GetComponent<Renderer>(),cull=CullMode.Off};
                foreach(var s in new[]{floor,wall}){s.inputs.albedo=Vector3.one;s.inputs.mos=new Vector3(0,1,0);}
                scene.surfaces=new[]{floor,wall};var g=scene.screenShadow.gtao;g.enabled=true;g.slices=2;
                SceneDeferredCamera.Frame Render(){camera.Render();if(!scene.TryGetFrame(out var f))throw new InvalidOperationException(scene.UnavailableReason);return f;}
                var baseline=ReadSceneTarget(Render().shadowOcclusion);var baselineHdr=ReadSceneTarget(target);
                Check("default-no-temporal-targets-or-motion",scene.GtaoTemporalTargetCount==0&&scene.MotionTargetCount==0&&!g.temporal.enabled);
                g.temporal.enabled=true;camera.Render();Check("requires-explicit-scene-motion",!scene.TryGetFrame(out _)&&scene.GtaoTemporalTargetCount==0&&scene.UnavailableReason.Contains("motion"));scene.motion.enabled=true;
                var referenceHost=Own(new GameObject("Fresh high-direction AO reference"));var referenceCamera=referenceHost.AddComponent<Camera>();referenceCamera.enabled=false;
                var referenceTarget=Target(97,97);var referenceScene=referenceHost.AddComponent<SceneDeferredCamera>();referenceScene.sceneEnabled=true;referenceScene.sceneLayers=scene.sceneLayers;
                referenceScene.lightRadiance=Vector3.zero;referenceScene.ambientIrradiance=Vector3.one;referenceScene.screenShadow.enabled=true;
                Color[] Reference()
                {
                    referenceCamera.CopyFrom(camera);referenceCamera.enabled=false;referenceCamera.targetTexture=referenceTarget;
                    referenceCamera.transform.SetPositionAndRotation(camera.transform.position,camera.transform.rotation);referenceScene.surfaces=scene.surfaces;
                    var r=referenceScene.screenShadow.gtao;r.enabled=true;r.slices=g.slices*6;r.stepsPerSide=g.stepsPerSide;r.maxRadiusPixels=g.maxRadiusPixels;r.radius=g.radius;r.strength=g.strength;r.normalBias=g.normalBias;r.falloffStart=g.falloffStart;r.thicknessBlend=g.thicknessBlend;r.resolution=g.resolution;r.reconstructionDepthTolerance=g.reconstructionDepthTolerance;r.reconstructionNormalThreshold=g.reconstructionNormalThreshold;
                    referenceCamera.Render();if(!referenceScene.TryGetFrame(out var frame))throw new InvalidOperationException(referenceScene.UnavailableReason);return ReadSceneTarget(frame.shadowOcclusion);
                }
                Color[] oldHistory=null,oldNormal=null,lastRaw=null,lastResult=null,lastMotion=null,lastGeometry=null,lastIdentity=null;
                Matrix4x4 oldView=default,oldProjection=default;
                Vector3 Position(float u,float v,float depth,Matrix4x4 view,Matrix4x4 projection)
                {
                    var inverse=(projection*view).inverse;var a=inverse*new Vector4(u*2-1,v*2-1,-1,1);a/=a.w;var b=inverse*new Vector4(u*2-1,v*2-1,1,1);b/=b.w;
                    float da=-(view*a).z,db=-(view*b).z;return Vector3.LerpUnclamped(a,b,(depth-da)/(db-da));
                }
                Vector3 Normal(Color c)=>new Vector3(c.r,c.g,c.b).normalized;
                int used=0,reactive=0,unmapped=0;
                SceneDeferredCamera.Frame Case(string name,bool reset=false)
                {
                    var f=Render();int w=f.gtaoCurrent.width,h=f.gtaoCurrent.height;
                    var raw=ReadSceneTarget(f.gtaoCurrent);var result=ReadSceneTarget(f.gtaoHistory);var normals=ReadSceneTarget(f.gtaoHistoryNormalIdentity);
                    var motion=ReadSceneTarget(f.motionVectors);var mapping=ReadSceneTarget(f.previousNormalIdentity);var geo=ReadSceneTarget(f.screenGeometry);var combined=ReadSceneTarget(f.shadowOcclusion);
                    var view=camera.worldToCameraMatrix;var projection=camera.projectionMatrix;var settings=g.temporal;
                    float error=0,normalError=0,combineError=0;used=reactive=unmapped=0;bool finite=true;int covered=0;
                    for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                    {
                        int i=y*w+x;float u=(x+.5f)/w,v=(y+.5f)/h;Color expected=new Color(1,0,0,0);Vector3 n=Normal(geo[i]);
                        if(geo[i].a>0)
                        {
                            covered++;float center=raw[i].r;expected=new Color(center,geo[i].a,1,0);
                            float pu=u-motion[i].r,pv=v-motion[i].g;float total=0,sum=0,age=0;
                            if(!reset&&oldHistory!=null&&motion[i].a>.5f&&motion[i].b>0&&mapping[i].a>0&&pu>=0&&pv>=0&&pu<1&&pv<1)
                            {
                                var point=Position(pu,pv,motion[i].b,oldView,oldProjection);var expectedNormal=Normal(mapping[i]);
                                float fx=x-motion[i].r*w,fy=y-motion[i].g*h;int bx=Mathf.FloorToInt(fx),by=Mathf.FloorToInt(fy);fx-=bx;fy-=by;
                                for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                                {
                                    int px=bx+dx,py=by+dy;if(px<0||py<0||px>=w||py>=h)continue;int j=py*w+px;
                                    var a=oldHistory[j];var data=oldNormal[j];var pn=Normal(data);
                                    if(a.g<=0||a.b<1||data.a!=mapping[i].a||Vector3.Dot(pn,expectedNormal)<settings.normalThreshold)continue;
                                    var delta=Position((px+.5f)/w,(py+.5f)/h,a.g,oldView,oldProjection)-point;
                                    if(Mathf.Max(Mathf.Abs(Vector3.Dot(delta,pn)),Mathf.Abs(Vector3.Dot(delta,expectedNormal)))>settings.depthTolerance)continue;
                                    float weight=(dx==0?1-fx:fx)*(dy==0?1-fy:fy);sum+=a.r*weight;age+=a.b*weight;total+=weight;
                                }
                            }
                            if(total>1e-6f)
                            {
                                sum/=total;age/=total;
                                if(Mathf.Abs(sum-center)>settings.reactiveThreshold)reactive++;
                                else
                                {
                                    float lo=center,hi=center,count=0;double moment=0,square=0;var point=Position(u,v,geo[i].a,view,projection);
                                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                                    {
                                        int px=x+dx,py=y+dy;if(px<0||py<0||px>=w||py>=h)continue;int j=py*w+px;var pn=Normal(geo[j]);
                                        if(geo[j].a<=0||mapping[j].a!=mapping[i].a||Vector3.Dot(pn,n)<settings.normalThreshold)continue;
                                        var delta=Position((px+.5f)/w,(py+.5f)/h,geo[j].a,view,projection)-point;
                                        if(Mathf.Max(Mathf.Abs(Vector3.Dot(delta,pn)),Mathf.Abs(Vector3.Dot(delta,n)))>settings.depthTolerance)continue;
                                        float a=raw[j].r;lo=Mathf.Min(lo,a);hi=Mathf.Max(hi,a);moment+=a;square+=(double)a*a;count++;
                                    }
                                    double average=count>0?moment/count:center;float mean=(float)average,sigma=count>0?(float)Math.Sqrt(Math.Max(0,square/count-average*average)):0;
                                    lo=Mathf.Min(center,Mathf.Max(lo-settings.clampPadding,mean-settings.varianceGamma*sigma-settings.clampPadding));
                                    hi=Mathf.Max(center,Mathf.Min(hi+settings.clampPadding,mean+settings.varianceGamma*sigma+settings.clampPadding));
                                    lo=Mathf.Max(lo,center-settings.maximumHistoryDeviation);hi=Mathf.Min(hi,center+settings.maximumHistoryDeviation);
                                    float weight=Mathf.Min(settings.historyWeight,age/(age+1))*Mathf.Clamp01(total);
                                    expected=new Color(Mathf.Lerp(center,Mathf.Clamp(sum,lo,hi),weight),geo[i].a,1+Mathf.Min(age,settings.maximumHistory-1)*Mathf.Clamp01(total),weight);used++;
                                }
                            }
                            else unmapped++;
                            normalError=Mathf.Max(normalError,Vector3.Distance(Normal(normals[i]),n),Mathf.Abs(normals[i].a-mapping[i].a));
                        }
                        else normalError=Mathf.Max(normalError,Mathf.Abs(normals[i].r),Mathf.Abs(normals[i].g),Mathf.Abs(normals[i].b),Mathf.Abs(normals[i].a));
                        for(int k=0;k<4;k++){error=Mathf.Max(error,Mathf.Abs(result[i][k]-expected[k]));finite&=!float.IsNaN(result[i][k])&&!float.IsInfinity(result[i][k]);}
                        if(scene.screenShadow.capsules.Length==0)combineError=Mathf.Max(combineError,Mathf.Abs(combined[i].g-result[i].r));
                    }
                    Check(name+"-whole-image-reproject-reject-clip-age-oracle",finite&&error<.0001f,error);
                    Check(name+"-current-normal-identity-and-rg8-consumption",normalError<.0001f&&combineError<.0025f,Mathf.Max(normalError,combineError));
                    Check(name+"-owned-five-targets-two-draws",covered>0&&scene.GtaoTemporalTargetCount==5&&scene.GtaoTemporalRawDrawCalls==1&&scene.GtaoTemporalResolveDrawCalls==1&&scene.GtaoHistoryAvailable);
                    if(reset)Check(name+"-rejects-history-and-restarts-phase",used==0&&scene.GtaoTemporalPhase==0,unmapped);
                    oldHistory=result;oldNormal=normals;oldView=view;oldProjection=projection;lastRaw=raw;lastResult=result;lastMotion=motion;lastGeometry=geo;lastIdentity=mapping;return f;
                }
                Case("first",true);
                foreach(var resolution in new[]{SceneGtaoResolution.Full,SceneGtaoResolution.Half})
                {
                    g.resolution=resolution;scene.ResetGtaoHistory();oldHistory=null;var reference=Reference();var rawFrames=new List<Color[]>();var filteredFrames=new List<Color[]>();var phaseSum=new float[target.width*target.height];
                    for(int frame=0;frame<30;frame++)
                    {
                        Case("stationary-"+resolution+"-"+frame,frame==0);Check("phase-sequence-"+resolution+"-"+frame,scene.GtaoTemporalPhase==frame%6);
                        if(frame<6)for(int i=0;i<phaseSum.Length;i++)phaseSum[i]+=lastRaw[i].r/6;
                        if(frame>=18){rawFrames.Add(lastRaw);filteredFrames.Add(lastResult);}
                    }
                    double rawError=0,filteredError=0,rawVariation=0,filteredVariation=0;int pixels=0;float integralError=0;
                    for(int i=0;i<phaseSum.Length;i++)
                    {
                        integralError=Mathf.Max(integralError,Mathf.Abs(phaseSum[i]-reference[i].g));
                        if(lastGeometry[i].a<=0||reference[i].g>.98f)continue;pixels++;
                        for(int frame=0;frame<rawFrames.Count;frame++)
                        {
                            rawError+=Math.Abs(rawFrames[frame][i].r-reference[i].g);filteredError+=Math.Abs(filteredFrames[frame][i].r-reference[i].g);
                            if(frame>0){rawVariation+=Math.Abs(rawFrames[frame][i].r-rawFrames[frame-1][i].r);filteredVariation+=Math.Abs(filteredFrames[frame][i].r-filteredFrames[frame-1][i].r);}
                        }
                    }
                    Check("six-rotations-match-fresh-twelve-direction-integral-"+resolution,integralError<.003f,integralError);
                    Check("stationary-temporal-error-improves-"+resolution,pixels>100&&rawError>1&&filteredError<rawError*.95,(float)(filteredError/Math.Max(rawError,1e-9)));
                    Check("stationary-temporal-variation-reduces-"+resolution,rawVariation>1&&filteredVariation<rawVariation*.8,(float)(filteredVariation/Math.Max(rawVariation,1e-9)));
                    var preview=new Color[lastResult.Length];for(int i=0;i<preview.Length;i++)preview[i]=new Color(lastResult[i].r,lastResult[i].r,lastResult[i].r,1);
                    SaveSsrPreview("scene-gtao-temporal-stationary-"+resolution,preview,97,97,false);
                }
                g.temporal.reactiveThreshold=.025f;Case("dynamic-reactive-threshold",true);
                int disoccluded=0,strongReactive=0,moving=0;float largestGhost=0;var original=box.transform.position;
                for(int step=0;step<10;step++)
                {
                    box.transform.position=new Vector3(.25f-.12f*(step+1),.04f*step,-.3f);Case("moving-occluder-"+step);var reference=Reference();
                    strongReactive+=reactive;
                    for(int i=0;i<lastResult.Length;i++)
                    {
                        if(Mathf.Abs(lastMotion[i].r)+Mathf.Abs(lastMotion[i].g)>.001f)moving++;
                        if(lastIdentity[i].a>0&&lastResult[i].a==0&&lastMotion[i].a>0)disoccluded++;
                        if(lastGeometry[i].a>0&&reference[i].g>.995f)largestGhost=Mathf.Max(largestGhost,1-lastResult[i].r);
                    }
                }
                Check("moving-shadow-and-disocclusion-controls-nonvacuous",strongReactive>20&&disoccluded>20&&moving>100,strongReactive);
                Check("uncovered-white-reference-has-bounded-ghost",largestGhost<.12f,largestGhost);
                box.transform.position=original;Case("occluder-return");
                var rotation=box.transform.rotation;
                box.transform.rotation=Quaternion.Euler(13,24,17);Case("self-occluding-rotated-box");
                box.transform.rotation=Quaternion.Euler(-8,-19,17);Case("opposite-side-disocclusion");
                box.transform.rotation=rotation;Case("rotated-box-return");
                var alpha=Own(new Texture2D(2,2,TextureFormat.RGBA32,false,true));alpha.filterMode=FilterMode.Point;alpha.SetPixels(new[]{Color.clear,Color.white,Color.white,Color.clear});alpha.Apply();
                wall.inputs.albedoMap=alpha;wall.alphaCutoff=.5f;Case("alpha-cutout-revision");Case("alpha-cutout-seeded");
                alpha.SetPixels(new[]{Color.white,Color.clear,Color.clear,Color.white});alpha.Apply();Case("alpha-cutout-disocclusion");Check("alpha-revision-rejects-visible-surface",unmapped>100,unmapped);
                wall.inputs.albedoMap=null;wall.alphaCutoff=0;Case("alpha-cutout-return");
                var isolated=Case("before-camera-interleave");var isolatedPixels=ReadSceneTarget(isolated.gtaoHistory);
                referenceScene.motion.enabled=true;referenceScene.screenShadow.gtao.temporal.enabled=true;
                referenceCamera.Render();referenceCamera.Render();
                Check("two-camera-independent-history",referenceScene.TryGetFrame(out var otherFrame)&&otherFrame.gtaoHistory!=isolated.gtaoHistory&&otherFrame.gtaoCurrent!=isolated.gtaoCurrent&&otherFrame.gtaoHistoryNormalIdentity!=isolated.gtaoHistoryNormalIdentity&&isolated.IsCurrent&&ScenePixelsEqual(isolatedPixels,ReadSceneTarget(isolated.gtaoHistory)));
                Case("after-camera-interleave");Check("interleave-retains-correspondence",used>100,used);
                referenceScene.screenShadow.gtao.temporal.enabled=false;referenceScene.motion.enabled=false;

                var skinHost=Own(new GameObject("Temporal two-bone occluder"));skinHost.layer=25;
                skinHost.transform.SetPositionAndRotation(box.transform.position,box.transform.rotation);skinHost.transform.localScale=box.transform.localScale;
                var bone0=Own(new GameObject("Temporal bone0")).transform;bone0.SetParent(skinHost.transform,false);
                var bone1=Own(new GameObject("Temporal bone1")).transform;bone1.SetParent(skinHost.transform,false);
                var skin=skinHost.AddComponent<SkinnedMeshRenderer>();var mesh=Own(Instantiate(box.GetComponent<MeshFilter>().sharedMesh));var vertices=mesh.vertices;var weights=new BoneWeight[vertices.Length];
                var deltas=new Vector3[vertices.Length];for(int i=0;i<vertices.Length;i++){float blend=(vertices[i].y+.5f)*.8f;weights[i]=new BoneWeight{boneIndex0=0,weight0=1-blend,boneIndex1=1,weight1=blend};deltas[i]=new Vector3(.08f*(vertices[i].y+.5f),0,-.06f*(vertices[i].x+.5f));}
                mesh.boneWeights=weights;mesh.bindposes=new[]{Matrix4x4.identity,Matrix4x4.identity};mesh.AddBlendShapeFrame("Temporal synthetic shape",100,deltas,new Vector3[vertices.Length],new Vector3[vertices.Length]);
                skin.sharedMesh=mesh;skin.bones=new[]{bone0,bone1};skin.rootBone=bone0;skin.sharedMaterial=wall.renderer.sharedMaterial;skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*10);
                var rigid=wall.renderer;wall.renderer=skin;box.SetActive(false);g.temporal.maximumFrameGap=3;yield return null;Case("skin-first",true);Case("skin-seeded");
                for(int pose=1;pose<=3;pose++)
                {
                    bone1.localPosition=new Vector3(.08f*pose,.015f*pose,-.04f*pose);bone1.localRotation=Quaternion.Euler(3*pose,5*pose,-4*pose);skin.SetBlendShapeWeight(0,20*pose);
                    yield return null;yield return null;Case("skin-deform-"+pose);int movingSkin=0,reusedSkin=0;
                    for(int i=0;i<lastMotion.Length;i++)if(Mathf.Abs(lastMotion[i].r)+Mathf.Abs(lastMotion[i].g)>.001f){movingSkin++;if(lastResult[i].a>0)reusedSkin++;}
                    Check("skin-deform-motion-and-history-nonvacuous-"+pose,movingSkin>50&&reusedSkin>20,reusedSkin);
                }
                skinHost.SetActive(false);box.SetActive(true);wall.renderer=rigid;g.temporal.maximumFrameGap=1;Case("skin-to-rigid",true);
                camera.transform.position+=new Vector3(.11f,.07f,-.03f);Case("camera-reprojection");Check("camera-reprojection-uses-history",used>100,used);
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_GTAO_TEMPORAL")=="1")
                {
                    bool started=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try{if(started){Case("native-prior");camera.transform.position+=new Vector3(.08f,0,0);box.transform.position+=new Vector3(-.08f,.04f,0);Case("native-current");}}
                    finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("requested-native-capture",started&&ended);
                }
                scene.ResetMotionHistory();Case("motion-reset",true);Case("motion-reset-reseed");
                var oldFrame=Render();scene.ResetGtaoHistory();Check("explicit-reset-invalidates-frame",!oldFrame.IsCurrent&&!scene.TryGetFrame(out _));Case("explicit-reset",true);
                camera.transform.position+=new Vector3(1.1f,0,0);Case("camera-cut",true);camera.transform.position=new Vector3(0,0,-4);Case("camera-cut-return",true);
                g.radius=.8f;Case("radius-change",true);g.radius=.7f;Case("radius-return",true);
                g.temporal.historyWeight=.6f;Case("history-configuration-change",true);g.temporal.historyWeight=.85f;Case("history-configuration-return",true);
                floor.motionRevision++;Case("surface-revision");Check("surface-revision-rejects-floor",unmapped>100,unmapped);
                yield return null;yield return null;Case("skipped-camera-frame-rejects-age",true);
                camera.orthographic=false;camera.fieldOfView=55;Case("projection-cut",true);Case("perspective-reseed");
                var p=camera.projectionMatrix;p.m02+=.03f;p.m12-=.02f;camera.projectionMatrix=p;Case("minor-off-axis-reprojection");
                camera.ResetProjectionMatrix();camera.orthographic=true;Case("orthographic-return",true);
                foreach(var size in new[]{new Vector2Int(65,49),new Vector2Int(1,1),new Vector2Int(9,1),new Vector2Int(1,9)})
                {camera.targetTexture=Target(size.x,size.y);oldHistory=null;Case("resize-"+size.x+"x"+size.y,true);Case("resized-reseed-"+size.x+"x"+size.y);}camera.targetTexture=target;oldHistory=null;Case("size-return",true);
                foreach(int kind in new[]{0,1,2})
                {
                    var frame=Render();(kind==0?frame.gtaoCurrent:kind==1?frame.gtaoHistory:frame.gtaoHistoryNormalIdentity).Release();Check("lost-target-invalidates-"+kind,!frame.IsCurrent);Case("lost-target-reseed-"+kind,true);
                }
                scene.mainLightShadow.enabled=true;scene.mainLightShadow.origin=new Vector3(0,0,-3);scene.mainLightShadow.farPlane=6;scene.mainLightShadow.halfSize=Vector2.one*2;
                scene.mainLightShadow.casters=new[]{new SceneShadowCaster{renderer=wall.renderer,cull=CullMode.Off}};scene.lightDirection=new Vector3(.5f,.15f,-1);scene.lightRadiance=new Vector3(2,1,.5f);
                var temporalMain=ReadSceneTarget(Case("main-shadow-outside-history").shadowOcclusion);
                g.temporal.enabled=false;var spatialMain=ReadSceneTarget(Render().shadowOcclusion);bool sameRed=true;int darkRed=0;
                for(int i=0;i<temporalMain.Length;i++){sameRed&=temporalMain[i].r==spatialMain[i].r;if(temporalMain[i].r<.5f)darkRed++;}
                Check("main-red-identical-and-nonconstant",sameRed&&darkRed>50,darkRed);g.temporal.enabled=true;oldHistory=null;Case("main-temporal-reseed",true);
                scene.screenShadow.capsules=new[]{new SceneCapsuleOccluder{start=new Vector3(-.35f,-.4f,-.2f),end=new Vector3(.2f,.4f,-.2f),radius=.12f}};
                g.enabled=false;var capsuleOnly=ReadSceneTarget(Render().shadowOcclusion);g.enabled=true;
                foreach(var mode in new[]{SceneAmbientCombination.Multiply,SceneAmbientCombination.Minimum})
                {
                    g.combineWithCapsules=mode;oldHistory=null;var frame=Case("capsule-mode-"+mode,true);var pixels=ReadSceneTarget(frame.shadowOcclusion);float worst=0;int both=0;
                    for(int i=0;i<pixels.Length;i++){float expected=mode==SceneAmbientCombination.Minimum?Mathf.Min(lastResult[i].r,capsuleOnly[i].g):lastResult[i].r*capsuleOnly[i].g;worst=Mathf.Max(worst,Mathf.Abs(expected-pixels[i].g));if(lastResult[i].r<.99f&&capsuleOnly[i].g<.99f)both++;}
                    Check("full-resolution-capsules-combined-after-history-"+mode,worst<.005f&&both>30,worst);
                }
                scene.screenShadow.capsules=Array.Empty<SceneCapsuleOccluder>();g.combineWithCapsules=SceneAmbientCombination.Multiply;
                scene.ambientIrradiance=Vector3.zero;scene.lightRadiance=new Vector3(2,1,.5f);floor.inputs.emission=wall.inputs.emission=new Vector3(.2f,.1f,.05f);
                g.temporal.enabled=false;Render();var directEmission=ReadSceneTarget(target);g.temporal.enabled=true;oldHistory=null;Case("direct-emission-reseed",true);var temporalDirect=ReadSceneTarget(target);
                Check("direct-emission-identical-without-indirect",ScenePixelsEqual(directEmission,temporalDirect));
                scene.mainLightShadow.enabled=false;scene.ambientIrradiance=Vector3.one;scene.lightRadiance=Vector3.zero;floor.inputs.emission=wall.inputs.emission=Vector3.zero;
                g.temporal.rotateSamples=false;Case("rotation-disabled",true);Case("rotation-disabled-stationary");Check("rotation-disabled-stays-phase-zero",scene.GtaoTemporalPhase==0);
                var currentNoRotation=lastRaw;g.temporal.enabled=false;var legacy=ReadSceneTarget(Render().shadowOcclusion);float difference=0;for(int i=0;i<legacy.Length;i++)difference=Mathf.Max(difference,Mathf.Abs(legacy[i].g-currentNoRotation[i].r));
                Check("rotation-off-current-matches-legacy-half",difference<.0025f,difference);Check("disabled-releases-temporal-only",scene.GtaoTemporalTargetCount==0&&scene.MotionTargetCount==2&&scene.GtaoTemporalRawDrawCalls==0);
                scene.motion.enabled=false;g.resolution=SceneGtaoResolution.Full;camera.transform.position=new Vector3(0,0,-4);box.transform.position=original;var restored=ReadSceneTarget(Render().shadowOcclusion);
                Check("default-full-rg8-and-hdr-restored-exact",ScenePixelsEqual(baseline,restored)&&ScenePixelsEqual(baselineHdr,ReadSceneTarget(target))&&scene.MotionTargetCount==0&&scene.GtaoTemporalTargetCount==0);scene.motion.enabled=true;
                void Reject(string name){camera.Render();Check(name,!scene.TryGetFrame(out _)&&scene.GtaoTemporalTargetCount==0&&!string.IsNullOrEmpty(scene.UnavailableReason));}
                g.temporal.enabled=true;g.temporal.historyWeight=float.NaN;g.strength=0;Reject("invalid-before-zero-strength-pruning");g.temporal.historyWeight=.85f;g.strength=1;
                g.temporal.maximumHistory=1;Reject("history-budget-rejected");g.temporal.maximumHistory=16;
                g.temporal.maximumFrameGap=0;Reject("frame-gap-rejected");g.temporal.maximumFrameGap=1;
                g.temporal.normalThreshold=1;Reject("normal-threshold-rejected");g.temporal.normalThreshold=.9f;
                g.temporal.reactiveThreshold=0;Reject("reactive-threshold-rejected");g.temporal.reactiveThreshold=.2f;
                g.temporal.enabled=false;g.temporal.historyWeight=float.NaN;Render();Check("disabled-invalid-settings-ignored",scene.GtaoTemporalTargetCount==0);
                g.temporal.historyWeight=.85f;g.temporal.enabled=true;oldHistory=null;var end=Case("final-reseed",true);scene.enabled=false;
                Check("disable-releases-all-history",!end.IsCurrent&&!end.gtaoCurrent.IsCreated()&&!end.gtaoHistory.IsCreated()&&!end.gtaoHistoryNormalIdentity.IsCreated()&&scene.GtaoTemporalTargetCount==0);
                camera.targetTexture=null;referenceCamera.targetTexture=null;host.SetActive(false);referenceHost.SetActive(false);
            }
            finally
            {
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
        }
    }
}
