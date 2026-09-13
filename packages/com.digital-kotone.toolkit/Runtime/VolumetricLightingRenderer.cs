using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Finite-medium single scattering from actual depth and independently owned dynamic shadow maps.</summary>
    public sealed class VolumetricLightingRenderer : IDisposable
    {
        public readonly struct Frame
        {
            private readonly VolumetricLightingRenderer owner;
            private readonly uint generation;
            public readonly RenderTexture color, scattering, shadowAtlas;
            internal Frame(VolumetricLightingRenderer renderer)
            {owner=renderer;generation=renderer.generation;color=renderer.color;scattering=renderer.scattering;shadowAtlas=renderer.shadows.Atlas;}
            public bool IsCurrent=>owner!=null&&owner.hasFrame&&owner.generation==generation&&owner.Created;
        }
        // Reuse the tested camera snapshot/unprojection contract, without enabling its fog media.
        private readonly FogVolumeSettings viewSettings=new FogVolumeSettings{enabled=true};
        private readonly SceneLightShadowAtlas shadows=new SceneLightShadowAtlas("Toolkit volumetric dynamic shadow atlas");
        private readonly List<SceneDecalLight> shadowLights=new List<SceneDecalLight>();
        private readonly List<Material> materials=new List<Material>();
        private readonly List<VolumetricSpotLight> lights=new List<VolumetricSpotLight>();
        private Material composite;
        private RenderTexture color,scattering;
        private uint generation;
        private bool hasFrame;
        public int DrawCalls {get;private set;}
        public int LightCount=>lights.Count;
        public int TargetCount=>Created?2+(shadows.Atlas!=null?1:0):0;
        public int ShadowMapCount=>shadows.MapCount;
        public int ShadowCasterDrawCalls=>shadows.CasterDrawCalls;
        public string UnavailableReason {get;private set;}
        private bool Created=>color!=null&&color.IsCreated()&&scattering!=null&&scattering.IsCreated()&&(shadows.Atlas==null||shadows.Atlas.IsCreated());
        public bool TryGetFrame(out Frame frame){frame=default;if(!hasFrame||!Created)return false;frame=new Frame(this);return true;}

        public bool TryRender(RenderTexture source,FogVolumeDepth depth,Camera camera,VolumetricLightingSettings settings,out Frame frame,RenderTexture protection=null)
        {
            frame=default;generation++;hasFrame=false;DrawCalls=0;UnavailableReason=null;
            if(settings==null||!settings.enabled){Release();return false;}
            if(!settings.Validate(out var reason))return Fail(reason);
            if(!Valid(source)||!Valid(depth.texture)||source.sRGB||depth.texture.sRGB||source==depth.texture||Owns(source)||Owns(depth.texture)||
                source.width!=depth.texture.width||source.height!=depth.texture.height||
                (source.format!=RenderTextureFormat.ARGBFloat&&source.format!=RenderTextureFormat.ARGBHalf&&source.format!=RenderTextureFormat.RGB111110Float)||
                (depth.encoding!=FogDepthEncoding.LinearEye&&depth.encoding!=FogDepthEncoding.Device)||
                (depth.encoding==FogDepthEncoding.LinearEye&&depth.texture.format!=RenderTextureFormat.RFloat&&depth.texture.format!=RenderTextureFormat.RHalf)||
                (depth.encoding==FogDepthEncoding.Device&&depth.texture.format!=RenderTextureFormat.RFloat&&depth.texture.format!=RenderTextureFormat.Depth)||
                (protection!=null&&(!Valid(protection)||protection.sRGB||Owns(protection)||protection==source||protection==depth.texture||protection.width!=source.width||protection.height!=source.height||
                    (protection.format!=RenderTextureFormat.R8&&protection.format!=RenderTextureFormat.RFloat))))
                return Fail("Volumetric lighting requires distinct matching linear HDR, depth and optional protection targets");
            if(!FogVolumeBinding.TryCreate(viewSettings,camera,source.width,source.height,out var view,out reason))return Fail(reason);
            var shader=Resources.Load<Shader>("VolumetricLighting");
            if(shader==null||!shader.isSupported||!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat,FormatUsage.Render)||!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat,FormatUsage.Sample))
                return Fail("Volumetric shader or float render/sample targets unavailable");
            var saved=RenderTexture.active;
            try
            {
                lights.Clear();shadowLights.Clear();
                foreach(var light in settings.lights)if(light!=null&&light.enabled&&light.linearRadiance!=Vector3.zero)
                {lights.Add(light);shadowLights.Add(light.ShadowLight());}
                if(Owns(RenderTexture.active))RenderTexture.active=null;
                if(!shadows.Prepare(shadowLights,settings.shadows,false,out reason))return Fail(reason);
                if(!Created||color.width!=source.width||color.height!=source.height)
                {ReleaseTargets();color=Allocate(source.width,source.height,"HDR output");scattering=Allocate(source.width,source.height,"single scattering");}
                while(materials.Count<lights.Count)materials.Add(new Material(shader){hideFlags=HideFlags.HideAndDontSave});
                while(materials.Count>lights.Count){int last=materials.Count-1;UnityEngine.Object.Destroy(materials[last]);materials.RemoveAt(last);}
                if(composite==null)composite=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                void Bind(Material material)
                {
                    view.Apply(material);material.SetTexture("_VolumeDepth",depth.texture);material.SetTexture("_VolumeProtection",protection);
                    material.SetVector("_VolumeInput",new Vector4((int)depth.encoding,protection!=null?1:0,settings.affectSky?1:0,settings.attenuateBackground?1:0));
                    material.SetVector("_MediumCenter",settings.mediumCenter);material.SetVector("_MediumHalfSize",settings.mediumHalfSize);
                    material.SetVector("_MediumParameters",new Vector4(settings.extinction,settings.anisotropy,settings.samplesPerLight,0));
                    material.SetVector("_MediumAlbedo",settings.scatteringAlbedo);
                }
                var commands=new CommandBuffer{name="Toolkit volumetric dynamic source shadows"};
                try{shadows.Record(commands);Graphics.ExecuteCommandBuffer(commands);}finally{commands.Release();}
                RenderTexture.active=scattering;GL.Clear(false,true,Color.clear);
                for(int i=0;i<lights.Count;i++)
                {
                    var material=materials[i];var light=lights[i];Bind(material);shadows.Bind(material);shadows.BindSingle(material,i);
                    var forward=light.rotation.normalized*Vector3.forward;
                    material.SetVector("_VolumeLightPositionRange",new Vector4(light.position.x,light.position.y,light.position.z,light.range));
                    material.SetVector("_VolumeLightDirectionOuter",new Vector4(forward.x,forward.y,forward.z,Mathf.Cos(light.outerAngle*Mathf.Deg2Rad*.5f)));
                    material.SetVector("_VolumeLightRadianceInner",new Vector4(light.linearRadiance.x,light.linearRadiance.y,light.linearRadiance.z,Mathf.Cos(light.innerAngle*Mathf.Deg2Rad*.5f)));
                    material.SetFloat("_VolumeFalloff",light.falloffExponent);Graphics.Blit(source,scattering,material,0);DrawCalls++;
                }
                Bind(composite);composite.SetTexture("_VolumeScattering",scattering);Graphics.Blit(source,color,composite,1);DrawCalls++;
                hasFrame=true;frame=new Frame(this);return true;
            }
            catch(Exception error){return Fail("Volumetric render failed: "+error.GetType().Name);}
            finally{RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;}
        }
        private static bool Valid(RenderTexture t)=>t!=null&&t.IsCreated()&&t.antiAliasing==1&&!t.useMipMap&&!t.useDynamicScale&&t.dimension==TextureDimension.Tex2D&&t.volumeDepth==1;
        private bool Owns(RenderTexture t)=>t!=null&&(t==color||t==scattering||t==shadows.Atlas);
        private bool Fail(string reason){Release();UnavailableReason=reason;return false;}
        private static RenderTexture Allocate(int width,int height,string label)
        {
            var t=new RenderTexture(width,height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){name="Toolkit volumetric "+label,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave};
            t.Create();if(!t.IsCreated()||t.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat){t.Release();UnityEngine.Object.Destroy(t);throw new InvalidOperationException("Volumetric target allocation failed");}return t;
        }
        private void ReleaseTargets(){if(Owns(RenderTexture.active))RenderTexture.active=null;foreach(var t in new[]{color,scattering})if(t!=null){t.Release();UnityEngine.Object.Destroy(t);}color=scattering=null;hasFrame=false;}
        private void Release(){ReleaseTargets();shadows.Dispose();foreach(var material in materials)UnityEngine.Object.Destroy(material);materials.Clear();lights.Clear();shadowLights.Clear();if(composite!=null)UnityEngine.Object.Destroy(composite);composite=null;DrawCalls=0;}
        public void Dispose(){generation++;Release();}
    }
}
