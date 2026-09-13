Shader "Hidden/PhotoStudio/FaceDecalProbe"
{
    Properties { _ProbeTint ("Literal tint", Vector) = (.17,.31,.47,.61) _ProbeScale ("Scale", Vector) = (1,1,1,0) _ProbeMask ("Mask",2D)="white"{} _ProbeCutoff ("Cutoff",Float)=0
        _ProbeStencilRef ("Stencil ref",Float)=0 _ProbeStencilWrite ("Stencil write mask",Float)=0 }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite On ZTest LEqual
            Stencil { Ref [_ProbeStencilRef] WriteMask [_ProbeStencilWrite] Comp Always Pass Replace }
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _ProbeTint,_ProbeScale;sampler2D _ProbeMask;float _ProbeCutoff;
            struct input {float4 vertex:POSITION;float2 uv:TEXCOORD0;};
            struct output {float4 position:SV_POSITION;float2 uv:TEXCOORD0;};
            output vert(input v){output o;v.vertex.xyz*=_ProbeScale.xyz;o.position=UnityObjectToClipPos(v.vertex);o.uv=v.uv;return o;}
            float4 frag(output i):SV_Target {clip(tex2D(_ProbeMask,i.uv).a-_ProbeCutoff);return _ProbeTint;}
            ENDCG
        }
    }
}
