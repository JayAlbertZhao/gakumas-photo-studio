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
        [Serializable] private sealed class PostMeasurement
        {
            public string view,variant;
            public int actorPixels,headPixels,actorChanged,headChanged;
            public float actorMeanError,headMeanError,maximumDifference;
        }
        [Serializable] private sealed class PostMeasurements
        {
            public string schema="photo-studio.character-post-diagnostics.v1";
            public string scope="One caller-supplied character/costume. Linear output differences against same-time native-resolution and module-off controls; no original-image, jitter convergence or mobile-quality acceptance inferred.";
            public float cameraStepInCharacterHeights=.04f;
            public string colorLutSampling="ExplicitFloatTrilinear";
            public float characterHeight;
            public float[] sampleTimes={.7f,.72f,.74f,.76f};
            public List<PostMeasurement> measurements=new List<PostMeasurement>();
            public List<IdlePostMeasurement> idleWholeImage=new List<IdlePostMeasurement>();
        }
        [Serializable] private sealed class IdlePostMeasurement
        {public string view;public float maximumDifference;public int changedPixels;}
        private void VerifyDesktopPostCharacter(Report report,DesktopHostExample example,Transform head,Bounds bounds,
            Action<int> view,Func<string,float,Color[]> run)
        {
            var s=example.Configuration;var camera=example.RenderCamera;var observations=new PostMeasurements {characterHeight=bounds.size.y};
            void Check(string name,bool ok,float value=0)=>report.checks.Add(new Check {name="desktop-character-post-"+name,accepted=ok,value=value});
            var originalOutput=s.scene.output;var originalDepth=s.scene.depthStencil;var originalNormal=s.scene.normalIdentity;
            var originalBase=s.scene.materialBase;var originalMos=s.scene.materialMos;var originalTarget=camera.targetTexture;
            var originalSampling=s.colorGradeSampling;
            var lowTargets=new Dictionary<int,RenderTexture[]>();
            RenderTexture Target(int size,GraphicsFormat color,GraphicsFormat depth=GraphicsFormat.None)
            {
                var t=Own(new RenderTexture(new RenderTextureDescriptor(size,size,color,0){depthStencilFormat=depth}) {name="Character post real low geometry"});
                if(!t.Create())throw new InvalidOperationException("Character post target allocation");return t;
            }
            void Attach(bool reconstruct,FsrQuality quality)
            {
                example.ResetHistory();s.fsr.enabled=reconstruct;s.fsr.quality=quality;
                if(!reconstruct)
                {
                    s.scene.output=originalOutput;s.scene.depthStencil=originalDepth;s.scene.normalIdentity=originalNormal;
                    s.scene.materialBase=originalBase;s.scene.materialMos=originalMos;camera.targetTexture=originalTarget;return;
                }
                if(!s.fsr.TryGetRenderSize(new Vector2Int(Size,Size),out var size))throw new InvalidOperationException("Character post FSR size");
                if(!lowTargets.TryGetValue(size.x,out var targets))
                {
                    targets=new[]{Target(size.x,GraphicsFormat.B10G11R11_UFloatPack32),Target(size.x,GraphicsFormat.None,GraphicsFormat.D32_SFloat_S8_UInt),
                        Target(size.x,GraphicsFormat.R16G16B16A16_SFloat),Target(size.x,GraphicsFormat.R8G8B8A8_SRGB),Target(size.x,GraphicsFormat.R8G8B8A8_UNorm)};
                    lowTargets.Add(size.x,targets);
                }
                s.scene.output=targets[0];s.scene.depthStencil=targets[1];s.scene.normalIdentity=targets[2];s.scene.materialBase=targets[3];s.scene.materialMos=targets[4];camera.targetTexture=targets[0];
            }
            float[] times=observations.sampleTimes;
            Vector3 viewPosition=camera.transform.position;
            Color[][] Sequence(string name,bool pan=true)
            {
                example.ResetHistory();var frames=new Color[times.Length][];
                for(int i=0;i<times.Length;i++)
                {
                    // A measured positive exposure control. The supplied idle
                    // clip alone can move less than the filter's half-pixel
                    // exposure cutoff, so absence of blur there is expected.
                    camera.transform.position=viewPosition+(pan?camera.transform.right*(bounds.size.y*.04f*i):Vector3.zero);
                    frames[i]=run("post-"+name+"-"+i,times[i]);
                }
                return frames;
            }
            RectInt HeadRect()
            {
                var point=camera.WorldToViewportPoint(head.position);float radius=bounds.size.y*.16f;
                var offset=camera.WorldToViewportPoint(head.position+camera.transform.up*radius);
                float r=Mathf.Abs(offset.y-point.y)*Size;
                int left=Mathf.Clamp(Mathf.FloorToInt(point.x*Size-r),0,Size),bottom=Mathf.Clamp(Mathf.FloorToInt(point.y*Size-r),0,Size);
                int right=Mathf.Clamp(Mathf.CeilToInt(point.x*Size+r),left,Size),top=Mathf.Clamp(Mathf.CeilToInt(point.y*Size+r),bottom,Size);
                return new RectInt(left,bottom,right-left,top-bottom);
            }
            PostMeasurement Measure(string label,string variant,Color[] reference,Color[] other,bool[] mask,RectInt headRect)
            {
                var row=new PostMeasurement {view=label,variant=variant,maximumDifference=MaximumDifference(reference,other)};
                double actorError=0,headError=0;
                for(int p=0;p<mask.Length;p++)if(mask[p])
                {
                    float error=(Mathf.Abs(reference[p].r-other[p].r)+Mathf.Abs(reference[p].g-other[p].g)+Mathf.Abs(reference[p].b-other[p].b))/3;
                    row.actorPixels++;actorError+=error;if(error>.00001f)row.actorChanged++;
                    if(headRect.Contains(new Vector2Int(p%Size,p/Size))){row.headPixels++;headError+=error;if(error>.00001f)row.headChanged++;}
                }
                row.actorMeanError=(float)(actorError/Math.Max(1,row.actorPixels));row.headMeanError=(float)(headError/Math.Max(1,row.headPixels));observations.measurements.Add(row);return row;
            }
            try
            {
                // Explicit precision option; the default hardware path remains
                // unchanged. Its retained paired native diagnostic exhibits
                // subtexel-weight jumps after otherwise close FSR results.
                s.colorGradeSampling=ColorLutSampling.ExplicitFloatTrilinear;
                s.actorMotion.enabled=s.includeSceneMotion=s.temporal.enabled=true;s.temporal.jitterUv=Vector2.zero;s.motionBlurJitterUv=Vector2.zero;
                s.effects.enabled=s.depthOfField.enabled=s.motionBlur.enabled=s.bloom.enabled=s.diffusion.enabled=true;
                // Explicit authored stress profile: broad weak glow and a narrow
                // head focus range. The old example's broad focus intentionally
                // leaves the actor sharp and is not a positive DOF control.
                s.bloom.threshold=.1f;s.bloom.intensity=.1f;s.diffusion.intensity=.15f;s.diffusion.radiusPixels=2.5f;
                s.fsr.encoding=FsrInputEncoding.LinearHdr;s.fsr.stabilizeLumaGradients=true;s.fsr.allowRasterFallback=false;s.fsr.backend=FsrBackend.Compute;s.fsrOutputSize=new Vector2Int(Size,Size);
                foreach(int angle in new[]{0,90,180})
                {
                    view(angle);
                    viewPosition=camera.transform.position;
                    float focus=-camera.worldToCameraMatrix.MultiplyPoint(head.position).z;
                    s.depthOfField.focusNear=Mathf.Max(.01f,focus-.1f);s.depthOfField.focusFar=focus+.1f;
                    s.depthOfField.nearTransition=s.depthOfField.farTransition=.4f;
                    Attach(false,FsrQuality.Quality);var native=Sequence("view-"+angle+"-native");
                    int visibility=camera.cullingMask;Color[][] excluded;
                    try{camera.cullingMask=1<<22;excluded=Sequence("view-"+angle+"-excluded");}
                    finally{camera.cullingMask=visibility;}
                    var mask=new bool[Size*Size];for(int p=0;p<mask.Length;p++)mask[p]=Mathf.Abs(native[3][p].r-excluded[3][p].r)+Mathf.Abs(native[3][p].g-excluded[3][p].g)+Mathf.Abs(native[3][p].b-excluded[3][p].b)>.001f;
                    var headRect=HeadRect();
                    foreach(var quality in new[]{FsrQuality.Quality,FsrQuality.Performance})
                    {
                        string label="view-"+angle+"-"+quality;Attach(true,quality);var full=Sequence(label+"-pan-full");
                        s.fsr.TryGetRenderSize(new Vector2Int(Size,Size),out var expectedSize);
                        Check(label+"-actual-low-geometry",s.scene.output.width==expectedSize.x&&s.scene.depthStencil.width==expectedSize.x&&expectedSize.x<Size&&example.Display.width==Size);
                        var nativeError=Measure(label,"native-resolution-reference",native[3],full[3],mask,headRect);
                        Check(label+"-actor-and-head-visible-in-reference",nativeError.actorPixels>1000&&nativeError.headPixels>100,nativeError.headPixels);
                        Check(label+"-native-reference-metrics-finite-only",Finite(nativeError.actorMeanError)&&Finite(nativeError.headMeanError)&&Finite(nativeError.maximumDifference));
                        s.fsr.backend=FsrBackend.Raster;var raster=Sequence(label+"-raster");s.fsr.backend=FsrBackend.Compute;
                        for(int i=0;i<times.Length;i++)Check(label+"-backend-color-"+i,MaximumDifference(full[i],raster[i])<.0001f,MaximumDifference(full[i],raster[i]));
                        foreach(string effect in new[]{"taa","motion-blur","bloom","diffusion","dof"})
                        {
                            void Enable(bool value)
                            {switch(effect){case "taa":s.temporal.enabled=value;break;case "motion-blur":s.motionBlur.enabled=value;break;case "bloom":s.bloom.enabled=value;break;case "diffusion":s.diffusion.enabled=value;break;case "dof":s.depthOfField.enabled=value;break;}}
                            Enable(false);Color[][] disabled;try{disabled=Sequence(label+"-no-"+effect);}finally{Enable(true);}
                            var difference=Measure(label,effect+"-off",full[3],disabled[3],mask,headRect);
                            // Require response on the actual character, not merely
                            // the bright background panel or moving generated orb.
                            Check(label+"-"+effect+"-actor-response",difference.actorChanged>10,difference.actorChanged);
                            if(effect=="motion-blur")Check(label+"-cold-no-exposure-exact",MaximumDifference(full[0],disabled[0])==0,MaximumDifference(full[0],disabled[0]));
                        }
                        var repeated=Sequence(label+"-repeat");for(int i=0;i<times.Length;i++)Check(label+"-seek-replay-exact-"+i,MaximumDifference(full[i],repeated[i])==0,MaximumDifference(full[i],repeated[i]));
                        var idle=Sequence(label+"-idle",false);s.motionBlur.enabled=false;
                        var idleNoBlur=Sequence(label+"-idle-no-blur",false);s.motionBlur.enabled=true;
                        // Idle uses another camera position; do not reuse the
                        // panned character mask or claim a head-region metric.
                        observations.idleWholeImage.Add(new IdlePostMeasurement {view=label,
                            maximumDifference=MaximumDifference(idle[3],idleNoBlur[3]),changedPixels=Changed(idle[3],idleNoBlur[3],.00001f)});
                        example.ResetHistory();run("post-"+label+"-paused-cold",.7f);var second=run("post-"+label+"-paused-warm",.7f);
                        s.motionBlur.enabled=false;example.ResetHistory();run("post-"+label+"-paused-no-blur-cold",.7f);var noBlur=run("post-"+label+"-paused-no-blur-warm",.7f);s.motionBlur.enabled=true;
                        Check(label+"-paused-no-exposure-exact",MaximumDifference(second,noBlur)==0,MaximumDifference(second,noBlur));
                    }
                }
                // Diagnostic capture only: identical explicit cold scene twice
                // in one native capture, separating backend math from startup,
                // motion history and source changes. Never substitutes for the
                // ordinary whole-sequence backend checks above.
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_CHARACTER_CASE")=="post-backend-pair")
                {
                    view(0);Attach(true,FsrQuality.Quality);s.fsr.backend=FsrBackend.Compute;
                    bool began=RenderDocCaptureBridge.BeginOffscreenCapture();
                    try
                    {
                        example.ResetHistory();var compute=run("post-pair-compute",.7f);
                        example.ResetHistory();s.fsr.backend=FsrBackend.Raster;var raster=run("post-pair-raster",.7f);
                        Check("paired-cold-backend-color",MaximumDifference(compute,raster)<.0001f,MaximumDifference(compute,raster));
                    }
                    finally{s.fsr.backend=FsrBackend.Compute;Check("paired-native-capture",began&&RenderDocCaptureBridge.EndOffscreenCapture());}
                }
            }
            finally
            {
                example.ResetHistory();s.fsr.enabled=s.motionBlur.enabled=s.bloom.enabled=s.diffusion.enabled=false;
                s.colorGradeSampling=originalSampling;
                s.scene.output=originalOutput;s.scene.depthStencil=originalDepth;s.scene.normalIdentity=originalNormal;s.scene.materialBase=originalBase;s.scene.materialMos=originalMos;camera.targetTexture=originalTarget;
                File.WriteAllText(Path.Combine(directory,"character-post-diagnostics.json"),JsonUtility.ToJson(observations,true));
            }
        }
    }
}
