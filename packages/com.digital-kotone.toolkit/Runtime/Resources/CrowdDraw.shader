Shader "Hidden/GakumasPhotoMode/CrowdDraw"
{
    Properties { _Cull("Cull",Float)=2 }
    SubShader
    {
        Cull [_Cull] ZWrite On ZTest LEqual Blend Off
        CGINCLUDE
        #define TOOLKIT_FORWARD_TOON
        #include "SceneForwardLighting.hlsl"
        #include "CrowdShared.hlsl"
        sampler2D _CrowdNormalDepth;
        #if defined(CROWD_LIGHTSTICKS)
        sampler2D _CrowdLightstickMask;
        StructuredBuffer<float4> _CrowdLightstickRadiance;
        #endif
        float4x4 _CrowdView;
        struct Varying
        {
            float4 position:SV_POSITION;float3 world:TEXCOORD0;float3 normal:TEXCOORD1;float4 tangent:TEXCOORD2;float4 uv:TEXCOORD3;
            nointerpolation uint id:TEXCOORD4;nointerpolation uint direction:TEXCOORD5;
        };
        struct Targets { float4 color:SV_Target0;float eye:SV_Target1;float deviceDepth:SV_Depth; };
        Varying MeshVertex(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
        {
            uint id=_CrowdIndices[_CrowdBucket*_CrowdCapacity+instance];CrowdInstanceData item=_CrowdInstances[id];CrowdVertex v=_CrowdVertices[vertex];
            Varying o;o.world=item.positionScale.xyz+CrowdRotate(v.position.xyz*item.positionScale.w,item.rotationType.xy);
            o.position=mul(_ViewProjection,float4(o.world,1));o.normal=CrowdRotate(v.normal.xyz,item.rotationType.xy);
            o.tangent=float4(CrowdRotate(v.tangent.xyz,item.rotationType.xy),v.tangent.w);o.uv=float4(v.uv.xy*_UvST.xy+_UvST.zw,v.uv.zw);
            o.id=id;o.direction=0;return o;
        }
        Varying BillboardVertex(float4 vertex:POSITION,uint instance:SV_InstanceID)
        {
            uint id=_CrowdIndices[_CrowdBucket*_CrowdCapacity+instance];CrowdInstanceData item=_CrowdInstances[id];float4 sphere=_CrowdBounds[_CrowdType];
            float3 center=item.positionScale.xyz+CrowdRotate(sphere.xyz*item.positionScale.w,item.rotationType.xy);
            float3 facing=CrowdFacing(center),right=CrowdRight(facing),up=cross(right,facing);float radius=sphere.w*item.positionScale.w;
            Varying o;o.world=center+(right*vertex.x+up*vertex.y)*radius;o.position=mul(_ViewProjection,float4(o.world,1));
            o.normal=facing;o.tangent=float4(right,1);o.uv=float4(vertex.xy*.5+.5,0,0);o.id=id;o.direction=CrowdDirection(facing,item.rotationType.xy);return o;
        }
        Targets Shade(Varying input, float lightstickMask)
        {
            ForwardVarying surface;surface.position=input.position;surface.world=input.world;surface.normal=input.normal;surface.tangent=input.tangent;surface.uv=input.uv.xy;surface.uv2=input.uv.zw;
            float4 material=ForwardFragment(surface);CrowdInstanceData item=_CrowdInstances[input.id];Targets o;
            float3 radiance=material.rgb/max(material.a,1e-8)*item.tint.rgb;
            #if defined(CROWD_LIGHTSTICKS)
            radiance+=lightstickMask*_CrowdLightstickRadiance[input.id].rgb;
            #endif
            o.color=float4(clamp(radiance,0,65504),1);o.eye=-mul(_CrowdView,float4(input.world,1)).z;
            float4 clipPosition=mul(_ViewProjection,float4(input.world,1));o.deviceDepth=clipPosition.z/clipPosition.w;
            #if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
            o.deviceDepth=o.deviceDepth*.5+.5;
            #endif
            return o;
        }
        Targets MeshFragment(Varying input)
        {
            float mask=0;
            #if defined(CROWD_LIGHTSTICKS)
            mask=saturate(tex2D(_CrowdLightstickMask,input.uv.xy).r);
            #endif
            return Shade(input,mask);
        }
        Targets BillboardFragment(Varying input)
        {
            CrowdInstanceData item=_CrowdInstances[input.id];float4 sphere=_CrowdBounds[_CrowdType];
            float2 uv=clamp(input.uv.xy,.5/_CrowdAtlasSize.z,1-.5/_CrowdAtlasSize.z);
            // Camera projection's native texture orientation is reflected by the atlas row/UV convention.
            float2 atlasUv=(float2(input.direction,_CrowdType)+uv)/float2(4,_CrowdAtlasSize.w);
            float4 captured=tex2D(_CrowdNormalDepth,atlasUv);float3 center=item.positionScale.xyz+CrowdRotate(sphere.xyz*item.positionScale.w,item.rotationType.xy);
            float3 facing=CrowdFacing(center);input.world+=facing*(captured.w*item.positionScale.w);
            input.normal=CrowdRotate(captured.xyz,item.rotationType.xy);input.uv=float4(atlasUv,0,0);
            float mask=0;
            #if defined(CROWD_LIGHTSTICKS)
            mask=tex2D(_EmissionMap,atlasUv).a;
            #endif
            return Shade(input,mask);
        }
        ENDCG
        Pass
        {
            Name "CROWD_CURRENT_MESH"
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ CROWD_LIGHTSTICKS
            #pragma multi_compile_local __ SCENE_LIGHT_SHADOWS
            #pragma multi_compile_local __ SCENE_MAIN_LIGHT_SHADOWS
            #pragma vertex MeshVertex
            #pragma fragment MeshFragment
            ENDCG
        }
        Pass
        {
            Name "CROWD_FOUR_VIEW_IMPOSTOR"
            Cull Off
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ CROWD_LIGHTSTICKS
            #pragma multi_compile_local __ SCENE_LIGHT_SHADOWS
            #pragma multi_compile_local __ SCENE_MAIN_LIGHT_SHADOWS
            #pragma vertex BillboardVertex
            #pragma fragment BillboardFragment
            ENDCG
        }
    }
}
