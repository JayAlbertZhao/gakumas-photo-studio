Shader "Hidden/GakumasPhotoMode/AuthoredBloom"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        CGINCLUDE
        #include "UnityCG.cginc"
        UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_BloomSource);
        UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_BloomLow);
        float4 _BloomSourceSize,_BloomLowSize,_BloomTargetSize,_BloomSettings;
        float4 Vertex(float4 position:POSITION):SV_POSITION{return float4(position.xy,0,1);}
        float3 Clean(float3 v)
        {
            return float3(isfinite(v.x)?clamp(v.x,0,65504):0,isfinite(v.y)?clamp(v.y,0,65504):0,isfinite(v.z)?clamp(v.z,0,65504):0);
        }
        float3 Source(int2 p){return Clean(_BloomSource.Load(int3(clamp(p,0,(int2)_BloomSourceSize.xy-1),0)).rgb);}
        float3 Low(int2 p){return _BloomLow.Load(int3(clamp(p,0,(int2)_BloomLowSize.xy-1),0)).rgb;}
        float3 SampleSource(float2 uv)
        {
            float2 p=uv*_BloomSourceSize.xy-.5;int2 a=(int2)floor(p);float2 f=frac(p);
            return lerp(lerp(Source(a),Source(a+int2(1,0)),f.x),lerp(Source(a+int2(0,1)),Source(a+1),f.x),f.y);
        }
        float3 SampleLow(float2 uv)
        {
            float2 p=uv*_BloomLowSize.xy-.5;int2 a=(int2)floor(p);float2 f=frac(p);
            return lerp(lerp(Low(a),Low(a+int2(1,0)),f.x),lerp(Low(a+int2(0,1)),Low(a+1),f.x),f.y);
        }
        float3 Four(float2 uv)
        {
            float2 o=.5/_BloomSourceSize.xy;
            return .25*(SampleSource(uv+float2(-o.x,-o.y))+SampleSource(uv+float2(o.x,-o.y))+
                SampleSource(uv+float2(-o.x,o.y))+SampleSource(uv+o));
        }
        float4 Prefilter(float4 pixel:SV_POSITION):SV_Target
        {
            float3 c=Four(pixel.xy/_BloomTargetSize.xy);float b=max(c.r,max(c.g,c.b));
            float k=_BloomSettings.x*_BloomSettings.y;
            float soft=clamp(b-_BloomSettings.x+k,0,2*k);
            soft=k>0?soft*soft/(4*k):0;
            return float4(c*(max(b-_BloomSettings.x,soft)/max(b,1e-20)),1);
        }
        float4 Downsample(float4 pixel:SV_POSITION):SV_Target{return float4(Four(pixel.xy/_BloomTargetSize.xy),1);}
        float4 Upsample(float4 pixel:SV_POSITION):SV_Target
        {
            float3 high=Source((int2)pixel.xy),low=SampleLow(pixel.xy/_BloomTargetSize.xy);
            return float4(high+(low-high)*_BloomSettings.z,1);
        }
        float4 Composite(float4 pixel:SV_POSITION):SV_Target
        {
            float4 current=_BloomSource.Load(int3((int2)pixel.xy,0));
            return float4(current.rgb+SampleLow(pixel.xy/_BloomTargetSize.xy)*_BloomSettings.w,current.a);
        }
        ENDCG
        Pass
        {
            Name "AUTHORED_THRESHOLD"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Prefilter
            ENDCG
        }
        Pass
        {
            Name "PYRAMID_DOWNSAMPLE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Downsample
            ENDCG
        }
        Pass
        {
            Name "NORMALIZED_SCATTER_UPSAMPLE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Upsample
            ENDCG
        }
        Pass
        {
            Name "SINGLE_HDR_COMPOSITION"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Composite
            ENDCG
        }
    }
}
