Shader "Hidden/GakumasPhotoMode/PhotoColorGrade"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Exposure, _Saturation, _Contrast, _Vignette;
            fixed4 frag(v2f_img i):SV_Target
            {
                fixed4 src=tex2D(_MainTex,i.uv);
                float3 c=src.rgb*_Exposure;
                float luma=dot(c,float3(0.2126,0.7152,0.0722));
                c=lerp(luma.xxx,c,_Saturation);
                c=(c-0.5)*_Contrast+0.5;
                float2 p=i.uv*2.0-1.0;
                p.x*=_ScreenParams.x/_ScreenParams.y;
                float vig=smoothstep(1.25,0.28,dot(p,p));
                c*=lerp(1.0,vig,_Vignette);
                return fixed4(saturate(c),src.a);
            }
            ENDCG
        }
    }
}
