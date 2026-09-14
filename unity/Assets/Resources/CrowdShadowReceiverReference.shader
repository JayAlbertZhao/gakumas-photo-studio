Shader "Hidden/GakumasPhotoMode/CrowdShadowReceiverReference"
{
    // Fixture-only observation of the two existing receiver-coordinate producers.
    SubShader
    {
        Pass
        {
            Cull Off ZTest Always ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ForwardVertex
            #pragma fragment ObserveForward
            #include "Packages/com.digital-kotone.toolkit/Runtime/Resources/SceneForwardLighting.hlsl"
            float4 ObserveForward(ForwardVarying input) : SV_Target { return float4(input.world, 1); }
            ENDCG
        }
        Pass
        {
            Cull Off ZTest Always ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex LightVert
            #pragma fragment ObserveDeferred
            #include "Packages/com.digital-kotone.toolkit/Runtime/Resources/SceneDecalLight.hlsl"
            float4 ObserveDeferred(LightVarying input) : SV_Target { return float4(LightWorld(input.uv, tex2D(_G2, input.uv).a), 1); }
            ENDCG
        }
    }
}
