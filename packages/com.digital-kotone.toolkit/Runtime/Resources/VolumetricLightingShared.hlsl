        #include "UnityCG.cginc"
        #include "FogVolume.hlsl"
        #include "SceneLightShadow.hlsl"
        Texture2D<float4> _MainTex,_VolumeScattering;
        Texture2D<float> _VolumeDepth,_VolumeProtection;
        float4 _VolumeInput,_MediumCenter,_MediumHalfSize,_MediumParameters,_MediumAlbedo;
        float4 _VolumeLightPositionRange,_VolumeLightDirectionOuter,_VolumeLightRadianceInner;
        float _VolumeFalloff;

        bool VolumeRay(float2 pixel,out float3 start,out float3 direction,out float rayLength)
        {
            start=direction=0;rayLength=0;int3 texel=int3(int2(pixel),0);
            if(_VolumeInput.y>.5){float p=_VolumeProtection.Load(texel);if(!FogFinite(p)||p>0)return false;}
            float z=_VolumeDepth.Load(texel);if(!FogFinite(z)||z<0)return false;
            bool device=_VolumeInput.x>.5,sky=device?(_FogViewParameters.z>.5?z==0:z==1):z==0;
            if((sky&&_VolumeInput.z<.5)||(device&&z>1)||!FogNear(pixel,start))return false;
            float3 end;
            if(device)
            {if(!FogUnproject(pixel,_FogViewParameters.z>.5?z:lerp(_FogViewParameters.w,1,z),end))return false;}
            else
            {
                if(!FogUnproject(pixel,_FogViewParameters.z>.5?0:1,end))return false;
                float a=-mul(_FogWorldToView,float4(start,1)).z,b=-mul(_FogWorldToView,float4(end,1)).z;
                if(!FogFinite(a)||!FogFinite(b)||b<=a||(!sky&&z<a))return false;
                end=lerp(start,end,sky?1:saturate((z-a)/(b-a)));
            }
            direction=end-start;rayLength=length(direction);
            if(!FogFinite(rayLength)||rayLength<=1e-7||rayLength>4e6)return false;
            direction/=rayLength;return true;
        }
        bool VolumeBox(float3 origin,float3 direction,float maximum,out float enter,out float leave)
        {
            enter=0;leave=maximum;float3 local=origin-_MediumCenter.xyz;
            [unroll] for(int axis=0;axis<3;axis++)
            {
                if(abs(direction[axis])<1e-8)
                {
                    float tolerance=0;
                    #if defined(HEAVY_FX_VOLUME)
                    // Closed parallel slab: unprojection can put an exact face ray a
                    // few float ULPs outside. Keep resolvably outside rays excluded.
                    float scale=max(max(abs(origin[axis]),abs(_MediumCenter[axis])),_MediumHalfSize[axis]);
                    tolerance=4*(asfloat(asuint(scale)+1)-scale);
                    #endif
                    if(abs(local[axis])>_MediumHalfSize[axis]+tolerance)return false;
                }
                else
                {
                    float a=(-_MediumHalfSize[axis]-local[axis])/direction[axis],b=(_MediumHalfSize[axis]-local[axis])/direction[axis];
                    enter=max(enter,min(a,b));leave=min(leave,max(a,b));
                }
            }
            return leave>enter;
        }
        bool VolumeCone(float3 origin,float3 direction,inout float enter,inout float leave)
        {
            float radius=_VolumeLightPositionRange.w;
            float3 o=(origin-_VolumeLightPositionRange.xyz)/radius,axis=_VolumeLightDirectionOuter.xyz;
            float nearest=-dot(o,direction);float3 perpendicular=o+nearest*direction;
            float halfSquared=1-dot(perpendicular,perpendicular);if(halfSquared<=0)return false;
            float halfChord=sqrt(halfSquared);enter=max(enter,(nearest-halfChord)*radius);leave=min(leave,(nearest+halfChord)*radius);if(leave<=enter)return false;
            float a0=dot(o,axis),a1=dot(direction,axis),cosineSquared=_VolumeLightDirectionOuter.w*_VolumeLightDirectionOuter.w;
            float A=a1*a1-cosineSquared,B=2*(a0*a1-cosineSquared*dot(o,direction)),C=a0*a0-cosineSquared*dot(o,o);
            float events[5];int count=2;events[0]=enter;events[1]=leave;
            if(abs(a1)>1e-8)events[count++]=clamp(-a0/a1*radius,enter,leave);
            if(abs(A)<1e-8){if(abs(B)>1e-8)events[count++]=clamp(-C/B*radius,enter,leave);}
            else
            {
                float discriminant=B*B-4*A*C;
                if(discriminant>=0)
                {
                    float root=sqrt(discriminant),q=-.5*(B+(B>=0?root:-root));
                    if(abs(q)>1e-12){events[count++]=clamp(q/A*radius,enter,leave);events[count++]=clamp(C/q*radius,enter,leave);}
                    else events[count++]=clamp(-B/(2*A)*radius,enter,leave);
                }
            }
            [loop] for(int j=1;j<count;j++){float key=events[j];int k=j-1;[loop] while(k>=0){if(events[k]<=key)break;events[k+1]=events[k];k--;}events[k+1]=key;}
            float lo=leave,hi=enter;
            [loop] for(int e=0;e<count-1;e++)
            {
                float3 p=o+direction*((events[e]+events[e+1])*(.5/radius));float along=dot(p,axis);
                if(along>=0&&along*along>=cosineSquared*dot(p,p)){lo=min(lo,events[e]);hi=max(hi,events[e+1]);}
            }
            enter=lo;leave=hi;return leave>enter;
        }
        float4 VolumeScatterRay(float3 start,float3 direction,float lengthRay)
        {
            float mediumEnter,mediumLeave;
            if(_MediumParameters.x<=0||!VolumeBox(start,direction,lengthRay,mediumEnter,mediumLeave))return 0;
            float enter=mediumEnter,leave=mediumLeave;if(!VolumeCone(start,direction,enter,leave))return 0;
            float stepLength=(leave-enter)/_MediumParameters.z,total=0,sigma=_MediumParameters.x,g=_MediumParameters.y;
            float stepOpacity=FogOneMinusExp(sigma*stepLength);
            [loop] for(int i=0;i<(int)_MediumParameters.z;i++)
            {
                float a=enter+i*stepLength,s=a+stepLength*.5;float3 world=start+direction*s,delta=world-_VolumeLightPositionRange.xyz;
                float distanceToLight=length(delta);if(distanceToLight<1e-7)continue;
                float3 incoming=delta/distanceToLight;float cosine=dot(incoming,_VolumeLightDirectionOuter.xyz);
                float angular=_VolumeLightRadianceInner.w>_VolumeLightDirectionOuter.w?saturate((cosine-_VolumeLightDirectionOuter.w)/(_VolumeLightRadianceInner.w-_VolumeLightDirectionOuter.w)):1;
                float attenuation=pow(saturate(1-distanceToLight/_VolumeLightPositionRange.w),_VolumeFalloff)*angular;
                // Incoming points with photon propagation, outgoing points to the camera.
                float mu=dot(incoming,-direction),denominator=1+g*(g-2*mu);
                float phase=(1-g*g)/(4*UNITY_PI*denominator*sqrt(denominator));
                float lightEnter,lightLeave;float lightPath=VolumeBox(world,-incoming,distanceToLight,lightEnter,lightLeave)?lightLeave-lightEnter:0;
                float visibility=1;
                #if defined(SCENE_LIGHT_SHADOWS)
                SceneShadowData data;data.worldToShadow=_SingleShadowMatrix;data.atlasST=_SingleShadowST;data.depth=_SingleShadowDepth;data.options=_SingleShadowOptions;
                visibility=SceneLightVisibility(world,float3(0,0,0),data);
                #endif
                total+=exp(-sigma*(a-mediumEnter+lightPath))*stepOpacity*phase*attenuation*visibility;
            }
            return float4(total*_MediumAlbedo.xyz*_VolumeLightRadianceInner.xyz,0);
        }
        float4 Scatter(v2f_img input):SV_Target
        {
            float3 start,direction;float lengthRay;
            if(_MediumParameters.x<=0||!VolumeRay(input.pos.xy,start,direction,lengthRay))return 0;
            return VolumeScatterRay(start,direction,lengthRay);
        }
        float4 Composite(v2f_img input):SV_Target
        {
            int3 texel=int3(int2(input.pos.xy),0);float4 original=_MainTex.Load(texel);float3 start,direction;float lengthRay,enter,leave;
            if(!VolumeRay(input.pos.xy,start,direction,lengthRay))return original;
            float transmission=1;
            if(_VolumeInput.w>.5&&VolumeBox(start,direction,lengthRay,enter,leave))transmission=exp(-_MediumParameters.x*(leave-enter));
            return float4(original.rgb*transmission+_VolumeScattering.Load(texel).rgb,original.a);
        }
