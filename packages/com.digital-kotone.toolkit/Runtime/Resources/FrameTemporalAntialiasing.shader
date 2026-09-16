Shader "Hidden/GakumasPhotoMode/FrameTemporalAntialiasing"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            Name "HALF4_MOTION_HDR_VARIANCE_RESOLVE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "UnityCG.cginc"
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_TemporalCurrent);
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_TemporalOpaque);
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_TemporalMotion);
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_TemporalPreviousDepth);
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_TemporalHistory);
            UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_TemporalGuide);
            float4 _TemporalSize,_TemporalHistorySettings,_TemporalRejection,_TemporalJitter;
            float4 Vertex(float4 position:POSITION):SV_POSITION {return float4(position.xy,0,1);}
            int2 Bound(int2 p){return clamp(p,0,(int2)_TemporalSize.xy-1);}
            float4 Current(int2 p){return _TemporalCurrent.Load(int3(Bound(p),0));}
            uint Identity(float packed){return (uint)packed&~14u;}
            float Luma(float3 c){return dot(c,float3(.212673,.715152,.072175));}
            float3 Clean(float3 c){return clamp(c,0,65504);}
            struct Result {float4 color:SV_Target0;float4 metadata:SV_Target1;};
            Result Fragment(float4 pixel:SV_POSITION)
            {
                Result o;o.metadata=0;
                int2 stable=(int2)pixel.xy;
                float4 centreMotion=_TemporalMotion.Load(int3(stable,0));
                bool noJitter=((uint)centreMotion.a&4u)!=0;
                float2 raster=pixel.xy-.5-(noJitter?0:_TemporalJitter.xy*_TemporalSize.xy);
                int2 first=(int2)floor(raster);float2 f=frac(raster);
                o.color=lerp(lerp(Current(first),Current(first+int2(1,0)),f.x),lerp(Current(first+int2(0,1)),Current(first+1),f.x),f.y);
                if(any(raster<-.5)||any(raster>=_TemporalSize.xy-.5))return o;
                int2 p=Bound((int2)floor(raster+.5));float4 motion=_TemporalMotion.Load(int3(p,0));
                uint flags=(uint)motion.a|(uint)centreMotion.a;
                if((flags&4u)!=0){o.color=Current(stable);return o;}
                uint id=Identity(motion.a);if(id==0||motion.b<=0)return o;
                bool changedByFx=_TemporalRejection.z>.5&&any(Current(p)!=_TemporalOpaque.Load(int3(p,0)));
                o.metadata=float4(motion.b,id,1,0);
                if((flags&2u)!=0||changedByFx){o.metadata.z=0;return o;}
                float expected=_TemporalPreviousDepth.Load(int3(p,0)).r;
                if(_TemporalHistorySettings.x<.5||(flags&8u)==0||expected<=0)return o;
                float2 previous=raster-motion.xy*_TemporalSize.xy+_TemporalJitter.zw*_TemporalSize.xy;
                int2 start=(int2)floor(previous);float2 blend=frac(previous);
                float3 oldColor=0;float total=0,age=0;
                [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++)
                {
                    int2 tap=start+int2(x,y);if(any(tap<0)||any(tap>=(int2)_TemporalSize.xy))continue;
                    float4 meta=_TemporalGuide.Load(int3(tap,0));
                    // Half current depth has finite quantization; add its known
                    // relative storage bound, not an unbounded motion tolerance.
                    float tolerance=_TemporalRejection.x+abs(expected)*.000977;
                    if((uint)meta.g!=id||meta.b<1||abs(meta.r-expected)>tolerance)continue;
                    float2 axis=lerp(1-blend,blend,float2(x,y));float w=axis.x*axis.y;
                    oldColor+=Clean(_TemporalHistory.Load(int3(tap,0)).rgb)*w;age+=meta.b*w;total+=w;
                }
                if(total<=1e-6)return o;oldColor/=total;age/=total;
                float3 current=Clean(o.color.rgb),change=abs(oldColor/(1+Luma(oldColor))-current/(1+Luma(current)));
                if(max(change.r,max(change.g,change.b))>_TemporalRejection.y)return o;
                float3 lo=current,hi=current,sum=0,square=0;float count=0;
                [unroll]for(int ny=-1;ny<=1;ny++)[unroll]for(int nx=-1;nx<=1;nx++)
                {
                    int2 tap=p+int2(nx,ny);if(any(tap<0)||any(tap>=(int2)_TemporalSize.xy))continue;
                    float4 guide=_TemporalMotion.Load(int3(tap,0));
                    if(Identity(guide.a)!=id||((uint)guide.a&6u)!=0||abs(guide.b-motion.b)>_TemporalRejection.x+abs(motion.b)*.000977)continue;
                    if(_TemporalRejection.z>.5&&any(Current(tap)!=_TemporalOpaque.Load(int3(tap,0))))continue;
                    float3 sample=Clean(Current(tap).rgb),d=sample-current;
                    lo=min(lo,sample);hi=max(hi,sample);sum+=d;square+=d*d;count++;
                }
                float3 dm=count>0?sum/count:0,mean=current+dm;
                float3 sigma=count>0?sqrt(max(0,square/count-dm*dm))*_TemporalHistorySettings.w:0;
                // Current color must remain inside the clipping box: otherwise
                // an identical stationary history is pulled toward its neighbors
                // and a stable HDR highlight dims on every repeated render.
                float3 lower=min(current,max(lo,mean-sigma)),upper=max(current,min(hi,mean+sigma));
                float3 midpoint=(lower+upper)*.5,extent=max((upper-lower)*.5,.000061);
                float3 delta=oldColor-midpoint,ratio=abs(delta/extent);float largest=max(ratio.r,max(ratio.g,ratio.b));
                float3 clipped=Clean(largest>1?midpoint+delta/largest:oldColor);
                float weight=min(_TemporalHistorySettings.y,age/(age+1))*saturate(total);
                float wc=(1-weight)/(1+Luma(current)),wh=weight/(1+Luma(clipped));
                o.color.rgb=Clean(current+(clipped-current)*(wh/max(wc+wh,1e-20)));
                o.metadata.b=1+min(age,_TemporalHistorySettings.z-1)*saturate(total);o.metadata.a=weight;
                return o;
            }
            ENDCG
        }
    }
}
