Shader "Hidden/GakumasPhotoMode/VolumetricLighting"
{
    Properties { _MainTex("Linear HDR",2D)="white"{} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "VolumetricLightingShared.hlsl"
        ENDCG
        Pass
        {
            Name "SHADOWED_SINGLE_SCATTERING"
            Blend One One, Zero One
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Scatter
            #pragma multi_compile_local _ SCENE_LIGHT_SHADOWS
            ENDCG
        }
        Pass
        {
            Name "FINITE_MEDIUM_COMPOSITE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Composite
            ENDCG
        }
    }
}
