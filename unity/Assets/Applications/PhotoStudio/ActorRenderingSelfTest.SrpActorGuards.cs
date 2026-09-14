using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifySrpActorGuards(Report report)
        {
            yield return null;
            var oldGraphics=GraphicsSettings.renderPipelineAsset;var oldQuality=QualitySettings.renderPipeline;
            var oldActive=RenderTexture.active;var oldKey=Shader.GetGlobalVector("_ActorKeyColor");var oldDebug=Shader.GetGlobalFloat("_FaceDebugMode");
            var preparations=new List<ActorForwardDrawSet.PreparedFrame>();var scenes=new List<TileSceneRenderer.PreparedFrame>();SrpActorForward actor=null;
            void Check(string name,bool ok,float value=0)=>FrameworkCheck(report,"srp-actor-guard-"+name,ok,value);
            try
            {
                const int width=97,height=73;
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                var camera=Own(new GameObject("Actor guard camera")).AddComponent<Camera>();camera.enabled=false;camera.allowMSAA=false;
                camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.aspect=(float)width/height;camera.transform.position=new Vector3(0,0,-3);camera.cullingMask=1<<22;
                RenderTexture Target(GraphicsFormat format,GraphicsFormat depth=GraphicsFormat.None,bool create=true)
                { var t=Own(new RenderTexture(new RenderTextureDescriptor(width,height,format,0) { depthStencilFormat=depth }));if(create&&!t.Create())throw new InvalidOperationException("Guard texture allocation");return t; }
                var output=Target(GraphicsFormat.B10G11R11_UFloatPack32);var hardware=Target(GraphicsFormat.None,GraphicsFormat.D32_SFloat_S8_UInt);camera.targetTexture=output;
                var alternate=Target(GraphicsFormat.B10G11R11_UFloatPack32);
                var go=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));go.name="Actor guard geometry";go.layer=22;
                var renderer=go.GetComponent<Renderer>();var filter=go.GetComponent<MeshFilter>();var originalMesh=filter.sharedMesh;
                var material=Own(new Material(Resources.Load<Shader>("PhotoModeFallback")));renderer.sharedMaterial=material;
                material.SetFloat("_OutlineEnabled",0);material.SetFloat("_VertexColor",0);material.SetVector("_Color",new Vector4(.75f,.125f,.25f,1));
                var parameters=new ActorForwardParameters();parameters.SetFloat("_FaceDebugMode",7);
                var settings=new ActorForwardDrawSet.Settings { renderers=new[]{renderer},parameters=parameters,outlines=false,hairCover=false };
                var sceneSettings=new TileSceneRenderer.Settings { enabled=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,
                    output=output,depthStencil=hardware,geometryDepthId=true,background=new Color(.03125f,.0625f,.125f,1) };
                var background=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));background.name="Actor guard scene-only background";background.layer=22;
                background.transform.position=new Vector3(.3f,.2f,1);background.transform.localScale=new Vector3(4,3,1);
                sceneSettings.surfaces=new[]{new SceneDeferredCamera.Surface { renderer=background.GetComponent<Renderer>(),
                    inputs=new SceneDeferredCamera.MaterialInputs { albedo=Vector3.zero,mos=new Vector3(0,1,0),emission=new Vector3(.125f,.25f,.5f) } }};
                sceneSettings.lightRadiance=sceneSettings.ambientIrradiance=Vector3.zero;
                ActorForwardDrawSet.PreparedFrame Prepare()
                { if(!ActorForwardDrawSet.TryPrepare(camera,settings,out var p,out var why))throw new InvalidOperationException(why);preparations.Add(p);return p; }
                void RejectPrepare(string name,Action change,Action restore,string reason)
                {
                    change();ActorForwardDrawSet.PreparedFrame rejected=null;
                    try { bool ok=ActorForwardDrawSet.TryPrepare(camera,settings,out rejected,out var why);Check("reject-"+name,!ok&&rejected==null&&!string.IsNullOrEmpty(why)&&why.Contains(reason)); }
                    finally { rejected?.Dispose();restore(); }
                }
                void Invalidates(string name,Action change,Action restore)
                {
                    using var p=Prepare();Check(name+"-starts-valid",p.IsValid);
                    change();try { Check(name+"-invalidates",!p.IsValid); }finally{restore();}
                    Check(name+"-restores",p.IsValid);
                }
                void Throws(string name,Action action)
                { bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check("parameter-reject-"+name,rejected); }
                Throws("unknown-float",()=>parameters.SetFloat("_NotAnActorInput",1));
                Throws("null-vector",()=>parameters.SetVector(null,Vector4.one));
                Throws("nan-float",()=>parameters.SetFloat("_FaceDebugMode",float.NaN));
                Throws("infinite-vector",()=>parameters.SetVector("_ActorKeyColor",new Vector4(float.PositiveInfinity,0,0,0)));
                Throws("oversized-float",()=>parameters.SetFloat("_FaceDebugMode",1e13f));
                Throws("fractional-decal-count",()=>parameters.SetFloat("_FaceDecalCount",1.5f));
                Throws("excess-decal-count",()=>parameters.SetFloat("_FaceDecalCount",9));
                Throws("negative-light-count",()=>parameters.SetAdditionalLightCount(-1));
                Throws("excess-light-count",()=>parameters.SetAdditionalLightCount(9));
                Throws("wrong-texture-dimension",()=>parameters.SetTexture("_ActorEnvironmentCube",Texture2D.whiteTexture));
                Throws("excess-vector-array",()=>parameters.SetVectorArray("_ActorAdditionalColors",new Vector4[9]));
                Throws("nonfinite-vector-array",()=>parameters.SetVectorArray("_ActorAdditionalColors",new[]{new Vector4(float.NaN,0,0,0)}));
                Throws("excess-matrix-array",()=>parameters.SetFaceDecalMatrices(new Matrix4x4[9]));
                var badMatrix=Matrix4x4.identity;badMatrix.m23=float.NaN;
                Throws("nonfinite-shadow-matrix",()=>parameters.SetShadowMatrix(badMatrix));
                Throws("nonfinite-decal-matrix",()=>parameters.SetFaceDecalMatrices(new[]{badMatrix}));
                var badProbe=new SphericalHarmonicsL2();badProbe[2,8]=float.NaN;
                Throws("nonfinite-ambient-probe",()=>parameters.SetAmbientProbe(badProbe));
                Throws("null-ambient-material",()=>ActorForwardParameters.BindAmbientProbe(null,new SphericalHarmonicsL2()));
                var probeMaterial=Own(new Material(material));var probeSentinel=new Vector4(.2f,.3f,.4f,.5f);
                probeMaterial.SetVector("_ActorForwardSH0",probeSentinel);probeMaterial.SetFloat("_UseActorForwardAmbientSH",0);
                Throws("nonfinite-material-probe",()=>ActorForwardParameters.BindAmbientProbe(probeMaterial,badProbe));
                Check("invalid-probe-leaves-material-unchanged",probeMaterial.GetVector("_ActorForwardSH0")==probeSentinel&&probeMaterial.GetFloat("_UseActorForwardAmbientSH")==0);
                Material neutralProbeMaterial=null;settings.configureMaterial=(r,i,m)=>neutralProbeMaterial=m;
                using(var p=Prepare())Check("invalid-probe-leaves-parameters-neutral",neutralProbeMaterial.GetVector("_ActorForwardSH0")==Vector4.zero&&neutralProbeMaterial.GetVector("_ActorForwardSH6")==Vector4.zero&&neutralProbeMaterial.GetFloat("_UseActorForwardAmbientSH")==1);
                settings.configureMaterial=null;
                var arrayNames=new[]{"_ActorEnvironmentArray","_ActorEyeEnvironmentArray"};var savedTextures=Array.ConvertAll(arrayNames,Shader.GetGlobalTexture);
                var arrayFlags=new[]{"_UseCapturedActorEnvironmentArray","_UseCapturedType1ActorEnvironmentArray","_UseCapturedEyeEnvironmentArray"};var savedFlags=Array.ConvertAll(arrayFlags,Shader.GetGlobalFloat);
                try
                {
                    foreach(var flag in arrayFlags)Shader.SetGlobalFloat(flag,0);foreach(var name in arrayNames)Shader.SetGlobalTexture(name,Texture2D.blackTexture);
                    var legacy=ActorForwardParameters.CaptureCurrentGlobals();Material legacyMaterial=null;settings.parameters=legacy;settings.configureMaterial=(r,i,m)=>legacyMaterial=m;
                    using(var p=Prepare())Check("inactive-legacy-array-sentinel-normalized",legacyMaterial.GetTexture(arrayNames[0])==null&&legacyMaterial.GetTexture(arrayNames[1])==null&&Shader.GetGlobalTexture(arrayNames[0])==Texture2D.blackTexture);
                    foreach(var flag in arrayFlags){Shader.SetGlobalFloat(flag,1);Throws("active-legacy-array-"+flag,()=>ActorForwardParameters.CaptureCurrentGlobals());Shader.SetGlobalFloat(flag,0);}
                    Shader.SetGlobalTexture(arrayNames[0],Texture2D.whiteTexture);Throws("inactive-nonsentinel-array",()=>ActorForwardParameters.CaptureCurrentGlobals());
                }
                finally
                {
                    for(int i=0;i<arrayNames.Length;i++)Shader.SetGlobalTexture(arrayNames[i],savedTextures[i]);for(int i=0;i<arrayFlags.Length;i++)Shader.SetGlobalFloat(arrayFlags[i],savedFlags[i]);
                    settings.parameters=parameters;settings.configureMaterial=null;
                }
                RejectPrepare("null-renderer",()=>settings.renderers=new Renderer[]{null},()=>settings.renderers=new[]{renderer},"Null actor renderer");
                RejectPrepare("duplicate-renderer",()=>settings.renderers=new[]{renderer,renderer},()=>settings.renderers=new[]{renderer},"Duplicate actor renderer");
                RejectPrepare("zero-draw-budget",()=>settings.maximumDraws=0,()=>settings.maximumDraws=1024,"Invalid Actor Forward inputs");
                RejectPrepare("null-parameters",()=>settings.parameters=null,()=>settings.parameters=parameters,"Invalid Actor Forward inputs");
                RejectPrepare("null-material",()=>renderer.sharedMaterial=null,()=>renderer.sharedMaterial=material,"Requires full toolkit ActorToon");
                var wrong=Own(new Material(Resources.Load<Shader>("PlanarCapture")));
                RejectPrepare("reduced-planar-material",()=>renderer.sharedMaterial=wrong,()=>renderer.sharedMaterial=material,"Requires full toolkit ActorToon");
                RejectPrepare("unsupported-type",()=>material.SetFloat("_ShaderType",7),()=>material.SetFloat("_ShaderType",0),"Unsupported actor type");
                var block=new MaterialPropertyBlock();block.SetFloat("_ShaderType",float.NaN);
                RejectPrepare("invalid-block-type",()=>renderer.SetPropertyBlock(block),()=>renderer.SetPropertyBlock(null),"Unsupported actor type");
                RejectPrepare("missing-mesh",()=>filter.sharedMesh=null,()=>filter.sharedMesh=originalMesh,"Invalid actor geometry");
                RejectPrepare("missing-submesh",()=>renderer.sharedMaterials=new[]{material,material},()=>renderer.sharedMaterials=new[]{material},"Invalid actor geometry");
                var line=Own(new GameObject("Unsupported actor renderer")).AddComponent<LineRenderer>();line.gameObject.layer=22;line.sharedMaterial=material;
                RejectPrepare("unsupported-renderer",()=>settings.renderers=new Renderer[]{line},()=>settings.renderers=new[]{renderer},"Invalid actor geometry");
                int callbacks=0;
                RejectPrepare("budget-before-clones",()=>{settings.outlines=true;material.SetFloat("_OutlineEnabled",1);settings.maximumDraws=1;settings.configureMaterial=(r,i,m)=>callbacks++;},
                    ()=>{settings.outlines=false;material.SetFloat("_OutlineEnabled",0);settings.maximumDraws=1024;settings.configureMaterial=null;},"draw budget exceeded");
                Check("excess-draw-budget-never-invokes-callback",callbacks==0,callbacks);
                RejectPrepare("callback-queue",()=>settings.configureMaterial=(r,i,m)=>m.renderQueue=3000,()=>settings.configureMaterial=null,"changed shader or queue");
                RejectPrepare("callback-shader",()=>settings.configureMaterial=(r,i,m)=>m.shader=wrong.shader,()=>settings.configureMaterial=null,"changed shader or queue");
                RejectPrepare("callback-zwrite",()=>settings.configureMaterial=(r,i,m)=>m.SetFloat("_ZWrite",0),()=>settings.configureMaterial=null,"changed fixed input");
                RejectPrepare("callback-transform",()=>settings.configureMaterial=(r,i,m)=>r.transform.position=Vector3.right,
                    ()=>{settings.configureMaterial=null;go.transform.position=Vector3.zero;},"inputs changed during preparation");
                var later=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));later.name="Later callback input";later.layer=22;var laterRenderer=later.GetComponent<Renderer>();laterRenderer.sharedMaterial=material;
                RejectPrepare("callback-later-renderer",()=>{settings.renderers=new[]{renderer,laterRenderer};settings.configureMaterial=(r,i,m)=>later.transform.position=Vector3.right;},
                    ()=>{settings.renderers=new[]{renderer};settings.configureMaterial=null;later.transform.position=Vector3.zero;},"inputs changed during preparation");
                later.SetActive(false);
                RejectPrepare("callback-exception",()=>settings.configureMaterial=(r,i,m)=>throw new ArgumentException("deliberate callback rejection"),()=>settings.configureMaterial=null,"deliberate callback rejection");
                var missing=Target(GraphicsFormat.R8G8B8A8_UNorm,create:false);
                RejectPrepare("uncreated-material-texture",()=>material.SetTexture("_MainTex",missing),()=>material.SetTexture("_MainTex",null),"stored and sampleable");
                var sampled=Target(GraphicsFormat.R8G8B8A8_UNorm);
                material.SetTexture("_MainTex",sampled);using(var p=Prepare()){sampled.Release();Check("released-sampled-texture-invalidates",!p.IsValid);sampled.Create();}material.SetTexture("_MainTex",null);
                Invalidates("camera-culling-mask",()=>camera.cullingMask=0,()=>camera.cullingMask=1<<22);
                Invalidates("camera-target",()=>camera.targetTexture=alternate,()=>camera.targetTexture=output);
                Invalidates("camera-viewport",()=>camera.rect=new Rect(0,0,.5f,1),()=>camera.rect=new Rect(0,0,1,1));
                Invalidates("camera-projection",()=>camera.fieldOfView=50,()=>camera.fieldOfView=60);
                Invalidates("camera-transform",()=>camera.transform.position+=Vector3.right,()=>camera.transform.position-=Vector3.right);
                using(var p=Prepare())
                {
                    camera.allowDynamicResolution=true;
                    // Some backends reject this camera flag. Record that exclusion;
                    // a no-op setter cannot exercise the changed-camera rejection.
                    bool supported=camera.allowDynamicResolution;
                    Check(supported?"camera-dynamic-resolution-invalidates":"camera-dynamic-resolution-flag-unavailable",supported?!p.IsValid:p.IsValid,supported?1:0);
                    camera.allowDynamicResolution=false;Check("camera-dynamic-resolution-restores",p.IsValid);
                }
                Invalidates("renderer-transform",()=>go.transform.position=Vector3.right,()=>go.transform.position=Vector3.zero);
                Invalidates("renderer-enabled",()=>renderer.enabled=false,()=>renderer.enabled=true);
                Invalidates("renderer-force-off",()=>renderer.forceRenderingOff=true,()=>renderer.forceRenderingOff=false);
                Invalidates("renderer-active",()=>go.SetActive(false),()=>go.SetActive(true));
                Invalidates("renderer-layer",()=>go.layer=21,()=>go.layer=22);
                var replacement=Own(Instantiate(originalMesh));
                Invalidates("mesh-replacement",()=>filter.sharedMesh=replacement,()=>filter.sharedMesh=originalMesh);
                renderer.enabled=false;using(var p=Prepare()){Check("disabled-renderer-excluded",p.IsValid&&p.DrawCount==0);renderer.enabled=true;Check("excluded-renderer-reactivation-invalidates",!p.IsValid);}
                go.layer=21;using(var p=Prepare()){Check("layer-excluded-renderer-empty",p.IsValid&&p.DrawCount==0);go.layer=22;Check("excluded-layer-change-invalidates",!p.IsValid);}
                using(var p=Prepare()){p.Dispose();p.Dispose();Check("drawset-dispose-idempotent",!p.IsValid&&p.DrawCount==0&&p.MaterialCount==0&&material!=null&&originalMesh!=null&&output.IsCreated());}

                Material snapshot=null;var sentinel=new Vector4(.0625f,.125f,.25f,.5f);Shader.SetGlobalVector("_ActorKeyColor",sentinel);
                parameters.SetVector("_ActorKeyColor",new Vector4(.25f,.5f,.75f,1));
                var array=new[]{new Vector4(1,2,3,4)};parameters.SetVectorArray("_ActorAdditionalColors",array);array[0]=Vector4.zero;
                var matrices=new[]{Matrix4x4.Translate(Vector3.right)};parameters.SetFaceDecalMatrices(matrices);matrices[0]=Matrix4x4.zero;
                settings.configureMaterial=(r,i,m)=>snapshot=m;
                using(var p=Prepare())
                {
                    Check("explicit-input-overrides-global-without-writing",snapshot.GetVector("_ActorKeyColor")==new Vector4(.25f,.5f,.75f,1)&&Shader.GetGlobalVector("_ActorKeyColor")==sentinel);
                    var captured=snapshot.GetVectorArray("_ActorAdditionalColors");var capturedMatrices=snapshot.GetMatrixArray("_FaceDecalWorldToDecal");
                    Check("array-inputs-copied-and-padded",captured.Length==8&&captured[0]==new Vector4(1,2,3,4)&&captured[7]==Vector4.zero&&capturedMatrices.Length==8&&capturedMatrices[0]==Matrix4x4.Translate(Vector3.right)&&capturedMatrices[7]==Matrix4x4.zero);
                    parameters.SetVector("_ActorKeyColor",Vector4.one);Shader.SetGlobalVector("_ActorKeyColor",Vector4.zero);
                    Check("prepared-material-isolates-later-input-and-global-change",snapshot.GetVector("_ActorKeyColor")==new Vector4(.25f,.5f,.75f,1)&&p.IsValid);
                    Check("source-material-not-replaced",renderer.sharedMaterial==material&&snapshot!=material&&snapshot.GetFloat("_ZWrite")==material.GetFloat("_ZWrite"));
                }
                settings.configureMaterial=null;parameters.SetVectorArray("_ActorAdditionalColors",Array.Empty<Vector4>());parameters.SetFaceDecalMatrices(Array.Empty<Matrix4x4>());

                var config=new SrpActorForward.Settings { enabled=true };actor=new SrpActorForward(camera,config);var draws=Prepare();
                TileSceneRenderer.PreparedFrame Scene()
                { if(!TileSceneRenderer.TryPrepare(camera,sceneSettings,out var s,out var why))throw new InvalidOperationException(why);scenes.Add(s);return s; }
                void Submit(Action<ScriptableRenderContext> action)
                { Exception failure=null;RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{try{action(context);}catch(Exception e){failure=e;}} });if(failure!=null)throw failure; }
                var first=Scene();SrpActorForward.Frame current=default;
                Submit(context=>{if(!first.TryRecord(context,out _,out var why)||!actor.TryRecord(context,first,draws,1,out current,out why))throw new InvalidOperationException(why);});
                var before=ReadSceneTarget(current.color);var beforeDepth=ReadSceneTarget(current.eyeDepth);Check("initial-current-ticket",current.IsCurrent);
                var candidate=Scene();Submit(context=>{if(!candidate.TryRecord(context,out _,out var why))throw new InvalidOperationException(why);});
                void RejectRecord(string name,Action change,Action restore,string reason,ulong sequence=2,TileSceneRenderer.PreparedFrame source=null,ActorForwardDrawSet.PreparedFrame input=null)
                {
                    change();try{Submit(context=>{bool ok=actor.TryRecord(context,source??candidate,input??draws,sequence,out var rejected,out var why);
                        Check("record-reject-"+name,!ok&&!rejected.IsCurrent&&!string.IsNullOrEmpty(why)&&why.Contains(reason));});}finally{restore();}
                    Check("record-reject-"+name+"-preserves-prior",current.IsCurrent&&ScenePixelsEqual(before,ReadSceneTarget(current.color))&&ScenePixelsEqual(beforeDepth,ReadSceneTarget(current.eyeDepth)));
                }
                RejectRecord("disabled",()=>config.enabled=false,()=>config.enabled=true,"disabled or disposed");
                RejectRecord("budget",()=>config.maximumMiB=0,()=>config.maximumMiB=256,"texture budget");
                RejectRecord("zero-sequence",()=>{},()=>{},"monotonic positive sequence",0);
                RejectRecord("duplicate-sequence",()=>{},()=>{},"monotonic positive sequence",1);
                RejectRecord("reused-scene",()=>{},()=>{},"fresh scene",2,first);
                var unrecorded=Scene();RejectRecord("unrecorded-scene",()=>{},()=>{},"recorded current scene",2,unrecorded);
                RejectRecord("camera-mask-change",()=>camera.cullingMask=0,()=>camera.cullingMask=1<<22,"matching full Actor draw preparation");
                RejectRecord("renderer-moved",()=>go.transform.position=Vector3.right,()=>go.transform.position=Vector3.zero,"matching full Actor draw preparation");
                var disposedDraws=Prepare();disposedDraws.Dispose();RejectRecord("disposed-draws",()=>{},()=>{},"matching full Actor draw preparation",2,null,disposedDraws);
                material.SetTexture("_MainTex",current.color);var feedback=Prepare();material.SetTexture("_MainTex",null);
                RejectRecord("color-feedback",()=>{},()=>{},"must not sample their current output",2,null,feedback);
                block.Clear();block.SetTexture("_MainTex",current.eyeDepth);renderer.SetPropertyBlock(block);var blockFeedback=Prepare();
                RejectRecord("property-block-depth-feedback",()=>{},()=>{},"must not sample their current output",2,null,blockFeedback);renderer.SetPropertyBlock(null);
                var foreign=Own(new GameObject("Foreign actor camera")).AddComponent<Camera>();foreign.CopyFrom(camera);foreign.enabled=false;foreign.transform.position=camera.transform.position;
                if(!ActorForwardDrawSet.TryPrepare(foreign,settings,out var foreignDraws,out var foreignError))throw new InvalidOperationException(foreignError);preparations.Add(foreignDraws);
                RejectRecord("foreign-camera",()=>{},()=>{},"matching full Actor draw preparation",2,null,foreignDraws);
                Submit(context=>{bool ok=actor.TryRecord(context,candidate,draws,2,default(SrpTileReflection.Frame),out var rejected,out var why);
                    Check("record-reject-empty-reflection",!ok&&!rejected.IsCurrent&&why.Contains("exact scene and sequence")&&current.IsCurrent);});
                // A material-local debug value must remain authoritative when unrelated globals change.
                Shader.SetGlobalFloat("_FaceDebugMode",0);Shader.SetGlobalVector("_ActorKeyColor",Vector4.zero);
                SrpActorForward.Frame next=default;Submit(context=>{if(!actor.TryRecord(context,candidate,draws,2,out next,out var why))throw new InvalidOperationException(why);});
                Check("record-after-rejections-and-global-change",next.IsCurrent&&!current.IsCurrent&&ScenePixelsEqual(before,ReadSceneTarget(next.color))&&ScenePixelsEqual(beforeDepth,ReadSceneTarget(next.eyeDepth)));
                hardware.Release();Check("released-scene-raster-depth-retires-output",!next.IsCurrent);hardware.Create();
                next.color.Release();Check("released-owned-target-retires-output",!next.IsCurrent);
                var recovery=Scene();Submit(context=>{if(!recovery.TryRecord(context,out _,out var why)||!actor.TryRecord(context,recovery,draws,3,out next,out why))throw new InvalidOperationException(why);});
                Check("owned-target-reallocation-recovers",next.IsCurrent&&ScenePixelsEqual(before,ReadSceneTarget(next.color))&&actor.NominalTextureBytes==(long)width*height*20);
                draws.Dispose();Check("disposed-preparation-retires-output",!next.IsCurrent);
                actor.Dispose();actor.Dispose();Check("compositor-dispose-preserves-borrowed-targets",actor.NominalTextureBytes==0&&output.IsCreated()&&hardware.IsCreated()&&material!=null&&originalMesh!=null);
            }
            finally
            {
                Shader.SetGlobalVector("_ActorKeyColor",oldKey);Shader.SetGlobalFloat("_FaceDebugMode",oldDebug);RenderTexture.active=oldActive;
                actor?.Dispose();foreach(var p in preparations)p.Dispose();foreach(var s in scenes)s.Dispose();
                GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;
                foreach(var value in _owned){if(value is GameObject go){var c=go.GetComponent<Camera>();if(c!=null)c.targetTexture=null;}}
                foreach(var value in _owned){if(value is RenderTexture t)t.Release();if(value!=null)DestroyImmediate(value);}_owned.Clear();
            }
        }
    }
}
