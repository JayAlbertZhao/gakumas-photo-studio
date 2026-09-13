Shader "Hidden/GakumasPhotoMode/FogVolume"
{
    Properties { _MainTex ("Linear HDR",2D)="white"{} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        #include "FogVolume.hlsl"
        Texture2D<float4> _MainTex, _FogTransfer;
        Texture2D<float> _FogDepth, _FogProtection;
        float4 _FogInput;
        float4 Transfer(v2f_img i):SV_Target
        {
            int2 p=int2(i.pos.xy);float4 clear=float4(0,0,0,1);
            if(_FogInput.y>.5){float protect=_FogProtection.Load(int3(p,0));if(!FogFinite(protect)||protect>0)return clear;}
            float depth=_FogDepth.Load(int3(p,0));if(!FogFinite(depth)||depth<0)return clear;
            bool device=_FogInput.x>.5,sky=device?(_FogViewParameters.z>.5?depth<=0:depth>=1):depth==0;
            if(device&&depth>1)return clear;
            float3 start,finish;if(!FogNear(i.pos.xy,start))return clear;
            if(device)
            {if(!FogUnproject(i.pos.xy,_FogViewParameters.z>.5?depth:lerp(_FogViewParameters.w,1,depth),finish))return clear;}
            else
            {
                float3 farPoint;if(!FogUnproject(i.pos.xy,_FogViewParameters.z>.5?0:1,farPoint))return clear;
                float nearZ=-mul(_FogWorldToView,float4(start,1)).z,farZ=-mul(_FogWorldToView,float4(farPoint,1)).z;
                if(!FogFinite(nearZ)||!FogFinite(farZ)||farZ<=nearZ||(!sky&&depth<nearZ))return clear;
                finish=lerp(start,farPoint,sky?1:saturate((depth-nearZ)/(farZ-nearZ)));
            }
            return FogIntegrate(start,finish,sky);
        }
        float4 Composite(v2f_img i):SV_Target
        {
            int3 p=int3(int2(i.pos.xy),0);float4 source=_MainTex.Load(p),fog=_FogTransfer.Load(p);
            return float4(source.rgb*fog.a+fog.rgb,source.a);
        }
        ENDCG
        Pass
        {
            Name "FOG_TRANSFER"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Transfer
            ENDCG
        }
        Pass
        {
            Name "FOG_COMPOSITE"
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Composite
            ENDCG
        }
    }
}
