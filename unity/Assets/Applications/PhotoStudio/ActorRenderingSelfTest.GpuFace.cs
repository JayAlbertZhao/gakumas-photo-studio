using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyGpuFace(Report report)
        {
            yield return null;
            var renderers = FindObjectsOfType<Renderer>(); var forced = new bool[renderers.Length];
            for (int i=0;i<renderers.Length;i++) { forced[i]=renderers[i].forceRenderingOff; renderers[i].forceRenderingOff=true; }
            var saved = RenderTexture.active;
            bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_GPU_FACE")=="1",started=false;
            try
            {
                void Check(string name,bool ok,float difference=0) => FrameworkCheck(report,"gpu-face-"+name,ok,difference);
                const int width=17,height=9,vertices=width*height,shapes=137;
                var positions=new Vector3[vertices];var normals=new Vector3[vertices];var tangents=new Vector4[vertices];var uvs=new Vector2[vertices];
                var influences=new uint[vertices*4];var triangles=new List<int>();var deltas=new List<GpuFaceDeformer.Delta>();
                for(int v=0;v<vertices;v++)
                {
                    int x=v%width,y=v/width;positions[v]=new Vector3(x*.1f-.8f,y*.1f-.4f,3+.02f*Mathf.Sin(x*.7f+y));
                    normals[v]=v%13==0?Vector3.zero:new Vector3(.2f*Mathf.Sin(v),.1f,-1.7f);
                    tangents[v]=v%17==0?new Vector4(0,0,0,-1):new Vector4(1.8f,.12f*Mathf.Cos(v),.04f,v%2==0?-1:1);
                    uvs[v]=new Vector2(x/(float)(width-1),y/(float)(height-1));
                    influences[v*4]=(30000u<<16)|0;influences[v*4+1]=(10000u<<16)|1;
                    influences[v*4+2]=(16000u<<16)|2;influences[v*4+3]=(9000u<<16)|60000;
                    if(v%11==0)for(int k=0;k<4;k++)influences[v*4+k]=0;
                    if(v%7==0){influences[v*4]=65535u<<16;for(int k=1;k<4;k++)influences[v*4+k]=0;}
                    if(x<width-1&&y<height-1) triangles.AddRange(new[]{v,v+width,v+1,v+1,v+width,v+width+1});
                }
                for(int s=0;s<shapes;s++) for(int v=s%7;v<vertices;v+=7)
                {
                    var delta=new Vector3(.003f*Mathf.Sin(v+s),.002f*Mathf.Cos(v-s),.001f*Mathf.Sin(s));
                    deltas.Add(new GpuFaceDeformer.Delta(s,v,delta));
                    if(v%19==0)deltas.Add(new GpuFaceDeformer.Delta(s,v,delta*-.3f));
                }
                var deltaArray=deltas.ToArray();var weights=new float[shapes];
                for(int i=0;i<shapes;i++) weights[i]=i%7==0?0:(i%7==1?.00009f:Mathf.Sin(i)*.9f);
                var matrices=new[]{Matrix4x4.identity,
                    Matrix4x4.TRS(new Vector3(.07f,.04f,-.02f),Quaternion.Euler(5,9,-3),new Vector3(1.1f,.9f,1.03f)),
                    Matrix4x4.TRS(new Vector3(-.02f,.03f,.08f),Quaternion.Euler(-3,4,2),new Vector3(.9f,1.02f,.94f))};
                var cameraObject=Own(new GameObject("Sparse GPU face actual camera"));var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;
                camera.orthographic=true;camera.orthographicSize=.85f;camera.aspect=1.4f;camera.nearClipPlane=.1f;camera.farClipPlane=9;
                camera.cullingMask=1<<26;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.1f,.2f,.3f,.6f);
                camera.allowHDR=true;camera.allowMSAA=false;
                var target=Own(new RenderTexture(173,127,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                var obj=Own(new GameObject("Sparse GPU face direct MeshRenderer"));obj.layer=26;var filter=obj.AddComponent<MeshFilter>();var renderer=obj.AddComponent<MeshRenderer>();
                var material=Own(new Material(Shader.Find("Hidden/GakumasPhotoMode/FaceDeformationProbe")));renderer.sharedMaterial=material;
                Color[] Render(Mesh m){filter.sharedMesh=m;camera.Render();return ReadSceneTarget(target);}
                if(capture)started=RenderDocCaptureBridge.BeginOffscreenCapture();
                foreach(bool split in new[]{false,true})
                {
                    string label=split?"split-four-streams":"interleaved";
                    var source=Own(new Mesh{name="Independent 137-target face "+label});
                    if(split)
                    {
                        source.SetVertexBufferParams(vertices,new VertexAttributeDescriptor(VertexAttribute.Position,VertexAttributeFormat.Float32,3,0),
                            new VertexAttributeDescriptor(VertexAttribute.Normal,VertexAttributeFormat.Float32,3,1),
                            new VertexAttributeDescriptor(VertexAttribute.Tangent,VertexAttributeFormat.Float32,4,2),
                            new VertexAttributeDescriptor(VertexAttribute.TexCoord0,VertexAttributeFormat.Float32,2,3));
                        source.SetVertexBufferData(positions,0,0,vertices,0);source.SetVertexBufferData(normals,0,0,vertices,1);
                        source.SetVertexBufferData(tangents,0,0,vertices,2);source.SetVertexBufferData(uvs,0,0,vertices,3);
                    }
                    else { source.vertices=positions;source.normals=normals;source.tangents=tangents;source.uv=uvs; }
                    source.triangles=triangles.ToArray();source.RecalculateBounds();
                    Check(label+"-create",GpuFaceDeformer.TryCreate(source,shapes,deltaArray,influences,3,out var gpu,out var reason));
                    if(gpu==null)throw new InvalidOperationException(reason);
                    using(gpu)
                    {
                        Check(label+"-no-unsubmitted-output",gpu.Mesh==null&&!gpu.LastDispatchSucceeded);
                        var cpu=Own(Instantiate(source));
                        float[][] first=null;
                        for(int step=0;step<5;step++)
                        {
                            var state=step==1?new[]{Matrix4x4.identity,Matrix4x4.identity,Matrix4x4.identity}:(Matrix4x4[])matrices.Clone();
                            if(step==2)state[0].m03=1e-6f;
                            var w=(float[])weights.Clone();if(step==3)Array.Clear(w,0,w.Length);
                            Check(label+"-step-"+step+"-dispatch",gpu.TryDispatch(w,state));
                            if(!gpu.LastDispatchSucceeded)throw new InvalidOperationException(gpu.UnavailableReason);
                            FaceCpuReference(positions,normals,tangents,deltaArray,w,influences,state,out var p,out var n,out var t);
                            var actual=ReadFaceGpu(gpu.Mesh);float maximum=0;bool handed=true,bounds=true,cpuRest=true,uvExact=true;
                            var meshCpu=gpu.Mesh.vertices;
                            for(int v=0;v<vertices;v++)
                            {
                                for(int c=0;c<3;c++){maximum=Mathf.Max(maximum,Mathf.Abs(actual[v][c]-p[v][c]));maximum=Mathf.Max(maximum,Mathf.Abs(actual[v][3+c]-n[v][c]));maximum=Mathf.Max(maximum,Mathf.Abs(actual[v][6+c]-t[v][c]));}
                                handed&=actual[v][9]==t[v].w;bounds&=gpu.Mesh.bounds.Contains(p[v]);cpuRest&=meshCpu[v]==positions[v];
                                uvExact&=actual[v][10]==uvs[v].x&&actual[v][11]==uvs[v].y;
                            }
                            Check(label+"-step-"+step+"-all-position-normal-tangent-max",maximum<=.00002f&&!float.IsNaN(maximum),maximum);
                            Check(label+"-step-"+step+"-handedness-uv-cpu-copy-bounds",handed&&bounds&&cpuRest&&uvExact);
                            if(step==0)first=actual;
                            if(step==4)
                            {
                                bool exact=true;for(int v=0;v<vertices;v++)for(int c=0;c<12;c++)exact&=actual[v][c]==first[v][c];
                                Check(label+"-seek-exact",exact);
                            }
                            cpu.vertices=p;cpu.normals=n;cpu.tangents=t;cpu.RecalculateBounds();
                            var expected=Render(cpu);var image=Render(gpu.Mesh);float imageMax=0;double sum=0;int covered=0;
                            for(int i=0;i<image.Length;i++)
                            {
                                if(image[i].a>.9f)covered++;
                                for(int c=0;c<4;c++){float d=Mathf.Abs(image[i][c]-expected[i][c]);imageMax=Mathf.Max(imageMax,d);sum+=d;}
                            }
                            Check(label+"-step-"+step+"-whole-image-max",imageMax<=.02f,imageMax);
                            Check(label+"-step-"+step+"-whole-image-mean",sum/(image.Length*4)<=.00005,(float)(sum/(image.Length*4)));
                            Check(label+"-step-"+step+"-actual-raster-positive-control",covered>2000,covered);
                            if(step==0)SaveSsrPreview("gpu-face-"+label,image,target.width,target.height,false);
                        }
                        var borrowed=gpu.Mesh;var invalid=(float[])weights.Clone();invalid[0]=float.NaN;
                        Check(label+"-reject-nan-no-current-output",!gpu.TryDispatch(invalid,matrices)&&gpu.Mesh==null);
                        Check(label+"-recover-current",gpu.TryDispatch(weights,matrices)&&gpu.Mesh==borrowed);
                        // Force Unity to recreate the output buffer with unchanged layout.
                        borrowed.UploadMeshData(false);
                        Check(label+"-reacquire-after-upload",gpu.TryDispatch(weights,matrices));
                        var reacquired=ReadFaceGpu(gpu.Mesh);bool same=true;
                        for(int v=0;v<vertices;v++)for(int c=0;c<12;c++)same&=reacquired[v][c]==first[v][c];
                        Check(label+"-recreated-buffer-output-exact",same);
                        filter.sharedMesh=source;gpu.Dispose();gpu.Dispose();
                        Check(label+"-dispose-no-output",gpu.Mesh==null&&borrowed==null&&gpu.BufferBytes==0&&!gpu.TryDispatch(weights,matrices));
                        Check(label+"-caller-rest-preserved",source.vertices[13]==positions[13]&&source.vertexBufferTarget==GraphicsBuffer.Target.Vertex);
                    }
                }
                VerifyGpuFaceGuards(report);
                VerifyGpuFaceBridge(report,camera,material);
                camera.targetTexture=null;target.Release();
            }
            finally
            {
                if(capture)FrameworkCheck(report,"gpu-face-requested-native-capture",started&&RenderDocCaptureBridge.EndOffscreenCapture(),0);
                RenderTexture.active=saved;
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
                for(int i=0;i<renderers.Length;i++)if(renderers[i]!=null)renderers[i].forceRenderingOff=forced[i];
            }
        }

        private void VerifyGpuFaceGuards(Report report)
        {
            void Check(string name,bool ok) => FrameworkCheck(report,"gpu-face-guard-"+name,ok,0);
            var source=Own(new Mesh());source.vertices=new[]{Vector3.zero,Vector3.right,Vector3.up};source.triangles=new[]{0,1,2};
            var deltas=new[]{new GpuFaceDeformer.Delta(0,1,new Vector3(.2f,-.1f,.05f))};var weights=new[]{.6f};
            bool Create(Mesh mesh,int shapes,GpuFaceDeformer.Delta[] d,uint[] packed,int bones)
            {bool ok=GpuFaceDeformer.TryCreate(mesh,shapes,d,packed,bones,out var owner,out _);owner?.Dispose();return ok;}
            float PositionX(Mesh m)
            {using(var buffer=m.GetVertexBuffer(0)){var data=new uint[9];buffer.GetData(data);return BitConverter.ToSingle(BitConverter.GetBytes(data[3]),0);}}
            Check("null-source",!Create(null,1,deltas,null,0));
            Check("shape-capacity",!Create(source,4097,deltas,null,0));
            Check("missing-four-influences",!Create(source,1,deltas,new uint[2],1));
            Check("invalid-vertex",!Create(source,1,new[]{new GpuFaceDeformer.Delta(0,3,Vector3.one)},null,0));
            Check("invalid-shape",!Create(source,1,new[]{new GpuFaceDeformer.Delta(1,1,Vector3.one)},null,0));
            Check("unordered-shapes",!Create(source,2,new[]{new GpuFaceDeformer.Delta(1,1,Vector3.one),new GpuFaceDeformer.Delta(0,0,Vector3.one)},null,0));
            Check("nan-delta",!Create(source,1,new[]{new GpuFaceDeformer.Delta(0,1,new Vector3(float.NaN,0,0))},null,0));
            source.UploadMeshData(true);Check("unreadable-source",!Create(source,1,deltas,null,0));
            source=Own(new Mesh());source.vertices=new[]{Vector3.zero,Vector3.right,Vector3.up};source.triangles=new[]{0,1,2};
            Check("no-direction-channels",GpuFaceDeformer.TryCreate(source,1,deltas,null,0,out var a,out _));
            Check("second-independent-owner",GpuFaceDeformer.TryCreate(source,1,deltas,null,0,out var b,out _));
            try
            {
                Check("first-dispatch",a.TryDispatch(weights,Array.Empty<Matrix4x4>()));
                var first=a.Mesh;
                Check("second-dispatch",b.TryDispatch(new[]{-.5f},Array.Empty<Matrix4x4>()));
                Check("separate-current-results",Mathf.Abs(PositionX(first)-1.12f)<1e-6f&&Mathf.Abs(PositionX(b.Mesh)-.9f)<1e-6f);
                Check("wrong-weight-count",!a.TryDispatch(Array.Empty<float>(),Array.Empty<Matrix4x4>())&&a.Mesh==null);
                Check("wrong-bone-count",!a.TryDispatch(weights,new[]{Matrix4x4.identity})&&a.Mesh==null);
                Check("infinite-weight",!a.TryDispatch(new[]{float.PositiveInfinity},Array.Empty<Matrix4x4>()));
                Check("weight-bound",!a.TryDispatch(new[]{65f},Array.Empty<Matrix4x4>()));
                Check("bounded-negative-weight",a.TryDispatch(new[]{-64f},Array.Empty<Matrix4x4>())&&a.Mesh.bounds.Contains(Vector3.right+deltas[0].position*-64f));
                UnityEngine.Object.DestroyImmediate(first);Check("destroyed-mesh",!a.TryDispatch(weights,Array.Empty<Matrix4x4>())&&a.Mesh==null);
                Check("other-owner-survives",b.TryDispatch(weights,Array.Empty<Matrix4x4>()));
                var changed=b.Mesh;changed.Clear();changed.vertices=new[]{Vector3.zero,Vector3.up};
                Check("changed-layout",!b.TryDispatch(weights,Array.Empty<Matrix4x4>())&&b.Mesh==null);
            }
            finally {a?.Dispose();b?.Dispose();}
            Check("zero-shape-create",GpuFaceDeformer.TryCreate(source,0,Array.Empty<GpuFaceDeformer.Delta>(),null,0,out var zero,out _));
            using(zero)Check("zero-shape-bone-exact-rest",zero.TryDispatch(Array.Empty<float>(),Array.Empty<Matrix4x4>())&&PositionX(zero.Mesh)==1);
            var packedOne=new uint[12];for(int v=0;v<3;v++)packedOne[v*4]=65535u<<16;
            Check("one-bone-create",GpuFaceDeformer.TryCreate(source,1,deltas,packedOne,1,out var bone,out _));
            using(bone)
            {
                var matrix=Matrix4x4.identity;matrix.m00=float.NaN;
                Check("nan-matrix",!bone.TryDispatch(weights,new[]{matrix})&&bone.Mesh==null);
                matrix=Matrix4x4.identity;matrix.m30=.1f;Check("nonaffine-matrix",!bone.TryDispatch(weights,new[]{matrix}));
                matrix=Matrix4x4.identity;matrix.m03=1e-6f;
                Check("near-identity-snaps-exact",bone.TryDispatch(weights,new[]{matrix})&&PositionX(bone.Mesh)==1.12f);
                var actual=bone.Mesh;var mutable=(float[])weights.Clone();bone.TryDispatch(mutable,new[]{Matrix4x4.identity});mutable[0]=-10;
                Check("borrowed-weights-upload-is-snapshot",PositionX(actual)==1.12f);
                actual.vertexBufferTarget=GraphicsBuffer.Target.Vertex;
                Check("missing-raw-target-rejected",!bone.TryDispatch(weights,new[]{Matrix4x4.identity})&&bone.Mesh==null);
            }
            source.MarkDynamic();
            bool created=GpuFaceDeformer.TryCreate(source,1,deltas,null,0,out var dynamicOwner,out var dynamicReason);
            try
            {
                bool submitted=created&&dynamicOwner.TryDispatch(weights,Array.Empty<Matrix4x4>());
                Check("dynamic-caller-mesh-hint-safe",submitted?Mathf.Abs(PositionX(dynamicOwner.Mesh)-1.12f)<1e-6f:
                    (!created?dynamicReason!=null:dynamicOwner.Mesh==null&&dynamicOwner.UnavailableReason!=null));
            }
            finally{dynamicOwner?.Dispose();}
        }

        private void VerifyGpuFaceBridge(Report report,Camera camera,Material material)
        {
            void Check(string name,bool ok,float value=0) => FrameworkCheck(report,"gpu-face-bridge-"+name,ok,value);
            var owner=Own(new GameObject("Generated CPU GPU corrected face bridge"));owner.layer=26;
            var model=owner.AddComponent<VL.FaceSystem.VLActorFaceModel>();var filter=owner.AddComponent<MeshFilter>();
            owner.AddComponent<MeshRenderer>().sharedMaterial=material;
            var mesh=Own(new Mesh());mesh.vertices=new[]{new Vector3(-.2f,-.2f,3),new Vector3(.2f,-.2f,3),new Vector3(0,.2f,3)};
            mesh.normals=new[]{Vector3.back,Vector3.back,Vector3.back};mesh.tangents=new[]{new Vector4(1,0,0,1),new Vector4(1,0,0,-1),new Vector4(1,0,0,1)};
            mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.up};mesh.triangles=new[]{0,2,1};filter.sharedMesh=mesh;
            var head=Own(new GameObject("Generated corrected head bone")).transform;
            model.bones=new[]{head};model.bindposes=new[]{Matrix4x4.identity};model.boneWeightAndIndices=new uint[12];
            for(int i=0;i<3;i++)model.boneWeightAndIndices[i*4]=65535u<<16;
            foreach(string name in new[]{"expression","side090","b_eye.eye_001"})model.blendShapes.Add(new VL.FaceSystem.VLFaceBlendShape{blendShapeName=name});
            for(int s=0;s<3;s++)model.blendShapes[s].blendShapeVertices.Add(new VL.FaceSystem.VLFaceBlendShapeVertex{vertIndex=s,position=new Vector3(.02f*(s+1),-.01f*s,-.015f)});
            var correction=owner.AddComponent<Campus.Common.CampusActorFaceCorrection>();correction.faceModel=model;
            correction.blendShapeIndices=new[]{0,1};correction.curves=new[]{AnimationCurve.Linear(0,0,90,1),AnimationCurve.Linear(0,0,90,1)};
            var face=owner.AddComponent<FaceExpressionRenderer>();Check("initialize",face.Initialize(model,model));
            face.SetAutomaticBlinkEnabled(false);face.InitializeGaze(null,null,camera);face.SetFaceCorrectionPoseTarget(head);
            Check("default-cpu-zero-gpu-buffers",!face.GpuDeformationEnabled&&!face.IsGpuDeformationActive&&face.GpuDeformationBufferBytes==0);
            for(int step=0;step<5;step++)
            {
                face.GpuDeformationEnabled=false;
                head.SetPositionAndRotation(new Vector3(.03f*step,.02f,0),Quaternion.Euler(0,step*7,2));
                model.SetWeight(0,.15f*step);model.SetWeight(2,step%2==0?.4f:0);face.ApplyCurrentWeights();
                var p=filter.sharedMesh.vertices;var n=filter.sharedMesh.normals;var t=filter.sharedMesh.tangents;
                float correctionWeight=face.ViewProfileWeight,blink=face.BlinkWeight;
                face.GpuDeformationEnabled=true;Check("step-"+step+"-active",face.IsGpuDeformationActive&&!face.CpuBoneDisplacementIsCurrent);
                var actual=ReadFaceGpu(filter.sharedMesh);float error=0;
                for(int v=0;v<3;v++)for(int c=0;c<3;c++){error=Mathf.Max(error,Mathf.Abs(actual[v][c]-p[v][c]));error=Mathf.Max(error,Mathf.Abs(actual[v][c+3]-n[v][c]));error=Mathf.Max(error,Mathf.Abs(actual[v][c+6]-t[v][c]));}
                Check("step-"+step+"-existing-cpu-equivalent",error<=.00002f,error);
                Check("step-"+step+"-correction-blink-preserved",face.ViewProfileWeight==correctionWeight&&face.BlinkWeight==blink);
            }
            // A failed current output must restore and recompute CPU geometry in the same call.
            var lost=filter.sharedMesh;DestroyImmediate(lost);face.ApplyCurrentWeights();
            Check("destroyed-output-current-cpu-fallback",!face.IsGpuDeformationActive&&face.GpuDeformationUnavailableReason!=null&&filter.sharedMesh!=null&&face.CpuBoneDisplacementIsCurrent);
            var fallback=filter.sharedMesh.vertices;face.GpuDeformationEnabled=false;face.ApplyCurrentWeights();
            bool exact=true;var restored=filter.sharedMesh.vertices;for(int v=0;v<3;v++)exact&=fallback[v]==restored[v];
            Check("fallback-equals-explicit-cpu",exact&&face.GpuDeformationBufferBytes==0);
            owner.SetActive(false);
        }

        private static float[][] ReadFaceGpu(Mesh mesh)
        {
            var data=new uint[mesh.vertexBufferCount][];
            for(int s=0;s<data.Length;s++)using(var b=mesh.GetVertexBuffer(s)){data[s]=new uint[mesh.vertexCount*mesh.GetVertexBufferStride(s)/4];b.GetData(data[s]);}
            var result=new float[mesh.vertexCount][];
            var attrs=new[]{VertexAttribute.Position,VertexAttribute.Normal,VertexAttribute.Tangent,VertexAttribute.TexCoord0};
            for(int v=0;v<result.Length;v++)
            {
                result[v]=new float[12];int output=0;
                for(int a=0;a<attrs.Length;a++)
                {
                    int stream=mesh.GetVertexAttributeStream(attrs[a]),offset=(v*mesh.GetVertexBufferStride(stream)+mesh.GetVertexAttributeOffset(attrs[a]))/4;
                    int count=a<2?3:(a==2?4:2);
                    for(int c=0;c<count;c++)result[v][output++]=BitConverter.ToSingle(BitConverter.GetBytes(data[stream][offset+c]),0);
                }
            }
            return result;
        }

        // Independent vertex loop, deliberately not CSR and not a call to the runtime CPU path.
        private static void FaceCpuReference(Vector3[] positions,Vector3[] normals,Vector4[] tangents,GpuFaceDeformer.Delta[] deltas,
            float[] weights,uint[] influences,Matrix4x4[] matrices,out Vector3[] output,out Vector3[] normal,out Vector4[] tangent)
        {
            output=(Vector3[])positions.Clone();normal=(Vector3[])normals.Clone();tangent=(Vector4[])tangents.Clone();
            var canonical=(Matrix4x4[])matrices.Clone();var moving=new bool[matrices.Length];
            for(int b=0;b<matrices.Length;b++)
            {
                for(int r=0;r<4;r++)for(int c=0;c<4;c++)if(Mathf.Abs(matrices[b][r,c]-(r==c?1:0))>1e-5f)moving[b]=true;
                if(!moving[b])canonical[b]=Matrix4x4.identity;
            }
            for(int v=0;v<positions.Length;v++)
            {
                var blended=positions[v];foreach(var d in deltas)if(d.vertex==v&&Mathf.Abs(weights[d.shape])>=.0001f)blended+=d.position*weights[d.shape];
                output[v]=blended;bool active=false;
                for(int k=0;k<4;k++){uint packed=influences[v*4+k];int index=(int)(packed&65535);if((packed>>16)>0&&index<moving.Length&&moving[index])active=true;}
                if(!active)continue;
                Vector3 p=Vector3.zero,n=Vector3.zero,t=Vector3.zero;float total=0;
                for(int k=0;k<4;k++)
                {
                    uint packed=influences[v*4+k];int index=(int)(packed&65535);float w=(packed>>16)*(1f/65535f);
                    if(index>=canonical.Length||w<=0)continue;
                    p+=canonical[index].MultiplyPoint3x4(blended)*w;n+=canonical[index].MultiplyVector(normals[v])*w;
                    t+=canonical[index].MultiplyVector((Vector3)tangents[v])*w;total+=w;
                }
                float inverse=1f/total;p*=inverse;n*=inverse;t*=inverse;
                output[v]=p;normal[v]=n.sqrMagnitude>0?n.normalized:normals[v];
                var direction=t.sqrMagnitude>0?t.normalized:Vector3.right;tangent[v]=new Vector4(direction.x,direction.y,direction.z,tangents[v].w);
            }
        }
    }
}
