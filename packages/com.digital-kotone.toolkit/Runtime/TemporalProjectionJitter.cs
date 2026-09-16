using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Caller-owned offscreen projection jitter. No Camera/global mutation,
    /// implicit clock, history ownership or default application opt-in.</summary>
    public static class TemporalProjectionJitter
    {
        public readonly struct Sample
        {
            public readonly Matrix4x4 projection;
            // Actual texture-raster displacement and its opposite correction.
            // Frame/Scene TAA sample current at uv-correctionUv.
            public readonly Vector2 rasterOffsetUv,correctionUv;
            internal Sample(Matrix4x4 p,Vector2 displacement)
            {projection=p;rasterOffsetUv=displacement;correctionUv=-displacement;}
        }

        /// <summary>Eight centered Halton(2,3) samples, in camera pixel axes.
        /// Explicit phase wraps; reset phase and histories together on a cut.</summary>
        public static Vector2 Offset(uint phase)
        {
            uint index=phase%8+1;
            return new Vector2(RadicalInverse(index,2)-.4453125f,RadicalInverse(index,3)-.5f);
        }
        private static float RadicalInverse(uint index,uint radix)
        {float result=0,scale=1;while(index!=0){scale/=radix;result+=(index%radix)*scale;index/=radix;}return result;}
        private static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);

        /// <summary>Translate clip x/y by w-scaled subpixel offsets. Supports
        /// perspective/orthographic/off-axis projections without assuming m02/m12.
        /// Must run on Unity's main thread; GPU projection conversion describes
        /// render-into-texture, never a backbuffer. Offset is in camera pixel axes,
        /// not already flipped texture UV. Consumers receive the measured UV sign.</summary>
        public static bool TryCreate(Matrix4x4 baseProjection,Vector2Int rasterSize,
            Vector2 cameraPixelOffset,out Sample sample)
        {
            sample=default;
            if(rasterSize.x<1||rasterSize.y<1||rasterSize.x>16384||rasterSize.y>16384||
                !Finite(cameraPixelOffset.x)||!Finite(cameraPixelOffset.y)||
                Mathf.Abs(cameraPixelOffset.x)>.5f||Mathf.Abs(cameraPixelOffset.y)>.5f)return false;
            for(int i=0;i<16;i++)if(!Finite(baseProjection[i]))return false;
            float determinant=baseProjection.determinant;
            if(!Finite(determinant)||Mathf.Abs(determinant)<1e-20f)return false;
            if(cameraPixelOffset.x==0&&cameraPixelOffset.y==0){sample=new Sample(baseProjection,Vector2.zero);return true;}
            var p=baseProjection;var w=baseProjection.GetRow(3);
            p.SetRow(0,baseProjection.GetRow(0)+w*(2*cameraPixelOffset.x/rasterSize.x));
            p.SetRow(1,baseProjection.GetRow(1)+w*(2*cameraPixelOffset.y/rasterSize.y));
            var gpuBase=GL.GetGPUProjectionMatrix(baseProjection,true);
            var gpuJittered=GL.GetGPUProjectionMatrix(p,true);var gpuW=gpuBase.GetRow(3);
            int pivot=0;for(int c=1;c<4;c++)if(Mathf.Abs(gpuW[c])>Mathf.Abs(gpuW[pivot]))pivot=c;
            if(Mathf.Abs(gpuW[pivot])<1e-20f)return false;
            var dx=gpuJittered.GetRow(0)-gpuBase.GetRow(0);var dy=gpuJittered.GetRow(1)-gpuBase.GetRow(1);
            float x=dx[pivot]/gpuW[pivot],y=dy[pivot]/gpuW[pivot];
            for(int c=0;c<4;c++)
                if(gpuJittered[3,c]!=gpuBase[3,c]||!Finite(dx[c])||!Finite(dy[c])||
                    Mathf.Abs(dx[c]-x*gpuW[c])>1e-6f||Mathf.Abs(dy[c]-y*gpuW[c])>1e-6f)return false;
            var offset=new Vector2(x,y*(SystemInfo.graphicsUVStartsAtTop?-1:1))*.5f;
            if(!Finite(offset.x)||!Finite(offset.y))return false;
            sample=new Sample(p,offset);return true;
        }
    }
}
