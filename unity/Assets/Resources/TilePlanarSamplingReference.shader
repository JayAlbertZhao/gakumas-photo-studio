Shader "Hidden/PhotoStudio/TilePlanarSamplingReference"
{
    Properties
    {
        _ReferenceCapture ("Borrowed real capture",2D)="black" {}
        _ReferenceQuery ("CPU ray UV and material LOD",2D)="black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            Name "HARDWARE_FILTER_ONLY_CPU_QUERY_REFERENCE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _ReferenceCapture;
            Texture2D<float4> _ReferenceQuery;
            float4 _ReferenceSize;
            float4 frag(v2f_img i):SV_Target
            {
                int2 pixel=min((int2)(i.uv*_ReferenceSize.xy),(int2)_ReferenceSize.xy-1);
                float4 query=_ReferenceQuery.Load(int3(pixel,0));
                // Only the engine/GPU sampler is shared with production. No plane,
                // camera matrices, depth/group lookup, normalization or resolve code.
                return tex2Dlod(_ReferenceCapture,float4(query.xy,0,query.z));
            }
            ENDCG
        }
    }
}
