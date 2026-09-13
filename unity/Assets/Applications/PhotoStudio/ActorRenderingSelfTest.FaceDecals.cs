using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyAuthoredFaceDecals(Report report)
        {
            yield return null;
            var others=FindObjectsOfType<Renderer>();var forced=others.Select(r=>r.forceRenderingOff).ToArray();
            foreach(var r in others)r.forceRenderingOff=true;
            var active=RenderTexture.active;CommandBuffer cb=null;FaceDecalRenderer owner=null;
            bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_FACE_DECALS")=="1",started=false;
            try
            {
                void Check(string name,bool ok,float difference=0)=>FrameworkCheck(report,"face-decal-"+name,ok,difference);
                const int width=113,height=79;
                var camera=Own(new GameObject("Authored decal actual camera")).AddComponent<Camera>();camera.enabled=false;camera.cullingMask=1<<26;
                camera.orthographic=true;camera.orthographicSize=1;camera.aspect=width/(float)height;camera.nearClipPlane=.1f;camera.farClipPlane=10;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.11f,.21f,.32f,.63f);camera.allowHDR=true;camera.allowMSAA=false;
                var target=Own(new RenderTexture(width,height,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                var receiverObject=Own(new GameObject("Explicit face decal receiver"));receiverObject.layer=26;
                var mesh=Own(new Mesh{name="Independent decal plane"});
                mesh.vertices=new[]{new Vector3(-5,-5,3),new Vector3(5,-5,3),new Vector3(5,5,3),new Vector3(-5,5,3)};
                mesh.normals=Enumerable.Repeat(Vector3.back,4).ToArray();mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.triangles=new[]{0,2,1,0,3,2};mesh.RecalculateBounds();
                receiverObject.AddComponent<MeshFilter>().sharedMesh=mesh;var receiver=receiverObject.AddComponent<MeshRenderer>();
                var source=Own(new Material(Shader.Find("Hidden/PhotoStudio/FaceDecalProbe")));var baseColor=new Color(.17f,.31f,.47f,.61f);source.SetVector("_ProbeTint",baseColor);receiver.sharedMaterial=source;
                source.SetInt("_ProbeStencilRef",5);source.SetInt("_ProbeStencilWrite",255);
                var originalBlock=new MaterialPropertyBlock();originalBlock.SetFloat("_TestUnrelated",.731f);receiver.SetPropertyBlock(originalBlock);
                var atlas=Own(new Texture2D(8,4,TextureFormat.RGBAFloat,false,true));atlas.wrapMode=TextureWrapMode.Clamp;atlas.filterMode=FilterMode.Bilinear;
                var texels=new Color[32];for(int y=0;y<4;y++)for(int x=0;x<8;x++)texels[y*8+x]=new Color(.2f+x*.1f,.1f+y*.15f,.3f+x*.02f,.2f+(x+y)*.035f);
                atlas.SetPixels(texels);atlas.Apply();
                var config=new FaceDecalRenderer.Receiver{surface=new SceneDepthData.Surface{renderer=receiver,cull=CullMode.Off}};
                var configs=new[]{config};owner=new FaceDecalRenderer();cb=new CommandBuffer{name="Actual independent authored decal test"};camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha,cb);
                Color[] Render(){camera.Render();return ReadSceneTarget(target);}
                var baseline=Render();
                void Compare(string label,Color[] expected,Color[] actual)
                {
                    double sum=0;float maximum=0;bool finite=true,alpha=true;
                    for(int i=0;i<actual.Length;i++)for(int c=0;c<4;c++){float d=Mathf.Abs(expected[i][c]-actual[i][c]);maximum=Mathf.Max(maximum,d);sum+=d;finite&=!float.IsNaN(d)&&!float.IsInfinity(d);if(c==3)alpha&=expected[i].a==actual[i].a;}
                    Check(label+"-whole-image-max",finite&&maximum<=.0005f,maximum);Check(label+"-whole-image-mean",finite&&sum/(actual.Length*4)<=.00002f,(float)(sum/(actual.Length*4)));Check(label+"-alpha-exact",alpha);
                }
                var poses=new[]{new FaceDecalPose{size=new Vector3(1.37f,1.13f,.41f),edgeFeather=.17f,tint=new Vector4(.8f,.7f,1.1f,.8f),opacity=.7f},
                    new FaceDecalPose{size=new Vector3(.93f,1.27f,.53f),positionOffset=new Vector3(.17f,-.13f,.03f),rotationDegrees=new Vector3(9,13,24),uvScale=new Vector2(.4f,.7f),uvBias=new Vector2(.21f,.13f),uvRotationDegrees=37,edgeFeather=.11f,opacity=.63f,blend=FaceDecalBlend.AlphaModulated}};
                FaceDecalProjector.Data[] Data()=>poses.Select(p=>new FaceDecalProjector.Data(Matrix4x4.Translate(new Vector3(0,0,3)),p.Copy())).ToArray();
                Color[] Reference(FaceDecalProjector.Data[] data)
                {
                    var expected=(Color[])baseline.Clone();
                    for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                    {
                        Ray ray=camera.ViewportPointToRay(new Vector3((x+.5f)/width,(y+.5f)/height,0));float distance=(3-ray.origin.z)/ray.direction.z;
                        var world=ray.origin+ray.direction*distance;var color=expected[y*width+x];
                        foreach(var d in data)color=IndependentFaceDecal(color,world,Vector3.back,d,texels,8,4);
                        expected[y*width+x]=color;
                    }
                    return expected;
                }
                if(capture)started=RenderDocCaptureBridge.BeginOffscreenCapture();
                for(int sample=0;sample<8;sample++)
                {
                    camera.orthographic=sample<4;camera.fieldOfView=37;
                    camera.projectionMatrix=sample==7?Matrix4x4.Frustum(-.065f,.081f,-.045f,.057f,.1f,10):camera.orthographic?Matrix4x4.Ortho(-camera.aspect,camera.aspect,-1,1,.1f,10):Matrix4x4.Perspective(37,camera.aspect,.1f,10);
                    cb.Clear();baseline=Render();var data=Data();
                    if(sample%4==1){data[0].pose.edgeFeather=0;data[1].pose.edgeFeather=0;}
                    if(sample%4==2){data[0].pose.pivot=new Vector3(.13f,-.11f,.04f);data[0].pose.scale=new Vector3(-1.2f,.8f,1.1f);data[1].pose.angleFadeStart=0;data[1].pose.angleFadeEnd=60;}
                    if(sample==6)data[0].parentToWorld=Matrix4x4.Translate(new Vector3(0,0,3))*Matrix4x4.TRS(new Vector3(.1f,-.05f,0),Quaternion.Euler(3,7,13),new Vector3(-1.1f,.85f,1));
                    if(sample%4==3)Array.Reverse(data);
                    Check("sample-"+sample+"-prepare",owner.TryPrepare(atlas,data,configs));Check("sample-"+sample+"-record",owner.Record(cb));
                    var actual=Render();Compare("sample-"+sample,Reference(data),actual);
                    Check("sample-"+sample+"-positive-coverage",actual.Where((v,i)=>Mathf.Abs(v.r-baseline[i].r)>.002f).Count()>20);
                    if(sample==0)SaveSsrPreview("authored-face-decals",actual,width,height,false);
                }
                if(started){Check("native-capture-end",RenderDocCaptureBridge.EndOffscreenCapture());started=false;}
                // Frame/keyframe path is independent of a Unity object and caller baseline.
                var animation=new FaceDecalAnimation{duration=2,tracks=new[]{new FaceDecalAnimation.Track{channel=FaceDecalChannel.Opacity,keys=new[]{new FaceDecalAnimation.Key(0,0),new FaceDecalAnimation.Key(1,1,FaceDecalInterpolation.Hold),new FaceDecalAnimation.Key(2,.25f)}}}};
                foreach(var time in new[]{-1.0,0,.25,1,1.9,2,9,.25})
                {
                    bool ok=animation.TrySample(poses[0],time,out var value,out var reason);float expected=time<=0?0:time<1?(float)time:time<2?1:.25f;
                    Check("curve-clamp-"+time.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"-"+report.checks.Count,ok&&value.opacity==expected&&poses[0].opacity==.7f);
                }
                animation.wrap=FaceDecalWrap.Loop;Check("negative-loop",animation.TrySample(poses[0],-.25,out var loop,out _)&&loop.opacity==1);
                animation.wrap=FaceDecalWrap.PingPong;Check("pingpong-turn",animation.TrySample(poses[0],2.75,out var ping,out _)&&ping.opacity==1);
                animation.tracks[0].keys[0].interpolation=FaceDecalInterpolation.Hermite;
                Check("hermite-mid",animation.TrySample(poses[0],.25,out var hermite,out _)&&Mathf.Abs(hermite.opacity-.15625f)<1e-7f);
                Check("nonfinite-time-rejected",!animation.TrySample(poses[0],double.NaN,out var missing,out _)&&missing==null);
                animation.tracks[0].keys[1].seconds=0;Check("duplicate-key-rejected",!animation.TrySample(poses[0],.3,out missing,out _)&&missing==null);
                var projectorObject=Own(new GameObject("Animatable decal fields"));var component=projectorObject.AddComponent<FaceDecalProjector>();
                var clip=Own(new AnimationClip{legacy=true});clip.SetCurve("",typeof(FaceDecalProjector),"opacity",AnimationCurve.Linear(0,0,1,1));
                clip.SetCurve("",typeof(FaceDecalProjector),"uvRotationDegrees",AnimationCurve.Linear(0,0,1,90));
                clip.SampleAnimation(projectorObject,.25f);Check("actual-animationclip-component-fields",Mathf.Abs(component.opacity-.25f)<1e-6f&&Mathf.Abs(component.uvRotationDegrees-22.5f)<1e-6f,component.opacity);
                clip.SampleAnimation(projectorObject,.75f);clip.SampleAnimation(projectorObject,.25f);Check("actual-animationclip-seek",Mathf.Abs(component.opacity-.25f)<1e-6f,component.opacity);
                cb.Clear();Check("empty-no-draw-no-material",owner.TryPrepare(null,Array.Empty<FaceDecalProjector.Data>(),configs)&&owner.Draws.Count==0&&owner.MaterialCount==0);Compare("clear-default-restored",baseline,Render());
                Check("source-material-intact",receiver.sharedMaterial==source&&source.GetVector("_ProbeTint")== (Vector4)baseColor);
                var block=new MaterialPropertyBlock();receiver.GetPropertyBlock(block);Check("source-block-intact",block.GetFloat("_TestUnrelated")==.731f);
                Check("duplicate-receiver-rejected",!owner.TryPrepare(atlas,Data(),new[]{config,config})&&owner.Draws.Count==0);
                var bad=Data();bad[0].pose.size.x=0;Check("zero-size-rejected",!owner.TryPrepare(atlas,bad,configs)&&owner.Draws.Count==0);
                bad=Data();bad[0].parentToWorld=Matrix4x4.zero;Check("singular-parent-rejected",!owner.TryPrepare(atlas,bad,configs));
                bad=Data();bad[0].pose.tint.x=float.NaN;Check("nonfinite-tint-rejected",!owner.TryPrepare(atlas,bad,configs));
                Check("projector-capacity-rejected",!owner.TryPrepare(atlas,Enumerable.Repeat(Data()[0],9).ToArray(),configs));
                using(var second=new FaceDecalRenderer())
                {
                    Check("two-independent-owners",owner.TryPrepare(atlas,Data(),configs)&&second.TryPrepare(atlas,new[]{Data()[1]},configs)&&owner.Draws[0].material!=second.Draws[0].material);
                    Check("snapshot-not-input-alias",owner.Draws[0].surface!=config.surface);
                    receiver.forceRenderingOff=true;Check("lost-active-receiver-rejected",!owner.Record(cb)&&owner.Draws.Count==0);receiver.forceRenderingOff=false;
                }
                owner.Dispose();Check("disposed-rejected",!owner.TryPrepare(atlas,Data(),configs));owner.Dispose();
                camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha,cb);
                VerifyFaceDecalLifecycle(report,camera,target,receiver,source,atlas,config,poses,texels);
                VerifyFaceDecalGuards(report,receiver,atlas,config,poses);
                receiver.forceRenderingOff=true;
                var deformed=VerifyFaceDecalDeformedGeometry(report,camera,target,source,atlas);
                while(deformed.MoveNext())yield return deformed.Current;
                (deformed as IDisposable)?.Dispose();
            }
            finally
            {
                if(started)RenderDocCaptureBridge.EndOffscreenCapture();cb?.Clear();cb?.Release();owner?.Dispose();
                RenderTexture.active=active;for(int i=0;i<others.Length;i++)if(others[i]!=null)others[i].forceRenderingOff=forced[i];
            }
        }

        private void VerifyFaceDecalLifecycle(Report report,Camera camera,RenderTexture target,MeshRenderer receiver,Material source,Texture2D atlas,
            FaceDecalRenderer.Receiver config,FaceDecalPose[] poses,Color[] texels)
        {
            void Check(string name,bool ok,float difference=0)=>FrameworkCheck(report,"face-decal-lifecycle-"+name,ok,difference);
            Color[] Render(){camera.Render();return ReadSceneTarget(target);}
            var baseline=Render();var baseColor=(Color)source.GetVector("_ProbeTint");
            var root=Own(new GameObject("Authored animated projection group"));root.transform.position=new Vector3(0,0,3);
            var projectors=new FaceDecalProjector[poses.Length];
            for(int i=0;i<poses.Length;i++){var obj=Own(new GameObject("Projector "+i));obj.transform.SetParent(root.transform,false);projectors[i]=obj.AddComponent<FaceDecalProjector>();Check("typed-pose-apply-"+i,projectors[i].TryApplyPose(poses[i],out _));}
            var layer=camera.gameObject.AddComponent<FaceDecalLayer>();layer.atlas=atlas;layer.projectors=projectors;layer.receivers=new[]{config};
            void Oracle(string label,Color[] before)
            {
                var data=projectors.Where(p=>p.isActiveAndEnabled).Select(p=>p.Snapshot()).ToArray();var expected=(Color[])before.Clone();
                for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)
                {
                    int i=y*target.width+x;
                    // Opaque/cutout visibility is the recorded input, not a pixel exclusion.
                    // Every foreground/clear pixel remains in the equality calculation.
                    if(before[i]!=baseColor)continue;
                    Ray ray=camera.ViewportPointToRay(new Vector3((x+.5f)/target.width,(y+.5f)/target.height,0));var world=ray.origin+ray.direction*((3-ray.origin.z)/ray.direction.z);
                    foreach(var d in data)expected[i]=IndependentFaceDecal(expected[i],world,Vector3.back,d,texels,8,4);
                }
                var actual=Render();float maximum=0;double sum=0;bool alpha=true;
                for(int i=0;i<actual.Length;i++)for(int c=0;c<4;c++){float d=Mathf.Abs(actual[i][c]-expected[i][c]);maximum=Mathf.Max(maximum,d);sum+=d;if(c==3)alpha&=actual[i].a==expected[i].a;}
                Check(label+"-whole-image",maximum<=.0005f&&sum/(actual.Length*4)<=.00002f,maximum);Check(label+"-alpha",alpha);
                Check(label+"-actual-submission",layer.SubmittedReceivers==1&&layer.UnavailableReason==null);
            }
            try
            {
                Check("default-component-no-command",Render().SequenceEqual(baseline)&&camera.GetCommandBuffers(CameraEvent.BeforeForwardAlpha).Length==0);
                layer.decalsEnabled=true;Oracle("actual-camera-adapter",baseline);
                var clip=Own(new AnimationClip{legacy=true});clip.SetCurve("Projector 0",typeof(FaceDecalProjector),"opacity",AnimationCurve.Linear(0,.2f,1,.9f));
                clip.SetCurve("Projector 0",typeof(FaceDecalProjector),"uvRotationDegrees",AnimationCurve.Linear(0,-30,1,45));
                clip.SampleAnimation(root,.2f);Oracle("animationclip-render-020",baseline);var first=Render();
                clip.SampleAnimation(root,.8f);var changed=Render();Check("animation-changes-actual-image",!changed.SequenceEqual(first));
                clip.SampleAnimation(root,.2f);Check("animation-seek-image-exact",first.SequenceEqual(Render()));
                layer.decalsEnabled=false;Check("disable-restores-image-and-command",Render().SequenceEqual(baseline)&&camera.GetCommandBuffers(CameraEvent.BeforeForwardAlpha).Length==0);
                layer.decalsEnabled=true;Oracle("reenable",baseline);
                projectors[0].size.x=0;Check("bad-pose-current-frame-cleared",Render().SequenceEqual(baseline)&&layer.UnavailableReason!=null&&layer.SubmittedReceivers==0);projectors[0].size.x=poses[0].size.x;Oracle("bad-pose-recovery",baseline);
                var collision=new MaterialPropertyBlock();receiver.GetPropertyBlock(collision);collision.SetFloat("_AuthoredDecalCount",8);receiver.SetPropertyBlock(collision);
                Check("reserved-block-rejected-not-overridden",Render().SequenceEqual(baseline)&&layer.UnavailableReason!=null);collision.Clear();collision.SetFloat("_TestUnrelated",.731f);receiver.SetPropertyBlock(collision);Oracle("reserved-block-recovery",baseline);
                camera.cullingMask=0;Render();Check("camera-layer-mask-no-draw",layer.SubmittedReceivers==0&&camera.GetCommandBuffers(CameraEvent.BeforeForwardAlpha).Length==0);camera.cullingMask=1<<26;
                var secondCamera=Own(new GameObject("Other camera no decal state")).AddComponent<Camera>();secondCamera.CopyFrom(camera);secondCamera.enabled=false;secondCamera.targetTexture=target;
                secondCamera.Render();Check("second-camera-isolation",ReadSceneTarget(target).SequenceEqual(baseline));
                config.stencilComparison=CompareFunction.Never;Check("explicit-stencil-rejection",Render().SequenceEqual(baseline));config.stencilComparison=CompareFunction.Always;
                config.stencilComparison=CompareFunction.Equal;config.stencilReference=5;Oracle("populated-stencil-matches",baseline);
                config.stencilReference=7;Check("populated-stencil-mismatch",Render().SequenceEqual(baseline));config.stencilReference=0;config.stencilComparison=CompareFunction.Always;
                var mask=Own(new Texture2D(2,2,TextureFormat.RGBA32,false,true));mask.filterMode=FilterMode.Point;mask.wrapMode=TextureWrapMode.Clamp;
                mask.SetPixels(new[]{Color.clear,Color.white,Color.white,Color.clear});mask.Apply();source.SetTexture("_ProbeMask",mask);source.SetFloat("_ProbeCutoff",.5f);config.surface.alphaMask=mask;config.surface.alphaCutoff=.5f;
                layer.decalsEnabled=false;var cutout=Render();Check("cutout-has-visible-and-empty",cutout.Any(c=>c==baseColor)&&cutout.Any(c=>c!=baseColor));layer.decalsEnabled=true;Oracle("matching-cutout-depth",cutout);
                source.SetTexture("_ProbeMask",Texture2D.whiteTexture);source.SetFloat("_ProbeCutoff",0);config.surface.alphaMask=null;config.surface.alphaCutoff=0;
                var foreground=Own(new GameObject("Foreground occluder"));foreground.layer=26;var fm=Own(new Mesh());
                fm.vertices=new[]{new Vector3(-.6f,-5,2.5f),new Vector3(.2f,-5,2.5f),new Vector3(.2f,5,2.5f),new Vector3(-.6f,5,2.5f)};
                fm.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};fm.triangles=new[]{0,2,1,0,3,2};fm.RecalculateBounds();foreground.AddComponent<MeshFilter>().sharedMesh=fm;
                var fr=foreground.AddComponent<MeshRenderer>();var fmat=Own(new Material(source));fmat.SetVector("_ProbeTint",new Vector4(.81f,.08f,.11f,.9f));fr.sharedMaterial=fmat;
                layer.decalsEnabled=false;var occluded=Render();Check("foreground-positive-control",occluded.Any(c=>c.r>.8f)&&occluded.Any(c=>c==baseColor));layer.decalsEnabled=true;Oracle("foreground-occlusion",occluded);fr.forceRenderingOff=true;
                var dynamicAtlas=Own(new RenderTexture(8,4,0,RenderTextureFormat.ARGBFloat));dynamicAtlas.Create();
                var beforeBlit=RenderTexture.active;try{Graphics.Blit(atlas,dynamicAtlas);}finally{RenderTexture.active=beforeBlit;}
                layer.atlas=dynamicAtlas;Render();dynamicAtlas.Release();
                Check("lost-atlas-current-frame-cleared",Render().SequenceEqual(baseline)&&layer.UnavailableReason!=null);layer.atlas=atlas;Oracle("atlas-recovery",baseline);
                camera.rect=new Rect(0,0,.5f,1);Render();Check("partial-viewport-rejected",layer.SubmittedReceivers==0&&layer.UnavailableReason!=null);camera.rect=new Rect(0,0,1,1);
                foreach(var p in projectors)p.enabled=false;Check("disabled-projectors-zero-work",Render().SequenceEqual(baseline)&&layer.SubmittedReceivers==0);foreach(var p in projectors)p.enabled=true;
                layer.enabled=false;Check("component-disable-removes-owned-commands",camera.GetCommandBuffers(CameraEvent.BeforeForwardAlpha).Length==0&&Render().SequenceEqual(baseline));
                layer.enabled=true;Oracle("component-enable-restores",baseline);
            }
            finally
            {
                layer.enabled=false;source.SetTexture("_ProbeMask",Texture2D.whiteTexture);source.SetFloat("_ProbeCutoff",0);
                config.surface.alphaMask=null;config.surface.alphaCutoff=0;config.stencilComparison=CompareFunction.Always;
                camera.rect=new Rect(0,0,1,1);camera.cullingMask=1<<26;
            }
        }

        private void VerifyFaceDecalGuards(Report report,MeshRenderer receiver,Texture atlas,FaceDecalRenderer.Receiver config,FaceDecalPose[] poses)
        {
            void Check(string name,bool ok)=>FrameworkCheck(report,"face-decal-guard-"+name,ok);
            var inputs=new[]{new FaceDecalProjector.Data(Matrix4x4.Translate(new Vector3(0,0,3)),poses[0].Copy())};
            using(var owner=new FaceDecalRenderer())
            {
                bool Prepare()=>owner.TryPrepare(atlas,inputs,new[]{config});
                Check("receiver-capacity",!owner.TryPrepare(atlas,inputs,Enumerable.Repeat(config,129).ToArray()));
                Check("null-atlas",!owner.TryPrepare(null,inputs,new[]{config}));
                var cube=Own(new Cubemap(4,TextureFormat.RGBA32,false));Check("cube-atlas",!owner.TryPrepare(cube,inputs,new[]{config}));
                int slot=config.surface.materialIndex;config.surface.materialIndex=99;Check("missing-submesh",!Prepare());config.surface.materialIndex=slot;
                var scale=config.surface.vertexScale;config.surface.vertexScale=Vector3.zero;Check("degenerate-vertex-scale",!Prepare());config.surface.vertexScale=scale;
                config.surface.alphaCutoff=float.NaN;Check("nonfinite-cutout",!Prepare());config.surface.alphaCutoff=0;
                inputs[0].pose.scale.x=0;Check("singular-projector-scale",!Prepare());inputs[0].pose.scale.x=1;
                inputs[0].pose.angleFadeStart=90;inputs[0].pose.angleFadeEnd=45;Check("reversed-angle",!Prepare());inputs[0].pose.angleFadeStart=inputs[0].pose.angleFadeEnd=180;
                inputs[0].pose.opacity=0;Check("zero-opacity-no-owned-work",Prepare()&&owner.MaterialCount==0&&owner.Draws.Count==0);inputs[0].pose.opacity=.7f;
                var filter=receiver.GetComponent<MeshFilter>();var original=filter.sharedMesh;var stripped=Own(Instantiate(original));stripped.normals=Array.Empty<Vector3>();filter.sharedMesh=stripped;
                Check("normal-free-unangular",Prepare());inputs[0].pose.angleFadeStart=0;Check("angular-requires-normal",!Prepare());inputs[0].pose.angleFadeStart=180;filter.sharedMesh=original;
                Check("recovery",Prepare());var oldScale=receiver.transform.localScale;receiver.transform.localScale=Vector3.zero;
                var command=new CommandBuffer();try{Check("record-current-singular-transform",!owner.Record(command)&&owner.Draws.Count==0);}finally{command.Release();receiver.transform.localScale=oldScale;}
            }
            var baseline=new FaceDecalPose{angleFadeStart=0,angleFadeEnd=180};
            var tracks=new List<FaceDecalAnimation.Track>();var expected=baseline.Copy();
            for(int channel=0;channel<28;channel++)
            {
                float value=channel==25?.2f:channel==26?30:channel==27?90:.1f+channel*.025f;
                tracks.Add(new FaceDecalAnimation.Track{channel=(FaceDecalChannel)channel,keys=new[]{new FaceDecalAnimation.Key(.2f,value),new FaceDecalAnimation.Key(.8f,value)}});
                if(channel<3)expected.positionOffset[channel]=value;else if(channel<6)expected.rotationDegrees[channel-3]=value;
                else if(channel<9)expected.scale[channel-6]=value;else if(channel<12)expected.size[channel-9]=value;else if(channel<15)expected.pivot[channel-12]=value;
                else if(channel<17)expected.uvScale[channel-15]=value;else if(channel<19)expected.uvBias[channel-17]=value;else if(channel==19)expected.uvRotationDegrees=value;
                else if(channel<24)expected.tint[channel-20]=value;else if(channel==24)expected.opacity=value;else if(channel==25)expected.edgeFeather=value;else if(channel==26)expected.angleFadeStart=value;else expected.angleFadeEnd=value;
            }
            var animation=new FaceDecalAnimation{tracks=tracks.ToArray()};var before=JsonUtility.ToJson(baseline);
            foreach(double time in new[]{0.0,.5,1.0})Check("all-28-channels-"+time,animation.TrySample(baseline,time,out var p,out _)&&JsonUtility.ToJson(p)==JsonUtility.ToJson(expected));
            var roundtrip=JsonUtility.FromJson<FaceDecalAnimation>(JsonUtility.ToJson(animation));Check("typed-json-roundtrip",roundtrip.TrySample(baseline,.5,out var sampled,out _)&&JsonUtility.ToJson(sampled)==JsonUtility.ToJson(expected));
            animation.tracks[1].channel=animation.tracks[0].channel;Check("duplicate-channel-atomic",!animation.TrySample(baseline,.5,out sampled,out _)&&sampled==null&&JsonUtility.ToJson(baseline)==before);
            animation.tracks=new[]{new FaceDecalAnimation.Track{channel=FaceDecalChannel.Opacity,keys=new[]{new FaceDecalAnimation.Key(0,.5f,FaceDecalInterpolation.Hermite){outTangent=10},new FaceDecalAnimation.Key(1,.5f){inTangent=-10}}}};
            Check("curve-overshoot-rejected-not-clamped",!animation.TrySample(baseline,.5,out sampled,out _)&&sampled==null);
            animation.tracks[0].keys=new[]{new FaceDecalAnimation.Key(0,0),new FaceDecalAnimation.Key(1,1)};
            animation.wrap=FaceDecalWrap.Loop;Check("loop-end-is-start",animation.TrySample(baseline,1,out sampled,out _)&&sampled.opacity==0);
            animation.wrap=FaceDecalWrap.PingPong;Check("pingpong-end-is-end",animation.TrySample(baseline,1,out sampled,out _)&&sampled.opacity==1);
            animation.duration=.000001f;animation.tracks=Array.Empty<FaceDecalAnimation.Track>();Check("minimum-float-duration-boundary",animation.TrySample(baseline,0,out sampled,out _));
            animation.duration=1000000;Check("maximum-float-duration-boundary",animation.TrySample(baseline,1e300,out sampled,out _));
        }

        private IEnumerator VerifyFaceDecalDeformedGeometry(Report report,Camera camera,RenderTexture target,Material material,Texture atlas)
        {
            void Check(string name,bool ok,float value=0)=>FrameworkCheck(report,"face-decal-deformed-"+name,ok,value);
            camera.orthographic=false;camera.fieldOfView=37;camera.ResetProjectionMatrix();
            var mesh=Own(new Mesh());var positions=new[]{new Vector3(-.7f,-.6f,3),new Vector3(.7f,-.6f,3),new Vector3(.7f,.6f,3),new Vector3(-.7f,.6f,3)};
            mesh.vertices=positions;mesh.normals=Enumerable.Repeat(Vector3.back,4).ToArray();mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.triangles=new[]{0,2,1,0,3,2};mesh.RecalculateBounds();
            var reference=Own(new GameObject("Independent CPU-deformed receiver"));reference.layer=26;var filter=reference.AddComponent<MeshFilter>();var cpu=Own(Instantiate(mesh));filter.sharedMesh=cpu;var renderer=reference.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;
            var skinObject=Own(new GameObject("Actual skinned decal receiver"));skinObject.layer=26;var skin=skinObject.AddComponent<SkinnedMeshRenderer>();skin.sharedMaterial=material;
            var bone=Own(new GameObject("Actual animated decal receiver bone"));bone.transform.SetParent(skinObject.transform,false);
            var skinned=Own(Instantiate(mesh));skinned.bindposes=new[]{Matrix4x4.identity};skinned.boneWeights=Enumerable.Repeat(new BoneWeight{boneIndex0=0,weight0=1},4).ToArray();
            // Keep the renderer root fixed; the independent oracle below applies the
            // child bone's complete affine transform, including nonuniform scale.
            skin.sharedMesh=skinned;skin.bones=new[]{bone.transform};skin.rootBone=skinObject.transform;skin.localBounds=new Bounds(new Vector3(0,0,3),Vector3.one*8);skin.updateWhenOffscreen=true;skin.forceRenderingOff=true;
            var projector=Own(new GameObject("Projection follows explicit transform" )).AddComponent<FaceDecalProjector>();projector.transform.position=new Vector3(0,0,3);projector.size=new Vector3(1.2f,1.2f,.9f);projector.edgeFeather=.1f;projector.opacity=.8f;
            var layer=camera.gameObject.GetComponent<FaceDecalLayer>();layer.enabled=true;layer.atlas=atlas;layer.projectors=new[]{projector};
            Color[] Render(Renderer current)
            {
                renderer.forceRenderingOff=current!=renderer;skin.forceRenderingOff=current!=skin;
                layer.receivers=new[]{new FaceDecalRenderer.Receiver{surface=new SceneDepthData.Surface{renderer=current,cull=CullMode.Off}}};
                camera.Render();return ReadSceneTarget(target);
            }
            void Compare(string name,Color[] a,Color[] b)
            {
                float maximum=0;double sum=0;for(int i=0;i<a.Length;i++)for(int c=0;c<4;c++){float d=Mathf.Abs(a[i][c]-b[i][c]);maximum=Mathf.Max(maximum,d);sum+=d;}
                Check(name+"-whole-image",maximum<=.0005f&&sum/(a.Length*4)<=.00002f,maximum);
            }
            try
            {
                Color[] first=null;
                for(int sample=0;sample<4;sample++)
                {
                    int pose=sample==3?0:sample;
                    bone.transform.localPosition=new Vector3(pose*.17f,-pose*.05f,pose*.12f);bone.transform.localRotation=Quaternion.Euler(0,0,pose*8);bone.transform.localScale=new Vector3(1+pose*.07f,1-pose*.04f,1);
                    // Match the existing SceneMotion fixture's two-frame skin-update
                    // boundary. An imperative bone write in a coroutine is not proof
                    // that the engine has produced a new renderable skin buffer yet.
                    renderer.forceRenderingOff=true;skin.forceRenderingOff=false;
                    yield return null;yield return null;
                    cpu.vertices=positions.Select(p=>bone.transform.localToWorldMatrix.MultiplyPoint3x4(p)).ToArray();cpu.RecalculateBounds();
                    layer.decalsEnabled=false;var skinOff=Render(skin);var off=Render(renderer);Compare("actual-skin-"+sample+"-base-geometry",off,skinOff);
                    layer.decalsEnabled=true;var expected=Render(renderer);var actual=Render(skin);
                    if(sample==1){SaveSsrPreview("face-decal-skin-cpu-off",off,target.width,target.height,false);SaveSsrPreview("face-decal-skin-actual-off",skinOff,target.width,target.height,false);SaveSsrPreview("face-decal-skin-cpu-on",expected,target.width,target.height,false);SaveSsrPreview("face-decal-skin-actual-on",actual,target.width,target.height,false);}
                    Compare("actual-skin-"+sample,expected,actual);Check("actual-skin-positive-overlay-"+sample,expected.Where((c,i)=>Math.Abs(c.r-off[i].r)>.002).Count()>20);
                    if(sample==0)first=actual;if(sample==3)Check("skin-seek-exact",first.SequenceEqual(actual));
                }
                skin.forceRenderingOff=true;var deltas=positions.Select((p,i)=>new GpuFaceDeformer.Delta(0,i,new Vector3(.25f,.1f,.2f))).ToArray();
                Check("gpu-only-create",GpuFaceDeformer.TryCreate(mesh,1,deltas,null,0,out var gpu,out var reason));if(gpu==null)throw new InvalidOperationException(reason);
                using(gpu)
                {
                    foreach(float weight in new[]{0f,.5f,1f,0f})
                    {
                        Check("gpu-dispatch-"+weight+"-"+report.checks.Count,gpu.TryDispatch(new[]{weight},Array.Empty<Matrix4x4>()));
                        cpu.vertices=positions.Select(p=>p+new Vector3(.25f,.1f,.2f)*weight).ToArray();cpu.RecalculateBounds();filter.sharedMesh=cpu;var expected=Render(renderer);
                        filter.sharedMesh=gpu.Mesh;Compare("gpu-current-"+weight+"-"+report.checks.Count,expected,Render(renderer));
                        Check("gpu-cpu-copy-remains-rest-"+weight+"-"+report.checks.Count,gpu.Mesh.vertices.SequenceEqual(positions));
                    }
                    filter.sharedMesh=cpu;
                }
            }
            finally{layer.enabled=false;renderer.forceRenderingOff=true;skin.forceRenderingOff=true;}
        }

        private static Color IndependentFaceDecal(Color color,Vector3 world,Vector3 normal,FaceDecalProjector.Data data,Color[] texels,int width,int height)
        {
            var p=data.pose;var basis=data.parentToWorld*Matrix4x4.TRS(p.positionOffset,Quaternion.Euler(p.rotationDegrees),p.scale);
            var local=basis.inverse.MultiplyPoint3x4(world)-p.pivot;local=new Vector3(local.x/p.size.x,local.y/p.size.y,local.z/p.size.z);
            double edge=.5-Math.Max(Math.Abs(local.x),Math.Max(Math.Abs(local.y),Math.Abs(local.z)));if(edge<0)return color;
            double coverage=p.edgeFeather==0?1:Math.Min(1,edge/p.edgeFeather);
            if(p.angleFadeStart<180)
            {
                double cosine=Vector3.Dot(normal,-((Vector3)basis.GetColumn(2)).normalized),a=Math.Cos(p.angleFadeStart*Math.PI/180),b=Math.Cos(p.angleFadeEnd*Math.PI/180);
                coverage*=a==b?(cosine>=b?1:0):Math.Max(0,Math.Min(1,(cosine-b)/(a-b)));
            }
            double rad=p.uvRotationDegrees*Math.PI/180;
            double u=((Math.Cos(rad)*local.x-Math.Sin(rad)*local.y)+.5)*p.uvScale.x+p.uvBias.x;
            double v=((Math.Sin(rad)*local.x+Math.Cos(rad)*local.y)+.5)*p.uvScale.y+p.uvBias.y;
            double sx=u*width-.5,sy=v*height-.5;int ix=(int)Math.Floor(sx),iy=(int)Math.Floor(sy);double tx=sx-ix,ty=sy-iy;
            Color Tap(int x,int y)=>texels[Math.Max(0,Math.Min(height-1,y))*width+Math.Max(0,Math.Min(width-1,x))];
            var a00=Tap(ix,iy);var a10=Tap(ix+1,iy);var a01=Tap(ix,iy+1);var a11=Tap(ix+1,iy+1);var texel=new double[4];
            for(int c=0;c<4;c++)texel[c]=(a00[c]*(1-tx)+a10[c]*tx)*(1-ty)+(a01[c]*(1-tx)+a11[c]*tx)*ty;
            double alpha=Math.Max(0,Math.Min(1,texel[3]))*p.tint.w*p.opacity*coverage;
            for(int c=0;c<3;c++)
            {
                double value=texel[c]*p.tint[c];if(p.blend==FaceDecalBlend.AlphaModulated)value=1+(value-1)*Math.Max(0,Math.Min(1,texel[3]));
                color[c]=(float)(color[c]*(1-alpha)+value*alpha);
            }
            return color;
        }
    }
}
