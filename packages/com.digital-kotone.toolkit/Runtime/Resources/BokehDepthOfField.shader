Shader "Hidden/GakumasPhotoMode/BokehDepthOfField"
{
    Properties { _MainTex ("Source", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"
        #if defined(BOKEH_30)
        #define SAMPLE_COUNT 29
        #else
        #define SAMPLE_COUNT 42
        #endif
        Texture2D<float4> _MainTex;
        float4 _MainTex_TexelSize;
        Texture2D<float> _LinearDepth;
        float4 _FocusRange, _LensParameters;
        float4 _FocusControl;
        Texture2D<float> _FullCoCTexture;
        Texture2D<float4> _DOFBackTexture;
        Texture2D<float4> _DOFFrontTexture;
        Texture2D<float> _InflatedCoCTexture;
        Texture2D<float4> _SourceGatherTexture;
        Texture2D<float4> _CoCGatherTexture;
        Texture2D<float4> _DOFBackGatherTexture;
        Texture2D<float4> _InflateTexture;
        // Explicit samplers/LOD: caller filter/wrap and forced anisotropy must
        // not turn a 30/43 gather into a derivative-dependent anisotropic blur.
        SamplerState sampler_LinearClamp;
        SamplerState sampler_PointClamp;
        float4 _SourceSize;
        float4 _CoCParams;
        float4 _BokehKernel[SAMPLE_COUNT];
        float4 _BokehConstants;

        #define FocusDistance _CoCParams.x
        #define MaxCoC _CoCParams.y
        #define MaxRadius _CoCParams.z

        float SafeWeightDenominator(float value)
        {
            return value == 0.0 ? 1.0 : value;
        }

        float4 FragCoC(v2f_img input) : SV_Target
        {
            float depth=_LinearDepth.Load(int3((int2)input.pos.xy,0));
            if(!(depth>0)||depth>3.402823e+38)return .5;
            float signedRadius;
            if(_FocusControl.x>.5)
            {
                float near=saturate((_FocusRange.x-depth)/_FocusRange.z);
                float far=saturate((depth-_FocusRange.y)/_FocusRange.w);
                signedRadius=(far*far*(3-2*far)-near*near*(3-2*near))*MaxRadius;
            }
            else
            {
                float diameter=_LensParameters.x*(1-_LensParameters.y/depth);
                signedRadius=diameter*_LensParameters.z;
            }
            signedRadius=clamp(signedRadius,-MaxRadius,MaxRadius)*(signedRadius<0?_FocusControl.y:_FocusControl.z);
            return signedRadius/MaxRadius*.5+.5;
        }

        void GatherPrefilterSamples(
            float2 uv,
            out float4 color0, out float4 color1,
            out float4 color2, out float4 color3,
            out float4 signedCoC)
        {
            float4 red = _SourceGatherTexture.GatherRed(sampler_LinearClamp, uv);
            float4 green = _SourceGatherTexture.GatherGreen(sampler_LinearClamp, uv);
            float4 blue = _SourceGatherTexture.GatherBlue(sampler_LinearClamp, uv);
            color0 = float4(red.x, green.x, blue.x, 1.0);
            color1 = float4(red.y, green.y, blue.y, 1.0);
            color2 = float4(red.z, green.z, blue.z, 1.0);
            color3 = float4(red.w, green.w, blue.w, 1.0);
            signedCoC = _CoCGatherTexture.GatherRed(sampler_LinearClamp, uv) * 2.0 - 1.0;
        }

        float4 FragPrefilterFar(v2f_img input) : SV_Target
        {
            float4 c0, c1, c2, c3, coc;
            GatherPrefilterSamples(input.uv, c0, c1, c2, c3, coc);
            float4 farCoC = saturate(coc);
            float4 radius = farCoC * MaxRadius;
            float4 t = saturate(radius / _BokehConstants.x);
            float4 weight = t * t * (3.0 - 2.0 * t);
            float denominator = dot(weight, 1.0);
            if (denominator < 0.004) denominator = 1.0;
            float3 color = c0.rgb * weight.x + c1.rgb * weight.y +
                c2.rgb * weight.z + c3.rgb * weight.w;
            return float4(color / denominator,
                max(max(farCoC.x, farCoC.y), max(farCoC.z, farCoC.w)) * MaxRadius);
        }

        float4 FragPrefilterNear(v2f_img input) : SV_Target
        {
            float4 c0, c1, c2, c3, coc;
            GatherPrefilterSamples(input.uv, c0, c1, c2, c3, coc);
            float4 nearCoC = saturate(-coc);
            float3 color = (c0.rgb + c1.rgb + c2.rgb + c3.rgb) * 0.25;
            return float4(color,
                max(max(nearCoC.x, nearCoC.y), max(nearCoC.z, nearCoC.w)) * MaxRadius);
        }

        float4 GatherInflate(float2 uv, bool fromPackedAlpha)
        {
            return fromPackedAlpha?_InflateTexture.GatherAlpha(sampler_PointClamp,uv):_InflateTexture.GatherRed(sampler_PointClamp,uv);
        }

        float InflateCoC(float2 uv, float extent, bool fromPackedAlpha)
        {
            float2 sourceTexel = _SourceSize.zw;
            float4 positiveNegative = GatherInflate(
                uv + float2(extent, -extent) * sourceTexel, fromPackedAlpha);
            float result = max(positiveNegative.x,
                max(positiveNegative.y, positiveNegative.w));

            float4 positivePositive = GatherInflate(
                uv + float2(extent, extent) * sourceTexel, fromPackedAlpha);
            result = max(result, max(positivePositive.x,
                max(positivePositive.y, positivePositive.z)));

            float4 negativePositive = GatherInflate(
                uv + float2(-extent, extent) * sourceTexel, fromPackedAlpha);
            result = max(result, max(negativePositive.y,
                max(negativePositive.z, negativePositive.w)));

            float4 negativeNegative = GatherInflate(
                uv + float2(-extent, -extent) * sourceTexel, fromPackedAlpha);
            return max(result, max(negativeNegative.x,
                max(negativeNegative.z, negativeNegative.w)));
        }

        float4 FragInflateFirst(v2f_img input) : SV_Target
        {
            return InflateCoC(input.uv, 4.0, true);
        }

        float4 FragInflateSecond(v2f_img input) : SV_Target
        {
            return InflateCoC(input.uv, 8.0, false);
        }

        float FarWeight(float centreCoC, float sampleCoC, float kernelRadius, float radiusScale)
        {
            float coc = max(min(centreCoC, sampleCoC), 0.0);
            return saturate((coc - kernelRadius * radiusScale + _BokehConstants.y) /
                max(_BokehConstants.y, 0.000001));
        }

        float4 FragBlurFar(v2f_img input) : SV_Target
        {
            float4 centre = _MainTex.SampleLevel(sampler_LinearClamp, input.uv, 0);
            if (centre.a <= 0.0) return 0.0;

            float centreWeight = saturate((max(centre.a, 0.0) + _BokehConstants.y) /
                max(_BokehConstants.y, 0.000001));
            float4 accumulation = float4(centre.rgb, 1.0) * centreWeight;
            float radiusScale = centre.a / max(MaxRadius, 0.000001) * 0.875;
            [unroll]
            for (int index = 0; index < SAMPLE_COUNT; index++)
            {
                float4 kernel = _BokehKernel[index];
                float4 sampleValue = _MainTex.SampleLevel(sampler_LinearClamp, input.uv + kernel.wy * radiusScale, 0);
                float weight = FarWeight(centre.a, sampleValue.a, kernel.z, radiusScale);
                accumulation += float4(sampleValue.rgb, 1.0) * weight;
            }
            return float4(accumulation.rgb / SafeWeightDenominator(accumulation.a), centre.a);
        }

        float NearWeight(float centreCoC, float sampleCoC, float kernelRadius)
        {
            const float radiusScale = 0.4375;
            float coc = max(max(centreCoC, sampleCoC), 0.0);
            return saturate((coc - kernelRadius * radiusScale + _BokehConstants.y) /
                max(_BokehConstants.y, 0.000001));
        }

        float4 FragBlurNear(v2f_img input) : SV_Target
        {
            if (_InflatedCoCTexture.SampleLevel(sampler_PointClamp, input.uv, 0).r <= 0.0) return 0.0;

            float4 centre = _MainTex.SampleLevel(sampler_LinearClamp, input.uv, 0);
            float centreWeight = saturate((max(centre.a, 0.0) + _BokehConstants.y) /
                max(_BokehConstants.y, 0.000001));
            float4 accumulation = float4(centre.rgb, 1.0) * centreWeight;
            [unroll]
            for (int index = 0; index < SAMPLE_COUNT; index++)
            {
                float4 kernel = _BokehKernel[index];
                float4 sampleValue = _MainTex.SampleLevel(sampler_LinearClamp, input.uv + kernel.wy * 0.4375, 0);
                float weight = NearWeight(centre.a, sampleValue.a, kernel.z);
                accumulation += float4(sampleValue.rgb, 1.0) * weight;
            }
            float3 color = accumulation.rgb / SafeWeightDenominator(accumulation.a);
            float alpha = saturate((accumulation.a / (SAMPLE_COUNT + 1.0)) * _BokehConstants.w);
            return float4(color * alpha, alpha);
        }

        float4 FragFloodFill(v2f_img input) : SV_Target
        {
            float4 centre = _MainTex.SampleLevel(sampler_LinearClamp, input.uv, 0);
            if (centre.a < 0.001) return centre;

            float radiusScale = centre.a / max(MaxRadius, 0.000001) * 0.5;
            float threshold = MaxRadius * 0.1;
            float3 result = centre.rgb;
            [unroll]
            for (int index = 0; index < 7; index++)
            {
                float4 sampleValue = _MainTex.SampleLevel(sampler_LinearClamp, input.uv + _BokehKernel[index].wy * radiusScale, 0);
                float accepted = step(centre.a - sampleValue.a, threshold);
                result = max(result, max(sampleValue.rgb * accepted, 0.0));
            }
            return float4(result, centre.a);
        }

        float4 FragPostBlurFar(v2f_img input) : SV_Target
        {
            float4 centre = _MainTex.SampleLevel(sampler_LinearClamp, input.uv, 0);
            if (centre.a <= 0.0) return 0.0;

            float2 offset = _SourceSize.zw;
            float4 s0 = _MainTex.SampleLevel(sampler_LinearClamp, input.uv + float2(offset.x, offset.y), 0);
            float4 s1 = _MainTex.SampleLevel(sampler_LinearClamp, input.uv + float2(offset.x, -offset.y), 0);
            float4 s2 = _MainTex.SampleLevel(sampler_LinearClamp, input.uv + float2(-offset.x, -offset.y), 0);
            float4 s3 = _MainTex.SampleLevel(sampler_LinearClamp, input.uv + float2(-offset.x, offset.y), 0);
            float threshold = MaxRadius * 0.1;
            float3 result = centre.rgb;
            result = max(result, max(s0.rgb * step(centre.a - s0.a, threshold), 0.0));
            result = max(result, max(s1.rgb * step(centre.a - s1.a, threshold), 0.0));
            result = max(result, max(s2.rgb * step(centre.a - s2.a, threshold), 0.0));
            result = max(result, max(s3.rgb * step(centre.a - s3.a, threshold), 0.0));
            return float4(result, centre.a);
        }

        float4 FragPostBlurNear(v2f_img input) : SV_Target
        {
            if (_InflatedCoCTexture.SampleLevel(sampler_PointClamp, input.uv, 0).r <= 0.0) return 0.0;
            float2 offset = _SourceSize.zw;
            float4 result = _MainTex.SampleLevel(sampler_LinearClamp, input.uv + float2(offset.x, offset.y), 0);
            result += _MainTex.SampleLevel(sampler_LinearClamp, input.uv + float2(offset.x, -offset.y), 0);
            result += _MainTex.SampleLevel(sampler_LinearClamp, input.uv + float2(-offset.x, -offset.y), 0);
            result += _MainTex.SampleLevel(sampler_LinearClamp, input.uv + float2(-offset.x, offset.y), 0);
            return result * 0.25;
        }

        float3 SampleBackDepthAware(float2 uv, float farCoC)
        {
            float2 offset = _SourceSize.zw;
            float2 uv0 = uv + float2(-offset.x, offset.y);
            float2 uv1 = uv + float2(offset.x, offset.y);
            float2 uv2 = uv + float2(offset.x, -offset.y);
            float2 uv3 = uv - offset;
            float4 alpha = _DOFBackGatherTexture.GatherAlpha(
                sampler_LinearClamp, uv) / max(MaxRadius, 0.000001);
            float4 difference = saturate(farCoC - alpha);
            float threshold = farCoC * 0.75;
            float3 result = 0.0;
            if (all(difference < threshold))
            {
                result = _DOFBackGatherTexture.SampleLevel(
                    sampler_LinearClamp, uv, 0.0).rgb;
            }
            else
            {
                float best = difference.x;
                float2 bestUv = uv0;
                if (difference.y < best) { best = difference.y; bestUv = uv1; }
                if (difference.z < best) { best = difference.z; bestUv = uv2; }
                if (difference.w < best) bestUv = uv3;
                result = _DOFBackGatherTexture.SampleLevel(
                    sampler_PointClamp, bestUv, 0.0).rgb;
            }
            return result;
        }

        float4 Composite(float2 uv, bool includeForeground)
        {
            float farCoC = saturate(_FullCoCTexture.SampleLevel(sampler_PointClamp, uv, 0).r * 2.0 - 1.0);
            float4 source = _MainTex.SampleLevel(sampler_LinearClamp, uv, 0);
            float3 back = SampleBackDepthAware(uv, farCoC);
            float farWeight = smoothstep(_BokehConstants.x, _BokehConstants.y,
                farCoC * MaxRadius);
            float3 color = lerp(source.rgb, back, farWeight);
            if (includeForeground)
            {
                float4 front = _DOFFrontTexture.SampleLevel(sampler_LinearClamp, uv, 0);
                color = color * (1.0 - front.a) + front.rgb;
            }
            return float4(color, source.a);
        }

        float4 FragComposite(v2f_img input) : SV_Target
        {
            return Composite(input.uv, false);
        }

        float4 FragCompositeFore(v2f_img input) : SV_Target
        {
            return Composite(input.uv, true);
        }
        ENDCG

        Pass
        {
            Name "EXPLICIT_LINEAR_DEPTH_FOCUS_RANGE_COC"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragCoC
            #pragma target 3.5
            ENDCG
        }
        Pass
        {
            Name "BOKEH_PREFILTER_FAR"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragPrefilterFar
            #pragma target 4.5
            ENDCG
        }
        Pass
        {
            Name "BOKEH_PREFILTER_NEAR"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragPrefilterNear
            #pragma target 4.5
            ENDCG
        }
        Pass
        {
            Name "INFLATE_COC_FIRST"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragInflateFirst
            #pragma target 4.5
            ENDCG
        }
        Pass
        {
            Name "INFLATE_COC_SECOND"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragInflateSecond
            #pragma target 4.5
            ENDCG
        }
        Pass
        {
            Name "BOKEH_BLUR_FAR_HIGH"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma multi_compile_local __ BOKEH_30
            #pragma fragment FragBlurFar
            #pragma target 4.5
            ENDCG
        }
        Pass
        {
            Name "BOKEH_BLUR_NEAR_HIGH"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma multi_compile_local __ BOKEH_30
            #pragma fragment FragBlurNear
            #pragma target 4.5
            ENDCG
        }
        Pass
        {
            Name "BOKEH_FLOOD_FILL"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragFloodFill
            #pragma target 4.5
            ENDCG
        }
        Pass
        {
            Name "BOKEH_POST_BLUR_FAR"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragPostBlurFar
            #pragma target 3.5
            ENDCG
        }
        Pass
        {
            Name "BOKEH_POST_BLUR_NEAR"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragPostBlurNear
            #pragma target 3.5
            ENDCG
        }
        Pass
        {
            Name "BOKEH_COMPOSITE"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragComposite
            #pragma target 4.5
            ENDCG
        }
        Pass
        {
            Name "BOKEH_COMPOSITE_FORE"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragCompositeFore
            #pragma target 4.5
            ENDCG
        }
    }
    Fallback Off
}
