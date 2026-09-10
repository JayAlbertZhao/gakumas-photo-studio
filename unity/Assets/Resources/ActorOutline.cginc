#include "UnityCG.cginc"
sampler2D _MainTex;
float4 _MainTex_ST, _Color, _ActorColor, _OutlineColor;
float4 _WardrobeScaleCorrection;
float4 _ActorOutlineParameters; // near/far width in cm, inverse distance, focal factor
float _OutlineEnabled, _VertexColor, _UseAlphaClip, _Cutoff;

struct OutlineInput
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    float4 color : COLOR;
    float2 uv : TEXCOORD0;
};

struct OutlineVaryings
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : TEXCOORD1;
};

OutlineVaryings outlineVertex(OutlineInput input)
{
    OutlineVaryings output;
    // Each byte packs two four-bit controls, not a surface albedo color.
    float4 bytes = round(saturate(input.color) * 255.0);
    float4 high = floor(bytes / 16.0);
    float4 low = bytes - 16.0 * high;
    high /= 15.0;
    low /= 15.0;
    float packed = step(0.5, _VertexColor);
    float widthMask = lerp(1.0, low.b, packed);
    // Depth counts 0..15 clip-offset units. Normalizing this nibble like
    // RGB/width reduces the authored offset fifteenfold.
    float depthOffset = floor(bytes.b / 16.0) * packed;
    output.color = lerp(float4(0.08, 0.06, 0.08, 1.0),
        float4(high.r, low.r, high.g, low.a), packed);
    float3 position = input.vertex.xyz * _WardrobeScaleCorrection.xyz;
    float3 world = mul(unity_ObjectToWorld, float4(position, 1.0)).xyz;
    float blend = saturate(distance(world, _WorldSpaceCameraPos) *
        _ActorOutlineParameters.z * _ActorOutlineParameters.w);
    float width = lerp(_ActorOutlineParameters.x, _ActorOutlineParameters.y, blend) *
        0.01 * widthMask * _OutlineEnabled;
    // TANGENT.xyz is an authored object-space extrusion vector: its length,
    // including zero, is significant. Transform the displaced position rather
    // than normalizing an inverse-transpose normal in world space.
    position = (input.vertex.xyz + input.tangent.xyz * width) * _WardrobeScaleCorrection.xyz;
    world = mul(unity_ObjectToWorld, float4(position, 1.0)).xyz;
    output.position = UnityWorldToClipPos(world);
    // Move away from the camera; account for reversed depth rather than
    // assuming the same clip-space sign on D3D and non-reversed backends.
    #if defined(UNITY_REVERSED_Z)
    output.position.z -= depthOffset * (0.001 / 15.0);
    #else
    output.position.z += depthOffset * (0.001 / 15.0);
    #endif
    output.uv = TRANSFORM_TEX(input.uv, _MainTex);
    return output;
}

float4 outlineFragment(OutlineVaryings input) : SV_Target
{
    clip(_OutlineEnabled - 0.5);
    float noise = frac(52.9829189 * frac(dot(floor(input.position.xy),
        float2(0.06711056, 0.00583715))));
    clip(_ActorColor.a - noise);
    float4 baseColor = tex2D(_MainTex, input.uv) * _Color;
    if (_UseAlphaClip > 0.5) clip(baseColor.a - _Cutoff);
    // Packed outline RGB is an independent authored color. Its fourth nibble
    // is not a request to blend bright surface albedo into the silhouette.
    float3 color = input.color.rgb;
    color = lerp(color, _OutlineColor.rgb, saturate(_OutlineColor.a));
    return float4(max(color * _ActorColor.rgb, 0.0), 1.0);
}
