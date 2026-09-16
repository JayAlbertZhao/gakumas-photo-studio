#ifndef TOOLKIT_ACTOR_FORWARD_SHADOW
#define TOOLKIT_ACTOR_FORWARD_SHADOW
// R32 axial light depth. Integer loads avoid consuming another D3D sampler and
// explicit FLOAT prevents Vulkan relaxed precision from changing depth tests.
UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_ActorForwardShadowMap);
float _UseActorForwardShadow;
float4x4 _ActorForwardShadowMatrix;
float4 _ActorForwardShadowPlane, _ActorForwardShadowDepth, _ActorForwardShadowOptions;
float ActorForwardShadowTap(int2 pixel, int size, float compared, float2 uv, float2 slope)
{
    pixel = clamp(pixel, 0, size - 1);
    float tapDepth = compared + dot((pixel + .5) / size - uv, slope);
    return tapDepth <= _ActorForwardShadowMap.Load(int3(pixel, 0)).r;
}
float ActorForwardShadowVisibility(float3 world, float3 normal)
{
    float4 depth = _ActorForwardShadowDepth, options = _ActorForwardShadowOptions;
    // Derivatives must be evaluated before per-fragment bounds rejection. The
    // raster triangle plane, not the smoothed shading normal, predicts map depth.
    float3 planeNormal = cross(ddx(world), ddy(world));
    float denominator = dot(planeNormal, _ActorForwardShadowPlane.xyz);
    float2 slope = 0;
    if (options.w > .5 && abs(denominator) > length(planeNormal) * .0001)
    {
        float3 right = _ActorForwardShadowMatrix[0].xyz;
        float3 up = _ActorForwardShadowMatrix[1].xyz;
        slope = -2 * float2(dot(planeNormal, right) / dot(right, right),
                            dot(planeNormal, up) / dot(up, up)) / (denominator * depth.y);
        #if UNITY_UV_STARTS_AT_TOP
        slope.y = -slope.y;
        #endif
    }
    if (options.x <= 0) return 1;
    float4 receiver = float4(world + normal * depth.w, 1);
    float axial = dot(_ActorForwardShadowPlane, receiver);
    if (axial < depth.x || axial > depth.y) return 1;
    float4 projected = mul(_ActorForwardShadowMatrix, receiver);
    float2 uv = projected.xy / projected.w * .5 + .5;
    if (any(uv < 0) || any(uv > 1)) return 1;
    #if UNITY_UV_STARTS_AT_TOP
    uv.y = 1 - uv.y;
    #endif
    int size = (int)round(1 / options.z);
    int2 pixel = (int2)floor(uv * size);
    float compared = (axial - depth.z) / depth.y;
    float visibility = 0;
    if (options.y < .5)
        visibility = ActorForwardShadowTap(pixel, size, compared, uv, slope);
    else
    {
        [unroll] for (int y = -1; y <= 1; y++) [unroll] for (int x = -1; x <= 1; x++)
            visibility += ActorForwardShadowTap(pixel + int2(x,y), size, compared, uv, slope);
        visibility /= 9;
    }
    return lerp(1, visibility, options.x);
}
#endif
