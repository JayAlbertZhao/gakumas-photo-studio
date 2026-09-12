using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifySceneScreenShadows(Report report)
        {
            yield return null;
            var previous=FindObjectsOfType<Renderer>();var forced=new bool[previous.Length];
            for(int i=0;i<previous.Length;i++){forced[i]=previous[i].forceRenderingOff;previous[i].forceRenderingOff=true;}
            try
            {
                void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"scene-screen-shadow-"+name,ok,error);
                var host=Own(new GameObject("Screen shadow host"));var camera=host.AddComponent<Camera>();
                camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;
                camera.transform.position=new Vector3(0,0,-4);camera.orthographic=true;camera.orthographicSize=2;camera.aspect=1;
                camera.nearClipPlane=.1f;camera.farClipPlane=40;camera.cullingMask=1<<26;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                var target=Own(new RenderTexture(129,129,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                var stage=host.AddComponent<SceneDeferredCamera>();stage.sceneEnabled=true;stage.sceneLayers=1<<25;
                stage.lightDirection=Vector3.back;stage.lightRadiance=new Vector3(3,2,1);stage.ambientIrradiance=new Vector3(.4f,.3f,.2f);
                var receiver=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));receiver.layer=25;receiver.transform.localScale=Vector3.one*3.8f;
                var mesh=Own(Instantiate(receiver.GetComponent<MeshFilter>().sharedMesh));mesh.uv2=mesh.uv;receiver.GetComponent<MeshFilter>().sharedMesh=mesh;
                var surface=new SceneDeferredCamera.Surface{renderer=receiver.GetComponent<Renderer>(),cull=CullMode.Off};
                surface.inputs.albedo=new Vector3(.5f,.4f,.3f);surface.inputs.mos=new Vector3(.1f,.7f,.4f);surface.inputs.emission=new Vector3(.12f,.08f,.04f);stage.surfaces=new[]{surface};
                var casterObject=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));casterObject.layer=24;casterObject.transform.position=new Vector3(.2f,.15f,-1);casterObject.transform.localScale=new Vector3(.8f,.6f,1);
                var caster=new SceneShadowCaster{renderer=casterObject.GetComponent<Renderer>(),cull=CullMode.Off};
                var main=stage.mainLightShadow;main.enabled=true;main.origin=new Vector3(0,0,-3);main.farPlane=6;main.halfSize=Vector2.one*2;main.casters=new[]{caster};
                var settings=stage.screenShadow;
                SceneDeferredCamera.Frame Render(){camera.Render();if(!stage.TryGetFrame(out var f))throw new InvalidOperationException(stage.UnavailableReason);return f;}
                Color[] Pixels()=>ReadSceneTarget(camera.targetTexture);
                Vector3 Rgb(Color p)=>new Vector3(p.r,p.g,p.b);
                float Error(Vector3 a,Vector3 b)=>Mathf.Max(Mathf.Abs(a.x-b.x),Mathf.Abs(a.y-b.y),Mathf.Abs(a.z-b.z));
                var baselineFrame=Render();var baseline=Pixels();Check("default-no-screen-allocation",baselineFrame.shadowOcclusion==null&&stage.ScreenShadowTargetCount==0&&stage.ScreenShadowGeometryDrawCalls==0);
                settings.enabled=true;var initial=Render();var initialPixels=Pixels();
                Check("actual-full-resolution-rg8-target",initial.shadowOcclusion.graphicsFormat==GraphicsFormat.R8G8_UNorm&&initial.shadowOcclusion.width==129&&initial.shadowOcclusion.height==129&&initial.shadowOcclusion.depth==0&&initial.shadowOcclusion.filterMode==FilterMode.Point);
                Check("geometry-prepass-and-resolve-submitted",stage.ScreenShadowTargetCount==2&&stage.ScreenShadowGeometryDrawCalls==1&&stage.ScreenShadowResolveDrawCalls==1);
                Check("hard-shadow-matches-old-direct-path",PixelError(baseline,initialPixels)<.003f,PixelError(baseline,initialPixels));
                void GeometryOracle(string name)
                {
                    var f=Render();var geometry=ReadSceneTarget(f.screenGeometry);var coverage=ReadSceneTarget(f.albedoCoverage);float worst=0;int samples=0,background=0;
                    for(int y=0;y<129;y++)for(int x=0;x<129;x++)
                    {
                        int i=y*129+x;if(coverage[i].a<.5f){if(geometry[i].a==0)background++;continue;}
                        var ray=camera.ViewportPointToRay(new Vector3((x+.5f)/129,(y+.5f)/129));
                        if(!new Plane(receiver.transform.forward,receiver.transform.position).Raycast(ray,out float distance))continue;
                        float depth=-camera.worldToCameraMatrix.MultiplyPoint(ray.GetPoint(distance)).z;
                        var normal=receiver.transform.worldToLocalMatrix.transpose.MultiplyVector(Vector3.back).normalized;
                        worst=Mathf.Max(worst,Mathf.Abs(depth-geometry[i].a),Error(normal,Rgb(geometry[i])));samples++;
                    }
                    Check(name+"-geometry-depth-normal-oracle",worst<.00003f&&samples>8000&&background>500,worst);
                }
                void MainOracle(string name)
                {
                    var f=Render();var mask=ReadSceneTarget(f.shadowOcclusion);var geometry=ReadSceneTarget(f.screenGeometry);
                    var inverse=caster.renderer.localToWorldMatrix.inverse;var light=stage.lightDirection.normalized;float worst=0;int samples=0,dark=0;
                    for(int y=0;y<129;y++)for(int x=0;x<129;x++)
                    {
                        int i=y*129+x;if(geometry[i].a<=0)continue;var ray=camera.ViewportPointToRay(new Vector3((x+.5f)/129,(y+.5f)/129));
                        if(!new Plane(receiver.transform.forward,receiver.transform.position).Raycast(ray,out float distance))continue;
                        var world=ray.GetPoint(distance)+Rgb(geometry[i]).normalized*main.normalBias;var local=inverse.MultiplyPoint(world);var direction=inverse.MultiplyVector(light);
                        float t=-local.z/direction.z;var hit=local+direction*t;
                        if(Mathf.Abs(Mathf.Abs(hit.x)-.5f)<.06f||Mathf.Abs(Mathf.Abs(hit.y)-.5f)<.06f)continue;
                        bool blocked=caster.renderer.enabled&&t>main.depthBias&&Mathf.Abs(hit.x)<.5f&&Mathf.Abs(hit.y)<.5f;
                        float expected=blocked?1-main.strength:1;worst=Mathf.Max(worst,Mathf.Abs(expected-mask[i].r));samples++;if(blocked)dark++;
                    }
                    Check(name+"-main-source-ray-oracle",worst<=1f/255&&samples>8000&&dark>30,worst);
                }
                GeometryOracle("orthographic");MainOracle("orthographic");
                main.strength=.37f;MainOracle("fractional-strength");main.strength=1;
                main.filter=SceneShadowFilter.Pcf3x3;MainOracle("pcf-interiors");main.filter=SceneShadowFilter.Hard;
                casterObject.transform.position+=new Vector3(.4f,-.3f,0);MainOracle("moving-caster");casterObject.transform.position-=new Vector3(.4f,-.3f,0);
                stage.lightDirection=new Vector3(.2f,-.15f,-1);MainOracle("oblique-main");stage.lightDirection=Vector3.back;
                camera.orthographic=false;camera.fieldOfView=55;GeometryOracle("perspective");MainOracle("perspective");camera.orthographic=true;
                camera.projectionMatrix=Matrix4x4.Ortho(-1.9f,2.1f,-2.1f,1.9f,.2f,25);GeometryOracle("custom-projection");MainOracle("custom-projection");camera.ResetProjectionMatrix();
                var geometryBefore=ReadSceneTarget(Render().screenGeometry);var maskBefore=ReadSceneTarget(Render().shadowOcclusion);
                var normalMap=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));normalMap.SetPixel(0,0,new Color(.8f,.5f,.9f,1));normalMap.Apply();surface.inputs.normalMap=normalMap;
                var mapped=Render();Check("normal-map-does-not-change-geometry-or-screen-shadow",ScenePixelsEqual(geometryBefore,ReadSceneTarget(mapped.screenGeometry))&&ScenePixelsEqual(maskBefore,ReadSceneTarget(mapped.shadowOcclusion)));
                surface.inputs.normalMap=null;

                var capsule=new SceneCapsuleOccluder{start=new Vector3(-.4f,-.3f,-.3f),end=new Vector3(.35f,.45f,-.3f),radius=.13f};settings.capsules=new[]{capsule};
                double Dot(Vector3 a,Vector3 b)=>(double)a.x*b.x+(double)a.y*b.y+(double)a.z*b.z;
                double RayCapsule(Vector3 origin,Vector3 ray,SceneCapsuleOccluder c,double limit)
                {
                    // Double-precision intersection with the union of a finite cylinder
                    // and both endpoint spheres. No sampled GPU mask is used by this oracle.
                    var segment=c.end-c.start;double length2=Dot(segment,segment);var delta=origin-c.start;
                    double along=length2>1e-12?Math.Max(0,Math.Min(1,Dot(delta,segment)/length2)):0;
                    double distance2=0;for(int j=0;j<3;j++){double v=delta[j]-segment[j]*along;distance2+=v*v;}
                    if(distance2<=(double)c.radius*c.radius)return 0;
                    double nearest=limit,aa=Dot(ray,ray);
                    for(int endpoint=0;endpoint<2;endpoint++)
                    {
                        var center=endpoint==0?c.start:c.end;
                        var d=origin-center;double bb=Dot(d,ray),cc=Dot(d,d)-(double)c.radius*c.radius,discriminant=bb*bb-aa*cc;
                        if(discriminant>=0){double t=(-bb-Math.Sqrt(discriminant))/aa;if(t>=0)nearest=Math.Min(nearest,t);}
                    }
                    if(length2<1e-12)return nearest;
                    double dr=Dot(delta,segment),vr=Dot(ray,segment),a=aa-vr*vr/length2,b=Dot(delta,ray)-dr*vr/length2,cc2=Dot(delta,delta)-dr*dr/length2-(double)c.radius*c.radius;
                    double disc=b*b-a*cc2;if(a>1e-10&&disc>=0)for(int sign=-1;sign<=1;sign+=2)
                    {double t=(-b+sign*Math.Sqrt(disc))/a,position=dr+t*vr;if(t>=0&&position>=0&&position<=length2)nearest=Math.Min(nearest,t);}
                    return nearest;
                }
                void CapsuleOracle(string name,bool expectOcclusion=true)
                {
                    var f=Render();var geometry=ReadSceneTarget(f.screenGeometry);var mask=ReadSceneTarget(f.shadowOcclusion);float worst=0;int samples=0,occluded=0;
                    for(int y=0;y<129;y++)for(int x=0;x<129;x++)
                    {
                        int index=y*129+x;if(geometry[index].a<=0)continue;var eyeRay=camera.ViewportPointToRay(new Vector3((x+.5f)/129,(y+.5f)/129));
                        if(!new Plane(receiver.transform.forward,receiver.transform.position).Raycast(eyeRay,out float distance))continue;
                        var normal=Rgb(geometry[index]).normalized;var world=eyeRay.GetPoint(distance)+normal*settings.capsuleNormalBias;
                        var tangent=Vector3.Cross(Mathf.Abs(normal.y)<.9f?Vector3.up:Vector3.right,normal).normalized;var bitangent=Vector3.Cross(normal,tangent);double visibility=0;
                        for(int i=0;i<settings.capsuleSamples;i++)
                        {
                            int bits=i;double v=0,fraction=.5;while(bits>0){v+=(bits%2)*fraction;bits/=2;fraction*=.5;}
                            double u=(i+.5)/settings.capsuleSamples,angle=2*Math.PI*v;
                            var ray=tangent*(float)(Math.Sqrt(u)*Math.Cos(angle))+bitangent*(float)(Math.Sqrt(u)*Math.Sin(angle))+normal*(float)Math.Sqrt(1-u);
                            double nearest=settings.capsuleMaxDistance;foreach(var c in settings.capsules)if(c.enabled)nearest=RayCapsule(world,ray,c,nearest);
                            visibility+=nearest/settings.capsuleMaxDistance;
                        }
                        float expected=Mathf.Lerp(1,(float)(visibility/settings.capsuleSamples),settings.capsuleStrength);
                        worst=Mathf.Max(worst,Mathf.Abs(expected-mask[index].g));samples++;if(expected<.99f)occluded++;
                    }
                    Check(name+"-capsule-hemisphere-ray-oracle",worst<.004f&&samples>12000&&(expectOcclusion?occluded>100:occluded==0),worst);
                }
                foreach(int count in new[]{8,16,32,64}){settings.capsuleSamples=count;CapsuleOracle("samples-"+count);}settings.capsuleSamples=32;
                SaveSsrPreview("scene-screen-shadow-capsule",ReadSceneTarget(Render().shadowOcclusion),129,129,false);
                var oldEnd=capsule.end;capsule.end=capsule.start;CapsuleOracle("zero-length-is-sphere");capsule.end=oldEnd;
                var oldStart=capsule.start;capsule.start=capsule.end;capsule.end=oldStart;CapsuleOracle("endpoint-order");capsule.end=capsule.start;capsule.start=oldStart;capsule.end=oldEnd;
                capsule.start+=Vector3.right*.3f;capsule.end+=Vector3.right*.3f;CapsuleOracle("moving-capsule");capsule.start=oldStart;capsule.end=oldEnd;
                settings.capsuleStrength=.35f;CapsuleOracle("partial-ao-strength");settings.capsuleStrength=1;
                settings.capsuleNormalBias=.05f;CapsuleOracle("ao-normal-bias");settings.capsuleNormalBias=.002f;
                settings.capsuleMaxDistance=.1f;CapsuleOracle("finite-ao-distance",false);settings.capsuleMaxDistance=1;
                capsule.start=new Vector3(0,-.4f,0);capsule.end=new Vector3(0,.4f,0);capsule.radius=.3f;CapsuleOracle("inside-capsule");capsule.start=oldStart;capsule.end=oldEnd;capsule.radius=.13f;
                capsule.enabled=false;CapsuleOracle("disabled-capsule",false);capsule.enabled=true;
                receiver.transform.position=Vector3.forward*.2f;GeometryOracle("moving-receiver");CapsuleOracle("moving-receiver");receiver.transform.position=Vector3.zero;
                receiver.transform.rotation=Quaternion.Euler(10,15,0);GeometryOracle("sloped-receiver");CapsuleOracle("sloped-receiver");receiver.transform.rotation=Quaternion.identity;
                camera.orthographic=false;camera.fieldOfView=55;CapsuleOracle("perspective-receiver");camera.orthographic=true;
                var secondCapsule=new SceneCapsuleOccluder{start=new Vector3(.2f,-.5f,-.4f),end=new Vector3(.7f,.2f,-.25f),radius=.19f};
                settings.capsules=new[]{capsule,secondCapsule};CapsuleOracle("two-capsules-nearest-union");var union=ReadSceneTarget(Render().shadowOcclusion);settings.capsules=new[]{secondCapsule,capsule};
                Check("capsule-order-independent",ScenePixelsEqual(union,ReadSceneTarget(Render().shadowOcclusion)));settings.capsules=new[]{capsule,capsule};CapsuleOracle("duplicate-capsule-not-double-darkened");settings.capsules=new[]{capsule};
                main.enabled=false;CapsuleOracle("ao-without-main-light-shadow");var aoOnly=ReadSceneTarget(Render().shadowOcclusion);bool whiteMain=true;foreach(var p in aoOnly)whiteMain&=p.r==1;Check("ao-only-red-channel-visible",whiteMain);main.enabled=true;
                var albedo=Own(new Texture2D(2,1,TextureFormat.RGBAFloat,false,true));albedo.filterMode=FilterMode.Point;albedo.SetPixels(new[]{new Color(1,1,1,0),Color.white});albedo.Apply();surface.inputs.albedoMap=albedo;surface.alphaCutoff=.5f;
                var cut=Render();var cutG=ReadSceneTarget(cut.screenGeometry);var cutMask=ReadSceneTarget(cut.shadowOcclusion);var cutCoverage=ReadSceneTarget(cut.albedoCoverage);int holes=0;bool clean=true;
                for(int i=0;i<cutG.Length;i++)if(cutCoverage[i].a<.5f){holes++;clean&=cutG[i].a==0&&cutMask[i].r==1&&cutMask[i].g==1;}
                Check("receiver-cutout-prepass-matches-material-and-clear-mask",holes>6000&&clean);surface.inputs.albedoMap=null;surface.alphaCutoff=0;

                var noActor=Render();var noActorMask=ReadSceneTarget(noActor.shadowOcclusion);var noActorGeometry=ReadSceneTarget(noActor.screenGeometry);var noActorImage=Pixels();
                var actor=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));actor.layer=26;actor.transform.localScale=Vector3.one*.4f;actor.transform.position=Vector3.back;
                var actorMaterial=Own(new Material(Resources.Load<Shader>("MonitorEmission")));actorMaterial.SetTexture("_MonitorTex",Texture2D.whiteTexture);actorMaterial.SetVector("_MonitorTint",new Vector4(1,0,1,1));actor.GetComponent<Renderer>().sharedMaterial=actorMaterial;
                var withActor=Render();Check("forward-actor-excluded-from-prepass-and-visibility",ScenePixelsEqual(noActorMask,ReadSceneTarget(withActor.shadowOcclusion))&&ScenePixelsEqual(noActorGeometry,ReadSceneTarget(withActor.screenGeometry)));
                Check("forward-actor-not-darkened-by-scene-screen-ao",Error(Rgb(Pixels()[64*129+64]),new Vector3(1,0,1))<.002f);
                actor.transform.position=Vector3.forward;Render();Check("scene-depth-still-occludes-forward-actor",ScenePixelsEqual(noActorImage,Pixels()));actor.SetActive(false);

                var originalRenderer=surface.renderer;var skinHost=Own(new GameObject("Screen prepass skinned receiver"));skinHost.layer=25;skinHost.transform.localScale=receiver.transform.localScale;
                var bone=Own(new GameObject("Screen prepass receiver bone")).transform;bone.SetParent(skinHost.transform,false);
                var skin=skinHost.AddComponent<SkinnedMeshRenderer>();var skinMesh=Own(Instantiate(mesh));var weights=new BoneWeight[mesh.vertexCount];for(int i=0;i<weights.Length;i++)weights[i]=new BoneWeight{boneIndex0=0,weight0=1};
                skinMesh.boneWeights=weights;skinMesh.bindposes=new[]{Matrix4x4.identity};skin.sharedMesh=skinMesh;skin.bones=new[]{bone};skin.rootBone=bone;skin.sharedMaterial=originalRenderer.sharedMaterial;skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*5);
                var control=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));control.layer=25;control.transform.localScale=receiver.transform.localScale;var controlMesh=Own(Instantiate(mesh));control.GetComponent<MeshFilter>().sharedMesh=controlMesh;
                for(int pose=0;pose<3;pose++)
                {
                    bone.localPosition=new Vector3(.02f*pose,-.01f*pose,0);bone.localRotation=Quaternion.Euler(pose*4,pose*5,pose*3);surface.renderer=skin;yield return null;
                    var sf=Render();var sg=ReadSceneTarget(sf.screenGeometry);var sv=ReadSceneTarget(sf.shadowOcclusion);var si=Pixels();
                    var vertices=mesh.vertices;var normals=mesh.normals;var deform=Matrix4x4.TRS(bone.localPosition,bone.localRotation,bone.localScale);
                    for(int i=0;i<vertices.Length;i++){vertices[i]=deform.MultiplyPoint(vertices[i]);normals[i]=deform.MultiplyVector(normals[i]);}controlMesh.vertices=vertices;controlMesh.normals=normals;controlMesh.RecalculateBounds();
                    surface.renderer=control.GetComponent<Renderer>();var cf=Render();float ge=PixelError(sg,ReadSceneTarget(cf.screenGeometry)),ve=PixelError(sv,ReadSceneTarget(cf.shadowOcclusion)),ie=PixelError(si,Pixels());
                    Check("skin-static-prepass-depth-normal-"+pose,ge<.00003f,ge);Check("skin-static-rg8-visibility-"+pose,ve<=1f/255+.000001f,ve);Check("skin-static-complete-hdr-"+pose,ie<.003f,ie);
                }
                surface.renderer=originalRenderer;skinHost.SetActive(false);control.SetActive(false);

                var spot=new SceneDecalLight{shape=SceneDecalLightShape.Spot,position=new Vector3(0,0,-2),range=5,spotInnerAngle=90,spotOuterAngle=90,radiance=new Vector3(.3f,.6f,.9f)};spot.shadow.enabled=true;
                var point=new SceneDecalLight{shape=SceneDecalLightShape.Point,position=new Vector3(.4f,.2f,-2),range=5,radiance=new Vector3(.5f,.3f,.1f)};point.shadow.enabled=true;
                stage.decalLighting.enabled=true;stage.decalLighting.lights=new[]{spot,point};stage.decalLighting.shadows.casters=new[]{caster};
                void ScopeOracle(string name)
                {
                    var radiance=stage.lightRadiance;var ambient=stage.ambientIrradiance;float giScale=stage.giBaseScale;var emission=surface.inputs.emission;
                    bool lamps=stage.decalLighting.enabled;settings.enabled=false;main.enabled=false;surface.inputs.emission=Vector3.zero;stage.ambientIrradiance=Vector3.zero;stage.giBaseScale=0;stage.decalLighting.enabled=false;
                    Render();var direct=Pixels();stage.lightRadiance=Vector3.zero;stage.ambientIrradiance=ambient;stage.giBaseScale=giScale;Render();var indirect=Pixels();
                    stage.ambientIrradiance=Vector3.zero;stage.giBaseScale=0;stage.decalLighting.enabled=lamps;Render();var additive=Pixels();
                    stage.decalLighting.enabled=false;surface.inputs.emission=emission;Render();var emissive=Pixels();
                    stage.lightRadiance=radiance;stage.ambientIrradiance=ambient;stage.giBaseScale=giScale;stage.decalLighting.enabled=lamps;main.enabled=true;settings.enabled=true;
                    var f=Render();var actual=Pixels();var visibility=ReadSceneTarget(f.shadowOcclusion);var coverage=ReadSceneTarget(f.albedoCoverage);float worst=0;
                    for(int i=0;i<actual.Length;i++)if(coverage[i].a>.5f)worst=Mathf.Max(worst,Error(Rgb(direct[i])*visibility[i].r+Rgb(indirect[i])*visibility[i].g+Rgb(additive[i])+Rgb(emissive[i]),Rgb(actual[i])));
                    Check(name+"-independent-direct-ao-additive-emission",worst<.003f,worst);
                }
                ScopeOracle("ambient-and-mixed-lights");
                var gi=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));gi.SetPixel(0,0,new Color(2,1,.5f,1));gi.Apply();surface.gi=new SceneGiInput{source=SceneGiSource.Lightmap,lightmap=gi};stage.directionalGiWeight=.7f;stage.giBaseScale=.4f;
                ScopeOracle("baked-gi-base-and-direct-modulation");surface.gi.source=SceneGiSource.None;stage.directionalGiWeight=0;stage.giBaseScale=1;
                var mixed=Render();var mixedImage=Pixels();Check("mixed-source-depth-submitted-once",stage.LightShadowMapCount==7&&stage.LightShadowCasterDrawCalls==7&&stage.MainShadowCasterDrawCalls==1);
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SCREEN_SHADOW")=="1")
                {
                    bool started=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;try{if(started){Render();Pixels();}}finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("requested-native-screen-shadow-capture",started&&ended);
                }
                SaveSsrPreview("scene-screen-shadow-mixed",Pixels(),129,129,false);
                stage.decalLighting.backend=SceneDecalLightBackend.Scalar;Render();Check("screen-with-scalar-lights",PixelError(mixedImage,Pixels())<.003f,PixelError(mixedImage,Pixels()));stage.decalLighting.backend=SceneDecalLightBackend.Instanced;
                var first=Render();var firstMask=ReadSceneTarget(first.shadowOcclusion);var firstImage=Pixels();
                var other=Own(new GameObject("Second screen-shadow camera"));var camera2=other.AddComponent<Camera>();camera2.CopyFrom(camera);camera2.enabled=false;camera2.transform.position=camera.transform.position;
                var target2=Own(new RenderTexture(97,65,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target2.Create();camera2.targetTexture=target2;
                var stage2=other.AddComponent<SceneDeferredCamera>();stage2.sceneEnabled=true;stage2.sceneLayers=stage.sceneLayers;stage2.surfaces=stage.surfaces;stage2.screenShadow=new SceneScreenShadowSettings{enabled=true,capsules=new[]{secondCapsule}};
                camera2.Render();Check("two-camera-independent-screen-buffers",stage2.TryGetFrame(out var frame2)&&frame2.shadowOcclusion!=first.shadowOcclusion&&frame2.screenGeometry!=first.screenGeometry&&first.IsCurrent);
                Check("other-camera-does-not-overwrite-first",ScenePixelsEqual(firstMask,ReadSceneTarget(first.shadowOcclusion))&&ScenePixelsEqual(firstImage,Pixels()));other.SetActive(false);camera2.targetTexture=null;
                foreach(bool geometry in new[]{false,true})
                {var old=Render();var lost=geometry?old.screenGeometry:old.shadowOcclusion;lost.Release();Check("lost-target-invalidates-frame-"+geometry,!old.IsCurrent);var rebuilt=Render();Check("lost-target-recreated-"+geometry,rebuilt.screenGeometry.IsCreated()&&rebuilt.shadowOcclusion.IsCreated()&&(geometry?rebuilt.screenGeometry:rebuilt.shadowOcclusion)!=lost);}
                var beforeResize=Render();camera.targetTexture=target2;var resized=Render();Check("odd-host-resize-rebuilds-both-targets",resized.shadowOcclusion.width==97&&resized.shadowOcclusion.height==65&&!beforeResize.IsCurrent&&!beforeResize.shadowOcclusion.IsCreated());camera.targetTexture=target;
                void Reject(string name){camera.Render();Check(name,!stage.TryGetFrame(out _)&&stage.ScreenShadowTargetCount==0&&stage.ScreenShadowResolveDrawCalls==0&&!string.IsNullOrEmpty(stage.UnavailableReason));}
                settings.capsules=null;Reject("null-capsules-rejected");settings.capsules=new SceneCapsuleOccluder[17];Reject("capsule-budget-rejected");settings.capsules=new SceneCapsuleOccluder[1];Reject("missing-capsule-rejected");settings.capsules=new[]{capsule};
                capsule.radius=0;Reject("zero-radius-rejected");capsule.radius=.13f;capsule.start=new Vector3(float.NaN,0,0);Reject("nan-endpoint-rejected");capsule.start=oldStart;
                settings.capsuleSamples=12;Reject("unsupported-sample-count-rejected");settings.capsuleSamples=32;settings.capsuleStrength=float.NaN;Reject("nan-strength-rejected");settings.capsuleStrength=1;
                settings.capsuleMaxDistance=0;Reject("zero-max-distance-rejected");settings.capsuleMaxDistance=1;settings.capsuleNormalBias=-1;Reject("negative-normal-bias-rejected");settings.capsuleNormalBias=.002f;
                settings.capsules=Array.Empty<SceneCapsuleOccluder>();main.enabled=false;Render();Check("empty-effects-no-extra-targets",stage.ScreenShadowTargetCount==0&&stage.ScreenShadowGeometryDrawCalls==0);main.enabled=true;
                settings.capsules=new[]{capsule};settings.capsuleStrength=0;main.enabled=false;Render();Check("zero-ao-strength-without-main-no-target",stage.ScreenShadowTargetCount==0);settings.capsuleStrength=1;main.enabled=true;
                stage.decalLighting.enabled=false;settings.enabled=false;Render();Check("disabled-restores-original-direct-image",ScenePixelsEqual(baseline,Pixels())&&stage.ScreenShadowTargetCount==0);
                settings.capsules=null;Render();Check("disabled-invalid-config-ignored",stage.ScreenShadowTargetCount==0);settings.capsules=new[]{capsule};settings.enabled=true;
                var final=Render();stage.enabled=false;Check("component-disable-releases-screen-targets",!final.screenGeometry.IsCreated()&&!final.shadowOcclusion.IsCreated()&&stage.ScreenShadowTargetCount==0);
                camera.targetTexture=null;host.SetActive(false);receiver.SetActive(false);casterObject.SetActive(false);
            }
            finally
            {
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
        }
    }
}
