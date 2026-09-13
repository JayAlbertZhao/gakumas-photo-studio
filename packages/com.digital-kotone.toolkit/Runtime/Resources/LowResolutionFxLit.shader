Shader "Hidden/GakumasPhotoMode/LowResolutionFxLit"
{
    Properties { _Cull("Cull",Float)=0 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "FxForwardSurface.hlsl"
        ENDCG
        Pass
        {
            Name "FX_LIT_WORKING_GEOMETRY"
            Cull [_Cull] Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma multi_compile_local __ SCENE_BAKED_SHADOW_INPUT
            #pragma multi_compile_local __ SCENE_BAKED_LIGHT_CHANNELS
            #pragma target 4.5
            #pragma multi_compile_local __ FX_LIT_LOCAL_SHADOWS
            #pragma multi_compile_local __ FX_LIT_MAIN_SHADOWS
            #pragma vertex FxLitVertexProgram
            #pragma fragment FxLitSurfaceColor
            ENDCG
        }
        Pass
        {
            Name "FX_LIT_FULL_OR_REPLAY"
            Cull [_Cull] Blend One OneMinusSrcAlpha, Zero One ColorMask RGB
            CGPROGRAM
            #pragma multi_compile_local __ SCENE_BAKED_SHADOW_INPUT
            #pragma multi_compile_local __ SCENE_BAKED_LIGHT_CHANNELS
            #pragma target 4.5
            #pragma multi_compile_local __ FX_LIT_LOCAL_SHADOWS
            #pragma multi_compile_local __ FX_LIT_MAIN_SHADOWS
            #pragma vertex FxLitVertexProgram
            #pragma fragment FxLitSurfaceColor
            ENDCG
        }
    }
}
