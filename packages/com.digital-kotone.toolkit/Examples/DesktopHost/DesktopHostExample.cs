using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode.Examples
{
    /// <summary>An opt-in, generated-content desktop host. This teaching example
    /// deliberately synchronizes the GPU each frame: simple lifetime ownership,
    /// not a performance template. The reusable renderer itself never waits.</summary>
    [AddComponentMenu("Character Toolkit/Examples/Desktop Host")]
    public sealed class DesktopHostExample : MonoBehaviour
    {
        [Range(128,1280)] public int width=640;
        [Range(96,720)] public int height=360;
        public bool animate=true, presentToScreen=true;
        public RenderTexture Display { get; private set; }
        public Camera RenderCamera { get; private set; }
        public DesktopFrameRenderer.Settings Configuration { get; private set; }
        public string LastError { get; private set; }
        public ulong RenderedFrames { get; private set; }
        public double LastRenderedTimeSeconds { get; private set; }
        public bool IsInitialized => frameRenderer!=null;
        public bool HasCompletedFrame => displayReady;
        private static DesktopHostExample activeExample;
        private readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
        private DesktopFrameRenderer frameRenderer;
        private ActorPlanarCaptureSet mirror;
        private ColorGradingLut grade;
        private DesktopExamplePipelineAsset asset;
        private DesktopExampleRequest offscreenRequest;
        private RenderPipelineAsset oldGraphics,oldQuality;
        private Transform body,head,leftArm,rightArm,particle;
        private LowResolutionFxSurface particleSurface;
        private RenderTexture completionTarget;
        private Texture2D completionReadback;
        private ulong sequence;
        private bool started,inContext,presentThisContext,displayReady;

        private void Start() { started=true;if(!IsInitialized)Initialize(); }
        private void OnEnable() { if(started&&!IsInitialized)Initialize(); }
        private void OnDisable() { Shutdown(); }
        private T Own<T>(T value) where T:UnityEngine.Object { owned.Add(value);return value; }

        /// <summary>Temporarily selects this example's SRP. Call on the main thread
        /// outside rendering; only this example's camera/content is drawn.</summary>
        public void Initialize()
        {
            if(IsInitialized)return;
            if(activeExample!=null&&activeExample!=this)throw new InvalidOperationException("Only one desktop example may own the pipeline");
            if(width<128||width>1280||height<96||height>720)throw new ArgumentOutOfRangeException("Example dimensions");
            if(QualitySettings.activeColorSpace!=ColorSpace.Linear)throw new InvalidOperationException("Use Linear project color space");
            var api=SystemInfo.graphicsDeviceType;
            if(api!=GraphicsDeviceType.Direct3D11&&api!=GraphicsDeviceType.Vulkan)throw new NotSupportedException("Example currently requires desktop D3D11 or Vulkan");
            oldGraphics=GraphicsSettings.renderPipelineAsset;oldQuality=QualitySettings.renderPipeline;activeExample=this;
            try
            {
                BuildContent();
                frameRenderer=new DesktopFrameRenderer(RenderCamera,Configuration);mirror=new ActorPlanarCaptureSet();
                asset=Own(ScriptableObject.CreateInstance<DesktopExamplePipelineAsset>());asset.owner=this;
                offscreenRequest=Own(ScriptableObject.CreateInstance<DesktopExampleRequest>());
                RenderPipelineManager.endContextRendering+=FinishContext;
                GraphicsSettings.renderPipelineAsset=asset;QualitySettings.renderPipeline=asset;
                LastError=null;sequence=RenderedFrames=0;LastRenderedTimeSeconds=double.NaN;
            }
            catch { Shutdown();throw; }
        }

        /// <summary>Same compiled scene/host as normal Play mode, with explicit time
        /// and no backbuffer presentation. The returned owned Display is GPU complete.</summary>
        public bool RenderOffscreen(double seconds)
        {
            if(!IsInitialized||inContext)throw new InvalidOperationException("Initialize outside rendering first");
            if(double.IsNaN(seconds)||double.IsInfinity(seconds)||Math.Abs(seconds)>1e8)throw new ArgumentOutOfRangeException(nameof(seconds));
            if(GraphicsSettings.currentRenderPipeline!=asset)throw new InvalidOperationException("Another host selected a different pipeline");
            offscreenRequest.seconds=seconds;
            RenderPipeline.SubmitRenderRequest(RenderCamera,offscreenRequest);
            return LastError==null&&displayReady;
        }

        internal void RecordContext(ScriptableRenderContext context,double seconds,bool present)
        {
            inContext=true;presentThisContext=present&&presentToScreen;LastError=null;displayReady=false;
            try
            {
                UpdateContent(seconds);
                context.SetupCameraProperties(RenderCamera);
                DesktopFrameRenderer.OpaqueFrame opaque;
                string error;bool recorded;
                // A failed attempt can already have queued draws. Always submit
                // before the completion/retirement path, including on failure.
                try { recorded=frameRenderer.TryRecord(context,++sequence,1,out opaque,out error); }
                finally { context.Submit(); }
                if(!recorded)throw new InvalidOperationException(error);
                if(!frameRenderer.TryFinishAfterSubmission(opaque,seconds,out var frame,out error))throw new InvalidOperationException(error);
                // Keep the sample-owned display independent of the retired core ticket.
                if(frame.color.graphicsFormat!=Display.graphicsFormat)throw new InvalidOperationException("Example expects its float32 authored grade output");
                Graphics.CopyTexture(frame.color,Display);displayReady=true;RenderedFrames++;LastRenderedTimeSeconds=seconds;
            }
            catch(Exception error) { LastError=error.Message;Debug.LogError("[DesktopHostExample] "+LastError); }
        }

        private void FinishContext(ScriptableRenderContext context,List<Camera> cameras)
        {
            if(!inContext)return;
            var active=RenderTexture.active;bool srgb=GL.sRGBWrite;
            try
            {
                // Unity's SRP backbuffer Blit is deliberately in this callback.
                if(presentThisContext&&displayReady)
                { GL.sRGBWrite=true;Graphics.Blit(Display,(RenderTexture)null); }
                WaitForOwnedGpuWork();
                frameRenderer.RetireAfterGpuCompletion();
            }
            finally { GL.sRGBWrite=srgb;RenderTexture.active=active!=null&&active.IsCreated()?active:null;inContext=false; }
        }

        private void WaitForOwnedGpuWork()
        {
            if(completionTarget==null||completionReadback==null)return;
            var active=RenderTexture.active;
            try
            {
                // Ordered after Submit, all immediate post work and presentation.
                // One-pixel synchronous readback is intentional and documented.
                Graphics.Blit(Texture2D.blackTexture,completionTarget);
                RenderTexture.active=completionTarget;completionReadback.ReadPixels(new Rect(0,0,1,1),0,0,false);
            }
            finally { RenderTexture.active=active!=null&&active.IsCreated()?active:null; }
        }

        public void Shutdown()
        {
            if(inContext)throw new InvalidOperationException("Do not disable or dispose the example inside rendering");
            RenderPipelineManager.endContextRendering-=FinishContext;
            WaitForOwnedGpuWork();frameRenderer?.Dispose();frameRenderer=null;mirror?.Dispose();mirror=null;grade?.Dispose();grade=null;
            // Never overwrite a different host's later pipeline selection.
            if(asset!=null&&GraphicsSettings.renderPipelineAsset==asset)GraphicsSettings.renderPipelineAsset=oldGraphics;
            if(asset!=null&&QualitySettings.renderPipeline==asset)QualitySettings.renderPipeline=oldQuality;
            if(RenderCamera!=null)RenderCamera.targetTexture=null;
            for(int i=owned.Count-1;i>=0;i--)
            {
                var item=owned[i];if(item==null)continue;
                if(item is GameObject go)go.SetActive(false);
                if(item is RenderTexture target)target.Release();
                if(Application.isPlaying)Destroy(item);else DestroyImmediate(item);
            }
            owned.Clear();asset=null;offscreenRequest=null;Configuration=null;Display=null;RenderCamera=null;completionTarget=null;completionReadback=null;
            displayReady=false;if(activeExample==this)activeExample=null;
        }

        private void BuildContent()
        {
            var root=Own(new GameObject("Toolkit generated desktop example"));
            GameObject Primitive(string name,PrimitiveType kind,Vector3 position,Vector3 scale)
            {
                var go=GameObject.CreatePrimitive(kind);go.name=name;go.transform.SetParent(root.transform,false);go.layer=22;
                go.transform.localPosition=position;go.transform.localScale=scale;Destroy(go.GetComponent<Collider>());return go;
            }
            var cameraObject=new GameObject("Example camera");cameraObject.transform.SetParent(root.transform,false);
            RenderCamera=cameraObject.AddComponent<Camera>();RenderCamera.allowMSAA=false;RenderCamera.cullingMask=1<<22;
            RenderCamera.nearClipPlane=.1f;RenderCamera.farClipPlane=30;RenderCamera.fieldOfView=42;RenderCamera.aspect=width/(float)height;
            RenderCamera.transform.position=new Vector3(3,2.6f,-5);RenderCamera.transform.LookAt(new Vector3(0,1,0));
            RenderTexture Target(GraphicsFormat color,GraphicsFormat depth=GraphicsFormat.None,int w=0,int h=0)
            {
                var t=Own(new RenderTexture(new RenderTextureDescriptor(w==0?width:w,h==0?height:h,color,0){depthStencilFormat=depth})
                    {name="Desktop example owned target",filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});
                if(!t.Create())throw new InvalidOperationException("Example target allocation failed");return t;
            }
            var s=Configuration=new DesktopFrameRenderer.Settings { enabled=true };
            s.scene.enabled=true;s.scene.backend=TileRenderPass.BackendPolicy.AllowEmulation;s.scene.geometryDepthId=true;s.scene.positionLighting=true;
            s.scene.output=Target(GraphicsFormat.B10G11R11_UFloatPack32);RenderCamera.targetTexture=s.scene.output;
            s.scene.depthStencil=Target(GraphicsFormat.None,GraphicsFormat.D32_SFloat_S8_UInt);
            s.scene.normalIdentity=Target(GraphicsFormat.R16G16B16A16_SFloat);s.scene.materialBase=Target(GraphicsFormat.R8G8B8A8_SRGB);s.scene.materialMos=Target(GraphicsFormat.R8G8B8A8_UNorm);
            Display=Target(GraphicsFormat.R32G32B32A32_SFloat);completionTarget=Target(GraphicsFormat.R32G32B32A32_SFloat,GraphicsFormat.None,1,1);
            completionReadback=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            var floor=Primitive("Reflective receiving floor",PrimitiveType.Cube,new Vector3(0,-.1f,0),new Vector3(10,.2f,10));
            var wall=Primitive("Background wall",PrimitiveType.Cube,new Vector3(0,2,3),new Vector3(10,4,.2f));
            var sign=Primitive("Authored HDR wall panel",PrimitiveType.Cube,new Vector3(-1.5f,1.8f,2.85f),new Vector3(1.2f,1.7f,.1f));
            SceneDeferredCamera.Surface Surface(GameObject go,Vector3 color,Vector3 mos,Vector3 emission)=>new SceneDeferredCamera.Surface {
                renderer=go.GetComponent<Renderer>(),receiverGroup=1,inputs=new SceneDeferredCamera.MaterialInputs { albedo=color,mos=mos,emission=emission } };
            s.scene.surfaces=new[]{Surface(floor,new Vector3(.15f,.19f,.23f),new Vector3(.3f,1,.92f),Vector3.zero),
                Surface(wall,new Vector3(.21f,.26f,.34f),new Vector3(0,1,.1f),Vector3.zero),
                Surface(sign,Vector3.zero,new Vector3(0,1,0),new Vector3(.08f,.4f,.7f))};
            s.scene.background=new Color(.035f,.05f,.08f,1);s.scene.lightDirection=new Vector3(-.7f,1,-.4f);
            s.scene.lightRadiance=Vector3.one*1.2f;s.scene.ambientIrradiance=Vector3.one*.3f;s.scene.directionalSpecularScale=.2f;
            var ramp=Own(new Texture2D(64,2,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp});
            for(int y=0;y<2;y++)for(int x=0;x<64;x++){float v=Mathf.Lerp(.25f,1,Mathf.SmoothStep(0,1,(x/63f-.4f)*5));ramp.SetPixel(x,y,new Color(v,v,v,1));}ramp.Apply();
            var actors=new List<Renderer>();
            Transform Actor(string name,PrimitiveType kind,Vector3 position,Vector3 scale,Color color)
            {
                var go=Primitive(name,kind,position,scale);var r=go.GetComponent<Renderer>();
                var shader=Resources.Load<Shader>("PhotoModeFallback");if(shader==null)throw new InvalidOperationException("Toolkit Actor shader missing");
                var m=Own(new Material(shader));m.SetVector("_Color",color);m.SetFloat("_VertexColor",0);m.SetFloat("_DisableDefMap",1);
                // Definition channels: ramp offset, smoothness, metallic, specular
                // visibility. Author a dielectric; the shader's archive-oriented
                // constant fallback is fully metallic with no visible specular.
                m.SetVector("_DefValue",new Vector4(.5f,.25f,0,1));
                m.SetFloat("_OutlineEnabled",0);m.SetTexture("_RampTex",ramp);m.SetFloat("_UseAlphaClip",0);r.sharedMaterial=m;actors.Add(r);return go.transform;
            }
            body=Actor("Animated body",PrimitiveType.Capsule,new Vector3(0,1,0),new Vector3(.65f,.55f,.5f),new Color(.12f,.55f,.75f));
            head=Actor("Animated head",PrimitiveType.Sphere,new Vector3(0,1.9f,0),Vector3.one*.7f,new Color(.95f,.67f,.42f));
            leftArm=Actor("Left arm",PrimitiveType.Capsule,new Vector3(-.57f,1.2f,0),new Vector3(.2f,.4f,.2f),new Color(.12f,.55f,.75f));
            rightArm=Actor("Right arm",PrimitiveType.Capsule,new Vector3(.57f,1.2f,0),new Vector3(.2f,.4f,.2f),new Color(.12f,.55f,.75f));
            s.actors.renderers=actors.ToArray();s.actors.outlines=false;s.actors.hairCover=false;
            var probe=new SphericalHarmonicsL2();probe.AddAmbientLight(new Color(.28f,.31f,.36f));s.actors.parameters.SetAmbientProbe(probe);
            s.actors.parameters.SetVector("_CapturedLightDirection",new Vector4(.6f,1,-.8f,1));s.actors.parameters.SetVector("_CapturedLightColor",Vector4.one);
            s.actors.parameters.SetVector("_ActorLightingScales",new Vector4(1,1,1,0));
            s.selfShadow.enabled=true;s.selfShadowDirection=new Vector3(.6f,1,-.8f);s.selfShadow.origin=Vector3.up+s.selfShadowDirection.normalized*6;
            s.selfShadow.halfSize=Vector2.one*3;s.selfShadow.farPlane=15;s.selfShadow.resolution=512;
            s.scene.mainLightShadow=new SceneDirectionalShadowSettings { enabled=true,origin=Vector3.up+s.scene.lightDirection.normalized*6,halfSize=Vector2.one*4,farPlane=15,resolution=512 };
            s.planar.enabled=true;s.planar.planeNormal=Vector3.up;s.planar.receiverGroup=1;s.planar.resolutionScale=.5f;
            s.reflections.enabled=true;s.reflections.sceneOnlyInput=true;s.reflections.normalDistortion=Vector2.zero;
            var cube=Own(new Cubemap(2,TextureFormat.RGBAHalf,false));for(int f=0;f<6;f++)cube.SetPixels(new[]{new Color(.1f,.13f,.18f),new Color(.1f,.13f,.18f),new Color(.1f,.13f,.18f),new Color(.1f,.13f,.18f)},(CubemapFace)f);cube.Apply();s.reflections.probe=cube;
            particle=Primitive("Transparent moving orb",PrimitiveType.Quad,new Vector3(1,1,-.7f),Vector3.one*.8f).transform;particle.GetComponent<Renderer>().enabled=false;
            particleSurface=new LowResolutionFxSurface { mesh=particle.GetComponent<MeshFilter>().sharedMesh,resolution=FxResolution.Half,linearRadiance=new Vector3(.2f,1.5f,2),opacity=.5f,radialSoftness=.8f };
            s.effects.enabled=true;s.effects.geometry.enabled=true;s.effects.geometry.surfaces=new[]{particleSurface};
            s.depthOfField.enabled=true;s.depthOfField.focusNear=3.5f;s.depthOfField.focusFar=7;s.depthOfField.farTransition=2;s.depthOfField.maximumRadius=.008f;
            grade=ColorGradingLut.Bake(new ColorGradingProfile { size=32,maximumInput=8 });s.colorGrade=grade;
        }

        private void UpdateContent(double seconds)
        {
            float t=animate?(float)seconds:0,bounce=.08f*Mathf.Sin(t*2);
            body.position=new Vector3(0,1+bounce,0);head.position=new Vector3(0,1.9f+bounce,0);
            leftArm.rotation=Quaternion.Euler(0,0,25+20*Mathf.Sin(t*2));rightArm.rotation=Quaternion.Euler(0,0,-25-20*Mathf.Sin(t*2));
            particle.position=new Vector3(1.1f*Mathf.Cos(t),1+.4f*Mathf.Sin(t*1.5f),-.7f);particle.rotation=RenderCamera.transform.rotation;
            particleSurface.localToWorld=particle.localToWorldMatrix;
            if(!ActorShadowInputs.TryCapture(Configuration.actors.renderers,0,out var casters,out var error))throw new InvalidOperationException(error);
            Configuration.selfShadow.casters=casters;Configuration.scene.mainLightShadow.casters=casters;
            var reflected=RenderCamera.worldToCameraMatrix*PlanarReflection.ReflectionMatrix(Configuration.planar.planePoint,Configuration.planar.planeNormal);
            if(!mirror.TryRefresh(Configuration.actors.renderers,reflected,new ActorPlanarLighting { lightDirection=Configuration.selfShadowDirection,ambientColor=Vector3.one*.3f },out error))throw new InvalidOperationException(error);
            Configuration.planar.draws=mirror.Draws;
        }
    }

    // The supported engine's native render-loop bridge accepts UnityEngine.Object.
    // A plain managed object's leading zero-valued field can be misread as a
    // null native handle, routing time zero through ordinary wall-clock Render.
    // Own a real Unity object; do not depend on managed field layout/sentinels.
    internal sealed class DesktopExampleRequest:ScriptableObject { public double seconds; }
    internal sealed class DesktopExamplePipelineAsset:RenderPipelineAsset
    {
        internal DesktopHostExample owner;
        protected override RenderPipeline CreatePipeline()=>new DesktopExamplePipeline(owner);
    }
    internal sealed class DesktopExamplePipeline:RenderPipeline
    {
        private readonly DesktopHostExample owner;
        internal DesktopExamplePipeline(DesktopHostExample value) { owner=value; }
        protected override void Render(ScriptableRenderContext context,Camera[] cameras)
        {
            if(owner==null||!owner.IsInitialized||Array.IndexOf(cameras,owner.RenderCamera)<0)return;
            var list=new List<Camera>{owner.RenderCamera};BeginContextRendering(context,list);
            try { owner.RecordContext(context,Time.timeAsDouble,true); }
            finally { EndContextRendering(context,list); }
        }
        protected override bool IsRenderRequestSupported<T>(Camera camera,T request)=>owner!=null&&camera==owner.RenderCamera&&request is DesktopExampleRequest;
        protected override void ProcessRenderRequests<T>(ScriptableRenderContext context,Camera camera,T request)
        {
            if(!(request is DesktopExampleRequest value)||camera!=owner.RenderCamera)throw new ArgumentException("Unexpected example render request");
            var cameras=new List<Camera>{camera};BeginContextRendering(context,cameras);
            try { owner.RecordContext(context,value.seconds,false); }
            finally { EndContextRendering(context,cameras); }
        }
    }
}
