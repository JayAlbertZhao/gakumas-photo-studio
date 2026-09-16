Shader "Hidden/GakumasPhotoMode/FrameMotionBlurGuide"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            Name "CURRENT_HALF4_MOTION_BLUR_GUIDE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "UnityCG.cginc"
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_FrameBlurMotion);
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_FrameBlurOpaque);
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_FrameBlurFx);
            float _FrameBlurExcluded[256],_FrameBlurCheckFx;
            float4 Vertex(float4 position:POSITION):SV_POSITION{return float4(position.xy,0,1);}
            struct Result {float4 guide:SV_Target0;float protection:SV_Target1;};
            Result Fragment(float4 pixel:SV_POSITION)
            {
                Result o;o.guide=0;o.protection=0;int2 p=(int2)pixel.xy;
                float4 m=_FrameBlurMotion.Load(int3(p,0));
                if(any((asuint(m)&0x7fffffffu)>=0x7f800000u)||m.a<0||m.a>2047||m.a!=floor(m.a))return o;
                uint packed=(uint)m.a,id=packed>>4;
                // ExcludeTAA alone does not exclude motion blur. Blended draws
                // and explicit surface exclusions come from the producer's
                // identity table; the packed attachment ABI is unchanged.
                bool protect=(packed&4u)!=0||_FrameBlurExcluded[id*2+(packed&1u)]>.5||
                    (_FrameBlurCheckFx>.5&&any(_FrameBlurFx.Load(int3(p,0))!=_FrameBlurOpaque.Load(int3(p,0))));
                o.protection=protect?4.0/255.0:0;
                if(!protect&&id>0&&(packed&8u)!=0&&m.b>0)o.guide=float4(m.xy,m.b,1);
                return o;
            }
            ENDCG
        }
    }
}
