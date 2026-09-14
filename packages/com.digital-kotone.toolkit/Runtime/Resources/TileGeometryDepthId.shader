Shader "Hidden/GakumasPhotoMode/TileGeometryDepthId"
{
    Properties
    {
        _Cull ("Cull",Float)=2
        _AlbedoMap ("Coverage",2D)="white" {}
        _MosMap ("Smoothness",2D)="white" {}
    }
    SubShader
    {
        Pass
        {
            Name "TILE_GEOMETRY_DEPTH_ID"
            Cull [_Cull] ZTest LEqual ZWrite On Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Geometry
            #pragma fragment DepthId
            #include "UnityCG.cginc"
            sampler2D _AlbedoMap,_MosMap;
            float4 _UvST;
            float3 _VertexScale,_Mos;
            float _Alpha,_Cutoff,_ReflectionThreshold,_ReceiveReflections;
            float4x4 _ViewProjection,_SceneView;
            struct Input { float4 position:POSITION;float3 normal:NORMAL;float2 uv:TEXCOORD0; };
            struct Varying { float4 position:SV_POSITION;float2 uv:TEXCOORD0;float depth:TEXCOORD1;float3 normal:TEXCOORD2; };
            Varying Geometry(Input v)
            {
                Varying o;v.position.xyz*=_VertexScale;float4 world=mul(unity_ObjectToWorld,v.position);
                o.position=mul(_ViewProjection,world);o.depth=-mul(_SceneView,world).z;o.uv=v.uv*_UvST.xy+_UvST.zw;
                o.normal=UnityObjectToWorldNormal(v.normal/_VertexScale);return o;
            }
            struct Output { float depth:SV_Target0;float4 normalMask:SV_Target1; };
            Output DepthId(Varying i)
            {
                clip(tex2D(_AlbedoMap,i.uv).a*_Alpha-_Cutoff);
                Output o;o.depth=i.depth;float3 n=i.normal*rsqrt(max(dot(i.normal,i.normal),1e-12));
                o.normalMask=float4(n*.5+.5,_ReceiveReflections*step(_ReflectionThreshold,tex2D(_MosMap,i.uv).b*_Mos.b));return o;
            }
            ENDCG
        }
    }
}
