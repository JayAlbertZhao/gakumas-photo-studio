Shader "Hidden/GakumasPhotoMode/SceneDecalLightInstanced"
{
    SubShader
    {
        Pass
        {
            Name "INSTANCED_LIGHT_VOLUMES"
            Cull Off ZTest Always ZWrite Off Blend One One ColorMask RGB
            CGPROGRAM
            #pragma multi_compile_local __ SCENE_BAKED_SHADOW_PACKED
            #pragma multi_compile_local __ SCENE_BAKED_LIGHT_CHANNELS
            #pragma target 4.5
            #pragma vertex LightVert
            #pragma fragment LightFrag
            #pragma multi_compile_local __ SCENE_LIGHT_SHADOWS
            #pragma multi_compile_local __ SCENE_LEAF_LIGHTING
            #define SCENE_LIGHT_INSTANCED 1
            #include "SceneDecalLight.hlsl"
            ENDCG
        }
    }
}
