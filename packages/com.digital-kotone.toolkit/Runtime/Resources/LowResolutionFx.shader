Shader "Hidden/GakumasPhotoMode/LowResolutionFx"
{
    Properties { _MainTex("Linear HDR",2D)="white"{} _Cull("Cull",Float)=0 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "LowResolutionFxShared.hlsl"
        ENDCG
        Pass
        {
            Name "FX_OPAQUE_DEPTH_RANGE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ReduceDepth
            ENDCG
        }
        Pass
        {
            Name "FX_LOW_PREMULTIPLIED_COLOR"
            Cull [_Cull] Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SurfaceVertex
            #pragma fragment SurfaceColor
            ENDCG
        }
        Pass
        {
            Name "FX_FULL_COLOR_OR_EDGE_REPLAY"
            Cull [_Cull] Blend One OneMinusSrcAlpha, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SurfaceVertex
            #pragma fragment SurfaceColor
            ENDCG
        }
        Pass
        {
            Name "FX_DISTORTION_FIELD"
            Cull [_Cull] Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SurfaceVertex
            #pragma fragment DistortionField
            ENDCG
        }
        Pass
        {
            Name "FX_UPSCALE_COLOR"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ResolveColor
            ENDCG
        }
        Pass
        {
            Name "FX_UPSCALE_DISTORTION"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ResolveDistortion
            ENDCG
        }
        Pass
        {
            Name "FX_DISTORTION_EDGE_REPLAY"
            Cull [_Cull] Blend One OneMinusSrcAlpha, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SurfaceVertex
            #pragma fragment DistortionReplay
            ENDCG
        }
        Pass
        {
            Name "FX_EXACT_HDR_COPY"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment CopyColor
            ENDCG
        }
    }
}
