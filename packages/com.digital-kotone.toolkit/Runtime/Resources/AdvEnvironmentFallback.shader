Shader "GakumasPhotoMode/AdvEnvironmentFallback"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
        _Color ("Base Color", Color) = (1,1,1,1)
        _BumpMap ("Normal", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0,2)) = 1
        _DefMap ("Definition (M/E/AO/S)", 2D) = "white" {}
        _DefValue ("Definition Scale", Vector) = (1,1,1,1)
        _Tiling ("Source Tiling", Vector) = (1,1,0,0)
        _Offset ("Source Offset", Vector) = (0,0,0,0)
        _EmissionMap ("Legacy Emission", 2D) = "black" {}
        _EmissionColor ("Emission Color", Color) = (0,0,0,0)
        _EmissionAlbedoScale ("Emission Albedo Scale", Range(0,1)) = 0
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        _UseAlphaClip ("Use Alpha Clip", Float) = 0
        _UseEmission ("Use Legacy Emission", Float) = 0
        _UseDefinition ("Use Definition Map", Float) = 0
        _UseNormal ("Use Normal Map", Float) = 0
        _ShaderFamily ("Source Family", Float) = 3
        _Premultiply ("Premultiply Alpha", Float) = 0
        _EnvironmentReflections ("Environment Reflections", Float) = 1
        _SpecularHighlights ("Specular Highlights", Float) = 1
        _EnvironmentBakedScale ("Environment Baked Scale", Float) = 1
        _EnvironmentBakedChroma ("Environment Baked Chroma", Range(0,1)) = 1
        _EnvironmentDirectScale ("Environment Direct Scale", Float) = 1
        _EnvironmentReflectionScale ("Environment Reflection Scale", Float) = 1
        _EnvironmentUseSourceMainLight ("Use Source Main Light", Float) = 0
        _EnvironmentSourceMainLightDirection ("Source Main Light Direction", Vector) = (0,1,0,0)
        _EnvironmentSourceMainLightColor ("Source Main Light Color", Vector) = (0,0,0,0)
        _EnvironmentSourceSpotCount ("Source Spot Count", Float) = 0
        _EnvironmentUseSourceShadowMask ("Use Source Shadow Mask", Float) = 0
        _EnvironmentUseSourceRealtimeShadow ("Use Source Realtime Shadow", Float) = 0
        _EnvironmentSourceShadowMask ("Source Shadow Mask", 2D) = "white" {}
        _EnvironmentSourceMainOcclusionSelector ("Source Main Occlusion Selector", Vector) = (0,0,0,0)
        _EnvironmentDebugMode ("Environment Debug Mode", Float) = 0
        _LegacyLighting ("PASS282 Legacy Lighting", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _AlphaSrcBlend ("Alpha Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _AlphaDstBlend ("Alpha Dst Blend", Float) = 0
        [Toggle] _ZWrite ("Z Write", Float) = 1
        [Toggle] _AlphaToMask ("Alpha To Coverage", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull [_Cull]
        Blend [_SrcBlend] [_DstBlend], [_AlphaSrcBlend] [_AlphaDstBlend]
        ZWrite [_ZWrite]
        AlphaToMask [_AlphaToMask]

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D _BumpMap;
            sampler2D _DefMap;
            sampler2D _EmissionMap;
            fixed4 _Color;
            fixed4 _EmissionColor;
            float4 _DefValue;
            float4 _Tiling;
            float4 _Offset;
            float _BumpScale;
            float _EmissionAlbedoScale;
            float _Cutoff;
            float _UseAlphaClip;
            float _UseEmission;
            float _UseDefinition;
            float _UseNormal;
            float _ShaderFamily;
            float _Premultiply;
            float _EnvironmentReflections;
            float _SpecularHighlights;
            float _EnvironmentBakedScale;
            float _EnvironmentBakedChroma;
            float _EnvironmentDirectScale;
            float _EnvironmentReflectionScale;
            float _EnvironmentUseSourceMainLight;
            float4 _EnvironmentSourceMainLightDirection;
            float4 _EnvironmentSourceMainLightColor;
            float _EnvironmentSourceSpotCount;
            float4 _EnvironmentSourceSpotPositionRange[8];
            float4 _EnvironmentSourceSpotDirectionScale[8];
            float4 _EnvironmentSourceSpotColorOffset[8];
            float4 _EnvironmentSourceSpotOcclusionSelector[8];
            float _EnvironmentUseSourceShadowMask;
            float _EnvironmentUseSourceRealtimeShadow;
            sampler2D _EnvironmentSourceShadowMask;
            float4 _EnvironmentSourceMainOcclusionSelector;
            float _EnvironmentDebugMode;
            float _LegacyLighting;
            sampler2D _EnvironmentDecalBaseAtlas;
            sampler2D _EnvironmentDecalDefinitionAtlas;
            float _EnvironmentDecalCount;
            float4x4 _EnvironmentDecalWorldToDecal[4];
            float4 _EnvironmentDecalUvScaleBias[4];
            float4 _EnvironmentDecalDefinitionScale;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
                float2 uv : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 uv : TEXCOORD0;
                half3 normal : TEXCOORD1;
                half3 tangent : TEXCOORD2;
                half3 bitangent : TEXCOORD3;
                float3 worldPos : TEXCOORD4;
                float2 lightmapUV : TEXCOORD5;
                SHADOW_COORDS(6)
            };

            v2f vert(appdata value)
            {
                v2f output;
                output.pos = UnityObjectToClipPos(value.vertex);
                output.uv.xy = value.uv * _Tiling.xy + _Offset.xy;
                output.uv.zw = TRANSFORM_TEX(value.uv, _MainTex);
                output.normal = UnityObjectToWorldNormal(value.normal);
                output.tangent = UnityObjectToWorldDir(value.tangent.xyz);
                output.bitangent = cross(output.normal, output.tangent) *
                    (value.tangent.w * unity_WorldTransformParams.w);
                output.worldPos = mul(unity_ObjectToWorld, value.vertex).xyz;
                #ifdef LIGHTMAP_ON
                    output.lightmapUV = value.uv1 * unity_LightmapST.xy + unity_LightmapST.zw;
                #else
                    output.lightmapUV = 0;
                #endif
                TRANSFER_SHADOW(output);
                return output;
            }

            half3 SourceNormal(v2f input, float2 uv)
            {
                half3 geometric = normalize(input.normal);
                if (_UseNormal < 0.5) return geometric;

                // Exact unpack shape recovered from Environment/Default's
                // GBuffer program: x=R*A, y=G, scale xy, reconstruct z and
                // interpolate z by the saturated normal scale.
                half4 packed = tex2D(_BumpMap, uv);
                half2 rawXY = half2(packed.r * packed.a, packed.g) * 2.0h - 1.0h;
                half rawZ = sqrt(max(0.0h, 1.0h - saturate(dot(rawXY, rawXY))));
                half3 tangentNormal = half3(
                    rawXY * _BumpScale,
                    lerp(1.0h, rawZ, saturate(_BumpScale)));
                half3 worldNormal =
                    normalize(input.tangent) * tangentNormal.x +
                    normalize(input.bitangent) * tangentNormal.y +
                    geometric * tangentNormal.z;
                return normalize(worldNormal);
            }

            half3 BakedDiffuse(v2f input, half3 normal)
            {
                #ifdef LIGHTMAP_ON
                    half4 encoded = UNITY_SAMPLE_TEX2D(unity_Lightmap, input.lightmapUV);
                    // The original Environment/Default LIGHTMAP_ON GBuffer
                    // program samples the HDR lightmap and writes sample.rgb
                    // directly to target 3.  The classroom lightmap is BC6H,
                    // so the texture unit has already decoded it to linear
                    // HDR.  Unity's generic DecodeLightmap path applies the
                    // player project's unrelated encoding contract a second
                    // time and over-amplifies the source scene by roughly an
                    // order of magnitude.
                    return encoded.rgb;
                #else
                    return ShadeSH9(half4(normal, 1.0h));
                #endif
            }

            half4 SourceRawBakedOcclusion(v2f input)
            {
                if (_EnvironmentUseSourceShadowMask < 0.5)
                    return half4(1.0h, 1.0h, 1.0h, 1.0h);
                // Environment/Default's SHADOWS_SHADOWMASK GBuffer variant
                // writes the raw source mask for deferred light selection.
                // The local built-in renderer does not enable that source URP
                // keyword, so bind the preserved atlas explicitly.  Dynamic
                // source renderers still receive their interpolated probe
                // occlusion through Unity's per-renderer constant.
                #ifdef LIGHTMAP_ON
                    return tex2D(_EnvironmentSourceShadowMask, input.lightmapUV);
                #else
                    return unity_ProbesOcclusion;
                #endif
            }

            half SourceBakedShadow(half4 rawMask, half4 selector)
            {
                // This is URP BakedShadow's exact selector form.  A zero
                // selector means that the light has no baked occlusion
                // channel and therefore remains fully visible.
                return 1.0h + dot(rawMask - 1.0h, selector);
            }

            half3 SourceSpotDiffuse(
                float3 worldPosition, half3 normal, half4 rawOcclusion)
            {
                half3 value = 0.0h;
                [unroll]
                for (int index = 0; index < 8; index++)
                {
                    if (index >= (int)_EnvironmentSourceSpotCount) continue;
                    float3 toLight = _EnvironmentSourceSpotPositionRange[index].xyz -
                        worldPosition;
                    float distanceSquared = max(dot(toLight, toLight), 1e-4);
                    half3 lightDirection = toLight * rsqrt(distanceSquared);

                    // Same smooth inverse-square range shape used by the
                    // source URP/deferred lighting helpers.
                    float rangeFactor = distanceSquared *
                        _EnvironmentSourceSpotPositionRange[index].w;
                    half rangeAttenuation = saturate(1.0h -
                        (half)(rangeFactor * rangeFactor));
                    rangeAttenuation *= rangeAttenuation;
                    rangeAttenuation /= (half)distanceSquared;

                    // Direction points from the source light into the cone.
                    // y = reciprocal inner/outer cosine span; the matching
                    // offset is stored in spot color.w.
                    half cone = saturate(dot(
                        (half3)_EnvironmentSourceSpotDirectionScale[index].xyz,
                        -lightDirection) *
                        (half)_EnvironmentSourceSpotDirectionScale[index].w +
                        (half)_EnvironmentSourceSpotColorOffset[index].w);
                    cone *= cone;
                    half ndotl = saturate(dot(normal, lightDirection));
                    half bakedShadow = SourceBakedShadow(rawOcclusion,
                        _EnvironmentSourceSpotOcclusionSelector[index]);
                    value += (half3)_EnvironmentSourceSpotColorOffset[index].rgb *
                        (ndotl / UNITY_PI) * rangeAttenuation * cone * bakedShadow;
                }
                return value;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float2 uv = _LegacyLighting > 0.5 ? input.uv.zw : input.uv.xy;
                fixed4 albedo = tex2D(_MainTex, uv) * _Color;
                if (_UseAlphaClip > 0.5) clip(albedo.a - _Cutoff);

                // Source classroom monitors are URP DecalProjectors.  The
                // Photo Studio camera uses the built-in renderer, so replay
                // the serialized projector volume and atlas contract directly
                // against the reconstructed environment fragments.
                half environmentDecalWeight = 0.0h;
                half4 environmentDecalDefinition = 0.0h;
                [unroll]
                for (int decalIndex = 0; decalIndex < 4; decalIndex++)
                {
                    if (decalIndex >= (int)_EnvironmentDecalCount) continue;
                    float3 decalPosition = mul(
                        _EnvironmentDecalWorldToDecal[decalIndex],
                        float4(input.worldPos, 1.0)).xyz;
                    if (max(max(abs(decalPosition.x), abs(decalPosition.y)),
                            abs(decalPosition.z)) > 0.5)
                        continue;
                    float4 atlasContract = _EnvironmentDecalUvScaleBias[decalIndex];
                    float2 decalUv = float2(
                        decalPosition.x + 0.5,
                        decalPosition.z + 0.5) * atlasContract.xy + atlasContract.zw;
                    half4 decalBase = tex2D(_EnvironmentDecalBaseAtlas, decalUv);
                    environmentDecalWeight = decalBase.a;
                    albedo.rgb = lerp(albedo.rgb, decalBase.rgb, decalBase.a);
                    environmentDecalDefinition =
                        tex2D(_EnvironmentDecalDefinitionAtlas, decalUv) *
                        _EnvironmentDecalDefinitionScale;
                    break;
                }

                if (_EnvironmentDebugMode > 0.5 && _EnvironmentDebugMode < 1.5)
                    return fixed4(albedo.rgb, albedo.a);

                half3 normal = _LegacyLighting > 0.5
                    ? normalize(input.normal)
                    : SourceNormal(input, uv);
                normal = normalize(lerp(
                    normal, normalize(input.normal), environmentDecalWeight));
                half3 lightDirection = _EnvironmentUseSourceMainLight > 0.5
                    ? normalize(_EnvironmentSourceMainLightDirection.xyz)
                    : normalize(UnityWorldSpaceLightDir(input.worldPos));
                half3 mainLightColor = _EnvironmentUseSourceMainLight > 0.5
                    ? _EnvironmentSourceMainLightColor.rgb
                    : _LightColor0.rgb;
                // The stock shadow macro is keyed to the player scene's
                // selected ForwardBase light.  It evaluates to zero for the
                // separately supplied mixed source light, so do not apply an
                // unrelated shadow channel here.  Source shadowmask support
                // is reconstructed independently below the lighting baseline.
                half stockShadowAttenuation = SHADOW_ATTENUATION(input);
                half attenuation = _EnvironmentUseSourceMainLight > 0.5
                    ? 1.0h : stockShadowAttenuation;
                half ndotl = saturate(dot(normal, lightDirection));
                half4 sourceRawOcclusion = SourceRawBakedOcclusion(input);
                half sourceMainBakedShadow = SourceBakedShadow(
                    sourceRawOcclusion, _EnvironmentSourceMainOcclusionSelector);
                half sourceMainAttenuation = attenuation;
                if (_EnvironmentUseSourceMainLight > 0.5)
                {
                    sourceMainAttenuation = _EnvironmentUseSourceRealtimeShadow > 0.5
                        ? stockShadowAttenuation : 1.0h;
                    sourceMainAttenuation = min(
                        sourceMainAttenuation, sourceMainBakedShadow);
                }

                if (_EnvironmentDebugMode > 3.5 && _EnvironmentDebugMode < 4.5)
                    return fixed4(sourceRawOcclusion.rgb, albedo.a);
                if (_EnvironmentDebugMode > 4.5 && _EnvironmentDebugMode < 5.5)
                    return fixed4(sourceMainBakedShadow.xxx, albedo.a);
                if (_EnvironmentDebugMode > 5.5 && _EnvironmentDebugMode < 6.5)
                    return fixed4(stockShadowAttenuation.xxx, albedo.a);

                if (_LegacyLighting > 0.5)
                {
                    half3 ambient = ShadeSH9(half4(normal, 1.0h));
                    half3 lighting = ambient + _LightColor0.rgb *
                        (0.28h + 0.72h * ndotl) * attenuation;
                    half3 legacyEmission = tex2D(_EmissionMap, uv).rgb *
                        _EmissionColor.rgb * _UseEmission;
                    return fixed4(albedo.rgb * lighting + legacyEmission, albedo.a);
                }

                // Recovered Default GBuffer channel contract:
                // Def R=metallic, G=emission mask, B=occlusion, A=smoothness.
                half4 definition = _UseDefinition > 0.5
                    ? tex2D(_DefMap, uv) * _DefValue
                    : half4(0.0h, 0.0h, 1.0h, 0.25h);
                definition = lerp(
                    definition, environmentDecalDefinition,
                    environmentDecalWeight);
                half metallic = saturate(definition.r);
                half occlusion = saturate(definition.b);
                half smoothness = saturate(definition.a);

                // Environment/Emission writes its authored base directly to
                // the emission target.  Keep that family independent of the
                // local scene's reconstructed light transport.
                if (_ShaderFamily > 0.5 && _ShaderFamily < 1.5)
                {
                    half3 emissionOnly = albedo.rgb;
                    if (_Premultiply > 0.5) emissionOnly *= albedo.a;
                    return fixed4(emissionOnly, albedo.a);
                }

                half3 bakedSource = BakedDiffuse(input, normal);
                half bakedLuminance = dot(bakedSource,
                    half3(0.2126h, 0.7152h, 0.0722h));
                bakedSource = lerp(bakedLuminance.xxx, bakedSource,
                    saturate(_EnvironmentBakedChroma));
                half3 baked = bakedSource * occlusion * _EnvironmentBakedScale;
                if (_EnvironmentDebugMode > 1.5 && _EnvironmentDebugMode < 2.5)
                    return fixed4(BakedDiffuse(input, normal), albedo.a);
                // The source Environment deferred path applies the Lambert
                // diffuse BRDF normalization.  Its mixed directional light is
                // not selected by the stock ForwardBase light loop after this
                // AssetBundle scene is loaded additively, so the source light
                // contract is supplied explicitly by the runtime.
                half3 direct = (mainLightColor * (ndotl / UNITY_PI) *
                    sourceMainAttenuation +
                    SourceSpotDiffuse(input.worldPos, normal, sourceRawOcclusion)) *
                    _EnvironmentDirectScale;
                if (_EnvironmentDebugMode > 2.5 && _EnvironmentDebugMode < 3.5)
                    return fixed4(direct, albedo.a);
                half3 diffuseColor = albedo.rgb * (1.0h - 0.96h * metallic);
                half3 color = diffuseColor * (baked + direct);

                half3 viewDirection = normalize(_WorldSpaceCameraPos.xyz - input.worldPos);
                half3 halfDirection = normalize(lightDirection + viewDirection);
                half ndoth = saturate(dot(normal, halfDirection));
                half ldoth = saturate(dot(lightDirection, halfDirection));
                half perceptualRoughness = 1.0h - smoothness;
                half roughness = max(0.045h, perceptualRoughness * perceptualRoughness);
                half roughness2 = roughness * roughness;
                half denominator = max(0.001h,
                    ndoth * ndoth * (roughness2 - 1.0h) + 1.0h);
                half distribution = roughness2 /
                    max(0.001h, UNITY_PI * denominator * denominator);
                half3 f0 = lerp(0.04h.xxx, albedo.rgb, metallic);
                half3 fresnel = f0 + (1.0h - f0) * pow(1.0h - ldoth, 5.0h);
                color += direct * distribution * fresnel *
                    (0.25h * _SpecularHighlights);

                half3 reflectionDirection = reflect(-viewDirection, normal);
                half4 encodedReflection = UNITY_SAMPLE_TEXCUBE_LOD(
                    unity_SpecCube0, reflectionDirection, perceptualRoughness * 6.0h);
                half3 reflection = DecodeHDR(encodedReflection, unity_SpecCube0_HDR);
                half nv = saturate(dot(normal, viewDirection));
                half3 environmentFresnel = f0 + (1.0h - f0) * pow(1.0h - nv, 5.0h);
                color += reflection * environmentFresnel * occlusion *
                    smoothness * _EnvironmentReflections *
                    _EnvironmentReflectionScale;

                half emissionMask = max(0.0h, definition.g - 0.02h);
                half3 emissionAlbedo = lerp(
                    1.0h.xxx, albedo.rgb, saturate(_EmissionAlbedoScale));
                color += emissionAlbedo * emissionMask * _EmissionColor.rgb;

                if (_Premultiply > 0.5) color *= albedo.a;
                return fixed4(color, albedo.a);
            }
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Tiling;
            float4 _Offset;
            fixed4 _Color;
            float _Cutoff;
            float _UseAlphaClip;
            struct appdata { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
            struct v2f { V2F_SHADOW_CASTER; float2 uv:TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o;
                o.uv = v.uv * _Tiling.xy + _Offset.xy;
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }
            float4 frag(v2f input) : SV_Target
            {
                if (_UseAlphaClip > 0.5)
                    clip(tex2D(_MainTex, input.uv).a * _Color.a - _Cutoff);
                SHADOW_CASTER_FRAGMENT(input)
            }
            ENDCG
        }
    }
    Fallback Off
}
