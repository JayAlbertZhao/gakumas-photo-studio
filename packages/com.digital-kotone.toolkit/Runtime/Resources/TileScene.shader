Shader "Hidden/GakumasPhotoMode/TileScene"
{
    Properties
    {
        _Cull ("Cull", Float) = 2
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
            Name "TILE_SCENE_MATERIAL_GI_MASK"
            Cull [_Cull] ZTest LEqual ZWrite On Blend Off
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
            Name "TILE_SCENE_DIRECTIONAL_PBR"
            Cull Off ZTest Always ZWrite Off Blend One One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Fullscreen
            #pragma fragment Lighting
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "SceneBakedShadow.hlsl"
            FRAMEBUFFER_INPUT_FLOAT(0);
            FRAMEBUFFER_INPUT_FLOAT(1);
            FRAMEBUFFER_INPUT_FLOAT(2);
            FRAMEBUFFER_INPUT_FLOAT(3);
            float4x4 _InverseViewProjection;
            float3 _CameraPosition, _CameraForward, _LightDirection, _LightRadiance, _AmbientIrradiance;
            float4 _DirectionalResponse;
            float _Orthographic, _GiBaseScale;
            struct Screen { float4 position:SV_POSITION; float2 uv:TEXCOORD0; };
            Screen Fullscreen(float4 vertex:POSITION)
            {
                Screen o; o.position=float4(vertex.xy,0,1); o.uv=vertex.xy*.5+.5;
                #if UNITY_UV_STARTS_AT_TOP
                o.uv.y=1-o.uv.y;
                #endif
                return o;
            }
            float3 SafeNormal(float3 n) { return n*rsqrt(max(dot(n,n),1e-12)); }
            float4 Lighting(Screen i):SV_Target
            {
                float4 base=LOAD_FRAMEBUFFER_INPUT(0,i.position);
                float4 mos=LOAD_FRAMEBUFFER_INPUT(1,i.position);
                float4 normal=LOAD_FRAMEBUFFER_INPUT(2,i.position);
                float3 gi=LOAD_FRAMEBUFFER_INPUT(3,i.position).rgb;
                clip(normal.a-.5);
                float2 xy=i.uv*2-1;
                #if UNITY_UV_STARTS_AT_TOP
                xy.y=-xy.y;
                #endif
                // Directional PBR needs a view ray, not a surface position. A finite
                // far-plane endpoint supplies the same ray without a depth attachment read.
                float4 worldH=mul(_InverseViewProjection,float4(xy,0,1)); float3 world=worldH.xyz/worldH.w;
                float3 n=SafeNormal(normal.xyz), v=SafeNormal(lerp(_CameraPosition-world,-_CameraForward,_Orthographic));
                float3 l=_LightDirection,h=SafeNormal(v+l);
                float nl=saturate(dot(n,l)),nv=saturate(dot(n,v)),nh=saturate(dot(n,h)),vh=saturate(dot(v,h));
                float metallic=mos.r,ao=mos.g,rough=max(1-mos.b,.045);
                float a2=rough*rough*rough*rough,denominator=nh*nh*(a2-1)+1;
                float distribution=a2/max(PI*denominator*denominator,1e-8);
                float visibility=.5/max(nl*sqrt(nv*nv*(1-a2)+a2)+nv*sqrt(nl*nl*(1-a2)+a2),1e-6);
                float3 f0=lerp(.04,base.rgb,metallic),f=f0+(1-f0)*pow(1-vh,5);
                float3 diffuse=base.rgb*(1-metallic)/PI;
                float3 direct=((1-f)*diffuse*_DirectionalResponse.x+distribution*visibility*f*_DirectionalResponse.y)*_LightRadiance*nl;
                if(_DirectionalResponse.w>0)direct+=(1-f0)*diffuse*_DirectionalResponse.x*_DirectionalResponse.w*_LightRadiance*saturate(-dot(n,l));
                bool baked=normal.a>256.5;
                if(baked)direct*=lerp(1,gi,_DirectionalResponse.z);
                direct*=SceneBakedSelect(SceneBakedUnpack(float2(base.a,mos.a)),_MainBakedChannel);
                float3 indirect=diffuse*_AmbientIrradiance*ao;
                if(baked)indirect=base.rgb*(1-metallic)*gi*_GiBaseScale*ao;
                return float4(clamp(direct+indirect,0,float3(65024,65024,64512)),0);
            }
            ENDHLSL
        }
        Pass
        {
            Name "TILE_SCENE_FINAL_REUSE_GI"
            Cull Off ZTest Always ZWrite Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Fullscreen
            #pragma fragment Resolve
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            FRAMEBUFFER_INPUT_FLOAT(0);
            FRAMEBUFFER_INPUT_FLOAT(1);
            float4 _Background;
            float4 Fullscreen(float4 vertex:POSITION):SV_POSITION { return float4(vertex.xy,0,1); }
            float4 Resolve(float4 position:SV_POSITION):SV_Target
            {
                float3 value=LOAD_FRAMEBUFFER_INPUT(0,position).rgb;
                float identity=LOAD_FRAMEBUFFER_INPUT(1,position).a;
                if(identity<.5)value=_Background.rgb;
                // Additive packed HDR can overflow; final output explicitly saturates to finite representable maxima.
                return float4(clamp(value,0,float3(65024,65024,64512)),1);
            }
            ENDHLSL
        }
    }
}
