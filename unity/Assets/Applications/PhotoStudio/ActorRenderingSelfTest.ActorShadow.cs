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
        private IEnumerator VerifyActorShadow(Report report)
        {
            yield return null;
            var oldGraphics = GraphicsSettings.renderPipelineAsset; var oldQuality = QualitySettings.renderPipeline;
            var oldActive = RenderTexture.active;
            var scenes = new List<TileSceneRenderer.PreparedFrame>(); var draws = new List<ActorForwardDrawSet.PreparedFrame>();
            var shadow = new SrpActorShadow(); SrpActorForward compositor = null;
            void Check(string name, bool ok, float difference = 0) => FrameworkCheck(report, "actor-shadow-" + name, ok, difference);
            try
            {
                const int size = 129;
                var pipeline = Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                GraphicsSettings.renderPipelineAsset = pipeline; QualitySettings.renderPipeline = pipeline;
                var camera = Own(new GameObject("Independent actor shadow camera")).AddComponent<Camera>();
                camera.enabled = false; camera.allowMSAA = false; camera.orthographic = true; camera.orthographicSize = 2;
                camera.aspect = 1; camera.nearClipPlane = .1f; camera.farClipPlane = 20; camera.transform.position = new Vector3(0,0,-4);
                camera.cullingMask = 1 << 22;
                RenderTexture Target(GraphicsFormat format, GraphicsFormat depth = GraphicsFormat.None)
                {
                    var t = Own(new RenderTexture(new RenderTextureDescriptor(size,size,format,0) { depthStencilFormat = depth }));
                    if (!t.Create()) throw new InvalidOperationException("Shadow fixture target unavailable"); return t;
                }
                var color = Target(GraphicsFormat.B10G11R11_UFloatPack32); camera.targetTexture = color;
                var depth = Target(GraphicsFormat.None,GraphicsFormat.D32_SFloat_S8_UInt);
                var readback = Target(GraphicsFormat.R32G32B32A32_SFloat);
                GameObject Quad(string name, Vector3 position, Vector3 scale)
                {
                    var go = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.name = name; go.layer = 22;
                    go.transform.position = position; go.transform.localScale = scale; return go;
                }
                Material Actor(Renderer r)
                {
                    var m = Own(new Material(Resources.Load<Shader>("PhotoModeFallback"))); r.sharedMaterial = m;
                    m.SetFloat("_OutlineEnabled",0); m.SetFloat("_VertexColor",0); m.SetFloat("_Cull",0); return m;
                }
                var ground = Quad("Background receiving independent drop shadow", new Vector3(0,0,1), Vector3.one * 4);
                var receiver = Quad("Full Actor self-shadow receiver", Vector3.zero, new Vector3(2.8f,2.8f,1)).GetComponent<Renderer>();
                var receiverMaterial = Actor(receiver);
                var caster = Quad("Moving full Actor caster", new Vector3(.17f,.23f,-.6f), new Vector3(.93f,.71f,1)).GetComponent<Renderer>();
                var casterMaterial = Actor(caster); caster.gameObject.layer = 23;
                var main = new SceneDirectionalShadowSettings { enabled=true, origin=new Vector3(0,0,-3), halfSize=Vector2.one*2, farPlane=6, resolution=256 };
                var self = new SceneDirectionalShadowSettings { enabled=true, origin=main.origin, halfSize=main.halfSize, farPlane=6, resolution=256 };
                var surface = new SceneDeferredCamera.Surface { renderer=ground.GetComponent<Renderer>(), cull=CullMode.Off };
                surface.inputs.albedo = new Vector3(.6f,.3f,.2f); surface.inputs.mos = new Vector3(0,1,0);
                var sceneSettings = new TileSceneRenderer.Settings {
                    enabled=true, backend=TileRenderPass.BackendPolicy.AllowEmulation, positionLighting=true, geometryDepthId=true,
                    output=color, depthStencil=depth, surfaces=new[]{surface}, lightDirection=Vector3.back,
                    lightRadiance=Vector3.one*2, ambientIrradiance=Vector3.one*.1f, mainLightShadow=main
                };
                var parameters = new ActorForwardParameters(); parameters.SetFloat("_FaceDebugMode",12);
                var actors = new ActorForwardDrawSet.Settings { renderers=new[]{receiver}, parameters=parameters, outlines=false, hairCover=false };
                compositor = new SrpActorForward(camera,new SrpActorForward.Settings { enabled=true });
                Vector3 selfDirection = Vector3.back; ulong sequence = 0; bool planeOnly = false;
                SrpActorShadow.Frame ticket = default; SrpActorForward.Frame composed = default;
                Color[] actorPixels = null, scenePixels = null, mapPixels = null;
                void CaptureInputs()
                {
                    if (!ActorShadowInputs.TryCapture(new[]{planeOnly?receiver:caster},0,out var inputs,out var error)) throw new InvalidOperationException(error);
                    self.casters = inputs; main.casters = inputs;
                }
                Color[] Read(RenderTexture t)
                {
                    if (t == color) { Graphics.Blit(t,readback); return ReadSceneTarget(readback); }
                    return ReadSceneTarget(t);
                }
                void Run(string name, bool analytic = true)
                {
                    CaptureInputs(); sequence++;
                    if (!TileSceneRenderer.TryPrepare(camera,sceneSettings,out var scene,out var why)) throw new InvalidOperationException(why);
                    scenes.Add(scene); ActorForwardDrawSet.PreparedFrame prepared = null;
                    RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                        if (!shadow.TryRecord(context,selfDirection,self,sequence,out ticket,out var error)) throw new InvalidOperationException(error);
                        actors.selfShadow = ticket;
                        if (!ActorForwardDrawSet.TryPrepare(camera,actors,out prepared,out error)) throw new InvalidOperationException(error);
                        draws.Add(prepared);
                        if (!scene.TryRecord(context,out _,out error) || !compositor.TryRecord(context,scene,prepared,sequence,out composed,out error)) throw new InvalidOperationException(error);
                    }});
                    actorPixels = Read(composed.color); scenePixels = Read(color); mapPixels = ticket.depth != null ? Read(ticket.depth) : null;
                    Check(name+"-current",ticket.IsCurrent&&composed.IsCurrent&&prepared.IsValid);
                    bool finite = true; foreach(var p in actorPixels) for(int c=0;c<4;c++) finite &= !float.IsNaN(p[c])&&!float.IsInfinity(p[c]);
                    Check(name+"-finite",finite);
                    SaveSsrPreview("actor-shadow-"+name,actorPixels,size,size,false);
                    SaveSsrPreview("actor-shadow-"+name+"-background",scenePixels,size,size,false);
                    if(mapPixels != null) SaveSsrPreview("actor-shadow-"+name+"-map",mapPixels,ticket.depth.width,ticket.depth.height,false);
                    if(!analytic)return;
                    // Independent world-ray test for receiver interiors. Exclude only the
                    // raster/PCF boundary band, not arbitrary failing pixels.
                    int lit=0, blocked=0; float worst=0;
                    var inverse = (caster.localToWorldMatrix*Matrix4x4.Scale(self.casters.Length>0?self.casters[0].vertexScale:Vector3.one)).inverse;
                    for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                    {
                        var ray=camera.ViewportPointToRay(new Vector3((x+.5f)/size,(y+.5f)/size));
                        var world=ray.GetPoint(-ray.origin.z/ray.direction.z);
                        if(Mathf.Abs(world.x)>1.3f||Mathf.Abs(world.y)>1.3f)continue;
                        var local=inverse.MultiplyPoint(world);var direction=inverse.MultiplyVector(selfDirection.normalized);
                        float distance=-local.z/direction.z;var hit=local+direction*distance;
                        if(Mathf.Abs(Mathf.Abs(hit.x)-.5f)<.09f||Mathf.Abs(Mathf.Abs(hit.y)-.5f)<.09f)continue;
                        bool occluded=distance>self.depthBias&&Mathf.Abs(hit.x)<.5f&&Mathf.Abs(hit.y)<.5f&&self.casters.Length>0;
                        float expected=occluded?1-self.strength:1;int index=y*size+x;
                        worst=Mathf.Max(worst,Mathf.Abs(actorPixels[index].r-expected));
                        if(occluded)blocked++;else lit++;
                    }
                    Check(name+"-independent-ray-interiors",worst<.001f&&lit>3000&&blocked>100,worst);
                }
                Run("independent-lights"); var originalActor=actorPixels; var originalScene=scenePixels; var originalMap=mapPixels;
                Check("map-real-axial-depth",Array.Exists(originalMap,p=>p.r<.9f)&&Array.TrueForAll(originalMap,p=>Mathf.Abs(p.r-1)<1e-6f||Mathf.Abs(p.r-.4f)<1e-6f));
                selfDirection=new Vector3(.55f,-.25f,-1);Run("self-direction");
                Check("self-direction-changes-actor",PixelError(originalActor,actorPixels)>.5f);
                Check("self-direction-preserves-background",ScenePixelsEqual(originalScene,scenePixels));
                selfDirection=Vector3.back; self.strength=.35f;Run("self-strength");
                Check("self-strength-preserves-background",ScenePixelsEqual(originalScene,scenePixels));
                self.strength=0;Run("self-disabled");
                Check("zero-strength-no-allocation",ticket.depth==null&&shadow.CasterDrawCalls==0);
                self.strength=1; sceneSettings.lightDirection=new Vector3(-.5f,.2f,-1);Run("background-direction");
                Check("background-direction-preserves-self-map",ScenePixelsEqual(originalMap,mapPixels));
                Check("background-direction-changes-scene",PixelError(originalScene,scenePixels)>.01f);
                sceneSettings.lightDirection=Vector3.back;
                caster.transform.position+=new Vector3(-.5f,.3f,0);Check("moving-caster-invalidates-ticket",!ticket.IsCurrent&&!composed.IsCurrent);
                Run("moving-caster");Check("moving-caster-changes-shadow",PixelError(originalActor,actorPixels)>.5f);
                caster.transform.position-=new Vector3(-.5f,.3f,0);
                casterMaterial.SetVector("_WardrobeScaleCorrection",new Vector4(1.4f,.7f,1,0));Run("wardrobe-scale");
                casterMaterial.SetVector("_WardrobeScaleCorrection",Vector4.one);
                self.filter=SceneShadowFilter.Pcf3x3;Run("pcf");Check("pcf-changes-edges",PixelError(originalActor,actorPixels)>.01f);self.filter=SceneShadowFilter.Hard;
                var alpha=Own(new Texture2D(2,1,TextureFormat.RGBAFloat,false,true));alpha.filterMode=FilterMode.Point;alpha.wrapMode=TextureWrapMode.Clamp;
                alpha.SetPixels(new[]{new Color(1,1,1,0),Color.white});alpha.Apply();casterMaterial.SetTexture("_MainTex",alpha);casterMaterial.SetFloat("_UseAlphaClip",1);casterMaterial.SetFloat("_Cutoff",.5f);
                Run("cutout",false);var halfMap=mapPixels;
                Check("alpha-cutout-changes-map",PixelError(originalMap,halfMap)>.1f);
                casterMaterial.SetVector("_ActorTextureFrame",new Vector4(.5f,1,.5f,0));Run("atlas-opaque-frame",false);
                Check("atlas-frame-restores-full-map",ScenePixelsEqual(originalMap,mapPixels));
                // Keep the empty-frame footprint inside the transparent texel. At an
                // exact atlas boundary, fwidth includes helper lanes from the opaque
                // texel and the full Actor coverage policy can retain a one-pixel edge.
                casterMaterial.SetVector("_ActorTextureFrame",new Vector4(.25f,1,.125f,0));Run("atlas-empty-frame",false);
                Check("atlas-frame-clears-map",Array.TrueForAll(mapPixels,p=>p.r==1));
                casterMaterial.SetFloat("_UseAlphaClip",0);casterMaterial.SetVector("_ActorTextureFrame",Vector4.zero);
                var block=new MaterialPropertyBlock();block.SetVector("_ActorColor",new Vector4(1,1,1,0));caster.SetPropertyBlock(block);Run("mpb-zero-fade",false);
                Check("zero-fade-clears-entire-map",Array.TrueForAll(mapPixels,p=>p.r==1));
                block.SetVector("_ActorColor",new Vector4(1,1,1,.5f));caster.SetPropertyBlock(block);Run("mpb-half-fade",false);
                int fullCount=0,halfCount=0;foreach(var p in originalMap)if(p.r<.9f)fullCount++;foreach(var p in mapPixels)if(p.r<.9f)halfCount++;
                Check("timeline-fade-coverage",halfCount>fullCount*.4f&&halfCount<fullCount*.6f);
                var submeshBlock=new MaterialPropertyBlock();submeshBlock.SetVector("_ActorColor",Vector4.one);caster.SetPropertyBlock(submeshBlock,0);Run("submesh-replaces-mpb",false);
                Check("submesh-block-restores-full-map",ScenePixelsEqual(originalMap,mapPixels));caster.SetPropertyBlock(null,0);caster.SetPropertyBlock(null);
                block.Clear();block.SetFloat("_ShadowFar",0);caster.SetPropertyBlock(block);
                Check("reserved-mpb-rejected",!ActorShadowInputs.TryCapture(new[]{caster},0,out _,out _));caster.SetPropertyBlock(null);
                Check("duplicate-caster-rejected",!ActorShadowInputs.TryCapture(new[]{caster,caster},0,out _,out _));
                Check("nonfinite-lod-rejected",!ActorShadowInputs.TryCapture(new[]{caster},float.NaN,out _,out _));
                self.strength=1;Run("restored");Check("restored-whole-color",ScenePixelsEqual(originalActor,actorPixels));
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                    Check("duplicate-sequence-rejected",!shadow.TryRecord(context,selfDirection,self,sequence,out _,out _));
                    Check("duplicate-rejection-keeps-current-ticket",ticket.IsCurrent&&composed.IsCurrent);
                    Check("zero-sequence-rejected",!shadow.TryRecord(context,selfDirection,self,0,out _,out _));
                    self.casters[0].alphaMap=ticket.depth;
                    Check("output-feedback-rejected",!shadow.TryRecord(context,selfDirection,self,sequence+1,out _,out _));
                    Check("failed-preparation-retires-consumer",!ticket.IsCurrent&&!composed.IsCurrent);
                    self.casters[0].alphaMap=null;
                }});
                Run("after-feedback-rejection");
                ticket.depth.Release();Check("released-shadow-retires-consumer",!ticket.IsCurrent&&!composed.IsCurrent);
                Run("after-shadow-reallocation");Check("reallocated-shadow-restores-color",ScenePixelsEqual(originalActor,actorPixels));
                casterMaterial.SetFloat("_UseAlphaClip",float.NaN);
                Check("nonfinite-material-input-rejected",!ActorShadowInputs.TryCapture(new[]{caster},0,out _,out _));casterMaterial.SetFloat("_UseAlphaClip",0);
                // A single sloping plane must not shadow itself. The intentionally
                // wrong smooth normals also reject a shading-normal approximation.
                planeOnly=true;receiver.transform.rotation=Quaternion.Euler(37,29,12);
                var planeMesh=Own(Instantiate(receiver.GetComponent<MeshFilter>().sharedMesh));
                var wrongNormals=planeMesh.normals;for(int i=0;i<wrongNormals.Length;i++)wrongNormals[i]=Vector3.right;
                planeMesh.normals=wrongNormals;receiver.GetComponent<MeshFilter>().sharedMesh=planeMesh;
                self.resolution=32;self.filter=SceneShadowFilter.Pcf3x3;self.depthBias=.0002f;
                float PlaneError(string name)
                {
                    var plane=new Plane(receiver.transform.forward,receiver.transform.position);float error=0;int coverage=0;
                    for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                    {
                        var ray=camera.ViewportPointToRay(new Vector3((x+.5f)/size,(y+.5f)/size));
                        if(!plane.Raycast(ray,out float distance))continue;
                        var world=ray.GetPoint(distance);
                        // The tilted Actor corner can lie behind the independent
                        // z=1 scene receiver and is then correctly depth-occluded.
                        if(world.z>=ground.transform.position.z)continue;
                        var local=receiver.transform.InverseTransformPoint(world);
                        if(Mathf.Abs(local.x)>.4f||Mathf.Abs(local.y)>.4f)continue;
                        error=Mathf.Max(error,Mathf.Abs(actorPixels[y*size+x].r-1));coverage++;
                    }
                    Check(name+"-independent-coverage",coverage>1000,coverage);return error;
                }
                shadow.ReceiverPlaneBias=false;Run("sloped-plane-constant-bias",false);float acne=PlaneError("constant-bias");
                Check("constant-bias-acne-negative-control",acne>.2f,acne);
                shadow.ReceiverPlaneBias=true;Run("sloped-plane-receiver-bias",false);float corrected=PlaneError("receiver-plane");
                Check("receiver-plane-removes-self-acne",corrected<.001f,corrected);
                shadow.Dispose();Check("disposed-producer-retires-consumer",!ticket.IsCurrent&&!composed.IsCurrent);
            }
            finally
            {
                compositor?.Dispose();foreach(var f in draws)f.Dispose();foreach(var f in scenes)f.Dispose();shadow.Dispose();
                GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;RenderTexture.active=oldActive;
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
        }
    }
}
