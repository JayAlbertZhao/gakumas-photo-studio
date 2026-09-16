Shader "Hidden/GakumasPhotoMode/ActorLightShadowCaster"
{
    Properties { _ShadowCull ("Cull", Float) = 2 }
    SubShader
    {
        Pass
        {
            Name "ACTOR_LIGHT_SHADOW_DEPTH"
            Cull [_ShadowCull] ZTest LEqual ZWrite On Blend Off ColorMask R
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local __ SCENE_SHADOW_ORTHOGRAPHIC SCENE_SHADOW_POINT
            #include "UnityCG.cginc"
            float4x4 _ShadowViewProjection;
            float3 _ShadowVertexScale;
            float4 _ShadowUvST, _ShadowDepthPlane, _ShadowPointOrigin, _ShadowActorCoverage;
            float _ShadowFar, _ShadowAlpha, _ShadowCutoff;
            sampler2D _ShadowAlphaMap;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Varying { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float3 world : TEXCOORD1; float axial : TEXCOORD2; };
            Varying vert(Input input)
            {
                Varying o;
                float4 world = mul(unity_ObjectToWorld, float4(input.vertex.xyz * _ShadowVertexScale, 1));
                o.position = mul(_ShadowViewProjection, world); o.world = world.xyz;
                #if defined(SCENE_SHADOW_ORTHOGRAPHIC)
                o.axial = dot(_ShadowDepthPlane, world);
                #else
                o.axial = o.position.w;
                #endif
                o.uv = input.uv * _ShadowUvST.xy + _ShadowUvST.zw; return o;
            }
            float frag(Varying input) : SV_Target
            {
                // Same coverage policy in light-raster pixels. No view-dependent front-hair fade.
                if (_ShadowActorCoverage.y <= 0) discard;
                float noise = frac(52.9829189 * frac(dot(floor(input.position.xy), float2(.06711056, .00583715))));
                clip(_ShadowActorCoverage.y - noise);
                if (_ShadowActorCoverage.x > .5)
                {
                    float alpha = tex2Dbias(_ShadowAlphaMap, float4(input.uv, 0, _ShadowActorCoverage.z)).a * _ShadowAlpha;
                    clip((alpha - _ShadowCutoff) / max(fwidth(alpha), .0001) + .5);
                }
                #if defined(SCENE_SHADOW_POINT)
                float radial = length(input.world - _ShadowPointOrigin.xyz);
                clip(radial - _ShadowPointOrigin.w); clip(_ShadowFar - radial);
                return radial / _ShadowFar;
                #else
                return input.axial / _ShadowFar;
                #endif
            }
            ENDCG
        }
    }
}
