Shader "Hidden/GakumasPhotoMode/ScreenSpaceReflection"
{
    Properties
    {
        _Cull ("Cull", Float) = 2
        [HideInInspector] _MainTex ("HDR source", 2D) = "black" {}
        [HideInInspector] _SsrNormalMask ("Scene normal mask", 2D) = "black" {}
        [HideInInspector] _SsrVisibility ("Visible receivers", 2D) = "black" {}
        [HideInInspector] _SsrPlanarCoverage ("Planar priority regions", 2D) = "black" {}
        [HideInInspector] _SsrHistoryColor ("Actor free history color", 2D) = "black" {}
        [HideInInspector] _SsrHistoryDepth ("Actor free history depth", 2D) = "black" {}
        [HideInInspector] _SsrReflection ("Radiance and confidence", 2D) = "black" {}
        [HideInInspector] _SsrDepth0 ("Scene depth level 0", 2D) = "black" {}
        [HideInInspector] _SsrDepth1 ("Scene depth level 1", 2D) = "black" {}
        [HideInInspector] _SsrDepth2 ("Scene depth level 2", 2D) = "black" {}
        [HideInInspector] _SsrDepth3 ("Scene depth level 3", 2D) = "black" {}
        [HideInInspector] _SsrDepth4 ("Scene depth level 4", 2D) = "black" {}
        [HideInInspector] _SsrDepth5 ("Scene depth level 5", 2D) = "black" {}
        [HideInInspector] _SsrDepth6 ("Scene depth level 6", 2D) = "black" {}
        [HideInInspector] _SsrDepth7 ("Scene depth level 7", 2D) = "black" {}
        [HideInInspector] _SsrDepth8 ("Scene depth level 8", 2D) = "black" {}
        [HideInInspector] _SsrDepth9 ("Scene depth level 9", 2D) = "black" {}
        [HideInInspector] _SsrDepth10 ("Scene depth level 10", 2D) = "black" {}
        [HideInInspector] _SsrDepth11 ("Scene depth level 11", 2D) = "black" {}
        [HideInInspector] _SsrDepth12 ("Scene depth level 12", 2D) = "black" {}
        [HideInInspector] _SsrDepth13 ("Scene depth level 13", 2D) = "black" {}
        [HideInInspector] _SsrDepth14 ("Scene depth level 14", 2D) = "black" {}
    }
    SubShader
    {
        CGINCLUDE
        #include "UnityCG.cginc"
        #include "ScreenSpaceReflectionTrace.hlsl"
        ENDCG
        Pass
        {
            Name "VISIBLE_RECEIVER"
            Cull [_Cull] ZWrite Off ZTest LEqual Blend Off ColorMask R
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _SsrAlphaMask, _SsrSmoothnessMap;
            float4 _SsrAlphaST, _SsrSmoothnessST;
            float3 _SsrVertexScale;
            float _SsrAlphaCutoff, _SsrSmoothness, _SsrSmoothnessThreshold;
            struct input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct varying { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            varying vert(input v)
            {
                varying o; v.vertex.xyz *= _SsrVertexScale;
                o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o;
            }
            float4 frag(varying i) : SV_Target
            {
                clip(tex2D(_SsrAlphaMask, i.uv * _SsrAlphaST.xy + _SsrAlphaST.zw).a - _SsrAlphaCutoff);
                float smoothness = _SsrSmoothness * tex2D(_SsrSmoothnessMap, i.uv * _SsrSmoothnessST.xy + _SsrSmoothnessST.zw).r;
                return float4(step(_SsrSmoothnessThreshold, smoothness), 0, 0, 0);
            }
            ENDCG
        }
        Pass
        {
            Name "HIERARCHICAL_TRACE"
            Cull Off ZWrite Off ZTest Always Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target { return TraceSceneReflection(i.uv); }
            ENDCG
        }
        Pass
        {
            Name "ADDITIVE_HDR_COMPOSITE"
            Cull Off ZWrite Off ZTest Always Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            sampler2D _MainTex, _SsrReflection;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 color = tex2D(_MainTex, i.uv);
                float4 reflected = tex2D(_SsrReflection, i.uv);
                color.rgb += reflected.rgb * reflected.a * _SsrHistory.w;
                return color;
            }
            ENDCG
        }
    }
}
