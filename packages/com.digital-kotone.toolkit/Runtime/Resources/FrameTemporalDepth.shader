Shader "Hidden/GakumasPhotoMode/FrameTemporalDepth"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            Name "CURRENT_R32_TEMPORAL_COORDINATES"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_local _ TOOLKIT_DEPTH_COVERAGE_ANCHOR
            #include "UnityCG.cginc"
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_TemporalRawDepth);
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_TemporalDepthMotion);
            float4 _TemporalDepthSize,_TemporalDepthCorrection;
            float4 Vertex(float4 p:POSITION):SV_POSITION{return float4(p.xy,0,1);}
            int2 Bound(int2 p){return clamp(p,0,(int2)_TemporalDepthSize.xy-1);}
            uint Identity(float packed){return (uint)packed&~14u;}
            float Fragment(float4 pixel:SV_POSITION):SV_Target
            {
                int2 stable=(int2)pixel.xy;float4 centre=_TemporalDepthMotion.Load(int3(stable,0));
                bool noJitter=((uint)centre.a&4u)!=0;
                float2 raster=pixel.xy-.5-(noJitter?0:_TemporalDepthCorrection.xy*_TemporalDepthSize.xy);
                int2 p=Bound((int2)floor(raster+.5));float4 motion=_TemporalDepthMotion.Load(int3(p,0));
                #if defined(TOOLKIT_DEPTH_COVERAGE_ANCHOR)
                int2 first=(int2)floor(raster);float2 f=frac(raster);
                float closest=Identity(motion.a)!=0&&motion.b>0?motion.b:1e30;
                [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++)
                {
                    float2 axis=lerp(1-f,f,float2(x,y));int2 q=Bound(first+int2(x,y));
                    float4 g=_TemporalDepthMotion.Load(int3(q,0));
                    if(axis.x*axis.y>1e-6&&Identity(g.a)!=0&&((uint)g.a&6u)==0&&g.b>0&&g.b<closest)
                    {p=q;motion=g;closest=g.b;}
                }
                #endif
                if((((uint)motion.a|(uint)centre.a)&4u)!=0)p=stable;
                return _TemporalRawDepth.Load(int3(p,0)).r;
            }
            ENDCG
        }
    }
}
