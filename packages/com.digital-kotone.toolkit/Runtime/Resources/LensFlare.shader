Shader "Hidden/GakumasPhotoMode/LensFlare"
{
    Properties { _MainTex("Linear HDR",2D)="white"{} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "LensFlareShared.hlsl"
        ENDCG
        Pass
        {
            Name "CURRENT_SOURCE_VISIBILITY"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment SourceVisibility
            ENDCG
        }
        Pass
        {
            Name "INSTANCED_OPTICAL_ELEMENTS"
            Blend One One, Zero One
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ElementVertex
            #pragma fragment ElementFragment
            ENDCG
        }
        Pass
        {
            Name "HDR_ARTIFACT_RESOLVE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment FlareComposite
            ENDCG
        }
    }
}
