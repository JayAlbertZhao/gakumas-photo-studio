Shader "Hidden/GakumasPhotoMode/TemporalClassification"
{
    Properties { _Cull ("Cull", Float) = 2 }
    SubShader
    {
        Pass
        {
            Cull [_Cull] ZTest LEqual ZWrite Off
            Blend One One
            BlendOp Max
            ColorMask R
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _TemporalAlphaMask;
            float4 _TemporalAlphaMaskST;
            float3 _TemporalVertexScale;
            float _TemporalFlags, _TemporalAlphaCutoff;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata input)
            {
                v2f output;
                input.vertex.xyz *= _TemporalVertexScale;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv * _TemporalAlphaMaskST.xy + _TemporalAlphaMaskST.zw;
                return output;
            }
            float4 frag(v2f input) : SV_Target
            {
                clip(tex2D(_TemporalAlphaMask, input.uv).a - _TemporalAlphaCutoff);
                // Values 4/6 both select NoJitter, which takes precedence over 2.
                return float4(_TemporalFlags, 0, 0, 0);
            }
            ENDCG
        }
    }
}
