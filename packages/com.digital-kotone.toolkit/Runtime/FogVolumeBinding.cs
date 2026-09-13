using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Immutable settings/view snapshot shared by opaque resolve and forward materials.</summary>
    public sealed class FogVolumeBinding
    {
        private readonly Vector4[] _spheres = new Vector4[FogVolumeSettings.MaximumSpheres];
        private readonly Vector4[] _colors = new Vector4[FogVolumeSettings.MaximumSpheres];
        private readonly Vector4[] _media = new Vector4[FogVolumeSettings.MaximumSpheres];
        private Vector4 _distance, _distanceColor, _control, _viewParameters;
        private Matrix4x4 _inverse, _worldToView;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int SphereCount => (int)_control.x;
        private FogVolumeBinding() { }

        public static bool TryCreate(FogVolumeSettings settings,Camera camera,int width,int height,out FogVolumeBinding binding,out string reason)
        {
            binding=null;reason=null;
            if(settings==null||!settings.enabled){reason="Fog disabled";return false;}
            if(settings.stepsPerInterval<1||settings.stepsPerInterval>32||!FogVolumeSettings.Range(settings.maximumOpacity,0,1))
            {reason="Invalid fog quality or opacity";return false;}
            if(camera==null||width<1||height<1||camera.stereoEnabled||camera.rect!=new Rect(0,0,1,1)||
                !FogVolumeSettings.Range(camera.nearClipPlane,.0001f,1e6f)||!FogVolumeSettings.Range(camera.farClipPlane,camera.nearClipPlane+.0001f,1e6f))
            {reason="Fog requires a finite full-viewport non-XR camera";return false;}
            var view=camera.worldToCameraMatrix;var vp=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)*view;
            if(!ValidMatrix(view)||!ValidMatrix(vp)||!FogVolumeSettings.Finite(vp.determinant)||Mathf.Abs(vp.determinant)<1e-12f||!ValidMatrix(vp.inverse))
            {reason="Invalid fog view/projection";return false;}
            var result=new FogVolumeBinding{Width=width,Height=height,_inverse=vp.inverse,_worldToView=view};
            bool reversed=SystemInfo.usesReversedZBuffer;
            // clip z on D3D/Metal/Vulkan is 0..1; OpenGL is -1..1.
            var api=SystemInfo.graphicsDeviceType;
            bool gl=api==UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore||api==UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3;
            result._viewParameters=new Vector4(camera.nearClipPlane,camera.farClipPlane,reversed?1:0,gl?-1:0);
            var d=settings.distance;
            if(d!=null&&d.enabled)
            {
                if(!FogVolumeSettings.Range(d.density,0,1e4f)||!FogVolumeSettings.Range(d.startDistance,0,1e6f)||!FogVolumeSettings.Range(d.endDistance,d.startDistance,1e6f)||!FogVolumeSettings.Radiance(d.linearColor))
                {reason="Invalid distance medium";return false;}
                result._distance=new Vector4(d.density,d.startDistance,d.endDistance,d.affectSky?1:0);result._distanceColor=d.linearColor;
            }
            var active=new List<FogVolumeSettings.SphereMedium>();
            if(settings.spheres!=null)foreach(var s in settings.spheres)
            {
                if(s==null||!s.enabled)continue;
                if(!FogVolumeSettings.Position(s.center)||!FogVolumeSettings.Range(s.radius,.0001f,1e6f)||!FogVolumeSettings.Range(s.density,0,1e4f)||!FogVolumeSettings.Radiance(s.linearColor))
                {reason="Invalid sphere medium";return false;}
                if(s.density>0)active.Add(s);
            }
            if(active.Count>FogVolumeSettings.MaximumSpheres){reason="At most eight active sphere media are supported";return false;}
            // Stable arithmetic ordering independent of a caller's array permutation.
            active.Sort(Compare);
            for(int i=0;i<active.Count;i++)
            {
                var s=active[i];result._spheres[i]=new Vector4(s.center.x,s.center.y,s.center.z,s.radius);
                result._colors[i]=s.linearColor;result._media[i]=new Vector4(s.density,s.affectSky?1:0,0,0);
            }
            result._control=new Vector4(active.Count,settings.stepsPerInterval,settings.maximumOpacity,0);
            binding=result;return true;
        }
        private static int Compare(FogVolumeSettings.SphereMedium a,FogVolumeSettings.SphereMedium b)
        {
            int c=a.center.x.CompareTo(b.center.x);if(c!=0)return c;c=a.center.y.CompareTo(b.center.y);if(c!=0)return c;
            c=a.center.z.CompareTo(b.center.z);if(c!=0)return c;c=a.radius.CompareTo(b.radius);if(c!=0)return c;
            c=a.density.CompareTo(b.density);if(c!=0)return c;
            for(int i=0;i<3;i++){c=a.linearColor[i].CompareTo(b.linearColor[i]);if(c!=0)return c;}
            return a.affectSky.CompareTo(b.affectSky);
        }
        private static bool ValidMatrix(Matrix4x4 m){for(int i=0;i<16;i++)if(!FogVolumeSettings.Range(m[i],-1e8f,1e8f))return false;return true;}

        /// <summary>Bind an owned material implementing FogVolume.hlsl. Never changes shader globals.</summary>
        public void Apply(Material material)
        {
            if(material==null)throw new ArgumentNullException(nameof(material));
            material.SetMatrix("_FogInverseVP",_inverse);material.SetMatrix("_FogWorldToView",_worldToView);
            material.SetVector("_FogViewParameters",_viewParameters);
            material.SetVector("_FogScreen",new Vector4(Width,Height,1f/Width,1f/Height));
            material.SetVector("_FogDistance",_distance);material.SetVector("_FogDistanceColor",_distanceColor);
            material.SetVector("_FogControl",_control);material.SetVectorArray("_FogSpheres",_spheres);
            material.SetVectorArray("_FogColors",_colors);material.SetVectorArray("_FogMedia",_media);
        }
    }
}
