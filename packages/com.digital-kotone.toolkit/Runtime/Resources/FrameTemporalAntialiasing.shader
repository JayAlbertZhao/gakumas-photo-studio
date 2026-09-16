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
            #pragma multi_compile_local _ TOOLKIT_TAA_COHERENT_FOOTPRINT
            #pragma multi_compile_local _ TOOLKIT_TAA_SURFACE_COVERAGE
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
            #if defined(TOOLKIT_TAA_SURFACE_COVERAGE)
            float4 CubicWeights(float t)
            {
                float t2=t*t,t3=t2*t;
                return float4(-.5*t+t2-.5*t3,1-2.5*t2+1.5*t3,.5*t+2*t2-1.5*t3,-.5*t2+.5*t3);
            }
            #endif
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
                #if defined(TOOLKIT_TAA_SURFACE_COVERAGE)
                // Transport the reconstructed edge with its nearest visible
                // tracked surface, including pixels whose nearest texel is sky.
                // The color remains the complete bilinear coverage, not the
                // selected surface's point color.
                float anchorDepth=Identity(motion.a)!=0&&motion.b>0?motion.b:1e30;
                [unroll]for(int ay=0;ay<2;ay++)[unroll]for(int ax=0;ax<2;ax++)
                {
                    float2 axis=lerp(1-f,f,float2(ax,ay));int2 q=Bound(first+int2(ax,ay));
                    float4 g=_TemporalMotion.Load(int3(q,0));
                    if(axis.x*axis.y>1e-6&&Identity(g.a)!=0&&((uint)g.a&6u)==0&&g.b>0&&g.b<anchorDepth)
                    {p=q;motion=g;anchorDepth=g.b;}
                }
                #endif
                uint flags=(uint)motion.a|(uint)centreMotion.a;
                if((flags&4u)!=0){o.color=Current(stable);return o;}
                uint id=Identity(motion.a);
                #if !defined(TOOLKIT_TAA_SURFACE_COVERAGE)
                if(id==0||motion.b<=0)return o;
                #endif
                bool changedByFx=_TemporalRejection.z>.5&&any(Current(p)!=_TemporalOpaque.Load(int3(p,0)));
                o.metadata=float4(motion.b,id,1,0);
                if((flags&2u)!=0||changedByFx){o.metadata.z=0;return o;}
                #if defined(TOOLKIT_TAA_SURFACE_COVERAGE)
                bool currentEligible=true;
                [unroll]for(int ey=0;ey<2;ey++)[unroll]for(int ex=0;ex<2;ex++)
                {
                    float2 axis=lerp(1-f,f,float2(ex,ey));int2 q=Bound(first+int2(ex,ey));
                    float4 g=_TemporalMotion.Load(int3(q,0));
                    bool excluded=((uint)g.a&6u)!=0||(_TemporalRejection.z>.5&&any(Current(q)!=_TemporalOpaque.Load(int3(q,0))));
                    currentEligible=currentEligible&&!(axis.x*axis.y>1e-6&&excluded);
                }
                if(!currentEligible){o.metadata.z=0;return o;}
                // Untracked background still passes through. Its validity age
                // distinguishes usable coverage from previous FX/flag rejection.
                if(id==0||motion.b<=0)return o;
                #endif
                #if defined(TOOLKIT_TAA_COHERENT_FOOTPRINT)
                // The current color was bilinearly reconstructed before its
                // nearest motion identity was selected. Do not label a color
                // mixing separate surfaces as reusable single-surface history.
                bool coherent=true;
                [unroll]for(int cy=0;cy<2;cy++)[unroll]for(int cx=0;cx<2;cx++)
                {
                    float2 axis=lerp(1-f,f,float2(cx,cy));
                    int2 tap=Bound(first+int2(cx,cy));float4 guide=_TemporalMotion.Load(int3(tap,0));
                    bool mixed=Identity(guide.a)!=id||((uint)guide.a&6u)!=0||abs(guide.b-motion.b)>_TemporalRejection.x+abs(motion.b)*.000977||
                        (_TemporalRejection.z>.5&&any(Current(tap)!=_TemporalOpaque.Load(int3(tap,0))));
                    coherent=coherent&&!(axis.x*axis.y>1e-6&&mixed);
                }
                if(!coherent){o.metadata.z=0;return o;}
                #endif
                float expected=_TemporalPreviousDepth.Load(int3(p,0)).r;
                if(_TemporalHistorySettings.x<.5||(flags&8u)==0||expected<=0)return o;
                float2 previous=raster-motion.xy*_TemporalSize.xy+_TemporalJitter.zw*_TemporalSize.xy;
                int2 start=(int2)floor(previous);float2 blend=frac(previous);
                float3 oldColor=0;float total=0,age=0;
                #if defined(TOOLKIT_TAA_SURFACE_COVERAGE)
                bool wholeFootprint=true;
                float3 historyLo=65504,historyHi=0;
                #endif
                [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++)
                {
                    int2 tap=start+int2(x,y);
                    #if defined(TOOLKIT_TAA_SURFACE_COVERAGE)
                    float2 axis=lerp(1-blend,blend,float2(x,y));float w=axis.x*axis.y;
                    if(w<=1e-6)continue;
                    if(any(tap<0)||any(tap>=(int2)_TemporalSize.xy)){wholeFootprint=false;continue;}
                    #else
                    if(any(tap<0)||any(tap>=(int2)_TemporalSize.xy))continue;
                    #endif
                    float4 meta=_TemporalGuide.Load(int3(tap,0));
                    // Half current depth has finite quantization; add its known
                    // relative storage bound, not an unbounded motion tolerance.
                    float tolerance=_TemporalRejection.x+abs(expected)*.000977;
                    #if defined(TOOLKIT_TAA_SURFACE_COVERAGE)
                    // A single-surface guide anchors motion, not color coverage.
                    // Preserve every bilinear history tap, including background,
                    // only when the current footprint supports that surface.
                    bool supported=false;
                    [unroll]for(int sy=0;sy<2;sy++)[unroll]for(int sx=0;sx<2;sx++)
                    {
                        float2 supportAxis=lerp(1-f,f,float2(sx,sy));
                        int2 q=Bound(first+int2(sx,sy));float4 g=_TemporalMotion.Load(int3(q,0));
                        uint surface=Identity(g.a);float depth=_TemporalPreviousDepth.Load(int3(q,0)).r;
                        bool excluded=((uint)g.a&6u)!=0||(_TemporalRejection.z>.5&&any(Current(q)!=_TemporalOpaque.Load(int3(q,0))));
                        bool compatible=surface==0?meta.g==0&&meta.b>=1:((uint)g.a&8u)!=0&&meta.b>=1&&depth>0&&
                            abs(meta.r-depth)<=_TemporalRejection.x+abs(depth)*.000977&&
                            all(abs((g.xy-motion.xy)*_TemporalSize.xy)<=1);
                        supported=supported||(supportAxis.x*supportAxis.y>1e-6&&!excluded&&(uint)meta.g==surface&&compatible);
                    }
                    if(!supported){wholeFootprint=false;continue;}
                    float3 tapColor=Clean(_TemporalHistory.Load(int3(tap,0)).rgb);
                    historyLo=min(historyLo,tapColor);historyHi=max(historyHi,tapColor);
                    #else
                    if((uint)meta.g!=id||meta.b<1||abs(meta.r-expected)>tolerance)continue;
                    float2 axis=lerp(1-blend,blend,float2(x,y));float w=axis.x*axis.y;
                    #endif
                    oldColor+=Clean(_TemporalHistory.Load(int3(tap,0)).rgb)*w;age+=meta.b*w;total+=w;
                }
                #if defined(TOOLKIT_TAA_SURFACE_COVERAGE)
                if(!wholeFootprint)return o;
                #endif
                if(total<=1e-6)return o;oldColor/=total;age/=total;
                #if defined(TOOLKIT_TAA_SURFACE_COVERAGE)
                // Repeated bilinear history resampling broadens moving edges.
                // A cubic estimate restores subpixel detail, but its negative
                // lobes may not introduce colors beyond the validated inner
                // footprint; spatial variance/reactive clipping still follows.
                float4 wx=CubicWeights(blend.x),wy=CubicWeights(blend.y);float3 cubic=0;
                [unroll]for(int hy=0;hy<4;hy++)[unroll]for(int hx=0;hx<4;hx++)
                    cubic+=Clean(_TemporalHistory.Load(int3(Bound(start+int2(hx-1,hy-1)),0)).rgb)*(wx[hx]*wy[hy]);
                oldColor=clamp(cubic,historyLo,historyHi);
                #endif
                float3 current=Clean(o.color.rgb),change=abs(oldColor/(1+Luma(oldColor))-current/(1+Luma(current)));
                if(max(change.r,max(change.g,change.b))>_TemporalRejection.y)return o;
                float3 lo=current,hi=current,sum=0,square=0;float count=0;
                [unroll]for(int ny=-1;ny<=1;ny++)[unroll]for(int nx=-1;nx<=1;nx++)
                {
                    int2 tap=p+int2(nx,ny);if(any(tap<0)||any(tap>=(int2)_TemporalSize.xy))continue;
                    float4 guide=_TemporalMotion.Load(int3(tap,0));
                    #if defined(TOOLKIT_TAA_SURFACE_COVERAGE)
                    if(((uint)guide.a&6u)!=0)continue;
                    #else
                    if(Identity(guide.a)!=id||((uint)guide.a&6u)!=0||abs(guide.b-motion.b)>_TemporalRejection.x+abs(motion.b)*.000977)continue;
                    #endif
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
                #if defined(TOOLKIT_TAA_SURFACE_COVERAGE)
                // Fractional reprojection repeatedly filters the same history.
                // Discount its age by the concentration of the validated inner
                // footprint instead of treating filtered detail as fresh samples.
                // Integer transport retains full age; half-pixel transport in
                // both axes retains one quarter. This is a detail-confidence
                // proxy, not a claim of independent statistical sample count.
                float2 concentration=blend*blend+(1-blend)*(1-blend);
                age*=concentration.x*concentration.y;
                #endif
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
