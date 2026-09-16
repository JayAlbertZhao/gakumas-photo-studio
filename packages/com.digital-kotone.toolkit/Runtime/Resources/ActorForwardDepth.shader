Shader "Hidden/GakumasPhotoMode/ActorForwardDepth"
{
    SubShader
    {
        Cull Off ZTest Always Blend Off
        CGINCLUDE
        #include "UnityCG.cginc"
        UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_ActorSourceColor);
        UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_ActorSourceHardwareDepth);
        // Explicit precision survives Unity's Vulkan HLSLcc depth-view lowering.
        // A plain Texture2D<float> produced RelaxedPrecision on the fetched depth.
        UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_ActorHardwareDepth);
        float4 _ActorTargetSize;
        float4x4 _ActorInverseProjection;
        struct Varying { float4 position:SV_POSITION;float2 uv:TEXCOORD0; };
        Varying Vertex(appdata_img i)
        {
            Varying o;o.position=float4(i.vertex.xy,0,1);o.uv=i.texcoord;
            #if UNITY_UV_STARTS_AT_TOP
            o.position.y=-o.position.y;
            #endif
            return o;
        }
        int2 Pixel(float2 uv) { return min((int2)(uv*_ActorTargetSize.xy),(int2)_ActorTargetSize.xy-1); }
        float2 ClipXY(float2 uv)
        {
            float2 xy=uv*2-1;
            #if UNITY_UV_STARTS_AT_TOP
            xy.y=-xy.y;
            #endif
            return xy;
        }
        ENDCG
        Pass
        {
            Name "SEED_SCENE_COLOR_AND_HARDWARE_DEPTH"
            ZWrite On
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Seed
            struct Output { float4 color:SV_Target;float depth:SV_Depth; };
            Output Seed(Varying i)
            {
                Output o;int2 p=Pixel(i.uv);o.color=_ActorSourceColor.Load(int3(p,0));
                // Preserve raster Z exactly, including coplanar LEqual. An eye-depth
                // unprojection/reprojection roundtrip loses ULPs and changes coverage.
                // Stencil remains the explicit zero clear of the new Actor target.
                o.depth=_ActorSourceHardwareDepth.Load(int3(p,0)).r;
                return o;
            }
            ENDCG
        }
        Pass
        {
            Name "EXPORT_CURRENT_SCENE_AND_ACTOR_EYE_DEPTH"
            ZWrite Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment EyeDepth
            float EyeDepth(Varying i):SV_Target
            {
                float z=_ActorHardwareDepth.Load(int3(Pixel(i.uv),0)).r;
                #if defined(UNITY_REVERSED_Z)
                if(z<=0)return 0;
                #else
                if(z>=1)return 0;
                #endif
                float4 view=mul(_ActorInverseProjection,float4(ClipXY(i.uv),z,1));return max(0,-view.z/view.w);
            }
            ENDCG
        }
        Pass
        {
            Name "COPY_REFLECTED_COLOR_WITHOUT_DEPTH_FEEDBACK"
            ZWrite Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment CopyColor
            float4 CopyColor(Varying i):SV_Target { return _ActorSourceColor.Load(int3(Pixel(i.uv),0)); }
            ENDCG
        }
        Pass
        {
            Name "RESET_SCENE_STENCIL_PRESERVE_RASTER_DEPTH"
            ZWrite Off ColorMask 0
            Stencil { Ref 0 Comp Always Pass Replace WriteMask 255 }
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment ClearStencil
            float4 ClearStencil(Varying i):SV_Target { return 0; }
            ENDCG
        }
    }
}
