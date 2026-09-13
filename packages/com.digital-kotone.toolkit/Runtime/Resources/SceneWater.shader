Shader "Hidden/GakumasPhotoMode/SceneWater"
{
    Properties { _Cull("Cull",Float)=2 }
    SubShader
    {
        Pass
        {
            Name "CURRENT_LIT_WATER_TRANSMISSION"
            Cull [_Cull] ZTest Always ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex WaterVertex
            #pragma fragment WaterFragment
            #pragma multi_compile_local __ SCENE_LIGHT_SHADOWS
            #pragma multi_compile_local __ SCENE_MAIN_LIGHT_SHADOWS
            #include "SceneForwardLighting.hlsl"
            #include "FogVolume.hlsl"
            Texture2D<float4> _WaterBackground, _WaterPlanar;
            Texture2D<float> _WaterDepth;
            samplerCUBE _WaterProbe;
            float4 _WaterInput, _WaterOptics, _WaterReflection, _WaterWaveA, _WaterWaveB, _WaterNormalScroll;
            float2 _WaterReflectionDistortion;
            float2 _WaterPixelAxes;
            float3 _WaterAbsorption, _WaterScattering;
            float _WaterTransformSign;

            ForwardVarying WaterVertex(ForwardInput input)
            {
                ForwardVarying o=ForwardVertex(input);
                // Bare DrawRenderer does not guarantee unity_WorldTransformParams.
                // Preserve current GPU-skinned attributes, explicitly supply only parity.
                o.tangent.w=input.tangent.w*_WaterTransformSign*sign(_VertexScale.x*_VertexScale.y*_VertexScale.z);
                return o;
            }
            float WaterDepth(int2 pixel)
            {
                float z=_WaterDepth.Load(int3(pixel,0));
                if(!FogFinite(z)||z<0)return -1;
                if(_WaterInput.x>.5)
                {
                    if(z>1)return -1;
                    if(_FogViewParameters.z>.5?z==0:z==1)return _FogViewParameters.y;
                    float3 world;if(!FogUnproject(pixel+.5,_FogViewParameters.z>.5?z:lerp(_FogViewParameters.w,1,z),world))return -1;
                    z=-mul(_FogWorldToView,float4(world,1)).z;
                }
                else if(z==0)return _FogViewParameters.y;
                return z>=_FogViewParameters.x?min(z,_FogViewParameters.y):-1;
            }
            float2 WaterSlope(float2 uv,float4 wave)
            {return wave.z*wave.xy*cos(dot(uv,wave.xy)+wave.w);}
            float3 WaterTransmission(float2 position,float2 offset,float eye,float3 fallback)
            {
                float2 p=position+offset-.5;int2 basePixel=(int2)floor(p);float2 f=p-basePixel;
                float3 result=0;float total=0;
                [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++)
                {
                    int2 tap=basePixel+int2(x,y);
                    if(any(tap<0)||any(tap>=(int2)_FogScreen.xy))continue;
                    if(WaterDepth(tap)<eye-_WaterInput.y)continue;
                    float3 color=_WaterBackground.Load(int3(tap,0)).rgb;if(!FogFinite3(color))continue;
                    float w=(x==0?1-f.x:f.x)*(y==0?1-f.y:f.y);result+=color*w;total+=w;
                }
                return total>1e-6?result/total:fallback;
            }
            float3 WaterPlanar(float2 pixel,float3 fallback)
            {
                float2 p=pixel-.5;int2 basePixel=(int2)floor(p);float2 f=p-basePixel;
                float3 sum=0;float coverage=0;
                [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++)
                {
                    int2 tap=basePixel+int2(x,y);if(any(tap<0)||any(tap>=(int2)_FogScreen.xy))continue;
                    float4 sample=_WaterPlanar.Load(int3(tap,0));if(!FogFinite3(sample.rgb)||!FogFinite(sample.a))continue;
                    float weight=(x==0?1-f.x:f.x)*(y==0?1-f.y:f.y)*saturate(sample.a);
                    sum+=sample.rgb*weight;coverage+=weight;
                }
                return sum+fallback*(1-coverage);
            }
            float4 WaterFragment(ForwardVarying input):SV_Target
            {
                int2 pixel=(int2)input.position.xy;
                float eye=-mul(_FogWorldToView,float4(input.world,1)).z,opaque=WaterDepth(pixel);
                if(!FogFinite(eye)||eye<_FogViewParameters.x||eye>_FogViewParameters.y||opaque<eye-_WaterInput.y)discard;
                float4 background=_WaterBackground.Load(int3(pixel,0));
                if(!FogFinite3(background.rgb)||!FogFinite(background.a))discard;
                float3 geometric=ForwardNormal(input.normal),t=ForwardNormal(input.tangent.xyz-geometric*dot(geometric,input.tangent.xyz));
                float3 b=ForwardNormal(cross(geometric,t))*input.tangent.w;
                float2 slope=WaterSlope(input.uv,_WaterWaveA)+WaterSlope(input.uv,_WaterWaveB);
                float3 map=float3(0,0,1);
                if(_WaterNormalScroll.z>.5)map=ForwardNormal(tex2D(_NormalMap,frac(input.uv+_WaterNormalScroll.xy)).rgb*2-1);
                map=ForwardNormal(float3(map.xy*_WaterOptics.w-slope,max(map.z,.001)));
                float3 n=ForwardNormal(t*map.x+b*map.y+geometric*map.z);
                float3 v=ForwardNormal(lerp(_CameraPosition-input.world,-_CameraForward,_Orthographic));
                if(dot(n,v)<0)n=-n;
                float rayScale=1/max(abs(dot(v,_CameraForward)),.0001);
                float thickness=min(max(opaque-eye,0)*rayScale,_WaterInput.z);
                float3 normalDelta=mul((float3x3)_FogWorldToView,n-geometric);
                // GPU projection already accounts for rendering into a texture.
                // Convert view axes through projection AND viewport parity, once.
                float2 offset=normalDelta.xy*_WaterPixelAxes*_WaterInput.w*thickness;
                float3 transmitted=WaterTransmission(input.position.xy,offset,eye,background.rgb);
                float3 attenuation=exp(-_WaterAbsorption*thickness);
                float fresnel=(_WaterOptics.x+(1-_WaterOptics.x)*pow(1-saturate(dot(n,v)),5))*_WaterOptics.y;
                float smoothness=saturate(tex2D(_MosMap,input.uv).b*_Mos.b);
                float3 reflected=0;
                if(_WaterReflection.x>.5)reflected=max(0,texCUBElod(_WaterProbe,float4(reflect(-v,n),(1-smoothness)*_WaterReflection.y)).rgb);
                if(_WaterReflection.z>.5)reflected=WaterPlanar(input.position.xy+normalDelta.xy*_WaterReflectionDistortion*_FogScreen.xy,reflected);
                input.normal=n;float4 lit=ForwardFragment(input);
                float alpha=lit.a;
                float3 radiance=(transmitted*attenuation+_WaterScattering*(1-attenuation))*(1-fresnel)+reflected*fresnel;
                // Shared Forward evaluates authored surface lighting and premultiplies once.
                if(_WaterOptics.z>0)alpha*=saturate(thickness/_WaterOptics.z);
                float3 color=radiance*alpha+lit.rgb*(lit.a>1e-6?alpha/lit.a:0)+background.rgb*(1-alpha);
                return float4(clamp(color,0,65504),alpha+background.a*(1-alpha));
            }
            ENDCG
        }
    }
}
