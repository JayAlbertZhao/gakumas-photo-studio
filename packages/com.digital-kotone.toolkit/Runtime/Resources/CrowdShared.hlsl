struct CrowdVertex { float4 position,normal,tangent,uv; };
struct CrowdInstanceData { float4 positionScale,rotationType,tint; };
StructuredBuffer<CrowdVertex> _CrowdVertices;
StructuredBuffer<CrowdInstanceData> _CrowdInstances;
StructuredBuffer<uint> _CrowdIndices;
StructuredBuffer<float4> _CrowdBounds;
uint _CrowdBucket,_CrowdCapacity,_CrowdType;
float4 _CrowdAtlasSize; // total pixel width/height, tile side, prototype count
float3 _CrowdViewRight;
float3 CrowdRotate(float3 p,float2 yaw)
{return float3(yaw.y*p.x+yaw.x*p.z,p.y,-yaw.x*p.x+yaw.y*p.z);}
float3 CrowdFacing(float3 center)
{
    float3 direction=lerp(_CameraPosition-center,-_CameraForward,_Orthographic);
    return dot(direction,direction)>1e-10?normalize(direction):float3(0,0,-1);
}
float3 CrowdRight(float3 facing)
{
    float3 right=float3(-facing.z,0,facing.x);
    return dot(right,right)>1e-10?normalize(right):normalize(_CrowdViewRight);
}
uint CrowdDirection(float3 facing,float2 yaw)
{
    float3 local=CrowdRotate(facing,float2(-yaw.x,yaw.y));
    float angle=atan2(local.x,-local.z);return ((uint)(floor(angle/(UNITY_PI*.5)+.5)+4))&3u;
}
