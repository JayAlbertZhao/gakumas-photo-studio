// Independent motion attachment wrapper around the actual color/coverage pass.
// The default Actor passes never include this file. Clip snapshots contain the
// actual deformed/extruded vertex result, not a CPU bake or a guessed rigid pose.
#if defined(TOOLKIT_TEMPORAL_OUTLINE)
    #include "ActorOutline.cginc"
    #define TemporalInput OutlineInput
    #define TemporalSurface OutlineVaryings
    #define TemporalVertexFunction outlineVertex
    #define TemporalPosition position
#else
    #include "ActorSurface.cginc"
    #define TemporalInput appdata
    #define TemporalSurface v2f
    #define TemporalVertexFunction vert
    #define TemporalPosition pos
#endif

UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_ActorPreviousClip);
float4 _ActorClipSize, _ActorMotionSize;
float4x4 _ActorMotionInverseProjection, _ActorPreviousInverseProjection;
float _ActorMotionIdentity, _ActorMotionFlags, _ActorMotionHistory;

struct TemporalVarying
{
    TemporalSurface surface;
    float4 previousClip : TEXCOORD10;
    float4 currentClip : TEXCOORD11;
};
TemporalVarying TemporalVertex(TemporalInput input, uint id : SV_VertexID)
{
    TemporalVarying o;
    o.surface=TemporalVertexFunction(input);
    o.currentClip=o.surface.TemporalPosition;
    uint width=(uint)_ActorClipSize.x;
    o.previousClip=0;
    if(_ActorMotionHistory>.5)
        o.previousClip=_ActorPreviousClip.Load(int3(id%width,id/width,0));
    return o;
}
struct TemporalOutput
{
    float4 color : SV_Target0;
    float4 motionDepthIdentity : SV_Target1;
    // Supplementary expected previous eye depth enables deformation-aware
    // disocclusion rejection; it is not part of the reference Half4 layout.
    float previousDepth : SV_Target2;
};
TemporalOutput TemporalFragment(TemporalVarying input, float facing : VFACE)
{
    TemporalOutput o;
    #if defined(TOOLKIT_TEMPORAL_OUTLINE)
    o.color=outlineFragment(input.surface);
    #else
    o.color=frag(input.surface,facing);
    #endif
    float4 pixel=input.surface.TemporalPosition;
    float2 uv=pixel.xy/_ActorMotionSize.xy;
    float2 clipXY=uv*2-1;
    #if UNITY_UV_STARTS_AT_TOP
    clipXY.y=-clipXY.y;
    #endif
    float4 view=mul(_ActorMotionInverseProjection,float4(clipXY,pixel.z,1));
    float depth=max(0,-view.z/view.w);
    uint flags=(uint)_ActorMotionFlags&6u;
    float2 velocity=0;o.previousDepth=0;
    if(_ActorMotionHistory>.5&&input.previousClip.w>1e-6)
    {
        // Interpolate both clip positions with the same raster weights. Using
        // SV_Position for one side introduces raster interpolation error even
        // for an exactly stationary vertex stream.
        float2 delta=(input.currentClip.xy/input.currentClip.w-input.previousClip.xy/input.previousClip.w)*.5;
        #if UNITY_UV_STARTS_AT_TOP
        delta.y=-delta.y;
        #endif
        float4 previousView=mul(_ActorPreviousInverseProjection,input.previousClip);
        float oldDepth=-previousView.z/previousView.w;
        if(oldDepth>0&&oldDepth<=65504&&all(abs(delta)<65504))
        {velocity=delta;o.previousDepth=oldDepth;flags|=8u;}
    }
    // IDs occupy bits4..10, temporal flags bits1..2, valid-history bit3.
    // All values fit exact half integers. This is our ABI, not an original ID map.
    o.motionDepthIdentity=float4(velocity,min(depth,65504),((uint)_ActorMotionIdentity<<4)|flags|1u);
    return o;
}

struct ClipVertex { float4 clip : SV_POSITION; uint id : TEXCOORD0; };
struct ClipPixel { float4 position : SV_POSITION; float4 clip : TEXCOORD0; };
ClipVertex SnapshotVertex(TemporalInput input,uint id:SV_VertexID)
{
    ClipVertex o;o.clip=TemporalVertexFunction(input).TemporalPosition;o.id=id;return o;
}
[maxvertexcount(3)]
void SnapshotGeometry(triangle ClipVertex vertices[3],inout PointStream<ClipPixel> stream)
{
    [unroll]for(uint i=0;i<3;i++)
    {
        uint width=(uint)_ActorClipSize.x;
        float2 uv=(float2(vertices[i].id%width,vertices[i].id/width)+.5)/_ActorClipSize.xy;
        #if UNITY_UV_STARTS_AT_TOP
        uv.y=1-uv.y;
        #endif
        ClipPixel o;o.position=float4(uv*2-1,0,1);o.clip=vertices[i].clip;stream.Append(o);
    }
}
float4 SnapshotFragment(ClipPixel input):SV_Target { return input.clip; }
