Shader "Hidden/GakumasPhotoMode/TileScenePosition"
{
    Properties
    {
        _Cull ("Cull", Float) = 2
        _AlbedoMap ("Albedo coverage", 2D) = "white" {}
        _SceneEyeDepth ("Current positive eye depth", 2D) = "black" {}
    }
    SubShader
    {
        Pass
        {
            Name "TILE_SCENE_CURRENT_EYE_DEPTH"
            Cull [_Cull] ZTest LEqual ZWrite On Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Geometry
            #pragma fragment Depth
            #include "UnityCG.cginc"
            sampler2D _AlbedoMap;
            float4 _UvST;
            float3 _VertexScale;
            float _Alpha, _Cutoff;
            float4x4 _ViewProjection, _SceneView;
            struct Input { float4 position:POSITION; float2 uv:TEXCOORD0; };
            struct Varying { float4 position:SV_POSITION; float2 uv:TEXCOORD0; float depth:TEXCOORD1; };
            Varying Geometry(Input v)
            {
                Varying o; v.position.xyz *= _VertexScale;
                float4 world=mul(unity_ObjectToWorld,v.position);
                o.position=mul(_ViewProjection,world); o.depth=-mul(_SceneView,world).z;
                o.uv=v.uv*_UvST.xy+_UvST.zw; return o;
            }
            float Depth(Varying i):SV_Target
            { clip(tex2D(_AlbedoMap,i.uv).a*_Alpha-_Cutoff); return i.depth; }
            ENDCG
        }
        Pass
        {
            Name "TILE_SCENE_POSITION_LIGHTING"
            Cull Off ZTest Always ZWrite Off Blend One One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Fullscreen
            #pragma fragment Lighting
            #pragma multi_compile_local __ SCENE_LIGHT_SHADOWS
            #pragma multi_compile_local __ SCENE_MAIN_LIGHT_SHADOWS
            #pragma multi_compile_local __ SCENE_BAKED_LIGHT_CHANNELS
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #define UNITY_PI PI
            #define TOOLKIT_FORWARD_EVALUATION_ONLY
            #define SCENE_BAKED_SHADOW_INPUT
            #include "SceneForwardLighting.hlsl"
            FRAMEBUFFER_INPUT_FLOAT(0);
            FRAMEBUFFER_INPUT_FLOAT(1);
            FRAMEBUFFER_INPUT_FLOAT(2);
            FRAMEBUFFER_INPUT_FLOAT(3);
            Texture2D<float> _SceneEyeDepth;
            float4x4 _InverseViewProjection, _SceneView;
            struct Screen { float4 position:SV_POSITION; float2 xy:TEXCOORD0; };
            Screen Fullscreen(float4 v:POSITION)
            { Screen o; o.position=float4(v.xy,0,1); o.xy=v.xy; return o; }
            float4 Lighting(Screen i):SV_Target
            {
                float4 base=LOAD_FRAMEBUFFER_INPUT(0,i.position),mos=LOAD_FRAMEBUFFER_INPUT(1,i.position);
                float4 normal=LOAD_FRAMEBUFFER_INPUT(2,i.position);
                float4 gi=float4(LOAD_FRAMEBUFFER_INPUT(3,i.position).rgb,normal.a>256.5?1:0);
                clip(normal.a-.5);
                // A separately completed geometry prepass supplies an ordinary R32 texture.
                // Never sample the D32 target that is attached to this render pass.
                float depth=_SceneEyeDepth.Load(int3((int2)i.position.xy,0));
                clip(depth-1e-8);
                float4 a=mul(_InverseViewProjection,float4(i.xy,0,1)),b=mul(_InverseViewProjection,float4(i.xy,1,1));
                a/=a.w; b/=b.w;
                float da=-mul(_SceneView,a).z,db=-mul(_SceneView,b).z;
                float3 world=lerp(a.xyz,b.xyz,(depth-da)/(db-da));
                float3 n=ForwardNormal(normal.xyz),v=ForwardNormal(lerp(_CameraPosition-world,-_CameraForward,_Orthographic));
                float4 baked=SceneBakedUnpack(float2(base.a,mos.a));
                float receiver=normal.a-1-(gi.a>.5?256:0);
                float3 direct=ForwardBrdf(base.rgb,mos.rgb,n,v,_LightDirection,_DirectionalResponse)*_LightRadiance;
                if(gi.a>.5)direct*=lerp(1,gi.rgb,_DirectionalResponse.z);
                float visibility=SceneBakedSelect(baked,_MainBakedChannel);
                #if defined(SCENE_MAIN_LIGHT_SHADOWS)
                ForwardMainShadowData shadow; shadow.worldToShadow=_SingleShadowMatrix; shadow.atlasST=_SingleShadowST;
                shadow.depth=_SingleShadowDepth; shadow.options=_SingleShadowOptions;
                visibility=min(visibility,ForwardMainVisibility(world,n,shadow));
                #endif
                direct*=visibility;
                // Accumulate every local light in FP32, quantize once into packed HDR.
                if(_ForwardTiled!=0)
                {
                    uint2 tile=(uint2)i.position.xy/(uint)_ForwardTileSize;
                    uint start=(tile.y*_ForwardTilesX+tile.x)*_ForwardWords;
                    [loop] for(uint word=0;word<(uint)_ForwardWords;word++)
                    {
                        uint mask=_ForwardTiles[start+word];
                        [loop] while(mask!=0)
                        {
                            uint bit=firstbitlow(mask);mask&=mask-1;
                            direct+=ForwardLocalForReceiver(word*32+bit,world,n,v,base.rgb,mos.rgb,gi,baked,receiver);
                        }
                    }
                }
                else [loop] for(uint index=0;index<(uint)_ForwardLightCount;index++)
                    direct+=ForwardLocalForReceiver(index,world,n,v,base.rgb,mos.rgb,gi,baked,receiver);
                float3 indirect=base.rgb*((1-mos.r)/PI)*_AmbientIrradiance*mos.g;
                if(gi.a>.5)indirect=base.rgb*(1-mos.r)*gi.rgb*_GiBaseScale*mos.g;
                return float4(clamp(direct+indirect,0,float3(65024,65024,64512)),0);
            }
            ENDHLSL
        }
    }
}
