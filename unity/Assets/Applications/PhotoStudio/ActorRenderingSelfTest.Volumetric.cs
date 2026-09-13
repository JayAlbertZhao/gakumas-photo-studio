using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyVolumetricLighting(Report report)
        {
            yield return null;
            var oldRenderers=FindObjectsOfType<Renderer>();var forced=new bool[oldRenderers.Length];
            for(int i=0;i<oldRenderers.Length;i++){forced[i]=oldRenderers[i].forceRenderingOff;oldRenderers[i].forceRenderingOff=true;}
            var renderer=new VolumetricLightingRenderer();var other=new VolumetricLightingRenderer();var saved=RenderTexture.active;var oldAnisotropy=QualitySettings.anisotropicFiltering;
            try
            {
                void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"volumetric-"+name,ok,error);
                RenderTexture Target(int w,int h,RenderTextureFormat format,int depthBits=0)
                {var t=Own(new RenderTexture(w,h,depthBits,format,RenderTextureReadWrite.Linear){name="Volumetric fixture "+w+"x"+h,filterMode=FilterMode.Point});t.Create();return t;}
                void Upload(RenderTexture target,Func<int,int,Color> pixel)
                {
                    var t=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point};var data=new Color[target.width*target.height];
                    for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)data[y*target.width+x]=pixel(x,y);
                    try{t.SetPixels(data);t.Apply();Graphics.Blit(t,target);}finally{Destroy(t);}
                }
                var host=Own(new GameObject("Volumetric actual camera"));var camera=host.AddComponent<Camera>();camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;
                camera.nearClipPlane=.3f;camera.farClipPlane=15;camera.fieldOfView=55;camera.aspect=43f/31;camera.orthographicSize=2.7f;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.1f,.2f,.3f,.37f);camera.cullingMask=1<<25;camera.renderingPath=RenderingPath.Forward;
                var source=Target(43,31,RenderTextureFormat.ARGBFloat);var depth=Target(43,31,RenderTextureFormat.RFloat);
                Upload(source,(x,y)=>new Color(.05f+x*.006f,.15f+y*.008f,.27f,.1f+x*.017f));Upload(depth,(x,y)=>new Color(x<8?2:0,0,0,0));
                var settings=new VolumetricLightingSettings();Check("default-off-zero-targets",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _)&&renderer.TargetCount==0&&renderer.DrawCalls==0);
                settings.enabled=true;settings.mediumCenter=new Vector3(0,0,6);settings.mediumHalfSize=new Vector3(4,3,4);settings.extinction=.13f;settings.samplesPerLight=256;
                var light=new VolumetricSpotLight{position=new Vector3(-1.2f,1.4f,2.7f),rotation=Quaternion.LookRotation(new Vector3(.2f,-.1f,1)),range=8,innerAngle=27,outerAngle=52,linearRadiance=new Vector3(9,4,2)};
                settings.lights=new[]{light};
                float lastError=0;
                VolumetricLightingRenderer.Frame Render(string name,bool oracle=true,float tolerance=.0002f,RenderTexture protection=null)
                {
                    var active=RenderTexture.active;
                    if(!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out var frame,protection))throw new InvalidOperationException(renderer.UnavailableReason);
                    Check(name+"-current-lease-budget",frame.IsCurrent&&renderer.TryGetFrame(out _)&&renderer.DrawCalls==renderer.LightCount+1&&RenderTexture.active==active);
                    var input=ReadSceneTarget(source);var output=ReadSceneTarget(frame.color);var scatter=ReadSceneTarget(frame.scattering);var z=ReadSceneTarget(depth);var mask=protection!=null?ReadSceneTarget(protection):null;float error=0,alpha=0;bool finite=true;
                    var shadowPixels=frame.shadowAtlas!=null?ReadSceneTarget(frame.shadowAtlas):null;int shadowSize=frame.shadowAtlas!=null?frame.shadowAtlas.width:0;
                    for(int y=0;y<source.height;y++)for(int x=0;x<source.width;x++)
                    {
                        int p=y*source.width+x;alpha=Mathf.Max(alpha,Mathf.Abs(input[p].a-output[p].a));for(int c=0;c<4;c++)finite&=!float.IsNaN(output[p][c])&&!float.IsInfinity(output[p][c]);
                        if(!oracle)continue;
                        var a=camera.ViewportToWorldPoint(new Vector3((x+.5f)/source.width,(y+.5f)/source.height,camera.nearClipPlane));
                        var b=camera.ViewportToWorldPoint(new Vector3((x+.5f)/source.width,(y+.5f)/source.height,z[p].r==0?camera.farClipPlane:Mathf.Min(z[p].r,camera.farClipPlane)));
                        bool invalid=float.IsNaN(z[p].r)||float.IsInfinity(z[p].r)||(z[p].r!=0&&z[p].r<camera.nearClipPlane)||(mask!=null&&mask[p].r>0);
                        var expected=invalid?new Color(0,0,0,1):VolumeDenseReference(settings,a,b,z[p].r==0,shadowPixels,shadowSize,2048);
                        for(int c=0;c<3;c++){error=Mathf.Max(error,Mathf.Abs(scatter[p][c]-expected[c]));error=Mathf.Max(error,Mathf.Abs(output[p][c]-(input[p][c]*expected.a+expected[c])));}
                    }
                    Check(name+"-finite-exact-current-alpha",finite&&alpha==0,alpha);
                    lastError=error;if(oracle)Check(name+"-whole-image-dense-world-integral",error<tolerance,error);
                    return frame;
                }
                var first=Render("finite-medium-single-spot");SaveSsrPreview("volumetric-single-spot",ReadSceneTarget(first.color),43,31,false);
                settings.samplesPerLight=8;Render("quality-8-measurement",true,float.PositiveInfinity);float low=lastError;
                settings.samplesPerLight=64;Render("quality-64-measurement",true,float.PositiveInfinity);float medium=lastError;
                settings.samplesPerLight=256;Render("quality-256-measurement");Check("unshadowed-cone-quality-converges",low>medium*2&&medium>lastError*2,lastError);
                foreach(float angle in new[]{4f,12,90})
                {light.outerAngle=angle;light.innerAngle=angle*.5f;var narrow=Render("cone-angle-"+angle);double energy=0;foreach(var pixel in ReadSceneTarget(narrow.scattering))energy+=pixel.r;Check("cone-angle-"+angle+"-nonvacuous",energy>1e-6,(float)energy);}
                light.innerAngle=light.outerAngle=52;Render("hard-cone-boundary",true,.0004f);light.innerAngle=27;
                foreach(float g in new[]{-.6f,0,.6f}){settings.anisotropy=g;Render("phase-g-"+g);}
                settings.anisotropy=0;
                settings.attenuateBackground=false;Render("additive-only");settings.attenuateBackground=true;
                settings.affectSky=false;Render("sky-protected");settings.affectSky=true;
                settings.extinction=0;var vacuum=Render("vacuum");Check("zero-extinction-exact",ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(vacuum.color)));settings.extinction=.13f;
                var mask=Target(43,31,RenderTextureFormat.R8);Upload(mask,(x,y)=>new Color(y<10?1:0,0,0,0));Render("explicit-pixel-protection",true,.0002f,mask);
                Upload(depth,(x,y)=>new Color(x<6?-1:x<12?.1f:x<18?float.NaN:x<24?float.PositiveInfinity:7,0,0,0));Render("invalid-depth-passthrough");
                Upload(depth,(x,y)=>new Color(x<8?2:0,0,0,0));
                settings.mediumCenter=new Vector3(40,0,6);var outside=Render("medium-outside-frustum");Check("outside-medium-exact",ScenePixelsEqual(ReadSceneTarget(source),ReadSceneTarget(outside.color)));settings.mediumCenter=new Vector3(0,0,6);
                foreach(int projection in new[]{1,2})
                {
                    camera.orthographic=projection==1;camera.ResetProjectionMatrix();if(projection==2){var p=camera.projectionMatrix;p.m02=.2f;p.m12=-.27f;camera.projectionMatrix=p;}
                    Render("projection-"+projection);
                }
                camera.orthographic=false;camera.ResetProjectionMatrix();camera.transform.position=new Vector3(0,0,4);Render("camera-inside-medium-cone");camera.transform.position=Vector3.zero;
                var second=new VolumetricSpotLight{position=new Vector3(1.3f,-.8f,3),rotation=Quaternion.LookRotation(new Vector3(-.3f,.2f,1)),range=8,innerAngle=32,outerAngle=60,linearRadiance=new Vector3(1,4,8)};
                settings.lights=new[]{light,second};Render("two-colored-overlapping-lights");settings.lights=new[]{light};
                // Real registered caster geometry updates every render, without scene discovery.
                var blocker=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));blocker.name="Volumetric moving shadow caster";blocker.layer=25;
                blocker.transform.SetPositionAndRotation(light.position+light.rotation*new Vector3(.08f,.12f,2),light.rotation);blocker.transform.localScale=new Vector3(1.1f,1.25f,1);
                blocker.GetComponent<Renderer>().sharedMaterial=Own(new Material(Resources.Load<Shader>("StudioAccent")));
                light.shadow.enabled=true;light.shadow.depthBias=0;light.shadow.normalBias=0;settings.shadows.tileResolution=256;
                settings.shadows.casters=new[]{new SceneShadowCaster{renderer=blocker.GetComponent<Renderer>(),cull=CullMode.Off}};
                var shadowed=Render("actual-raster-depth-shadow",true,.002f);Check("actual-one-map-one-caster",renderer.ShadowMapCount==1&&renderer.ShadowCasterDrawCalls==1&&renderer.TargetCount==3);
                var dark=ReadSceneTarget(shadowed.scattering);light.shadow.enabled=false;var unshadowed=Render("shadow-disabled");var bright=ReadSceneTarget(unshadowed.scattering);double reduction=0;
                for(int i=0;i<dark.Length;i++)reduction+=bright[i].r-dark[i].r;
                Check("shadow-nonvacuous-integrated-beam-loss",reduction>.1,(float)reduction);light.shadow.enabled=true;
                blocker.transform.position+=light.rotation*new Vector3(1.2f,0,0);var moved=Render("moving-caster",true,.002f);var shifted=ReadSceneTarget(moved.scattering);double change=0;
                for(int i=0;i<dark.Length;i++)change+=Math.Abs(dark[i].r-shifted[i].r);
                Check("moving-caster-changes-volume-not-only-surface",change>.1,(float)change);SaveSsrPreview("volumetric-moving-caster",ReadSceneTarget(moved.color),43,31,false);
                light.shadow.filter=SceneShadowFilter.Pcf3x3;Render("pcf-nine-tap-shadow",true,.002f);light.shadow.filter=SceneShadowFilter.Hard;
                var caster=settings.shadows.casters[0];var originalCaster=caster.renderer;
                var skinHost=Own(new GameObject("Volumetric skinned occluder"));skinHost.layer=25;
                skinHost.transform.SetPositionAndRotation(light.position+light.rotation*new Vector3(.08f,.12f,2),light.rotation);skinHost.transform.localScale=Vector3.one*1.2f;
                var bone=Own(new GameObject("Volumetric occluder bone")).transform;bone.SetParent(skinHost.transform,false);
                var mesh=blocker.GetComponent<MeshFilter>().sharedMesh;var skinMesh=Own(Instantiate(mesh));var weights=new BoneWeight[mesh.vertexCount];
                for(int i=0;i<weights.Length;i++)weights[i]=new BoneWeight{boneIndex0=0,weight0=1};skinMesh.boneWeights=weights;skinMesh.bindposes=new[]{Matrix4x4.identity};
                var skin=skinHost.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=skinMesh;skin.bones=new[]{bone};skin.rootBone=bone;skin.sharedMaterial=originalCaster.sharedMaterial;skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*5);
                var control=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));control.layer=25;control.transform.SetPositionAndRotation(skinHost.transform.position,skinHost.transform.rotation);control.transform.localScale=skinHost.transform.localScale;
                var controlMesh=Own(Instantiate(mesh));control.GetComponent<MeshFilter>().sharedMesh=controlMesh;
                Color[] firstSkin=null;
                for(int step=0;step<2;step++)
                {
                    bone.localPosition=new Vector3(step*.65f,-step*.2f,0);bone.localRotation=Quaternion.Euler(0,0,step*25);caster.renderer=skin;yield return null;
                    var skinnedFrame=Render("bone-deformed-occluder-"+step,true,.002f);var skinPixels=ReadSceneTarget(skinnedFrame.scattering);var skinDepth=ReadSceneTarget(skinnedFrame.shadowAtlas);
                    if(step==0)firstSkin=skinPixels;else{double delta=0;for(int i=0;i<skinPixels.Length;i++)delta+=Math.Abs(skinPixels[i].r-firstSkin[i].r);Check("bone-motion-changes-integrated-light",delta>.1,(float)delta);}
                    var vertices=mesh.vertices;var deformation=Matrix4x4.TRS(bone.localPosition,bone.localRotation,bone.localScale);
                    for(int i=0;i<vertices.Length;i++)vertices[i]=deformation.MultiplyPoint(vertices[i]);controlMesh.vertices=vertices;controlMesh.RecalculateBounds();caster.renderer=control.GetComponent<Renderer>();
                    var staticFrame=Render("cpu-deformed-control-"+step,true,.002f);var staticPixels=ReadSceneTarget(staticFrame.scattering);var staticDepth=ReadSceneTarget(staticFrame.shadowAtlas);float error=0;
                    for(int i=0;i<skinDepth.Length;i++)error=Mathf.Max(error,Mathf.Abs(skinDepth[i].r-staticDepth[i].r));
                    for(int i=0;i<skinPixels.Length;i++)for(int c=0;c<3;c++)error=Mathf.Max(error,Mathf.Abs(skinPixels[i][c]-staticPixels[i][c]));
                    Check("skinned-atlas-and-volume-equal-cpu-deformation-"+step,error<.00002f,error);
                }
                caster.renderer=originalCaster;skinHost.SetActive(false);control.SetActive(false);
                var alphaMap=Own(new Texture2D(2,1,TextureFormat.RGBAFloat,false,true));alphaMap.filterMode=FilterMode.Point;alphaMap.SetPixels(new[]{new Color(1,1,1,0),Color.white});alphaMap.Apply();
                var solidPixels=ReadSceneTarget(Render("solid-before-cutout",false).scattering);caster.alphaMap=alphaMap;caster.cutoff=.5f;
                var cutPixels=ReadSceneTarget(Render("alpha-cutout-occluder",true,.002f).scattering);double opened=0;
                for(int i=0;i<cutPixels.Length;i++)opened+=cutPixels[i].r-solidPixels[i].r;Check("alpha-hole-opens-volume",opened>.01,(float)opened);caster.alphaMap=null;caster.cutoff=0;
                var samplerBaseline=ReadSceneTarget(Render("sampler-baseline",false).color);source.filterMode=depth.filterMode=FilterMode.Trilinear;source.anisoLevel=depth.anisoLevel=16;source.wrapMode=depth.wrapMode=TextureWrapMode.Repeat;QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;
                Check("input-samplers-and-forced-anisotropy-do-not-change-output",ScenePixelsEqual(samplerBaseline,ReadSceneTarget(Render("forced-anisotropy",false).color)));
                source.filterMode=depth.filterMode=FilterMode.Point;source.anisoLevel=depth.anisoLevel=0;source.wrapMode=depth.wrapMode=TextureWrapMode.Clamp;QualitySettings.anisotropicFiltering=oldAnisotropy;
                // Independently render each shadowed light, then compare their sum with a
                // three-tile atlas (including a second row), without assuming identical maps.
                var third=new VolumetricSpotLight{position=new Vector3(-.4f,.3f,2.4f),rotation=Quaternion.LookRotation(new Vector3(.1f,.1f,1)),range=9,innerAngle=25,outerAngle=57,linearRadiance=new Vector3(3,7,1)};
                second.shadow.enabled=third.shadow.enabled=true;var three=new[]{light,second,third};var sum=new Color[source.width*source.height];
                foreach(var item in three){settings.lights=new[]{item};var separate=ReadSceneTarget(Render("separate-shadowed-light-"+item.linearRadiance,false).scattering);for(int i=0;i<sum.Length;i++)sum[i]+=separate[i];}
                settings.lights=three;var combined=ReadSceneTarget(Render("three-shadow-atlas-tiles",false).scattering);float sumError=0;
                for(int i=0;i<sum.Length;i++)for(int c=0;c<3;c++)sumError=Mathf.Max(sumError,Mathf.Abs(sum[i][c]-combined[i][c]));
                Check("three-shadow-maps-equal-independent-light-sum",renderer.ShadowMapCount==3&&renderer.ShadowCasterDrawCalls==3&&sumError<.00002f,sumError);settings.lights=new[]{light};
                var lease=Render("before-loss",false);other.TryRender(source,new FogVolumeDepth(depth),camera,settings,out var otherFrame);
                lease.shadowAtlas.Release();Check("lost-shadow-invalidates-frame",!lease.IsCurrent&&!renderer.TryGetFrame(out _));var rebuilt=Render("recreated-shadow",false);
                Check("alias-rejected-and-invalidates",!renderer.TryRender(rebuilt.color,new FogVolumeDepth(depth),camera,settings,out _)&&!rebuilt.IsCurrent);
                Check("separate-renderer-survives",otherFrame.IsCurrent);Render("recovered-alias",false);
                source=Target(47,29,RenderTextureFormat.ARGBHalf);depth=Target(47,29,RenderTextureFormat.RHalf);camera.aspect=47f/29;
                Upload(source,(x,y)=>new Color(.3f,2,.8f,.375f));Upload(depth,(x,y)=>new Color(9,0,0,0));Render("resize-half-hdr-depth",true,.002f);
                var wrong=Target(13,11,RenderTextureFormat.RFloat);Check("wrong-depth-size-rejected",!renderer.TryRender(source,new FogVolumeDepth(wrong),camera,settings,out _));
                settings.samplesPerLight=7;Check("undersampled-request-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _));settings.samplesPerLight=256;
                settings.mediumHalfSize.x=float.NaN;Check("invalid-medium-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _)&&renderer.TargetCount==0);settings.mediumHalfSize.x=4;
                light.outerAngle=0;Check("invalid-cone-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _));light.outerAngle=52;
                settings.enabled=false;Check("disabled-releases",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _)&&renderer.TargetCount==0);settings.enabled=true;
                var final=Render("before-dispose",false);renderer.Dispose();Check("dispose-releases-and-invalidates",!final.IsCurrent&&!final.color.IsCreated()&&renderer.TargetCount==0);

                // Actual geometry depth and the production image-effects bridge.
                const int width=97,height=65;camera.aspect=width/(float)height;camera.depthTextureMode=DepthTextureMode.Depth;
                var target=Target(width,height,RenderTextureFormat.ARGBFloat,24);camera.targetTexture=target;var nativeDepth=Target(width,height,RenderTextureFormat.RFloat);
                blocker.GetComponent<Renderer>().sharedMaterial=Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback")));
                var wall=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));wall.layer=25;wall.transform.position=new Vector3(.35f,-.4f,3.2f);wall.transform.localScale=new Vector3(.8f,1.1f,1);wall.GetComponent<Renderer>().sharedMaterial=blocker.GetComponent<Renderer>().sharedMaterial;
                var post=host.AddComponent<OriginalStyleRenderPipeline>();post.volumetricLighting=settings;Color[] actualInput=null;
                post.volumetricDepthProvider=(view,input)=>{actualInput=ReadSceneTarget(input);Graphics.Blit(Shader.GetGlobalTexture("_CameraDepthTexture"),nativeDepth);return new FogVolumeDepth(nativeDepth,FogDepthEncoding.Device);};
                bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_VOLUMETRIC")=="1",started=false,ended=false;
                try
                {
                    if(capture)started=RenderDocCaptureBridge.BeginOffscreenCapture();
                    for(int step=0;step<2;step++)
                    {
                        camera.orthographic=step==1;camera.ResetProjectionMatrix();
                        if(step==0){var p=camera.projectionMatrix;p.m02=.19f;p.m12=-.23f;camera.projectionMatrix=p;}
                        blocker.transform.position+=light.rotation*new Vector3(-.4f,.1f,0);camera.Render();
                        if(!post.TryGetVolumetricLightingFrame(out var actual))throw new InvalidOperationException("Actual volumetric post frame missing: "+post.VolumetricLightingUnavailableReason);
                        var raw=ReadSceneTarget(nativeDepth);var result=ReadSceneTarget(actual.color);var atlas=ReadSceneTarget(actual.shadowAtlas);float maximum=0,alpha=0;int surfaces=0,skyPixels=0;
                        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                        {
                            int i=y*width+x;float z=raw[i].r;bool sky=z==0;if(sky)skyPixels++;else surfaces++;
                            float eye=sky?camera.farClipPlane:camera.orthographic?camera.farClipPlane-z*(camera.farClipPlane-camera.nearClipPlane):camera.nearClipPlane*camera.farClipPlane/(camera.nearClipPlane+z*(camera.farClipPlane-camera.nearClipPlane));
                            var near=camera.ViewportToWorldPoint(new Vector3((x+.5f)/width,(y+.5f)/height,camera.nearClipPlane));var end=camera.ViewportToWorldPoint(new Vector3((x+.5f)/width,(y+.5f)/height,eye));
                            var expected=VolumeDenseReference(settings,near,end,sky,atlas,actual.shadowAtlas.width,2048);
                            for(int c=0;c<3;c++)maximum=Mathf.Max(maximum,Mathf.Abs(result[i][c]-(actualInput[i][c]*expected.a+expected[c])));
                            alpha=Mathf.Max(alpha,Mathf.Abs(result[i].a-actualInput[i].a));
                        }
                        Check("actual-camera-current-depth-post-"+step,maximum<.002f&&alpha==0&&surfaces>20&&skyPixels>20,maximum);
                        SaveSsrPreview("volumetric-actual-camera-"+step,ReadSceneTarget(target),width,height,false);
                        SaveSsrPreview("volumetric-actual-scattering-"+step,ReadSceneTarget(actual.scattering),width,height,false);
                    }
                }
                finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                if(capture)Check("requested-native-capture",started&&ended);
                post.volumetricDepthProvider=null;camera.Render();Check("missing-provider-no-stale-frame",!post.TryGetVolumetricLightingFrame(out _)&&post.VolumetricLightingUnavailableReason!=null);
                post.volumetricDepthProvider=(view,input)=>throw new InvalidOperationException("Deliberate volume depth provider failure");camera.Render();Check("provider-exception-current-passthrough",!post.TryGetVolumetricLightingFrame(out _)&&post.VolumetricLightingUnavailableReason!=null);
                settings.enabled=false;camera.Render();var legacy=ReadSceneTarget(target);settings.enabled=true;camera.Render();settings.enabled=false;camera.Render();
                Check("production-default-exact-restored",ScenePixelsEqual(legacy,ReadSceneTarget(target))&&post.VolumetricLightingUnavailableReason==null);post.enabled=false;camera.targetTexture=null;host.SetActive(false);
            }
            finally
            {
                renderer.Dispose();other.Dispose();QualitySettings.anisotropicFiltering=oldAnisotropy;RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;
                for(int i=0;i<oldRenderers.Length;i++)if(oldRenderers[i]!=null)oldRenderers[i].forceRenderingOff=forced[i];foreach(var obj in _owned)if(obj!=null)Destroy(obj);_owned.Clear();
            }
        }

        private static bool VolumeReferenceBox(VolumetricLightingSettings settings,double[] p,double[] d,double length,out double start,out double end)
        {
            start=0;end=length;
            for(int i=0;i<3;i++)
            {
                double local=p[i]-settings.mediumCenter[i],half=settings.mediumHalfSize[i];
                if(Math.Abs(d[i])<1e-12){if(Math.Abs(local)>half)return false;continue;}
                double x=(-half-local)/d[i],y=(half-local)/d[i];start=Math.Max(start,Math.Min(x,y));end=Math.Min(end,Math.Max(x,y));
            }
            return end>start;
        }
        private static Color VolumeDenseReference(VolumetricLightingSettings settings,Vector3 from,Vector3 to,bool sky,Color[] shadow,int shadowSize,int steps)
        {
            if(sky&&!settings.affectSky)return new Color(0,0,0,1);
            var p=new[]{(double)from.x,from.y,from.z};var d=new[]{(double)to.x-from.x,(double)to.y-from.y,(double)to.z-from.z};double length=Math.Sqrt(d[0]*d[0]+d[1]*d[1]+d[2]*d[2]);
            if(length==0)return new Color(0,0,0,1);for(int i=0;i<3;i++)d[i]/=length;
            if(!VolumeReferenceBox(settings,p,d,length,out double enter,out double leave))return new Color(0,0,0,1);
            double ds=(leave-enter)/steps,sigma=settings.extinction;var result=new double[3];
            var world=new double[3];var incoming=new double[3];var towardsLight=new double[3];
            for(int j=0;j<steps;j++)
            {
                double t=enter+(j+.5)*ds;for(int i=0;i<3;i++)world[i]=p[i]+d[i]*t;
                foreach(var light in settings.lights)
                {
                    if(light==null||!light.enabled)continue;double r2=0;
                    for(int i=0;i<3;i++){incoming[i]=world[i]-light.position[i];r2+=incoming[i]*incoming[i];}
                    double r=Math.Sqrt(r2);if(r<1e-8||r>=light.range)continue;
                    var forward=light.rotation.normalized*Vector3.forward;double cosine=0,mu=0;
                    for(int i=0;i<3;i++){incoming[i]/=r;towardsLight[i]=-incoming[i];cosine+=incoming[i]*forward[i];mu-=incoming[i]*d[i];}
                    double outer=Math.Cos(light.outerAngle*Math.PI/360),inner=Math.Cos(light.innerAngle*Math.PI/360);if(cosine<outer)continue;
                    double angular=inner>outer?Math.Min(1,Math.Max(0,(cosine-outer)/(inner-outer))):1;
                    double lightPath=VolumeReferenceBox(settings,world,towardsLight,r,out double a,out double b)?b-a:0;
                    double g=settings.anisotropy,phase=(1-g*g)/(4*Math.PI*Math.Pow(1+g*g-2*g*mu,1.5)),visibility=1;
                    if(shadow!=null&&light.shadow!=null&&light.shadow.enabled&&light.shadow.strength>0)
                    {
                        // This fixture currently has one actual Spot tile, sampled independently
                        // from its readback. Multi-light atlas routing has separate source tests.
                        var local=Quaternion.Inverse(light.rotation.normalized)*(new Vector3((float)world[0],(float)world[1],(float)world[2])-light.position);
                        if(local.z>=light.shadow.nearPlane&&local.z<=light.range)
                        {
                            double half=Math.Tan(light.outerAngle*Math.PI/360)*local.z,u=.5+local.x/(2*half),v=.5+local.y/(2*half);
                            if(u>=0&&u<=1&&v>=0&&v<=1)
                            {
                                double receiver=(local.z-light.shadow.depthBias)/light.range;int taps=light.shadow.filter==SceneShadowFilter.Hard?0:1;double total=0;
                                for(int y=-taps;y<=taps;y++)for(int x=-taps;x<=taps;x++)
                                {int ix=Math.Max(0,Math.Min(shadowSize-1,(int)Math.Floor(u*shadowSize+x))),iy=Math.Max(0,Math.Min(shadowSize-1,(int)Math.Floor(v*shadowSize+y)));total+=receiver<=shadow[iy*shadowSize+ix].r?1:0;}
                                visibility=1-light.shadow.strength+light.shadow.strength*total/((2*taps+1)*(2*taps+1));
                            }
                        }
                    }
                    double weight=Math.Exp(-sigma*(t-enter+lightPath))*sigma*ds*phase*Math.Pow(Math.Max(0,1-r/light.range),light.falloffExponent)*angular*visibility;
                    for(int c=0;c<3;c++)result[c]+=weight*light.linearRadiance[c]*settings.scatteringAlbedo[c];
                }
            }
            return new Color((float)result[0],(float)result[1],(float)result[2],settings.attenuateBackground?(float)Math.Exp(-sigma*(leave-enter)):1);
        }
    }
}
