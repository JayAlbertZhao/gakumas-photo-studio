Shader "Hidden/GakumasPhotoMode/MotionBlur"
{
    Properties { _MainTex("Current HDR",2D)="black"{} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        CGINCLUDE
        #include "UnityCG.cginc"
        Texture2D<float4> _MainTex,_MotionDepth,_TileMaximum,_NeighborhoodMaximum;
        Texture2D<float> _NoJitterFlags;
        float4 _Size,_TileSize,_MotionMapping,_Filter,_Noise;
        bool Inside(int2 pixel){return all(pixel>=0)&&all(pixel<(int2)_Size.xy);}
        bool Protected(int2 pixel)
        {
            return _Noise.y>.5 && (((uint)round(_NoJitterFlags.Load(int3(pixel,0))*255))&4)!=0;
        }
        float4 Motion(int2 pixel)
        {
            if(!Inside(pixel)||Protected(pixel))return 0;
            int2 gp=(int2)floor(pixel+.5+_MotionMapping.zw*_Size.xy);
            if(!Inside(gp)||Protected(gp))return 0;
            float4 sample=_MotionDepth.Load(int3(gp,0));
            if(sample.w!=1 || !(sample.z>0) || any((asuint(sample)&0x7fffffffu)>=0x7f800000u))return 0;
            float2 v=(sample.xy+_MotionMapping.xy)*_Size.xy*_Filter.x;
            float magnitude=length(v);if((asuint(magnitude)&0x7fffffffu)>=0x7f800000u)return 0;
            v*=min(1,_TileSize.z/max(magnitude,1e-12));
            // Raster subpixel correspondence noise has no meaningful shutter direction.
            // Treat sub-half-pixel exposure as stationary, while still receiving other moving samples.
            if(magnitude<.5)v=0;
            return float4(v,sample.z,1);
        }
        float4 Tile(v2f_img input):SV_Target
        {
            int2 start=(int2)input.pos.xy*(int)_TileSize.z;float2 best=0;float square=0;
            [loop]for(int y=0;y<(int)_TileSize.z;y++)[loop]for(int x=0;x<(int)_TileSize.z;x++)
            {
                float2 v=Motion(start+int2(x,y)).xy;float q=dot(v,v);
                if(q>square){best=v;square=q;}
            }
            return float4(best,sqrt(square),1);
        }
        float4 Neighborhood(v2f_img input):SV_Target
        {
            int2 center=(int2)input.pos.xy;float2 best=0;float square=0;
            [unroll]for(int y=-1;y<=1;y++)[unroll]for(int x=-1;x<=1;x++)
            {
                int2 p=center+int2(x,y);if(any(p<0)||any(p>=(int2)_TileSize.xy))continue;
                float2 v=_TileMaximum.Load(int3(p,0)).xy;float q=dot(v,v);
                if(q>square){best=v;square=q;}
            }
            return float4(best,sqrt(square),1);
        }
        float Cone(float distance,float radius){return radius>.00001?saturate(1-distance/radius):0;}
        float Cylinder(float distance,float radius){return radius>.00001?1-smoothstep(radius*.95,radius*1.05,distance):0;}
        float2 Direction(float2 v){return v/max(length(v),1e-12);}
        float Noise(int2 pixel)
        {
            uint seed=(uint)_Noise.z|((uint)_Noise.w<<16);
            uint h=(uint)pixel.x*1664525u+(uint)pixel.y*1013904223u+seed;
            h^=h>>16;h*=2246822519u;h^=h>>13;
            return (h&65535u)/65536.0;
        }
        // Independent tile/local-direction reconstruction informed by McGuire12 and Guertin13.
        // No previous color, hidden-surface reconstruction, or claim of the lecture's unpublished filter.
        float4 Reconstruct(v2f_img input):SV_Target
        {
            int2 center=(int2)input.pos.xy;float4 color=_MainTex.Load(int3(center,0));float4 local=Motion(center);
            if(local.w!=1)return color;
            float2 largest=_NeighborhoodMaximum.Load(int3(center/(int)_TileSize.z,0)).xy;
            float radius=length(largest),localRadius=length(local.xy);if(radius<.5)return color;
            float2 main=Direction(largest),secondary=localRadius>=.5?Direction(local.xy):float2(-main.y,main.x);
            int directions=_TileSize.w>.5?2:1,count=(int)_Filter.z/directions;
            float total=(float)_Filter.z/(_Filter.w*max(localRadius,.5));float3 sum=color.rgb*total;
            float jitter=(Noise(center)-.5)*_Noise.x;
            [loop]for(int direction=0;direction<directions;direction++)[loop]for(int i=0;i<count;i++)
            {
                float2 axis=direction==0?main:secondary;
                float location=((i+.5+jitter)/count*2-1)*radius;
                int2 candidate=(int2)floor(center+.5+axis*location);if(!Inside(candidate))continue;
                float4 other=Motion(candidate);if(other.w!=1)continue;
                float2 offset=candidate-center;float distance=length(offset),otherRadius=length(other.xy);
                float a=abs(dot(Direction(local.xy),axis)),b=abs(dot(Direction(other.xy),axis));
                float closer=saturate(1-(other.z-local.z)/_Filter.y),farther=saturate(1-(local.z-other.z)/_Filter.y);
                float weight=closer*Cone(distance,otherRadius)*b+farther*Cone(distance,localRadius)*a+
                    2*Cylinder(distance,min(localRadius,otherRadius))*max(a,b);
                sum+=_MainTex.Load(int3(candidate,0)).rgb*weight;total+=weight;
            }
            return float4(sum/total,color.a);
        }
        ENDCG
        Pass
        {
            Name "TILE_MAXIMUM"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Tile
            ENDCG
        }
        Pass
        {
            Name "NEIGHBORHOOD_MAXIMUM"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Neighborhood
            ENDCG
        }
        Pass
        {
            Name "CURRENT_HDR_MOTION_RECONSTRUCTION"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Reconstruct
            ENDCG
        }
    }
}
