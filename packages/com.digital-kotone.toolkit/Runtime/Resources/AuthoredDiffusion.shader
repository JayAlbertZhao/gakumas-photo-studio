Shader "Hidden/GakumasPhotoMode/AuthoredDiffusion"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        CGINCLUDE
        #include "UnityCG.cginc"
        UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_DiffusionSource);
        UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_DiffusionBlur);
        float4 _DiffusionSourceSize,_DiffusionTargetSize,_DiffusionBlurSize,_DiffusionStep;
        float _DiffusionIntensity;
        float4 Vertex(float4 position:POSITION):SV_POSITION{return float4(position.xy,0,1);}
        float3 Clean(float3 v){return float3(isfinite(v.x)?clamp(v.x,0,65504):0,isfinite(v.y)?clamp(v.y,0,65504):0,isfinite(v.z)?clamp(v.z,0,65504):0);}
        float3 Source(int2 p){return Clean(_DiffusionSource.Load(int3(clamp(p,0,(int2)_DiffusionSourceSize.xy-1),0)).rgb);}
        float3 Blur(int2 p){return _DiffusionBlur.Load(int3(clamp(p,0,(int2)_DiffusionBlurSize.xy-1),0)).rgb;}
        float3 SampleSource(float2 uv)
        {
            float2 p=uv*_DiffusionSourceSize.xy-.5;int2 a=(int2)floor(p);float2 f=frac(p);
            return lerp(lerp(Source(a),Source(a+int2(1,0)),f.x),lerp(Source(a+int2(0,1)),Source(a+1),f.x),f.y);
        }
        float3 SampleBlur(float2 uv)
        {
            float2 p=uv*_DiffusionBlurSize.xy-.5;int2 a=(int2)floor(p);float2 f=frac(p);
            return lerp(lerp(Blur(a),Blur(a+int2(1,0)),f.x),lerp(Blur(a+int2(0,1)),Blur(a+1),f.x),f.y);
        }
        float4 Reduce(float4 pixel:SV_POSITION):SV_Target
        {
            float2 uv=pixel.xy/_DiffusionTargetSize.xy;
            // Destination footprint; factor1 is a true copy, not an extra blur.
            if(all(_DiffusionSourceSize.xy==_DiffusionTargetSize.xy))return float4(Source((int2)pixel.xy),1);
            float2 o=.25/_DiffusionTargetSize.xy;
            return float4(.25*(SampleSource(uv-o)+SampleSource(uv+o)+SampleSource(uv+float2(-o.x,o.y))+SampleSource(uv+float2(o.x,-o.y))),1);
        }
        float4 Convolve(float4 pixel:SV_POSITION):SV_Target
        {
            if(all(_DiffusionStep.xy==0))return float4(Source((int2)pixel.xy),1);
            float2 uv=pixel.xy/_DiffusionTargetSize.xy,step=_DiffusionStep.xy;
            return float4((SampleSource(uv-step*2)+4*SampleSource(uv-step)+6*SampleSource(uv)+4*SampleSource(uv+step)+SampleSource(uv+step*2))/16,1);
        }
        float4 Composite(float4 pixel:SV_POSITION):SV_Target
        {
            float4 source=_DiffusionSource.Load(int3((int2)pixel.xy,0));
            if(_DiffusionIntensity==0)return source;
            // Finite HDR source is a caller precondition. Only the blur branch is
            // sanitized. Never clamp the base image or diffuse coverage alpha.
            float3 blur=all(_DiffusionBlurSize.xy==_DiffusionTargetSize.xy)?Blur((int2)pixel.xy):SampleBlur(pixel.xy/_DiffusionTargetSize.xy);
            return float4(source.rgb+_DiffusionIntensity*max(blur-max(source.rgb,0),0),source.a);
        }
        ENDCG
        Pass
        {
            Name "DIFFUSION_REDUCE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Reduce
            ENDCG
        }
        Pass
        {
            Name "DIFFUSION_BINOMIAL"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Convolve
            ENDCG
        }
        Pass
        {
            Name "DIFFUSION_POSITIVE_HDR_LIFT"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Composite
            ENDCG
        }
    }
}
