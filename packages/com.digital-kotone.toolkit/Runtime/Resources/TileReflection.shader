Shader "Hidden/GakumasPhotoMode/TileReflection"
{
    Properties { [HideInInspector] _MainTex ("Current actor-free HDR source", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        CGINCLUDE
        #include "UnityCG.cginc"
        Texture2D<float> _TileDepth;
        Texture2D<float4> _TileGeometry, _TileNormal, _TileBase, _TileMos, _TileReflection, _TileResponse, _TileRadiance;
        sampler2D _MainTex;
        samplerCUBE _TileProbe;
        float4 _TileSize, _TileOptions, _TileDistortion, _TileProbeOptions, _TileProbeDecode;
        float4x4 _TileInverseProjection, _TileInverseView, _TileView;
        int2 Pixel(float2 uv) { return min((int2)(uv*_TileSize.xy),(int2)_TileSize.xy-1); }
        float3 SafeNormal(float3 n) { return n*rsqrt(max(dot(n,n),1e-12)); }
        float Group(float identity) { return fmod(identity-1,256)+1; }
        float3 Position(float2 uv,float depth)
        {
            float2 xy=uv*2-1;
            #if UNITY_UV_STARTS_AT_TOP
            xy.y=-xy.y;
            #endif
            float4 a=mul(_TileInverseProjection,float4(xy,0,1));
            float4 b=mul(_TileInverseProjection,float4(xy,1,1)); a/=a.w; b/=b.w;
            return lerp(a.xyz,b.xyz,(-depth-a.z)/(b.z-a.z));
        }
        float3 View(float2 uv,int2 pixel)
        {
            float3 p=Position(uv,_TileDepth.Load(int3(pixel,0)));
            return SafeNormal(mul((float3x3)_TileInverseView,lerp(-p,float3(0,0,1),_TileOptions.z)));
        }
        ENDCG
        Pass
        {
            Name "TILE_ZERO_TO_FAR_DEPTH"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            float frag(v2f_img i):SV_Target
            { float depth=_TileDepth.Load(int3(Pixel(i.uv),0)); return depth>0?min(depth,_TileOptions.x):_TileOptions.x; }
            ENDCG
        }
        Pass
        {
            Name "TILE_POST_DECAL_RECEIVER_METADATA"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i):SV_Target
            {
                int2 p=Pixel(i.uv); float identity=_TileNormal.Load(int3(p,0)).a;
                if(identity<.5)return 0;
                float smoothness=_TileMos.Load(int3(p,0)).b;
                return float4(_TileGeometry.Load(int3(p,0)).a*step(_TileOptions.y,smoothness),smoothness,Group(identity),0);
            }
            ENDCG
        }
        Pass
        {
            Name "TILE_POST_DECAL_SPECULAR_RESPONSE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i):SV_Target
            {
                int2 p=Pixel(i.uv); float4 n=_TileNormal.Load(int3(p,0)); if(n.a<.5)return 0;
                float3 mos=_TileMos.Load(int3(p,0)).rgb;
                float3 f0=lerp(.04,_TileBase.Load(int3(p,0)).rgb,mos.r);
                float nv=saturate(dot(SafeNormal(n.rgb),View(i.uv,p)));
                return float4((f0+(1-f0)*pow(1-nv,5))*mos.b*mos.g*_TileOptions.w,1);
            }
            ENDCG
        }
        Pass
        {
            Name "TILE_PROBE_SSR_NORMAL_DIFFERENCE_RESOLVE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i):SV_Target
            {
                int2 p=Pixel(i.uv); float4 n=_TileNormal.Load(int3(p,0)); if(n.a<.5)return 0;
                float3 shading=SafeNormal(n.rgb), probe=0;
                if(_TileProbeOptions.x>.5)
                {
                    float roughness=1-_TileMos.Load(int3(p,0)).b;
                    float4 encoded=texCUBElod(_TileProbe,float4(reflect(-View(i.uv,p),shading),roughness*_TileProbeOptions.z));
                    probe=_TileProbeOptions.y>.5?DecodeHDR(encoded,_TileProbeDecode):encoded.rgb;
                }
                float3 geometric=SafeNormal(_TileGeometry.Load(int3(p,0)).rgb*2-1);
                float3 delta=mul((float3x3)_TileView,shading-geometric);
                float2 uv=i.uv+delta.xz*_TileDistortion.xy;
                float4 ssr=0;
                if(all(uv>=0)&&all(uv<1))
                {
                    int2 q=Pixel(uv); float other=_TileNormal.Load(int3(q,0)).a;
                    if(other>.5 && abs(Group(other)-Group(n.a))<.25)ssr=_TileReflection.Load(int3(q,0));
                }
                return float4(clamp(lerp(probe,ssr.rgb,saturate(ssr.a)),0,65504),1);
            }
            ENDCG
        }
        Pass
        {
            Name "TILE_NONRECURSIVE_REFLECTION_COMPOSITE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i):SV_Target
            {
                int3 p=int3(Pixel(i.uv),0); float4 color=tex2D(_MainTex,i.uv);
                color.rgb=clamp(color.rgb+_TileRadiance.Load(p).rgb*_TileResponse.Load(p).rgb,0,65504); return color;
            }
            ENDCG
        }
    }
}
