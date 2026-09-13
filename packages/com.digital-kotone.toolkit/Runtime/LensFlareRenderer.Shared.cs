using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class LensFlareRenderer
    {
        private Material sharedMaterial;
        internal RenderTexture SharedVisibility => visibility;
        internal bool SharedCreated => visibility!=null&&visibility.IsCreated()&&emitterBuffer!=null&&elementBuffer!=null;
        // Internal producer path: never allocates artifacts or a private full HDR output.
        internal bool PrepareShared(RenderTexture source,FogVolumeDepth depth,Camera camera,LensFlareSettings settings,
            double time,RenderTexture protection,out string reason)
        {
            generation++;hasFrame=false;reason=null;DrawCalls=0;
            if(settings==null||!settings.enabled){Release();return true;}
            if(!settings.Validate(out reason))return false;
            if(double.IsNaN(time)||double.IsInfinity(time)||Math.Abs(time)>1e9){reason="Invalid joint optical time";return false;}
            if(settings.atlas!=null&&(settings.atlas.mipmapCount!=1||settings.atlas.width>4096||settings.atlas.height>4096||
                GraphicsFormatUtility.IsSRGBFormat(settings.atlas.graphicsFormat)||!SystemInfo.IsFormatSupported(settings.atlas.graphicsFormat,FormatUsage.Sample)))
            {reason="Joint optical atlas requires a linear non-mipmapped texture up to 4096 square";return false;}
            if(!FogVolumeBinding.TryCreate(viewSettings,camera,source.width,source.height,out var view,out reason))return false;
            Prepare(camera,source.width,source.height,settings,time);
            if(elements.Count==0){Release();return true;}
            var shader=Resources.Load<Shader>("LensFlare");var shared=Resources.Load<Shader>("HeavyFxOptics");
            if(shader==null||!shader.isSupported||shared==null||!shared.isSupported||!SystemInfo.supportsInstancing)
            {reason="Joint optical shaders or instancing unavailable";return false;}
            if(visibility==null||!visibility.IsCreated()||visibility.width!=emitters.Count)
            {ReleaseTargets();visibility=Allocate(emitters.Count,1,RenderTextureFormat.RFloat,"joint source visibility");}
            Upload(ref emitterBuffer,emitters);Upload(ref elementBuffer,elements);
            if(material==null)material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
            if(sharedMaterial==null)sharedMaterial=new Material(shared){hideFlags=HideFlags.HideAndDontSave};
            view.Apply(material);material.SetBuffer("_FlareEmitters",emitterBuffer);
            material.SetTexture("_FlareDepth",depth.texture);
            material.SetVector("_FlareInput",new Vector4((int)depth.encoding,protection!=null?1:0,source.width,source.height));
            Graphics.Blit(source,visibility,material,0);DrawCalls++;
            sharedMaterial.SetBuffer("_FlareEmitters",emitterBuffer);sharedMaterial.SetBuffer("_FlareElements",elementBuffer);
            sharedMaterial.SetTexture("_FlareVisibility",visibility);
            sharedMaterial.SetTexture("_FlareAtlas",settings.atlas!=null?settings.atlas:Texture2D.whiteTexture);
            return true;
        }
        internal void DrawShared(RenderTexture target,Action<Material> bind)
        {
            if(elements.Count==0)return;
            bind(sharedMaterial);sharedMaterial.SetVector("_FlareInput",new Vector4(0,0,target.width,target.height));
            var commands=new CommandBuffer{name="Toolkit joint optical elements"};
            try
            {
                commands.SetRenderTarget(target);commands.SetViewport(new Rect(0,0,target.width,target.height));
                commands.DrawProcedural(Matrix4x4.identity,sharedMaterial,0,MeshTopology.Triangles,6,elements.Count);
                Graphics.ExecuteCommandBuffer(commands);DrawCalls++;
            }
            finally{commands.Release();}
        }
        private void ReleaseShared(){if(sharedMaterial!=null)UnityEngine.Object.Destroy(sharedMaterial);sharedMaterial=null;}
    }
}
