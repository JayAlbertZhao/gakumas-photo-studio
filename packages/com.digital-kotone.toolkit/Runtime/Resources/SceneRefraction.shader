Shader "Hidden/GakumasPhotoMode/SceneRefraction"
{
    SubShader
    {
        Cull Off Blend Off
        HLSLINCLUDE
        #include "UnityCG.cginc"
        #include "FogVolume.hlsl"
        float4x4 _RefractionVP, _RefractionViewInverse, _RefractionProjection, _RefractionInverseProjection, _RefractionEnvironmentRotation;
        float _RefractionOrthographic, _RefractionMip;
        float3 _RefractionIor, _RefractionAbsorption, _RefractionRadianceScale;
        float4 _RefractionPlanes[128];
        int _RefractionPlaneCount, _RefractionInterfaces;
        sampler2D _RefractionSource, _RefractionOpaqueDepth;
        samplerCUBE _RefractionEnvironment;
        struct Output { float4 color : SV_Target0; float4 remaining : SV_Target1; float eye : SV_Target2; float z : SV_Depth; };
        struct Varying { float4 position : SV_POSITION; };
        Varying Fullscreen(float4 position : POSITION) { Varying o; o.position=float4(position.xy,0,1); return o; }
        Varying Geometry(float4 position : POSITION) { Varying o; o.position=mul(_RefractionVP,mul(unity_ObjectToWorld,position)); return o; }
        float4 ViewPoint(float2 pixel,float clipDepth)
        {
            float2 xy=pixel*_FogScreen.zw*2-1;
            #if UNITY_UV_STARTS_AT_TOP
            xy.y=-xy.y;
            #endif
            return mul(_RefractionInverseProjection,float4(xy,clipDepth,1));
        }
        float3 NearView(float2 pixel)
        {float4 p=ViewPoint(pixel,_FogViewParameters.z>.5?1:_FogViewParameters.w);return p.xyz/p.w;}
        float DeviceDepth(float2 pixel,float eye)
        {
            float3 view=NearView(pixel);
            if(_RefractionOrthographic>.5)
            {
                float4 far=ViewPoint(pixel,_FogViewParameters.z>.5?0:1);float3 delta=far.xyz/far.w-view;
                view+=delta*((-eye-view.z)/delta.z);
            }
            else view*=(-eye/view.z);
            float4 p=mul(_RefractionProjection,float4(view,1));float z=p.z/p.w;
            return saturate(_FogViewParameters.w<-.5?z*.5+.5:z);
        }
        Output Initialize(Varying i)
        {
            float2 uv=i.position.xy*_FogScreen.zw;Output o;
            o.color=tex2Dlod(_RefractionSource,float4(uv,0,0));o.remaining=0;
            o.eye=tex2Dlod(_RefractionOpaqueDepth,float4(uv,0,0)).r;
            float eye=FogFinite(o.eye)&&o.eye>0?clamp(o.eye,_FogViewParameters.x,_FogViewParameters.y):_FogViewParameters.y;
            o.z=DeviceDepth(i.position.xy,eye);return o;
        }
        bool Interval(float3 origin,float3 direction,out float entry,out float exit,out int entering,out int leaving)
        {
            entry=-1e30;exit=1e30;entering=leaving=-1;
            [loop] for(int f=0;f<_RefractionPlaneCount;f++)
            {
                float4 plane=_RefractionPlanes[f];float d=dot(plane.xyz,direction),h=dot(plane.xyz,origin)+plane.w;
                if(abs(d)<1e-8){if(h>0)return false;continue;}
                float t=-h/d;
                if(d<0){if(t>entry){entry=t;entering=f;}}
                else if(t<exit){exit=t;leaving=f;}
            }
            return entering>=0&&leaving>=0&&exit>=max(0,entry);
        }
        bool NextBoundary(float3 position,float3 direction,out float travel,out int face)
        {
            travel=1e30;face=-1;
            [loop] for(int f=0;f<_RefractionPlaneCount;f++)
            {
                float4 p=_RefractionPlanes[f];float d=dot(p.xyz,direction);
                if(d<=1e-8)continue;
                float t=-(dot(p.xyz,position)+p.w)/d;
                // Roundoff at the current supporting plane can be slightly negative.
                // No geometric ray offset is accumulated into absorption distance.
                if(t<0)t=0;
                if(t<travel){travel=t;face=f;}
            }
            return face>=0;
        }
        // Direction points along the camera path; normal opposes that direction.
        // Ratio is incident IOR / transmitted IOR, unlike a material's relative IOR.
        float Interface(float3 d,float3 n,float ratio,out float3 transmitted)
        {
            if(ratio==1){transmitted=d;return 0;}
            float c=saturate(-dot(d,n));float k=1-ratio*ratio*max(0,1-c*c);
            transmitted=0;if(k<=0)return 1;
            float ct=sqrt(k);transmitted=normalize(ratio*d+(ratio*c-ct)*n);
            float parallel=(ratio*c-ct)/(ratio*c+ct),perpendicular=(c-ratio*ct)/(c+ratio*ct);
            return saturate(.5*(parallel*parallel+perpendicular*perpendicular));
        }
        float Environment(float3 direction,int channel)
        {
            float3 d=mul((float3x3)_RefractionEnvironmentRotation,direction);
            float3 sample=texCUBElod(_RefractionEnvironment,float4(d,_RefractionMip)).rgb;
            return FogFinite(sample[channel])?clamp(sample[channel],0,65504)*_RefractionRadianceScale[channel]:0;
        }
        float Trace(float3 origin,float3 direction,float entry,int entering,bool inside,int channel,out float remainder)
        {
            float eta=_RefractionIor[channel],radiance=0,weight=1;float3 position=origin,d=direction;
            if(!inside)
            {
                position=origin+entry*d;float3 n=_RefractionPlanes[entering].xyz,transmitted;
                float f=Interface(d,n,1/eta,transmitted);
                radiance=f*Environment(reflect(d,n),channel);
                // Radiance transport across entry and exit cancels for an exterior sensor.
                weight=(1-f)/(eta*eta);d=transmitted;
            }
            [loop] for(int b=0;b<_RefractionInterfaces&&weight>0;b++)
            {
                float distance;int face;if(!NextBoundary(position,d,distance,face))break;
                position+=d*distance;weight*=exp(-_RefractionAbsorption[channel]*distance);
                float3 n=_RefractionPlanes[face].xyz,transmitted;float f=Interface(d,-n,eta,transmitted);
                if(f<1)radiance+=weight*(1-f)*(eta*eta)*Environment(transmitted,channel);
                weight*=f;d=normalize(reflect(d,n));
            }
            // Upper bound on remaining escaping coefficient, in exterior-environment radiance units.
            // For an inside sensor the IOR radiance scale is retained rather than silently cancelled.
            remainder=weight*(eta*eta);return radiance;
        }
        Output Refraction(Varying i)
        {
            // Form the ray before adding world translation. Subtracting two nearly
            // equal world points at a small near clip amplifies angular error near TIR.
            float3 nearView=NearView(i.position.xy);if(!FogFinite3(nearView))discard;
            float3 origin=mul(_RefractionViewInverse,float4(0,0,0,1)).xyz;
            float3 viewDirection=normalize(nearView);
            if(_RefractionOrthographic>.5)
            {
                float4 far=ViewPoint(i.position.xy,_FogViewParameters.z>.5?0:1);
                viewDirection=normalize(far.xyz/far.w-nearView);
                float3 sensor=nearView-viewDirection*(nearView.z/viewDirection.z);
                origin=mul(_RefractionViewInverse,float4(sensor,1)).xyz;
            }
            float3 direction=normalize(mul((float3x3)_RefractionViewInverse,viewDirection));float entry,exit;int entering,leaving;
            if(!Interval(origin,direction,entry,exit,entering,leaving))discard;
            bool inside=entry<0;float first=inside?exit:entry;
            float eye=-mul(_FogWorldToView,float4(origin+first*direction,1)).z;
            float lastEye=-mul(_FogWorldToView,float4(origin+exit*direction,1)).z;
            float nearEye=-nearView.z;
            if(lastEye<nearEye||eye>_FogViewParameters.y)discard;
            Output o;o.eye=max(nearEye,eye);o.z=DeviceDepth(i.position.xy,o.eye);o.color.a=o.remaining.a=1;
            [unroll] for(int c=0;c<3;c++)o.color[c]=Trace(origin,direction,entry,entering,inside,c,o.remaining[c]);
            return o;
        }
        ENDHLSL
        Pass
        {
            ZTest Always ZWrite On
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Fullscreen
            #pragma fragment Initialize
            ENDHLSL
        }
        Pass
        {
            ZTest LEqual ZWrite On
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Geometry
            #pragma fragment Refraction
            ENDHLSL
        }
    }
    Fallback Off
}
