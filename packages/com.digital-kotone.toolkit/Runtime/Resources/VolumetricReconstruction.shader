Shader "Hidden/GakumasPhotoMode/VolumetricReconstruction"
{
    Properties { _MainTex("Linear HDR",2D)="white"{} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "VolumetricLightingShared.hlsl"
        Texture2D<float2> _VolumeDepthRange;
        Texture2D<float4> _VolumeLowScattering;
        Texture2D<float> _VolumeReintegration;
        float4 _VolumeLowSize,_VolumeReconstructionTolerance;

        float ReconstructionDepth(float2 pixel)
        {
            float3 start,direction;float rayLength;
            if(!VolumeRay(pixel,start,direction,rayLength))return -1;
            float value=_VolumeDepth.Load(int3(int2(pixel),0));
            if(_VolumeInput.x<.5)return value==0?_FogViewParameters.y:min(value,_FogViewParameters.y);
            return min(-mul(_FogWorldToView,float4(start+direction*rayLength,1)).z,_FogViewParameters.y);
        }
        float2 ReduceRange(v2f_img input):SV_Target
        {
            int2 pixel=int2(input.pos.xy),lo=(int2)floor(pixel*_FogScreen.xy/_VolumeLowSize.xy);
            int2 hi=min((int2)ceil((pixel+1)*_FogScreen.xy/_VolumeLowSize.xy),(int2)_FogScreen.xy);
            float nearest=_FogViewParameters.y,furthest=0;
            [loop]for(int y=lo.y;y<hi.y;y++)[loop]for(int x=lo.x;x<hi.x;x++)
            {
                float z=ReconstructionDepth(float2(x,y)+.5);if(z<0)return float2(-1,-1);
                nearest=min(nearest,z);furthest=max(furthest,z);
            }
            return float2(nearest,furthest);
        }
        void LowFootprint(float2 pixel,out int2 basePixel,out float2 fraction)
        {
            // Integer texel-center arithmetic preserves exactly aligned zero weights.
            // A reciprocal/FMA rounding residue must not activate another guide tap.
            int2 denominator=2*(int2)_FogScreen.xy;
            int2 numerator=(2*(int2)pixel+1)*(int2)_VolumeLowSize.xy-(int2)_FogScreen.xy;
            basePixel=numerator/denominator;int2 remainder=numerator-basePixel*denominator;
            int2 negative=int2(remainder.x<0?1:0,remainder.y<0?1:0);
            basePixel-=negative;remainder+=negative*denominator;
            fraction=(float2)remainder/(float2)denominator;
        }
        int2 ClampLow(int2 pixel){return clamp(pixel,int2(0,0),(int2)_VolumeLowSize.xy-1);}
        float ReintegrationMask(v2f_img input):SV_Target
        {
            float z=ReconstructionDepth(input.pos.xy);if(z<0||_MediumParameters.x<=0)return 0;
            float tolerance=_VolumeReconstructionTolerance.x+z*_VolumeReconstructionTolerance.y;
            int2 p;float2 f;LowFootprint(input.pos.xy,p,f);
            // Outside the low sample-center hull, Clamp would extrapolate a different
            // ray without any neighbor contrast in the missing direction. Reintegrate.
            float2 coordinate=p+f;
            if(any(coordinate<0)||any(coordinate>_VolumeLowSize.xy-1))return 1;
            float3 smallest=1e30,largest=-1e30;
            [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++)
            {
                float weight=(x==0?1-f.x:f.x)*(y==0?1-f.y:f.y);if(weight<=0)continue;
                int2 tap=ClampLow(p+int2(x,y));float2 range=_VolumeDepthRange.Load(int3(tap,0));
                if(range.x<0||range.y-range.x>tolerance||abs(z-range.x)>tolerance||abs(z-range.y)>tolerance)return 1;
                float3 value=_VolumeLowScattering.Load(int3(tap,0)).rgb;if(!FogFinite3(value))return 1;
                smallest=min(smallest,value);largest=max(largest,value);
            }
            return any(largest-smallest>_VolumeReconstructionTolerance.z+largest*_VolumeReconstructionTolerance.w)?1:0;
        }
        float4 ReconstructScatter(v2f_img input):SV_Target
        {
            if(_VolumeReintegration.Load(int3(int2(input.pos.xy),0))>.5||ReconstructionDepth(input.pos.xy)<0)return 0;
            int2 p;float2 f;LowFootprint(input.pos.xy,p,f);float3 sum=0;
            [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++)
                sum+=_VolumeLowScattering.Load(int3(ClampLow(p+int2(x,y)),0)).rgb*(x==0?1-f.x:f.x)*(y==0?1-f.y:f.y);
            return float4(sum,0);
        }
        float4 Reintegrate(v2f_img input):SV_Target
        {
            // This branch precedes the expensive medium/light/visibility integration.
            if(_VolumeReintegration.Load(int3(int2(input.pos.xy),0))<.5)return 0;
            return Scatter(input);
        }
        ENDCG
        Pass
        {
            Name "VOLUME_CURRENT_OPAQUE_RANGE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ReduceRange
            ENDCG
        }
        Pass
        {
            Name "VOLUME_REINTEGRATION_MASK"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ReintegrationMask
            ENDCG
        }
        Pass
        {
            Name "VOLUME_RECONSTRUCT_SCATTERING"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ReconstructScatter
            ENDCG
        }
        Pass
        {
            Name "VOLUME_DEPTH_RADIANCE_EDGE_REINTEGRATION"
            Blend One One, Zero One
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Reintegrate
            #pragma multi_compile_local _ SCENE_LIGHT_SHADOWS
            ENDCG
        }
    }
}
