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
        Pass
        {
            Name "SCENE_JOINED_HALF4_MOTION"
            Cull [_Cull] ZWrite Off ZTest LEqual Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex VertexHalf4
            #pragma fragment FragmentHalf4
            #include "UnityCG.cginc"
            Texture2D<float4> _PreviousVertices;
            float4x4 _ViewProjection,_PreviousViewProjection,_PreviousView,_CurrentView;
            float4 _UvST,_VertexTextureSize;
            float3 _VertexScale;
            float _HistoryValid,_SurfaceIdentity,_Alpha,_Cutoff,_TemporalFlags;
            sampler2D _AlphaMap;
            struct InputHalf4 {float4 position:POSITION;float2 uv:TEXCOORD0;uint id:SV_VertexID;};
            struct VaryingHalf4 {float4 position:SV_POSITION;float2 uv:TEXCOORD0;float4 currentClip:TEXCOORD1;float4 previousClip:TEXCOORD2;float2 depths:TEXCOORD3;};
            VaryingHalf4 VertexHalf4(InputHalf4 v)
            {
                VaryingHalf4 o;v.position.xyz*=_VertexScale;
                float4 world=mul(unity_ObjectToWorld,v.position);
                o.position=mul(_ViewProjection,world);o.currentClip=o.position;
                o.uv=v.uv*_UvST.xy+_UvST.zw;o.previousClip=0;o.depths=float2(-mul(_CurrentView,world).z,0);
                if(_HistoryValid>.5)
                {
                    uint index=v.id*2,width=(uint)_VertexTextureSize.z;
                    float4 old=_PreviousVertices.Load(int3(index%width,index/width,0));
                    o.previousClip=mul(_PreviousViewProjection,old);o.depths.y=-mul(_PreviousView,old).z;
                }
                return o;
            }
            struct ResultHalf4 {float4 motion:SV_Target0;float expectedDepth:SV_Target1;};
            ResultHalf4 FragmentHalf4(VaryingHalf4 i)
            {
                clip(tex2D(_AlphaMap,i.uv).a*_Alpha-_Cutoff);
                ResultHalf4 o;o.expectedDepth=0;float2 velocity=0;uint flags=(uint)_TemporalFlags&6u;
                if(_HistoryValid>.5&&i.previousClip.w>1e-6&&i.depths.y>0&&i.depths.y<=65504)
                {
                    velocity=(i.currentClip.xy/i.currentClip.w-i.previousClip.xy/i.previousClip.w)*.5;
                    #if UNITY_UV_STARTS_AT_TOP
                    velocity.y=-velocity.y;
                    #endif
                    o.expectedDepth=i.depths.y;flags|=8u;
                }
                // Scene IDs have bit0 clear; Actor IDs use bit0 set. Both namespaces
                // retain127IDs and exact Half integer storage, without collisions.
                o.motion=float4(velocity,min(max(i.depths.x,0),65504),((uint)_SurfaceIdentity<<4)|flags);
                return o;
            }
            ENDCG
        }
    }
}
