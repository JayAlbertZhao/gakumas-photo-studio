Shader "Hidden/GakumasPhotoMode/TileRenderPassReference"
{
    Properties
    {
        _Base ("Base", Vector) = (1,0,0,1)
        _Mos ("MOS", Vector) = (0,1,0,1)
        _Normal ("Normal / ID", Vector) = (0,0,1,4)
        _Emission ("Emission", Vector) = (.125,.25,.5,0)
        _Gi ("GI", Vector) = (.5,1,2,0)
        _Pattern ("Diagnostic pattern", Float) = 0
        _Height ("Diagnostic height", Float) = 64
        _ForbiddenSample ("Alias validation only, never sampled", 2D) = "black" {}
    }
    SubShader
    {
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        float4x4 _ClipFromLocal;
        float4x4 unity_ObjectToWorld;
        float4 _Base, _Mos, _Normal, _Emission, _Gi;
        float _Pattern, _Height;
        struct Varying { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
        Varying Vertex(float4 position : POSITION)
        {
            Varying o; o.position = mul(_ClipFromLocal, mul(unity_ObjectToWorld, position)); o.uv = position.xy * .5 + .5; return o;
        }
        ENDHLSL
        Pass
        {
            Name "AUTHORED_FIVE_MRT_GEOMETRY"
            Cull Off ZTest LEqual ZWrite On
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Geometry
            struct GBuffer { float4 base : SV_Target0; float4 mos : SV_Target1; float4 normal : SV_Target2; float4 emission : SV_Target3; float4 gi : SV_Target4; };
            GBuffer Geometry(Varying i)
            {
                GBuffer o; o.base = _Base; o.mos = _Mos;
                o.normal = _Normal; o.emission = _Emission; o.gi = _Gi;
                if (_Pattern > .5)
                {
                    // Symmetric Y deliberately removes API viewport-origin ambiguity.
                    uint cell = ((uint)floor(i.position.x / 4) + (uint)floor(min(i.position.y, _Height - i.position.y) / 4)) % 4;
                    if (cell == 3) discard;
                    o.base = cell % 2 == 0 ? float4(1,0,0,1) : float4(0,1,0,1);
                }
                return o;
            }
            ENDHLSL
        }
        Pass
        {
            Name "CURRENT_PIXEL_MATERIAL_CONSUMER"
            Cull Off ZTest Always ZWrite Off Blend One One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Lighting
            FRAMEBUFFER_INPUT_FLOAT(0);
            FRAMEBUFFER_INPUT_FLOAT(1);
            FRAMEBUFFER_INPUT_FLOAT(2);
            FRAMEBUFFER_INPUT_FLOAT(3);
            float4 Lighting(Varying i) : SV_Target
            {
                float4 base = LOAD_FRAMEBUFFER_INPUT(0, i.position);
                float4 mos = LOAD_FRAMEBUFFER_INPUT(1, i.position);
                float4 normal = LOAD_FRAMEBUFFER_INPUT(2, i.position);
                float4 gi = LOAD_FRAMEBUFFER_INPUT(3, i.position);
                // A known channel-dependent equation for schedule validation, not PBR parity.
                return float4(base.rgb * (mos.g * gi.rgb) + normal.rgb * .25 + mos.rgb * .125, 0);
            }
            ENDHLSL
        }
        Pass
        {
            Name "REUSE_NORMAL_AND_GI_ATTACHMENTS"
            Cull Off ZTest Always ZWrite Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Reuse
            FRAMEBUFFER_INPUT_FLOAT(0);
            struct Output { float4 guide : SV_Target0; float4 color : SV_Target1; };
            Output Reuse(Varying i)
            {
                float3 value = LOAD_FRAMEBUFFER_INPUT(0, i.position).rgb;
                Output o; o.guide = float4(value.yz, .5, 7); o.color = float4(value.bgr * float3(1,.5,2), 1); return o;
            }
            ENDHLSL
        }
    }
}
