Shader "Hidden/GakumasPhotoMode/SceneDeferred"
{
    Properties { _Cull ("Cull", Float) = 2 }
    SubShader
    {
        CGINCLUDE
        #include "UnityCG.cginc"
        #include "SceneGi.hlsl"
        sampler2D _AlbedoMap, _NormalMap, _MosMap, _EmissionMap, _HeightMap;
        sampler2D _G0, _G1, _G2, _G3;
        sampler2D _DecalLightAccumulation;
        float _HasDecalLights;
        sampler2D _BakedDiffuseGi;
        float4 _DirectionalResponse;
        float _GiBaseScale, _HasBakedGi;
        float4 _UvST, _Weights, _HeightParameters;
        float3 _Albedo, _Mos, _Emission, _VertexScale, _MosWeight;
        float _Alpha, _HasNormal, _Cutoff, _ReceiverGroup;
        float4x4 _ViewProjection, _View, _InverseViewProjection, _WorldToDecal;
        float3 _DecalTangent, _DecalBitangent, _DecalFacing;
        float3 _CameraPosition, _CameraForward, _LightDirection, _LightRadiance, _AmbientIrradiance;
        float _Orthographic;
        struct buffers {
            float4 albedo : SV_Target0; float4 normal : SV_Target1; float4 mos : SV_Target2; float4 emission : SV_Target3;
            #if defined(SCENE_GI_OUTPUT)
            float4 gi : SV_Target4;
            #endif
        };
        struct screen { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
        screen fullscreen(float4 vertex : POSITION)
        {
            screen o; o.pos = float4(vertex.xy, 0, 1); o.uv = vertex.xy * .5 + .5;
            #if UNITY_UV_STARTS_AT_TOP
            o.uv.y = 1 - o.uv.y;
            #endif
            return o;
        }
        float3 safeNormal(float3 n) { return n * rsqrt(max(dot(n, n), 1e-12)); }
        float3 mappedNormal(float2 uv)
        {
            // Explicit linear RGB tangent normal, not platform-dependent DXT5nm packing.
            float3 n = tex2D(_NormalMap, uv).rgb * 2 - 1;
            return _HasNormal > .5 ? safeNormal(n + float3(0, 0, 1e-8)) : float3(0, 0, 1);
        }
        float3 worldPosition(float2 uv, float eyeDepth)
        {
            float2 xy = uv * 2 - 1;
            #if UNITY_UV_STARTS_AT_TOP
            xy.y = -xy.y;
            #endif
            float4 a = mul(_InverseViewProjection, float4(xy, 0, 1)); a /= a.w;
            float4 b = mul(_InverseViewProjection, float4(xy, 1, 1)); b /= b.w;
            float da = -mul(_View, a).z, db = -mul(_View, b).z;
            return lerp(a.xyz, b.xyz, (eyeDepth - da) / (db - da));
        }
        buffers readBuffers(float2 uv)
        {
            buffers o; o.albedo = tex2D(_G0, uv); o.normal = tex2D(_G1, uv);
            o.mos = tex2D(_G2, uv); o.emission = tex2D(_G3, uv);
            #if defined(SCENE_GI_OUTPUT)
            o.gi = 0;
            #endif
            return o;
        }
        ENDCG
        Pass
        {
            Name "SCENE_MATERIAL_GEOMETRY"
            Cull [_Cull] ZTest LEqual ZWrite On Blend Off
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex geometry
            #pragma fragment materialData
            #pragma multi_compile_local _ SCENE_GI_OUTPUT
            struct vertex { float4 pos : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; };
            struct geometryOut {
                float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 normal : TEXCOORD1;
                float3 tangent : TEXCOORD2; float sign : TEXCOORD3; float depth : TEXCOORD4;
                float2 uv2 : TEXCOORD5;
            };
            geometryOut geometry(vertex v)
            {
                geometryOut o; v.pos.xyz *= _VertexScale;
                float4 world = mul(unity_ObjectToWorld, v.pos); o.pos = mul(_ViewProjection, world);
                o.depth = -mul(_View, world).z; o.uv = v.uv * _UvST.xy + _UvST.zw;
                o.uv2 = v.uv2;
                o.normal = UnityObjectToWorldNormal(v.normal / _VertexScale);
                o.tangent = mul((float3x3)unity_ObjectToWorld, v.tangent.xyz * _VertexScale);
                o.sign = v.tangent.w * sign(determinant((float3x3)unity_ObjectToWorld)) * sign(_VertexScale.x * _VertexScale.y * _VertexScale.z);
                return o;
            }
            buffers materialData(geometryOut i)
            {
                float4 base = tex2D(_AlbedoMap, i.uv); clip(base.a * _Alpha - _Cutoff);
                float3 n = safeNormal(i.normal), t = safeNormal(i.tangent - n * dot(n, i.tangent));
                float3 map = mappedNormal(i.uv);
                n = safeNormal(map.x * t + map.y * cross(n, t) * i.sign + map.z * n);
                buffers o; o.albedo = float4(saturate(base.rgb * _Albedo), 1);
                o.normal = float4(n, _ReceiverGroup); o.mos = float4(saturate(tex2D(_MosMap, i.uv).rgb * _Mos), i.depth);
                o.emission = float4(clamp(tex2D(_EmissionMap, i.uv).rgb * _Emission, 0, 65504), 0);
                #if defined(SCENE_GI_OUTPUT)
                o.gi = SceneGi(i.uv2, n);
                #endif
                return o;
            }
            ENDCG
        }
        Pass
        {
            Name "PROJECTED_MATERIAL_CHANNELS"
            Cull Off ZTest Always ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex fullscreen
            #pragma fragment decalData
            buffers decalData(screen i)
            {
                buffers o = readBuffers(i.uv);
                if (o.albedo.a < .5 || abs(o.normal.a - _ReceiverGroup) > .1) return o;
                float3 world = worldPosition(i.uv, o.mos.a);
                float3 box = mul(_WorldToDecal, float4(world, 1)).xyz;
                if (any(abs(box) > .5)) return o;
                float3 n = safeNormal(o.normal.xyz);
                if (dot(n, _DecalFacing) < _HeightParameters.z) return o;
                float2 uv = (box.xy + .5) * _UvST.xy + _UvST.zw;
                float4 base = tex2D(_AlbedoMap, uv); float coverage = saturate(base.a * _Alpha);
                float heightCoverage = saturate((_HeightParameters.x * tex2D(_HeightMap, uv).r - (box.z + .5)) / _HeightParameters.y);
                float3 mosWeight = _MosWeight * coverage;
                // Height only modulates AO, leaving the other channels' alpha independent.
                mosWeight.y *= lerp(1, heightCoverage, _Weights.w);
                o.albedo.rgb = lerp(o.albedo.rgb, saturate(base.rgb * _Albedo), coverage * _Weights.x);
                o.mos.rgb = lerp(o.mos.rgb, saturate(tex2D(_MosMap, uv).rgb * _Mos), mosWeight);
                if (_Weights.y > 0 && coverage > 0)
                {
                    float3 t = _DecalTangent - n * dot(n, _DecalTangent);
                    // Projector edge-on normals have no stable tangent; keep the receiver normal.
                    if (dot(t, t) > 1e-8)
                    {
                        t = safeNormal(t); float3 b = cross(n, t); b *= dot(b, _DecalBitangent) >= 0 ? 1 : -1;
                        float3 map = mappedNormal(uv);
                        float3 projected = safeNormal(map.x * t + map.y * b + map.z * n);
                        o.normal.xyz = safeNormal(lerp(n, projected, coverage * _Weights.y));
                    }
                }
                o.emission.rgb = lerp(o.emission.rgb, clamp(tex2D(_EmissionMap, uv).rgb * _Emission, 0, 65504), coverage * _Weights.z);
                return o;
            }
            ENDCG
        }
        Pass
        {
            Name "SCENE_HDR_LIGHTING_AND_DEPTH"
            Cull Off ZTest Always ZWrite On Blend Off
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex fullscreen
            #pragma fragment lighting
            #pragma multi_compile_local __ SCENE_MAIN_LIGHT_SHADOWS
            #if defined(SCENE_MAIN_LIGHT_SHADOWS)
            #define SCENE_LIGHT_SHADOWS 1
            #define SCENE_SHADOW_ORTHOGRAPHIC 1
            #include "SceneLightShadow.hlsl"
            #endif
            struct litOutput { float4 color : SV_Target; float depth : SV_Depth; };
            litOutput lighting(screen i)
            {
                buffers data = readBuffers(i.uv); clip(data.albedo.a - .5);
                float3 world = worldPosition(i.uv, data.mos.a), n = safeNormal(data.normal.xyz);
                float3 v = safeNormal(lerp(_CameraPosition - world, -_CameraForward, _Orthographic));
                float3 l = _LightDirection, h = safeNormal(v + l);
                float nl = saturate(dot(n, l)), nv = saturate(dot(n, v)), nh = saturate(dot(n, h)), vh = saturate(dot(v, h));
                float metallic = data.mos.r, ao = data.mos.g, rough = max(1 - data.mos.b, .045);
                float a2 = rough * rough * rough * rough;
                float denominator = nh * nh * (a2 - 1) + 1;
                float distribution = a2 / max(UNITY_PI * denominator * denominator, 1e-8);
                float visibility = .5 / max(nl * sqrt(nv * nv * (1 - a2) + a2) + nv * sqrt(nl * nl * (1 - a2) + a2), 1e-6);
                float3 f0 = lerp(.04, data.albedo.rgb, metallic), f = f0 + (1 - f0) * pow(1 - vh, 5);
                float3 diffuse = data.albedo.rgb * (1 - metallic) / UNITY_PI;
                // AO affects indirect diffuse only. Emission never receives lighting a second time.
                float3 direct = ((1 - f) * diffuse * _DirectionalResponse.x + distribution * visibility * f * _DirectionalResponse.y) * _LightRadiance * nl;
                // Art-directed inverse-vector diffuse only; not a second specular light or a bounce solver.
                if (_DirectionalResponse.w > 0)
                    direct += (1 - f0) * diffuse * _DirectionalResponse.x * _DirectionalResponse.w * _LightRadiance * saturate(-dot(n, l));
                float4 gi = tex2D(_BakedDiffuseGi, i.uv);
                if (_HasBakedGi > .5 && gi.a > .5) direct *= lerp(1, gi.rgb, _DirectionalResponse.z);
                #if defined(SCENE_MAIN_LIGHT_SHADOWS)
                SceneShadowData shadow; shadow.worldToShadow = _SingleShadowMatrix; shadow.atlasST = _SingleShadowST;
                shadow.depth = _SingleShadowDepth; shadow.options = _SingleShadowOptions;
                direct *= SceneLightVisibility(world, n, shadow);
                #endif
                float3 indirect = diffuse * _AmbientIrradiance * ao;
                // Baked response already includes Lambert integration, unlike legacy incident ambientIrradiance.
                if (_HasBakedGi > .5 && gi.a > .5) indirect = data.albedo.rgb * (1 - metallic) * gi.rgb * _GiBaseScale * ao;
                if (_HasDecalLights > .5) direct += tex2D(_DecalLightAccumulation, i.uv).rgb;
                litOutput o; o.color = float4(clamp(direct + indirect + data.emission.rgb, 0, 65504), 1);
                float4 clipPosition = mul(_ViewProjection, float4(world, 1)); o.depth = clipPosition.z / clipPosition.w;
                #if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
                o.depth = o.depth * .5 + .5;
                #endif
                return o;
            }
            ENDCG
        }
    }
}
