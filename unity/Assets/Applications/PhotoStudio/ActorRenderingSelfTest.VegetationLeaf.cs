using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyVegetationLeaf(Report report)
        {
            yield return null;
            void Check(string name,bool ok,float error=0) => FrameworkCheck(report,"vegetation-leaf-"+name,ok,error);
            var previous=FindObjectsOfType<Renderer>();var forced=previous.Select(r=>r.forceRenderingOff).ToArray();
            foreach(var r in previous)r.forceRenderingOff=true;
            var active=RenderTexture.active;VegetationWindDeformer wind=null;
            try
            {
                const int size=65;
                var host=Own(new GameObject("Thin leaf independent material fixture"));var camera=host.AddComponent<Camera>();
                camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;camera.cullingMask=0;
                camera.orthographic=true;camera.orthographicSize=1.25f;camera.aspect=1;camera.transform.position=new Vector3(0,0,-3);
                camera.nearClipPlane=.1f;camera.farClipPlane=20;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                var target=Own(new RenderTexture(size,size,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                var stage=host.AddComponent<SceneDeferredCamera>();stage.sceneEnabled=true;stage.sceneLayers=1<<25;
                stage.motion.enabled=true;
                stage.lightDirection=Vector3.back;stage.lightRadiance=new Vector3(1.3f,.9f,.7f);stage.ambientIrradiance=new Vector3(.07f,.11f,.13f);
                var source=Own(new Mesh{name="Authored leaf quad"});source.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
                source.normals=Enumerable.Repeat(Vector3.back,4).ToArray();source.tangents=Enumerable.Repeat(new Vector4(1,0,0,-1),4).ToArray();
                source.uv=new[]{new Vector2(0,0),new Vector2(1,0),new Vector2(1,1),new Vector2(0,1)};source.uv2=source.uv;source.triangles=new[]{0,2,1,0,3,2};source.RecalculateBounds();
                var leafObject=Own(new GameObject("Explicit current thin leaf"));leafObject.layer=25;var filter=leafObject.AddComponent<MeshFilter>();filter.sharedMesh=source;
                var renderer=leafObject.AddComponent<MeshRenderer>();var borrowed=Own(new Material(Resources.Load<Shader>("CrowdNativeReference")));renderer.sharedMaterial=borrowed;
                Texture2D Map(string name,Color[] values)
                {var m=Own(new Texture2D(2,2,TextureFormat.RGBAFloat,false,true){name=name,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Repeat});m.SetPixels(values);m.Apply(false);return m;}
                var thickness=Map("Authored linear leaf thickness",new[]{new Color(.1f,0,0,1),new Color(.4f,0,0,1),new Color(.7f,0,0,1),new Color(1,0,0,1)});
                var cutoff=Map("Independent leaf cutout",new[]{Color.white,Color.white,new Color(1,1,1,0),Color.white});
                var normalMap=Map("Independent leaf tangent normals",Enumerable.Repeat(new Color(.65f,.6f,.96f,1),4).ToArray());
                var leaf=new VegetationLeafMaterial{enabled=true,thicknessMap=thickness,strength=.7f,thickness=1.3f};
                var inputs=new SceneDeferredCamera.MaterialInputs{albedo=new Vector3(.2f,.55f,.31f),mos=new Vector3(0,.8f,.3f),emission=new Vector3(.015f,.025f,.01f),uvST=new Vector4(.73f,.81f,.047f,.091f)};
                var surface=new SceneDeferredCamera.Surface{renderer=renderer,cull=CullMode.Off,inputs=inputs,leaf=leaf};stage.surfaces=new[]{surface};
                var forward=host.AddComponent<SceneForwardLightingCamera>();forward.surfaceLayers=1<<25;
                var forwardSurface=new SceneForwardSurface{renderer=renderer,cull=CullMode.Off,inputs=inputs,leaf=leaf};forward.settings.surfaces=new[]{forwardSurface};
                forward.settings.backend=SceneForwardLightBackend.BruteForce;
                SceneDeferredCamera.Frame Render(){camera.Render();if(!stage.TryGetFrame(out var f))throw new InvalidOperationException(stage.UnavailableReason);return f;}
                Vector3 Rgb(Color c)=>new Vector3(c.r,c.g,c.b);
                Vector3 Tau(float sample)
                {var v=Vector3.zero;for(int c=0;c<3;c++)v[c]=(float)(leaf.strength*leaf.transmissionTint[c]*Math.Exp(-leaf.absorption[c]*(double)leaf.thickness*sample));return v;}
                Color Sample(Texture2D map,Vector2 uv){uv=new Vector2(uv.x-Mathf.Floor(uv.x),uv.y-Mathf.Floor(uv.y));return map.GetPixel(Mathf.FloorToInt(uv.x*map.width),Mathf.FloorToInt(uv.y*map.height));}
                int cases=0;Color[] firstBack=null;
                var atlasRadiance=Vector3.one;
                float Baked(SceneBakedShadowChannel channel)
                {
                    if(channel==SceneBakedShadowChannel.None||!surface.bakedShadow.Enabled)return 1;
                    int c=(int)channel-1,levels=c==0?255:c==3?3:7;
                    return Mathf.Floor(surface.bakedShadow.visibility[c]*levels+.5f)/levels;
                }
                Color[] Case(string name,bool staticMap=true,float shadowVisibility=1,bool compareForward=true)
                {
                    var frame=Render();var actual=ReadSceneTarget(target);var albedo=ReadSceneTarget(frame.albedoCoverage);var normals=ReadSceneTarget(frame.normalGroup);
                    var mos=ReadSceneTarget(frame.mosDepth);var emission=ReadSceneTarget(frame.emission);var tau=frame.leafTransmission!=null?ReadSceneTarget(frame.leafTransmission):null;
                    var gi=frame.bakedDiffuseGi!=null?ReadSceneTarget(frame.bakedDiffuseGi):null;var expected=new Color[actual.Length];float materialError=0;int covered=0;
                    for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                    {
                        int i=y*size+x;expected[i]=Color.black;if(albedo[i].a<.5f){if(tau!=null)materialError=Mathf.Max(materialError,Mathf.Abs(tau[i].a));continue;}covered++;
                        var world=camera.ViewportToWorldPoint(new Vector3((x+.5f)/size,(y+.5f)/size,mos[i].a));var n=Rgb(normals[i]).normalized;var v=Vector3.back;
                        var transfer=tau!=null?Rgb(tau[i]):Vector3.zero;
                        if(staticMap&&leaf.enabled)
                        {
                            var p=leafObject.transform.InverseTransformPoint(world);var uv=new Vector2((p.x+1)*.5f,(p.y+1)*.5f);uv=Vector2.Scale(uv,new Vector2(inputs.uvST.x,inputs.uvST.y))+new Vector2(inputs.uvST.z,inputs.uvST.w);
                            var reference=Tau(leaf.thicknessMap!=null?Sample(thickness,uv).r:1);
                            materialError=Mathf.Max(materialError,Mathf.Abs(transfer.x-reference.x),Mathf.Abs(transfer.y-reference.y),Mathf.Abs(transfer.z-reference.z),Mathf.Abs(tau[i].a-1));
                        }
                        var baseColor=Rgb(albedo[i]);var material=Rgb(mos[i]);
                        var direct=Vector3.Scale(LeafCpuBrdf(baseColor,material,n,v,stage.lightDirection.normalized,stage.directionalDiffuseScale,stage.directionalSpecularScale,stage.directionalBacklight,transfer),stage.lightRadiance);
                        if(gi!=null&&gi[i].a>.5f)direct=Vector3.Scale(direct,Vector3.Lerp(Vector3.one,Rgb(gi[i]),stage.directionalGiWeight));
                        direct*=Mathf.Min(shadowVisibility,Baked(stage.mainBakedShadowChannel));
                        if(stage.decalLighting.enabled)foreach(var light in stage.decalLighting.lights)
                        {
                            if(!light.enabled||light.receiverGroup!=0&&light.receiverGroup!=surface.receiverGroup)continue;
                            var local=LeafCpuLocal(light,baseColor,material,world,n,v,transfer);
                            if(gi!=null&&gi[i].a>.5f)local=Vector3.Scale(local,Vector3.Lerp(Vector3.one,Rgb(gi[i]),light.giWeight));
                            direct+=Vector3.Scale(local,atlasRadiance)*Mathf.Min(shadowVisibility,Baked(light.bakedShadowChannel));
                        }
                        var indirect=Vector3.Scale(baseColor,stage.ambientIrradiance)*((1-material.x)*material.y/Mathf.PI);
                        if(gi!=null&&gi[i].a>.5f)indirect=Vector3.Scale(baseColor,Rgb(gi[i]))*((1-material.x)*material.y*stage.giBaseScale);
                        var rgb=direct+indirect+Rgb(emission[i]);expected[i]=new Color(rgb.x,rgb.y,rgb.z,1);
                    }
                    float e=PixelError(actual,expected);Check(name+"-whole-independent-lighting",covered>100&&e<=.0002f,e);
                    Check(name+"-whole-optical-metadata",materialError<=.0005f,materialError);
                    Check(name+"-owned-attachment",leaf.enabled?frame.leafTransmission!=null&&stage.LeafResourceBytes==size*size*8:frame.leafTransmission==null&&stage.LeafResourceBytes==0);
                    SaveSsrPreview("vegetation-leaf-"+name,actual,size,size,false);cases++;
                    if(compareForward)
                    {
                        forwardSurface.alphaCutoff=surface.alphaCutoff;forwardSurface.gi=surface.gi;forwardSurface.bakedShadow=surface.bakedShadow;
                        forward.settings.lightDirection=stage.lightDirection;forward.settings.lightRadiance=stage.lightRadiance;forward.settings.ambientIrradiance=stage.ambientIrradiance;
                        forward.settings.diffuseScale=stage.directionalDiffuseScale;forward.settings.specularScale=stage.directionalSpecularScale;forward.settings.backlightScale=stage.directionalBacklight;
                        forward.settings.directionalGiWeight=stage.directionalGiWeight;forward.settings.giBaseScale=stage.giBaseScale;forward.settings.localLights=stage.decalLighting;forward.settings.mainLightShadow=stage.mainLightShadow;
                        forward.settings.mainBakedShadowChannel=stage.mainBakedShadowChannel;
                        stage.sceneEnabled=false;forward.settings.enabled=true;camera.Render();var forwardPixels=ReadSceneTarget(target);
                        if(!string.IsNullOrEmpty(forward.UnavailableReason))throw new InvalidOperationException(forward.UnavailableReason);
                        // Deferred metadata is Half; Forward evaluates full material precision.
                        float difference=PixelError(actual,forwardPixels);Check(name+"-whole-forward-material",difference<=.003f,difference);
                        forward.settings.enabled=false;stage.sceneEnabled=true;
                    }
                    return actual;
                }
                leaf.enabled=false;Case("disabled");leaf.enabled=true;Case("front");stage.lightDirection=Vector3.forward;firstBack=Case("backlit");
                leaf.thickness=0;var thin=Case("zero-thickness");Check("thickness-changes-actual-radiance",PixelError(firstBack,thin)>.01f);leaf.thickness=1.3f;
                leaf.strength=0;Case("zero-strength");leaf.strength=1;Case("full-strength");leaf.strength=.7f;
                inputs.normalMap=normalMap;Case("mapped-normal");inputs.normalMap=null;
                inputs.albedoMap=cutoff;surface.alphaCutoff=.5f;Case("cutout");
                leafObject.transform.localScale=new Vector3(-.83f,1.07f,.91f);Case("mirrored-nonuniform");leafObject.transform.localScale=Vector3.one;
                leafObject.transform.localRotation=Quaternion.Euler(0,180,0);Case("opposite-visible-side");leafObject.transform.localRotation=Quaternion.identity;
                inputs.mos.x=1;Case("metal-excludes-diffuse-transmission");inputs.mos.x=0;
                stage.directionalBacklight=.6f;Case("existing-artistic-backlight");stage.directionalBacklight=0;
                stage.lightRadiance=Vector3.zero;stage.decalLighting.enabled=true;
                foreach(var shape in new[]{SceneDecalLightShape.Point,SceneDecalLightShape.Spot,SceneDecalLightShape.Capsule,SceneDecalLightShape.Area})
                {
                    var light=new SceneDecalLight{shape=shape,position=new Vector3(.13f,.19f,2),rotation=Quaternion.Euler(0,180,0),range=5,radiance=new Vector3(1.3f,.7f,1.1f),spotInnerAngle=120,spotOuterAngle=120};
                    stage.decalLighting.lights=new[]{light};stage.decalLighting.backend=SceneDecalLightBackend.Scalar;var scalar=Case("local-"+shape+"-scalar");
                    stage.decalLighting.backend=SceneDecalLightBackend.Instanced;forward.settings.backend=SceneForwardLightBackend.Tiled;var instanced=Case("local-"+shape+"-instanced");
                    Check("local-"+shape+"-native-backends",PixelError(scalar,instanced)<=.0002f,PixelError(scalar,instanced));
                }
                stage.decalLighting.enabled=false;stage.lightRadiance=new Vector3(1.3f,.9f,.7f);
                var giMap=Map("Independent leaf GI",Enumerable.Repeat(new Color(.6f,.4f,.9f,1),4).ToArray());
                var giDirection=Map("Independent GI direction",Enumerable.Repeat(new Color(.5f,.5f,.75f,.5f),4).ToArray());
                var leafGi=new SceneGiInput{source=SceneGiSource.Lightmap,lightmap=giMap,directionality=giDirection};surface.gi=leafGi;
                stage.directionalGiWeight=.4f;Case("two-hemisphere-gi");
                var giFrame=Render();var actualGi=ReadSceneTarget(giFrame.bakedDiffuseGi);var actualTau=ReadSceneTarget(giFrame.leafTransmission);float giError=0;
                for(int i=0;i<actualGi.Length;i++)if(actualGi[i].a>.5f)
                {
                    // DecodeDirectionalLightmap: (.5 + dot(n, encoded.xyz-.5)) / encoded.w.
                    // This authored front/back pair is exactly .5 and1.5 times source RGB.
                    var reference=Vector3.Scale(new Vector3(.6f,.4f,.9f),Vector3.one*.5f+Rgb(actualTau[i]));
                    giError=Mathf.Max(giError,Mathf.Abs(actualGi[i].r-reference.x),Mathf.Abs(actualGi[i].g-reference.y),Mathf.Abs(actualGi[i].b-reference.z));
                }
                Check("independent-whole-gi-hemisphere-mixture",giError<=.001f,giError);
                var beforeDecal=ReadSceneTarget(target);
                stage.decals=new[]{new SceneDeferredCamera.Decal{localToWorld=Matrix4x4.TRS(Vector3.zero,Quaternion.identity,new Vector3(3,3,2)),
                    inputs=new SceneDeferredCamera.MaterialInputs{albedo=new Vector3(.65f,.21f,.4f),mos=new Vector3(.2f,.7f,.4f)},albedoWeight=.8f,mosWeight=Vector3.one*.3f}};
                var withDecal=Case("projected-leaf-material",true,1,false);Check("projected-material-changes-current-leaf",PixelError(beforeDecal,withDecal)>.02f);stage.decals=Array.Empty<SceneDeferredCamera.Decal>();
                surface.bakedShadow=new SceneBakedShadowInput{source=SceneBakedShadowSource.Constant,visibility=new Vector4(102/255f,5/7f,2/7f,2/3f),dither=false};
                foreach(var channel in new[]{SceneBakedShadowChannel.R,SceneBakedShadowChannel.G,SceneBakedShadowChannel.B,SceneBakedShadowChannel.A})
                {stage.mainBakedShadowChannel=channel;Case("seven-mrt-baked-main-"+channel);}
                var combined=Render();Check("seven-mrt-distinct-current-contracts",combined.leafTransmission!=combined.bakedShadowMask&&combined.leafTransmission!=combined.bakedDiffuseGi&&combined.bakedShadowMask!=combined.bakedDiffuseGi);
                stage.mainBakedShadowChannel=SceneBakedShadowChannel.R;
                var monitorHost=Own(new GameObject("Leaf HDR monitor camera"));var monitorCamera=monitorHost.AddComponent<Camera>();monitorCamera.enabled=false;
                monitorCamera.orthographic=true;monitorCamera.orthographicSize=.5f;monitorCamera.transform.position=new Vector3(0,0,-2);monitorCamera.cullingMask=1<<22;
                monitorCamera.clearFlags=CameraClearFlags.SolidColor;monitorCamera.backgroundColor=Color.black;
                var monitorQuad=Own(new GameObject("Authored monitor radiance surface"));monitorQuad.layer=22;
                var monitorMesh=Own(Instantiate(source));monitorMesh.colors=Enumerable.Repeat(Color.white,4).ToArray();monitorQuad.AddComponent<MeshFilter>().sharedMesh=monitorMesh;
                var monitorMaterial=Own(new Material(Resources.Load<Shader>("MonitorCanvas")));monitorQuad.AddComponent<MeshRenderer>().sharedMaterial=monitorMaterial;
                var monitor=monitorHost.AddComponent<HdrMonitor>();monitor.monitorEnabled=true;monitor.width=16;monitor.height=16;
                var monitorLight=new SceneDecalLight{position=new Vector3(.13f,.19f,2),range=5,radiance=new Vector3(1.3f,.7f,1.1f),giWeight=.35f,bakedShadowChannel=SceneBakedShadowChannel.G};
                stage.decalLighting.lights=new[]{monitorLight};stage.decalLighting.monitor=monitor;stage.decalLighting.enabled=true;
                Color[] oldMonitor=null;
                foreach(var radiance in new[]{new Vector3(.5f,1.25f,.25f),new Vector3(.25f,.75f,1.5f)})
                {
                    atlasRadiance=radiance;monitorMaterial.SetVector("_Radiance",radiance);monitor.RequestUpdate();
                    if(!monitor.TryUpdate(0,0,out var content))throw new InvalidOperationException(monitor.UnavailableReason);
                    float monitorError=ReadSceneTarget(content.texture).Max(p=>Mathf.Max(Mathf.Abs(p.r-radiance.x),Mathf.Abs(p.g-radiance.y),Mathf.Abs(p.b-radiance.z)));
                    Check("actual-monitor-producer-"+radiance,monitorError<=.00001f,monitorError);
                    var currentMonitor=Case("current-monitor-"+radiance);if(oldMonitor!=null)Check("monitor-update-changes-current-leaf",PixelError(oldMonitor,currentMonitor)>.005f);oldMonitor=currentMonitor;
                }
                foreach(var backend in new[]{SceneDecalLightBackend.Scalar,SceneDecalLightBackend.Instanced})
                {stage.decalLighting.backend=backend;Case("baked-gi-monitor-local-"+backend);}
                stage.decalLighting.enabled=false;stage.decalLighting.monitor=null;atlasRadiance=Vector3.one;
                surface.gi=new SceneGiInput();surface.bakedShadow.source=SceneBakedShadowSource.None;stage.mainBakedShadowChannel=SceneBakedShadowChannel.None;stage.directionalGiWeight=0;
                var caster=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));caster.layer=24;caster.transform.position=Vector3.forward;caster.transform.localScale=Vector3.one*5;
                var main=stage.mainLightShadow;main.origin=new Vector3(0,0,3);main.farPlane=6;main.halfSize=Vector2.one*3;main.resolution=128;main.normalBias=.01f;
                var self=new SceneShadowCaster{renderer=renderer,cull=CullMode.Off,alphaMap=cutoff,uvST=inputs.uvST,cutoff=.5f};
                main.casters=new[]{self};main.enabled=true;Case("backlit-self-shadow-bias");
                main.casters=new[]{self,new SceneShadowCaster{renderer=caster.GetComponent<Renderer>(),cull=CullMode.Off}};
                Case("occluder-blocks-transmission",true,0);stage.screenShadow.enabled=true;Case("screen-shadow-keeps-leaf-visibility",true,0);
                caster.transform.position=new Vector3(6,0,1);Case("moved-caster-restores-transmission");stage.screenShadow.enabled=false;main.enabled=false;
                Check("create-current-wind",VegetationWindDeformer.TryCreate(source,Enumerable.Repeat(new Vector3(1,.17f,.4f),4).ToArray(),VegetationWindBackend.Gpu,false,16,out wind,out var windError));
                if(wind==null)throw new InvalidOperationException(windError);
                var field=new VegetationWindSettings{enabled=true,rootLocal=new Vector3(0,-1,0),height=2,displacementWorld=new Vector3(.13f,0,.18f)};
                if(!wind.TryUpdate(field,.37,Matrix4x4.identity))throw new InvalidOperationException(wind.UnavailableReason);filter.sharedMesh=wind.Mesh;
                Case("current-wind",false);
                surface.gi=leafGi;surface.bakedShadow.source=SceneBakedShadowSource.Constant;stage.mainBakedShadowChannel=SceneBakedShadowChannel.R;
                stage.decalLighting.enabled=true;stage.decalLighting.monitor=monitor;atlasRadiance=new Vector3(.25f,.75f,1.5f);monitorLight.shadow.enabled=true;monitorLight.shadow.normalBias=.01f;
                stage.decalLighting.shadows.tileResolution=128;stage.decalLighting.shadows.casters=new[]{self};main.enabled=true;main.casters=new[]{self};
                Case("combined-current-leaf-and-wind",false,1,false);
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_VEGETATION_LEAF")=="1")
                {
                    FsrCaptureDrain(target);bool began=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try{if(!wind.TryUpdate(field,.74,Matrix4x4.identity))throw new InvalidOperationException(wind.UnavailableReason);Render();FsrCaptureDrain(target);}
                    finally{if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("native-current-wind-seven-mrt-to-light-consumers",began&&ended);
                }
                main.enabled=false;stage.decalLighting.enabled=false;stage.decalLighting.monitor=null;atlasRadiance=Vector3.one;surface.gi=new SceneGiInput();surface.bakedShadow.source=SceneBakedShadowSource.None;stage.mainBakedShadowChannel=SceneBakedShadowChannel.None;
                filter.sharedMesh=source;
                var before=Render();var owned=before.leafTransmission;owned.Release();Check("loss-invalidates-frame",!before.IsCurrent);Check("loss-recreates-attachment",Render().leafTransmission!=owned);
                leaf.enabled=false;Render();Check("disabled-releases-attachment",stage.LeafResourceBytes==0);leaf.enabled=true;
                leaf.absorption.x=float.NaN;camera.Render();Check("invalid-material-rejected",!stage.TryGetFrame(out _)&&stage.LeafResourceBytes==0&&!string.IsNullOrEmpty(stage.UnavailableReason));leaf.absorption.x=2;
                var current=Render();leaf.thicknessMap=current.leafTransmission;camera.Render();Check("owned-read-write-alias-rejected",!stage.TryGetFrame(out _)&&stage.LeafResourceBytes==0);leaf.thicknessMap=thickness;
                Render();Check("borrowed-mesh-material-unchanged",filter.sharedMesh==source&&renderer.sharedMaterial==borrowed&&source.vertexCount==4);
                var beforeResize=Render();var beforeResizeTexture=beforeResize.leafTransmission;
                var larger=Own(new RenderTexture(512,512,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));larger.Create();camera.targetTexture=larger;
                var resized=Render();Check("resize-replaces-current-attachment",!beforeResize.IsCurrent&&!beforeResizeTexture.IsCreated()&&resized.leafTransmission.width==512&&stage.LeafResourceBytes==512*512*8);
                stage.maximumLeafResourceMiB=1;
                camera.Render();Check("aggregate-attachment-budget-before-allocation",!stage.TryGetFrame(out _)&&stage.LeafResourceBytes==0&&!string.IsNullOrEmpty(stage.UnavailableReason));
                camera.targetTexture=target;stage.maximumLeafResourceMiB=128;Render();
                var nonlinear=Own(new Texture2D(2,2,TextureFormat.RGBA32,false,false));leaf.thicknessMap=nonlinear;camera.Render();Check("srgb-thickness-rejected",!stage.TryGetFrame(out _)&&stage.LeafResourceBytes==0);leaf.thicknessMap=thickness;
                leaf.enabled=false;leaf.strength=float.NaN;Render();Check("disabled-invalid-fields-not-active",stage.LeafResourceBytes==0);leaf.strength=.7f;leaf.enabled=true;
                var finalLeaf=Render().leafTransmission;stage.enabled=false;Check("component-disable-releases-leaf",!finalLeaf.IsCreated()&&stage.LeafResourceBytes==0);
                Check("actual-material-cases",cases>=24);
            }
            finally
            {
                RenderTexture.active=active;wind?.Dispose();
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
            }
        }

        private static Vector3 LeafCpuBrdf(Vector3 albedo,Vector3 mos,Vector3 n,Vector3 v,Vector3 l,float diffuse,float specular,float backlight,Vector3 tau)
        {
            // Independent double-precision reflected/transmitted energy accounting.
            var h=(v+l).normalized;double nl=Math.Max(0,Vector3.Dot(n,l)),back=Math.Max(0,-Vector3.Dot(n,l)),nv=Math.Max(0,Vector3.Dot(n,v));
            double nh=Math.Max(0,Vector3.Dot(n,h)),vh=Math.Max(0,Vector3.Dot(v,h)),a2=Math.Pow(Math.Max(1-mos.z,.045),4);
            double denominator=nh*nh*(a2-1)+1,d=a2/Math.Max(Math.PI*denominator*denominator,1e-8);
            double visibility=.5/Math.Max(nl*Math.Sqrt(nv*nv*(1-a2)+a2)+nv*Math.Sqrt(nl*nl*(1-a2)+a2),1e-6);
            var result=Vector3.zero;
            for(int c=0;c<3;c++)
            {
                double f0=.04*(1-mos.x)+albedo[c]*mos.x,f=f0+(1-f0)*Math.Pow(1-vh,5),lambert=albedo[c]*(1-mos.x)/Math.PI;
                double reflected=lambert*(1-tau[c])*diffuse*((1-f)*nl+(1-f0)*backlight*back);
                double transmitted=lambert*tau[c]*diffuse*(1-f0)*back;
                result[c]=(float)(reflected+transmitted+d*visibility*f*specular*nl);
            }
            return result;
        }
        private static Vector3 LeafCpuLocal(SceneDecalLight light,Vector3 albedo,Vector3 mos,Vector3 world,Vector3 n,Vector3 v,Vector3 tau)
        {
            var rotation=light.rotation.normalized;var delta=world-light.position;var source=light.position;
            var x=rotation*Vector3.right;var y=rotation*Vector3.up;var z=rotation*Vector3.forward;float distance;
            if(light.shape==SceneDecalLightShape.Capsule){source+=x*Mathf.Clamp(Vector3.Dot(delta,x),-light.halfLength,light.halfLength);distance=Vector3.Distance(world,source);}
            else if(light.shape==SceneDecalLightShape.Area)
            {
                distance=Vector3.Dot(delta,z);if(distance<=0||distance>=light.range)return Vector3.zero;
                float px=Vector3.Dot(delta,x)/(light.halfSize.x+light.areaSpread.x*distance),py=Vector3.Dot(delta,y)/(light.halfSize.y+light.areaSpread.y*distance);
                if(Mathf.Abs(px)>1||Mathf.Abs(py)>1)return Vector3.zero;source+=x*px*light.halfSize.x+y*py*light.halfSize.y;
            }
            else distance=delta.magnitude;
            double attenuation=Math.Pow(Math.Max(0,1-distance/light.range),light.falloffExponent);
            if(light.shape==SceneDecalLightShape.Spot)
            {
                if(distance<=1e-6f)return Vector3.zero;float cosine=Vector3.Dot(delta/distance,z),inner=Mathf.Cos(light.spotInnerAngle*Mathf.Deg2Rad/2),outer=Mathf.Cos(light.spotOuterAngle*Mathf.Deg2Rad/2);
                if(cosine<outer)return Vector3.zero;if(inner>outer)attenuation*=Math.Max(0,Math.Min(1,(cosine-outer)/(inner-outer)));
            }
            return Vector3.Scale(LeafCpuBrdf(albedo,mos,n,v,(source-world).normalized,light.diffuseScale,light.specularScale,light.backlightScale,tau),light.radiance)*(float)attenuation;
        }
    }
}
