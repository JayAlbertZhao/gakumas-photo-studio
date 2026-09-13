using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Owned immutable baked grade. Bake on Unity's main thread; share read-only across renderers.</summary>
    public sealed class ColorGradingLut : IDisposable
    {
        private Texture3D texture;
        private Color[] values;
        public int Size { get; private set; }
        public ColorLutDomain Domain { get; private set; }
        public float MaximumInput { get; private set; }
        public string ProfileJson { get; private set; }
        public bool IsValid => texture!=null && values!=null && texture.width==Size && texture.height==Size && texture.depth==Size && texture.format==TextureFormat.RGBAFloat;
        /// <summary>Borrowed GPU texture. Do not mutate or destroy it.</summary>
        public Texture3D Texture => texture;
        public Color[] CopyValues() { if(!IsValid) throw new ObjectDisposedException(nameof(ColorGradingLut));return (Color[])values.Clone(); }
        private ColorGradingLut() { }
        public static ColorGradingLut Bake(ColorGradingProfile profile)
        {
            if(profile==null || !profile.IsValid) throw new ArgumentException("Invalid color grading profile");
            if(!SystemInfo.supports3DTextures || !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat)) throw new NotSupportedException("Float3D color LUT unavailable");
            var snapshot=ColorGradingProfile.FromJson(profile.ToJson(false));
            var result=new ColorGradingLut{Size=snapshot.size,Domain=snapshot.domain,MaximumInput=snapshot.maximumInput,ProfileJson=snapshot.ToJson(false)};
            try
            {
                int size=result.Size;result.values=new Color[size*size*size];var transform=new ColorGradingMath.Transform(snapshot);
                var axis=new float[size];for(int i=0;i<size;i++)axis[i]=ColorGradingMath.DecodeDomain(i/(float)(size-1),result.Domain,result.MaximumInput);
                for(int b=0;b<size;b++)for(int g=0;g<size;g++)for(int r=0;r<size;r++)
                    result.values[r+size*(g+size*b)]=transform.Evaluate(new Color(axis[r],axis[g],axis[b],1));
                result.texture=new Texture3D(size,size,size,TextureFormat.RGBAFloat,false,true){name="Toolkit authored color LUT",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
                result.texture.SetPixels(result.values);result.texture.Apply(false,true);return result;
            }
            catch {result.Dispose();throw;}
        }
        public void Dispose() { if(texture!=null) UnityEngine.Object.Destroy(texture);texture=null;values=null; }
    }
}
