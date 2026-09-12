using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifySceneGtaoSpatial(Report report)
        {
            yield return null;
            var previous=FindObjectsOfType<Renderer>();var forced=new bool[previous.Length];
            for(int i=0;i<previous.Length;i++){forced[i]=previous[i].forceRenderingOff;previous[i].forceRenderingOff=true;}
            try
            {
                void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"scene-gtao-spatial-"+name,ok,error);
                var host=Own(new GameObject("Spatial GTAO host"));var camera=host.AddComponent<Camera>();camera.enabled=false;
                camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;
                camera.transform.position=new Vector3(0,0,-4);camera.orthographic=true;camera.orthographicSize=2;camera.aspect=1;
                camera.nearClipPlane=.1f;camera.farClipPlane=40;camera.cullingMask=1<<26;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                RenderTexture Target(int w,int h){var rt=Own(new RenderTexture(w,h,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));rt.Create();return rt;}
                var target=Target(129,129);camera.targetTexture=target;
                var stage=host.AddComponent<SceneDeferredCamera>();stage.sceneEnabled=true;stage.sceneLayers=1<<25;
                stage.lightRadiance=Vector3.zero;stage.ambientIrradiance=Vector3.one;
                var plane=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));plane.layer=25;plane.transform.localScale=Vector3.one*3.8f;
                var cube=Own(GameObject.CreatePrimitive(PrimitiveType.Cube));cube.layer=25;cube.transform.position=new Vector3(.25f,0,-.3f);cube.transform.localScale=new Vector3(.4f,1.8f,.6f);
                var floor=new SceneDeferredCamera.Surface{renderer=plane.GetComponent<Renderer>(),cull=CullMode.Off};
                var wall=new SceneDeferredCamera.Surface{renderer=cube.GetComponent<Renderer>(),cull=CullMode.Off};
                foreach(var s in new[]{floor,wall}){s.inputs.albedo=Vector3.one;s.inputs.mos=new Vector3(0,1,0);}
                stage.surfaces=new[]{floor,wall};stage.screenShadow.enabled=true;
                var g=stage.screenShadow.gtao;g.enabled=true;
                SceneDeferredCamera.Frame Render(){camera.Render();if(!stage.TryGetFrame(out var f))throw new InvalidOperationException(stage.UnavailableReason);return f;}
                Color[] Native(RenderTexture rt)
                {
                    // This offscreen GL.GetGPUProjectionMatrix(..., true) path
                    // already aligns ReadPixels rows with the shader's UV rows.
                    // Do not apply another API-origin flip to guide coordinates.
                    return ReadSceneTarget(rt);
                }
                Vector3 Position(int x,int y,float depth,int width,int height)
                {
                    float u=(x+.5f)/width,v=(y+.5f)/height;
                    var ray=camera.ViewportPointToRay(new Vector3(u,v));var view=camera.worldToCameraMatrix;
                    return ray.GetPoint((depth+view.MultiplyPoint(ray.origin).z)/-view.MultiplyVector(ray.direction).z);
                }
                Vector3 Normal(Color c)=>new Vector3(c.r,c.g,c.b).normalized;
                Color[] Case(string name,bool requireDark=true,bool requireFallback=false)
                {
                    int w=camera.targetTexture.width,h=camera.targetTexture.height,cw=(w+1)/2,ch=(h+1)/2;
                    g.resolution=SceneGtaoResolution.Full;var full=Render();var fullMask=Native(full.shadowOcclusion);var fullGeometry=Native(full.screenGeometry);
                    Check(name+"-full-has-no-extra-target-or-draw",full.gtaoCoarse==null&&stage.ScreenShadowTargetCount==2&&stage.GtaoCoarseDrawCalls==0);
                    g.resolution=SceneGtaoResolution.Half;var frame=Render();var coarse=Native(frame.gtaoCoarse);var mask=Native(frame.shadowOcclusion);var geometry=Native(frame.screenGeometry);
                    Check(name+"-half-dimensions-and-ownership",frame.gtaoCoarse.width==cw&&frame.gtaoCoarse.height==ch&&
                        frame.gtaoCoarse.graphicsFormat==UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_SFloat&&
                        stage.ScreenShadowTargetCount==3&&stage.GtaoCoarseDrawCalls==1&&stage.ScreenShadowResolveDrawCalls==1&&!full.IsCurrent);
                    bool same=ScenePixelsEqual(fullGeometry,geometry),red=true,selected=true;float sampleError=0;int coarseCovered=0;
                    for(int cy=0;cy<ch;cy++)for(int cx=0;cx<cw;cx++)
                    {
                        int picked=-1;for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                        {int x=cx*2+dx,y=cy*2+dy;if(x>=w||y>=h)continue;int i=y*w+x;if(geometry[i].a>0&&(picked<0||geometry[i].a<geometry[picked].a))picked=i;}
                        var actual=coarse[cy*cw+cx];if(picked<0){selected&=actual.r==0&&actual.g==0&&actual.b==0&&actual.a==1;continue;}
                        selected&=actual.r==picked%w&&actual.g==picked/w&&actual.b==geometry[picked].a;coarseCovered++;
                        sampleError=Mathf.Max(sampleError,Mathf.Abs(actual.a-fullMask[picked].g));
                    }
                    Check(name+"-nearest-positive-depth-and-exact-source-pixel",selected&&same&&coarseCovered>0);
                    Check(name+"-coarse-ao-matches-full-receiver-before-unorm",sampleError<.0025f,sampleError);
                    var positions=new Vector3[w*h];for(int y=0;y<h;y++)for(int x=0;x<w;x++)if(geometry[y*w+x].a>0)positions[y*w+x]=Position(x,y,geometry[y*w+x].a,w,h);
                    double worst=0;int fallback=0,interpolated=0,rejected=0,dark=0;bool background=true,finite=true;
                    for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                    {
                        int i=y*w+x;red&=mask[i].r==fullMask[i].r;
                        finite&=!float.IsNaN(mask[i].g)&&mask[i].g>=0&&mask[i].g<=1;
                        if(geometry[i].a<=0){background&=mask[i].r==1&&mask[i].g==1;continue;}
                        var n=Normal(geometry[i]);int bx=(int)Math.Floor((x+.5)*.5-.5)-1,by=(int)Math.Floor((y+.5)*.5-.5)-1;
                        double sum=0,total=0;int supports=0;
                        for(int dy=0;dy<4;dy++)for(int dx=0;dx<4;dx++)
                        {
                            int cx=bx+dx,cy=by+dy;if(cx<0||cx>=cw||cy<0||cy>=ch)continue;
                            var a=coarse[cy*cw+cx];if(a.b<=0)continue;
                            int j=(int)a.g*w+(int)a.r;var normal=Normal(geometry[j]);var delta=positions[j]-positions[i];
                            double sep=Math.Max(Math.Abs(Vector3.Dot(delta,n)),Math.Abs(Vector3.Dot(delta,normal)));
                            double spatial=Math.Max(0,1-Math.Abs(a.r-x)/4.0)*Math.Max(0,1-Math.Abs(a.g-y)/4.0);
                            double weight=spatial*Math.Max(0,1-sep/g.reconstructionDepthTolerance)*
                                Math.Max(0,Math.Min(1,(Vector3.Dot(n,normal)-g.reconstructionNormalThreshold)/(1-g.reconstructionNormalThreshold)));
                            if(weight>0)supports++;else rejected++;
                            total+=weight;sum+=weight*a.a;
                        }
                        double expected;if(total<=1e-6){expected=fullMask[i].g;fallback++;}else{expected=sum/total;if(supports>1)interpolated++;}
                        worst=Math.Max(worst,Math.Abs(expected-mask[i].g));if(mask[i].g<.98f)dark++;
                    }
                    Check(name+"-independent-bilateral-whole-image-oracle",worst<.004&&finite&&background&&(!requireDark||dark>30),(float)worst);
                    Check(name+"-main-red-and-full-geometry-unchanged",red&&same);
                    Check(name+"-support-and-fallback",(!requireFallback||fallback>100)&&(w*h<64||interpolated>20),fallback);
                    return mask;
                }
                var baseline=Render();Check("default-resolution-full",g.resolution==SceneGtaoResolution.Full&&baseline.gtaoCoarse==null);
                var contact=Case("contact-odd");var preview=new Color[contact.Length];
                // Preview uses bottom-left image layout like all existing fixture PNGs.
                var view=ReadSceneTarget(Render().shadowOcclusion);for(int i=0;i<view.Length;i++)preview[i]=new Color(view[i].g,view[i].g,view[i].g,1);
                SaveSsrPreview("scene-gtao-half-contact-visibility",preview,129,129,false);
                SaveSsrPreview("scene-gtao-half-contact-hdr",ReadSceneTarget(target),129,129,false);
                var position=cube.transform.position;var scale=cube.transform.localScale;
                int changed=0;g.resolution=SceneGtaoResolution.Full;var noFilter=Native(Render().shadowOcclusion);
                for(int i=0;i<contact.Length;i++)if(Mathf.Abs(noFilter[i].g-contact[i].g)>.01f)changed++;
                Check("coarse-reconstruction-actually-changes-contact",changed>100,changed);
                g.reconstructionDepthTolerance=.7f;Case("permissive-plane-separation");g.reconstructionDepthTolerance=.02f;
                cube.transform.rotation=Quaternion.Euler(19,27,11);g.reconstructionNormalThreshold=0;Case("permissive-normal");
                g.reconstructionNormalThreshold=.9999f;Case("strict-normal");g.reconstructionNormalThreshold=.9f;cube.transform.rotation=Quaternion.identity;
                cube.transform.position=new Vector3(-.42f,.24f,-.21f);Case("moved-contact");cube.transform.position=position;
                cube.transform.rotation=Quaternion.Euler(19,27,11);Case("normal-discontinuity");cube.transform.rotation=Quaternion.identity;
                cube.transform.localScale=new Vector3(.04f,1.8f,.6f);Case("thin-silhouette");cube.transform.localScale=scale;
                cube.transform.position=new Vector3(.25f,0,-2);Case("far-disconnected",false);cube.transform.position=position;
                camera.orthographic=false;camera.fieldOfView=55;Case("perspective");
                var projection=camera.projectionMatrix;projection.m02=.13f;projection.m12=-.09f;projection.m01=.11f;camera.projectionMatrix=projection;Case("custom-perspective");camera.ResetProjectionMatrix();camera.orthographic=true;
                camera.nearClipPlane=3.3f;Case("near-sphere-intersection");camera.nearClipPlane=3.7f;Case("near-occluder-clipped",false);camera.nearClipPlane=.1f;
                wall.renderer.enabled=false;
                foreach(bool perspective in new[]{false,true})
                {
                    camera.orthographic=!perspective;plane.transform.rotation=Quaternion.Euler(21,32,13);g.slices=1;
                    var white=Case("sloped-white-"+perspective,false);bool all=true;foreach(var c in white)all&=c.g==1;Check("true-tangent-white-"+perspective,all);
                }
                g.slices=4;plane.transform.rotation=Quaternion.identity;camera.orthographic=true;wall.renderer.enabled=true;
                wall.inputs.alpha=0;wall.alphaCutoff=.5f;Case("cutout",false);wall.inputs.alpha=1;wall.alphaCutoff=0;
                // Every coarse cell chooses the foreground; the one-pixel holes
                // expose a different parallel receiver requiring real full AO fallback.
                var perforated=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));perforated.layer=25;
                perforated.transform.position=new Vector3(0,0,-.6f);perforated.transform.localScale=Vector3.one*4;
                var alpha=Own(new Texture2D(129,129,TextureFormat.RGBA32,false,true));alpha.filterMode=FilterMode.Point;alpha.wrapMode=TextureWrapMode.Clamp;
                var alphaPixels=new Color[129*129];for(int y=0;y<129;y++)for(int x=0;x<129;x++)alphaPixels[y*129+x]=new Color(1,1,1,x%2==0&&y%2==0?1:0);alpha.SetPixels(alphaPixels);alpha.Apply();
                var grid=new SceneDeferredCamera.Surface{renderer=perforated.GetComponent<Renderer>(),cull=CullMode.Off,alphaCutoff=.5f};grid.inputs.albedoMap=alpha;grid.inputs.mos=new Vector3(0,1,0);
                stage.surfaces=new[]{floor,grid};Case("perforated-disconnected-fallback",true,true);stage.surfaces=new[]{floor,wall};perforated.SetActive(false);
                foreach(var size in new[]{new Vector2Int(97,65),new Vector2Int(128,96),new Vector2Int(1,1),new Vector2Int(1,9),new Vector2Int(9,1)})
                {camera.targetTexture=Target(size.x,size.y);Case("size-"+size.x+"x"+size.y,size.x>9&&size.y>9);}camera.targetTexture=target;
                var unscaled=Case("before-scale");plane.transform.localScale*=2;cube.transform.localScale*=2;cube.transform.position*=2;camera.transform.position*=2;
                camera.orthographicSize*=2;camera.nearClipPlane*=2;camera.farClipPlane*=2;g.radius*=2;g.normalBias*=2;g.reconstructionDepthTolerance*=2;
                Check("world-scale-invariance",ScenePixelsEqual(unscaled,Case("scaled")));
                plane.transform.localScale/=2;cube.transform.localScale=scale;cube.transform.position=position;camera.transform.position=new Vector3(0,0,-4);
                camera.orthographicSize=2;camera.nearClipPlane=.1f;camera.farClipPlane=40;g.radius=.7f;g.normalBias=.002f;g.reconstructionDepthTolerance=.02f;
                stage.mainLightShadow.enabled=true;stage.mainLightShadow.origin=new Vector3(0,0,-3);stage.mainLightShadow.farPlane=6;stage.mainLightShadow.halfSize=Vector2.one*2;
                stage.mainLightShadow.casters=new[]{new SceneShadowCaster{renderer=wall.renderer,cull=CullMode.Off}};
                stage.lightRadiance=new Vector3(2,1,.5f);stage.lightDirection=new Vector3(.5f,.15f,-1);
                var main=Case("with-main-shadow");int redDark=0;foreach(var c in main)if(c.r<.5f)redDark++;Check("main-red-nonconstant",redDark>50);
                var gtaoOnly=Native(Render().shadowOcclusion);
                stage.screenShadow.capsules=new[]{new SceneCapsuleOccluder{start=new Vector3(-.35f,-.4f,-.2f),end=new Vector3(.2f,.4f,-.2f),radius=.12f}};
                g.enabled=false;var caps=Native(Render().shadowOcclusion);Check("disabled-half-releases-only-coarse",stage.ScreenShadowTargetCount==2&&stage.GtaoCoarseDrawCalls==0);g.enabled=true;
                foreach(var mode in new[]{SceneAmbientCombination.Multiply,SceneAmbientCombination.Minimum})
                {
                    g.combineWithCapsules=mode;var combined=Native(Render().shadowOcclusion);float error=0;int both=0;
                    for(int i=0;i<combined.Length;i++){float expected=mode==SceneAmbientCombination.Multiply?gtaoOnly[i].g*caps[i].g:Mathf.Min(gtaoOnly[i].g,caps[i].g);error=Mathf.Max(error,Mathf.Abs(expected-combined[i].g));if(gtaoOnly[i].g<.99f&&caps[i].g<.99f)both++;}
                    Check("full-resolution-capsule-combination-"+mode,error<.006f&&both>30,error);
                }
                g.combineWithCapsules=SceneAmbientCombination.Multiply;
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_GTAO_SPATIAL")=="1")
                {
                    bool started=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;try{if(started){Render();ReadSceneTarget(target);}}finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("requested-native-capture",started&&ended);
                }
                var first=Render();var firstData=Native(first.gtaoCoarse);
                var other=Own(new GameObject("Other spatial GTAO camera"));var camera2=other.AddComponent<Camera>();camera2.CopyFrom(camera);camera2.enabled=false;camera2.transform.position=camera.transform.position;camera2.targetTexture=Target(97,65);
                var stage2=other.AddComponent<SceneDeferredCamera>();stage2.sceneEnabled=true;stage2.sceneLayers=stage.sceneLayers;stage2.surfaces=stage.surfaces;
                stage2.screenShadow.enabled=true;stage2.screenShadow.gtao.enabled=true;stage2.screenShadow.gtao.resolution=SceneGtaoResolution.Half;stage2.screenShadow.gtao.radius=1.4f;
                camera2.Render();Check("two-camera-coarse-isolation",stage2.TryGetFrame(out var second)&&second.gtaoCoarse!=first.gtaoCoarse&&first.IsCurrent&&ScenePixelsEqual(firstData,Native(first.gtaoCoarse)));other.SetActive(false);camera2.targetTexture=null;
                foreach(int lost in new[]{0,1,2})
                {
                    var old=Render();(lost==0?old.screenGeometry:lost==1?old.shadowOcclusion:old.gtaoCoarse).Release();
                    Check("lost-target-invalidates-"+lost,!old.IsCurrent);var fresh=Render();Check("lost-target-rebuilds-"+lost,fresh.IsCurrent&&stage.ScreenShadowTargetCount==3&&fresh.gtaoCoarse.IsCreated());
                }
                void Reject(string name){camera.Render();Check(name,!stage.TryGetFrame(out _)&&stage.ScreenShadowTargetCount==0&&stage.GtaoCoarseDrawCalls==0&&!string.IsNullOrEmpty(stage.UnavailableReason));}
                g.resolution=(SceneGtaoResolution)99;Reject("unknown-resolution-rejected");g.resolution=SceneGtaoResolution.Half;
                foreach(float value in new[]{0f,-1f,float.NaN,float.PositiveInfinity,10001f}){g.reconstructionDepthTolerance=value;Reject("invalid-depth-"+value);}g.reconstructionDepthTolerance=.02f;
                foreach(float value in new[]{-1f,1f,float.NaN,float.PositiveInfinity}){g.reconstructionNormalThreshold=value;Reject("invalid-normal-"+value);}g.reconstructionNormalThreshold=.9f;
                g.strength=0;g.reconstructionDepthTolerance=0;Reject("validate-before-zero-strength");g.reconstructionDepthTolerance=.02f;
                Check("zero-strength-no-coarse-with-main",Render().gtaoCoarse==null&&stage.ScreenShadowTargetCount==2);g.strength=1;
                g.resolution=SceneGtaoResolution.Full;g.reconstructionDepthTolerance=float.NaN;g.reconstructionNormalThreshold=float.NaN;
                Check("full-ignores-unused-reconstruction",Render().gtaoCoarse==null&&stage.ScreenShadowTargetCount==2);
                g.resolution=SceneGtaoResolution.Half;g.enabled=false;Check("disabled-ignores-unused-reconstruction",Render().gtaoCoarse==null);
                g.enabled=true;g.reconstructionDepthTolerance=.02f;g.reconstructionNormalThreshold=.9f;
                var end=Render();stage.enabled=false;Check("disable-releases-all-three",!end.IsCurrent&&!end.screenGeometry.IsCreated()&&!end.shadowOcclusion.IsCreated()&&!end.gtaoCoarse.IsCreated());
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
