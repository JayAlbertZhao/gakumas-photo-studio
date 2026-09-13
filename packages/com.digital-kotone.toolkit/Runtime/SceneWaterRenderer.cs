using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit full-resolution lit transmission after opaque color/depth production.</summary>
    public sealed class SceneWaterRenderer : IDisposable
    {
        public readonly struct Frame
        {
            private readonly SceneWaterRenderer owner;
            private readonly ulong generation;
            public readonly RenderTexture color;
            internal Frame(SceneWaterRenderer value) { owner=value; generation=value.generation; color=value.current; }
            public bool IsCurrent => owner!=null && owner.valid && owner.generation==generation && owner.Created;
        }
        private readonly SceneForwardLightResources lighting = new SceneForwardLightResources();
        private readonly List<SceneWaterSurface> active = new List<SceneWaterSurface>();
        private readonly List<Material> materials = new List<Material>();
        private readonly FogVolumeSettings viewSettings = new FogVolumeSettings { enabled = true };
        private RenderTexture a, b, current;
        private ulong generation;
        private bool valid;
        public string UnavailableReason { get; private set; }
        public int SubmittedSurfaces { get; private set; }
        public int SubmittedLights => lighting.SubmittedLights;
        public int TileCount => lighting.TileCount;
        public int ShadowMapCount => lighting.LocalShadowMapCount+lighting.MainShadowMapCount;
        public int PlanarSurfaces { get; private set; }
        public SceneForwardLightBackend Backend => lighting.Backend;
        public string FallbackReason => lighting.FallbackReason;
        public int TargetCount => (a!=null?1:0)+(b!=null?1:0)+lighting.ShadowTargetCount;
        public long ColorTargetBytes => a!=null?(long)a.width*a.height*32:0;
        public long LightingBufferBytes => lighting.BufferBytes;
        private bool Created => a!=null && b!=null && a.IsCreated() && b.IsCreated() && lighting.IsCreated;
        private bool Owns(RenderTexture t) => t!=null && (t==a || t==b || lighting.Owns(t));
        public bool TryGetFrame(out Frame frame) { frame=default; if(!valid || !Created)return false; frame=new Frame(this);return true; }

        /// <summary>Source is opaque/earlier color. Depth is matching current opaque depth, never water depth.</summary>
        public bool TryRender(RenderTexture source, FogVolumeDepth depth, Camera camera, SceneWaterSettings settings, out Frame frame)
        {
            frame=default; generation++; valid=false; SubmittedSurfaces=PlanarSurfaces=0; UnavailableReason=null;
            if(settings==null || !settings.enabled) return Fail("Disabled");
            if(camera==null || GraphicsSettings.currentRenderPipeline!=null || camera.actualRenderingPath!=RenderingPath.Forward)
                return Fail("Water requires a Built-in Forward view");
            if(Owns(settings.lighting?.localLights?.atlas as RenderTexture))
                return Fail("Water cannot reuse its owned output as a light atlas");
            if(!Texture(source) || !Texture(depth.texture) || source==depth.texture || Owns(source) || Owns(depth.texture) ||
                source.width!=depth.texture.width || source.height!=depth.texture.height ||
                (source.format!=RenderTextureFormat.ARGBFloat && source.format!=RenderTextureFormat.ARGBHalf && source.format!=RenderTextureFormat.RGB111110Float) ||
                (depth.encoding!=FogDepthEncoding.LinearEye && depth.encoding!=FogDepthEncoding.Device) ||
                (depth.encoding==FogDepthEncoding.LinearEye && depth.texture.format!=RenderTextureFormat.RFloat && depth.texture.format!=RenderTextureFormat.RHalf) ||
                (depth.encoding==FogDepthEncoding.Device && depth.texture.format!=RenderTextureFormat.RFloat && depth.texture.format!=RenderTextureFormat.Depth))
                return Fail("Water requires distinct, matching, fixed linear HDR and explicit opaque depth");
            if(settings.surfaces==null || settings.surfaces.Length>SceneWaterSettings.MaximumSurfaces ||
                !Range(settings.depthBias,0,1) || double.IsNaN(settings.seconds) || double.IsInfinity(settings.seconds) || Math.Abs(settings.seconds)>1e12 ||
                settings.maximumTargetMiB<1 || settings.maximumTargetMiB>2048)
                return Fail("Invalid water surfaces, clock, depth bias or memory budget");
            if((long)source.width*source.height*32>(long)settings.maximumTargetMiB*1024*1024)
                return Fail("Water color targets exceed memory budget");
            if(!FogVolumeBinding.TryCreate(viewSettings,camera,source.width,source.height,out var view,out var error)) return Fail(error);
            var saved=RenderTexture.active;
            try
            {
                active.Clear(); var seen=new HashSet<(Renderer,int)>();
                foreach(var water in settings.surfaces)
                {
                    if(water?.surface==null || !water.surface.enabled)continue;
                    if(!Validate(water,camera,out error) || !seen.Add((water.surface.renderer,water.surface.submesh))) return Fail(error??"Duplicate water surface");
                    var r=water.surface.renderer;
                    if(r.enabled && !r.forceRenderingOff && r.gameObject.activeInHierarchy && water.surface.inputs.alpha>0)active.Add(water);
                }
                if(active.Count==0)return Fail("No active water surfaces");
                var shader=Resources.Load<Shader>("SceneWater");
                if(shader==null || !shader.isSupported || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)) return Fail("Water float shader/target unavailable");
                if(!lighting.Prepare(camera,settings.lighting,source.width,source.height,out error))return Fail(error);
                if(a==null || b==null || !a.IsCreated() || !b.IsCreated() || a.width!=source.width || a.height!=source.height)
                { ReleaseTargets(); a=Target(source.width,source.height,"A"); b=Target(source.width,source.height,"B"); }
                while(materials.Count<active.Count)materials.Add(new Material(shader){hideFlags=HideFlags.HideAndDontSave});
                while(materials.Count>active.Count){int i=materials.Count-1;UnityEngine.Object.Destroy(materials[i]);materials.RemoveAt(i);}
                // Each surface samples the completed previous layer and writes a different target.
                // No framebuffer feedback and no shared-material/global shader mutation.
                var commands=new CommandBuffer { name="Toolkit current lit water transmission" };
                try
                {
                    lighting.Record(commands); commands.Blit(source,a); current=a;
                    for(int i=0;i<active.Count;i++)
                    {
                        var water=active[i];var s=water.surface;var m=materials[i];
                        SceneDeferredCamera.BindInputs(m,s.inputs); lighting.Bind(m); view.Apply(m);
                        m.SetFloat("_Cull",(int)s.cull);m.SetFloat("_Cutoff",s.alphaCutoff);m.SetFloat("_ReceiverGroup",s.receiverGroup);
                        m.SetVector("_VertexScale",s.vertexScale);m.SetFloat("_Additive",0);m.SetFloat("_HasNormal",0);
                        m.SetFloat("_SceneGiMode",0);if(s.gi!=null && !s.gi.Bind(m,s.renderer,out error))return Fail(error);
                        m.SetFloat("_WaterTransformSign",Mathf.Sign(s.renderer.localToWorldMatrix.determinant));
                        var projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);
                        m.SetVector("_WaterPixelAxes",new Vector4(Mathf.Sign(projection.m00),Mathf.Sign(projection.m11)*(SystemInfo.graphicsUVStartsAtTop?-1:1),0,0));
                        m.SetTexture("_WaterBackground",current);m.SetTexture("_WaterDepth",depth.texture);
                        m.SetVector("_WaterInput",new Vector4((int)depth.encoding,settings.depthBias,water.maximumThickness,water.refractionPixelsPerUnit));
                        float f0=(water.indexOfRefraction-1)/(water.indexOfRefraction+1);f0*=f0;
                        m.SetVector("_WaterOptics",new Vector4(f0,water.reflectionStrength,water.shoreFadeDistance,water.normalStrength));
                        m.SetVector("_WaterAbsorption",water.absorption);m.SetVector("_WaterScattering",water.scatteringRadiance);
                        m.SetVector("_WaterWaveA",new Vector4(water.waveA.x,water.waveA.y,water.waveA.z,Phase(settings.seconds*water.waveA.w)));
                        m.SetVector("_WaterWaveB",new Vector4(water.waveB.x,water.waveB.y,water.waveB.z,Phase(settings.seconds*water.waveB.w)));
                        m.SetVector("_WaterNormalScroll",new Vector4(Fraction(settings.seconds*water.normalScroll.x),Fraction(settings.seconds*water.normalScroll.y),s.inputs.normalMap!=null?1:0,0));
                        m.SetTexture("_WaterProbe",water.reflectionProbe);
                        RenderTexture planar=null;
                        if(water.planarReflection!=null)water.planarReflection.TryGetReflection(camera,source.width,source.height,out planar);
                        if(planar!=null)PlanarSurfaces++;
                        m.SetTexture("_WaterPlanar",planar!=null?planar:Texture2D.blackTexture);
                        m.SetVector("_WaterReflection",new Vector4(water.reflectionProbe!=null?1:0,water.probeMaximumMip,planar!=null?1:0,0));
                        m.SetVector("_WaterReflectionDistortion",water.reflectionDistortion);
                        var next=current==a?b:a;commands.Blit(current,next);commands.SetRenderTarget(next);
                        commands.DrawRenderer(s.renderer,m,s.submesh,0);current=next;SubmittedSurfaces++;
                    }
                    Graphics.ExecuteCommandBuffer(commands);
                }
                finally { commands.Release(); }
                valid=true;frame=new Frame(this);return true;
            }
            catch(Exception exception){return Fail("Water render failed: "+exception.GetType().Name);}
            finally {RenderTexture.active=saved!=null && saved.IsCreated()?saved:null;}
        }
        private bool Validate(SceneWaterSurface w,Camera camera,out string error)
        {
            error="Invalid water material, explicit geometry, transform or texture";var s=w.surface;var r=s.renderer;
            if(r==null || s.additive || !SceneDeferredCamera.Inputs(s.inputs) || s.inputs.mos.x!=0 ||
                !Range(s.alphaCutoff,0,1) || s.receiverGroup<0 || s.receiverGroup>255 || (int)s.cull<0 || (int)s.cull>2 ||
                !Vector(s.vertexScale,-1e6f,1e6f) || Mathf.Abs(s.vertexScale.x*s.vertexScale.y*s.vertexScale.z)<1e-8f ||
                !SceneDeferredCamera.Matrix(r.localToWorldMatrix) || r.HasPropertyBlock() || (camera.cullingMask&(1<<r.gameObject.layer))!=0 ||
                !Range(w.indexOfRefraction,1,2.5f) || !Vector(w.absorption,0,10000) || !Vector(w.scatteringRadiance,0,65504) ||
                !Range(w.maximumThickness,0,1000) || !Range(w.refractionPixelsPerUnit,0,512) || !Range(w.reflectionStrength,0,1) ||
                !Range(w.probeMaximumMip,0,12) || !Range(w.normalStrength,0,2) || !Range(w.shoreFadeDistance,0,10))return false;
            foreach(var wave in new[]{w.waveA,w.waveB})
                if(!Range(wave.x,-1000,1000)||!Range(wave.y,-1000,1000)||!Range(wave.z,-1,1)||!Range(wave.w,-1000,1000))return false;
            for(int i=0;i<2;i++)if(!Range(w.normalScroll[i],-100,100)||!Range(w.reflectionDistortion[i],-.5f,.5f))return false;
            Mesh mesh=r is SkinnedMeshRenderer skin?skin.sharedMesh:r is MeshRenderer?r.GetComponent<MeshFilter>()?.sharedMesh:null;
            if(mesh==null || s.submesh<0 || s.submesh>=mesh.subMeshCount || s.submesh>=r.sharedMaterials.Length ||
                mesh.GetTopology(s.submesh)!=MeshTopology.Triangles || !mesh.HasVertexAttribute(VertexAttribute.Position) ||
                !mesh.HasVertexAttribute(VertexAttribute.Normal)||!mesh.HasVertexAttribute(VertexAttribute.Tangent)||!mesh.HasVertexAttribute(VertexAttribute.TexCoord0))return false;
            foreach(var t in new[]{s.inputs.albedoMap,s.inputs.normalMap,s.inputs.mosMap,s.inputs.emissionMap})
                if(t!=null && (t.dimension!=TextureDimension.Tex2D || (t is RenderTexture rt && (!Texture(rt)||Owns(rt)))))return false;
            if(w.reflectionProbe!=null && w.probeMaximumMip>w.reflectionProbe.mipmapCount-1)return false;
            if(s.gi!=null && (Owns(s.gi.lightmap as RenderTexture)||Owns(s.gi.directionality as RenderTexture)))return false;
            if(s.gi!=null && !s.gi.Validate(r,mesh,out error))return false;
            error=null;return true;
        }
        private static bool Texture(RenderTexture t)=>t!=null&&t.IsCreated()&&!t.sRGB&&t.dimension==TextureDimension.Tex2D&&t.antiAliasing==1&&!t.useDynamicScale&&t.width<=4096&&t.height<=4096;
        private static bool Range(float x,float lo,float hi)=>!float.IsNaN(x)&&!float.IsInfinity(x)&&x>=lo&&x<=hi;
        private static bool Vector(Vector3 v,float lo,float hi)=>Range(v.x,lo,hi)&&Range(v.y,lo,hi)&&Range(v.z,lo,hi);
        private static float Phase(double v)=>(float)(v%(2*Math.PI));
        private static float Fraction(double v)=>(float)(v-Math.Floor(v));
        private static RenderTexture Target(int w,int h,string label)
        {
            var t=new RenderTexture(w,h,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){name="Toolkit water HDR "+label,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
            if(!t.Create()){UnityEngine.Object.Destroy(t);throw new InvalidOperationException("Water allocation failed");}return t;
        }
        private bool Fail(string error){UnavailableReason=error;Release();return false;}
        private void ReleaseTargets()
        {
            current=null;foreach(var t in new[]{a,b})if(t!=null){if(RenderTexture.active==t)RenderTexture.active=null;t.Release();UnityEngine.Object.Destroy(t);}a=b=null;
        }
        private void Release(){valid=false;SubmittedSurfaces=PlanarSurfaces=0;lighting.Dispose();ReleaseTargets();foreach(var m in materials)UnityEngine.Object.Destroy(m);materials.Clear();active.Clear();}
        public void Dispose(){generation++;Release();}
    }
}
