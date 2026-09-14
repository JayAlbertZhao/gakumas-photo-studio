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
        private IEnumerator VerifySrpActorLayers(Report report)
        {
            yield return null;
            var oldGraphics=GraphicsSettings.renderPipelineAsset;var oldQuality=QualitySettings.renderPipeline;
            var oldActive=RenderTexture.active;var scenes=new List<TileSceneRenderer.PreparedFrame>();
            var sets=new List<ActorForwardDrawSet.PreparedFrame>();SrpActorForward actor=null;
            void Check(string name,bool ok,float difference=0)=>FrameworkCheck(report,"srp-actor-layers-"+name,ok,difference);
            try
            {
                const int w=97,h=73;
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                var camera=Own(new GameObject("Layered full Actor SRP camera")).AddComponent<Camera>();camera.enabled=false;
                camera.transform.position=new Vector3(0,0,-3);camera.fieldOfView=55;camera.nearClipPlane=.1f;camera.farClipPlane=30;camera.aspect=(float)w/h;camera.allowMSAA=false;camera.cullingMask=1<<22;
                RenderTexture Target(GraphicsFormat format,string name,GraphicsFormat depth=GraphicsFormat.None)
                { var t=Own(new RenderTexture(new RenderTextureDescriptor(w,h,format,0) { depthStencilFormat=depth }) { name=name,filterMode=FilterMode.Point });if(!t.Create())throw new InvalidOperationException(name);return t; }
                var output=Target(GraphicsFormat.B10G11R11_UFloatPack32,"Layered scene-only color");camera.targetTexture=output;
                var sceneHardwareDepth=Target(GraphicsFormat.None,"Current stored scene raster depth",GraphicsFormat.D32_SFloat_S8_UInt);
                var floatRead=Target(GraphicsFormat.R32G32B32A32_SFloat,"Layered packed readback");
                var reference=Target(GraphicsFormat.R16G16B16A16_SFloat,"Ordinary Forward independent layered target",GraphicsFormat.D32_SFloat_S8_UInt);
                var referenceDepth=Target(GraphicsFormat.R32_SFloat,"Ordinary Forward actual depth readback");
                var referenceCamera=Own(new GameObject("Ordinary layered Forward camera")).AddComponent<Camera>();referenceCamera.enabled=false;
                var depthReader=Own(new Material(Resources.Load<Shader>("ActorForwardDepth")));
                var fullscreen=Own(new Mesh { name="Fixture readback fullscreen" });
                fullscreen.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
                fullscreen.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};fullscreen.triangles=new[]{0,1,2,0,2,3};
                Renderer Quad(string name,Vector3 position,Vector3 scale,Material material,bool reverse=false)
                {
                    var go=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));go.name=name;go.layer=22;go.transform.position=position;go.transform.localScale=scale;
                    if(reverse)go.transform.rotation=Quaternion.Euler(0,180,0);var r=go.GetComponent<Renderer>();r.sharedMaterial=material;return r;
                }
                var sceneSurfaces=new List<SceneDeferredCamera.Surface>();
                void Scene(string name,Vector3 position,Vector3 scale,Vector3 emission)
                {
                    var m=Own(new Material(Resources.Load<Shader>("PlanarCapture")));m.SetVector("_Color",new Vector4(0,0,0,1));m.SetVector("_Emission",emission);
                    var r=Quad(name,position,scale,m);sceneSurfaces.Add(new SceneDeferredCamera.Surface { renderer=r,cull=CullMode.Back,
                        inputs=new SceneDeferredCamera.MaterialInputs { albedo=Vector3.zero,mos=new Vector3(0,1,0),emission=emission } });
                }
                Scene("Far asymmetric scene",new Vector3(.3f,.25f,1),new Vector3(4,3,1),new Vector3(.125f,.25f,.5f));
                Scene("Scene foreground over all Actor layers",new Vector3(-.55f,.15f,-1),new Vector3(.6f,1,1),new Vector3(.5f,.25f,.125f));
                var sceneSettings=new TileSceneRenderer.Settings { enabled=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,geometryDepthId=true,output=output,depthStencil=sceneHardwareDepth,
                    surfaces=sceneSurfaces.ToArray(),background=new Color(.03125f,.0625f,.125f,1),lightRadiance=Vector3.zero,ambientIrradiance=Vector3.zero };
                Texture2D Tex(Color color) { var t=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true) { filterMode=FilterMode.Point });t.SetPixel(0,0,color);t.Apply();return t; }
                var ramp=Tex(new Color(1,1,1,0));var hairMap=Tex(new Color(.6f,.3f,.1f,1));
                Material ActorMaterial(int type,Vector4 tint,int queue)
                {
                    var m=Own(new Material(Resources.Load<Shader>("PhotoModeFallback")));m.SetFloat("_ShaderType",type);m.SetVector("_Color",tint);m.renderQueue=queue;
                    m.SetFloat("_VertexColor",0);m.SetFloat("_DisableDefMap",1);m.SetVector("_DefValue",new Vector4(.5f,0,0,0));m.SetFloat("_OutlineEnabled",0);
                    m.SetTexture("_ShadeTex",Texture2D.blackTexture);m.SetTexture("_RampTex",ramp);m.SetTexture("_RampAddTex",Texture2D.blackTexture);return m;
                }
                var eyeMaterial=ActorMaterial(3,new Vector4(.2f,.5f,.8f,1),2000);eyeMaterial.SetFloat("_StencilRef",68);eyeMaterial.SetFloat("_StencilReadMask",108);eyeMaterial.SetFloat("_StencilWriteMask",108);
                var hairMaterial=ActorMaterial(8,Vector4.one,2301);hairMaterial.SetTexture("_MainTex",hairMap);hairMaterial.SetFloat("_StencilRef",64);hairMaterial.SetFloat("_StencilReadMask",108);
                hairMaterial.SetFloat("_StencilWriteMask",96);hairMaterial.SetFloat("_StencilComp",(float)CompareFunction.GreaterEqual);hairMaterial.SetFloat("_StencilPass",(float)StencilOp.Keep);
                var outlineMaterial=ActorMaterial(8,Vector4.one,2302);outlineMaterial.SetFloat("_StencilComp",(float)CompareFunction.Never);outlineMaterial.SetFloat("_OutlineEnabled",1);
                outlineMaterial.SetVector("_OutlineColor",new Vector4(.1f,.2f,.7f,1));outlineMaterial.SetShaderPassEnabled("ActorHairCover",false);
                var eye=Quad("White-eye stencil region",new Vector3(.2f,0,0),new Vector3(1.3f,.9f,1),eyeMaterial);
                var hair=Quad("Authored marked hair",new Vector3(.1f,0,-.2f),new Vector3(2.2f,1.6f,1),hairMaterial);
                var outline=Quad("Backface outline behind faded bangs",new Vector3(.2f,0,-.1f),new Vector3(1.25f,.85f,1),outlineMaterial,true);outline.enabled=false;
                var parameters=new ActorForwardParameters();parameters.SetFloat("_CapturedDirectScale",1/.96f);parameters.SetFloat("_UseCapturedDirectSpecular",0);
                parameters.SetVector("_HeadDirection",Vector3.back);parameters.SetVector("_HeadUpDirection",Vector3.up);parameters.SetVector("_ActorOutlineParameters",Vector4.zero);
                var materials=new Dictionary<Renderer,Material>();var supplements=new Dictionary<Renderer,Material>();
                var drawSettings=new ActorForwardDrawSet.Settings { parameters=parameters,outlines=false,hairCover=true,
                    configureMaterial=(r,index,m)=>{if(index!=0)throw new InvalidOperationException("Fixture expects single submesh");if(m.shader.name=="GakumasPhotoMode/ActorToon")materials[r]=m;else supplements[r]=m;} };
                actor=new SrpActorForward(camera,new SrpActorForward.Settings { enabled=true });ulong sequence=0;Color[] lastDepth=null;
                Color[] Read(RenderTexture t) { if(t==output){var a=RenderTexture.active;Graphics.Blit(t,floatRead);RenderTexture.active=a;return ReadSceneTarget(floatRead);}return ReadSceneTarget(t); }
                void Save(string name,Color[] pixels)
                { SaveSsrPreview("srp-actor-layers-"+name,pixels,w,h,false);using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"srp-actor-layers-"+name+".raw")));foreach(var p in pixels)for(int c=0;c<4;c++)writer.Write(p[c]); }
                float Difference(Color[] a,Color[] b) { float e=0;for(int p=0;p<a.Length;p++)for(int c=0;c<4;c++)e=Mathf.Max(e,Mathf.Abs(a[p][c]-b[p][c]));return e; }
                Color[] Run(string name,Renderer[] renderers,bool ordinaryProbe=false)
                {
                    drawSettings.renderers=renderers;materials.Clear();supplements.Clear();
                    if(!ActorForwardDrawSet.TryPrepare(camera,drawSettings,out var draws,out var why))throw new InvalidOperationException(why);sets.Add(draws);
                    if(!TileSceneRenderer.TryPrepare(camera,sceneSettings,out var scene,out why))throw new InvalidOperationException(why);scenes.Add(scene);sequence++;
                    string error=null;SrpActorForward.Frame frame=default;
                    RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>{if(!scene.TryRecord(context,out _,out error))return;} });
                    if(error!=null)throw new InvalidOperationException(error);var before=Read(output);var sourceDepth=Read(scene.EyeDepth);
                    bool requested=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SRP_ACTOR")=="1";
                    string selected=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SRP_ACTOR_CASE");requested&=string.IsNullOrEmpty(selected)||selected==name;
                    bool began=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();
                    Color[] actual=null,actualDepth=null;
                    try
                    {
                        RenderPipeline.SubmitRenderRequest(camera,new TilePassTestRequest { record=context=>actor.TryRecord(context,scene,draws,sequence,out frame,out error) });
                        if(error!=null||!frame.IsCurrent)throw new InvalidOperationException(error??"Missing layered output");
                        actual=Read(frame.color);actualDepth=Read(frame.eyeDepth);lastDepth=actualDepth;
                        Check(name+"-scene-inputs-unchanged",ScenePixelsEqual(before,Read(output))&&ScenePixelsEqual(sourceDepth,Read(scene.EyeDepth)));
                        Save(name+"-color",actual);Save(name+"-depth",actualDepth);Save(name+"-scene",before);Save(name+"-scene-depth",sourceDepth);
                    }
                    finally { if(began)Check(name+"-native-capture",RenderDocCaptureBridge.EndOffscreenCapture()); }
                    // Independent reference: ordinary automatic main draws, plus an
                    // explicitly authored BeforeForwardAlpha schedule. Never enumerate
                    // the SRP preparation's internal commands to construct this schedule.
                    var original=new Dictionary<Renderer,Material>();foreach(var r in renderers)original[r]=r.sharedMaterial;
                    try
                    {
                        GraphicsSettings.renderPipelineAsset=null;QualitySettings.renderPipeline=null;
                        referenceCamera.CopyFrom(camera);referenceCamera.enabled=false;referenceCamera.renderingPath=RenderingPath.Forward;
                        referenceCamera.transform.SetPositionAndRotation(camera.transform.position,camera.transform.rotation);referenceCamera.targetTexture=reference;
                        referenceCamera.clearFlags=CameraClearFlags.Nothing;referenceCamera.allowHDR=true;
                        using var clear=new CommandBuffer { name="Reference literal linear clear" };clear.SetRenderTarget(reference);clear.ClearRenderTarget(true,true,sceneSettings.background);
                        using var supplemental=new CommandBuffer { name="Reference body outline, hair cover, hair outline" };
                        foreach(var r in renderers)if(r.enabled)
                        {
                            r.sharedMaterial=materials[r];
                            // The probe oracle must use Unity's engine-owned SH buffer,
                            // not the material-local function being tested by the SRP.
                            if(ordinaryProbe)materials[r].SetFloat("_UseActorForwardAmbientSH",0);
                        }
                        void Pass(string pass,bool hairOnly)
                        {
                            foreach(var r in renderers)
                            {
                                if(!r.enabled||!supplements.TryGetValue(r,out var m))continue;
                                bool isHair=original[r].GetFloat("_ShaderType")==8;if(isHair!=hairOnly)continue;
                                if(pass=="ACTOR_HAIR_COVER"&&!original[r].GetShaderPassEnabled("ActorHairCover"))continue;
                                if(pass=="ACTOR_OUTLINE"&&(original[r].GetFloat("_OutlineEnabled")<=.5f||!original[r].GetShaderPassEnabled("ActorOutline")))continue;
                                supplemental.DrawRenderer(r,m,0,m.FindPass(pass));
                            }
                        }
                        if(drawSettings.outlines)Pass("ACTOR_OUTLINE",false);
                        if(drawSettings.hairCover)Pass("ACTOR_HAIR_COVER",true);
                        if(drawSettings.outlines)Pass("ACTOR_OUTLINE",true);
                        referenceCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,clear);referenceCamera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha,supplemental);
                        bool referenceBegan=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();
                        try
                        {
                            referenceCamera.Render();
                            // Decode a different, ordinary camera's actual hardware depth.
                            // This readback math was separately checked against CPU/native
                            // depth. Native acceptance also compares raw D32/stencil bytes.
                            depthReader.SetTexture("_ActorHardwareDepth",reference,RenderTextureSubElement.Depth);depthReader.SetVector("_ActorTargetSize",new Vector4(w,h,0,0));
                            depthReader.SetMatrix("_ActorInverseProjection",GL.GetGPUProjectionMatrix(referenceCamera.projectionMatrix,true).inverse);
                            using var readDepth=new CommandBuffer { name="Read independent Forward hardware depth" };readDepth.SetRenderTarget(referenceDepth);readDepth.SetViewport(new Rect(0,0,w,h));
                            readDepth.DrawMesh(fullscreen,Matrix4x4.identity,depthReader,0,1);Graphics.ExecuteCommandBuffer(readDepth);
                            var expected=Read(reference);var expectedDepth=Read(referenceDepth);float colorError=Difference(actual,expected),depthError=0;bool finite=true;int visible=0;
                            for(int p=0;p<actual.Length;p++)
                            { depthError=Mathf.Max(depthError,Mathf.Abs(actualDepth[p].r-expectedDepth[p].r));for(int c=0;c<4;c++)finite&=!float.IsNaN(actual[p][c])&&!float.IsInfinity(actual[p][c])&&!float.IsNaN(expected[p][c])&&!float.IsInfinity(expected[p][c]);
                                finite&=!float.IsNaN(actualDepth[p].r)&&!float.IsInfinity(actualDepth[p].r)&&!float.IsNaN(expectedDepth[p].r)&&!float.IsInfinity(expectedDepth[p].r);
                                if(Mathf.Abs(actual[p].r-before[p].r)+Mathf.Abs(actual[p].g-before[p].g)+Mathf.Abs(actual[p].b-before[p].b)>.01f)visible++; }
                            Check(name+"-ordinary-forward-whole-color",colorError<.00001f,colorError);Check(name+"-ordinary-forward-whole-depth",depthError<.00001f,depthError);
                            Check(name+"-finite-visible",finite&&visible>100,visible);Save(name+"-reference",expected);Save(name+"-reference-depth",expectedDepth);
                        }
                        finally
                        {
                            if(referenceBegan)Check(name+"-native-reference-capture",RenderDocCaptureBridge.EndOffscreenCapture());
                            referenceCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,clear);referenceCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha,supplemental);
                        }
                    }
                    finally { foreach(var pair in original){pair.Key.sharedMaterial=pair.Value;if(ordinaryProbe)materials[pair.Key].SetFloat("_UseActorForwardAmbientSH",1);}GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline; }
                    return actual;
                }
                var pair=new[]{hair,eye};var baseline=Run("front-marked-bangs",pair);var baselineDepth=lastDepth;
                drawSettings.hairCover=false;var missing=Run("hair-cover-disabled",pair);drawSettings.hairCover=true;
                Check("fully-faded-cover-color-stable",ScenePixelsEqual(baseline,missing));
                Check("fully-faded-cover-owns-depth",Difference(baselineDepth,lastDepth)>.1f,Difference(baselineDepth,lastDepth));
                foreach(float mask in new[]{0f,.5f,1f})
                { hairMap.SetPixel(0,0,new Color(.6f,.3f,.1f,mask));hairMap.Apply();var color=Run("hair-mask-"+Mathf.RoundToInt(mask*100),pair);
                    if(mask==.5f)Check("half-covered-bangs-color-positive-control",Difference(color,baseline)>.1f,Difference(color,baseline)); }
                parameters.SetVector("_HeadDirection",Vector3.right);var oblique=Run("head-oblique",pair);Check("head-angle-changes-marked-coverage",Difference(baseline,oblique)>.1f);
                parameters.SetVector("_HeadDirection",Vector3.back);parameters.SetVector("_HeadUpDirection",new Vector3(0,.8f,.6f));Run("head-elevated",pair);parameters.SetVector("_HeadUpDirection",Vector3.up);
                drawSettings.outlines=true;outline.enabled=true;var trio=new[]{outline,hair,eye};var blocked=Run("faded-bangs-block-own-outline",trio);
                hairMaterial.SetFloat("_ZWrite",0);var leaking=Run("hair-depth-optout-outline-control",trio);hairMaterial.SetFloat("_ZWrite",1);
                Check("hair-cover-depth-positive-control",Difference(blocked,leaking)>.1f,Difference(blocked,leaking));
                Check("hair-depth-restore-exact",ScenePixelsEqual(blocked,Run("hair-depth-restored",trio)));
                eyeMaterial.SetFloat("_StencilWriteMask",0);Run("eye-stencil-disabled",trio);eyeMaterial.SetFloat("_StencilWriteMask",108);
                Check("stencil-clear-and-replay-exact",ScenePixelsEqual(blocked,Run("eye-stencil-restored",trio)));
                outline.enabled=false;drawSettings.outlines=false;hair.enabled=false;eye.enabled=false;
                var farAlpha=ActorMaterial(0,new Vector4(.2f,.8f,.1f,.5f),3000);var nearAlpha=ActorMaterial(0,new Vector4(.8f,.1f,.6f,.5f),3000);
                foreach(var m in new[]{farAlpha,nearAlpha}){m.SetFloat("_SrcBlend",5);m.SetFloat("_DstBlend",10);m.SetFloat("_SrcAlphaBlend",1);m.SetFloat("_DstAlphaBlend",10);m.SetFloat("_ZWrite",0);}
                var far=Quad("Far straight-alpha Actor",new Vector3(.2f,0,0),new Vector3(1.7f,1.3f,1),farAlpha);
                var near=Quad("Near straight-alpha Actor",new Vector3(.3f,.1f,-.3f),new Vector3(1.5f,1.3f,1),nearAlpha);
                var transparent=Run("transparent-back-to-front",new[]{near,far});Check("transparent-input-order-independent",ScenePixelsEqual(transparent,Run("transparent-input-reversed",new[]{far,near})));
                nearAlpha.renderQueue=2999;var opposite=Run("transparent-authored-queue",new[]{near,far});Check("transparent-queue-positive-control",Difference(transparent,opposite)>.1f);nearAlpha.renderQueue=3000;
                nearAlpha.SetFloat("_SrcBlend",1);nearAlpha.SetFloat("_ShaderType",4);Run("eye-premultiplied-over-alpha",new[]{near,far});
                nearAlpha.SetFloat("_ShaderType",5);nearAlpha.SetFloat("_SrcBlend",5);nearAlpha.SetFloat("_DstBlend",1);Run("additive-highlight-over-alpha",new[]{near,far});
                near.enabled=false;var single=new[]{far};farAlpha.renderQueue=2000;farAlpha.SetVector("_Color",new Vector4(.75f,.5f,.25f,1));
                farAlpha.SetFloat("_SrcBlend",1);farAlpha.SetFloat("_DstBlend",0);farAlpha.SetFloat("_DstAlphaBlend",0);farAlpha.SetFloat("_ZWrite",1);
                var cutout=Own(new Texture2D(8,8,TextureFormat.RGBAFloat,false,true) { filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp });
                var cutoutPixels=new Color[64];for(int y=0;y<8;y++)for(int x=0;x<8;x++)cutoutPixels[y*8+x]=new Color(.25f+x/16f,.75f-y/16f,.5f,(x+y)%3==0?0:1);
                cutout.SetPixels(cutoutPixels);cutout.Apply();farAlpha.SetTexture("_MainTex",cutout);farAlpha.SetFloat("_UseAlphaClip",1);
                Run("authored-cutout",single);farAlpha.SetFloat("_UseAlphaClip",0);
                var block=new MaterialPropertyBlock();block.SetVector("_ActorColor",new Vector4(.6f,.4f,.8f,.5f));far.SetPropertyBlock(block);Run("actor-tint-and-dither-fade",single);
                var submeshBlock=new MaterialPropertyBlock();submeshBlock.SetVector("_Color",new Vector4(.125f,.75f,.5f,1));far.SetPropertyBlock(submeshBlock,0);
                Run("submesh-block-replaces-renderer-block",single);far.SetPropertyBlock(null);far.SetPropertyBlock(null,0);
                farAlpha.SetVector("_ActorTextureFrame",new Vector4(.5f,.5f,.5f,0));Run("motion-atlas-frame",single);farAlpha.SetVector("_ActorTextureFrame",Vector4.zero);
                farAlpha.SetTexture("_MainTex",Texture2D.whiteTexture);var unlit=Run("explicit-light-baseline",single);
                parameters.SetAdditionalLightCount(2);
                parameters.SetVectorArray("_ActorAdditionalPositions",new[]{new Vector4(-.8f,.2f,-1,.1f),new Vector4(.8f,.4f,-1,.15f)});
                parameters.SetVectorArray("_ActorAdditionalColors",new[]{new Vector4(.1f,.8f,.3f,0),new Vector4(.8f,.2f,.1f,0)});
                parameters.SetVectorArray("_ActorAdditionalDirections",new[]{new Vector4(0,0,1,-1),new Vector4(0,0,1,.6f)});
                parameters.SetVectorArray("_ActorAdditionalSpots",new[]{Vector4.zero,new Vector4(.85f,0,0,0)});
                var localLit=Run("explicit-point-and-spot",single);Check("explicit-lights-positive-control",Difference(unlit,localLit)>.05f,Difference(unlit,localLit));parameters.SetAdditionalLightCount(0);
                parameters.SetFloat("_UseCapturedAmbientSH",1);parameters.SetVector("_ActorLightingScales",new Vector4(1,1,1,0));
                parameters.SetVector("_CapturedSH0",new Vector4(0,0,0,.3f));parameters.SetVector("_CapturedSH1",new Vector4(0,0,0,.1f));parameters.SetVector("_CapturedSH2",new Vector4(0,0,0,.2f));
                var ambient=Run("explicit-authored-ambient",single);Check("explicit-ambient-positive-control",Difference(unlit,ambient)>.05f,Difference(unlit,ambient));
                parameters.SetFloat("_UseCapturedAmbientSH",0);parameters.SetVector("_ActorLightingScales",new Vector4(0,1,1,0));
                var probe=new SphericalHarmonicsL2();
                for(int c=0;c<3;c++)for(int coefficient=0;coefficient<9;coefficient++)
                    probe[c,coefficient]=coefficient==0?.55f+.15f*c:((c+coefficient)%2==0?1:-1)*(.0125f+.004f*c)*coefficient;
                parameters.SetAmbientProbe(probe);parameters.SetVector("_ActorLightingScales",new Vector4(1,1,1,0));
                var probeBlock=new MaterialPropertyBlock();probeBlock.CopySHCoefficientArraysFrom(new[]{probe});
                var oldProbeUsage=far.lightProbeUsage;far.lightProbeUsage=LightProbeUsage.CustomProvided;far.SetPropertyBlock(probeBlock);
                var probeLit=Run("renderer-sh-engine-oracle",single,true);
                Check("renderer-sh-positive-control",Difference(unlit,probeLit)>.05f,Difference(unlit,probeLit));
                foreach(float angle in new[]{-40f,35f})
                {
                    far.transform.rotation=Quaternion.Euler(angle*.5f,angle,0);
                    Run("renderer-sh-oblique-"+angle,single,true);
                }
                far.transform.rotation=Quaternion.identity;
                Check("renderer-sh-normal-revisit-exact",ScenePixelsEqual(probeLit,Run("renderer-sh-restored",single,true)));
                far.SetPropertyBlock(null);far.lightProbeUsage=oldProbeUsage;
                parameters.SetAmbientProbe(new SphericalHarmonicsL2());parameters.SetVector("_ActorLightingScales",new Vector4(0,1,1,0));
                farAlpha.SetFloat("_UseEmission",1);farAlpha.SetTexture("_EmissionMap",Tex(new Color(1,.25f,.5f,1)));farAlpha.SetVector("_EmissionColor",new Vector4(.5f,1,.75f,0));
                var emissive=Run("material-hdr-emission",single);Check("material-emission-positive-control",Difference(unlit,emissive)>.2f,Difference(unlit,emissive));farAlpha.SetFloat("_UseEmission",0);
                parameters.SetFloat("_UseCapturedDirectSpecular",1);parameters.SetFloat("_ActorEnvironmentIntensity",1);
                var cube=Own(new Cubemap(4,TextureFormat.RGBAFloat,false));
                for(int face=0;face<6;face++){var pixels=new Color[16];for(int p=0;p<16;p++)pixels[p]=new Color(.1f*(face+1),.2f,.3f,1);cube.SetPixels(pixels,(CubemapFace)face);}cube.Apply();
                parameters.SetTexture("_ActorEnvironmentCube",cube);parameters.SetTexture("_ActorEyeEnvironmentCube",cube);
                farAlpha.SetVector("_DefValue",new Vector4(.5f,.4f,.7f,0));Run("explicit-environment-cubemap",single);
                farAlpha.SetFloat("_ShaderType",4);farAlpha.SetFloat("_SrcBlend",1);farAlpha.SetFloat("_DstBlend",10);Run("eye-environment-cubemap",single);
                // Adversarial equality case: reconstructing hardware Z from eye depth
                // can lose a ULP and reject a coincident Actor that should pass LEqual.
                // Use a distinct opaque queue so the ordinary Actor is unambiguously last.
                parameters.SetFloat("_ActorEnvironmentIntensity",0);parameters.SetFloat("_UseCapturedDirectSpecular",0);
                farAlpha.SetFloat("_ShaderType",0);farAlpha.SetFloat("_DstBlend",0);farAlpha.SetVector("_DefValue",new Vector4(.5f,0,0,0));farAlpha.renderQueue=2400;
                far.transform.position=new Vector3(-.55f,.15f,-1);far.transform.localScale=new Vector3(.6f,1,1);
                foreach(var range in new[]{new Vector2(.1f,30),new Vector2(.03f,500),new Vector2(.37f,17)})
                {
                    camera.nearClipPlane=range.x;camera.farClipPlane=range.y;
                    Run("coplanar-near-"+Mathf.RoundToInt(range.x*100),single);
                }
                camera.orthographic=true;camera.orthographicSize=1.8f;Run("coplanar-orthographic",single);
            }
            finally
            {
                RenderTexture.active=oldActive;actor?.Dispose();foreach(var s in sets)s.Dispose();foreach(var s in scenes)s.Dispose();
                GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;
                foreach(var value in _owned){if(value is RenderTexture t)t.Release();if(value!=null)DestroyImmediate(value);}_owned.Clear();
            }
            yield return null;
        }
    }
}
