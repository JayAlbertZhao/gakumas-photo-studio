using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyTileReflection(Report report)
        {
            yield return null;
            var oldGraphics=GraphicsSettings.renderPipelineAsset; var oldQuality=QualitySettings.renderPipeline;
            var oldActive=RenderTexture.active; var frames=new List<TileSceneRenderer.PreparedFrame>();
            SrpTileReflection reflections=null;
            void Check(string name,bool ok,float value=0)=>FrameworkCheck(report,"tile-reflection-"+name,ok,value);
            try
            {
                const int width=97,height=73;
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                GraphicsSettings.renderPipelineAsset=pipeline; QualitySettings.renderPipeline=pipeline;
                var camera=Own(new GameObject("Tile real SSR scene camera")).AddComponent<Camera>(); camera.enabled=false; camera.allowMSAA=false;
                camera.nearClipPlane=.1f; camera.farClipPlane=30; camera.fieldOfView=55; camera.aspect=(float)width/height;
                camera.transform.position=new Vector3(0,2.5f,-4); camera.transform.LookAt(new Vector3(0,.5f,3));
                RenderTexture Target(GraphicsFormat format,string name)
                { var t=Own(new RenderTexture(new RenderTextureDescriptor(width,height,format,0)) { name=name,filterMode=FilterMode.Point });if(!t.Create())throw new InvalidOperationException(name);return t; }
                var output=Target(GraphicsFormat.B10G11R11_UFloatPack32,"Tile SSR scene input"); camera.targetTexture=output;
                var normal=Target(GraphicsFormat.R16G16B16A16_SFloat,"Tile SSR mapped normal");
                var materialBase=Target(GraphicsFormat.R8G8B8A8_SRGB,"Tile SSR stored post-decal base");
                var mos=Target(GraphicsFormat.R8G8B8A8_UNorm,"Tile SSR stored post-decal MOS");
                var floatRead=Target(GraphicsFormat.R32G32B32A32_SFloat,"Tile SSR linear float readback");
                SceneDeferredCamera.Surface Quad(string name,Vector3 position,Vector3 scale,Quaternion rotation,Vector3 emission,int group)
                {
                    var go=Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.name=name; go.layer=22;
                    go.transform.SetPositionAndRotation(position,rotation); go.transform.localScale=scale;
                    return new SceneDeferredCamera.Surface { renderer=go.GetComponent<Renderer>(),receiverGroup=group,cull=CullMode.Off,
                        inputs=new SceneDeferredCamera.MaterialInputs { albedo=new Vector3(.2f,.4f,.6f),mos=new Vector3(.7f,.8f,.8f),emission=emission } };
                }
                var floor=Quad("Tile SSR floor",new Vector3(0,0,3),new Vector3(12,12,1),Quaternion.Euler(90,0,0),Vector3.one*.05f,7);
                var left=Quad("Tile SSR red wall",new Vector3(-1.5f,2,6),new Vector3(3,4,1),Quaternion.identity,new Vector3(4,.5f,.25f),8);
                var right=Quad("Tile SSR green wall",new Vector3(1.5f,2,6),new Vector3(3,4,1),Quaternion.identity,new Vector3(.25f,4,.5f),9);
                left.inputs.mos.z=right.inputs.mos.z=0;
                var actor=Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); actor.name="Not registered: excluded blue actor"; actor.layer=23;
                actor.transform.position=new Vector3(0,.8f,2); actor.transform.localScale=new Vector3(.8f,1.6f,.8f);
                var actorMaterial=Own(new Material(Resources.Load<Shader>("StudioAccent"))); actorMaterial.SetColor("_Color",Color.blue);
                actor.GetComponent<Renderer>().sharedMaterial=actorMaterial;
                var sceneSettings=new TileSceneRenderer.Settings { enabled=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,
                    geometryDepthId=true,output=output,normalIdentity=normal,materialBase=materialBase,materialMos=mos,
                    surfaces=new[]{floor,left,right},lightRadiance=Vector3.zero,ambientIrradiance=Vector3.zero,
                    background=new Color(.02f,.03f,.04f,1) };
                var cube=Own(new Cubemap(4,TextureFormat.RGBAHalf,true));
                for(int face=0;face<6;face++)for(int mip=0;mip<3;mip++)
                { int size=4>>mip;var colors=new Color[size*size];for(int p=0;p<colors.Length;p++)colors[p]=new Color(1,2,4,1);cube.SetPixels(colors,(CubemapFace)face,mip); }
                cube.Apply(false,false);
                var settings=new SrpTileReflection.Settings { sceneOnlyInput=true,maximumSteps=512,probe=cube,normalDistortion=Vector2.zero,allowComputeFallback=false };
                Color probeExpected=new Color(1,2,4,1);
                reflections=new SrpTileReflection(camera,settings);
                ulong sequence=0,revision=1; SrpTileReflection.Frame previous=default,current=default;
                TileSceneRenderer.PreparedFrame lastScene=null;
                Color[] Read(RenderTexture target)
                {
                    if(target==output || target==materialBase)
                    { var active=RenderTexture.active;Graphics.Blit(target,floatRead);RenderTexture.active=active;return ReadSceneTarget(floatRead); }
                    return ReadSceneTarget(target);
                }
                void Save(string name,RenderTexture target)
                {
                    var values=Read(target); SaveSsrPreview("tile-reflection-"+name,values,target.width,target.height,false);
                    using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"tile-reflection-"+name+".raw")));
                    foreach(var value in values)for(int c=0;c<4;c++)writer.Write(value[c]);
                }
                float Error(Color a,Color b) { float e=0;for(int c=0;c<4;c++)e=Mathf.Max(e,Mathf.Abs(a[c]-b[c]));return e; }
                Color[] Run(string name,bool expectHistory,bool enabled=true)
                {
                    if(!TileSceneRenderer.TryPrepare(camera,sceneSettings,out var scene,out var reason))throw new InvalidOperationException(reason);
                    frames.Add(scene); lastScene=scene; sequence++;
                    Check(name+"-prepare-not-recorded",!scene.IsRecorded);
                    var request=new TilePassTestRequest { record=context=>{
                        Check(name+"-reject-unrecorded",!reflections.TryRecord(context,scene,sequence,revision,out _,out _));
                        if(!scene.TryRecord(context,out var budget,out var error))throw new InvalidOperationException(error);
                        Check(name+"-same-tile-budget",budget.colorTileBits==256&&budget.subpasses==3);
                        bool ok=reflections.TryRecord(context,scene,sequence,revision,out current,out error);
                        if(enabled&&!ok)throw new InvalidOperationException(error);
                        Check(name+"-record",ok==enabled);
                        if(enabled)Check(name+"-reject-duplicate",!reflections.TryRecord(context,scene,sequence,revision,out _,out _));
                    }};
                    bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_REFLECTION")=="1";
                    string selected=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_REFLECTION_CASE"); capture&=string.IsNullOrEmpty(selected)||selected==name;
                    bool began=capture&&RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try { RenderPipeline.SubmitRenderRequest(camera,request); if(capture)Read(output); }
                    finally { if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture(); }
                    if(capture)Check(name+"-native-capture",began&&ended);
                    if(!enabled) { Check(name+"-no-allocation",reflections.NominalTextureBytes==0);return Read(output); }
                    Check(name+"-history",reflections.UsedHistory==expectHistory);
                    Check(name+"-current-ticket",current.IsCurrent&&!previous.IsCurrent);previous=current;
                    var baseValues=Read(materialBase);var material=Read(mos);var normals=Read(normal);var geometry=Read(scene.GeometryDepthId);
                    var eye=Read(scene.EyeDepth);var metadata=Read(current.visibility);var response=Read(current.response);
                    var radiance=Read(current.radiance);var color=Read(current.color);var input=Read(output);var reflected=Read(current.reflection);
                    float metadataError=0,responseError=0,compositeError=0,resolveError=0;bool finite=true;
                    for(int p=0;p<color.Length;p++)
                    {
                        bool covered=normals[p].a>.5f;float group=covered?(normals[p].a-1)%256+1:0;
                        var expectedMeta=covered?new Color(geometry[p].a*(material[p].b>=settings.smoothnessThreshold?1:0),material[p].b,group,0):Color.clear;
                        metadataError=Mathf.Max(metadataError,Error(metadata[p],expectedMeta));
                        Color expectedResponse=Color.clear,expectedRadiance=Color.clear;
                        if(covered)
                        {
                            Ray ray=camera.ViewportPointToRay(new Vector3((p%width+.5f)/width,(p/width+.5f)/height,0));
                            var n=new Vector3(normals[p].r,normals[p].g,normals[p].b).normalized;
                            float fresnel=Mathf.Pow(1-Mathf.Clamp01(Vector3.Dot(n,-ray.direction)),5);
                            expectedResponse.a=1;
                            for(int c=0;c<3;c++) { float f0=Mathf.Lerp(.04f,baseValues[p][c],material[p].r);expectedResponse[c]=(f0+(1-f0)*fresnel)*material[p].b*material[p].g*settings.specularScale; }
                            Vector3 ng=new Vector3(geometry[p].r*2-1,geometry[p].g*2-1,geometry[p].b*2-1).normalized;
                            Vector3 delta=camera.worldToCameraMatrix.MultiplyVector(n-ng);
                            float u=(p%width+.5f)/width+delta.x*settings.normalDistortion.x;
                            // The resolve perturbs texture UVs directly; projection Y
                            // conversion has already happened in reconstruction.
                            float v=(p/width+.5f)/height+delta.z*settings.normalDistortion.y;
                            Color reflection=Color.clear;
                            if(u>=0&&u<1&&v>=0&&v<1)
                            { int q=(int)(v*height)*width+(int)(u*width);if(normals[q].a>.5f&&(normals[q].a-1)%256+1==group)reflection=reflected[q]; }
                            Color probe=settings.probe!=null?probeExpected:Color.black;
                            expectedRadiance=Color.Lerp(probe,reflection,reflection.a);expectedRadiance.a=1;
                        }
                        responseError=Mathf.Max(responseError,Error(response[p],expectedResponse));
                        resolveError=Mathf.Max(resolveError,Error(radiance[p],expectedRadiance));
                        Color expected=input[p];for(int c=0;c<3;c++)expected[c]=Mathf.Min(65504,input[p][c]+radiance[p][c]*response[p][c]);
                        compositeError=Mathf.Max(compositeError,Error(color[p],expected));
                        for(int c=0;c<4;c++)finite&=!float.IsNaN(color[p][c])&&!float.IsInfinity(color[p][c]);
                    }
                    Check(name+"-whole-post-decal-metadata",metadataError<.001f,metadataError);
                    Check(name+"-whole-material-fresnel-response",responseError<.002f,responseError);
                    Check(name+"-whole-probe-ssr-distortion-resolve",resolveError<.004f,resolveError);
                    Check(name+"-whole-nonrecursive-composite",compositeError<.008f,compositeError);Check(name+"-finite",finite);
                    if(settings.roughness.enabled)
                    {
                        // CPU world-space ray intersections, not shader inverse-projection math.
                        var positions=new Vector3[color.Length];var ng=new Vector3[color.Length];
                        for(int p=0;p<color.Length;p++)
                        {
                            var ray=camera.ViewportPointToRay(new Vector3((p%width+.5f)/width,(p/width+.5f)/height,0));
                            float origin=camera.worldToCameraMatrix.MultiplyPoint(ray.origin).z;
                            float direction=camera.worldToCameraMatrix.MultiplyVector(ray.direction).z;
                            positions[p]=ray.GetPoint((-eye[p].r-origin)/direction);
                            ng[p]=new Vector3(geometry[p].r*2-1,geometry[p].g*2-1,geometry[p].b*2-1).normalized;
                        }
                        Color[] Filter(Color[] values,bool vertical)
                        {
                            var result=new Color[values.Length];var r=settings.roughness;
                            for(int p=0;p<values.Length;p++)
                            {
                                var m=metadata[p];if(m.r<.5f||m.b<.5f||values[p].a<=1e-5f)continue;
                                float radius=Mathf.Min(12,r.maximumRadiusPixels*height/r.referenceHeight)*(1-m.g)*(1-m.g);
                                if(radius<=1e-5f) { result[p]=values[p];continue; }
                                float total=0,trusted=0;Vector3 sum=Vector3.zero;
                                for(int offset=-12;offset<=12;offset++)
                                {
                                    float weight=Mathf.Max(0,radius+1-Mathf.Abs(offset));if(weight<=0)continue;
                                    int x=p%width+(vertical?0:offset),y=p/width+(vertical?offset:0);
                                    if(x<0||x>=width||y<0||y>=height)continue;int q=y*width+x;var other=metadata[q];
                                    if(other.r<.5f||Mathf.Abs(other.b-m.b)>.25f||Mathf.Abs(other.g-m.g)>r.smoothnessTolerance||Vector3.Dot(ng[p],ng[q])<r.normalThreshold)continue;
                                    var delta=positions[q]-positions[p];if(Mathf.Max(Mathf.Abs(Vector3.Dot(delta,ng[p])),Mathf.Abs(Vector3.Dot(delta,ng[q])))>r.planeTolerance)continue;
                                    float trust=Mathf.Clamp01(values[q].a);total+=weight;trusted+=weight*trust;
                                    sum+=new Vector3(values[q].r,values[q].g,values[q].b)*(weight*trust);
                                }
                                if(trusted<=1e-5f||total<=1e-5f)continue;
                                sum/=trusted;var value=new Color(sum.x,sum.y,sum.z,Mathf.Min(values[p].a,trusted/total));
                                for(int c=0;c<4;c++)value[c]=Mathf.HalfToFloat(Mathf.FloatToHalf(value[c]));result[p]=value;
                            }
                            return result;
                        }
                        var expected=Filter(Filter(Read(current.rawReflection),false),true);float filterError=0;
                        for(int p=0;p<expected.Length;p++)filterError=Mathf.Max(filterError,Error(expected[p],reflected[p]));
                        Check(name+"-whole-two-axis-world-space-filter",filterError<.008f,filterError);
                    }
                    Color[] levelValues=null;int oldW=width,oldH=height;float minError=0;int backgrounds=0;
                    for(int level=0;level<current.DepthLevelCount;level++)
                    {
                        var target=current.GetDepthLevel(level);var values=Read(target);
                        for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)
                        {
                            float expected=scene.FarClip;
                            if(level==0) { float d=eye[y*width+x].r;expected=d>0?Mathf.Min(d,scene.FarClip):scene.FarClip;if(d==0)backgrounds++; }
                            else for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)expected=Mathf.Min(expected,levelValues[Mathf.Min(y*2+dy,oldH-1)*oldW+Mathf.Min(x*2+dx,oldW-1)].r);
                            minError=Mathf.Max(minError,Mathf.Abs(values[y*target.width+x].r-expected));
                        }
                        levelValues=values;oldW=target.width;oldH=target.height;
                        Save(name+"-depth-"+level,target);
                    }
                    Check(name+"-whole-ceil-min-hierarchy",minError==0&&oldW==1&&oldH==1,minError);Check(name+"-real-empty-background",backgrounds>0,backgrounds);
                    foreach(var pair in new[]{("input",output),("base",materialBase),("mos",mos),("normal",normal),("geometry",scene.GeometryDepthId),
                        ("metadata",current.visibility),("raw",current.rawReflection),("filtered",current.reflection),("response",current.response),("radiance",current.radiance),("color",current.color)})Save(name+"-"+pair.Item1,pair.Item2);
                    return Read(current.rawReflection);
                }
                var disabled=Run("disabled",false,false);settings.enabled=true;
                var cold=Run("raster-cold",false);Check("cold-no-ssr",SsrHitCount(cold)==0);
                var warm=Run("raster-warm",true);Check("real-reflected-wall",SsrHitCount(warm)>30,SsrHitCount(warm));
                int compared=0,wrong=0;
                for(int p=0;p<warm.Length;p++)if(warm[p].a>.01f)
                {
                    var ray=camera.ViewportPointToRay(new Vector3((p%width+.5f)/width,(p/width+.5f)/height,0));
                    if(!new Plane(Vector3.up,Vector3.zero).Raycast(ray,out float distance))continue;
                    var point=ray.GetPoint(distance);var direction=Vector3.Reflect(ray.direction,Vector3.up);
                    if(direction.z<=.0001f)continue;var hit=point+direction*((6-point.z)/direction.z);
                    if(hit.y<.2f||hit.y>3.8f||Mathf.Abs(hit.x)<.2f||Mathf.Abs(hit.x)>2.8f)continue;
                    compared++;Color expected=hit.x<0?new Color(4,.5f,.25f,warm[p].a):new Color(.25f,4,.5f,warm[p].a);if(Error(warm[p],expected)>.004f)wrong++;
                }
                Check("analytic-two-color-wall-interior",compared>20&&wrong==0,wrong);
                var repeat=Run("raster-repeat",true);Check("no-recursive-feedback",ScenePixelsEqual(warm,repeat));
                actor.transform.position+=new Vector3(2,1,0);Check("unregistered-actor-excluded",ScenePixelsEqual(warm,Run("actor-moved",true)));
                settings.backend=SceneShaderBackend.Compute;Run("compute-cold",false);
                var compute=Run("compute-warm",true);float cross=0;for(int p=0;p<warm.Length;p++)cross=Mathf.Max(cross,Error(compute[p],warm[p]));Check("raster-compute-real-hit-equivalence",cross<.004f,cross);
                settings.roughness.enabled=true;settings.roughness.referenceHeight=height;settings.roughness.maximumRadiusPixels=12;
                Run("compute-filtered",true);var computeFilter=Read(current.reflection);
                settings.backend=SceneShaderBackend.Raster;Run("filtered-raster-cold",false);Run("raster-filtered",true);
                cross=0;var rasterFilter=Read(current.reflection);for(int p=0;p<warm.Length;p++)cross=Mathf.Max(cross,Error(computeFilter[p],rasterFilter[p]));Check("queued-two-axis-raster-compute-equivalence",cross<.004f,cross);
                settings.roughness.enabled=false;
                settings.useHierarchy=false;Run("level-zero-trace",true);Check("level-zero-real-hits",SsrHitCount(Read(current.rawReflection))>30);settings.useHierarchy=true;
                var map=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));map.SetPixel(0,0,new Color(.8f,.5f,.9f,1));map.Apply();
                floor.inputs.normalMap=map;settings.normalDistortion=new Vector2(.1f,-.1f);
                Check("mapped-normal-does-not-drive-trace",ScenePixelsEqual(warm,Run("mapped-distortion",true)));floor.inputs.normalMap=null;settings.normalDistortion=Vector2.zero;
                var decal=new SceneDeferredCamera.Decal { receiverGroup=7,localToWorld=Matrix4x4.TRS(new Vector3(0,0,3),Quaternion.Euler(90,0,0),new Vector3(20,20,1)),albedoWeight=0,mosWeight=Vector3.forward };
                decal.inputs.mos=new Vector3(.2f,.3f,0);sceneSettings.decals=new[]{decal};
                Check("post-decal-roughness-disables-ssr",SsrHitCount(Run("decal-roughness",true))==0);
                decal.mosWeight=Vector3.right;decal.albedoWeight=1;decal.inputs.albedo=new Vector3(.8f,.2f,.4f);Run("decal-metal-base",true);
                sceneSettings.decals=Array.Empty<SceneDeferredCamera.Decal>();revision++;Run("revision-reset",false);
                settings.probe=null;Run("no-probe",true);settings.probe=cube;
                settings.decodeProbeHdr=true;settings.probeDecode=new Vector4(2,1,0,0);probeExpected=new Color(2,4,8,1);Run("decoded-hdr-probe",true);
                settings.decodeProbeHdr=false;probeExpected=new Color(8,4,2,1);floor.inputs.mos.z=0;
                for(int face=0;face<6;face++)cube.SetPixels(new[]{probeExpected},(CubemapFace)face,2);cube.Apply(false,false);
                Run("rough-probe-mip",true);
                for(int face=0;face<6;face++)cube.SetPixels(new[]{new Color(1,2,4,1)},(CubemapFace)face,2);cube.Apply(false,false);
                probeExpected=new Color(1,2,4,1);floor.inputs.mos.z=.8f;
                sequence++;Run("sequence-gap",false);Run("sequence-rewarm",true);
                var lost=current;current.rawReflection.Release();Check("lost-owned-target-invalidates-ticket",!lost.IsCurrent);Run("lost-target-recovery",false);
                camera.transform.position+=Vector3.right*3;Run("camera-cut",false);camera.transform.position-=Vector3.right*3;
                camera.orthographic=true;camera.orthographicSize=4;Run("orthographic-cold",false);Run("orthographic-warm",true);
                camera.orthographic=false;settings.specularScale=0;Check("zero-response-preserves-input",Run("zero-response",false)!=null&&ScenePixelsEqual(Read(output),Read(current.color)));settings.specularScale=1;
                void Reject(string name,Action change,Action restore)
                {
                    if(!TileSceneRenderer.TryPrepare(camera,sceneSettings,out var candidate,out var reason))throw new InvalidOperationException(reason);
                    frames.Add(candidate);
                    var request=new TilePassTestRequest { record=context=>{
                        if(!candidate.TryRecord(context,out _,out var error))throw new InvalidOperationException(error);
                        change();Check("reject-"+name,!reflections.TryRecord(context,candidate,sequence+1,revision,out _,out _));restore();
                    }};
                    RenderPipeline.SubmitRenderRequest(camera,request);
                }
                Reject("missing-scene-only-contract",()=>settings.sceneOnlyInput=false,()=>settings.sceneOnlyInput=true);
                Reject("invalid-number",()=>settings.thickness=float.NaN,()=>settings.thickness=.12f);
                Reject("texture-budget",()=>settings.maximumMiB=0,()=>settings.maximumMiB=256);
                Reject("camera-changed-after-record",()=>camera.transform.position+=Vector3.right,()=>camera.transform.position-=Vector3.right);
                Reject("viewport-changed-after-record",()=>camera.rect=new Rect(0,0,.5f,1),()=>camera.rect=new Rect(0,0,1,1));
                sceneSettings.materialBase=null;
                Reject("missing-material-export",()=>{},()=>{});sceneSettings.materialBase=materialBase;
                sceneSettings.geometryDepthId=false;
                Reject("missing-geometric-depth",()=>{},()=>{});sceneSettings.geometryDepthId=true;
                Check("source-survives-consumer",output.IsCreated()&&materialBase.IsCreated());
                var ticket=current;reflections.Dispose();Check("disposed-ticket-invalid",!ticket.IsCurrent&&output.IsCreated());
            }
            finally
            {
                RenderTexture.active=oldActive;reflections?.Dispose();foreach(var frame in frames)frame.Dispose();
                GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;
                foreach(var o in _owned)if(o is GameObject go) { var c=go.GetComponent<Camera>();if(c!=null)c.targetTexture=null;go.SetActive(false); }
                foreach(var o in _owned)if(o is RenderTexture t)t.Release();
            }
        }
    }
}
