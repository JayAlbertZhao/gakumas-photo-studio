Shader "Hidden/PhotoStudio/AuthoredFaceDecal"
{
    Properties
    {
        _AuthoredDecalAtlas ("Atlas", 2D) = "white" {}
        _AuthoredDecalReceiverMask ("Receiver alpha", 2D) = "white" {}
        _AuthoredDecalCull ("Cull", Float) = 2
        _AuthoredDecalStencilComp ("Stencil compare", Float) = 8
        _AuthoredDecalStencilRef ("Stencil ref", Float) = 0
        _AuthoredDecalStencilRead ("Stencil read", Float) = 255
    }
    SubShader
    {
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _AuthoredDecalAtlas, _AuthoredDecalReceiverMask;
        float4x4 _AuthoredDecalWorldToLocal[8];
        float4 _AuthoredDecalUv[8], _AuthoredDecalTint[8], _AuthoredDecalSettings[8], _AuthoredDecalRotation[8], _AuthoredDecalDirection[8];
        float4 _AuthoredDecalReceiverUv, _AuthoredDecalVertexScale;
        float _AuthoredDecalCutoff; int _AuthoredDecalCount;
        struct appdata { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
        struct v2f { float4 position:SV_POSITION; float3 world:TEXCOORD0; float3 normal:TEXCOORD1; float2 uv:TEXCOORD2; };
        v2f Vertex(appdata v)
        {
            v2f o;v.vertex.xyz*=_AuthoredDecalVertexScale.xyz;
            o.position=UnityObjectToClipPos(v.vertex);o.world=mul(unity_ObjectToWorld,v.vertex).xyz;
            o.normal=UnityObjectToWorldNormal(v.normal/_AuthoredDecalVertexScale.xyz);
            o.uv=v.uv*_AuthoredDecalReceiverUv.xy+_AuthoredDecalReceiverUv.zw;return o;
        }
        float4 Overlay(v2f input,float facing:VFACE):SV_Target
        {
            clip(tex2D(_AuthoredDecalReceiverMask,input.uv).a-_AuthoredDecalCutoff);
            float3 normal=input.normal*rsqrt(max(dot(input.normal,input.normal),1e-20))*(facing>=0?1:-1);
            float4 result=0;
            [loop] for(int i=0;i<_AuthoredDecalCount;i++)
            {
                float3 p=mul(_AuthoredDecalWorldToLocal[i],float4(input.world,1)).xyz;
                float edge=.5-max(abs(p.x),max(abs(p.y),abs(p.z)));
                float4 options=_AuthoredDecalSettings[i];
                float coverage=edge>=0?(options.y>0?saturate(edge/options.y):1):0;
                float4 turn=_AuthoredDecalRotation[i];
                float cosine=dot(normal,_AuthoredDecalDirection[i].xyz);
                float angle=turn.z==turn.w?(cosine>=turn.w?1:0):saturate((cosine-turn.w)/(turn.z-turn.w));
                if(options.w>0)coverage*=angle;
                float2 projected=float2(turn.x*p.x-turn.y*p.y,turn.y*p.x+turn.x*p.y)+.5;
                float2 uv=projected*_AuthoredDecalUv[i].xy+_AuthoredDecalUv[i].zw;
                float4 texel=tex2D(_AuthoredDecalAtlas,uv);float4 tint=_AuthoredDecalTint[i];
                float alpha=saturate(texel.a)*tint.a*options.x*coverage;
                float3 color=texel.rgb*tint.rgb;
                if(options.z>.5)color=lerp(1.0.xxx,color,saturate(texel.a));
                result.rgb=result.rgb*(1-alpha)+color*alpha;
                result.a=result.a*(1-alpha)+alpha;
            }
            return result;
        }
        float4 NoCoverage(v2f input):SV_Target { return 0; }
        ENDCG
        Pass
        {
            Name "PROJECTED_FACE_OVERLAY"
            Cull [_AuthoredDecalCull] ZWrite Off ZTest Equal
            Blend One OneMinusSrcAlpha, Zero One
            ColorMask RGB
            Stencil { Ref [_AuthoredDecalStencilRef] ReadMask [_AuthoredDecalStencilRead] WriteMask 0 Comp [_AuthoredDecalStencilComp] Pass Keep }
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Overlay
            ENDCG
        }
        Pass
        {
            Name "PRESERVE_EXISTING_PLANAR_COVERAGE"
            Cull Off ZWrite Off ZTest Always ColorMask 0
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment NoCoverage
            ENDCG
        }
    }
}
