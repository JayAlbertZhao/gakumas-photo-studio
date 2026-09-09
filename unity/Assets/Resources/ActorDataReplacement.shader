Shader "Hidden/GakumasPhotoMode/ActorDataReplacement"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
        [HideInInspector] _BaseMap_ST ("Original Base UV Transform", Vector) = (1,1,0,0)
        _UseAlphaClip ("Use Alpha Clip", Float) = 0
        _Cutoff ("Cutoff", Range(0,1)) = 0.33
        _Cull ("Cull", Float) = 2
        _ShaderType ("Campus Shader Type", Float) = 0
        _WardrobeScaleCorrection ("Wardrobe Scale Correction", Vector) = (1,1,1,0)
        _ActorColor ("ADV Actor Color", Vector) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _BaseMap_ST;
            float _UseAlphaClip, _Cutoff, _ShaderType;
            float4 _WardrobeScaleCorrection;
            float4 _ActorColor;
            struct appdata { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float3 normal:TEXCOORD0; float2 uv:TEXCOORD1; };
            v2f vert(appdata input)
            {
                v2f output;
                input.vertex.xyz *= _WardrobeScaleCorrection.xyz;
                input.normal.xz /= max(_WardrobeScaleCorrection.xz, 0.0001);
                output.pos = UnityObjectToClipPos(input.vertex);
                output.normal = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, input.normal));
                float2 ordinaryUv = TRANSFORM_TEX(input.uv, _MainTex);
                float eyeHighlight = 1.0 - step(0.25, abs(_ShaderType - 5.0));
                output.uv = lerp(ordinaryUv,
                    input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw,
                    eyeHighlight);
                return output;
            }
            float4 frag(v2f input):SV_Target
            {
                float actorFadeNoise = frac(52.9829189 * frac(
                    dot(floor(input.pos.xy), float2(0.06711056, 0.00583715))));
                clip(_ActorColor.a - actorFadeNoise);
                float alpha = tex2D(_MainTex, input.uv).a;
                if (_UseAlphaClip > 0.5) clip(alpha - _Cutoff);
                // The original Actor MRT packs actor/material flags beside motion and
                // depth. Preserve a compact material discriminator in A so later actor
                // diffusion and outlines do not have to infer skin from final color.
                float materialCode = (clamp(floor(_ShaderType + 0.5), 0.0, 14.0) + 1.0) / 16.0;
                return float4(input.normal * 0.5 + 0.5, materialCode);
            }
            ENDCG
        }
    }
}
