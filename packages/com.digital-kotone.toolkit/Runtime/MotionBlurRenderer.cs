using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Caller-owned current color and point-sampled visible motion/depth. All UVs use texture coordinates.</summary>
    public readonly struct MotionBlurInput
    {
        public readonly RenderTexture color, motionDepth;
        // Guide RG = current raster UV - previous raster UV; B = current positive view depth.
        // Guide A = 1 for valid correspondence; every other value is protected/passthrough.
        public readonly float sampleInterval;
        // If stable output samples raw at uv-jitter, physical motion is rawMotion + currentJitter - previousJitter.
        public readonly Vector2 jitterDeltaUv, colorToGuideUv;
        // Optional R8 normalized temporal flags, bit4 protects mixed-coordinate NoJitter pixels.
        public readonly RenderTexture noJitterFlags;
        public MotionBlurInput(RenderTexture color,RenderTexture motionDepth,float sampleInterval,
            Vector2 jitterDeltaUv=default,Vector2 colorToGuideUv=default,RenderTexture noJitterFlags=null)
        {this.color=color;this.motionDepth=motionDepth;this.sampleInterval=sampleInterval;this.jitterDeltaUv=jitterDeltaUv;this.colorToGuideUv=colorToGuideUv;this.noJitterFlags=noJitterFlags;}
    }

    /// <summary>Independent current-image motion blur, with no application or private-asset dependency.</summary>
    public sealed class MotionBlurRenderer : IDisposable
    {
        public readonly struct Frame
        {
            private readonly MotionBlurRenderer owner;
            private readonly uint generation;
            public readonly RenderTexture color, tileMaximum, neighborhoodMaximum;
            internal Frame(MotionBlurRenderer owner)
            {this.owner=owner;generation=owner.generation;color=owner.output;tileMaximum=owner.tile;neighborhoodMaximum=owner.neighborhood;}
            public bool IsCurrent => owner!=null && owner.valid && generation==owner.generation && owner.Created;
        }
        private Material material;
        private RenderTexture output,tile,neighborhood;
        private uint generation;
        private bool valid;
        public int DrawCalls {get;private set;}
        public int TargetCount => (output!=null?1:0)+(tile!=null?1:0)+(neighborhood!=null?1:0);
        public string UnavailableReason {get;private set;}
        private bool Created => Live(output) && Live(tile) && Live(neighborhood);
        public bool TryGetFrame(out Frame frame) {frame=default;if(!valid||!Created)return false;frame=new Frame(this);return true;}
        public bool Owns(RenderTexture target) => target!=null && (target==output || target==tile || target==neighborhood);

        public bool TryRender(MotionBlurInput input,MotionBlurSettings settings,out Frame frame)
        {
            generation++;valid=false;DrawCalls=0;UnavailableReason=null;frame=default;
            var source=input.color;var guide=input.motionDepth;
            if(settings==null || !settings.enabled || !settings.IsValid) return Fail("Valid enabled motion blur settings required");
            if(!TextureInput(source) || source.sRGB || Owns(source) ||
               (source.format!=RenderTextureFormat.ARGBFloat && source.format!=RenderTextureFormat.ARGBHalf && source.format!=RenderTextureFormat.RGB111110Float) ||
               !TextureInput(guide) || guide.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat || Owns(guide) || source==guide || guide.width!=source.width || guide.height!=source.height)
                return Fail("Distinct matching current linear HDR and float4 motion/depth required");
            if(!MotionBlurSettings.Range(input.sampleInterval,0,100000) || !Uv(input.jitterDeltaUv) || !Uv(input.colorToGuideUv)) return Fail("Invalid motion interval or UV mapping");
            if(input.noJitterFlags!=null && (!TextureInput(input.noJitterFlags) || input.noJitterFlags.graphicsFormat!=GraphicsFormat.R8_UNorm || input.noJitterFlags.width!=source.width || input.noJitterFlags.height!=source.height || Owns(input.noJitterFlags)))
                return Fail("Current matching linear R8 temporal flags required when supplied");
            var shader=Resources.Load<Shader>("MotionBlur");var format=GraphicsFormat.R32G32B32A32_SFloat;
            if(shader==null || !shader.isSupported || !SystemInfo.IsFormatSupported(format,FormatUsage.Render) || !SystemInfo.IsFormatSupported(format,FormatUsage.Sample)) return Fail("Motion blur float targets/shader unavailable");
            var active=RenderTexture.active;
            try
            {
                int radius=settings.maximumRadiusPixels,tw=(source.width+radius-1)/radius,th=(source.height+radius-1)/radius;
                if(!Created || output.width!=source.width || output.height!=source.height || tile.width!=tw || tile.height!=th)
                {
                    ReleaseTargets();output=Target(source.width,source.height,"Toolkit motion blur HDR output");
                    tile=Target(tw,th,"Toolkit motion blur tile maximum");neighborhood=Target(tw,th,"Toolkit motion blur neighborhood maximum");
                }
                if(material==null) material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                material.SetTexture("_MotionDepth",guide);material.SetTexture("_TileMaximum",tile);material.SetTexture("_NeighborhoodMaximum",neighborhood);
                material.SetTexture("_NoJitterFlags",input.noJitterFlags!=null?(Texture)input.noJitterFlags:Texture2D.blackTexture);
                material.SetVector("_Size",new Vector4(source.width,source.height,1f/source.width,1f/source.height));
                material.SetVector("_TileSize",new Vector4(tw,th,radius,settings.dualDirections?1:0));
                material.SetVector("_MotionMapping",new Vector4(input.jitterDeltaUv.x,input.jitterDeltaUv.y,input.colorToGuideUv.x,input.colorToGuideUv.y));
                material.SetVector("_Filter",new Vector4(settings.HalfDisplacementScale(input.sampleInterval),settings.softDepthExtent,settings.samples,settings.centerWeightDenominator));
                material.SetVector("_Noise",new Vector4(settings.sampleJitter,input.noJitterFlags!=null?1:0,settings.noiseSeed&65535,settings.noiseSeed>>16));
                Graphics.Blit(source,tile,material,0);Graphics.Blit(source,neighborhood,material,1);Graphics.Blit(source,output,material,2);
                DrawCalls=3;valid=true;frame=new Frame(this);return true;
            }
            catch(Exception error){return Fail("Motion blur failed: "+error.GetType().Name);}
            finally {RenderTexture.active=Live(active)?active:null;}
        }
        private static bool Uv(Vector2 value)=>MotionBlurSettings.Range(value.x,-1,1)&&MotionBlurSettings.Range(value.y,-1,1);
        private static bool Live(RenderTexture texture)=>texture!=null&&texture.IsCreated();
        private static bool TextureInput(RenderTexture texture)=>Live(texture)&&texture.dimension==TextureDimension.Tex2D&&texture.volumeDepth==1&&texture.antiAliasing==1&&!texture.useMipMap&&!texture.useDynamicScale;
        private static RenderTexture Target(int width,int height,string name)
        {
            var target=new RenderTexture(width,height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){name=name,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};target.Create();
            if(!target.IsCreated()||target.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat){Release(target);throw new InvalidOperationException("Motion blur float target allocation failed");}return target;
        }
        private bool Fail(string reason){Dispose();UnavailableReason=reason;return false;}
        private static void Release(RenderTexture target){if(target!=null){if(RenderTexture.active==target)RenderTexture.active=null;target.Release();UnityEngine.Object.Destroy(target);}}
        private void ReleaseTargets(){Release(output);Release(tile);Release(neighborhood);output=tile=neighborhood=null;valid=false;}
        public void Dispose(){generation++;ReleaseTargets();if(material!=null)UnityEngine.Object.Destroy(material);material=null;DrawCalls=0;}
    }
}
