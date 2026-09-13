#include "LowResolutionFxShared.hlsl"
// Medium shadows and material shadows are separate variants/resources.
#undef SCENE_LIGHT_SHADOWS
#undef SCENE_MAIN_LIGHT_SHADOWS
#if defined(FX_LIT_LOCAL_SHADOWS)
#define SCENE_LIGHT_SHADOWS
#endif
#if defined(FX_LIT_MAIN_SHADOWS)
#define SCENE_MAIN_LIGHT_SHADOWS
#endif
#include "SceneForwardLighting.hlsl"
#undef SCENE_LIGHT_SHADOWS
#undef SCENE_MAIN_LIGHT_SHADOWS
#undef SCENE_LIGHT_INSTANCED
#undef FORWARD_LOCAL_SHADOWS

struct FxLitVertex
{
    float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT;
    float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; float4 color : COLOR;
};
struct FxLitVarying
{
    float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 world : TEXCOORD1;
    float3 normal : TEXCOORD2; float4 tangent : TEXCOORD3; float2 uv2 : TEXCOORD4; float2 materialUv : TEXCOORD5;
    float4 color : COLOR;
};
FxLitVarying FxLitVertexProgram(FxLitVertex v)
{
    ForwardInput input; input.vertex=v.vertex; input.normal=v.normal; input.tangent=v.tangent; input.uv=v.uv; input.uv2=v.uv2;
    ForwardVarying value=ForwardVertex(input);
    FxLitVarying o; o.pos=value.position; o.world=value.world; o.normal=value.normal; o.tangent=value.tangent;
    o.materialUv=value.uv; o.uv2=value.uv2; o.uv=v.uv; o.color=v.color; return o;
}
FxVarying FxLitUnlitInput(FxLitVarying input)
{
    FxVarying o; o.pos=input.pos; o.uv=input.uv; o.world=input.world; o.color=input.color; return o;
}
float FxLitMaterialAlpha(FxLitVarying input)
{
    float value=saturate(tex2D(_AlbedoMap,input.materialUv).a*_Alpha); clip(value-_Cutoff); return value;
}
float4 FxLitSurfaceColor(FxLitVarying input) : SV_Target
{
    float eye; float2 fullPixel;
    float4 fx=FxSample(FxLitUnlitInput(input),eye,fullPixel);
    ForwardVarying surface; surface.position=float4(fullPixel,0,1); surface.world=input.world;
    surface.normal=input.normal; surface.tangent=input.tangent; surface.uv=input.materialUv; surface.uv2=input.uv2;
    // Unchanged ForwardFragment consumes current full-source tile coordinates even
    // when rasterizing into an odd-sized reduced target or full-size repair pass.
    float4 material=ForwardFragment(surface);
    float3 color=material.rgb*fx.rgb*_FxRadiance.rgb;
    if(_FxFlags.x>.5)
    {
        float3 start;
        if(FogNear(fullPixel,start))
        { float4 fog=FogIntegrate(start,input.world,false); color=color*fog.a+(_FxSurface.x<.5?fog.rgb*material.a:0); }
    }
    #if defined(FX_HAS_MEDIUM)
    color=FxLitMedium(color,input.world,fullPixel,material.a);
    #endif
    return float4(color*fx.a,_FxSurface.x<.5?fx.a*material.a:0);
}
