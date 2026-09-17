using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Explicit art-directed focus slab from caller-owned world bounds.
    /// No scene search, readback, history, camera mutation or settings mutation.</summary>
    public static class BokehFocusRange
    {
        /// <summary>Returns positive eye-depth near/far, in the same units as the
        /// supplied affine world-to-camera matrix (visible camera space is -Z).
        /// Bounds must conservatively include the CURRENT deformation, including
        /// shader displacement. Invalid/behind-camera bounds fail, not clamp.</summary>
        public static bool TryFit(Matrix4x4 worldToCamera,IReadOnlyList<Bounds> worldBounds,
            float padding,out Vector2 range)
        {
            range=default;
            if(worldBounds==null||worldBounds.Count==0||!Finite(padding)||padding<0||padding>100000)return false;
            for(int i=0;i<16;i++)if(!Finite(worldToCamera[i]))return false;
            if(worldToCamera.m30!=0||worldToCamera.m31!=0||worldToCamera.m32!=0||worldToCamera.m33!=1)return false;
            double x=worldToCamera.m20,y=worldToCamera.m21,z=worldToCamera.m22;
            if(x*x+y*y+z*z<1e-12)return false;
            double near=double.PositiveInfinity,far=double.NegativeInfinity;
            for(int i=0;i<worldBounds.Count;i++)
            {
                var b=worldBounds[i];var c=b.center;var e=b.extents;
                if(!Finite(c.x)||!Finite(c.y)||!Finite(c.z)||!Finite(e.x)||!Finite(e.y)||!Finite(e.z)||e.x<0||e.y<0||e.z<0)return false;
                double center=-(x*c.x+y*c.y+z*c.z+worldToCamera.m23);
                double radius=Math.Abs(x)*e.x+Math.Abs(y)*e.y+Math.Abs(z)*e.z;
                near=Math.Min(near,center-radius-padding);far=Math.Max(far,center+radius+padding);
            }
            if(near<.001||far>100000||far<near)return false;
            // Float conversion must not round the fitted slab inward. One ULP
            // of outward slack also covers ordinary bound arithmetic rounding.
            float lo=(float)near,hi=(float)far;
            lo=BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(lo)-1);
            hi=BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(hi)+1);
            if(lo<.001f||hi>100000)return false;
            range=new Vector2(lo,hi);return true;
        }
        private static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
    }
}
