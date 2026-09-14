using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit full ActorToon shader inputs for command-buffer draws. Linear
    /// vectors, not ShaderLab Colors. No scene search, global writes or private assets.
    /// Renderers' property blocks retain Unity's normal precedence over these values.</summary>
    public sealed class ActorForwardParameters
    {
        private static readonly string[] Floats=(
            "_FaceDebugMode _CapturedDiffuseBlend _CapturedDirectScale _CapturedAmbientScale _CapturedActorTextureLodBias _CapturedActorOutputScale "+
            "_CapturedType4DebugStage _CapturedType4RampFlip _CapturedType4DiffuseF0 _CapturedType4DefinitionVisibility _CapturedType4OutputScale "+
            "_CapturedType1OutputScale _CapturedType1BodyOutputScale _CapturedType1HairOutputScale _CapturedType1DebugStage _CapturedType1DebugVariant "+
            "_CapturedType5OutputScale _UseCapturedDirectSpecular _UseCapturedReceiverNormal _UseExactViewRimBasis _UseCapturedAmbientSH _UseActorForwardAmbientSH "+
            "_ActorEnvironmentIntensity _CapturedSkinSaturation _CapturedEyeCubeTransformMode _CapturedActorCubeTransformMode _UseCapturedEnvironmentBasis "+
            "_UseCapturedEyeEnvironmentArray _UseCapturedActorEnvironmentArray _UseCapturedType1ActorEnvironmentArray _FaceDecalCount "+
            "_UseCapturedActorShadow _UseExactCapturedActorShadowMatrix _CapturedActorShadowStrength _CapturedActorShadowUseOffset _CapturedActorFacePartsShadowStrength").Split(' ');
        private static readonly string[] Vectors=(
            "_ActorMatcapParameters _ActorLightingScales _ActorKeyColor _ActorRimColor _ActorEyeHighlightColor _CapturedLightDirection _CapturedLightColor "+
            "_CapturedShadeTint _CapturedShadeAdditive _CapturedCameraUp _CapturedRimDirection _CapturedRimViewDirection _CapturedRimParameters "+
            "_CapturedSH0 _CapturedSH1 _CapturedSH2 _CapturedSH3 _CapturedSH4 _CapturedSH5 _CapturedSH6 _CapturedReflectionColor _CapturedEyeReflectionColor "+
            "_HeadDirection _HeadUpDirection _HeadRightDirection _HeadPosition _ActorFillDirection _ActorFillColor _ActorRimLightDirection _ActorRimLightColor "+
            "_CapturedActorShadowTexelSize _ActorOutlineParameters _ActorForwardSH0 _ActorForwardSH1 _ActorForwardSH2 _ActorForwardSH3 _ActorForwardSH4 _ActorForwardSH5 _ActorForwardSH6").Split(' ');
        private static readonly string[] ProbeNames={"unity_SHAr","unity_SHAg","unity_SHAb","unity_SHBr","unity_SHBg","unity_SHBb","unity_SHC"};
        private static readonly string[] Textures={"_ActorEnvironmentCube","_ActorEyeEnvironmentCube","_ActorEnvironmentArray","_ActorEyeEnvironmentArray","_CapturedActorShadowTex"};
        private static readonly string[] VectorArrays={"_ActorAdditionalPositions","_ActorAdditionalColors","_ActorAdditionalDirections","_ActorAdditionalSpots","_FaceDecalUvScaleBias","_FaceDecalFade"};
        private readonly Dictionary<string,float> floats=new Dictionary<string,float>();
        private readonly Dictionary<string,Vector4> vectors=new Dictionary<string,Vector4>();
        private readonly Dictionary<string,Texture> textures=new Dictionary<string,Texture>();
        private readonly Dictionary<string,Vector4[]> arrays=new Dictionary<string,Vector4[]>();
        private Matrix4x4 shadow=Matrix4x4.identity;
        private Matrix4x4[] decals=new Matrix4x4[8];
        private int additionalCount;

        public ActorForwardParameters()
        {
            foreach(var n in Floats)floats[n]=0;foreach(var n in Vectors)vectors[n]=Vector4.zero;
            foreach(var n in Textures)textures[n]=null;foreach(var n in VectorArrays)arrays[n]=new Vector4[8];
            foreach(var n in new[]{"_CapturedDiffuseBlend","_CapturedDirectScale","_CapturedAmbientScale","_CapturedActorOutputScale",
                "_CapturedType4RampFlip","_CapturedType4DiffuseF0","_CapturedType4DefinitionVisibility","_CapturedType4OutputScale",
                "_CapturedType1OutputScale","_CapturedType1BodyOutputScale","_CapturedType1HairOutputScale","_CapturedType5OutputScale",
                "_UseCapturedDirectSpecular","_CapturedActorShadowStrength","_CapturedActorFacePartsShadowStrength","_UseActorForwardAmbientSH"})floats[n]=1;
            foreach(var n in new[]{"_ActorKeyColor","_ActorEyeHighlightColor","_CapturedLightColor","_CapturedShadeTint","_CapturedReflectionColor","_CapturedEyeReflectionColor"})vectors[n]=Vector4.one;
            foreach(var n in new[]{"_CapturedRimDirection","_CapturedRimViewDirection","_HeadDirection","_ActorFillDirection","_ActorRimLightDirection"})vectors[n]=Vector3.forward;
            vectors["_CapturedCameraUp"]=vectors["_HeadUpDirection"]=Vector3.up;vectors["_HeadRightDirection"]=Vector3.left;
            vectors["_CapturedLightDirection"]=new Vector4(0,0,-1,1);vectors["_ActorMatcapParameters"]=new Vector4(.3f,1,1,0);
            vectors["_ActorLightingScales"]=new Vector4(0,1,1,0);vectors["_CapturedRimParameters"]=new Vector4(0,.85f,32,0);
            vectors["_ActorOutlineParameters"]=new Vector4(.04f,.12f,1f/3,1);
        }
        public void SetFloat(string name,float value)
        {
            if(name==null||!floats.ContainsKey(name)||!Finite(value))throw new ArgumentException(name);
            if(name=="_FaceDecalCount"&&(value<0||value>8||value!=Mathf.Floor(value)))throw new ArgumentOutOfRangeException(name);
            floats[name]=value;
        }
        public void SetVector(string name,Vector4 value) { if(!vectors.ContainsKey(name)||!Finite(value))throw new ArgumentException(name);vectors[name]=value; }
        public void SetTexture(string name,Texture value)
        {
            if(!textures.ContainsKey(name))throw new ArgumentException(name);
            if(value!=null && value.dimension!=(name.EndsWith("Cube")?TextureDimension.Cube:name.EndsWith("Array")?TextureDimension.Tex2DArray:TextureDimension.Tex2D))throw new ArgumentException(name);
            textures[name]=value;
        }
        public void SetVectorArray(string name,Vector4[] values)
        {
            if(!arrays.ContainsKey(name)||values==null||values.Length>8)throw new ArgumentException(name);
            var copy=new Vector4[8];for(int i=0;i<values.Length;i++){if(!Finite(values[i]))throw new ArgumentException(name);copy[i]=values[i];}arrays[name]=copy;
        }
        public void SetAdditionalLightCount(int count) { if(count<0||count>8)throw new ArgumentOutOfRangeException(nameof(count));additionalCount=count; }
        public void SetShadowMatrix(Matrix4x4 matrix) { if(!Finite(matrix))throw new ArgumentException(nameof(matrix));shadow=matrix; }
        public void SetFaceDecalMatrices(Matrix4x4[] matrices)
        { if(matrices==null||matrices.Length>8)throw new ArgumentException(nameof(matrices));var copy=new Matrix4x4[8];for(int i=0;i<matrices.Length;i++){if(!Finite(matrices[i]))throw new ArgumentException(nameof(matrices));copy[i]=matrices[i];}decals=copy; }
        public void SetAmbientProbe(SphericalHarmonicsL2 probe)
        {
            var packed=PackProbe(probe);for(int i=0;i<7;i++)vectors["_ActorForwardSH"+i]=packed[i];floats["_UseActorForwardAmbientSH"]=1;
        }
        /// <summary>Bind an explicit per-renderer probe to an owned full Actor material.
        /// Unity's engine-owned unity_SH* constant buffer ignores Material.SetVector;
        /// these separate material-local coefficients do not alter global probe state.</summary>
        public static void BindAmbientProbe(Material material,SphericalHarmonicsL2 probe)
        {
            if(material==null)throw new ArgumentNullException(nameof(material));var packed=PackProbe(probe);
            for(int i=0;i<7;i++)material.SetVector("_ActorForwardSH"+i,packed[i]);material.SetFloat("_UseActorForwardAmbientSH",1);
        }
        private static Vector4[] PackProbe(SphericalHarmonicsL2 probe)
        {
            var block=new MaterialPropertyBlock();block.CopySHCoefficientArraysFrom(new[]{probe});var values=new List<Vector4>();var packed=new Vector4[7];
            for(int i=0;i<7;i++){values.Clear();block.GetVectorArray(ProbeNames[i],values);packed[i]=values.Count>0?values[0]:Vector4.zero;if(!Finite(packed[i]))throw new ArgumentException("Nonfinite ambient probe");}
            return packed;
        }
        /// <summary>Explicit legacy-host bridge only. Snapshots current CPU Shader globals;
        /// does not infer queued command-buffer state or per-renderer probes. Texture content
        /// stays borrowed. New hosts can construct neutral inputs and configure them directly.</summary>
        public static ActorForwardParameters CaptureCurrentGlobals()
        {
            var p=new ActorForwardParameters();foreach(var n in Floats)if(n!="_UseActorForwardAmbientSH")p.SetFloat(n,Shader.GetGlobalFloat(n));
            // CPU globals cannot recover Unity's current per-renderer probe binding.
            // Keep the explicit neutral probe until the host supplies one.
            foreach(var n in Vectors)if(!n.StartsWith("_ActorForwardSH",StringComparison.Ordinal))p.SetVector(n,Shader.GetGlobalVector(n));
            foreach(var n in Textures)
            {
                var value=Shader.GetGlobalTexture(n);
                // The legacy host explicitly binds a 2D black placeholder while
                // array sampling is disabled. Normalize only that exact sentinel;
                // active arrays and all other wrong dimensions remain errors.
                bool inactive=n=="_ActorEnvironmentArray"?p.floats["_UseCapturedActorEnvironmentArray"]<=0&&p.floats["_UseCapturedType1ActorEnvironmentArray"]<=0:
                    n=="_ActorEyeEnvironmentArray"&&p.floats["_UseCapturedEyeEnvironmentArray"]<=0;
                if(inactive&&value==Texture2D.blackTexture)value=null;
                p.SetTexture(n,value);
            }
            foreach(var n in VectorArrays)p.SetVectorArray(n,Shader.GetGlobalVectorArray(n)??Array.Empty<Vector4>());
            p.SetAdditionalLightCount(Shader.GetGlobalInt("_ActorAdditionalLightCount"));p.SetShadowMatrix(Shader.GetGlobalMatrix("_CapturedActorWorldToShadow"));
            p.SetFaceDecalMatrices(Shader.GetGlobalMatrixArray("_FaceDecalWorldToDecal")??Array.Empty<Matrix4x4>());return p;
        }
        internal void Apply(Material material)
        {
            foreach(var p in floats)material.SetFloat(p.Key,p.Value);foreach(var p in vectors)material.SetVector(p.Key,p.Value);
            foreach(var p in textures)material.SetTexture(p.Key,p.Value);foreach(var p in arrays)material.SetVectorArray(p.Key,p.Value);
            material.SetInt("_ActorAdditionalLightCount",additionalCount);material.SetMatrix("_CapturedActorWorldToShadow",shadow);material.SetMatrixArray("_FaceDecalWorldToDecal",decals);
            // The full shader uses its explicit shadow source. Do not inherit an unrelated
            // Built-in ForwardBase screen-shadow variant in the SRP host.
            foreach(var keyword in new[]{"SHADOWS_SCREEN","SHADOWS_DEPTH","SHADOWS_CUBE","LIGHTMAP_ON","DIRLIGHTMAP_COMBINED","DYNAMICLIGHTMAP_ON","SHADOWS_SHADOWMASK","LIGHTMAP_SHADOW_MIXING","VERTEXLIGHT_ON"})material.DisableKeyword(keyword);
        }
        internal static IEnumerable<string> TextureNames=>Textures;
        internal static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v)&&Mathf.Abs(v)<=1e12f;
        internal static bool Finite(Vector4 v)=>Finite(v.x)&&Finite(v.y)&&Finite(v.z)&&Finite(v.w);
        internal static bool Finite(Matrix4x4 v) { for(int i=0;i<16;i++)if(!Finite(v[i]))return false;return true; }
    }
}
