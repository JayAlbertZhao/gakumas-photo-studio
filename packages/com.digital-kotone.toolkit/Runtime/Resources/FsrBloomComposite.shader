Shader "Hidden/Toolkit/FsrBloomComposite"
{
    Properties { _MainTex ("Linear HDR source", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "FSR_BLOOM_BEFORE_UPSCALE"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            Texture2D<float4> _MainTex, _BloomTex;
            SamplerState sampler_LinearClamp;
            float _BloomWeight;
            float4 frag(v2f_img input) : SV_Target
            {
                float4 value = _MainTex.SampleLevel(sampler_LinearClamp, input.uv, 0);
                value.rgb += _BloomTex.SampleLevel(sampler_LinearClamp, input.uv, 0).rgb * _BloomWeight;
                return value;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
