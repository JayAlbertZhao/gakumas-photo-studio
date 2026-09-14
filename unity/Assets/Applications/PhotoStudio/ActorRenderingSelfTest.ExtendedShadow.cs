using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyExtendedSourceShadows(Report report)
        {
            yield return null;
            void Check(string n,bool ok,float e=0)=>FrameworkCheck(report,"extended-source-shadow-"+n,ok,e);
            var previous=FindObjectsOfType<Renderer>();var forced=previous.Select(r=>r.forceRenderingOff).ToArray();
            foreach(var r in previous)r.forceRenderingOff=true;var saved=RenderTexture.active;
            try
            {
                const int size=65;
                var host=Own(new GameObject("Independent extended-source visibility fixture"));var camera=host.AddComponent<Camera>();
                camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;camera.cullingMask=0;
                camera.orthographic=true;camera.orthographicSize=1.7f;camera.aspect=1;camera.transform.position=new Vector3(0,0,-4);
                camera.nearClipPlane=.1f;camera.farClipPlane=20;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                var target=Own(new RenderTexture(size,size,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                var stage=host.AddComponent<SceneDeferredCamera>();stage.sceneEnabled=true;stage.sceneLayers=1<<25;
                stage.lightRadiance=stage.ambientIrradiance=Vector3.zero;stage.giBaseScale=0;
                var receiver=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));receiver.layer=25;receiver.transform.localScale=new Vector3(3.8f,3.8f,1);
                var receiverMesh=Own(Instantiate(receiver.GetComponent<MeshFilter>().sharedMesh));receiverMesh.uv2=receiverMesh.uv;receiver.GetComponent<MeshFilter>().sharedMesh=receiverMesh;
                var surface=new SceneDeferredCamera.Surface{renderer=receiver.GetComponent<Renderer>(),cull=CullMode.Off};
                surface.inputs.albedo=new Vector3(.5f,.375f,.25f);surface.inputs.mos=new Vector3(0,.75f,.25f);surface.inputs.emission=new Vector3(.03125f,.0625f,.015625f);stage.surfaces=new[]{surface};
                var occluder=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));occluder.layer=24;
                occluder.transform.SetPositionAndRotation(new Vector3(.09f,.17f,-.9f),Quaternion.Euler(11,7,13));occluder.transform.localScale=new Vector3(.55f,.65f,1);
                var caster=new SceneShadowCaster{renderer=occluder.GetComponent<Renderer>(),cull=CullMode.Off};
                var borrowedMaterial=caster.renderer.sharedMaterial;var borrowedMesh=occluder.GetComponent<MeshFilter>().sharedMesh;
                var light=new SceneDecalLight{shape=SceneDecalLightShape.Capsule,position=new Vector3(.17f,-.13f,-2),range=5,
                    halfLength=1.1f,halfSize=new Vector2(1.1f,.9f),areaSpread=new Vector2(.35f,.25f),radiance=new Vector3(2,3,1),specularScale=0};
                var options=stage.decalLighting;options.enabled=true;options.backend=SceneDecalLightBackend.Instanced;options.allowInstancingFallback=false;
                options.lights=new[]{light};options.shadows.casters=new[]{caster};options.shadows.tileResolution=32;
                var forward=host.AddComponent<SceneForwardLightingCamera>();forward.surfaceLayers=1<<25;
                var forwardSurface=new SceneForwardSurface{renderer=surface.renderer,cull=CullMode.Off,inputs=surface.inputs};forward.settings.surfaces=new[]{forwardSurface};
                forward.settings.lightRadiance=forward.settings.ambientIrradiance=Vector3.zero;forward.settings.giBaseScale=0;forward.settings.localLights=options;
                SceneDeferredCamera.Frame Render(){camera.Render();if(!stage.TryGetFrame(out var f))throw new InvalidOperationException(stage.UnavailableReason);return f;}
                Color[] Pixels()=>ReadSceneTarget(target);
                void RawPair(string name,Color[] actual,Color[] expected)
                {
                    using(var writer=new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(_directory,"extended-source-shadow-"+name+".raw"))))
                        for(int i=0;i<actual.Length;i++)for(int c=0;c<4;c++){writer.Write(actual[i][c]);writer.Write(expected[i][c]);}
                }
                Vector3 Rgb(Color c)=>new Vector3(c.r,c.g,c.b);
                var baseline=Render();Check("default-no-source-allocation",baseline.lightShadowAtlas==null&&stage.LightShadowMapCount==0);
                light.shadow.enabled=true;light.shadow.extendedSourceCoverage=true;
                // Independently enumerate the regular equal-measure source lattice.
                Vector3[] Sources()
                {
                    int n=light.shadow.extendedSamplesPerAxis,rows=light.shape==SceneDecalLightShape.Area?n:1;
                    return Enumerable.Range(0,n*rows).Select(i=>light.position+light.rotation.normalized*new Vector3(
                        ((i%n+.5f)/n*2-1)*(light.shape==SceneDecalLightShape.Area?light.halfSize.x:light.halfLength),
                        light.shape==SceneDecalLightShape.Area?((i/n+.5f)/rows*2-1)*light.halfSize.y:0,0)).ToArray();
                }
                float Radius()
                {
                    if(light.shape==SceneDecalLightShape.Capsule)return light.range+2*light.halfLength;
                    double x=2*light.halfSize.x+light.range*light.areaSpread.x,y=2*light.halfSize.y+light.range*light.areaSpread.y,z=light.range;
                    return (float)Math.Sqrt(x*x+y*y+z*z);
                }
                var axes=new[]{Vector3.right,Vector3.left,Vector3.up,Vector3.down,Vector3.forward,Vector3.back};
                var ups=new[]{Vector3.up,Vector3.up,Vector3.forward,Vector3.forward,Vector3.up,Vector3.up};var rights=Enumerable.Range(0,6).Select(i=>Vector3.Cross(ups[i],axes[i])).ToArray();
                int Face(Vector3 d)
                {
                    float maximum=axes.Max(a=>Vector3.Dot(a,d));for(int i=0;i<6;i++)if(Vector3.Dot(axes[i],d)>=maximum*(1-1e-5f))return i;return 0;
                }
                float Visibility(Vector3 world,Color[] atlas,int width,Vector3[] sources)
                {
                    int tile=options.shadows.tileResolution,grid=width/tile;float sum=0,far=Radius();
                    for(int s=0;s<sources.Length;s++)
                    {
                        var d=world-sources[s];float radial=d.magnitude;
                        if(radial<light.shadow.nearPlane||radial>far){sum++;continue;}
                        float receiverDepth=(radial-light.shadow.depthBias)/far;
                        float Tap(Vector3 direction)
                        {
                            int face=Face(direction),map=s*6+face;float axial=Vector3.Dot(direction,axes[face]);
                            int Texel(float coordinate)
                            {
                                float edge=Mathf.Round(coordinate);
                                if(Mathf.Abs(coordinate-edge)<=tile/1048576f)coordinate=edge;
                                return Mathf.Clamp(Mathf.FloorToInt(coordinate),0,tile-1);
                            }
                            int x=Texel((Vector3.Dot(direction,rights[face])/axial+1)*.5f*tile);
                            int y=Texel((Vector3.Dot(direction,ups[face])/axial+1)*.5f*tile);
                            return receiverDepth<=atlas[(map/grid*tile+y)*width+map%grid*tile+x].r?1:0;
                        }
                        if(light.shadow.filter==SceneShadowFilter.Hard)sum+=Tap(d);
                        else
                        {
                            int face=Face(d);float axial=Vector3.Dot(d,axes[face]),u=Vector3.Dot(d,rights[face])/axial,v=Vector3.Dot(d,ups[face])/axial;
                            for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++)sum+=Tap(axes[face]+rights[face]*(u+2f*x/tile)+ups[face]*(v+2f*y/tile))/9;
                        }
                    }
                    return Mathf.Lerp(1,sum/sources.Length,light.shadow.strength);
                }
                void DepthOracle(string name,SceneDeferredCamera.Frame frame)
                {
                    var depths=ReadSceneTarget(frame.lightShadowAtlas);var reference=Enumerable.Repeat(Color.white,depths.Length).ToArray();var sources=Sources();int tile=options.shadows.tileResolution,width=frame.lightShadowAtlas.width,grid=width/tile;
                    float far=Radius(),worst=0;int occupied=0,empty=0;
                    bool active=caster.renderer.enabled&&!caster.renderer.forceRenderingOff&&caster.renderer.gameObject.activeInHierarchy;
                    for(int s=0;s<sources.Length;s++)for(int face=0;face<6;face++)
                    {
                        var raster=ExtendedRasterDepth(borrowedMesh,caster,sources[s],axes[face],rights[face],ups[face],tile,light.shadow.nearPlane,far,active);
                        for(int y=0;y<tile;y++)for(int x=0;x<tile;x++)
                        {
                            float expected=raster[y*tile+x];
                            int m=s*6+face,index=(m/grid*tile+y)*width+m%grid*tile+x;float actual=depths[index].r,difference=Mathf.Abs(actual-expected);
                            worst=Mathf.Max(worst,float.IsNaN(difference)||float.IsInfinity(difference)?float.MaxValue:difference);reference[index]=new Color(expected,0,0,1);
                            if(expected<1)occupied++;else empty++;
                        }
                    }
                    bool clear=true;for(int m=sources.Length*6;m<grid*grid;m++)for(int y=0;y<tile;y++)for(int x=0;x<tile;x++)clear&=depths[(m/grid*tile+y)*width+m%grid*tile+x].r==1;
                    Check(name+"-whole-native-source-depth",worst<=.00002f&&empty>1000,worst);Check(name+"-unused-atlas-tiles-clear",clear);
                    if(worst>.00002f){RawPair(name+"-depth-failure",depths,reference);Debug.Log("[ExtendedShadowDiagnostic] "+name+" depth error="+worst+" atlas="+width+" samples="+sources.Length);}
                    Check(name+"-actual-source-draws",stage.LightShadowMapCount==sources.Length*6&&stage.LightShadowCasterDrawCalls==(active?sources.Length*6:0),occupied);
                }
                int cases=0;
                Color[] Case(string name,bool depthOracle=true,bool compareForward=true)
                {
                    light.shadow.enabled=false;Render();var lit=Pixels();var radiance=light.radiance;light.radiance=Vector3.zero;Render();var basis=Pixels();light.radiance=radiance;light.shadow.enabled=true;
                    bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_EXTENDED_SHADOW")=="1"&&
                        (name=="Capsule-midpoints-4"||name=="Area-midpoints-2"||name=="scalar-current-source");
                    void Captured(string suffix,Action draw,bool enabled)
                    {
                        if(!enabled){draw();return;}
                        target.name="Extended source final "+name+"-"+suffix;FsrCaptureDrain(target);
                        bool began=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                        try{draw();FsrCaptureDrain(target);}finally{if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                        Check(name+"-native-"+suffix,began&&ended);var pixels=Pixels();RawPair(name+"-native-final-"+suffix,pixels,pixels);
                    }
                    SceneDeferredCamera.Frame frame=default;Captured("deferred",()=>frame=Render(),capture);
                    var actual=Pixels();var normals=ReadSceneTarget(frame.normalGroup);var geometry=ReadSceneTarget(frame.mosDepth);var coverage=ReadSceneTarget(frame.albedoCoverage);
                    var atlas=ReadSceneTarget(frame.lightShadowAtlas);var sources=Sources();var expectedPixels=new Color[actual.Length];var multipliedPixels=new Color[actual.Length];float worst=0;int partial=0,blocked=0,mismatches=0,worstIndex=0;
                    bool hasBaked=surface.bakedShadow!=null&&surface.bakedShadow.source==SceneBakedShadowSource.Constant&&light.bakedShadowChannel!=SceneBakedShadowChannel.None;
                    float bakedVisibility=1;
                    if(hasBaked)
                    {
                        int channel=(int)light.bakedShadowChannel-1,levels=channel==0?255:channel==3?3:7;
                        bakedVisibility=Mathf.Floor(surface.bakedShadow.visibility[channel]*levels+.5f)/levels;
                    }
                    if(capture)RawPair(name+"-native-atlas",atlas,atlas);
                    for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                    {
                        int i=y*size+x;float visibility=1;
                        if(coverage[i].a>.5f)
                        {
                            var world=camera.ViewportToWorldPoint(new Vector3((x+.5f)/size,(y+.5f)/size,geometry[i].a));var n=Rgb(normals[i]).normalized;
                            if(surface.leaf!=null&&surface.leaf.enabled)
                            {
                                var delta=world-light.position;var rotation=light.rotation.normalized;
                                var direction=light.position+rotation*Vector3.right*Mathf.Clamp(Vector3.Dot(delta,rotation*Vector3.right),-light.halfLength,light.halfLength)-world;
                                if(Vector3.Dot(n,direction)<0)n=-n;
                            }
                            visibility=Visibility(world+n*light.shadow.normalBias,atlas,frame.lightShadowAtlas.width,sources);
                            if(visibility<.999f)blocked++;if(visibility>.001f&&visibility<.999f)partial++;
                        }
                        // lit already contains the baked factor. The published mixed
                        // contract takes MIN, not the product of two occlusion masks.
                        multipliedPixels[i]=basis[i]+(lit[i]-basis[i])*visibility;
                        float factor=hasBaked?(bakedVisibility>0?Mathf.Min(bakedVisibility,visibility)/bakedVisibility:0):visibility;
                        var expected=basis[i]+(lit[i]-basis[i])*factor;
                        expectedPixels[i]=expected;float error=0;
                        for(int c=0;c<4;c++){float difference=Mathf.Abs(expected[c]-actual[i][c]);error=Mathf.Max(error,float.IsNaN(difference)||float.IsInfinity(difference)?float.MaxValue:difference);}
                        if(error>worst){worst=error;worstIndex=i;}if(error>.0003f)mismatches++;
                    }
                    Check(name+"-whole-independent-visibility",worst<=.0003f,worst);
                    if(hasBaked)Check(name+"-min-not-product",ExtendedPixelError(actual,multipliedPixels)>.005f,ExtendedPixelError(actual,multipliedPixels));
                    if(worst>.0003f)
                    {
                        RawPair(name+"-visibility-failure",actual,expectedPixels);RawPair(name+"-actual-atlas",atlas,atlas);
                        RawPair(name+"-lit-basis",lit,basis);
                        Debug.Log("[ExtendedShadowDiagnostic] "+name+" visibility error="+worst+" pixels="+mismatches+" worst="+(worstIndex%size)+","+(worstIndex/size)+" actual="+actual[worstIndex]+" expected="+expectedPixels[worstIndex]);
                    }
                    Check(name+"-current-frame",frame.IsCurrent&&stage.SubmittedLights==1);
                    if(depthOracle)DepthOracle(name,frame);
                    if(compareForward)
                    {
                        forwardSurface.gi=surface.gi;forwardSurface.bakedShadow=surface.bakedShadow;forwardSurface.leaf=surface.leaf;
                        stage.sceneEnabled=false;forward.settings.enabled=true;
                        foreach(var backend in new[]{SceneForwardLightBackend.BruteForce,SceneForwardLightBackend.Tiled})
                        {
                            forward.settings.backend=backend;Captured("forward-"+backend,()=>camera.Render(),capture&&name!="scalar-current-source");
                            if(!string.IsNullOrEmpty(forward.UnavailableReason))throw new InvalidOperationException(forward.UnavailableReason);
                            var forwardPixels=Pixels();float difference=ExtendedPixelError(actual,forwardPixels);Check(name+"-whole-forward-"+backend,difference<=.003f,difference);
                            if(difference>.003f)RawPair(name+"-forward-"+backend+"-failure",forwardPixels,actual);
                        }
                        forward.settings.enabled=false;stage.sceneEnabled=true;
                    }
                    if(name=="Capsule-midpoints-4"||name=="Area-midpoints-4")Check(name+"-finite-source-penumbra",partial>20&&blocked>30,partial);
                    SaveSsrPreview("extended-source-shadow-"+name,actual,size,size,false);cases++;return actual;
                }
                foreach(var shape in new[]{SceneDecalLightShape.Capsule,SceneDecalLightShape.Area})
                {
                    light.shape=shape;
                    for(int n=1;n<=4;n++){light.shadow.extendedSamplesPerAxis=n;Case(shape+"-midpoints-"+n);}
                    light.shadow.extendedSamplesPerAxis=2;light.shadow.filter=SceneShadowFilter.Pcf3x3;Case(shape+"-pcf");light.shadow.filter=SceneShadowFilter.Hard;
                    light.rotation=Quaternion.Euler(7,13,29);light.position+=new Vector3(.17f,-.1f,.05f);Case(shape+"-current-rotated-source");light.rotation=Quaternion.identity;light.position=new Vector3(.17f,-.13f,-2);
                }
                light.shape=SceneDecalLightShape.Capsule;light.halfLength=0;Case("zero-length-source");light.halfLength=1.1f;
                light.shadow.strength=.37f;Case("partial-strength");light.shadow.strength=1;
                light.shadow.depthBias=.35f;light.shadow.normalBias=.13f;Case("world-unit-bias");light.shadow.depthBias=.002f;light.shadow.normalBias=0;
                light.shadow.nearPlane=1.5f;Case("per-source-near-sphere");light.shadow.nearPlane=.05f;
                camera.orthographic=false;camera.fieldOfView=43;Case("perspective-current-view");camera.orthographic=true;
                occluder.transform.position+=new Vector3(.38f,-.24f,.17f);Case("moving-current-caster");
                var alphaMap=Own(new Texture2D(2,2,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});
                alphaMap.SetPixels(new[]{Color.white,Color.clear,Color.clear,Color.white});alphaMap.Apply(false);caster.alphaMap=alphaMap;caster.cutoff=.5f;Case("current-caster-cutout");caster.alphaMap=null;caster.cutoff=0;
                caster.renderer.forceRenderingOff=true;Case("disabled-caster");caster.renderer.forceRenderingOff=false;
                options.backend=SceneDecalLightBackend.Scalar;var scalar=Case("scalar-current-source");options.backend=SceneDecalLightBackend.Instanced;var instanced=Case("instanced-current-source");
                Check("scalar-instanced-whole-equality",ExtendedPixelError(scalar,instanced)<=.00002f,ExtendedPixelError(scalar,instanced));
                var leaf=new VegetationLeafMaterial{enabled=true,thickness=.6f,strength=.7f};surface.leaf=leaf;Case("current-leaf-consumer");surface.leaf=null;
                Check("initial-whole-cases",cases==23,cases);
                occluder.transform.SetPositionAndRotation(new Vector3(.09f,.17f,-.9f),Quaternion.Euler(11,7,13));
                // Both sides of the declared numerical edge band remain distinct.
                foreach(var shape in new[]{SceneDecalLightShape.Capsule,SceneDecalLightShape.Area})
                {
                    light.shape=shape;light.shadow.extendedSamplesPerAxis=shape==SceneDecalLightShape.Capsule?4:2;
                    var frames=new Color[4][];var offsets=new[]{-.0004f,-.0000001f,.0000001f,.0004f};
                    for(int i=0;i<4;i++)
                    {
                        light.position=new Vector3(.17f,-.13f,-2)+(shape==SceneDecalLightShape.Capsule?Vector3.right:Vector3.up)*offsets[i];
                        frames[i]=Case(shape+"-texel-edge-side-"+i);
                    }
                    Check(shape+"-inside-edge-band-stable",ExtendedPixelError(frames[1],frames[2])<=.0003f,ExtendedPixelError(frames[1],frames[2]));
                    Check(shape+"-outside-edge-band-not-merged",ExtendedPixelError(frames[0],frames[3])>.02f,ExtendedPixelError(frames[0],frames[3]));
                }
                light.position=new Vector3(0,0,-1);light.range=2;light.halfLength=4;light.halfSize=new Vector2(4,.9f);light.areaSpread=Vector2.zero;
                occluder.transform.SetPositionAndRotation(new Vector3(0,0,-.5f),Quaternion.identity);occluder.transform.localScale=new Vector3(8,4,1);
                foreach(var shape in new[]{SceneDecalLightShape.Capsule,SceneDecalLightShape.Area})
                {
                    light.shape=shape;light.shadow.extendedSamplesPerAxis=shape==SceneDecalLightShape.Capsule?4:2;
                    var shadowed=Case(shape+"-conservative-side-support");light.shadow.enabled=false;Render();var lit=Pixels();
                    var radiance=light.radiance;light.radiance=Vector3.zero;Render();var unlit=Pixels();light.radiance=radiance;light.shadow.enabled=true;
                    Check(shape+"-distant-source-still-occluded",Sources().Any(s=>s.magnitude>light.range)&&ExtendedPixelError(shadowed,unlit)<=.0003f&&ExtendedPixelError(lit,unlit)>.05f,ExtendedPixelError(shadowed,unlit));
                }
                light.position=new Vector3(.17f,-.13f,-2);light.range=5;light.halfLength=1.1f;light.halfSize=new Vector2(1.1f,.9f);light.areaSpread=new Vector2(.35f,.25f);light.shadow.extendedSamplesPerAxis=2;
                occluder.transform.SetPositionAndRotation(new Vector3(.09f,.17f,-.9f),Quaternion.Euler(11,7,13));occluder.transform.localScale=new Vector3(.55f,.65f,1);
                var giMap=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));giMap.SetPixel(0,0,new Color(.25f,.5f,.75f));giMap.Apply(false);
                surface.gi=new SceneGiInput{source=SceneGiSource.Lightmap,lightmap=giMap};light.giWeight=.6f;stage.giBaseScale=forward.settings.giBaseScale=.7f;
                foreach(var shape in new[]{SceneDecalLightShape.Capsule,SceneDecalLightShape.Area}){light.shape=shape;Case(shape+"-current-gi-multiplication");}
                surface.bakedShadow=new SceneBakedShadowInput{source=SceneBakedShadowSource.Constant,dither=false,visibility=new Vector4(128f/255,3f/7,6f/7,2f/3)};
                for(int channel=1;channel<=4;channel++)
                {light.shape=channel%2==0?SceneDecalLightShape.Area:SceneDecalLightShape.Capsule;light.bakedShadowChannel=(SceneBakedShadowChannel)channel;Case("current-gi-baked-channel-"+channel);}
                var monitorHost=Own(new GameObject("Extended-shadow current Monitor"));var monitorCamera=monitorHost.AddComponent<Camera>();
                monitorCamera.CopyFrom(camera);monitorCamera.enabled=false;monitorCamera.targetTexture=null;monitorCamera.cullingMask=1<<22;
                monitorCamera.transform.position=new Vector3(0,0,-3);monitorCamera.orthographicSize=1;
                var panel=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));panel.layer=22;panel.transform.localScale=new Vector3(5,3,1);
                var panelMaterial=Own(new Material(Resources.Load<Shader>("MonitorEmission")));panelMaterial.SetTexture("_MonitorTex",Texture2D.whiteTexture);panel.GetComponent<Renderer>().sharedMaterial=panelMaterial;
                var monitor=monitorHost.AddComponent<HdrMonitor>();monitor.width=65;monitor.height=33;monitor.monitorEnabled=true;options.monitor=monitor;
                ulong revision=0;
                foreach(var shape in new[]{SceneDecalLightShape.Capsule,SceneDecalLightShape.Area})
                {
                    light.shape=shape;panelMaterial.SetVector("_MonitorTint",new Vector3(4,.5f,.25f));Check(shape+"-real-monitor-red-update",monitor.TryUpdate(0,revision++,out _));var red=Case(shape+"-current-monitor-red");
                    panelMaterial.SetVector("_MonitorTint",new Vector3(.25f,.5f,4));Check(shape+"-real-monitor-blue-update",monitor.TryUpdate(0,revision++,out _));var blue=Case(shape+"-current-monitor-blue");
                    Check(shape+"-real-monitor-content-changed",ExtendedPixelError(red,blue)>.05f,ExtendedPixelError(red,blue));
                }
                options.monitor=null;monitorHost.SetActive(false);panel.SetActive(false);surface.gi=null;surface.bakedShadow=null;light.bakedShadowChannel=SceneBakedShadowChannel.None;
                light.giWeight=0;stage.giBaseScale=forward.settings.giBaseScale=0;
                receiver.transform.rotation=Quaternion.Euler(0,180,0);light.backlightScale=.7f;Case("current-backlight-visibility");receiver.transform.rotation=Quaternion.identity;light.backlightScale=0;
                Check("expanded-whole-cases",cases==44,cases);
                // Mixed native atlas first tiles, shader indices and batch boundaries.
                var mixed=new[]{
                    new SceneDecalLight{shape=SceneDecalLightShape.Spot,position=new Vector3(.213f,-.271f,-2.11f),range=5,spotOuterAngle=97,radiance=new Vector3(.5f,1,.25f),specularScale=0},
                    light,
                    new SceneDecalLight{shape=SceneDecalLightShape.Point,position=new Vector3(-.317f,.239f,-1.87f),range=5,radiance=new Vector3(1,.25f,.5f),specularScale=0},
                    new SceneDecalLight{shape=SceneDecalLightShape.Area,position=new Vector3(-.123f,.197f,-2.17f),range=5,halfSize=new Vector2(.9f,.7f),areaSpread=new Vector2(.2f,.3f),radiance=new Vector3(.25f,.5f,1),specularScale=0}
                };
                light.shape=SceneDecalLightShape.Capsule;foreach(var lamp in mixed){lamp.shadow.enabled=true;lamp.shadow.extendedSourceCoverage=true;}
                options.lights=Array.Empty<SceneDecalLight>();Render();var basePixels=Pixels();var expectedMixed=(Color[])basePixels.Clone();
                foreach(var lamp in mixed){options.lights=new[]{lamp};Render();var single=Pixels();for(int i=0;i<single.Length;i++)expectedMixed[i]+=single[i]-basePixels[i];}
                options.lights=mixed;
                foreach(var backend in new[]{SceneDecalLightBackend.Scalar,SceneDecalLightBackend.Instanced})foreach(int batch in new[]{1,2,256})
                {
                    options.backend=backend;options.batchSize=batch;var mixedFrame=Render();var actualMixed=Pixels();float error=ExtendedPixelError(actualMixed,expectedMixed);
                    Check("mixed-"+backend+"-batch-"+batch,error<=.0003f&&stage.SubmittedLights==4&&stage.LightShadowMapCount==43&&stage.LightShadowCasterDrawCalls==43,error);
                    SaveSsrPreview("extended-source-shadow-mixed-"+backend+"-"+batch,actualMixed,size,size,false);
                }
                stage.sceneEnabled=false;forward.settings.enabled=true;forwardSurface.gi=null;forwardSurface.bakedShadow=null;forwardSurface.leaf=null;
                foreach(var backend in new[]{SceneForwardLightBackend.BruteForce,SceneForwardLightBackend.Tiled})
                {forward.settings.backend=backend;camera.Render();float error=ExtendedPixelError(Pixels(),expectedMixed);Check("mixed-forward-"+backend,error<=.003f&&forward.SubmittedLights==4&&forward.LocalShadowMapCount==43,error);}
                forward.settings.enabled=false;stage.sceneEnabled=true;options.lights=new[]{light};options.backend=SceneDecalLightBackend.Instanced;options.batchSize=256;
                // Native one-bone caster, against independently transformed authored vertices.
                var staticRenderer=caster.renderer;var skinHost=Own(new GameObject("Extended-shadow native caster"));skinHost.layer=24;
                var skin=skinHost.AddComponent<SkinnedMeshRenderer>();var skinMesh=Own(Instantiate(borrowedMesh));
                skinMesh.vertices=borrowedMesh.vertices.Select(v=>Vector3.Scale(v,new Vector3(.55f,.65f,1))).ToArray();
                skinMesh.boneWeights=Enumerable.Repeat(new BoneWeight{boneIndex0=0,weight0=1},skinMesh.vertexCount).ToArray();skinMesh.bindposes=new[]{Matrix4x4.identity};
                var bone=Own(new GameObject("Extended-shadow current bone")).transform;skin.sharedMesh=skinMesh;skin.sharedMaterial=borrowedMaterial;skin.bones=new[]{bone};skin.rootBone=bone;
                skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*20);
                for(int pose=0;pose<3;pose++)
                {
                    bone.SetPositionAndRotation(new Vector3(.09f+pose*.1f,.17f-pose*.12f,-.9f+pose*.04f),Quaternion.Euler(11+pose*9,7+pose*4,13+pose*19));
                    light.shape=pose==1?SceneDecalLightShape.Area:SceneDecalLightShape.Capsule;yield return null;
                    caster.renderer=skin;var native=Case("native-caster-pose-"+pose,false);var nativeDepth=ReadSceneTarget(Render().lightShadowAtlas);
                    caster.renderer=staticRenderer;occluder.transform.SetPositionAndRotation(bone.position,bone.rotation);var reference=Case("authored-caster-pose-"+pose);
                    float imageError=ExtendedPixelError(native,reference),depthError=ExtendedPixelError(nativeDepth,ReadSceneTarget(Render().lightShadowAtlas));
                    Check("native-authored-caster-image-"+pose,imageError<=.0003f,imageError);Check("native-authored-caster-depth-"+pose,depthError<=.00002f,depthError);
                }
                skinHost.SetActive(false);Check("all-whole-cases",cases==50,cases);
                void Reject(string name){camera.Render();Check(name,!stage.TryGetFrame(out _)&&stage.LightShadowMapCount==0&&stage.LightTargetCount==0);}
                light.shadow.extendedSamplesPerAxis=5;Reject("invalid-sample-count-rejected");light.shadow.extendedSamplesPerAxis=2;
                options.shadows.maxExtendedSourceSamples=1;Reject("source-budget-before-allocation");options.shadows.maxExtendedSourceSamples=64;
                options.shadows.maxExtendedCasterDraws=1;Reject("draw-budget-before-allocation");options.shadows.maxExtendedCasterDraws=32768;
                options.shadows.tileResolution=2048;Reject("oversized-atlas-rejected");options.shadows.tileResolution=32;
                var old=Render();var oldAtlas=old.lightShadowAtlas;options.shadows.tileResolution=64;var resized=Render();Check("resized-atlas-invalidates-frame",!old.IsCurrent&&!oldAtlas.IsCreated()&&resized.IsCurrent);options.shadows.tileResolution=32;
                var lost=Render();lost.lightShadowAtlas.Release();Check("lost-atlas-invalidates-frame",!lost.IsCurrent);Check("lost-atlas-recreated",Render().lightShadowAtlas.IsCreated());
                light.shadow.strength=0;Check("zero-strength-releases-source-atlas",Render().lightShadowAtlas==null);light.shadow.strength=1;
                light.shadow.enabled=false;Check("disabled-releases-source-atlas",Render().lightShadowAtlas==null);
                Check("borrowed-caster-unchanged",caster.renderer.sharedMaterial==borrowedMaterial&&occluder.GetComponent<MeshFilter>().sharedMesh==borrowedMesh&&!caster.renderer.forceRenderingOff);
            }
            finally
            {
                RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
        }

        // Independent D3D11 raster oracle for this authored mesh: clipping,
        // 8-bit subpixel snapping, top-left coverage and perspective attributes.
        // An ideal plane ray is not a native depth oracle at snapped triangle edges.
        private static float[] ExtendedRasterDepth(Mesh mesh,SceneShadowCaster caster,Vector3 source,Vector3 forward,Vector3 right,Vector3 up,int size,float near,float far,bool active)
        {
            var result=Enumerable.Repeat(1f,size*size).ToArray();if(!active)return result;
            var positions=mesh.vertices;var uvs=mesh.uv;var indices=mesh.GetTriangles(caster.materialIndex);
            var matrix=caster.renderer.localToWorldMatrix;
            var vertices=positions.Select((v,i)=>new ExtendedRasterVertex {
                relative=matrix.MultiplyPoint3x4(Vector3.Scale(v,caster.vertexScale))-source,
                uv=new Vector2(uvs[i].x*caster.uvST.x+caster.uvST.z,uvs[i].y*caster.uvST.y+caster.uvST.w)
            }).ToArray();
            double Z(ExtendedRasterVertex v)=>Vector3.Dot(v.relative,forward);
            System.Collections.Generic.List<ExtendedRasterVertex> Clip(System.Collections.Generic.List<ExtendedRasterVertex> input,double plane,bool lower)
            {
                var output=new System.Collections.Generic.List<ExtendedRasterVertex>();
                for(int i=0;i<input.Count;i++)
                {
                    var a=input[i];var b=input[(i+1)%input.Count];double da=Z(a)-plane,db=Z(b)-plane;
                    bool insideA=lower?da>=0:da<=0,insideB=lower?db>=0:db<=0;
                    if(insideA)output.Add(a);
                    if(insideA!=insideB){float t=(float)(da/(da-db));output.Add(new ExtendedRasterVertex{relative=Vector3.LerpUnclamped(a.relative,b.relative,t),uv=Vector2.LerpUnclamped(a.uv,b.uv,t)});}
                }
                return output;
            }
            double Edge(double ax,double ay,double bx,double by,double px,double py)=>(bx-ax)*(py-ay)-(by-ay)*(px-ax);
            bool Covered(double edge,double ax,double ay,double bx,double by)=>edge>0||(edge==0&&(by<ay||(by==ay&&bx<ax)));
            for(int triangle=0;triangle<indices.Length;triangle+=3)
            {
                var polygon=new System.Collections.Generic.List<ExtendedRasterVertex>{vertices[indices[triangle]],vertices[indices[triangle+1]],vertices[indices[triangle+2]]};
                polygon=Clip(Clip(polygon,near/Math.Sqrt(3),true),far,false);
                for(int t=1;t+1<polygon.Count;t++)
                {
                    var v=new[]{polygon[0],polygon[t],polygon[t+1]};var x=new double[3];var y=new double[3];var z=new double[3];
                    for(int k=0;k<3;k++)
                    {
                        z[k]=Z(v[k]);x[k]=Math.Round((Vector3.Dot(v[k].relative,right)/z[k]*.5+.5)*size*256)/256;
                        y[k]=Math.Round((Vector3.Dot(v[k].relative,up)/z[k]*.5+.5)*size*256)/256;
                    }
                    double area=Edge(x[0],y[0],x[1],y[1],x[2],y[2]);if(area==0)continue;
                    if(area<0){var swap=v[1];v[1]=v[2];v[2]=swap;foreach(var a in new[]{x,y,z}){double value=a[1];a[1]=a[2];a[2]=value;}area=-area;}
                    for(int py=0;py<size;py++)for(int px=0;px<size;px++)
                    {
                        double a=Edge(x[1],y[1],x[2],y[2],px+.5,py+.5),b=Edge(x[2],y[2],x[0],y[0],px+.5,py+.5),c=Edge(x[0],y[0],x[1],y[1],px+.5,py+.5);
                        if(!Covered(a,x[1],y[1],x[2],y[2])||!Covered(b,x[2],y[2],x[0],y[0])||!Covered(c,x[0],y[0],x[1],y[1]))continue;
                        a/=z[0];b/=z[1];c/=z[2];double sum=a+b+c;a/=sum;b/=sum;c/=sum;
                        double rx=a*v[0].relative.x+b*v[1].relative.x+c*v[2].relative.x,ry=a*v[0].relative.y+b*v[1].relative.y+c*v[2].relative.y,rz=a*v[0].relative.z+b*v[1].relative.z+c*v[2].relative.z;
                        double radial=Math.Sqrt(rx*rx+ry*ry+rz*rz);if(radial<near||radial>far)continue;
                        float alpha=caster.alpha;
                        if(caster.alphaMap is Texture2D map)
                        {
                            double u=a*v[0].uv.x+b*v[1].uv.x+c*v[2].uv.x,w=a*v[0].uv.y+b*v[1].uv.y+c*v[2].uv.y;
                            alpha*=map.GetPixel(Mathf.Clamp((int)Math.Floor(u*map.width),0,map.width-1),Mathf.Clamp((int)Math.Floor(w*map.height),0,map.height-1)).a;
                        }
                        if(alpha>=caster.cutoff)result[py*size+px]=Mathf.Min(result[py*size+px],(float)(radial/far));
                    }
                }
            }
            return result;
        }
        private struct ExtendedRasterVertex {public Vector3 relative;public Vector2 uv;}
        private static float ExtendedPixelError(Color[] a,Color[] b)
        {
            foreach(var pixels in new[]{a,b})foreach(var value in pixels)for(int c=0;c<4;c++)
                if(float.IsNaN(value[c])||float.IsInfinity(value[c]))return float.MaxValue;
            return PixelError(a,b);
        }
    }
}
