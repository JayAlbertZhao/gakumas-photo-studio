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
        [Serializable] private sealed class JitterQuality
        {
            public string view,variant;
            public int pixels,frames;
            public float meanMappedMse,temporalMappedVariance,lastMappedMse;
        }
        [Serializable] private sealed class JitterDiagnostics
        {
            public string schema="photo-studio.character-jitter.v1";
            public string scope="Static .7s head close-up; independent4x4 spatial HDR integration at512. Native512 and actual342/256geometry before FSR to512. Actor visibility difference at matching unjittered projection, dilated1pixel. Eight centered Halton phases, two cycles. No original-image/mobile/animated-occlusion acceptance inferred.";
            public List<JitterQuality> measurements=new List<JitterQuality>();
        }
        private void VerifyDesktopCharacterJitter(Report report,DesktopHostExample example,Transform head,Bounds bounds,
            Action<int> view,Func<string,float,Color[]> run)
        {
            var s=example.Configuration;var camera=example.RenderCamera;var observations=new JitterDiagnostics();
            var originalProjection=camera.projectionMatrix;var originalPosition=camera.transform.position;var originalRotation=camera.transform.rotation;
            var lut=s.colorGrade;var sampling=s.colorGradeSampling;
            var originalOutput=s.scene.output;var originalDepth=s.scene.depthStencil;var originalNormal=s.scene.normalIdentity;
            var originalBase=s.scene.materialBase;var originalMos=s.scene.materialMos;var originalTarget=camera.targetTexture;
            var lowTargets=new Dictionary<int,RenderTexture[]>();
            RenderTexture Target(int size,GraphicsFormat color,GraphicsFormat depth=GraphicsFormat.None)
            {
                var t=Own(new RenderTexture(new RenderTextureDescriptor(size,size,color,0){depthStencilFormat=depth}) {name="Character jitter actual low geometry"});
                if(!t.Create())throw new InvalidOperationException("Character jitter target allocation");return t;
            }
            void Attach(int level)
            {
                example.ResetHistory();s.fsr.enabled=level!=0;
                if(level==0)
                {
                    s.scene.output=originalOutput;s.scene.depthStencil=originalDepth;s.scene.normalIdentity=originalNormal;
                    s.scene.materialBase=originalBase;s.scene.materialMos=originalMos;camera.targetTexture=originalTarget;return;
                }
                s.fsr.quality=level==1?FsrQuality.Quality:FsrQuality.Performance;
                if(!s.fsr.TryGetRenderSize(new Vector2Int(Size,Size),out var size))throw new InvalidOperationException("Character jitter FSR size");
                if(!lowTargets.TryGetValue(size.x,out var targets))
                {
                    targets=new[]{Target(size.x,GraphicsFormat.B10G11R11_UFloatPack32),Target(size.x,GraphicsFormat.None,GraphicsFormat.D32_SFloat_S8_UInt),
                        Target(size.x,GraphicsFormat.R16G16B16A16_SFloat),Target(size.x,GraphicsFormat.R8G8B8A8_SRGB),Target(size.x,GraphicsFormat.R8G8B8A8_UNorm)};
                    lowTargets.Add(size.x,targets);
                }
                s.scene.output=targets[0];s.scene.depthStencil=targets[1];s.scene.normalIdentity=targets[2];s.scene.materialBase=targets[3];s.scene.materialMos=targets[4];camera.targetTexture=targets[0];
            }
            void Check(string name,bool ok,float value=0)=>report.checks.Add(new Check {name="desktop-character-jitter-"+name,accepted=ok,value=value});
            float Mapped(float x){x=Mathf.Max(0,x);return x/(1+x);}
            Color[] Average(List<Color[]> frames)
            {
                var result=new Color[Size*Size];
                for(int p=0;p<result.Length;p++)for(int c=0;c<4;c++){double sum=0;foreach(var frame in frames)sum+=frame[p][c];result[p][c]=(float)(sum/frames.Count);}
                return result;
            }
            JitterQuality Measure(string label,string variant,List<Color[]> frames,Color[] reference,bool[] mask)
            {
                var row=new JitterQuality {view=label,variant=variant,frames=frames.Count};double error=0,variance=0,last=0;
                for(int p=0;p<mask.Length;p++)if(mask[p])
                {
                    row.pixels++;
                    for(int c=0;c<3;c++)
                    {
                        double sum=0,square=0,target=Mapped(reference[p][c]);
                        foreach(var frame in frames){double v=Mapped(frame[p][c]);sum+=v;square+=v*v;error+=(v-target)*(v-target);}
                        variance+=Math.Max(0,square/frames.Count-Math.Pow(sum/frames.Count,2));
                        double d=Mapped(frames[frames.Count-1][p][c])-target;last+=d*d;
                    }
                }
                row.meanMappedMse=(float)(error/Math.Max(1,row.pixels*3*frames.Count));row.temporalMappedVariance=(float)(variance/Math.Max(1,row.pixels*3));row.lastMappedMse=(float)(last/Math.Max(1,row.pixels*3));
                observations.measurements.Add(row);return row;
            }
            try
            {
                s.actorMotion.enabled=s.includeSceneMotion=true;
                s.effects.enabled=s.depthOfField.enabled=s.motionBlur.enabled=s.bloom.enabled=s.fsr.enabled=false;
                s.reflections.enabled=s.planar.enabled=false;
                s.fsr.encoding=FsrInputEncoding.LinearHdr;s.fsr.stabilizeLumaGradients=true;s.fsr.backend=FsrBackend.Compute;s.fsr.allowRasterFallback=false;s.fsrOutputSize=new Vector2Int(Size,Size);
                // A proven exact zero-effect Float4 copy permits raw HDR output
                // without grading each sample before independent integration.
                s.diffusion.enabled=true;s.diffusion.intensity=0;s.diffusion.radiusPixels=0;s.diffusion.downsample=1;s.colorGrade=null;
                s.temporal.historyWeight=.95f;s.temporal.maximumHistory=32;s.temporal.varianceGamma=.9f;s.temporal.reactiveThreshold=.2f;
                int[] angles=Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_JITTER_FRONT_ONLY")=="1"?new[]{0}:new[]{0,90,180};
                foreach(int angle in angles)
                {
                    Attach(0);
                    app.EvaluateMotion(.7f);view(angle);
                    var target=head.position+head.up*(bounds.size.y*.025f);
                    camera.transform.position=target+(camera.transform.position-bounds.center).normalized*(bounds.size.y*.62f);
                    camera.transform.LookAt(target);camera.ResetProjectionMatrix();var basis=camera.projectionMatrix;
                    string label="view-"+angle,casePrefix="";
                    void Apply(Vector2 offset,int correction=1)
                    {
                        if(!TemporalProjectionJitter.TryCreate(basis,new Vector2Int(s.scene.output.width,s.scene.output.height),offset,out var sample))throw new InvalidOperationException("Projection jitter input rejected");
                        camera.projectionMatrix=sample.projection;s.temporal.jitterUv=sample.correctionUv*correction;s.motionBlurJitterUv=sample.correctionUv;
                    }
                    Color[] Render(string name)=>run("jitter-"+label+"-"+casePrefix+name,.7f);
                    s.temporal.enabled=false;Apply(Vector2.zero);example.ResetHistory();var raw=Render("unjittered");
                    int maskBefore=camera.cullingMask;Color[] excluded;
                    try{camera.cullingMask=1<<22;example.ResetHistory();excluded=Render("actor-excluded");}
                    finally{camera.cullingMask=maskBefore;}
                    var grid=new List<Color[]>();
                    for(int y=0;y<4;y++)for(int x=0;x<4;x++)
                    {Apply(new Vector2((x+.5f)/4-.5f,(y+.5f)/4-.5f));example.ResetHistory();grid.Add(Render("reference-"+x+"-"+y));}
                    var reference=Average(grid);Save("desktop-character-jitter-"+label+"-reference-4x4",reference);
                    var visible=new bool[Size*Size];for(int p=0;p<visible.Length;p++)visible[p]=Mathf.Abs(raw[p].r-excluded[p].r)+Mathf.Abs(raw[p].g-excluded[p].g)+Mathf.Abs(raw[p].b-excluded[p].b)>.01f;
                    var mask=new bool[Size*Size];for(int y=0;y<Size;y++)for(int x=0;x<Size;x++)for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                        if(x+dx>=0&&x+dx<Size&&y+dy>=0&&y+dy<Size)mask[x+y*Size]|=visible[x+dx+(y+dy)*Size];
                    foreach(int level in new[]{0,1,2})
                    {
                    Attach(level);casePrefix=(level==0?"Native":s.fsr.quality.ToString())+"-";string settingLabel=label+"-"+casePrefix;
                    Apply(Vector2.zero);s.temporal.enabled=false;example.ResetHistory();var unfiltered=Render("unjittered");
                    var unjittered=Measure(settingLabel,"unjittered",new List<Color[]>{unfiltered},reference,mask);Check(settingLabel+"actor-closeup-visible",unjittered.pixels>10000,unjittered.pixels);
                    List<Color[]> Sequence(string name,bool cold,int correction)
                    {
                        example.ResetHistory();s.temporal.enabled=true;var frames=new List<Color[]>();
                        for(uint i=0;i<16;i++)
                        {
                            Apply(TemporalProjectionJitter.Offset(i),correction);if(cold)example.ResetHistory();
                            var frame=Render(name+"-"+i);if(i>=8)frames.Add(frame);
                        }
                        return frames;
                    }
                    var cold=Measure(settingLabel,"cold-correct",Sequence("cold-correct",true,1),reference,mask);
                    var warm=Measure(settingLabel,"warm-correct",Sequence("warm-correct",false,1),reference,mask);
                    var wrong=Measure(settingLabel,"warm-wrong-sign",Sequence("warm-wrong-sign",false,-1),reference,mask);
                    var ignored=Measure(settingLabel,"warm-no-correction",Sequence("warm-no-correction",false,0),reference,mask);
                    Check(settingLabel+"history-improves-spatial-reference",warm.meanMappedMse<cold.meanMappedMse,warm.meanMappedMse/Mathf.Max(cold.meanMappedMse,1e-20f));
                    Check(settingLabel+"history-improves-unjittered-aliasing",warm.meanMappedMse<unjittered.meanMappedMse,warm.meanMappedMse/Mathf.Max(unjittered.meanMappedMse,1e-20f));
                    Check(settingLabel+"history-reduces-static-phase-variance",warm.temporalMappedVariance<cold.temporalMappedVariance,warm.temporalMappedVariance/Mathf.Max(cold.temporalMappedVariance,1e-20f));
                    Check(settingLabel+"correct-sign-beats-wrong-sign",warm.meanMappedMse<wrong.meanMappedMse,warm.meanMappedMse/Mathf.Max(wrong.meanMappedMse,1e-20f));
                    Check(settingLabel+"correct-sign-beats-ignored-correction",warm.meanMappedMse<ignored.meanMappedMse,warm.meanMappedMse/Mathf.Max(ignored.meanMappedMse,1e-20f));
                    Apply(Vector2.zero);example.ResetHistory();s.temporal.enabled=false;var restored=Render("restored");
                    Check(settingLabel+"reset-projection-restores-unfiltered-frame",MaximumDifference(unfiltered,restored)==0,MaximumDifference(unfiltered,restored));
                    }
                }
            }
            finally
            {
                example.ResetHistory();camera.projectionMatrix=originalProjection;camera.transform.SetPositionAndRotation(originalPosition,originalRotation);
                s.temporal.jitterUv=s.motionBlurJitterUv=Vector2.zero;s.colorGrade=lut;s.colorGradeSampling=sampling;s.diffusion.enabled=false;
                Attach(0);
                File.WriteAllText(Path.Combine(directory,"character-jitter-diagnostics.json"),JsonUtility.ToJson(observations,true));
            }
        }
    }
}
