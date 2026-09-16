using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        [Serializable] private sealed class DesktopSkinDiagnostic
        {
            public Matrix4x4 bone0,bone1,root,view,projection;
            public Vector3[] source,expectedWorld,blendDelta;public Vector4[] sourceTangents;
            public Vector4 wardrobe,outlineWidths;public float blendWeight;
        }
        private IEnumerator VerifyDesktopHost(Report report)
        {
            yield return null;
            var oldGraphics=GraphicsSettings.renderPipelineAsset;var oldQuality=QualitySettings.renderPipeline;var oldActive=RenderTexture.active;
            DesktopFrameRenderer host=null;ColorGradingLut lut=null;
            using var controlFx=new HeavyFxRenderer();using var controlDof=new BokehDepthOfFieldRenderer();using var controlGrade=new ColorGradingRenderer();
            void Check(string n,bool accepted,float value=0)=>FrameworkCheck(report,"desktop-host-"+n,accepted,value);
            try
            {
                const int width=129,height=97;
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                var camera=Own(new GameObject("Desktop joined pipeline camera")).AddComponent<Camera>();camera.enabled=false;camera.allowMSAA=false;
                camera.orthographic=true;camera.orthographicSize=2;camera.aspect=(float)width/height;camera.nearClipPlane=.1f;camera.farClipPlane=20;camera.cullingMask=1<<22;
                camera.transform.position=new Vector3(0,0,-4);
                RenderTexture Target(GraphicsFormat color,GraphicsFormat depth=GraphicsFormat.None)
                {var t=Own(new RenderTexture(new RenderTextureDescriptor(width,height,color,0){depthStencilFormat=depth}));if(!t.Create())throw new InvalidOperationException("Desktop target");return t;}
                var output=Target(GraphicsFormat.B10G11R11_UFloatPack32);camera.targetTexture=output;
                var readback=Target(GraphicsFormat.R32G32B32A32_SFloat);
                GameObject Quad(string name,float z,Vector3 scale)
                {var go=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));go.name=name;go.layer=22;go.transform.position=new Vector3(0,0,z);go.transform.localScale=scale;return go;}
                var background=Quad("Scene only receiving wall",2,new Vector3(7,5,1)).GetComponent<Renderer>();
                var actor=Quad("Full Actor current geometry",0,new Vector3(1.5f,1.7f,1)).GetComponent<Renderer>();
                var material=Own(new Material(Resources.Load<Shader>("PhotoModeFallback")));actor.sharedMaterial=material;
                material.SetFloat("_OutlineEnabled",0);material.SetFloat("_VertexColor",0);material.SetFloat("_Cull",0);
                material.SetColor("_Color",new Color(2,.125f,.25f,1));
                var fx=Quad("Current full transparent layer",-.5f,new Vector3(3,2.5f,1)).GetComponent<Renderer>();fx.enabled=false;
                var s=new DesktopFrameRenderer.Settings { enabled=true };s.scene.enabled=true;s.scene.backend=TileRenderPass.BackendPolicy.AllowEmulation;
                s.scene.geometryDepthId=true;s.scene.positionLighting=true;s.scene.output=output;s.scene.depthStencil=Target(GraphicsFormat.None,GraphicsFormat.D32_SFloat_S8_UInt);
                s.scene.normalIdentity=Target(GraphicsFormat.R16G16B16A16_SFloat);s.scene.materialBase=Target(GraphicsFormat.R8G8B8A8_SRGB);s.scene.materialMos=Target(GraphicsFormat.R8G8B8A8_UNorm);
                var surface=new SceneDeferredCamera.Surface { renderer=background,cull=CullMode.Off };
                surface.inputs.albedo=new Vector3(.25f,.5f,.75f);surface.inputs.mos=new Vector3(.5f,1,.8f);surface.inputs.emission=Vector3.one*.025f;
                var detail=Quad("Background DOF detail",1.9f,new Vector3(.35f,.45f,1));detail.transform.position=new Vector3(1.8f,.9f,1.9f);
                var detailSurface=new SceneDeferredCamera.Surface { renderer=detail.GetComponent<Renderer>(),cull=CullMode.Off };
                detailSurface.inputs.albedo=Vector3.zero;detailSurface.inputs.mos=new Vector3(0,1,0);detailSurface.inputs.emission=new Vector3(.6f,.05f,.025f);
                var sceneSurfaces=new[]{surface,detailSurface};s.scene.surfaces=sceneSurfaces;
                var reflectedEmitter=Quad("Explicit reduced mirror emitter",0,new Vector3(.75f,.75f,1)).GetComponent<Renderer>();
                reflectedEmitter.transform.position=new Vector3(-1.7f,1.1f,0);
                var mirrorMaterial=Own(new Material(Resources.Load<Shader>("PlanarCapture")));
                mirrorMaterial.SetColor("_Color",Color.white);mirrorMaterial.SetColor("_AmbientColor",Color.black);mirrorMaterial.SetColor("_LightColor",Color.black);
                mirrorMaterial.SetVector("_Emission",new Vector4(.2f,2,.5f,0));mirrorMaterial.SetFloat("_Cull",(float)CullMode.Off);
                s.planar.planePoint=new Vector3(0,0,2);s.planar.planeNormal=Vector3.back;s.planar.resolutionScale=1;s.planar.maximumRoughnessMip=0;s.planar.receiverGroup=surface.receiverGroup;
                s.planar.draws=new[]{new PlanarReflection.Draw { surface=new SceneDepthData.Surface { renderer=reflectedEmitter,cull=CullMode.Off },material=mirrorMaterial }};
                s.scene.lightDirection=Vector3.back;s.scene.lightRadiance=Vector3.one*.05f;s.scene.directionalSpecularScale=.1f;s.scene.ambientIrradiance=Vector3.one*.1f;
                s.actors.renderers=new[]{actor};s.actors.outlines=false;s.actors.hairCover=false;s.actors.parameters.SetFloat("_FaceDebugMode",7);
                s.selfShadow.enabled=true;s.selfShadow.origin=new Vector3(0,0,-3);s.selfShadow.halfSize=Vector2.one*3;s.selfShadow.farPlane=10;s.selfShadow.resolution=256;s.selfShadowDirection=Vector3.back;
                s.scene.mainLightShadow=new SceneDirectionalShadowSettings { enabled=true,origin=new Vector3(-2,0,-3),halfSize=Vector2.one*4,farPlane=10,resolution=256 };
                s.reflections.enabled=true;s.reflections.sceneOnlyInput=true;s.reflections.normalDistortion=Vector2.zero;
                var cube=Own(new Cubemap(2,TextureFormat.RGBAHalf,false));
                for(int face=0;face<6;face++)cube.SetPixels(new[]{new Color(.2f,.4f,.6f),new Color(.2f,.4f,.6f),new Color(.2f,.4f,.6f),new Color(.2f,.4f,.6f)},(CubemapFace)face);cube.Apply();s.reflections.probe=cube;
                var fxSurface=new LowResolutionFxSurface { mesh=fx.GetComponent<MeshFilter>().sharedMesh,localToWorld=fx.localToWorldMatrix,
                    resolution=FxResolution.Full,linearRadiance=new Vector3(.125f,.25f,1),opacity=.4f,blend=FxBlend.Alpha };
                s.effects.enabled=true;s.effects.geometry.enabled=true;s.effects.geometry.surfaces=new[]{fxSurface};
                var gradeFilter=new Vector3(.125f,.2f,.1f);
                lut=ColorGradingLut.Bake(new ColorGradingProfile { size=16,domain=ColorLutDomain.Linear,maximumInput=4,toneMapping=ColorToneMapping.Clip,colorFilter=gradeFilter });s.colorGrade=lut;
                host=new DesktopFrameRenderer(camera,s);ulong sequence=0;DesktopFrameRenderer.Frame previous=default;
                // Read the producer, not Unity's default Blit shader: the latter can
                // change sample precision even when both textures store float32.
                Color[] Read(RenderTexture t)=>ReadSceneTarget(t);
                float Difference(Color[] a,Color[] b){float e=0;for(int p=0;p<a.Length;p++)for(int c=0;c<4;c++)e=Mathf.Max(e,Mathf.Abs(a[p][c]-b[p][c]));return e;}
                void Save(string name,Color[] pixels)
                {SaveSsrPreview("desktop-host-"+name,pixels,width,height,false);using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"desktop-host-"+name+".raw")));foreach(var p in pixels)for(int c=0;c<4;c++)writer.Write(p[c]);}
                Color[] Run(string name,bool warm)
                {
                    string selected=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_HOST_CASE")??"cold";
                    bool capture=name==selected&&Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_HOST")=="1";
                    bool began=capture&&RenderDocCaptureBridge.BeginOffscreenCapture();
                    try
                    {
                    if(!ActorShadowInputs.TryCapture(s.actors.renderers,0,out var casters,out var error))throw new InvalidOperationException(error);
                    s.selfShadow.casters=casters;s.scene.mainLightShadow.casters=casters;
                    DesktopFrameRenderer.OpaqueFrame opaque=default;sequence++;
                    RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                        if(!host.TryRecord(context,sequence,1,out opaque,out var why))throw new InvalidOperationException(why);
                    }});
                    Check(name+"-opaque-current",opaque.IsCurrent&&host.HasPendingWork&&opaque.selfShadow.HasValue&&opaque.selfShadow.Value.IsCurrent);
                    Check(name+"-scene-history-state",host.UsedReflectionHistory==warm);
                    RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>Check(name+"-reject-record-before-retire",!host.TryRecord(context,sequence+1,1,out _,out _)) });
                    if(s.planar.enabled)
                    {
                        Check(name+"-current-planar-and-reflection-tickets",opaque.planar.HasValue&&opaque.planar.Value.IsCurrent&&opaque.reflections.HasValue&&opaque.reflections.Value.IsCurrent);
                        var planarPixels=Read(opaque.planar.Value.reflection);int covered=0;foreach(var p in planarPixels)if(p.a>.01f)covered++;
                        Check(name+"-real-projected-planar-coverage",covered>100,covered);Save(name+"-planar",planarPixels);
                    }
                    var sceneBefore=Read(output);var depthBefore=Read(opaque.actors.eyeDepth);var actorBefore=Read(opaque.actors.color);
                    if(!host.TryFinishAfterSubmission(opaque,.75,out var frame,out error))throw new InvalidOperationException(error);
                    var final=Read(frame.color); // Synchronous readback also establishes GPU completion before retirement.
                    Check(name+"-final-current",frame.IsCurrent&&frame.opaque.IsCurrent&&!previous.IsCurrent);
                    Check(name+"-no-double-finish",!host.TryFinishAfterSubmission(opaque,.75,out _,out _));
                    Check(name+"-scene-input-preserved",Difference(sceneBefore,Read(output))==0);
                    Check(name+"-opaque-depth-preserved",Difference(depthBefore,Read(frame.eyeDepth))==0);
                    bool finite=true;foreach(var p in final)for(int c=0;c<4;c++)finite&=!float.IsNaN(p[c])&&!float.IsInfinity(p[c]);Check(name+"-finite",finite);
                    if(!controlFx.TryRender(opaque.actors.color,new FogVolumeDepth(opaque.actors.eyeDepth),camera,s.effects,.75,out var cf))throw new InvalidOperationException(controlFx.UnavailableReason);
                    var fxPixels=Read(cf.color);float fxResponse=Difference(fxPixels,actorBefore);
                    if(name=="cold")
                    {
                        Graphics.Blit(cf.color,readback);var viaBlit=Read(readback);
                        float conversion=Difference(fxPixels,viaBlit);
                        Check("default-blit-readback-difference-metric",!float.IsNaN(conversion),conversion);
                        Save("cold-fx-direct",fxPixels);Save("cold-fx-default-blit",viaBlit);
                        if(!cf.TryGetLastBatch(FxResolution.Full,out var effect,out _))throw new InvalidOperationException("Missing current full FX batch");
                        Save("cold-fx-effect",Read(effect));
                    }
                    Check(name+"-transparent-draw-positive-control",fxResponse>.01f,fxResponse);
                    if(fxSurface.resolution==FxResolution.Full)
                    {
                        var expected=(Color[])actorBefore.Clone();int visibleFx=0,occludedFx=0;
                        var inverse=fxSurface.localToWorld.inverse;var origin=fxSurface.localToWorld.MultiplyPoint(Vector3.zero);
                        var normal=fxSurface.localToWorld.MultiplyVector(Vector3.forward).normalized;
                        for(int p=0;p<expected.Length;p++)
                        {
                            var ray=camera.ViewportPointToRay(new Vector3((p%width+.5f)/width,(p/width+.5f)/height,0));
                            float t=Vector3.Dot(origin-ray.origin,normal)/Vector3.Dot(ray.direction,normal);
                            var point=ray.GetPoint(t);var local=inverse.MultiplyPoint(point);float eye=-camera.worldToCameraMatrix.MultiplyPoint(point).z;
                            if(t>0&&Mathf.Abs(local.x)<.5f&&Mathf.Abs(local.y)<.5f)
                            {
                                if(depthBefore[p].r<=0||eye<=depthBefore[p].r+s.effects.geometry.depthBias)
                                {for(int c=0;c<3;c++)expected[p][c]=fxSurface.opacity*fxSurface.linearRadiance[c]+(1-fxSurface.opacity)*expected[p][c];visibleFx++;}
                                else occludedFx++;
                            }
                        }
                        float fxError=Difference(expected,fxPixels);Check(name+"-full-transparent-independent-ray-blend",fxError<.00002f,fxError);
                        Check(name+"-ray-covered-pixels",visibleFx>100,visibleFx);
                        if(origin.z>0)Check(name+"-actor-depth-occludes-transparent",occludedFx>100,occludedFx);
                        Save(name+"-fx-oracle",expected);
                    }
                    var control=cf.color;
                    if(s.depthOfField.enabled)
                    {
                        if(!controlDof.TryRender(control,opaque.actors.eyeDepth,s.depthOfField,out var df))throw new InvalidOperationException(controlDof.UnavailableReason);
                        control=df.color;float dofResponse=Difference(fxPixels,Read(control));Check(name+"-dof-positive-control",dofResponse>.001f,dofResponse);
                    }
                    if(!controlGrade.TryRender(control,lut,out var cg))throw new InvalidOperationException(controlGrade.UnavailableReason);
                    float errorValue=Difference(final,Read(cg.color));Check(name+"-explicit-module-chain-whole-color",errorValue==0,errorValue);
                    var gradeInput=Read(control);var expectedGrade=(Color[])gradeInput.Clone();
                    for(int p=0;p<expectedGrade.Length;p++)for(int c=0;c<3;c++)expectedGrade[p][c]=Mathf.Clamp(expectedGrade[p][c],0,4)*gradeFilter[c];
                    float gradeError=Difference(final,expectedGrade);Check(name+"-ideal-linear-lut-equation-error-metric",!float.IsNaN(gradeError),gradeError);
                    VerifyDesktopGradeWeights(report,name,control,gradeInput,final,lut,gradeFilter);
                    float response=Difference(final,actorBefore);Check(name+"-effects-post-positive-control",response>.1f,response);
                    Save(name+"-scene",sceneBefore);Save(name+"-actor",actorBefore);Save(name+"-final",final);Save(name+"-depth",depthBefore);
                    Save(name+"-grade-input",gradeInput);
                    previous=frame;host.RetireAfterGpuCompletion();Check(name+"-retired",!frame.IsCurrent&&!opaque.IsCurrent&&!host.HasPendingWork);return final;
                    }
                    finally { if(capture)Check("requested-native-capture",began&&RenderDocCaptureBridge.EndOffscreenCapture()); }
                }
                var first=Run("cold",false);var second=Run("warm",true);Check("stable-repeat",Difference(first,second)==0);
                actor.transform.position=new Vector3(.5f,.2f,0);var moved=Run("moved-actor",true);float motion=Difference(second,moved);Check("motion-changes-final",motion>.1f,motion);
                fxSurface.localToWorld=Matrix4x4.TRS(new Vector3(0,0,.5f),Quaternion.identity,new Vector3(3,2.5f,1));Run("behind-actor",true);
                fxSurface.resolution=FxResolution.Half;Run("half-effects",true);
                s.depthOfField.enabled=true;s.depthOfField.focusNear=3;s.depthOfField.focusFar=4.5f;s.depthOfField.farTransition=1;s.depthOfField.maximumRadius=.04f;Run("depth-of-field",true);
                s.depthOfField.enabled=false;fxSurface.resolution=FxResolution.Full;
                var noPlanar=Run("before-planar",true);s.planar.enabled=true;var withPlanar=Run("planar-joined",true);
                float planarResponse=Difference(noPlanar,withPlanar);Check("planar-changes-integrated-final",planarResponse>.001f,planarResponse);
                var planarRepeat=Run("planar-warm",true);Check("planar-stable-repeat",Difference(withPlanar,planarRepeat)==0);
                s.planar.enabled=false;var restored=Run("planar-disabled-restored",true);Check("planar-disabled-whole-color-restored",Difference(noPlanar,restored)==0);
                Check("reject-default-finish",!host.TryFinishAfterSubmission(default,0,out _,out _));
                Check("reject-retired-finish",!host.TryFinishAfterSubmission(previous.opaque,0,out _,out _));
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                    Check("reject-zero-sequence",!host.TryRecord(context,0,1,out _,out _));
                    Check("reject-repeated-sequence",!host.TryRecord(context,sequence,1,out _,out _));
                }});
                s.scene.surfaces=new[]{surface,new SceneDeferredCamera.Surface { renderer=actor }};
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>Check("reject-actor-in-scene-history",!host.TryRecord(context,++sequence,1,out _,out _)) });
                Check("invalid-input-no-pending-work",!host.HasPendingWork);s.scene.surfaces=sceneSurfaces;
                DesktopFrameRenderer.OpaqueFrame failed=default;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                    if(!host.TryRecord(context,++sequence,1,out failed,out var why))throw new InvalidOperationException(why);
                }});
                Check("reject-nonfinite-time-retains-opaque",!host.TryFinishAfterSubmission(failed,double.NaN,out _,out _)&&failed.IsCurrent);
                fxSurface.opacity=float.NaN;
                Check("post-failure-invalidates-opaque-requires-retire",!host.TryFinishAfterSubmission(failed,0,out _,out var failure)&&failure!=null&&!failed.IsCurrent&&host.HasPendingWork);
                Read(failed.actors.color);host.RetireAfterGpuCompletion();fxSurface.opacity=.4f;
                Run("recovered-after-failed-post",false);
                s.effects.enabled=false;s.depthOfField.enabled=false;s.colorGrade=null;s.selfShadow.enabled=false;s.reflections.enabled=false;
                DesktopFrameRenderer.OpaqueFrame bare=default;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                    if(!host.TryRecord(context,++sequence,1,out bare,out var why))throw new InvalidOperationException(why);
                }});
                var bareInput=Read(bare.actors.color);
                Check("optional-producers-disabled",!bare.selfShadow.HasValue&&!bare.reflections.HasValue&&!bare.planar.HasValue);
                Check("disabled-post-finish",host.TryFinishAfterSubmission(bare,0,out var bareFinal,out _)&&bareFinal.IsCurrent);
                Check("disabled-post-exact-passthrough",Difference(bareInput,Read(bareFinal.color))==0&&bareFinal.color==bare.actors.color);
                host.RetireAfterGpuCompletion();
                // Appended storage controls keep every prior fixture unchanged.
                s.effects.enabled=true;s.depthOfField.enabled=true;s.colorGrade=lut;
                VerifyDesktopStorage(report,host,camera,s,ref sequence);
                var skinFixture=VerifyDesktopSkinMotion(report,host,camera,s,sequence,value=>sequence=value);
                while(skinFixture.MoveNext())yield return skinFixture.Current;
                VerifyDesktopMotionBlur(report,host,camera,s,ref sequence);
                VerifyDesktopBloom(report,host,camera,s,ref sequence);
                VerifyDesktopFsr(report,host,camera,s,ref sequence);
                VerifyDesktopDiffusion(report,host,camera,s,ref sequence);
                host.Dispose();
                Check("storage-dispose-preserves-borrowed-scene",s.scene.output.IsCreated()&&s.scene.depthStencil.IsCreated()&&s.scene.normalIdentity.IsCreated());
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>Check("disposed-host-rejects-record",!host.TryRecord(context,++sequence,1,out _,out _)) });
            }
            finally
            {
                host?.Dispose();lut?.Dispose();GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;RenderTexture.active=oldActive;
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
            VerifyDesktopExample(report);
            VerifyExplicitColorLut(report);
            VerifyProjectionJitter(report);
        }

        private IEnumerator VerifyDesktopSkinMotion(Report report,DesktopFrameRenderer host,Camera camera,DesktopFrameRenderer.Settings settings,ulong sequence,Action<ulong> completed)
        {
            void Check(string n,bool ok,float error=0)=>FrameworkCheck(report,"desktop-skin-motion-"+n,ok,error);
            int width=settings.scene.output.width,height=settings.scene.output.height;
            int rasterBits=8;
            if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Vulkan&&
                (!int.TryParse(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_RASTER_SUBPIXEL_BITS"),out rasterBits)||rasterBits<4||rasterBits>16))
                throw new InvalidOperationException("Skin oracle requires independently queried raster subpixel precision");
            float rasterScale=1<<rasterBits;
            var source=settings.actors.renderers[0];
            var mesh=Own(Instantiate(source.GetComponent<MeshFilter>().sharedMesh));
            var vertices=mesh.vertices;var triangles=mesh.triangles;
            var weights=new BoneWeight[vertices.Length];var blendDelta=new Vector3[vertices.Length];
            var authoredTangents=new Vector4[vertices.Length];
            var authoredColors=new Color32[vertices.Length];
            for(int i=0;i<vertices.Length;i++)
            {
                float t=(vertices[i].x+.5f)*.6f;
                weights[i]=new BoneWeight {boneIndex0=0,weight0=1-t,boneIndex1=1,weight1=t};
                blendDelta[i]=new Vector3(.02f*i,.013f*i,-.018f*i);
                authoredTangents[i]=new Vector4(vertices[i].x*2,vertices[i].y*2,-.25f,1);
                authoredColors[i]=new Color32(0x12,0x30,0x7f,0xff);
            }
            // Establish tangent data before constructing blend-shape/skinning
            // buffers; late mutation can leave the engine's bound skin stream
            // using its original tangents, despite the CPU Mesh array changing.
            mesh.tangents=authoredTangents;
            mesh.colors32=authoredColors;
            mesh.boneWeights=weights;mesh.bindposes=new[]{Matrix4x4.identity,Matrix4x4.identity};
            mesh.AddBlendShapeFrame("Independent Actor blend deformation",100,blendDelta,new Vector3[vertices.Length],new Vector3[vertices.Length]);
            var skinObject=Own(new GameObject("Independent two-bone Actor motion"));skinObject.layer=source.gameObject.layer;
            var bone0=Own(new GameObject("Actor oracle bone0")).transform;bone0.SetParent(skinObject.transform,false);
            var bone1=Own(new GameObject("Actor oracle bone1")).transform;bone1.SetParent(skinObject.transform,false);
            var skin=skinObject.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=mesh;skin.bones=new[]{bone0,bone1};skin.rootBone=bone0;
            skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*10);
            skinObject.transform.localScale=new Vector3(1.5f,1.7f,1.2f);
            var material=Own(new Material(source.sharedMaterial));skin.sharedMaterial=material;
            material.SetVector("_WardrobeScaleCorrection",Vector4.one);material.SetFloat("_Cull",0);
            settings.actors.renderers=new Renderer[]{skin};settings.actors.outlines=false;settings.actors.hairCover=false;
            settings.effects.enabled=settings.temporal.enabled=settings.depthOfField.enabled=false;settings.colorGrade=null;
            settings.reflections.enabled=settings.planar.enabled=settings.selfShadow.enabled=false;settings.scene.mainLightShadow.enabled=false;
            settings.actorMotion.enabled=true;settings.includeSceneMotion=settings.reuseSceneMotionStorage=false;
            settings.actorStorage=SrpActorForward.Storage.SeparateHalf;
            host.ResetHistoryAfterGpuCompletion();
            Vector3[] oldWorld=null;Matrix4x4 oldVp=default,oldView=default,oldProjection=default;
            int oldDepthNibble=0;
            bool outlineOracle=false;int surfaceIdentity=0;
            var outlineWidths=new Vector4(2,10,.25f,1);
            Vector3[] WorldVertices()
            {
                // Independently evaluate authored bind poses, weights and blend
                // deltas. No BakeMesh and no producer clip-atlas readback.
                var world=new Vector3[vertices.Length];
                var root=Matrix4x4.TRS(bone0.position,bone0.rotation,Vector3.one);
                var scale=(Vector3)material.GetVector("_WardrobeScaleCorrection");
                for(int i=0;i<world.Length;i++)
                {
                    var v=vertices[i]+blendDelta[i]*(skin.GetBlendShapeWeight(0)/100);
                    var w=bone0.localToWorldMatrix.MultiplyPoint3x4(v)*weights[i].weight0+
                        bone1.localToWorldMatrix.MultiplyPoint3x4(v)*weights[i].weight1;
                    // Unity's skinned draw stream uses root-bone TR with unit
                    // scale; wardrobe correction is applied in that basis.
                    world[i]=root.MultiplyPoint3x4(Vector3.Scale(root.inverse.MultiplyPoint3x4(w),scale));
                    if(outlineOracle)
                    {
                        float blend=Mathf.Clamp01(Vector3.Distance(world[i],camera.transform.position)*outlineWidths.z*outlineWidths.w);
                        float extrusion=Mathf.Lerp(outlineWidths.x,outlineWidths.y,blend)*.01f;
                        var tangent=(Vector3)authoredTangents[i];
                        var worldTangent=bone0.localToWorldMatrix.MultiplyVector(tangent)*weights[i].weight0+
                            bone1.localToWorldMatrix.MultiplyVector(tangent)*weights[i].weight1;
                        // Preserve the authored tangent length. Extrusion and
                        // wardrobe scaling happen in the skinned root basis.
                        world[i]+=root.MultiplyVector(Vector3.Scale(root.inverse.MultiplyVector(worldTangent)*extrusion,scale));
                    }
                }
                return world;
            }
            void Case(string name,bool cold=false,bool requireMotion=true)
            {
                var world=WorldVertices();var view=camera.worldToCameraMatrix;var vp=camera.projectionMatrix*view;
                var diagnostic=new DesktopSkinDiagnostic {bone0=bone0.localToWorldMatrix,bone1=bone1.localToWorldMatrix,
                    root=Matrix4x4.TRS(bone0.position,bone0.rotation,Vector3.one),view=view,projection=camera.projectionMatrix,
                    source=vertices,expectedWorld=world,blendDelta=blendDelta,sourceTangents=authoredTangents,
                    wardrobe=material.GetVector("_WardrobeScaleCorrection"),outlineWidths=outlineWidths,blendWeight=skin.GetBlendShapeWeight(0)};
                File.WriteAllText(Path.Combine(_directory,"desktop-skin-motion-"+name+".json"),JsonUtility.ToJson(diagnostic,true));
                bool requested=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_STORAGE_CASE")=="skin-"+name;
                bool began=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();
                DesktopFrameRenderer.OpaqueFrame opaque=default;
                Exception failure=null;
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                    try{if(!host.TryRecord(context,++sequence,1,out opaque,out var why))throw new InvalidOperationException(why);}
                    catch(Exception error){failure=error;}
                }});
                if(failure!=null)throw failure;
                var motion=ReadSceneTarget(opaque.actors.motionDepthIdentity);var previousDepth=ReadSceneTarget(opaque.actors.expectedPreviousDepth);
                if(!host.TryFinishAfterSubmission(opaque,0,out var frame,out var error))throw new InvalidOperationException(error);
                var color=ReadSceneTarget(frame.color);host.RetireAfterGpuCompletion();
                if(requested)Check(name+"-native-capture",began&&RenderDocCaptureBridge.EndOffscreenCapture());
                SaveSsrPreview("desktop-skin-motion-"+name,color,width,height,false);
                using(var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"desktop-skin-motion-"+name+".raw"))))
                    foreach(var pixel in motion)for(int c=0;c<4;c++)writer.Write(pixel[c]);
                int tested=0,moving=0;float uvError=0,depthError=0;bool valid=true;
                var clips=new Vector4[world.Length];var screen=new Vector3[world.Length];
                for(int i=0;i<world.Length;i++)
                {
                    var w=world[i];var c=vp*new Vector4(w.x,w.y,w.z,1);clips[i]=c;
                    screen[i]=new Vector3(Mathf.Round((c.x/c.w*.5f+.5f)*width*rasterScale)/rasterScale,
                        Mathf.Round((c.y/c.w*.5f+.5f)*height*rasterScale)/rasterScale,c.w);
                }
                for(int y=0;y<height;y+=2)for(int x=0;x<width;x+=2)for(int t=0;t<triangles.Length;t+=3)
                {
                    var a=screen[triangles[t]];var b=screen[triangles[t+1]];var c=screen[triangles[t+2]];
                    float det=(b.y-c.y)*(a.x-c.x)+(c.x-b.x)*(a.y-c.y);if(Mathf.Abs(det)<1e-6f)continue;
                    float u=((b.y-c.y)*(x+.5f-c.x)+(c.x-b.x)*(y+.5f-c.y))/det;
                    float v=((c.y-a.y)*(x+.5f-c.x)+(a.x-c.x)*(y+.5f-c.y))/det;
                    if(u<.05f||v<.05f||1-u-v<.05f)continue;
                    var actual=motion[y*width+x];tested++;
                    int identity=(int)actual.a>>4;
                    if(!outlineOracle&&surfaceIdentity==0)surfaceIdentity=identity;
                    if(outlineOracle)valid&=identity!=0&&identity!=surfaceIdentity;
                    if(cold){valid&=((int)actual.a&9)==1&&actual.r==0&&actual.g==0&&previousDepth[y*width+x].r==0;break;}
                    var bary=new Vector3(u/a.z,v/b.z,(1-u-v)/c.z);bary/=bary.x+bary.y+bary.z;
                    Vector4 nowClip=Vector4.zero,priorClip=Vector4.zero;Vector3 priorPoint=Vector3.zero;
                    for(int k=0;k<3;k++)
                    {
                        int i=triangles[t+k];var old=oldWorld[i];nowClip+=clips[i]*bary[k];
                        priorClip+=(oldVp*new Vector4(old.x,old.y,old.z,1))*bary[k];priorPoint+=old*bary[k];
                    }
                    var expected=new Vector2((nowClip.x/nowClip.w-priorClip.x/priorClip.w)*.5f,
                        (nowClip.y/nowClip.w-priorClip.y/priorClip.w)*.5f);
                    valid&=((int)actual.a&9)==9;uvError=Mathf.Max(uvError,Mathf.Abs(expected.x-actual.r),Mathf.Abs(expected.y-actual.g));
                    float expectedDepth=-oldView.MultiplyPoint3x4(priorPoint).z;
                    if(oldDepthNibble>0)
                    {
                        // D3D11/Vulkan use a zero-to-one hardware depth range;
                        // the independent CPU projection is minus-one-to-one.
                        // Convert the authored away-from-camera clip offset to
                        // that range before reconstructing previous eye depth.
                        // Use the closed form in double precision: projecting
                        // near z/w=1 then inverting a float matrix cancels nearly
                        // equal values and biases this independent depth oracle.
                        double bias=2d*oldDepthNibble*.001/15;
                        expectedDepth=oldProjection.m32==-1
                            ?(float)(expectedDepth*oldProjection.m23/(oldProjection.m23+bias))
                            :(float)(expectedDepth-bias/oldProjection.m22);
                    }
                    depthError=Mathf.Max(depthError,Mathf.Abs(previousDepth[y*width+x].r-expectedDepth));
                    if(expected.magnitude>.0001f)moving++;break;
                }
                Check(name+"-independent-interior-coverage",tested>100&&valid,tested);
                if(!cold)
                {
                    Check(name+"-independent-two-bone-uv",uvError<.00004f,uvError);
                    Check(name+"-independent-two-bone-previous-depth",depthError<.00001f,depthError);
                    if(requireMotion)Check(name+"-deformation-positive-control",moving>50,moving);
                    else Check(name+"-stationary-negative-control",moving==0&&uvError==0,uvError);
                }
                oldWorld=world;oldView=view;oldVp=vp;oldProjection=camera.projectionMatrix;
                oldDepthNibble=outlineOracle&&material.GetFloat("_VertexColor")>.5f?7:0;
            }
            yield return null;yield return null;Case("cold",true);Case("stationary",false,false);
            for(int pose=1;pose<=3;pose++)
            {
                bone0.localPosition=new Vector3(-.015f*pose,.02f*pose,0);bone0.localRotation=Quaternion.Euler(2*pose,3*pose,-2*pose);
                bone1.localPosition=new Vector3(.025f*pose,-.01f*pose,-.015f*pose);bone1.localRotation=Quaternion.Euler(-3*pose,4*pose,3*pose);
                yield return null;yield return null;Case("bone-pose-"+pose);
            }
            skinObject.transform.localScale=new Vector3(1.6f,1.9f,1.1f);
            yield return null;yield return null;Case("nonuniform-root");
            skin.SetBlendShapeWeight(0,65);yield return null;yield return null;Case("blendshape");
            material.SetVector("_WardrobeScaleCorrection",new Vector4(1.12f,.91f,1.07f,0));Case("wardrobe-scale");
            camera.orthographic=false;camera.fieldOfView=48;camera.ResetProjectionMatrix();Case("perspective");
            var projection=camera.projectionMatrix;projection.m02+=.013f;projection.m12-=.009f;projection.m01+=.007f;camera.projectionMatrix=projection;
            bone1.localPosition+=new Vector3(.04f,.03f,-.02f);skin.SetBlendShapeWeight(0,25);
            yield return null;yield return null;Case("off-axis-blend-and-bone");
            // Back-facing authored geometry isolates the outline stream: the
            // ordinary main pass culls it, while Cull Front draws the extrusion.
            // This exercises outline identity and previous extruded positions,
            // rather than accidentally validating the unextruded main surface.
            for(int t=0;t<triangles.Length;t+=3){int index=triangles[t+1];triangles[t+1]=triangles[t+2];triangles[t+2]=index;}
            mesh.triangles=triangles;
            material.SetFloat("_Cull",(float)CullMode.Back);material.SetFloat("_OutlineEnabled",1);material.SetFloat("_VertexColor",0);
            settings.actors.parameters.SetVector("_ActorOutlineParameters",outlineWidths);settings.actors.outlines=true;outlineOracle=true;
            yield return null;yield return null;Case("outline-cold",true);Case("outline-stationary",false,false);
            bone0.localRotation=Quaternion.Euler(3,6,-2);bone1.localPosition+=new Vector3(.02f,-.035f,.02f);
            yield return null;yield return null;Case("outline-bone-deformation");
            skin.SetBlendShapeWeight(0,80);yield return null;yield return null;Case("outline-blendshape");
            outlineWidths=new Vector4(4,14,.2f,1);settings.actors.parameters.SetVector("_ActorOutlineParameters",outlineWidths);Case("outline-width-change");
            material.SetFloat("_VertexColor",1);Case("outline-packed-enable",false,false);Case("outline-packed-stationary",false,false);
            bone1.localPosition+=new Vector3(-.025f,.015f,-.03f);yield return null;yield return null;Case("outline-packed-bone-motion");
            completed(sequence);
        }

        private void VerifyDesktopStorage(Report report,DesktopFrameRenderer host,Camera camera,DesktopFrameRenderer.Settings settings,ref ulong sequence)
        {
            void Check(string n,bool ok,float v=0)=>FrameworkCheck(report,"desktop-storage-"+n,ok,v);
            float Difference(Color[] a,Color[] b){float e=0;for(int p=0;p<a.Length;p++)for(int c=0;c<4;c++)e=Mathf.Max(e,Mathf.Abs(a[p][c]-b[p][c]));return e;}
            ulong serial=sequence;
            Color[] lastMotion=null,lastExpectedDepth=null,lastTemporal=null,lastOpaque=null;
            Color[] lastPostDepth=null,lastCoC=null;
            Color[] Run(string name,SrpActorForward.Storage storage,out Color[] depth,bool reset=true,bool? reflectionHistory=null)
            {
                settings.actorStorage=storage;if(reset)host.ResetHistoryAfterGpuCompletion();
                bool reuse=storage==SrpActorForward.Storage.ReuseScenePacked;
                DesktopFrameRenderer.OpaqueFrame opaque=default;
                bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_STORAGE_CASE")==name;
                bool began=capture&&RenderDocCaptureBridge.BeginOffscreenCapture();
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                    if(!host.TryRecord(context,++serial,1,out opaque,out var why))throw new InvalidOperationException(why);
                    var scene=opaque.Scene;
                    Check(name+"-scene-content-state",scene.IsRecorded&&scene.SceneContentAvailable==(!reuse&&!settings.reuseSceneMotionStorage));
                    Check(name+"-exact-color-alias",(opaque.actors.color==settings.scene.output)==reuse);
                    if(settings.actorMotion.enabled)Check(name+"-exact-motion-alias",(opaque.actors.motionDepthIdentity==settings.scene.normalIdentity)==settings.reuseSceneMotionStorage);
                    if(reuse||settings.reuseSceneMotionStorage)
                    {
                        using var lateReflection=new SrpTileReflection(camera,settings.reflections);
                        using var latePlanar=new SrpTilePlanarReflection(camera,settings.planar);
                        using var lateActor=new SrpActorForward(camera,new SrpActorForward.Settings { enabled=true });
                        if(!ActorForwardDrawSet.TryPrepare(camera,settings.actors,out var draws,out why))throw new InvalidOperationException(why);
                        using(draws)
                        {
                            Check(name+"-reject-late-scene-reflection",!lateReflection.TryRecord(context,scene,serial+1,1,out _,out _));
                            Check(name+"-reject-late-scene-planar",!latePlanar.TryRecord(context,scene,serial+1,out _,out _));
                            Check(name+"-reject-second-actor-consumer",!lateActor.TryRecord(context,scene,draws,serial+1,out _,out _));
                        }
                    }
                }});
                Check(name+"-scene-only-history-state",host.UsedReflectionHistory==(reflectionHistory??(settings.reflections.enabled&&!reset)));
                var expectedFormat=storage==SrpActorForward.Storage.SeparateHalf?GraphicsFormat.R16G16B16A16_SFloat:GraphicsFormat.B10G11R11_UFloatPack32;
                Check(name+"-exact-native-format",opaque.actors.color.graphicsFormat==expectedFormat);
                long pixels=(long)settings.scene.output.width*settings.scene.output.height;
                Check(name+"-owned-attachment-budget",host.ActorNominalTextureBytes==pixels*(reuse?4:storage==SrpActorForward.Storage.SeparateHalf?20:16),host.ActorNominalTextureBytes);
                depth=ReadSceneTarget(opaque.actors.eyeDepth);
                lastMotion=opaque.actors.motionDepthIdentity!=null?ReadSceneTarget(opaque.actors.motionDepthIdentity):null;
                lastExpectedDepth=opaque.actors.expectedPreviousDepth!=null?ReadSceneTarget(opaque.actors.expectedPreviousDepth):null;
                lastOpaque=ReadSceneTarget(opaque.actors.color);
                if(!host.TryFinishAfterSubmission(opaque,.75,out var frame,out var error))throw new InvalidOperationException(error);
                bool depthProof=name.StartsWith("depth-align-",StringComparison.Ordinal);
                if(depthProof)
                {
                    lastPostDepth=ReadSceneTarget(frame.postEyeDepth);
                    lastCoC=frame.encodedCoC!=null?ReadSceneTarget(frame.encodedCoC):null;
                    Check(name+"-geometry-depth-preserved",frame.eyeDepth==opaque.actors.eyeDepth);
                    if(frame.temporalDepth.HasValue)
                    {
                        Check(name+"-aligned-frame-current",frame.temporalDepth.Value.IsCurrent&&frame.postEyeDepth==frame.temporalDepth.Value.eyeDepth);
                        Check(name+"-aligned-depth-format-budget",frame.postEyeDepth.graphicsFormat==GraphicsFormat.R32_SFloat&&host.TemporalDepthNominalTextureBytes==pixels*4,host.TemporalDepthNominalTextureBytes);
                    }
                    foreach(var item in new[]{Tuple.Create("depth",lastPostDepth),Tuple.Create("coc",lastCoC)})if(item.Item2!=null)
                    {
                        using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"desktop-storage-"+name+"-"+item.Item1+".raw")));
                        foreach(var pixel in item.Item2)for(int c=0;c<4;c++)writer.Write(pixel[c]);
                    }
                }
                lastTemporal=frame.temporal.HasValue?ReadSceneTarget(frame.temporal.Value.metadata):null;
                if(lastTemporal!=null)
                {
                    Check(name+"-temporal-current",frame.temporal.Value.IsCurrent);
                    Check(name+"-temporal-owned-budget",host.TemporalNominalTextureBytes==pixels*64,host.TemporalNominalTextureBytes);
                    using var guideWriter=new BinaryWriter(File.Create(Path.Combine(_directory,"desktop-storage-"+name+"-temporal.raw")));
                    foreach(var p in lastTemporal)for(int c=0;c<4;c++)guideWriter.Write(p[c]);
                }
                var result=ReadSceneTarget(frame.color);
                if(capture)Check(name+"-native-capture",began&&RenderDocCaptureBridge.EndOffscreenCapture());
                Check(name+"-post-chain-current",frame.IsCurrent&&opaque.IsCurrent);
                SaveSsrPreview("desktop-storage-"+name,result,settings.scene.output.width,settings.scene.output.height,false);
                using(var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"desktop-storage-"+name+".raw"))))
                    foreach(var p in result)for(int c=0;c<4;c++)writer.Write(p[c]);
                host.RetireAfterGpuCompletion();
                if(depthProof&&frame.temporalDepth.HasValue)Check(name+"-aligned-frame-retired",!frame.temporalDepth.Value.IsCurrent&&frame.postEyeDepth.IsCreated());
                Check(name+"-borrowed-targets-survive-retirement",settings.scene.output.IsCreated()&&settings.scene.depthStencil.IsCreated()&&settings.scene.normalIdentity.IsCreated()&&!frame.IsCurrent);
                return result;
            }
            // A material input must not alias either destructive output, even if
            // it was valid while preparing the scene. Failure must not consume it.
            foreach(var feedback in new[]{settings.scene.output,settings.scene.depthStencil})
            {
                settings.actorStorage=SrpActorForward.Storage.ReuseScenePacked;
                var old= settings.actors.configureMaterial;
                try
                {
                    settings.actors.configureMaterial=(r,i,m)=>{old?.Invoke(r,i,m);m.SetTexture("_MainTex",feedback);};
                    RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                        bool ok=host.TryRecord(context,++serial,1,out _,out var error);
                        Check((feedback==settings.scene.output?"color":"depth")+"-material-feedback-rejected",!ok&&error!=null&&error.Contains("sample"));
                    }});
                    ReadSceneTarget(settings.scene.output);host.RetireAfterGpuCompletion();
                }
                finally { settings.actors.configureMaterial=old; }
            }
            foreach(bool reflections in new[]{false,true})
            {
                settings.reflections.enabled=reflections;settings.planar.enabled=reflections;
                string prefix=reflections?"reflections":"direct";
                var half=Run(prefix+"-half",SrpActorForward.Storage.SeparateHalf,out var halfDepth);
                var packed=Run(prefix+"-packed",SrpActorForward.Storage.SeparatePacked,out var packedDepth);
                var reused=Run(prefix+"-reuse",SrpActorForward.Storage.ReuseScenePacked,out var reusedDepth);
                Check(prefix+"-reuse-whole-post-color-exact-packed-control",Difference(packed,reused)==0,Difference(packed,reused));
                Check(prefix+"-reuse-whole-depth-exact",Difference(halfDepth,reusedDepth)==0&&Difference(packedDepth,reusedDepth)==0,Difference(halfDepth,reusedDepth));
                Check(prefix+"-packed-precision-is-visible-metric",!float.IsNaN(Difference(half,packed)),Difference(half,packed));
                var repeated=Run(prefix+"-reuse-repeat",SrpActorForward.Storage.ReuseScenePacked,out _,false);
                Check(prefix+"-reuse-repeat-exact",Difference(reused,repeated)==0,Difference(reused,repeated));
                var restored=Run(prefix+"-half-restored",SrpActorForward.Storage.SeparateHalf,out _);
                Check(prefix+"-default-restored-exact",Difference(half,restored)==0,Difference(half,restored));
            }
            // Leave the compositor borrowing actual scene targets for Dispose coverage.
            Run("reuse-before-dispose",SrpActorForward.Storage.ReuseScenePacked,out _);
            settings.reflections.enabled=settings.planar.enabled=false;
            var noMotion=Run("motion-disabled-control",SrpActorForward.Storage.SeparateHalf,out _);
            settings.actorMotion.enabled=true;
            var coldMotion=Run("motion-cold",SrpActorForward.Storage.SeparateHalf,out _);
            Check("motion-mrt-retains-whole-color",Difference(noMotion,coldMotion)==0,Difference(noMotion,coldMotion));
            int coldCovered=0;bool coldInvalid=true;
            foreach(var p in lastMotion)if(p.a>0){coldCovered++;coldInvalid&=((int)p.a&8)==0&&p.r==0&&p.g==0;}
            Check("motion-cold-coverage-without-history",coldCovered>100&&coldInvalid,coldCovered);
            Run("motion-stationary",SrpActorForward.Storage.SeparateHalf,out _,false);
            int warmCovered=0;float stationaryError=0,previousDepthError=0;
            for(int i=0;i<lastMotion.Length;i++)if(lastMotion[i].a>0)
            {
                if(((int)lastMotion[i].a&8)!=0)warmCovered++;
                stationaryError=Mathf.Max(stationaryError,Mathf.Abs(lastMotion[i].r),Mathf.Abs(lastMotion[i].g));
                previousDepthError=Mathf.Max(previousDepthError,Mathf.Abs(lastExpectedDepth[i].r-4));
            }
            Check("motion-stationary-history-valid",warmCovered==coldCovered,warmCovered);
            Check("motion-stationary-analytic-zero",stationaryError<.000002f,stationaryError);
            Check("motion-previous-analytic-eye-depth",previousDepthError<.000002f,previousDepthError);
            var actor=settings.actors.renderers[0];var originalPosition=actor.transform.position;
            actor.transform.position+=new Vector3(.25f,.125f,0);
            Run("motion-translated",SrpActorForward.Storage.SeparateHalf,out _,false);
            var gpuProjection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);
            var oldClip=gpuProjection*camera.worldToCameraMatrix*new Vector4(originalPosition.x,originalPosition.y,originalPosition.z,1);
            var newClip=gpuProjection*camera.worldToCameraMatrix*new Vector4(actor.transform.position.x,actor.transform.position.y,actor.transform.position.z,1);
            float expectedX=(newClip.x/newClip.w-oldClip.x/oldClip.w)*.5f,expectedY=(newClip.y/newClip.w-oldClip.y/oldClip.w)*.5f;
            // Account for BOTH the render-texture projection and API UV origin.
            if(SystemInfo.graphicsUVStartsAtTop)expectedY=-expectedY;
            float motionError=0;int valid=0;
            foreach(var p in lastMotion)if(((int)p.a&8)!=0){valid++;motionError=Mathf.Max(motionError,Mathf.Abs(p.r-expectedX),Mathf.Abs(p.g-expectedY));}
            Check("motion-translation-independent-projection",valid>100&&motionError<.00004f,motionError);
            actor.transform.position=originalPosition;
            bool oldFx=settings.effects.enabled,oldDof=settings.depthOfField.enabled;var oldLut=settings.colorGrade;
            settings.effects.enabled=false;settings.depthOfField.enabled=false;settings.colorGrade=null;
            settings.temporal.enabled=true;
            var temporalCold=Run("temporal-cold",SrpActorForward.Storage.SeparateHalf,out _);
            Check("temporal-cold-preserves-current",Difference(temporalCold,lastOpaque)==0,Difference(temporalCold,lastOpaque));
            int AgeCount(float age){int count=0;foreach(var p in lastTemporal)if(p.g>0&&Mathf.Abs(p.b-age)<.00001f)count++;return count;}
            int UsedCount(){int count=0;foreach(var p in lastTemporal)if(p.a>0)count++;return count;}
            Check("temporal-cold-actor-age-one",AgeCount(1)==coldCovered,AgeCount(1));
            var temporalWarm=Run("temporal-stationary",SrpActorForward.Storage.SeparateHalf,out _,false);
            Check("temporal-stationary-uses-actual-history",UsedCount()==coldCovered&&AgeCount(2)==coldCovered,UsedCount());
            Check("temporal-stationary-retains-color",Difference(temporalCold,temporalWarm)<.000002f,Difference(temporalCold,temporalWarm));
            settings.temporal.contentRevision++;
            Run("temporal-revision",SrpActorForward.Storage.SeparateHalf,out _,false);
            Check("temporal-revision-rejects-history",UsedCount()==0&&AgeCount(1)==coldCovered,UsedCount());
            Run("temporal-revision-warm",SrpActorForward.Storage.SeparateHalf,out _,false);
            Check("temporal-revision-recovers-history",UsedCount()==coldCovered,UsedCount());
            serial++;
            Run("temporal-sequence-gap",SrpActorForward.Storage.SeparateHalf,out _,false);
            Check("temporal-gap-rejects-history",UsedCount()==0,UsedCount());
            settings.effects.enabled=oldFx;
            var fxSurface=settings.effects.geometry.surfaces[0];var oldFxTransform=fxSurface.localToWorld;
            fxSurface.localToWorld=Matrix4x4.TRS(new Vector3(0,0,-.5f),Quaternion.identity,new Vector3(3,2.5f,1));
            var temporalFx=Run("temporal-fx",SrpActorForward.Storage.SeparateHalf,out _,false);
            int fxExcluded=0;foreach(var p in lastTemporal)if(p.g>0&&p.b==0&&p.a==0)fxExcluded++;
            Check("temporal-current-fx-conservative-rejection",fxExcluded>100,fxExcluded);
            settings.temporal.enabled=false;var fxControl=Run("temporal-fx-control",SrpActorForward.Storage.SeparateHalf,out _);
            Check("temporal-fx-preserves-whole-current-composite",Difference(temporalFx,fxControl)==0,Difference(temporalFx,fxControl));
            settings.temporal.enabled=true;fxSurface.localToWorld=oldFxTransform;
            settings.effects.enabled=false;
            var originalConfigure=settings.actors.configureMaterial;
            var pattern=Own(new Texture2D(16,16,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});
            void Pattern(bool inverted){var p=new Color[256];for(int y=0;y<16;y++)for(int x=0;x<16;x++)
                {float c=(((x+y)&1)==0)^inverted?.4f:.6f;p[x+16*y]=new Color(c,c,c,1);}pattern.SetPixels(p);pattern.Apply();}
            settings.actors.configureMaterial=(r,i,m)=>{originalConfigure?.Invoke(r,i,m);m.SetFloat("_FaceDebugMode",14);m.SetColor("_Color",Color.white);m.SetTexture("_MainTex",pattern);};
            Pattern(false);var patternCold=Run("temporal-pattern-cold",SrpActorForward.Storage.SeparateHalf,out _);
            Pattern(true);var patternWarm=Run("temporal-pattern-warm",SrpActorForward.Storage.SeparateHalf,out _,false);
            double currentEnergy=0,resolvedEnergy=0;int accumulated=0;float response=0;
            for(int i=0;i<lastTemporal.Length;i++)if(lastTemporal[i].a>0)
            {
                float mean=(patternCold[i].r+lastOpaque[i].r)*.5f;
                currentEnergy+=Math.Pow(lastOpaque[i].r-mean,2);resolvedEnergy+=Math.Pow(patternWarm[i].r-mean,2);accumulated++;
                response=Mathf.Max(response,Mathf.Abs(patternWarm[i].r-lastOpaque[i].r));
            }
            Check("temporal-pattern-history-changes-color",accumulated>100&&response>.01f,response);
            Check("temporal-pattern-reduces-alternating-energy",currentEnergy>.1&&resolvedEnergy<currentEnergy*.8,(float)(resolvedEnergy/Math.Max(currentEnergy,1e-20)));
            var hdrPattern=new Color[256];for(int i=0;i<hdrPattern.Length;i++)hdrPattern[i]=new Color(.125f,.125f,.125f,1);
            hdrPattern[8+16*8]=new Color(60,40,5,1);pattern.SetPixels(hdrPattern);pattern.Apply();
            var hdrCold=Run("temporal-sparse-hdr-cold",SrpActorForward.Storage.SeparateHalf,out _);
            var hdrWarm=Run("temporal-sparse-hdr-warm",SrpActorForward.Storage.SeparateHalf,out _,false);
            float peak=0;foreach(var p in hdrCold)peak=Mathf.Max(peak,p.r);
            Check("temporal-stationary-hdr-outlier-is-fixed-point",peak>32&&UsedCount()>100&&Difference(hdrCold,hdrWarm)<.00001f,Difference(hdrCold,hdrWarm));
            settings.actors.temporalFlags=(r,i)=>TemporalPixelFlags.ExcludeTaa;
            var excluded=Run("temporal-excluded",SrpActorForward.Storage.SeparateHalf,out _,false);
            Check("temporal-authored-exclude-bypasses-color",UsedCount()==0&&Difference(excluded,lastOpaque)==0,Difference(excluded,lastOpaque));
            settings.actors.temporalFlags=(r,i)=>TemporalPixelFlags.NoJitter;
            settings.temporal.jitterUv=new Vector2(.25f/settings.scene.output.width,.25f/settings.scene.output.height);
            var noJitter=Run("temporal-no-jitter",SrpActorForward.Storage.SeparateHalf,out _,false);
            float noJitterError=0;int noJitterPixels=0;
            for(int i=0;i<lastMotion.Length;i++)if(((int)lastMotion[i].a&4)!=0)
            {noJitterPixels++;for(int c=0;c<4;c++)noJitterError=Mathf.Max(noJitterError,Mathf.Abs(noJitter[i][c]-lastOpaque[i][c]));}
            Check("temporal-authored-no-jitter-exact-raster",noJitterPixels==coldCovered&&noJitterError==0&&UsedCount()==0,noJitterError);
            settings.actors.temporalFlags=null;settings.temporal.jitterUv=Vector2.zero;
            settings.includeSceneMotion=true;
            Run("joined-cold",SrpActorForward.Storage.SeparateHalf,out _);
            var joined=Run("joined-stationary",SrpActorForward.Storage.SeparateHalf,out _,false);
            int scenePixels=0,actorPixels=0,sceneHistory=0;float sceneZero=0;
            for(int i=0;i<lastMotion.Length;i++)if(lastMotion[i].a>0)
            {
                if(((int)lastMotion[i].a&1)==0)
                {scenePixels++;if(lastTemporal[i].a>0)sceneHistory++;sceneZero=Mathf.Max(sceneZero,Mathf.Abs(lastMotion[i].r),Mathf.Abs(lastMotion[i].g));}
                else actorPixels++;
            }
            Check("joined-independent-scene-actor-identities",scenePixels>1000&&actorPixels==coldCovered,scenePixels);
            Check("joined-stationary-scene-motion-zero",sceneZero<.000002f,sceneZero);
            Check("joined-scene-color-history-used",sceneHistory>1000,sceneHistory);
            settings.reuseSceneMotionStorage=true;
            Run("joined-reuse-cold",SrpActorForward.Storage.SeparateHalf,out _);
            var joinedReuse=Run("joined-reuse-stationary",SrpActorForward.Storage.SeparateHalf,out _,false);
            Check("joined-gbuffer2-reuse-preserves-whole-temporal-color",Difference(joined,joinedReuse)==0,Difference(joined,joinedReuse));
            var movingScene=settings.scene.surfaces[0].renderer;var oldScenePosition=movingScene.transform.position;
            movingScene.transform.position+=new Vector3(.25f,.125f,0);
            Run("joined-scene-translated",SrpActorForward.Storage.SeparateHalf,out _,false);
            int sceneMoved=0;float sceneMoveError=0;
            // Orthographic camera: same independent projection displacement as the
            // Actor translation above, irrespective of the scene wall's depth.
            foreach(var p in lastMotion)if(((int)p.a&1)==0&&((int)p.a&8)!=0&&((int)p.a>>4)==1)
            {sceneMoved++;sceneMoveError=Mathf.Max(sceneMoveError,Mathf.Abs(p.r-expectedX),Mathf.Abs(p.g-expectedY));}
            Check("joined-scene-translation-analytic-motion",sceneMoved>1000&&sceneMoveError<.00004f,sceneMoveError);
            movingScene.transform.position=oldScenePosition;
            settings.reflections.enabled=settings.planar.enabled=true;settings.reuseSceneMotionStorage=false;
            Run("joined-packed-cold",SrpActorForward.Storage.SeparatePacked,out _);
            var packedJoined=Run("joined-packed-warm",SrpActorForward.Storage.SeparatePacked,out _,false);
            long separateMotionBytes=host.MotionNominalTextureBytes;
            settings.reuseSceneMotionStorage=true;
            Run("joined-double-reuse-cold",SrpActorForward.Storage.ReuseScenePacked,out _);
            var doubleReuse=Run("joined-double-reuse-warm",SrpActorForward.Storage.ReuseScenePacked,out _,false);
            Check("joined-gbuffer2-and4-reflected-temporal-whole-color",Difference(packedJoined,doubleReuse)==0,Difference(packedJoined,doubleReuse));
            long savedMotionBytes=separateMotionBytes-host.MotionNominalTextureBytes;
            Check("joined-reuse-saves-one-owned-half4",savedMotionBytes==(long)settings.scene.output.width*settings.scene.output.height*8,savedMotionBytes);
            // Fail after actual temporal commands have run, then require both
            // geometry correspondence and color histories to recover cold.
            DesktopFrameRenderer.OpaqueFrame temporalFailure=default;
            RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{
                if(!host.TryRecord(context,++serial,1,out temporalFailure,out var why))throw new InvalidOperationException(why);
            }});
            settings.depthOfField.enabled=true;float oldRadius=settings.depthOfField.maximumRadius;settings.depthOfField.maximumRadius=float.NaN;
            Check("joined-post-temporal-failure-invalidates-frame",!host.TryFinishAfterSubmission(temporalFailure,.75,out _,out var failedWhy)&&failedWhy!=null&&!temporalFailure.IsCurrent);
            ReadSceneTarget(temporalFailure.actors.color);host.RetireAfterGpuCompletion();
            settings.depthOfField.enabled=false;settings.depthOfField.maximumRadius=oldRadius;
            Run("joined-recovered-cold",SrpActorForward.Storage.ReuseScenePacked,out _,false,false);
            Check("joined-failed-post-resets-motion-and-color-history",UsedCount()==0,UsedCount());
            Run("joined-recovered-warm",SrpActorForward.Storage.ReuseScenePacked,out _,false);
            Check("joined-failed-post-history-recovers",UsedCount()>1000,UsedCount());
            // Invalid input attempts may have queued scene/reflection commands.
            // Synchronize those commands before restoring inputs or retiring them.
            void RejectTemporalRecord(string name,string reason)
            {
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{
                    bool ok=host.TryRecord(context,++serial,1,out var rejected,out var why);
                    Debug.Log("[DesktopTemporalRejection] "+name+": accepted="+ok+"; reason="+why);
                    Check(name,!ok&&!rejected.IsCurrent&&why!=null&&why.IndexOf(reason,StringComparison.OrdinalIgnoreCase)>=0);
                }});
                ReadSceneTarget(settings.scene.output);host.RetireAfterGpuCompletion();
                Check(name+"-borrowed-targets-live",settings.scene.output.IsCreated()&&settings.scene.normalIdentity.IsCreated()&&settings.scene.depthStencil.IsCreated());
            }
            int vertexLimit=settings.actorMotion.maximumTrackedVertices;
            settings.actorMotion.maximumTrackedVertices=1;RejectTemporalRecord("joined-vertex-budget-rejected","vertex budget");settings.actorMotion.maximumTrackedVertices=vertexLimit;
            int actorBudget=settings.actorMotionMaximumMiB;
            settings.actorMotionMaximumMiB=0;RejectTemporalRecord("joined-actor-texture-budget-rejected","texture budget");settings.actorMotionMaximumMiB=actorBudget;
            int sceneBudget=settings.sceneMotionMaximumMiB;
            settings.sceneMotionMaximumMiB=0;RejectTemporalRecord("joined-scene-texture-budget-rejected","Half4 motion inputs");settings.sceneMotionMaximumMiB=sceneBudget;
            int temporalBudget=settings.temporalMaximumMiB;
            settings.temporalMaximumMiB=0;RejectTemporalRecord("joined-temporal-budget-rejected","budget");settings.temporalMaximumMiB=temporalBudget;
            var configured=settings.actors.configureMaterial;
            settings.actors.configureMaterial=(r,i,m)=>{configured?.Invoke(r,i,m);m.SetTexture("_MainTex",settings.scene.normalIdentity);};
            RejectTemporalRecord("joined-gbuffer2-feedback-rejected","sampling feedback");settings.actors.configureMaterial=configured;
            foreach(bool sceneBlock in new[]{false,true})
            {
                var renderer=sceneBlock?settings.scene.surfaces[0].renderer:settings.actors.renderers[0];
                int submesh=sceneBlock?settings.scene.surfaces[0].materialIndex:0;
                var savedBlock=new MaterialPropertyBlock();renderer.GetPropertyBlock(savedBlock,submesh);
                var testBlock=new MaterialPropertyBlock();renderer.GetPropertyBlock(testBlock,submesh);
                testBlock.SetFloat(sceneBlock?"_SurfaceIdentity":"_ActorMotionIdentity",64);renderer.SetPropertyBlock(testBlock,submesh);
                // Tile scene rejects all implicit renderer property blocks before
                // the narrower scene-motion reserved-input guard is reached.
                try { RejectTemporalRecord(sceneBlock?"joined-scene-implicit-block-rejected":"joined-actor-reserved-block-rejected",sceneBlock?"property block":"reserved"); }
                finally { renderer.SetPropertyBlock(savedBlock.isEmpty?null:savedBlock,submesh); }
            }
            Run("joined-negative-controls-recovered",SrpActorForward.Storage.ReuseScenePacked,out _);
            Check("joined-negative-controls-recover-cold",UsedCount()==0,UsedCount());
            // Simulate loss of a producer attachment only AFTER its queued draws
            // complete. Current tickets and post must reject it, not sample freed data.
            DesktopFrameRenderer.OpaqueFrame lostAttachment=default;
            RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{
                if(!host.TryRecord(context,++serial,1,out lostAttachment,out var why))throw new InvalidOperationException(why);
            }});
            ReadSceneTarget(lostAttachment.actors.color);lostAttachment.actors.expectedPreviousDepth.Release();
            Check("joined-released-previous-depth-invalidates-ticket",!lostAttachment.IsCurrent);
            Check("joined-released-previous-depth-rejects-post",!host.TryFinishAfterSubmission(lostAttachment,.75,out _,out _));
            host.RetireAfterGpuCompletion();
            Run("joined-released-target-recovered",SrpActorForward.Storage.ReuseScenePacked,out _);
            Check("joined-released-target-restarts-cold",UsedCount()==0,UsedCount());
            // Independent triangle interpolation oracle for the actual Actor
            // stream. Mutate only an owned mesh copy; never BakeMesh or infer
            // correspondence from the producer's snapshots/output pixels.
            settings.reflections.enabled=settings.planar.enabled=false;
            settings.temporal.enabled=false;settings.includeSceneMotion=false;settings.reuseSceneMotionStorage=false;
            var filter=actor.GetComponent<MeshFilter>();var sourceMesh=filter.sharedMesh;
            var oracleMesh=Own(Instantiate(sourceMesh));filter.sharedMesh=oracleMesh;
            var sourceVertices=oracleMesh.vertices;var triangles=oracleMesh.triangles;
            var sourceRotation=actor.transform.rotation;var sourceProjection=camera.projectionMatrix;
            bool sourceOrthographic=camera.orthographic;float sourceFov=camera.fieldOfView;
            Vector3[] priorWorld=null;Matrix4x4 priorVp=default,priorView=default;
            int rasterWidth=settings.scene.output.width,rasterHeight=settings.scene.output.height;
            int rasterBits=8;
            if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Vulkan&&
                (!int.TryParse(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_RASTER_SUBPIXEL_BITS"),out rasterBits)||rasterBits<4||rasterBits>16))
                throw new InvalidOperationException("Vulkan motion oracle requires the device's queried subPixelPrecisionBits in GAKUMAS_SELFTEST_RASTER_SUBPIXEL_BITS");
            float rasterScale=1<<rasterBits;
            void MotionOracle(string name,bool cold=false)
            {
                var local=oracleMesh.vertices;var world=new Vector3[local.Length];
                for(int i=0;i<world.Length;i++)world[i]=actor.localToWorldMatrix.MultiplyPoint3x4(local[i]);
                var view=camera.worldToCameraMatrix;var vp=camera.projectionMatrix*view;
                Run(name,SrpActorForward.Storage.SeparateHalf,out _,cold);
                if(!cold)
                {
                    int tested=0,moving=0;float uvError=0,depthError=0;bool valid=true;
                    for(int y=0;y<rasterHeight;y+=2)for(int x=0;x<rasterWidth;x+=2)
                    {
                        for(int t=0;t<triangles.Length;t+=3)
                        {
                            var projected=new Vector3[3];var clips=new Vector4[3];
                            for(int k=0;k<3;k++)
                            {
                                var w=world[triangles[t+k]];var c=vp*new Vector4(w.x,w.y,w.z,1);clips[k]=c;
                                float px=(c.x/c.w*.5f+.5f)*rasterWidth,py=(c.y/c.w*.5f+.5f)*rasterHeight;
                                // D3D11 uses eight bits; Vulkan uses the independently
                                // queried VkPhysicalDeviceLimits value, not a fit to
                                // rendered pixels. Both interpolate snapped triangles.
                                px=Mathf.Round(px*rasterScale)/rasterScale;py=Mathf.Round(py*rasterScale)/rasterScale;
                                projected[k]=new Vector3(px,py,c.w);
                            }
                            var a=projected[0];var b=projected[1];var c0=projected[2];
                            float det=(b.y-c0.y)*(a.x-c0.x)+(c0.x-b.x)*(a.y-c0.y);
                            if(Mathf.Abs(det)<1e-6f)continue;
                            float u=((b.y-c0.y)*(x+.5f-c0.x)+(c0.x-b.x)*(y+.5f-c0.y))/det;
                            float v=((c0.y-a.y)*(x+.5f-c0.x)+(a.x-c0.x)*(y+.5f-c0.y))/det;
                            if(u<.05f||v<.05f||1-u-v<.05f)continue;
                            var bary=new Vector3(u/a.z,v/b.z,(1-u-v)/c0.z);bary/=bary.x+bary.y+bary.z;
                            Vector4 oldClip=Vector4.zero,currentClip=Vector4.zero;Vector3 previousPoint=Vector3.zero;
                            for(int k=0;k<3;k++)
                            {
                                var old=priorWorld[triangles[t+k]];oldClip+=(priorVp*new Vector4(old.x,old.y,old.z,1))*bary[k];
                                currentClip+=clips[k]*bary[k];previousPoint+=old*bary[k];
                            }
                            var expected=new Vector2((currentClip.x/currentClip.w-oldClip.x/oldClip.w)*.5f,
                                (currentClip.y/currentClip.w-oldClip.y/oldClip.w)*.5f);
                            var actual=lastMotion[y*rasterWidth+x];var previousDepth=lastExpectedDepth[y*rasterWidth+x].r;
                            valid&=((int)actual.a&9)==9;
                            uvError=Mathf.Max(uvError,Mathf.Abs(expected.x-actual.r),Mathf.Abs(expected.y-actual.g));
                            depthError=Mathf.Max(depthError,Mathf.Abs(previousDepth+priorView.MultiplyPoint3x4(previousPoint).z));
                            tested++;if(expected.magnitude>.0001f)moving++;break;
                        }
                    }
                    Check(name+"-independent-triangle-motion",tested>100&&moving>50&&valid&&uvError<.00004f,uvError);
                    Check(name+"-independent-previous-eye-depth",tested>100&&depthError<.00001f,depthError);
                }
                priorWorld=world;priorView=view;priorVp=vp;
            }
            try
            {
                MotionOracle("oracle-cold",true);
                var deformed=(Vector3[])sourceVertices.Clone();deformed[2]+=new Vector3(.065f,-.035f,.08f);
                oracleMesh.vertices=deformed;oracleMesh.RecalculateBounds();MotionOracle("oracle-vertex-deformation");
                actor.transform.rotation=Quaternion.Euler(4,7,-3)*sourceRotation;MotionOracle("oracle-rotated-deformation");
                camera.orthographic=false;camera.fieldOfView=48;camera.ResetProjectionMatrix();MotionOracle("oracle-perspective-change");
                var offAxis=camera.projectionMatrix;offAxis.m02+=.013f;offAxis.m12-=.009f;offAxis.m01+=.007f;
                camera.projectionMatrix=offAxis;MotionOracle("oracle-off-axis-change");
                deformed[0]+=new Vector3(-.04f,.02f,-.045f);oracleMesh.vertices=deformed;oracleMesh.RecalculateBounds();MotionOracle("oracle-perspective-deformation");
            }
            finally
            {
                filter.sharedMesh=sourceMesh;actor.transform.rotation=sourceRotation;
                camera.orthographic=sourceOrthographic;camera.fieldOfView=sourceFov;camera.projectionMatrix=sourceProjection;
            }
            Pattern(false);settings.temporal.enabled=true;settings.effects.enabled=false;
            Vector2 firstJitter=new Vector2(.25f/rasterWidth,.375f/rasterHeight);
            void ApplyJitter(Vector2 uv)
            {
                var projection=sourceProjection;projection.m03+=2*uv.x;projection.m13+=2*uv.y;
                camera.projectionMatrix=projection;settings.temporal.jitterUv=uv;
            }
            try
            {
                ApplyJitter(firstJitter);var jitterCold=Run("oracle-jitter-cold",SrpActorForward.Storage.SeparateHalf,out _);
                float sampleError=0;
                Color At(int x,int y)=>lastOpaque[Mathf.Clamp(x,0,rasterWidth-1)+rasterWidth*Mathf.Clamp(y,0,rasterHeight-1)];
                for(int y=0;y<rasterHeight;y++)for(int x=0;x<rasterWidth;x++)
                {
                    // CPU bilinear reconstruction at independently specified UV,
                    // including clamped border pixels; no TAA shader as reference.
                    float px=x-firstJitter.x*rasterWidth,py=y-firstJitter.y*rasterHeight;
                    int ix=Mathf.FloorToInt(px),iy=Mathf.FloorToInt(py);
                    var expected=Color.LerpUnclamped(Color.LerpUnclamped(At(ix,iy),At(ix+1,iy),px-ix),
                        Color.LerpUnclamped(At(ix,iy+1),At(ix+1,iy+1),px-ix),py-iy);
                    for(int c=0;c<4;c++)sampleError=Mathf.Max(sampleError,Mathf.Abs(expected[c]-jitterCold[x+y*rasterWidth][c]));
                }
                Check("oracle-applied-jitter-independent-current-filter",sampleError<.00001f&&UsedCount()==0,sampleError);
                var nextJitter=new Vector2(-.25f/rasterWidth,-.125f/rasterHeight);ApplyJitter(nextJitter);
                Run("oracle-jitter-warm",SrpActorForward.Storage.SeparateHalf,out _,false);
                float jitterMotionError=0;int jitterCovered=0;
                foreach(var p in lastMotion)if(((int)p.a&9)==9)
                {jitterCovered++;jitterMotionError=Mathf.Max(jitterMotionError,Mathf.Abs(p.r-(nextJitter.x-firstJitter.x)),Mathf.Abs(p.g-(nextJitter.y-firstJitter.y)));}
                Check("oracle-applied-jitter-independent-motion",jitterCovered>100&&jitterMotionError<.00004f,jitterMotionError);
                Check("oracle-applied-jitter-reprojects-history",UsedCount()>100,UsedCount());
            }
            finally {camera.projectionMatrix=sourceProjection;settings.temporal.jitterUv=Vector2.zero;}
            settings.includeSceneMotion=true;
            try
            {
                Run("oracle-occlusion-cold",SrpActorForward.Storage.SeparateHalf,out _);
                Run("oracle-occlusion-warm",SrpActorForward.Storage.SeparateHalf,out _,false);
                var beforeVisibility=(Color[])lastMotion.Clone();actor.transform.position+=new Vector3(.5f,0,0);
                var revealed=Run("oracle-occlusion-revealed",SrpActorForward.Storage.SeparateHalf,out _,false);
                int uncovered=0,retainedSceneHistory=0,retainedActorHistory=0;float revealedError=0;bool rejected=true;
                for(int i=0;i<lastMotion.Length;i++)
                {
                    bool wasActor=((int)beforeVisibility[i].a&1)!=0;
                    bool isScene=lastMotion[i].a>0&&((int)lastMotion[i].a&1)==0;
                    if(wasActor&&isScene)
                    {
                        uncovered++;rejected&=lastTemporal[i].a==0&&lastTemporal[i].b==1;
                        for(int c=0;c<4;c++)revealedError=Mathf.Max(revealedError,Mathf.Abs(revealed[i][c]-lastOpaque[i][c]));
                    }
                    if(lastTemporal[i].a>0){if(isScene)retainedSceneHistory++;else if(((int)lastMotion[i].a&1)!=0)retainedActorHistory++;}
                }
                Check("oracle-disoccluded-scene-rejects-actor-history",uncovered>100&&rejected&&revealedError==0,revealedError);
                Check("oracle-disocclusion-keeps-valid-scene-history",retainedSceneHistory>1000,retainedSceneHistory);
                Check("oracle-disocclusion-keeps-corresponding-actor-history",retainedActorHistory>100,retainedActorHistory);
            }
            finally {actor.transform.position=originalPosition;}
            // A single nearest identity must not make a bilinearly mixed color
            // eligible as coherent history when the caller selects this policy.
            settings.reflections.enabled=settings.planar.enabled=false;
            settings.effects.enabled=settings.depthOfField.enabled=false;settings.colorGrade=null;
            settings.includeSceneMotion=true;settings.reuseSceneMotionStorage=false;
            settings.actors.configureMaterial=originalConfigure;
            var coherentProjection=camera.projectionMatrix;
            try
            {
                if(!TemporalProjectionJitter.TryCreate(coherentProjection,new Vector2Int(settings.scene.output.width,settings.scene.output.height),new Vector2(.25f,-.375f),out var jitter))throw new InvalidOperationException("Coherent footprint projection");
                camera.projectionMatrix=jitter.projection;settings.temporal.jitterUv=jitter.correctionUv;
                settings.temporal.rejectMixedSurfaceHistory=false;
                var legacyCold=Run("coherent-legacy-cold",SrpActorForward.Storage.SeparateHalf,out _);
                Run("coherent-legacy-warm",SrpActorForward.Storage.SeparateHalf,out _,false);
                settings.temporal.rejectMixedSurfaceHistory=true;
                var switched=Run("coherent-enabled-cold",SrpActorForward.Storage.SeparateHalf,out _,false);
                Check("coherent-enable-resets-history",UsedCount()==0,UsedCount());
                Check("coherent-cold-preserves-current",Difference(switched,legacyCold)<.00001f,Difference(switched,legacyCold));
                var coherent=Run("coherent-enabled-warm",SrpActorForward.Storage.SeparateHalf,out _,false);
                int w=settings.scene.output.width,h=settings.scene.output.height,mixed=0,solidHistory=0;float mixedError=0;bool rejected=true;
                int Index(int x,int y)=>Mathf.Clamp(x,0,w-1)+Mathf.Clamp(y,0,h-1)*w;
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                {
                    int i=x+y*w;float rx=x-jitter.correctionUv.x*w,ry=y-jitter.correctionUv.y*h;
                    if(rx<-.5f||ry<-.5f||rx>=w-.5f||ry>=h-.5f)continue;
                    int firstX=Mathf.FloorToInt(rx),firstY=Mathf.FloorToInt(ry);float fx=rx-firstX,fy=ry-firstY;
                    var m=lastMotion[Index(Mathf.FloorToInt(rx+.5f),Mathf.FloorToInt(ry+.5f))];int id=(int)m.a&~14;
                    if(id==0||m.b<=0||(((int)m.a|(int)lastMotion[i].a)&6)!=0)continue;
                    bool incompatible=false;var current=Color.clear;
                    for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                    {
                        float weight=(dx==0?1-fx:fx)*(dy==0?1-fy:fy);int p=Index(firstX+dx,firstY+dy);var tap=lastMotion[p];current+=lastOpaque[p]*weight;
                        if(weight>1e-6f&&(((int)tap.a&~14)!=id||((int)tap.a&6)!=0||Mathf.Abs(tap.b-m.b)>settings.temporal.depthTolerance+Mathf.Abs(m.b)*.000977f))incompatible=true;
                    }
                    if(incompatible)
                    {
                        mixed++;rejected&=lastTemporal[i].b==0&&lastTemporal[i].a==0;
                        for(int c=0;c<4;c++)mixedError=Mathf.Max(mixedError,Mathf.Abs(current[c]-coherent[i][c]));
                    }
                    else if(lastTemporal[i].a>0)solidHistory++;
                }
                Check("coherent-mixed-footprint-independent-rejection",mixed>20&&rejected,mixed);
                Check("coherent-mixed-footprint-independent-current-color",mixedError<.00001f,mixedError);
                Check("coherent-retains-solid-history",solidHistory>100,solidHistory);
                settings.actors.temporalFlags=(r,i)=>TemporalPixelFlags.NoJitter;
                var noJitterCoherent=Run("coherent-no-jitter",SrpActorForward.Storage.SeparateHalf,out _,false);
                float noJitterCoherentError=0;int noJitterCoherentPixels=0;
                for(int i=0;i<lastMotion.Length;i++)if(((int)lastMotion[i].a&4)!=0)
                {
                    noJitterCoherentPixels++;rejected&=lastTemporal[i].a==0;
                    for(int c=0;c<4;c++)noJitterCoherentError=Mathf.Max(noJitterCoherentError,Mathf.Abs(noJitterCoherent[i][c]-lastOpaque[i][c]));
                }
                Check("coherent-no-jitter-preserves-exact-raster",noJitterCoherentPixels>100&&rejected&&noJitterCoherentError==0,noJitterCoherentError);
                settings.actors.temporalFlags=null;settings.temporal.rejectMixedSurfaceHistory=false;
                var legacyRestored=Run("coherent-legacy-restored",SrpActorForward.Storage.SeparateHalf,out _,false);
                Check("coherent-disable-resets-history",UsedCount()==0,UsedCount());
                Check("coherent-keyword-disabled-default-exact",Difference(legacyRestored,legacyCold)==0,Difference(legacyRestored,legacyCold));
            }
            finally {camera.projectionMatrix=coherentProjection;settings.temporal.jitterUv=Vector2.zero;settings.temporal.rejectMixedSurfaceHistory=false;settings.actors.temporalFlags=null;}
            // Coverage reconstruction is separately opt-in. Keep every prior
            // fixture ahead of it so old records/artifacts remain comparable.
            try
            {
                int w=settings.scene.output.width,h=settings.scene.output.height;
                if(!TemporalProjectionJitter.TryCreate(coherentProjection,new Vector2Int(w,h),new Vector2(.25f,-.375f),out var jitter))throw new InvalidOperationException("Coverage projection");
                camera.projectionMatrix=jitter.projection;settings.temporal.jitterUv=jitter.correctionUv;
                var legacy=Run("coverage-legacy-cold",SrpActorForward.Storage.SeparateHalf,out _);
                Run("coverage-legacy-warm",SrpActorForward.Storage.SeparateHalf,out _,false);
                settings.temporal.preserveSurfaceCoverage=true;
                var cold=Run("coverage-enabled-cold",SrpActorForward.Storage.SeparateHalf,out _,false);
                Check("coverage-enable-resets-history",UsedCount()==0,UsedCount());
                Check("coverage-cold-preserves-current",Difference(cold,legacy)<.00001f,Difference(cold,legacy));
                Run("coverage-stationary-warm",SrpActorForward.Storage.SeparateHalf,out _,false);
                int Index(int x,int y)=>Mathf.Clamp(x,0,w-1)+Mathf.Clamp(y,0,h-1)*w;
                int mixedUsed=0;bool finite=true;
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                {
                    int i=x+y*w;float rx=x-jitter.correctionUv.x*w,ry=y-jitter.correctionUv.y*h;
                    int sx=Mathf.FloorToInt(rx),sy=Mathf.FloorToInt(ry),id=(int)lastTemporal[i].g;
                    bool mixed=false;for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)mixed|=((int)lastMotion[Index(sx+dx,sy+dy)].a&~14)!=id;
                    if(id>0&&mixed&&lastTemporal[i].a>0)mixedUsed++;
                    for(int c=0;c<4;c++)finite&=!float.IsNaN(lastTemporal[i][c])&&!float.IsInfinity(lastTemporal[i][c]);
                }
                Check("coverage-retains-mixed-history",mixedUsed>20&&finite,mixedUsed);
                var priorGuide=(Color[])lastTemporal.Clone();
                actor.transform.position+=new Vector3(.0375f,.0225f,0);
                Run("coverage-moving",SrpActorForward.Storage.SeparateHalf,out _,false);
                int used=0,fractional=0;bool supportedAll=true;
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                {
                    int i=x+y*w;if(lastTemporal[i].a<=0)continue;used++;
                    float rx=x-jitter.correctionUv.x*w,ry=y-jitter.correctionUv.y*h;
                    int cx=Mathf.FloorToInt(rx),cy=Mathf.FloorToInt(ry);float fx=rx-cx,fy=ry-cy;
                    var motion=lastMotion[Index(Mathf.FloorToInt(rx+.5f),Mathf.FloorToInt(ry+.5f))];
                    float closest=((int)motion.a&~14)!=0&&motion.b>0?motion.b:float.MaxValue;
                    for(int ay=0;ay<2;ay++)for(int ax=0;ax<2;ax++)
                    {
                        if((ax==0?1-fx:fx)*(ay==0?1-fy:fy)<=1e-6f)continue;
                        var g=lastMotion[Index(cx+ax,cy+ay)];
                        if(((int)g.a&~14)!=0&&((int)g.a&6)==0&&g.b>0&&g.b<closest){motion=g;closest=g.b;}
                    }
                    float px=rx-motion.r*w+jitter.correctionUv.x*w,py=ry-motion.g*h+jitter.correctionUv.y*h;
                    int bx=Mathf.FloorToInt(px),by=Mathf.FloorToInt(py);float ux=px-bx,uy=py-by;
                    if(ux>.01f&&ux<.99f&&uy>.01f&&uy<.99f)fractional++;
                    for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                    {
                        if((dx==0?1-ux:ux)*(dy==0?1-uy:uy)<=1e-6f)continue;
                        int hx=bx+dx,hy=by+dy;if(hx<0||hx>=w||hy<0||hy>=h){supportedAll=false;continue;}
                        var meta=priorGuide[hx+hy*w];bool supported=false;
                        for(int sy=0;sy<2;sy++)for(int sx=0;sx<2;sx++)
                        {
                            if((sx==0?1-fx:fx)*(sy==0?1-fy:fy)<=1e-6f)continue;
                            int q=Index(cx+sx,cy+sy);var g=lastMotion[q];int id=(int)g.a&~14;float depth=lastExpectedDepth[q].r;
                            bool compatible=id==0?meta.g==0&&meta.b>=1:((int)g.a&8)!=0&&meta.b>=1&&depth>0&&
                                Mathf.Abs(meta.r-depth)<=settings.temporal.depthTolerance+Mathf.Abs(depth)*.000977f&&
                                Mathf.Abs((g.r-motion.r)*w)<=1&&Mathf.Abs((g.g-motion.g)*h)<=1;
                            supported|=((int)g.a&6)==0&&(int)meta.g==id&&compatible;
                        }
                        supportedAll&=supported;
                    }
                }
                Check("coverage-moving-whole-footprint-supported",used>100&&fractional>20&&supportedAll,fractional);
                actor.transform.position=originalPosition;
                settings.actors.temporalFlags=(r,i)=>TemporalPixelFlags.NoJitter;
                var coverageNoJitter=Run("coverage-no-jitter",SrpActorForward.Storage.SeparateHalf,out _,false);
                int coverageExcluded=0;float error=0;bool rejected=true;
                for(int i=0;i<lastMotion.Length;i++)if(((int)lastMotion[i].a&4)!=0)
                {coverageExcluded++;rejected&=lastTemporal[i].a==0&&lastTemporal[i].b==0;for(int c=0;c<4;c++)error=Mathf.Max(error,Mathf.Abs(coverageNoJitter[i][c]-lastOpaque[i][c]));}
                Check("coverage-no-jitter-exact-and-ineligible",coverageExcluded>100&&rejected&&error==0,error);
                settings.actors.temporalFlags=(r,i)=>TemporalPixelFlags.ExcludeTaa;
                Run("coverage-excluded",SrpActorForward.Storage.SeparateHalf,out _,false);
                rejected=true;coverageExcluded=0;
                for(int i=0;i<lastMotion.Length;i++)if(((int)lastMotion[i].a&2)!=0){coverageExcluded++;rejected&=lastTemporal[i].a==0&&lastTemporal[i].b==0;}
                Check("coverage-exclude-taa-ineligible",coverageExcluded>100&&rejected,coverageExcluded);
                settings.actors.temporalFlags=null;settings.temporal.preserveSurfaceCoverage=false;
                var restored=Run("coverage-legacy-restored",SrpActorForward.Storage.SeparateHalf,out _,false);
                Check("coverage-disable-resets-history",UsedCount()==0,UsedCount());
                Check("coverage-keyword-disabled-default-exact",Difference(restored,legacy)==0,Difference(restored,legacy));
            }
            finally {camera.projectionMatrix=coherentProjection;actor.transform.position=originalPosition;settings.temporal.jitterUv=Vector2.zero;settings.temporal.preserveSurfaceCoverage=false;settings.actors.temporalFlags=null;}
            var depthSettings=settings.depthOfField;
            bool depthEnabled=depthSettings.enabled;var focusMode=depthSettings.focusMode;
            float focusNear=depthSettings.focusNear,focusFar=depthSettings.focusFar,nearTransition=depthSettings.nearTransition,farTransition=depthSettings.farTransition;
            try
            {
                int w=settings.scene.output.width,h=settings.scene.output.height;
                depthSettings.enabled=true;depthSettings.focusMode=BokehFocusMode.FocusRange;
                depthSettings.focusNear=3.7f;depthSettings.focusFar=3.8f;depthSettings.nearTransition=depthSettings.farTransition=.4f;
                actor.transform.position=originalPosition+new Vector3(0,0,.00113f);
                if(!TemporalProjectionJitter.TryCreate(coherentProjection,new Vector2Int(w,h),new Vector2(.25f,-.375f),out var jitter))throw new InvalidOperationException("Temporal DOF projection");
                camera.projectionMatrix=jitter.projection;settings.temporal.jitterUv=jitter.correctionUv;
                foreach(bool coverage in new[]{false,true})
                {
                    settings.temporal.preserveSurfaceCoverage=coverage;
                    string label="depth-align-"+(coverage?"coverage":"nearest");Run(label,SrpActorForward.Storage.SeparateHalf,out var rawDepth);
                    float depthError=0,cocError=0,wrongRaw=0,halfError=0;int movedAnchor=0;
                    int Index(int x,int y)=>Mathf.Clamp(x,0,w-1)+Mathf.Clamp(y,0,h-1)*w;
                    for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                    {
                        int i=x+y*w;float rx=x-jitter.correctionUv.x*w,ry=y-jitter.correctionUv.y*h;
                        int px=Mathf.FloorToInt(rx+.5f),py=Mathf.FloorToInt(ry+.5f),p=Index(px,py);var m=lastMotion[p];
                        if(coverage)
                        {
                            int firstX=Mathf.FloorToInt(rx),firstY=Mathf.FloorToInt(ry);float fx=rx-firstX,fy=ry-firstY;
                            float closest=((int)m.a&~14)!=0&&m.b>0?m.b:float.MaxValue;
                            for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                            {
                                if((dx==0?1-fx:fx)*(dy==0?1-fy:fy)<=1e-6f)continue;
                                int q=Index(firstX+dx,firstY+dy);var g=lastMotion[q];
                                if(((int)g.a&~14)!=0&&((int)g.a&6)==0&&g.b>0&&g.b<closest){p=q;m=g;closest=g.b;}
                            }
                        }
                        if((((int)m.a|(int)lastMotion[i].a)&4)!=0)p=i;
                        float expected=rawDepth[p].r;depthError=Mathf.Max(depthError,Mathf.Abs(lastPostDepth[i].r-expected));
                        float coc=depthSettings.EvaluateRadius(expected)/depthSettings.maximumRadius*.5f+.5f;
                        cocError=Mathf.Max(cocError,Mathf.Abs(lastCoC[i].r-coc));
                        wrongRaw=Mathf.Max(wrongRaw,Mathf.Abs(expected-rawDepth[i].r));
                        if(((int)m.a&~14)!=0)halfError=Mathf.Max(halfError,Mathf.Abs(expected-m.b));
                        if(p!=i&&Mathf.Abs(expected-rawDepth[i].r)>.01f)movedAnchor++;
                    }
                    Check(label+"-independent-r32-depth-exact",depthError==0,depthError);
                    Check(label+"-actual-dof-coc-from-aligned-depth",cocError<.000002f,cocError);
                    Check(label+"-does-not-substitute-half-depth",halfError>.0001f,halfError);
                    if(coverage)Check(label+"-raw-depth-negative-control",movedAnchor>20&&wrongRaw>.1f,movedAnchor);
                }
                settings.actors.temporalFlags=(r,i)=>TemporalPixelFlags.NoJitter;
                Run("depth-align-no-jitter",SrpActorForward.Storage.SeparateHalf,out var noJitterDepth);
                float noJitterDepthError=0;int noJitterDepthPixels=0;
                for(int i=0;i<lastMotion.Length;i++)if(((int)lastMotion[i].a&4)!=0)
                {noJitterDepthPixels++;noJitterDepthError=Mathf.Max(noJitterDepthError,Mathf.Abs(lastPostDepth[i].r-noJitterDepth[i].r));}
                Check("depth-align-no-jitter-original-r32-exact",noJitterDepthPixels>100&&noJitterDepthError==0,noJitterDepthError);
                settings.actors.temporalFlags=null;camera.projectionMatrix=coherentProjection;settings.temporal.jitterUv=Vector2.zero;
                Run("depth-align-zero-correction",SrpActorForward.Storage.SeparateHalf,out var zeroDepth);
                Check("depth-align-zero-correction-bypasses-resampling",Difference(zeroDepth,lastPostDepth)==0,Difference(zeroDepth,lastPostDepth));
            }
            finally
            {
                camera.projectionMatrix=coherentProjection;actor.transform.position=originalPosition;settings.temporal.jitterUv=Vector2.zero;settings.temporal.preserveSurfaceCoverage=false;settings.actors.temporalFlags=null;
                depthSettings.enabled=depthEnabled;depthSettings.focusMode=focusMode;depthSettings.focusNear=focusNear;depthSettings.focusFar=focusFar;
                depthSettings.nearTransition=nearTransition;depthSettings.farTransition=farTransition;
            }
            settings.reflections.enabled=settings.planar.enabled=false;
            settings.includeSceneMotion=false;settings.reuseSceneMotionStorage=false;
            settings.actors.configureMaterial=originalConfigure;
            settings.effects.enabled=oldFx;settings.depthOfField.enabled=oldDof;settings.colorGrade=oldLut;
            settings.temporal.enabled=false;settings.actorMotion.enabled=false;
            settings.actorStorage=SrpActorForward.Storage.SeparateHalf;sequence=serial;
        }

        // Measure all eight filter weights at the actual post-FX/DOF coordinates.
        // Parity labels cover every LUT cell. Expected node values below are the
        // independent linear profile equation, not another call to the grade math.
        private void VerifyDesktopGradeWeights(Report report,string name,RenderTexture input,Color[] pixels,Color[] actual,ColorGradingLut lut,Vector3 filter)
        {
            void Check(string n,bool ok,float e=0)=>FrameworkCheck(report,"desktop-host-"+name+"-lut-"+n,ok,e);
            const int size=16;const float maximum=4;
            var nodes=lut.CopyValues();float nodeError=0;
            for(int z=0;z<size;z++)for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            {
                var expected=new Color(x*maximum/(size-1)*filter.x,y*maximum/(size-1)*filter.y,z*maximum/(size-1)*filter.z,1);
                for(int c=0;c<4;c++)nodeError=Mathf.Max(nodeError,Mathf.Abs(nodes[x+size*(y+size*z)][c]-expected[c]));
            }
            Check("all-nodes-independent-profile",nodeError<.000001f,nodeError);
            var sums=new double[pixels.Length];var reconstructed=new double[pixels.Length*3];
            double weightError=0,partitionError=0,reconstructionError=0;bool bounded=true;
            using var renderer=new ColorGradingRenderer();
            for(int group=0;group<3;group++)
            {
                using var probe=ColorGradingLut.Bake(new ColorGradingProfile { size=size,domain=ColorLutDomain.Linear,maximumInput=maximum,toneMapping=ColorToneMapping.Clip });
                var basis=new Color[size*size*size];
                for(int z=0;z<size;z++)for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                {
                    int label=(x&1)|((y&1)<<1)|((z&1)<<2);var value=new Color(0,0,0,1);
                    if(label/3==group)value[label%3]=1;basis[x+size*(y+size*z)]=value;
                }
                var texture=new Texture3D(size,size,size,TextureFormat.RGBAFloat,false,true) { name="Desktop actual-coordinate filter weights",filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp };
                texture.SetPixels(basis);texture.Apply(false,true);
                Destroy(probe.Texture);
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                typeof(ColorGradingLut).GetField("texture",flags).SetValue(probe,texture);
                typeof(ColorGradingLut).GetField("values",flags).SetValue(probe,basis);
                if(!renderer.TryRender(input,probe,out var frame))throw new InvalidOperationException(renderer.UnavailableReason);
                var weights=ReadSceneTarget(frame.color);
                for(int p=0;p<pixels.Length;p++)
                {
                    var low=new int[3];var fraction=new double[3];
                    for(int c=0;c<3;c++)
                    {double pos=Math.Max(0,Math.Min(maximum,pixels[p][c]))/maximum*(size-1);low[c]=Math.Min(size-2,(int)Math.Floor(pos));fraction[c]=pos-low[c];}
                    int parity=(low[0]&1)|((low[1]&1)<<1)|((low[2]&1)<<2);
                    for(int c=0;c<3;c++)
                    {
                        int label=group*3+c;if(label>=8)continue;int corner=label^parity;
                        double weight=weights[p][c],ideal=1;
                        bounded&=weight>=0&&weight<=1;sums[p]+=weight;
                        for(int axis=0;axis<3;axis++)
                        {
                            int high=(corner>>axis)&1;ideal*=high==0?1-fraction[axis]:fraction[axis];
                            reconstructed[p*3+axis]+=weight*(low[axis]+high)*maximum/(size-1)*filter[axis];
                        }
                        weightError=Math.Max(weightError,Math.Abs(weight-ideal));
                    }
                }
            }
            float alphaError=0;
            for(int p=0;p<pixels.Length;p++)
            {
                partitionError=Math.Max(partitionError,Math.Abs(sums[p]-1));alphaError=Mathf.Max(alphaError,Mathf.Abs(actual[p].a-pixels[p].a));
                for(int c=0;c<3;c++)reconstructionError=Math.Max(reconstructionError,Math.Abs(actual[p][c]-reconstructed[p*3+c]));
            }
            Check("actual-coordinate-weights-bounded",bounded);
            Check("actual-coordinate-partition",partitionError<.000001,(float)partitionError);
            Check("actual-coordinate-weight-rounding",weightError<(3*.6+1)/256+.00001,(float)weightError);
            Check("independent-profile-measured-weights-whole-color",reconstructionError<.00002,(float)reconstructionError);
            Check("alpha-preserved",alphaError==0,alphaError);
            ColorLutReferenceError(lut,pixels,actual,out _,out float interval);
            Check("independent-local-sampling-interval",interval<.000005f,interval);
        }
    }
}
