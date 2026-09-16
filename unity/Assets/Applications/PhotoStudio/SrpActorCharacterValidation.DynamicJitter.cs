using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using GakumasPhotoMode.Examples;

namespace GakumasPhotoMode
{
    public sealed partial class SrpActorCharacterValidation
    {
        [Serializable] private sealed class DynamicJitterMetric
        {
            public string view,profile,quality,variant,region;
            public int frame,pixels;
            public float mappedMse,maximumMappedError;
        }
        [Serializable] private sealed class DynamicJitterDiagnostics
        {
            public string schema="photo-studio.character-dynamic-jitter.v1";
            public bool rejectMixedSurfaceHistory;
            public bool pairedPolicy;
            public string scope="Same-time16sample native spatial references. Camera-only translation, animation-only time, and static-camera/actor moving opaque panel are separate. Actor-visible and silhouette masks are image-difference proxies, not semantic hair labels. Newly revealed actor mask has physical reveal meaning only in the panel profile. No original-game/mobile/fullpost acceptance.";
            public List<DynamicJitterMetric> measurements=new List<DynamicJitterMetric>();
        }
        private void VerifyDesktopDynamicJitter(Report report,DesktopHostExample example,Transform head,Bounds bounds,
            Action<int> view,Func<string,float,Color[]> run)
        {
            var s=example.Configuration;var camera=example.RenderCamera;var observations=new DynamicJitterDiagnostics();
            var originalProjection=camera.projectionMatrix;var originalPosition=camera.transform.position;var originalRotation=camera.transform.rotation;
            var originalOutput=s.scene.output;var originalDepth=s.scene.depthStencil;var originalNormal=s.scene.normalIdentity;
            var originalBase=s.scene.materialBase;var originalMos=s.scene.materialMos;var originalTarget=camera.targetTexture;
            var panel=s.scene.surfaces[2].renderer.transform;var panelPosition=panel.position;var panelRotation=panel.rotation;var panelScale=panel.localScale;
            var lowTargets=new Dictionary<int,RenderTexture[]>();
            RenderTexture Target(int size,GraphicsFormat color,GraphicsFormat depth=GraphicsFormat.None)
            {
                var t=Own(new RenderTexture(new RenderTextureDescriptor(size,size,color,0){depthStencilFormat=depth}) {name="Dynamic character actual low geometry"});
                if(!t.Create())throw new InvalidOperationException("Dynamic character target allocation");return t;
            }
            void Attach(int level)
            {
                example.ResetHistory();s.fsr.enabled=level!=0;
                if(level==0)
                {s.scene.output=originalOutput;s.scene.depthStencil=originalDepth;s.scene.normalIdentity=originalNormal;s.scene.materialBase=originalBase;s.scene.materialMos=originalMos;camera.targetTexture=originalTarget;return;}
                s.fsr.quality=level==1?FsrQuality.Quality:FsrQuality.Performance;
                if(!s.fsr.TryGetRenderSize(new Vector2Int(Size,Size),out var size))throw new InvalidOperationException("Dynamic FSR size");
                if(!lowTargets.TryGetValue(size.x,out var targets))
                {
                    targets=new[]{Target(size.x,GraphicsFormat.B10G11R11_UFloatPack32),Target(size.x,GraphicsFormat.None,GraphicsFormat.D32_SFloat_S8_UInt),
                        Target(size.x,GraphicsFormat.R16G16B16A16_SFloat),Target(size.x,GraphicsFormat.R8G8B8A8_SRGB),Target(size.x,GraphicsFormat.R8G8B8A8_UNorm)};
                    lowTargets.Add(size.x,targets);
                }
                s.scene.output=targets[0];s.scene.depthStencil=targets[1];s.scene.normalIdentity=targets[2];s.scene.materialBase=targets[3];s.scene.materialMos=targets[4];camera.targetTexture=targets[0];
            }
            void Check(string name,bool ok,float value=0)=>report.checks.Add(new Check {name="desktop-character-dynamic-"+name,accepted=ok,value=value});
            Color[] Average(List<Color[]> frames)
            {
                var result=new Color[Size*Size];for(int p=0;p<result.Length;p++)for(int c=0;c<4;c++)
                {double total=0;foreach(var frame in frames)total+=frame[p][c];result[p][c]=(float)(total/frames.Count);}return result;
            }
            float Mapped(float v){v=Mathf.Max(0,v);return v/(1+v);}
            DynamicJitterMetric Measure(string label,string profile,string quality,string variant,string region,int frame,Color[] actual,Color[] reference,bool[] mask)
            {
                var row=new DynamicJitterMetric {view=label,profile=profile,quality=quality,variant=variant,region=region,frame=frame};double error=0;
                for(int p=0;p<mask.Length;p++)if(mask[p])
                {
                    row.pixels++;for(int c=0;c<3;c++){float d=Mathf.Abs(Mapped(actual[p][c])-Mapped(reference[p][c]));error+=(double)d*d;row.maximumMappedError=Mathf.Max(row.maximumMappedError,d);}
                }
                row.mappedMse=(float)(error/Math.Max(1,row.pixels*3));observations.measurements.Add(row);return row;
            }
            try
            {
                s.actorMotion.enabled=s.includeSceneMotion=true;
                s.effects.enabled=s.depthOfField.enabled=s.motionBlur.enabled=s.bloom.enabled=s.fsr.enabled=false;s.reflections.enabled=s.planar.enabled=false;
                s.fsr.encoding=FsrInputEncoding.LinearHdr;s.fsr.stabilizeLumaGradients=true;s.fsr.backend=FsrBackend.Compute;s.fsr.allowRasterFallback=false;s.fsrOutputSize=new Vector2Int(Size,Size);
                s.diffusion.enabled=true;s.diffusion.intensity=0;s.diffusion.radiusPixels=0;s.diffusion.downsample=1;s.colorGrade=null;
                s.temporal.historyWeight=.95f;s.temporal.maximumHistory=32;s.temporal.varianceGamma=.9f;s.temporal.reactiveThreshold=.2f;
                s.temporal.rejectMixedSurfaceHistory=Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_DYNAMIC_COHERENT")=="1";
                observations.rejectMixedSurfaceHistory=s.temporal.rejectMixedSurfaceHistory;
                observations.pairedPolicy=Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_DYNAMIC_PAIRED_POLICY")=="1";
                if(observations.pairedPolicy&&!observations.rejectMixedSurfaceHistory)throw new InvalidOperationException("Paired policy requires explicit coherent mode");
                var angles=Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_JITTER_FRONT_ONLY")=="1"?new[]{0}:new[]{0,90,180};
                var levels=Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_DYNAMIC_QUALITY_ONLY")=="1"?new[]{1}:new[]{0,1,2};
                foreach(int angle in angles)foreach(string profile in new[]{"camera","animation","reveal"})
                {
                    Attach(0);panel.localScale=panelScale;app.EvaluateMotion(.7f);view(angle);
                    var target=head.position+head.up*(bounds.size.y*.025f);var direction=(camera.transform.position-bounds.center).normalized;
                    camera.transform.position=target+direction*(bounds.size.y*.62f);camera.transform.LookAt(target);camera.ResetProjectionMatrix();
                    var origin=camera.transform.position;var rotation=camera.transform.rotation;var basis=camera.projectionMatrix;var right=camera.transform.right;
                    string label="view-"+angle,full=label+"-"+profile;var references=new Dictionary<int,Color[]>();
                    var masks=new Dictionary<int,bool[]>();var edges=new Dictionary<int,bool[]>();var reveals=new Dictionary<int,bool[]>();
                    float Set(int frame,Vector2 offset)
                    {
                        camera.transform.SetPositionAndRotation(origin+(profile=="camera"?right*(bounds.size.y*.0075f*(frame-7.5f)):Vector3.zero),rotation);
                        if(profile=="reveal")
                        {
                            panel.SetPositionAndRotation(target+direction*(bounds.size.y*.18f)+right*(bounds.size.y*.03f*(frame-7.5f)),rotation);
                            panel.localScale=new Vector3(bounds.size.y*.16f,bounds.size.y*.45f,bounds.size.y*.015f);
                        }
                        if(!TemporalProjectionJitter.TryCreate(basis,new Vector2Int(s.scene.output.width,s.scene.output.height),offset,out var sample))throw new InvalidOperationException("Dynamic projection rejected");
                        camera.projectionMatrix=sample.projection;s.temporal.jitterUv=s.motionBlurJitterUv=sample.correctionUv;
                        return profile=="animation"?.7f+frame*.02f:.7f;
                    }
                    Color[] Render(string name,int frame,Vector2 offset)=>run("dynamic-"+full+"-"+name,Set(frame,offset));
                    bool[] previousVisible=null;Color[] firstUnjittered=null;
                    // References are rendered before the measured histories, at
                    // each exact trajectory time. Every reference draw is cold.
                    for(int frame=7;frame<16;frame++)
                    {
                        s.temporal.enabled=false;example.ResetHistory();var raw=Render("reference-unjittered-"+frame,frame,Vector2.zero);
                        int mask=camera.cullingMask;Color[] excluded;
                        try{camera.cullingMask=1<<22;example.ResetHistory();excluded=Render("reference-excluded-"+frame,frame,Vector2.zero);}
                        finally{camera.cullingMask=mask;}
                        var visible=new bool[Size*Size];for(int p=0;p<visible.Length;p++)visible[p]=Mathf.Abs(raw[p].r-excluded[p].r)+Mathf.Abs(raw[p].g-excluded[p].g)+Mathf.Abs(raw[p].b-excluded[p].b)>.01f;
                        if(frame==7){previousVisible=visible;firstUnjittered=raw;continue;}
                        var area=new bool[Size*Size];var edge=new bool[Size*Size];var reveal=new bool[Size*Size];
                        for(int y=0;y<Size;y++)for(int x=0;x<Size;x++)
                        {
                            int p=x+y*Size;bool inside=false,outside=false;
                            for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)if(x+dx>=0&&x+dx<Size&&y+dy>=0&&y+dy<Size)
                            {bool v=visible[x+dx+(y+dy)*Size];inside|=v;outside|=!v;}
                            area[p]=inside;edge[p]=inside&&outside;reveal[p]=visible[p]&&!previousVisible[p];
                        }
                        masks.Add(frame,area);edges.Add(frame,edge);reveals.Add(frame,reveal);previousVisible=visible;
                        var grid=new List<Color[]>();for(int y=0;y<4;y++)for(int x=0;x<4;x++)
                        {example.ResetHistory();grid.Add(Render("reference-"+frame+"-"+x+"-"+y,frame,new Vector2((x+.5f)/4-.5f,(y+.5f)/4-.5f)));}
                        var integrated=Average(grid);references.Add(frame,integrated);Save("desktop-character-dynamic-"+full+"-reference-4x4-"+frame,integrated);
                        if(frame==15)Check(full+"-trajectory-positive-control",Changed(firstUnjittered,raw,.001f)>100,Changed(firstUnjittered,raw,.001f));
                    }
                    foreach(int level in levels)
                    {
                        Attach(level);string quality=level==0?"Native":s.fsr.quality.ToString();
                        var areaError=new Dictionary<string,double>();var edgeError=new Dictionary<string,double>();int areaPixels=0,edgePixels=0,revealPixels=0;
                        Color[] unjitteredLast=null;
                        var variants=observations.pairedPolicy?new[]{"unjittered","cold","legacy-warm","warm"}:new[]{"unjittered","cold","warm"};
                        foreach(string variant in variants)
                        {
                            s.temporal.rejectMixedSurfaceHistory=observations.rejectMixedSurfaceHistory&&variant!="legacy-warm";
                            example.ResetHistory();s.temporal.enabled=variant!="unjittered";double a=0,e=0;
                            for(int frame=variant=="unjittered"?8:0;frame<16;frame++)
                            {
                                if(variant!="warm"&&variant!="legacy-warm")example.ResetHistory();var pixels=Render(quality+"-"+variant+"-"+frame,frame,variant=="unjittered"?Vector2.zero:TemporalProjectionJitter.Offset((uint)frame));
                                if(variant=="unjittered"&&frame==15)unjitteredLast=pixels;
                                if(frame<8)continue;
                                var ar=Measure(label,profile,quality,variant,"actor-dependent",frame,pixels,references[frame],masks[frame]);
                                var ed=Measure(label,profile,quality,variant,"silhouette-band",frame,pixels,references[frame],edges[frame]);
                                var re=Measure(label,profile,quality,variant,"newly-visible",frame,pixels,references[frame],reveals[frame]);
                                a+=ar.mappedMse*ar.pixels;e+=ed.mappedMse*ed.pixels;
                                if(variant=="warm"){areaPixels+=ar.pixels;edgePixels+=ed.pixels;revealPixels+=re.pixels;}
                            }
                            areaError.Add(variant,a);edgeError.Add(variant,e);
                        }
                        string name=full+"-"+quality;
                        Check(name+"-actor-region-visible",areaPixels>10000,areaPixels);Check(name+"-silhouette-region-visible",edgePixels>100,edgePixels);
                        if(profile=="reveal")Check(name+"-actual-reveal-region-visible",revealPixels>100,revealPixels);
                        Check(name+"-history-improves-actor-reference",areaError["warm"]<areaError["cold"],(float)(areaError["warm"]/Math.Max(areaError["cold"],1e-20)));
                        Check(name+"-history-improves-silhouette-reference",edgeError["warm"]<edgeError["cold"],(float)(edgeError["warm"]/Math.Max(edgeError["cold"],1e-20)));
                        if(observations.pairedPolicy)
                        {
                            example.ResetHistory();s.temporal.enabled=false;var replay=Render(quality+"-trajectory-replay-15",15,Vector2.zero);
                            Check(name+"-trajectory-replay-exact",MaximumDifference(unjitteredLast,replay)==0,MaximumDifference(unjitteredLast,replay));
                        }
                    }
                }
            }
            finally
            {
                example.ResetHistory();Attach(0);camera.projectionMatrix=originalProjection;camera.transform.SetPositionAndRotation(originalPosition,originalRotation);
                panel.SetPositionAndRotation(panelPosition,panelRotation);panel.localScale=panelScale;
                s.temporal.jitterUv=s.motionBlurJitterUv=Vector2.zero;
                File.WriteAllText(Path.Combine(directory,"character-dynamic-jitter-diagnostics.json"),JsonUtility.ToJson(observations,true));
            }
        }
    }
}
