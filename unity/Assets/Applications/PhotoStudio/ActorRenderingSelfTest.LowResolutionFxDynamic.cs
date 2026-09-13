using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyLowFxDynamics(Report report,Camera camera,RenderTexture source,RenderTexture depth,Mesh quad,Texture2D texture)
        {
            var fx=new LowResolutionFxRenderer(); var referenceRenderer=new LowResolutionFxRenderer();
            var originalTarget=camera.targetTexture; var cameraPosition=camera.transform.position; var cameraRotation=camera.transform.rotation;
            try
            {
                void Check(string name,bool ok,float difference=0)=>FrameworkCheck(report,"low-fx-dynamic-"+name,ok,difference);
                Color[] Render(LowResolutionFxSettings settings)
                { if(!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,out var frame))throw new InvalidOperationException(fx.UnavailableReason);return ReadSceneTarget(frame.color); }
                void Compare(string name,Color[] result,Color[] expected,double limit,double meanLimit)
                {
                    float maximum=0,alpha=0;double sum=0;
                    for(int i=0;i<result.Length;i++){alpha=Mathf.Max(alpha,Mathf.Abs(result[i].a-expected[i].a));for(int c=0;c<3;c++){float d=Mathf.Abs(result[i][c]-expected[i][c]);maximum=Mathf.Max(maximum,d);sum+=d;}}
                    double mean=sum/(result.Length*3);Check(name+"-whole-image",maximum<limit && mean<meanLimit && alpha==0,maximum);Check(name+"-mean-error",mean<meanLimit,(float)mean);
                }
                var input=ReadSceneTarget(source); var z=ReadSceneTarget(depth);
                var coloredQuad=Own(Instantiate(quad)); coloredQuad.colors=new[]{new Color(.7f,.5f,.9f,.6f),new Color(.7f,.5f,.9f,.6f),new Color(.7f,.5f,.9f,.6f),new Color(.7f,.5f,.9f,.6f)};
                var surface=new LowResolutionFxSurface{mesh=coloredQuad,localToWorld=Matrix4x4.TRS(new Vector3(.3f,-.1f,7),Quaternion.Euler(0,0,13),new Vector3(2,1.4f,1)),
                    linearRadiance=new Vector3(2,.7f,.3f),opacity=.65f,radialSoftness=.8f,texture=texture,vertexColor=true,resolution=FxResolution.Full};
                var settings=new LowResolutionFxSettings{enabled=true,surfaces=new[]{surface}};
                foreach(var blend in new[]{FxBlend.Alpha,FxBlend.Additive,FxBlend.Distortion})
                {
                    surface.blend=blend;surface.distortionOffset=new Vector2(.03f,-.017f);surface.distortionTextureScale=new Vector2(.15f,.08f);
                    foreach(var resolution in new[]{FxResolution.Full,FxResolution.Half,FxResolution.Quarter})
                    {
                        surface.resolution=resolution;var expected=LowFxTexturedPlaneReference(camera,surface,input,z,settings.depthBias);
                        Compare("vertex-map-"+blend+"-"+resolution,Render(settings),expected,resolution==FxResolution.Full?.0005:.08,resolution==FxResolution.Full?.0001:.007);
                    }
                }
                surface.blend=FxBlend.Alpha;surface.resolution=FxResolution.Full;surface.softIntersectionDistance=20;
                Compare("soft-depth-intersection",Render(settings),LowFxTexturedPlaneReference(camera,surface,input,z,settings.depthBias),.0005,.0001);surface.softIntersectionDistance=0;
                settings.fog.enabled=true;settings.fog.distance.enabled=true;settings.fog.distance.density=.12f;settings.fog.distance.startDistance=0;settings.fog.distance.endDistance=100;
                settings.fog.distance.linearColor=new Color(.11f,.17f,.29f,1);settings.fog.maximumOpacity=1;
                foreach(var blend in new[]{FxBlend.Alpha,FxBlend.Additive})
                { surface.blend=blend;Compare("per-surface-fog-"+blend,Render(settings),LowFxTexturedPlaneReference(camera,surface,input,z,settings.depthBias,settings),.0005,.0001); }
                settings.fog.enabled=false;surface.blend=FxBlend.Alpha;
                camera.transform.SetPositionAndRotation(new Vector3(.3f,.2f,-.5f),Quaternion.Euler(7,13,-4));
                surface.localToWorld=camera.transform.localToWorldMatrix*Matrix4x4.TRS(new Vector3(.3f,-.1f,7),Quaternion.Euler(0,0,13),new Vector3(2,1.4f,1));
                Compare("moved-rotated-view",Render(settings),LowFxTexturedPlaneReference(camera,surface,input,z,settings.depthBias),.0005,.0001);
                camera.transform.SetPositionAndRotation(cameraPosition,cameraRotation);

                // Independent current one-bone deformation versus explicit CPU-deformed
                // vertex data, not BakeMesh as the reference or a previous GPU frame.
                var skinObject=Own(new GameObject("FX current skinned surface"));skinObject.layer=25;skinObject.transform.position=new Vector3(0,0,7);
                var boneObject=Own(new GameObject("FX independent bone"));boneObject.transform.SetParent(skinObject.transform,false);
                var skinMesh=Own(Instantiate(quad));skinMesh.bindposes=new[]{Matrix4x4.identity};skinMesh.boneWeights=new[]{
                    new BoneWeight{boneIndex0=0,weight0=1},new BoneWeight{boneIndex0=0,weight0=1},new BoneWeight{boneIndex0=0,weight0=1},new BoneWeight{boneIndex0=0,weight0=1}};
                var skinned=skinObject.AddComponent<SkinnedMeshRenderer>();skinned.sharedMesh=skinMesh;skinned.bones=new[]{boneObject.transform};skinned.rootBone=boneObject.transform;
                skinned.updateWhenOffscreen=true;skinned.localBounds=new Bounds(Vector3.zero,Vector3.one*20);
                var callerMaterial=Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback")));skinned.sharedMaterial=callerMaterial;
                var explicitMesh=Own(Instantiate(quad));surface.mesh=null;surface.renderer=skinned;surface.vertexColor=false;surface.texture=null;surface.radialSoftness=.9f;
                surface.resolution=FxResolution.Half;surface.linearRadiance=new Vector3(1.3f,.4f,.1f);
                var cpuSurface=new LowResolutionFxSurface{mesh=explicitMesh,localToWorld=skinObject.transform.localToWorldMatrix,linearRadiance=surface.linearRadiance,opacity=surface.opacity,radialSoftness=surface.radialSoftness,resolution=surface.resolution};
                var cpuSettings=new LowResolutionFxSettings{enabled=true,surfaces=new[]{cpuSurface}};
                var cameraTarget=Own(new RenderTexture(113,79,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));cameraTarget.Create();camera.targetTexture=cameraTarget;
                Color[] skinFirst=null;
                for(int step=0;step<3;step++)
                {
                    float time=step==1?1.7f:.25f;boneObject.transform.localPosition=new Vector3(Mathf.Sin(time)*.7f,Mathf.Cos(time)*.2f,0);boneObject.transform.localRotation=Quaternion.Euler(0,0,time*17);
                    yield return null;camera.Render();var result=Render(settings);
                    var matrix=Matrix4x4.TRS(boneObject.transform.localPosition,boneObject.transform.localRotation,Vector3.one);var vertices=quad.vertices;
                    for(int i=0;i<vertices.Length;i++)vertices[i]=matrix.MultiplyPoint(vertices[i]);explicitMesh.vertices=vertices;explicitMesh.RecalculateBounds();
                    if(!referenceRenderer.TryRender(source,new FogVolumeDepth(depth),camera,cpuSettings,out var cpuFrame))throw new InvalidOperationException(referenceRenderer.UnavailableReason);
                    Compare("current-bone-"+step,result,ReadSceneTarget(cpuFrame.color),.0005,.0001);
                    if(step==0)skinFirst=result;else Check(step==1?"bone-motion-changes-image":"bone-seek-restores-image",step==1?!ScenePixelsEqual(skinFirst,result):ScenePixelsEqual(skinFirst,result));
                }
                Check("skinned-caller-material-preserved",skinned.sharedMaterial==callerMaterial);skinObject.SetActive(false);camera.targetTexture=originalTarget;

                // A single independently generated particle mesh, with ordered quads,
                // current vertex colors and deterministic seek. No prefab/assets needed.
                var particleMesh=Own(new Mesh{name="Self-authored ordered particle batch"});var particleSurface=new LowResolutionFxSurface{mesh=particleMesh,vertexColor=true,linearRadiance=Vector3.one,opacity=.18f,radialSoftness=.9f,resolution=FxResolution.Half};
                settings.surfaces=new[]{particleSurface};Color[] particleFirst=null;
                for(int step=0;step<3;step++)
                {
                    double time=step==1?1.6:.25;var vertices=new List<Vector3>();var uvs=new List<Vector2>();var colors=new List<Color>();var triangles=new List<int>();var expected=(Color[])input.Clone();
                    for(int particle=0;particle<64;particle++)
                    {
                        float px=(float)(Math.Sin(particle*2.17+time)*2.1),py=(float)(Math.Cos(particle*1.31+time*.7)*1.25),eye=9-particle*.035f;
                        var model=Matrix4x4.TRS(new Vector3(px,py,eye),Quaternion.Euler(0,0,particle*13),new Vector3(.45f,.35f,1));
                        var color=new Color(.3f+(particle%5)*.2f,.25f+(particle%3)*.3f,.2f+(particle%7)*.08f,.6f);
                        int offset=vertices.Count;foreach(var vertex in quad.vertices){vertices.Add(model.MultiplyPoint(vertex));colors.Add(color);}uvs.AddRange(quad.uv);foreach(var index in quad.triangles)triangles.Add(offset+index);
                        var analytical=new LowResolutionFxSurface{mesh=quad,localToWorld=model,linearRadiance=new Vector3(color.r,color.g,color.b),opacity=particleSurface.opacity*color.a,radialSoftness=particleSurface.radialSoftness};
                        expected=LowFxTexturedPlaneReference(camera,analytical,expected,z,settings.depthBias);
                    }
                    particleMesh.Clear();particleMesh.SetVertices(vertices);particleMesh.SetUVs(0,uvs);particleMesh.SetColors(colors);particleMesh.SetTriangles(triangles,0);particleMesh.RecalculateBounds();
                    var result=Render(settings);Compare("particle-mesh-time-"+step,result,expected,.08,.007);
                    if(step==0){particleFirst=result;SaveSsrPreview("low-fx-particle-mesh",result,113,79,false);}
                    else Check(step==1?"particle-motion-changes-image":"particle-seek-restores-image",step==1?!ScenePixelsEqual(particleFirst,result):ScenePixelsEqual(particleFirst,result));
                    Check("particle-single-batch-"+step,fx.BatchCount==1 && particleMesh.vertexCount==256);
                }
                particleSurface.resolution=FxResolution.Full;Render(settings);Check("unused-low-targets-released",fx.TargetCount==2);
                particleSurface.submesh=1;Check("invalid-submesh-rejected",!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _)&&fx.TargetCount==0);particleSurface.submesh=0;
                particleSurface.textureST.x=float.NaN;Check("invalid-uv-rejected",!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _));particleSurface.textureST.x=1;
                particleSurface.resolution=(FxResolution)3;Check("invalid-resolution-rejected",!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _));particleSurface.resolution=FxResolution.Half;
                Check("missing-camera-rejected",!fx.TryRender(source,new FogVolumeDepth(depth),null,settings,out _));
                Check("protection-input-alias-rejected",!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,out _,depth));
                Render(settings);fx.TryGetFrame(out var beforeDispose);fx.Dispose();Check("dispose-releases-current-output",!beforeDispose.IsCurrent&&!beforeDispose.color.IsCreated()&&fx.TargetCount==0);
            }
            finally
            { fx.Dispose();referenceRenderer.Dispose();camera.targetTexture=originalTarget;camera.transform.SetPositionAndRotation(cameraPosition,cameraRotation); }
        }
    }
}
