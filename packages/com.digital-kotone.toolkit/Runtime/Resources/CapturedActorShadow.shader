Shader "GakumasPhotoMode/CapturedActorShadow"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.33
        _UseAlphaClip ("Use Alpha Clip", Float) = 0
        _ShaderType ("Campus Shader Type", Float) = 0
        _Cull ("Cull", Float) = 2
        _WardrobeScaleCorrection ("Wardrobe Scale Correction", Vector) = (1,1,1,0)
        _ActorColor ("ADV Actor Color", Vector) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask R

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff;
            float _UseAlphaClip;
            float _ShaderType;
            float _CapturedShadowDiagnosticType;
            float4 _CapturedShadowCasterDirection;
            float4 _CapturedShadowCasterBias;
            float4x4 _CapturedActorWorldToShadow;
            float _UseExactCapturedActorShadowMatrix;
            float4 _WardrobeScaleCorrection;
            float4 _ActorColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float depth : TEXCOORD1;
            };

            v2f vert(appdata input)
            {
                v2f output;
                input.vertex.xyz *= _WardrobeScaleCorrection.xyz;
                input.normal.xz /= max(_WardrobeScaleCorrection.xz, 0.0001);
                float3 worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                float3 worldNormal = UnityObjectToWorldNormal(input.normal);
                float3 lightDirection = normalize(_CapturedShadowCasterDirection.xyz);
                float normalScale = 1.0 - saturate(dot(lightDirection, worldNormal));
                worldPosition += lightDirection * _CapturedShadowCasterBias.x;
                worldPosition += worldNormal * normalScale * _CapturedShadowCasterBias.y;
                float4 cameraClip = mul(UNITY_MATRIX_VP, float4(worldPosition, 1.0));
                float3 capturedUVW = mul(_CapturedActorWorldToShadow, float4(worldPosition, 1.0)).xyz;
                // D3D viewport Y runs from clip +1 at the top to -1 at the
                // bottom, matching the captured texture's v=0 top convention.
                float4 capturedClip = float4(
                    capturedUVW.x * 2.0 - 1.0,
                    1.0 - capturedUVW.y * 2.0,
                    capturedUVW.z,
                    1.0);
                output.pos = lerp(cameraClip, capturedClip, saturate(_UseExactCapturedActorShadowMatrix));
                output.depth = lerp(cameraClip.z / cameraClip.w, capturedUVW.z,
                                    saturate(_UseExactCapturedActorShadowMatrix));
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            float frag(v2f input) : SV_Target
            {
                float actorFadeNoise = frac(52.9829189 * frac(
                    dot(floor(input.pos.xy), float2(0.06711056, 0.00583715))));
                clip(_ActorColor.a - actorFadeNoise);
                // Negative disables the diagnostic.  A non-negative value drops
                // one original material class while retaining source-material
                // properties through RenderWithShader.
                if (_CapturedShadowDiagnosticType > 99.0)
                {
                    float retainedType = _CapturedShadowDiagnosticType - 100.0;
                    if (abs(_ShaderType - retainedType) >= 0.25) clip(-1.0);
                }
                else if (_CapturedShadowDiagnosticType >= -0.25 &&
                         abs(_ShaderType - _CapturedShadowDiagnosticType) < 0.25)
                {
                    clip(-1.0);
                }
                float alpha = tex2D(_MainTex, input.uv).a;
                if (_UseAlphaClip > 0.5) clip(alpha - _Cutoff);
                return input.depth;
            }
            ENDCG
        }
    }
}
