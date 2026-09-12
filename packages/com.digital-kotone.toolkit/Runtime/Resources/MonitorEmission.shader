Shader "GakumasPhotoMode/MonitorEmission"
{
    Properties
    {
        _MonitorTex ("Published linear HDR monitor", 2D) = "black" {}
        _MonitorUV ("Monitor scale/offset", Vector) = (1,1,0,0)
        _MonitorChannel ("Mesh UV channel 0-3", Float) = 0
        _MonitorTint ("Linear RGB tint", Vector) = (1,1,1,1)
        _MonitorIntensity ("Emission multiplier", Float) = 1
        _LedPattern ("LED pattern", 2D) = "white" {}
        _LedTiling ("LED repeats in monitor UV", Vector) = (1,1,0,0)
        _LedStrength ("LED strength", Range(0,1)) = 0
        _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Name "MONITOR_EMISSION"
            Cull [_Cull] ZWrite On ZTest LEqual Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MonitorTex, _LedPattern;
            float4 _MonitorUV, _MonitorTint, _LedTiling;
            float _MonitorChannel, _MonitorIntensity, _LedStrength;
            struct input { float4 vertex:POSITION; float2 uv:TEXCOORD0; float2 uv1:TEXCOORD1; float2 uv2:TEXCOORD2; float2 uv3:TEXCOORD3; };
            struct varying { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            varying vert(input v)
            {
                varying o; o.pos = UnityObjectToClipPos(v.vertex);
                float2 uv = _MonitorChannel < .5 ? v.uv : _MonitorChannel < 1.5 ? v.uv1 : _MonitorChannel < 2.5 ? v.uv2 : v.uv3;
                o.uv = uv * _MonitorUV.xy + _MonitorUV.zw; return o;
            }
            float4 frag(varying i):SV_Target
            {
                float3 radiance = tex2D(_MonitorTex, i.uv).rgb;
                float3 dots = tex2D(_LedPattern, frac(i.uv * _LedTiling.xy)).rgb;
                radiance *= _MonitorTint.rgb * _MonitorIntensity * lerp(1, saturate(dots), _LedStrength);
                // UI alpha was already composited by the monitor camera.
                // Multiplying it here would attenuate translucent UI twice.
                return float4(clamp(radiance, 0, 65504), 1);
            }
            ENDCG
        }
    }
}
