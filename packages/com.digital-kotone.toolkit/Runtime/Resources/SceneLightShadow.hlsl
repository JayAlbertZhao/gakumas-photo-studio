#if defined(SCENE_LIGHT_SHADOWS)
struct SceneShadowData { float4x4 worldToShadow; float4 atlasST, depth, options; };
sampler2D _LightShadowAtlas;
#if defined(SCENE_SHADOW_ORTHOGRAPHIC)
float4 _ShadowDepthPlane;
#endif
#if defined(SCENE_LIGHT_INSTANCED)
StructuredBuffer<SceneShadowData> _SceneLightShadows;
#else
float4x4 _SingleShadowMatrix;
float4 _SingleShadowST, _SingleShadowDepth, _SingleShadowOptions;
#endif
float SceneLightVisibility(float3 world, float3 normal, SceneShadowData data)
{
    if (data.options.x <= 0) return 1;
    float4 projected = mul(data.worldToShadow, float4(world + normal * data.depth.w, 1));
    #if defined(SCENE_SHADOW_ORTHOGRAPHIC)
    float axial = dot(_ShadowDepthPlane, float4(world + normal * data.depth.w, 1));
    if (axial < data.depth.x || axial > data.depth.y) return 1;
    #else
    if (projected.w < data.depth.x || projected.w > data.depth.y) return 1;
    #endif
    float2 uv = projected.xy / projected.w * .5 + .5;
    if (any(uv < 0) || any(uv > 1)) return 1;
    #if UNITY_UV_STARTS_AT_TOP
    uv.y = 1 - uv.y;
    #endif
    uv = uv * data.atlasST.xy + data.atlasST.zw;
    float texel = data.options.z;
    float2 low = data.atlasST.zw + .5 * texel, high = data.atlasST.zw + data.atlasST.xy - .5 * texel;
    #if defined(SCENE_SHADOW_ORTHOGRAPHIC)
    float receiver = (axial - data.depth.z) / data.depth.y;
    #else
    float receiver = (projected.w - data.depth.z) / data.depth.y;
    #endif
    float visibility = 0;
    if (data.options.y < .5)
        visibility = receiver <= tex2Dlod(_LightShadowAtlas, float4(clamp(uv, low, high), 0, 0)).r;
    else
    {
        [unroll] for (int y = -1; y <= 1; y++) [unroll] for (int x = -1; x <= 1; x++)
            visibility += receiver <= tex2Dlod(_LightShadowAtlas, float4(clamp(uv + float2(x,y) * texel, low, high), 0, 0)).r;
        visibility /= 9;
    }
    return lerp(1, visibility, data.options.x);
}
#endif
