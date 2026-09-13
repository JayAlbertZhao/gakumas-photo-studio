using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Typed medium/surface/optical producers sharing ordered premultiplied work targets.</summary>
    public sealed class HeavyFxRenderer : IDisposable
    {
        private sealed class Scratch { public RenderTexture effect, range; }
        private sealed class Batch
        {
            public FxResolution resolution;
            public bool medium, distortion, optics;
            public int start, end;
            public HeavyFxBatchInfo Info => new HeavyFxBatchInfo(resolution,medium,distortion,optics,end-start);
        }
        public readonly struct Frame
        {
            private readonly HeavyFxRenderer owner;
            private readonly uint generation;
            public readonly RenderTexture color, shadowAtlas, opticalVisibility, repairMask;
            internal Frame(HeavyFxRenderer value)
            { owner=value;generation=value.generation;color=value.current;shadowAtlas=value.shadows.Atlas;opticalVisibility=value.optics.SharedVisibility;repairMask=value.repair; }
            public bool IsCurrent => owner!=null&&owner.hasFrame&&generation==owner.generation&&owner.Created;
            public bool TryGetLastBatch(FxResolution resolution,out RenderTexture effect,out RenderTexture depthRange)
            {
                effect=depthRange=null;
                if(!IsCurrent||!ValidResolution(resolution))return false;
                var s=owner.scratch[Index(resolution)];if(s==null)return false;
                effect=s.effect;depthRange=s.range;return true;
            }
            public bool TryGetBatchInfo(int index,out HeavyFxBatchInfo info)
            {
                info=default;if(!IsCurrent||index<0||index>=owner.batches.Count)return false;
                info=owner.batches[index].Info;return true;
            }
        }
        private readonly Scratch[] scratch=new Scratch[3];
        private readonly bool[] needed=new bool[3];
        private readonly List<Batch> batches=new List<Batch>();
        private readonly List<LowResolutionFxSurface> surfaces=new List<LowResolutionFxSurface>();
        private readonly List<VolumetricSpotLight> lights=new List<VolumetricSpotLight>();
        private readonly List<SceneDecalLight> shadowLights=new List<SceneDecalLight>();
        private readonly SceneLightShadowAtlas shadows=new SceneLightShadowAtlas("Toolkit joint medium shadows");
        private readonly LensFlareRenderer optics=new LensFlareRenderer();
        private readonly FogVolumeSettings viewOnly=new FogVolumeSettings{enabled=true};
        private Material resolve, surfaceMaterial, mediumMaterial;
        private RenderTexture a,b,current,repair;
        private bool hasFrame, needsOptics;
        private uint generation;
        public int DrawCalls {get;private set;}
        public int ReplayDrawCalls {get;private set;}
        public int BatchCount => batches.Count;
        public int ShadowCasterDrawCalls => shadows.CasterDrawCalls;
        public long TargetBytes {get;private set;}
        public int TargetCount
        {get{int count=a!=null?3:0;foreach(var s in scratch)if(s!=null)count+=2;return count+(optics.SharedVisibility!=null?1:0)+(shadows.Atlas!=null?1:0);}}
        public string UnavailableReason {get;private set;}
        private bool Created
        {
            get
            {
                if(a==null||!a.IsCreated()||b==null||!b.IsCreated()||repair==null||!repair.IsCreated()||(needsOptics&&!optics.SharedCreated)||(shadows.Atlas!=null&&!shadows.Atlas.IsCreated()))return false;
                foreach(var s in scratch)if(s!=null&&(s.effect==null||!s.effect.IsCreated()||s.range==null||!s.range.IsCreated()))return false;
                return true;
            }
        }
        public bool TryGetFrame(out Frame frame)
        {frame=default;if(!hasFrame||!Created)return false;frame=new Frame(this);return true;}

        public bool TryRender(RenderTexture source,FogVolumeDepth depth,Camera camera,HeavyFxSettings settings,
            double timeSeconds,out Frame frame,RenderTexture protection=null)
        {
            frame=default;generation++;hasFrame=false;DrawCalls=ReplayDrawCalls=0;UnavailableReason=null;
            if(settings==null||!settings.enabled){Release();return false;}
            if(!Inputs(source,depth,protection))return Fail("Joint FX requires distinct matching current linear HDR/depth/protection targets");
            if(!Range(settings.depthAbsoluteTolerance,0,10)||!Range(settings.depthRelativeTolerance,0,1)||!Range(settings.effectEdgeThreshold,0,65504)||
                settings.maximumTargetMiB<1||settings.maximumTargetMiB>2048||double.IsNaN(timeSeconds)||double.IsInfinity(timeSeconds)||Math.Abs(timeSeconds)>1e9)
                return Fail("Invalid joint reconstruction, time or target budget");
            bool hasMedium=settings.medium!=null&&settings.medium.enabled;
            bool hasGeometry=settings.geometry!=null&&settings.geometry.enabled;
            bool hasOptics=settings.optics!=null&&settings.optics.enabled;
            string reason;
            if(hasMedium&&!settings.medium.Validate(out reason))return Fail(reason);
            if(hasOptics&&!settings.optics.Validate(out reason))return Fail(reason);
            if(hasGeometry&&(settings.geometry.surfaces==null||settings.geometry.surfaces.Length>LowResolutionFxSettings.MaximumSurfaces||!Range(settings.geometry.depthBias,0,10)))
                return Fail("Invalid joint geometry or surface budget");
            bool surfaceFog=hasGeometry&&settings.geometry.fog!=null&&settings.geometry.fog.enabled;
            if(hasMedium&&surfaceFog)return Fail("Choose one medium: joint volume and distance/sphere surface fog cannot overlap");
            if(!FogVolumeBinding.TryCreate(surfaceFog?settings.geometry.fog:viewOnly,camera,source.width,source.height,out var view,out reason))return Fail(reason);
            var saved=RenderTexture.active;
            try
            {
                surfaces.Clear();batches.Clear();lights.Clear();shadowLights.Clear();Array.Clear(needed,0,needed.Length);
                if(hasMedium)
                {
                    foreach(var light in settings.medium.lights)if(light!=null&&light.enabled&&light.linearRadiance!=Vector3.zero)
                    {lights.Add(light);shadowLights.Add(light.ShadowLight());}
                    batches.Add(new Batch{resolution=(FxResolution)settings.medium.resolution,medium=true});
                }
                if(hasGeometry)foreach(var s in settings.geometry.surfaces)
                {
                    if(s==null||!s.enabled||s.opacity==0)continue;
                    if(!LowResolutionFxRenderer.ValidateSurface(s,out reason))return Fail(reason);
                    var last=batches.Count>0?batches[batches.Count-1]:null;
                    if(last==null||last.distortion||s.blend==FxBlend.Distortion||last.resolution!=s.resolution)
                    {last=new Batch{resolution=s.resolution,start=surfaces.Count,end=surfaces.Count,distortion=s.blend==FxBlend.Distortion};batches.Add(last);}
                    surfaces.Add(s);last.end=surfaces.Count;
                }
                // Reserve the potential optical batch before allocation. Empty optics may later remove it.
                Batch opticalBatch=null;bool opticalBatchNew=false;
                if(hasOptics)
                {
                    opticalBatch=batches.Count>0?batches[batches.Count-1]:null;
                    if(opticalBatch==null||opticalBatch.distortion||opticalBatch.resolution!=(FxResolution)settings.optics.resolution)
                    {opticalBatch=new Batch{resolution=(FxResolution)settings.optics.resolution,start=surfaces.Count,end=surfaces.Count};batches.Add(opticalBatch);opticalBatchNew=true;}
                    opticalBatch.optics=true;
                }
                if(batches.Count==0){Release();return false;}
                foreach(var batch in batches)needed[Index(batch.resolution)]=true;
                long budget=(long)source.width*source.height*33+(hasOptics?LensFlareSettings.MaximumEmitters*4:0);
                for(int i=0;i<3;i++)if(needed[i])budget+=(long)Divide(source.width,1<<i)*Divide(source.height,1<<i)*24;
                if(budget>(long)settings.maximumTargetMiB*1024*1024)return Fail("Joint owned targets exceed the explicit memory budget");
                var shader=Resources.Load<Shader>("HeavyFx");
                if(shader==null||!shader.isSupported||!Supported(GraphicsFormat.R32G32B32A32_SFloat)||!Supported(GraphicsFormat.R32G32_SFloat)||!Supported(GraphicsFormat.R8_UNorm)||
                    !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat,FormatUsage.Blend)||(hasOptics&&!Supported(GraphicsFormat.R32_SFloat)))
                    return Fail("Joint shaders or float target/blend capabilities unavailable");
                if(!Created||a.width!=source.width||a.height!=source.height)ReleaseTargets();
                if(a==null){a=Allocate(source.width,source.height,RenderTextureFormat.ARGBFloat,"HDR A");b=Allocate(source.width,source.height,RenderTextureFormat.ARGBFloat,"HDR B");repair=Allocate(source.width,source.height,RenderTextureFormat.R8,"current repair mask");}
                if(!shadows.Prepare(shadowLights,hasMedium?settings.medium.shadows:null,false,out reason))return Fail(reason);
                if(!optics.PrepareShared(source,depth,camera,hasOptics?settings.optics:null,timeSeconds,protection,out reason))return Fail(reason);
                needsOptics=optics.ElementCount>0;
                if(hasOptics&&!needsOptics)
                {opticalBatch.optics=false;if(opticalBatchNew)batches.Remove(opticalBatch);}
                if(batches.Count==0){Release();return false;}
                Array.Clear(needed,0,needed.Length);foreach(var batch in batches)needed[Index(batch.resolution)]=true;
                TargetBytes=(long)source.width*source.height*33+(optics.SharedVisibility!=null?(long)optics.SharedVisibility.width*4:0);
                for(int i=0;i<3;i++)
                {
                    if(!needed[i]){ReleaseScratch(i);continue;}
                    int w=Divide(source.width,1<<i),h=Divide(source.height,1<<i);
                    if(scratch[i]==null){scratch[i]=new Scratch();scratch[i].effect=Allocate(w,h,RenderTextureFormat.ARGBFloat,"shared effect /"+(1<<i));scratch[i].range=Allocate(w,h,RenderTextureFormat.RGFloat,"shared range /"+(1<<i));}
                    TargetBytes+=(long)w*h*24;
                }
                if(resolve==null)resolve=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                if(surfaceMaterial==null)surfaceMaterial=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                if(mediumMaterial==null)mediumMaterial=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                void Common(Material material,FxResolution resolution,int phase,Scratch targets,bool readEffect)
                {
                    view.Apply(material);material.SetMatrix("_FxViewProjection",GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)*camera.worldToCameraMatrix);
                    material.SetTexture("_FxDepth",depth.texture);material.SetTexture("_FxProtection",protection);
                    material.SetTexture("_FxDepthRange",targets!=null?targets.range:Texture2D.blackTexture);
                    material.SetTexture("_FxEffect",readEffect&&targets!=null?targets.effect:Texture2D.blackTexture);
                    material.SetTexture("_FxRepair",repair);
                    material.SetTexture("_FxBackground",Texture2D.blackTexture);
                    material.SetVector("_FxInput",new Vector4((int)depth.encoding,protection!=null?1:0,(int)resolution,phase));
                    material.SetVector("_FxLowSize",new Vector4(Divide(source.width,(int)resolution),Divide(source.height,(int)resolution),source.width/(float)source.height,0));
                    material.SetVector("_FxTolerance",new Vector4(hasGeometry?settings.geometry.depthBias:.001f,settings.depthAbsoluteTolerance,settings.depthRelativeTolerance,settings.effectEdgeThreshold));
                    material.SetVector("_HeavyMedium",new Vector4(hasMedium?1:0,hasMedium&&settings.medium.attenuateBackground?1:0,lights.Count>0?1:0,1));
                    if(hasMedium)
                    {
                        var m=settings.medium;material.SetVector("_MediumCenter",m.mediumCenter);material.SetVector("_MediumHalfSize",m.mediumHalfSize);
                        material.SetVector("_MediumParameters",new Vector4(m.extinction,m.anisotropy,m.samplesPerLight,0));material.SetVector("_MediumAlbedo",m.scatteringAlbedo);
                        material.SetVector("_VolumeInput",new Vector4(0,0,m.affectSky?1:0,m.attenuateBackground?1:0));
                    }
                }
                void Light(Material material,int index)
                {
                    if(index<0){material.DisableKeyword("SCENE_LIGHT_SHADOWS");return;}
                    var light=lights[index];var forward=light.rotation.normalized*Vector3.forward;
                    shadows.Bind(material);shadows.BindSingle(material,index);
                    material.SetVector("_VolumeLightPositionRange",new Vector4(light.position.x,light.position.y,light.position.z,light.range));
                    material.SetVector("_VolumeLightDirectionOuter",new Vector4(forward.x,forward.y,forward.z,Mathf.Cos(light.outerAngle*Mathf.Deg2Rad*.5f)));
                    material.SetVector("_VolumeLightRadianceInner",new Vector4(light.linearRadiance.x,light.linearRadiance.y,light.linearRadiance.z,Mathf.Cos(light.innerAngle*Mathf.Deg2Rad*.5f)));
                    material.SetFloat("_VolumeFalloff",light.falloffExponent);
                }
                void Submit(LowResolutionFxSurface s,RenderTexture target,int pass)
                {
                    var commands=new CommandBuffer{name="Toolkit joint current surface"};
                    try
                    {
                        commands.SetRenderTarget(target);commands.SetViewport(new Rect(0,0,target.width,target.height));
                        if(s.renderer!=null)commands.DrawRenderer(s.renderer,surfaceMaterial,s.submesh,pass);
                        else commands.DrawMesh(s.mesh,s.localToWorld,surfaceMaterial,s.submesh,pass);
                        Graphics.ExecuteCommandBuffer(commands);DrawCalls++;
                    }
                    finally{commands.Release();}
                }
                void DrawBatch(Batch batch,RenderTexture target,int phase,Scratch targets)
                {
                    int before=DrawCalls;
                    if(batch.medium)
                    {
                        Common(mediumMaterial,batch.resolution,phase,targets,phase==2);
                        Graphics.Blit(current,target,mediumMaterial,phase==2?9:8);DrawCalls++;
                        for(int i=0;i<lights.Count;i++){Light(mediumMaterial,i);Graphics.Blit(current,target,mediumMaterial,10);DrawCalls++;}
                    }
                    for(int index=batch.start;index<batch.end;index++)
                    {
                        var s=surfaces[index];Common(surfaceMaterial,batch.resolution,phase,targets,phase==2);
                        surfaceMaterial.SetTexture("_FxBackground",batch.distortion&&phase==2?current:Texture2D.blackTexture);
                        surfaceMaterial.SetTexture("_FxTexture",s.texture!=null?s.texture:Texture2D.whiteTexture);
                        surfaceMaterial.SetVector("_FxTextureST",s.textureST);
                        surfaceMaterial.SetVector("_FxRadiance",new Vector4(s.linearRadiance.x,s.linearRadiance.y,s.linearRadiance.z,s.opacity));
                        surfaceMaterial.SetVector("_FxSurface",new Vector4((int)s.blend,s.vertexColor?1:0,s.radialSoftness,s.softIntersectionDistance));
                        surfaceMaterial.SetVector("_FxDistortion",new Vector4(s.distortionOffset.x,s.distortionOffset.y,s.distortionTextureScale.x,s.distortionTextureScale.y));
                        surfaceMaterial.SetVector("_FxFlags",new Vector4(s.fog&&surfaceFog?1:0,s.texture!=null?1:0,0,0));
                        surfaceMaterial.SetVector("_HeavyMedium",new Vector4(hasMedium?1:0,hasMedium&&settings.medium.attenuateBackground?1:0,lights.Count>0?1:0,s.fog?1:0));
                        surfaceMaterial.SetFloat("_Cull",(int)s.cull);Light(surfaceMaterial,lights.Count>0?0:-1);
                        Submit(s,target,batch.distortion?(phase==2?6:3):(phase==2?2:1));
                        if(!batch.distortion&&hasMedium&&s.fog&&s.blend==FxBlend.Alpha)
                            for(int i=1;i<lights.Count;i++){Light(surfaceMaterial,i);Submit(s,target,11);}
                    }
                    if(batch.optics)
                    {optics.DrawShared(target,m=>Common(m,batch.resolution,phase,targets,phase==2));DrawCalls++;}
                    if(phase==2)ReplayDrawCalls+=DrawCalls-before;
                }
                var shadowCommands=new CommandBuffer{name="Toolkit joint current medium shadows"};
                try{shadows.Record(shadowCommands);Graphics.ExecuteCommandBuffer(shadowCommands);}finally{shadowCommands.Release();}
                DrawCalls=optics.DrawCalls;
                Common(resolve,FxResolution.Full,0,null,false);Graphics.Blit(source,a,resolve,7);DrawCalls++;current=a;
                for(int i=0;i<3;i++)if(needed[i])
                {Common(resolve,(FxResolution)(1<<i),1,null,false);Graphics.Blit(source,scratch[i].range,resolve,0);DrawCalls++;}
                foreach(var batch in batches)
                {
                    var targets=scratch[Index(batch.resolution)];RenderTexture.active=targets.effect;GL.Clear(false,true,Color.clear);
                    DrawBatch(batch,targets.effect,1,targets);
                    // One current decision per pixel and batch, shared by resolve and all
                    // medium/surface/optical replay producers. Full writes an all-zero mask.
                    Common(resolve,batch.resolution,batch.distortion?1:0,targets,true);
                    Graphics.Blit(current,repair,resolve,12);DrawCalls++;
                    var next=current==a?b:a;Common(resolve,batch.resolution,1,targets,true);resolve.SetTexture("_FxBackground",current);
                    Graphics.Blit(current,next,resolve,batch.distortion?5:4);DrawCalls++;
                    if(batch.resolution!=FxResolution.Full)DrawBatch(batch,next,2,targets);
                    current=next;
                }
                hasFrame=true;frame=new Frame(this);return true;
            }
            catch(Exception error){return Fail("Joint FX render failed: "+error.GetType().Name);}
            finally{RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;}
        }
        private bool Inputs(RenderTexture source,FogVolumeDepth depth,RenderTexture protection)
        {
            if(!Valid(source)||!Valid(depth.texture)||source.sRGB||depth.texture.sRGB||source==depth.texture||Owns(source)||Owns(depth.texture)||
                source.width!=depth.texture.width||source.height!=depth.texture.height||
                (source.format!=RenderTextureFormat.ARGBFloat&&source.format!=RenderTextureFormat.ARGBHalf&&source.format!=RenderTextureFormat.RGB111110Float)||
                (depth.encoding!=FogDepthEncoding.LinearEye&&depth.encoding!=FogDepthEncoding.Device)||
                (depth.encoding==FogDepthEncoding.LinearEye&&depth.texture.format!=RenderTextureFormat.RFloat&&depth.texture.format!=RenderTextureFormat.RHalf)||
                (depth.encoding==FogDepthEncoding.Device&&depth.texture.format!=RenderTextureFormat.RFloat&&depth.texture.format!=RenderTextureFormat.Depth))return false;
            return protection==null||(Valid(protection)&&!protection.sRGB&&!Owns(protection)&&protection!=source&&protection!=depth.texture&&
                protection.width==source.width&&protection.height==source.height&&(protection.format==RenderTextureFormat.R8||protection.format==RenderTextureFormat.RFloat));
        }
        private static bool Valid(RenderTexture t)=>t!=null&&t.IsCreated()&&t.width<=16384&&t.height<=16384&&t.antiAliasing==1&&!t.useMipMap&&!t.useDynamicScale&&t.dimension==TextureDimension.Tex2D&&t.volumeDepth==1;
        private static bool Range(float v,float lo,float hi)=>FogVolumeSettings.Range(v,lo,hi);
        private static bool Supported(GraphicsFormat f)=>SystemInfo.IsFormatSupported(f,FormatUsage.Render)&&SystemInfo.IsFormatSupported(f,FormatUsage.Sample);
        private static bool ValidResolution(FxResolution r)=>r==FxResolution.Full||r==FxResolution.Half||r==FxResolution.Quarter;
        private static int Index(FxResolution r)=>r==FxResolution.Full?0:r==FxResolution.Half?1:2;
        private static int Divide(int value,int divisor)=>(value+divisor-1)/divisor;
        private bool Owns(RenderTexture t)
        {
            if(t==null)return false;if(t==a||t==b||t==repair||t==optics.SharedVisibility||t==shadows.Atlas)return true;
            foreach(var s in scratch)if(s!=null&&(t==s.effect||t==s.range))return true;return false;
        }
        private static RenderTexture Allocate(int w,int h,RenderTextureFormat format,string name)
        {
            var t=new RenderTexture(w,h,0,format,RenderTextureReadWrite.Linear){name="Toolkit joint FX "+name,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave};
            var expected=format==RenderTextureFormat.ARGBFloat?GraphicsFormat.R32G32B32A32_SFloat:format==RenderTextureFormat.R8?GraphicsFormat.R8_UNorm:GraphicsFormat.R32G32_SFloat;
            if(!t.Create()||t.graphicsFormat!=expected){t.Release();UnityEngine.Object.Destroy(t);throw new InvalidOperationException("Joint target allocation failed");}return t;
        }
        private static void DestroyTarget(RenderTexture target){if(target!=null){target.Release();UnityEngine.Object.Destroy(target);}}
        private void ReleaseScratch(int i){var s=scratch[i];if(s==null)return;DestroyTarget(s.effect);DestroyTarget(s.range);scratch[i]=null;}
        private void ReleaseTargets()
        {
            if(Owns(RenderTexture.active))RenderTexture.active=null;
            DestroyTarget(a);DestroyTarget(b);DestroyTarget(repair);a=b=current=repair=null;for(int i=0;i<3;i++)ReleaseScratch(i);hasFrame=false;TargetBytes=0;
        }
        private void Release()
        {
            ReleaseTargets();shadows.Dispose();optics.Dispose();needsOptics=false;
            foreach(var material in new[]{resolve,surfaceMaterial,mediumMaterial})if(material!=null)UnityEngine.Object.Destroy(material);
            resolve=surfaceMaterial=mediumMaterial=null;surfaces.Clear();batches.Clear();lights.Clear();shadowLights.Clear();DrawCalls=ReplayDrawCalls=0;
        }
        private bool Fail(string reason){Release();UnavailableReason=reason;return false;}
        public void Dispose(){generation++;Release();}
    }
}
