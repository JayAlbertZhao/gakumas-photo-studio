#ifndef TOOLKIT_SCENE_BAKED_SHADOW
#define TOOLKIT_SCENE_BAKED_SHADOW
sampler2D _SceneBakedShadowMap, _PackedBakedShadow;
float4 _SceneBakedShadowST, _SceneBakedShadowConstant;
float _SceneBakedShadowMode, _SceneBakedShadowDither, _MainBakedChannel;
#if defined(SCENE_BAKED_LIGHT_CHANNELS)
#if defined(SCENE_LIGHT_INSTANCED)
StructuredBuffer<int> _SceneBakedChannels;
#else
float _SingleBakedChannel;
#endif
#endif
float4 SceneBakedSample(float2 uv2)
{
    #if defined(SCENE_BAKED_SHADOW_INPUT)
    if (_SceneBakedShadowMode < .5) return 1;
    if (_SceneBakedShadowMode < 1.5) return saturate(_SceneBakedShadowConstant);
    return saturate(tex2D(_SceneBakedShadowMap, uv2 * _SceneBakedShadowST.xy + _SceneBakedShadowST.zw));
    #else
    return 1;
    #endif
}
float2 SceneBakedPack(float4 visibility, float2 pixel)
{
    const float rank[16] = { 0,8,2,10,12,4,14,6,3,11,1,9,15,7,13,5 };
    float threshold = _SceneBakedShadowDither > .5 ? (rank[(int)fmod(floor(pixel.y),4)*4+(int)fmod(floor(pixel.x),4)]+.5)/16 : .5;
    float3 q = floor(saturate(visibility.gba) * float3(7,7,3) + threshold);
    return float2(floor(saturate(visibility.r)*255+.5), dot(q,float3(32,4,1))) / 255;
}
float4 SceneBakedUnpack(float2 encoded)
{
    float2 bytes = floor(saturate(encoded)*255+.5);
    return float4(bytes.x/255, floor(bytes.y/32)/7, fmod(floor(bytes.y/4),8)/7, fmod(bytes.y,4)/3);
}
float SceneBakedSelect(float4 visibility, float channel)
{
    if (channel < .5) return 1;
    if (channel < 1.5) return visibility.r;
    if (channel < 2.5) return visibility.g;
    if (channel < 3.5) return visibility.b;
    return visibility.a;
}
#endif
