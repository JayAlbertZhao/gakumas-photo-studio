Shader "Hidden/GakumasPhotoMode/OriginalStylePost"
{
    Properties { _MainTex ("Source", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "BLOOM_PREFILTER"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _BloomThreshold, _BloomKnee;
            float4 frag(v2f_img input):SV_Target
            {
                // PS 7AEB255D: half-resolution 4-tap downsample followed by
                // a hard max-channel threshold (0.89000553 in this frame).
                float2 offset = _MainTex_TexelSize.xy;
                float3 color = tex2D(_MainTex, input.uv + float2(-offset.x, -offset.y)).rgb;
                color += tex2D(_MainTex, input.uv + float2(-offset.x, offset.y)).rgb;
                color += tex2D(_MainTex, input.uv + float2(offset.x, -offset.y)).rgb;
                color += tex2D(_MainTex, input.uv + float2(offset.x, offset.y)).rgb;
                color *= 0.25;
                float brightness = max(max(color.r, color.g), color.b);
                float contribution = max(brightness - _BloomThreshold, 0.0) / max(brightness, 0.0001);
                return float4(color * contribution, 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "KAWASE"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _KawaseOffset;
            float4 frag(v2f_img input):SV_Target
            {
                float2 offset = _MainTex_TexelSize.xy * (_KawaseOffset + 0.5);
                float3 color = tex2D(_MainTex, input.uv + float2(-offset.x, -offset.y)).rgb;
                color += tex2D(_MainTex, input.uv + float2(offset.x, -offset.y)).rgb;
                color += tex2D(_MainTex, input.uv + float2(-offset.x, offset.y)).rgb;
                color += tex2D(_MainTex, input.uv + float2(offset.x, offset.y)).rgb;
                return float4(color * 0.25, 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "BLOOM_UPSAMPLE"
            Blend Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex, _BloomTex;
            float4 _BloomTex_TexelSize;
            float4 frag(v2f_img input):SV_Target
            {
                float3 high = tex2D(_MainTex, input.uv).rgb;
                float2 offset = _BloomTex_TexelSize.xy * 0.70344545;
                float3 low = tex2D(_BloomTex, input.uv + float2(-offset.x, -offset.y)).rgb;
                low += tex2D(_BloomTex, input.uv + float2(-offset.x, offset.y)).rgb;
                low += tex2D(_BloomTex, input.uv + float2(offset.x, -offset.y)).rgb;
                low += tex2D(_BloomTex, input.uv + float2(offset.x, offset.y)).rgb;
                low *= 0.25;
                // Captured 3450B5B7 chain: four-tap low level plus the current
                // level, with no ad-hoc attenuation.  A single 0.18920708 scale
                // is applied after the final half-resolution upsample.
                return float4(high + low, 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "SCREEN_SPACE_OCCLUSION"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _CameraDepthNormalsTexture;
            float4 _CameraDepthNormalsTexture_TexelSize;
            float _AORadius, _AOIntensity;

            void ReadDepthNormal(float2 uv, out float depth, out float3 normal)
            {
                DecodeDepthNormal(tex2D(_CameraDepthNormalsTexture, uv), depth, normal);
            }

            float4 frag(v2f_img input):SV_Target
            {
                float centerDepth;
                float3 centerNormal;
                ReadDepthNormal(input.uv, centerDepth, centerNormal);
                if (centerDepth >= 0.9999) return 1.0;

                const float2 directions[8] = {
                    float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1),
                    float2(0.7071, 0.7071), float2(-0.7071, 0.7071),
                    float2(0.7071, -0.7071), float2(-0.7071, -0.7071)
                };
                float2 texel = _CameraDepthNormalsTexture_TexelSize.xy * _AORadius;
                float occlusion = 0.0;
                [unroll] for (int index = 0; index < 8; index++)
                {
                    float sampleDepth;
                    float3 sampleNormal;
                    ReadDepthNormal(input.uv + directions[index] * texel, sampleDepth, sampleNormal);
                    float depthDelta = centerDepth - sampleDepth;
                    float nearer = saturate((depthDelta - 0.00018) * 150.0);
                    float range = saturate(1.0 - abs(depthDelta) * 48.0);
                    float crease = saturate(1.0 - dot(centerNormal, sampleNormal)) * 0.22;
                    occlusion += max(nearer * range, crease * range);
                }
                float ao = saturate(1.0 - occlusion * (0.125 * _AOIntensity));
                return float4(ao, ao, ao, 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "OCCLUSION_BILATERAL_BLUR"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex, _CameraDepthNormalsTexture;
            float4 _MainTex_TexelSize;

            float ReadDepth(float2 uv)
            {
                float depth;
                float3 normal;
                DecodeDepthNormal(tex2D(_CameraDepthNormalsTexture, uv), depth, normal);
                return depth;
            }

            float4 frag(v2f_img input):SV_Target
            {
                float centerDepth = ReadDepth(input.uv);
                float sum = tex2D(_MainTex, input.uv).r * 0.40;
                float weightSum = 0.40;
                const float2 directions[4] = {
                    float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1)
                };
                [unroll] for (int index = 0; index < 4; index++)
                {
                    float2 uv = input.uv + directions[index] * _MainTex_TexelSize.xy;
                    float weight = 0.15 * exp2(-abs(ReadDepth(uv) - centerDepth) * 1600.0);
                    sum += tex2D(_MainTex, uv).r * weight;
                    weightSum += weight;
                }
                float ao = sum / max(weightSum, 0.0001);
                return float4(ao, ao, ao, 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "FINAL_COMPOSITE"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex, _BloomTex, _BlurTex, _ActorDataTex, _OcclusionTex;
            sampler3D _CapturedColorLut;
            float4 _ActorDataTex_TexelSize;
            float _BloomIntensity, _Exposure, _Saturation, _Contrast, _OcclusionStrength;
            float _OutlineWidth, _OutlineStrength;
            float _UseCapturedColorLut;
            float _CapturedPostInputScale;
            float _CapturedPostExposure;
            float _CapturedBlurLiftScale;
            float _CapturedBackgroundBlurLiftScale;
            float _CapturedBloomScale;
            float _CapturedBloomIntensity;
            float _CapturedDiffusionContrastThreshold;
            float _CapturedDiffusionContrastPower;
            float _CapturedDiffusionBlend;
            float _CapturedDiffusionNormalization;
            float _CapturedChromaticRadialScale;
            float _CapturedGlobalBlurLiftCalibration;
            float4 _OutlineColor;

            float3 AcesFilm(float3 x)
            {
                const float a = 2.51;
                const float b = 0.03;
                const float c = 2.43;
                const float d = 0.59;
                const float e = 0.14;
                return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
            }

            float4 frag(v2f_img input):SV_Target
            {
                float3 hdr = tex2D(_MainTex, input.uv).rgb;
                float3 bloom = tex2D(_BloomTex, input.uv).rgb;
                float ao = tex2D(_OcclusionTex, input.uv).r;

                float2 texel = _ActorDataTex_TexelSize.xy * _OutlineWidth;
                float4 center = tex2D(_ActorDataTex, input.uv);
                float4 left = tex2D(_ActorDataTex, input.uv + float2(-texel.x, 0));
                float4 right = tex2D(_ActorDataTex, input.uv + float2(texel.x, 0));
                float4 down = tex2D(_ActorDataTex, input.uv + float2(0, -texel.y));
                float4 up = tex2D(_ActorDataTex, input.uv + float2(0, texel.y));
                float centerMask = step(0.03125, center.a);
                // The captured AO draws precede the active Actor MRT segment.
                // Their result is already present in the background HDR that
                // StencilDeferred copies into the shared MRT; Actor pixels are
                // shaded afterwards and do not receive a second full-screen AO.
                hdr *= lerp(1.0, ao, _OcclusionStrength * (1.0 - centerMask));
                float minMask = min(min(step(0.03125, left.a), step(0.03125, right.a)),
                                    min(step(0.03125, down.a), step(0.03125, up.a)));
                float silhouette = centerMask * (1.0 - minMask);
                float3 normal = center.rgb * 2.0 - 1.0;
                float normalEdge = max(max(length(normal - (left.rgb * 2.0 - 1.0)), length(normal - (right.rgb * 2.0 - 1.0))),
                                       max(length(normal - (down.rgb * 2.0 - 1.0)), length(normal - (up.rgb * 2.0 - 1.0))));
                // The actor pass already carries authored crease/definition maps.  Keep this
                // term only for large geometric breaks; a lower threshold outlines the split
                // normals down the center of the face, which the original composite does not.
                normalEdge = centerMask * smoothstep(0.75, 1.35, normalEdge) * 0.08;
                float outline = saturate((silhouette + normalEdge) * _OutlineStrength);
                hdr = lerp(hdr, hdr * _OutlineColor.rgb, outline * _OutlineColor.a);

                float3 color;
                if (_UseCapturedColorLut > 0.5)
                {
                    // Literal PS 6460E6E1 dataflow. t0 is the full-resolution
                    // temporal image, t1 is a 720x405 separable Gaussian copy,
                    // and t3 is the already-scaled half-resolution bloom chain.
                    // The shader also introduces a tiny radial RGB separation.
                    float2 centered = input.uv * 2.0 - 1.0;
                    float2 radial = centered * dot(centered, centered) *
                        _CapturedChromaticRadialScale;
                    float2 greenUv = input.uv - radial * (1.0 / 3.0);
                    float2 blueUv = input.uv - radial * (2.0 / 3.0);
                    float3 sharp = float3(hdr.r,
                                          tex2D(_MainTex, greenUv).g,
                                          tex2D(_MainTex, blueUv).b);
                    float3 blurred = tex2D(_BlurTex, input.uv).rgb;
                    // The captured composite uses max(sharp, blurred).  The
                    // reconstructed studio has no matching 3D scene behind the
                    // Actor, so its low-frequency surface exceeds the captured
                    // positive lift on Actor pixels even though sharp t0 is
                    // already aligned.  The measured compensation is therefore
                    // scoped to Actor pixels.  Background pixels retain the
                    // literal DXBC contract, which is required once the original
                    // riverbed scene can be loaded.  The global diagnostic switch
                    // preserves the pre-pass177 behaviour for controlled A/B.
                    float3 blurLift = max(blurred - sharp, 0.0);
                    float blurLiftScale = lerp(_CapturedBackgroundBlurLiftScale,
                                               _CapturedBlurLiftScale, centerMask);
                    blurLiftScale = lerp(blurLiftScale, _CapturedBlurLiftScale,
                                         step(0.5, _CapturedGlobalBlurLiftCalibration));
                    float3 shapedInput = saturate(sharp + blurLift * blurLiftScale);
                    float3 curveLow = 2.0 * shapedInput * shapedInput;
                    float3 curveHigh = 1.0 - 2.0 * (1.0 - shapedInput) * (1.0 - shapedInput);
                    float3 curve = lerp(curveLow, curveHigh,
                        step(_CapturedDiffusionContrastThreshold, shapedInput));
                    float3 shaped = lerp(shapedInput, curve,
                        _CapturedDiffusionContrastPower);
                    float3 capturedBloom = bloom *
                        (_CapturedBloomIntensity * _CapturedBloomScale);
                    float3 gradedInput = clamp((capturedBloom +
                                                (sharp + shaped * _CapturedDiffusionBlend) *
                                                    _CapturedDiffusionNormalization) *
                                               (_CapturedPostExposure * _CapturedPostInputScale), 0.0, 100.0);
                    float3 coordinate = min(log2(gradedInput * 5.555556 + 0.047996) * 0.0735 + 0.386036, 1.0);
                    coordinate = coordinate * (31.0 / 32.0) + (0.5 / 32.0);
                    color = tex3D(_CapturedColorLut, coordinate).rgb;
                }
                else
                {
                    hdr += bloom * _BloomIntensity;
                    color = AcesFilm(max(hdr * _Exposure, 0.0));
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    color = lerp(luma.xxx, color, _Saturation);
                    color = (color - 0.5) * _Contrast + 0.5;
                }
                return float4(color, 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "EDGE_AA_RESOLVE"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float Luma(float3 color)
            {
                return dot(color, float3(0.299, 0.587, 0.114));
            }

            float4 frag(v2f_img input):SV_Target
            {
                float2 texel = _MainTex_TexelSize.xy;
                float3 rgbNW = tex2D(_MainTex, input.uv + texel * float2(-1, 1)).rgb;
                float3 rgbNE = tex2D(_MainTex, input.uv + texel * float2(1, 1)).rgb;
                float3 rgbSW = tex2D(_MainTex, input.uv + texel * float2(-1, -1)).rgb;
                float3 rgbSE = tex2D(_MainTex, input.uv + texel * float2(1, -1)).rgb;
                float3 rgbM = tex2D(_MainTex, input.uv).rgb;
                float lumaNW = Luma(rgbNW), lumaNE = Luma(rgbNE);
                float lumaSW = Luma(rgbSW), lumaSE = Luma(rgbSE), lumaM = Luma(rgbM);
                float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
                float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
                if (lumaMax - lumaMin < max(0.045, lumaMax * 0.15)) return float4(rgbM, 1.0);

                float2 direction;
                direction.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
                direction.y = (lumaNW + lumaSW) - (lumaNE + lumaSE);
                float reduction = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.03125, 0.0078125);
                float reciprocalMinimum = 1.0 / (min(abs(direction.x), abs(direction.y)) + reduction);
                direction = clamp(direction * reciprocalMinimum, -4.0, 4.0) * texel;
                float3 rgbA = 0.5 * (
                    tex2D(_MainTex, input.uv + direction * (1.0 / 3.0 - 0.5)).rgb +
                    tex2D(_MainTex, input.uv + direction * (2.0 / 3.0 - 0.5)).rgb);
                float3 rgbB = rgbA * 0.5 + 0.25 * (
                    tex2D(_MainTex, input.uv + direction * -0.5).rgb +
                    tex2D(_MainTex, input.uv + direction * 0.5).rgb);
                float lumaB = Luma(rgbB);
                float3 antialiased = (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;
                return float4(lerp(rgbM, antialiased, 0.48), 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "TEMPORAL_RESOLVE"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex, _HistoryTex, _CameraMotionVectorsTexture, _CameraDepthTexture;
            float4 _MainTex_TexelSize;
            float _HistoryValid, _TemporalBlend;

            float Luma(float3 color)
            {
                return dot(color, float3(0.212673, 0.715152, 0.072175));
            }

            void ConsiderNearestMotion(float2 uv, inout float bestDepth, inout float2 bestMotion)
            {
                float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
#if defined(UNITY_REVERSED_Z)
                bool nearer = depth > bestDepth;
#else
                bool nearer = depth < bestDepth;
#endif
                if (nearer)
                {
                    bestDepth = depth;
                    bestMotion = tex2D(_CameraMotionVectorsTexture, uv).xy;
                }
            }

            float4 frag(v2f_img input):SV_Target
            {
                float2 uv = input.uv;
                float3 current = tex2D(_MainTex, uv).rgb;
                if (_HistoryValid < 0.5) return float4(current, 1.0);

                // C5783A18 gathers the packed depth channel from two diagonally
                // adjacent 2x2 quads, selects the nearest of their seven unique
                // pixels, then reads motion at that pixel.  Unity exposes depth
                // and motion separately, but the selection topology is identical.
                float2 texel = _MainTex_TexelSize.xy;
#if defined(UNITY_REVERSED_Z)
                float nearestDepth = 0.0;
#else
                float nearestDepth = 1.0;
#endif
                float2 motion = tex2D(_CameraMotionVectorsTexture, uv).xy;
                ConsiderNearestMotion(uv + texel * float2( 0,-1), nearestDepth, motion);
                ConsiderNearestMotion(uv + texel * float2( 0, 0), nearestDepth, motion);
                ConsiderNearestMotion(uv + texel * float2(-1, 0), nearestDepth, motion);
                ConsiderNearestMotion(uv + texel * float2(-1,-1), nearestDepth, motion);
                ConsiderNearestMotion(uv + texel * float2( 0, 1), nearestDepth, motion);
                ConsiderNearestMotion(uv + texel * float2( 1, 1), nearestDepth, motion);
                ConsiderNearestMotion(uv + texel * float2( 1, 0), nearestDepth, motion);
                float2 previousUv = uv - motion;
                float inside = step(0.0, previousUv.x) * step(previousUv.x, 1.0)
                             * step(0.0, previousUv.y) * step(previousUv.y, 1.0);
                if (inside < 0.5) return float4(current, 1.0);

                float3 minimum = current;
                float3 maximum = current;
                float3 mean = current;
                float3 secondMoment = current * current;
                const float2 offsets[8] = {
                    float2(-1,-1), float2(0,-1), float2(1,-1), float2(-1,0),
                    float2(1,0), float2(-1,1), float2(0,1), float2(1,1)
                };
                [unroll] for (int index = 0; index < 8; index++)
                {
                    float3 sampleColor = tex2D(_MainTex, uv + offsets[index] * texel).rgb;
                    minimum = min(minimum, sampleColor);
                    maximum = max(maximum, sampleColor);
                    mean += sampleColor;
                    secondMoment += sampleColor * sampleColor;
                }
                mean *= 1.0 / 9.0;
                secondMoment *= 1.0 / 9.0;

                float3 history = tex2D(_HistoryTex, previousUv).rgb;
                // Exact high-level form of bound PS C5783A18. It uses a 3x3
                // variance box with gamma=0.9, clips history to that AABB along
                // the history-to-centre ray, then blends in luminance-compressed
                // space with current weight 0.05 (history weight 0.95).
                float3 deviation = sqrt(abs(secondMoment - mean * mean)) * 0.9;
                float3 lower = max(minimum, mean - deviation);
                float3 upper = min(maximum, mean + deviation);
                float3 midpoint = (lower + upper) * 0.5;
                float3 extent = max((upper - lower) * 0.5, 0.000061.xxx);
                float3 delta = history - midpoint;
                float largest = max(abs(delta.x / extent.x),
                                    max(abs(delta.y / extent.y), abs(delta.z / extent.z)));
                float3 clippedHistory = largest > 1.0 ? midpoint + delta / largest : history;

                float3 historyCompressed = clippedHistory / (1.0 + Luma(clippedHistory));
                float3 currentCompressed = current / (1.0 + Luma(current));
                float currentWeight = lerp(1.0, 1.0 - _TemporalBlend, inside);
                float3 mixedCompressed = lerp(historyCompressed, currentCompressed, currentWeight);
                float inverseWeight = max(1.0 - Luma(mixedCompressed), 0.000061);
                return float4(max(mixedCompressed / inverseWeight, 0.0), 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "LEGACY_ACTOR_DIFFUSION_DIAGNOSTIC"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex, _ActorDataTex;
            float4 _MainTex_TexelSize;
            float _DiffusionStrength;

            float Luma(float3 color)
            {
                return dot(color, float3(0.212673, 0.715152, 0.072175));
            }

            float4 frag(v2f_img input):SV_Target
            {
                float2 uv = input.uv;
                float3 center = tex2D(_MainTex, uv).rgb;
                float4 actor = tex2D(_ActorDataTex, uv);
                float materialType = floor(actor.a * 16.0 + 0.5) - 1.0;
                float isFaceSkin = 1.0 - step(0.49, abs(materialType - 9.0));
                if (isFaceSkin < 0.5 || _DiffusionStrength <= 0.0001)
                    return float4(center, 1.0);

                float3 centerNormal = normalize(actor.rgb * 2.0 - 1.0);
                float2 texel = _MainTex_TexelSize.xy;
                float3 sum = center * 0.28;
                float weightSum = 0.28;
                const float2 offsets[8] = {
                    float2(-1,-1), float2(0,-1), float2(1,-1), float2(-1,0),
                    float2(1,0), float2(-1,1), float2(0,1), float2(1,1)
                };
                [unroll] for (int index = 0; index < 8; index++)
                {
                    float2 sampleUv = uv + offsets[index] * texel;
                    float4 sampleActor = tex2D(_ActorDataTex, sampleUv);
                    float sameMaterial = 1.0 - step(0.02, abs(sampleActor.a - actor.a));
                    float3 sampleNormal = normalize(sampleActor.rgb * 2.0 - 1.0);
                    float normalWeight = smoothstep(0.72, 0.98, dot(centerNormal, sampleNormal));
                    float kernelWeight = ((index & 1) != 0) ? 0.105 : 0.075;
                    float weight = sameMaterial * normalWeight * kernelWeight;
                    sum += tex2D(_MainTex, sampleUv).rgb * weight;
                    weightSum += weight;
                }

                float3 diffused = sum / max(weightSum, 0.0001);
                // Historical local diagnostic only. Bound PS C5783A is the temporal
                // resolve, not a skin diffusion shader; production bypasses this pass.
                diffused *= (Luma(center) + 0.0001) / (Luma(diffused) + 0.0001);
                diffused = clamp(diffused, center * 0.82, center * 1.18);
                return float4(lerp(center, diffused, _DiffusionStrength), 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "CAPTURED_SCENE_DOWNSAMPLE"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _CapturedBlurTexelSize;

            float4 frag(v2f_img input):SV_Target
            {
                // PS 297F9D16: four samples at +/- half a destination texel.
                float2 offset = _CapturedBlurTexelSize.xy * 0.5;
                float4 color = tex2D(_MainTex, input.uv + float2(-offset.x, -offset.y));
                color += tex2D(_MainTex, input.uv + float2(-offset.x, offset.y));
                color += tex2D(_MainTex, input.uv + float2(offset.x, -offset.y));
                color += tex2D(_MainTex, input.uv + float2(offset.x, offset.y));
                return color * 0.25;
            }
            ENDCG
        }

        Pass
        {
            Name "CAPTURED_SCENE_GAUSSIAN"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float2 _CapturedGaussianDirection;
            float _CapturedDiffusionStride;

            float4 frag(v2f_img input):SV_Target
            {
                // PS 585D350A/61F76919: separable 9-tap kernel with a 0.36
                // texel stride and weights summing to 5.8 (1/5.8=0.172414).
                const float weights[9] = { 0.25, 0.5, 0.75, 0.9, 1.0, 0.9, 0.75, 0.5, 0.25 };
                float2 stepUv = _MainTex_TexelSize.xy *
                    _CapturedGaussianDirection * _CapturedDiffusionStride;
                float4 color = 0.0;
                [unroll] for (int index = 0; index < 9; index++)
                {
                    float offset = index - 4.0;
                    color += min(tex2D(_MainTex, input.uv + stepUv * offset), 1.0) * weights[index];
                }
                return color * 0.1724137931;
            }
            ENDCG
        }
    }
}
