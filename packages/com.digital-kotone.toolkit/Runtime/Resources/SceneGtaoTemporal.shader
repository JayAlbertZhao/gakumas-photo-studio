Shader "Hidden/GakumasPhotoMode/SceneGtaoTemporal"
{
    SubShader
    {
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _ScreenGeometry;
        float4x4 _ScreenInverseViewProjection,_ScreenView;
        struct Screen { float4 position:SV_POSITION; float2 uv:TEXCOORD0; };
        Screen vert(float4 vertex:POSITION)
        {
            Screen o;o.position=float4(vertex.xy,0,1);o.uv=vertex.xy*.5+.5;
            #if UNITY_UV_STARTS_AT_TOP
            o.uv.y=1-o.uv.y;
            #endif
            return o;
        }
        float3 Position(float2 uv,float depth,float4x4 inverseVp,float4x4 view)
        {
            float2 xy=uv*2-1;
            #if UNITY_UV_STARTS_AT_TOP
            xy.y=-xy.y;
            #endif
            float4 a=mul(inverseVp,float4(xy,0,1));a/=a.w;
            float4 b=mul(inverseVp,float4(xy,1,1));b/=b.w;
            float da=-mul(view,a).z,db=-mul(view,b).z;
            return lerp(a.xyz,b.xyz,(depth-da)/(db-da));
        }
        float3 World(float2 uv,float depth){return Position(uv,depth,_ScreenInverseViewProjection,_ScreenView);}
        float3 Normal(float3 n){return n*rsqrt(max(dot(n,n),1e-12));}
        ENDCG
        Pass
        {
            Name "GTAO_CURRENT_TEMPORAL_SAMPLE"
            Cull Off ZTest Always ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local __ SCENE_GTAO_HALF
            #pragma multi_compile_local __ SCENE_GTAO_ROTATED
            #include "SceneGtao.hlsl"
            #if defined(SCENE_GTAO_HALF)
            #include "SceneGtaoReconstruction.hlsl"
            #endif
            float4 frag(Screen input):SV_Target
            {
                float4 geometry=tex2D(_ScreenGeometry,input.uv);if(geometry.a<=0)return float4(1,0,0,0);
                float3 world=World(input.uv,geometry.a),normal=Normal(geometry.xyz);
                #if defined(SCENE_GTAO_HALF)
                float ao=GtaoReconstruct(input.uv,world,normal,geometry.a);
                #else
                float ao=GtaoVisibility(input.uv,world,normal,geometry.a);
                #endif
                return float4(ao,0,0,0);
            }
            ENDCG
        }
        Pass
        {
            Name "GTAO_REPROJECT_REJECT_CLIP_ACCUMULATE"
            Cull Off ZTest Always ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _CurrentAo,_Motion,_MotionNormalIdentity,_HistoryAo,_HistoryNormalIdentity;
            float4x4 _HistoryInverseViewProjection,_HistoryView;
            float4 _TemporalSize,_TemporalHistory,_TemporalRejection,_TemporalClamp;
            struct Result { float4 aoDepthAgeWeight:SV_Target0; float4 normalIdentity:SV_Target1; };
            Result frag(Screen input)
            {
                // Pixel-centre coordinates avoid interpolated fullscreen UV error
                // accumulating into fractional history age at silhouette boundaries.
                input.uv=input.position.xy/_TemporalSize.zw;
                Result o;o.aoDepthAgeWeight=float4(1,0,0,0);o.normalIdentity=0;
                float4 geometry=tex2D(_ScreenGeometry,input.uv);if(geometry.a<=0)return o;
                float current=tex2D(_CurrentAo,input.uv).r;
                float4 mapping=tex2D(_MotionNormalIdentity,input.uv);
                float3 normal=Normal(geometry.xyz);
                o.aoDepthAgeWeight=float4(current,geometry.a,1,0);o.normalIdentity=float4(normal,mapping.a);
                float4 motion=tex2D(_Motion,input.uv);float2 oldUv=input.uv-motion.xy;
                if(_TemporalHistory.x<.5||motion.a<.5||mapping.a<=0||motion.b<=0||any(oldUv<0)||any(oldUv>=1))return o;
                float3 expected=Position(oldUv,motion.b,_HistoryInverseViewProjection,_HistoryView);
                float3 expectedNormal=Normal(mapping.xyz);
                precise float2 texel=floor(input.position.xy)-motion.xy*_TemporalSize.zw;
                float2 first=floor(texel),fraction=texel-first;
                float oldAo=0,oldAge=0,total=0;
                [unroll] for(int y=0;y<2;y++)[unroll] for(int x=0;x<2;x++)
                {
                    float2 pixel=first+float2(x,y);
                    if(any(pixel<0)||any(pixel>=_TemporalSize.zw))continue;
                    float2 uv=(pixel+.5)*_TemporalSize.xy;
                    float4 history=tex2Dlod(_HistoryAo,float4(uv,0,0));
                    float4 data=tex2Dlod(_HistoryNormalIdentity,float4(uv,0,0));
                    float3 n=Normal(data.xyz);
                    if(history.g<=0||history.b<1||data.a!=mapping.a||dot(n,expectedNormal)<_TemporalRejection.y)continue;
                    float3 delta=Position(uv,history.g,_HistoryInverseViewProjection,_HistoryView)-expected;
                    if(max(abs(dot(delta,n)),abs(dot(delta,expectedNormal)))>_TemporalRejection.x)continue;
                    float2 weight=lerp(1-fraction,fraction,float2(x,y));float w=weight.x*weight.y;
                    oldAo+=history.r*w;oldAge+=history.b*w;total+=w;
                }
                if(total<=1e-6)return o;
                oldAo/=total;oldAge/=total;
                // Receiver motion alone cannot detect a moving occluder's shadow.
                // Reject strong visibility changes before neighborhood clipping.
                if(abs(oldAo-current)>_TemporalRejection.z)return o;
                float lo=current,hi=current,sum=0,square=0,count=0;
                float3 world=World(input.uv,geometry.a);float2 pixel0=floor(input.uv*_TemporalSize.zw);
                [unroll] for(int y=-1;y<=1;y++)[unroll] for(int x=-1;x<=1;x++)
                {
                    float2 pixel=pixel0+float2(x,y);
                    if(any(pixel<0)||any(pixel>=_TemporalSize.zw))continue;
                    float2 uv=(pixel+.5)*_TemporalSize.xy;float4 g=tex2Dlod(_ScreenGeometry,float4(uv,0,0));
                    float id=tex2Dlod(_MotionNormalIdentity,float4(uv,0,0)).a;float3 n=Normal(g.xyz);
                    if(g.a<=0||id!=mapping.a||dot(n,normal)<_TemporalRejection.y)continue;
                    float3 delta=World(uv,g.a)-world;
                    if(max(abs(dot(delta,n)),abs(dot(delta,normal)))>_TemporalRejection.x)continue;
                    float a=tex2Dlod(_CurrentAo,float4(uv,0,0)).r;lo=min(lo,a);hi=max(hi,a);
                    // Centred moments avoid catastrophic cancellation near white AO.
                    float d=a-current;sum+=d;square+=d*d;count++;
                }
                float deltaMean=count>0?sum/count:0,mean=current+deltaMean;
                float sigma=count>0?sqrt(max(0,square/count-deltaMean*deltaMean)):0;
                float padding=_TemporalClamp.y;
                lo=min(current,max(lo-padding,mean-_TemporalClamp.x*sigma-padding));
                hi=max(current,min(hi+padding,mean+_TemporalClamp.x*sigma+padding));
                lo=max(lo,current-_TemporalRejection.w);hi=min(hi,current+_TemporalRejection.w);
                float history=clamp(oldAo,lo,hi);
                float weight=min(_TemporalHistory.y,oldAge/(oldAge+1))*saturate(total);
                o.aoDepthAgeWeight.r=lerp(current,history,weight);
                o.aoDepthAgeWeight.b=1+min(oldAge,_TemporalHistory.z-1)*saturate(total);
                o.aoDepthAgeWeight.a=weight;return o;
            }
            ENDCG
        }
    }
}
