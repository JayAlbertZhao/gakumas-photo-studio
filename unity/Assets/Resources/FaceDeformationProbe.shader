Shader "Hidden/GakumasPhotoMode/FaceDeformationProbe"
{
    SubShader
    {
        Pass
        {
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex:POSITION; float3 normal:NORMAL; float4 tangent:TANGENT; float2 uv:TEXCOORD0; };
            struct Output { float4 position:SV_POSITION; float3 color:TEXCOORD0; };
            Output vert(Input v)
            {
                Output o; o.position=UnityObjectToClipPos(v.vertex);
                o.color=.35+v.normal*.11+v.tangent.xyz*.07+float3(v.uv*.17,v.tangent.w*.06);return o;
            }
            float4 frag(Output i):SV_Target{return float4(i.color,1);}
            ENDHLSL
        }
    }
}
