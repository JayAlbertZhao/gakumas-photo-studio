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
        private IEnumerator VerifyTileScene(Report report)
        {
            yield return null;
            var previousGraphics=GraphicsSettings.renderPipelineAsset; var previousQuality=QualitySettings.renderPipeline;
            var previousActive=RenderTexture.active;
            var frames=new List<TileSceneRenderer.PreparedFrame>();
            bool legacy=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_TILE_SCENE_LEGACY")=="1";
            Debug.Log("[TileSceneSelfTest] legacy="+legacy+"; threading="+SystemInfo.renderingThreadingMode);
            try
            {
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                if(!legacy){GraphicsSettings.renderPipelineAsset=pipeline; QualitySettings.renderPipeline=pipeline;}
                var camera=Own(new GameObject("Tile scene request camera")).AddComponent<Camera>(); camera.enabled=false;
                camera.orthographic=true; camera.orthographicSize=1; camera.aspect=64f/48; camera.fieldOfView=50;
                camera.nearClipPlane=.1f; camera.farClipPlane=10; camera.allowMSAA=false;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                camera.renderingPath=RenderingPath.Forward;camera.cullingMask=1<<26;
                var oldScene=legacy?camera.gameObject.AddComponent<SceneDeferredCamera>():null;
                RenderTexture legacyOutput=null;
                float meshAspect=camera.aspect;
                RenderTexture Target(GraphicsFormat format,string name,int width=64,int height=48)
                {
                    var t=Own(new RenderTexture(new RenderTextureDescriptor(width,height,format,0)) { name=name,filterMode=FilterMode.Point });
                    if(!t.Create())throw new InvalidOperationException("Tile scene target allocation failed"); return t;
                }
                var output=Target(GraphicsFormat.B10G11R11_UFloatPack32,"Tile scene final");
                var normal=Target(GraphicsFormat.R16G16B16A16_SFloat,"Tile scene normal identity");
                camera.targetTexture=output;
                var host=Own(new GameObject("Authored tile material plane")); host.transform.position=new Vector3(0,0,3);
                host.layer=25;
                var mesh=Own(new Mesh { name="Authored UV0 UV2 tangent plane" });
                mesh.vertices=new[]{new Vector3(-2*camera.aspect,-2,0),new Vector3(2*camera.aspect,-2,0),new Vector3(2*camera.aspect,2,0),new Vector3(-2*camera.aspect,2,0)};
                mesh.normals=new[]{Vector3.back,Vector3.back,Vector3.back,Vector3.back};
                mesh.tangents=new[]{new Vector4(1,0,0,1),new Vector4(1,0,0,1),new Vector4(1,0,0,1),new Vector4(1,0,0,1)};
                mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.uv2=mesh.uv;
                mesh.triangles=new[]{0,2,1,0,3,2};mesh.RecalculateBounds();
                host.AddComponent<MeshFilter>().sharedMesh=mesh;
                var renderer=host.AddComponent<MeshRenderer>();
                var borrowed=Own(new Material(Resources.Load<Shader>("TileScene")));renderer.sharedMaterial=borrowed;
                var surface=new SceneDeferredCamera.Surface { renderer=renderer,cull=CullMode.Off,receiverGroup=7 };
                var settings=new TileSceneRenderer.Settings { enabled=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,
                    surfaces=new[]{surface},output=output,normalIdentity=normal,ambientIrradiance=Vector3.zero };
                void Check(string name,bool accepted,float value=0)=>FrameworkCheck(report,"tile-scene-"+name,accepted,value);
                Texture2D Texture(Func<int,int,Color> value)
                {
                    var t=Own(new Texture2D(4,4,TextureFormat.RGBAFloat,false,true) { filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp });
                    for(int y=0;y<4;y++)for(int x=0;x<4;x++)t.SetPixel(x,y,value(x,y));t.Apply(false,false);return t;
                }
                var albedo=Texture((x,y)=>new Color(x%2,y%2,(x+y)%2,(x+y)%3==0?0:1));
                var emission=Texture((x,y)=>new Color(x%2*.125f,y%2*.25f,.5f));
                var normalMap=Texture((x,y)=>new Color(.8f,.5f,.9f,1));
                var giMap=Texture((x,y)=>new Color(x%2==0?.5f:1,y%2==0?1:2,.5f,1));
                var shadow=Texture((x,y)=>new Color(x%2,y%2,(x+y)%2,1));
                Color Tex(Texture source,Vector2 uv,Color fallback)
                {
                    if(source==null)return fallback;
                    return ((Texture2D)source).GetPixel(Mathf.Clamp((int)Math.Floor(uv.x*4),0,3),Mathf.Clamp((int)Math.Floor(uv.y*4),0,3));
                }
                void Save(string name,Color[] data)
                {
                    SaveSsrPreview("tile-scene-"+name,data,output.width,output.height,false);
                    using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"tile-scene-"+name+".raw")));
                    foreach(var p in data)for(int c=0;c<4;c++)writer.Write(p[c]);
                }
                Color[] Run(string name)
                {
                    Check(name+"-prepare",TileSceneRenderer.TryPrepare(camera,settings,out var frame,out var error));
                    if(frame==null)throw new InvalidOperationException(error);frames.Add(frame);
                    var request=new TilePassTestRequest { record=context=>{
                        if(!frame.TryRecord(context,out var submitted,out var reason))throw new InvalidOperationException(reason);
                        Check(name+"-submission",submitted.subpasses==3&&submitted.draws==settings.surfaces.Length+2&&
                            submitted.colorTileBits==256&&submitted.depthBits==32&&submitted.nominalBytes==output.width*output.height*28);
                        Check(name+"-one-shot",!frame.TryRecord(context,out _,out var again)&&again.Contains("already recorded"));
                    }};
                    if(!legacy&&!RenderPipeline.SupportsRenderRequest(camera,request))throw new InvalidOperationException("Tile scene request unavailable");
                    bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_SCENE")=="1";
                    string selected=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_SCENE_CASE");
                    capture=!legacy&&capture&&(string.IsNullOrEmpty(selected)||selected==name);bool began=false,ended=false;
                    SceneDeferredCamera.Frame oldFrame=default;
                    if(capture)began=RenderDocCaptureBridge.BeginOffscreenCapture();
                    try
                    {
                        if(legacy)
                        {
                            if(legacyOutput==null||legacyOutput.width!=output.width||legacyOutput.height!=output.height)
                            {
                                legacyOutput=Own(new RenderTexture(output.width,output.height,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));
                                legacyOutput.Create();
                            }
                            camera.targetTexture=legacyOutput;camera.backgroundColor=settings.background;
                            oldScene.sceneEnabled=true;oldScene.sceneLayers=1<<25;oldScene.surfaces=settings.surfaces;
                            oldScene.lightDirection=settings.lightDirection;oldScene.lightRadiance=settings.lightRadiance;
                            oldScene.ambientIrradiance=settings.ambientIrradiance;oldScene.giBaseScale=settings.giBaseScale;
                            oldScene.directionalGiWeight=settings.directionalGiWeight;oldScene.directionalDiffuseScale=settings.directionalDiffuseScale;
                            oldScene.directionalSpecularScale=settings.directionalSpecularScale;oldScene.directionalBacklight=settings.directionalBacklight;
                            oldScene.mainBakedShadowChannel=settings.mainBakedShadowChannel;camera.Render();
                            if(!oldScene.TryGetFrame(out oldFrame))throw new InvalidOperationException(oldScene.UnavailableReason);
                            Check(name+"-existing-camera-consumer",oldScene.SubmittedSurfaces==settings.surfaces.Length);
                        }
                        else RenderPipeline.SubmitRenderRequest(camera,request);
                        if(capture)ReadSceneTarget(output);
                    }
                    finally { if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture(); }
                    if(capture)Check(name+"-capture",began&&ended);
                    var values=ReadSceneTarget(legacy?legacyOutput:output);var normals=ReadSceneTarget(legacy?oldFrame.normalGroup:normal);
                    float maximum=0,normalError=0;bool finite=true;
                    for(int y=0;y<output.height;y++)for(int x=0;x<output.width;x++)
                    {
                        var activeSurface=settings.surfaces.Length>1&&x>=output.width/2?settings.surfaces[1]:surface;
                        Matrix4x4 transform=activeSurface.renderer.localToWorldMatrix;
                        if(activeSurface.renderer is SkinnedMeshRenderer skinned)transform=skinned.bones[0].localToWorldMatrix*skinned.sharedMesh.bindposes[0];
                        float z=transform.m23;
                        float halfHeight=camera.orthographic?1:z*Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f);
                        var world=new Vector3(((x+.5f)/output.width*2-1)*halfHeight*camera.aspect,((y+.5f)/output.height*2-1)*halfHeight,z);
                        Vector3 local=transform.inverse.MultiplyPoint(world);local=new Vector3(local.x/activeSurface.vertexScale.x,local.y/activeSurface.vertexScale.y,local.z/activeSurface.vertexScale.z);
                        Vector2 uv2=new Vector2(local.x/(4*meshAspect)+.5f,local.y/4+.5f);
                        var st=activeSurface.inputs.uvST;var uv=new Vector2(uv2.x*st.x+st.z,uv2.y*st.y+st.w);
                        Color baseSample=Tex(activeSurface.inputs.albedoMap,uv,Color.white),emit=Tex(activeSurface.inputs.emissionMap,uv,Color.white);
                        bool covered=baseSample.a*activeSurface.inputs.alpha>=activeSurface.alphaCutoff;
                        Vector3 n=Vector3.back;
                        if(activeSurface.inputs.normalMap!=null)n=new Vector3(.6f,0,-.8f);
                        // Inverse transpose for the plane normal, affine vector for its tangent.
                        Vector3 ng=transform.inverse.transpose.MultiplyVector(new Vector3(0,0,-1/activeSurface.vertexScale.z)).normalized;
                        Vector3 tangent=transform.MultiplyVector(new Vector3(activeSurface.vertexScale.x,0,0));
                        tangent=(tangent-ng*Vector3.Dot(ng,tangent)).normalized;
                        n=activeSurface.inputs.normalMap!=null?(tangent*.6f+ng*.8f).normalized:ng;
                        Vector3 stored=n;for(int c=0;c<3;c++)stored[c]=Mathf.HalfToFloat(Mathf.FloatToHalf(stored[c]));n=stored.normalized;
                        bool hasGi=activeSurface.gi!=null&&activeSurface.gi.source!=SceneGiSource.None;
                        Color expectNormal=covered?new Color(stored.x,stored.y,stored.z,legacy?activeSurface.receiverGroup:1+activeSurface.receiverGroup+(hasGi?256:0)):Color.clear;
                        for(int c=0;c<4;c++)normalError=Mathf.Max(normalError,Mathf.Abs(normals[y*output.width+x][c]-expectNormal[c]));
                        Vector3 b=Vector3.Scale(new Vector3(baseSample.r,baseSample.g,baseSample.b),activeSurface.inputs.albedo);
                        Color mask=Tex(activeSurface.bakedShadow?.texture,uv2,Color.white);
                        float visibility=1;
                        if(activeSurface.bakedShadow!=null&&activeSurface.bakedShadow.source==SceneBakedShadowSource.Constant)
                            mask=new Color(activeSurface.bakedShadow.visibility.x,activeSurface.bakedShadow.visibility.y,activeSurface.bakedShadow.visibility.z,activeSurface.bakedShadow.visibility.w);
                        if(settings.mainBakedShadowChannel!=SceneBakedShadowChannel.None)visibility=mask[(int)settings.mainBakedShadowChannel-1];
                        Vector2 giUv=uv2;
                        if(hasGi){var gst=activeSurface.gi.lightmapST;giUv=new Vector2(uv2.x*gst.x+gst.z,uv2.y*gst.y+gst.w);}
                        Color giSample=hasGi?Tex(activeSurface.gi.lightmap,giUv,Color.black):Color.black;
                        var gi=new Vector3(giSample.r,giSample.g,giSample.b);
                        if(hasGi&&activeSurface.gi.source==SceneGiSource.Probe)gi=new Vector3(.5f,1,.5f);
                        Vector3 lighting=TileSceneCpuLight(b,activeSurface.inputs.mos,n,camera.orthographic?Vector3.back:-world.normalized,settings,gi,hasGi,visibility);
                        var lightingCandidates=new List<Vector3>{lighting};
                        if(!legacy&&name=="midpoint-srgb-mos")
                        {
                            lightingCandidates.Clear();
                            // Enumerate allowed adjacent format choices before observing the image.
                            // RGB lighting is channel-separable for this material model.
                            foreach(int baseByte in new[]{127,128})for(int bits=0;bits<8;bits++)
                            {
                                float encoded=baseByte/255f;
                                float linear=(float)Math.Pow((encoded+.055)/1.055,2.4);
                                var qmos=new Vector3((127+(bits&1))/255f,(127+((bits>>1)&1))/255f,(127+((bits>>2)&1))/255f);
                                lightingCandidates.Add(TileSceneCpuLight(Vector3.one*linear,qmos,n,Vector3.back,settings,gi,hasGi,visibility));
                            }
                        }
                        Vector3 e=Vector3.Scale(new Vector3(emit.r,emit.g,emit.b),activeSurface.inputs.emission);
                        Color value=values[y*output.width+x];finite&=value.a==1;
                        for(int c=0;c<3;c++)
                        {
                            finite&=!float.IsNaN(value[c])&&!float.IsInfinity(value[c]);
                            // All fixture albedo/MOS/GI values are exactly representable. Only
                            // packed lighting conversion/blend/resolve choices remain discrete.
                            var candidates=new List<double>();
                            if(!covered)candidates.Add(settings.background[c]);
                            else if(legacy)candidates.Add(Math.Min(65504,lighting[c]+e[c]));
                            else foreach(var lightCandidate in lightingCandidates)foreach(double l in TileScenePackedNeighbors(lightCandidate[c],c))
                                foreach(double initial in TileScenePackedNeighbors(e[c],c))
                                    foreach(double sum in TileScenePackedNeighbors(l+initial,c))candidates.Add(sum);
                            double difference=double.PositiveInfinity;
                            foreach(double candidate in candidates)difference=Math.Min(difference,Math.Abs(value[c]-candidate));
                            maximum=Mathf.Max(maximum,(float)difference);
                        }
                    }
                    Check(name+"-whole-independent-light",finite&&maximum<=(legacy?.003f:.0003f),maximum);
                    Check(name+"-whole-normal-identity",normalError<=.0006f,normalError);
                    Save(name,values);Save(name+"-normal",normals);return values;
                }
                surface.inputs.mos=new Vector3(0,1,0);
                Run("dielectric-directional");
                surface.inputs.mos.x=1;Run("metal-directional");surface.inputs.mos.x=0;
                surface.inputs.albedo=Vector3.one*.21404114f;surface.inputs.mos=Vector3.one*.5f;
                settings.lightDirection=new Vector3(.4f,.3f,-1);Run("midpoint-srgb-mos");
                surface.inputs.albedo=Vector3.one;surface.inputs.mos=new Vector3(0,1,0);settings.lightDirection=Vector3.back;
                surface.inputs.albedoMap=albedo;surface.inputs.emissionMap=emission;surface.inputs.emission=Vector3.one;
                Run("textured-emission");surface.alphaCutoff=.5f;Run("cutout-background");
                settings.lightRadiance=Vector3.zero;Run("emission-unlit");settings.lightRadiance=Vector3.one;
                surface.inputs.normalMap=normalMap;settings.lightDirection=new Vector3(.4f,.3f,-1);Run("mapped-normal-oblique-light");
                surface.inputs.mos.z=1;Run("smooth-mapped-specular");surface.inputs.mos.z=0;
                camera.orthographic=false;Run("perspective-depth-view");camera.orthographic=true;
                host.transform.localScale=new Vector3(-1,1,1);Run("mirrored-normal-basis");host.transform.localScale=Vector3.one;
                host.transform.localScale=new Vector3(2,.75f,1.5f);surface.vertexScale=new Vector3(.5f,2,1);
                Run("nonuniform-vertex-scale");host.transform.localScale=Vector3.one;surface.vertexScale=Vector3.one;
                surface.gi=new SceneGiInput { source=SceneGiSource.Lightmap,lightmap=giMap };Run("uv2-baked-gi");
                surface.inputs.uvST=new Vector4(1,1,.25f,0);Run("uv0-independent-from-uv2");surface.inputs.uvST=new Vector4(1,1,0,0);
                var probe=new SphericalHarmonicsL2();probe.AddAmbientLight(new Color(.5f,1,.5f));
                surface.gi=new SceneGiInput { source=SceneGiSource.Probe,probe=probe };Run("explicit-sh-probe");
                surface.gi=new SceneGiInput { source=SceneGiSource.Lightmap,lightmap=giMap };
                settings.directionalGiWeight=1;Run("gi-modulates-direct");
                settings.giBaseScale=0;Run("gi-base-disabled");settings.giBaseScale=1;
                surface.inputs.mos.y=0;Run("ao-indirect-only");surface.inputs.mos.y=1;
                surface.bakedShadow=new SceneBakedShadowInput { source=SceneBakedShadowSource.Texture,texture=shadow,dither=false };
                foreach(var channel in new[]{SceneBakedShadowChannel.R,SceneBakedShadowChannel.G,SceneBakedShadowChannel.B,SceneBakedShadowChannel.A})
                {settings.mainBakedShadowChannel=channel;Run("inline-baked-mask-"+channel);}
                settings.mainBakedShadowChannel=SceneBakedShadowChannel.None;
                settings.lightDirection=Vector3.forward;settings.directionalBacklight=1;Run("back-direction-diffuse");
                surface.gi=null;surface.bakedShadow=null;surface.inputs.albedoMap=null;surface.inputs.normalMap=null;
                surface.inputs.emissionMap=null;surface.inputs.emission=Vector3.one*65504;surface.alphaCutoff=0;
                settings.directionalGiWeight=0;settings.lightDirection=Vector3.back;settings.lightRadiance=Vector3.one*65504;
                Run("finite-hdr-saturation");
                surface.gi=new SceneGiInput { source=SceneGiSource.Lightmap,lightmap=giMap };
                surface.inputs.emission=Vector3.one;surface.inputs.albedoMap=albedo;settings.lightRadiance=Vector3.one;
                Run("after-hdr-fresh-frame");
                var secondHost=Own(new GameObject("Near right-half GI-free receiver"));
                secondHost.layer=25;
                secondHost.transform.position=new Vector3(camera.aspect*.5f,0,2);secondHost.transform.localScale=new Vector3(.25f,1,1);
                secondHost.AddComponent<MeshFilter>().sharedMesh=mesh;var secondRenderer=secondHost.AddComponent<MeshRenderer>();secondRenderer.sharedMaterial=borrowed;
                var second=new SceneDeferredCamera.Surface { renderer=secondRenderer,cull=CullMode.Off,receiverGroup=255 };
                second.inputs.albedo=Vector3.up;second.inputs.mos=new Vector3(0,1,0);
                settings.surfaces=new[]{surface,second};Run("overlap-per-surface-gi-presence");settings.surfaces=new[]{surface};
                var skinHost=Own(new GameObject("Current skinned tile material receiver"));skinHost.transform.position=host.transform.position;
                skinHost.layer=25;
                var bone=Own(new GameObject("Authored tile bone")).transform;bone.SetParent(skinHost.transform,false);
                var skinMesh=Own(Instantiate(mesh));skinMesh.bindposes=new[]{bone.worldToLocalMatrix*skinHost.transform.localToWorldMatrix};
                var weights=new BoneWeight[4];for(int i=0;i<4;i++)weights[i]=new BoneWeight { boneIndex0=0,weight0=1 };skinMesh.boneWeights=weights;
                var skin=skinHost.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=skinMesh;skin.sharedMaterial=borrowed;
                skin.bones=new[]{bone};skin.rootBone=bone;skin.updateWhenOffscreen=true;surface.renderer=skin;
                yield return null;Run("skinned-rest-pose");bone.localPosition=new Vector3(.31f,.17f,0);
                yield return null;Run("skinned-current-bone");surface.renderer=renderer;
                output=Target(GraphicsFormat.B10G11R11_UFloatPack32,"Odd tile scene final",65,47);
                normal=Target(GraphicsFormat.R16G16B16A16_SFloat,"Odd tile normal",65,47);
                settings.output=output;settings.normalIdentity=normal;camera.targetTexture=output;camera.aspect=65f/47;
                // Move both sample domains away from exact Point-texel discontinuities. A
                // mathematically exact UV=.5 can interpolate to either adjacent texel on GPU.
                surface.inputs.uvST=new Vector4(1,1,.03125f,.046875f);
                surface.gi.lightmapST=new Vector4(1,1,.03125f,.046875f);
                Run("odd-dimensions-current-material");
                var before=ReadSceneTarget(output);
                void Reject(string name,Action change,Action restore,string reason)
                {
                    change();bool accepted=TileSceneRenderer.TryPrepare(camera,settings,out var invalid,out var error);
                    Check("reject-"+name,!accepted&&invalid==null&&error!=null&&error.Contains(reason));invalid?.Dispose();restore();
                }
                Reject("disabled",()=>settings.enabled=false,()=>settings.enabled=true,"disabled");
                Reject("leaf",()=>surface.leaf=new VegetationLeafMaterial { enabled=true },()=>surface.leaf=null,"leaf");
                Reject("material-alias",()=>surface.inputs.albedoMap=output,()=>surface.inputs.albedoMap=albedo,"ordinary sampled");
                Reject("gi-alias",()=>surface.gi.lightmap=output,()=>surface.gi.lightmap=giMap,"ordinary sampled");
                Reject("normal-format",()=>settings.normalIdentity=output,()=>settings.normalIdentity=normal,"exact-format");
                Reject("singular-scale",()=>surface.vertexScale=Vector3.zero,()=>surface.vertexScale=Vector3.one,"surface");
                Reject("duplicate-surface",()=>settings.surfaces=new[]{surface,surface},()=>settings.surfaces=new[]{surface},"geometry");
                var block=new MaterialPropertyBlock();block.SetFloat("_Cutoff",1);
                Reject("renderer-property-block",()=>renderer.SetPropertyBlock(block),()=>renderer.SetPropertyBlock(null),"implicit property block");
                Reject("nan-light",()=>settings.lightRadiance=new Vector3(float.NaN,0,0),()=>settings.lightRadiance=Vector3.one,"lighting");
                var depthPlan=new TileRenderPass.Plan { enabled=true,width=output.width,height=output.height,
                    backend=TileRenderPass.BackendPolicy.AllowEmulation,depthAttachment=1,
                    attachments=new[]{new TileRenderPass.Attachment { format=output.graphicsFormat,target=output,store=true },new TileRenderPass.Attachment { format=GraphicsFormat.D32_SFloat }},
                    subpasses=new[]{new TileRenderPass.Subpass { colors=new[]{0} },new TileRenderPass.Subpass { inputs=new[]{1},depthReadOnly=true }} };
                bool depthValid=TileRenderPass.Validate(depthPlan,out _,out var depthError);
                Check("depth-input-policy",SystemInfo.graphicsDeviceType==GraphicsDeviceType.Vulkan?depthValid:!depthValid&&depthError.Contains("read-only Vulkan"));
                Check("rejection-preserves-output",ScenePixelsEqual(before,ReadSceneTarget(output)));
                Check("borrowed-material-unchanged",renderer.sharedMaterial==borrowed&&!borrowed.IsKeywordEnabled("SCENE_BAKED_SHADOW_INPUT"));
                yield return null;
                foreach(var f in frames){f.Dispose();f.Dispose();}
                Check("borrowed-targets-and-mesh-retained",output.IsCreated()&&normal.IsCreated()&&mesh.vertexCount==4&&renderer.sharedMaterial==borrowed);
                Check("disposed-before-context",!frames[0].TryRecord(default,out _,out var disposed)&&disposed.Contains("disposed"));
            }
            finally
            {
                foreach(var frame in frames)frame.Dispose();
                GraphicsSettings.renderPipelineAsset=previousGraphics;QualitySettings.renderPipeline=previousQuality;
                Graphics.SetRenderTarget(previousActive);RenderTexture.active=previousActive;
                foreach(var item in _owned)if(item!=null)Destroy(item);_owned.Clear();
            }
            FrameworkCheck(report,"tile-scene-restores-default-host",GraphicsSettings.renderPipelineAsset==previousGraphics&&QualitySettings.renderPipeline==previousQuality&&RenderTexture.active==previousActive);
        }

        private static Vector3 TileSceneCpuLight(Vector3 color,Vector3 mos,Vector3 n,Vector3 view,TileSceneRenderer.Settings s,Vector3 gi,bool baked,float shadow)
        {
            Vector3 light=s.lightDirection.normalized,half=(view+light).normalized;
            double nl=Math.Max(0,Vector3.Dot(n,light)),nv=Math.Max(0,Vector3.Dot(n,view));
            double nh=Math.Max(0,Vector3.Dot(n,half)),vh=Math.Max(0,Vector3.Dot(view,half));
            double rough=Math.Max(1-mos.z,.045),a2=Math.Pow(rough,4),denom=nh*nh*(a2-1)+1;
            double distribution=a2/Math.Max(Math.PI*denom*denom,1e-8);
            double visibility=.5/Math.Max(nl*Math.Sqrt(nv*nv*(1-a2)+a2)+nv*Math.Sqrt(nl*nl*(1-a2)+a2),1e-6);
            var result=Vector3.zero;
            for(int c=0;c<3;c++)
            {
                double f0=.04*(1-mos.x)+color[c]*mos.x,f=f0+(1-f0)*Math.Pow(1-vh,5),diffuse=color[c]*(1-mos.x)/Math.PI;
                double direct=((1-f)*diffuse*s.directionalDiffuseScale+distribution*visibility*f*s.directionalSpecularScale)*s.lightRadiance[c]*nl;
                direct+=(1-f0)*diffuse*s.directionalDiffuseScale*s.directionalBacklight*s.lightRadiance[c]*Math.Max(0,-Vector3.Dot(n,light));
                if(baked)direct*=1-s.directionalGiWeight+gi[c]*s.directionalGiWeight;
                double indirect=baked?color[c]*(1-mos.x)*gi[c]*s.giBaseScale*mos.y:diffuse*s.ambientIrradiance[c]*mos.y;
                result[c]=(float)(direct*shadow+indirect);
            }
            return result;
        }
        private static double[] TileScenePackedNeighbors(double input,int channel)
        {
            double maximum=channel==2?64512:65024,value=Math.Max(0,Math.Min(maximum,input));
            if(value==0)return new[]{0.0};
            int mantissa=channel==2?5:6;
            double step=Math.Pow(2,Math.Max(-14,Math.Floor(Math.Log(value,2)))-mantissa);
            return new[]{Math.Floor(value/step)*step,Math.Min(maximum,Math.Ceiling(value/step)*step)};
        }
    }
}
