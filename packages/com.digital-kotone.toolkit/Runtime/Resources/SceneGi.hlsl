// Explicit linear precomputed diffuse response. No material color, AO or emission here.
sampler2D _SceneGiLightmap, _SceneGiDirection;
float4 _SceneGiST, _SceneGiDecode;
float _SceneGiMode, _SceneGiDirectional;
float4 _SceneGiSHAr, _SceneGiSHAg, _SceneGiSHAb, _SceneGiSHBr, _SceneGiSHBg, _SceneGiSHBb, _SceneGiSHC;
float4 SceneGi(float2 uv2, float3 n)
{
    if (_SceneGiMode < .5) return 0;
    float3 response;
    if (_SceneGiMode < 1.5)
    {
        float2 uv = uv2 * _SceneGiST.xy + _SceneGiST.zw;
        float4 encoded = tex2D(_SceneGiLightmap, uv); response = encoded.rgb;
        if (_SceneGiDecode.x > .5 && _SceneGiDecode.x < 1.5)
            response *= _SceneGiDecode.y * pow(saturate(encoded.a), _SceneGiDecode.z);
        else if (_SceneGiDecode.x > 1.5) response *= _SceneGiDecode.y;
        if (_SceneGiDirectional > .5)
            response = DecodeDirectionalLightmap(response, tex2D(_SceneGiDirection, uv), n);
    }
    else
    {
        float4 normal = float4(n, 1), quadratic = n.xyzz * n.yzzx;
        response = float3(dot(_SceneGiSHAr, normal), dot(_SceneGiSHAg, normal), dot(_SceneGiSHAb, normal));
        response += float3(dot(_SceneGiSHBr, quadratic), dot(_SceneGiSHBg, quadratic), dot(_SceneGiSHBb, quadratic));
        response += _SceneGiSHC.rgb * (n.x * n.x - n.y * n.y);
    }
    return float4(clamp(response, 0, 65504), 1);
}
