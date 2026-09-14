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
        private IEnumerator VerifySrpMonitor(Report report)
        {
            yield return null;
            var oldGraphics=GraphicsSettings.renderPipelineAsset;var oldQuality=QualitySettings.renderPipeline;
            var oldActive=RenderTexture.active;var frames=new List<TileSceneRenderer.PreparedFrame>();
            SrpHdrMonitor producer=null,viewer=null;var binding=new MonitorEmissionMaterial();
            void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"srp-monitor-"+name,ok,error);
            try
            {
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                Camera Camera(string name,int layer,float aspect,float size)
                {
                    var c=Own(new GameObject(name)).AddComponent<Camera>();c.enabled=false;c.orthographic=true;
                    c.orthographicSize=size;c.aspect=aspect;c.nearClipPlane=.01f;c.farClipPlane=20;
                    c.transform.position=new Vector3(0,0,-2);c.cullingMask=1<<layer;
                    c.clearFlags=CameraClearFlags.SolidColor;c.backgroundColor=Color.clear;c.allowHDR=false;c.allowMSAA=true;
                    return c;
                }
                RenderTexture Target(int w,int h,GraphicsFormat format,string name)
                { var t=Own(new RenderTexture(new RenderTextureDescriptor(w,h,format,0)) { name=name });if(!t.Create())throw new InvalidOperationException(name);return t; }
                var source=Camera("Actual SRP UI source",24,2,.5f);
                var external=Target(16,16,GraphicsFormat.R16G16B16A16_SFloat,"Borrowed camera target");source.targetTexture=external;
                var config=new SrpHdrMonitor.Settings { width=64,height=32 };
                producer=new SrpHdrMonitor(source,config);
                Check("default-disabled",!producer.TryGetFrame(out _)&&producer.NominalColorBytes==0);
                config.enabled=true;
                var canvasHost=Own(new GameObject("Authored animated UI",typeof(RectTransform),typeof(Canvas)));canvasHost.layer=24;
                var canvas=canvasHost.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;canvas.worldCamera=source;
                canvasHost.GetComponent<RectTransform>().sizeDelta=new Vector2(2,1);
                var panels=new RawImage[4];var radiance=new[]{new Vector3(4,.25f,.125f),new Vector3(.125f,3,.25f),new Vector3(.5f,.25f,6),new Vector3(2,4,.5f)};
                for(int i=0;i<4;i++)
                {
                    var go=Own(new GameObject("HDR quadrant "+i,typeof(RectTransform),typeof(CanvasRenderer),typeof(RawImage)));go.layer=24;
                    go.transform.SetParent(canvasHost.transform,false);var r=go.GetComponent<RectTransform>();r.sizeDelta=new Vector2(1,.5f);
                    r.anchoredPosition=new Vector2(i%2==0?-.5f:.5f,i<2?-.25f:.25f);
                    panels[i]=go.GetComponent<RawImage>();panels[i].texture=Texture2D.whiteTexture;
                    panels[i].material=Own(new Material(Resources.Load<Shader>("MonitorCanvas")));
                }
                int prepared=0;float scale=1,opacity=1;
                producer.PrepareCapture+=time=>{
                    prepared++;
                    Canvas.ForceUpdateCanvases();
                    Vector2 size=canvasHost.GetComponent<RectTransform>().rect.size;
                    for(int i=0;i<4;i++)
                    {
                        panels[i].material.SetVector("_Radiance",radiance[i]*scale);panels[i].color=new Color(1,1,1,opacity);
                        panels[i].rectTransform.sizeDelta=size*.5f;
                        panels[i].rectTransform.anchoredPosition=Vector2.Scale(size,new Vector2(i%2==0?-.25f:.25f,i<2?-.25f:.25f));
                    }
                    Canvas.ForceUpdateCanvases();
                };
                var meshHost=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));meshHost.name="Live monitor emissive consumer";meshHost.layer=25;
                meshHost.transform.localScale=new Vector3(2,1,1);var meshRenderer=meshHost.GetComponent<Renderer>();
                var meshCamera=Camera("SRP emission consumer camera",25,2,.5f);
                viewer=new SrpHdrMonitor(meshCamera,new SrpHdrMonitor.Settings { enabled=true,width=64,height=32,updateMode=MonitorUpdateMode.EveryCall });
                var emission=new MonitorEmissionSettings { cull=CullMode.Off };
                var camera=Camera("SRP live light receiver camera",26,64f/48,1);camera.transform.position=Vector3.zero;
                var output=Target(64,48,GraphicsFormat.B10G11R11_UFloatPack32,"Live monitor lit scene");camera.targetTexture=output;
                var normal=Target(64,48,GraphicsFormat.R16G16B16A16_SFloat,"Live monitor receiver normals");
                var floatRead=Target(64,48,GraphicsFormat.R32G32B32A32_SFloat,"Live light float readback");
                var receiver=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));receiver.layer=26;receiver.transform.position=new Vector3(0,0,3);
                receiver.transform.localScale=new Vector3(8,8,1);
                var surface=new SceneDeferredCamera.Surface { renderer=receiver.GetComponent<Renderer>(),cull=CullMode.Off,receiverGroup=7 };
                surface.inputs.albedo=Vector3.one;surface.inputs.mos=new Vector3(0,1,0);
                var light=new SceneDecalLight { position=new Vector3(0,0,.5f),range=6,radiance=new Vector3(2,1,.5f),specularScale=0,
                    halfLength=1,halfSize=new Vector2(1.5f,1.2f),monitorUV=new Vector4(1,1,0,0) };
                var settings=new TileSceneRenderer.Settings { enabled=true,positionLighting=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,
                    surfaces=new[]{surface},output=output,normalIdentity=normal,lightRadiance=Vector3.zero,ambientIrradiance=Vector3.zero,giBaseScale=0,
                    localLightBackend=SceneForwardLightBackend.BruteForce,localLights=new SceneDecalLightSettings { enabled=true,lights=new[]{light},srpMonitor=producer } };
                SrpHdrMonitor.Frame current=default,display=default;
                Color[] Expected()
                {
                    var p=new Color[config.width*config.height];float a=((Color32)new Color(1,1,1,opacity)).a/255f;
                    for(int y=0;y<config.height;y++)for(int x=0;x<config.width;x++)
                    {
                        Vector3 c=radiance[(x<config.width/2?0:1)+(y<config.height/2?0:2)]*scale*a;
                        p[y*config.width+x]=new Color(Mathf.HalfToFloat(Mathf.FloatToHalf(c.x)),Mathf.HalfToFloat(Mathf.FloatToHalf(c.y)),Mathf.HalfToFloat(Mathf.FloatToHalf(c.z)),Mathf.HalfToFloat(Mathf.FloatToHalf(a)));
                    }
                    return p;
                }
                void Save(string name,Color[] pixels,int w,int h)
                {
                    SaveSsrPreview("srp-monitor-"+name,pixels,w,h,false);
                    using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"srp-monitor-"+name+".raw")));
                    foreach(var p in pixels)for(int c=0;c<4;c++)writer.Write(p[c]);
                }
                void Run(string name,double time,ulong revision,bool due)
                {
                    int before=prepared;var previous=current;
                    if(!producer.TryPrepare(time,revision))throw new InvalidOperationException(producer.UnavailableReason);
                    if(!viewer.TryPrepare(time,revision))throw new InvalidOperationException(viewer.UnavailableReason);
                    var request=new TilePassTestRequest { record=context=>{
                        if(!producer.TryRecord(context,out current))throw new InvalidOperationException(producer.UnavailableReason);
                        Check(name+"-schedule",producer.DidRecord==due&&prepared==before+(due?1:0));
                        Check(name+"-source-camera-restored",source.targetTexture==external&&!source.allowHDR&&source.allowMSAA&&!source.enabled);
                        if(!binding.TryBind(current,emission,out var error))throw new InvalidOperationException(error);
                        meshRenderer.sharedMaterial=binding.Material;
                        if(!viewer.TryRecord(context,out display))throw new InvalidOperationException(viewer.UnavailableReason);
                        context.SetupCameraProperties(camera);
                        if(!TileSceneRenderer.TryPrepare(camera,settings,out var frame,out error))throw new InvalidOperationException(error);
                        frames.Add(frame);
                        if(!frame.TryRecord(context,out _,out error))throw new InvalidOperationException(error);
                    }};
                    bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SRP_MONITOR")=="1";
                    string selected=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SRP_MONITOR_CASE");capture&=string.IsNullOrEmpty(selected)||selected==name;
                    bool began=capture&&RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try { RenderPipeline.SubmitRenderRequest(camera,request);if(capture)ReadSceneTarget(output); }
                    finally { if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture(); }
                    if(capture)Check(name+"-capture",began&&ended);
                    var expected=Expected();var actual=ReadSceneTarget(current.texture);var shown=ReadSceneTarget(display.texture);
                    float sourceError=PixelError(actual,expected),emissionError=0,lightError=0;
                    Check(name+"-whole-real-ui-hdr-alpha",sourceError<=.004f,sourceError);
                    for(int y=0;y<32;y++)for(int x=0;x<64;x++)
                    {
                        Vector2 uv=new Vector2((x+.5f)/64,(y+.5f)/32);uv=Vector2.Scale(uv,new Vector2(emission.monitorUV.x,emission.monitorUV.y))+new Vector2(emission.monitorUV.z,emission.monitorUV.w);
                        Color e=SrpMonitorSample(expected,config.width,config.height,uv);e.a=1;
                        emissionError=Mathf.Max(emissionError,PixelError(new[]{shown[y*64+x]},new[]{e}));
                    }
                    Check(name+"-whole-real-emission",emissionError<=.006f,emissionError);
                    var active=RenderTexture.active;Graphics.Blit(output,floatRead);RenderTexture.active=active;
                    var lit=ReadSceneTarget(floatRead);
                    for(int y=0;y<48;y++)for(int x=0;x<64;x++)
                    {
                        Vector3 world=new Vector3(((x+.5f)/64*2-1)*camera.aspect,(y+.5f)/48*2-1,3),low=Vector3.zero,high=Vector3.zero;
                        foreach(var l in settings.localLights.lights)
                        {
                            Vector3 delta=world-l.position;Vector2 uv=new Vector2(l.monitorUV.z,l.monitorUV.w);
                            if(l.shape==SceneDecalLightShape.Capsule)uv+=new Vector2(l.monitorUV.x,l.monitorUV.y)*(Mathf.Clamp(delta.x,-l.halfLength,l.halfLength)/(2*l.halfLength)+.5f);
                            if(l.shape==SceneDecalLightShape.Area)uv+=Vector2.Scale(new Vector2(delta.x/l.halfSize.x*.5f+.5f,delta.y/l.halfSize.y*.5f+.5f),new Vector2(l.monitorUV.x,l.monitorUV.y));
                            Vector3 minimum=Vector3.one*float.PositiveInfinity,maximum=Vector3.one*float.NegativeInfinity;
                            // Bound a full 1/256 texel of bilinear fraction precision, before
                            // the linear-in-radiance BRDF. No pixel exclusion or fitted shader.
                            foreach(float dx in new[]{-1f,0,1})foreach(float dy in new[]{-1f,0,1})
                            {
                                Color sample=SrpMonitorSample(expected,config.width,config.height,uv+new Vector2(dx/(256*config.width),dy/(256*config.height)));
                                Vector3 response=TilePositionCpuLocal(l,world,Vector3.back,Vector3.back,7,false,Vector3.zero,new Vector3(sample.r,sample.g,sample.b));
                                minimum=Vector3.Min(minimum,response);maximum=Vector3.Max(maximum,response);
                            }
                            low+=minimum;high+=maximum;
                        }
                        for(int c=0;c<3;c++)
                        {
                            double arithmetic=high[c]==0?0:2e-5*Math.Max(1,Math.Abs(high[c])),minimum=double.PositiveInfinity,maximum=double.NegativeInfinity;
                            foreach(double value in TileScenePackedNeighbors(low[c]-arithmetic,c))minimum=Math.Min(minimum,value);
                            foreach(double value in TileScenePackedNeighbors(high[c]+arithmetic,c))maximum=Math.Max(maximum,value);
                            double actualChannel=lit[y*64+x][c],error=Math.Max(0,Math.Max(minimum-actualChannel,actualChannel-maximum));
                            lightError=Mathf.Max(lightError,(float)error);
                        }
                    }
                    Check(name+"-whole-independent-live-light",lightError<.004f,lightError);
                    Check(name+"-current-frame-and-stable-publication",current.IsCurrent&&display.IsCurrent&&
                        (previous.texture==null||previous.texture==current.texture)&&(!due||previous.sequence==0||!previous.IsCurrent));
                    Save(name+"-ui",actual,config.width,config.height);Save(name+"-emission",shown,64,32);Save(name+"-light",lit,64,48);
                }
                ulong revision=0;
                foreach(var shape in new[]{SceneDecalLightShape.Point,SceneDecalLightShape.Capsule,SceneDecalLightShape.Area})
                foreach(float alpha in new[]{1f,.5f})foreach(float gain in new[]{1f,2f})
                {
                    light.shape=shape;light.monitorUV=shape==SceneDecalLightShape.Point?new Vector4(0,0,.25f,.75f):new Vector4(1,1,0,0);
                    opacity=alpha;scale=gain;revision++;
                    Run(shape+"-alpha-"+(alpha==1?"one":"half")+"-gain-"+(gain==1?"one":"two"),revision,revision,true);
                }
                Run("unchanged",revision+.1,revision,false);
                producer.RequestUpdate();Run("explicit-request",revision+.2,revision,true);
                Run("backwards",0,revision,true);
                config.updateMode=MonitorUpdateMode.FixedRate;config.updatesPerSecond=10;
                Run("fixed-config",1,revision,true);Run("fixed-skip",1.01,revision,false);Run("fixed-due",1.2,revision,true);
                config.updateMode=MonitorUpdateMode.EveryCall;Run("every-first",2,revision,true);Run("every-second",2,revision,true);
                emission.monitorUV=new Vector4(-1,-1,1,1);Run("emission-flipped-uv",3,revision,true);emission.monitorUV=new Vector4(1,1,0,0);
                settings.localLightBackend=SceneForwardLightBackend.Tiled;
                var many=new SceneDecalLight[110];for(int i=0;i<many.Length;i++)many[i]=new SceneDecalLight { shape=SceneDecalLightShape.Point,
                    position=new Vector3(0,0,.5f),range=6,radiance=new Vector3(.02f,.01f,.005f),specularScale=0,
                    monitorUV=new Vector4(0,0,i%2==0?.25f:.75f,i%3==0?.25f:.75f) };
                settings.localLights.lights=many;Run("110-live-grid-lights",4,++revision,true);
                canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.planeDistance=1;
                Run("screen-space-camera-ui",5,++revision,true);
                Check("owned-two-hdr-target-color-cost",producer.NominalColorBytes==64*32*16);
                bool Attempt(double time)
                {
                    bool ok=false;
                    if(!producer.TryPrepare(time,revision))return false;
                    RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>ok=producer.TryRecord(context,out _) });
                    if(ok&&producer.TryGetFrame(out var frame))ReadSceneTarget(frame.texture);
                    return ok;
                }
                bool recordWithoutTicket=true;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>recordWithoutTicket=producer.TryRecord(context,out _) });
                Check("one-shot-prepared-ticket",!recordWithoutTicket);
                producer.TryPrepare(5,revision);source.backgroundColor=Color.black;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>recordWithoutTicket=producer.TryRecord(context,out _) });
                Check("changed-prepared-camera-rejected",!recordWithoutTicket);source.backgroundColor=Color.clear;
                bool recursiveRejected=false;Action<double> recursive=time=>recursiveRejected=!producer.TryPrepare(time,revision);
                producer.PrepareCapture+=recursive;Check("recursive-prepare-rejected",Attempt(5)&&recursiveRejected);producer.PrepareCapture-=recursive;
                config.updateMode=MonitorUpdateMode.WhenDirty;Check("schedule-config-recovery",Attempt(5));
                producer.RequestUpdate();producer.TryPrepare(5,revision);source.backgroundColor=Color.black;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>recordWithoutTicket=producer.TryRecord(context,out _) });
                source.backgroundColor=Color.clear;
                Check("rejected-ticket-preserves-content-refresh",!recordWithoutTicket&&Attempt(5)&&producer.DidRecord);
                producer.RequestUpdate();int count=prepared;Check("superseded-prepare-first",producer.TryPrepare(5,revision));
                Check("superseded-prepare-keeps-pending-capture",Attempt(5)&&producer.DidRecord&&prepared==count+2);
                Action<double> request=time=>producer.RequestUpdate();producer.PrepareCapture+=request;producer.RequestUpdate();
                Check("request-callback-first",Attempt(5));producer.PrepareCapture-=request;
                Check("request-in-prepare-not-dropped",Attempt(5)&&producer.DidRecord);
                source.enabled=true;Check("enabled-camera-rejected",!Attempt(5)&&source.targetTexture==external);source.enabled=false;
                config.width=0;Check("invalid-size-rejected",!Attempt(5)&&!current.IsCurrent);config.width=64;
                Check("invalid-clock-rejected",!Attempt(double.NaN));
                Action<double> fault=time=>throw new InvalidOperationException("Authored preparation fault");producer.PrepareCapture+=fault;
                Check("preparation-failure-retains-restores",!Attempt(5)&&source.targetTexture==external&&!source.allowHDR&&producer.NominalColorBytes==64*32*16);
                producer.PrepareCapture-=fault;Check("recovers-after-fault",Attempt(5));
                producer.TryGetFrame(out current);
                config.enabled=false;Check("disable-invalidates",!current.IsCurrent&&!Attempt(5));
                Check("stale-emission-clears",!binding.TryBind(current,emission,out _)&&binding.Material.GetTexture("_MonitorTex")==Texture2D.blackTexture);
                config.enabled=true;Check("reenable-records",Attempt(6));producer.TryGetFrame(out current);
                config.width=96;config.height=48;Check("resize-invalidates",!current.IsCurrent&&Attempt(7));
                producer.TryGetFrame(out var resized);ReadSceneTarget(resized.texture);
                Check("resize-owned-not-borrowed",resized.texture!=current.texture&&resized.texture.width==96&&source.targetTexture==external&&external.IsCreated());
                producer.Dispose();Check("disposed-rejects-and-invalidates",!resized.IsCurrent&&!Attempt(8)&&producer.NominalColorBytes==0&&external.IsCreated());
                meshRenderer.sharedMaterial=null;
            }
            finally
            {
                // Readbacks above synchronize every submitted source/consumer before release.
                RenderTexture.active=oldActive;foreach(var f in frames)f.Dispose();binding.Dispose();viewer?.Dispose();producer?.Dispose();
                GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;
                foreach(var o in _owned)if(o is GameObject go)go.SetActive(false);
                foreach(var o in _owned)if(o is Camera c)c.targetTexture=null;
                foreach(var o in _owned)if(o is RenderTexture rt)rt.Release();
            }
        }
        private static Color SrpMonitorSample(Color[] values,int width,int height,Vector2 uv)
        {
            float x=Mathf.Clamp(uv.x*width-.5f,0,width-1),y=Mathf.Clamp(uv.y*height-.5f,0,height-1);
            int x0=(int)Math.Floor(x),y0=(int)Math.Floor(y),x1=Math.Min(x0+1,width-1),y1=Math.Min(y0+1,height-1);
            return Color.LerpUnclamped(Color.LerpUnclamped(values[y0*width+x0],values[y0*width+x1],x-x0),
                Color.LerpUnclamped(values[y1*width+x0],values[y1*width+x1],x-x0),y-y0);
        }
    }
}
