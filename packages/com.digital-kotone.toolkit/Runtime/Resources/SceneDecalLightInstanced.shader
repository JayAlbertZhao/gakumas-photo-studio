Shader "Hidden/GakumasPhotoMode/SceneDecalLightInstanced"
{
    SubShader
    {
        Pass
        {
            Name "INSTANCED_LIGHT_VOLUMES"
            Cull Off ZTest Always ZWrite Off Blend One One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex LightVert
            #pragma fragment LightFrag
            #define SCENE_LIGHT_INSTANCED 1
            #include "SceneDecalLight.hlsl"
            ENDCG
        }
    }
}
