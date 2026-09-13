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
                float3 coordinate=(saturate(domain)*(_LutParameters.x-1)+.5)/_LutParameters.x;
                return float4(_AuthoredLut.SampleLevel(sampler_LinearClamp,coordinate,0).rgb,value.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
