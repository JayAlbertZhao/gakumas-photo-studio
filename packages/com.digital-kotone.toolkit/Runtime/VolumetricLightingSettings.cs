using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum VolumetricResolution { Full=1, Half=2, Quarter=4 }

    /// <summary>Reduced integration controls; Full retains its original path and ignores these settings.</summary>
    [Serializable]
    public sealed class VolumetricReconstructionSettings
    {
        [Min(0)] public float depthAbsoluteTolerance=.005f;
        [Range(0,1)] public float depthRelativeTolerance=.002f;
        [Min(0)] public float radianceAbsoluteTolerance=.001f;
        [Range(0,1)] public float radianceRelativeTolerance=.02f;
        [Range(1,2048)] public int maximumTargetMiB=512;
        internal bool Validate()=>FogVolumeSettings.Range(depthAbsoluteTolerance,0,10)&&FogVolumeSettings.Range(depthRelativeTolerance,0,1)&&
            FogVolumeSettings.Range(radianceAbsoluteTolerance,0,65504)&&FogVolumeSettings.Range(radianceRelativeTolerance,0,1)&&maximumTargetMiB>=1&&maximumTargetMiB<=2048;
    }

    /// <summary>Independent finite homogeneous medium and shadowed spot-light integration.</summary>
    [Serializable]
    public sealed class VolumetricLightingSettings
    {
        public const int MaximumLights=16;
        public bool enabled;
        public Vector3 mediumCenter=Vector3.zero;
        public Vector3 mediumHalfSize=new Vector3(20,20,20);
        [Min(0)] public float extinction=.08f;
        public Vector3 scatteringAlbedo=Vector3.one;
        [Range(-.95f,.95f)] public float anisotropy;
        [Range(8,256)] public int samplesPerLight=64;
        public bool attenuateBackground=true;
        public bool affectSky=true;
        public VolumetricResolution resolution=VolumetricResolution.Full;
        public VolumetricReconstructionSettings reconstruction=new VolumetricReconstructionSettings();
        public VolumetricSpotLight[] lights=Array.Empty<VolumetricSpotLight>();
        public SceneLightShadowSettings shadows=new SceneLightShadowSettings();

        internal bool Validate(out string reason)
        {
            reason=null;
            if((resolution!=VolumetricResolution.Full&&resolution!=VolumetricResolution.Half&&resolution!=VolumetricResolution.Quarter)||
                (resolution!=VolumetricResolution.Full&&(reconstruction==null||!reconstruction.Validate())))
            {reason="Invalid volumetric resolution or reconstruction settings";return false;}
            if(!FogVolumeSettings.Position(mediumCenter)||!FogVolumeSettings.Range(extinction,0,100)||
                !FogVolumeSettings.Range(anisotropy,-.95f,.95f)||samplesPerLight<8||samplesPerLight>256||lights==null||lights.Length>MaximumLights)
            {reason="Invalid volumetric medium, sample count or light budget";return false;}
            for(int i=0;i<3;i++)if(!FogVolumeSettings.Range(mediumHalfSize[i],.001f,1e4f)||!FogVolumeSettings.Range(scatteringAlbedo[i],0,1))
            {reason="Invalid volumetric bounds or scattering albedo";return false;}
            foreach(var light in lights)if(light!=null&&light.enabled&&!light.Validate(out reason))return false;
            return true;
        }
    }

    [Serializable]
    public sealed class VolumetricSpotLight
    {
        public bool enabled=true;
        public Vector3 position;
        public Quaternion rotation=Quaternion.identity;
        [Min(.001f)] public float range=10;
        [Range(0,179)] public float innerAngle=25;
        [Range(.1f,179)] public float outerAngle=40;
        public Vector3 linearRadiance=Vector3.one*10;
        [Range(1,8)] public float falloffExponent=1;
        public SceneLightShadowInput shadow=new SceneLightShadowInput();

        internal bool Validate(out string reason)
        {
            reason=null;float norm=rotation.x*rotation.x+rotation.y*rotation.y+rotation.z*rotation.z+rotation.w*rotation.w;
            if(!FogVolumeSettings.Position(position)||!FogVolumeSettings.Range(norm,1e-8f,1e8f)||
                !FogVolumeSettings.Range(range,.001f,1e4f)||!FogVolumeSettings.Range(outerAngle,.1f,179)||!FogVolumeSettings.Range(innerAngle,0,outerAngle)||
                !FogVolumeSettings.Range(falloffExponent,1,8))
            {reason="Invalid volumetric spot geometry or attenuation";return false;}
            for(int i=0;i<3;i++)if(!FogVolumeSettings.Range(linearRadiance[i],0,65504)){reason="Invalid linear volumetric radiance";return false;}
            reason=SceneLightShadowAtlas.ValidateLight(ShadowLight());return reason==null;
        }
        internal SceneDecalLight ShadowLight()=>new SceneDecalLight{shape=SceneDecalLightShape.Spot,position=position,rotation=rotation.normalized,range=range,spotInnerAngle=innerAngle,spotOuterAngle=outerAngle,shadow=shadow};
    }
}
