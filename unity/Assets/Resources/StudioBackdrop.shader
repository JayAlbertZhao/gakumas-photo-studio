Shader "GakumasPhotoMode/StudioBackdrop"
{
    Properties
    {
        _TopColor ("Top", Color) = (0.38,0.55,0.84,1)
        _MiddleColor ("Middle", Color) = (0.72,0.66,0.91,1)
        _BottomColor ("Bottom", Color) = (0.98,0.80,0.73,1)
        _GlowColor ("Glow", Color) = (0.55,0.90,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Opaque" }
        Cull Off ZWrite On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _TopColor, _MiddleColor, _BottomColor, _GlowColor;
            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
            fixed4 frag(v2f i):SV_Target
            {
                float low=smoothstep(0.02,0.52,i.uv.y);
                float high=smoothstep(0.48,0.98,i.uv.y);
                fixed3 c=lerp(_BottomColor.rgb,_MiddleColor.rgb,low);
                c=lerp(c,_TopColor.rgb,high);
                float glow=exp(-18.0*dot(i.uv-float2(0.70,0.66),i.uv-float2(0.70,0.66)));
                c+=_GlowColor.rgb*glow*0.12;
                float diagonal=smoothstep(0.49,0.5,frac(i.uv.x*1.3+i.uv.y*0.55))*0.015;
                c+=diagonal;
                return fixed4(c,1);
            }
            ENDCG
        }
    }
}
