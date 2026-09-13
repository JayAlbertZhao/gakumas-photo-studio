Shader "Hidden/GakumasPhotoMode/HeavyFxOptics"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "JOINT_SHARED_OPTICAL_ELEMENTS"
            Blend One One, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ElementVertex
            #pragma fragment JointElement
            #define HEAVY_FX_GRID
            #include "LowResolutionFxShared.hlsl"
            #define _MainTex _HeavyUnusedOpticalSource
            #include "LensFlareShared.hlsl"
            #undef _MainTex
            float4 JointElement(ElementVarying input):SV_Target
            {
                if(_FxInput.w>1.5&&!FxNeedsRepair(input.position.xy,false))return 0;
                if(_FxInput.w!=1&&FxProtected((int2)input.position.xy))return 0;
                return ElementFragment(input);
            }
            ENDCG
        }
    }
}
