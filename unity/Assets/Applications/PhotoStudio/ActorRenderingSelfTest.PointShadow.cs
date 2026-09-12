using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyScenePointShadows(Report report)
        {
            yield return null;
            var previous = FindObjectsOfType<Renderer>(); var forced = new bool[previous.Length];
            for (int i = 0; i < previous.Length; i++) { forced[i] = previous[i].forceRenderingOff; previous[i].forceRenderingOff = true; }
            try
            {
                void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "scene-point-shadow-" + name, ok, error);
                var host = Own(new GameObject("Point shadow host")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
                camera.orthographic = true; camera.orthographicSize = 2; camera.aspect = 1; camera.nearClipPlane = .01f; camera.farClipPlane = 30;
                camera.cullingMask = 1 << 26; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                var target = Own(new RenderTexture(129,129,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var stage = host.AddComponent<SceneDeferredCamera>(); stage.sceneEnabled = true; stage.sceneLayers = 1 << 25;
                stage.lightRadiance = stage.ambientIrradiance = Vector3.zero; stage.giBaseScale = 0;
                var receiver = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); receiver.layer = 25; receiver.transform.localScale = Vector3.one * 3.8f;
                var mesh = Own(Instantiate(receiver.GetComponent<MeshFilter>().sharedMesh)); mesh.uv2 = mesh.uv; receiver.GetComponent<MeshFilter>().sharedMesh = mesh;
                var surface = new SceneDeferredCamera.Surface { renderer = receiver.GetComponent<Renderer>(), cull = CullMode.Off };
                surface.inputs.albedo = new Vector3(.5f,.4f,.3f); surface.inputs.mos = new Vector3(.1f,.7f,.4f); surface.inputs.emission = new Vector3(.12f,.08f,.04f); stage.surfaces = new[]{surface};
                var occluder = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); occluder.layer = 24; occluder.transform.localScale = new Vector3(1.2f,1.1f,1);
                var caster = new SceneShadowCaster { renderer = occluder.GetComponent<Renderer>(), cull = CullMode.Off };
                var borrowedMaterial = caster.renderer.sharedMaterial; var borrowedMesh = occluder.GetComponent<MeshFilter>().sharedMesh;
                var light = new SceneDecalLight { shape = SceneDecalLightShape.Point, position = new Vector3(.3f,-.4f,.2f), range = 6, radiance = new Vector3(3,2,1) };
                var options = stage.decalLighting; options.enabled = true; options.allowInstancingFallback = false; options.backend = SceneDecalLightBackend.Instanced;
                options.lights = new[]{light}; options.shadows.casters = new[]{caster}; options.shadows.maxShadowedLights = 1;
                var forward = new[]{Vector3.right,Vector3.left,Vector3.up,Vector3.down,Vector3.forward,Vector3.back};
                var up = new[]{Vector3.up,Vector3.up,Vector3.forward,Vector3.forward,Vector3.up,Vector3.up};
                var right = new Vector3[6]; for(int i=0;i<6;i++) right[i] = Vector3.Cross(up[i],forward[i]);
                Vector3 direction = Vector3.forward; Transform deformBone = null;
                void Arrange(Vector3 d)
                {
                    direction = d.normalized; var rotation = Quaternion.LookRotation(direction,Mathf.Abs(direction.y)>.95f ? Vector3.forward : Vector3.up);
                    camera.transform.SetPositionAndRotation(light.position,rotation);
                    receiver.transform.SetPositionAndRotation(light.position+direction*3,rotation);
                    occluder.transform.SetPositionAndRotation(light.position+direction*1.5f+rotation*new Vector3(.1f,.15f,0),rotation);
                }
                SceneDeferredCamera.Frame Render() { camera.Render(); if(!stage.TryGetFrame(out var f)) throw new InvalidOperationException(stage.UnavailableReason); return f; }
                Color[] Pixels() => ReadSceneTarget(target);
                Vector3 Rgb(Color c) => new Vector3(c.r,c.g,c.b);
                float Error(Vector3 a,Vector3 b) => Mathf.Max(Mathf.Abs(a.x-b.x),Mathf.Abs(a.y-b.y),Mathf.Abs(a.z-b.z));
                Matrix4x4 Inverse() => ((deformBone != null ? deformBone.localToWorldMatrix : caster.renderer.localToWorldMatrix)*Matrix4x4.Scale(caster.vertexScale)).inverse;
                Arrange(Vector3.forward); var disabledFrame = Render(); var original = Pixels();
                Check("default-disabled-no-allocation",disabledFrame.lightShadowAtlas==null&&stage.LightShadowMapCount==0);
                light.shadow.enabled = true;
                // Independent ray/plane geometry oracle. Only silhouette/alpha raster bands
                // are excluded; cube seams and corners remain in the accepted sample domain.
                void Oracle(string name, bool expectOcclusion = true)
                {
                    light.shadow.enabled=false; Render(); var lit=Pixels(); var saved=light.radiance;
                    light.radiance=Vector3.zero; Render(); var basis=Pixels(); light.radiance=saved; light.shadow.enabled=true;
                    var f=Render(); var actual=Pixels(); var g0=ReadSceneTarget(f.albedoCoverage); var g1=ReadSceneTarget(f.normalGroup);
                    var inverse=Inverse(); var origin=inverse.MultiplyPoint(light.position);
                    bool active=Array.IndexOf(options.shadows.casters,caster)>=0&&caster.renderer.enabled&&!caster.renderer.forceRenderingOff&&caster.renderer.gameObject.activeInHierarchy;
                    float worst=0; int samples=0,blockedCount=0,clearCount=0,seams=0;
                    for(int y=0;y<129;y++)for(int x=0;x<129;x++)
                    {
                        int index=y*129+x; if(g0[index].a<.5f)continue;
                        var ray=camera.ViewportPointToRay(new Vector3((x+.5f)/129,(y+.5f)/129));
                        if(!new Plane(direction,receiver.transform.position).Raycast(ray,out float distance))continue;
                        var world=ray.GetPoint(distance)+Rgb(g1[index]).normalized*light.shadow.normalBias;
                        var local=inverse.MultiplyPoint(world); float t=-origin.z/(local.z-origin.z); var hit=Vector3.LerpUnclamped(origin,local,t);
                        if(Mathf.Abs(Mathf.Abs(hit.x)-.5f)<.06f||Mathf.Abs(Mathf.Abs(hit.y)-.5f)<.06f)continue;
                        float alpha=caster.alpha;
                        if(caster.alphaMap is Texture2D map)
                        {
                            float u=(hit.x+.5f)*caster.uvST.x+caster.uvST.z; if(Mathf.Abs(u-.5f)<.06f)continue;
                            alpha*=map.GetPixel(Mathf.Clamp(Mathf.FloorToInt(u*map.width),0,map.width-1),0).a;
                        }
                        float radial=Vector3.Distance(world,light.position),hitRadius=radial*t;
                        bool blocked=active&&t>0&&t<1&&Mathf.Abs(hit.x)<.5f&&Mathf.Abs(hit.y)<.5f&&alpha>=caster.cutoff&&
                            radial>=light.shadow.nearPlane&&radial<=light.range&&hitRadius>=light.shadow.nearPlane&&hitRadius<=light.range&&radial-light.shadow.depthBias>hitRadius;
                        var expected=Rgb(basis[index])+(Rgb(lit[index])-Rgb(basis[index]))*(blocked?1-light.shadow.strength:1);
                        worst=Mathf.Max(worst,Error(expected,Rgb(actual[index])));samples++;if(blocked)blockedCount++;else clearCount++;
                        var r=world-light.position;float a=Mathf.Abs(r.x),b=Mathf.Abs(r.y),c=Mathf.Abs(r.z),m=Mathf.Max(a,b,c);
                        if((Mathf.Abs(a-b)<.015f&&a>=c)||(Mathf.Abs(a-c)<.015f&&a>=b)||(Mathf.Abs(b-c)<.015f&&b>=a))seams++;
                    }
                    Check(name+"-cpu-ray-interiors",worst<.003f&&samples>8000&&clearCount>1000&&(expectOcclusion?blockedCount>30:blockedCount==0),worst);
                    if(name.StartsWith("edge")||name.StartsWith("corner"))Check(name+"-includes-face-boundaries",seams>10);
                }
                // Producer oracle: world rays through native atlas pixel centers against
                // the actual planar geometry. Radial depth varies within a flat triangle.
                void DepthOracle(string name)
                {
                    var f=Render();var pixels=ReadSceneTarget(f.lightShadowAtlas);int tile=options.shadows.tileResolution,grid=f.lightShadowAtlas.width/tile;
                    var inverse=Inverse();var origin=inverse.MultiplyPoint(light.position);float worst=0;int filled=0,empty=0;
                    for(int face=0;face<6;face++)for(int y=1;y<tile;y+=3)for(int x=1;x<tile;x+=3)
                    {
                        var ray=(forward[face]+right[face]*((x+.5f)/tile*2-1)+up[face]*((y+.5f)/tile*2-1)).normalized;
                        var local=inverse.MultiplyVector(ray);float t=-origin.z/local.z;var hit=origin+local*t;
                        if(Mathf.Abs(Mathf.Abs(hit.x)-.5f)<.012f||Mathf.Abs(Mathf.Abs(hit.y)-.5f)<.012f)continue;
                        float expected=t>=light.shadow.nearPlane&&t<=light.range&&Mathf.Abs(hit.x)<.5f&&Mathf.Abs(hit.y)<.5f?t/light.range:1;
                        float actual=pixels[(face/grid*tile+y)*f.lightShadowAtlas.width+face%grid*tile+x].r;
                        worst=Mathf.Max(worst,Mathf.Abs(expected-actual));if(expected<1)filled++;else empty++;
                    }
                    Check(name+"-six-face-radial-depth",worst<.00001f&&filled>40&&empty>1000&&stage.LightShadowMapCount==6&&stage.LightShadowCasterDrawCalls==6,worst);
                    bool unused=true;for(int y=2*tile;y<3*tile;y++)for(int x=0;x<3*tile;x++)unused&=pixels[y*3*tile+x].r==1;
                    Check(name+"-unused-atlas-row-clear",unused&&grid==3);
                }
                for(int face=0;face<6;face++) {Arrange(forward[face]);Oracle("axis-"+face);DepthOracle("axis-"+face);}
                for(int a=0;a<3;a++)for(int b=a+1;b<3;b++)for(int s=-1;s<=1;s+=2)for(int t=-1;t<=1;t+=2)
                {var d=Vector3.zero;d[a]=s;d[b]=t;Arrange(d);Oracle("edge-"+a+b+"-"+s+"-"+t);}
                for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
                {Arrange(new Vector3(x,y,z));Oracle("corner-"+x+"-"+y+"-"+z);}
                DepthOracle("corner");SaveSsrPreview("scene-point-shadow-corner",Pixels(),129,129,false);
                Arrange(new Vector3(1,0,1));
                occluder.transform.position+=camera.transform.right*.5f;
                light.shadow.filter=SceneShadowFilter.Pcf3x3;
                // Consumer oracle uses read-back source depth and independently projects
                // each tap through the six dot-product bases. Compare ALL receiver pixels.
                void FilterOracle(string name)
                {
                    light.shadow.enabled=false;Render();var lit=Pixels();var saved=light.radiance;light.radiance=Vector3.zero;Render();var basis=Pixels();light.radiance=saved;light.shadow.enabled=true;
                    var f=Render();var actual=Pixels();var atlas=ReadSceneTarget(f.lightShadowAtlas);var g0=ReadSceneTarget(f.albedoCoverage);
                    int tile=options.shadows.tileResolution,grid=f.lightShadowAtlas.width/tile,crossings=0,differentClamp=0,samples=0,mismatches=0;float worst=0;
                    int Select(Vector3 d)
                    {
                        float maximum=0;for(int i=0;i<6;i++)maximum=Mathf.Max(maximum,Vector3.Dot(d,forward[i]));
                        for(int i=0;i<6;i++)if(Vector3.Dot(d,forward[i])>=maximum*(1-1e-5f))return i;return 0;
                    }
                    float Tap(Vector3 d,float receiverDepth,int forcedFace=-1)
                    {
                        int face=forcedFace<0?Select(d):forcedFace;float axial=Vector3.Dot(d,forward[face]);
                        int x=Mathf.Clamp(Mathf.FloorToInt((Vector3.Dot(d,right[face])/axial*.5f+.5f)*tile),0,tile-1);
                        int y=Mathf.Clamp(Mathf.FloorToInt((Vector3.Dot(d,up[face])/axial*.5f+.5f)*tile),0,tile-1);
                        return receiverDepth<=atlas[(face/grid*tile+y)*f.lightShadowAtlas.width+face%grid*tile+x].r?1:0;
                    }
                    for(int y=0;y<129;y++)for(int x=0;x<129;x++)
                    {
                        int index=y*129+x;if(g0[index].a<.5f)continue;var ray=camera.ViewportPointToRay(new Vector3((x+.5f)/129,(y+.5f)/129));
                        if(!new Plane(direction,receiver.transform.position).Raycast(ray,out float distance))continue;
                        var d=ray.GetPoint(distance)-light.position;int face=Select(d);float axial=Vector3.Dot(d,forward[face]);
                        float u=Vector3.Dot(d,right[face])/axial,v=Vector3.Dot(d,up[face])/axial,depth=(d.magnitude-light.shadow.depthBias)/light.range,visibility=0,clamped=0;
                        for(int j=-1;j<=1;j++)for(int i=-1;i<=1;i++)
                        {
                            var tap=forward[face]+right[face]*(u+2f*i/tile)+up[face]*(v+2f*j/tile);
                            if(Select(tap)!=face)crossings++;visibility+=Tap(tap,depth);clamped+=Tap(tap,depth,face);
                        }
                        if(visibility!=clamped)differentClamp++;
                        var expected=Rgb(basis[index])+(Rgb(lit[index])-Rgb(basis[index]))*Mathf.Lerp(1,visibility/9,light.shadow.strength);
                        if(Error(expected,Rgb(actual[index]))>.003f)
                        {
                            if(mismatches<8)Debug.Log("[PointPcf] "+name+" xy="+x+","+y+" d="+d.ToString("F8")+" face="+face+" uv="+u.ToString("F8")+","+v.ToString("F8")+" expected="+visibility+"/9 actual="+((actual[index].r-basis[index].r)/(lit[index].r-basis[index].r)).ToString("F8"));
                            mismatches++;
                        }
                        worst=Mathf.Max(worst,Error(expected,Rgb(actual[index])));samples++;
                    }
                    Check(name+"-full-image-pcf-reference",worst<.003f&&samples>12000&&crossings>100,worst);
                    Check(name+"-adjacent-face-taps-differ-from-border-clamp",differentClamp>0,differentClamp);
                    if(mismatches>0)Debug.Log("[PointPcf] "+name+" mismatches="+mismatches);
                }
                FilterOracle("edge"); SaveSsrPreview("scene-point-shadow-seam-pcf",Pixels(),129,129,false);
                Arrange(Vector3.one);occluder.transform.position+=camera.transform.right*.5f;FilterOracle("corner");
                light.shadow.filter=SceneShadowFilter.Hard;Arrange(Vector3.forward);
                var initial=Pixels();
                foreach(var backend in new[]{SceneDecalLightBackend.Scalar,SceneDecalLightBackend.Instanced}) {options.backend=backend;Oracle("backend-"+backend);}
                occluder.transform.position+=new Vector3(.3f,-.4f,.2f);Oracle("moving-caster");Arrange(Vector3.forward);
                light.position+=new Vector3(-.4f,.1f,.2f);Oracle("moving-light");light.position-=new Vector3(-.4f,.1f,.2f);
                light.rotation=Quaternion.Euler(80,-43,19);Oracle("point-rotation-does-not-constrain-coverage");light.rotation=Quaternion.identity;
                occluder.transform.rotation*=Quaternion.Euler(20,30,25);Oracle("sloping-caster");DepthOracle("sloping");Arrange(Vector3.forward);
                light.shadow.strength=.4f;Oracle("partial-strength");light.shadow.strength=1;
                light.shadow.depthBias=2;Oracle("world-radial-bias",false);light.shadow.depthBias=.002f;
                light.shadow.normalBias=2;Oracle("world-normal-bias",false);light.shadow.normalBias=0;
                light.shadow.nearPlane=2.5f;Oracle("radial-near-clips-caster",false);light.shadow.nearPlane=.05f;
                Arrange(Vector3.one);light.shadow.nearPlane=1.2f;Oracle("corner-radial-near-preserves-caster-below-axial-near");DepthOracle("radial-near-corner");light.shadow.nearPlane=.05f;Arrange(Vector3.forward);
                occluder.transform.position+=direction*5;Oracle("caster-after-range",false);Arrange(Vector3.forward);
                caster.renderer.enabled=false;Oracle("disabled-caster",false);caster.renderer.enabled=true;
                caster.renderer.forceRenderingOff=true;Oracle("forced-off-caster",false);caster.renderer.forceRenderingOff=false;
                occluder.SetActive(false);Oracle("inactive-caster",false);occluder.SetActive(true);
                options.shadows.casters=Array.Empty<SceneShadowCaster>();Oracle("empty-casters",false);options.shadows.casters=new[]{caster};
                caster.vertexScale=new Vector3(.7f,1.3f,1);Oracle("explicit-vertex-scale");caster.vertexScale=Vector3.one;
                var alpha=Own(new Texture2D(2,1,TextureFormat.RGBAFloat,false,true));alpha.filterMode=FilterMode.Point;alpha.wrapMode=TextureWrapMode.Clamp;alpha.SetPixels(new[]{new Color(1,1,1,0),Color.white});alpha.Apply();
                caster.alphaMap=alpha;caster.cutoff=.5f;Oracle("alpha-cutout");caster.uvST=new Vector4(-1,1,1,0);Oracle("cutout-uv-flip");caster.alphaMap=null;caster.cutoff=0;caster.uvST=new Vector4(1,1,0,0);
                camera.orthographic=false;camera.fieldOfView=60;Oracle("perspective-host");camera.orthographic=true;
                var gi=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));gi.SetPixel(0,0,new Color(2,1,.5f,1));gi.Apply();surface.gi=new SceneGiInput{source=SceneGiSource.Lightmap,lightmap=gi};stage.giBaseScale=.4f;light.giWeight=1;
                Oracle("gi-direct-with-independent-base-and-emission");light.diffuseScale=0;Oracle("specular-only");light.diffuseScale=1;light.specularScale=0;Oracle("diffuse-only");light.specularScale=1;
                surface.gi.source=SceneGiSource.None;stage.giBaseScale=0;light.giWeight=0;

                var nearObject=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));nearObject.layer=24;
                nearObject.transform.SetPositionAndRotation((occluder.transform.position+light.position)*.5f,occluder.transform.rotation);nearObject.transform.localScale=occluder.transform.localScale*.5f;
                var nearCaster=new SceneShadowCaster{renderer=nearObject.GetComponent<Renderer>(),cull=CullMode.Off};
                var farDepth=ReadSceneTarget(Render().lightShadowAtlas);options.shadows.casters=new[]{caster,nearCaster};var nearDepth=ReadSceneTarget(Render().lightShadowAtlas);float nearestError=0;
                for(int i=0;i<farDepth.Length;i++)nearestError=Mathf.Max(nearestError,Mathf.Abs(nearDepth[i].r-(farDepth[i].r<1?farDepth[i].r*.5f:1)));
                Check("nearest-radial-depth-half-distance",nearestError<.00001f,nearestError);
                options.shadows.casters=new[]{nearCaster,caster};Check("reversed-caster-order-same-nearest-depth",ScenePixelsEqual(nearDepth,ReadSceneTarget(Render().lightShadowAtlas)));
                options.shadows.casters=new[]{caster};nearObject.SetActive(false);

                var skinHost=Own(new GameObject("Point shadow skinned caster"));skinHost.layer=24;skinHost.transform.SetPositionAndRotation(occluder.transform.position,occluder.transform.rotation);skinHost.transform.localScale=Vector3.one;
                var bone=Own(new GameObject("Point shadow bone")).transform;bone.SetParent(skinHost.transform,false);
                var skin=skinHost.AddComponent<SkinnedMeshRenderer>();var skinMesh=Own(Instantiate(borrowedMesh));var weights=new BoneWeight[skinMesh.vertexCount];for(int i=0;i<weights.Length;i++)weights[i]=new BoneWeight{boneIndex0=0,weight0=1};
                skinMesh.boneWeights=weights;skinMesh.bindposes=new[]{Matrix4x4.identity};skin.sharedMesh=skinMesh;skin.bones=new[]{bone};skin.rootBone=bone;skin.sharedMaterial=borrowedMaterial;skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*5);
                var control=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));control.layer=24;control.transform.SetPositionAndRotation(skinHost.transform.position,skinHost.transform.rotation);
                var controlMesh=Own(Instantiate(borrowedMesh));control.GetComponent<MeshFilter>().sharedMesh=controlMesh;var originalRenderer=caster.renderer;caster.renderer=skin;deformBone=bone;
                for(int i=0;i<3;i++)
                {
                    bone.localPosition=new Vector3(-i*.3f,i*.15f,0);bone.localRotation=Quaternion.Euler(i*12,i*15,i*20);yield return null;Oracle("skinned-pose-"+i);
                    var skinPixels=Pixels();var skinDepth=ReadSceneTarget(Render().lightShadowAtlas);var vertices=borrowedMesh.vertices;var deform=Matrix4x4.TRS(bone.localPosition,bone.localRotation,bone.localScale);
                    for(int v=0;v<vertices.Length;v++)vertices[v]=deform.MultiplyPoint(vertices[v]);controlMesh.vertices=vertices;controlMesh.RecalculateBounds();caster.renderer=control.GetComponent<Renderer>();var f=Render();
                    Check("skin-vs-static-depth-"+i,PixelError(skinDepth,ReadSceneTarget(f.lightShadowAtlas))<.000001f,PixelError(skinDepth,ReadSceneTarget(f.lightShadowAtlas)));
                    Check("skin-vs-static-complete-image-"+i,PixelError(skinPixels,Pixels())<.003f,PixelError(skinPixels,Pixels()));caster.renderer=skin;
                }
                caster.renderer=originalRenderer;deformBone=null;skinHost.SetActive(false);control.SetActive(false);
                // All six faces have actual geometry in the native mixed-light capture.
                var captureCasters=new SceneShadowCaster[6];var captureObjects=new GameObject[6];
                for(int i=0;i<6;i++)
                {
                    var obj=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));obj.layer=24;obj.transform.SetPositionAndRotation(light.position+forward[i]*(1+.1f*i),Quaternion.LookRotation(forward[i],up[i]));obj.transform.localScale=Vector3.one*.5f;
                    captureObjects[i]=obj;captureCasters[i]=new SceneShadowCaster{renderer=obj.GetComponent<Renderer>(),cull=CullMode.Off};
                }
                var spot=new SceneDecalLight{shape=SceneDecalLightShape.Spot,position=light.position,range=6,spotInnerAngle=90,spotOuterAngle=90,radiance=new Vector3(.4f,.8f,1.2f)};spot.shadow.enabled=true;
                options.shadows.maxShadowedLights=2;options.shadows.casters=captureCasters;options.lights=new[]{spot,light};
                stage.lightDirection=Vector3.back;stage.lightRadiance=new Vector3(.3f,.2f,.1f);stage.mainLightShadow=new SceneDirectionalShadowSettings{enabled=true,origin=light.position,farPlane=6,casters=captureCasters};
                var mixed=Render();var mixedPixels=Pixels();
                Check("mixed-point-spot-main-resources",stage.LightShadowMapCount==7&&stage.LightShadowCasterDrawCalls==42&&stage.MainShadowTargetCount==1&&mixed.lightShadowAtlas!=mixed.mainLightShadowDepth);
                options.backend=SceneDecalLightBackend.Scalar;Render();Check("mixed-scalar-instanced-image",PixelError(mixedPixels,Pixels())<.003f,PixelError(mixedPixels,Pixels()));options.backend=SceneDecalLightBackend.Instanced;
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_POINT_SHADOW")=="1")
                {
                    bool started=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;try{if(started){Render();Pixels();}}finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("requested-native-six-face-mixed-capture",started&&ended);
                }
                SaveSsrPreview("scene-point-shadow-mixed",Pixels(),129,129,false);
                var otherPoint=new SceneDecalLight{shape=SceneDecalLightShape.Point,position=light.position+Vector3.right*.4f,range=5,radiance=new Vector3(.2f,.7f,.4f)};
                otherPoint.shadow.enabled=true;otherPoint.shadow.filter=SceneShadowFilter.Pcf3x3;options.shadows.maxShadowedLights=3;
                options.enabled=false;Render();var noLamps=Pixels();options.enabled=true;var lamps=new[]{spot,light,otherPoint};var sum=new Vector3[noLamps.Length];
                foreach(var lamp in lamps){options.lights=new[]{lamp};Render();var image=Pixels();for(int i=0;i<sum.Length;i++)sum[i]+=Rgb(image[i])-Rgb(noLamps[i]);}
                options.lights=lamps;var multi=Render();var multiPixels=Pixels();float sumError=0;
                for(int i=0;i<sum.Length;i++)sumError=Mathf.Max(sumError,Error(Rgb(noLamps[i])+sum[i],Rgb(multiPixels[i])));
                Check("two-points-plus-spot-independent-light-sum",sumError<.003f&&stage.LightShadowMapCount==13&&multi.lightShadowAtlas.width==1024,sumError);
                options.backend=SceneDecalLightBackend.Scalar;Render();Check("two-points-plus-spot-scalar-image",PixelError(multiPixels,Pixels())<.003f,PixelError(multiPixels,Pixels()));options.backend=SceneDecalLightBackend.Instanced;
                foreach(var obj in captureObjects)obj.SetActive(false);options.shadows.casters=new[]{caster};options.lights=new[]{light};stage.mainLightShadow.enabled=false;stage.lightRadiance=Vector3.zero;
                var first=Render();var firstPixels=Pixels();var firstAtlas=ReadSceneTarget(first.lightShadowAtlas);
                var host2=Own(new GameObject("Independent point shadow camera"));var camera2=host2.AddComponent<Camera>();camera2.CopyFrom(camera);camera2.enabled=false;camera2.transform.SetPositionAndRotation(camera.transform.position,camera.transform.rotation);
                var target2=Own(new RenderTexture(97,65,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target2.Create();camera2.targetTexture=target2;
                var stage2=host2.AddComponent<SceneDeferredCamera>();stage2.sceneEnabled=true;stage2.sceneLayers=stage.sceneLayers;stage2.surfaces=stage.surfaces;
                stage2.decalLighting=new SceneDecalLightSettings{enabled=true,lights=new[]{otherPoint},shadows=new SceneLightShadowSettings{tileResolution=128,casters=new[]{caster}}};
                camera2.Render();Check("per-camera-independent-six-face-target",stage2.TryGetFrame(out var second)&&second.lightShadowAtlas!=first.lightShadowAtlas&&second.lightShadowAtlas.width==384&&first.IsCurrent);
                Check("second-camera-preserves-first-depth-and-image",ScenePixelsEqual(firstPixels,Pixels())&&ScenePixelsEqual(firstAtlas,ReadSceneTarget(first.lightShadowAtlas)));host2.SetActive(false);camera2.targetTexture=null;
                camera.targetTexture=target2;var resized=Render();Check("host-resize-keeps-point-tile-resolution",resized.albedoCoverage.width==97&&resized.lightShadowAtlas.width==768);camera.targetTexture=target;
                var old=Render();old.lightShadowAtlas.Release();Check("lost-depth-invalidates-frame",!old.IsCurrent);var rebuilt=Render();Check("lost-depth-rebuilt",rebuilt.lightShadowAtlas.IsCreated()&&rebuilt.lightShadowAtlas!=old.lightShadowAtlas);
                var prior=Render();var depthTarget=prior.lightShadowAtlas;options.shadows.tileResolution=128;Render();Check("tile-resize-releases-old-atlas",!prior.IsCurrent&&!depthTarget.IsCreated());options.shadows.tileResolution=256;
                void Reject(string name){camera.Render();Check(name,!stage.TryGetFrame(out _)&&stage.LightShadowMapCount==0&&!string.IsNullOrEmpty(stage.UnavailableReason));}
                options.shadows.tileResolution=2048;Reject("six-face-atlas-texture-budget-rejected");options.shadows.tileResolution=256;
                options.lights=new[]{spot,light};options.shadows.maxShadowedLights=1;Reject("two-source-budget-not-face-budget");options.lights=new[]{light};Render();Check("one-source-budget-allows-six-faces",stage.LightShadowMapCount==6);
                var sixteen=new SceneDecalLight[16];for(int i=0;i<16;i++)sixteen[i]=light;options.lights=sixteen;options.shadows.maxShadowedLights=16;options.shadows.tileResolution=32;options.shadows.casters=Array.Empty<SceneShadowCaster>();
                var maximumBudget=Render();Check("sixteen-sources-ninety-six-maps",stage.LightShadowMapCount==96&&maximumBudget.lightShadowAtlas.width==320&&stage.LightShadowCasterDrawCalls==0);
                options.lights=new[]{light};options.shadows.tileResolution=256;options.shadows.casters=new[]{caster};
                light.shadow.nearPlane=light.range;Reject("near-equals-range-rejected");light.shadow.nearPlane=.05f;
                light.shadow.strength=0;Render();Check("zero-strength-releases-atlas",stage.LightShadowMapCount==0&&ScenePixelsEqual(original,Pixels()));light.shadow.strength=1;
                light.radiance=Vector3.zero;Render();Check("zero-radiance-no-depth",stage.LightShadowMapCount==0);light.radiance=new Vector3(3,2,1);
                var savedPosition=light.position;light.position+=Vector3.right*100;Render();Check("offscreen-light-no-depth",stage.LightShadowMapCount==0);light.position=savedPosition;
                light.shadow.enabled=false;Render();Check("disabled-restores-original-point-image",ScenePixelsEqual(original,Pixels()));light.shadow.enabled=true;
                Check("borrowed-input-state-preserved",caster.renderer.sharedMaterial==borrowedMaterial&&occluder.GetComponent<MeshFilter>().sharedMesh==borrowedMesh&&!caster.renderer.HasPropertyBlock()&&caster.renderer.enabled&&!caster.renderer.forceRenderingOff);
                var finalDepth=Render().lightShadowAtlas;stage.enabled=false;Check("host-disable-releases-six-face-atlas",!finalDepth.IsCreated()&&stage.LightShadowMapCount==0);
                camera.targetTexture=null;host.SetActive(false);receiver.SetActive(false);occluder.SetActive(false);
            }
            finally
            {
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
        }
    }
}
