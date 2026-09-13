Shader "GakumasPhotoMode/FogVolumeSurface"
{
    // Vector avoids Unity's automatic sRGB conversion of ShaderLab Color properties.
    Properties { _LinearColor("Linear RGB and coverage",Vector)=(1,1,1,0.5) }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off ZWrite Off ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Pass
        {
            Name "FOG_FORWARD_SURFACE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "FogVolume.hlsl"
            float4 _LinearColor;
            struct Vertex { float4 vertex:POSITION; };
            struct Varying { float4 pos:SV_POSITION;float3 world:TEXCOORD0; };
            Varying vert(Vertex v){Varying o;o.pos=UnityObjectToClipPos(v.vertex);o.world=mul(unity_ObjectToWorld,v.vertex).xyz;return o;}
            float4 frag(Varying i):SV_Target
            {
                float3 start;float4 fog=float4(0,0,0,1);if(FogNear(i.pos.xy,start))fog=FogIntegrate(start,i.world,false);
                return float4(_LinearColor.rgb*fog.a+fog.rgb,saturate(_LinearColor.a));
            }
            ENDCG
        }
    }
}
