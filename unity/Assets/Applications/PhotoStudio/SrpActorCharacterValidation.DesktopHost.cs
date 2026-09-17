using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using GakumasPhotoMode.Examples;

namespace GakumasPhotoMode
{
    public sealed partial class SrpActorCharacterValidation
    {
        // The ordinary Forward controls run first in this same process. This adds
        // the local character to the actual compiled example's whole frame host.
        private void VerifyDesktopCharacter(Report report,Renderer[] renderers,Transform head,
            Bounds bounds,ActorForwardParameters inputs,string sourceBefore)
        {
            var go=new GameObject("Real character desktop host validation");
            var example=go.AddComponent<DesktopHostExample>();example.width=example.height=Size;example.presentToScreen=false;
            var previousGraphics=GraphicsSettings.renderPipelineAsset;var previousQuality=QualitySettings.renderPipeline;
            void Check(string n,bool ok,float v=0)=>report.checks.Add(new Check { name="desktop-character-"+n,accepted=ok,value=v });
            try
            {
                example.Initialize();var camera=example.RenderCamera;camera.enabled=false;
                foreach(var renderer in renderers)camera.cullingMask|=1<<renderer.gameObject.layer;
                var s=example.Configuration;s.actors.renderers=renderers;s.actors.parameters=inputs;s.actors.outlines=s.actors.hairCover=true;
                bool detailsOff=false;
                s.actors.configureMaterial=(r,i,m)=>{
                    var probe=RenderSettings.ambientProbe;
                    if(r.lightProbeUsage!=LightProbeUsage.Off&&r.lightProbeUsage!=LightProbeUsage.CustomProvided)
                        LightProbes.GetInterpolatedProbe(r.probeAnchor!=null?r.probeAnchor.position:r.bounds.center,r,out probe);
                    if(r.lightProbeUsage==LightProbeUsage.CustomProvided)throw new InvalidOperationException("Explicit custom probe data required");
                    ActorForwardParameters.BindAmbientProbe(m,probe);
                    if(detailsOff){m.SetTexture("_ShadeTex",Texture2D.whiteTexture);m.SetTexture("_RampAddTex",Texture2D.blackTexture);m.SetTexture("_HighlightTex",Texture2D.blackTexture);}
                };
                float radius=Mathf.Max(1,bounds.extents.magnitude),distance=Mathf.Max(1,bounds.size.y)*2;
                s.selfShadowDirection=new Vector3(.6f,1,.7f).normalized;
                s.selfShadow.origin=bounds.center+s.selfShadowDirection*radius*2;s.selfShadow.halfSize=Vector2.one*radius*1.4f;
                s.selfShadow.farPlane=radius*4;s.selfShadow.normalBias=s.selfShadow.halfSize.x/s.selfShadow.resolution;
                void View(int angle)
                {
                    var rotation=Quaternion.Euler(0,angle,0);var direction=rotation*Vector3.forward;
                    camera.transform.position=bounds.center+direction*distance;
                    camera.transform.LookAt(bounds.center);
                    // Move the backdrop behind each view; otherwise the rear
                    // camera sees the wall, hiding the very hair being checked.
                    s.scene.surfaces[1].renderer.transform.SetPositionAndRotation(new Vector3(bounds.center.x,2,bounds.center.z)-direction*3,rotation);
                    s.scene.surfaces[2].renderer.transform.SetPositionAndRotation(new Vector3(bounds.center.x,1.8f,bounds.center.z)-direction*2.85f-rotation*Vector3.right*1.5f,rotation);
                }
                Action beforePose=null,afterPose=null;
                Color[] Run(string name,float time=0,float? poseTime=null)
                {
                    beforePose?.Invoke();
                    app.EvaluateMotion(poseTime??time);
                    afterPose?.Invoke();
                    inputs.SetVector("_HeadDirection",new Vector4(head.forward.x,head.forward.y,head.forward.z,1));
                    inputs.SetVector("_HeadUpDirection",new Vector4(head.up.x,head.up.y,head.up.z,1));
                    inputs.SetVector("_HeadRightDirection",new Vector4(-head.right.x,-head.right.y,-head.right.z,1));
                    var pos=head.position+head.up*.1f;inputs.SetVector("_HeadPosition",new Vector4(pos.x,pos.y,pos.z,1));
                    inputs.SetVector("_ActorOutlineParameters",new Vector4(.04f,.12f,1f/3,Mathf.Tan(15.5f*Mathf.Deg2Rad)/Mathf.Tan(camera.fieldOfView*.5f*Mathf.Deg2Rad)));
                    bool requested=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_CHARACTER_CASE")==name||
                        Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_CHARACTER_REFERENCE_CASE")==name;
                    bool began=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();Color[] pixels;
                    try
                    {
                        bool ok=example.RenderOffscreen(time);Check(name+"-render",ok&&example.HasCompletedFrame);
                        if(!ok)throw new InvalidOperationException(example.LastError);
                        pixels=ReadPixels(example.Display);Save("desktop-character-"+name,pixels);
                    }
                    finally { if(began)Check(name+"-native-capture",RenderDocCaptureBridge.EndOffscreenCapture()); }
                    Check(name+"-finite",pixels.All(p=>Finite(p.r)&&Finite(p.g)&&Finite(p.b)&&Finite(p.a)));
                    Check(name+"-source-materials-unchanged",SourceSnapshot(renderers)==sourceBefore);
                    return pixels;
                }
                if(renderers.Any(r=>r.gameObject.layer==22))throw new InvalidOperationException("Visibility control needs a separate generated scenery layer");
                if(Environment.GetCommandLineArgs().Contains("--validate-desktop-additional-lights"))
                {
                    VerifyDesktopAdditionalLights(report,example,bounds,inputs,View,name=>Run(name,.7f,.7f));
                    example.Shutdown();Check("additional-shutdown-preserves-character",SourceSnapshot(renderers)==sourceBefore&&renderers.All(r=>r!=null&&r.enabled));
                    Check("additional-shutdown-restores-pipeline",GraphicsSettings.renderPipelineAsset==previousGraphics&&QualitySettings.renderPipeline==previousQuality);
                    return;
                }
                if(Environment.GetCommandLineArgs().Contains("--validate-desktop-focus"))
                {
                    VerifyDesktopFocus(report,example,renderers,head,bounds,View,
                        name=>Run(name,.7f,.7f),(before,after)=>{beforePose=before;afterPose=after;});
                    example.Shutdown();Check("focus-shutdown-preserves-character",SourceSnapshot(renderers)==sourceBefore&&renderers.All(r=>r!=null&&r.enabled));
                    Check("focus-shutdown-restores-pipeline",GraphicsSettings.renderPipelineAsset==previousGraphics&&QualitySettings.renderPipeline==previousQuality);
                    return;
                }
                foreach(int angle in new[]{0,90,180,270})
                {
                    View(angle);var visible=Run("view-"+angle);int mask=camera.cullingMask;Color[] excluded;
                    // Keep the same casters, reflection, scenery and effects;
                    // remove only the main-view Actor. Shadow changes cannot
                    // make an occluded or missing Actor pass this control.
                    try { camera.cullingMask=1<<22;excluded=Run("view-"+angle+"-actor-excluded"); }
                    finally { camera.cullingMask=mask; }
                    Check("view-"+angle+"-actor-visible",Changed(visible,excluded,.001f)>1000,Changed(visible,excluded,.001f));
                }
                View(0);Run("front-cold");var front=Run("front-warm");var repeat=Run("front-repeat");
                Check("warm-repeat-whole-color",MaximumDifference(front,repeat)<.00001f,MaximumDifference(front,repeat));
                var moved=Run("motion-070",.7f);Check("motion-positive-control",Changed(front,moved,.001f)>100,Changed(front,moved,.001f));
                Run("motion-restored");var restored=Run("motion-restored-warm");
                Check("motion-seek-restores-color",MaximumDifference(front,restored)<.00001f,MaximumDifference(front,restored));
                detailsOff=true;var simple=Run("details-off");detailsOff=false;
                Check("authored-materials-positive-control",Changed(front,simple,.001f)>100,Changed(front,simple,.001f));
                s.selfShadow.strength=0;var noSelf=Run("self-shadow-off");s.selfShadow.strength=1;
                Check("self-shadow-positive-control",Changed(front,noSelf,.001f)>100,Changed(front,noSelf,.001f));
                s.scene.mainLightShadow.strength=0;var noDrop=Run("drop-shadow-off");s.scene.mainLightShadow.strength=1;
                Check("background-shadow-positive-control",Changed(front,noDrop,.001f)>100,Changed(front,noDrop,.001f));
                s.planar.enabled=false;var noPlanar=Run("planar-off");s.planar.enabled=true;
                Check("planar-positive-control",Changed(front,noPlanar,.001f)>100,Changed(front,noPlanar,.001f));
                s.effects.enabled=false;var noFx=Run("effects-off");s.effects.enabled=true;
                Check("effects-positive-control",Changed(front,noFx,.001f)>100,Changed(front,noFx,.001f));
                s.depthOfField.enabled=false;var noDof=Run("dof-off");s.depthOfField.enabled=true;
                Check("dof-positive-control",Changed(front,noDof,.001f)>100,Changed(front,noDof,.001f));
                Run("all-restored");var final=Run("all-restored-warm");
                Check("all-controls-restore-whole-color",MaximumDifference(front,final)<.00001f,MaximumDifference(front,final));
                foreach(int angle in new[]{0,90,180})
                {
                    View(angle);string label="storage-view-"+angle;
                    s.actorStorage=SrpActorForward.Storage.SeparateHalf;Run(label+"-half-cold",.7f);var half=Run(label+"-half",.7f);
                    s.actorStorage=SrpActorForward.Storage.SeparatePacked;Run(label+"-packed-cold",.7f);var packed=Run(label+"-packed",.7f);
                    s.actorStorage=SrpActorForward.Storage.ReuseScenePacked;Run(label+"-reuse-cold",.7f);var reuse=Run(label+"-reuse",.7f);
                    Check(label+"-reuse-exact-packed-control",MaximumDifference(packed,reuse)==0,MaximumDifference(packed,reuse));
                    Check(label+"-packed-precision-difference-metric",Finite(MaximumDifference(half,packed)),MaximumDifference(half,packed));
                    s.actorStorage=SrpActorForward.Storage.SeparateHalf;Run(label+"-restored-cold",.7f);var restoredStorage=Run(label+"-restored",.7f);
                    Check(label+"-default-restored",MaximumDifference(half,restoredStorage)<.00001f,MaximumDifference(half,restoredStorage));
                }
                // Same real character, full reflection/FX/DOF/grade stack. Reset
                // every history (including reflection) before each paired sequence.
                // Only storage ownership differs between packed and reused runs.
                s.allowImmutableUnreadableMotionMeshes=true;
                float[] times={.7f,.7f,.72f,.74f};
                foreach(int angle in new[]{0,90,180})
                {
                    View(angle);string label="joined-view-"+angle;
                    s.actorStorage=SrpActorForward.Storage.SeparatePacked;s.reuseSceneMotionStorage=false;
                    s.actorMotion.enabled=false;s.includeSceneMotion=false;s.temporal.enabled=false;example.ResetHistory();
                    var control=new Color[times.Length][];
                    for(int i=0;i<times.Length;i++)control[i]=Run(label+"-disabled-"+i,times[i]);
                    s.actorMotion.enabled=true;s.includeSceneMotion=true;example.ResetHistory();
                    for(int i=0;i<times.Length;i++)
                    {
                        var motionOnly=Run(label+"-motion-only-"+i,times[i]);
                        Check(label+"-motion-keeps-full-color-"+i,MaximumDifference(control[i],motionOnly)==0,MaximumDifference(control[i],motionOnly));
                    }
                    s.temporal.enabled=true;example.ResetHistory();
                    var resolved=new Color[times.Length][];
                    for(int i=0;i<times.Length;i++)resolved[i]=Run(label+"-owned-"+i,times[i]);
                    Check(label+"-cold-resolve-exact",MaximumDifference(control[0],resolved[0])==0,MaximumDifference(control[0],resolved[0]));
                    Check(label+"-animated-temporal-positive-control",Changed(control[3],resolved[3],.00001f)>100,Changed(control[3],resolved[3],.00001f));
                    s.actorStorage=SrpActorForward.Storage.ReuseScenePacked;s.reuseSceneMotionStorage=true;example.ResetHistory();
                    for(int i=0;i<times.Length;i++)
                    {
                        var reused=Run(label+"-reuse-"+i,times[i]);
                        Check(label+"-reuse-two-gbuffers-exact-"+i,MaximumDifference(resolved[i],reused)==0,MaximumDifference(resolved[i],reused));
                    }
                    // Seek after warm history must reproduce the original cold result.
                    example.ResetHistory();var sought=Run(label+"-seek-cold",times[0]);
                    Check(label+"-seek-resets-whole-chain",MaximumDifference(resolved[0],sought)==0,MaximumDifference(resolved[0],sought));
                }
                if(Environment.GetCommandLineArgs().Contains("--validate-desktop-exposure"))
                    VerifyDesktopExposure(report,example,head,bounds,View,(name,time,pose)=>Run(name,time,pose));
                else if(Environment.GetCommandLineArgs().Contains("--validate-desktop-dynamic-jitter"))
                    VerifyDesktopDynamicJitter(report,example,head,bounds,View,(name,time)=>Run(name,time));
                else if(Environment.GetCommandLineArgs().Contains("--validate-desktop-jitter"))
                    VerifyDesktopCharacterJitter(report,example,head,bounds,View,(name,time)=>Run(name,time));
                else VerifyDesktopPostCharacter(report,example,head,bounds,View,(name,time)=>Run(name,time));
                example.Shutdown();Check("shutdown-preserves-character",SourceSnapshot(renderers)==sourceBefore&&renderers.All(r=>r!=null&&r.enabled));
                Check("shutdown-restores-pipeline",GraphicsSettings.renderPipelineAsset==previousGraphics&&QualitySettings.renderPipeline==previousQuality);
            }
            finally { example.Shutdown();go.SetActive(false);Destroy(go); }
        }
    }
}
