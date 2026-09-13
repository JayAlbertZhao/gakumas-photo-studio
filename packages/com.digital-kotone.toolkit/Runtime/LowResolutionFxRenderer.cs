using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Ordered mixed-resolution geometry, premultiplied composition and depth-edge replay.</summary>
    public sealed class LowResolutionFxRenderer : IDisposable
    {
        private sealed class Scratch { public RenderTexture effect, depthRange; }
        public readonly struct Frame
        {
            private readonly LowResolutionFxRenderer owner;
            private readonly uint generation;
            public readonly RenderTexture color;
            internal Frame(LowResolutionFxRenderer value)
            { owner=value; generation=value.generation; color=value.current; }
            public bool IsCurrent => owner != null && owner.hasFrame && generation == owner.generation && owner.Created;
            /// <summary>Last batch at this resolution, not a retained history of every batch.</summary>
            public bool TryGetLastBatch(FxResolution resolution, out RenderTexture effect, out RenderTexture depthRange)
            {
                effect=depthRange=null;
                if (!IsCurrent || !ValidResolution(resolution)) return false;
                var scratch=owner.scratch[Index(resolution)];
                if (scratch==null) return false;
                effect=scratch.effect; depthRange=scratch.depthRange; return true;
            }
        }
        private readonly List<LowResolutionFxSurface> active = new List<LowResolutionFxSurface>();
        private readonly List<Material> surfaces = new List<Material>();
        private readonly Scratch[] scratch = new Scratch[3];
        private readonly bool[] needed = new bool[3];
        private readonly FogVolumeSettings viewOnly = new FogVolumeSettings { enabled=true };
        private RenderTexture a, b, current;
        private Material resolve;
        private uint generation;
        private bool hasFrame;
        public int DrawCalls { get; private set; }
        public int BatchCount { get; private set; }
        public int EdgeReplayDraws { get; private set; }
        public long TargetBytes { get; private set; }
        public int TargetCount { get { int count=a!=null?2:0; foreach(var s in scratch) if(s!=null) count+=2; return count; } }
        public string UnavailableReason { get; private set; }
        private bool Created
        {
            get
            {
                if (a==null || !a.IsCreated() || b==null || !b.IsCreated()) return false;
                foreach(var s in scratch) if(s!=null && (s.effect==null || !s.effect.IsCreated() || s.depthRange==null || !s.depthRange.IsCreated())) return false;
                return true;
            }
        }
        public bool TryGetFrame(out Frame frame)
        { frame=default; if(!hasFrame || !Created) return false; frame=new Frame(this); return true; }

        public bool TryRender(RenderTexture source, FogVolumeDepth depth, Camera camera,
            LowResolutionFxSettings settings, out Frame frame, RenderTexture protection=null)
        {
            frame=default; generation++; hasFrame=false; DrawCalls=BatchCount=EdgeReplayDraws=0; UnavailableReason=null;
            if(settings==null || !settings.enabled) { Release(); return false; }
            if(!Valid(source) || !Valid(depth.texture) || source.sRGB || depth.texture.sRGB || source==depth.texture || Owns(source) || Owns(depth.texture) ||
                source.width!=depth.texture.width || source.height!=depth.texture.height ||
                (source.format!=RenderTextureFormat.ARGBFloat && source.format!=RenderTextureFormat.ARGBHalf && source.format!=RenderTextureFormat.RGB111110Float) ||
                (depth.encoding!=FogDepthEncoding.LinearEye && depth.encoding!=FogDepthEncoding.Device) ||
                (depth.encoding==FogDepthEncoding.LinearEye && depth.texture.format!=RenderTextureFormat.RFloat && depth.texture.format!=RenderTextureFormat.RHalf) ||
                (depth.encoding==FogDepthEncoding.Device && depth.texture.format!=RenderTextureFormat.RFloat && depth.texture.format!=RenderTextureFormat.Depth) ||
                (protection!=null && (!Valid(protection) || protection.sRGB || Owns(protection) || protection==source || protection==depth.texture ||
                    protection.width!=source.width || protection.height!=source.height || (protection.format!=RenderTextureFormat.R8 && protection.format!=RenderTextureFormat.RFloat))))
                return Fail("FX requires distinct matching linear HDR, depth and optional protection targets");
            if(settings.surfaces==null || settings.surfaces.Length>LowResolutionFxSettings.MaximumSurfaces ||
                !Range(settings.depthBias,0,10) || !Range(settings.depthAbsoluteTolerance,0,10) || !Range(settings.depthRelativeTolerance,0,1) ||
                !Range(settings.effectEdgeThreshold,0,65504) || settings.maximumTargetMiB<1 || settings.maximumTargetMiB>2048)
                return Fail("Invalid FX surface budget, reconstruction tolerances or target memory budget");
            if(!FogVolumeBinding.TryCreate(settings.fog!=null && settings.fog.enabled?settings.fog:viewOnly,camera,source.width,source.height,out var view,out var reason)) return Fail(reason);
            var saved=RenderTexture.active;
            try
            {
                active.Clear(); Array.Clear(needed,0,needed.Length);
                foreach(var surface in settings.surfaces)
                {
                    if(surface==null || !surface.enabled || surface.opacity==0) continue;
                    if(!ValidateSurface(surface,out reason)) return Fail(reason);
                    active.Add(surface);
                    if(surface.resolution!=FxResolution.Full || surface.blend==FxBlend.Distortion) needed[Index(surface.resolution)]=true;
                }
                if(active.Count==0) { Release(); return false; }
                long bytes=(long)source.width*source.height*32;
                for(int i=0;i<3;i++) if(needed[i]) bytes+=(long)Divide(source.width,1<<i)*Divide(source.height,1<<i)*24;
                if(bytes>(long)settings.maximumTargetMiB*1024*1024) return Fail("FX targets exceed the explicit memory budget");
                var shader=Resources.Load<Shader>("LowResolutionFx");
                if(shader==null || !shader.isSupported || !Supported(GraphicsFormat.R32G32B32A32_SFloat) || !Supported(GraphicsFormat.R32G32_SFloat) ||
                    !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat,FormatUsage.Blend))
                    return Fail("FX shader or float color/depth-range targets unavailable");
                if(!Created || a.width!=source.width || a.height!=source.height) ReleaseTargets();
                if(a==null) { a=Allocate(source.width,source.height,RenderTextureFormat.ARGBFloat,"HDR A"); b=Allocate(source.width,source.height,RenderTextureFormat.ARGBFloat,"HDR B"); }
                for(int i=0;i<3;i++)
                {
                    if(!needed[i]) { ReleaseScratch(i); continue; }
                    if(scratch[i]==null)
                    {
                        scratch[i]=new Scratch();
                        scratch[i].effect=Allocate(Divide(source.width,1<<i),Divide(source.height,1<<i),RenderTextureFormat.ARGBFloat,"effect /"+(1<<i));
                        scratch[i].depthRange=Allocate(Divide(source.width,1<<i),Divide(source.height,1<<i),RenderTextureFormat.RGFloat,"opaque range /"+(1<<i));
                    }
                }
                TargetBytes=bytes;
                if(resolve==null) resolve=new Material(shader) { hideFlags=HideFlags.HideAndDontSave };
                while(surfaces.Count<active.Count) surfaces.Add(new Material(shader) { hideFlags=HideFlags.HideAndDontSave });
                while(surfaces.Count>active.Count) { UnityEngine.Object.Destroy(surfaces[surfaces.Count-1]); surfaces.RemoveAt(surfaces.Count-1); }
                void Common(Material material, FxResolution resolution, int phase, Scratch targets, bool readEffects)
                {
                    view.Apply(material);
                    material.SetMatrix("_FxViewProjection",GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)*camera.worldToCameraMatrix);
                    material.SetTexture("_FxDepth",depth.texture); material.SetTexture("_FxProtection",protection);
                    material.SetTexture("_FxEffect",readEffects && targets!=null?targets.effect:Texture2D.blackTexture);
                    material.SetTexture("_FxDepthRange",targets!=null?targets.depthRange:Texture2D.blackTexture);
                    material.SetVector("_FxInput",new Vector4((int)depth.encoding,protection!=null?1:0,(int)resolution,phase));
                    material.SetVector("_FxLowSize",new Vector4(Divide(source.width,(int)resolution),Divide(source.height,(int)resolution),source.width/(float)source.height,0));
                    material.SetVector("_FxTolerance",new Vector4(settings.depthBias,settings.depthAbsoluteTolerance,settings.depthRelativeTolerance,settings.effectEdgeThreshold));
                }
                Common(resolve,FxResolution.Full,0,null,false);
                Graphics.Blit(source,a,resolve,7); DrawCalls++; current=a;
                for(int i=0;i<3;i++) if(needed[i])
                {
                    Common(resolve,(FxResolution)(1<<i),1,null,false);
                    Graphics.Blit(source,scratch[i].depthRange,resolve,0); DrawCalls++;
                }
                void Draw(int start,int end,RenderTexture target,int phase,Scratch targets,bool distortion)
                {
                    var commands=new CommandBuffer { name="Toolkit ordered FX "+(phase==1?"low":phase==2?"depth edge replay":"full") };
                    try
                    {
                        commands.SetRenderTarget(target); commands.SetViewport(new Rect(0,0,target.width,target.height));
                        if(phase==1 || (distortion && phase==0)) commands.ClearRenderTarget(false,true,Color.clear);
                        for(int index=start;index<end;index++)
                        {
                            var s=active[index]; var material=surfaces[index]; Common(material,s.resolution,phase,targets,phase==2);
                            material.SetTexture("_FxBackground",distortion && phase==2?current:Texture2D.blackTexture);
                            material.SetTexture("_FxTexture",s.texture!=null?s.texture:Texture2D.whiteTexture);
                            material.SetVector("_FxTextureST",s.textureST); material.SetVector("_FxRadiance",new Vector4(s.linearRadiance.x,s.linearRadiance.y,s.linearRadiance.z,s.opacity));
                            material.SetVector("_FxSurface",new Vector4((int)s.blend,s.vertexColor?1:0,s.radialSoftness,s.softIntersectionDistance));
                            material.SetVector("_FxDistortion",new Vector4(s.distortionOffset.x,s.distortionOffset.y,s.distortionTextureScale.x,s.distortionTextureScale.y));
                            material.SetVector("_FxFlags",new Vector4(s.fog && settings.fog!=null && settings.fog.enabled?1:0,s.texture!=null?1:0,0,0));
                            material.SetFloat("_Cull",(int)s.cull);
                            int pass=distortion?(phase==2?6:3):(phase==1?1:2);
                            if(s.renderer!=null) commands.DrawRenderer(s.renderer,material,s.submesh,pass);
                            else commands.DrawMesh(s.mesh,s.localToWorld,material,s.submesh,pass);
                            DrawCalls++; if(phase==2) EdgeReplayDraws++;
                        }
                        Graphics.ExecuteCommandBuffer(commands);
                    }
                    finally { commands.Release(); }
                }
                int start=0;
                while(start<active.Count)
                {
                    var first=active[start]; int end=start+1; bool distortion=first.blend==FxBlend.Distortion;
                    if(!distortion) while(end<active.Count && active[end].blend!=FxBlend.Distortion && active[end].resolution==first.resolution) end++;
                    BatchCount++;
                    if(first.resolution==FxResolution.Full && !distortion) Draw(start,end,current,0,null,false);
                    else
                    {
                        var targets=scratch[Index(first.resolution)]; int phase=first.resolution==FxResolution.Full?0:1;
                        Draw(start,end,targets.effect,phase,targets,distortion);
                        var next=current==a?b:a;
                        Common(resolve,first.resolution,phase,targets,true); resolve.SetTexture("_FxBackground",current);
                        Graphics.Blit(current,next,resolve,distortion?5:4); DrawCalls++;
                        if(first.resolution!=FxResolution.Full) Draw(start,end,next,2,targets,distortion);
                        current=next;
                    }
                    start=end;
                }
                hasFrame=true; frame=new Frame(this); return true;
            }
            catch(Exception error) { return Fail("FX render failed: "+error.GetType().Name); }
            finally { RenderTexture.active=saved!=null && saved.IsCreated()?saved:null; }
        }

        internal static bool ValidateSurface(LowResolutionFxSurface s,out string reason)
        {
            reason="Invalid FX geometry, linear material, UV or blend settings";
            if((s.renderer==null)==(s.mesh==null) || !ValidResolution(s.resolution) || s.blend<FxBlend.Alpha || s.blend>FxBlend.Distortion ||
                s.submesh<0 || s.cull<CullMode.Off || s.cull>CullMode.Back || !Range(s.linearRadiance.x,0,65504) || !Range(s.linearRadiance.y,0,65504) || !Range(s.linearRadiance.z,0,65504) || !Range(s.opacity,0,1) ||
                !Range(s.radialSoftness,0,1) || !Range(s.softIntersectionDistance,0,1e4f)) return false;
            for(int i=0;i<4;i++) if(!Range(s.textureST[i],-1e6f,1e6f)) return false;
            for(int i=0;i<2;i++) if(!Range(s.distortionOffset[i],-1,1) || !Range(s.distortionTextureScale[i],-1,1)) return false;
            Mesh geometry=s.mesh;
            if(s.renderer!=null)
            {
                if(s.renderer is SkinnedMeshRenderer skinned) geometry=skinned.sharedMesh;
                else if(s.renderer is MeshRenderer) geometry=s.renderer.GetComponent<MeshFilter>()?.sharedMesh;
                else return false;
            }
            var matrix=s.renderer!=null?s.renderer.localToWorldMatrix:s.localToWorld;
            for(int i=0;i<16;i++) if(!Range(matrix[i],-1e8f,1e8f)) return false;
            if(geometry==null || s.submesh>=geometry.subMeshCount || geometry.GetTopology(s.submesh)!=MeshTopology.Triangles) return false;
            if(s.texture!=null && (s.texture.mipmapCount!=1 || s.texture.width>4096 || s.texture.height>4096 ||
                GraphicsFormatUtility.IsSRGBFormat(s.texture.graphicsFormat) || !SystemInfo.IsFormatSupported(s.texture.graphicsFormat,FormatUsage.Sample)))
            { reason="FX texture must be a caller-owned linear non-mipmapped Texture2D, at most 4096 square"; return false; }
            reason=null; return true;
        }
        private static int Divide(int size,int scale) => (size+scale-1)/scale;
        private static int Index(FxResolution value) => value==FxResolution.Full?0:value==FxResolution.Half?1:2;
        private static bool ValidResolution(FxResolution r) => r==FxResolution.Full || r==FxResolution.Half || r==FxResolution.Quarter;
        private static bool Range(float value,float min,float max) => FogVolumeSettings.Range(value,min,max);
        private static bool Supported(GraphicsFormat f) => SystemInfo.IsFormatSupported(f,FormatUsage.Render) && SystemInfo.IsFormatSupported(f,FormatUsage.Sample);
        private static bool Valid(RenderTexture t) => t!=null && t.IsCreated() && t.antiAliasing==1 && !t.useMipMap && !t.useDynamicScale && t.dimension==TextureDimension.Tex2D && t.volumeDepth==1;
        private bool Owns(RenderTexture t)
        {
            if(t==null) return false; if(t==a || t==b) return true;
            foreach(var s in scratch) if(s!=null && (t==s.effect || t==s.depthRange)) return true; return false;
        }
        private bool Fail(string reason) { Release(); UnavailableReason=reason; return false; }
        private static RenderTexture Allocate(int width,int height,RenderTextureFormat format,string label)
        {
            var target=new RenderTexture(width,height,0,format,RenderTextureReadWrite.Linear) { name="Toolkit FX "+label,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave };
            if(!target.Create()) { UnityEngine.Object.Destroy(target); throw new InvalidOperationException("FX target allocation failed"); } return target;
        }
        private static void DestroyTarget(RenderTexture t) { if(t!=null) { t.Release(); UnityEngine.Object.Destroy(t); } }
        private void ReleaseScratch(int index) { var s=scratch[index]; if(s==null) return; DestroyTarget(s.effect); DestroyTarget(s.depthRange); scratch[index]=null; }
        private void ReleaseTargets()
        {
            if(Owns(RenderTexture.active)) RenderTexture.active=null;
            DestroyTarget(a); DestroyTarget(b); a=b=current=null;
            for(int i=0;i<3;i++) ReleaseScratch(i); hasFrame=false; TargetBytes=0;
        }
        private void Release()
        {
            ReleaseTargets(); if(resolve!=null) UnityEngine.Object.Destroy(resolve); resolve=null;
            foreach(var m in surfaces) UnityEngine.Object.Destroy(m); surfaces.Clear(); active.Clear(); DrawCalls=BatchCount=EdgeReplayDraws=0;
        }
        public void Dispose() { generation++; Release(); }
    }
}
