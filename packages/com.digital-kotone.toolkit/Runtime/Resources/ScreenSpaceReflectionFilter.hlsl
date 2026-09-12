#ifndef TOOLKIT_SSR_FILTER_INCLUDED
#define TOOLKIT_SSR_FILTER_INCLUDED
// Uses explicit geometry/camera declarations from ScreenSpaceReflectionTrace.
Texture2D<float4> _SsrFilterInput;
float4 _SsrFilterOptions; // Radius at target height, plane tolerance, normal dot, smoothness tolerance.
float4 _SsrFilterAxis;

float4 FilterSceneReflection(int2 pixel)
{
    float4 center = _SsrFilterInput.Load(int3(pixel, 0));
    float4 receiver = _SsrVisibility.Load(int3(pixel, 0)); // R eligibility, G smoothness, B receiver ID.
    if (receiver.r < .5 || receiver.b < .5 || center.a <= 1e-5) return 0;
    if (_SsrPlanarAvailable > .5 && _SsrPlanarCoverage.Load(int3(pixel, 0)).a > 1e-5) return 0;
    float radius = _SsrFilterOptions.x * (1 - receiver.g) * (1 - receiver.g);
    if (radius <= 1e-5) return center;
    float2 uv = (float2(pixel) + .5) * _SsrSize.zw;
    float3 position = PositionAtDepth(uv, _SsrDepth0.Load(int3(pixel, 0)));
    float3 normal = normalize(mul((float3x3)_SsrView, _SsrNormalMask.Load(int3(pixel, 0)).rgb * 2 - 1));
    float3 sum = 0; float trustedWeight = 0, totalWeight = 0;
    [loop] for (int offset = -12; offset <= 12; offset++)
    {
        float weight = max(0, radius + 1 - abs(offset));
        if (weight <= 0) continue;
        int2 samplePixel = pixel + (int2)_SsrFilterAxis.xy * offset;
        if (any(samplePixel < 0) || any(samplePixel >= (int2)_SsrSize.xy)) continue;
        float4 other = _SsrVisibility.Load(int3(samplePixel, 0));
        if (other.r < .5 || abs(other.b - receiver.b) > .25 || abs(other.g - receiver.g) > _SsrFilterOptions.w) continue;
        if (_SsrPlanarAvailable > .5 && _SsrPlanarCoverage.Load(int3(samplePixel, 0)).a > 1e-5) continue;
        float3 otherNormal = normalize(mul((float3x3)_SsrView, _SsrNormalMask.Load(int3(samplePixel, 0)).rgb * 2 - 1));
        if (dot(normal, otherNormal) < _SsrFilterOptions.z) continue;
        float3 otherPosition = PositionAtDepth((float2(samplePixel) + .5) * _SsrSize.zw, _SsrDepth0.Load(int3(samplePixel, 0)));
        float3 delta = otherPosition - position;
        if (max(abs(dot(delta, normal)), abs(dot(delta, otherNormal))) > _SsrFilterOptions.y) continue;
        float4 sampleValue = _SsrFilterInput.Load(int3(samplePixel, 0));
        float trust = saturate(sampleValue.a);
        totalWeight += weight;
        trustedWeight += weight * trust;
        sum += sampleValue.rgb * (weight * trust);
    }
    if (trustedWeight <= 1e-5 || totalWeight <= 1e-5) return 0;
    // Straight HDR radiance; misses reduce confidence, never contribute black
    // radiance or resurrect an invalid center. Confidence cannot increase.
    return float4(sum / trustedWeight, min(center.a, trustedWeight / totalWeight));
}
#endif
