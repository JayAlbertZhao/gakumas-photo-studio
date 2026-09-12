using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifySceneGtao(Report report)
        {
            yield return null;
            var previous=FindObjectsOfType<Renderer>();var forced=new bool[previous.Length];
            for(int i=0;i<previous.Length;i++){forced[i]=previous[i].forceRenderingOff;previous[i].forceRenderingOff=true;}
            try
            {
                void Check(string n,bool ok,float e=0)=>FrameworkCheck(report,"scene-gtao-"+n,ok,e);
                var host=Own(new GameObject("GTAO host"));var camera=host.AddComponent<Camera>();
                camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;
                camera.transform.position=new Vector3(0,0,-4);camera.orthographic=true;camera.orthographicSize=2;camera.aspect=1;
                camera.nearClipPlane=.1f;camera.farClipPlane=40;camera.cullingMask=1<<26;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                var target=Own(new RenderTexture(129,129,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                var stage=host.AddComponent<SceneDeferredCamera>();stage.sceneEnabled=true;stage.sceneLayers=1<<25;stage.lightRadiance=Vector3.zero;stage.ambientIrradiance=Vector3.one;
                var plane=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));plane.layer=25;plane.transform.localScale=Vector3.one*3.8f;
                var cube=Own(GameObject.CreatePrimitive(PrimitiveType.Cube));cube.layer=25;cube.transform.position=new Vector3(.25f,0,-.3f);cube.transform.localScale=new Vector3(.4f,1.8f,.6f);
                foreach(var obj in new[]{plane,cube}){var mesh=Own(Instantiate(obj.GetComponent<MeshFilter>().sharedMesh));mesh.uv2=mesh.uv;obj.GetComponent<MeshFilter>().sharedMesh=mesh;}
                var floor=new SceneDeferredCamera.Surface{renderer=plane.GetComponent<Renderer>(),cull=CullMode.Off};
                var wall=new SceneDeferredCamera.Surface{renderer=cube.GetComponent<Renderer>(),cull=CullMode.Off};
                foreach(var s in new[]{floor,wall}){s.inputs.albedo=Vector3.one;s.inputs.mos=new Vector3(0,1,0);}
                stage.surfaces=new[]{floor,wall};wall.renderer.enabled=false;
                SceneDeferredCamera.Frame Render(){camera.Render();if(!stage.TryGetFrame(out var f))throw new InvalidOperationException(stage.UnavailableReason);return f;}
                Color[] Pixels()=>ReadSceneTarget(camera.targetTexture);
                void White(string name){var mask=ReadSceneTarget(Render().shadowOcclusion);bool all=true;float error=0;foreach(var c in mask){all&=c.g==1;error=Mathf.Max(error,1-c.g);}Check(name,all,error);}
                var baseline=Render();var baseImage=Pixels();var screen=stage.screenShadow;var g=screen.gtao;
                Check("default-no-allocation",baseline.shadowOcclusion==null&&stage.ScreenShadowTargetCount==0&&!g.enabled);
                screen.enabled=true;g.enabled=true;var flat=Render();var flatMask=ReadSceneTarget(flat.shadowOcclusion);
                bool white=true;foreach(var c in flatMask)white&=c.r==1&&c.g==1;
                Check("isolated-plane-and-background-unoccluded",white);
                Check("plane-hdr-unchanged",ScenePixelsEqual(baseImage,Pixels()));
                Check("gtao-alone-produces-prepass-and-resolve",stage.ScreenShadowGeometryDrawCalls==1&&stage.ScreenShadowResolveDrawCalls==1&&stage.ScreenShadowTargetCount==2&&flat.mainLightShadowDepth==null);

                Vector3 Position(int x,int y,float depth,int width,int height)
                {
                    var ray=camera.ViewportPointToRay(new Vector3((x+.5f)/width,(y+.5f)/height));
                    var view=camera.worldToCameraMatrix;float originDepth=-view.MultiplyPoint(ray.origin).z,rate=-view.MultiplyVector(ray.direction).z;
                    return ray.GetPoint((depth-originDepth)/rate);
                }
                // Integrate the clipped cosine density numerically, independently
                // of the shader's closed-form signed antiderivative.
                double Integral(double a,double b,double nv,double nt)
                {
                    if(b<=a)return 0;const int count=256;double sum=0;
                    for(int j=0;j<=count;j++)
                    {double theta=a+(b-a)*j/count;double value=Math.Max(nv*Math.Cos(theta)+nt*Math.Sin(theta),0)*Math.Abs(Math.Sin(theta));sum+=(j==0||j==count?1:j%2==0?2:4)*value;}
                    return sum*(b-a)/(3*count);
                }
                void Oracle(string name,bool darkExpected=true)
                {
                    var f=Render();int width=camera.targetTexture.width,height=camera.targetTexture.height;
                    var geometry=ReadSceneTarget(f.screenGeometry);var visibility=ReadSceneTarget(f.shadowOcclusion);var positions=new Vector3[geometry.Length];
                    for(int y=0;y<height;y++)for(int x=0;x<width;x++)if(geometry[y*width+x].a>0)positions[y*width+x]=Position(x,y,geometry[y*width+x].a,width,height);
                    int tested=0,dark=0;double worst=0;bool finite=true,background=true;
                    foreach(var c in visibility){finite&=!float.IsNaN(c.g)&&c.g>=0&&c.g<=1;if(c.g<.98f)dark++;}
                    for(int i=0;i<geometry.Length;i++)if(geometry[i].a<=0)background&=visibility[i].r==1&&visibility[i].g==1;
                    var vp=camera.projectionMatrix*camera.worldToCameraMatrix;
                    for(int y=3;y<height;y+=7)for(int x=3;x<width;x+=7)
                    {
                        int index=y*width+x;if(geometry[index].a<=0)continue;
                        Vector3 n=new Vector3(geometry[index].r,geometry[index].g,geometry[index].b).normalized;
                        Vector3 world=positions[index],v=-camera.ViewportPointToRay(new Vector3((x+.5f)/width,(y+.5f)/height)).direction.normalized;
                        Vector3 axis=Position(x+1,y,geometry[index].a,width,height)-world;axis=(axis-v*Vector3.Dot(axis,v)).normalized;Vector3 cross=Vector3.Cross(v,axis),origin=world+n*g.normalBias;
                        double nv=Vector3.Dot(n,v),blocked=0;
                        if(nv>1e-5)
                        for(int slice=0;slice<g.slices;slice++)
                        {
                            double phi=(slice+.5)*Math.PI/g.slices;Vector3 t=axis*(float)Math.Cos(phi)+cross*(float)Math.Sin(phi);
                            Vector4 clip=vp*new Vector4(world.x,world.y,world.z,1),dt=vp*new Vector4(t.x,t.y,t.z,0);
                            double px=.5*(dt.x*clip.w-clip.x*dt.w)/(clip.w*clip.w)*width,py=.5*(dt.y*clip.w-clip.y*dt.w)/(clip.w*clip.w)*height;
                            double length=Math.Sqrt(px*px+py*py),radius=Math.Min(length*g.radius,g.maxRadiusPixels);if(radius<1||length<1e-6)continue;
                            px/=length;py/=length;double nt=Vector3.Dot(n,t),gamma=Math.Atan2(nt,nv),lo=gamma-Math.PI*.5,hi=gamma+Math.PI*.5;
                            double Horizon(int sign,double baseCos)
                            {
                                double horizon=baseCos;
                                for(int step=1;step<=g.stepsPerSide;step++)
                                {
                                    double offset=1+(radius-1)*step*step/(g.stepsPerSide*g.stepsPerSide),candidate=baseCos;
                                    int sx=(int)Math.Floor(x+.5+sign*px*offset),sy=(int)Math.Floor(y+.5+sign*py*offset);
                                    if(sx>=0&&sx<width&&sy>=0&&sy<height&&(sx!=x||sy!=y)&&geometry[sy*width+sx].a>0)
                                    {
                                        Vector3 delta=positions[sy*width+sx]-origin;double distance=delta.magnitude;
                                        if(distance>1e-6&&distance<g.radius&&Vector3.Dot(delta,n)>0)
                                        {
                                            double axial=Vector3.Dot(delta,v),transverse=Vector3.Dot(delta,t)*sign,projected=Math.Sqrt(axial*axial+transverse*transverse);
                                            if(projected>1e-6&&transverse>0)
                                            {double fade=Math.Max(0,Math.Min(1,(1-distance/g.radius)/Math.Max(1-g.falloffStart,1e-6)));candidate=Math.Max(baseCos,baseCos+(Math.Max(-1,Math.Min(1,axial/projected))-baseCos)*fade);}
                                        }
                                    }
                                    horizon=candidate>=horizon?candidate:horizon+(candidate-horizon)*g.thicknessBlend;
                                }
                                return Math.Max(-1,Math.Min(1,horizon));
                            }
                            double hneg=-Math.Acos(Horizon(-1,Math.Cos(lo))),hpos=Math.Acos(Horizon(1,Math.Cos(hi)));
                            blocked+=Integral(lo,Math.Max(lo,hneg),nv,nt)+Integral(Math.Min(hi,hpos),hi,nv,nt);
                        }
                        double expected=1-g.strength*Math.Min(1,Math.Max(0,blocked/g.slices));
                        worst=Math.Max(worst,Math.Abs(expected-visibility[index].g));tested++;
                    }
                    Check(name+"-numerical-slice-oracle",worst<.004&&tested>120&&finite&&background&&(!darkExpected||dark>30),(float)worst);
                }
                wall.renderer.enabled=true;Oracle("contact-corner");
                var actualGeometry=ReadSceneTarget(Render().screenGeometry);float geometryError=0;int geometryPixels=0;
                for(int y=0;y<129;y++)for(int x=0;x<129;x++)
                {
                    float wx=(x+.5f)/129*4-2,wy=(y+.5f)/129*4-2;
                    bool onPlane=Mathf.Abs(wx)<1.9f&&Mathf.Abs(wy)<1.9f,onCube=wx>.05f&&wx<.45f&&Mathf.Abs(wy)<.9f;
                    var actual=actualGeometry[y*129+x];float expected=onCube?3.4f:onPlane?4:0;geometryError=Mathf.Max(geometryError,Mathf.Abs(actual.a-expected));
                    if(expected>0){geometryError=Mathf.Max(geometryError,Mathf.Abs(actual.r),Mathf.Abs(actual.g),Mathf.Abs(actual.b+1));geometryPixels++;}
                }
                Check("actual-orthographic-step-depth-normal-oracle",geometryError<1e-6f&&geometryPixels>14000,geometryError);
                var contact=ReadSceneTarget(Render().shadowOcclusion);int contactDark=0,convexLight=0;
                for(int y=8;y<121;y++)for(int x=8;x<121;x++)
                {if(x<64&&x>48&&contact[y*129+x].g<.98f)contactDark++;if(x>67&&x<77&&y>45&&y<80&&contact[y*129+x].g>.99f)convexLight++;}
                Check("concave-contact-dark-and-convex-front-visible",contactDark>30&&convexLight>100);
                var preview=new Color[contact.Length];for(int i=0;i<preview.Length;i++)preview[i]=new Color(contact[i].g,contact[i].g,contact[i].g,1);
                SaveSsrPreview("scene-gtao-contact-visibility",preview,129,129,false);SaveSsrPreview("scene-gtao-contact-hdr",Pixels(),129,129,false);
                foreach(int count in new[]{1,2,8,16}){g.slices=count;Oracle("slices-"+count);}g.slices=4;
                foreach(int count in new[]{2,4,16,32}){g.stepsPerSide=count;Oracle("steps-"+count);}g.stepsPerSide=8;
                g.radius=.2f;Oracle("radius-below-visible-step-height",false);White("short-radius-does-not-infer-hidden-sidewall");g.radius=1.4f;Oracle("radius-1.4");g.radius=.7f;
                g.strength=.35f;Oracle("partial-strength");g.strength=1;
                g.normalBias=.04f;Oracle("normal-bias");g.normalBias=.002f;
                g.falloffStart=1;Oracle("hard-world-radius");g.falloffStart=.8f;
                g.maxRadiusPixels=8;Oracle("pixel-radius-cap");g.maxRadiusPixels=64;
                var cubePosition=cube.transform.position;cube.transform.position=new Vector3(-.4f,.3f,-.25f);Oracle("moving-occluder");cube.transform.position=cubePosition;
                cube.transform.rotation=Quaternion.Euler(7,20,13);Oracle("rotated-occluder");cube.transform.rotation=Quaternion.identity;
                camera.orthographic=false;camera.fieldOfView=55;Oracle("perspective");
                camera.transform.position=new Vector3(.8f,.2f,-4);camera.transform.LookAt(Vector3.zero);Oracle("oblique-view");camera.transform.position=new Vector3(0,0,-4);camera.transform.rotation=Quaternion.identity;
                var projection=camera.projectionMatrix;projection.m02=.11f;projection.m12=-.09f;projection.m01=.08f;camera.projectionMatrix=projection;Oracle("custom-perspective");camera.ResetProjectionMatrix();camera.orthographic=true;
                projection=Matrix4x4.Ortho(-1.7f,2.3f,-2.2f,1.8f,.1f,40);projection.m01=.08f;camera.projectionMatrix=projection;Oracle("custom-orthographic");camera.ResetProjectionMatrix();
                camera.nearClipPlane=3.3f;Oracle("near-plane-intersects-search-radius");camera.nearClipPlane=3.7f;Oracle("near-clipped-occluder",false);White("clipped-front-does-not-leave-occlusion");camera.nearClipPlane=.1f;
                wall.renderer.enabled=false;plane.transform.rotation=Quaternion.Euler(22,30,0);Oracle("sloped-isolated-plane",false);White("sloped-plane-has-no-finite-slice-darkening");
                camera.orthographic=false;camera.fieldOfView=55;Oracle("perspective-sloped-isolated-plane",false);White("perspective-sloped-plane-unoccluded");camera.orthographic=true;
                plane.transform.rotation=Quaternion.Euler(-31,19,17);g.slices=1;White("single-slice-sloped-plane-unoccluded");g.slices=4;plane.transform.rotation=Quaternion.identity;wall.renderer.enabled=true;
                var beforeNormal=ReadSceneTarget(Render().shadowOcclusion);var normalMap=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));normalMap.SetPixel(0,0,new Color(.8f,.3f,.9f,1));normalMap.Apply();floor.inputs.normalMap=normalMap;
                Check("normal-map-does-not-change-gtao",ScenePixelsEqual(beforeNormal,ReadSceneTarget(Render().shadowOcclusion)));floor.inputs.normalMap=null;
                wall.inputs.alpha=0;wall.alphaCutoff=.5f;Oracle("alpha-cutout-removes-occluder",false);var cut=ReadSceneTarget(Render().shadowOcclusion);bool clear=true;foreach(var c in cut)clear&=c.g==1;Check("cutout-does-not-leave-contact-ghost",clear);wall.inputs.alpha=1;wall.alphaCutoff=0;
                var cubeScale=cube.transform.localScale;cube.transform.localScale=new Vector3(.06f,1.8f,.6f);Oracle("thin-occluder");var thin=ReadSceneTarget(Render().shadowOcclusion);
                g.thicknessBlend=.2f;Oracle("thin-decay-heuristic");var decayed=ReadSceneTarget(Render().shadowOcclusion);double lighter=0;bool noDarker=true;
                for(int i=0;i<thin.Length;i++){lighter+=decayed[i].g-thin[i].g;noDarker&=decayed[i].g>=thin[i].g;}
                Check("thin-decay-reduces-overocclusion",noDarker&&lighter>1,(float)lighter);g.thicknessBlend=0;cube.transform.localScale=cubeScale;
                cube.transform.position=new Vector3(.25f,0,-2);Oracle("far-disconnected-surface",false);cube.transform.position=cubePosition;
                var unscaled=ReadSceneTarget(Render().shadowOcclusion);plane.transform.localScale*=2;cube.transform.localScale*=2;cube.transform.position*=2;camera.transform.position*=2;camera.orthographicSize*=2;camera.nearClipPlane*=2;camera.farClipPlane*=2;g.radius*=2;g.normalBias*=2;Oracle("world-scale");
                var scaled=ReadSceneTarget(Render().shadowOcclusion);float scaleError=PixelError(unscaled,scaled);Check("world-unit-radius-scale-invariance",scaleError<.004f,scaleError);
                plane.transform.localScale/=2;cube.transform.localScale=cubeScale;cube.transform.position=cubePosition;camera.transform.position=new Vector3(0,0,-4);camera.orthographicSize=2;camera.nearClipPlane=.1f;camera.farClipPlane=40;g.radius=.7f;g.normalBias=.002f;

                var gtaoOnly=ReadSceneTarget(Render().shadowOcclusion);
                screen.capsules=new[]{new SceneCapsuleOccluder{start=new Vector3(-.35f,-.4f,-.2f),end=new Vector3(.2f,.4f,-.2f),radius=.12f}};
                g.enabled=false;var capsuleOnly=ReadSceneTarget(Render().shadowOcclusion);g.enabled=true;
                foreach(var mode in new[]{SceneAmbientCombination.Multiply,SceneAmbientCombination.Minimum})
                {
                    g.combineWithCapsules=mode;var combined=ReadSceneTarget(Render().shadowOcclusion);float worst=0;int both=0;
                    for(int i=0;i<combined.Length;i++){float expected=mode==SceneAmbientCombination.Minimum?Mathf.Min(gtaoOnly[i].g,capsuleOnly[i].g):gtaoOnly[i].g*capsuleOnly[i].g;worst=Mathf.Max(worst,Mathf.Abs(expected-combined[i].g));if(gtaoOnly[i].g<.99f&&capsuleOnly[i].g<.99f)both++;}
                    Check("capsule-combination-"+mode,worst<.006f&&both>30,worst);
                }
                g.combineWithCapsules=SceneAmbientCombination.Multiply;
                stage.mainLightShadow.enabled=true;stage.mainLightShadow.origin=new Vector3(0,0,-3);stage.mainLightShadow.farPlane=6;stage.mainLightShadow.halfSize=Vector2.one*2;
                stage.mainLightShadow.casters=new[]{new SceneShadowCaster{renderer=wall.renderer,cull=CullMode.Off}};stage.lightRadiance=new Vector3(2,1,.5f);stage.lightDirection=new Vector3(.5f,.15f,-1);
                g.enabled=false;var oldMain=ReadSceneTarget(Render().shadowOcclusion);g.enabled=true;var newMain=ReadSceneTarget(Render().shadowOcclusion);bool redSame=true;int redDark=0;for(int i=0;i<newMain.Length;i++){redSame&=newMain[i].r==oldMain[i].r;if(newMain[i].r<.5f)redDark++;}Check("main-shadow-red-unchanged",redSame&&redDark>50);
                var emission=new Vector3(.2f,.1f,.05f);floor.inputs.emission=wall.inputs.emission=emission;
                void LightingScope(string name)
                {
                    var light=stage.lightRadiance;var ambient=stage.ambientIrradiance;float giScale=stage.giBaseScale;
                    g.enabled=false;screen.enabled=false;stage.ambientIrradiance=Vector3.zero;stage.giBaseScale=0;Render();var nonAmbient=Pixels();
                    stage.lightRadiance=Vector3.zero;stage.mainLightShadow.enabled=false;floor.inputs.emission=wall.inputs.emission=Vector3.zero;stage.ambientIrradiance=ambient;stage.giBaseScale=giScale;Render();var indirect=Pixels();
                    stage.lightRadiance=light;stage.mainLightShadow.enabled=true;floor.inputs.emission=wall.inputs.emission=emission;screen.enabled=true;g.enabled=true;
                    var f=Render();var image=Pixels();var mask=ReadSceneTarget(f.shadowOcclusion);float worst=0;
                    for(int i=0;i<image.Length;i++)foreach(int channel in new[]{0,1,2})worst=Mathf.Max(worst,Mathf.Abs(nonAmbient[i][channel]+indirect[i][channel]*mask[i].g-image[i][channel]));
                    Check(name+"-direct-and-emission-not-ao-darkened",worst<.004f,worst);
                }
                LightingScope("ambient");var gi=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));gi.SetPixel(0,0,new Color(1.7f,.8f,.3f,1));gi.Apply();
                floor.gi=new SceneGiInput{source=SceneGiSource.Lightmap,lightmap=gi};wall.gi=new SceneGiInput{source=SceneGiSource.Lightmap,lightmap=gi};stage.directionalGiWeight=.6f;LightingScope("baked-gi");floor.gi.source=wall.gi.source=SceneGiSource.None;stage.directionalGiWeight=0;
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_GTAO")=="1")
                {
                    bool started=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;try{if(started){Render();Pixels();}}finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("requested-native-capture",started&&ended);
                }
                var first=Render();var maskFirst=ReadSceneTarget(first.shadowOcclusion);
                var other=Own(new GameObject("Other GTAO camera"));var camera2=other.AddComponent<Camera>();camera2.CopyFrom(camera);camera2.enabled=false;camera2.transform.position=camera.transform.position;
                var target2=Own(new RenderTexture(97,65,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target2.Create();camera2.targetTexture=target2;
                var stage2=other.AddComponent<SceneDeferredCamera>();stage2.sceneEnabled=true;stage2.sceneLayers=stage.sceneLayers;stage2.surfaces=stage.surfaces;stage2.screenShadow.enabled=true;stage2.screenShadow.gtao.enabled=true;stage2.screenShadow.gtao.radius=1.4f;
                camera2.Render();Check("two-camera-isolation",stage2.TryGetFrame(out var second)&&second.shadowOcclusion!=first.shadowOcclusion&&first.IsCurrent&&ScenePixelsEqual(maskFirst,ReadSceneTarget(first.shadowOcclusion)));other.SetActive(false);camera2.targetTexture=null;
                foreach(bool geometry in new[]{false,true}){var old=Render();(geometry?old.screenGeometry:old.shadowOcclusion).Release();Check("lost-target-invalidates-frame-"+geometry,!old.IsCurrent);var fresh=Render();Check("lost-target-rebuilt-"+geometry,fresh.IsCurrent&&fresh.screenGeometry.IsCreated()&&fresh.shadowOcclusion.IsCreated());}
                screen.capsules=Array.Empty<SceneCapsuleOccluder>();stage.mainLightShadow.enabled=false;camera.targetTexture=target2;Oracle("odd-resize");camera.targetTexture=target;
                void Reject(string name){camera.Render();Check(name,!stage.TryGetFrame(out _)&&stage.ScreenShadowTargetCount==0&&!string.IsNullOrEmpty(stage.UnavailableReason));}
                g.radius=0;Reject("zero-radius-rejected");g.radius=.7f;g.strength=float.NaN;Reject("nan-strength-rejected");g.strength=1;
                g.slices=0;Reject("zero-slices-rejected");g.slices=17;Reject("slice-budget-rejected");g.slices=4;g.stepsPerSide=1;Reject("step-minimum-rejected");g.stepsPerSide=33;Reject("step-budget-rejected");g.stepsPerSide=8;
                g.maxRadiusPixels=0;Reject("pixel-radius-zero-rejected");g.maxRadiusPixels=257;Reject("pixel-radius-budget-rejected");g.maxRadiusPixels=64;
                g.normalBias=-1;Reject("negative-bias-rejected");g.normalBias=1;Reject("bias-beyond-radius-rejected");g.normalBias=.002f;
                g.falloffStart=1.1f;Reject("invalid-falloff-rejected");g.falloffStart=.8f;g.thicknessBlend=-1;Reject("invalid-thickness-rejected");g.thicknessBlend=0;
                g.combineWithCapsules=(SceneAmbientCombination)99;Reject("unknown-combination-rejected");g.combineWithCapsules=SceneAmbientCombination.Multiply;
                g.strength=0;var empty=Render();Check("zero-strength-no-targets",empty.shadowOcclusion==null&&stage.ScreenShadowGeometryDrawCalls==0);g.slices=0;Reject("invalid-before-zero-strength-pruning");g.slices=4;g.strength=1;
                g.enabled=false;g.radius=float.NaN;var off=Render();Check("disabled-invalid-gtao-ignored",off.shadowOcclusion==null);g.radius=.7f;
                screen.gtao=null;Check("null-gtao-disabled",Render().shadowOcclusion==null);screen.gtao=g;g.enabled=true;
                var end=Render();stage.enabled=false;Check("disable-releases-borrowed-targets",!end.IsCurrent&&!end.screenGeometry.IsCreated()&&!end.shadowOcclusion.IsCreated());
                camera.targetTexture=null;host.SetActive(false);plane.SetActive(false);cube.SetActive(false);
            }
            finally
            {
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
        }
    }
}
