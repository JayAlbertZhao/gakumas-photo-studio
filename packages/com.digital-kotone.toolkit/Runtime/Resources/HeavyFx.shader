Shader "Hidden/GakumasPhotoMode/HeavyFx"
{
    Properties { _MainTex("Linear HDR",2D)="white"{} _Cull("Cull",Float)=0 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #define HEAVY_FX_GRID
        #define HEAVY_FX_LIGHTING
        float3 HeavySurfaceLight(float3 color,float3 world,float2 pixel);
        #include "LowResolutionFxShared.hlsl"
        #define _MainTex _HeavyUnusedVolumeSource
        #define HEAVY_FX_VOLUME
        #include "VolumetricLightingShared.hlsl"
        #undef _MainTex
        // medium enabled, background extinction enabled, light bound, surface medium enabled
        float4 _HeavyMedium;
        bool HeavyRay(float2 pixel,float eye,out float3 start,out float3 direction,out float distance)
        {
            start=direction=0;distance=0;float3 end;
            if(!FogNear(pixel,start)||!FogUnproject(pixel,_FogViewParameters.z>.5?0:1,end))return false;
            float a=-mul(_FogWorldToView,float4(start,1)).z,b=-mul(_FogWorldToView,float4(end,1)).z;
            if(eye<a||b<=a)return false;
            end=lerp(start,end,saturate((eye-a)/(b-a))); direction=end-start;distance=length(direction);
            if(!FogFinite(distance)||distance<=1e-7)return false;
            direction/=distance;return true;
        }
        float HeavyTransmission(float3 start,float3 direction,float distance)
        {
            float enter,leave;
            return _HeavyMedium.y>.5&&VolumeBox(start,direction,distance,enter,leave)?exp(-_MediumParameters.x*(leave-enter)):1;
        }
        bool HeavyOpaqueRay(float2 pixel,out float3 start,out float3 direction,out float distance)
        {
            start=direction=0;distance=0;
            bool low=_FxInput.w==1;float2 fullPixel=low?pixel*_FogScreen.xy/_FxLowSize.xy:pixel;
            int2 full=low?(int2)(((2*(uint2)pixel+1)*(uint2)_FogScreen.xy)/(2*(uint2)_FxLowSize.xy)):(int2)pixel;
            full=clamp(full,0,(int2)_FogScreen.xy-1);
            if(_FxInput.w>1.5&&!FxNeedsRepair(fullPixel,false))return false;
            if(!low&&FxProtected(full))return false;
            float raw=_FxDepth.Load(int3(full,0));
            bool sky=_FxInput.x>.5?(_FogViewParameters.z>.5?raw==0:raw==1):raw==0;
            if(sky&&_VolumeInput.z<.5)return false;
            float eye=low?_FxDepthRange.Load(int3((int2)pixel,0)).x:FxDepthAt(full);
            return eye>=0&&HeavyRay(fullPixel,eye,start,direction,distance);
        }
        float4 HeavyOpacity(v2f_img input):SV_Target
        {
            float3 start,direction;float distance;
            if(!HeavyOpaqueRay(input.pos.xy,start,direction,distance))return 0;
            return float4(0,0,0,1-HeavyTransmission(start,direction,distance));
        }
        float4 HeavyScatter(v2f_img input):SV_Target
        {
            float3 start,direction;float distance;
            if(!HeavyOpaqueRay(input.pos.xy,start,direction,distance))return 0;
            return VolumeScatterRay(start,direction,distance);
        }
        float3 HeavySurfaceLight(float3 color,float3 world,float2 pixel)
        {
            if(_HeavyMedium.x<.5||_HeavyMedium.w<.5)return color;
            float3 start,direction;float distance;
            if(!HeavyRay(pixel,-mul(_FogWorldToView,float4(world,1)).z,start,direction,distance))return color;
            return color*HeavyTransmission(start,direction,distance)+
                (_FxSurface.x<.5&&_HeavyMedium.z>.5?VolumeScatterRay(start,direction,distance).rgb:0);
        }
        float4 HeavyAdditionalSurface(FxVarying input):SV_Target
        {
            if(_HeavyMedium.x<.5||_HeavyMedium.w<.5||_FxSurface.x>.5)return 0;
            float eye;float2 pixel;float4 value=FxSample(input,eye,pixel);float3 start,direction;float distance;
            if(!HeavyRay(pixel,eye,start,direction,distance))return 0;
            return float4(VolumeScatterRay(start,direction,distance).rgb*value.a,0);
        }
        float HeavyRepair(v2f_img input):SV_Target
        { return FxComputeNeedsRepair(input.pos.xy,_FxInput.w>.5)?1:0; }
        ENDCG
        Pass
        {
            Name "JOINT_OPAQUE_RANGE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ReduceDepth
            ENDCG
        }
        Pass
        {
            Name "JOINT_SHARED_SURFACE"
            Cull [_Cull] Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile __ SCENE_LIGHT_SHADOWS
            #pragma vertex SurfaceVertex
            #pragma fragment SurfaceColor
            ENDCG
        }
        Pass
        {
            Name "JOINT_SURFACE_REPLAY"
            Cull [_Cull] Blend One OneMinusSrcAlpha, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile __ SCENE_LIGHT_SHADOWS
            #pragma vertex SurfaceVertex
            #pragma fragment SurfaceColor
            ENDCG
        }
        Pass
        {
            Name "JOINT_DISTORTION_FIELD"
            Cull [_Cull]
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SurfaceVertex
            #pragma fragment DistortionField
            ENDCG
        }
        Pass
        {
            Name "JOINT_SINGLE_UPSCALE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ResolveColor
            ENDCG
        }
        Pass
        {
            Name "JOINT_DISTORTION_BARRIER"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ResolveDistortion
            ENDCG
        }
        Pass
        {
            Name "JOINT_DISTORTION_REPLAY"
            Cull [_Cull] Blend One OneMinusSrcAlpha, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SurfaceVertex
            #pragma fragment DistortionReplay
            ENDCG
        }
        Pass
        {
            Name "JOINT_SOURCE_COPY"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment CopyColor
            ENDCG
        }
        Pass
        {
            Name "JOINT_MEDIUM_OPACITY"
            Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment HeavyOpacity
            ENDCG
        }
        Pass
        {
            Name "JOINT_MEDIUM_OPACITY_REPLAY"
            Blend One OneMinusSrcAlpha, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment HeavyOpacity
            ENDCG
        }
        Pass
        {
            Name "JOINT_MEDIUM_SCATTER"
            Blend One One, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile __ SCENE_LIGHT_SHADOWS
            #pragma vertex vert_img
            #pragma fragment HeavyScatter
            ENDCG
        }
        Pass
        {
            Name "JOINT_SURFACE_ADDITIONAL_SCATTER"
            Cull [_Cull] Blend One One, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile __ SCENE_LIGHT_SHADOWS
            #pragma vertex SurfaceVertex
            #pragma fragment HeavyAdditionalSurface
            ENDCG
        }
        Pass
        {
            Name "JOINT_CURRENT_REPAIR_MASK"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment HeavyRepair
            ENDCG
        }
    }
}
