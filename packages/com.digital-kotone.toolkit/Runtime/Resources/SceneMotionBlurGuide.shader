Shader "Hidden/GakumasPhotoMode/SceneMotionBlurGuide"
{
    Properties { _Cull("Cull",Float)=2 }
    SubShader
    {
        Pass
        {
            Name "ACTUAL_FRAMEBUFFER_VISIBLE_MOTION_DEPTH"
            Cull [_Cull] ZTest LEqual ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            Texture2D<float4> _Motion;
            sampler2D _AlphaMap;
            float4x4 _ViewProjection,_View;
            float4 _AlphaST;
            float3 _VertexScale;
            float _Alpha,_Cutoff,_Correspondence,_Excluded;
            struct Input {float4 position:POSITION;float2 uv:TEXCOORD0;};
            struct Varying {float4 position:SV_POSITION;float2 uv:TEXCOORD0;float depth:TEXCOORD1;};
            Varying Vert(Input input)
            {
                Varying output;input.position.xyz*=_VertexScale;float4 world=mul(unity_ObjectToWorld,input.position);
                output.position=mul(_ViewProjection,world);output.depth=-mul(_View,world).z;output.uv=input.uv*_AlphaST.xy+_AlphaST.zw;return output;
            }
            float4 Frag(Varying input):SV_Target
            {
                clip(tex2D(_AlphaMap,input.uv).a*_Alpha-_Cutoff);
                float4 motion=_Motion.Load(int3((int2)input.position.xy,0));
                return float4(motion.xy,input.depth,motion.a>.5&&_Correspondence>.5&&_Excluded<.5?1:0);
            }
            ENDCG
        }
    }
}
