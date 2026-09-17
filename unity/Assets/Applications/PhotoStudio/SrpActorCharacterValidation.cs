using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Opt-in full Actor SRP/ordinary Forward controls using the host's local
    /// character. No assets, pipeline changes or validation work in the default app.</summary>
    public sealed partial class SrpActorCharacterValidation : MonoBehaviour
    {
        [Serializable] private sealed class Check { public string name;public bool accepted;public float value; }
        [Serializable] private sealed class Geometry { public string renderer;public int vertices,submeshes;public bool skinned; }
        [Serializable] private sealed class PrecisionSample
        {
            public int sample,differentChannels,firstX=-1,firstY=-1,firstChannel=-1;
            public Vector2 pixelOffset;public Matrix4x4 view,projection;
            public float maximumDifference,firstControl,firstMotion;
            public bool float32Measured;public int float32DifferentChannels;
            public float float32MaximumDifference,float32ControlAtFirst,float32MotionAtFirst;
            public float halfCopyControlAtFirst,halfCopyMotionAtFirst;
        }
        [Serializable] private sealed class Report
        {
            public string schema="photo-studio.srp-actor-character.v1",graphicsDevice,costume,error;
            public bool accepted;public int renderers,draws,materials;public int[] materialTypes;
            public int readbackScratchTextures,previewScratchTextures,readbackCalls,previewExports;
            public long scratchTextureBytes;
            public List<Geometry> geometry=new List<Geometry>();public List<Check> checks=new List<Check>();
            public List<PrecisionSample> precisionSweep=new List<PrecisionSample>();
        }
        private const int Size=512;
        private PhotoModeApp app;private string directory;
        private readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
        private readonly Dictionary<Vector2Int,Texture2D> readbackScratch=new Dictionary<Vector2Int,Texture2D>();
        private Texture2D previewScratch;
        private int readbackCalls,previewExports;
        private T Own<T>(T value) where T:UnityEngine.Object { owned.Add(value);return value; }
        public static bool TryStart(PhotoModeApp app)
        {
            var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--validate-srp-actor-character");if(index<0)return false;
            try
            {
                if(!args.Contains("--photo-mode")||index+1>=args.Length||args[index+1].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("Requires --photo-mode and an output directory");
                var validation=app.gameObject.AddComponent<SrpActorCharacterValidation>();validation.app=app;validation.directory=Path.GetFullPath(args[index+1]);
            }
            catch(Exception error){Debug.LogError("[SrpActorCharacterValidation] "+error.Message);Application.Quit(3);}
            return true;
        }
        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(2);
            var report=new Report { graphicsDevice=SystemInfo.graphicsDeviceVersion,costume=app.CurrentCostume };
            var oldGraphics=GraphicsSettings.renderPipelineAsset;var oldQuality=QualitySettings.renderPipeline;var oldActive=RenderTexture.active;
            bool oldPaused=app.IsPlaybackPaused;var previousOffscreen=new Dictionary<SkinnedMeshRenderer,bool>();
            var scenes=new List<TileSceneRenderer.PreparedFrame>();var preparations=new List<ActorForwardDrawSet.PreparedFrame>();SrpActorForward actor=null;
            bool shadowControls=Environment.GetCommandLineArgs().Contains("--validate-actor-shadows");
            SrpActorShadow selfShadow=shadowControls?new SrpActorShadow():null;
            void Check(string name,bool accepted,float value=0)=>report.checks.Add(new Check { name=name,accepted=accepted,value=value });
            try
            {
                Directory.CreateDirectory(directory);
                if(GraphicsSettings.currentRenderPipeline!=null||app.CharacterRoot==null)throw new InvalidOperationException("Requires initialized Built-in local character");
                app.SetPlaybackPaused(true);app.EvaluateMotion(0);
                var renderers=app.CharacterRoot.GetComponentsInChildren<Renderer>().Where(r=>r.enabled&&!r.forceRenderingOff&&r.gameObject.activeInHierarchy).ToArray();
                if(renderers.Length==0)throw new InvalidOperationException("No local character renderers");
                var sources=renderers.ToDictionary(r=>r,r=>r.sharedMaterials);
                var receiveShadows=renderers.ToDictionary(r=>r,r=>r.receiveShadows);
                foreach(var r in renderers)
                {
                    var skin=r as SkinnedMeshRenderer;var mesh=skin!=null?skin.sharedMesh:r.GetComponent<MeshFilter>()?.sharedMesh;
                    if(mesh==null)throw new InvalidOperationException("Missing character mesh");
                    report.geometry.Add(new Geometry { renderer=r.name,vertices=mesh.vertexCount,submeshes=mesh.subMeshCount,skinned=skin!=null });
                    if(skin!=null){previousOffscreen[skin]=skin.updateWhenOffscreen;skin.updateWhenOffscreen=true;}
                }
                report.renderers=renderers.Length;report.materialTypes=sources.Values.SelectMany(a=>a).Select(m=>(int)m.GetFloat("_ShaderType")).Distinct().OrderBy(x=>x).ToArray();
                var bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);var center=bounds.center;float distance=Mathf.Max(1,bounds.size.y)*2;
                var headDriver=app.CharacterRoot.GetComponent<ActorHeadLightingDriver>();
                var head=headDriver!=null?(Transform)typeof(ActorHeadLightingDriver).GetField("_head",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(headDriver):null;
                if(head==null)throw new InvalidOperationException("Missing current character head basis");
                var camera=Own(new GameObject("Full Actor explicit SRP validation camera")).AddComponent<Camera>();camera.enabled=false;camera.allowMSAA=false;camera.allowHDR=true;
                camera.fieldOfView=40;camera.nearClipPlane=.03f;camera.farClipPlane=distance*5;camera.aspect=1;camera.cullingMask=1<<29;
                foreach(var r in renderers)camera.cullingMask|=1<<r.gameObject.layer;
                RenderTexture Target(GraphicsFormat format,string name,GraphicsFormat depth=GraphicsFormat.None)
                { var t=Own(new RenderTexture(new RenderTextureDescriptor(Size,Size,format,0) { depthStencilFormat=depth }) { name=name,filterMode=FilterMode.Point });if(!t.Create())throw new InvalidOperationException(name);return t; }
                var output=Target(GraphicsFormat.B10G11R11_UFloatPack32,"Character scene-only color");camera.targetTexture=output;
                var hardware=Target(GraphicsFormat.None,"Character scene raster depth",GraphicsFormat.D32_SFloat_S8_UInt);
                var readFloat=Target(GraphicsFormat.R32G32B32A32_SFloat,"Character packed readback");
                var reference=Target(GraphicsFormat.R16G16B16A16_SFloat,"Ordinary full character control",GraphicsFormat.D32_SFloat_S8_UInt);
                var referenceDepth=Target(GraphicsFormat.R32_SFloat,"Ordinary full character eye depth");
                var referenceCamera=Own(new GameObject("Ordinary Forward character camera")).AddComponent<Camera>();referenceCamera.enabled=false;
                var back=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));back.name="Controlled scene behind character";back.layer=29;back.transform.localScale=Vector3.one*distance*4;
                var backMaterial=Own(new Material(Resources.Load<Shader>("PlanarCapture")));back.GetComponent<Renderer>().sharedMaterial=backMaterial;
                var background=new Color(.03125f,.0625f,.125f,1);var emission=new Vector3(.0625f,.125f,.25f);
                backMaterial.SetVector("_Color",new Vector4(0,0,0,1));backMaterial.SetVector("_Emission",emission);backMaterial.SetVector("_AmbientColor",Vector4.zero);backMaterial.SetVector("_LightColor",Vector4.zero);
                var sceneSettings=new TileSceneRenderer.Settings { enabled=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,geometryDepthId=true,
                    output=output,depthStencil=hardware,background=background,lightRadiance=Vector3.zero,ambientIrradiance=Vector3.zero,
                    surfaces=new[]{new SceneDeferredCamera.Surface { renderer=back.GetComponent<Renderer>(),cull=CullMode.Back,
                        inputs=new SceneDeferredCamera.MaterialInputs { albedo=Vector3.zero,mos=new Vector3(0,1,0),emission=emission } }} };
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                var depthReader=Own(new Material(Resources.Load<Shader>("ActorForwardDepth")));
                var quad=Own(new Mesh { name="Character independent depth readback" });quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
                quad.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};quad.triangles=new[]{0,1,2,0,2,3};
                var main=new Dictionary<(Renderer,int),Material>();var supplements=new Dictionary<(Renderer,int),Material>();
                var baseInputs=ActorForwardParameters.CaptureCurrentGlobals();baseInputs.SetFloat("_FaceDebugMode",0);
                var settings=new ActorForwardDrawSet.Settings { renderers=renderers,parameters=baseInputs };
                var selfDirection=new Vector3(.6f,1,.7f).normalized;
                float shadowRadius=Mathf.Max(1,bounds.extents.magnitude);
                var shadowSettings=new SceneDirectionalShadowSettings { enabled=true, halfSize=Vector2.one*shadowRadius*1.4f,
                    nearPlane=.01f,farPlane=shadowRadius*4,resolution=Size,depthBias=.002f,filter=SceneShadowFilter.Pcf3x3 };
                // Half a light texel compensates curvature between neighboring
                // triangle planes; keep the explicit world-unit bias visible.
                shadowSettings.normalBias=shadowSettings.halfSize.x/shadowSettings.resolution;
                bool detailsOff=false;bool ambientEnabled=false;
                settings.configureMaterial=(r,index,m)=>{
                    if(m.shader.name=="GakumasPhotoMode/ActorToon")main[(r,index)]=m;else supplements[(r,index)]=m;
                    if(detailsOff){m.SetTexture("_ShadeTex",Texture2D.whiteTexture);m.SetTexture("_RampAddTex",Texture2D.blackTexture);m.SetTexture("_HighlightTex",Texture2D.blackTexture);}
                    var probe=RenderSettings.ambientProbe;
                    if(r.lightProbeUsage!=LightProbeUsage.Off&&r.lightProbeUsage!=LightProbeUsage.CustomProvided)
                        LightProbes.GetInterpolatedProbe(r.probeAnchor!=null?r.probeAnchor.position:r.bounds.center,r,out probe);
                    if(r.lightProbeUsage==LightProbeUsage.CustomProvided)throw new InvalidOperationException("This real-character fixture requires non-custom probes");
                    ActorForwardParameters.BindAmbientProbe(m,probe);
                };
                actor=new SrpActorForward(camera,new SrpActorForward.Settings { enabled=true });ulong sequence=0;SrpActorForward.Frame current=default;
                void View(int angle)
                {
                    var direction=Quaternion.Euler(0,angle,0)*Vector3.forward;camera.transform.position=center+direction*distance;camera.transform.LookAt(center);
                    back.transform.SetPositionAndRotation(center-direction*distance,Quaternion.LookRotation(-direction));
                }
                Color[] Read(RenderTexture t)
                { if(t==output){Graphics.Blit(t,readFloat);return ReadPixels(readFloat);}return ReadPixels(t); }
                string sourceBefore=SourceSnapshot(renderers);
                Color[] Run(string name)
                {
                    baseInputs.SetVector("_HeadDirection",new Vector4(head.forward.x,head.forward.y,head.forward.z,1));
                    baseInputs.SetVector("_HeadUpDirection",new Vector4(head.up.x,head.up.y,head.up.z,1));baseInputs.SetVector("_HeadRightDirection",new Vector4(-head.right.x,-head.right.y,-head.right.z,1));
                    var headPosition=head.position+head.up*.1f;baseInputs.SetVector("_HeadPosition",new Vector4(headPosition.x,headPosition.y,headPosition.z,1));
                    baseInputs.SetVector("_ActorOutlineParameters",new Vector4(.04f,.12f,1f/3,Mathf.Tan(15.5f*Mathf.Deg2Rad)/Mathf.Tan(camera.fieldOfView*.5f*Mathf.Deg2Rad)));
                    if(ambientEnabled){baseInputs.SetFloat("_UseCapturedAmbientSH",0);baseInputs.SetVector("_ActorLightingScales",new Vector4(1,1,1,0));}
                    main.Clear();supplements.Clear();
                    sequence++;
                    if(shadowControls)
                    {
                        if(!ActorShadowInputs.TryCapture(renderers,Shader.GetGlobalFloat("_CapturedActorTextureLodBias"),out var casters,out var captureError))throw new InvalidOperationException(captureError);
                        shadowSettings.casters=casters;shadowSettings.origin=center+selfDirection*shadowRadius*2;
                        GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                        Exception shadowFailure=null;
                        RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                            try
                            {
                                if(!selfShadow.TryRecord(context,selfDirection,shadowSettings,sequence,out var shadowFrame,out var error))throw new InvalidOperationException(error);
                                settings.selfShadow=shadowFrame;
                            }
                            catch(Exception e){shadowFailure=e;}
                        }});
                        if(shadowFailure!=null)throw shadowFailure;
                        Check(name+"-current-self-shadow",settings.selfShadow.HasValue&&settings.selfShadow.Value.IsCurrent&&
                            (shadowSettings.strength==0?selfShadow.CasterDrawCalls==0:selfShadow.CasterDrawCalls>0),selfShadow.CasterDrawCalls);
                        if(settings.selfShadow.Value.depth!=null)Save(name+"-shadow",Read(settings.selfShadow.Value.depth));
                    }
                    if(!ActorForwardDrawSet.TryPrepare(camera,settings,out var draws,out var why))throw new InvalidOperationException(why);preparations.Add(draws);
                    if(!TileSceneRenderer.TryPrepare(camera,sceneSettings,out var scene,out why))throw new InvalidOperationException(why);scenes.Add(scene);
                    report.draws=draws.DrawCount;report.materials=draws.MaterialCount;
                    GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                    bool requested=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SRP_ACTOR")=="1"&&Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SRP_ACTOR_CASE")==name;
                    bool began=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();Color[] actual=null,actualDepth=null,sceneColor=null;Exception failure=null;
                    try
                    {
                        RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{
                            try{if(!scene.TryRecord(context,out _,out var error)||!actor.TryRecord(context,scene,draws,sequence,out current,out error))throw new InvalidOperationException(error);}catch(Exception e){failure=e;}
                        }});
                        if(failure!=null)throw failure;if(!current.IsCurrent)throw new InvalidOperationException("No current full Actor output");
                        actual=Read(current.color);actualDepth=Read(current.eyeDepth);sceneColor=Read(output);
                        Save(name+"-srp",actual);Save(name+"-srp-depth",actualDepth);Save(name+"-scene",sceneColor);
                    }
                    finally{if(began)Check(name+"-native-capture",RenderDocCaptureBridge.EndOffscreenCapture());}
                    try
                    {
                        GraphicsSettings.renderPipelineAsset=null;QualitySettings.renderPipeline=null;
                        referenceCamera.CopyFrom(camera);referenceCamera.enabled=false;referenceCamera.renderingPath=RenderingPath.Forward;referenceCamera.targetTexture=reference;
                        referenceCamera.transform.SetPositionAndRotation(camera.transform.position,camera.transform.rotation);referenceCamera.clearFlags=CameraClearFlags.Nothing;
                        foreach(var r in renderers)
                        {
                            r.sharedMaterials=Enumerable.Range(0,sources[r].Length).Select(i=>main[(r,i)]).ToArray();
                            // Compare the same explicit inputs. The ordinary camera
                            // otherwise adds its automatic Screenspace ShadowMap;
                            // this SRP API requires a separately supplied shadow map.
                            r.receiveShadows=false;
                        }
                        using var clear=new CommandBuffer { name="Character reference literal linear clear" };clear.SetRenderTarget(reference);clear.ClearRenderTarget(true,true,background);
                        using var supplemental=new CommandBuffer { name="Character reference body outline, hair cover, hair outline" };
                        void Pass(string pass,bool hair)
                        {
                            foreach(var r in renderers)for(int i=0;i<sources[r].Length;i++)
                            {
                                var source=sources[r][i];if((source.GetFloat("_ShaderType")==8)!=hair||!supplements.TryGetValue((r,i),out var m))continue;
                                if(pass=="ACTOR_OUTLINE"&&(source.GetFloat("_OutlineEnabled")<=.5f||!source.GetShaderPassEnabled("ActorOutline")))continue;
                                if(pass=="ACTOR_HAIR_COVER"&&!source.GetShaderPassEnabled("ActorHairCover"))continue;
                                supplemental.DrawRenderer(r,m,i,m.FindPass(pass));
                            }
                        }
                        if(settings.outlines)Pass("ACTOR_OUTLINE",false);if(settings.hairCover)Pass("ACTOR_HAIR_COVER",true);if(settings.outlines)Pass("ACTOR_OUTLINE",true);
                        referenceCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,clear);referenceCamera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha,supplemental);
                        bool referenceBegan=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();
                        try
                        {
                            referenceCamera.Render();depthReader.SetTexture("_ActorHardwareDepth",reference,RenderTextureSubElement.Depth);
                            depthReader.SetVector("_ActorTargetSize",new Vector4(Size,Size,0,0));depthReader.SetMatrix("_ActorInverseProjection",GL.GetGPUProjectionMatrix(camera.projectionMatrix,true).inverse);
                            using var read=new CommandBuffer();read.SetRenderTarget(referenceDepth);read.SetViewport(new Rect(0,0,Size,Size));read.DrawMesh(quad,Matrix4x4.identity,depthReader,0,1);Graphics.ExecuteCommandBuffer(read);
                            var expected=Read(reference);var expectedDepth=Read(referenceDepth);float colorError=MaximumDifference(actual,expected),depthError=MaximumDifference(actualDepth,expectedDepth);
                            int coverage=Changed(actual,sceneColor,.01f);bool finite=actual.Concat(expected).Concat(actualDepth).Concat(expectedDepth).All(p=>Finite(p.r)&&Finite(p.g)&&Finite(p.b)&&Finite(p.a));
                            Check(name+"-ordinary-forward-whole-color",colorError<.00001f,colorError);Check(name+"-ordinary-forward-whole-depth",depthError<.00001f,depthError);
                            Check(name+"-full-character-finite-visible",finite&&coverage>1000,coverage);Check(name+"-all-authored-submeshes",main.Count==sources.Values.Sum(a=>a.Length)&&draws.DrawCount>main.Count,draws.DrawCount);
                            Save(name+"-ordinary",expected);Save(name+"-ordinary-depth",expectedDepth);
                        }
                        finally
                        {
                            if(referenceBegan)Check(name+"-native-reference-capture",RenderDocCaptureBridge.EndOffscreenCapture());
                            referenceCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,clear);referenceCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha,supplemental);
                        }
                    }
                    finally { foreach(var r in renderers){r.sharedMaterials=sources[r];r.receiveShadows=receiveShadows[r];}GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline; }
                    Check(name+"-source-materials-not-mutated",SourceSnapshot(renderers)==sourceBefore);return actual;
                }
                Color[] front=null;
                foreach(int angle in new[]{0,90,180,270}){View(angle);var pixels=Run("view-"+angle);if(angle==0)front=pixels;}
                View(0);Check("front-view-revisit-exact",MaximumDifference(front,Run("front-revisit"))==0);
                app.EvaluateMotion(.7f);var moved=Run("motion-070");Check("skinned-motion-positive-control",Changed(front,moved,.001f)>100,Changed(front,moved,.001f));
                app.EvaluateMotion(0);var restored=Run("motion-restored");Check("motion-seek-restores-full-color",MaximumDifference(front,restored)==0,MaximumDifference(front,restored));
                detailsOff=true;var simplified=Run("material-detail-negative-control");detailsOff=false;
                Check("authored-material-details-positive-control",Changed(restored,simplified,.001f)>100,Changed(restored,simplified,.001f));
                Check("owned-detail-override-restores",MaximumDifference(restored,Run("details-restored"))==0);
                ambientEnabled=true;var ambient=Run("explicit-renderer-ambient");Check("ambient-probe-positive-control",Changed(restored,ambient,.001f)>100,Changed(restored,ambient,.001f));
                if(shadowControls)
                {
                    var light=Shader.GetGlobalVector("_CapturedLightDirection");
                    shadowSettings.strength=0;var clear=Run("self-shadow-zero");
                    Check("self-shadow-full-material-positive-control",Changed(ambient,clear,.001f)>100,Changed(ambient,clear,.001f));
                    shadowSettings.strength=1;selfDirection=new Vector3(-.6f,.8f,.7f).normalized;
                    var turned=Run("self-shadow-direction");
                    Check("self-shadow-direction-full-material-positive-control",Changed(ambient,turned,.001f)>100,Changed(ambient,turned,.001f));
                    Check("self-shadow-does-not-change-toon-global",Shader.GetGlobalVector("_CapturedLightDirection")==light);
                    selfDirection=new Vector3(.6f,1,.7f).normalized;
                    Check("self-shadow-restore-whole-color",MaximumDifference(ambient,Run("self-shadow-restored"))==0);
                }
                // Appended opt-in motion controls preserve all existing ordinary
                // Forward evidence, including real outline/hair/stencil coverage.
                using(var temporal=new FrameTemporalAntialiasing())
                {
                    // Local bundle topology is immutable throughout these controls;
                    // do not rewrite/reimport user assets to manufacture readability.
                    actor.Configuration.allowImmutableUnreadableMotionMeshes=true;
                    var temporalSettings=new FrameTemporalAntialiasing.Settings {enabled=true};
                    foreach(int angle in new[]{0,90,180})
                    {
                        View(angle);app.EvaluateMotion(.7f);string label="temporal-view-"+angle;
                        actor.Configuration.motion.enabled=false;var control=Run(label+"-disabled");
                        actor.Configuration.motion.enabled=true;var cold=Run(label+"-cold");
                        Check(label+"-motion-mrt-retains-real-color",MaximumDifference(control,cold)==0,MaximumDifference(control,cold));
                        if(!temporal.TryRender(current,current.color,temporalSettings,128,out var coldFrame,out var why))throw new InvalidOperationException(why);
                        var coldTemporal=Read(coldFrame.color);var coldGuide=Read(coldFrame.metadata);
                        Check(label+"-cold-temporal-preserves-real-color",MaximumDifference(cold,coldTemporal)==0,MaximumDifference(cold,coldTemporal));
                        var warm=Run(label+"-stationary");var motionPixels=Read(current.motionDepthIdentity);
                        int visibleMotion=0,validMotion=0;float motionError=0;
                        foreach(var p in motionPixels)if(p.a>0)
                        {visibleMotion++;if(((int)p.a&8)!=0)validMotion++;motionError=Mathf.Max(motionError,Mathf.Abs(p.r),Mathf.Abs(p.g));}
                        Check(label+"-real-history-coverage",visibleMotion>1000&&visibleMotion==validMotion,validMotion);
                        Check(label+"-real-stationary-zero-motion",motionError<.00004f,motionError);
                        if(!temporal.TryRender(current,current.color,temporalSettings,128,out var warmFrame,out why))throw new InvalidOperationException(why);
                        var resolved=Read(warmFrame.color);var guide=Read(warmFrame.metadata);
                        int reused=guide.Count(p=>p.a>0);Check(label+"-real-color-consumes-history",reused>1000,reused);
                        Check(label+"-real-stationary-color-stable",MaximumDifference(warm,resolved)<.00005f,MaximumDifference(warm,resolved));
                        Save(label+"-motion",motionPixels);Save(label+"-temporal",resolved);Save(label+"-temporal-guide",guide);
                        app.EvaluateMotion(.72f);var deformed=Run(label+"-deformed");motionPixels=Read(current.motionDepthIdentity);
                        int moving=motionPixels.Count(p=>((int)p.a&8)!=0&&(Mathf.Abs(p.r)+Mathf.Abs(p.g))>.00001f);
                        Check(label+"-real-deformation-motion-positive",moving>100,moving);
                        if(!temporal.TryRender(current,current.color,temporalSettings,128,out var deformedFrame,out why))throw new InvalidOperationException(why);
                        resolved=Read(deformedFrame.color);guide=Read(deformedFrame.metadata);
                        Check(label+"-real-deformed-temporal-finite",resolved.All(p=>Finite(p.r)&&Finite(p.g)&&Finite(p.b)&&Finite(p.a)));
                        Save(label+"-deformed-motion",motionPixels);Save(label+"-deformed-temporal",resolved);Save(label+"-deformed-guide",guide);
                    }
                    actor.Configuration.motion.enabled=false;
                }
                // Diagnostic-only bounded subpixel sweep. All pairs execute in
                // this same synchronous frame/pose; never accept a later process
                // merely because its startup dynamics happen to avoid a delta.
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_ACTOR_PRECISION_SWEEP")=="1")
                {
                    View(180);app.EvaluateMotion(.7f);
                    var originalProjection=camera.projectionMatrix;
                    Color[] PrecisionDraw(string name,GraphicsFormat format,bool temporalPass)
                    {
                        // Diagnostic copies of the SAME current full materials.
                        // Half-format controls must reproduce the production
                        // images before Float32 differences can be attributed.
                        var target=Target(format,name,GraphicsFormat.D32_SFloat_S8_UInt);
                        var motionTarget=temporalPass?Target(GraphicsFormat.R16G16B16A16_SFloat,name+" motion"):null;
                        var priorTarget=temporalPass?Target(GraphicsFormat.R32_SFloat,name+" prior depth"):null;
                        var ordered=new List<(Renderer renderer,int submesh,Material material,string pass)>();
                        var mains=main.OrderBy(pair=>pair.Value.renderQueue)
                            .ThenBy(pair=>pair.Value.renderQueue>2500?camera.worldToCameraMatrix.MultiplyPoint(pair.Key.Item1.bounds.center).z:0)
                            .ThenBy(pair=>pair.Key.Item1.GetInstanceID()).ThenBy(pair=>pair.Key.Item2).ToArray();
                        foreach(var pair in mains)if(pair.Value.renderQueue<=2500)ordered.Add((pair.Key.Item1,pair.Key.Item2,pair.Value,"ACTOR_FORWARD_HDR"));
                        void AddSupplement(string pass,bool hair)
                        {
                            foreach(var r in renderers)for(int i=0;i<sources[r].Length;i++)
                            {
                                var source=sources[r][i];if((source.GetFloat("_ShaderType")==8)!=hair||!supplements.TryGetValue((r,i),out var material))continue;
                                if(pass=="ACTOR_OUTLINE"&&(source.GetFloat("_OutlineEnabled")<=.5f||!source.GetShaderPassEnabled("ActorOutline")))continue;
                                if(pass=="ACTOR_HAIR_COVER"&&!source.GetShaderPassEnabled("ActorHairCover"))continue;
                                ordered.Add((r,i,material,pass));
                            }
                        }
                        if(settings.outlines)AddSupplement("ACTOR_OUTLINE",false);
                        if(settings.hairCover)AddSupplement("ACTOR_HAIR_COVER",true);
                        if(settings.outlines)AddSupplement("ACTOR_OUTLINE",true);
                        foreach(var pair in mains)if(pair.Value.renderQueue>2500)ordered.Add((pair.Key.Item1,pair.Key.Item2,pair.Value,"ACTOR_FORWARD_HDR"));
                        using var commands=new CommandBuffer {name="Bounded Actor color precision diagnostic"};
                        commands.SetRenderTarget(target);commands.ClearRenderTarget(true,true,new Color(emission.x,emission.y,emission.z,1));
                        if(temporalPass)
                        {
                            commands.SetRenderTarget(motionTarget);commands.ClearRenderTarget(false,true,Color.clear);
                            commands.SetRenderTarget(priorTarget);commands.ClearRenderTarget(false,true,Color.clear);
                            commands.SetRenderTarget(new[]{new RenderTargetIdentifier(target),new RenderTargetIdentifier(motionTarget),new RenderTargetIdentifier(priorTarget)},target);
                        }
                        commands.SetViewport(new Rect(0,0,Size,Size));
                        foreach(var draw in ordered)
                        {
                            var material=draw.material;var pass=draw.pass;
                            if(temporalPass)
                            {
                                material=Own(new Material(Resources.Load<Shader>("ActorTemporal")));material.CopyPropertiesFromMaterial(draw.material);
                                material.SetFloat("_ActorMotionHistory",0);material.SetFloat("_ActorMotionIdentity",1);material.SetFloat("_ActorMotionFlags",0);
                                material.SetTexture("_ActorPreviousClip",Texture2D.blackTexture);material.SetVector("_ActorClipSize",new Vector4(2,2,0,0));
                                material.SetVector("_ActorMotionSize",new Vector4(Size,Size,0,0));
                                var inverse=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true).inverse;
                                material.SetMatrix("_ActorMotionInverseProjection",inverse);material.SetMatrix("_ActorPreviousInverseProjection",inverse);
                                pass=(pass=="ACTOR_FORWARD_HDR"?"ACTOR_FORWARD":pass)+"_TEMPORAL";
                            }
                            int index=material.FindPass(pass);if(index<0)throw new InvalidOperationException("Missing precision pass "+pass);
                            commands.DrawRenderer(draw.renderer,material,draw.submesh,index);
                        }
                        bool requested=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SRP_ACTOR")=="1"&&Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SRP_ACTOR_CASE")==name;
                        bool began=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();
                        try
                        {
                            RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest {record=context=>{context.SetupCameraProperties(camera);context.ExecuteCommandBuffer(commands);}});
                            var pixels=Read(target);Save(name,pixels);return pixels;
                        }
                        finally{if(requested)Check(name+"-native-capture",began&&RenderDocCaptureBridge.EndOffscreenCapture());}
                    }
                    Color[] PrecisionCopy(string name,Color[] source,GraphicsFormat format)
                    {
                        // Calibrate conversion through a full-precision texture
                        // Load; do not assume CPU Half round-to-nearest matches
                        // this device's attachment conversion behavior.
                        var texture=Own(new Texture2D(Size,Size,TextureFormat.RGBAFloat,false,true));texture.SetPixels(source);texture.Apply();
                        var copy=Own(new Material(Resources.Load<Shader>("ActorForwardDepth")));
                        copy.SetTexture("_ActorSourceColor",texture);copy.SetVector("_ActorTargetSize",new Vector4(Size,Size,0,0));
                        var target=Target(format,name);
                        using var commands=new CommandBuffer {name="Explicit Float32 load and attachment conversion control"};
                        commands.SetRenderTarget(target);commands.SetViewport(new Rect(0,0,Size,Size));commands.DrawMesh(quad,Matrix4x4.identity,copy,0,2);
                        Graphics.ExecuteCommandBuffer(commands);var pixels=Read(target);Save(name,pixels);return pixels;
                    }
                    try
                    {
                        for(int sample=0;sample<16;sample++)
                        {
                            var offset=new Vector2(sample/16f-.5f,((sample*3)%16)/16f-.5f);
                            var projection=originalProjection;
                            // Perspective clip.w=-view.z, so changing m02/m12
                            // by -2*UV applies a constant positive NDC offset.
                            projection.m02-=2*offset.x/Size;projection.m12-=2*offset.y/Size;
                            camera.projectionMatrix=projection;
                            string label="precision-rear-"+sample;
                            actor.Configuration.motion.enabled=false;var control=Run(label+"-disabled");
                            actor.Configuration.motion.enabled=true;var motion=Run(label+"-motion");
                            var observation=new PrecisionSample {sample=sample,pixelOffset=offset,
                                view=camera.worldToCameraMatrix,projection=projection,
                                maximumDifference=MaximumDifference(control,motion)};
                            for(int p=0;p<control.Length;p++)for(int c=0;c<4;c++)if(control[p][c]!=motion[p][c])
                            {
                                observation.differentChannels++;
                                if(observation.firstX<0){observation.firstX=p%Size;observation.firstY=p/Size;
                                    observation.firstChannel=c;observation.firstControl=control[p][c];observation.firstMotion=motion[p][c];}
                            }
                            report.precisionSweep.Add(observation);
                            Check(label+"-motion-mrt-exact-color",observation.differentChannels==0,observation.maximumDifference);
                            if(observation.differentChannels==0)continue;
                            // Preserve the first differing inputs and both repeat
                            // controls, rather than advancing time or relaxing the
                            // strict color gate. Native capture can target these
                            // names when the host has injected its capture bridge.
                            actor.Configuration.motion.enabled=false;var repeatedControl=Run("precision-first-delta-disabled-repeat");
                            actor.Configuration.motion.enabled=true;var repeatedMotion=Run("precision-first-delta-motion-repeat");
                            Check("precision-first-delta-disabled-repeat-exact",MaximumDifference(control,repeatedControl)==0,MaximumDifference(control,repeatedControl));
                            Check("precision-first-delta-motion-repeat-exact",MaximumDifference(motion,repeatedMotion)==0,MaximumDifference(motion,repeatedMotion));
                            var halfControl=PrecisionDraw("precision-half-control",GraphicsFormat.R16G16B16A16_SFloat,false);
                            var halfMotion=PrecisionDraw("precision-half-motion",GraphicsFormat.R16G16B16A16_SFloat,true);
                            Check("precision-isolated-half-control-matches-production",MaximumDifference(repeatedControl,halfControl)==0,MaximumDifference(repeatedControl,halfControl));
                            Check("precision-isolated-half-motion-matches-production",MaximumDifference(repeatedMotion,halfMotion)==0,MaximumDifference(repeatedMotion,halfMotion));
                            var floatControl=PrecisionDraw("precision-float-control",GraphicsFormat.R32G32B32A32_SFloat,false);
                            var floatMotion=PrecisionDraw("precision-float-motion",GraphicsFormat.R32G32B32A32_SFloat,true);
                            Check("precision-float-control-repeat-exact",MaximumDifference(floatControl,PrecisionDraw("precision-float-control-repeat",GraphicsFormat.R32G32B32A32_SFloat,false))==0);
                            Check("precision-float-motion-repeat-exact",MaximumDifference(floatMotion,PrecisionDraw("precision-float-motion-repeat",GraphicsFormat.R32G32B32A32_SFloat,true))==0);
                            Check("precision-float-finite",floatControl.Concat(floatMotion).All(p=>Finite(p.r)&&Finite(p.g)&&Finite(p.b)&&Finite(p.a)));
                            observation.float32Measured=true;observation.float32MaximumDifference=MaximumDifference(floatControl,floatMotion);
                            for(int p=0;p<floatControl.Length;p++)for(int c=0;c<4;c++)if(floatControl[p][c]!=floatMotion[p][c])observation.float32DifferentChannels++;
                            int first=observation.firstY*Size+observation.firstX;
                            observation.float32ControlAtFirst=floatControl[first][observation.firstChannel];observation.float32MotionAtFirst=floatMotion[first][observation.firstChannel];
                            Check("precision-float-upload-load-exact",MaximumDifference(floatControl,PrecisionCopy("precision-float-load-control",floatControl,GraphicsFormat.R32G32B32A32_SFloat))==0);
                            var copiedControl=PrecisionCopy("precision-half-load-control",floatControl,GraphicsFormat.R16G16B16A16_SFloat);
                            var copiedMotion=PrecisionCopy("precision-half-load-motion",floatMotion,GraphicsFormat.R16G16B16A16_SFloat);
                            observation.halfCopyControlAtFirst=copiedControl[first][observation.firstChannel];observation.halfCopyMotionAtFirst=copiedMotion[first][observation.firstChannel];
                            break;
                        }
                    }
                    finally {camera.projectionMatrix=originalProjection;actor.Configuration.motion.enabled=false;}
                }
                if(Environment.GetCommandLineArgs().Contains("--validate-secondary-motion"))
                {
                    actor.Configuration.motion.enabled=false;
                    VerifySecondaryCharacter(report,View,Run);
                }
                actor.Dispose();Check("dispose-keeps-original-character",!current.IsCurrent&&actor.NominalTextureBytes==0&&SourceSnapshot(renderers)==sourceBefore&&output.IsCreated());
                if(Environment.GetCommandLineArgs().Contains("--validate-desktop-character"))
                    VerifyDesktopCharacter(report,renderers,head,bounds,baseInputs,sourceBefore);
                report.readbackScratchTextures=readbackScratch.Count;report.previewScratchTextures=previewScratch!=null?1:0;
                report.readbackCalls=readbackCalls;report.previewExports=previewExports;
                foreach(var texture in readbackScratch.Values)report.scratchTextureBytes+=(long)texture.width*texture.height*16;
                if(previewScratch!=null)report.scratchTextureBytes+=(long)previewScratch.width*previewScratch.height*4;
                Check("export-reuses-bounded-scratch",readbackScratch.Count==1&&previewScratch!=null&&
                    readbackCalls>1&&previewExports>1&&report.scratchTextureBytes==(long)Size*Size*20,readbackScratch.Count+1);
                report.accepted=report.checks.All(c=>c.accepted);
            }
            catch(Exception error){report.error=error.ToString();Debug.LogException(error);}
            finally
            {
                actor?.Dispose();foreach(var p in preparations)p.Dispose();foreach(var s in scenes)s.Dispose();selfShadow?.Dispose();
                foreach(var pair in previousOffscreen)if(pair.Key!=null)pair.Key.updateWhenOffscreen=pair.Value;app.SetPlaybackPaused(oldPaused);
                GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;RenderTexture.active=oldActive;
                foreach(var value in owned)if(value is GameObject go){var c=go.GetComponent<Camera>();if(c!=null)c.targetTexture=null;}
                foreach(var value in owned){if(value is RenderTexture t)t.Release();if(value!=null)Destroy(value);}owned.Clear();
                readbackScratch.Clear();previewScratch=null;
            }
            try{File.WriteAllText(Path.Combine(directory,"srp-actor-character.json"),JsonUtility.ToJson(report,true));}catch(Exception error){report.accepted=false;Debug.LogException(error);}
            Debug.Log("[SrpActorCharacterValidation] accepted="+report.accepted+"; checks="+report.checks.Count);Application.Quit(report.accepted?0:2);
        }
        private static bool Finite(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x);
        private Color[] ReadPixels(RenderTexture texture)
        {
            // This diagnostic renders thousands of explicit samples in one
            // Unity update. Destroy is deferred, so per-sample textures would
            // accumulate until the whole matrix returns. Reuse only scratch;
            // GetPixels still returns an independent snapshot for every sample.
            var size=new Vector2Int(texture.width,texture.height);
            if(!readbackScratch.TryGetValue(size,out var copy))
            {copy=Own(new Texture2D(size.x,size.y,TextureFormat.RGBAFloat,false,true));readbackScratch.Add(size,copy);}
            var previous=RenderTexture.active;
            try{RenderTexture.active=texture;copy.ReadPixels(new Rect(0,0,size.x,size.y),0,0);copy.Apply();readbackCalls++;return copy.GetPixels();}
            finally{RenderTexture.active=previous;}
        }
        private void Save(string name,Color[] values)
        {
            if(previewScratch==null)previewScratch=Own(new Texture2D(Size,Size,TextureFormat.RGBA32,false,true));
            previewScratch.SetPixels(values.Select(p=>p.gamma).ToArray());previewScratch.Apply();
            File.WriteAllBytes(Path.Combine(directory,name+".png"),previewScratch.EncodeToPNG());previewExports++;
            // Focus checks retain bounded case inputs, not RAW duplicates of all
            // preceding ordinary-Forward controls. PNG/JSON assertions remain.
            if(Environment.GetCommandLineArgs().Contains("--validate-desktop-focus")&&!name.StartsWith("desktop-character-focus-",StringComparison.Ordinal))return;
            if(Environment.GetCommandLineArgs().Contains("--validate-desktop-additional-lights")&&!name.StartsWith("desktop-character-additional-",StringComparison.Ordinal))return;
            using var writer=new BinaryWriter(File.Create(Path.Combine(directory,name+".raw")));foreach(var p in values)for(int c=0;c<4;c++)writer.Write(p[c]);
        }
        private static float MaximumDifference(Color[] a,Color[] b)
        { float value=0;for(int p=0;p<a.Length;p++)for(int c=0;c<4;c++)value=Mathf.Max(value,Mathf.Abs(a[p][c]-b[p][c]));return value; }
        private static int Changed(Color[] a,Color[] b,float epsilon)
        { int count=0;for(int p=0;p<a.Length;p++)if(Mathf.Abs(a[p].r-b[p].r)+Mathf.Abs(a[p].g-b[p].g)+Mathf.Abs(a[p].b-b[p].b)>epsilon)count++;return count; }
        private static string SourceSnapshot(Renderer[] renderers)
        {
            var values=new List<string>();foreach(var r in renderers)foreach(var m in r.sharedMaterials)
            { values.Add(r.GetInstanceID()+"/"+m.GetInstanceID()+"/"+m.shader.name+"/"+m.renderQueue);
                foreach(var name in m.GetTexturePropertyNames())values.Add(name+"="+(m.GetTexture(name)!=null?m.GetTexture(name).GetInstanceID():0));
                foreach(var name in new[]{"_ShaderType","_OutlineEnabled","_UseAlphaClip","_Cull","_SrcBlend","_DstBlend","_ZWrite","_StencilComp","_StencilRef"})values.Add(name+"="+m.GetFloat(name).ToString("R")); }
            return string.Join("|",values);
        }
    }
}
