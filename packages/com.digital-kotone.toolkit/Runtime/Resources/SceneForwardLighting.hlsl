#include "UnityCG.cginc"
#include "SceneGi.hlsl"
#if defined(TOOLKIT_FORWARD_LEAF)
#include "VegetationLeaf.hlsl"
#define FORWARD_LEAF_PARAMETER , float3 leafTau
#define FORWARD_LEAF_ARGUMENT , leafTau
#else
#define FORWARD_LEAF_PARAMETER
#define FORWARD_LEAF_ARGUMENT
#endif
#define SCENE_LIGHT_INSTANCED
#include "SceneBakedShadow.hlsl"
#if defined(SCENE_LIGHT_SHADOWS)
#define FORWARD_LOCAL_SHADOWS
#endif
#include "SceneLightShadow.hlsl"
// Both producers have independent atlases. Alias the existing directional helper locally.
#if defined(SCENE_MAIN_LIGHT_SHADOWS)
#if !defined(SCENE_LIGHT_SHADOWS)
#define SCENE_LIGHT_SHADOWS
#endif
#undef SCENE_LIGHT_INSTANCED
#define SCENE_SHADOW_ORTHOGRAPHIC
#define SceneShadowData ForwardMainShadowData
#define SceneLightVisibility ForwardMainVisibility
#define _LightShadowAtlas _MainShadowAtlas
#include "SceneLightShadow.hlsl"
#undef _LightShadowAtlas
#undef SceneLightVisibility
#undef SceneShadowData
#undef SCENE_SHADOW_ORTHOGRAPHIC
#define SCENE_LIGHT_INSTANCED
#if !defined(FORWARD_LOCAL_SHADOWS)
#undef SCENE_LIGHT_SHADOWS
#endif
#endif

struct SceneLightData
{
    float4 positionRange, axisXLength, axisYWidth, axisZHeight;
    float4 radianceShape, uv, parameters, response, clipRect;
};
StructuredBuffer<SceneLightData> _SceneLights;
StructuredBuffer<uint> _ForwardTiles;
int _ForwardLightCount, _ForwardWords, _ForwardTileSize, _ForwardTilesX, _ForwardTiled;
sampler2D _AlbedoMap, _NormalMap, _MosMap, _EmissionMap, _LightAtlas;
float4 _UvST, _DirectionalResponse;
float3 _Albedo, _Mos, _Emission, _VertexScale, _LightDirection, _LightRadiance, _AmbientIrradiance;
float3 _CameraPosition, _CameraForward;
float _HasNormal, _Alpha, _Cutoff, _ReceiverGroup, _Additive, _Orthographic, _GiBaseScale;
#if defined(TOOLKIT_FORWARD_TOON)
float4 _ForwardToon; // threshold, softness, shadow diffuse level, enabled
#endif
float4x4 _ViewProjection;
float3 ForwardNormal(float3 n) { return n * rsqrt(max(dot(n, n), 1e-12)); }

// Same independently authored BRDF contract as scene decal lights, evaluated at this fragment's geometry.
float3 ForwardBrdf(float3 albedo, float3 mos, float3 n, float3 v, float3 l, float4 response FORWARD_LEAF_PARAMETER)
{
    float3 h = ForwardNormal(v + l);
    float nl = saturate(dot(n, l)), nv = saturate(dot(n, v)), nh = saturate(dot(n, h)), vh = saturate(dot(v, h));
    float rough = max(1 - mos.b, .045), a2 = rough * rough * rough * rough;
    float denominator = nh * nh * (a2 - 1) + 1;
    float distribution = a2 / max(UNITY_PI * denominator * denominator, 1e-8);
    float visibility = .5 / max(nl * sqrt(nv * nv * (1 - a2) + a2) + nv * sqrt(nl * nl * (1 - a2) + a2), 1e-6);
    float3 f0 = lerp(.04, albedo, mos.r), f = f0 + (1 - f0) * pow(1 - vh, 5);
    #if defined(TOOLKIT_FORWARD_TOON)
    if (_ForwardToon.w > .5)
    {
        float band = lerp(_ForwardToon.z, 1, smoothstep(_ForwardToon.x - _ForwardToon.y, _ForwardToon.x + _ForwardToon.y, nl));
        float3 toon = (1 - f) * albedo * ((1 - mos.r) / UNITY_PI) * response.x * band + distribution * visibility * f * response.y * nl;
        if (response.w > 0) toon += (1 - f0) * albedo * ((1 - mos.r) / UNITY_PI) * response.x * response.w * saturate(-dot(n, l));
        return toon;
    }
    #endif
    float3 result = ((1 - f) * albedo * ((1 - mos.r) / UNITY_PI) * response.x + distribution * visibility * f * response.y) * nl;
    if (response.w > 0) result += (1 - f0) * albedo * ((1 - mos.r) / UNITY_PI) * response.x * response.w * saturate(-dot(n, l));
    #if defined(TOOLKIT_FORWARD_LEAF)
    if (_LeafEnabled > .5) result = LeafDiffuse(albedo*((1-mos.r)/UNITY_PI),f,f0,nl,saturate(-dot(n,l)),response.x,response.w,leafTau) +
        distribution * visibility * f * response.y * nl;
    #endif
    return result;
}

float3 ForwardLocal(uint index, float3 world, float3 n, float3 v, float3 albedo, float3 mos, float4 gi, float4 baked FORWARD_LEAF_PARAMETER)
{
    SceneLightData light = _SceneLights[index];
    if (light.parameters.z > .5 && abs(_ReceiverGroup - light.parameters.z) > .1) return 0;
    float3 delta = world - light.positionRange.xyz, source = light.positionRange.xyz;
    float2 atlasUv = light.uv.zw;
    float distanceToSource;
    if (light.radianceShape.w < .5) distanceToSource = length(delta);
    else if (light.radianceShape.w < 1.5)
    {
        float along = clamp(dot(delta, light.axisXLength.xyz), -light.axisXLength.w, light.axisXLength.w);
        source += light.axisXLength.xyz * along; distanceToSource = length(world - source);
        float t = light.axisXLength.w > 1e-6 ? along / (2 * light.axisXLength.w) + .5 : .5;
        atlasUv += light.uv.xy * t;
    }
    else if (light.radianceShape.w < 2.5)
    {
        float z = dot(delta, light.axisZHeight.xyz); if (z <= 0 || z >= light.positionRange.w) return 0;
        float2 nearHalf = float2(light.axisYWidth.w, light.axisZHeight.w);
        float2 projectedHalf = nearHalf + light.parameters.xy * z;
        float2 projected = float2(dot(delta, light.axisXLength.xyz), dot(delta, light.axisYWidth.xyz)) / projectedHalf;
        if (any(abs(projected) > 1)) return 0;
        source += light.axisXLength.xyz * (projected.x * nearHalf.x) + light.axisYWidth.xyz * (projected.y * nearHalf.y);
        atlasUv += (projected * .5 + .5) * light.uv.xy; distanceToSource = z;
    }
    else distanceToSource = length(delta);
    float attenuation = pow(saturate(1 - distanceToSource / light.positionRange.w), light.parameters.w);
    if (light.radianceShape.w > 2.5)
    {
        if (distanceToSource <= 1e-6) return 0;
        float cosine = dot(delta / distanceToSource, light.axisZHeight.xyz);
        if (cosine < light.parameters.y) return 0;
        attenuation *= light.parameters.x > light.parameters.y ? saturate((cosine - light.parameters.y) / (light.parameters.x - light.parameters.y)) : 1;
    }
    float3 response = ForwardBrdf(albedo, mos, n, v, ForwardNormal(source - world), light.response FORWARD_LEAF_ARGUMENT);
    #if defined(TOOLKIT_FORWARD_LEAF)
    if (_LeafEnabled > .5) n = LeafShadowNormal(n,source-world);
    #endif
    if (gi.a > .5 && light.response.z > 0) response *= lerp(1, gi.rgb, light.response.z);
    #if defined(SCENE_BAKED_SHADOW_INPUT) && defined(SCENE_BAKED_LIGHT_CHANNELS)
    float maskVisibility = SceneBakedSelect(baked, _SceneBakedChannels[index]);
    #if defined(FORWARD_LOCAL_SHADOWS)
    maskVisibility = min(maskVisibility, SceneLightVisibility(world, n, _SceneLightShadows[index]));
    #endif
    attenuation *= maskVisibility;
    #elif defined(FORWARD_LOCAL_SHADOWS)
    attenuation *= SceneLightVisibility(world, n, _SceneLightShadows[index]);
    #endif
    return response * clamp(tex2Dlod(_LightAtlas, float4(atlasUv, 0, 0)).rgb, 0, 65504) * light.radianceShape.rgb * attenuation;
}
// Existing specialized consumers retain the same unmasked contract.
#if defined(TOOLKIT_FORWARD_LEAF)
float3 ForwardLocal(uint index, float3 world, float3 n, float3 v, float3 albedo, float3 mos, float4 gi, float4 baked)
{ return ForwardLocal(index,world,n,v,albedo,mos,gi,baked,0); }
#endif
float3 ForwardLocal(uint index, float3 world, float3 n, float3 v, float3 albedo, float3 mos, float4 gi)
{ return ForwardLocal(index, world, n, v, albedo, mos, gi, 1); }

struct ForwardInput { float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; };
struct ForwardVarying { float4 position : SV_POSITION; float3 world : TEXCOORD0; float3 normal : TEXCOORD1; float4 tangent : TEXCOORD2; float2 uv : TEXCOORD3; float2 uv2 : TEXCOORD4; };
ForwardVarying ForwardVertex(ForwardInput input)
{
    ForwardVarying o; input.vertex.xyz *= _VertexScale;
    float4 world = mul(unity_ObjectToWorld, input.vertex); o.world = world.xyz; o.position = mul(_ViewProjection, world);
    o.normal = UnityObjectToWorldNormal(input.normal / _VertexScale);
    // Manual DrawRenderer/DrawMesh do not guarantee WorldTransformParams.w.
    // Derive parity from the current matrix, as Deferred/Reflection already do.
    float handedness = sign(determinant((float3x3)unity_ObjectToWorld)) * sign(_VertexScale.x * _VertexScale.y * _VertexScale.z);
    o.tangent = float4(mul((float3x3)unity_ObjectToWorld, input.tangent.xyz * _VertexScale), input.tangent.w * handedness);
    o.uv = input.uv * _UvST.xy + _UvST.zw; o.uv2 = input.uv2; return o;
}
float4 ForwardFragment(ForwardVarying input) : SV_Target
{
    float4 texel = tex2D(_AlbedoMap, input.uv);
    float alpha = saturate(texel.a * _Alpha); clip(alpha - _Cutoff);
    float3 albedo = saturate(texel.rgb * _Albedo), mos = saturate(tex2D(_MosMap, input.uv).rgb * _Mos);
    float3 n = ForwardNormal(input.normal);
    if (_HasNormal > .5)
    {
        float3 t = ForwardNormal(input.tangent.xyz - n * dot(n, input.tangent.xyz));
        float3 b = ForwardNormal(cross(n, t)) * input.tangent.w;
        float3 map = ForwardNormal(tex2D(_NormalMap, input.uv).xyz * 2 - 1);
        n = ForwardNormal(t * map.x + b * map.y + n * map.z);
    }
    float3 v = ForwardNormal(lerp(_CameraPosition - input.world, -_CameraForward, _Orthographic));
    #if defined(TOOLKIT_FORWARD_LEAF)
    float3 leafTau = 0;
    if (_LeafEnabled > .5) { leafTau = LeafTransmission(input.uv); n *= LeafFacing(input.normal,v); }
    #endif
    float4 gi = SceneGi(input.uv2, n);
    #if defined(TOOLKIT_FORWARD_LEAF)
    if (_LeafEnabled > .5) gi.rgb = lerp(gi.rgb,SceneGi(input.uv2,-n).rgb,leafTau);
    #endif
    float4 baked = SceneBakedSample(input.uv2);
    float3 direct = ForwardBrdf(albedo, mos, n, v, _LightDirection, _DirectionalResponse FORWARD_LEAF_ARGUMENT) * _LightRadiance;
    float3 shadowNormal = n;
    #if defined(TOOLKIT_FORWARD_LEAF)
    if (_LeafEnabled > .5) shadowNormal = LeafShadowNormal(n,_LightDirection);
    #endif
    if (gi.a > .5) direct *= lerp(1, gi.rgb, _DirectionalResponse.z);
    #if defined(SCENE_MAIN_LIGHT_SHADOWS)
    ForwardMainShadowData shadow; shadow.worldToShadow = _SingleShadowMatrix; shadow.atlasST = _SingleShadowST;
    shadow.depth = _SingleShadowDepth; shadow.options = _SingleShadowOptions;
    #if defined(SCENE_BAKED_SHADOW_INPUT)
    direct *= min(ForwardMainVisibility(input.world, shadowNormal, shadow), SceneBakedSelect(baked, _MainBakedChannel));
    #else
    direct *= ForwardMainVisibility(input.world, shadowNormal, shadow);
    #endif
    #elif defined(SCENE_BAKED_SHADOW_INPUT)
    direct *= SceneBakedSelect(baked, _MainBakedChannel);
    #endif
    if (_ForwardTiled != 0)
    {
        uint2 tile = (uint2)input.position.xy / (uint)_ForwardTileSize;
        uint start = (tile.y * _ForwardTilesX + tile.x) * _ForwardWords;
        [loop] for (uint word = 0; word < (uint)_ForwardWords; word++)
        {
            uint mask = _ForwardTiles[start + word];
            [loop] while (mask != 0)
            {
                uint bit = firstbitlow(mask); mask &= mask - 1;
                direct += ForwardLocal(word * 32 + bit, input.world, n, v, albedo, mos, gi, baked FORWARD_LEAF_ARGUMENT);
            }
        }
    }
    else [loop] for (uint index = 0; index < (uint)_ForwardLightCount; index++)
        direct += ForwardLocal(index, input.world, n, v, albedo, mos, gi, baked FORWARD_LEAF_ARGUMENT);
    float3 indirect = albedo * ((1 - mos.r) / UNITY_PI) * _AmbientIrradiance * mos.g;
    if (gi.a > .5) indirect = albedo * (1 - mos.r) * gi.rgb * _GiBaseScale * mos.g;
    float3 emission = clamp(tex2D(_EmissionMap, input.uv).rgb * _Emission, 0, 65504);
    return float4(clamp(direct + indirect + emission, 0, 65504) * alpha, _Additive > .5 ? 0 : alpha);
}
