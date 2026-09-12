Shader "Hidden/GakumasPhotoMode/SceneMotion"
{
    Properties { _Cull("Cull",Float)=2 }
    SubShader
    {
        Pass
        {
            Name "SCENE_PREVIOUS_GEOMETRY_CORRESPONDENCE"
            Cull [_Cull] ZWrite On ZTest LEqual Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            Texture2D<float4> _PreviousVertices;
            float4x4 _ViewProjection,_PreviousViewProjection,_PreviousView;
            float4 _UvST,_MotionSize,_VertexTextureSize;
            float3 _VertexScale;
            float _HistoryValid,_SurfaceIdentity,_Alpha,_Cutoff;
            sampler2D _AlphaMap;
            struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; uint id:SV_VertexID; };
            struct Varying { float4 position:SV_POSITION; float2 uv:TEXCOORD0; float4 previousWorld:TEXCOORD1; float3 previousNormal:TEXCOORD2; };
            float4 Previous(uint index)
            {
                uint width=(uint)_VertexTextureSize.z;
                return _PreviousVertices.Load(int3(index%width,index/width,0));
            }
            Varying vert(Input input)
            {
                Varying o;input.vertex.xyz*=_VertexScale;
                o.position=mul(_ViewProjection,mul(unity_ObjectToWorld,input.vertex));
                o.uv=input.uv*_UvST.xy+_UvST.zw;
                o.previousWorld=0;o.previousNormal=0;
                if(_HistoryValid>.5){o.previousWorld=Previous(input.id*2);o.previousNormal=Previous(input.id*2+1).xyz;}
                return o;
            }
            struct Result { float4 motion:SV_Target0; float4 normalIdentity:SV_Target1; };
            Result frag(Varying input)
            {
                clip(tex2D(_AlphaMap,input.uv).a*_Alpha-_Cutoff);
                Result o;o.motion=0;o.normalIdentity=float4(0,0,0,_SurfaceIdentity);
                float4 clipPosition=mul(_PreviousViewProjection,float4(input.previousWorld.xyz,1));
                float depth=-mul(_PreviousView,float4(input.previousWorld.xyz,1)).z;
                if(_HistoryValid>.5&&input.previousWorld.w>.9999&&clipPosition.w>1e-6&&depth>0)
                {
                    float2 previousUv=clipPosition.xy/clipPosition.w*.5+.5;
                    #if UNITY_UV_STARTS_AT_TOP
                    previousUv.y=1-previousUv.y;
                    #endif
                    float2 currentUv=input.position.xy*_MotionSize.xy;
                    o.motion=float4(currentUv-previousUv,depth,1);
                    o.normalIdentity.xyz=input.previousNormal*rsqrt(max(dot(input.previousNormal,input.previousNormal),1e-12));
                }
                return o;
            }
            ENDCG
        }
        Pass
        {
            Name "SCENE_ACTUAL_VERTEX_SNAPSHOT"
            Cull Off ZWrite Off ZTest Always Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #include "UnityCG.cginc"
            float3 _VertexScale;
            float4 _VertexTextureSize;
            struct Input { float4 vertex:POSITION; float3 normal:NORMAL; uint id:SV_VertexID; };
            struct Vertex { float4 world:POSITION; float3 normal:NORMAL; uint id:TEXCOORD0; };
            struct Pixel { float4 position:SV_POSITION; float4 value:TEXCOORD0; };
            Vertex vert(Input v)
            {
                Vertex o;v.vertex.xyz*=_VertexScale;o.world=mul(unity_ObjectToWorld,v.vertex);
                o.normal=UnityObjectToWorldNormal(v.normal/_VertexScale);o.id=v.id;return o;
            }
            [maxvertexcount(6)]
            void geom(triangle Vertex vertices[3],inout PointStream<Pixel> stream)
            {
                [unroll] for(uint i=0;i<3;i++)[unroll] for(uint k=0;k<2;k++)
                {
                    uint index=vertices[i].id*2+k,width=(uint)_VertexTextureSize.z;
                    float2 uv=(float2(index%width,index/width)+.5)*_VertexTextureSize.xy;
                    #if UNITY_UV_STARTS_AT_TOP
                    uv.y=1-uv.y;
                    #endif
                    Pixel o;o.position=float4(uv*2-1,0,1);
                    o.value=k==0?float4(vertices[i].world.xyz,1):float4(normalize(vertices[i].normal),0);
                    stream.Append(o);
                }
            }
            float4 frag(Pixel input):SV_Target { return input.value; }
            ENDCG
        }
    }
}
