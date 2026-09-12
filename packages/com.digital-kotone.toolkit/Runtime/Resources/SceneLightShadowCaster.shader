Shader "Hidden/GakumasPhotoMode/SceneLightShadowCaster"
{
    Properties { _Cull ("Cull", Float) = 2 }
    SubShader
    {
        Pass
        {
            Name "LIGHT_SOURCE_SHADOW_DEPTH"
            Cull [_Cull] ZTest LEqual ZWrite On Blend Off ColorMask R
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4x4 _ShadowViewProjection;
            float3 _ShadowVertexScale;
            float4 _ShadowUvST;
            float _ShadowFar, _ShadowAlpha, _ShadowCutoff;
            sampler2D _ShadowAlphaMap;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Varying { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float depth : TEXCOORD1; };
            Varying vert(Input input)
            {
                Varying o;
                float4 world = mul(unity_ObjectToWorld, float4(input.vertex.xyz * _ShadowVertexScale, 1));
                o.position = mul(_ShadowViewProjection, world);
                // Perspective clip.w is positive light-view axial distance. Color depth is
                // conventional 0..1, independent of the hardware's reversed-Z attachment.
                o.depth = o.position.w / _ShadowFar;
                o.uv = input.uv * _ShadowUvST.xy + _ShadowUvST.zw; return o;
            }
            float frag(Varying input) : SV_Target
            {
                clip(tex2D(_ShadowAlphaMap, input.uv).a * _ShadowAlpha - _ShadowCutoff);
                return input.depth;
            }
            ENDCG
        }
    }
}
