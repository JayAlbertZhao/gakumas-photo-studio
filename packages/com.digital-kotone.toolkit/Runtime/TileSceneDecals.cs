using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    // In-place channel blending; no DBuffer or full-screen GBuffer ping-pong.
    internal static class TileSceneDecals
    {
        private static bool Active(SceneDeferredCamera.Decal d) => d!=null && d.enabled &&
            (d.albedoWeight!=0||d.normalWeight!=0||d.emissionWeight!=0||d.mosWeight.x!=0||d.mosWeight.y!=0||d.mosWeight.z!=0);
        internal static int Count(SceneDeferredCamera.Decal[] values)
        { int count=0;if(values!=null)foreach(var d in values)if(Active(d))count++;return count; }
        internal static string Validate(SceneDeferredCamera.Decal[] values)
        {
            if(values==null||values.Length>256)return "Tile decals require an explicit collection of at most 256 projectors";
            bool Unit(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x)&&x>=0&&x<=1;
            foreach(var d in values)
            {
                if(!Active(d))continue;
                if(!SceneDeferredCamera.Inputs(d.inputs)||!SceneDeferredCamera.Matrix(d.localToWorld)||
                    d.localToWorld.m30!=0||d.localToWorld.m31!=0||d.localToWorld.m32!=0||d.localToWorld.m33!=1||
                    d.receiverGroup<1||d.receiverGroup>255||!Unit(d.albedoWeight)||!Unit(d.normalWeight)||!Unit(d.emissionWeight)||
                    !Unit(d.mosWeight.x)||!Unit(d.mosWeight.y)||!Unit(d.mosWeight.z)||!Unit(d.height)||
                    !HdrMonitor.Range(d.heightFade,.0001f,1e6f)||!HdrMonitor.Range(d.minimumFacing,-1,1))return "Invalid tile decal projector/material/weights";
                foreach(var t in new[]{d.inputs.albedoMap,d.inputs.normalMap,d.inputs.mosMap,d.inputs.emissionMap,d.heightMap})
                    if(t!=null&&(t.dimension!=TextureDimension.Tex2D||(t is RenderTexture rt&&(!rt.IsCreated()||rt.antiAliasing!=1))))
                        return "Invalid tile decal texture";
            }
            return null;
        }
        internal static TileRenderPass.Draw[] Prepare(SceneDeferredCamera.Decal[] decals,RenderTexture depth,RenderTexture normals,
            Matrix4x4 view,Matrix4x4 vp,Mesh quad,TileSceneRenderer.PreparedFrame owner)
        {
            var shader=Resources.Load<Shader>("TileSceneDecal");var draws=new List<TileRenderPass.Draw>();
            foreach(var d in decals)
            {
                if(!Active(d))continue;
                var m=new Material(shader) { name="Toolkit tile material projector",hideFlags=HideFlags.HideAndDontSave };owner.Add(m);
                SceneDeferredCamera.BindInputs(m,d.inputs);m.SetTexture("_SceneEyeDepth",depth);m.SetTexture("_GeometryDepthId",normals);
                m.SetMatrix("_InverseViewProjection",vp.inverse);m.SetMatrix("_SceneView",view);
                m.SetMatrix("_WorldToDecal",d.localToWorld.inverse);m.SetVector("_DecalTangent",d.localToWorld.GetColumn(0));
                m.SetVector("_DecalBitangent",d.localToWorld.GetColumn(1));m.SetVector("_DecalFacing",d.localToWorld.inverse.transpose.MultiplyVector(Vector3.back).normalized);
                m.SetFloat("_ReceiverGroup",d.receiverGroup);m.SetVector("_Weights",new Vector4(d.albedoWeight,d.normalWeight,d.emissionWeight,d.heightOcclusion?1:0));
                m.SetVector("_MosWeight",d.mosWeight);m.SetTexture("_HeightMap",d.heightMap!=null?d.heightMap:Texture2D.whiteTexture);
                m.SetVector("_HeightParameters",new Vector4(d.height,d.heightFade,d.minimumFacing,0));
                // Separate MOS component weights require independent scalar blend factors.
                // Append to the geometry subpass: fixed-function attachment access is
                // rasterization-ordered, without a new cross-subpass blend dependency.
                // Preserve every alpha field and disable writes to the fifth (GI) target.
                for(int pass=1;pass<=3;pass++)draws.Add(new TileRenderPass.Draw { mesh=quad,material=m,shaderPass=pass });
            }
            return draws.ToArray();
        }
    }
}
