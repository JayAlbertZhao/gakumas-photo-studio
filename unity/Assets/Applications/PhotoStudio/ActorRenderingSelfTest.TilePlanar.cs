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
        private IEnumerator VerifyTilePlanar(Report report)
        {
            yield return null;
            var oldGraphics=GraphicsSettings.renderPipelineAsset;var oldQuality=QualitySettings.renderPipeline;
            var oldActive=RenderTexture.active;var scenes=new List<TileSceneRenderer.PreparedFrame>();
            SrpTilePlanarReflection planar=null;SrpTileReflection resolver=null;
            void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"tile-planar-"+name,ok,error);
            try
            {
                const int width=97,height=73;
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                var camera=Own(new GameObject("SRP mirror analytic camera")).AddComponent<Camera>();camera.enabled=false;camera.allowMSAA=false;
                camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.fieldOfView=55;camera.aspect=(float)width/height;
                camera.transform.position=new Vector3(0,1,-3);camera.transform.rotation=Quaternion.identity;
                RenderTexture Target(GraphicsFormat format,string name)
                { var t=Own(new RenderTexture(new RenderTextureDescriptor(width,height,format,0)) { name=name,filterMode=FilterMode.Point });if(!t.Create())throw new InvalidOperationException(name);return t; }
                var output=Target(GraphicsFormat.B10G11R11_UFloatPack32,"Tile Planar scene");camera.targetTexture=output;
                var normal=Target(GraphicsFormat.R16G16B16A16_SFloat,"Tile Planar normals");
                var mos=Target(GraphicsFormat.R8G8B8A8_UNorm,"Tile Planar MOS");
                var materialBase=Target(GraphicsFormat.R8G8B8A8_SRGB,"Tile Planar base");
                var readFloat=Target(GraphicsFormat.R32G32B32A32_SFloat,"Tile Planar readback");
                var sampleReference=Target(GraphicsFormat.R32G32B32A32_SFloat,"Planar CPU-query hardware sampler reference");
                var referenceQuery=Own(new Texture2D(width,height,TextureFormat.RGBAFloat,false,true) { filterMode=FilterMode.Point });
                var referenceMaterial=Own(new Material(Resources.Load<Shader>("TilePlanarSamplingReference")));
                referenceMaterial.SetTexture("_ReferenceQuery",referenceQuery);referenceMaterial.SetVector("_ReferenceSize",new Vector4(width,height,0,0));
                var before=Target(GraphicsFormat.R16G16B16A16_SFloat,"Before mirror camera-state sentinel");
                var after=Target(GraphicsFormat.R16G16B16A16_SFloat,"After mirror camera-state sentinel");
                var sentinel=Own(new Material(Resources.Load<Shader>("PlanarCapture")));
                sentinel.SetColor("_Color",Color.black);sentinel.SetVector("_Emission",new Vector4(.25f,.5f,1,0));sentinel.SetFloat("_Cull",(float)CullMode.Back);
                GameObject Quad(string name,Vector3 position,Vector3 size,bool reverse=false)
                {
                    var go=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));go.name=name;go.layer=22;
                    go.transform.position=position;go.transform.localScale=size;go.transform.rotation=Quaternion.Euler(0,reverse?180:0,0);return go;
                }
                var receiver=Quad("Mirror receiver",new Vector3(0,1,0),new Vector3(5,4,1));
                var surface=new SceneDeferredCamera.Surface { renderer=receiver.GetComponent<Renderer>(),receiverGroup=7,cull=CullMode.Back,
                    inputs=new SceneDeferredCamera.MaterialInputs { albedo=Vector3.one*.4f,mos=new Vector3(.5f,1,1),emission=Vector3.one*.05f } };
                var sceneSettings=new TileSceneRenderer.Settings { enabled=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,geometryDepthId=true,
                    output=output,normalIdentity=normal,materialBase=materialBase,materialMos=mos,surfaces=new[]{surface},lightRadiance=Vector3.zero,ambientIrradiance=Vector3.zero };
                PlanarReflection.Draw Draw(string name,Vector3 position,Vector3 size,Color color)
                {
                    var go=Quad(name,position,size,true);var material=Own(new Material(Resources.Load<Shader>("PlanarCapture")));
                    material.SetColor("_Color",new Color(1,1,1,0));material.SetColor("_AmbientColor",Color.black);material.SetColor("_LightColor",Color.black);
                    material.SetVector("_Emission",new Vector4(color.r,color.g,color.b,0));material.SetFloat("_Cutoff",0);material.SetFloat("_Cull",(float)CullMode.Back);
                    go.GetComponent<Renderer>().sharedMaterial=material;
                    return new PlanarReflection.Draw { surface=new SceneDepthData.Surface { renderer=go.GetComponent<Renderer>(),cull=CullMode.Back },material=material };
                }
                var red=Draw("Offscreen red emitter",new Vector3(-.5f,1.3f,-4),new Vector3(2,2,1),new Color(4,.5f,.25f,0));
                var green=Draw("Near green emitter",new Vector3(.5f,.7f,-2),new Vector3(1,1,1),new Color(.25f,4,.5f,0));
                var clipped=Draw("Wrong side must be clipped",new Vector3(0,1,.5f),new Vector3(8,8,1),new Color(0,0,8,0));
                var settings=new SrpTilePlanarReflection.Settings { enabled=true,planeNormal=Vector3.back,receiverGroup=7,resolutionScale=1,maximumRoughnessMip=0,draws=new[]{red,green,clipped} };
                planar=new SrpTilePlanarReflection(camera,settings);
                var rs=new SrpTileReflection.Settings { enabled=true,sceneOnlyInput=true,maximumSteps=512,normalDistortion=Vector2.zero,allowComputeFallback=false };
                resolver=new SrpTileReflection(camera,rs);
                var orientation=Quaternion.identity;var translation=Vector3.zero;
                var transforms=new[]{camera.transform,receiver.transform,red.surface.renderer.transform,green.surface.renderer.transform,clipped.surface.renderer.transform};
                var initialPositions=new Vector3[transforms.Length];var initialRotations=new Quaternion[transforms.Length];
                for(int i=0;i<transforms.Length;i++) { initialPositions[i]=transforms[i].position;initialRotations[i]=transforms[i].rotation; }
                void CoordinateFrame(Quaternion rotation,Vector3 offset)
                {
                    orientation=rotation;translation=offset;
                    for(int i=0;i<transforms.Length;i++)transforms[i].SetPositionAndRotation(offset+rotation*initialPositions[i],rotation*initialRotations[i]);
                    settings.planePoint=offset;settings.planeNormal=rotation*Vector3.back;
                }
                ulong sequence=0;SrpTilePlanarReflection.Frame last=default,current=default;SrpTileReflection.Frame resolved=default;
                Color[] Read(RenderTexture target)
                { if(target==output) { var active=RenderTexture.active;Graphics.Blit(target,readFloat);RenderTexture.active=active;return ReadSceneTarget(readFloat); }return ReadSceneTarget(target); }
                void Save(string name,RenderTexture target)
                { var values=Read(target);SaveSsrPreview("tile-planar-"+name,values,target.width,target.height,false);
                    using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"tile-planar-"+name+".raw")));foreach(var value in values)for(int c=0;c<4;c++)writer.Write(value[c]); }
                float Error(Color a,Color b) { float e=0;for(int c=0;c<4;c++)e=Mathf.Max(e,Mathf.Abs(a[c]-b[c]));return e; }
                Color[] Run(string name,bool analytic=true,bool usePlanar=true)
                {
                    if(!TileSceneRenderer.TryPrepare(camera,sceneSettings,out var scene,out var why))throw new InvalidOperationException(why);
                    scenes.Add(scene);sequence++;var oldView=camera.worldToCameraMatrix;var oldProjection=camera.projectionMatrix;
                    var request=new TilePassTestRequest { record=context=>{
                        void Sentinel(RenderTexture target)
                        {
                            using var command=new CommandBuffer { name="Planar host camera-state sentinel" };
                            command.SetRenderTarget(target);command.ClearRenderTarget(true,true,Color.clear);
                            command.DrawRenderer(receiver.GetComponent<Renderer>(),sentinel,0,0);context.ExecuteCommandBuffer(command);
                        }
                        Sentinel(before);
                        Check(name+"-reject-unrecorded",!planar.TryRecord(context,scene,sequence,out _,out _));
                        if(!scene.TryRecord(context,out _,out var error))throw new InvalidOperationException(error);
                        if(!planar.TryRecord(context,scene,sequence,out current,out error))throw new InvalidOperationException(error);
                        Check(name+"-reject-duplicate",!planar.TryRecord(context,scene,sequence,out _,out _));
                        Check(name+"-reject-invalid-planar",!resolver.TryRecord(context,scene,sequence,1,default,out _,out _));
                        bool ok=usePlanar?resolver.TryRecord(context,scene,sequence,1,current,out resolved,out error):
                            resolver.TryRecord(context,scene,sequence,1,out resolved,out error);
                        if(!ok)throw new InvalidOperationException(error);
                        Sentinel(after);
                    }};
                    bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_PLANAR")=="1";
                    string selected=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_PLANAR_CASE");capture&=string.IsNullOrEmpty(selected)||selected==name;
                    bool began=capture&&RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try
                    {
                    RenderPipeline.SubmitRenderRequest(camera,request);if(capture)Read(output);
                    Check(name+"-current",current.IsCurrent&&resolved.IsCurrent&&!last.IsCurrent);last=current;
                    Check(name+"-source-camera-unchanged",camera.worldToCameraMatrix==oldView&&camera.projectionMatrix==oldProjection&&camera.targetTexture==output);
                    var sentinelPixels=Read(before);
                    Check(name+"-gpu-camera-and-culling-restored",Array.Exists(sentinelPixels,p=>p.b>.9f)&&ScenePixelsEqual(sentinelPixels,Read(after)));
                    Check(name+"-explicit-eight-byte-depth-stencil",current.capture.depthStencilFormat==GraphicsFormat.D32_SFloat_S8_UInt);
                    long budget=(long)width*height*8+(long)current.capture.width*current.capture.height*8;
                    for(int mip=0;mip<current.capture.mipmapCount;mip++)budget+=(long)Mathf.Max(1,current.capture.width>>mip)*Mathf.Max(1,current.capture.height>>mip)*8;
                    Check(name+"-complete-capture-mips-depth-output-budget",planar.NominalTextureBytes==budget,planar.NominalTextureBytes-budget);
                    var captured=Read(current.capture);var projected=Read(current.reflection);var traced=Read(resolved.rawReflection);
                    var radiance=Read(resolved.radiance);var response=Read(resolved.response);var color=Read(resolved.color);var input=Read(output);var normals=Read(normal);
                    int covered=0,skipped=0;float resolveError=0,compositeError=0,projectError=0;bool finite=true;
                    var depths=Read(scene.EyeDepth);
                    var materialMos=Read(mos);var resolvedReflection=Read(resolved.reflection);
                    var geometry=Read(scene.GeometryDepthId);
                    if(settings.maximumRoughnessMip>0)
                        for(int mip=1;mip<current.capture.mipmapCount;mip++)
                        {
                            int w=Mathf.Max(1,current.capture.width>>mip),h=Mathf.Max(1,current.capture.height>>mip);
                            var target=Own(new RenderTexture(w,h,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target.Create();
                            Graphics.CopyTexture(current.capture,0,mip,target,0,0);Save(name+"-capture-mip-"+mip,target);target.Release();
                        }
                    // Isolate external hardware filtering from our geometric/material
                    // contract. CPU rays and world-plane reflection derive UV/LOD without
                    // using the producer's matrices or sampling its projected output.
                    // Ideal CPU lerps are not bit-equivalent to the native sampler (the
                    // retained v8 diagnostics demonstrate this even with exact input mips).
                    var queries=new Color[width*height];var eligible=new bool[queries.Length];
                    for(int p=0;p<queries.Length;p++)
                    {
                        var ray=camera.ViewportPointToRay(new Vector3((p%width+.5f)/width,(p/width+.5f)/height,0));
                        float originZ=camera.worldToCameraMatrix.MultiplyPoint(ray.origin).z,directionZ=camera.worldToCameraMatrix.MultiplyVector(ray.direction).z;
                        var world=ray.GetPoint((-depths[p].r-originZ)/directionZ);var n=settings.planeNormal.normalized;
                        float distance=Vector3.Dot(n,world-settings.planePoint);
                        var uv=camera.WorldToViewportPoint(world-2*distance*n);
                        float roughness=1-materialMos[p].b;
                        queries[p]=new Color(uv.x,uv.y,roughness*roughness*Mathf.Min(settings.maximumRoughnessMip,current.capture.mipmapCount-1),1);
                        eligible[p]=normals[p].a>.5f && (normals[p].a-1)%256==settings.receiverGroup && depths[p].r>0 &&
                            Mathf.Abs(distance)<=settings.receiverPlaneTolerance && uv.z>0 && uv.x>=0 && uv.x<=1 && uv.y>=0 && uv.y<=1;
                    }
                    referenceQuery.SetPixels(queries);referenceQuery.Apply(false,false);referenceMaterial.SetTexture("_ReferenceCapture",current.capture);
                    var previousTarget=RenderTexture.active;Graphics.Blit(Texture2D.blackTexture,sampleReference,referenceMaterial,0);RenderTexture.active=previousTarget;
                    var sampled=Read(sampleReference);
                    for(int p=0;p<projected.Length;p++)
                    {
                        if(projected[p].a>1e-5f) { covered++;if(traced[p].a==0&&resolvedReflection[p].a==0)skipped++; }
                        Color want=normals[p].a>.5f?new Color(0,0,0,1):Color.clear;
                        if(normals[p].a>.5f)
                        {
                            var n=new Vector3(normals[p].r,normals[p].g,normals[p].b).normalized;
                            var ng=new Vector3(geometry[p].r*2-1,geometry[p].g*2-1,geometry[p].b*2-1).normalized;
                            var delta=camera.worldToCameraMatrix.MultiplyVector(n-ng);
                            float u=(p%width+.5f)/width+delta.x*rs.normalDistortion.x;
                            // These are already normalized texture UVs, not clip Y.
                            float v=(p/width+.5f)/height+delta.z*rs.normalDistortion.y;
                            Color screen=Color.clear,mirrorSample=Color.clear;
                            if(u>=0&&u<1&&v>=0&&v<1)
                            {
                                int q=(int)(v*height)*width+(int)(u*width);
                                if(normals[q].a>.5f&&(normals[q].a-1)%256==(normals[p].a-1)%256)
                                { screen=resolvedReflection[q];if(usePlanar)mirrorSample=projected[q]; }
                            }
                            var probe=rs.probe!=null?new Color(1,2,4,1):Color.black;
                            for(int c=0;c<3;c++)want[c]=Mathf.Lerp(Mathf.Lerp(probe[c],screen[c],screen.a),mirrorSample[c],mirrorSample.a);
                        }
                        resolveError=Mathf.Max(resolveError,Error(want,radiance[p]));
                        var expected=input[p];for(int c=0;c<3;c++)expected[c]=Mathf.Min(65504,input[p][c]+response[p][c]*radiance[p][c]);
                        compositeError=Mathf.Max(compositeError,Error(expected,color[p]));
                        for(int c=0;c<4;c++)finite&=!float.IsNaN(color[p][c])&&!float.IsInfinity(color[p][c]);
                        {
                            Color expectedProjection=Color.clear;
                            var sample=sampled[p];
                            if(eligible[p] && sample.a>1e-5f)
                                expectedProjection=new Color(sample.r/sample.a,sample.g/sample.a,sample.b/sample.a,sample.a*settings.strength);
                            projectError=Mathf.Max(projectError,Error(expectedProjection,projected[p]));
                        }
                    }
                    if(usePlanar)Check(name+"-all-planar-covered-pixels-skip-ssr",covered==skipped,covered-skipped);
                    Check(name+"-whole-planar-first-resolve",resolveError<.004f,resolveError);
                    Check(name+"-whole-nonrecursive-composite",compositeError<.008f,compositeError);Check(name+"-finite",finite);
                    Check(name+"-whole-visible-receiver-projection",projectError<.008f,projectError);
                    if(analytic)
                    {
                        int compared=0,wrong=0,redPixels=0,greenPixels=0;
                        // Independent reflection of ordinary camera rays across z=0.
                        // Exclude primitive edges, not entire failed regions.
                        for(int p=0;p<captured.Length;p++)
                        {
                            var ray=camera.ViewportPointToRay(new Vector3((p%width+.5f)/width,(p/width+.5f)/height,0));
                            Vector3 origin=Quaternion.Inverse(orientation)*(ray.origin-translation),direction=Quaternion.Inverse(orientation)*ray.direction;origin.z=-origin.z;direction.z=-direction.z;
                            Color want=Color.clear;float nearest=float.PositiveInfinity;bool edge=false;
                            foreach(var draw in new[]{red,green})
                            {
                                var t=draw.surface.renderer.transform;var position=Quaternion.Inverse(orientation)*(t.position-translation);float distance=(position.z-origin.z)/direction.z;
                                var point=origin+direction*distance;float dx=Mathf.Abs(point.x-position.x)-t.localScale.x*.5f;
                                float dy=Mathf.Abs(point.y-position.y)-t.localScale.y*.5f;
                                if(Mathf.Abs(dx)<.02f&&dy<.02f || Mathf.Abs(dy)<.02f&&dx<.02f)edge=true;
                                if(distance>0 && distance<nearest && dx<0 && dy<0) { nearest=distance;var emission=draw.material.GetVector("_Emission");want=new Color(emission.x,emission.y,emission.z,1); }
                            }
                            if(edge)continue;compared++;if(want.r==4)redPixels++;if(want.g==4)greenPixels++;
                            if(Error(captured[p],want)>.004f)wrong++;
                        }
                        Check(name+"-analytic-mirrored-ray-depth-clipping",compared>6500&&wrong==0,wrong);
                        Check(name+"-real-offscreen-and-occluding-emitter",redPixels>30&&greenPixels>30&&(settings.receiverGroup!=7||covered>30),covered);
                    }
                    foreach(var pair in new[]{("capture",current.capture),("projected",current.reflection),("trace",resolved.rawReflection),("radiance",resolved.radiance),
                        ("response",resolved.response),("color",resolved.color),("source",output),("normal",normal),("geometry",scene.GeometryDepthId),("filtered",resolved.reflection),("mos",mos),("depth",scene.EyeDepth)})Save(name+"-"+pair.Item1,pair.Item2);
                    Save(name+"-hardware-reference",sampleReference);
                    if(name=="raster-cold" || name=="rough-mip-projection")
                    {
                        var negative=(Color[])queries.Clone();
                        for(int p=0;p<negative.Length;p++) { if(name=="raster-cold")negative[p].r+=.125f;else negative[p].b=0; }
                        referenceQuery.SetPixels(negative);referenceQuery.Apply(false,false);
                        previousTarget=RenderTexture.active;Graphics.Blit(Texture2D.blackTexture,sampleReference,referenceMaterial,0);RenderTexture.active=previousTarget;
                        var wrong=Read(sampleReference);int different=0;for(int p=0;p<wrong.Length;p++)if(Error(wrong[p],sampled[p])>.02f)different++;
                        Check(name+"-independent-query-negative-control",different>50,different);
                        Save(name+"-wrong-query-reference",sampleReference);
                        referenceQuery.SetPixels(queries);referenceQuery.Apply(false,false);
                    }
                    using(var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"tile-planar-"+name+"-cpu-sampling-query.raw"))))
                        foreach(var query in queries)for(int c=0;c<4;c++)writer.Write(query[c]);
                    return projected;
                    }
                    finally { if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture();if(capture)Check(name+"-native-capture",began&&ended); }
                }
                var cold=Run("raster-cold");var warm=Run("raster-warm");Check("capture-has-no-temporal-dependence",ScenePixelsEqual(cold,warm));
                rs.backend=SceneShaderBackend.Compute;Run("compute-cold");Run("compute-warm");rs.backend=SceneShaderBackend.Raster;
                settings.draws=Array.Empty<PlanarReflection.Draw>();var empty=Run("empty",false);Check("empty-capture-clears-coverage",Array.TrueForAll(empty,p=>p==Color.clear));
                settings.draws=new[]{red,green,clipped};settings.receiverGroup=8;Check("wrong-group-no-projection",Array.TrueForAll(Run("wrong-group"),p=>p==Color.clear));
                settings.receiverGroup=7;settings.strength=.25f;Run("quarter-strength");settings.strength=1;
                settings.resolutionScale=.5f;Run("half-resolution",false);settings.resolutionScale=1;
                settings.maximumRoughnessMip=5;surface.inputs.mos.z=.3f;Run("rough-mip-projection",false);
                var smoothDecal=new SceneDeferredCamera.Decal { receiverGroup=7,localToWorld=Matrix4x4.TRS(new Vector3(0,1,0),Quaternion.identity,new Vector3(6,5,1)),albedoWeight=0,mosWeight=Vector3.forward };
                smoothDecal.inputs.mos=Vector3.forward;sceneSettings.decals=new[]{smoothDecal};Run("post-decal-smooth-projection",false);
                sceneSettings.decals=Array.Empty<SceneDeferredCamera.Decal>();surface.inputs.mos.z=1;settings.maximumRoughnessMip=0;
                camera.orthographic=true;camera.orthographicSize=2;Run("orthographic");camera.orthographic=false;
                CoordinateFrame(Quaternion.Euler(20,30,12),new Vector3(1,2,3));Run("tilted-translated-plane");
                CoordinateFrame(Quaternion.identity,Vector3.zero);
                var occluder=Quad("Current Tile foreground on same group",new Vector3(-.3f,1.1f,-.4f),new Vector3(.7f,.9f,1));
                var occludingSurface=new SceneDeferredCamera.Surface { renderer=occluder.GetComponent<Renderer>(),receiverGroup=7,cull=CullMode.Back,
                    inputs=new SceneDeferredCamera.MaterialInputs { mos=Vector3.one,emission=Vector3.one*.2f } };
                sceneSettings.surfaces=new[]{surface,occludingSurface};Run("same-group-off-plane-occlusion",false);
                occludingSurface.receiverGroup=8;Run("other-group-occlusion",false);sceneSettings.surfaces=new[]{surface};
                occluder.SetActive(false);
                void Reject(string name,Action change,Action restore)
                {
                    if(!TileSceneRenderer.TryPrepare(camera,sceneSettings,out var scene,out var reason))throw new InvalidOperationException(reason);
                    scenes.Add(scene);
                    RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                        if(!scene.TryRecord(context,out _,out var error))throw new InvalidOperationException(error);
                        change();Check("reject-"+name,!planar.TryRecord(context,scene,sequence+1,out _,out _));restore();
                    }});
                }
                Reject("disabled",()=>settings.enabled=false,()=>settings.enabled=true);
                Reject("nonfinite-plane",()=>settings.planeNormal=new Vector3(float.NaN,0,0),()=>settings.planeNormal=Vector3.back);
                Reject("zero-normal",()=>settings.planeNormal=Vector3.zero,()=>settings.planeNormal=Vector3.back);
                Reject("negative-camera-side",()=>settings.planeNormal=Vector3.forward,()=>settings.planeNormal=Vector3.back);
                Reject("bad-group",()=>settings.receiverGroup=256,()=>settings.receiverGroup=7);
                Reject("budget",()=>settings.maximumMiB=0,()=>settings.maximumMiB=128);
                Reject("bad-pass",()=>red.shaderPass=1000,()=>red.shaderPass=0);
                Reject("bad-coverage-pass",()=>{red.coverageMaterial=red.material;red.coverageShaderPass=1000;},()=>{red.coverageMaterial=null;red.coverageShaderPass=0;});
                Reject("viewport-changed",()=>camera.rect=new Rect(0,0,.5f,1),()=>camera.rect=new Rect(0,0,1,1));
                Reject("camera-changed",()=>camera.transform.position+=Vector3.right,()=>camera.transform.position-=Vector3.right);
                sceneSettings.materialMos=null;Reject("missing-mos-export",()=>{},()=>{});sceneSettings.materialMos=mos;
                Run("after-rejections");
                // Reuse the toolkit's actual reduced ActorToon adapter in the SRP capture.
                // No main material mutation by the producer, no hand-authored reflection atlas.
                using(var actorSet=new ActorPlanarCaptureSet())
                {
                    var actor=Quad("Reduced actor capture",new Vector3(0,1,-2),new Vector3(1.5f,1.5f,1),true);
                    var actorRenderer=actor.GetComponent<Renderer>();var actorMaterial=Own(new Material(Resources.Load<Shader>("PhotoModeFallback")));
                    actorRenderer.sharedMaterial=actorMaterial;
                    actorMaterial.SetFloat("_StencilComp",8);actorMaterial.SetFloat("_StencilPass",0);actorMaterial.SetFloat("_StencilWriteMask",0);
                    actorMaterial.SetFloat("_VertexColor",0);actorMaterial.SetFloat("_DisableDefMap",1);actorMaterial.SetVector("_DefValue",new Vector4(.5f,0,0,0));
                    actorMaterial.SetTexture("_MainTex",ResolveTexture(new[]{new Color(.8f,.2f,.1f,.25f)},1,1));
                    actorMaterial.SetTexture("_RampTex",Texture2D.whiteTexture);actorMaterial.SetTexture("_ShadeTex",Texture2D.whiteTexture);
                    actorMaterial.SetTexture("_RampAddTex",Texture2D.blackTexture);actorMaterial.SetShaderPassEnabled("ActorHairCover",false);
                    actorMaterial.SetFloat("_Cull",(float)CullMode.Back);
                    var lighting=new ActorPlanarLighting { ambientColor=Vector3.zero,lightColor=Vector3.one,lightDirection=Vector3.forward };
                    Renderer[] actorRenderers={actorRenderer};
                    void Refresh()
                    {
                        if(!actorSet.TryRefresh(actorRenderers,camera.worldToCameraMatrix*PlanarReflection.ReflectionMatrix(settings.planePoint,settings.planeNormal),lighting,out var error))throw new InvalidOperationException(error);
                        settings.draws=actorSet.Draws;
                    }
                    Color Center()=>Read(current.capture)[(height/2)*width+width/2];
                    foreach(int type in new[]{0,1,2,3,4,5,6,8,9})
                    {
                        actorMaterial.SetFloat("_ShaderType",type);actorMaterial.SetFloat("_SrcBlend",type==2?5:1);
                        actorMaterial.SetFloat("_DstBlend",type==2||type==4?10:type==5?1:0);actorMaterial.SetFloat("_ZWrite",type==2||type==4||type==5?0:1);
                        Refresh();Run("actor-type-"+type,false);float alpha=type==2||type==4?.25f:1;
                        var expected=new Color(.8f*alpha,.2f*alpha,.1f*alpha,alpha);float error=Error(Center(),expected);
                        Check("actor-type-"+type+"-independent-rgba",error<.001f,error);
                        Check("actor-type-"+type+"-source-material-retained",actorRenderer.sharedMaterial==actorMaterial&&actorSet.Draws[0].material!=actorMaterial);
                    }
                    actorMaterial.SetFloat("_ShaderType",0);actorMaterial.SetFloat("_SrcBlend",1);actorMaterial.SetFloat("_DstBlend",0);actorMaterial.SetFloat("_ZWrite",1);
                    actorMaterial.SetFloat("_UseAlphaClip",1);actorMaterial.SetFloat("_Cutoff",.5f);Refresh();Run("actor-cutout-reject",false);
                    Check("actor-cutout-clears-color-and-coverage",Array.TrueForAll(Read(current.capture),p=>p==Color.clear));
                    actorMaterial.SetFloat("_UseAlphaClip",0);
                    var block=new MaterialPropertyBlock();block.SetVector("_Color",new Vector4(0,1,0,1));actorRenderer.SetPropertyBlock(block);Refresh();Run("actor-property-block",false);
                    Check("actor-property-block-snapshot",Center().r==0&&Center().g>.19f&&Center().b==0);
                    actorRenderer.SetPropertyBlock(null);
                    var eye=Quad("Reduced stencil eye",new Vector3(0,1,-1.8f),new Vector3(2.4f,.7f,1),true);
                    var eyeMaterial=Own(new Material(actorMaterial));eye.GetComponent<Renderer>().sharedMaterial=eyeMaterial;
                    eyeMaterial.SetFloat("_ShaderType",4);eyeMaterial.SetFloat("_ZWrite",0);eyeMaterial.SetFloat("_SrcBlend",1);eyeMaterial.SetFloat("_DstBlend",10);eyeMaterial.renderQueue=2002;
                    eyeMaterial.SetTexture("_MainTex",ResolveTexture(new[]{new Color(0,.8f,0,.5f)},1,1));
                    eyeMaterial.SetFloat("_StencilRef",32);eyeMaterial.SetFloat("_StencilReadMask",32);eyeMaterial.SetFloat("_StencilComp",3);
                    actorMaterial.SetFloat("_StencilRef",32);actorMaterial.SetFloat("_StencilWriteMask",32);actorMaterial.SetFloat("_StencilPass",2);
                    actorRenderers=new[]{eye.GetComponent<Renderer>(),actorRenderer};Refresh();Run("actor-stencil-eye",false);
                    var stencil=Read(current.capture);float stencilError=Error(Center(),new Color(.4f,.5f,.05f,1));Check("actor-stencil-interior",stencilError<.002f,stencilError);
                    eyeMaterial.SetFloat("_StencilComp",8);Refresh();Run("actor-stencil-negative-control",false);
                    var leaked=Read(current.capture);int leaking=0;
                    for(int p=0;p<leaked.Length;p++)if(stencil[p].a==0&&leaked[p].a>.1f)leaking++;
                    Check("actor-stencil-rejects-real-outside-coverage",leaking>20,leaking);
                    eyeMaterial.SetFloat("_StencilComp",3);Refresh();Run("actor-stencil-restored",false);
                    Check("actor-stencil-cleared-replayed-every-frame",ScenePixelsEqual(stencil,Read(current.capture)));
                    actor.SetActive(false);eye.SetActive(false);
                }
                // Positive SSR counterexample: the same floor pixels really hit scene
                // walls without Planar, then disappear from trace when Planar takes priority.
                camera.transform.position=new Vector3(0,2.5f,-4);camera.transform.LookAt(new Vector3(0,.5f,3));
                receiver.transform.SetPositionAndRotation(new Vector3(0,0,3),Quaternion.Euler(90,0,0));receiver.transform.localScale=new Vector3(12,12,1);
                red.surface.renderer.transform.SetPositionAndRotation(new Vector3(-1.5f,2,6),Quaternion.identity);red.surface.renderer.transform.localScale=new Vector3(3,4,1);
                green.surface.renderer.transform.SetPositionAndRotation(new Vector3(1.5f,2,6),Quaternion.identity);green.surface.renderer.transform.localScale=new Vector3(3,4,1);
                SceneDeferredCamera.Surface Wall(PlanarReflection.Draw draw,int group)
                {
                    var emission=draw.material.GetVector("_Emission");return new SceneDeferredCamera.Surface { renderer=draw.surface.renderer,receiverGroup=group,cull=CullMode.Off,
                        inputs=new SceneDeferredCamera.MaterialInputs { emission=new Vector3(emission.x,emission.y,emission.z),mos=Vector3.zero } };
                }
                sceneSettings.surfaces=new[]{surface,Wall(red,8),Wall(green,9)};settings.draws=new[]{red,green};settings.planeNormal=Vector3.up;
                surface.inputs.mos.z=.8f;
                Run("floor-without-planar-cold",false,false);Run("floor-without-planar-warm",false,false);var positive=Read(resolved.rawReflection);
                Check("floor-ssr-positive-control",SsrHitCount(positive)>30,SsrHitCount(positive));
                Run("floor-planar-priority",false);var priority=Read(resolved.rawReflection);var floorProjection=Read(current.reflection);int suppressed=0;
                for(int p=0;p<priority.Length;p++)if(positive[p].a>.01f&&floorProjection[p].a>1e-5f&&priority[p].a==0)suppressed++;
                Check("real-ssr-hits-suppressed-by-planar",suppressed>30,suppressed);
                rs.backend=SceneShaderBackend.Compute;Run("floor-compute-cold",false);Run("floor-compute-warm",false);
                rs.roughness.enabled=true;rs.roughness.maximumRadiusPixels=12;rs.roughness.referenceHeight=height;Run("floor-compute-filtered",false);
                rs.backend=SceneShaderBackend.Raster;Run("floor-raster-filtered-cold",false);Run("floor-raster-filtered-warm",false);
                settings.draws=new[]{red};Run("floor-partial-planar-filtered",false);
                Check("partial-planar-keeps-real-uncovered-ssr",SsrHitCount(Read(resolved.reflection))>30,SsrHitCount(Read(resolved.reflection)));
                var probe=Own(new Cubemap(1,TextureFormat.RGBAHalf,false));for(int face=0;face<6;face++)probe.SetPixels(new[]{new Color(1,2,4,1)},(CubemapFace)face);probe.Apply(false,false);rs.probe=probe;
                settings.strength=.5f;Run("floor-partial-coverage-probe",false);settings.strength=1;
                var map=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));map.SetPixel(0,0,new Color(.8f,.5f,.9f,1));map.Apply(false,false);
                surface.inputs.normalMap=map;rs.normalDistortion=new Vector2(.1f,-.1f);Run("floor-planar-normal-difference",false);
                rs.normalDistortion=new Vector2(1,-1);Run("floor-planar-outside-probe",false);
                surface.inputs.normalMap=null;rs.normalDistortion=Vector2.zero;
                var lost=current;current.capture.Release();Check("lost-capture-invalidates-producer-and-consumer",!lost.IsCurrent&&!resolved.IsCurrent);
                Run("lost-capture-recovery",false);
                var ticket=current;planar.Dispose();Check("producer-disposal-invalidates-consumer",!ticket.IsCurrent&&!resolved.IsCurrent&&output.IsCreated());
            }
            finally
            {
                RenderTexture.active=oldActive;resolver?.Dispose();planar?.Dispose();foreach(var scene in scenes)scene.Dispose();
                GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;
                foreach(var o in _owned)if(o is GameObject go) { var camera=go.GetComponent<Camera>();if(camera!=null)camera.targetTexture=null;go.SetActive(false); }
                foreach(var o in _owned)if(o is RenderTexture t)t.Release();
            }
        }
    }
}
