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
        private IEnumerator VerifySrpActor(Report report)
        {
            yield return null;
            var oldGraphics=GraphicsSettings.renderPipelineAsset;var oldQuality=QualitySettings.renderPipeline;
            var oldActive=RenderTexture.active;var scenes=new List<TileSceneRenderer.PreparedFrame>();
            var preparations=new List<ActorForwardDrawSet.PreparedFrame>();SrpActorForward actor=null;
            void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"srp-actor-"+name,ok,error);
            try
            {
                const int width=97,height=73;
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                var camera=Own(new GameObject("Full Actor integration camera")).AddComponent<Camera>();camera.enabled=false;camera.allowMSAA=false;
                camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.fieldOfView=55;camera.aspect=(float)width/height;
                camera.transform.position=new Vector3(0,0,-3);camera.cullingMask=1<<22;
                RenderTexture Target(GraphicsFormat format,string name)
                { var t=Own(new RenderTexture(new RenderTextureDescriptor(width,height,format,0)) { name=name,filterMode=FilterMode.Point });if(!t.Create())throw new InvalidOperationException(name);return t; }
                var output=Target(GraphicsFormat.B10G11R11_UFloatPack32,"Actor scene-only packed color");camera.targetTexture=output;
                var sceneHardwareDepth=Own(new RenderTexture(new RenderTextureDescriptor(width,height,GraphicsFormat.None,0) { depthStencilFormat=GraphicsFormat.D32_SFloat_S8_UInt }));
                if(!sceneHardwareDepth.Create())throw new InvalidOperationException("Scene hardware depth allocation failed");
                var readFloat=Target(GraphicsFormat.R32G32B32A32_SFloat,"Actor packed color readback");
                GameObject Quad(string name,Vector3 position,Vector3 scale)
                { var go=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));go.name=name;go.layer=22;go.transform.position=position;go.transform.localScale=scale;return go; }
                var back=Quad("Asymmetric far scene",new Vector3(.3f,.25f,1),new Vector3(4,3,1));
                var occluder=Quad("Near scene occluder",new Vector3(-.5f,.3f,-1),new Vector3(.65f,1.1f,1));
                SceneDeferredCamera.Surface Surface(GameObject go,Vector3 emission)=>new SceneDeferredCamera.Surface { renderer=go.GetComponent<Renderer>(),cull=CullMode.Back,
                    inputs=new SceneDeferredCamera.MaterialInputs { albedo=Vector3.zero,mos=new Vector3(0,1,0),emission=emission } };
                var sceneSettings=new TileSceneRenderer.Settings { enabled=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,geometryDepthId=true,
                    output=output,depthStencil=sceneHardwareDepth,surfaces=new[]{Surface(back,new Vector3(.125f,.25f,.5f)),Surface(occluder,new Vector3(.5f,.25f,.125f))},
                    background=new Color(.03125f,.0625f,.125f,1),lightRadiance=Vector3.zero,ambientIrradiance=Vector3.zero };
                var goActor=Quad("Authored full Actor surface",new Vector3(.15f,-.2f,0),new Vector3(1.6f,1.25f,1));
                var renderer=goActor.GetComponent<Renderer>();var material=Own(new Material(Resources.Load<Shader>("PhotoModeFallback")));renderer.sharedMaterial=material;
                var actorColor=new Vector4(.75f,.125f,.25f,1);material.SetVector("_Color",actorColor);material.SetFloat("_VertexColor",0);material.SetFloat("_OutlineEnabled",0);
                var parameters=new ActorForwardParameters();parameters.SetFloat("_FaceDebugMode",7);
                var drawSettings=new ActorForwardDrawSet.Settings { parameters=parameters,outlines=false,hairCover=false };
                Material referenceActor=null;
                drawSettings.configureMaterial=(r,submesh,m)=>{if(m.shader.name=="GakumasPhotoMode/ActorToon")referenceActor=m;};
                var referenceCamera=Own(new GameObject("Independent ordinary Forward camera")).AddComponent<Camera>();referenceCamera.enabled=false;
                var referenceTarget=Own(new RenderTexture(new RenderTextureDescriptor(width,height,GraphicsFormat.R16G16B16A16_SFloat,0) { depthStencilFormat=GraphicsFormat.D32_SFloat_S8_UInt }));
                if(!referenceTarget.Create())throw new InvalidOperationException("Reference target allocation failed");
                foreach(var surface in sceneSettings.surfaces)
                {
                    var m=Own(new Material(Resources.Load<Shader>("PlanarCapture")));m.SetVector("_Color",new Vector4(0,0,0,1));
                    m.SetVector("_Emission",surface.inputs.emission);m.SetVector("_AmbientColor",Vector4.zero);m.SetVector("_LightColor",Vector4.zero);
                    surface.renderer.sharedMaterial=m;
                }
                var settings=new SrpActorForward.Settings { enabled=true };actor=new SrpActorForward(camera,settings);
                ulong sequence=0;SrpActorForward.Frame current=default,last=default;
                Color[] Read(RenderTexture target)
                { if(target==output){var active=RenderTexture.active;Graphics.Blit(target,readFloat);RenderTexture.active=active;return ReadSceneTarget(readFloat);}return ReadSceneTarget(target); }
                void Save(string name,Color[] values)
                { SaveSsrPreview("srp-actor-"+name,values,width,height,false);using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"srp-actor-"+name+".raw")));foreach(var p in values)for(int c=0;c<4;c++)writer.Write(p[c]); }
                float Error(Color a,Color b) { float e=0;for(int c=0;c<4;c++)e=Mathf.Max(e,Mathf.Abs(a[c]-b[c]));return e; }
                void Run(string name,bool includeActor,bool analytic=true,bool ordinaryReference=false)
                {
                    drawSettings.renderers=includeActor?new[]{renderer}:Array.Empty<Renderer>();
                    if(!ActorForwardDrawSet.TryPrepare(camera,drawSettings,out var draws,out var why))throw new InvalidOperationException(why);preparations.Add(draws);
                    if(!TileSceneRenderer.TryPrepare(camera,sceneSettings,out var scene,out why))throw new InvalidOperationException(why);scenes.Add(scene);sequence++;
                    Color[] beforeColor=null,beforeDepth=null;
                    RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                        Check(name+"-unrecorded-rejected",!actor.TryRecord(context,scene,draws,sequence,out _,out _));
                        if(!scene.TryRecord(context,out _,out var error))throw new InvalidOperationException(error);
                    }});
                    beforeColor=Read(output);beforeDepth=Read(scene.EyeDepth);
                    string recordError=null;
                    bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SRP_ACTOR")=="1";
                    string selected=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SRP_ACTOR_CASE");capture&=string.IsNullOrEmpty(selected)||selected==name;
                    bool began=capture&&RenderDocCaptureBridge.BeginOffscreenCapture();
                    try
                    {
                    RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                        if(!actor.TryRecord(context,scene,draws,sequence,out current,out recordError))return;
                        Check(name+"-duplicate-rejected",!actor.TryRecord(context,scene,draws,sequence,out _,out _));
                    }});
                    if(recordError!=null||!current.IsCurrent)throw new InvalidOperationException(recordError??"Missing current Actor recording");
                    Check(name+"-current",current.IsCurrent&&!last.IsCurrent);last=current;
                    Check(name+"-preserves-scene-inputs",ScenePixelsEqual(beforeColor,Read(output))&&ScenePixelsEqual(beforeDepth,Read(scene.EyeDepth)));
                    Check(name+"-formats-budget",current.color.graphicsFormat==GraphicsFormat.R16G16B16A16_SFloat&&current.color.depthStencilFormat==GraphicsFormat.D32_SFloat_S8_UInt&&
                        current.eyeDepth.graphicsFormat==GraphicsFormat.R32_SFloat&&actor.NominalTextureBytes==(long)width*height*20);
                    var color=Read(current.color);var depth=Read(current.eyeDepth);float colorError=0,depthError=0;int covered=0,hidden=0;bool finite=true;
                    for(int p=0;p<color.Length;p++)
                    {
                        var expected=beforeColor[p];float expectedDepth=beforeDepth[p].r;
                        if(includeActor)
                        {
                            var ray=camera.ViewportPointToRay(new Vector3((p%width+.5f)/width,(p/width+.5f)/height,0));
                            var hit=ray.GetPoint((goActor.transform.position.z-ray.origin.z)/ray.direction.z);
                            var local=goActor.transform.InverseTransformPoint(hit);
                            if(Mathf.Abs(local.x)<.5f&&Mathf.Abs(local.y)<.5f)
                            {
                                float actorDepth=-camera.worldToCameraMatrix.MultiplyPoint(hit).z;
                                if(expectedDepth==0||actorDepth<=expectedDepth){expected=actorColor;expectedDepth=actorDepth;covered++;}else hidden++;
                            }
                        }
                        colorError=Mathf.Max(colorError,Error(expected,color[p]));depthError=Mathf.Max(depthError,Mathf.Abs(expectedDepth-depth[p].r));
                        for(int c=0;c<4;c++)finite&=!float.IsNaN(color[p][c])&&!float.IsInfinity(color[p][c]);finite&=!float.IsNaN(depth[p].r)&&!float.IsInfinity(depth[p].r);
                    }
                    if(analytic)Check(name+"-whole-color-cpu",colorError<.0001f,colorError);Check(name+"-whole-eye-depth-cpu",depthError<.00002f,depthError);
                    Check(name+"-finite",finite);if(includeActor)Check(name+"-visible-and-occluded-positive-control",covered>100&&hidden>20,covered);
                    Save(name+"-scene",beforeColor);Save(name+"-scene-depth",beforeDepth);Save(name+"-color",color);Save(name+"-depth",depth);
                    }
                    finally { if(began)Check(name+"-native-capture",RenderDocCaptureBridge.EndOffscreenCapture()); }
                    if(ordinaryReference)
                    {
                        // Ordinary Built-in camera draws own geometry/depth/opaque/alpha
                        // ordering. It does not use the SRP seed, drawset or export commands.
                        // Shared full shader math is intentional: this tests host integration.
                        var sourceMaterial=renderer.sharedMaterial;
                        try
                        {
                            GraphicsSettings.renderPipelineAsset=null;QualitySettings.renderPipeline=null;
                            referenceCamera.CopyFrom(camera);referenceCamera.enabled=false;referenceCamera.renderingPath=RenderingPath.Forward;
                            referenceCamera.transform.SetPositionAndRotation(camera.transform.position,camera.transform.rotation);
                            referenceCamera.targetTexture=referenceTarget;referenceCamera.clearFlags=CameraClearFlags.Nothing;referenceCamera.allowHDR=true;
                            // Literal linear clear avoids Camera.backgroundColor's gamma
                            // roundtrip, whose Half quantization otherwise differs by 1 ULP.
                            using var clear=new CommandBuffer { name="Reference literal linear camera clear" };
                            clear.SetRenderTarget(referenceTarget);clear.ClearRenderTarget(true,true,sceneSettings.background);
                            referenceCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,clear);
                            try { renderer.sharedMaterial=referenceActor;referenceCamera.Render(); }
                            finally { referenceCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,clear); }
                            var expected=Read(referenceTarget);var actual=Read(current.color);float difference=0;int actorPixels=0;
                            for(int p=0;p<expected.Length;p++){difference=Mathf.Max(difference,Error(expected[p],actual[p]));if(Error(actual[p],beforeColor[p])>.01f)actorPixels++;}
                            Check(name+"-ordinary-forward-whole-color",difference<.00001f,difference);Check(name+"-ordinary-forward-positive-control",actorPixels>100,actorPixels);
                            Save(name+"-ordinary-forward",expected);
                        }
                        finally { renderer.sharedMaterial=sourceMaterial;GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline; }
                    }
                }
                Run("perspective-empty",false);Run("perspective-opaque",true,true,true);
                camera.orthographic=true;camera.orthographicSize=1.8f;
                Run("orthographic-empty",false);Run("orthographic-opaque",true);
                camera.orthographic=false;
                parameters.SetFloat("_FaceDebugMode",0);material.SetFloat("_DisableDefMap",1);material.SetVector("_DefValue",new Vector4(.5f,.4f,0,0));
                foreach(int type in new[]{0,1,2,3,4,5,6,8,9})
                { material.SetFloat("_ShaderType",type);Run("full-type-"+type,true,false,true); }
                actor.Dispose();Check("dispose-retires-ticket",!current.IsCurrent&&actor.NominalTextureBytes==0);
            }
            finally
            {
                RenderTexture.active=oldActive;actor?.Dispose();foreach(var p in preparations)p.Dispose();foreach(var s in scenes)s.Dispose();
                GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;
                foreach(var value in _owned){if(value is RenderTexture t)t.Release();if(value!=null)DestroyImmediate(value);}_owned.Clear();
            }
            yield return null;
            var layers=VerifySrpActorLayers(report);
            try { while(layers.MoveNext())yield return layers.Current; }
            finally { (layers as IDisposable)?.Dispose(); }
            var guards=VerifySrpActorGuards(report);
            try { while(guards.MoveNext())yield return guards.Current; }
            finally { (guards as IDisposable)?.Dispose(); }
            var reflections=VerifySrpActorReflection(report);
            try { while(reflections.MoveNext())yield return reflections.Current; }
            finally { (reflections as IDisposable)?.Dispose(); }
        }
    }
}
