using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyTileDecal(Report report)
        {
            yield return null;
            var oldGraphics=GraphicsSettings.renderPipelineAsset;var oldQuality=QualitySettings.renderPipeline;
            var oldActive=RenderTexture.active;var frames=new List<TileSceneRenderer.PreparedFrame>();
            SrpHdrMonitor monitor=null;ulong monitorRevision=0;
            void Check(string name,bool ok,float value=0)=>FrameworkCheck(report,"tile-decal-"+name,ok,value);
            try
            {
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                var camera=Own(new GameObject("Current material decal camera")).AddComponent<Camera>();camera.enabled=false;
                camera.orthographic=true;camera.orthographicSize=1;camera.aspect=64f/48;camera.nearClipPlane=.1f;camera.farClipPlane=20;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.clear;camera.allowMSAA=false;
                RenderTexture Target(GraphicsFormat format,string name)
                { var t=Own(new RenderTexture(new RenderTextureDescriptor(64,48,format,0)) { name=name });if(!t.Create())throw new InvalidOperationException(name);return t; }
                var output=Target(GraphicsFormat.B10G11R11_UFloatPack32,"Tile decal lit output");camera.targetTexture=output;
                var normal=Target(GraphicsFormat.R16G16B16A16_SFloat,"Tile decal mapped normal and metadata");
                var readback=Target(GraphicsFormat.R32G32B32A32_SFloat,"Tile decal float readback");
                var host=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));host.layer=26;host.transform.position=new Vector3(0,0,3);host.transform.localScale=new Vector3(8,8,1);
                var mesh=Own(UnityEngine.Object.Instantiate(host.GetComponent<MeshFilter>().sharedMesh));mesh.uv2=mesh.uv;
                host.GetComponent<MeshFilter>().sharedMesh=mesh;
                var surface=new SceneDeferredCamera.Surface { renderer=host.GetComponent<Renderer>(),receiverGroup=7,cull=CullMode.Off };
                surface.inputs.albedo=Vector3.one;surface.inputs.mos=new Vector3(0,1,.5f);
                var settings=new TileSceneRenderer.Settings { enabled=true,geometryDepthId=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,
                    output=output,normalIdentity=normal,surfaces=new[]{surface},ambientIrradiance=new Vector3(.4f,.5f,.6f),giBaseScale=0 };
                var decal=new SceneDeferredCamera.Decal { receiverGroup=7,localToWorld=Matrix4x4.TRS(new Vector3(0,0,3),Quaternion.identity,new Vector3(4,3,1)) };
                decal.inputs.albedo=new Vector3(.25f,.5f,.75f);decal.inputs.mos=new Vector3(.75f,.25f,.8f);decal.inputs.emission=new Vector3(4,2,1);
                Texture2D Constant(Color color)
                { var t=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));t.SetPixel(0,0,color);t.Apply();return t; }
                var map=Constant(new Color(.8f,.5f,.9f,1));
                Color Tex(Texture texture,Vector2 uv,Color fallback)
                {
                    if(!(texture is Texture2D t))return fallback;
                    int x=Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(uv.x,1)*t.width),0,t.width-1);
                    int y=Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(uv.y,1)*t.height),0,t.height-1);
                    return t.GetPixel(x,y);
                }
                // Independent ray/quad intersection reference, including equal-depth draw order.
                SceneDeferredCamera.Surface Visible(int x,int y,out Vector3 world,out Vector3 ng,out Vector2 uv)
                {
                    Ray ray=camera.ViewportPointToRay(new Vector3((x+.5f)/64,(y+.5f)/48,0));
                    SceneDeferredCamera.Surface hit=null;float closest=float.PositiveInfinity;
                    world=ng=Vector3.zero;uv=Vector2.zero;
                    foreach(var s in settings.surfaces)
                    {
                        var transform=s.renderer.localToWorldMatrix*Matrix4x4.Scale(s.vertexScale);var inverse=transform.inverse;
                        var origin=inverse.MultiplyPoint(ray.origin);var direction=inverse.MultiplyVector(ray.direction);
                        if(Mathf.Abs(direction.z)<1e-8f)continue;
                        float t=-origin.z/direction.z;var local=origin+direction*t;
                        if(t<0||t>closest||Mathf.Abs(local.x)>.5f||Mathf.Abs(local.y)>.5f)continue;
                        var st=s.inputs.uvST;var candidate=new Vector2((local.x+.5f)*st.x+st.z,(local.y+.5f)*st.y+st.w);
                        if(Tex(s.inputs.albedoMap,candidate,Color.white).a*s.inputs.alpha<s.alphaCutoff)continue;
                        hit=s;closest=t;world=ray.GetPoint(t);ng=inverse.transpose.MultiplyVector(Vector3.back).normalized;uv=candidate;
                    }
                    return hit;
                }
                void Save(string name,Color[] values)
                {
                    SaveSsrPreview("tile-decal-"+name,values,64,48,false);
                    using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"tile-decal-"+name+".raw")));
                    foreach(var v in values)for(int c=0;c<4;c++)writer.Write(v[c]);
                }
                Color[] Run(string name,int activeDecals=0)
                {
                    TileSceneRenderer.PreparedFrame frame=null;
                    void Prepare()
                    {
                        if(!TileSceneRenderer.TryPrepare(camera,settings,out frame,out var error))throw new InvalidOperationException(error);
                        frames.Add(frame);Check(name+"-current-geometry-targets",frame.EyeDepth!=null&&frame.GeometryDepthId!=null&&frame.DepthBudget.colorTileBits==64&&frame.DepthBudget.nominalBytes==64*48*12);
                        Check(name+"-budget",frame.Budget.colorTileBits==256&&frame.Budget.nominalBytes==64*48*(activeDecals>0?32:28)&&frame.Budget.subpasses==3&&
                            frame.Budget.draws==settings.surfaces.Length+2+activeDecals*3);
                    }
                    if(monitor==null)Prepare();
                    else if(!monitor.TryPrepare(++monitorRevision,monitorRevision))throw new InvalidOperationException(monitor.UnavailableReason);
                    var request=new TilePassTestRequest { record=context=>{
                        if(monitor!=null)
                        {
                            if(!monitor.TryRecord(context,out var current))throw new InvalidOperationException(monitor.UnavailableReason);
                            decal.inputs.emissionMap=current.texture;context.SetupCameraProperties(camera);Prepare();
                            Check(name+"-current-srp-monitor",current.IsCurrent&&monitor.DidRecord);
                        }
                        if(!frame.TryRecord(context,out _,out var reason))throw new InvalidOperationException(reason);
                        Check(name+"-one-shot",!frame.TryRecord(context,out _,out _));
                    }};
                    bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_DECAL")=="1";
                    string selected=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_DECAL_CASE");capture&=string.IsNullOrEmpty(selected)||selected==name;
                    bool began=capture&&RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try { RenderPipeline.SubmitRenderRequest(camera,request);if(capture)ReadSceneTarget(output); }
                    finally { if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture(); }
                    if(capture)Check(name+"-capture",began&&ended);
                    var active=RenderTexture.active;Graphics.Blit(output,readback);RenderTexture.active=active;
                    var color=ReadSceneTarget(readback);var normals=ReadSceneTarget(normal);var geometry=ReadSceneTarget(frame.GeometryDepthId);var depth=ReadSceneTarget(frame.EyeDepth);
                    bool finite=true;float depthError=0,geometryError=0,maskError=0,identityError=0;
                    for(int p=0;p<color.Length;p++)
                    {
                        for(int c=0;c<4;c++)finite&=!float.IsNaN(color[p][c])&&!float.IsInfinity(color[p][c]);
                        var hit=Visible(p%64,p/64,out var world,out var ng,out var uv);
                        float expectedDepth=hit==null?0:-camera.worldToCameraMatrix.MultiplyPoint(world).z;
                        float expectedMask=hit!=null&&(settings.reflectionExcludedLayers.value&(1<<hit.renderer.gameObject.layer))==0&&
                            Tex(hit.inputs.mosMap,uv,Color.white).b*hit.inputs.mos.z>=settings.reflectionSmoothnessThreshold?1:0;
                        Vector3 expectedGeometry=hit==null?Vector3.zero:ng*.5f+Vector3.one*.5f;
                        depthError=Mathf.Max(depthError,Mathf.Abs(depth[p].r-expectedDepth));
                        for(int c=0;c<3;c++)geometryError=Mathf.Max(geometryError,Mathf.Abs(geometry[p][c]-expectedGeometry[c]));
                        maskError=Mathf.Max(maskError,Mathf.Abs(geometry[p].a-expectedMask));
                        float identity=hit==null?0:1+hit.receiverGroup+(hit.gi!=null&&hit.gi.source!=SceneGiSource.None?256:0);
                        identityError=Mathf.Max(identityError,Mathf.Abs(normals[p].a-identity));
                    }
                    // Same independent CPU-ray/plane arithmetic budget as the existing position suite.
                    Check(name+"-whole-current-depth",depthError<2e-4f,depthError);
                    // UNorm8 permits either neighbour at the exact .5 tie; never relax the SSR bit.
                    Check(name+"-whole-unmapped-normal",geometryError<=.5f/255+2e-6f,geometryError);
                    Check(name+"-whole-reflection-mask",maskError==0,maskError);
                    Check(name+"-whole-group-gi-preserved",identityError==0,identityError);
                    Check(name+"-finite-hdr",finite);
                    Save(name+"-color",color);Save(name+"-normal",normals);Save(name+"-geometry",geometry);Save(name+"-depth",depth);
                    return color;
                }
                var baseline=Run("geometry-only");
                surface.inputs.normalMap=map;var mapped=Run("mapped-surface");Check("mapped-surface-affects-light",!ScenePixelsEqual(baseline,mapped));
                surface.inputs.normalMap=null;settings.reflectionSmoothnessThreshold=.75f;Run("roughness-mask");
                settings.reflectionSmoothnessThreshold=.5f;settings.reflectionExcludedLayers=1<<26;Run("reflection-layer-mask");settings.reflectionExcludedLayers=0;
                settings.decals=new[]{decal};
                var albedo=Run("albedo",1);Check("albedo-projector-affects-real-light",!ScenePixelsEqual(baseline,albedo));
                decal.albedoWeight=0;decal.emissionWeight=1;var emission=Run("emission",1);
                float delta=0;for(int p=0;p<emission.Length;p++)delta=Mathf.Max(delta,Mathf.Abs(emission[p].r-baseline[p].r-4));
                Check("emission-reaches-output",delta<.08f,delta);
                decal.emissionWeight=0;decal.normalWeight=1;decal.inputs.normalMap=map;Run("normal",1);
                decal.normalWeight=0;decal.inputs.normalMap=null;
                foreach(int channel in new[]{0,1,2}) { decal.mosWeight=Vector3.zero;decal.mosWeight[channel]=1;Check("mos-"+channel+"-affects-light",!ScenePixelsEqual(baseline,Run("mos-"+channel,1))); }
                decal.albedoWeight=.25f;decal.normalWeight=.6f;decal.inputs.normalMap=map;decal.emissionWeight=.75f;decal.mosWeight=new Vector3(.2f,.4f,.8f);decal.inputs.alpha=.5f;
                Run("independent-weights",1);
                decal.receiverGroup=8;Check("mismatched-group-byte-exact",ScenePixelsEqual(baseline,Run("wrong-group",1)));
                decal.receiverGroup=7;decal.minimumFacing=1;Check("geometry-facing-rejects",ScenePixelsEqual(baseline,Run("facing-reject",1)));
                decal.minimumFacing=-1;decal.enabled=false;Check("disabled-decal-byte-exact",ScenePixelsEqual(baseline,Run("disabled")));
                decal.enabled=true;decal.inputs.alpha=1;decal.albedoWeight=0;decal.normalWeight=0;decal.emissionWeight=0;decal.mosWeight=Vector3.zero;
                Check("all-zero-weights-byte-exact",ScenePixelsEqual(baseline,Run("zero-weights")));
                decal.mosWeight=Vector3.up;decal.heightOcclusion=true;decal.height=0;decal.heightFade=1;
                Check("height-no-coverage-byte-exact",ScenePixelsEqual(baseline,Run("height-none",1)));
                decal.height=1;decal.heightMap=Constant(new Color(.75f,.75f,.75f,1));Run("height-partial",1);
                decal.heightMap=null;decal.heightOcclusion=false;decal.mosWeight=Vector3.zero;decal.albedoWeight=.5f;
                var second=new SceneDeferredCamera.Decal { receiverGroup=7,localToWorld=decal.localToWorld,albedoWeight=.5f };
                second.inputs.albedo=new Vector3(.75f,.25f,.125f);settings.decals=new[]{decal,second};
                var order=Run("overlap-ab",2);settings.decals=new[]{second,decal};Check("overlap-order-matters",!ScenePixelsEqual(order,Run("overlap-ba",2)));
                settings.decals=new[]{decal};decal.albedoWeight=1;
                var textured=Own(new Texture2D(2,2,TextureFormat.RGBAFloat,false,true) { filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Repeat });
                textured.SetPixels(new[]{new Color(.25f,.5f,.75f,0),new Color(.75f,.25f,.5f,.5f),new Color(.5f,.75f,.25f,1),Color.white});textured.Apply();
                decal.inputs.albedoMap=textured;decal.inputs.mosMap=textured;decal.inputs.emissionMap=textured;
                decal.mosWeight=new Vector3(.2f,.4f,.8f);decal.normalWeight=.6f;decal.emissionWeight=.75f;
                decal.inputs.uvST=new Vector4(1.2f,.8f,.15f,-.1f);
                Run("textured-uv-alpha",1);
                decal.localToWorld=Matrix4x4.TRS(new Vector3(.1f,-.1f,3),Quaternion.Euler(0,0,27),new Vector3(2.1f,1.3f,.8f));Run("rotated-nonuniform-projector",1);
                host.transform.rotation=Quaternion.Euler(0,19,0);host.transform.localScale=new Vector3(8,5,1.5f);surface.vertexScale=new Vector3(.9f,1.1f,.8f);
                camera.transform.position=new Vector3(.2f,-.15f,.25f);Run("moved-camera-geometry",1);
                surface.inputs.normalMap=map;decal.minimumFacing=.96f;Run("mapped-geometry-facing",1);
                surface.inputs.normalMap=null;decal.minimumFacing=-1;camera.transform.position=Vector3.zero;host.transform.rotation=Quaternion.identity;
                host.transform.localScale=new Vector3(8,8,1);surface.vertexScale=Vector3.one;
                decal.localToWorld=second.localToWorld;surface.inputs.mosMap=textured;Run("textured-reflection-mask",1);surface.inputs.mosMap=null;
                var front=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));front.layer=26;front.transform.position=new Vector3(0,0,3);front.transform.localScale=new Vector3(8,8,1);
                var frontSurface=new SceneDeferredCamera.Surface { renderer=front.GetComponent<Renderer>(),receiverGroup=255,cull=CullMode.Off };
                frontSurface.inputs=surface.inputs;settings.surfaces=new[]{surface,frontSurface};Run("coplanar-wrong-group",1);
                decal.receiverGroup=255;Run("coplanar-group-255",1);
                frontSurface.receiverGroup=0;Run("non-receiver-zero",1);
                frontSurface.receiverGroup=255;front.transform.position=new Vector3(0,0,2.8f);Run("occluded-receiver",1);
                frontSurface.inputs=new SceneDeferredCamera.MaterialInputs { albedoMap=textured,albedo=Vector3.one,mos=new Vector3(0,1,.5f) };
                frontSurface.alphaCutoff=.25f;Run("cutout-visible-groups",1);
                settings.surfaces=new[]{surface};front.SetActive(false);decal.receiverGroup=7;
                surface.gi=new SceneGiInput { source=SceneGiSource.Lightmap,lightmap=Constant(new Color(.5f,1,2,1)) };
                surface.bakedShadow=new SceneBakedShadowInput { source=SceneBakedShadowSource.Constant,visibility=new Vector4(.25f,.5f,.75f,1) };
                settings.mainBakedShadowChannel=SceneBakedShadowChannel.R;settings.giBaseScale=1;Run("gi-shadow-metadata",1);
                settings.positionLighting=true;Run("position-lighting-integration",1);settings.positionLighting=false;
                surface.gi=null;surface.bakedShadow=null;settings.mainBakedShadowChannel=SceneBakedShadowChannel.None;
                settings.giBaseScale=0;settings.lightRadiance=Vector3.zero;settings.ambientIrradiance=Vector3.zero;
                decal.inputs=new SceneDeferredCamera.MaterialInputs { emission=Vector3.one };decal.albedoWeight=decal.normalWeight=0;
                decal.mosWeight=Vector3.zero;decal.emissionWeight=1;
                var source=Own(new GameObject("Decal live UI camera")).AddComponent<Camera>();source.enabled=false;
                source.orthographic=true;source.orthographicSize=1;source.aspect=64f/48;source.nearClipPlane=.1f;source.farClipPlane=20;
                source.transform.position=new Vector3(0,0,-2);source.cullingMask=1<<24;source.clearFlags=CameraClearFlags.SolidColor;source.backgroundColor=Color.clear;
                var canvasHost=Own(new GameObject("Decal live HDR UI",typeof(RectTransform),typeof(Canvas)));canvasHost.layer=24;
                var canvas=canvasHost.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;canvas.worldCamera=source;
                canvasHost.GetComponent<RectTransform>().sizeDelta=new Vector2(64f/24,2);
                var panels=new RawImage[2];float monitorGain=1;
                for(int i=0;i<2;i++)
                {
                    var go=Own(new GameObject("Decal UI panel "+i,typeof(RectTransform),typeof(CanvasRenderer),typeof(RawImage)));go.layer=24;
                    go.transform.SetParent(canvasHost.transform,false);var r=go.GetComponent<RectTransform>();r.sizeDelta=new Vector2(64f/48,2);r.anchoredPosition=new Vector2((i==0?-1:1)*64f/96,0);
                    panels[i]=go.GetComponent<RawImage>();panels[i].texture=Texture2D.whiteTexture;panels[i].material=Own(new Material(Resources.Load<Shader>("MonitorCanvas")));
                }
                monitor=new SrpHdrMonitor(source,new SrpHdrMonitor.Settings { enabled=true,width=64,height=48,updateMode=MonitorUpdateMode.EveryCall });
                monitor.PrepareCapture+=time=>{
                    for(int i=0;i<2;i++)panels[i].material.SetVector("_Radiance",(i==0?new Vector3(4,.5f,1):new Vector3(.25f,2,8))*monitorGain);
                    Canvas.ForceUpdateCanvases();
                };
                var live=Run("live-srp-monitor",1);monitorGain=.5f;var changed=Run("live-srp-monitor-update",1);
                Check("live-ui-revision-reaches-projector",!ScenePixelsEqual(live,changed));
                monitor.Dispose();monitor=null;canvasHost.SetActive(false);decal.inputs.emissionMap=null;
                void Reject(string name) { bool ok=TileSceneRenderer.TryPrepare(camera,settings,out var invalid,out _);Check("reject-"+name,!ok&&invalid==null);invalid?.Dispose(); }
                settings.decals=null;Reject("null-decals");settings.decals=new[]{decal};decal.receiverGroup=0;Reject("group-zero");decal.receiverGroup=7;
                decal.normalWeight=float.NaN;Reject("nan-weight");decal.normalWeight=.6f;
                decal.localToWorld=Matrix4x4.Scale(Vector3.zero);Reject("singular-projector");decal.localToWorld=second.localToWorld;
                decal.inputs.emissionMap=output;Reject("output-feedback");decal.inputs.emissionMap=textured;
                settings.reflectionSmoothnessThreshold=float.NaN;Reject("nan-threshold");settings.reflectionSmoothnessThreshold=.5f;
                settings.maximumAttachmentMiB=0;Reject("attachment-budget");settings.maximumAttachmentMiB=128;
                decal.albedoWeight=decal.normalWeight=decal.emissionWeight=0;decal.mosWeight=new Vector3(-1e-8f,0,0);Reject("tiny-negative-mos-weight");
                decal.mosWeight=new Vector3(1e-8f,0,0);Run("tiny-nonzero-mos-weight",1);
                decal.enabled=false;
                settings.geometryDepthId=false;
                Check("absent-decal-no-auxiliary-allocation",TileSceneRenderer.TryPrepare(camera,settings,out var empty,out _)&&empty.GeometryDepthId==null&&empty.EyeDepth==null&&empty.Budget.nominalBytes==64*48*28);
                if(empty!=null)frames.Add(empty);
            }
            finally
            {
                RenderTexture.active=oldActive;foreach(var frame in frames)frame.Dispose();monitor?.Dispose();
                GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;
                foreach(var o in _owned)if(o is GameObject go) { var camera=go.GetComponent<Camera>();if(camera!=null)camera.targetTexture=null;go.SetActive(false); }
                foreach(var o in _owned)if(o is RenderTexture rt)rt.Release();
            }
        }
    }
}
