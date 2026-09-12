Shader "Hidden/GakumasPhotoMode/SceneDepthData"
{
    Properties
    {
        _Cull ("Cull", Float) = 2
        [HideInInspector] _MainTex ("Depth reduction input", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            Name "SCENE_GEOMETRY"
            Cull [_Cull] ZWrite On ZTest LEqual Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4x4 _SceneViewProjection, _SceneView;
            float3 _SceneVertexScale;
            sampler2D _SceneAlphaMask, _SceneSmoothnessMap;
            float4 _SceneAlphaMaskST, _SceneSmoothnessMapST;
            float _SceneAlphaCutoff, _SceneSmoothness, _SceneSmoothnessThreshold, _SceneReceiveReflections;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f {
                float4 pos : SV_POSITION; float3 normal : TEXCOORD0;
                float2 uv : TEXCOORD1; float depth : TEXCOORD2;
            };
            v2f vert(appdata v)
            {
                v2f o;
                v.vertex.xyz *= _SceneVertexScale;
                float4 world = mul(unity_ObjectToWorld, v.vertex);
                o.pos = mul(_SceneViewProjection, world);
                o.depth = -mul(_SceneView, world).z;
                o.normal = UnityObjectToWorldNormal(v.normal / _SceneVertexScale);
                o.uv = v.uv;
                return o;
            }
            struct output { float4 normalMask : SV_Target0; float depth : SV_Target1; };
            output frag(v2f i)
            {
                clip(tex2D(_SceneAlphaMask, i.uv * _SceneAlphaMaskST.xy + _SceneAlphaMaskST.zw).a - _SceneAlphaCutoff);
                float smoothness = _SceneSmoothness * tex2D(_SceneSmoothnessMap,
                    i.uv * _SceneSmoothnessMapST.xy + _SceneSmoothnessMapST.zw).r;
                output o;
                // Smooth mesh normals only: no normal map and no ActorData/TAA IDs.
                o.normalMask = float4(normalize(i.normal) * .5 + .5,
                    _SceneReceiveReflections * step(_SceneSmoothnessThreshold, smoothness));
                o.depth = i.depth;
                return o;
            }
            ENDCG
        }
        Pass
        {
            Name "MIN_DEPTH"
            Cull Off ZWrite Off ZTest Always Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float frag(v2f_img i) : SV_Target
            {
                float2 size = _MainTex_TexelSize.zw;
                float2 outputSize = ceil(size * .5);
                float2 first = floor(i.uv * outputSize) * 2;
                float result = 3.402823466e+38;
                [unroll] for (int y = 0; y < 2; y++)
                [unroll] for (int x = 0; x < 2; x++)
                {
                    float2 pixel = min(first + float2(x, y), size - 1);
                    result = min(result, tex2Dlod(_MainTex, float4((pixel + .5) / size, 0, 0)).r);
                }
                return result;
            }
            ENDCG
        }
    }
}
