Shader "Hidden/GakumasPhotoMode/SceneForwardLighting"
{
    Properties { _Cull("Cull", Float) = 2 _DestinationBlend("Destination blend", Float) = 10 }
    SubShader
    {
        Pass
        {
            Name "TRANSPARENT_CURRENT_GEOMETRY_FORWARD_PLUS"
            Cull [_Cull] ZTest LEqual ZWrite Off
            Blend One [_DestinationBlend], One [_DestinationBlend]
            CGPROGRAM
            #pragma multi_compile_local __ SCENE_BAKED_SHADOW_INPUT
            #pragma multi_compile_local __ SCENE_BAKED_LIGHT_CHANNELS
            #pragma target 4.5
            #pragma vertex ForwardVertex
            #pragma fragment ForwardFragment
            #pragma multi_compile_local __ SCENE_LIGHT_SHADOWS
            #pragma multi_compile_local __ SCENE_MAIN_LIGHT_SHADOWS
            #include "SceneForwardLighting.hlsl"
            ENDCG
        }
    }
}
