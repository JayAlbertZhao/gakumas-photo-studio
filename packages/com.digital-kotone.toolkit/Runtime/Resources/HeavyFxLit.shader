Shader "Hidden/GakumasPhotoMode/HeavyFxLit"
{
    Properties { _Cull("Cull",Float)=0 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #define HEAVY_FX_GRID
        #define FX_HAS_MEDIUM
        float3 FxLitMedium(float3 color,float3 world,float2 pixel,float alpha);
        #include "FxForwardSurface.hlsl"
        // The medium's scalar shadow is not the surface's main/local light atlas.
        #if defined(FX_MEDIUM_SHADOWS)
        #define SCENE_LIGHT_SHADOWS
        #endif
        #define SceneShadowData FxMediumShadowData
        #define ScenePointFace FxMediumPointFace
        #define ScenePointDirection FxMediumPointDirection
        #define ScenePointTap FxMediumPointTap
        #define ScenePointVisibility FxMediumPointVisibility
        #define SceneLightVisibility FxMediumLightVisibility
        #define _LightShadowAtlas _FxMediumShadowAtlas
        #define _SingleShadowMatrix _FxMediumShadowMatrix
        #define _SingleShadowST _FxMediumShadowST
        #define _SingleShadowDepth _FxMediumShadowDepth
        #define _SingleShadowOptions _FxMediumShadowOptions
        #define _MainTex _FxUnusedVolumeSource
        #define HEAVY_FX_VOLUME
        #include "VolumetricLightingShared.hlsl"
        #undef _MainTex
        float4 _HeavyMedium;
        bool FxLitRay(float2 pixel,float eye,out float3 start,out float3 direction,out float distance)
        {
            start=direction=0; distance=0; float3 end;
            if(!FogNear(pixel,start)||!FogUnproject(pixel,_FogViewParameters.z>.5?0:1,end))return false;
            float a=-mul(_FogWorldToView,float4(start,1)).z,b=-mul(_FogWorldToView,float4(end,1)).z;
            if(eye<a||b<=a)return false;
            end=lerp(start,end,saturate((eye-a)/(b-a)));direction=end-start;distance=length(direction);
            if(!FogFinite(distance)||distance<=1e-7)return false;
            direction/=distance;return true;
        }
        float3 FxLitMedium(float3 color,float3 world,float2 pixel,float alpha)
        {
            if(_HeavyMedium.x<.5||_HeavyMedium.w<.5)return color;
            float3 start,direction;float distance;
            if(!FxLitRay(pixel,-mul(_FogWorldToView,float4(world,1)).z,start,direction,distance))return color;
            float enter,leave;
            float transmission=_HeavyMedium.y>.5&&VolumeBox(start,direction,distance,enter,leave)?exp(-_MediumParameters.x*(leave-enter)):1;
            return color*transmission+(_FxSurface.x<.5&&_HeavyMedium.z>.5?VolumeScatterRay(start,direction,distance).rgb*alpha:0);
        }
        float4 FxLitAdditionalMedium(FxLitVarying input) : SV_Target
        {
            if(_HeavyMedium.x<.5||_HeavyMedium.w<.5||_FxSurface.x>.5)return 0;
            float eye;float2 pixel;float4 value=FxSample(FxLitUnlitInput(input),eye,pixel);float alpha=FxLitMaterialAlpha(input);
            float3 start,direction;float distance;
            if(!FxLitRay(pixel,eye,start,direction,distance))return 0;
            return float4(VolumeScatterRay(start,direction,distance).rgb*(value.a*alpha),0);
        }
        ENDCG
        Pass
        {
            Name "JOINT_LIT_WORKING_GEOMETRY"
            Cull [_Cull] Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local __ FX_LIT_LOCAL_SHADOWS
            #pragma multi_compile_local __ FX_LIT_MAIN_SHADOWS
            #pragma multi_compile_local __ FX_MEDIUM_SHADOWS
            #pragma vertex FxLitVertexProgram
            #pragma fragment FxLitSurfaceColor
            ENDCG
        }
        Pass
        {
            Name "JOINT_LIT_REPLAY"
            Cull [_Cull] Blend One OneMinusSrcAlpha, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local __ FX_LIT_LOCAL_SHADOWS
            #pragma multi_compile_local __ FX_LIT_MAIN_SHADOWS
            #pragma multi_compile_local __ FX_MEDIUM_SHADOWS
            #pragma vertex FxLitVertexProgram
            #pragma fragment FxLitSurfaceColor
            ENDCG
        }
        Pass
        {
            Name "JOINT_LIT_ADDITIONAL_MEDIUM"
            Cull [_Cull] Blend One One, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local __ FX_LIT_LOCAL_SHADOWS
            #pragma multi_compile_local __ FX_LIT_MAIN_SHADOWS
            #pragma multi_compile_local __ FX_MEDIUM_SHADOWS
            #pragma vertex FxLitVertexProgram
            #pragma fragment FxLitAdditionalMedium
            ENDCG
        }
    }
}
