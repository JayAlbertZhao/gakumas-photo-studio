Shader "GakumasPhotoMode/StudioFloor"
{
    Properties
    {
        _Color ("Color", Color) = (0.77,0.73,0.87,1)
        _Glossiness ("Smoothness", Range(0,1)) = 0.28
        _Metallic ("Metallic", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        fixed4 _Color;
        half _Glossiness;
        half _Metallic;
        struct Input { float3 worldPos; };
        void surf(Input i, inout SurfaceOutputStandard o)
        {
            float ring=sin(length(i.worldPos.xz)*4.5)*0.5+0.5;
            o.Albedo=_Color.rgb*(0.97+ring*0.025);
            o.Metallic=_Metallic;
            o.Smoothness=_Glossiness;
            o.Alpha=1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
