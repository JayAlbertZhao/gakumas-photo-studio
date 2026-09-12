Shader "GakumasPhotoMode/MonitorCanvas"
{
    Properties
    {
        [PerRendererData] _MainTex ("UI texture", 2D) = "white" {}
        _Radiance ("Linear HDR RGB multiplier", Vector) = (1,1,1,1)
        _StencilComp ("Stencil comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil operation", Float) = 0
        _StencilWriteMask ("Stencil write mask", Float) = 255
        _StencilReadMask ("Stencil read mask", Float) = 255
        _ColorMask ("Color mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use alpha clip", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "CanUseSpriteAtlas"="True" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            Name "HDR_CANVAS"
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST, _Radiance, _TextureSampleAdd, _ClipRect;
            struct input { float4 vertex:POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; };
            struct varying { float4 pos:SV_POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; float2 local:TEXCOORD1; };
            varying vert(input v)
            { varying o; o.pos = UnityObjectToClipPos(v.vertex); o.local = v.vertex.xy; o.color = v.color; o.uv = TRANSFORM_TEX(v.uv, _MainTex); return o; }
            float4 frag(varying i):SV_Target
            {
                float4 sample = (tex2D(_MainTex, i.uv) + _TextureSampleAdd) * i.color;
                sample.rgb = clamp(sample.rgb * _Radiance.rgb, 0, 65504);
                sample.a = saturate(sample.a);
                #ifdef UNITY_UI_CLIP_RECT
                sample.a *= UnityGet2DClipping(i.local, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(sample.a - .001);
                #endif
                return sample;
            }
            ENDCG
        }
    }
}
