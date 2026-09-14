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
        private IEnumerator VerifySrpActorReflection(Report report)
        {
            yield return null;
            var oldGraphics=GraphicsSettings.renderPipelineAsset;var oldQuality=QualitySettings.renderPipeline;var oldActive=RenderTexture.active;
            var scenes=new List<TileSceneRenderer.PreparedFrame>();var preparations=new List<ActorForwardDrawSet.PreparedFrame>();
            SrpActorForward actor=null;SrpTileReflection resolver=null,control=null,polluted=null;
            void Check(string name,bool ok,float value=0)=>FrameworkCheck(report,"srp-actor-reflection-"+name,ok,value);
            try
            {
                const int width=97,height=73;
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                var camera=Own(new GameObject("Full Actor scene-only history camera")).AddComponent<Camera>();camera.enabled=false;camera.allowMSAA=false;
                camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.fieldOfView=55;camera.aspect=(float)width/height;camera.cullingMask=1<<22;
                camera.transform.position=new Vector3(0,2.5f,-4);camera.transform.LookAt(new Vector3(0,.5f,3));
                RenderTexture Target(GraphicsFormat format,GraphicsFormat depth=GraphicsFormat.None)
                { var t=Own(new RenderTexture(new RenderTextureDescriptor(width,height,format,0) { depthStencilFormat=depth }) { filterMode=FilterMode.Point });if(!t.Create())throw new InvalidOperationException("Actor reflection target allocation");return t; }
                var output=Target(GraphicsFormat.B10G11R11_UFloatPack32);camera.targetTexture=output;
                var hardware=Target(GraphicsFormat.None,GraphicsFormat.D32_SFloat_S8_UInt);var normal=Target(GraphicsFormat.R16G16B16A16_SFloat);
                var baseColor=Target(GraphicsFormat.R8G8B8A8_SRGB);var mos=Target(GraphicsFormat.R8G8B8A8_UNorm);
                var readFloat=Target(GraphicsFormat.R32G32B32A32_SFloat);var backup=Target(GraphicsFormat.B10G11R11_UFloatPack32);
                SceneDeferredCamera.Surface Quad(string name,Vector3 position,Vector3 scale,Quaternion rotation,Vector3 emission)
                {
                    var go=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));go.name=name;go.layer=22;go.transform.SetPositionAndRotation(position,rotation);go.transform.localScale=scale;
                    return new SceneDeferredCamera.Surface { renderer=go.GetComponent<Renderer>(),cull=CullMode.Off,
                        inputs=new SceneDeferredCamera.MaterialInputs { albedo=new Vector3(.2f,.4f,.6f),mos=new Vector3(.7f,.8f,.8f),emission=emission } };
                }
                var floor=Quad("History reflective floor",new Vector3(0,0,3),new Vector3(12,12,1),Quaternion.Euler(90,0,0),Vector3.one*.05f);
                var left=Quad("History red wall",new Vector3(-1.5f,2,6),new Vector3(3,4,1),Quaternion.identity,new Vector3(4,.5f,.25f));
                var right=Quad("History green wall",new Vector3(1.5f,2,6),new Vector3(3,4,1),Quaternion.identity,new Vector3(.25f,4,.5f));
                left.inputs.mos.z=right.inputs.mos.z=0;
                var sceneSettings=new TileSceneRenderer.Settings { enabled=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,geometryDepthId=true,
                    output=output,depthStencil=hardware,normalIdentity=normal,materialBase=baseColor,materialMos=mos,surfaces=new[]{floor,left,right},
                    lightRadiance=Vector3.zero,ambientIrradiance=Vector3.zero,background=new Color(.02f,.03f,.04f,1) };
                var cube=Own(new Cubemap(4,TextureFormat.RGBAHalf,true));
                for(int face=0;face<6;face++)for(int mip=0;mip<3;mip++)
                { int size=4>>mip;var values=new Color[size*size];for(int p=0;p<values.Length;p++)values[p]=new Color(1,2,4,1);cube.SetPixels(values,(CubemapFace)face,mip); }cube.Apply(false,false);
                SrpTileReflection Resolver()=>new SrpTileReflection(camera,new SrpTileReflection.Settings { enabled=true,sceneOnlyInput=true,
                    maximumSteps=512,normalDistortion=Vector2.zero,probe=cube,allowComputeFallback=false });
                resolver=Resolver();control=Resolver();polluted=Resolver();actor=new SrpActorForward(camera,new SrpActorForward.Settings { enabled=true });
                var goActor=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));goActor.name="Full Actor excluded from scene reflection history";goActor.layer=22;
                goActor.transform.SetPositionAndRotation(camera.transform.position+camera.transform.forward*4+camera.transform.up*.8f,camera.transform.rotation);goActor.transform.localScale=new Vector3(2.2f,2.2f,1);
                var actorRenderer=goActor.GetComponent<Renderer>();var material=Own(new Material(Resources.Load<Shader>("PhotoModeFallback")));actorRenderer.sharedMaterial=material;
                material.SetFloat("_OutlineEnabled",0);material.SetFloat("_VertexColor",0);
                var inputs=new ActorForwardParameters();inputs.SetFloat("_FaceDebugMode",7);
                var actorSettings=new ActorForwardDrawSet.Settings { renderers=new[]{actorRenderer},parameters=inputs,outlines=false,hairCover=false };
                Color actorColor=new Color(.125f,.25f,4,1);material.SetVector("_Color",actorColor);
                ulong sequence=0;TileSceneRenderer.PreparedFrame lastScene=null;ActorForwardDrawSet.PreparedFrame lastDraws=null;
                SrpActorForward.Frame current=default;SrpTileReflection.Frame reflected=default,reference=default,negative=default;
                void Submit(Action<ScriptableRenderContext> action)
                { Exception failure=null;RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{try{action(context);}catch(Exception e){failure=e;}} });if(failure!=null)throw failure; }
                Color[] Read(RenderTexture target)
                { if(target==output||target==baseColor||target==backup){Graphics.Blit(target,readFloat);return ReadSceneTarget(readFloat);}return ReadSceneTarget(target); }
                void Save(string name,Color[] values)
                { SaveSsrPreview("srp-actor-reflection-"+name,values,width,height,false);using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"srp-actor-reflection-"+name+".raw")));foreach(var p in values)for(int c=0;c<4;c++)writer.Write(p[c]); }
                float Difference(Color[] a,Color[] b)
                { float value=0;for(int p=0;p<a.Length;p++)for(int c=0;c<4;c++)value=Mathf.Max(value,Mathf.Abs(a[p][c]-b[p][c]));return value; }
                int Changed(Color[] a,Color[] b)
                { int count=0;for(int p=0;p<a.Length;p++)if(Mathf.Max(Mathf.Abs(a[p].r-b[p].r),Mathf.Abs(a[p].g-b[p].g),Mathf.Abs(a[p].b-b[p].b))>.02f)count++;return count; }
                Color[] Run(string name,bool warm)
                {
                    if(!ActorForwardDrawSet.TryPrepare(camera,actorSettings,out lastDraws,out var why))throw new InvalidOperationException(why);preparations.Add(lastDraws);
                    if(!TileSceneRenderer.TryPrepare(camera,sceneSettings,out lastScene,out why))throw new InvalidOperationException(why);scenes.Add(lastScene);sequence++;
                    Submit(context=>{if(!lastScene.TryRecord(context,out _,out var error)||!resolver.TryRecord(context,lastScene,sequence,1,out reflected,out error))throw new InvalidOperationException(error);});
                    var sceneTargets=new[]{output,normal,baseColor,mos,lastScene.EyeDepth,lastScene.GeometryDepthId};var before=new List<Color[]>();foreach(var t in sceneTargets)before.Add(Read(t));
                    var resolved=Read(reflected.color);var raw=Read(reflected.rawReflection);var previous=current;
                    Submit(context=>{if(!actor.TryRecord(context,lastScene,lastDraws,sequence,reflected,out current,out var error))throw new InvalidOperationException(error);
                        // The independent history owner records AFTER Actor commands.
                        if(!control.TryRecord(context,lastScene,sequence,1,out reference,out error))throw new InvalidOperationException(error);});
                    var actual=Read(current.color);var depth=Read(current.eyeDepth);var expected=(Color[])resolved.Clone();var expectedDepth=before[4];float depthError=0;int visible=0;bool finite=true;
                    for(int p=0;p<actual.Length;p++)
                    {
                        float z=expectedDepth[p].r;var ray=camera.ViewportPointToRay(new Vector3((p%width+.5f)/width,(p/width+.5f)/height,0));
                        float denominator=Vector3.Dot(ray.direction,goActor.transform.forward);
                        float distance=Vector3.Dot(goActor.transform.position-ray.origin,goActor.transform.forward)/denominator;
                        var point=ray.GetPoint(distance);var local=goActor.transform.InverseTransformPoint(point);float actorZ=-camera.worldToCameraMatrix.MultiplyPoint(point).z;
                        if(distance>0&&Mathf.Abs(local.x)<.5f&&Mathf.Abs(local.y)<.5f&&(z==0||actorZ<=z)){expected[p]=actorColor;z=actorZ;visible++;}
                        depthError=Mathf.Max(depthError,Mathf.Abs(depth[p].r-z));
                        for(int c=0;c<4;c++)finite&=!float.IsNaN(actual[p][c])&&!float.IsInfinity(actual[p][c]);finite&=!float.IsNaN(depth[p].r)&&!float.IsInfinity(depth[p].r);
                    }
                    float colorError=Difference(actual,expected);Check(name+"-whole-color-cpu",colorError<.0001f,colorError);Check(name+"-whole-depth-cpu",depthError<.00002f,depthError);
                    Check(name+"-visible-actor-positive-control",visible>100,visible);Check(name+"-finite",finite);
                    Check(name+"-current-exact-source-ticket",current.IsCurrent&&reflected.IsCurrent&&reference.IsCurrent&&!previous.IsCurrent);
                    Check(name+"-history-state",resolver.UsedHistory==warm&&control.UsedHistory==warm);
                    float sceneError=0;for(int i=0;i<sceneTargets.Length;i++)sceneError=Mathf.Max(sceneError,Difference(before[i],Read(sceneTargets[i])));
                    Check(name+"-all-scene-exports-preserved",sceneError==0,sceneError);
                    float reflectionError=Mathf.Max(Difference(raw,Read(reflected.rawReflection)),Difference(resolved,Read(reflected.color)));
                    Check(name+"-reflection-inputs-preserved",reflectionError==0,reflectionError);
                    float historyError=Mathf.Max(Difference(raw,Read(reference.rawReflection)),Difference(resolved,Read(reference.color)));
                    Check(name+"-independent-after-actor-history-exact",historyError==0,historyError);
                    if(warm){int hits=SsrHitCount(raw);Check(name+"-real-history-ray-hits",hits>30,hits);}
                    Save(name+"-scene",before[0]);Save(name+"-resolved",resolved);Save(name+"-raw",raw);Save(name+"-control-raw",Read(reference.rawReflection));Save(name+"-actor",actual);Save(name+"-depth",depth);
                    return actual;
                }
                Run("cold-blue",false);var blue=Run("warm-blue",true);
                actorColor=new Color(4,.125f,.25f,1);material.SetVector("_Color",actorColor);var red=Run("warm-red",true);
                Check("actor-color-positive-control",Changed(blue,red)>100,Changed(blue,red));
                actorColor=new Color(.125f,.25f,4,1);material.SetVector("_Color",actorColor);var restored=Run("warm-blue-restored",true);
                Check("actor-revisit-exact",Difference(blue,restored)==0,Difference(blue,restored));
                using(var rejector=new SrpActorForward(camera,new SrpActorForward.Settings { enabled=true }))
                {
                    if(!TileSceneRenderer.TryPrepare(camera,sceneSettings,out var foreign,out var why))throw new InvalidOperationException(why);scenes.Add(foreign);
                    Submit(context=>{
                        if(!foreign.TryRecord(context,out _,out var error))throw new InvalidOperationException(error);
                        bool ok=rejector.TryRecord(context,foreign,lastDraws,sequence,reflected,out _,out error);Check("reject-different-scene-same-sequence",!ok&&error.Contains("exact scene and sequence")&&rejector.NominalTextureBytes==0);
                        ok=rejector.TryRecord(context,lastScene,lastDraws,sequence+1,reflected,out _,out error);Check("reject-same-scene-different-sequence",!ok&&error.Contains("exact scene and sequence")&&rejector.NominalTextureBytes==0);
                    });
                }
                // Deliberately violate the scene-only promise in a SEPARATE negative
                // history owner. This proves that the real rays can see Actor pollution.
                Graphics.CopyTexture(output,backup);Graphics.Blit(current.color,output);Save("deliberately-polluted-history-source",Read(output));
                Submit(context=>{if(!polluted.TryRecord(context,lastScene,sequence,1,out negative,out var why))throw new InvalidOperationException(why);});Read(negative.rawReflection);
                Graphics.CopyTexture(backup,output);
                Run("warm-after-pollution-control",true);
                Submit(context=>{if(!polluted.TryRecord(context,lastScene,sequence,1,out negative,out var why))throw new InvalidOperationException(why);});
                var wrongHistory=Read(negative.rawReflection);var correctHistory=Read(reflected.rawReflection);int wrongPixels=Changed(wrongHistory,correctHistory);
                Check("deliberate-actor-history-pollution-positive-control",polluted.UsedHistory&&wrongPixels>20,wrongPixels);Save("polluted-next-frame-raw",wrongHistory);
                resolver.ResetHistory();Check("reflection-history-reset-retires-actor-ticket",!reflected.IsCurrent&&!current.IsCurrent);
                control.ResetHistory();
                var recovered=Run("history-reset-recovers",false);
                Check("history-reset-still-draws-full-actor",Changed(recovered,Read(reflected.color))>100);
                resolver.Dispose();Check("disposed-reflection-owner-retires-actor-ticket",!current.IsCurrent&&output.IsCreated()&&hardware.IsCreated());
            }
            finally
            {
                RenderTexture.active=oldActive;actor?.Dispose();resolver?.Dispose();control?.Dispose();polluted?.Dispose();
                foreach(var p in preparations)p.Dispose();foreach(var s in scenes)s.Dispose();GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;
                foreach(var value in _owned)if(value is GameObject go){var c=go.GetComponent<Camera>();if(c!=null)c.targetTexture=null;}
                foreach(var value in _owned){if(value is RenderTexture t)t.Release();if(value!=null)DestroyImmediate(value);}_owned.Clear();
            }
        }
    }
}
