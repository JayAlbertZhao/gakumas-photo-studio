using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public enum FogDepthEncoding { LinearEye, Device }
    public readonly struct FogVolumeDepth
    {
        public readonly RenderTexture texture;
        public readonly FogDepthEncoding encoding;
        public FogVolumeDepth(RenderTexture texture,FogDepthEncoding encoding=FogDepthEncoding.LinearEye){this.texture=texture;this.encoding=encoding;}
    }

    /// <summary>Explicit single-depth opaque fog resolve. Forward transparency consumes the same binding separately.</summary>
    public sealed class FogVolumeRenderer : IDisposable
    {
        public readonly struct Frame
        {
            private readonly FogVolumeRenderer _owner;
            private readonly uint _generation;
            public readonly RenderTexture color, transfer;
            internal Frame(FogVolumeRenderer owner){_owner=owner;_generation=owner._generation;color=owner._color;transfer=owner._transfer;}
            public bool IsCurrent=>_owner!=null&&_owner._hasFrame&&_owner._generation==_generation&&_owner.Created;
        }
        private RenderTexture _color,_transfer;
        private Material _material;
        private uint _generation;
        private bool _hasFrame;
        public int TargetCount=>Created?2:0;
        public int DrawCalls { get; private set; }
        public string UnavailableReason { get; private set; }
        private bool Created=>_color!=null&&_color.IsCreated()&&_transfer!=null&&_transfer.IsCreated();
        public bool TryGetFrame(out Frame frame){frame=default;if(!_hasFrame||!Created)return false;frame=new Frame(this);return true;}
        public bool TryRender(RenderTexture source,FogVolumeDepth depth,FogVolumeBinding binding,out Frame frame,RenderTexture protection=null)
        {
            frame=default;_generation++;_hasFrame=false;DrawCalls=0;UnavailableReason=null;
            if(binding==null){Release();return false;}
            if(!Valid(source)||!Valid(depth.texture)||source.sRGB||depth.texture.sRGB||!Matches(source,binding)||!Matches(depth.texture,binding)||
                Owns(source)||Owns(depth.texture)||source==depth.texture||
                (source.format!=RenderTextureFormat.ARGBFloat&&source.format!=RenderTextureFormat.ARGBHalf&&source.format!=RenderTextureFormat.RGB111110Float)||
                (depth.encoding!=FogDepthEncoding.LinearEye&&depth.encoding!=FogDepthEncoding.Device)||
                (depth.encoding==FogDepthEncoding.LinearEye&&depth.texture.format!=RenderTextureFormat.RFloat&&depth.texture.format!=RenderTextureFormat.RHalf)||
                (depth.encoding==FogDepthEncoding.Device&&depth.texture.format!=RenderTextureFormat.Depth&&depth.texture.format!=RenderTextureFormat.RFloat)||
                (protection!=null&&(!Valid(protection)||protection.sRGB||!Matches(protection,binding)||Owns(protection)||protection==source||protection==depth.texture||
                    (protection.format!=RenderTextureFormat.R8&&protection.format!=RenderTextureFormat.RFloat))))
                return Fail("Fog needs distinct matching full-resolution linear HDR/depth/protection targets");
            var shader=Resources.Load<Shader>("FogVolume");
            if(shader==null||!shader.isSupported||!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat,FormatUsage.Render)||!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat,FormatUsage.Sample))
                return Fail("Fog shader or float render/sample targets unavailable");
            var saved=RenderTexture.active;
            try
            {
                if(!Created||_color.width!=source.width||_color.height!=source.height)
                {ReleaseTargets();_color=Allocate(source.width,source.height,"HDR color");_transfer=Allocate(source.width,source.height,"radiance transmittance");}
                if(_material==null)_material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                binding.Apply(_material);_material.SetTexture("_FogDepth",depth.texture);
                _material.SetTexture("_FogProtection",protection);_material.SetVector("_FogInput",new Vector4((int)depth.encoding,protection!=null?1:0,0,0));
                Graphics.Blit(source,_transfer,_material,0);DrawCalls++;
                _material.SetTexture("_FogTransfer",_transfer);Graphics.Blit(source,_color,_material,1);DrawCalls++;
                _hasFrame=true;frame=new Frame(this);return true;
            }
            catch(Exception e){return Fail("Fog render failed: "+e.GetType().Name);}
            finally{RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;}
        }
        private static bool Matches(RenderTexture t,FogVolumeBinding b)=>t.width==b.Width&&t.height==b.Height;
        private static bool Valid(RenderTexture t)=>t!=null&&t.IsCreated()&&t.antiAliasing==1&&!t.useDynamicScale&&!t.useMipMap&&t.dimension==TextureDimension.Tex2D&&t.volumeDepth==1;
        private bool Owns(RenderTexture t)=>t!=null&&(t==_color||t==_transfer);
        private bool Fail(string reason){Release();UnavailableReason=reason;return false;}
        private static RenderTexture Allocate(int w,int h,string name)
        {
            var t=new RenderTexture(w,h,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){name="Toolkit fog "+name,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave};
            t.Create();if(!t.IsCreated()||t.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat){t.Release();UnityEngine.Object.Destroy(t);throw new InvalidOperationException("Fog allocation failed");}return t;
        }
        private void ReleaseTargets(){if(Owns(RenderTexture.active))RenderTexture.active=null;foreach(var t in new[]{_color,_transfer})if(t!=null){t.Release();UnityEngine.Object.Destroy(t);}_color=_transfer=null;_hasFrame=false;}
        private void Release(){ReleaseTargets();if(_material!=null)UnityEngine.Object.Destroy(_material);_material=null;DrawCalls=0;}
        public void Dispose(){_generation++;Release();}
    }
}
