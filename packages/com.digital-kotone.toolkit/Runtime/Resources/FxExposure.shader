Shader "Hidden/GakumasPhotoMode/FxExposure"
{
    Properties { _MainTex("Current sample",2D)="black"{} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_MainTex);
        UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_FxExposureSource);
        Texture2D<float> _FxExposureProtection;
        float _FxExposureHasProtection;
        float _FxExposureWeight;
        float4 _VertexTextureSize;
        struct SnapshotInput {float4 vertex:POSITION;uint id:SV_VertexID;};
        struct SnapshotVertex {float4 world:POSITION;uint id:TEXCOORD0;};
        struct SnapshotPixel {float4 position:SV_POSITION;float4 world:TEXCOORD0;};
        SnapshotVertex Snapshot(SnapshotInput v){SnapshotVertex o;o.world=mul(unity_ObjectToWorld,v.vertex);o.id=v.id;return o;}
        [maxvertexcount(3)]
        void SnapshotPoints(triangle SnapshotVertex vertices[3],inout PointStream<SnapshotPixel> stream)
        {
            [unroll]for(uint i=0;i<3;i++)
            {
                uint index=vertices[i].id,width=(uint)_VertexTextureSize.z;
                float2 uv=(float2(index%width,index/width)+.5)*_VertexTextureSize.xy;
                #if UNITY_UV_STARTS_AT_TOP
                uv.y=1-uv.y;
                #endif
                SnapshotPixel o;o.position=float4(uv*2-1,0,1);o.world=float4(vertices[i].world.xyz,1);stream.Append(o);
            }
        }
        float4 SnapshotColor(SnapshotPixel i):SV_Target {return i.world;}
        float4 Accumulate(v2f_img i):SV_Target {return _MainTex.Load(int3((int2)i.pos.xy,0))*_FxExposureWeight;}
        float4 Resolve(v2f_img i):SV_Target
        {
            int3 p=int3((int2)i.pos.xy,0);
            // Do not round protected HDR pixels through an accumulation, even
            // when every shutter sample individually preserved them exactly.
            if(_FxExposureHasProtection>.5)
            {
                float protection=_FxExposureProtection.Load(p);
                if(!isfinite(protection)||protection>0)return _FxExposureSource.Load(p);
            }
            return float4(_MainTex.Load(p).rgb,_FxExposureSource.Load(p).a);
        }
        ENDCG
        Pass
        {
            Name "COHERENT_FX_SHUTTER_ACCUMULATION"
            Blend One One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Accumulate
            ENDCG
        }
        Pass
        {
            Name "CURRENT_ALPHA_FX_SHUTTER_RESOLVE"
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Resolve
            ENDCG
        }
        Pass
        {
            Name "ACTUAL_FX_VERTEX_ENDPOINT"
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Snapshot
            #pragma require geometry
            #pragma geometry SnapshotPoints
            #pragma fragment SnapshotColor
            ENDCG
        }
    }
}
