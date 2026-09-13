using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>One independent color transform after HDR effects, before UI and output transfer encoding.</summary>
    public sealed class ColorGradingRenderer : IDisposable
    {
        public readonly struct Frame
        {
            private readonly ColorGradingRenderer owner;
            private readonly uint generation;
            public readonly RenderTexture color;
            internal Frame(ColorGradingRenderer renderer) { owner=renderer;generation=renderer.generation;color=renderer.output; }
            public bool IsCurrent => owner!=null && owner.valid && owner.generation==generation && color!=null && color.IsCreated();
        }
        private Material material;
        private RenderTexture output;
        private uint generation;
        private bool valid;
        public string UnavailableReason { get; private set; }
        public int DrawCalls { get; private set; }
        public bool TryGetFrame(out Frame frame) { frame=default;if(!valid || output==null || !output.IsCreated())return false;frame=new Frame(this);return true; }
        public bool TryRender(RenderTexture source,ColorGradingLut lut,out Frame frame)
        {
            generation++;valid=false;DrawCalls=0;frame=default;UnavailableReason=null;
            if(source==null || !source.IsCreated() || source==output || source.sRGB || source.antiAliasing!=1 || source.useDynamicScale || source.useMipMap || source.dimension!=TextureDimension.Tex2D || source.volumeDepth!=1 ||
               (source.format!=RenderTextureFormat.ARGBFloat && source.format!=RenderTextureFormat.ARGBHalf && source.format!=RenderTextureFormat.RGB111110Float) || lut==null || !lut.IsValid)
                return Fail("Created external linear HDR and a live authored color LUT required");
            var shader=Resources.Load<Shader>("AuthoredColorLut");
            if(shader==null || !shader.isSupported || !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat,FormatUsage.Render)) return Fail("Color grading shader/float target unavailable");
            var active=RenderTexture.active;
            try
            {
                if(output==null || !output.IsCreated() || output.width!=source.width || output.height!=source.height)
                {
                    ReleaseTarget();output=new RenderTexture(source.width,source.height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){name="Toolkit authored color output",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};output.Create();
                    if(!output.IsCreated() || output.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat) return Fail("Color grading target allocation failed");
                }
                if(material==null) material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                material.SetTexture("_AuthoredLut",lut.Texture);
                material.SetVector("_LutParameters",new Vector4(lut.Size,lut.MaximumInput,(int)lut.Domain,1/Mathf.Log(1+lut.MaximumInput,2)));
                Graphics.Blit(source,output,material,0);DrawCalls=1;valid=true;frame=new Frame(this);return true;
            }
            catch(Exception error) {return Fail("Color grading failed: "+error.GetType().Name);}
            finally {RenderTexture.active=active!=null && active.IsCreated()?active:null;}
        }
        private bool Fail(string reason) {Dispose();UnavailableReason=reason;return false;}
        private void ReleaseTarget() {if(output!=null){if(RenderTexture.active==output)RenderTexture.active=null;output.Release();UnityEngine.Object.Destroy(output);}output=null;valid=false;}
        public void Dispose() {generation++;ReleaseTarget();if(material!=null)UnityEngine.Object.Destroy(material);material=null;DrawCalls=0;}
    }
}
