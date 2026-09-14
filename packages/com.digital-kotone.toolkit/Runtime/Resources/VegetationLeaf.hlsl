#ifndef TOOLKIT_VEGETATION_LEAF_INCLUDED
#define TOOLKIT_VEGETATION_LEAF_INCLUDED
sampler2D _LeafThicknessMap;
float4 _LeafTintStrength, _LeafAbsorptionThickness;
float _LeafEnabled, _LeafTwoSided;
float3 LeafTransmission(float2 uv)
{
    float thickness = saturate(tex2D(_LeafThicknessMap, uv).r) * _LeafAbsorptionThickness.w;
    return _LeafTintStrength.rgb * _LeafTintStrength.w * exp(-_LeafAbsorptionThickness.rgb * thickness);
}
float LeafFacing(float3 authoredNormal, float3 view)
{
    return _LeafTwoSided > .5 && dot(authoredNormal, view) < 0 ? -1 : 1;
}
// Specular is retained by the caller. Tau divides the diffuse response between
// the visible and opposite hemisphere. Existing artistic backlight applies to
// the reflected portion; it does not amplify transmitted energy a second time.
float3 LeafDiffuse(float3 diffuse, float3 fresnel, float3 f0, float nl, float back,
    float diffuseScale, float backlight, float3 tau)
{
    return diffuse * diffuseScale * ((1-fresnel)*(1-tau)*nl + (1-f0)*(backlight*(1-tau)+tau)*back);
}
float3 LeafShadowNormal(float3 normal, float3 lightDirection)
{
    return dot(normal, lightDirection) < 0 ? -normal : normal;
}
#endif
