// Independent implementation of the view-axis, cosine-weighted horizon integral
// in Jimenez et al., ATVI-TR-16-01 section 4. No third-party shader source.
float4x4 _ScreenViewProjection;
float4 _GtaoParameters, _GtaoQuality, _GtaoPixelSize;
float _GtaoMinimumAmbient;
#if defined(SCENE_GTAO_ROTATED)
float _GtaoSliceOffset;
#endif

float GtaoPrimitive(float angle, float nv, float nt)
{
    // Antiderivative of (nv*cos(theta)+nt*sin(theta))*abs(sin(theta)).
    float s = sin(angle);
    return sign(angle) * (.5 * nv * s * s + nt * (.5 * angle - .25 * sin(2 * angle)));
}
float GtaoArc(float lo, float hi, float nv, float nt)
{
    return hi > lo ? GtaoPrimitive(hi, nv, nt) - GtaoPrimitive(lo, nv, nt) : 0;
}
float GtaoHorizon(float2 uv, float3 origin, float3 normal, float3 view, float3 tangent, float2 directionPixels, float searchPixels, float baseCos)
{
    float horizon = baseCos;
    [loop] for (int j = 0; j < (int)_GtaoQuality.y; j++)
    {
        float f = (j + 1.0) / _GtaoQuality.y;
        float offset = 1 + (searchPixels - 1) * f * f;
        float2 sampleUv = uv + directionPixels * offset * _GtaoPixelSize.xy;
        float candidate = baseCos;
        // Do not clamp offscreen samples back onto the border geometry.
        if (all(sampleUv >= 0) && all(sampleUv < 1))
        {
            sampleUv = (floor(sampleUv * _GtaoPixelSize.zw) + .5) * _GtaoPixelSize.xy;
            float depth = tex2Dlod(_ScreenGeometry, float4(sampleUv, 0, 0)).a;
            if (depth > 0 && any(abs(sampleUv - uv) > _GtaoPixelSize.xy * .25))
            {
                float3 delta = World(sampleUv, depth) - origin;
                float distance = length(delta);
                // A snapped texel may lie outside the slice. Reject samples
                // below the true surface tangent BEFORE projecting to the slice;
                // otherwise a coplanar sloped surface can shadow itself.
                if (distance > 1e-6 && distance < _GtaoParameters.x && dot(delta, normal) > 0)
                {
                    // Texel snapping can move a sample out of its slice plane.
                    float axial = dot(delta, view), transverse = dot(delta, tangent);
                    float projectedLength = sqrt(axial * axial + transverse * transverse);
                    if (projectedLength > 1e-6 && transverse > 0)
                    {
                        float fade = saturate((1 - distance / _GtaoParameters.x) / max(1 - _GtaoParameters.w, 1e-6));
                        candidate = max(baseCos, lerp(baseCos, clamp(axial / projectedLength, -1, 1), fade));
                    }
                }
            }
        }
        horizon = candidate >= horizon ? candidate : lerp(horizon, candidate, _GtaoQuality.w);
    }
    return clamp(horizon, -1, 1);
}
float GtaoVisibility(float2 uv, float3 world, float3 normal, float depth)
{
    const float pi = 3.14159265359;
    // The view ray comes from the actual projection, including orthographic,
    // off-axis and oblique matrices, not camera position for every projection.
    float3 view = normalize(World(uv, depth - 1) - world);
    float nv = dot(normal, view);
    if (nv <= 1e-5 || dot(normal, normal) < 1e-6) return 1;
    float3 axis = World(uv + float2(_GtaoPixelSize.x, 0), depth) - world;
    axis = normalize(axis - view * dot(axis, view));
    float3 crossAxis = cross(view, axis), origin = world + normal * _GtaoParameters.z;
    float4 centerClip = mul(_ScreenViewProjection, float4(world, 1));
    float blocked = 0;
    [loop] for (int i = 0; i < (int)_GtaoQuality.x; i++)
    {
        float phi = (i + .5) * pi / _GtaoQuality.x;
        #if defined(SCENE_GTAO_ROTATED)
        phi += _GtaoSliceOffset * pi / _GtaoQuality.x;
        #endif
        float3 tangent = cos(phi) * axis + sin(phi) * crossAxis;
        float4 tangentClip = mul(_ScreenViewProjection, float4(tangent, 0));
        // Local derivative gives a world-radius footprint without projecting
        // an endpoint behind the eye when the AO sphere intersects near plane.
        float2 derivative = .5 * (tangentClip.xy * centerClip.w - centerClip.xy * tangentClip.w) / (centerClip.w * centerClip.w);
        #if UNITY_UV_STARTS_AT_TOP
        derivative.y = -derivative.y;
        #endif
        float2 pixelVector = derivative * _GtaoPixelSize.zw;
        float pixelLength = length(pixelVector);
        float searchPixels = min(pixelLength * _GtaoParameters.x, _GtaoQuality.z);
        if (searchPixels < 1 || pixelLength < 1e-6) continue;
        float2 directionPixels = pixelVector / pixelLength;
        float nt = dot(normal, tangent), gamma = atan2(nt, nv);
        float lo = gamma - pi * .5, hi = gamma + pi * .5;
        float negative = GtaoHorizon(uv, origin, normal, view, -tangent, -directionPixels, searchPixels, cos(lo));
        float positive = GtaoHorizon(uv, origin, normal, view, tangent, directionPixels, searchPixels, cos(hi));
        float visible = GtaoArc(max(lo, -acos(negative)), min(hi, acos(positive)), nv, nt);
        // Integrate blocked arcs and subtract from known unoccluded visibility.
        // This removes finite-slice bias on an isolated sloped plane.
        blocked += max(0, GtaoArc(lo, hi, nv, nt) - visible);
    }
    return lerp(1, saturate(1 - blocked / _GtaoQuality.x), _GtaoParameters.y);
}
