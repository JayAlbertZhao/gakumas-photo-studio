Shader "Hidden/GakumasPhotoMode/SceneTemporalAntialiasing"
{
    Properties { _Cull ("Cull",Float)=2 }
    SubShader
    {
        CGINCLUDE
        #include "UnityCG.cginc"
        float4 _Size;
        float4x4 _View;
        sampler2D _MotionNormalIdentity;
        float3 Normal(float3 n){return n*rsqrt(max(dot(n,n),1e-12));}
        ENDCG
        Pass
        {
            Name "ACTUAL_VISIBLE_SCENE_MESH_NORMAL_DEPTH_ID_FLAGS"
            Cull [_Cull] ZTest LEqual ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            float4x4 _ViewProjection;
            sampler2D _AlphaMap;
            float4 _AlphaST;float3 _VertexScale;float _Alpha,_Cutoff,_Flags;
            struct Input {float4 vertex:POSITION;float3 normal:NORMAL;float2 uv:TEXCOORD0;};
            struct Vertex {float4 position:SV_POSITION;float3 normal:TEXCOORD0;float2 uv:TEXCOORD1;float depth:TEXCOORD2;};
            struct Guide {float4 geometry:SV_Target0;float4 metadata:SV_Target1;};
            Vertex vert(Input i)
            {
                Vertex o;i.vertex.xyz*=_VertexScale;float4 world=mul(unity_ObjectToWorld,i.vertex);
                o.position=mul(_ViewProjection,world);o.normal=UnityObjectToWorldNormal(i.normal/_VertexScale);o.depth=-mul(_View,world).z;o.uv=i.uv*_AlphaST.xy+_AlphaST.zw;return o;
            }
            Guide frag(Vertex i)
            {
                clip(tex2D(_AlphaMap,i.uv).a*_Alpha-_Cutoff);Guide o;
                o.geometry=float4(Normal(i.normal),i.depth);
                float id=tex2Dlod(_MotionNormalIdentity,float4(i.position.xy/_Size.zw,0,0)).a;
                o.metadata=float4(id,_Flags,0,1);return o;
            }
            ENDCG
        }
        Pass
        {
            Name "MOTION_AWARE_HDR_VARIANCE_TEMPORAL_RESOLVE"
            Cull Off ZTest Always ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _CurrentColor,_VisibleGeometry,_VisibleIdentityFlags,_Motion;
            sampler2D _HistoryColor,_HistoryGeometry,_HistoryMetadata,_ExternalFlags;
            float4x4 _InverseViewProjection,_HistoryInverseViewProjection,_HistoryView;
            float4 _Jitter,_History,_Rejection;float _ExternalFlagsEnabled;
            struct Screen {float4 position:SV_POSITION;};
            struct Result {float4 color:SV_Target0;float4 geometry:SV_Target1;float4 metadata:SV_Target2;};
            Screen vert(float4 vertex:POSITION){Screen o;o.position=float4(vertex.xy,0,1);return o;}
            float3 World(float2 uv,float depth,float4x4 inverseVp,float4x4 view)
            {
                float2 xy=uv*2-1;
                #if UNITY_UV_STARTS_AT_TOP
                xy.y=-xy.y;
                #endif
                float4 a=mul(inverseVp,float4(xy,0,1));a/=a.w;float4 b=mul(inverseVp,float4(xy,1,1));b/=b.w;
                float da=-mul(view,a).z,db=-mul(view,b).z;return lerp(a.xyz,b.xyz,(depth-da)/(db-da));
            }
            float Luma(float3 c){return dot(c,float3(.212673,.715152,.072175));}
            float3 Clean(float3 c){return clamp(c,0,65504);}
            float3 Compress(float3 c){return c/(1+Luma(c));}
            // Geometry and motion are point samples of the jittered raster. Their
            // depths belong to that sample's ray, not the continuous color UV.
            float2 GuideUv(float2 uv){return (clamp(floor(uv*_Size.zw),0,_Size.zw-1)+.5)*_Size.xy;}
            float4 PointColor(float2 pixel){return tex2Dlod(_CurrentColor,float4((clamp(pixel,0,_Size.zw-1)+.5)*_Size.xy,0,0));}
            float4 Current(float2 texel)
            {
                float2 first=floor(texel),f=texel-first;
                return lerp(lerp(PointColor(first),PointColor(first+float2(1,0)),f.x),lerp(PointColor(first+float2(0,1)),PointColor(first+1),f.x),f.y);
            }
            float Flags(float2 uv)
            {
                float own=tex2Dlod(_VisibleIdentityFlags,float4(uv,0,0)).g;
                float external=_ExternalFlagsEnabled>.5?floor(tex2Dlod(_ExternalFlags,float4(uv,0,0)).r*255+.5):0;
                return (float)(((uint)own|(uint)external)&6u);
            }
            Result frag(Screen input)
            {
                float2 uv=input.position.xy/_Size.zw;float flags=Flags(uv);bool noJitter=((uint)flags&4u)!=0;
                precise float2 currentPixel=floor(input.position.xy)-(noJitter?0:_Jitter.xy*_Size.zw);
                float2 currentUv=(currentPixel+.5)/_Size.zw;
                Result o;o.color=Current(currentPixel);o.geometry=0;o.metadata=0;
                if(any(currentUv<0)||any(currentUv>=1))return o;
                flags=(float)((uint)flags|(uint)Flags(currentUv));
                if(((uint)flags&4u)!=0){o.color=PointColor(floor(input.position.xy));return o;}
                float4 g=tex2Dlod(_VisibleGeometry,float4(currentUv,0,0));float id=tex2Dlod(_VisibleIdentityFlags,float4(currentUv,0,0)).r;
                if(g.a<=0||id<=0)return o;
                float3 normal=Normal(g.rgb);o.geometry=float4(normal,g.a);o.metadata=float4(id,1,flags,0);
                if(((uint)flags&2u)!=0)return o;
                float3 current=Clean(o.color.rgb);o.color.rgb=current;
                float4 motion=tex2Dlod(_Motion,float4(currentUv,0,0));float3 oldNormal=Normal(tex2Dlod(_MotionNormalIdentity,float4(currentUv,0,0)).rgb);
                precise float2 texel=currentPixel-motion.xy*_Size.zw+_Jitter.zw*_Size.zw;
                float2 oldUv=(texel+.5)/_Size.zw;
                if(_History.x<.5||motion.a<.5||motion.b<=0||any(oldUv<0)||any(oldUv>=1))return o;
                float3 expected=World(GuideUv(currentUv)-motion.xy,motion.b,_HistoryInverseViewProjection,_HistoryView);
                float2 first=floor(texel),f=texel-first;
                float3 oldColor=0;float age=0,total=0;
                [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++)
                {
                    float2 pixel=first+float2(x,y);if(any(pixel<0)||any(pixel>=_Size.zw))continue;
                    float2 tapUv=(pixel+.5)*_Size.xy;float4 meta=tex2Dlod(_HistoryMetadata,float4(tapUv,0,0));
                    float4 previous=tex2Dlod(_HistoryGeometry,float4(tapUv,0,0));float3 n=Normal(previous.rgb);
                    if(meta.r!=id||meta.g<1||((uint)meta.b&6u)!=0||previous.a<=0||dot(n,oldNormal)<_Rejection.y)continue;
                    float3 delta=World(GuideUv(tapUv-_Jitter.zw),previous.a,_HistoryInverseViewProjection,_HistoryView)-expected;
                    if(max(abs(dot(delta,n)),abs(dot(delta,oldNormal)))>_Rejection.x)continue;
                    float2 axis=lerp(1-f,f,float2(x,y));float weight=axis.x*axis.y;
                    oldColor+=Clean(tex2Dlod(_HistoryColor,float4(tapUv,0,0)).rgb)*weight;age+=meta.g*weight;total+=weight;
                }
                if(total<=1e-6)return o;oldColor/=total;age/=total;
                float3 change=abs(Compress(oldColor)-Compress(current));if(max(change.r,max(change.g,change.b))>_Rejection.z)return o;
                float3 lo=current,hi=current,sum=0,square=0;float count=0;
                float3 currentWorld=World(GuideUv(currentUv),g.a,_InverseViewProjection,_View);
                [unroll]for(int y=-1;y<=1;y++)[unroll]for(int x=-1;x<=1;x++)
                {
                    float2 tapUv=currentUv+float2(x,y)*_Size.xy;if(any(tapUv<0)||any(tapUv>=1))continue;
                    float4 guide=tex2Dlod(_VisibleGeometry,float4(tapUv,0,0));float otherId=tex2Dlod(_VisibleIdentityFlags,float4(tapUv,0,0)).r;float3 n=Normal(guide.rgb);
                    if(guide.a<=0||otherId!=id||dot(n,normal)<_Rejection.y||Flags(tapUv)>0)continue;
                    float3 delta=World(GuideUv(tapUv),guide.a,_InverseViewProjection,_View)-currentWorld;
                    if(max(abs(dot(delta,n)),abs(dot(delta,normal)))>_Rejection.x)continue;
                    float3 sample=Clean(Current(currentPixel+float2(x,y)).rgb);lo=min(lo,sample);hi=max(hi,sample);float3 d=sample-current;sum+=d;square+=d*d;count++;
                }
                float3 deltaMean=count>0?sum/count:0,mean=current+deltaMean;
                float3 sigma=count>0?sqrt(max(0,square/count-deltaMean*deltaMean))*_History.w:0;
                float3 lower=max(lo,mean-sigma),upper=min(hi,mean+sigma);
                float3 midpoint=(lower+upper)*.5,extent=max((upper-lower)*.5,.000061);
                float3 delta=oldColor-midpoint,ratio=abs(delta/extent);float largest=max(ratio.r,max(ratio.g,ratio.b));
                float3 clipped=largest>1?midpoint+delta/largest:oldColor;
                float weight=min(_History.y,age/(age+1))*saturate(total);
                // Algebraically equivalent luminance compression, without subtracting
                // two nearly equal numbers when the input HDR luminance is very high.
                clipped=Clean(clipped);float wc=(1-weight)/(1+Luma(current)),wh=weight/(1+Luma(clipped));
                o.color.rgb=Clean((current*wc+clipped*wh)/max(wc+wh,1e-20));o.metadata.g=1+min(age,_History.z-1)*saturate(total);o.metadata.a=weight;return o;
            }
            ENDCG
        }
    }
}
