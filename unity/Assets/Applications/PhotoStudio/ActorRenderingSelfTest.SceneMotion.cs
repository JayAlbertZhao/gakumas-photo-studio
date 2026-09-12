using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        [Serializable] private sealed class MotionDiagnostic
        {
            public string name;
            public Matrix4x4 rendererMatrix,transformMatrix,bone0,bone1,view,projection;
            public Vector3[] source,expectedWorld,expectedNormals;
        }
        private IEnumerator VerifySceneMotion(Report report)
        {
            yield return null;
            var previous=FindObjectsOfType<Renderer>();var forced=new bool[previous.Length];
            for(int i=0;i<previous.Length;i++){forced[i]=previous[i].forceRenderingOff;previous[i].forceRenderingOff=true;}
            try
            {
                void Check(string n,bool ok,float e=0)=>FrameworkCheck(report,"scene-motion-"+n,ok,e);
                var host=Own(new GameObject("Scene motion host"));var camera=host.AddComponent<Camera>();camera.enabled=false;
                camera.allowHDR=true;camera.allowMSAA=false;camera.renderingPath=RenderingPath.Forward;camera.cullingMask=1<<26;
                camera.transform.position=new Vector3(0,0,-4);camera.orthographic=true;camera.orthographicSize=1.4f;camera.aspect=1;
                camera.nearClipPlane=.1f;camera.farClipPlane=40;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                RenderTexture Target(int w,int h){var rt=Own(new RenderTexture(w,h,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));rt.Create();return rt;}
                var target=Target(97,97);camera.targetTexture=target;
                var stage=host.AddComponent<SceneDeferredCamera>();stage.sceneEnabled=true;stage.sceneLayers=1<<25;stage.lightRadiance=Vector3.zero;stage.ambientIrradiance=Vector3.one;
                var plane=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));plane.layer=25;plane.transform.localScale=Vector3.one*1.8f;
                var mesh=Own(Instantiate(plane.GetComponent<MeshFilter>().sharedMesh));plane.GetComponent<MeshFilter>().sharedMesh=mesh;
                var renderer=plane.GetComponent<Renderer>();var surface=new SceneDeferredCamera.Surface{renderer=renderer,cull=CullMode.Off};surface.inputs.albedo=Vector3.one;surface.inputs.mos=new Vector3(0,1,0);stage.surfaces=new[]{surface};
                SceneDeferredCamera.Frame Render(){camera.Render();if(!stage.TryGetFrame(out var f))throw new InvalidOperationException(stage.UnavailableReason);return f;}
                var disabled=Render();var oldImage=ReadSceneTarget(target);Check("default-no-targets-or-snapshots",!stage.motion.enabled&&disabled.motionVectors==null&&stage.MotionTargetCount==0&&stage.MotionTrackedVertices==0);
                stage.motion.enabled=true;
                Vector3[] oldWorld=null,oldNormals=null;Matrix4x4 oldView=default,oldVp=default;int[] triangles=mesh.triangles;
                SkinnedMeshRenderer skin=null;Transform bone0=null,bone1=null;
                Vector3[] WorldVertices(out Vector3[] normals)
                {
                    var positions=mesh.vertices;normals=mesh.normals;var m=surface.renderer.localToWorldMatrix;
                    for(int i=0;i<positions.Length;i++)
                    {
                        if(skin!=null&&surface.renderer==skin)
                        {
                            if(mesh.blendShapeCount>0)
                            {
                                var delta=new Vector3[positions.Length];mesh.GetBlendShapeFrameVertices(0,0,delta,null,null);
                                positions[i]+=delta[i]*(skin.GetBlendShapeWeight(0)/100);
                            }
                            var weights=mesh.boneWeights[i];var bind=mesh.bindposes;
                            var b0=bone0.localToWorldMatrix*bind[0];var b1=bone1.localToWorldMatrix*bind[1];
                            var world=b0.MultiplyPoint(positions[i])*weights.weight0+b1.MultiplyPoint(positions[i])*weights.weight1;
                            // Unity's skin stream blends forward-transformed normals. The draw
                            // matrix is root-bone TR (unit scale), not Renderer.localToWorldMatrix.
                            var normal=b0.MultiplyVector(normals[i])*weights.weight0+b1.MultiplyVector(normals[i])*weights.weight1;
                            var root=Matrix4x4.TRS(skin.rootBone.position,skin.rootBone.rotation,Vector3.one);
                            positions[i]=root.MultiplyPoint(Vector3.Scale(root.inverse.MultiplyPoint(world),surface.vertexScale));
                            normal=root.inverse.MultiplyVector(normal);
                            normals[i]=root.MultiplyVector(new Vector3(normal.x/surface.vertexScale.x,normal.y/surface.vertexScale.y,normal.z/surface.vertexScale.z)).normalized;
                            continue;
                        }
                        positions[i]=m.MultiplyPoint(Vector3.Scale(positions[i],surface.vertexScale));var n=normals[i];
                        normals[i]=m.inverse.transpose.MultiplyVector(new Vector3(n.x/surface.vertexScale.x,n.y/surface.vertexScale.y,n.z/surface.vertexScale.z)).normalized;
                    }
                    return positions;
                }
                Color[] Case(string name,bool valid=true,bool requireMotion=false)
                {
                    var world=WorldVertices(out var normals);
                    if(skin!=null&&surface.renderer==skin)
                    {
                        var diagnostic=new MotionDiagnostic{name=name,rendererMatrix=skin.localToWorldMatrix,transformMatrix=skin.transform.localToWorldMatrix,
                            bone0=bone0.localToWorldMatrix,bone1=bone1.localToWorldMatrix,view=camera.worldToCameraMatrix,projection=camera.projectionMatrix,
                            source=mesh.vertices,expectedWorld=world,expectedNormals=normals};
                        System.IO.File.WriteAllText(System.IO.Path.Combine(_directory,"scene-motion-diagnostic-"+name+".json"),JsonUtility.ToJson(diagnostic,true));
                    }
                    var f=Render();var motion=ReadSceneTarget(f.motionVectors);var prior=ReadSceneTarget(f.previousNormalIdentity);
                    int width=camera.targetTexture.width,height=camera.targetTexture.height,tested=0,moving=0;float error=0,normalError=0;bool coverage=true;
                    for(int y=0;y<height;y+=2)for(int x=0;x<width;x+=2)
                    {
                        int index=y*width+x;var actual=motion[index];var data=prior[index];var ray=camera.ViewportPointToRay(new Vector3((x+.5f)/width,(y+.5f)/height));
                        int hit=-1;Vector3 bary=Vector3.zero;float nearest=float.PositiveInfinity;
                        for(int t=0;t<triangles.Length;t+=3)
                        {
                            Vector3 a=world[triangles[t]],e1=world[triangles[t+1]]-a,e2=world[triangles[t+2]]-a;var p=Vector3.Cross(ray.direction,e2);float det=Vector3.Dot(e1,p);if(Mathf.Abs(det)<1e-7f)continue;
                            var s=ray.origin-a;float u=Vector3.Dot(s,p)/det;var q=Vector3.Cross(s,e1);float v=Vector3.Dot(ray.direction,q)/det,dist=Vector3.Dot(e2,q)/det;
                            if(u>=0&&v>=0&&u+v<=1&&dist>=0&&dist<nearest){hit=t;bary=new Vector3(1-u-v,u,v);nearest=dist;}
                        }
                        bool expectedCovered=hit>=0;if(expectedCovered){var point=ray.GetPoint(nearest);float depth=-camera.worldToCameraMatrix.MultiplyPoint(point).z;expectedCovered=depth>=camera.nearClipPlane&&depth<=camera.farClipPlane;}
                        coverage&=(data.a>0)==expectedCovered;
                        if(data.a<=0){coverage&=actual==Color.clear;continue;}
                        if(!expectedCovered)continue;tested++;
                        if(!valid){error=Mathf.Max(error,Mathf.Abs(actual.r),Mathf.Abs(actual.g),Mathf.Abs(actual.b),Mathf.Abs(actual.a));normalError=Mathf.Max(normalError,new Vector3(data.r,data.g,data.b).magnitude);continue;}
                        // D3D11 rasterizer evaluates interpolation on its 8-bit subpixel grid.
                        // A ray/triangle hit against unsnapped vertices biases even stationary
                        // motion by up to half a subpixel; preserve the numerical tolerance.
                        if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Direct3D11)
                        {
                            var projected=new Vector3[3];var currentVp=camera.projectionMatrix*camera.worldToCameraMatrix;
                            for(int k=0;k<3;k++)
                            {
                                var v=world[triangles[hit+k]];var c=currentVp*new Vector4(v.x,v.y,v.z,1);
                                projected[k]=new Vector3(Mathf.Round((c.x/c.w*.5f+.5f)*width*256)/256,
                                    Mathf.Round((c.y/c.w*.5f+.5f)*height*256)/256,c.w);
                            }
                            var a=projected[0];var b=projected[1];var c0=projected[2];float px=x+.5f,py=y+.5f;
                            float det=(b.y-c0.y)*(a.x-c0.x)+(c0.x-b.x)*(a.y-c0.y);
                            float u=((b.y-c0.y)*(px-c0.x)+(c0.x-b.x)*(py-c0.y))/det;
                            float v0=((c0.y-a.y)*(px-c0.x)+(a.x-c0.x)*(py-c0.y))/det;
                            bary=new Vector3(u/a.z,v0/b.z,(1-u-v0)/c0.z);bary/=bary.x+bary.y+bary.z;
                        }
                        Vector3 previousPoint=Vector3.zero,previousNormal=Vector3.zero;
                        for(int k=0;k<3;k++){int j=triangles[hit+k];previousPoint+=oldWorld[j]*bary[k];previousNormal+=oldNormals[j]*bary[k];}
                        previousNormal.Normalize();var clip=oldVp*new Vector4(previousPoint.x,previousPoint.y,previousPoint.z,1);float depthOld=-oldView.MultiplyPoint(previousPoint).z;
                        var expected=new Vector4((x+.5f)/width-(clip.x/clip.w*.5f+.5f),(y+.5f)/height-(clip.y/clip.w*.5f+.5f),depthOld,1);
                        if(clip.w<=1e-6||depthOld<=0)expected=Vector4.zero;
                        for(int k=0;k<4;k++)error=Mathf.Max(error,Mathf.Abs(actual[k]-expected[k]));
                        if(expected.w>0)normalError=Mathf.Max(normalError,Vector3.Distance(new Vector3(data.r,data.g,data.b),previousNormal));
                        if(Mathf.Abs(actual.r)+Mathf.Abs(actual.g)>.002f)moving++;
                    }
                    Check(name+"-actual-current-raster-coverage",coverage&&tested>60,tested);
                    Check(name+"-previous-projection-and-depth-oracle",error<.00005f&&(!requireMotion||moving>40),error);
                    Check(name+"-previous-world-normal-oracle",normalError<.0001f,normalError);
                    Check(name+"-owned-targets-and-snapshot-budget",stage.MotionTargetCount==2&&stage.MotionDrawCalls==1&&stage.MotionTrackedVertices==mesh.vertexCount&&stage.MotionSnapshotTargetCount==2&&stage.MotionSnapshotDrawCalls==1&&stage.MotionHistoryAvailable);
                    oldWorld=world;oldNormals=normals;oldView=camera.worldToCameraMatrix;oldVp=camera.projectionMatrix*oldView;return motion;
                }
                Case("first-sample-invalid",false);Check("enabled-motion-keeps-hdr-exact",ScenePixelsEqual(oldImage,ReadSceneTarget(target)));
                Case("stationary");plane.transform.position=new Vector3(.21f,-.13f,.1f);Case("rigid-translation",true,true);
                plane.transform.rotation=Quaternion.Euler(12,21,14);Case("rigid-rotation",true,true);
                surface.vertexScale=new Vector3(.85f,1.12f,1.2f);Case("vertex-scale",true,true);
                camera.transform.position+=new Vector3(.12f,.08f,-.1f);Case("camera-translation",true,true);
                camera.transform.rotation=Quaternion.Euler(3,-4,1);Case("camera-rotation",true,true);
                camera.orthographic=false;camera.fieldOfView=48;Case("projection-change",true,true);
                var p=camera.projectionMatrix;p.m02=.11f;p.m12=-.07f;p.m01=.05f;camera.projectionMatrix=p;Case("off-axis-projection",true,true);
                camera.ResetProjectionMatrix();camera.orthographic=true;camera.transform.SetPositionAndRotation(new Vector3(0,0,-4),Quaternion.identity);
                plane.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);surface.vertexScale=Vector3.one;stage.ResetMotionHistory();Case("explicit-reset-invalid",false);Case("reset-reseed");
                var oldFrame=Render();stage.ResetMotionHistory();Check("reset-invalidates-borrowed-frame",!oldFrame.IsCurrent&&!stage.TryGetFrame(out _));Case("same-frame-reset",false);
                camera.transform.position=new Vector3(1.1f,0,-4);Case("camera-cut-invalid",false);camera.transform.position=new Vector3(0,0,-4);Case("camera-cut-return-invalid",false);Case("camera-cut-reseed");
                surface.motionRevision++;Case("caller-revision-invalid",false);Case("revision-reseed");
                var originalVertices=mesh.vertices;var deformed=mesh.vertices;deformed[2]+=new Vector3(-.08f,.1f,-.12f);mesh.vertices=deformed;mesh.RecalculateBounds();Case("mutable-vertex-deformation",true,true);
                mesh.vertices=originalVertices;mesh.RecalculateBounds();Case("vertex-return",true,true);
                var originalIndices=mesh.triangles;var changedIndices=(int[])originalIndices.Clone();int tmp=changedIndices[0];changedIndices[0]=changedIndices[1];changedIndices[1]=tmp;mesh.triangles=changedIndices;triangles=changedIndices;Case("topology-change-invalid",false);
                mesh.triangles=originalIndices;triangles=originalIndices;Case("topology-return-invalid",false);Case("topology-reseed");
                var alpha=Own(new Texture2D(2,2,TextureFormat.RGBA32,false,true));alpha.SetPixels(new[]{Color.white,Color.white,Color.white,Color.white});alpha.Apply();surface.inputs.albedoMap=alpha;surface.alphaCutoff=.5f;Case("alpha-source-change-invalid",false);Case("alpha-stable");alpha.SetPixel(0,0,Color.white);alpha.Apply();Case("alpha-revision-invalid",false);
                alpha.filterMode=FilterMode.Point;alpha.SetPixels(new[]{Color.clear,Color.white,Color.white,Color.clear});alpha.Apply();Render();
                var cutout=Render();var cutMotion=ReadSceneTarget(cutout.motionVectors);var cutIdentity=ReadSceneTarget(cutout.previousNormalIdentity);var cutCoverage=ReadSceneTarget(cutout.albedoCoverage);
                bool cutMatch=true;int cutCovered=0,cutClear=0;
                for(int i=0;i<cutMotion.Length;i++){bool covered=cutCoverage[i].a>.5f;cutMatch&=(cutMotion[i].a>.5f)==covered&&(cutIdentity[i].a>0)==covered;if(covered)cutCovered++;else{cutClear++;cutMatch&=cutMotion[i]==Color.clear&&cutIdentity[i]==Color.clear;}}
                Check("alpha-holes-match-actual-scene-and-clear-history",cutMatch&&cutCovered>500&&cutCovered<3000&&cutClear>500,cutCovered);
                surface.inputs.albedoMap=null;surface.alphaCutoff=0;Case("alpha-removed-invalid",false);Case("alpha-reseed");
                var quadMesh=mesh;var baseMesh=Own(new Mesh());var basePositions=new Vector3[8];var baseNormals=new Vector3[8];var baseUv=new Vector2[8];
                for(int i=0;i<8;i++){basePositions[i]=mesh.vertices[i%4];baseNormals[i]=mesh.normals[i%4];baseUv[i]=mesh.uv[i%4];}
                baseMesh.vertices=basePositions;baseMesh.normals=baseNormals;baseMesh.uv=baseUv;baseMesh.subMeshCount=2;baseMesh.SetIndices(originalIndices,MeshTopology.Triangles,0);baseMesh.SetIndices(originalIndices,MeshTopology.Triangles,1,true,4);
                var originalMaterials=renderer.sharedMaterials;renderer.sharedMaterials=new[]{originalMaterials[0],originalMaterials[0]};plane.GetComponent<MeshFilter>().sharedMesh=mesh=baseMesh;surface.materialIndex=1;triangles=mesh.GetIndices(1);
                Case("submesh-base-vertex-first",false);plane.transform.position+=new Vector3(.06f,-.04f,.02f);Case("submesh-base-vertex-correspondence",true,true);
                plane.GetComponent<MeshFilter>().sharedMesh=mesh=quadMesh;renderer.sharedMaterials=originalMaterials;surface.materialIndex=0;triangles=originalIndices;plane.transform.position=Vector3.zero;Case("submesh-return-invalid",false);Case("submesh-return-reseed");
                var second=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));second.layer=25;second.transform.localScale=Vector3.one*.8f;second.transform.position=new Vector3(-.65f,.03f,-.15f);
                var secondSurface=new SceneDeferredCamera.Surface{renderer=second.GetComponent<Renderer>(),cull=CullMode.Off};stage.surfaces=new[]{surface,secondSurface};Render();
                var idsBefore=ReadSceneTarget(Render().previousNormalIdentity);stage.surfaces=new[]{secondSurface,surface};var idsAfter=ReadSceneTarget(Render().previousNormalIdentity);
                var ids=new System.Collections.Generic.HashSet<float>();bool identityStable=true;
                for(int i=0;i<idsBefore.Length;i++){identityStable&=idsBefore[i].a==idsAfter[i].a;if(idsAfter[i].a>0)ids.Add(idsAfter[i].a);}
                Check("surface-reorder-preserves-two-visible-identities",identityStable&&ids.Count==2&&stage.MotionSnapshotTargetCount==4&&stage.MotionSnapshotDrawCalls==2);
                stage.surfaces=new[]{surface};second.SetActive(false);Render();Check("removed-surface-releases-only-own-snapshots",stage.MotionSnapshotTargetCount==2);Case("surface-reorder-return");
                var parent=Own(new GameObject("Motion shear parent"));parent.transform.localScale=new Vector3(1.2f,.8f,1.1f);parent.transform.rotation=Quaternion.Euler(7,13,5);plane.transform.SetParent(parent.transform,false);plane.transform.localRotation=Quaternion.Euler(9,-11,12);
                Case("static-nonuniform-root-shear",true,true);plane.transform.SetParent(null,false);plane.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);Case("static-parent-return",true,true);

                var skinHost=Own(new GameObject("Motion two-bone receiver"));skinHost.layer=25;
                bone0=Own(new GameObject("Motion bone0")).transform;bone0.SetParent(skinHost.transform,false);
                bone1=Own(new GameObject("Motion bone1")).transform;bone1.SetParent(skinHost.transform,false);
                skin=skinHost.AddComponent<SkinnedMeshRenderer>();mesh=Own(Instantiate(mesh));var weights=new BoneWeight[mesh.vertexCount];
                for(int i=0;i<weights.Length;i++){float t=(mesh.vertices[i].x+.5f)*.6f;weights[i]=new BoneWeight{boneIndex0=0,weight0=1-t,boneIndex1=1,weight1=t};}
                var blendDelta=new Vector3[mesh.vertexCount];for(int i=0;i<blendDelta.Length;i++)blendDelta[i]=new Vector3(.02f*i,.013f*i,-.018f*i);mesh.AddBlendShapeFrame("Motion synthetic deformation",100,blendDelta,new Vector3[blendDelta.Length],new Vector3[blendDelta.Length]);
                mesh.boneWeights=weights;mesh.bindposes=new[]{Matrix4x4.identity,Matrix4x4.identity};skin.sharedMesh=mesh;skin.bones=new[]{bone0,bone1};skin.rootBone=bone0;skin.sharedMaterial=renderer.sharedMaterial;skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*10);
                skinHost.transform.localScale=Vector3.one*1.8f;surface.renderer=skin;plane.SetActive(false);yield return null;Case("skin-first-invalid",false);Case("skin-stationary");
                for(int pose=1;pose<=3;pose++)
                {
                    bone0.localPosition=new Vector3(-.015f*pose,.02f*pose,0);bone0.localRotation=Quaternion.Euler(2*pose,3*pose,-2*pose);
                    bone1.localPosition=new Vector3(.025f*pose,-.01f*pose,-.015f*pose);bone1.localRotation=Quaternion.Euler(-3*pose,4*pose,3*pose);
                    yield return null;yield return null;Case("two-bone-deform-"+pose,true,true);
                }
                skinHost.transform.localScale=new Vector3(1.5f,1.9f,1.2f);yield return null;yield return null;Case("skin-nonuniform-root",true,true);
                skinHost.transform.SetParent(parent.transform,false);skinHost.transform.localRotation=Quaternion.Euler(8,5,-6);yield return null;yield return null;Case("skin-parent-shear",true,true);
                skin.SetBlendShapeWeight(0,65);yield return null;yield return null;Case("skin-blendshape-deformation",true,true);
                surface.vertexScale=new Vector3(.87f,1.04f,1.1f);Case("skin-shader-vertex-scale",true,true);Case("skin-scaled-stationary");
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_SCENE_MOTION")=="1")
                {
                    bone1.localPosition+=new Vector3(.04f,.03f,-.02f);yield return null;yield return null;
                    bool started=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try{if(started){Case("native-prior",true,true);camera.transform.position+=new Vector3(.07f,.02f,-.02f);Case("native-current",true,true);}}
                    finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("requested-native-capture",started&&ended);
                }
                var f0=Render();var first=ReadSceneTarget(f0.motionVectors);var other=Own(new GameObject("Other scene motion camera"));var camera2=other.AddComponent<Camera>();camera2.CopyFrom(camera);camera2.enabled=false;camera2.transform.SetPositionAndRotation(camera.transform.position,camera.transform.rotation);camera2.targetTexture=Target(65,49);
                var stage2=other.AddComponent<SceneDeferredCamera>();stage2.sceneEnabled=true;stage2.sceneLayers=stage.sceneLayers;stage2.surfaces=stage.surfaces;stage2.motion.enabled=true;camera2.Render();
                Check("two-camera-independent-snapshots",stage2.TryGetFrame(out var f2)&&f2.motionVectors!=f0.motionVectors&&!stage2.MotionContinuous&&f0.IsCurrent&&ScenePixelsEqual(first,ReadSceneTarget(f0.motionVectors)));other.SetActive(false);camera2.targetTexture=null;
                foreach(bool normal in new[]{false,true}){var old=Render();(normal?old.previousNormalIdentity:old.motionVectors).Release();Check("lost-target-invalidates-"+normal,!old.IsCurrent);var fresh=Render();Check("lost-target-reseed-"+normal,fresh.IsCurrent&&!stage.MotionContinuous&&stage.MotionTargetCount==2);}
                camera.targetTexture=Target(65,49);var resized=Render();Check("odd-resize-reseeds",!stage.MotionContinuous&&resized.motionVectors.width==65&&resized.motionVectors.height==49);camera.targetTexture=target;
                surface.renderer.enabled=false;Render();Check("hidden-surface-releases-snapshot",stage.MotionDrawCalls==0&&stage.MotionTrackedVertices==0&&stage.MotionSnapshotTargetCount==0);surface.renderer.enabled=true;
                bool noHistory=true;foreach(var c in ReadSceneTarget(Render().motionVectors))noHistory&=c==Color.clear;Check("reactivated-surface-invalid-history",noHistory);
                void Reject(string name){camera.Render();Check(name,!stage.TryGetFrame(out _)&&stage.MotionTargetCount==0&&stage.MotionTrackedVertices==0&&!string.IsNullOrEmpty(stage.UnavailableReason));}
                stage.motion.maximumTrackedVertices=1;Reject("vertex-budget-rejected");stage.motion.maximumTrackedVertices=1000000;
                stage.motion.cameraCutDistance=float.NaN;Reject("nan-cut-distance-rejected");stage.motion.cameraCutDistance=1;
                stage.motion.cameraCutAngle=181;Reject("invalid-cut-angle-rejected");stage.motion.cameraCutAngle=30;
                stage.motion.enabled=false;stage.motion.maximumTrackedVertices=0;Check("disabled-invalid-motion-ignored",Render().motionVectors==null&&stage.MotionSnapshotTargetCount==0);stage.motion.maximumTrackedVertices=1000000;stage.motion.enabled=true;
                var end=Render();stage.enabled=false;Check("disable-releases-targets-and-snapshots",!end.IsCurrent&&!end.motionVectors.IsCreated()&&!end.previousNormalIdentity.IsCreated()&&stage.MotionSnapshotTargetCount==0);
                camera.targetTexture=null;host.SetActive(false);skinHost.SetActive(false);
            }
            finally
            {
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
        }
    }
}
