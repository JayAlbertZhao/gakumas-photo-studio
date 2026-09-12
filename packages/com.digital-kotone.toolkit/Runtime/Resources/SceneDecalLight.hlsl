#include "UnityCG.cginc"
struct SceneLightData
{
    float4 positionRange, axisXLength, axisYWidth, axisZHeight;
    float4 radianceShape, uv, parameters, response, clipRect;
};
sampler2D _G0, _G1, _G2, _LightAtlas, _LightGi;
float4x4 _LightInverseViewProjection, _LightView;
float3 _LightCameraPosition, _LightCameraForward;
float _LightOrthographic, _LightHasGi;
#if defined(SCENE_LIGHT_INSTANCED)
StructuredBuffer<SceneLightData> _SceneLights;
uint _SceneLightOffset;
struct LightVertex { uint vertex : SV_VertexID; uint instance : SV_InstanceID; };
struct LightVarying { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; nointerpolation uint index : TEXCOORD1; };
SceneLightData LightInput(uint index) { return _SceneLights[index]; }
#else
float4 _SinglePositionRange, _SingleAxisXLength, _SingleAxisYWidth, _SingleAxisZHeight;
float4 _SingleRadianceShape, _SingleUv, _SingleParameters, _SingleResponse, _SingleClipRect;
struct LightVertex { float4 vertex : POSITION; };
struct LightVarying { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
SceneLightData LightInput()
{
    SceneLightData o;
    o.positionRange = _SinglePositionRange; o.axisXLength = _SingleAxisXLength;
    o.axisYWidth = _SingleAxisYWidth; o.axisZHeight = _SingleAxisZHeight;
    o.radianceShape = _SingleRadianceShape; o.uv = _SingleUv; o.parameters = _SingleParameters;
    o.response = _SingleResponse; o.clipRect = _SingleClipRect; return o;
}
#endif
LightVarying LightVert(LightVertex input)
{
    LightVarying o;
    #if defined(SCENE_LIGHT_INSTANCED)
    o.index = input.instance + _SceneLightOffset; SceneLightData light = LightInput(o.index);
    // Two clockwise triangles, generated once per real GPU instance.
    const float2 corners[6] = { float2(0,0), float2(0,1), float2(1,1), float2(0,0), float2(1,1), float2(1,0) };
    float2 corner = corners[input.vertex];
    #else
    SceneLightData light = LightInput(); float2 corner = input.vertex.xy * .5 + .5;
    #endif
    o.pos = float4(lerp(light.clipRect.xy, light.clipRect.zw, corner), 0, 1);
    o.uv = o.pos.xy * .5 + .5;
    #if UNITY_UV_STARTS_AT_TOP
    o.uv.y = 1 - o.uv.y;
    #endif
    return o;
}
float3 LightNormal(float3 n) { return n * rsqrt(max(dot(n, n), 1e-12)); }
float3 LightWorld(float2 uv, float depth)
{
    float2 xy = uv * 2 - 1;
    #if UNITY_UV_STARTS_AT_TOP
    xy.y = -xy.y;
    #endif
    float4 a = mul(_LightInverseViewProjection, float4(xy, 0, 1)); a /= a.w;
    float4 b = mul(_LightInverseViewProjection, float4(xy, 1, 1)); b /= b.w;
    float da = -mul(_LightView, a).z, db = -mul(_LightView, b).z;
    return lerp(a.xyz, b.xyz, (depth - da) / (db - da));
}
float3 LightBrdf(float3 albedo, float3 mos, float3 n, float3 v, float3 l, float4 response)
{
    float3 h = LightNormal(v + l);
    float nl = saturate(dot(n, l)), nv = saturate(dot(n, v)), nh = saturate(dot(n, h)), vh = saturate(dot(v, h));
    float rough = max(1 - mos.b, .045), a2 = rough * rough * rough * rough;
    float denominator = nh * nh * (a2 - 1) + 1;
    float distribution = a2 / max(UNITY_PI * denominator * denominator, 1e-8);
    float visibility = .5 / max(nl * sqrt(nv * nv * (1 - a2) + a2) + nv * sqrt(nl * nl * (1 - a2) + a2), 1e-6);
    float3 f0 = lerp(.04, albedo, mos.r), f = f0 + (1 - f0) * pow(1 - vh, 5);
    float3 result = ((1 - f) * albedo * ((1 - mos.r) / UNITY_PI) * response.x + distribution * visibility * f * response.y) * nl;
    if (response.w > 0) result += (1 - f0) * albedo * ((1 - mos.r) / UNITY_PI) * response.x * response.w * saturate(-dot(n, l));
    return result;
}
float4 LightFrag(LightVarying input) : SV_Target
{
    #if defined(SCENE_LIGHT_INSTANCED)
    SceneLightData light = LightInput(input.index);
    #else
    SceneLightData light = LightInput();
    #endif
    float4 albedo = tex2D(_G0, input.uv); clip(albedo.a - .5);
    float4 normal = tex2D(_G1, input.uv), mos = tex2D(_G2, input.uv);
    if (light.parameters.z > .5 && abs(normal.a - light.parameters.z) > .1) discard;
    float3 world = LightWorld(input.uv, mos.a), delta = world - light.positionRange.xyz;
    float3 source = light.positionRange.xyz; float2 atlasUv = light.uv.zw;
    float distanceToSource;
    if (light.radianceShape.w < .5) distanceToSource = length(delta);
    else if (light.radianceShape.w < 1.5)
    {
        float along = clamp(dot(delta, light.axisXLength.xyz), -light.axisXLength.w, light.axisXLength.w);
        source += light.axisXLength.xyz * along; distanceToSource = length(world - source);
        float t = light.axisXLength.w > 1e-6 ? along / (2 * light.axisXLength.w) + .5 : .5;
        atlasUv += light.uv.xy * t;
    }
    else
    {
        float z = dot(delta, light.axisZHeight.xyz); if (z <= 0 || z >= light.positionRange.w) discard;
        float2 nearHalf = float2(light.axisYWidth.w, light.axisZHeight.w);
        float2 projectedHalf = nearHalf + light.parameters.xy * z;
        float2 projected = float2(dot(delta, light.axisXLength.xyz), dot(delta, light.axisYWidth.xyz)) / projectedHalf;
        if (any(abs(projected) > 1)) discard;
        source += light.axisXLength.xyz * (projected.x * nearHalf.x) + light.axisYWidth.xyz * (projected.y * nearHalf.y);
        atlasUv += (projected * .5 + .5) * light.uv.xy; distanceToSource = z;
    }
    float attenuation = pow(saturate(1 - distanceToSource / light.positionRange.w), light.parameters.w);
    float3 direction = LightNormal(source - world), n = LightNormal(normal.xyz);
    float3 view = LightNormal(lerp(_LightCameraPosition - world, -_LightCameraForward, _LightOrthographic));
    float3 atlas = clamp(tex2Dlod(_LightAtlas, float4(atlasUv, 0, 0)).rgb, 0, 65504);
    // Float32 accumulation, then the host clamps once after adding all lighting and emission.
    float3 response = LightBrdf(albedo.rgb, mos.rgb, n, view, direction, light.response);
    if (_LightHasGi > .5 && light.response.z > 0)
    {
        float4 gi = tex2D(_LightGi, input.uv); if (gi.a > .5) response *= lerp(1, gi.rgb, light.response.z);
    }
    return float4(response * atlas * light.radianceShape.rgb * attenuation, 0);
}
