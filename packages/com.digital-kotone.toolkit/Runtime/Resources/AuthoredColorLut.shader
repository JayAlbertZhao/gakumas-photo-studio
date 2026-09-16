Shader "Hidden/DigitalKotone/AuthoredColorLut"
{
    Properties { _MainTex ("Linear HDR source", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "AUTHORED_COLOR_LUT"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma multi_compile_local _ TOOLKIT_LUT_EXPLICIT_TRILINEAR
            #include "UnityCG.cginc"
            Texture2D<float4> _MainTex;
            Texture3D<float4> _AuthoredLut;
            SamplerState sampler_PointClamp, sampler_LinearClamp;
            float4 _LutParameters; // size, maximum linear input, domain, reciprocal log2(1+maximum)
            float4 frag(v2f_img input):SV_Target
            {
                float4 value=_MainTex.SampleLevel(sampler_PointClamp,input.uv,0);
                float3 clean=float3(isnan(value.r)?0:value.r,isnan(value.g)?0:value.g,isnan(value.b)?0:value.b);
                float3 bounded=clamp(clean,0,_LutParameters.y);
                float3 domain=_LutParameters.z>.5?log2(1+bounded)*_LutParameters.w:bounded/_LutParameters.y;
                #if defined(TOOLKIT_LUT_EXPLICIT_TRILINEAR)
                float3 position=saturate(domain)*(_LutParameters.x-1);
                int3 low=(int3)floor(position),high=min(low+1,(int)_LutParameters.x-1);
                float3 weight=position-low;
                float3 z0=lerp(
                    lerp(_AuthoredLut.Load(int4(low.x,low.y,low.z,0)).rgb,_AuthoredLut.Load(int4(high.x,low.y,low.z,0)).rgb,weight.x),
                    lerp(_AuthoredLut.Load(int4(low.x,high.y,low.z,0)).rgb,_AuthoredLut.Load(int4(high.x,high.y,low.z,0)).rgb,weight.x),weight.y);
                float3 z1=lerp(
                    lerp(_AuthoredLut.Load(int4(low.x,low.y,high.z,0)).rgb,_AuthoredLut.Load(int4(high.x,low.y,high.z,0)).rgb,weight.x),
                    lerp(_AuthoredLut.Load(int4(low.x,high.y,high.z,0)).rgb,_AuthoredLut.Load(int4(high.x,high.y,high.z,0)).rgb,weight.x),weight.y);
                return float4(lerp(z0,z1,weight.z),value.a);
                #else
                float3 coordinate=(saturate(domain)*(_LutParameters.x-1)+.5)/_LutParameters.x;
                return float4(_AuthoredLut.SampleLevel(sampler_LinearClamp,coordinate,0).rgb,value.a);
                #endif
            }
            ENDHLSL
        }
    }
    Fallback Off
}
