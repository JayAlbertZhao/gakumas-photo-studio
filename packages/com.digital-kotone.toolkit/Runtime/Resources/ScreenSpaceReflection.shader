Shader "Hidden/GakumasPhotoMode/ScreenSpaceReflection"
{
    Properties
    {
        _Cull ("Cull", Float) = 2
        [HideInInspector] _MainTex ("HDR source", 2D) = "black" {}
        [HideInInspector] _SsrNormalMask ("Scene normal mask", 2D) = "black" {}
        [HideInInspector] _SsrVisibility ("Visible receivers", 2D) = "black" {}
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
        Texture2D<float4> _SsrNormalMask, _SsrVisibility, _SsrHistoryColor;
        Texture2D<float> _SsrHistoryDepth;
        Texture2D<float> _SsrDepth0;
        Texture2D<float> _SsrDepth1;
        Texture2D<float> _SsrDepth2;
        Texture2D<float> _SsrDepth3;
        Texture2D<float> _SsrDepth4;
        Texture2D<float> _SsrDepth5;
        Texture2D<float> _SsrDepth6;
        Texture2D<float> _SsrDepth7;
        Texture2D<float> _SsrDepth8;
        Texture2D<float> _SsrDepth9;
        Texture2D<float> _SsrDepth10;
        Texture2D<float> _SsrDepth11;
        Texture2D<float> _SsrDepth12;
        Texture2D<float> _SsrDepth13;
        Texture2D<float> _SsrDepth14;
        float4x4 _SsrInverseProjection, _SsrProjection, _SsrView, _SsrInverseView;
        float4x4 _SsrHistoryView, _SsrHistoryViewProjection;
        float4 _SsrSize, _SsrTrace, _SsrFrame, _SsrHistory;
        float DepthAt(int level, int2 pixel)
        {
            if (level == 0) return _SsrDepth0.Load(int3(pixel, 0));
            if (level == 1) return _SsrDepth1.Load(int3(pixel, 0));
            if (level == 2) return _SsrDepth2.Load(int3(pixel, 0));
            if (level == 3) return _SsrDepth3.Load(int3(pixel, 0));
            if (level == 4) return _SsrDepth4.Load(int3(pixel, 0));
            if (level == 5) return _SsrDepth5.Load(int3(pixel, 0));
            if (level == 6) return _SsrDepth6.Load(int3(pixel, 0));
            if (level == 7) return _SsrDepth7.Load(int3(pixel, 0));
            if (level == 8) return _SsrDepth8.Load(int3(pixel, 0));
            if (level == 9) return _SsrDepth9.Load(int3(pixel, 0));
            if (level == 10) return _SsrDepth10.Load(int3(pixel, 0));
            if (level == 11) return _SsrDepth11.Load(int3(pixel, 0));
            if (level == 12) return _SsrDepth12.Load(int3(pixel, 0));
            if (level == 13) return _SsrDepth13.Load(int3(pixel, 0));
            return _SsrDepth14.Load(int3(pixel, 0));
        }
        float3 PositionAtDepth(float2 uv, float depth)
        {
            float2 clipXY = uv * 2 - 1;
            #if UNITY_UV_STARTS_AT_TOP
                clipXY.y = -clipXY.y;
            #endif
            float4 a = mul(_SsrInverseProjection, float4(clipXY, 0, 1));
            float4 b = mul(_SsrInverseProjection, float4(clipXY, 1, 1));
            a /= a.w; b /= b.w;
            return lerp(a.xyz, b.xyz, (-depth - a.z) / (b.z - a.z));
        }
        float2 TextureUv(float4 clip)
        {
            float2 uv = clip.xy / clip.w * .5 + .5;
            #if UNITY_UV_STARTS_AT_TOP
                uv.y = 1 - uv.y;
            #endif
            return uv;
        }
        float3 AlongRay(float u, float3 q0, float3 dq, float k0, float dk)
        {
            return (q0 + dq * u) / (k0 + dk * u);
        }
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
            float4 frag(v2f_img i) : SV_Target
            {
                if (_SsrHistory.x < .5) return 0;
                int2 originPixel = min((int2)(i.uv * _SsrSize.xy), (int2)_SsrSize.xy - 1);
                float4 packed = _SsrNormalMask.Load(int3(originPixel, 0));
                float originDepth = DepthAt(0, originPixel);
                if (packed.a < .5 || _SsrVisibility.Load(int3(originPixel, 0)).r < .5 || originDepth >= _SsrFrame.x) return 0;
                float3 p = PositionAtDepth(i.uv, originDepth);
                float3 normalWorld = normalize(packed.rgb * 2 - 1);
                float3 normal = normalize(mul((float3x3)_SsrView, normalWorld));
                float3 incident = _SsrFrame.z > .5 ? float3(0, 0, -1) : normalize(p);
                float3 direction = reflect(incident, normal);
                float3 start = p + normal * _SsrTrace.z + direction * _SsrTrace.z;
                float distance = _SsrTrace.x;
                if (direction.z > 1e-6) distance = min(distance, (-_SsrFrame.y - start.z) / direction.z);
                if (distance <= _SsrTrace.z) return 0;
                float3 end = start + direction * distance;
                float4 clip0 = mul(_SsrProjection, float4(start, 1));
                float4 clip1 = mul(_SsrProjection, float4(end, 1));
                if (clip0.w <= 0 || clip1.w <= 0) return 0;
                float k0 = 1 / clip0.w, dk = 1 / clip1.w - k0;
                float3 q0 = start * k0, dq = end / clip1.w - q0;
                float2 uv0 = TextureUv(clip0);
                float2 duv = TextureUv(clip1) - uv0;
                float2 deltaPixels = duv * _SsrSize.xy;
                float epsilon = 1e-4 / max(max(abs(deltaPixels.x), abs(deltaPixels.y)), 1);
                float u = 0;
                int maximumLevel = (int)_SsrFrame.w, level = min(maximumLevel, 4);
                [loop] for (int iteration = 0; iteration < (int)_SsrTrace.w; iteration++)
                {
                    float2 uv = uv0 + duv * u;
                    if (u >= 1 || any(uv < 0) || any(uv >= 1)) break;
                    float2 pixel = uv * _SsrSize.xy;
                    float cellSize = exp2(level);
                    int2 cell = (int2)floor(pixel / cellSize);
                    float2 edge = (float2(cell) + step(0, deltaPixels)) * cellSize;
                    float ux = abs(deltaPixels.x) > 1e-7 ? (edge.x - uv0.x * _SsrSize.x) / deltaPixels.x : 1e20;
                    float uy = abs(deltaPixels.y) > 1e-7 ? (edge.y - uv0.y * _SsrSize.y) / deltaPixels.y : 1e20;
                    float next = min(1, min(ux, uy));
                    next = max(next, u + epsilon);
                    float depth0 = -AlongRay(u, q0, dq, k0, dk).z;
                    float depth1 = -AlongRay(min(next, 1), q0, dq, k0, dk).z;
                    float sceneDepth = DepthAt(level, cell);
                    // Skip a cell only when the entire ray segment is in front
                    // of its nearest depth. Otherwise descend, never average.
                    if (sceneDepth < _SsrFrame.x && max(depth0, depth1) >= sceneDepth - _SsrTrace.y)
                    {
                        if (level > 0) { level--; continue; }
                        float3 hitNormal = normalize(_SsrNormalMask.Load(int3(cell, 0)).rgb * 2 - 1);
                        float3 worldDirection = mul((float3x3)_SsrInverseView, direction);
                        if (any(cell != originPixel) && min(depth0, depth1) <= sceneDepth + _SsrTrace.y &&
                            dot(hitNormal, worldDirection) < -.01)
                        {
                            float divisor = dq.z + sceneDepth * dk;
                            float hitU = abs(divisor) > 1e-8 ? (-sceneDepth * k0 - q0.z) / divisor : u;
                            float3 hit = AlongRay(clamp(hitU, u, next), q0, dq, k0, dk);
                            float4 world = mul(_SsrInverseView, float4(hit, 1));
                            float4 previousClip = mul(_SsrHistoryViewProjection, world);
                            if (previousClip.w <= 0) return 0;
                            float2 previousUv = TextureUv(previousClip);
                            if (any(previousUv < 0) || any(previousUv >= 1)) return 0;
                            int2 previousPixel = (int2)(previousUv * _SsrSize.xy);
                            float previousDepth = _SsrHistoryDepth.Load(int3(previousPixel, 0));
                            float expectedDepth = -mul(_SsrHistoryView, world).z;
                            if (abs(previousDepth - expectedDepth) > _SsrHistory.y) return 0;
                            float border = min(min(previousUv.x, previousUv.y), min(1 - previousUv.x, 1 - previousUv.y));
                            float confidence = saturate(border / max(_SsrHistory.z, 1e-6)) *
                                saturate(1 - length(hit - p) / _SsrTrace.x);
                            return float4(_SsrHistoryColor.Load(int3(previousPixel, 0)).rgb, confidence);
                        }
                    }
                    u = next + epsilon;
                    level = min(level + 1, maximumLevel);
                }
                return 0;
            }
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
