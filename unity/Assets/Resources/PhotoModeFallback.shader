Shader "GakumasPhotoMode/ActorToon"
{
    Properties
    {
        // Campus uploads _BaseColor as a literal float4.  Declaring this as a
        // ShaderLab Color makes Unity apply an additional gamma-to-linear
        // transform in a Linear project (m_ehl 2.996 became roughly 11), so
        // retain the captured constant-buffer contract as a Vector.
        _Color ("Base Color", Vector) = (1,1,1,1)
        [HideInInspector] _ActorColor ("ADV Actor Color", Vector) = (1,1,1,1)
        _MainTex ("Base", 2D) = "white" {}
        [HideInInspector] _BaseMap_ST ("Original Base UV Transform", Vector) = (1,1,0,0)
        _ShadeTex ("Shade", 2D) = "white" {}
        _DefTex ("Definition", 2D) = "white" {}
        _RampTex ("Diffuse Ramp", 2D) = "white" {}
        _LayerTex ("Face Layer", 2D) = "black" {}
        _HighlightTex ("Highlight", 2D) = "black" {}
        _RampAddTex ("Ramp Add", 2D) = "black" {}
        _BumpMap ("Normal", 2D) = "bump" {}
        _AnisotropicMap ("Anisotropic", 2D) = "black" {}
        _ReflectionSphereMap ("Reflection Sphere", 2D) = "black" {}
        _ReflectionSphereMap_HDR ("Reflection Sphere Decode (scale, exponent, unused, alpha weight)", Vector) = (1,1,0,0)
        _EmissionMap ("Emission", 2D) = "black" {}
        [HideInInspector] _FaceDecalAtlas ("Original Face Decal Atlas", 2D) = "white" {}

        _ShaderType ("Campus Shader Type", Float) = 0
        [Toggle] _DisableDefMap ("Use Constant Definition", Float) = 0
        [Toggle] _OutlineEnabled ("Authored Outline Enabled", Float) = 1
        _OutlineColor ("Outline Color Override", Vector) = (0,0,0,0)
        _HairFadeParameters ("Hair View Fade", Vector) = (0.75,2,0.4,4)
        [HideInInspector] _CapturedType1Variant ("Captured Type 1 Variant", Float) = 0
        _EnableLayer ("Enable Layer", Float) = 0
        _LayerWeight ("Layer Weight", Range(0,1)) = 0
        _VertexColor ("Use Vertex Color", Float) = 1
        _UseBump ("Use Normal", Float) = 0
        _UseAnisotropic ("Use Anisotropic", Float) = 0
        _UseReflection ("Use Reflection", Float) = 0
        _UseEmission ("Use Emission", Float) = 0
        _BumpScale ("Normal Scale", Float) = 1
        _AnisotropicScale ("Anisotropic Scale", Range(-0.95,0.95)) = 0
        _DefValue ("Definition Value", Vector) = (0.5,0,1,0)
        _SpecularThreshold ("Specular Threshold", Vector) = (0.6,0.05,0,0)
        _RampAddColor ("Ramp Add Color", Color) = (1,1,1,1)
        _RimColor ("Authored Rim Color", Color) = (0,0,0,0)
        _EmissionColor ("Emission Color", Color) = (0,0,0,0)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.33
        _UseAlphaClip ("Use Alpha Clip", Float) = 0

        _Cull ("Cull", Float) = 2
        _SrcBlend ("Source Blend", Float) = 1
        _DstBlend ("Destination Blend", Float) = 0
        _SrcAlphaBlend ("Source Alpha Blend", Float) = 1
        _DstAlphaBlend ("Destination Alpha Blend", Float) = 0
        _ZWrite ("Depth Write", Float) = 1
        _ColorMask ("Color Mask", Float) = 15
        _StencilRef ("Stencil Ref", Float) = 64
        _StencilReadMask ("Stencil Read Mask", Float) = 108
        _StencilWriteMask ("Stencil Write Mask", Float) = 96
        _StencilComp ("Stencil Compare", Float) = 8
        _StencilPass ("Stencil Pass", Float) = 2
        _WardrobeScaleCorrection ("Wardrobe Scale Correction", Vector) = (1,1,1,0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 500

        Pass
        {
            Name "ACTOR_FORWARD_HDR"
            Tags { "LightMode"="ForwardBase" }
            Cull [_Cull]
            Blend [_SrcBlend] [_DstBlend], [_SrcAlphaBlend] [_DstAlphaBlend]
            ZWrite [_ZWrite]
            ColorMask [_ColorMask]
            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                WriteMask [_StencilWriteMask]
                Comp [_StencilComp]
                Pass [_StencilPass]
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fwdbase
            #include "ActorSurface.cginc"
            ENDCG
        }


        Pass
        {
            Name "CAPTURED_SHADOW_CASTER"
            Tags { "LightMode"="ShadowCaster" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask 0

            CGPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma target 4.5
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _UseAlphaClip, _Cutoff;
            float4 _ActorColor;
            float4 _CapturedShadowCasterDirection;
            float4 _CapturedShadowCasterBias;
            float4 _WardrobeScaleCorrection;

            struct shadow_appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct shadow_v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            shadow_v2f vertShadow(shadow_appdata input)
            {
                shadow_v2f output;
                input.vertex.xyz *= _WardrobeScaleCorrection.xyz;
                input.normal.xz /= max(_WardrobeScaleCorrection.xz, 0.0001);
                float useCapturedBias = saturate(_CapturedShadowCasterBias.z);

                // Diagnostic fallback to Unity's generic caster bias.
                float4 unityBiasedClip = UnityClipSpaceShadowCasterPos(input.vertex, input.normal);
                unityBiasedClip = UnityApplyLinearShadowBias(unityBiasedClip);

                // Bound caster VS E82E31C9 performs both offsets in world space;
                // all five captured rasterizer states have fixed/slope bias zero.
                float3 worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                float3 worldNormal = normalize(UnityObjectToWorldNormal(input.normal));
                float3 lightDirection = normalize(_CapturedShadowCasterDirection.xyz);
                float normalScale = 1.0 - saturate(dot(lightDirection, worldNormal));
                worldPosition += lightDirection * _CapturedShadowCasterBias.x;
                worldPosition += worldNormal * normalScale * _CapturedShadowCasterBias.y;
                float4 capturedBiasedClip = UnityWorldToClipPos(worldPosition);

                output.pos = lerp(unityBiasedClip, capturedBiasedClip, useCapturedBias);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            float4 fragShadow(shadow_v2f input) : SV_Target
            {
                float actorFadeNoise = frac(52.9829189 * frac(
                    dot(floor(input.pos.xy), float2(0.06711056, 0.00583715))));
                clip(_ActorColor.a - actorFadeNoise);
                if (_UseAlphaClip > 0.5)
                    clip(tex2D(_MainTex, input.uv).a - _Cutoff);
                return 0;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
