Shader "Hidden/GakumasPhotoMode/PlanarReflection"
{
    Properties
    {
        _Cull ("Cull", Float) = 2
        [HideInInspector] _MainTex ("Unused clear input", 2D) = "black" {}
        [HideInInspector] _PlanarCapture ("Capture", 2D) = "black" {}
        [HideInInspector] _PlanarAlpha ("Geometry alpha", 2D) = "white" {}
        [HideInInspector] _PlanarSmoothness ("Smoothness", 2D) = "white" {}
    }
    SubShader
    {
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _PlanarCapture, _PlanarAlpha, _PlanarSmoothness;
        float4 _PlanarAlphaST, _PlanarSmoothnessST, _PlanarPlane, _PlanarOptions;
        float3 _PlanarVertexScale;
        float _PlanarCutoff, _PlanarSmoothnessScale;
        float4x4 _PlanarViewProjection;
        struct input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
        struct varying { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 world : TEXCOORD1; };
        varying vert(input v)
        {
            varying o;
            v.vertex.xyz *= _PlanarVertexScale;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
            o.uv = v.uv; return o;
        }
        void Cutout(float2 uv)
        { clip(tex2D(_PlanarAlpha, uv * _PlanarAlphaST.xy + _PlanarAlphaST.zw).a - _PlanarCutoff); }
        ENDCG
        Pass
        {
            Name "CLEAR_COVERAGE"
            Cull Off ZWrite Off ZTest Always Blend Off ColorMask A
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target { return 0; }
            ENDCG
        }
        Pass
        {
            Name "CAPTURE_COVERAGE"
            Cull [_Cull] ZWrite Off ZTest Equal Blend Off ColorMask A
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(varying i) : SV_Target { Cutout(i.uv); return 1; }
            ENDCG
        }
        Pass
        {
            Name "PROJECT_VISIBLE_RECEIVERS"
            Cull [_Cull] ZWrite Off ZTest LEqual Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(varying i) : SV_Target
            {
                Cutout(i.uv);
                clip(_PlanarOptions.z - abs(dot(_PlanarPlane, float4(i.world, 1))));
                float4 projected = mul(_PlanarViewProjection, float4(i.world, 1));
                if (projected.w <= 0) return 0;
                float2 uv = projected.xy / projected.w * .5 + .5;
                #if UNITY_UV_STARTS_AT_TOP
                    uv.y = 1 - uv.y;
                #endif
                if (any(uv < 0) || any(uv > 1)) return 0;
                float smoothness = saturate(_PlanarSmoothnessScale * tex2D(_PlanarSmoothness, i.uv * _PlanarSmoothnessST.xy + _PlanarSmoothnessST.zw).r);
                // Independent box-mip approximation; not a GGX convolution.
                float roughness = 1 - smoothness;
                float4 reflected = tex2Dlod(_PlanarCapture, float4(uv, 0, roughness * roughness * _PlanarOptions.y));
                // Binary capture coverage becomes fractional at filtered edges.
                // Convert premultiplied mip radiance back to the borrowed API.
                if (reflected.a <= 1e-5) return 0;
                return float4(reflected.rgb / reflected.a, saturate(reflected.a) * _PlanarOptions.x);
            }
            ENDCG
        }
    }
}
