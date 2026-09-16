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
                host.RetireAfterGpuCompletion();host.Dispose();
                RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>Check("disposed-host-rejects-record",!host.TryRecord(context,++sequence,1,out _,out _)) });
            }
            finally
            {
                host?.Dispose();lut?.Dispose();GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;RenderTexture.active=oldActive;
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
            VerifyDesktopExample(report);
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
