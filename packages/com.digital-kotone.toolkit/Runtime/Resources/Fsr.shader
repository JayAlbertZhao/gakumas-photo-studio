Shader "Hidden/Toolkit/Fsr"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        HLSLINCLUDE
        #pragma target 4.5
        #include "UnityCG.cginc"
        #include "FsrShared.hlsl"
        float4 PrepareFragment(v2f_img input) : SV_Target { return FsrPrepare(uint2(input.pos.xy)); }
        float4 ExpandFragment(v2f_img input) : SV_Target { return FsrExpand(uint2(input.pos.xy)); }
        float4 FinishFragment(v2f_img input) : SV_Target { return FsrFinish(uint2(input.pos.xy)); }
        ENDHLSL
        Pass
        {
            Name "FSR_PREPARE"
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment PrepareFragment
            ENDHLSL
        }
        Pass
        {
            Name "FSR_EASU"
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment ExpandFragment
            #pragma multi_compile_local _ TOOLKIT_FSR_STABLE_GRADIENT
            ENDHLSL
        }
        Pass
        {
            Name "FSR_RCAS_DECODE"
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment FinishFragment
            ENDHLSL
        }
    }
    Fallback Off
}
