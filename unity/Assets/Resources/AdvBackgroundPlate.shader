Shader "GakumasPhotoMode/AdvBackgroundPlate"
{
    Properties
    {
        _MainTex ("Background", 2D) = "white" {}
        _LightmapScaleColor ("Lightmap Scale", Color) = (1,1,1,1)
        _RecoveredAmbientScale ("Recovered Ambient Scale", Float) = 1
        _RecoveredDirectScale ("Recovered Direct Scale", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        // The runtime camera-space quad is viewed from its back face.  The
        // source UI mesh is effectively two-sided for this use, and Unity's
        // Unlit/Texture fallback also rendered the same quad without culling.
        Cull Off
        ZWrite On
        Blend One Zero

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _LightmapScaleColor;
            float _RecoveredAmbientScale;
            float _RecoveredDirectScale;
            float4 _CapturedLightDirection;
            float4 _CapturedLightColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                half3 normal : TEXCOORD1;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.normal = UnityObjectToWorldNormal(input.normal);
                return output;
            }

            half4 frag(v2f input) : SV_Target
            {
                // UI/UIBackground samples an sRGB SRV, so tex2D is already
                // linear here.  Its exact no-keyword Forward PS computes:
                //   sample + max(SH(n), 0) * (sample * .96)^2 * lightmapScale
                // plus an optional main-light branch.  Town-street fog volume
                // components are inactive, therefore fog is deliberately not
                // folded into this material probe.
                half3 sampleColor = tex2D(_MainTex, input.uv).rgb;
                half3 normal = normalize(input.normal);
                half3 surface = sampleColor * 0.96h;
                half3 ambient = max(ShadeSH9(half4(normal, 1.0h)), 0.0h);
                half3 ambientContribution = ambient * surface * surface *
                    _LightmapScaleColor.rgb * _RecoveredAmbientScale;
                half3 lightDirection = normalize(_CapturedLightDirection.xyz);
                half ndotl = saturate(dot(normal, lightDirection));
                half3 directContribution = ndotl * surface *
                    _CapturedLightColor.rgb * _RecoveredDirectScale;
                return half4(sampleColor + ambientContribution + directContribution, 1.0h);
            }
            ENDCG
        }
    }
    Fallback Off
}
