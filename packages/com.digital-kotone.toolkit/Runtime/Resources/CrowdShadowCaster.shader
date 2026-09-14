Shader "Hidden/GakumasPhotoMode/CrowdShadowCaster"
{
    Properties { _Cull ("Cull", Float) = 2 }
    SubShader
    {
        Pass
        {
            Name "CROWD_CURRENT_SHADOW_DEPTH"
            Cull [_Cull] ZTest LEqual ZWrite On Blend Off ColorMask R
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local __ SCENE_SHADOW_ORTHOGRAPHIC SCENE_SHADOW_POINT
            #include "UnityCG.cginc"
            struct Vertex { float4 position, normal, tangent, uv; };
            struct Placement { float4 positionScale, rotationType, tint; };
            StructuredBuffer<Vertex> _CrowdVertices;
            StructuredBuffer<Placement> _CrowdInstances;
            uint _CrowdShadowStart;
            float4x4 _ShadowViewProjection;
            float4 _ShadowUvST, _ShadowDepthPlane, _ShadowPointOrigin;
            float _ShadowFar, _ShadowAlpha, _ShadowCutoff;
            sampler2D _ShadowAlphaMap;
            struct Varying { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float3 depth : TEXCOORD1; };
            Varying vert(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                Vertex v = _CrowdVertices[vertex]; Placement p = _CrowdInstances[_CrowdShadowStart + instance];
                float3 local = v.position.xyz * p.positionScale.w; float2 yaw = p.rotationType.xy;
                float4 world = float4(float3(yaw.y * local.x + yaw.x * local.z, local.y, -yaw.x * local.x + yaw.y * local.z) + p.positionScale.xyz, 1);
                Varying o; o.position = mul(_ShadowViewProjection, world); o.depth = 0;
                #if defined(SCENE_SHADOW_POINT)
                o.depth = world.xyz - _ShadowPointOrigin.xyz;
                #elif defined(SCENE_SHADOW_ORTHOGRAPHIC)
                o.depth.x = dot(_ShadowDepthPlane, world) / _ShadowFar;
                #else
                o.depth.x = o.position.w / _ShadowFar;
                #endif
                o.uv = v.uv.xy * _ShadowUvST.xy + _ShadowUvST.zw; return o;
            }
            float frag(Varying i) : SV_Target
            {
                clip(tex2D(_ShadowAlphaMap, i.uv).a * _ShadowAlpha - _ShadowCutoff);
                #if defined(SCENE_SHADOW_POINT)
                float radial = length(i.depth); clip(radial - _ShadowPointOrigin.w); clip(_ShadowFar - radial); return radial / _ShadowFar;
                #else
                return i.depth.x;
                #endif
            }
            ENDCG
        }
    }
}
