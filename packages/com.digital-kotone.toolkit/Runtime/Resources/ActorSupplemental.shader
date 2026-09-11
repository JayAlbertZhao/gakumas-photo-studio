Shader "Hidden/PhotoStudio/ActorSupplemental"
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
        [HideInInspector] _ActorTextureFrame ("Motion Atlas Frame (zero disables)", Vector) = (0,0,0,0)
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
        Pass
        {
            Name "ACTOR_OUTLINE"
            Tags { "LightMode"="Always" }
            Cull Front
            // Opaque outlines own their depth, including between submeshes.
            // Preserve an explicit material opt-out rather than disabling
            // depth writes for every outline and relying on hierarchy order.
            ZWrite [_ZWrite]
            ZTest LEqual
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex outlineVertex
            #pragma fragment outlineFragment
            #include "ActorOutline.cginc"
            ENDCG
        }

        Pass
        {
            Name "ACTOR_HAIR_COVER"
            Tags { "LightMode"="Always" }
            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha, Zero One
            // Complement the opaque hair's Ref >= masked-stencil test.
            // Eye-white and eyebrow regions use distinct bits (4 and 8).
            // Preserve those bits, but write hair depth even when color fades.
            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                WriteMask 0
                Comp Less
                Pass Keep
            }
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #define ACTOR_HAIR_COVER 1
            #include "ActorSurface.cginc"
            ENDCG
        }

    }
}
