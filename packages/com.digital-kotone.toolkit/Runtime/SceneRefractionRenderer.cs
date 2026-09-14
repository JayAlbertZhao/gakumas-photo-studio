using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit faceted-solid optics in a distant cubemap environment. No global state or runtime readback.</summary>
    public sealed class SceneRefractionRenderer : IDisposable
    {
        public readonly struct Frame
        {
            private readonly SceneRefractionRenderer owner;
            private readonly ulong generation;
            public readonly RenderTexture color, unresolvedThroughput, firstEyeDepth;
            internal Frame(SceneRefractionRenderer v)
            {owner=v;generation=v.generation;color=v.color;unresolvedThroughput=v.residual;firstEyeDepth=v.depth;}
            public bool IsCurrent=>owner!=null&&owner.valid&&owner.generation==generation&&owner.Created;
        }
        private readonly List<Material> materials=new List<Material>();
        private readonly List<SceneRefractionSurface> active=new List<SceneRefractionSurface>();
        private readonly FogVolumeSettings viewSettings=new FogVolumeSettings{enabled=true};
        private RenderTexture color,residual,depth;
        private Material initialize;
        private Mesh quad;
        private bool valid;
        private ulong generation;
        public string UnavailableReason { get; private set; }
        public int SubmittedSurfaces { get; private set; }
        public long TargetBytes=>color!=null?(long)color.width*color.height*40:0;
        public int TargetCount=>(color!=null?1:0)+(residual!=null?1:0)+(depth!=null?1:0);
        private bool Created=>color!=null&&residual!=null&&depth!=null&&color.IsCreated()&&residual.IsCreated()&&depth.IsCreated();
        private bool Owns(Texture t)=>t!=null&&(t==color||t==residual||t==depth);
        public bool TryGetFrame(out Frame frame){frame=default;if(!valid||!Created)return false;frame=new Frame(this);return true;}

        /// <summary>HDR and linear-eye depth must be from the same current opaque view. Returned depth is the primary surface, not refracted path depth.</summary>
        public bool TryRender(RenderTexture source,RenderTexture opaqueEyeDepth,Camera camera,SceneRefractionSettings settings,out Frame frame)
        {
            frame=default;generation++;valid=false;SubmittedSurfaces=0;UnavailableReason=null;
            if(settings==null||!settings.enabled)return Fail("Disabled");
            if(camera==null||GraphicsSettings.currentRenderPipeline!=null||camera.actualRenderingPath!=RenderingPath.Forward)return Fail("Refraction requires a Built-in Forward view");
            if(!Projection(camera))return Fail("Requires canonical perspective or affine orthographic projection, including offcenter/shear/oblique clipping");
            if(!Input(source)||!Input(opaqueEyeDepth)||source==opaqueEyeDepth||Owns(source)||Owns(opaqueEyeDepth)||
                source.width!=opaqueEyeDepth.width||source.height!=opaqueEyeDepth.height||
                (source.format!=RenderTextureFormat.ARGBFloat&&source.format!=RenderTextureFormat.ARGBHalf&&source.format!=RenderTextureFormat.RGB111110Float)||
                (opaqueEyeDepth.format!=RenderTextureFormat.RFloat&&opaqueEyeDepth.format!=RenderTextureFormat.RHalf))return Fail("Requires distinct matching current linear HDR and linear-eye depth");
            if(settings.surfaces==null||settings.surfaces.Length>SceneRefractionSettings.MaximumSurfaces||settings.maximumTargetMiB<1||settings.maximumTargetMiB>2048||
                (long)source.width*source.height*40>(long)settings.maximumTargetMiB*1048576)return Fail("Invalid refraction surface count or target budget");
            if(!FogVolumeBinding.TryCreate(viewSettings,camera,source.width,source.height,out var view,out var error))return Fail(error);
            var saved=RenderTexture.active;
            try
            {
                active.Clear();
                foreach(var s in settings.surfaces)
                {
                    if(s==null||!s.enabled)continue;
                    if(!Validate(s))return Fail("Invalid convex shape, affine transform or optical/cubemap input");
                    active.Add(s);
                }
                if(active.Count==0)return Fail("No active refractive solids");
                var shader=Resources.Load<Shader>("SceneRefraction");
                if(shader==null||!shader.isSupported||SystemInfo.supportedRenderTargetCount<3||
                    !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat,FormatUsage.Render)||
                    !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat,FormatUsage.Sample)||
                    !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat,FormatUsage.Render)||
                    !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat,FormatUsage.Sample))return Fail("Refraction float MRT unavailable");
                if(!Created||color.width!=source.width||color.height!=source.height)
                {ReleaseTargets();color=Target(source.width,source.height,RenderTextureFormat.ARGBFloat,24,"radiance");residual=Target(source.width,source.height,RenderTextureFormat.ARGBFloat,0,"unresolved throughput");depth=Target(source.width,source.height,RenderTextureFormat.RFloat,0,"primary eye depth");}
                if(initialize==null)initialize=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                while(materials.Count<active.Count)materials.Add(new Material(shader){hideFlags=HideFlags.HideAndDontSave});
                while(materials.Count>active.Count){int last=materials.Count-1;UnityEngine.Object.Destroy(materials[last]);materials.RemoveAt(last);}
                if(quad==null){quad=new Mesh{name="Toolkit refraction initialization quad",hideFlags=HideFlags.HideAndDontSave};quad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};quad.triangles=new[]{0,1,2,0,2,3};}
                var projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);var vp=projection*camera.worldToCameraMatrix;
                void BindView(Material m)
                {
                    view.Apply(m);m.SetMatrix("_RefractionVP",vp);m.SetMatrix("_RefractionViewInverse",camera.worldToCameraMatrix.inverse);
                    m.SetMatrix("_RefractionProjection",projection);m.SetMatrix("_RefractionInverseProjection",projection.inverse);m.SetFloat("_RefractionOrthographic",camera.orthographic?1:0);
                }
                BindView(initialize);initialize.SetTexture("_RefractionSource",source);initialize.SetTexture("_RefractionOpaqueDepth",opaqueEyeDepth);
                var commands=new CommandBuffer{name="Toolkit current convex multi-interface refraction"};
                try
                {
                    commands.SetRenderTarget(new RenderTargetIdentifier[]{color,residual,depth},color);
                    commands.DrawMesh(quad,Matrix4x4.identity,initialize,0,0);
                    for(int i=0;i<active.Count;i++)
                    {
                        var s=active[i];var m=materials[i];BindView(m);var planeTransform=s.localToWorld.inverse.transpose;
                        var planes=new Vector4[ConvexRefractionShape.MaximumTriangles];
                        for(int f=0;f<s.shape.PlaneCount;f++)
                        {
                            var p=planeTransform*s.shape.Planes[f];float length=new Vector3(p.x,p.y,p.z).magnitude;
                            if(!Range(length,1e-8f,1e8f)||!Range(p.w/length,-1e8f,1e8f))return Fail("Invalid transformed convex plane");planes[f]=p/length;
                        }
                        m.SetVectorArray("_RefractionPlanes",planes);m.SetInt("_RefractionPlaneCount",s.shape.PlaneCount);m.SetInt("_RefractionInterfaces",s.internalInterfaces);
                        m.SetVector("_RefractionIor",s.indexOfRefraction/s.exteriorIndexOfRefraction);m.SetVector("_RefractionAbsorption",s.absorption);
                        m.SetTexture("_RefractionEnvironment",s.environment);m.SetVector("_RefractionRadianceScale",s.radianceScale);m.SetFloat("_RefractionMip",s.environmentMip);
                        m.SetMatrix("_RefractionEnvironmentRotation",Matrix4x4.Rotate(Quaternion.Inverse(s.environmentRotation.normalized)));
                        commands.DrawMesh(s.shape.Mesh,s.localToWorld,m,0,1);SubmittedSurfaces++;
                    }
                    Graphics.ExecuteCommandBuffer(commands);
                }
                finally{commands.Release();}
                valid=true;frame=new Frame(this);return true;
            }
            catch(Exception e){return Fail("Refraction render failed: "+e.GetType().Name);}
            finally{RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;}
        }
        private bool Validate(SceneRefractionSurface s)
        {
            if(s.shape==null||!s.shape.IsCreated||!SceneDeferredCamera.Matrix(s.localToWorld)||
                s.localToWorld.m30!=0||s.localToWorld.m31!=0||s.localToWorld.m32!=0||s.localToWorld.m33!=1||
                !SceneDeferredCamera.Matrix(s.localToWorld.inverse)||!Vector(s.indexOfRefraction,1,4)||!Range(s.exteriorIndexOfRefraction,1,4)||
                !Vector(s.absorption,0,10000)||!Vector(s.radianceScale,0,64)||s.internalInterfaces<1||s.internalInterfaces>32||
                !Range(s.environmentMip,0,12)||!Range(s.environmentRotation.x,-1,1)||!Range(s.environmentRotation.y,-1,1)||
                !Range(s.environmentRotation.z,-1,1)||!Range(s.environmentRotation.w,-1,1)||Quaternion.Dot(s.environmentRotation,s.environmentRotation)<1e-8f)return false;
            var t=s.environment;
            if(t==null||Owns(t)||t.dimension!=TextureDimension.Cube||GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat)||
                (t.graphicsFormat!=GraphicsFormat.R16G16B16A16_SFloat&&t.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat&&t.graphicsFormat!=GraphicsFormat.B10G11R11_UFloatPack32)||
                s.environmentMip>t.mipmapCount-1)return false;
            if(t is RenderTexture rt&&(!rt.IsCreated()||rt.antiAliasing!=1||rt.useDynamicScale))return false;
            return true;
        }
        private static bool Projection(Camera camera)
        {
            var p=camera.projectionMatrix;
            if(p.m30!=0||p.m31!=0)return false;
            return camera.orthographic?p.m32==0&&p.m33>0:p.m32<0&&p.m33==0&&p.m03==0&&p.m13==0;
        }
        private static bool Input(RenderTexture t)=>t!=null&&t.IsCreated()&&!t.sRGB&&t.dimension==TextureDimension.Tex2D&&t.antiAliasing==1&&!t.useDynamicScale&&t.width<=4096&&t.height<=4096;
        private static bool Range(float v,float lo,float hi)=>!float.IsNaN(v)&&!float.IsInfinity(v)&&v>=lo&&v<=hi;
        private static bool Vector(Vector3 v,float lo,float hi)=>Range(v.x,lo,hi)&&Range(v.y,lo,hi)&&Range(v.z,lo,hi);
        private static RenderTexture Target(int w,int h,RenderTextureFormat f,int z,string name)
        {
            var t=new RenderTexture(w,h,z,f,RenderTextureReadWrite.Linear){name="Toolkit convex refraction "+name,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
            if(!t.Create()||t.format!=f){UnityEngine.Object.Destroy(t);throw new InvalidOperationException("Refraction allocation failed");}return t;
        }
        private bool Fail(string reason){UnavailableReason=reason;Release();return false;}
        private void ReleaseTargets(){foreach(var t in new[]{color,residual,depth})if(t!=null){if(RenderTexture.active==t)RenderTexture.active=null;t.Release();UnityEngine.Object.Destroy(t);}color=residual=depth=null;}
        private void Release(){valid=false;SubmittedSurfaces=0;ReleaseTargets();foreach(var m in materials)UnityEngine.Object.Destroy(m);materials.Clear();if(initialize!=null)UnityEngine.Object.Destroy(initialize);initialize=null;if(quad!=null)UnityEngine.Object.Destroy(quad);quad=null;active.Clear();}
        public void Dispose(){generation++;Release();}
    }
}
