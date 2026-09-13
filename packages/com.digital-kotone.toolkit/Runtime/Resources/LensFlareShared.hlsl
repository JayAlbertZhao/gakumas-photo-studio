        #include "UnityCG.cginc"
        #include "FogVolume.hlsl"
        struct FlareEmitter { float4 screen,occlusion,options,radiance; };
        struct FlareElement { float4 centerAxisX,axisYSource,tintSoftness,shape,atlas; };
        StructuredBuffer<FlareEmitter> _FlareEmitters;
        StructuredBuffer<FlareElement> _FlareElements;
        Texture2D<float> _FlareDepth,_FlareVisibility,_FlareProtection;
        Texture2D<float4> _MainTex,_FlareArtifacts,_FlareAtlas;
        float4 _FlareInput;

        float FlareVisible(float2 uv,float eye,float bias,float outside)
        {
            if(any(uv<0)||any(uv>=1))return outside;
            int2 pixel=int2(uv*_FogScreen.xy);float z=_FlareDepth.Load(int3(pixel,0));
            if(!FogFinite(z)||z<0)return 0;
            if(_FlareInput.x>.5)
            {
                if(z>1)return 0;
                bool sky=_FogViewParameters.z>.5?z==0:z==1;if(sky)return 1;
                float3 world;
                if(!FogUnproject(pixel+.5,_FogViewParameters.z>.5?z:lerp(_FogViewParameters.w,1,z),world))return 0;
                z=-mul(_FogWorldToView,float4(world,1)).z;
            }
            else if(z==0)return 1;
            if(!FogFinite(z)||z<_FogViewParameters.x)return 0;
            return z>=eye-bias;
        }
        float SourceVisibility(v2f_img input):SV_Target
        {
            FlareEmitter source=_FlareEmitters[(uint)input.pos.x];
            if(source.screen.w<=0)return 0;
            if(source.occlusion.w<.5)return source.screen.w;
            int grid=(int)source.options.x;float visible=0,count=0;
            [loop] for(int y=0;y<grid;y++) [loop] for(int x=0;x<grid;x++)
            {
                float2 offset=(float2(x,y)+.5)*(2.0/grid)-1;
                if(dot(offset,offset)>1)continue;
                visible+=FlareVisible(source.screen.xy+offset*source.occlusion.xy,source.screen.z,source.occlusion.z,source.options.y);count++;
            }
            return source.screen.w*visible/max(count,1);
        }
        struct ElementVarying
        {
            float4 position:SV_POSITION;
            float2 local:TEXCOORD0;
            nointerpolation uint index:TEXCOORD1;
            nointerpolation float3 radiance:TEXCOORD2;
        };
        ElementVarying ElementVertex(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
        {
            float2 local=vertex==0?float2(-1,-1):vertex==1?float2(1,-1):vertex==2?float2(1,1):vertex==3?float2(-1,-1):vertex==4?float2(1,1):float2(-1,1);
            FlareElement element=_FlareElements[instance];uint emitter=(uint)element.axisYSource.z;
            float visible=_FlareVisibility.Load(int3(emitter,0,0));
            // A padded screen rectangle keeps subpixel raster snapping from dropping a
            // valid edge sample. Evaluate the actual rotated profile from SV_Position.
            float2 extent=abs(element.centerAxisX.zw)+abs(element.axisYSource.xy)+1/_FlareInput.zw;
            float2 uv=element.centerAxisX.xy+extent*local;
            ElementVarying o;o.position=float4(uv*2-1,0,1);
            #if UNITY_UV_STARTS_AT_TOP
            o.position.y=-o.position.y;
            #endif
            // Entire clipped quad for a hidden source; never reuse old visibility.
            if(visible<=0)o.position=float4(2,2,0,1);
            o.local=local;o.index=instance;o.radiance=_FlareEmitters[emitter].radiance.xyz*element.tintSoftness.xyz*visible;return o;
        }
        float4 FlareAtlas(float2 local,float4 rect)
        {
            float2 p=(local*.5+.5)*rect.zw-.5;int2 a=(int2)floor(p);float2 f=frac(p);int2 maximum=(int2)rect.zw-1,start=(int2)rect.xy;
            float4 c00=_FlareAtlas.Load(int3(start+clamp(a,0,maximum),0)),c10=_FlareAtlas.Load(int3(start+clamp(a+int2(1,0),0,maximum),0));
            float4 c01=_FlareAtlas.Load(int3(start+clamp(a+int2(0,1),0,maximum),0)),c11=_FlareAtlas.Load(int3(start+clamp(a+1,0,maximum),0));
            return lerp(lerp(c00,c10,f.x),lerp(c01,c11,f.x),f.y);
        }
        float4 ElementFragment(ElementVarying input):SV_Target
        {
            FlareElement element=_FlareElements[input.index];int shape=(int)element.axisYSource.w;
            float2 delta=input.position.xy/_FlareInput.zw-element.centerAxisX.xy;
            float2 xAxis=element.centerAxisX.zw,yAxis=element.axisYSource.xy;
            float determinant=xAxis.x*yAxis.y-xAxis.y*yAxis.x;
            float2 local=float2(delta.x*yAxis.y-delta.y*yAxis.x,xAxis.x*delta.y-xAxis.y*delta.x)/determinant;
            if(any(abs(local)>1))return 0;
            float3 profile=1;float radius=length(local),q=radius;
            if(shape==4)
            {
                float4 texel=FlareAtlas(local,element.atlas);
                // Bad caller atlas texels do not contaminate the frame.
                if(!FogFinite3(texel.rgb)||!FogFinite(texel.a))return 0;
                profile=max(texel.rgb,0)*saturate(texel.a);
            }
            else
            {
                if(shape==1)q=abs(radius-element.shape.x)/element.shape.y;
                if(shape==2)
                {
                    float sector=2*UNITY_PI/element.shape.z,angle=atan2(local.y,local.x);
                    float localAngle=frac(angle/sector+.5)*sector-sector*.5;
                    q=radius*cos(localAngle)/cos(UNITY_PI/element.shape.z);
                }
                float coverage=element.tintSoftness.w>0?1-smoothstep(1-element.tintSoftness.w,1,q):(q<1?1:0);
                profile=pow(saturate(coverage),element.shape.w);
                // A finite central core makes the exact optical axis independent of atan2(0,0).
                if(shape==3&&radius>=.0001)profile*=pow(abs(cos(atan2(local.y,local.x)*element.shape.z*.5)),element.shape.w);
            }
            return float4(profile*input.radiance,0);
        }
        float4 FlareComposite(v2f_img input):SV_Target
        {
            int2 pixel=(int2)input.pos.xy;float4 original=_MainTex.Load(int3(pixel,0));
            if(_FlareInput.y>.5){float p=_FlareProtection.Load(int3(pixel,0));if(!FogFinite(p)||p>0)return original;}
            // Explicit bilinear reconstruction, independent of inherited samplers/anisotropy.
            float2 p=input.pos.xy*_FogScreen.zw*_FlareInput.zw-.5;int2 a=(int2)floor(p),maximum=(int2)_FlareInput.zw-1;float2 f=frac(p);
            float3 c00=_FlareArtifacts.Load(int3(clamp(a,0,maximum),0)).rgb,c10=_FlareArtifacts.Load(int3(clamp(a+int2(1,0),0,maximum),0)).rgb;
            float3 c01=_FlareArtifacts.Load(int3(clamp(a+int2(0,1),0,maximum),0)).rgb,c11=_FlareArtifacts.Load(int3(clamp(a+1,0,maximum),0)).rgb;
            return float4(original.rgb+lerp(lerp(c00,c10,f.x),lerp(c01,c11,f.x),f.y),original.a);
        }
