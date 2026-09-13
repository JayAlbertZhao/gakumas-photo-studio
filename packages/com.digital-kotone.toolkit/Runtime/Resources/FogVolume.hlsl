#ifndef TOOLKIT_FOG_VOLUME_INCLUDED
#define TOOLKIT_FOG_VOLUME_INCLUDED
// Independent finite-ray extinction model. No game shader source or bytecode.
#define FOG_MAX_SPHERES 8
#define FOG_MAX_EVENTS 20
float4 _FogSpheres[FOG_MAX_SPHERES], _FogColors[FOG_MAX_SPHERES], _FogMedia[FOG_MAX_SPHERES];
float4 _FogDistance, _FogDistanceColor, _FogControl, _FogScreen, _FogViewParameters;
float4x4 _FogInverseVP, _FogWorldToView;
bool FogFinite(float x) { return (asuint(x)&0x7fffffffu)<0x7f800000u; }
bool FogFinite3(float3 x) { return FogFinite(x.x)&&FogFinite(x.y)&&FogFinite(x.z); }
bool FogUnproject(float2 pixel,float clipDepth,out float3 world)
{
    float2 xy=pixel*_FogScreen.zw*2-1;
    #if UNITY_UV_STARTS_AT_TOP
    xy.y=-xy.y;
    #endif
    float4 p=mul(_FogInverseVP,float4(xy,clipDepth,1));
    world=p.xyz/p.w;return abs(p.w)>1e-12&&FogFinite3(world);
}
bool FogNear(float2 pixel,out float3 nearPoint)
{return FogUnproject(pixel,_FogViewParameters.z>.5?1:_FogViewParameters.w,nearPoint);}
float FogOneMinusExp(float tau)
{return tau<.01 ? tau*(1-tau*(.5-tau*(1.0/6-tau/24))) : 1-exp(-tau);}

// RGB is integrated constant per-medium source radiance; A is beam transmittance.
float4 FogIntegrate(float3 start,float3 finish,bool sky)
{
    float3 delta=finish-start;float lengthRay=length(delta);
    if(_FogControl.z<=0||!FogFinite3(start)||!FogFinite3(finish)||!FogFinite(lengthRay)||lengthRay<=1e-7||lengthRay>4e6)return float4(0,0,0,1);
    float3 direction=delta/lengthRay;
    float events[FOG_MAX_EVENTS];float4 chords[FOG_MAX_SPHERES];
    events[0]=0;events[1]=lengthRay;int count=2;
    float da=clamp(_FogDistance.y,0,lengthRay),db=clamp(_FogDistance.z,0,lengthRay);
    bool useDistance=_FogDistance.x>0&&db>da&&(!sky||_FogDistance.w>.5);
    if(useDistance){events[count++]=da;events[count++]=db;}
    [loop] for(int i=0;i<(int)_FogControl.x;i++)
    {
        float4 sphere=_FogSpheres[i];float3 origin=(start-sphere.xyz)/sphere.w;
        float closest=-dot(origin,direction);float3 perpendicular=origin+closest*direction;
        float halfSquared=1-dot(perpendicular,perpendicular);chords[i]=0;
        if(halfSquared<=0||(sky&&_FogMedia[i].y<.5))continue;
        float halfChord=sqrt(halfSquared);
        float a=max(0,(closest-halfChord)*sphere.w),b=min(lengthRay,(closest+halfChord)*sphere.w);
        if(b<=a)continue;
        chords[i]=float4(closest,halfSquared,a,b);events[count++]=a;events[count++]=b;
    }
    // Insertion sort orders geometry boundaries, never artist-supplied colors.
    [loop] for(int j=1;j<count;j++)
    {float key=events[j];int k=j-1;[loop] while(k>=0){if(events[k]<=key)break;events[k+1]=events[k];k--;}events[k+1]=key;}
    float3 radiance=0;float opticalDepth=0;
    [loop] for(int e=0;e<count-1;e++)
    {
        float interval=events[e+1]-events[e];if(interval<=0)continue;
        int steps=(int)_FogControl.y;
        [loop] for(int step=0;step<steps;step++)
        {
            float a=events[e]+interval*((float)step/steps),b=events[e]+interval*((float)(step+1)/steps);
            float tau=useDistance?_FogDistance.x*max(0,min(b,db)-max(a,da)):0;
            float3 weighted=tau*_FogDistanceColor.rgb;
            [loop] for(int m=0;m<(int)_FogControl.x;m++)
            {
                float4 chord=chords[m];float lo=max(a,chord.z),hi=min(b,chord.w);if(hi<=lo)continue;
                float radius=_FogSpheres[m].w,span=(hi-lo)/radius,midpoint=(lo+hi)*(.5/radius)-chord.x;
                float optical=_FogMedia[m].x*radius*span*max(0,chord.y-midpoint*midpoint-span*span/12);
                weighted+=optical*_FogColors[m].rgb;tau+=optical;
            }
            if(tau>0){float opacity=FogOneMinusExp(tau);radiance+=exp(-opticalDepth)*opacity*(weighted/tau);opticalDepth+=tau;}
        }
    }
    float opacity=FogOneMinusExp(opticalDepth),limited=min(opacity,_FogControl.z);
    if(opacity>0)radiance*=limited/opacity;
    return float4(radiance,1-limited);
}
#endif
