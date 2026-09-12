Shader "Hidden/GakumasPhotoMode/SceneLightShadowCaster"
{
    Properties { _Cull ("Cull", Float) = 2 }
    SubShader
    {
        Pass
        {
            Name "LIGHT_SOURCE_SHADOW_DEPTH"
            Cull [_Cull] ZTest LEqual ZWrite On Blend Off ColorMask R
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local __ SCENE_SHADOW_ORTHOGRAPHIC SCENE_SHADOW_POINT
            #include "UnityCG.cginc"
            float4x4 _ShadowViewProjection;
            float3 _ShadowVertexScale;
            float4 _ShadowUvST;
            float4 _ShadowDepthPlane;
            float4 _ShadowPointOrigin;
            float _ShadowFar, _ShadowAlpha, _ShadowCutoff;
            sampler2D _ShadowAlphaMap;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Varying {
                float4 position : SV_POSITION; float2 uv : TEXCOORD0;
                #if defined(SCENE_SHADOW_POINT)
                float3 fromLight : TEXCOORD1;
                #else
                float depth : TEXCOORD1;
                #endif
            };
            Varying vert(Input input)
            {
                Varying o;
                float4 world = mul(unity_ObjectToWorld, float4(input.vertex.xyz * _ShadowVertexScale, 1));
                o.position = mul(_ShadowViewProjection, world);
                // Perspective clip.w is positive light-view axial distance. Color depth is
                // conventional 0..1, independent of the hardware's reversed-Z attachment.
                #if defined(SCENE_SHADOW_POINT)
                o.fromLight = world.xyz - _ShadowPointOrigin.xyz;
                #elif defined(SCENE_SHADOW_ORTHOGRAPHIC)
                o.depth = dot(_ShadowDepthPlane, world) / _ShadowFar;
                #else
                o.depth = o.position.w / _ShadowFar;
                #endif
                o.uv = input.uv * _ShadowUvST.xy + _ShadowUvST.zw; return o;
            }
            float frag(Varying input) : SV_Target
            {
                clip(tex2D(_ShadowAlphaMap, input.uv).a * _ShadowAlpha - _ShadowCutoff);
                #if defined(SCENE_SHADOW_POINT)
                // Interpolate the vector, then measure length per fragment. Interpolating
                // vertex lengths would curve a plane's shadow depth incorrectly.
                float radial = length(input.fromLight);
                clip(radial - _ShadowPointOrigin.w); clip(_ShadowFar - radial);
                return radial / _ShadowFar;
                #else
                return input.depth;
                #endif
            }
            ENDCG
        }
    }
}
