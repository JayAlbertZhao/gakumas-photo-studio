using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyWater(Report report)
        {
            yield return null;
            var previous=FindObjectsOfType<Renderer>();var forced=previous.Select(r=>r.forceRenderingOff).ToArray();
            foreach(var r in previous)r.forceRenderingOff=true;
            var savedActive=RenderTexture.active;var water=new SceneWaterRenderer();
            try
            {
                void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"water-"+name,ok,error);
                var host=Own(new GameObject("Water actual opaque camera"));var camera=host.AddComponent<Camera>();
                camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;
                camera.cullingMask=1<<24;camera.orthographic=true;camera.orthographicSize=1.6f;camera.nearClipPlane=.1f;camera.farClipPlane=30;
                camera.transform.position=new Vector3(0,0,-4);camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.13f,.21f,.33f,.61f);
                RenderTexture Target(int w,int h,int d=0,RenderTextureFormat format=RenderTextureFormat.ARGBFloat)
                {var t=Own(new RenderTexture(w,h,d,format,RenderTextureReadWrite.Linear));t.Create();return t;}
                var target=Target(143,103,24);camera.targetTexture=target;camera.aspect=(float)target.width/target.height;
                Material Flat(Color c){var m=Own(new Material(Resources.Load<Shader>("StudioAccent")));m.SetColor("_Color",c);return m;}
                Renderer Quad(string name,Vector3 p,Vector3 scale,int layer,Color c)
                {
                    var go=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));go.name=name;go.layer=layer;go.transform.position=p;go.transform.localScale=scale;
                    var mesh=Own(Instantiate(go.GetComponent<MeshFilter>().sharedMesh));mesh.uv2=mesh.uv;go.GetComponent<MeshFilter>().sharedMesh=mesh;
                    var r=go.GetComponent<Renderer>();r.sharedMaterial=Flat(c);return r;
                }
                var back=Quad("Water opaque bottom",new Vector3(0,0,1.2f),new Vector3(20,20,1),24,new Color(.41f,.63f,.29f,.73f));
                var foreground=Quad("Water foreground occluder",new Vector3(.7f,.1f,-.7f),new Vector3(.43f,2.21f,1),24,new Color(.91f,.03f,.17f,1));
                var receiver=Quad("Current water plane",Vector3.zero,new Vector3(3.73f,2.81f,1),25,Color.magenta);
                var originalMaterial=receiver.sharedMaterial;
                var depth=host.AddComponent<SceneDepthData>();depth.buildDepthHierarchy=false;
                depth.surfaces=new[]{new SceneDepthData.Surface{renderer=back},new SceneDepthData.Surface{renderer=foreground}};
                var settings=new SceneWaterSettings{enabled=true};var s=new SceneWaterSurface();settings.surfaces=new[]{s};s.surface.renderer=receiver;s.surface.cull=CullMode.Off;
                s.surface.inputs.albedo=Vector3.zero;s.surface.inputs.alpha=.63f;s.waveA=s.waveB=Vector4.zero;
                s.refractionPixelsPerUnit=0;settings.lighting.lightRadiance=settings.lighting.ambientIrradiance=Vector3.zero;
                var probe=Own(new Cubemap(4,TextureFormat.RGBAFloat,false));var probeColor=new Color(.83f,.17f,.51f,1);
                foreach(CubemapFace face in new[]{CubemapFace.PositiveX,CubemapFace.NegativeX,CubemapFace.PositiveY,CubemapFace.NegativeY,CubemapFace.PositiveZ,CubemapFace.NegativeZ})probe.SetPixels(Enumerable.Repeat(probeColor,16).ToArray(),face);
                probe.Apply();s.reflectionProbe=probe;
                SceneDepthData.Frame depthFrame=default;Color[] source=null;
                void Opaque()
                {
                    camera.Render();if(!depth.TryGetFrame(camera,target.width,target.height,out depthFrame))throw new InvalidOperationException(depth.UnavailableReason);
                    source=ReadSceneTarget(target);
                }
                SceneWaterRenderer.Frame Render(string name)
                {
                    if(!water.TryRender(target,new FogVolumeDepth(depthFrame.linearDepth),camera,settings,out var frame))throw new InvalidOperationException(name+": "+water.UnavailableReason);
                    Check(name+"-current-output",frame.IsCurrent && water.SubmittedSurfaces==settings.surfaces.Length);
                    Check(name+"-source-and-material-unchanged",PixelError(source,ReadSceneTarget(target))==0 && receiver.sharedMaterial==originalMaterial && camera.targetTexture==target);
                    return frame;
                }
                Color[] Reference(Color[] background,SceneWaterSurface surface,Vector3 normal)
                {
                    var expected=(Color[])background.Clone();var t=surface.surface.renderer.transform;
                    var plane=new Plane(t.TransformDirection(Vector3.back),t.position);
                    var depths=ReadSceneTarget(depthFrame.linearDepth);
                    // These oracle rectangles are axis-aligned. D3D11 snaps edges
                    // to an eight-bit subpixel grid before applying top-left coverage.
                    // Keep the deliberately near-half-pixel63x41 boundary, not a looser gate.
                    var lo=new Vector2(float.PositiveInfinity,float.PositiveInfinity);var hi=-lo;
                    foreach(float u in new[]{-.5f,.5f})foreach(float v0 in new[]{-.5f,.5f})
                    {
                        var p0=camera.WorldToViewportPoint(t.TransformPoint(new Vector3(u,v0,0)));var p1=new Vector2(p0.x*target.width,p0.y*target.height);
                        if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Direct3D11)p1=new Vector2(Mathf.Round(p1.x*256)/256,Mathf.Round(p1.y*256)/256);
                        lo=Vector2.Min(lo,p1);hi=Vector2.Max(hi,p1);
                    }
                    for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)
                    {
                        int i=y*target.width+x;var ray=camera.ViewportPointToRay(new Vector3((x+.5f)/target.width,(y+.5f)/target.height));
                        if(!plane.Raycast(ray,out var distance))continue;var p=ray.GetPoint(distance);var local=t.InverseTransformPoint(p);
                        if(x+.5f<lo.x || x+.5f>=hi.x || y+.5f<lo.y || y+.5f>=hi.y)continue;
                        float eye=-camera.worldToCameraMatrix.MultiplyPoint(p).z;if(depths[i].r<eye-settings.depthBias)continue;
                        var v=camera.orthographic?-camera.transform.forward:(camera.transform.position-p).normalized;var n=normal;if(Vector3.Dot(n,v)<0)n=-n;
                        double thickness=Math.Min(surface.maximumThickness,Math.Max(0,depths[i].r-eye)/Math.Abs(Vector3.Dot(v,camera.transform.forward)));
                        double f0=Math.Pow((surface.indexOfRefraction-1)/(surface.indexOfRefraction+1),2);
                        double fresnel=(f0+(1-f0)*Math.Pow(1-Math.Max(0,Vector3.Dot(n,v)),5))*surface.reflectionStrength;
                        double alpha=surface.surface.inputs.alpha;if(surface.shoreFadeDistance>0)alpha*=Math.Min(1,thickness/surface.shoreFadeDistance);
                        var input=surface.surface.inputs;
                        var direct=Vector3.Scale(ForwardCpuBrdf(input.albedo,input.mos,n,v,settings.lighting.lightDirection.normalized,settings.lighting.diffuseScale,settings.lighting.specularScale,settings.lighting.backlightScale),settings.lighting.lightRadiance);
                        foreach(var light in settings.lighting.localLights.lights ?? Array.Empty<SceneDecalLight>())
                            if(settings.lighting.localLights.enabled && (light.receiverGroup==0 || light.receiverGroup==surface.surface.receiverGroup))direct+=ForwardCpuLocal(light,input.albedo,input.mos,p,n,v);
                        direct+=Vector3.Scale(input.albedo,settings.lighting.ambientIrradiance)*(input.mos.y/Mathf.PI)+input.emission;
                        for(int c=0;c<3;c++)
                        {
                            double attenuation=Math.Exp(-surface.absorption[c]*thickness);
                            double radiance=(background[i][c]*attenuation+surface.scatteringRadiance[c]*(1-attenuation))*(1-fresnel)+probeColor[c]*fresnel+direct[c];
                            expected[i][c]=(float)(radiance*alpha+background[i][c]*(1-alpha));
                        }
                        expected[i].a=(float)(alpha+background[i].a*(1-alpha));
                    }
                    return expected;
                }
                foreach(bool perspective in new[]{false,true})foreach(float ior in new[]{1,1.333f,2.1f})foreach(float absorption in new[]{0,.2f,3f})
                {
                    camera.orthographic=!perspective;camera.fieldOfView=51;s.indexOfRefraction=ior;s.absorption=new Vector3(absorption,absorption*.4f,absorption*.1f);Opaque();
                    var actual=ReadSceneTarget(Render($"oracle-{perspective}-{ior}-{absorption}").color);var expected=Reference(source,s,Vector3.back);float error=PixelError(actual,expected);
                    Check($"independent-Schlick-Beer-camera-ray-{perspective}-{ior}-{absorption}",error<=.0003f,error);
                }
                camera.orthographic=true;s.indexOfRefraction=1.333f;s.absorption=new Vector3(.3f,.08f,.04f);Opaque();
                var initial=Render("initial");var baseline=ReadSceneTarget(initial.color);
                Check("positive-water-change",PixelError(baseline,source)>.02f);
                int blocked=0;float foregroundError=0;var zpixels=ReadSceneTarget(depthFrame.linearDepth);
                for(int i=0;i<source.Length;i++)if(zpixels[i].r<3.5f){blocked++;for(int c=0;c<4;c++)foregroundError=Mathf.Max(foregroundError,Mathf.Abs(baseline[i][c]-source[i][c]));}
                Check("actual-opaque-foreground-exact",blocked>100 && foregroundError==0,foregroundError);
                SaveSsrPreview("water-flat-camera-source",source,target.width,target.height,false);
                SaveSsrPreview("water-flat-independent-result",baseline,target.width,target.height,false);
                settings.lighting.lightRadiance=new Vector3(.7f,.5f,.3f);settings.lighting.ambientIrradiance=new Vector3(.07f,.03f,.11f);
                s.surface.inputs.albedo=new Vector3(.13f,.19f,.17f);s.surface.inputs.emission=new Vector3(.01f,.02f,.03f);
                settings.lighting.localLights.enabled=true;settings.lighting.localLights.lights=Enumerable.Range(0,19).Select(i=>new SceneDecalLight{
                    shape=(SceneDecalLightShape)(i%4),position=new Vector3((i%5-2)*.61f,(i/5-1)*.51f,-1.2f),range=2.7f,
                    radiance=new Vector3(.19f,.11f,.13f),halfLength=.37f,halfSize=new Vector2(.31f,.43f),areaSpread=new Vector2(.33f,.21f),spotInnerAngle=59,spotOuterAngle=113}).ToArray();
                Color[] Pair(string name)
                {
                    settings.lighting.backend=SceneForwardLightBackend.BruteForce;var a=ReadSceneTarget(Render(name+"-brute").color);
                    settings.lighting.backend=SceneForwardLightBackend.Tiled;var b=ReadSceneTarget(Render(name+"-tiled").color);float error=PixelError(a,b);
                    Check(name+"-current-tiled-brute-equivalent",error<=.00003f && water.TileCount>0,error);return b;
                }
                var lit=Pair("lit-water");float litError=PixelError(lit,Reference(source,s,Vector3.back));Check("independent-current-PBR-local-shapes",litError<=.0003f,litError);
                Check("actual-lighting-positive",PixelError(lit,baseline)>.01f);
                Texture2D Constant(Color c){var t=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));t.SetPixel(0,0,c);t.Apply();return t;}
                var mapNormal=new Vector3(.24f,.57f,.79f).normalized;s.surface.inputs.normalMap=Constant(new Color(mapNormal.x*.5f+.5f,mapNormal.y*.5f+.5f,mapNormal.z*.5f+.5f));
                foreach(float sign in new[]{1f,-1f})
                {
                    receiver.transform.localScale=new Vector3(sign*3.73f,2.81f,1);Opaque();var actual=Pair("normal-parity-"+sign);
                    var n=new Vector3(sign*mapNormal.x,mapNormal.y,-mapNormal.z).normalized;float error=PixelError(actual,Reference(source,s,n));
                    Check("explicit-tangent-handedness-"+sign,error<=.0003f,error);
                }
                receiver.transform.localScale=new Vector3(3.73f,2.81f,1);s.surface.inputs.normalMap=null;
                s.waveA=new Vector4(13,3,.012f,1.1f);s.waveB=new Vector4(-4,17,.008f,-.8f);s.refractionPixelsPerUnit=28;Opaque();
                settings.seconds=0;var time0=Pair("waves-time-0");settings.seconds=.71;var time1=Pair("waves-time-1");settings.seconds=0;var replay=Pair("waves-time-replay");
                Check("explicit-time-normal-and-refraction-changes",PixelError(time0,time1)>.005f);Check("time-replay-whole-image-exact",PixelError(time0,replay)==0);
                SaveSsrPreview("water-lit-waves-time-0",time0,target.width,target.height,false);SaveSsrPreview("water-lit-waves-time-1",time1,target.width,target.height,false);
                s.waveA=s.waveB=Vector4.zero;s.refractionPixelsPerUnit=0;
                var noGi=Pair("without-GI");s.surface.gi.source=SceneGiSource.Lightmap;s.surface.gi.lightmap=Constant(new Color(.7f,.2f,.4f,1));
                var withGi=Pair("current-GI");Check("GI-positive",PixelError(noGi,withGi)>.01f);
                s.surface.gi.lightmap=Constant(new Color(.1f,.8f,.2f,1));Check("GI-current-input-change",PixelError(withGi,Pair("changed-GI"))>.01f);s.surface.gi.source=SceneGiSource.None;
                var casterRenderer=Quad("Water shadow caster",new Vector3(-.4f,.3f,-1),new Vector3(.61f,1.13f,1),26,Color.white);
                var caster=new SceneShadowCaster{renderer=casterRenderer,cull=CullMode.Off};var many=settings.lighting.localLights.lights;
                var local=new SceneDecalLight{shape=SceneDecalLightShape.Point,position=new Vector3(0,0,-2),range=6,radiance=Vector3.one*2};
                settings.lighting.localLights.lights=new[]{local};settings.lighting.localLights.shadows.casters=new[]{caster};settings.lighting.localLights.shadows.tileResolution=128;
                var main=settings.lighting.mainLightShadow;main.origin=new Vector3(0,0,-3);main.halfSize=Vector2.one*3;main.resolution=128;main.farPlane=12;main.casters=new[]{caster};
                var clearShadows=Pair("no-shadow");local.shadow.enabled=true;var pointShadow=Pair("point-shadow");
                Check("point-shadow-current-six-faces",water.ShadowMapCount==6 && PixelError(clearShadows,pointShadow)>.01f);
                main.enabled=true;var both=Pair("main-point-shadow");Check("separate-main-shadow-current",water.ShadowMapCount==7 && PixelError(pointShadow,both)>.005f);
                casterRenderer.transform.position+=Vector3.right*.6f;Check("moving-caster-current-water-lighting",PixelError(both,Pair("moving-caster"))>.01f);
                main.enabled=false;local.shadow.enabled=false;settings.lighting.localLights.lights=many;casterRenderer.gameObject.SetActive(false);

                // The reflected emitter is excluded from native opaque color/depth.
                var emitter=Quad("Water reflected HDR panel",new Vector3(-.5f,.2f,-1),new Vector3(1.2f,1.1f,1),26,Color.white);
                var emitterMaterial=Own(new Material(Resources.Load<Shader>("PlanarCapture")));emitterMaterial.SetColor("_Color",Color.black);
                emitterMaterial.SetColor("_Emission",new Color(5,.2f,1,1));emitterMaterial.SetFloat("_Cull",0);emitter.sharedMaterial=emitterMaterial;
                var planar=host.AddComponent<PlanarReflection>();planar.planePoint=Vector3.zero;planar.planeNormal=Vector3.back;planar.reflectedLayers=1<<26;
                planar.reflectedSurfaces=new[]{new PlanarReflection.Draw{surface=new SceneDepthData.Surface{renderer=emitter,cull=CullMode.Off},material=emitterMaterial}};
                var planarReceiver=new PlanarReflection.Receiver{surface=new SceneDepthData.Surface{renderer=receiver,cull=CullMode.Off},allowExcludedLayer=true};
                planar.receivers=new[]{planarReceiver};planar.reflectionsEnabled=true;planar.resolutionScale=1;
                Opaque();var cubeOnly=Pair("cube-fallback");s.planarReflection=planar;var reflected=Pair("current-planar");
                Check("same-camera-current-planar-positive",water.PlanarSurfaces==1 && PixelError(cubeOnly,reflected)>.01f);
                if(!planar.TryGetReflection(camera,target.width,target.height,out var reflection))throw new InvalidOperationException(planar.UnavailableReason);
                var reflectionPixels=ReadSceneTarget(reflection);int coverage=reflectionPixels.Count(c=>c.a>.1f);Check("planar-excluded-water-layer-real-coverage",coverage>100,coverage);
                var expectedPlanar=(Color[])cubeOnly.Clone();float reflectF0=Mathf.Pow((s.indexOfRefraction-1)/(s.indexOfRefraction+1),2);
                for(int i=0;i<expectedPlanar.Length;i++)for(int c=0;c<3;c++)expectedPlanar[i][c]+=(reflectionPixels[i][c]-probeColor[c])*reflectionPixels[i].a*reflectF0*s.surface.inputs.alpha;
                float planarError=PixelError(reflected,expectedPlanar);Check("planar-coverage-cube-independent-composite",planarError<=.0003f,planarError);
                SaveSsrPreview("water-actual-planar-lit-composite",reflected,target.width,target.height,false);
                planarReceiver.allowExcludedLayer=false;Opaque();Check("planar-old-default-excludes-layer",PixelError(cubeOnly,Pair("planar-default-excluded"))==0);
                planarReceiver.allowExcludedLayer=true;emitter.transform.position+=Vector3.right*.6f;Opaque();var movedReflection=Pair("planar-moving-emitter");
                Check("planar-current-emitter-change",PixelError(reflected,movedReflection)>.01f);
                planar.enabled=false;Check("missing-planar-cube-recovery",PixelError(cubeOnly,Pair("planar-disabled"))==0 && water.PlanarSurfaces==0);
                s.planarReflection=null;emitter.gameObject.SetActive(false);

                // Two distinct targets are necessary: the front surface samples the completed back layer.
                var frontWater=Quad("Second ordered water plane",new Vector3(-.2f,-.1f,-.3f),new Vector3(2.3f,1.7f,1),25,Color.magenta);
                var second=new SceneWaterSurface{waveA=Vector4.zero,waveB=Vector4.zero,reflectionProbe=probe,refractionPixelsPerUnit=0,absorption=new Vector3(.1f,.4f,.2f)};
                second.surface.renderer=frontWater;second.surface.cull=CullMode.Off;second.surface.inputs.albedo=new Vector3(.1f,.05f,.04f);second.surface.inputs.alpha=.41f;
                settings.surfaces=new[]{s,second};Opaque();var two=Pair("two-ordered-surfaces");
                var expectedTwo=Reference(Reference(source,s,Vector3.back),second,Vector3.back);float twoError=PixelError(two,expectedTwo);
                Check("two-surfaces-independent-composite",twoError<=.0003f,twoError);
                settings.surfaces=new[]{second,s};Check("wrong-order-negative-control",PixelError(two,Pair("wrong-layer-order"))>.002f);settings.surfaces=new[]{s};frontWater.gameObject.SetActive(false);

                // Independent scalar bilinear/foreground exclusion for a constant tilted normal.
                settings.lighting.lightRadiance=settings.lighting.ambientIrradiance=Vector3.zero;settings.lighting.localLights.enabled=false;
                s.surface.inputs.albedo=s.surface.inputs.emission=Vector3.zero;s.absorption=Vector3.zero;s.indexOfRefraction=1;s.reflectionStrength=0;s.refractionPixelsPerUnit=85;
                var bottomPattern=Quad("Water refracted blue bottom patch",new Vector3(-.5f,.4f,1),new Vector3(1.13f,1.21f,1),24,new Color(.03f,.17f,.83f,1));
                depth.surfaces=new[]{new SceneDepthData.Surface{renderer=back},new SceneDepthData.Surface{renderer=foreground},new SceneDepthData.Surface{renderer=bottomPattern}};
                s.surface.inputs.normalMap=Constant(new Color(mapNormal.x*.5f+.5f,mapNormal.y*.5f+.5f,mapNormal.z*.5f+.5f));Opaque();
                var transport=ReadSceneTarget(Render("guarded-transmission").color);var expectedTransport=(Color[])source.Clone();var flippedTransport=(Color[])source.Clone();int excludedTaps=0,movedPixels=0;
                var depthsForTransport=ReadSceneTarget(depthFrame.linearDepth);
                for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)
                {
                    int i=y*target.width+x;var ray=camera.ViewportPointToRay(new Vector3((x+.5f)/target.width,(y+.5f)/target.height));
                    if(!new Plane(Vector3.back,Vector3.zero).Raycast(ray,out var distance))continue;var p=receiver.transform.InverseTransformPoint(ray.GetPoint(distance));
                    if(Mathf.Abs(p.x)>.5f || Mathf.Abs(p.y)>.5f || depthsForTransport[i].r<4-settings.depthBias)continue;
                    float thickness=Mathf.Min(s.maximumThickness,Mathf.Max(0,depthsForTransport[i].r-4));
                    float sx=x+mapNormal.x*s.refractionPixelsPerUnit*thickness,sy=y+mapNormal.y*s.refractionPixelsPerUnit*thickness;
                    int bx=Mathf.FloorToInt(sx),by=Mathf.FloorToInt(sy);float fx=sx-bx,fy=sy-by,weight=0;var sum=Color.clear;
                    for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                    {
                        int tx=bx+dx,ty=by+dy;if(tx<0||ty<0||tx>=target.width||ty>=target.height){excludedTaps++;continue;}
                        if(depthsForTransport[ty*target.width+tx].r<4-settings.depthBias){excludedTaps++;continue;}
                        float w=(dx==0?1-fx:fx)*(dy==0?1-fy:fy);sum+=source[ty*target.width+tx]*w;weight+=w;
                    }
                    var value=weight>1e-6f?sum/weight:source[i];float alpha=s.surface.inputs.alpha;
                    expectedTransport[i]=value*alpha+source[i]*(1-alpha);expectedTransport[i].a=alpha+source[i].a*(1-alpha);
                    if(Mathf.Abs(value.r-source[i].r)>.01f)movedPixels++;
                    // Deliberately wrong view-axis reference diagnoses orientation without changing the gate.
                    sy=y-mapNormal.y*s.refractionPixelsPerUnit*thickness;by=Mathf.FloorToInt(sy);fy=sy-by;sum=Color.clear;weight=0;
                    for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                    {
                        int tx=bx+dx,ty=by+dy;if(tx<0||ty<0||tx>=target.width||ty>=target.height||depthsForTransport[ty*target.width+tx].r<4-settings.depthBias)continue;
                        float w=(dx==0?1-fx:fx)*(dy==0?1-fy:fy);sum+=source[ty*target.width+tx]*w;weight+=w;
                    }
                    value=weight>1e-6f?sum/weight:source[i];flippedTransport[i]=value*alpha+source[i]*(1-alpha);flippedTransport[i].a=expectedTransport[i].a;
                }
                float transportError=PixelError(expectedTransport,transport);Check("independent-four-tap-transmission",transportError<=.0003f && excludedTaps>100,transportError);
                Debug.Log("[WaterTransmission] view-axis-error="+transportError.ToString("R")+" opposite-axis-error="+PixelError(flippedTransport,transport).ToString("R"));
                Check("opposite-transmission-axis-negative-control",PixelError(flippedTransport,transport)>.01f);
                SaveSsrPreview("water-transmission-actual",transport,target.width,target.height,false);SaveSsrPreview("water-transmission-independent",expectedTransport,target.width,target.height,false);
                Check("guarded-transmission-positive-current-color",movedPixels>10,movedPixels);
                s.surface.inputs.normalMap=null;s.indexOfRefraction=1.333f;s.reflectionStrength=1;s.refractionPixelsPerUnit=0;
                var skinHost=Own(new GameObject("Water current native skin"));skinHost.layer=25;var skin=skinHost.AddComponent<SkinnedMeshRenderer>();skin.sharedMaterial=originalMaterial;
                var referenceMesh=receiver.GetComponent<MeshFilter>().sharedMesh;var rest=referenceMesh.vertices;var delta=new Vector3[rest.Length];delta[0]=new Vector3(.21f,.13f,0);
                var skinMesh=Own(Instantiate(referenceMesh));skinMesh.boneWeights=Enumerable.Repeat(new BoneWeight{boneIndex0=0,weight0=1},rest.Length).ToArray();
                skinMesh.bindposes=new[]{Matrix4x4.identity};skinMesh.AddBlendShapeFrame("Water current blend",100,delta,new Vector3[rest.Length],new Vector3[rest.Length]);
                var bone=Own(new GameObject("Water current bone")).transform;skin.sharedMesh=skinMesh;skin.bones=new[]{bone};skin.rootBone=bone;skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*20);
                var oldScale=receiver.transform.localScale;Color[] firstPose=null;
                for(int pose=0;pose<3;pose++)
                {
                    bone.SetPositionAndRotation(new Vector3(-.2f+pose*.23f,.1f-pose*.13f,0),Quaternion.Euler(pose*8,pose*11,pose*17));skin.SetBlendShapeWeight(0,pose*40);
                    receiver.transform.SetPositionAndRotation(bone.position,bone.rotation);receiver.transform.localScale=Vector3.one;
                    referenceMesh.vertices=rest.Select((p,i)=>p+delta[i]*(pose*.4f)).ToArray();referenceMesh.RecalculateBounds();
                    yield return null;yield return null;Opaque();
                    s.surface.renderer=skin;var actual=ReadSceneTarget(Render("current-skin-"+pose).color);
                    s.surface.renderer=receiver;var expected=ReadSceneTarget(Render("independent-posed-mesh-"+pose).color);
                    float error=PixelError(actual,expected);Check("native-skin-shape-independent-"+pose,error<=.00003f,error);
                    if(firstPose==null)firstPose=actual;else Check("native-skin-current-change-"+pose,PixelError(firstPose,actual)>.01f);
                }
                referenceMesh.vertices=rest;referenceMesh.RecalculateBounds();receiver.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);receiver.transform.localScale=oldScale;skinHost.SetActive(false);Opaque();
                var originalTarget=target;
                foreach(var format in new[]{RenderTextureFormat.ARGBFloat,RenderTextureFormat.ARGBHalf,RenderTextureFormat.RGB111110Float})
                {
                    target=Target(63,41,24,format);camera.targetTexture=target;camera.aspect=(float)target.width/target.height;Opaque();
                    var frame=Render("odd-format-"+format);var pixels=ReadSceneTarget(frame.color);var expected=Reference(source,s,Vector3.back);float error=PixelError(pixels,expected);
                    if(error>.0003f)
                    {
                        int worst=0;float peak=0;for(int i=0;i<pixels.Length;i++)for(int c=0;c<4;c++)if(Mathf.Abs(pixels[i][c]-expected[i][c])>peak){peak=Mathf.Abs(pixels[i][c]-expected[i][c]);worst=i;}
                        Debug.Log("[WaterFormat] "+format+" worst="+(worst%target.width)+","+(worst/target.width)+" actual="+pixels[worst]+" expected="+expected[worst]+" source="+source[worst]+" bounds="+receiver.bounds);
                        SaveSsrPreview("water-format-actual-"+format,pixels,target.width,target.height,false);SaveSsrPreview("water-format-expected-"+format,expected,target.width,target.height,false);
                    }
                    Check("odd-format-independent-"+format,error<=.0003f,error);Check("odd-format-exact-owned-budget-"+format,water.ColorTargetBytes==63*41*32 && frame.color.format==RenderTextureFormat.ARGBFloat);
                }
                target=originalTarget;camera.targetTexture=target;camera.aspect=(float)target.width/target.height;Opaque();
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_WATER")=="1")
                {
                    settings.lighting.lightRadiance=Vector3.one;settings.lighting.localLights.enabled=true;settings.lighting.localLights.lights=many;
                    s.waveA=new Vector4(13,3,.012f,1.1f);s.refractionPixelsPerUnit=28;s.absorption=new Vector3(.3f,.08f,.04f);Opaque();
                    FsrCaptureDrain(target);bool started=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try{var frame=Render("native-water");FsrCaptureDrain(frame.color);}finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("requested-native-current-water-capture",started&&ended);
                }
                var last=Render("before-disable");settings.enabled=false;
                Check("disabled-releases-and-invalidates",!water.TryRender(target,new FogVolumeDepth(depthFrame.linearDepth),camera,settings,out _) && !last.IsCurrent && water.TargetCount==0 && water.LightingBufferBytes==0);
                settings.enabled=true;Render("recovery");
                void Reject(string name,Action change,Action restore)
                {
                    change();bool ok=water.TryRender(target,new FogVolumeDepth(depthFrame.linearDepth),camera,settings,out var failed);
                    Check(name+"-rejected-without-stale-frame",!ok && !failed.IsCurrent && !water.TryGetFrame(out _) && water.TargetCount==0 && water.LightingBufferBytes==0);
                    restore();Render(name+"-recovery");
                }
                Reject("nonfinite-clock",()=>settings.seconds=double.NaN,()=>settings.seconds=0);
                Reject("negative-extinction",()=>s.absorption.x=-1,()=>s.absorption.x=.3f);
                Reject("unsupported-metallic",()=>s.surface.inputs.mos.x=1,()=>s.surface.inputs.mos.x=0);
                Reject("duplicate-surface",()=>settings.surfaces=new[]{s,s},()=>settings.surfaces=new[]{s});
                Reject("native-double-draw",()=>camera.cullingMask|=1<<25,()=>camera.cullingMask=1<<24);
                Reject("missing-tangent",()=>{var mesh=receiver.GetComponent<MeshFilter>().sharedMesh;mesh.tangents=null;},()=>{var mesh=receiver.GetComponent<MeshFilter>().sharedMesh;mesh.RecalculateTangents();});
                var atlasAlias=Render("before-atlas-alias").color;
                Reject("owned-light-atlas",()=>settings.lighting.localLights.atlas=atlasAlias,()=>settings.lighting.localLights.atlas=null);
                var giAlias=Render("before-GI-alias").color;
                Reject("owned-GI-lightmap",()=>{s.surface.gi.source=SceneGiSource.Lightmap;s.surface.gi.lightmap=giAlias;},()=>{s.surface.gi.source=SceneGiSource.None;s.surface.gi.lightmap=null;});
                var owned=Render("alias-source");Check("owned-source-rejected",!water.TryRender(owned.color,new FogVolumeDepth(depthFrame.linearDepth),camera,settings,out _) && !owned.IsCurrent && water.TargetCount==0);
                Render("alias-recovery");water.Dispose();Check("dispose-invalidates",water.TargetCount==0 && water.LightingBufferBytes==0 && !water.TryGetFrame(out _));
                Check("historical-frame-not-revived",!initial.IsCurrent);Render("dispose-reusable");
                // A perspective horizontal pool uses the same public API, current scene
                // depth, reflected HDR geometry and lights; no reference game assets.
                target=Target(513,289,24);camera.targetTexture=target;camera.aspect=(float)target.width/target.height;
                camera.orthographic=false;camera.fieldOfView=52;camera.transform.position=new Vector3(3,2.3f,-4.3f);camera.transform.LookAt(new Vector3(0,0,1));
                back.transform.SetPositionAndRotation(new Vector3(0,-.65f,1),Quaternion.Euler(90,0,0));back.transform.localScale=new Vector3(7,7,1);
                foreground.transform.SetPositionAndRotation(new Vector3(0,1.1f,3.5f),Quaternion.identity);foreground.transform.localScale=new Vector3(5.3f,2.2f,1);
                foreground.sharedMaterial=Flat(new Color(.17f,.21f,.28f,1));back.sharedMaterial=Flat(new Color(.23f,.29f,.34f,1));
                receiver.transform.SetPositionAndRotation(new Vector3(0,0,1),Quaternion.Euler(90,0,0));receiver.transform.localScale=new Vector3(6,6,1);
                bottomPattern.transform.SetPositionAndRotation(new Vector3(-.7f,-.63f,.8f),Quaternion.Euler(90,0,0));bottomPattern.transform.localScale=new Vector3(.6f,4.7f,1);
                emitter.gameObject.SetActive(true);emitter.gameObject.layer=24;emitter.transform.position=new Vector3(-1,1.1f,2.7f);emitter.transform.localScale=new Vector3(.65f,2.2f,1);
                depth.surfaces=new[]{back,foreground,bottomPattern,emitter}.Select(r=>new SceneDepthData.Surface{renderer=r,cull=CullMode.Off}).ToArray();
                planar.enabled=true;planar.planePoint=new Vector3(0,0,1);planar.planeNormal=Vector3.up;planar.reflectedLayers=1<<24;planarReceiver.allowExcludedLayer=true;
                s.planarReflection=planar;s.surface.inputs.albedo=new Vector3(.015f,.025f,.03f);s.surface.inputs.mos=new Vector3(0,1,.87f);s.surface.inputs.alpha=1;
                s.absorption=new Vector3(.8f,.2f,.08f);s.scatteringRadiance=new Vector3(.02f,.13f,.17f);s.waveA=new Vector4(27,9,.006f,1.3f);s.waveB=new Vector4(-7,23,.007f,-.8f);
                s.refractionPixelsPerUnit=11;s.shoreFadeDistance=.1f;settings.lighting.lightRadiance=Vector3.one*2;settings.lighting.lightDirection=new Vector3(-.3f,1,-.5f);
                settings.lighting.ambientIrradiance=Vector3.one*.1f;settings.lighting.localLights.enabled=true;settings.lighting.localLights.lights=new[]{new SceneDecalLight{shape=SceneDecalLightShape.Point,position=new Vector3(-1,1.1f,1.7f),range=4,radiance=new Vector3(3,.12f,.5f)}};
                Opaque();var pool0=ReadSceneTarget(Render("pool-time-0").color);settings.seconds=1.7;var pool1=ReadSceneTarget(Render("pool-time-1").color);
                Check("perspective-pool-current-wave-change",PixelError(pool0,pool1)>.005f);Check("perspective-pool-actual-depth-and-reflection",depth.SubmittedSurfaces==4 && water.PlanarSurfaces==1 && PixelError(pool0,source)>.05f);
                SaveSsrPreview("water-pool-opaque",source,target.width,target.height,false);SaveSsrPreview("water-pool-time-0",pool0,target.width,target.height,false);SaveSsrPreview("water-pool-time-1",pool1,target.width,target.height,false);
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_WATER")=="1")
                {
                    FsrCaptureDrain(target);bool started=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try{Opaque();var frame=Render("native-pool");FsrCaptureDrain(frame.color);}finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("requested-native-pool-camera-planar-water-capture",started&&ended);
                }
            }
            finally
            {
                water.Dispose();RenderTexture.active=savedActive!=null&&savedActive.IsCreated()?savedActive:null;
                foreach(var item in _owned)if(item!=null)Destroy(item);_owned.Clear();
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
            }
        }
    }
}
