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
        private IEnumerator VerifyTilePosition(Report report)
        {
            yield return null;
            var previousGraphics=GraphicsSettings.renderPipelineAsset;var previousQuality=QualitySettings.renderPipeline;
            var previousActive=RenderTexture.active;var frames=new List<TileSceneRenderer.PreparedFrame>();
            void Check(string name,bool ok,float value=0)=>FrameworkCheck(report,"tile-position-"+name,ok,value);
            try
            {
                var pipeline=Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                GraphicsSettings.renderPipelineAsset=pipeline;QualitySettings.renderPipeline=pipeline;
                var camera=Own(new GameObject("Tile position independent camera")).AddComponent<Camera>();camera.enabled=false;
                camera.orthographic=true;camera.orthographicSize=1;camera.aspect=64f/48;camera.fieldOfView=50;
                camera.nearClipPlane=.1f;camera.farClipPlane=20;camera.allowMSAA=false;
                var output=Own(new RenderTexture(new RenderTextureDescriptor(64,48,GraphicsFormat.B10G11R11_UFloatPack32,0)));output.Create();
                var normal=Own(new RenderTexture(new RenderTextureDescriptor(64,48,GraphicsFormat.R16G16B16A16_SFloat,0)));normal.Create();
                // This engine's CPU packed-HDR ReadPixels conversion mishandles positive
                // subnormals. Convert on the GPU, then read native float32 channels.
                var colorReadback=Own(new RenderTexture(new RenderTextureDescriptor(64,48,GraphicsFormat.R32G32B32A32_SFloat,0)));colorReadback.Create();
                camera.targetTexture=output;
                var mesh=Own(new Mesh { name="Independent oversized depth plane" });
                mesh.vertices=new[]{new Vector3(-8,-8,0),new Vector3(8,-8,0),new Vector3(8,8,0),new Vector3(-8,8,0)};
                mesh.normals=new[]{Vector3.back,Vector3.back,Vector3.back,Vector3.back};
                mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.uv2=mesh.uv;
                mesh.triangles=new[]{0,2,1,0,3,2};mesh.RecalculateBounds();
                var host=Own(new GameObject("Current depth receiver"));host.AddComponent<MeshFilter>().sharedMesh=mesh;
                var renderer=host.AddComponent<MeshRenderer>();renderer.sharedMaterial=Own(new Material(Resources.Load<Shader>("TileScene")));
                var surface=new SceneDeferredCamera.Surface { renderer=renderer,cull=CullMode.Off,receiverGroup=7 };
                surface.inputs.albedo=Vector3.one;surface.inputs.mos=new Vector3(0,1,0);
                var settings=new TileSceneRenderer.Settings { enabled=true,positionLighting=true,backend=TileRenderPass.BackendPolicy.AllowEmulation,
                    surfaces=new[]{surface},output=output,normalIdentity=normal,lightRadiance=Vector3.zero,ambientIrradiance=Vector3.zero,
                    giBaseScale=0,localLightBackend=SceneForwardLightBackend.BruteForce };
                var light=new SceneDecalLight { position=new Vector3(.2f,.1f,.5f),range=6,radiance=new Vector3(2,1,.5f),
                    specularScale=0,halfLength=.6f,halfSize=new Vector2(.7f,.6f),areaSpread=new Vector2(.15f,.2f) };
                settings.localLights=new SceneDecalLightSettings { enabled=true,lights=new[]{light} };
                Texture2D Constant(Color color)
                { var t=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));t.SetPixel(0,0,color);t.Apply();return t; }
                var giTexture=Constant(new Color(.5f,1,2,1));var atlas=Constant(new Color(2,.5f,1,1));
                float localVisibility=1,mainVisibility=1;
                int shadowMaps=0;
                var casterHost=Own(new GameObject("Current moving shadow plane"));casterHost.transform.position=new Vector3(0,0,1.5f);
                casterHost.AddComponent<MeshFilter>().sharedMesh=mesh;var casterRenderer=casterHost.AddComponent<MeshRenderer>();casterRenderer.sharedMaterial=renderer.sharedMaterial;
                var caster=new SceneShadowCaster { renderer=casterRenderer,cull=CullMode.Off };
                settings.localLights.shadows=new SceneLightShadowSettings { tileResolution=64,casters=new[]{caster} };
                void Save(string name,Color[] values)
                {
                    SaveSsrPreview("tile-position-"+name,values,output.width,output.height,false);
                    using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"tile-position-"+name+".raw")));
                    foreach(var value in values)for(int c=0;c<4;c++)writer.Write(value[c]);
                }
                void Run(string name)
                {
                    Check(name+"-prepare",TileSceneRenderer.TryPrepare(camera,settings,out var frame,out var error));
                    if(frame==null)throw new InvalidOperationException(error);frames.Add(frame);
                    Check(name+"-actual-backend-and-shadows",frame.LocalLightBackend==settings.localLightBackend&&frame.ShadowMapCount==shadowMaps);
                    var request=new TilePassTestRequest { record=context=>{
                        if(!frame.TryRecord(context,out var submission,out var reason))throw new InvalidOperationException(reason);
                        Check(name+"-budget",submission.colorTileBits==256&&submission.nominalBytes==64*48*28&&
                            frame.DepthBudget.colorTileBits==32&&frame.DepthBudget.nominalBytes==64*48*8&&frame.DepthBudget.draws==1);
                        Check(name+"-one-shot",!frame.TryRecord(context,out _,out _));
                    }};
                    bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_POSITION")=="1";
                    string selected=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_POSITION_CASE");
                    capture&=string.IsNullOrEmpty(selected)||selected==name;
                    bool began=capture&&RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try { RenderPipeline.SubmitRenderRequest(camera,request);if(capture)ReadSceneTarget(output); }
                    finally { if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture(); }
                    if(capture)Check(name+"-capture",began&&ended);
                    var oldActive=RenderTexture.active;
                    Graphics.Blit(output,colorReadback);RenderTexture.active=oldActive;
                    var colors=ReadSceneTarget(colorReadback);var depths=ReadSceneTarget(frame.EyeDepth);var normals=ReadSceneTarget(normal);
                    float depthError=0,colorError=0,normalError=0;bool finite=true;
                    Vector3 planeNormal=host.transform.TransformDirection(Vector3.back);
                    for(int y=0;y<48;y++)for(int x=0;x<64;x++)
                    {
                        float sx=((x+.5f)/64*2-1)*camera.aspect,sy=(y+.5f)/48*2-1;
                        float scale=Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f);
                        Vector3 origin=camera.transform.TransformPoint(camera.orthographic?new Vector3(sx,sy,0):Vector3.zero);
                        Vector3 ray=camera.transform.TransformDirection(camera.orthographic?Vector3.forward:new Vector3(sx*scale,sy*scale,1));
                        float t=Vector3.Dot(host.transform.position-origin,planeNormal)/Vector3.Dot(ray,planeNormal);
                        Vector3 world=origin+ray*t;float expectedDepth=camera.transform.InverseTransformPoint(world).z;
                        int p=y*64+x;
                        Vector3 local=host.transform.InverseTransformPoint(world);
                        local=new Vector3(local.x/surface.vertexScale.x,local.y/surface.vertexScale.y,local.z/surface.vertexScale.z);
                        var uv=surface.inputs.uvST;float alpha=1;
                        if(surface.inputs.albedoMap is Texture2D alphaMap)
                        {
                            int tx=Mathf.Clamp((int)Math.Floor(((local.x/16+.5f)*uv.x+uv.z)*alphaMap.width),0,alphaMap.width-1);
                            int ty=Mathf.Clamp((int)Math.Floor(((local.y/16+.5f)*uv.y+uv.w)*alphaMap.height),0,alphaMap.height-1);
                            alpha=alphaMap.GetPixel(tx,ty).a;
                        }
                        bool covered=alpha*surface.inputs.alpha>=surface.alphaCutoff;
                        depthError=Mathf.Max(depthError,Mathf.Abs(depths[p].r-(covered?expectedDepth:0)));
                        // Validate storage against independently computed geometry first. CPU
                        // FloatToHalf and render-target conversion need not select the same bin.
                        for(int c=0;c<3;c++)
                        {
                            float errorNormal=float.PositiveInfinity;
                            // Perspective interpolation/rsqrt may straddle an exact Half boundary.
                            // Bound the float arithmetic before enumerating storage bins.
                            foreach(float deltaNormal in covered?new[]{-1e-6f,0,1e-6f}:new[]{0f})
                                foreach(float allowed in TilePositionHalfNeighbors(covered?planeNormal[c]+deltaNormal:0))
                                    errorNormal=Mathf.Min(errorNormal,Mathf.Abs(normals[p][c]-allowed));
                            normalError=Mathf.Max(normalError,errorNormal);
                        }
                        Vector3 n=new Vector3(normals[p].r,normals[p].g,normals[p].b);
                        // Depth has independently passed the ray/plane field check above.
                        // Evaluate lighting at the actually stored position, using the analytic
                        // camera ray rather than the shader's inverse-VP reconstruction.
                        world=origin+ray*depths[p].r;
                        Vector3 view=camera.orthographic?-camera.transform.forward:(camera.transform.position-world).normalized;
                        Vector3 sum=Vector3.zero;
                        foreach(var current in settings.localLights.lights)
                        {
                            float mask=current.bakedShadowChannel==SceneBakedShadowChannel.None?1:surface.bakedShadow.visibility[(int)current.bakedShadowChannel-1];
                            float visibility=current.shadow!=null&&current.shadow.enabled?Mathf.Min(mask,localVisibility):mask;
                            sum+=TilePositionCpuLocal(current,world,n.normalized,view,surface.receiverGroup,
                                surface.gi!=null&&surface.gi.source!=SceneGiSource.None,new Vector3(.5f,1,2),settings.localLights.atlas!=null?new Vector3(2,.5f,1):Vector3.one)*visibility;
                        }
                        float mainMask=settings.mainBakedShadowChannel==SceneBakedShadowChannel.None?1:surface.bakedShadow.visibility[(int)settings.mainBakedShadowChannel-1];
                        sum+=TileSceneCpuLight(Vector3.one,new Vector3(0,1,0),n.normalized,view,settings,new Vector3(.5f,1,2),false,Mathf.Min(mainMask,mainVisibility));
                        if(!covered)sum=Vector3.zero;
                        for(int c=0;c<3;c++)
                        {
                            double difference=double.MaxValue;
                            // Propagate the float reconstruction/BRDF tolerance before the
                            // discontinuous HDR conversion. An exact-zero visibility stays exact.
                            double arithmetic=sum[c]==0?0:2e-5*Math.Max(1,Math.Abs(sum[c]));
                            foreach(double bound in new[]{sum[c]-arithmetic,(double)sum[c],sum[c]+arithmetic})
                                foreach(double allowed in TileScenePackedNeighbors(bound,c))difference=Math.Min(difference,Math.Abs(colors[p][c]-allowed));
                            colorError=Mathf.Max(colorError,(float)difference);
                            finite&=!float.IsNaN(colors[p][c])&&!float.IsInfinity(colors[p][c]);
                        }
                        finite&=colors[p].a==1;
                    }
                    Check(name+"-whole-real-depth",depthError<2e-4f,depthError);
                    Check(name+"-whole-normal",normalError<1e-6f,normalError);
                    Check(name+"-whole-independent-position-light",colorError<4e-4f&&finite,colorError);
                    Save(name+"-color",colors);Save(name+"-eye-depth",depths);Save(name+"-normal",normals);
                }
                foreach(bool perspective in new[]{false,true})foreach(var shape in new[]{SceneDecalLightShape.Point,SceneDecalLightShape.Capsule,SceneDecalLightShape.Area,SceneDecalLightShape.Spot})
                {
                    camera.orthographic=!perspective;light.shape=shape;light.spotInnerAngle=35;light.spotOuterAngle=65;
                    foreach(float z in new[]{2.2f,4.1f})
                    {
                        host.transform.position=new Vector3(0,0,z);host.transform.rotation=Quaternion.Euler(7,13,0);
                        string name=(perspective?"perspective":"orthographic")+"-"+shape+"-"+(z<3?"near":"far");
                        settings.localLightBackend=SceneForwardLightBackend.BruteForce;Run(name+"-brute");
                        settings.localLightBackend=SceneForwardLightBackend.Tiled;settings.allowLightFallback=false;Run(name+"-grid");
                    }
                }
                camera.orthographic=true;host.transform.position=new Vector3(0,0,3);host.transform.rotation=Quaternion.identity;
                light.shape=SceneDecalLightShape.Point;light.receiverGroup=8;Run("different-group-rejected");
                light.receiverGroup=7;Run("matching-group");
                settings.localLights.atlas=atlas;Run("hdr-atlas");
                surface.gi=new SceneGiInput { source=SceneGiSource.Lightmap,lightmap=giTexture };light.giWeight=1;Run("uv2-gi-local-response");
                var many=new SceneDecalLight[110];for(int i=0;i<many.Length;i++)many[i]=new SceneDecalLight { position=light.position+new Vector3(i%11*.05f,i/11*.03f,0),
                    range=6,radiance=light.radiance*.01f,specularScale=0 };
                settings.localLights.lights=many;settings.localLightBackend=SceneForwardLightBackend.BruteForce;Run("110-lights-brute");
                settings.localLightBackend=SceneForwardLightBackend.Tiled;Run("110-lights-grid");
                settings.localLights.lights=new[]{light};settings.localLights.atlas=null;surface.gi=null;light.giWeight=0;light.receiverGroup=0;
                foreach(var shape in new[]{SceneDecalLightShape.Point,SceneDecalLightShape.Spot,SceneDecalLightShape.Capsule,SceneDecalLightShape.Area})
                {
                    light.shape=shape;light.shadow.enabled=true;light.shadow.extendedSourceCoverage=true;light.shadow.extendedSamplesPerAxis=2;
                    shadowMaps=shape==SceneDecalLightShape.Point?6:shape==SceneDecalLightShape.Spot?1:shape==SceneDecalLightShape.Capsule?12:24;
                    casterHost.transform.position=new Vector3(0,0,1.5f);localVisibility=0;Run(shape+"-current-caster-blocks");
                    casterHost.transform.position=new Vector3(0,0,5);localVisibility=1;Run(shape+"-current-caster-behind");
                }
                light.shape=SceneDecalLightShape.Point;shadowMaps=6;casterHost.transform.position=new Vector3(0,0,1.5f);
                light.shadow.strength=.5f;localVisibility=.5f;Run("partial-shadow-strength");
                surface.bakedShadow=new SceneBakedShadowInput { source=SceneBakedShadowSource.Constant,visibility=new Vector4(128f/255,1,0,1) };
                light.bakedShadowChannel=SceneBakedShadowChannel.R;Run("min-baked-and-current-local");
                light.shadow.enabled=false;shadowMaps=0;Run("baked-local-without-shadow");
                light.bakedShadowChannel=SceneBakedShadowChannel.None;
                settings.localLights.lights=Array.Empty<SceneDecalLight>();settings.lightRadiance=new Vector3(1,2,.5f);settings.directionalSpecularScale=0;
                settings.mainLightShadow=new SceneDirectionalShadowSettings { enabled=true,origin=Vector3.zero,halfSize=new Vector2(4,4),farPlane=6,resolution=64,casters=new[]{caster} };
                mainVisibility=0;shadowMaps=1;Run("current-directional-blocks");
                casterHost.transform.position=new Vector3(0,0,5);mainVisibility=1;Run("current-directional-behind");
                settings.mainBakedShadowChannel=SceneBakedShadowChannel.R;Run("min-baked-and-current-main");
                settings.mainLightShadow.enabled=false;settings.mainBakedShadowChannel=SceneBakedShadowChannel.None;shadowMaps=0;settings.lightRadiance=Vector3.zero;
                settings.localLights.lights=new[]{light};settings.localLightBackend=SceneForwardLightBackend.BruteForce;
                var cutout=Own(new Texture2D(4,4,TextureFormat.RGBAFloat,false,true) { filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp });
                for(int y=0;y<4;y++)for(int x=0;x<4;x++)cutout.SetPixel(x,y,new Color(1,1,1,(x+y)%2));cutout.Apply();
                surface.inputs.albedoMap=cutout;surface.inputs.uvST=new Vector4(8,8,.0125f,.03125f);surface.alphaCutoff=.5f;
                surface.vertexScale=new Vector3(.75f,1.25f,1);Run("current-cutout-scaled-depth");
                surface.inputs.albedoMap=null;surface.alphaCutoff=0;surface.vertexScale=Vector3.one;
                camera.transform.SetPositionAndRotation(new Vector3(.12f,-.2f,.3f),Quaternion.Euler(3,-7,2));camera.orthographic=false;
                Run("moved-perspective-camera");
                var before=ReadSceneTarget(output);settings.positionLighting=false;
                Check("explicit-opt-in-required",!TileSceneRenderer.TryPrepare(camera,settings,out var rejected,out var rejection)&&rejected==null&&rejection.Contains("positionLighting"));
                Check("rejection-preserves-output",ScenePixelsEqual(before,ReadSceneTarget(output)));
                yield return null;
                foreach(var frame in frames){frame.Dispose();frame.Dispose();}
                Check("borrowed-output-retained",output.IsCreated()&&normal.IsCreated()&&mesh.vertexCount==4);
                Check("disposed-before-context",!frames[0].TryRecord(default,out _,out _));
            }
            finally
            {
                foreach(var frame in frames)frame.Dispose();GraphicsSettings.renderPipelineAsset=previousGraphics;QualitySettings.renderPipeline=previousQuality;
                Graphics.SetRenderTarget(previousActive);RenderTexture.active=previousActive;
                foreach(var item in _owned)if(item!=null)Destroy(item);_owned.Clear();
            }
            Check("restores-default-host",GraphicsSettings.renderPipelineAsset==previousGraphics&&QualitySettings.renderPipeline==previousQuality&&RenderTexture.active==previousActive);
        }

        private static float[] TilePositionHalfNeighbors(float value)
        {
            ushort bits=Mathf.FloatToHalf(value);float rounded=Mathf.HalfToFloat(bits);
            if(rounded==value)return new[]{rounded};
            int direction=(rounded<value?1:-1)*(value<0?-1:1);
            return new[]{rounded,Mathf.HalfToFloat((ushort)(bits+direction))};
        }

        private static Vector3 TilePositionCpuLocal(SceneDecalLight light,Vector3 world,Vector3 n,Vector3 view,int group,bool hasGi,Vector3 gi,Vector3 atlas)
        {
            if(!light.enabled||light.receiverGroup!=0&&light.receiverGroup!=group)return Vector3.zero;
            Vector3 delta=world-light.position,source=light.position;
            Vector3 ax=light.rotation*Vector3.right,ay=light.rotation*Vector3.up,az=light.rotation*Vector3.forward;
            float distance=delta.magnitude;
            if(light.shape==SceneDecalLightShape.Capsule)
            { source+=ax*Mathf.Clamp(Vector3.Dot(delta,ax),-light.halfLength,light.halfLength);distance=(world-source).magnitude; }
            if(light.shape==SceneDecalLightShape.Area)
            {
                float z=Vector3.Dot(delta,az);if(z<=0||z>=light.range)return Vector3.zero;
                float x=Vector3.Dot(delta,ax)/(light.halfSize.x+light.areaSpread.x*z),y=Vector3.Dot(delta,ay)/(light.halfSize.y+light.areaSpread.y*z);
                if(Mathf.Abs(x)>1||Mathf.Abs(y)>1)return Vector3.zero;
                source+=ax*x*light.halfSize.x+ay*y*light.halfSize.y;distance=z;
            }
            float attenuation=Mathf.Pow(Mathf.Clamp01(1-distance/light.range),light.falloffExponent);
            if(light.shape==SceneDecalLightShape.Spot)
            {
                if(distance<=1e-6f)return Vector3.zero;
                float cosine=Vector3.Dot(delta/distance,az),inner=Mathf.Cos(light.spotInnerAngle*Mathf.Deg2Rad*.5f),outer=Mathf.Cos(light.spotOuterAngle*Mathf.Deg2Rad*.5f);
                if(cosine<outer)return Vector3.zero;attenuation*=inner>outer?Mathf.Clamp01((cosine-outer)/(inner-outer)):1;
            }
            var response=new TileSceneRenderer.Settings { lightDirection=(source-world).normalized,lightRadiance=Vector3.Scale(light.radiance,atlas)*attenuation,
                ambientIrradiance=Vector3.zero,giBaseScale=0,directionalGiWeight=light.giWeight,directionalDiffuseScale=light.diffuseScale,
                directionalSpecularScale=light.specularScale,directionalBacklight=light.backlightScale };
            return TileSceneCpuLight(Vector3.one,new Vector3(0,1,0),n,view,response,gi,hasGi,1);
        }
    }
}
