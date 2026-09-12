#ifndef TOOLKIT_ACTOR_PLANAR_SURFACE
#define TOOLKIT_ACTOR_PLANAR_SURFACE
// Independent reduced surface. Keeps authored diffuse/detail inputs while
// omitting shadow sampling, environment/specular BRDF, additional lights,
// screen effects and outlines. Capture-specific names cannot inherit main
// renderer property blocks or the current main camera's global lighting.
sampler2D _CapMain, _CapShade, _CapDef, _CapRamp, _CapLayer, _CapHighlight, _CapRampAdd, _CapEmission;
float4 _CapMainST, _CapBaseST, _CapAtlas, _CapTint, _CapActorTint, _CapDefValue, _CapThreshold;
float4 _CapRampAddTint, _CapEmissionTint, _CapScale, _CapHeadRight, _CapHeadUp, _CapHeadForward, _CapHairFade;
float4 _CapKey, _CapAmbient, _CapShadeTint, _CapShadeAdditive, _CapLight;
float _CapType, _CapVariant, _CapUseDef, _CapLayerEnabled, _CapLayerWeight, _CapUseVertex;
float _CapAlphaClip, _CapCutoff, _CapEmissionEnabled, _CapHairCover, _CapOpacityMode, _CapPremultiply;
float _CapLodBias, _CapRampOffset, _CapSkinSaturation;
struct CaptureInput { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; float2 uv2:TEXCOORD1; float4 color:COLOR; };
struct CaptureVarying { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float2 layerUv:TEXCOORD1;
    float3 world:TEXCOORD2; float3 normal:TEXCOORD3; float3 objectNormal:TEXCOORD4; float rampRow:TEXCOORD5; };
CaptureVarying CaptureVertex(CaptureInput i)
{
    CaptureVarying o; i.vertex.xyz *= _CapScale.xyz; i.normal /= _CapScale.xyz;
    o.pos = UnityObjectToClipPos(i.vertex); o.world = mul(unity_ObjectToWorld, i.vertex).xyz;
    o.normal = UnityObjectToWorldNormal(i.normal); o.objectNormal = normalize(i.normal);
    o.uv = i.uv * _CapMainST.xy + _CapMainST.zw; o.layerUv = i.uv2 * float2(.5, 1);
    float high = floor(i.color.g * 15.9375 + .03125);
    o.rampRow = lerp(.5, (i.color.g * 255 - high * 16) / 15, saturate(_CapUseVertex)); return o;
}
float2 CaptureUv(CaptureVarying i)
{
    float2 uv = abs(_CapType - 5) < .25 ? i.uv * _CapBaseST.xy + _CapBaseST.zw : i.uv;
    if (_CapAtlas.x > 0 && _CapAtlas.y > 0) uv = uv * _CapAtlas.xy + _CapAtlas.zw;
    return uv;
}
float3 CaptureView(CaptureVarying i)
{ return normalize(unity_OrthoParams.w > .5 ? UNITY_MATRIX_V[2].xyz : _WorldSpaceCameraPos.xyz - i.world); }
float4 CaptureBase(CaptureVarying i)
{
    if (_CapActorTint.a <= 0) clip(-1);
    float noise = frac(52.9829189 * frac(dot(floor(i.pos.xy), float2(.06711056, .00583715))));
    clip(_CapActorTint.a - noise);
    float4 sample = tex2Dbias(_CapMain, float4(CaptureUv(i), 0, _CapLodBias));
    sample.a *= _CapTint.a;
    if (_CapAlphaClip > .5) clip((sample.a - _CapCutoff) / max(fwidth(sample.a), .0001) + .5);
    return sample;
}
float CaptureAlpha(CaptureVarying i, float baseAlpha)
{
    if (_CapHairCover > .5)
    {
        float3 view = CaptureView(i);
        float2 alignment = float2(dot(view, _CapHeadForward.xyz), abs(dot(view, _CapHeadUp.xyz)));
        float2 coverage = saturate(float2(_CapHairFade.x - alignment.x, alignment.y - _CapHairFade.z) * _CapHairFade.yw);
        // Recover the source texture alpha independently of material opacity.
        float rawAlpha = tex2Dbias(_CapMain, float4(CaptureUv(i), 0, _CapLodBias)).a;
        return saturate((1 - saturate(rawAlpha) * (1 - max(coverage.x, coverage.y))) * _CapTint.a);
    }
    return _CapOpacityMode > .5 ? saturate(baseAlpha) : 1;
}
float4 CaptureCoverage(CaptureVarying i) : SV_Target
{ float4 sample = CaptureBase(i); return float4(0, 0, 0, CaptureAlpha(i, sample.a)); }
float4 CaptureColor(CaptureVarying i, float facing:VFACE) : SV_Target
{
    float4 baseSample = CaptureBase(i); float2 uv = CaptureUv(i);
    float4 shade = tex2Dbias(_CapShade, float4(uv, 0, _CapLodBias));
    float4 def = _CapUseDef > .5 ? tex2Dbias(_CapDef, float4(uv, 0, _CapLodBias)) : _CapDefValue;
    if (_CapLayerEnabled > .5)
    {
        float4 layer = tex2Dbias(_CapLayer, float4(i.layerUv, 0, _CapLodBias - 1));
        float weight = saturate(layer.a * _CapLayerWeight);
        baseSample.rgb = lerp(baseSample.rgb, layer.rgb, weight);
        def = lerp(def, tex2Dbias(_CapLayer, float4(i.layerUv + float2(.5, 0), 0, _CapLodBias - 1)), weight);
    }
    float sign = facing >= 0 ? 1 : -1;
    float3 normal = normalize(i.normal) * sign, view = CaptureView(i), light = normalize(_CapLight.xyz);
    bool eye = abs(_CapType - 4) < .25, highlight = abs(_CapType - 5) < .25, hair = abs(_CapType - 8) < .25, skin = abs(_CapType - 9) < .25;
    if (hair)
    {
        float3 halfDirection = normalize(light + view + float3(0, 1e-6, 0));
        float response = pow(saturate(dot(normal, halfDirection)), 4);
        float gate = _CapThreshold.y <= 1e-5 ? step(_CapThreshold.x, response) : smoothstep(_CapThreshold.x - _CapThreshold.y, _CapThreshold.x + _CapThreshold.y, response);
        float accessory = i.uv.x > .75 && i.uv.y > .75 ? 1 : 0;
        baseSample.rgb = lerp(baseSample.rgb, tex2Dbias(_CapHighlight, float4(uv, 0, _CapLodBias)).rgb, gate * saturate(def.a) * (1 - accessory));
    }
    if (!eye && !highlight && !(abs(_CapType - 1) < .25 && _CapVariant >= 1.5))
    {
        float4 detail = tex2D(_CapRampAdd, float2(saturate(def.r * 2 - 1 + dot(normal, view)), saturate(i.rampRow)));
        float3 added = detail.rgb * _CapRampAddTint.rgb * (1 - detail.a);
        baseSample.rgb += added; shade.rgb += added;
    }
    float coordinate = saturate(dot(normal, light) * .5 + def.r - .5 * _CapRampOffset);
    if (skin)
    {
        float3 headNormal = normalize((_CapHeadRight.xyz * i.objectNormal.x + _CapHeadUp.xyz * i.objectNormal.y + _CapHeadForward.xyz * i.objectNormal.z) * sign);
        float headCoordinate = saturate(dot(headNormal, light) * .5 + def.r - .5 * _CapRampOffset);
        coordinate = lerp(coordinate, max(coordinate, headCoordinate), saturate(def.b));
    }
    float4 ramp = tex2D(_CapRamp, float2(coordinate, 0));
    float3 nonSkin = lerp(baseSample.rgb, shade.rgb * _CapShadeTint.rgb, saturate(ramp.a));
    float3 skinColor = baseSample.rgb * ramp.rgb * lerp(1, _CapShadeTint.rgb, saturate(ramp.a));
    float3 diffuse = lerp(nonSkin, skinColor, saturate(shade.a));
    float luma = dot(diffuse, float3(.2126729, .7151522, .0721750));
    diffuse = lerp(luma.xxx, diffuse, max(0, 1 + _CapSkinSaturation * saturate(shade.a))) * _CapTint.rgb;
    // Deliberately retain diffuse even on metallic costume pixels: this cheap
    // capture has no specular/environment substitute to carry that energy.
    float3 color = diffuse * (_CapAmbient.rgb + _CapKey.rgb) + ramp.a * _CapShadeAdditive.rgb;
    if (highlight) color = baseSample.rgb * _CapTint.rgb * _CapKey.rgb;
    color += tex2Dbias(_CapEmission, float4(i.uv, 0, _CapLodBias)).rgb * _CapEmissionTint.rgb * _CapEmissionEnabled;
    color *= _CapActorTint.rgb;
    float alpha = CaptureAlpha(i, baseSample.a);
    if (_CapPremultiply > .5) color *= alpha;
    return float4(max(color, 0), alpha);
}
#endif
