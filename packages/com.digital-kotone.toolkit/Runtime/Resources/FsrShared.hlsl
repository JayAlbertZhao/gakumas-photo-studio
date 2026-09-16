// Adapter only. EASU/RCAS are AMD FidelityFX FSR1 (MIT), supplied by UPM Core.
// See the package NOTICE. No original game shader is used or distributed here.
#ifndef TOOLKIT_FSR_SHARED
#define TOOLKIT_FSR_SHARED
#define A_GPU 1
#define A_HLSL 1
#define FSR_EASU_F 1
#define FSR_RCAS_F 1
#define FSR_RCAS_PASSTHROUGH_ALPHA 1
#define PLATFORM_SUPPORT_GATHER 1
#include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/ffx/ffx_a.hlsl"
float4 _FsrOptions; // input encoding, enable RCAS, sharpness stops, accurate normalization
AF1 ToolkitFsrRcasReciprocal(AF1 value)
{
    // Keep AMD's filter and limiters. Refine its normalization precision only:
    // the approximate reciprocal's small DC bias is destructive after inverse
    // HDR compression. The unmodified approximation remains an explicit control.
    return _FsrOptions.w > .5 ? 1.0 / value : APrxMedRcpF1(value);
}
#define APrxMedRcpF1 ToolkitFsrRcasReciprocal
#if defined(TOOLKIT_FSR_STABLE_GRADIENT)
AF1 ToolkitFsrGradientReciprocal(AF1 value)
{
    // Authored opt-in normalization floor, not an upstream AMD default.
    // The prepared luma range is 0..2. Gradients below 2/4096 must not
    // amplify Float32 preparation noise into order-one edge strengths.
    // EASU's other reciprocal inputs (lobe/normalized direction) exceed
    // this floor; its taps, reconstruction and deringing remain unchanged.
    return APrxLoRcpF1(max(value,2.0/4096.0));
}
#define APrxLoRcpF1 ToolkitFsrGradientReciprocal
#endif
#include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/ffx/ffx_fsr1.hlsl"
#undef APrxMedRcpF1
#if defined(TOOLKIT_FSR_STABLE_GRADIENT)
#undef APrxLoRcpF1
#endif

Texture2D<float4> _FsrInput;
SamplerState sampler_LinearClamp;
float4 _FsrInputSize, _FsrOutputSize, _FsrViewport;

float3 FsrBoundInput(float3 value)
{
    // Explicit data hygiene, not a CPU scan or an implicit HDR color-space guess.
    value = float3(isfinite(value.x) ? value.x : 0,
        isfinite(value.y) ? value.y : 0, isfinite(value.z) ? value.z : 0);
    return clamp(value, 0, 65504);
}

float4 FsrPrepare(uint2 pixel)
{
    float4 value = _FsrInput.Load(int3(int2(pixel) + int2(_FsrViewport.xy), 0));
    value.rgb = FsrBoundInput(value.rgb);
    if (_FsrOptions.x > 1.5) value.rgb /= 1 + max(value.r, max(value.g, value.b));
    else value.rgb = saturate(value.rgb);
    if (_FsrOptions.x < .5 || _FsrOptions.x > 1.5) value.rgb = sqrt(value.rgb);
    value.a = isfinite(value.a) ? saturate(value.a) : 0;
    return value;
}

AF4 FsrEasuRF(AF2 p) { return _FsrInput.GatherRed(sampler_LinearClamp, p); }
AF4 FsrEasuGF(AF2 p) { return _FsrInput.GatherGreen(sampler_LinearClamp, p); }
AF4 FsrEasuBF(AF2 p) { return _FsrInput.GatherBlue(sampler_LinearClamp, p); }
void FsrEasuProcessInput(inout AF4 r, inout AF4 g, inout AF4 b) { }
// The SM4.5 gather path is mandatory in both supported backends.

float4 FsrExpand(uint2 pixel)
{
    uint4 c0, c1, c2, c3;
    FsrEasuCon(c0, c1, c2, c3, _FsrInputSize.x, _FsrInputSize.y,
        _FsrInputSize.x, _FsrInputSize.y, _FsrOutputSize.x, _FsrOutputSize.y);
    float3 rgb;
    FsrEasuF(rgb, pixel, c0, c1, c2, c3);
    // Alpha is separate straight-alpha bilinear interpolation, not EASU.
    // Explicit FP32 weights avoid hardware sampler subtexel quantization, which
    // produces >.003 error on alternating alpha even when RGB is accurate.
    float2 position = float2(pixel) * asfloat(c0.xy) + asfloat(c0.zw);
    int2 basePixel = int2(floor(position)); float2 weight = frac(position);
    int2 last = int2(_FsrInputSize.xy) - 1;
    float a = _FsrInput.Load(int3(clamp(basePixel, int2(0, 0), last), 0)).a;
    float b = _FsrInput.Load(int3(clamp(basePixel + int2(1, 0), int2(0, 0), last), 0)).a;
    float c = _FsrInput.Load(int3(clamp(basePixel + int2(0, 1), int2(0, 0), last), 0)).a;
    float d = _FsrInput.Load(int3(clamp(basePixel + int2(1, 1), int2(0, 0), last), 0)).a;
    float alpha = lerp(lerp(a, b, weight.x), lerp(c, d, weight.x), weight.y);
    return float4(rgb, alpha);
}

AF4 FsrRcasLoadF(ASU2 pixel)
{
    return _FsrInput.Load(int3(clamp(pixel, int2(0, 0), int2(_FsrOutputSize.xy) - 1), 0));
}
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) { }

float4 FsrFinish(uint2 pixel)
{
    float4 value;
    if (_FsrOptions.y > .5)
    {
        uint4 constants;
        FsrRcasCon(constants, _FsrOptions.z);
        FsrRcasF(value.r, value.g, value.b, value.a, pixel, constants);
    }
    else value = FsrRcasLoadF(int2(pixel));
    value.rgb = saturate(value.rgb);
    if (_FsrOptions.x < .5 || _FsrOptions.x > 1.5) value.rgb *= value.rgb;
    if (_FsrOptions.x > 1.5)
        value.rgb = min(value.rgb / max(1.0 / 65505.0, 1 - max(value.r, max(value.g, value.b))), 65504);
    return value;
}
#endif
