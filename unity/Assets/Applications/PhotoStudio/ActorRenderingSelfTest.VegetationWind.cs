using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyVegetationWind(Report report)
        {
            yield return null;
            void Check(string name,bool ok,float error=0) => FrameworkCheck(report,"vegetation-wind-"+name,ok,error);
            var previous=FindObjectsOfType<Renderer>();var forced=previous.Select(r=>r.forceRenderingOff).ToArray();
            foreach(var r in previous)r.forceRenderingOff=true;
            var active=RenderTexture.active;VegetationWindDeformer gpu=null,cpu=null;
            try
            {
                const int width=97,height=97;
                var points=new List<Vector3>();var uv=new List<Vector2>();var coefficients=new List<Vector3>();var triangles=new List<int>();
                for(int blade=0;blade<3;blade++)
                {
                    int start=points.Count;
                    for(int row=0;row<=8;row++)for(int side=0;side<2;side++)
                    {
                        points.Add(new Vector3((blade-1)*.67f+(side-.5f)*(.27f-row*.014f),-.8f+row*.2f,0));
                        uv.Add(new Vector2(side,row/8f));coefficients.Add(new Vector3(.5f+blade*.25f,blade*.27f,.4f+blade*.3f));
                    }
                    for(int row=0;row<8;row++){int a=start+row*2;triangles.AddRange(new[]{a,a+2,a+1,a+1,a+2,a+3});}
                }
                var source=Own(new Mesh{name="Independent three rooted leaf strips"});source.vertices=points.ToArray();source.normals=Enumerable.Repeat(Vector3.back,points.Count).ToArray();
                source.tangents=Enumerable.Repeat(new Vector4(1,0,0,-1),points.Count).ToArray();source.uv=uv.ToArray();source.uv2=uv.ToArray();
                source.colors=Enumerable.Repeat(new Color(.13f,.27f,.59f,.71f),points.Count).ToArray();source.triangles=triangles.ToArray();source.RecalculateBounds();
                var sourceVertices=source.vertices;var sourceNormals=source.normals;var sourceTangents=source.tangents;var sourceUv=source.uv;var sourceColors=source.colors;var sourceBounds=source.bounds;
                var weights=coefficients.ToArray();
                Check("create-gpu",VegetationWindDeformer.TryCreate(source,weights,VegetationWindBackend.Gpu,false,16,out gpu,out var error));
                if(gpu==null)throw new InvalidOperationException(error);
                Check("create-explicit-cpu",VegetationWindDeformer.TryCreate(source,weights,VegetationWindBackend.Cpu,false,16,out cpu,out error));
                if(cpu==null)throw new InvalidOperationException(error);
                var settings=new VegetationWindSettings{rootLocal=new Vector3(0,-.8f,0),height=2,displacementWorld=new Vector3(.35f,0,.21f),flutterWorld=new Vector3(.08f,.015f,-.06f),flutterFrequencyHz=1.3f};
                var reference=Own(Instantiate(source));reference.name="Independent finite-difference wind reference";
                var leaves=new GameObject[2];var cameras=new Camera[2];var stages=new SceneDeferredCamera[2];var targets=new RenderTexture[2];
                var mask=Own(new Texture2D(2,2,TextureFormat.RGBAFloat,false,true));mask.filterMode=FilterMode.Point;mask.wrapMode=TextureWrapMode.Repeat;
                mask.SetPixels(new[]{Color.white,new Color(1,1,1,0),Color.white,Color.white});mask.Apply(false);
                var material=new SceneDeferredCamera.MaterialInputs{albedo=new Vector3(.15f,.51f,.23f),mos=new Vector3(0,1,.2f),emission=new Vector3(.01f,.03f,.01f),albedoMap=mask,uvST=new Vector4(.73f,.83f,.071f,.113f)};
                for(int i=0;i<2;i++)
                {
                    var host=Own(new GameObject("Vegetation consumer camera "+i));var camera=host.AddComponent<Camera>();cameras[i]=camera;
                    camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;camera.cullingMask=0;
                    camera.transform.position=new Vector3(0,0,-4);camera.orthographic=true;camera.orthographicSize=1.6f;camera.aspect=1;
                    camera.nearClipPlane=.1f;camera.farClipPlane=20;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                    targets[i]=Own(new RenderTexture(width,height,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));targets[i].Create();camera.targetTexture=targets[i];
                    var stage=host.AddComponent<SceneDeferredCamera>();stages[i]=stage;stage.sceneEnabled=true;stage.sceneLayers=1<<(24+i);stage.motion.enabled=true;
                    stage.lightRadiance=new Vector3(.7f,.5f,.3f);stage.ambientIrradiance=new Vector3(.11f,.17f,.13f);
                    var leaf=Own(new GameObject("Current wind leaves "+i));leaves[i]=leaf;leaf.layer=24+i;leaf.AddComponent<MeshFilter>();var renderer=leaf.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial=Own(new Material(Resources.Load<Shader>("CrowdNativeReference")));
                    var receiver=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));receiver.layer=24+i;receiver.transform.position=Vector3.forward;receiver.transform.localScale=Vector3.one*2.9f;
                    var surface=new SceneDeferredCamera.Surface{renderer=renderer,cull=CullMode.Off,inputs=material,alphaCutoff=.5f};
                    var ground=new SceneDeferredCamera.Surface{renderer=receiver.GetComponent<Renderer>(),cull=CullMode.Off};ground.inputs.albedo=new Vector3(.4f,.3f,.2f);
                    stage.surfaces=new[]{surface,ground};
                    var light=new SceneDecalLight{shape=SceneDecalLightShape.Spot,position=new Vector3(-.2f,.3f,-2),range=6,spotInnerAngle=100,spotOuterAngle=100,radiance=new Vector3(1.3f,.9f,.7f)};
                    light.shadow.enabled=true;light.shadow.depthBias=.003f;
                    stage.decalLighting.enabled=true;stage.decalLighting.lights=new[]{light};stage.decalLighting.backend=SceneDecalLightBackend.Scalar;
                    stage.decalLighting.shadows.tileResolution=128;stage.decalLighting.shadows.casters=new[]{new SceneShadowCaster{renderer=renderer,cull=CullMode.Off,alphaMap=mask,uvST=material.uvST,cutoff=.5f}};
                }
                Vector3[] referencePositions=null,referenceNormals=null;Vector4[] referenceTangents=null;
                void Reference(double time)
                {
                    referencePositions=(Vector3[])sourceVertices.Clone();referenceNormals=(Vector3[])sourceNormals.Clone();referenceTangents=(Vector4[])sourceTangents.Clone();
                    if(settings.enabled)
                    {
                        var inverse=leaves[0].transform.localToWorldMatrix.inverse;
                        var wind=inverse.MultiplyVector(settings.displacementWorld+(settings.naturalWind!=null?settings.naturalWind.Sample(time)*settings.naturalWindScale:Vector3.zero));
                        var flutter=inverse.MultiplyVector(settings.flutterWorld);var axis=settings.upLocal.normalized;
                        double phase=time*settings.flutterFrequencyHz+settings.phaseCycles;phase-=Math.Floor(phase);
                        for(int v=0;v<sourceVertices.Length;v++)
                        {
                            var w=weights[v];double wave=Math.Sin((phase+w.y)*2*Math.PI);
                            var d=new[]{wind.x*(double)w.x+flutter.x*w.z*wave,wind.y*(double)w.x+flutter.y*w.z*wave,wind.z*(double)w.x+flutter.z*w.z*wave};
                            double[] Field(double[] p)
                            {
                                double h=Math.Max(0,Math.Min(1,((p[0]-settings.rootLocal.x)*axis.x+(p[1]-settings.rootLocal.y)*axis.y+(p[2]-settings.rootLocal.z)*axis.z)/settings.height));
                                double shape=3*h*h-2*h*h*h;return new[]{p[0]+d[0]*shape,p[1]+d[1]*shape,p[2]+d[2]*shape};
                            }
                            var p0=sourceVertices[v];var point=new[]{(double)p0.x,p0.y,p0.z};var current=Field(point);
                            referencePositions[v]=new Vector3((float)current[0],(float)current[1],(float)current[2]);
                            // Independent symmetric finite differences, then cofactor
                            // normal transport; no production rank-one inverse formula.
                            var columns=new Vector3[3];
                            for(int a=0;a<3;a++)
                            {
                                var plus=(double[])point.Clone();var minus=(double[])point.Clone();plus[a]+=.000001;minus[a]-=.000001;
                                var hi=Field(plus);var lo=Field(minus);columns[a]=new Vector3((float)((hi[0]-lo[0])/.000002),(float)((hi[1]-lo[1])/.000002),(float)((hi[2]-lo[2])/.000002));
                            }
                            var n=sourceNormals[v];var normal=(Vector3.Cross(columns[1],columns[2])*n.x+Vector3.Cross(columns[2],columns[0])*n.y+Vector3.Cross(columns[0],columns[1])*n.z).normalized;
                            var tangent=sourceTangents[v];var t=columns[0]*tangent.x+columns[1]*tangent.y+columns[2]*tangent.z;t=(t-normal*Vector3.Dot(normal,t)).normalized;
                            referenceNormals[v]=normal;referenceTangents[v]=new Vector4(t.x,t.y,t.z,tangent.w);
                        }
                    }
                    reference.SetVertices(referencePositions);reference.SetNormals(referenceNormals);reference.SetTangents(referenceTangents);reference.RecalculateBounds();
                }
                SceneDeferredCamera.Frame Render(int i)
                {cameras[i].Render();if(!stages[i].TryGetFrame(out var frame))throw new InvalidOperationException(stages[i].UnavailableReason);return frame;}
                Color[] first=null,firstShadow=null;int step=0;
                void Case(string name,double time,bool expectMoving)
                {
                    var matrix=leaves[0].transform.localToWorldMatrix;
                    if(!gpu.TryUpdate(settings,time,matrix)||!cpu.TryUpdate(settings,time,matrix))throw new InvalidOperationException(gpu.UnavailableReason??cpu.UnavailableReason);
                    Reference(time);leaves[0].GetComponent<MeshFilter>().sharedMesh=gpu.Mesh;leaves[1].GetComponent<MeshFilter>().sharedMesh=reference;
                    var actual=ReadFaceGpu(gpu.Mesh);var fallback=ReadFaceGpu(cpu.Mesh);float fieldError=0,backendError=0;bool pinned=true,bounded=true,untouched=true;
                    for(int v=0;v<actual.Length;v++)
                    {
                        for(int c=0;c<3;c++){fieldError=Mathf.Max(fieldError,Mathf.Abs(actual[v][c]-referencePositions[v][c]),Mathf.Abs(actual[v][c+3]-referenceNormals[v][c]),Mathf.Abs(actual[v][c+6]-referenceTangents[v][c]));}
                        for(int c=0;c<12;c++)backendError=Mathf.Max(backendError,Mathf.Abs(actual[v][c]-fallback[v][c]));
                        if(sourceVertices[v].y==-.8f)pinned&=actual[v][0]==sourceVertices[v].x&&actual[v][1]==sourceVertices[v].y&&actual[v][2]==sourceVertices[v].z;
                        bounded&=gpu.Mesh.bounds.Contains(new Vector3(actual[v][0],actual[v][1],actual[v][2]));
                        untouched&=actual[v][9]==sourceTangents[v].w&&actual[v][10]==sourceUv[v].x&&actual[v][11]==sourceUv[v].y;
                    }
                    Check(name+"-every-vertex-field-and-jacobian",fieldError<=.00002f,fieldError);Check(name+"-every-cpu-gpu-attribute",backendError<=.00002f,backendError);
                    Check(name+"-pinned-root-bounds-and-uv",pinned&&bounded&&untouched);
                    var a=Render(0);var b=Render(1);var color=ReadSceneTarget(targets[0]);var expectedColor=ReadSceneTarget(targets[1]);
                    void Whole(string channel,RenderTexture x,RenderTexture y){float e=PixelError(ReadSceneTarget(x),ReadSceneTarget(y));Check(name+"-whole-"+channel,e<=.0002f,e);}
                    float colorError=PixelError(color,expectedColor);Check(name+"-whole-native-color",colorError<=.0002f,colorError);
                    Whole("normal",a.normalGroup,b.normalGroup);Whole("depth",a.mosDepth,b.mosDepth);Whole("shadow",a.lightShadowAtlas,b.lightShadowAtlas);
                    Whole("motion",a.motionVectors,b.motionVectors);Whole("previous-normal-identity",a.previousNormalIdentity,b.previousNormalIdentity);
                    var shadow=ReadSceneTarget(a.lightShadowAtlas);
                    if(first==null){first=color;firstShadow=shadow;}
                    else if(expectMoving){Check(name+"-positive-current-color",PixelError(first,color)>.01f);Check(name+"-positive-current-shadow",PixelError(firstShadow,shadow)>.001f);}
                    if(step++>0&&expectMoving)
                    {float movement=ReadSceneTarget(a.motionVectors).Where(c=>c.a>.5f).Select(c=>Mathf.Abs(c.r)+Mathf.Abs(c.g)).DefaultIfEmpty(0).Max();Check(name+"-positive-current-motion",movement>.0001f,movement);}
                    SaveSsrPreview("vegetation-wind-"+name,color,width,height,false);
                }
                Case("disabled-rest",double.NaN,false);Check("gpu-does-not-update-cpu-rest-copy",gpu.Mesh.vertices.SequenceEqual(sourceVertices));
                settings.enabled=true;
                foreach(double time in new[]{0.0,.137,.381,-.217,1000000.137})Case("clock-"+time,time,true);
                var pauseVertices=ReadFaceGpu(gpu.Mesh);var pauseColor=ReadSceneTarget(targets[0]);
                Case("paused",1000000.137,false);var pauseAgain=ReadFaceGpu(gpu.Mesh);
                Check("pause-current-geometry-and-color-exact",pauseVertices.Select((row,v)=>row.SequenceEqual(pauseAgain[v])).All(v=>v)&&PixelError(pauseColor,ReadSceneTarget(targets[0]))==0);
                // The existing scene-motion shader retains native subpixel-raster
                // residuals even for an unchanged ordinary CPU mesh. Check against
                // that stationary native control, not an assumed all-zero texture.
                var pausedMotion=ReadSceneTarget(Render(0).motionVectors);var stationaryReference=ReadSceneTarget(Render(1).motionVectors);
                float pauseMotionError=PixelError(pausedMotion,stationaryReference);
                Check("pause-native-stationary-motion-reference",pauseMotionError<=.0002f,pauseMotionError);
                foreach(var scale in new[]{new Vector3(1.1f,.87f,.93f),new Vector3(-1.1f,.87f,.93f)})
                {
                    for(int i=0;i<2;i++){leaves[i].transform.localScale=scale;leaves[i].transform.localRotation=Quaternion.Euler(7,13,-9);stages[i].ResetMotionHistory();}
                    Case("transform-"+scale,.271,false);Case("transform-moving-"+scale,.571,true);
                }
                for(int i=0;i<2;i++){leaves[i].transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);leaves[i].transform.localScale=Vector3.one;stages[i].ResetMotionHistory();}
                settings.displacementWorld=Vector3.zero;settings.flutterWorld=Vector3.zero;
                settings.naturalWind.enabled=true;settings.naturalWind.steadyForce=new Vector3(.25f,0,.15f);settings.naturalWind.sineAmplitude=Vector3.zero;settings.naturalWind.randomAmplitude=Vector3.zero;
                settings.naturalWind.gustSeconds=2;settings.naturalWind.calmSeconds=2;settings.naturalWind.calmStrength=0;
                Case("natural-gust",1,false);Case("natural-calm",3,false);
                Check("natural-calm-rest-vertex-exact",ReadFaceGpu(gpu.Mesh).Select((v,i)=>v[0]==sourceVertices[i].x&&v[1]==sourceVertices[i].y&&v[2]==sourceVertices[i].z).All(v=>v));
                settings.naturalWind.enabled=false;settings.displacementWorld=new Vector3(.35f,0,.21f);settings.flutterWorld=new Vector3(.08f,.015f,-.06f);
                Case("restored-current-field",.413,true);
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_VEGETATION_WIND")=="1")
                {
                    FsrCaptureDrain(targets[0]);bool began=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try{if(!gpu.TryUpdate(settings,.613,leaves[0].transform.localToWorldMatrix))throw new InvalidOperationException(gpu.UnavailableReason);Render(0);FsrCaptureDrain(targets[0]);}
                    finally{if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("native-compute-current-depth-shadow-motion",began&&ended);
                }
                void Reject(string name,Action change,Action restore)
                {change();Check("reject-"+name,!gpu.TryUpdate(settings,.2,Matrix4x4.identity)&&gpu.Mesh==null&&!string.IsNullOrEmpty(gpu.UnavailableReason));restore();Check("recover-"+name,gpu.TryUpdate(settings,.2,Matrix4x4.identity));}
                Reject("zero-height",()=>settings.height=0,()=>settings.height=2);
                Reject("nan-displacement",()=>settings.displacementWorld.x=float.NaN,()=>settings.displacementWorld.x=.35f);
                Reject("folding-jacobian",()=>settings.displacementWorld.y=2,()=>settings.displacementWorld.y=0);
                Reject("zero-up",()=>settings.upLocal=Vector3.zero,()=>settings.upLocal=Vector3.up);
                Reject("phase",()=>settings.phaseCycles=double.PositiveInfinity,()=>settings.phaseCycles=0);
                Check("reject-singular-transform",!gpu.TryUpdate(settings,.2,Matrix4x4.Scale(new Vector3(1,0,1)))&&gpu.Mesh==null);
                Check("reject-invalid-clock",!gpu.TryUpdate(settings,double.NaN,Matrix4x4.identity)&&gpu.Mesh==null);
                // Check finite values below Unity's vector equality/normalization
                // tolerances against actual streams, not an absolute image tolerance.
                settings.displacementWorld=new Vector3(0,0,.000002f);settings.flutterWorld=Vector3.zero;
                bool smallOk=gpu.TryUpdate(settings,0,Matrix4x4.identity)&&cpu.TryUpdate(settings,0,Matrix4x4.identity);
                var smallGpu=ReadFaceGpu(gpu.Mesh);var smallCpu=ReadFaceGpu(cpu.Mesh);float smallError=0;
                for(int v=0;v<sourceVertices.Length;v++)
                {
                    double h=(sourceVertices[v].y-(double)settings.rootLocal.y)/settings.height;
                    double z=settings.displacementWorld.z*(double)weights[v].x*h*h*(3-2*h);
                    smallError=Mathf.Max(smallError,(float)Math.Abs(smallGpu[v][2]-z),(float)Math.Abs(smallCpu[v][2]-z));
                }
                Check("small-nonzero-field-not-rest",smallOk&&smallGpu.Last()[2]>.000001f&&smallCpu.Last()[2]>.000001f&&smallError<1e-12f,smallError);
                settings.displacementWorld=new Vector3(.35f,0,.21f);settings.flutterWorld=new Vector3(.08f,.015f,-.06f);
                gpu.TryUpdate(settings,.2,Matrix4x4.identity);cpu.TryUpdate(settings,.2,Matrix4x4.identity);
                var regularGpu=ReadFaceGpu(gpu.Mesh);var regularCpu=ReadFaceGpu(cpu.Mesh);settings.upLocal=Vector3.up*.000002f;
                bool axisOk=gpu.TryUpdate(settings,.2,Matrix4x4.identity)&&cpu.TryUpdate(settings,.2,Matrix4x4.identity);
                var smallAxisGpu=ReadFaceGpu(gpu.Mesh);var smallAxisCpu=ReadFaceGpu(cpu.Mesh);
                Check("small-valid-axis-scale-invariant",axisOk&&regularGpu.Select((row,v)=>row.SequenceEqual(smallAxisGpu[v])).All(v=>v)&&regularCpu.Select((row,v)=>row.SequenceEqual(smallAxisCpu[v])).All(v=>v));
                settings.upLocal=Vector3.up;
                foreach(bool normalChannel in new[]{true,false})
                {
                    var scaled=Own(Instantiate(source));
                    if(normalChannel)scaled.normals=sourceNormals.Select(n=>n*.000002f).ToArray();
                    else scaled.tangents=sourceTangents.Select(t=>new Vector4(t.x*.000002f,t.y*.000002f,t.z*.000002f,t.w)).ToArray();
                    bool createdGpu=VegetationWindDeformer.TryCreate(scaled,weights,VegetationWindBackend.Gpu,false,16,out var scaledGpu,out error);
                    bool createdCpu=VegetationWindDeformer.TryCreate(scaled,weights,VegetationWindBackend.Cpu,false,16,out var scaledCpu,out error);
                    using(scaledGpu)using(scaledCpu)
                    {
                        bool ok=createdGpu&&createdCpu&&scaledGpu.TryUpdate(settings,.2,Matrix4x4.identity)&&scaledCpu.TryUpdate(settings,.2,Matrix4x4.identity);float e=float.PositiveInfinity;
                        if(ok)
                        {
                            var actualGpu=ReadFaceGpu(scaledGpu.Mesh);var actualCpu=ReadFaceGpu(scaledCpu.Mesh);e=0;
                            for(int v=0;v<actualGpu.Length;v++)for(int c=0;c<12;c++)e=Mathf.Max(e,Mathf.Abs(actualGpu[v][c]-regularGpu[v][c]),Mathf.Abs(actualCpu[v][c]-regularGpu[v][c]));
                        }
                        Check("small-valid-"+(normalChannel?"normal":"tangent")+"-direction",ok&&e<=.00002f,e);
                    }
                }
                // Explicit four-stream layout and multiple submeshes must remain
                // usable, rather than assuming one interleaved prototype layout.
                var split=Own(new Mesh{name="Independent four-stream leaves"});split.SetVertexBufferParams(source.vertexCount,
                    new VertexAttributeDescriptor(VertexAttribute.Position,VertexAttributeFormat.Float32,3,0),
                    new VertexAttributeDescriptor(VertexAttribute.Normal,VertexAttributeFormat.Float32,3,1),
                    new VertexAttributeDescriptor(VertexAttribute.Tangent,VertexAttributeFormat.Float32,4,2),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0,VertexAttributeFormat.Float32,2,3));
                split.SetVertexBufferData(sourceVertices,0,0,source.vertexCount,0);split.SetVertexBufferData(sourceNormals,0,0,source.vertexCount,1);
                split.SetVertexBufferData(sourceTangents,0,0,source.vertexCount,2);split.SetVertexBufferData(sourceUv,0,0,source.vertexCount,3);
                split.SetIndexBufferParams(triangles.Count,IndexFormat.UInt16);split.SetIndexBufferData(triangles.Select(v=>(ushort)v).ToArray(),0,0,triangles.Count);
                split.subMeshCount=2;split.SetSubMesh(0,new SubMeshDescriptor(0,triangles.Count/2));split.SetSubMesh(1,new SubMeshDescriptor(triangles.Count/2,triangles.Count/2));split.bounds=sourceBounds;
                var copiedWeights=(Vector3[])weights.Clone();
                Check("create-four-stream-two-submesh",VegetationWindDeformer.TryCreate(split,copiedWeights,VegetationWindBackend.Gpu,false,16,out var splitOwner,out error));
                if(splitOwner==null)throw new InvalidOperationException(error);
                using(splitOwner)
                {
                    Check("four-stream-update",splitOwner.TryUpdate(settings,.2,Matrix4x4.identity)&&gpu.TryUpdate(settings,.2,Matrix4x4.identity));
                    var expected=ReadFaceGpu(gpu.Mesh);var actual=ReadFaceGpu(splitOwner.Mesh);float e=0;
                    for(int v=0;v<actual.Length;v++)for(int c=0;c<12;c++)e=Mathf.Max(e,Mathf.Abs(actual[v][c]-expected[v][c]));
                    Check("four-stream-all-current-attributes",e<=.00002f,e);
                    Check("multiple-submesh-indices-preserved",splitOwner.Mesh.GetIndices(0).SequenceEqual(split.GetIndices(0))&&splitOwner.Mesh.GetIndices(1).SequenceEqual(split.GetIndices(1)));
                    Array.Clear(copiedWeights,0,copiedWeights.Length);splitOwner.TryUpdate(settings,.2,Matrix4x4.identity);var unchanged=ReadFaceGpu(splitOwner.Mesh);
                    Check("coefficients-snapshotted-not-borrowed",actual.Select((row,v)=>row.SequenceEqual(unchanged[v])).All(v=>v));
                    settings.enabled=false;splitOwner.TryUpdate(settings,double.NaN,Matrix4x4.zero);var restored=ReadFaceGpu(splitOwner.Mesh);var original=ReadFaceGpu(split);
                    Check("disabled-four-stream-all-channels-exact",restored.Select((row,v)=>row.SequenceEqual(original[v])).All(v=>v));settings.enabled=true;
                }
                void RejectCreate(string name,Mesh input,Vector3[] values,int budget=16)
                {bool ok=VegetationWindDeformer.TryCreate(input,values,VegetationWindBackend.Gpu,false,budget,out var rejected,out var reason);Check("reject-create-"+name,!ok&&rejected==null&&!string.IsNullOrEmpty(reason));rejected?.Dispose();}
                var badWeights=(Vector3[])weights.Clone();badWeights[0].x=-1;RejectCreate("negative-coefficient",source,badWeights);
                badWeights[0].x=.5f;badWeights[0].y=float.NaN;RejectCreate("nan-phase",source,badWeights);RejectCreate("coefficient-count",source,new Vector3[1]);
                var rigged=Own(Instantiate(source));rigged.bindposes=new[]{Matrix4x4.identity};RejectCreate("rig-not-silently-dropped",rigged,weights);
                var huge=Own(new Mesh{name="Vegetation allocation-boundary source"});huge.vertices=new Vector3[30000];huge.normals=Enumerable.Repeat(Vector3.back,30000).ToArray();huge.triangles=new[]{0,1,2};
                RejectCreate("aggregate-gpu-budget-before-owned-allocation",huge,new Vector3[30000],1);
                Check("create-loss-control",VegetationWindDeformer.TryCreate(source,weights,VegetationWindBackend.Gpu,false,16,out var lostOwner,out error));
                if(lostOwner!=null)using(lostOwner){lostOwner.TryUpdate(settings,.2,Matrix4x4.identity);DestroyImmediate(lostOwner.Mesh);Check("destroyed-output-not-current",!lostOwner.TryUpdate(settings,.2,Matrix4x4.identity)&&lostOwner.Mesh==null);}
                Check("source-inputs-unchanged",source.vertices.SequenceEqual(sourceVertices)&&source.normals.SequenceEqual(sourceNormals)&&source.tangents.SequenceEqual(sourceTangents)&&
                    source.uv.SequenceEqual(sourceUv)&&source.colors.SequenceEqual(sourceColors)&&source.bounds==sourceBounds&&source.triangles.SequenceEqual(triangles));
                Check("real-backend-diagnostics",gpu.Backend==VegetationWindBackend.Gpu&&cpu.Backend==VegetationWindBackend.Cpu&&gpu.DispatchCount>0&&cpu.DispatchCount==0&&gpu.GpuResourceBytes>cpu.GpuResourceBytes);
                leaves[0].GetComponent<MeshFilter>().sharedMesh=null;leaves[1].GetComponent<MeshFilter>().sharedMesh=null;gpu.Dispose();cpu.Dispose();
                Check("dispose-invalidates-only-owned-output",gpu.Mesh==null&&cpu.Mesh==null&&gpu.GpuResourceBytes==0&&cpu.GpuResourceBytes==0&&source!=null);
            }
            finally
            {
                RenderTexture.active=active;gpu?.Dispose();cpu?.Dispose();
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
        }
    }
}
