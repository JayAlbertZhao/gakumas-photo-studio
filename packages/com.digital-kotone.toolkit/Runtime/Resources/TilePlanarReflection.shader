Shader "Hidden/GakumasPhotoMode/TilePlanarReflection"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            Name "CURRENT_TILE_PLANAR_RECEIVER_PROJECTION"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            Texture2D<float> _TilePlanarDepth;
            Texture2D<float4> _TilePlanarNormal,_TilePlanarMos;
            sampler2D _TilePlanarCapture;
            float4x4 _TilePlanarInverseProjection,_TilePlanarInverseView,_TilePlanarCaptureVP;
            float4 _TilePlanarSize,_TilePlanarPlane,_TilePlanarOptions;
            float4 frag(v2f_img i):SV_Target
            {
                int2 pixel=min((int2)(i.uv*_TilePlanarSize.xy),(int2)_TilePlanarSize.xy-1);
                float identity=_TilePlanarNormal.Load(int3(pixel,0)).a;
                float depth=_TilePlanarDepth.Load(int3(pixel,0));
                if(identity<.5 || depth<=0 || abs(fmod(identity-1,256)-_TilePlanarOptions.x)>.25)return 0;
                float2 xy=i.uv*2-1;
                #if UNITY_UV_STARTS_AT_TOP
                xy.y=-xy.y;
                #endif
                float4 a=mul(_TilePlanarInverseProjection,float4(xy,0,1));a/=a.w;
                float4 b=mul(_TilePlanarInverseProjection,float4(xy,1,1));b/=b.w;
                float3 view=lerp(a.xyz,b.xyz,(-depth-a.z)/(b.z-a.z));
                float4 world=mul(_TilePlanarInverseView,float4(view,1));
                if(abs(dot(_TilePlanarPlane,world))>_TilePlanarOptions.y)return 0;
                float4 clip=mul(_TilePlanarCaptureVP,world);if(clip.w<=1e-6)return 0;
                float2 uv=clip.xy/clip.w*.5+.5;
                #if UNITY_UV_STARTS_AT_TOP
                uv.y=1-uv.y;
                #endif
                if(any(uv<0) || any(uv>1))return 0;
                float roughness=1-_TilePlanarMos.Load(int3(pixel,0)).b;
                float4 reflection=tex2Dlod(_TilePlanarCapture,float4(uv,0,roughness*roughness*_TilePlanarOptions.w));
                if(reflection.a<=1e-5)return 0;
                return float4(clamp(reflection.rgb/reflection.a,0,65504),saturate(reflection.a)*_TilePlanarOptions.z);
            }
            ENDCG
        }
    }
}
