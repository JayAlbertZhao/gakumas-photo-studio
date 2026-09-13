Shader "Hidden/GakumasPhotoMode/LowResolutionFx"
{
    Properties { _MainTex("Linear HDR",2D)="white"{} _Cull("Cull",Float)=0 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        #include "FogVolume.hlsl"
        Texture2D<float> _FxDepth, _FxProtection;
        Texture2D<float2> _FxDepthRange;
        Texture2D<float4> _MainTex, _FxEffect, _FxBackground, _FxTexture;
        float4x4 _FxViewProjection;
        float4 _FxInput, _FxLowSize, _FxTolerance, _FxTextureST, _FxRadiance, _FxSurface, _FxDistortion, _FxFlags;

        float FxDepthAt(int2 pixel)
        {
            float value=_FxDepth.Load(int3(pixel,0));
            if(!FogFinite(value) || value<0) return -1;
            if(_FxInput.x>.5)
            {
                if(value>1) return -1;
                if(_FogViewParameters.z>.5?value==0:value==1) return _FogViewParameters.y;
                float3 world;
                if(!FogUnproject(pixel+.5,_FogViewParameters.z>.5?value:lerp(_FogViewParameters.w,1,value),world)) return -1;
                value=-mul(_FogWorldToView,float4(world,1)).z;
            }
            else if(value==0) return _FogViewParameters.y;
            return FogFinite(value) && value>=_FogViewParameters.x?min(value,_FogViewParameters.y):-1;
        }
        bool FxProtected(int2 pixel)
        {
            if(_FxInput.y<.5) return false;
            float p=_FxProtection.Load(int3(pixel,0)); return !FogFinite(p) || p>0;
        }
        float2 ReduceDepth(v2f_img input):SV_Target
        {
            int2 pixel=int2(input.pos.xy);
            int2 lo=(int2)floor(pixel*_FogScreen.xy/_FxLowSize.xy);
            int2 hi=min((int2)ceil((pixel+1)*_FogScreen.xy/_FxLowSize.xy),(int2)_FogScreen.xy);
            float nearest=_FogViewParameters.y,furthest=0;
            [loop] for(int y=lo.y;y<hi.y;y++) [loop] for(int x=lo.x;x<hi.x;x++)
            {
                float z=FxDepthAt(int2(x,y));
                if(z<0) return float2(-1,-1);
                nearest=min(nearest,z); furthest=max(furthest,z);
            }
            return float2(nearest,furthest);
        }
        void FxFootprint(float2 pixel,out int2 basePixel,out float2 fraction)
        { float2 p=pixel*_FxLowSize.xy*_FogScreen.zw-.5; basePixel=(int2)floor(p); fraction=p-basePixel; }
        int2 FxClampLow(int2 pixel) { return clamp(pixel,int2(0,0),(int2)_FxLowSize.xy-1); }
        float4 FxEffectAt(int2 pixel) { return _FxEffect.Load(int3(FxClampLow(pixel),0)); }
        float4 FxUpsample(float2 pixel)
        {
            int2 p; float2 f; FxFootprint(pixel,p,f);
            return lerp(lerp(FxEffectAt(p),FxEffectAt(p+int2(1,0)),f.x),lerp(FxEffectAt(p+int2(0,1)),FxEffectAt(p+1),f.x),f.y);
        }
        bool FxNeedsRepair(float2 pixel,bool distortion)
        {
            if(_FxInput.z<1.5) return false;
            float depth=FxDepthAt((int2)pixel);
            if(depth<0) return true;
            float tolerance=_FxTolerance.y+depth*_FxTolerance.z;
            int2 p; float2 f; FxFootprint(pixel,p,f);
            float4 smallest=1e30,largest=-1e30;
            [unroll] for(int y=0;y<2;y++) [unroll] for(int x=0;x<2;x++)
            {
                float weight=(x==0?1-f.x:f.x)*(y==0?1-f.y:f.y);
                if(weight<=0) continue;
                int2 tap=FxClampLow(p+int2(x,y)); float2 range=_FxDepthRange.Load(int3(tap,0));
                if(range.x<0 || range.y-range.x>tolerance || abs(depth-range.x)>tolerance || abs(depth-range.y)>tolerance) return true;
                float4 value=_FxEffect.Load(int3(tap,0));
                if(distortion) value.z=0; // B stores weighted surface eye depth, not color.
                smallest=min(smallest,value); largest=max(largest,value);
            }
            return any(largest-smallest>_FxTolerance.w);
        }
        float4 FxTexture(float2 uv)
        {
            uint w,h; _FxTexture.GetDimensions(w,h);
            float2 p=saturate(uv)*float2(w,h)-.5; int2 basePixel=(int2)floor(p); float2 f=p-basePixel; float4 result=0;
            [unroll] for(int y=0;y<2;y++) [unroll] for(int x=0;x<2;x++)
            {
                int2 tap=clamp(basePixel+int2(x,y),int2(0,0),int2(w,h)-1);
                float4 value=_FxTexture.Load(int3(tap,0));
                if(!FogFinite3(value.rgb) || !FogFinite(value.a)) value=0;
                result+=value*(x==0?1-f.x:f.x)*(y==0?1-f.y:f.y);
            }
            return result;
        }
        float3 FxRefract(float2 pixel,float2 offset,float eye,float3 original)
        {
            float2 p=pixel+offset*_FogScreen.xy-.5; int2 basePixel=(int2)floor(p); float2 f=p-basePixel;
            float3 color=0; float sum=0;
            [unroll] for(int y=0;y<2;y++) [unroll] for(int x=0;x<2;x++)
            {
                int2 tap=basePixel+int2(x,y);
                if(any(tap<0) || any(tap>=(int2)_FogScreen.xy) || FxProtected(tap)) continue;
                float depth=FxDepthAt(tap);
                if(depth<eye-_FxTolerance.x) continue;
                float weight=(x==0?1-f.x:f.x)*(y==0?1-f.y:f.y);
                color+=_FxBackground.Load(int3(tap,0)).rgb*weight; sum+=weight;
            }
            return sum>1e-6?color/sum:original;
        }
        struct FxVertex { float4 vertex:POSITION; float2 uv:TEXCOORD0; float4 color:COLOR; };
        struct FxVarying { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float3 world:TEXCOORD1; float4 color:COLOR; };
        FxVarying SurfaceVertex(FxVertex v)
        {
            FxVarying o; float4 world=mul(unity_ObjectToWorld,v.vertex);
            o.pos=mul(_FxViewProjection,world); o.world=world.xyz; o.uv=v.uv; o.color=v.color; return o;
        }
        float4 FxSample(FxVarying input,out float eye,out float2 fullPixel)
        {
            fullPixel=_FxInput.w>.5 && _FxInput.w<1.5?input.pos.xy*_FogScreen.xy/_FxLowSize.xy:input.pos.xy;
            int2 pixel=clamp((int2)fullPixel,int2(0,0),(int2)_FogScreen.xy-1);
            eye=-mul(_FogWorldToView,float4(input.world,1)).z;
            if(!FogFinite(eye) || eye<_FogViewParameters.x || eye>_FogViewParameters.y) discard;
            if(_FxInput.w>1.5 && !FxNeedsRepair(fullPixel,_FxSurface.x>1.5)) discard;
            float opaque=_FxInput.w>.5 && _FxInput.w<1.5?_FxDepthRange.Load(int3((int2)input.pos.xy,0)).x:FxDepthAt(pixel);
            if(opaque<0 || opaque<eye-_FxTolerance.x || (_FxInput.w!=1 && FxProtected(pixel))) discard;
            float4 texel=FxTexture(input.uv*_FxTextureST.xy+_FxTextureST.zw);
            float alpha=saturate(texel.a)*_FxRadiance.a;
            if(_FxSurface.y>.5) { texel.rgb*=max(0,input.color.rgb); alpha*=saturate(input.color.a); }
            if(_FxSurface.z>0) alpha*=smoothstep(0,_FxSurface.z,1-length(input.uv*2-1));
            if(_FxSurface.w>0) alpha*=saturate((opaque-eye)/_FxSurface.w);
            return float4(max(0,texel.rgb),alpha);
        }
        float4 SurfaceColor(FxVarying input):SV_Target
        {
            float eye; float2 pixel; float4 value=FxSample(input,eye,pixel); float3 color=value.rgb*_FxRadiance.rgb;
            if(_FxFlags.x>.5)
            {
                float3 start;
                if(FogNear(pixel,start)) { float4 fog=FogIntegrate(start,input.world,false); color=color*fog.a+(_FxSurface.x<.5?fog.rgb:0); }
            }
            return float4(color*value.a,_FxSurface.x<.5?value.a:0);
        }
        float4 DistortionField(FxVarying input):SV_Target
        {
            float eye; float2 pixel; float4 value=FxSample(input,eye,pixel);
            float2 offset=(_FxDistortion.xy+(_FxFlags.y>.5?value.rg*2-1:0)*_FxDistortion.zw)/float2(_FxLowSize.z,1);
            return float4(offset*value.a,eye*value.a,value.a);
        }
        float4 DistortionReplay(FxVarying input):SV_Target
        {
            float eye; float2 pixel; float4 value=FxSample(input,eye,pixel);
            float2 offset=(_FxDistortion.xy+(_FxFlags.y>.5?value.rg*2-1:0)*_FxDistortion.zw)/float2(_FxLowSize.z,1);
            float3 original=_FxBackground.Load(int3((int2)pixel,0)).rgb;
            return float4(FxRefract(pixel,offset,eye,original)*value.a,value.a);
        }
        float4 ResolveColor(v2f_img input):SV_Target
        {
            float4 original=_MainTex.Load(int3((int2)input.pos.xy,0));
            if(FxProtected((int2)input.pos.xy) || FxNeedsRepair(input.pos.xy,false)) return original;
            float4 value=FxUpsample(input.pos.xy);
            return float4(value.rgb+original.rgb*(1-saturate(value.a)),original.a);
        }
        float4 ResolveDistortion(v2f_img input):SV_Target
        {
            float4 original=_MainTex.Load(int3((int2)input.pos.xy,0));
            if(FxProtected((int2)input.pos.xy) || FxNeedsRepair(input.pos.xy,true)) return original;
            float4 field=FxUpsample(input.pos.xy);
            if(field.a<=1e-6) return original;
            float3 refracted=FxRefract(input.pos.xy,field.xy/field.a,field.z/field.a,original.rgb);
            return float4(lerp(original.rgb,refracted,saturate(field.a)),original.a);
        }
        float4 CopyColor(v2f_img input):SV_Target { return _MainTex.Load(int3((int2)input.pos.xy,0)); }
        ENDCG
        Pass
        {
            Name "FX_OPAQUE_DEPTH_RANGE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ReduceDepth
            ENDCG
        }
        Pass
        {
            Name "FX_LOW_PREMULTIPLIED_COLOR"
            Cull [_Cull] Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SurfaceVertex
            #pragma fragment SurfaceColor
            ENDCG
        }
        Pass
        {
            Name "FX_FULL_COLOR_OR_EDGE_REPLAY"
            Cull [_Cull] Blend One OneMinusSrcAlpha, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SurfaceVertex
            #pragma fragment SurfaceColor
            ENDCG
        }
        Pass
        {
            Name "FX_DISTORTION_FIELD"
            Cull [_Cull] Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SurfaceVertex
            #pragma fragment DistortionField
            ENDCG
        }
        Pass
        {
            Name "FX_UPSCALE_COLOR"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ResolveColor
            ENDCG
        }
        Pass
        {
            Name "FX_UPSCALE_DISTORTION"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment ResolveDistortion
            ENDCG
        }
        Pass
        {
            Name "FX_DISTORTION_EDGE_REPLAY"
            Cull [_Cull] Blend One OneMinusSrcAlpha, Zero One ColorMask RGB
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex SurfaceVertex
            #pragma fragment DistortionReplay
            ENDCG
        }
        Pass
        {
            Name "FX_EXACT_HDR_COPY"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment CopyColor
            ENDCG
        }
    }
}
