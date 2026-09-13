Shader "Hidden/GakumasPhotoMode/CrowdNativeReference"
{
    // Fixture-only native vertex producer. The crowd runtime does not load this shader.
    Properties { _Cull("Cull", Float) = 2 _ZWrite("Depth writes", Float) = 1 }
    SubShader
    {
        Pass
        {
            Cull [_Cull] ZTest LEqual ZWrite [_ZWrite] Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ForwardVertex
            #pragma fragment NativeFragment
            #pragma multi_compile_local __ SCENE_LIGHT_SHADOWS
            #pragma multi_compile_local __ SCENE_MAIN_LIGHT_SHADOWS
            #define TOOLKIT_FORWARD_TOON
            #include "Packages/com.digital-kotone.toolkit/Runtime/Resources/SceneForwardLighting.hlsl"
            float _NativeDebug;
            float4 NativeFragment(ForwardVarying input):SV_Target
            {
                if(_NativeDebug==1)return float4(unity_WorldTransformParams.w,input.tangent.w,_HasNormal,1);
                if(_NativeDebug==2)return float4(input.normal,1);
                if(_NativeDebug==3)return input.tangent;
                if(_NativeDebug==4)return float4(input.uv,0,1);
                return ForwardFragment(input);
            }
            ENDCG
        }
    }
}
