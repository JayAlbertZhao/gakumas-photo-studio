Shader "Hidden/GakumasPhotoMode/SceneReflectionResolve"
{
    Properties
    {
        _Cull ("Cull", Float) = 2
        [HideInInspector] _MainTex ("Base without receiver indirect specular", 2D) = "black" {}
        [HideInInspector] _ReflectAlpha ("Alpha", 2D) = "white" {}
        [HideInInspector] _ReflectSmoothness ("Smoothness", 2D) = "white" {}
        [HideInInspector] _ReflectF0Map ("Linear F0 multiplier", 2D) = "white" {}
        [HideInInspector] _ReflectNormal ("Linear tangent-space RGB normal", 2D) = "white" {}
        [HideInInspector] _ReflectProbe ("Probe cube", Cube) = "" {}
        [HideInInspector] _ResolveProbe ("Probe radiance", 2D) = "black" {}
        [HideInInspector] _ResolveResponse ("Material response", 2D) = "black" {}
        [HideInInspector] _ResolveOffset ("Offset and receiver ID", 2D) = "black" {}
        [HideInInspector] _ResolvePlanar ("Planar radiance and coverage", 2D) = "black" {}
        [HideInInspector] _ResolveSsr ("SSR radiance and confidence", 2D) = "black" {}
        [HideInInspector] _ResolveRadiance ("Resolved radiance", 2D) = "black" {}
    }
    SubShader
    {
        Pass
        {
            Name "RECEIVER_PROBE_RESPONSE_OFFSET"
            Cull [_Cull] ZWrite Off ZTest LEqual Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _ReflectAlpha, _ReflectSmoothness, _ReflectF0Map, _ReflectNormal;
            samplerCUBE _ReflectProbe;
            float4 _ReflectAlphaST, _ReflectSmoothnessST, _ReflectF0ST, _ReflectNormalST;
            float4 _ReflectSurface, _ReflectNormalOptions, _ReflectProbeOptions, _ReflectProbeDecode;
            float3 _ReflectVertexScale, _ReflectF0;
            float _ReflectCutoff;
            struct input { float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT; float2 uv : TEXCOORD0; };
            struct varying { float4 pos : SV_POSITION; float3 world : TEXCOORD0; float3 normal : TEXCOORD1; float4 tangent : TEXCOORD2; float2 uv : TEXCOORD3; };
            varying vert(input v)
            {
                varying o; v.vertex.xyz *= _ReflectVertexScale;
                o.pos = UnityObjectToClipPos(v.vertex); o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normal = UnityObjectToWorldNormal(v.normal / _ReflectVertexScale);
                // DrawRenderer does not reliably populate WorldTransformParams.
                // Derive handedness from the actual matrix and explicit scale.
                float handedness = determinant((float3x3)unity_ObjectToWorld) *
                    _ReflectVertexScale.x * _ReflectVertexScale.y * _ReflectVertexScale.z < 0 ? -1 : 1;
                o.tangent = float4(mul((float3x3)unity_ObjectToWorld, v.tangent.xyz * _ReflectVertexScale), v.tangent.w * handedness);
                o.uv = v.uv; return o;
            }
            struct output { float4 probe : SV_Target0; float4 response : SV_Target1; float4 offset : SV_Target2; };
            output frag(varying i)
            {
                clip(tex2D(_ReflectAlpha, i.uv * _ReflectAlphaST.xy + _ReflectAlphaST.zw).a - _ReflectCutoff);
                float3 geometric = normalize(i.normal), shading = geometric;
                float3 tangent = i.tangent.xyz - geometric * dot(i.tangent.xyz, geometric);
                if (_ReflectNormalOptions.x > .5 && dot(tangent, tangent) > 1e-10 && abs(i.tangent.w) > .5)
                {
                    tangent = normalize(tangent);
                    float3 mapped = tex2D(_ReflectNormal, i.uv * _ReflectNormalST.xy + _ReflectNormalST.zw).rgb * 2 - 1;
                    mapped.xy *= _ReflectNormalOptions.y;
                    mapped.z = max(mapped.z, 1e-5);
                    mapped = normalize(mapped);
                    shading = normalize(tangent * mapped.x + cross(geometric, tangent) * i.tangent.w * mapped.y + geometric * mapped.z);
                }
                float3 view = unity_OrthoParams.w > .5 ? normalize(UNITY_MATRIX_V[2].xyz) : normalize(_WorldSpaceCameraPos.xyz - i.world);
                float smoothness = saturate(_ReflectSurface.x * tex2D(_ReflectSmoothness, i.uv * _ReflectSmoothnessST.xy + _ReflectSmoothnessST.zw).r);
                float3 f0 = saturate(_ReflectF0 * tex2D(_ReflectF0Map, i.uv * _ReflectF0ST.xy + _ReflectF0ST.zw).rgb);
                float3 fresnel = f0 + (1 - f0) * pow(1 - saturate(dot(shading, view)), 5);
                float3 response = fresnel * smoothness * _ReflectSurface.y * _ReflectSurface.z;
                float3 probe = 0;
                if (_ReflectProbeOptions.x > .5)
                {
                    float3 direction = reflect(-view, shading);
                    float4 encoded = texCUBElod(_ReflectProbe, float4(direction, (1 - smoothness) * _ReflectProbeOptions.z));
                    probe = _ReflectProbeOptions.y > .5 ? DecodeHDR(encoded, _ReflectProbeDecode) : encoded.rgb;
                }
                float3 normalDelta = mul((float3x3)UNITY_MATRIX_V, shading - geometric);
                output o;
                o.probe = float4(probe, 1); o.response = float4(response, 1);
                // Full-resolution distortion; trace still uses geometric normals.
                o.offset = float4(normalDelta.xz * _ReflectNormalOptions.zw, _ReflectSurface.w, 1);
                return o;
            }
            ENDCG
        }
        Pass
        {
            Name "PLANAR_SSR_PROBE_RESOLVE"
            Cull Off ZWrite Off ZTest Always Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _ResolveProbe, _ResolveOffset, _ResolvePlanar, _ResolveSsr;
            float4 _ResolveInputs;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 data = tex2D(_ResolveOffset, i.uv);
                if (data.a < .5) return 0;
                float3 probe = tex2D(_ResolveProbe, i.uv).rgb;
                float2 uv = i.uv + data.xy;
                // Never clamp a ray to the screen edge or cross receiver IDs.
                if (any(uv < 0) || any(uv >= 1) || abs(tex2D(_ResolveOffset, uv).z - data.z) > .25)
                    return float4(probe, 1);
                float4 planar = _ResolveInputs.x > .5 ? tex2D(_ResolvePlanar, uv) : 0;
                if (planar.a > 1e-5) return float4(lerp(probe, planar.rgb, saturate(planar.a)), 1);
                float4 ssr = _ResolveInputs.y > .5 ? tex2D(_ResolveSsr, uv) : 0;
                return float4(lerp(probe, ssr.rgb, saturate(ssr.a)), 1);
            }
            ENDCG
        }
        Pass
        {
            Name "REPLACE_INDIRECT_SPECULAR"
            Cull Off ZWrite Off ZTest Always Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _ResolveResponse, _ResolveRadiance;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 source = tex2D(_MainTex, i.uv);
                source.rgb += tex2D(_ResolveRadiance, i.uv).rgb * tex2D(_ResolveResponse, i.uv).rgb;
                return source;
            }
            ENDCG
        }
    }
}
