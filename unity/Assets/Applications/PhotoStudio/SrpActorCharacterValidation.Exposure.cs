using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using GakumasPhotoMode.Examples;

namespace GakumasPhotoMode
{
    public sealed partial class SrpActorCharacterValidation
    {
        [Serializable] private sealed class ExposureMetric
        {
            public string trajectory,variant,region;
            public int pixels;
            public float linearMse,mappedMse;
        }
        [Serializable] private sealed class ExposureDiagnostics
        {
            public string schema="photo-studio.character-exposure.v1";
            public string scope="One caller-supplied outfit, native512, opaque character/scenery. Centered32midpoint explicit-time exposure and separate16midpoint convergence diagnostic. Camera-only and accelerated clip-only trajectories. No TAA, DOF, FX, grading, original-game, transparent or mobile acceptance. Image-difference actor/silhouette masks are not semantic hair labels. Metrics are diagnostics, not an unconditional quality pass.";
            public float centerTime=.8f,sampleInterval=.02f,shutterAngle=180,halfExposure=.005f;
            public float cameraStepInCharacterHeights=.03f,animationRate=6;
            public int referenceSamples=32,convergenceSamples=16;
            public bool subpixelReconstruction;
            public bool coupledPost;
            public bool geometryExposure;
            public bool opaqueExposure;
            public bool depthOfFieldDuringExposure;
            public bool forwardOpaqueOwnership;
            public float transparentStepInCharacterHeights=.04f;
            public List<ExposureMetric> measurements=new List<ExposureMetric>();
        }
        private void VerifyDesktopExposure(Report report,DesktopHostExample example,Transform head,Bounds bounds,
            Action<int> view,Func<string,float,float,Color[]> run)
        {
            var s=example.Configuration;var camera=example.RenderCamera;var observations=new ExposureDiagnostics();
            var position=camera.transform.position;var rotation=camera.transform.rotation;var projection=camera.projectionMatrix;
            var grade=s.colorGrade;int visibility=camera.cullingMask;
            var priorSurfaces=s.effects.geometry.surfaces;
            bool priorOpaqueExposure=s.effects.exposure.reprojectOpaque;
            bool priorExposureDof=s.depthOfFieldDuringExposure;
            bool priorForwardOwnership=s.effects.exposure.forwardOpaqueOwnership;
            void Check(string name,bool ok,float value=0)=>report.checks.Add(new Check {name="desktop-character-exposure-"+name,accepted=ok,value=value});
            Color[] Average(List<Color[]> frames)
            {
                var result=new Color[Size*Size];
                for(int p=0;p<result.Length;p++)for(int c=0;c<4;c++)
                {double sum=0;foreach(var frame in frames)sum+=frame[p][c];result[p][c]=(float)(sum/frames.Count);}
                return result;
            }
            float Mapped(float x){x=Mathf.Max(0,x);return x/(1+x);}
            ExposureMetric Measure(string name,string variant,string region,Color[] actual,Color[] reference,bool[] mask)
            {
                var row=new ExposureMetric {trajectory=name,variant=variant,region=region};double linear=0,mapped=0;
                for(int p=0;p<mask.Length;p++)if(mask[p])
                {
                    row.pixels++;for(int c=0;c<3;c++)
                    {double d=(double)actual[p][c]-reference[p][c];linear+=d*d;d=(double)Mapped(actual[p][c])-Mapped(reference[p][c]);mapped+=d*d;}
                }
                row.linearMse=(float)(linear/Math.Max(1,row.pixels*3));row.mappedMse=(float)(mapped/Math.Max(1,row.pixels*3));
                observations.measurements.Add(row);Check(name+"-"+variant+"-"+region+"-finite-metric-only",row.pixels>0&&Finite(row.linearMse)&&Finite(row.mappedMse),row.mappedMse);
                return row;
            }
            try
            {
                s.actorMotion.enabled=s.includeSceneMotion=true;s.temporal.enabled=false;
                s.effects.enabled=s.depthOfField.enabled=s.bloom.enabled=s.fsr.enabled=false;s.planar.enabled=s.reflections.enabled=false;
                // Float32 identity conversion only, not a blur or nonlinear grade.
                s.diffusion.enabled=true;s.diffusion.intensity=s.diffusion.radiusPixels=0;s.diffusion.downsample=1;s.colorGrade=null;
                s.temporal.jitterUv=s.motionBlurJitterUv=Vector2.zero;
                s.motionBlur.exposure=MotionBlurExposure.ShutterAngle;s.motionBlur.shutterAngle=observations.shutterAngle;
                s.motionBlur.samples=64;s.motionBlur.maximumRadiusPixels=24;
                observations.subpixelReconstruction=Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_EXPOSURE_SUBPIXEL")=="1";
                s.motionBlur.subpixelReconstruction=observations.subpixelReconstruction;
                observations.coupledPost=Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_EXPOSURE_COUPLED_POST")=="1";
                observations.geometryExposure=observations.coupledPost&&Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_EXPOSURE_FX_GEOMETRY")=="1";
                observations.opaqueExposure=observations.geometryExposure&&Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_EXPOSURE_OPAQUE")=="1";
                s.effects.exposure.reprojectOpaque=observations.opaqueExposure;
                observations.depthOfFieldDuringExposure=observations.opaqueExposure&&Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_EXPOSURE_SHUTTER_DOF")=="1";
                s.depthOfFieldDuringExposure=observations.depthOfFieldDuringExposure;
                observations.forwardOpaqueOwnership=observations.opaqueExposure&&Environment.GetEnvironmentVariable("GAKUMAS_CHARACTER_EXPOSURE_FORWARD_OWNER")=="1";
                s.effects.exposure.forwardOpaqueOwnership=observations.forwardOpaqueOwnership;
                s.effects.exposure.samples=16;s.effects.exposure.shutterAngle=observations.shutterAngle;
                LowResolutionFxSurface transparent=null;
                if(observations.coupledPost)
                {
                    observations.scope="One caller-supplied outfit, native512, three views. DOF plus generated full-resolution alpha surface and centered32midpoint time integration, with separate16midpoint convergence. Camera-only, clip-only and transparent-only motion. The reference applies the SAME screen-space DOF at every sub-time: it checks temporal coupling, NOT physical aperture integration or correct transparent lens depth. No TAA, original-game, mobile or full framework acceptance. Actor/silhouette and FX masks are image-difference proxies.";
                    var mesh=Own(new Mesh {name="Independent coupled exposure alpha quad"});
                    mesh.vertices=new[]{new Vector3(-.5f,-.5f,0),new Vector3(.5f,-.5f,0),new Vector3(.5f,.5f,0),new Vector3(-.5f,.5f,0)};
                    mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.triangles=new[]{0,1,2,0,2,3};mesh.RecalculateBounds();
                    transparent=new LowResolutionFxSurface {mesh=mesh,resolution=FxResolution.Full,blend=FxBlend.Alpha,opacity=.6f,radialSoftness=.4f,linearRadiance=new Vector3(.1f,1.5f,2),fog=false};
                    s.effects.enabled=s.effects.geometry.enabled=s.depthOfField.enabled=true;
                    s.effects.geometry.surfaces=new[]{transparent};
                    s.depthOfField.focusMode=BokehFocusMode.FocusRange;s.depthOfField.maximumRadius=.012f;
                    s.depthOfField.nearBlur=s.depthOfField.farBlur=1;
                }
                foreach(int angle in new[]{0,90,180})foreach(string profile in observations.coupledPost?new[]{"camera","animation","transparent"}:new[]{"camera","animation"})
                {
                    app.EvaluateMotion(.7f);view(angle);var target=head.position+head.up*(bounds.size.y*.025f);
                    var direction=(camera.transform.position-bounds.center).normalized;
                    camera.transform.position=target+direction*(bounds.size.y*.62f);camera.transform.LookAt(target);camera.ResetProjectionMatrix();
                    var origin=camera.transform.position;var facing=camera.transform.rotation;var right=camera.transform.right;
                    float distance=Vector3.Distance(origin,target);
                    if(observations.coupledPost)
                    {
                        s.depthOfField.focusNear=distance*1.02f;s.depthOfField.focusFar=distance*1.15f;
                        s.depthOfField.nearTransition=distance*.3f;s.depthOfField.farTransition=distance*.5f;
                    }
                    string label="view-"+angle+"-"+profile;
                    Color[] Render(string name,float clock)
                    {
                        float offset=clock-observations.centerTime;
                        camera.transform.SetPositionAndRotation(origin+(profile=="camera"?right*(bounds.size.y*observations.cameraStepInCharacterHeights*offset/observations.sampleInterval):Vector3.zero),facing);
                        float pose=.7f+(profile=="animation"?offset*observations.animationRate:0);
                        if(transparent!=null)
                        {
                            // World-space surface: fixed during camera/clip controls,
                            // moves independently of opaque geometry in the FX control.
                            var center=Vector3.Lerp(origin,target,.75f)+right*(bounds.size.y*.06f);
                            if(profile=="transparent")center+=right*(bounds.size.y*observations.transparentStepInCharacterHeights*offset/observations.sampleInterval);
                            transparent.localToWorld=Matrix4x4.TRS(center,facing,Vector3.one*(bounds.size.y*.18f));
                        }
                        return run("exposure-"+label+"-"+name,clock,pose);
                    }
                    Color[] Sequence(string name,bool blur,bool expose=true)
                    {
                        s.motionBlur.enabled=blur;s.effects.exposure.enabled=observations.geometryExposure&&blur&&expose;example.ResetHistory();
                        Render(name+"-previous",observations.centerTime-observations.sampleInterval);
                        return Render(name+"-current",observations.centerTime);
                    }
                    var current=Sequence("unblurred",false);var blurred=Sequence("blurred",true);
                    Color[] legacy=null;
                    if(observations.subpixelReconstruction)
                    {s.motionBlur.subpixelReconstruction=false;legacy=Sequence("legacy",true,false);s.motionBlur.subpixelReconstruction=true;}
                    var repeated=Sequence("replay",true);
                    Check(label+"-seek-replay-exact",MaximumDifference(blurred,repeated)==0,MaximumDifference(blurred,repeated));
                    s.motionBlur.enabled=true;example.ResetHistory();var cold=Render("cold",observations.centerTime);
                    Check(label+"-cold-no-exposure-exact",MaximumDifference(current,cold)==0,MaximumDifference(current,cold));
                    var paused=Render("paused",observations.centerTime);
                    Check(label+"-paused-no-exposure-exact",MaximumDifference(current,paused)==0,MaximumDifference(current,paused));
                    s.motionBlur.enabled=false;s.effects.exposure.enabled=false;
                    Color[] noFx=null;
                    if(observations.coupledPost)
                    {
                        s.effects.enabled=false;example.ResetHistory();noFx=Render("fx-off",observations.centerTime);s.effects.enabled=true;
                        s.depthOfField.enabled=false;example.ResetHistory();var noDof=Render("dof-off",observations.centerTime);s.depthOfField.enabled=true;
                        Check(label+"-fx-positive-control",Changed(current,noFx,.00001f)>100,Changed(current,noFx,.00001f));
                        Check(label+"-dof-positive-control",Changed(current,noDof,.00001f)>100,Changed(current,noDof,.00001f));
                    }
                    camera.cullingMask=1<<22;example.ResetHistory();var excluded=Render("excluded",observations.centerTime);camera.cullingMask=visibility;
                    Color[] Reference(int samples)
                    {
                        var frames=new List<Color[]>();
                        for(int i=0;i<samples;i++)
                        {
                            float time=observations.centerTime+((i+.5f)/samples*2-1)*observations.halfExposure;
                            example.ResetHistory();frames.Add(Render("sample-"+samples+"-"+i,time));
                        }
                        var mean=Average(frames);Save("desktop-character-exposure-"+label+"-reference-"+samples,mean);return mean;
                    }
                    var reference=Reference(observations.referenceSamples);var convergence=Reference(observations.convergenceSamples);
                    var actor=new bool[Size*Size];var silhouette=new bool[actor.Length];var whole=new bool[actor.Length];
                    var fx=new bool[actor.Length];
                    for(int p=0;p<actor.Length;p++){whole[p]=true;actor[p]=Mathf.Abs(current[p].r-excluded[p].r)+Mathf.Abs(current[p].g-excluded[p].g)+Mathf.Abs(current[p].b-excluded[p].b)>.001f;}
                    if(noFx!=null)for(int p=0;p<fx.Length;p++)fx[p]=Mathf.Abs(current[p].r-noFx[p].r)+Mathf.Abs(current[p].g-noFx[p].g)+Mathf.Abs(current[p].b-noFx[p].b)>.001f;
                    for(int y=1;y<Size-1;y++)for(int x=1;x<Size-1;x++)
                    {
                        int p=y*Size+x;bool edge=actor[p]!=actor[p-1]||actor[p]!=actor[p+1]||actor[p]!=actor[p-Size]||actor[p]!=actor[p+Size];
                        if(edge)for(int dy=-3;dy<=3;dy++)for(int dx=-3;dx<=3;dx++)
                        {int xx=x+dx,yy=y+dy;if(xx>=0&&xx<Size&&yy>=0&&yy<Size)silhouette[yy*Size+xx]=true;}
                    }
                    foreach(string region in observations.coupledPost?new[]{"actor","silhouette","whole","fx"}:new[]{"actor","silhouette","whole"})
                    {
                        var mask=region=="actor"?actor:region=="silhouette"?silhouette:region=="fx"?fx:whole;
                        var rawError=Measure(label,"unblurred",region,current,reference,mask);var blurError=Measure(label,"blurred",region,blurred,reference,mask);
                        Measure(label,"reference16",region,convergence,reference,mask);
                        if(legacy!=null)
                        {
                            var legacyError=Measure(label,"legacy",region,legacy,reference,mask);
                            if(region!="whole")
                            {
                                Check(label+"-"+region+"-exposure-improves-unblurred",blurError.mappedMse<rawError.mappedMse,blurError.mappedMse/Mathf.Max(rawError.mappedMse,1e-20f));
                                Check(label+"-"+region+"-exposure-no-worse-than-legacy",blurError.mappedMse<=legacyError.mappedMse*1.00001f,blurError.mappedMse/Mathf.Max(legacyError.mappedMse,1e-20f));
                            }
                        }
                    }
                    Check(label+"-blur-positive-control",Changed(current,blurred,.00001f)>100,Changed(current,blurred,.00001f));
                    Check(label+"-time-integral-positive-control",Changed(current,reference,.00001f)>100,Changed(current,reference,.00001f));
                }
            }
            finally
            {
                example.ResetHistory();s.motionBlur.enabled=false;s.effects.exposure.enabled=false;s.colorGrade=grade;camera.cullingMask=visibility;
                s.effects.geometry.surfaces=priorSurfaces;
                s.effects.exposure.reprojectOpaque=priorOpaqueExposure;
                s.depthOfFieldDuringExposure=priorExposureDof;
                s.effects.exposure.forwardOpaqueOwnership=priorForwardOwnership;
                camera.transform.SetPositionAndRotation(position,rotation);camera.projectionMatrix=projection;
                File.WriteAllText(Path.Combine(directory,"character-exposure-diagnostics.json"),JsonUtility.ToJson(observations,true));
            }
        }
    }
}
