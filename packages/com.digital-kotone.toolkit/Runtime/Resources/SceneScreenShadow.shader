Shader "Hidden/GakumasPhotoMode/SceneScreenShadow"
{
    SubShader
    {
        Pass
        {
            Name "MAIN_SHADOW_AND_CAPSULE_AO"
            Cull Off ZTest Always ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local __ SCENE_MAIN_LIGHT_SHADOWS
            #include "UnityCG.cginc"
            #if defined(SCENE_MAIN_LIGHT_SHADOWS)
            #define SCENE_LIGHT_SHADOWS 1
            #define SCENE_SHADOW_ORTHOGRAPHIC 1
            #include "SceneLightShadow.hlsl"
            #endif
            sampler2D _ScreenGeometry;
            float4x4 _ScreenInverseViewProjection, _ScreenView;
            float4 _CapsuleA[16], _CapsuleB[16], _CapsuleParameters;
            int _CapsuleCount;
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
            float SphereHit(float3 origin, float3 ray, float3 center, float radius, float limit)
            {
                float3 delta = origin - center; float b = dot(delta, ray), c = dot(delta, delta) - radius * radius;
                float discriminant = b * b - c;
                if (discriminant < 0) return limit;
                float t = -b - sqrt(discriminant);
                return t >= 0 ? min(t, limit) : limit;
            }
            float CapsuleHit(float3 origin, float3 ray, float4 a, float3 b, float limit)
            {
                float3 segment = b - a.xyz, relative = origin - a.xyz;
                float lengthSquared = dot(segment, segment);
                float along = lengthSquared > 1e-12 ? saturate(dot(relative, segment) / lengthSquared) : 0;
                float3 closest = relative - segment * along;
                if (dot(closest, closest) <= a.w * a.w) return 0;
                float hit = min(SphereHit(origin, ray, a.xyz, a.w, limit), SphereHit(origin, ray, b, a.w, limit));
                if (lengthSquared <= 1e-12) return hit;
                float length = sqrt(lengthSquared); float3 axis = segment / length;
                float ro = dot(relative, axis), rd = dot(ray, axis);
                float qa = max(0, 1 - rd * rd), qb = dot(relative, ray) - ro * rd, qc = dot(relative, relative) - ro * ro - a.w * a.w;
                float discriminant = qb * qb - qa * qc;
                if (qa > 1e-8 && discriminant >= 0)
                {
                    float root = sqrt(discriminant);
                    float first = (-qb - root) / qa, last = (-qb + root) / qa;
                    float firstAlong = ro + first * rd, lastAlong = ro + last * rd;
                    if (first >= 0 && firstAlong >= 0 && firstAlong <= length) hit = min(hit, first);
                    if (last >= 0 && lastAlong >= 0 && lastAlong <= length) hit = min(hit, last);
                }
                return hit;
            }
            float AmbientVisibility(float3 world, float3 normal)
            {
                if (_CapsuleCount == 0 || dot(normal, normal) < 1e-6) return 1;
                float3 tangent = normalize(cross(abs(normal.y) < .9 ? float3(0,1,0) : float3(1,0,0), normal));
                float3 bitangent = cross(normal, tangent), origin = world + normal * _CapsuleParameters.w;
                float sum = 0; int samples = (int)_CapsuleParameters.x;
                [loop] for (int i = 0; i < samples; i++)
                {
                    // Six-bit radical inverse covers the supported 8..64 Hammersley sets.
                    uint bits = (uint)i; float v = 0, fraction = .5;
                    [unroll] for (int bit = 0; bit < 6; bit++) { v += (bits & 1u) * fraction; bits >>= 1; fraction *= .5; }
                    float u = (i + .5) / samples, angle = 6.28318530718 * v;
                    float3 ray = sqrt(u) * (cos(angle) * tangent + sin(angle) * bitangent) + sqrt(1 - u) * normal;
                    float nearest = _CapsuleParameters.z;
                    [loop] for (int c = 0; c < _CapsuleCount; c++) nearest = CapsuleHit(origin, ray, _CapsuleA[c], _CapsuleB[c].xyz, nearest);
                    // Cosine-weighted hemisphere integral with finite-distance linear fade.
                    sum += saturate(nearest / _CapsuleParameters.z);
                }
                return lerp(1, sum / samples, _CapsuleParameters.y);
            }
            float2 frag(Screen input) : SV_Target
            {
                float4 geometry = tex2D(_ScreenGeometry, input.uv);
                if (geometry.a <= 0) return 1;
                float3 world = World(input.uv, geometry.a), normal = geometry.xyz * rsqrt(max(dot(geometry.xyz, geometry.xyz), 1e-12));
                float visibility = 1;
                #if defined(SCENE_MAIN_LIGHT_SHADOWS)
                SceneShadowData data; data.worldToShadow = _SingleShadowMatrix; data.atlasST = _SingleShadowST;
                data.depth = _SingleShadowDepth; data.options = _SingleShadowOptions;
                visibility = SceneLightVisibility(world, normal, data);
                #endif
                return float2(visibility, AmbientVisibility(world, normal));
            }
            ENDCG
        }
    }
}
