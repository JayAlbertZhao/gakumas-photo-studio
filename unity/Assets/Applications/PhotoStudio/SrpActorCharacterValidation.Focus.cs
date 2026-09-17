using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using GakumasPhotoMode.Examples;

namespace GakumasPhotoMode
{
    public sealed partial class SrpActorCharacterValidation
    {
        [Serializable] private sealed class FocusCase
        {
            public string name;public float near,far,headDepth,handDepth;
            public Matrix4x4 view,projection;public Bounds[] bounds;
            public Vector3 headPoint,handPoint;public float height,padding;
            public int actorPixels,headPixels,handPixels,backgroundChanged,handPhysicalChanged;
            public float actorMaximumError,headMaximumError,handMaximumError,cocMaximumError;
        }
        [Serializable] private sealed class FocusReport
        {
            public string schema="photo-studio.character-focus.v1";
            public string scope="Art-directed whole-character focus, two views and explicit idle/reach arm poses. Actual depth/CoC and paired sharp/physical/range controls. No TAA, motion blur, aperture-ground-truth or mobile acceptance. Bounds are caller-owned, not inferred by the toolkit.";
            public List<FocusCase> cases=new List<FocusCase>();
        }
        private void VerifyDesktopFocus(Report report,DesktopHostExample example,Renderer[] renderers,Transform head,Bounds character,
            Action<int> view,Func<string,Color[]> run,Action<Action,Action> setPoseHooks)
        {
            var s=example.Configuration;var camera=example.RenderCamera;var rows=new FocusReport();
            // Face/hair prefabs may retain an unused duplicate skeleton. Select
            // the current body's humanoid rig, not the first matching name.
            var animator=head.GetComponentInParent<Animator>();
            if(animator==null||!animator.isHuman)throw new InvalidOperationException("Focus fixture requires the active body humanoid");
            Transform Bone(HumanBodyBones bone)=>animator.GetBoneTransform(bone)??throw new InvalidOperationException("Focus fixture requires "+bone);
            var upper=Bone(HumanBodyBones.RightUpperArm);var lower=Bone(HumanBodyBones.RightLowerArm);var hand=Bone(HumanBodyBones.RightHand);
            var upperRotation=upper.localRotation;var lowerRotation=lower.localRotation;
            var skins=renderers.OfType<SkinnedMeshRenderer>().ToArray();
            var recalculate=skins.Select(skin=>skin.forceMatrixRecalculationPerRender).ToArray();
            bool reach=false;int observedFrames=0;Color[] depth=null,coc=null;DesktopFrameRenderer.Frame observed=default;
            void Check(string n,bool ok,float v=0)=>report.checks.Add(new Check{name="desktop-character-focus-"+n,accepted=ok,value=v});
            void Restore(){upper.localRotation=upperRotation;lower.localRotation=lowerRotation;}
            void Pose()
            {
                if(!reach)return;
                float side=Mathf.Sign(Vector3.Dot(upper.position-head.position,camera.transform.right));
                var direction=(-camera.transform.forward+camera.transform.right*(side*.9f)-camera.transform.up*.15f).normalized;
                upper.rotation=Quaternion.FromToRotation(lower.position-upper.position,direction)*upper.rotation;
                lower.rotation=Quaternion.FromToRotation(hand.position-lower.position,direction)*lower.rotation;
            }
            void Observe(DesktopFrameRenderer.Frame frame)
            {
                Check("borrowed-frame-current-"+(observedFrames++),frame.IsCurrent);
                observed=frame;depth=ReadPixels(frame.eyeDepth);coc=frame.encodedCoC!=null?ReadPixels(frame.encodedCoC):null;
            }
            float Eye(Vector3 p)=>-camera.worldToCameraMatrix.MultiplyPoint3x4(p).z;
            bool Region(int p,Vector3 point,float radius)
            {
                var v=camera.WorldToViewportPoint(point);var edge=camera.WorldToViewportPoint(point+camera.transform.up*radius);
                float r=Mathf.Abs(edge.y-v.y)*Size,dx=p%Size+.5f-v.x*Size,dy=p/Size+.5f-v.y*Size;
                return dx*dx+dy*dy<=r*r;
            }
            try
            {
                // Explicit manual bone poses are rendered repeatedly in one
                // Unity update. Do not reuse the previous skin-matrix cache.
                foreach(var skin in skins)skin.forceMatrixRecalculationPerRender=true;
                setPoseHooks(Restore,Pose);example.FrameProduced+=Observe;
                s.temporal.enabled=s.actorMotion.enabled=s.includeSceneMotion=s.motionBlur.enabled=false;
                s.effects.enabled=s.bloom.enabled=s.fsr.enabled=false;s.planar.enabled=s.reflections.enabled=false;
                s.colorGrade=null;s.diffusion.enabled=true;s.diffusion.intensity=s.diffusion.radiusPixels=0;s.diffusion.downsample=1;
                s.depthOfField.nearBlur=0;s.depthOfField.farBlur=1;s.depthOfField.maximumRadius=.025f;
                s.depthOfField.nearTransition=s.depthOfField.farTransition=character.size.y*.6f;
                Color[] idle=null,idleDepth=null;
                foreach(int angle in new[]{0,35})foreach(bool extended in new[]{false,true})
                {
                    reach=extended;view(angle);
                    var direction=(camera.transform.position-character.center).normalized;
                    camera.transform.position=character.center+direction*(character.size.y*1.6f);camera.transform.LookAt(character.center);camera.ResetProjectionMatrix();
                    string label="focus-view-"+angle+(reach?"-reach":"-idle");
                    s.depthOfField.enabled=false;example.ResetHistory();var sharp=run(label+"-sharp");var actorDepth=depth;
                    if(!reach){idle=sharp;idleDepth=actorDepth;}
                    else
                    {
                        Check(label+"-reach-changes-actual-skin",Changed(idle,sharp,.001f)>100,Changed(idle,sharp,.001f));
                        int facePixels=0,covered=0;
                        for(int p=0;p<actorDepth.Length;p++)if(Region(p,head.position+head.up*character.size.y*.045f,character.size.y*.045f))
                        {facePixels++;if(Mathf.Abs(actorDepth[p].r-idleDepth[p].r)>.0001f)covered++;}
                        Check(label+"-reach-keeps-face-core-visible",facePixels>100&&covered==0,covered);
                    }
                    Check(label+"-borrow-expires-after-render",!observed.IsCurrent);
                    Save("desktop-character-"+label+"-depth",actorDepth);
                    var inputs=renderers.Select(r=>r.bounds).Concat(new[]{new Bounds(hand.position,Vector3.one*character.size.y*.12f)}).ToArray();
                    Check(label+"-fit-current-bounds",BokehFocusRange.TryFit(camera.worldToCameraMatrix,inputs,character.size.y*.025f,out var range));
                    if(range==Vector2.zero)throw new InvalidOperationException("Current focus bounds do not fit");
                    int mask=camera.cullingMask;Color[] excludedDepth;
                    try {camera.cullingMask=1<<22;example.ResetHistory();run(label+"-excluded");excludedDepth=depth;}
                    finally {camera.cullingMask=mask;}
                    Save("desktop-character-"+label+"-excluded-depth",excludedDepth);
                    var actor=new bool[Size*Size];for(int p=0;p<actor.Length;p++)actor[p]=Mathf.Abs(actorDepth[p].r-excludedDepth[p].r)>.0001f;
                    s.depthOfField.enabled=true;s.depthOfField.focusMode=BokehFocusMode.Physical;s.depthOfField.nearBlur=1;
                    s.depthOfField.focusDistance=Eye(head.position);s.depthOfField.focalLengthMillimetres=85;s.depthOfField.fNumber=1;
                    example.ResetHistory();var physical=run(label+"-physical");
                    s.depthOfField.focusMode=BokehFocusMode.FocusRange;s.depthOfField.focusNear=range.x;s.depthOfField.focusFar=range.y;s.depthOfField.nearBlur=0;
                    foreach(var samples in new[]{BokehSampleCount.Samples30,BokehSampleCount.Samples43})
                    {
                        s.depthOfField.sampleCount=samples;example.ResetHistory();var actual=run(label+"-range-"+(int)samples);
                        Save("desktop-character-"+label+"-coc-"+(int)samples,coc);
                        var row=new FocusCase{name=label+"-"+(int)samples,near=range.x,far=range.y,view=camera.worldToCameraMatrix,projection=camera.projectionMatrix,bounds=inputs,headDepth=Eye(head.position),handDepth=Eye(hand.position),headPoint=head.position+head.up*character.size.y*.06f,handPoint=hand.position,height=character.size.y,padding=character.size.y*.025f};
                        for(int p=0;p<actor.Length;p++)
                        {
                            float error=0,physicalError=0;
                            for(int c=0;c<3;c++){error=Mathf.Max(error,Mathf.Abs(actual[p][c]-sharp[p][c]));physicalError=Mathf.Max(physicalError,Mathf.Abs(physical[p][c]-sharp[p][c]));}
                            float expected=.5f+.5f*s.depthOfField.EvaluateRadius(actorDepth[p].r)/s.depthOfField.maximumRadius;
                            row.cocMaximumError=Mathf.Max(row.cocMaximumError,Mathf.Abs(coc[p].r-expected));
                            if(!actor[p]){if(error>.001f)row.backgroundChanged++;continue;}
                            row.actorPixels++;row.actorMaximumError=Mathf.Max(row.actorMaximumError,error);
                            if(Region(p,head.position+head.up*character.size.y*.06f,character.size.y*.09f))
                            {row.headPixels++;row.headMaximumError=Mathf.Max(row.headMaximumError,error);}
                            if(Region(p,hand.position,character.size.y*.07f)&&Mathf.Abs(actorDepth[p].r-row.handDepth)<character.size.y*.12f)
                            {row.handPixels++;row.handMaximumError=Mathf.Max(row.handMaximumError,error);if(physicalError>.001f)row.handPhysicalChanged++;}
                        }
                        Check(row.name+"-whole-character-sharp",row.actorPixels>3000&&row.actorMaximumError<.0002f,row.actorMaximumError);
                        Check(row.name+"-head-sharp",row.headPixels>30&&row.headMaximumError<.0002f,row.headMaximumError);
                        Check(row.name+"-background-remains-blurred",row.backgroundChanged>100,row.backgroundChanged);
                        Check(row.name+"-actual-coc-matches-depth",row.cocMaximumError<.00001f,row.cocMaximumError);
                        if(reach)
                        {
                            Check(row.name+"-hand-in-front-of-face",row.headDepth-row.handDepth>character.size.y*.1f,row.headDepth-row.handDepth);
                            Check(row.name+"-visible-hand-stays-sharp",row.handPixels>20&&row.handMaximumError<.0002f,row.handMaximumError);
                            Check(row.name+"-face-focused-physical-blurs-hand",row.handPhysicalChanged>20,row.handPhysicalChanged);
                        }
                        rows.cases.Add(row);
                    }
                }
                Action<DesktopFrameRenderer.Frame> fail=frame=>throw new InvalidOperationException("Deliberate focus observer control");
                example.FrameProduced+=fail;
                try {Check("observer-exception-fails-current-display",!example.RenderOffscreen(.7)&&!example.HasCompletedFrame&&example.LastError.Contains("Deliberate focus observer"));}
                finally {example.FrameProduced-=fail;}
                example.ResetHistory();run("focus-observer-recovered");
                Check("observer-exception-recovery",example.HasCompletedFrame&&example.LastError==null&&!observed.IsCurrent);
            }
            finally
            {
                example.FrameProduced-=Observe;setPoseHooks(null,null);Restore();
                for(int i=0;i<skins.Length;i++)skins[i].forceMatrixRecalculationPerRender=recalculate[i];
                File.WriteAllText(Path.Combine(directory,"character-focus-diagnostics.json"),JsonUtility.ToJson(rows,true));
            }
        }
    }
}
