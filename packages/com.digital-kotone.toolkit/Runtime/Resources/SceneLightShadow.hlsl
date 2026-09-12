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
#if !defined(SCENE_SHADOW_ORTHOGRAPHIC)
// World-aligned faces +X, -X, +Y, -Y, +Z, -Z. X wins ties, then Y.
// Treat a 1e-5 relative band as a tie: camera reconstruction and matrix products
// can differ by a few float ULPs on an exact seam. Keep the kernel basis stable.
// UV is in native atlas coordinates, consistent with the producer's GPU projection.
float3 ScenePointFace(float3 d)
{
    float3 a = abs(d);
    float maximum = max(a.x, max(a.y, a.z)), threshold = maximum * (1 - 1e-5);
    if (a.x >= threshold)
        return float3((d.x >= 0 ? -d.z : d.z) / a.x, d.y / a.x, d.x >= 0 ? 0 : 1);
    if (a.y >= threshold)
        return float3((d.y >= 0 ? -d.x : d.x) / a.y, d.z / a.y, d.y >= 0 ? 2 : 3);
    return float3((d.z >= 0 ? d.x : -d.x) / a.z, d.y / a.z, d.z >= 0 ? 4 : 5);
}
float3 ScenePointDirection(float2 p, float face)
{
    if (face < .5) return float3(1, p.y, -p.x);
    if (face < 1.5) return float3(-1, p.y, p.x);
    if (face < 2.5) return float3(-p.x, 1, p.y);
    if (face < 3.5) return float3(p.x, -1, p.y);
    if (face < 4.5) return float3(p.x, p.y, 1);
    return float3(-p.x, p.y, -1);
}
float ScenePointTap(float3 direction, float receiver, SceneShadowData data)
{
    float3 face = ScenePointFace(direction);
    float grid = round(1 / data.atlasST.x), tile = data.options.w - 1 + face.z;
    float2 start = float2(fmod(tile, grid), floor(tile / grid)) * data.atlasST.xy;
    float2 uv = (face.xy * .5 + .5) * data.atlasST.xy + start;
    float halfTexel = data.options.z * .5;
    return receiver <= tex2Dlod(_LightShadowAtlas, float4(clamp(uv, start + halfTexel, start + data.atlasST.xy - halfTexel), 0, 0)).r;
}
float ScenePointVisibility(float3 relative, SceneShadowData data)
{
    float radial = length(relative);
    if (radial < data.depth.x || radial > data.depth.y) return 1;
    float receiver = (radial - data.depth.z) / data.depth.y;
    if (data.options.y < .5) return lerp(1, ScenePointTap(relative, receiver, data), data.options.x);
    float3 face = ScenePointFace(relative);
    float step = 2 * data.options.z / data.atlasST.x, visibility = 0;
    // Offset in the selected face, reconstruct a direction, then SELECT AGAIN.
    // A tap crossing an edge/corner reads the adjacent face, never a repeated border.
    [unroll] for (int y = -1; y <= 1; y++) [unroll] for (int x = -1; x <= 1; x++)
        visibility += ScenePointTap(ScenePointDirection(face.xy + float2(x,y) * step, face.z), receiver, data);
    return lerp(1, visibility / 9, data.options.x);
}
#endif
float SceneLightVisibility(float3 world, float3 normal, SceneShadowData data)
{
    if (data.options.x <= 0) return 1;
    float4 projected = mul(data.worldToShadow, float4(world + normal * data.depth.w, 1));
    #if !defined(SCENE_SHADOW_ORTHOGRAPHIC)
    if (data.options.w > 0) return ScenePointVisibility(projected.xyz, data);
    #endif
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
