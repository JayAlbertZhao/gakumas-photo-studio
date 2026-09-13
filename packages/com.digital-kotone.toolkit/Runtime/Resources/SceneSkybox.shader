Shader "Hidden/PhotoStudio/SceneSkybox"
{
    Properties
    {
        _SkyCube ("Linear sky cube", Cube) = "" {}
        _SkyPanorama ("Linear sky panorama", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off ZTest LEqual
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma enable_d3d11_debug_symbols
            #pragma vertex SkyVertex
            #pragma fragment SkyFragment
            #include "UnityCG.cginc"
            samplerCUBE _SkyCube;
            sampler2D _SkyPanorama;
            float4x4 _SkyRotation;
            float3 _SkyZenith, _SkyHorizon, _SkyGround, _SkySunDirection, _SkySunRadiance;
            float4 _SkyTintExposure, _SkySun;
            float _SkyMode, _SkySourceMip, _SkyGradientPower;
            struct Input { float4 vertex : POSITION; };
            struct Varying { float4 position : SV_POSITION; };
            Varying SkyVertex(Input input)
            {
                // Native Skybox geometry provides coverage only. Ignore its
                // camera-centered model translation/scale and keep perspective
                // coverage even for the constant-direction orthographic sky.
                Varying output;
                float3 view = mul((float3x3)UNITY_MATRIX_V, input.vertex.xyz);
                output.position = mul(UNITY_MATRIX_P, float4(view, 0));
                output.position.w = -view.z;
                #if defined(UNITY_REVERSED_Z)
                output.position.z = 0;
                #else
                output.position.z = output.position.w;
                #endif
                return output;
            }
            float4 SkyFragment(Varying input) : SV_Target
            {
                // Reconstruct at the actual pixel center. Interpolating native
                // sky-sphere vertex directions imports subpixel raster snapping
                // into small sun disks and equirectangular seam coordinates.
                // Full viewport is an explicit material/host contract.
                float2 ndc = input.position.xy / _ScreenParams.xy * 2 - 1;
                #if UNITY_UV_STARTS_AT_TOP
                ndc.y = -ndc.y;
                #endif
                float3 view = float3((ndc.x + UNITY_MATRIX_P._m02) / UNITY_MATRIX_P._m00,
                    (ndc.y + UNITY_MATRIX_P._m12) / UNITY_MATRIX_P._m11, -1);
                if (unity_OrthoParams.w > .5) view = float3(0, 0, -1);
                float3 world = mul(transpose((float3x3)UNITY_MATRIX_V), view);
                float3 direction = normalize(mul((float3x3)_SkyRotation, world));
                float3 radiance;
                if (_SkyMode < .5)
                    radiance = lerp(_SkyHorizon, direction.y >= 0 ? _SkyZenith : _SkyGround, pow(abs(direction.y), _SkyGradientPower));
                else if (_SkyMode < 1.5) radiance = texCUBElod(_SkyCube, float4(direction, _SkySourceMip)).rgb;
                else
                {
                    // +Z atU=.5, +X atU=.75, north atV=1. RepeatU/ClampV
                    // are explicit input requirements rather than sampler mutation.
                    float2 uv = float2(atan2(direction.x, direction.z) / (2 * UNITY_PI) + .5, asin(clamp(direction.y, -1, 1)) / UNITY_PI + .5);
                    radiance = tex2Dlod(_SkyPanorama, float4(uv, 0, _SkySourceMip)).rgb;
                }
                if (_SkySun.x > .5)
                {
                    float3 delta = direction - _SkySunDirection;
                    float distanceSquared = dot(delta, delta);
                    float weight = _SkySun.w == 0 ? step(distanceSquared, _SkySun.y) : saturate((_SkySun.y - distanceSquared) / max(_SkySun.y - _SkySun.z, 1e-20));
                    weight = weight * weight * (3 - 2 * weight); radiance += _SkySunRadiance * weight;
                }
                return float4(clamp(max(radiance, 0) * _SkyTintExposure.rgb * _SkyTintExposure.a, 0, 65504), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
