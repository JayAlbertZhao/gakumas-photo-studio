// Independent geometry-guided spatial reconstruction. No temporal history.
sampler2D _GtaoCoarse;
float4 _GtaoCoarseSize, _GtaoReconstruction;
float GtaoReconstruct(float2 uv, float3 world, float3 normal, float depth)
{
    float2 pixel = uv * _GtaoPixelSize.zw - .5;
    float2 first = floor((pixel + .5) * .5 - .5) - 1;
    float sum = 0, weightSum = 0;
    [unroll] for (int y = 0; y < 4; y++)
    [unroll] for (int x = 0; x < 4; x++)
    {
        float2 cell = first + float2(x,y);
        if (all(cell >= 0) && all(cell < _GtaoCoarseSize.zw))
        {
            float4 sample = tex2Dlod(_GtaoCoarse, float4((cell+.5)*_GtaoCoarseSize.xy,0,0));
            if (sample.z > 0)
            {
                float2 sampleUv = (sample.xy+.5)*_GtaoPixelSize.xy;
                float3 n = tex2Dlod(_ScreenGeometry,float4(sampleUv,0,0)).xyz;
                n *= rsqrt(max(dot(n,n),1e-12));
                float3 delta = World(sampleUv,sample.z)-world;
                float separation = max(abs(dot(delta,normal)),abs(dot(delta,n)));
                float2 spatial = saturate(1-abs(sample.xy-pixel)*.25);
                float weight = spatial.x * spatial.y * saturate(1-separation/_GtaoReconstruction.x) *
                    saturate((dot(n,normal)-_GtaoReconstruction.y)/(1-_GtaoReconstruction.y));
                sum += sample.w * weight; weightSum += weight;
            }
        }
    }
    // Thin/isolated receivers without support retain their own horizon query;
    // do not borrow a nearer surface or silently brighten them to unoccluded.
    [branch] if (weightSum <= 1e-6) return GtaoVisibility(uv,world,normal,depth);
    return sum / weightSum;
}
