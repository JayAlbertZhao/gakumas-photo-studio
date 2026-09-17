Shader "Hidden/GakumasPhotoMode/OpaqueExposure"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            Name "UNMIXED_OPAQUE_SHUTTER_COLOR_AND_DEPTH"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "UnityCG.cginc"
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_OpaqueColor);
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_OpaqueMotion);
            Texture2D<float> _OpaqueDepth;
            Texture2D<float> _OpaquePreviousDepth;
            Texture2D<float> _OpaqueFlags;
            float4 _OpaqueSample; // width,height,phase,absolute depth tolerance
            float _OpaqueHasFlags,_OpaqueOrthographic;
            float4 Vertex(float4 p:POSITION):SV_POSITION{return float4(p.xy,0,1);}
            struct Result {float4 color:SV_Target0;float depth:SV_Target1;};
            bool Inside(int2 p){return all(p>=0)&&all(p<(int2)_OpaqueSample.xy);}
            float4 Data(int2 p)
            {
                if(!Inside(p))return 0;
                if(_OpaqueHasFlags>.5&&(((uint)round(_OpaqueFlags.Load(int3(p,0))*255))&4u)!=0)return 0;
                float4 m=_OpaqueMotion.Load(int3(p,0));float z=_OpaqueDepth.Load(int3(p,0)),oldZ=_OpaquePreviousDepth.Load(int3(p,0));
                if(!all(isfinite(m))||m.w!=1||m.z<=0||!isfinite(z)||!isfinite(oldZ)||z<=0||oldZ<=0)return 0;
                return float4(m.xy*_OpaqueSample.xy,z,oldZ);
            }
            float3 Offset(int2 p)
            {
                float4 d=Data(p);if(d.z<=0)return 0;
                float z=d.z,oldZ=d.w;float phaseDepth=z+(z-oldZ)*_OpaqueSample.z;
                if(!isfinite(phaseDepth)||phaseDepth<=.000001)return 0;
                // Project interpolated clip coordinates, not linearly interpolated
                // post-divide UVs: previous clip W is the previous view depth.
                float2 shift=d.xy*_OpaqueSample.z*(_OpaqueOrthographic>.5?1:oldZ/phaseDepth);
                if(!all(isfinite(shift)))return 0;return float3(shift,phaseDepth);
            }
            Result Fragment(float4 pixel:SV_POSITION)
            {
                int2 center=(int2)pixel.xy;Result original;
                original.color=_OpaqueColor.Load(int3(center,0));original.depth=_OpaqueDepth.Load(int3(center,0));
                if(_OpaqueSample.z==0)return original;
                float3 shifted=Offset(center);if(shifted.z<=0)return original;
                // Fixed-point inverse of the current-visible forward flow. It has
                // no hidden surface data; invalid/outside correspondences preserve
                // current input instead of inventing uncovered color or depth.
                float2 offset=-shifted.xy;
                [loop]for(int i=0;i<4;i++)
                {
                    int2 candidate=center+(int2)floor(offset+.5);
                    shifted=Offset(candidate);if(shifted.z<=0)return original;
                    offset=-shifted.xy;
                }
                int2 anchor=center+(int2)floor(offset+.5);float4 anchorData=Data(anchor);
                if(anchorData.z<=0)return original;
                int2 origin=center+(int2)floor(offset);float2 fraction=frac(offset);
                Result result;result.color=0;result.depth=0;
                [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++)
                {
                    int2 p=origin+int2(x,y);float weight=(x==0?1-fraction.x:fraction.x)*(y==0?1-fraction.y:fraction.y);
                    float4 data=Data(p);float sampleZ=data.z+(data.z-data.w)*_OpaqueSample.z;
                    bool valid=data.z>0&&isfinite(sampleZ)&&sampleZ>0&&abs(data.z-anchorData.z)<=_OpaqueSample.w&&length(data.xy-anchorData.xy)<=.5;
                    result.color+=(valid?_OpaqueColor.Load(int3(clamp(p,int2(0,0),(int2)_OpaqueSample.xy-1),0)):original.color)*weight;
                    result.depth+=(valid?sampleZ:original.depth)*weight;
                }
                result.color.a=original.color.a;
                return result;
            }
            ENDCG
        }
    }
}
