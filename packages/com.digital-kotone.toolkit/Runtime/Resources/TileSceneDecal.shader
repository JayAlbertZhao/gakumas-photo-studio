Shader "Hidden/GakumasPhotoMode/TileSceneDecal"
{
    Properties
    {
        _Cull ("Cull", Float) = 2
        _ReceiverGroup ("Receiver stencil",Float)=0
        _HeightMap ("Height",2D)="white" {}
        _SceneEyeDepth ("Current depth",2D)="black" {}
        _GeometryDepthId ("Geometric normal and SSR mask",2D)="black" {}
        _AlbedoMap ("Albedo", 2D) = "white" {}
        _NormalMap ("Linear RGB tangent normal", 2D) = "gray" {}
        _MosMap ("Metallic occlusion smoothness", 2D) = "white" {}
        _EmissionMap ("Emission", 2D) = "white" {}
        _SceneGiLightmap ("GI response", 2D) = "black" {}
        _SceneGiDirection ("GI direction", 2D) = "gray" {}
        _SceneBakedShadowMap ("Baked visibility", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            Name "TILE_SCENE_STAMPED_MATERIAL_GI_MASK"
            Cull [_Cull] ZTest LEqual ZWrite On Blend Off
            Stencil { Ref [_ReceiverGroup] Comp Always Pass Replace ReadMask 255 WriteMask 255 }
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Geometry
            #pragma fragment MaterialData
            #pragma multi_compile_local __ SCENE_BAKED_SHADOW_INPUT
            #include "UnityCG.cginc"
            #include "SceneGi.hlsl"
            #include "SceneBakedShadow.hlsl"
            sampler2D _AlbedoMap, _NormalMap, _MosMap, _EmissionMap;
            float4 _UvST;
            float3 _Albedo, _Mos, _Emission, _VertexScale;
            float _Alpha, _HasNormal, _Cutoff, _ReceiverGroup;
            float4x4 _ViewProjection;
            struct Input { float4 position:POSITION; float3 normal:NORMAL; float4 tangent:TANGENT; float2 uv:TEXCOORD0; float2 uv2:TEXCOORD1; };
            struct Varying { float4 position:SV_POSITION; float2 uv:TEXCOORD0; float3 normal:TEXCOORD1; float3 tangent:TEXCOORD2; float sign:TEXCOORD3; float2 uv2:TEXCOORD4; };
            float3 SafeNormal(float3 n) { return n * rsqrt(max(dot(n,n),1e-12)); }
            Varying Geometry(Input v)
            {
                Varying o; v.position.xyz *= _VertexScale;
                o.position=mul(_ViewProjection,mul(unity_ObjectToWorld,v.position));
                o.uv=v.uv*_UvST.xy+_UvST.zw; o.uv2=v.uv2;
                o.normal=UnityObjectToWorldNormal(v.normal/_VertexScale);
                o.tangent=mul((float3x3)unity_ObjectToWorld,v.tangent.xyz*_VertexScale);
                o.sign=v.tangent.w*sign(determinant((float3x3)unity_ObjectToWorld))*sign(_VertexScale.x*_VertexScale.y*_VertexScale.z);
                return o;
            }
            struct GBuffer { float4 base:SV_Target0; float4 mos:SV_Target1; float4 normal:SV_Target2; float4 emission:SV_Target3; float4 gi:SV_Target4; };
            GBuffer MaterialData(Varying i)
            {
                float4 base=tex2D(_AlbedoMap,i.uv); clip(base.a*_Alpha-_Cutoff);
                float3 n=SafeNormal(i.normal), t=SafeNormal(i.tangent-n*dot(n,i.tangent));
                float3 map=tex2D(_NormalMap,i.uv).rgb*2-1;
                map=_HasNormal>.5?SafeNormal(map+float3(0,0,1e-8)):float3(0,0,1);
                n=SafeNormal(map.x*t+map.y*cross(n,t)*i.sign+map.z*n);
                float2 mask=SceneBakedPack(SceneBakedSample(i.uv2),i.position.xy);
                float4 gi=SceneGi(i.uv2,n);
                GBuffer o;
                o.base=float4(saturate(base.rgb*_Albedo),mask.x);
                o.mos=float4(saturate(tex2D(_MosMap,i.uv).rgb*_Mos),mask.y);
                // Independent exact Half integer: zero background; 1+group plus 256 for GI.
                o.normal=float4(n,1+_ReceiverGroup+(gi.a>.5?256:0));
                o.emission=float4(clamp(tex2D(_EmissionMap,i.uv).rgb*_Emission,0,float3(65024,65024,64512)),0);
                o.gi=float4(min(gi.rgb,float3(65024,65024,64512)),0); return o;
            }
            ENDCG
        }
        Pass
        {
            Name "TILE_DECAL_MATERIAL_METALLIC"
            Cull Off ZTest Always ZWrite Off
            Stencil { Ref [_ReceiverGroup] Comp Equal Pass Keep ReadMask 255 WriteMask 0 }
            Blend 0 SrcAlpha OneMinusSrcAlpha
            ColorMask RGB 0
            Blend 1 SrcAlpha OneMinusSrcAlpha
            ColorMask R 1
            Blend 2 SrcAlpha OneMinusSrcAlpha
            ColorMask RGB 2
            Blend 3 SrcAlpha OneMinusSrcAlpha
            ColorMask RGB 3
            Blend 4 Off
            ColorMask 0 4
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Fullscreen
            #pragma fragment MaterialDecal
            #include "TileSceneDecal.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "TILE_DECAL_OCCLUSION"
            Cull Off ZTest Always ZWrite Off
            Stencil { Ref [_ReceiverGroup] Comp Equal Pass Keep ReadMask 255 WriteMask 0 }
            Blend 0 SrcAlpha OneMinusSrcAlpha
            ColorMask 0 0
            Blend 1 SrcAlpha OneMinusSrcAlpha
            ColorMask G 1
            Blend 2 SrcAlpha OneMinusSrcAlpha
            ColorMask 0 2
            Blend 3 SrcAlpha OneMinusSrcAlpha
            ColorMask 0 3
            Blend 4 Off
            ColorMask 0 4
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Fullscreen
            #pragma fragment MaterialDecal
            #define TILE_DECAL_OCCLUSION
            #include "TileSceneDecal.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "TILE_DECAL_SMOOTHNESS"
            Cull Off ZTest Always ZWrite Off
            Stencil { Ref [_ReceiverGroup] Comp Equal Pass Keep ReadMask 255 WriteMask 0 }
            Blend 0 SrcAlpha OneMinusSrcAlpha
            ColorMask 0 0
            Blend 1 SrcAlpha OneMinusSrcAlpha
            ColorMask B 1
            Blend 2 SrcAlpha OneMinusSrcAlpha
            ColorMask 0 2
            Blend 3 SrcAlpha OneMinusSrcAlpha
            ColorMask 0 3
            Blend 4 Off
            ColorMask 0 4
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Fullscreen
            #pragma fragment MaterialDecal
            #define TILE_DECAL_SMOOTHNESS
            #include "TileSceneDecal.hlsl"
            ENDHLSL
        }
    }
}
