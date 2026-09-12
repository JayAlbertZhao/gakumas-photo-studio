Shader "Hidden/GakumasPhotoMode/SceneDecalLightScalar"
{
    SubShader
    {
        Pass
        {
            Name "SCALAR_LIGHT_VOLUME"
            Cull Off ZTest Always ZWrite Off Blend One One ColorMask RGB
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex LightVert
            #pragma fragment LightFrag
            #include "SceneDecalLight.hlsl"
            ENDCG
        }
    }
}
