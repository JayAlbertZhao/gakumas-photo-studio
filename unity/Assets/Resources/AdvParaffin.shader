Shader "Hidden/Kotone/AdvParaffin"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _FlareTransform;
            float4 _FlareColor0;
            float4 _FlareColor1;

            float4 frag(v2f_img input) : SV_Target
            {
                // Exact PS F3F99D487EFC4346: offset around the authored screen
                // centre, anisotropic inverse-size scale, radial clamp, gradient,
                // then premultiplied-alpha over the existing camera color.
                float2 flareUv = (input.uv + _FlareTransform.xy - 0.5) *
                    _FlareTransform.zw;
                float radius = min(length(flareUv), 1.0);
                float alpha = 1.0 - radius;
                float3 flareColor = lerp(
                    _FlareColor0.rgb, _FlareColor1.rgb, radius) * alpha;
                float4 source = tex2D(_MainTex, input.uv);
                source.rgb = flareColor + source.rgb * (1.0 - alpha);
                return source;
            }
            ENDHLSL
        }
    }
}
