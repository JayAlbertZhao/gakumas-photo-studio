Shader "Hidden/DigitalKotone/AuthoredPostComposite"
{
    Properties { _MainTex ("Linear HDR source", 2D) = "black" {} }
    // Opt-in bridge for our existing post buffers. Never samples a captured LUT or tone-maps.
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "AUTHORED_PRE_GRADE_HDR"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            Texture2D<float4> _MainTex, _BloomTex, _BlurTex, _ActorDataTex, _OcclusionTex;
            SamplerState sampler_LinearClamp;
            float4 _ActorDataTex_TexelSize, _OutlineColor;
            float _OutlineWidth, _OutlineStrength, _OcclusionStrength;
            float _CapturedChromaticRadialScale, _CapturedBackgroundBlurLiftScale, _CapturedBlurLiftScale;
            float _CapturedGlobalBlurLiftCalibration, _CapturedDiffusionContrastThreshold, _CapturedDiffusionContrastPower;
            float _CapturedDiffusionBlend, _CapturedDiffusionNormalization, _CapturedBloomIntensity, _CapturedBloomScale;
            float4 frag(v2f_img input):SV_Target
            {
                float4 source=_MainTex.SampleLevel(sampler_LinearClamp,input.uv,0);
                float4 center=_ActorDataTex.SampleLevel(sampler_LinearClamp,input.uv,0);
                float2 offset=_ActorDataTex_TexelSize.xy*_OutlineWidth;
                float4 left=_ActorDataTex.SampleLevel(sampler_LinearClamp,input.uv-float2(offset.x,0),0);
                float4 right=_ActorDataTex.SampleLevel(sampler_LinearClamp,input.uv+float2(offset.x,0),0);
                float4 down=_ActorDataTex.SampleLevel(sampler_LinearClamp,input.uv-float2(0,offset.y),0);
                float4 up=_ActorDataTex.SampleLevel(sampler_LinearClamp,input.uv+float2(0,offset.y),0);
                float actor=step(.03125,center.a);
                float edge=actor*(1-min(min(step(.03125,left.a),step(.03125,right.a)),min(step(.03125,down.a),step(.03125,up.a))));
                float normalDifference=2*max(max(length(center.rgb-left.rgb),length(center.rgb-right.rgb)),max(length(center.rgb-down.rgb),length(center.rgb-up.rgb)));
                float outline=saturate((edge+actor*smoothstep(.75,1.35,normalDifference)*.08)*_OutlineStrength);
                float ao=_OcclusionTex.SampleLevel(sampler_LinearClamp,input.uv,0).r;
                float3 sharp=source.rgb*lerp(1,ao,_OcclusionStrength*(1-actor));
                sharp=lerp(sharp,sharp*_OutlineColor.rgb,outline*_OutlineColor.a);
                float2 centered=input.uv*2-1;
                float2 radial=centered*dot(centered,centered)*_CapturedChromaticRadialScale;
                sharp.g=_MainTex.SampleLevel(sampler_LinearClamp,input.uv-radial/3,0).g;
                sharp.b=_MainTex.SampleLevel(sampler_LinearClamp,input.uv-radial*2/3,0).b;
                float3 blur=_BlurTex.SampleLevel(sampler_LinearClamp,input.uv,0).rgb;
                float lift=lerp(_CapturedBackgroundBlurLiftScale,_CapturedBlurLiftScale,actor);
                lift=lerp(lift,_CapturedBlurLiftScale,step(.5,_CapturedGlobalBlurLiftCalibration));
                float3 shaped=saturate(sharp+max(blur-sharp,0)*lift);
                float3 curve=lerp(2*shaped*shaped,1-2*(1-shaped)*(1-shaped),step(_CapturedDiffusionContrastThreshold,shaped));
                shaped=lerp(shaped,curve,_CapturedDiffusionContrastPower);
                float3 bloom=_BloomTex.SampleLevel(sampler_LinearClamp,input.uv,0).rgb;
                // Profile exposure is baked into the authored LUT, applied exactly once after HDR effects.
                float3 hdr=bloom*(_CapturedBloomIntensity*_CapturedBloomScale)+(sharp+shaped*_CapturedDiffusionBlend)*_CapturedDiffusionNormalization;
                return float4(hdr,source.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
