Shader "Hidden/GakumasPhotoMode/CrowdCapture"
{
    Properties { _Cull("Cull",Float)=2 }
    SubShader
    {
        Cull [_Cull] ZWrite On ZTest LEqual
        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex VertexProgram
            #pragma fragment FragmentProgram
            #pragma multi_compile_local __ CROWD_LIGHTSTICKS
            #include "UnityCG.cginc"
            #include "SceneForwardLighting.hlsl"
            #include "CrowdShared.hlsl"
            float4x4 _CrowdCaptureVP;
            float3 _CrowdCaptureCenter,_CrowdCaptureDirection;
            #if defined(CROWD_LIGHTSTICKS)
            sampler2D _CrowdLightstickMask;
            #endif
            struct Varying { float4 position:SV_POSITION;float3 local:TEXCOORD0;float3 normal:TEXCOORD1;float4 tangent:TEXCOORD2;float2 uv:TEXCOORD3; };
            Varying VertexProgram(uint id:SV_VertexID)
            {
                CrowdVertex v=_CrowdVertices[id];Varying o;o.position=mul(_CrowdCaptureVP,v.position);o.local=v.position.xyz;
                o.normal=v.normal.xyz;o.tangent=v.tangent;o.uv=v.uv.xy*_UvST.xy+_UvST.zw;return o;
            }
            struct Targets { float4 albedo:SV_Target0;float4 normalDepth:SV_Target1;float4 mos:SV_Target2;float4 emission:SV_Target3; };
            Targets FragmentProgram(Varying input)
            {
                float4 color=tex2D(_AlbedoMap,input.uv);clip(saturate(color.a*_Alpha)-_Cutoff);
                float3 n=ForwardNormal(input.normal);
                if(_HasNormal>.5)
                {
                    float3 t=ForwardNormal(input.tangent.xyz-n*dot(n,input.tangent.xyz));float3 b=ForwardNormal(cross(n,t))*input.tangent.w;
                    float3 map=ForwardNormal(tex2D(_NormalMap,input.uv).xyz*2-1);n=ForwardNormal(t*map.x+b*map.y+n*map.z);
                }
                Targets o;o.albedo=float4(saturate(color.rgb*_Albedo),1);
                o.normalDepth=float4(n,dot(input.local-_CrowdCaptureCenter,_CrowdCaptureDirection));
                o.mos=float4(saturate(tex2D(_MosMap,input.uv).rgb*_Mos),1);o.emission=float4(clamp(tex2D(_EmissionMap,input.uv).rgb*_Emission,0,65504),1);
                #if defined(CROWD_LIGHTSTICKS)
                o.emission.a=saturate(tex2D(_CrowdLightstickMask,input.uv).r);
                #endif
                return o;
            }
            ENDCG
        }
    }
}
