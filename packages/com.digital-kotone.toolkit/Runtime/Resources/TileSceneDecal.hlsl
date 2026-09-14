// Independent projector math. Inputs were completed before this render pass;
// fixed-function blending accesses current MRT destinations without shader feedback.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
Texture2D<float> _SceneEyeDepth;
Texture2D<float4> _GeometryDepthId;
Texture2D _AlbedoMap,_NormalMap,_MosMap,_EmissionMap,_HeightMap;
SamplerState sampler_AlbedoMap,sampler_NormalMap,sampler_MosMap,sampler_EmissionMap,sampler_HeightMap;
float4x4 _InverseViewProjection,_SceneView,_WorldToDecal;
float4 _UvST,_Weights,_HeightParameters;
float3 _Albedo,_Mos,_Emission,_MosWeight,_DecalTangent,_DecalBitangent,_DecalFacing;
float _Alpha,_HasNormal;
struct Screen { float4 position:SV_POSITION;float2 xy:TEXCOORD0; };
Screen Fullscreen(float4 v:POSITION) { Screen o;o.position=float4(v.xy,0,1);o.xy=v.xy;return o; }
float3 DecalNormal(float3 n) { return n*rsqrt(max(dot(n,n),1e-12)); }
struct DecalOutput { float4 base:SV_Target0;float4 mos:SV_Target1;float4 normal:SV_Target2;float4 emission:SV_Target3; };
DecalOutput MaterialDecal(Screen i)
{
    int3 pixel=int3((int2)i.position.xy,0);float depth=_SceneEyeDepth.Load(pixel);clip(depth-1e-8);
    float4 a=mul(_InverseViewProjection,float4(i.xy,0,1)),b=mul(_InverseViewProjection,float4(i.xy,1,1));a/=a.w;b/=b.w;
    float da=-mul(_SceneView,a).z,db=-mul(_SceneView,b).z;float3 world=lerp(a.xyz,b.xyz,(depth-da)/(db-da));
    float3 box=mul(_WorldToDecal,float4(world,1)).xyz;clip(.5-abs(box));
    float3 n=DecalNormal(_GeometryDepthId.Load(pixel).rgb*2-1);clip(dot(n,_DecalFacing)-_HeightParameters.z);
    float2 uv=(box.xy+.5)*_UvST.xy+_UvST.zw;
    float4 base=_AlbedoMap.Sample(sampler_AlbedoMap,uv);float coverage=saturate(base.a*_Alpha);
    float height=saturate((_HeightParameters.x*_HeightMap.Sample(sampler_HeightMap,uv).r-(box.z+.5))/_HeightParameters.y);
    DecalOutput o;o.base=float4(saturate(base.rgb*_Albedo),coverage*_Weights.x);
    float3 mos=saturate(_MosMap.Sample(sampler_MosMap,uv).rgb*_Mos);
    #if defined(TILE_DECAL_OCCLUSION)
    o.mos=float4(mos,coverage*_MosWeight.y*lerp(1,height,_Weights.w));
    #elif defined(TILE_DECAL_SMOOTHNESS)
    o.mos=float4(mos,coverage*_MosWeight.z);
    #else
    o.mos=float4(mos,coverage*_MosWeight.x);
    #endif
    float3 t=_DecalTangent-n*dot(n,_DecalTangent);float weight=coverage*_Weights.y;
    if(dot(t,t)>1e-8)
    {
        t=DecalNormal(t);float3 bitangent=cross(n,t);bitangent*=dot(bitangent,_DecalBitangent)>=0?1:-1;
        float3 map=_HasNormal>.5?DecalNormal(_NormalMap.Sample(sampler_NormalMap,uv).rgb*2-1+float3(0,0,1e-8)):float3(0,0,1);
        n=DecalNormal(map.x*t+map.y*bitangent+map.z*n);
    }
    else weight=0;
    // Normal directions blend linearly here and normalize in the lighting consumer.
    o.normal=float4(n,weight);
    o.emission=float4(clamp(_EmissionMap.Sample(sampler_EmissionMap,uv).rgb*_Emission,0,float3(65024,65024,64512)),coverage*_Weights.z);
    return o;
}
