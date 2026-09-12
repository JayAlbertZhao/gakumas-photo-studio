Shader "GakumasPhotoMode/PlanarCapture"
{
    Properties
    {
        _BaseMap ("Base RGBA", 2D) = "white" {}
        _Color ("Base color", Color) = (1,1,1,1)
        [HDR] _Emission ("Emission", Color) = (0,0,0,0)
        _AmbientColor ("Ambient", Color) = (1,1,1,1)
        _LightColor ("Single light", Color) = (0,0,0,0)
        _LightDirection ("World surface-to-light direction", Vector) = (0,1,0,0)
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0
        _Cull ("Cull", Float) = 2
        _VertexScale ("Vertex scale", Vector) = (1,1,1,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Name "SINGLE_LIGHT_CAPTURE"
            Cull [_Cull] ZWrite On ZTest LEqual Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _BaseMap;
            float4 _BaseMap_ST, _Color, _Emission, _AmbientColor, _LightColor;
            float3 _LightDirection, _VertexScale;
            float _Cutoff;
            struct input { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct varying { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 normal : TEXCOORD1; };
            varying vert(input v)
            {
                varying o; v.vertex.xyz *= _VertexScale;
                o.pos = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.normal = UnityObjectToWorldNormal(v.normal / _VertexScale); return o;
            }
            float4 frag(varying i) : SV_Target
            {
                float4 color = tex2D(_BaseMap, i.uv) * _Color;
                clip(color.a - _Cutoff);
                float3 light = _LightDirection * rsqrt(max(dot(_LightDirection, _LightDirection), 1e-10));
                float diffuse = saturate(dot(normalize(i.normal), light));
                return float4(color.rgb * (_AmbientColor.rgb + _LightColor.rgb * diffuse) + _Emission.rgb, color.a);
            }
            ENDCG
        }
    }
}
