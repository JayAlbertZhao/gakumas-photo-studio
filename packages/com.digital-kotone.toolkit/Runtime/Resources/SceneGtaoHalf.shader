Shader "Hidden/GakumasPhotoMode/SceneGtaoHalf"
{
    SubShader
    {
        Pass
        {
            Name "GTAO_COARSE_RECEIVER_VISIBILITY"
            Cull Off ZTest Always ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _ScreenGeometry;
            float4x4 _ScreenInverseViewProjection, _ScreenView;
            float4 _GtaoCoarseSize;
            struct Screen { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Screen vert(float4 vertex : POSITION)
            {
                Screen o; o.position = float4(vertex.xy, 0, 1); o.uv = vertex.xy * .5 + .5;
                #if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1 - o.uv.y;
                #endif
                return o;
            }
            float3 World(float2 uv, float depth)
            {
                float2 xy = uv * 2 - 1;
                #if UNITY_UV_STARTS_AT_TOP
                xy.y = -xy.y;
                #endif
                float4 a = mul(_ScreenInverseViewProjection, float4(xy, 0, 1)); a /= a.w;
                float4 b = mul(_ScreenInverseViewProjection, float4(xy, 1, 1)); b /= b.w;
                float da = -mul(_ScreenView, a).z, db = -mul(_ScreenView, b).z;
                return lerp(a.xyz, b.xyz, (depth - da) / (db - da));
            }
            #include "SceneGtao.hlsl"
            float4 frag(Screen input) : SV_Target
            {
                float2 first = floor(input.uv * _GtaoCoarseSize.zw) * 2;
                float4 selected = 0; float2 pixel = 0;
                // Native texture-axis 2x2 cells; skip the absent odd-size edge.
                // Nearest positive depth wins. Exact ties keep x-first, then y.
                [unroll] for (int y = 0; y < 2; y++)
                [unroll] for (int x = 0; x < 2; x++)
                {
                    float2 p = first + float2(x,y);
                    if (all(p < _GtaoPixelSize.zw))
                    {
                        float4 g = tex2Dlod(_ScreenGeometry, float4((p + .5) * _GtaoPixelSize.xy, 0, 0));
                        if (g.a > 0 && (selected.a <= 0 || g.a < selected.a)) { selected = g; pixel = p; }
                    }
                }
                if (selected.a <= 0) return float4(0,0,0,1);
                float2 uv = (pixel + .5) * _GtaoPixelSize.xy;
                float3 normal = selected.xyz * rsqrt(max(dot(selected.xyz,selected.xyz),1e-12));
                return float4(pixel, selected.a, GtaoVisibility(uv, World(uv,selected.a), normal, selected.a));
            }
            ENDCG
        }
    }
}
