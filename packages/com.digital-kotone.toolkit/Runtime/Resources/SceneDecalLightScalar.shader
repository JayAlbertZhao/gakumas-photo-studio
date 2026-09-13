Shader "Hidden/GakumasPhotoMode/SceneDecalLightScalar"
{
    SubShader
    {
        Pass
        {
            Name "SCALAR_LIGHT_VOLUME"
            Cull Off ZTest Always ZWrite Off Blend One One ColorMask RGB
            CGPROGRAM
            #pragma multi_compile_local __ SCENE_BAKED_SHADOW_PACKED
            #pragma multi_compile_local __ SCENE_BAKED_LIGHT_CHANNELS
            #pragma target 3.0
            #pragma vertex LightVert
            #pragma fragment LightFrag
            #pragma multi_compile_local __ SCENE_LIGHT_SHADOWS
            #include "SceneDecalLight.hlsl"
            ENDCG
        }
    }
}
