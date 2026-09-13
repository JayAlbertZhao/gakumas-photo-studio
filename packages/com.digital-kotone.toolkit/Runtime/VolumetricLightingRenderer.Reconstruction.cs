using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class VolumetricLightingRenderer
    {
        private RenderTexture lowScattering,depthRange,reconstructionMask;
        private Material reconstruction;
        private readonly List<Material> reintegration=new List<Material>();
        public int ReintegrationDrawCalls {get;private set;}
        /// <summary>Owned color/scattering/reconstruction attachments; excludes inputs and shadow atlas/depth.</summary>
        public long TargetBytes=>Created?(long)color.width*color.height*32+
            (lowScattering!=null?(long)lowScattering.width*lowScattering.height*24+(long)color.width*color.height:0):0;
        private bool ReducedTargetsCreated=>lowScattering==null?depthRange==null&&reconstructionMask==null:
            lowScattering.IsCreated()&&depthRange!=null&&depthRange.IsCreated()&&reconstructionMask!=null&&reconstructionMask.IsCreated();
        private static int ReducedSize(int value,VolumetricResolution resolution)=>(value+(int)resolution-1)/(int)resolution;
        private static bool ReducedCapabilities(RenderTexture source,VolumetricLightingSettings settings,out string reason)
        {
            reason=null;int w=ReducedSize(source.width,settings.resolution),h=ReducedSize(source.height,settings.resolution);
            long bytes=(long)source.width*source.height*33+(long)w*h*24;
            if(bytes>(long)settings.reconstruction.maximumTargetMiB*1024*1024)
            {reason="Reduced volumetric targets exceed the explicit memory budget";return false;}
            foreach(var format in new[]{GraphicsFormat.R32G32_SFloat,GraphicsFormat.R8_UNorm})
                if(!SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))
                {reason="Reduced volumetric depth-range or reconstruction-mask targets unavailable";return false;}
            if(!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat,FormatUsage.Blend))
            {reason="Reduced volumetric float blending unavailable";return false;}
            return true;
        }
        private bool RenderReduced(RenderTexture source,FogVolumeDepth depth,Camera camera,VolumetricLightingSettings settings,
            FogVolumeBinding fullView,RenderTexture protection,out string reason)
        {
            reason=null;int width=ReducedSize(source.width,settings.resolution),height=ReducedSize(source.height,settings.resolution);
            var shader=Resources.Load<Shader>("VolumetricReconstruction");
            if(shader==null||!shader.isSupported){reason="Volumetric reconstruction shader unavailable";return false;}
            if(!FogVolumeBinding.TryCreate(viewSettings,camera,width,height,out var lowView,out reason))return false;
            if(lowScattering!=null&&(lowScattering.width!=width||lowScattering.height!=height))ReleaseReduced();
            if(lowScattering==null)
            {
                lowScattering=Allocate(width,height,"low single scattering");
                depthRange=AllocateGuide(width,height,RenderTextureFormat.RGFloat,GraphicsFormat.R32G32_SFloat,"opaque depth range");
                reconstructionMask=AllocateGuide(source.width,source.height,RenderTextureFormat.R8,GraphicsFormat.R8_UNorm,"reintegration mask");
            }
            if(reconstruction==null)reconstruction=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
            while(reintegration.Count<lights.Count)reintegration.Add(new Material(shader){hideFlags=HideFlags.HideAndDontSave});
            while(reintegration.Count>lights.Count){int last=reintegration.Count-1;UnityEngine.Object.Destroy(reintegration[last]);reintegration.RemoveAt(last);}
            void Bind(Material material,bool low)
            {
                (low?lowView:fullView).Apply(material);
                material.SetTexture("_VolumeDepth",low?depthRange:depth.texture);material.SetTexture("_VolumeProtection",low?null:protection);
                material.SetVector("_VolumeInput",new Vector4(low?0:(int)depth.encoding,!low&&protection!=null?1:0,settings.affectSky?1:0,settings.attenuateBackground?1:0));
                material.SetVector("_MediumCenter",settings.mediumCenter);material.SetVector("_MediumHalfSize",settings.mediumHalfSize);
                material.SetVector("_MediumParameters",new Vector4(settings.extinction,settings.anisotropy,settings.samplesPerLight,0));material.SetVector("_MediumAlbedo",settings.scatteringAlbedo);
            }
            void BindReconstruction(Material material,bool readRange,bool readScatter,bool readMask)
            {
                Bind(material,false);
                material.SetVector("_VolumeLowSize",new Vector4(width,height,1f/width,1f/height));
                var quality=settings.reconstruction;
                material.SetVector("_VolumeReconstructionTolerance",new Vector4(quality.depthAbsoluteTolerance,quality.depthRelativeTolerance,quality.radianceAbsoluteTolerance,quality.radianceRelativeTolerance));
                material.SetTexture("_VolumeDepthRange",readRange?depthRange:Texture2D.blackTexture);
                material.SetTexture("_VolumeLowScattering",readScatter?lowScattering:Texture2D.blackTexture);
                material.SetTexture("_VolumeReintegration",readMask?reconstructionMask:Texture2D.blackTexture);
            }
            void BindLight(Material material,int index)
            {
                var light=lights[index];shadows.Bind(material);shadows.BindSingle(material,index);var forward=light.rotation.normalized*Vector3.forward;
                material.SetVector("_VolumeLightPositionRange",new Vector4(light.position.x,light.position.y,light.position.z,light.range));
                material.SetVector("_VolumeLightDirectionOuter",new Vector4(forward.x,forward.y,forward.z,Mathf.Cos(light.outerAngle*Mathf.Deg2Rad*.5f)));
                material.SetVector("_VolumeLightRadianceInner",new Vector4(light.linearRadiance.x,light.linearRadiance.y,light.linearRadiance.z,Mathf.Cos(light.innerAngle*Mathf.Deg2Rad*.5f)));
                material.SetFloat("_VolumeFalloff",light.falloffExponent);
            }
            var commands=new CommandBuffer{name="Toolkit reduced volumetric dynamic source shadows"};
            try{shadows.Record(commands);Graphics.ExecuteCommandBuffer(commands);}finally{commands.Release();}
            BindReconstruction(reconstruction,false,false,false);Graphics.Blit(source,depthRange,reconstruction,0);DrawCalls++;
            RenderTexture.active=lowScattering;GL.Clear(false,true,Color.clear);
            for(int i=0;i<lights.Count;i++)
            {
                // The original integration pass executes at genuinely reduced dimensions.
                Bind(materials[i],true);BindLight(materials[i],i);Graphics.Blit(source,lowScattering,materials[i],0);DrawCalls++;
            }
            BindReconstruction(reconstruction,true,true,false);Graphics.Blit(source,reconstructionMask,reconstruction,1);DrawCalls++;
            BindReconstruction(reconstruction,true,true,true);Graphics.Blit(source,scattering,reconstruction,2);DrawCalls++;
            for(int i=0;i<lights.Count;i++)
            {
                var material=reintegration[i];BindReconstruction(material,false,false,true);BindLight(material,i);
                Graphics.Blit(source,scattering,material,3);DrawCalls++;ReintegrationDrawCalls++;
            }
            // Transmission is analytic at full depth, never interpolated across opaque edges.
            Bind(composite,false);composite.SetTexture("_VolumeScattering",scattering);Graphics.Blit(source,color,composite,1);DrawCalls++;
            return true;
        }
        private static RenderTexture AllocateGuide(int width,int height,RenderTextureFormat format,GraphicsFormat expected,string label)
        {
            var t=new RenderTexture(width,height,0,format,RenderTextureReadWrite.Linear){name="Toolkit volumetric "+label,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave};
            if(!t.Create()||t.graphicsFormat!=expected){t.Release();UnityEngine.Object.Destroy(t);throw new InvalidOperationException("Volumetric guide allocation failed");}return t;
        }
        private void ReleaseReduced()
        {
            if(RenderTexture.active!=null&&(RenderTexture.active==lowScattering||RenderTexture.active==depthRange||RenderTexture.active==reconstructionMask))RenderTexture.active=null;
            foreach(var t in new[]{lowScattering,depthRange,reconstructionMask})if(t!=null){t.Release();UnityEngine.Object.Destroy(t);}
            lowScattering=depthRange=reconstructionMask=null;
            foreach(var material in reintegration)UnityEngine.Object.Destroy(material);reintegration.Clear();
            if(reconstruction!=null)UnityEngine.Object.Destroy(reconstruction);reconstruction=null;ReintegrationDrawCalls=0;
        }
    }
}
