using System;
using System.Collections;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyFxGeometryExposure(Report report)
        {
            const int w=61,h=43;
            void Check(string n,bool ok,float value=0)=>FrameworkCheck(report,"fx-geometry-exposure-"+n,ok,value);
            float Difference(Color[] a,Color[] b){float d=0;for(int p=0;p<a.Length;p++)for(int c=0;c<4;c++)d=Mathf.Max(d,Mathf.Abs(a[p][c]-b[p][c]));return d;}
            RenderTexture Target(RenderTextureFormat f){var t=Own(new RenderTexture(w,h,0,f,RenderTextureReadWrite.Linear){filterMode=FilterMode.Point});t.Create();return t;}
            var source=Target(RenderTextureFormat.ARGBFloat);var depth=Target(RenderTextureFormat.RFloat);var mask=Target(RenderTextureFormat.R8);
            var upload=Own(new Texture2D(w,h,TextureFormat.RGBAFloat,false,true));var pixels=new Color[w*h];
            void Upload(RenderTexture t,Func<int,int,Color> value){for(int y=0;y<h;y++)for(int x=0;x<w;x++)pixels[y*w+x]=value(x,y);upload.SetPixels(pixels);upload.Apply();Graphics.Blit(upload,t);}
            Upload(source,(x,y)=>new Color(.1f+x*.003f,.2f+y*.005f,.3f,.15f+x*.009f));var original=ReadSceneTarget(source);
            Upload(depth,(x,y)=>new Color(12,0,0,0));Upload(mask,(x,y)=>new Color(x<9?1:0,0,0,0));
            var go=Own(new GameObject("Independent coherent FX shutter camera"));var camera=go.AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;
            camera.orthographicSize=1.5f;camera.aspect=w/(float)h;camera.nearClipPlane=.1f;camera.farClipPlane=20;camera.fieldOfView=45;
            var mesh=Own(new Mesh());mesh.vertices=new[]{new Vector3(-.7f,-.7f,0),new Vector3(.7f,-.7f,0),new Vector3(.7f,.7f,0),new Vector3(-.7f,.7f,0)};
            mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.triangles=new[]{0,1,2,0,2,3};mesh.RecalculateBounds();
            var rear=new LowResolutionFxSurface {mesh=mesh,resolution=FxResolution.Full,opacity=.65f,radialSoftness=.7f,linearRadiance=new Vector3(1.5f,.2f,.1f)};
            var front=new LowResolutionFxSurface {mesh=mesh,resolution=FxResolution.Full,opacity=.7f,radialSoftness=.6f,linearRadiance=new Vector3(.1f,.6f,1.8f)};
            var surfaces=new[]{rear,front};var s=new HeavyFxSettings {enabled=true};s.geometry.enabled=true;s.geometry.surfaces=surfaces;
            var referenceSettings=new HeavyFxSettings {enabled=true};referenceSettings.geometry.enabled=true;referenceSettings.geometry.surfaces=surfaces;
            using var renderer=new HeavyFxRenderer();using var reference=new HeavyFxRenderer();
            void Pose(float phase)
            {
                rear.localToWorld=Matrix4x4.TRS(new Vector3(-.1f+.8f*phase,0,5),Quaternion.identity,Vector3.one);
                front.localToWorld=Matrix4x4.TRS(new Vector3(.1f-.6f*phase,.1f,4),Quaternion.identity,Vector3.one);
            }
            HeavyFxRenderer.Frame Frame(HeavyFxRenderer r,HeavyFxSettings settings,double time,RenderTexture protection=null)
            {if(!r.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out var f,protection))throw new InvalidOperationException(r.UnavailableReason);return f;}
            Color[] Mean(Func<int,Color[]> sample,int count)
            {var sum=new double[w*h*4];for(int i=0;i<count;i++){var a=sample(i);for(int p=0;p<a.Length;p++)for(int c=0;c<4;c++)sum[p*4+c]+=a[p][c];}var mean=new Color[w*h];for(int p=0;p<mean.Length;p++)for(int c=0;c<4;c++)mean[p][c]=(float)(sum[p*4+c]/count);return mean;}
            Check("default-disabled",!s.exposure.enabled);
            foreach(var resolution in new[]{FxResolution.Full,FxResolution.Half,FxResolution.Quarter})foreach(bool perspective in new[]{false,true})
            {
                string label=resolution+"-"+perspective;camera.orthographic=!perspective;camera.ResetProjectionMatrix();
                rear.resolution=front.resolution=resolution;s.exposure.enabled=true;s.exposure.samples=16;renderer.ResetMotionHistory();
                Pose(-1);Frame(renderer,s,0);Pose(0);var frame=Frame(renderer,s,.02);var actual=ReadSceneTarget(frame.color);
                Check(label+"-shared-clock-samples",frame.geometryExposureSamples==16&&renderer.ExposureSnapshotDrawCalls==2);
                var expected=Mean(i=>{Pose(((i+.5f)/16*2-1)*.25f);return ReadSceneTarget(Frame(reference,referenceSettings,0).color);},16);
                float error=Difference(actual,expected);Check(label+"-independent-time-geometry-whole-color",error<.0002f,error);
                Pose(0);var sharp=ReadSceneTarget(Frame(reference,referenceSettings,0).color);
                Check(label+"-positive-coverage-response",Difference(actual,sharp)>.001f,Difference(actual,sharp));
                float alpha=0;for(int p=0;p<actual.Length;p++)alpha=Mathf.Max(alpha,Mathf.Abs(actual[p].a-original[p].a));
                Check(label+"-alpha-exact",alpha==0,alpha);
                renderer.ResetMotionHistory();Pose(-1);Frame(renderer,s,0);Pose(0);var replay=ReadSceneTarget(Frame(renderer,s,.02).color);
                Check(label+"-replay-exact",Difference(actual,replay)==0,Difference(actual,replay));
                var paused=ReadSceneTarget(Frame(renderer,s,.02).color);Check(label+"-paused-current-exact",Difference(paused,sharp)==0,Difference(paused,sharp));
                renderer.ResetMotionHistory();var cold=ReadSceneTarget(Frame(renderer,s,.5).color);Check(label+"-reset-current-exact",Difference(cold,sharp)==0,Difference(cold,sharp));
                SaveSsrPreview("fx-geometry-exposure-"+label,actual,w,h,false);
            }
            camera.orthographic=true;camera.ResetProjectionMatrix();rear.resolution=front.resolution=FxResolution.Full;
            // A negative control: composing separately time-averaged alpha layers
            // loses coverage correlation, even though each individual mean is right.
            Color[] LayerMean(LowResolutionFxSurface surface)
            {
                referenceSettings.geometry.surfaces=new[]{surface};
                return Mean(i=>{Pose(((i+.5f)/16*2-1)*.25f);var f=Frame(reference,referenceSettings,0);f.TryGetLastBatch(FxResolution.Full,out var layer,out _);return ReadSceneTarget(layer);},16);
            }
            var rearMean=LayerMean(rear);var frontMean=LayerMean(front);referenceSettings.geometry.surfaces=surfaces;
            var coherent=Mean(i=>{Pose(((i+.5f)/16*2-1)*.25f);return ReadSceneTarget(Frame(reference,referenceSettings,0).color);},16);
            var independent=new Color[w*h];for(int p=0;p<independent.Length;p++){independent[p]=frontMean[p]+rearMean[p]*(1-frontMean[p].a)+original[p]*((1-frontMean[p].a)*(1-rearMean[p].a));independent[p].a=original[p].a;}
            Check("separate-layer-mean-negative-control",Difference(coherent,independent)>.001f,Difference(coherent,independent));
            renderer.ResetMotionHistory();Pose(-1);Frame(renderer,s,0,mask);Pose(0);var protectedColor=ReadSceneTarget(Frame(renderer,s,.02,mask).color);
            float protectedError=0;for(int y=0;y<h;y++)for(int x=0;x<9;x++)for(int c=0;c<4;c++)protectedError=Mathf.Max(protectedError,Mathf.Abs(protectedColor[y*w+x][c]-original[y*w+x][c]));
            Check("protected-composite-exact",protectedError==0,protectedError);
            Check("snapshot-owned-budget",renderer.ExposureSnapshotBytes==256&&renderer.TargetCount==10,renderer.ExposureSnapshotBytes);
            Pose(0);var current=ReadSceneTarget(Frame(reference,referenceSettings,0).color);
            var rewind=ReadSceneTarget(Frame(renderer,s,.01).color);Check("rewind-cold-exact",Difference(current,rewind)==0,Difference(current,rewind));
            var longGap=ReadSceneTarget(Frame(renderer,s,10).color);Check("long-gap-cold-exact",Difference(current,longGap)==0,Difference(current,longGap));
            s.exposure.shutterAngle=0;var zero=ReadSceneTarget(Frame(renderer,s,10.02).color);Check("zero-shutter-exact",Difference(current,zero)==0,Difference(current,zero));s.exposure.shutterAngle=180;
            rear.motionRevision++;front.motionRevision++;var revised=ReadSceneTarget(Frame(renderer,s,10.04).color);Check("revision-cold-exact",Difference(current,revised)==0,Difference(current,revised));
            s.exposure.samples=3;Check("invalid-sample-budget-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,s,11,out _)&&renderer.TargetCount==0);s.exposure.samples=16;
            s.medium.enabled=true;Check("unsupported-medium-explicit",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,s,11,out _)&&renderer.UnavailableReason!=null);s.medium.enabled=false;
            front.blend=FxBlend.Distortion;Check("unsupported-distortion-explicit",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,s,11,out _)&&renderer.UnavailableReason!=null);front.blend=FxBlend.Alpha;
            var lease=Frame(renderer,s,12);renderer.Dispose();Check("dispose-invalidates-and-releases",!lease.IsCurrent&&renderer.TargetCount==0);

            // Independently change quadrature size, blend and ordered resolution
            // boundaries. The oracle renders actual authored sub-time geometry.
            front.blend=FxBlend.Additive;rear.resolution=FxResolution.Half;front.resolution=FxResolution.Quarter;
            foreach(int samples in new[]{2,4,8,32})
            {
                s.exposure.samples=samples;renderer.ResetMotionHistory();Pose(-1);Frame(renderer,s,0);Pose(0);
                var actual=ReadSceneTarget(Frame(renderer,s,.02).color);
                var expected=Mean(i=>{Pose(((i+.5f)/samples*2-1)*.25f);return ReadSceneTarget(Frame(reference,referenceSettings,0).color);},samples);
                float error=Difference(actual,expected);Check("mixed-alpha-additive-"+samples+"-independent-mean",error<.0002f,error);
            }
            front.blend=FxBlend.Alpha;rear.resolution=front.resolution=FxResolution.Full;s.exposure.samples=16;Pose(0);
            void Cold(string name,double t)
            {
                var a=ReadSceneTarget(Frame(renderer,s,t).color);var b=ReadSceneTarget(Frame(reference,referenceSettings,0).color);
                Check(name,renderer.ExposureSamples==1&&Difference(a,b)==0,Difference(a,b));
            }
            renderer.ResetMotionHistory();Frame(renderer,s,0);
            camera.transform.position=Vector3.right*2;Cold("camera-cut-current-exact",.02);camera.transform.position=Vector3.zero;
            Frame(renderer,s,0);camera.orthographicSize+=.01f;Cold("projection-change-current-exact",.02);camera.orthographicSize-=.01f;
            Frame(renderer,s,0);mesh.triangles=new[]{0,1,3,1,2,3};Cold("same-count-topology-change-current-exact",.02);mesh.triangles=new[]{0,1,2,0,2,3};
            s.exposure.maximumTrackedVertices=7;
            Check("vertex-budget-rejects-and-releases",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,s,1,out _)&&renderer.TargetCount==0);
            s.exposure.maximumTrackedVertices=1000000;s.geometry.surfaces=new[]{rear,rear};
            Check("duplicate-instance-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,s,1,out _)&&renderer.TargetCount==0);
            s.geometry.surfaces=surfaces;Frame(renderer,s,0);var owned=Frame(renderer,s,.02).color;
            Check("owned-input-alias-rejected",!renderer.TryRender(owned,new FogVolumeDepth(depth),camera,s,.04,out _)&&renderer.TargetCount==0);
            // Nonfinite RFloat masks have the same exact fallback as positive
            // masks, including after accumulation and a null-mask transition.
            var unusual=Target(RenderTextureFormat.RFloat);Upload(unusual,(x,y)=>new Color(x<9?float.NaN:0,0,0,0));
            renderer.ResetMotionHistory();Pose(-1);Frame(renderer,s,0,unusual);Pose(0);
            var protectedNan=ReadSceneTarget(Frame(renderer,s,.02,unusual).color);float nanError=0;
            for(int y=0;y<h;y++)for(int x=0;x<9;x++)for(int c=0;c<4;c++)nanError=Mathf.Max(nanError,Mathf.Abs(protectedNan[y*w+x][c]-original[y*w+x][c]));
            Check("nonfinite-protection-exact",nanError==0,nanError);
            s.exposure.enabled=false;Frame(renderer,s,.04);
            Check("disabled-releases-exposure-targets",renderer.ExposureSnapshotBytes==0&&renderer.TargetCount==5&&renderer.ExposureSamples==1);
            s.exposure.enabled=true;

            // A fixed renderer root and moving child bone ensure the endpoint
            // shader sees the actual engine-deformed stream, not just a matrix.
            var rig=Own(new GameObject("FX exposure actual GPU skin"));rig.layer=26;
            var bone=Own(new GameObject("FX exposure child bone"));bone.transform.SetParent(rig.transform,false);
            var skinMesh=Own(Instantiate(mesh));var weight=new BoneWeight {boneIndex0=0,weight0=1};
            skinMesh.boneWeights=new[]{weight,weight,weight,weight};skinMesh.bindposes=new[]{Matrix4x4.identity};
            skinMesh.normals=new[]{Vector3.back,Vector3.back,Vector3.back,Vector3.back};
            var skin=rig.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=skinMesh;skin.bones=new[]{bone.transform};skin.rootBone=rig.transform;
            skin.sharedMaterial=Own(new Material(Resources.Load<Shader>("HeavyFx")));skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(new Vector3(0,0,5),Vector3.one*12);
            var skinnedSurface=new LowResolutionFxSurface {renderer=skin,opacity=rear.opacity,radialSoftness=rear.radialSoftness,linearRadiance=rear.linearRadiance,resolution=FxResolution.Full};
            s.geometry.surfaces=new[]{skinnedSurface};referenceSettings.geometry.surfaces=new[]{rear};
            renderer.ResetMotionHistory();bone.transform.localPosition=new Vector3(-.8f,0,5);
            yield return null;yield return null;
            Frame(renderer,s,0);bone.transform.localPosition=new Vector3(0,0,5);
            yield return null;yield return null;
            var skinActual=ReadSceneTarget(Frame(renderer,s,.02).color);
            var skinExpected=Mean(i=>{rear.localToWorld=Matrix4x4.Translate(new Vector3(((i+.5f)/16*2-1)*.25f*.8f,0,5));return ReadSceneTarget(Frame(reference,referenceSettings,0).color);},16);
            float skinError=Difference(skinActual,skinExpected);Check("actual-skinned-endpoints-independent-mean",skinError<.0002f,skinError);
            rear.localToWorld=Matrix4x4.Translate(new Vector3(0,0,5));var skinSharp=ReadSceneTarget(Frame(reference,referenceSettings,0).color);
            var skinPaused=ReadSceneTarget(Frame(renderer,s,.02).color);
            Check("actual-skin-current-matches-independent-matrix",Difference(skinPaused,skinSharp)<.0002f,Difference(skinPaused,skinSharp));
            Check("actual-skin-positive-coverage",Difference(skinActual,skinSharp)>.001f,Difference(skinActual,skinSharp));
            var block=new MaterialPropertyBlock();block.SetFloat("_Unsupported",1);skin.SetPropertyBlock(block);
            Check("renderer-property-block-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,s,.04,out _)&&renderer.TargetCount==0);
            skin.SetPropertyBlock(null);rig.SetActive(false);
        }

        private void VerifyOpaqueExposureSample(Report report)
        {
            const int w=61,h=43;const float dx=3.125f,dy=-1.75f,z=5;
            void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"opaque-exposure-"+name,ok,error);
            RenderTexture Target(RenderTextureFormat format)
            {var t=Own(new RenderTexture(w,h,0,format,RenderTextureReadWrite.Linear){filterMode=FilterMode.Point});t.Create();return t;}
            var source=Target(RenderTextureFormat.ARGBFloat);var guide=Target(RenderTextureFormat.ARGBFloat);
            var depth=Target(RenderTextureFormat.RFloat);var previous=Target(RenderTextureFormat.RFloat);
            var upload=Own(new Texture2D(w,h,TextureFormat.RGBAFloat,false,true));var pixels=new Color[w*h];
            void Upload(RenderTexture t,Func<int,int,Color> fn)
            {for(int y=0;y<h;y++)for(int x=0;x<w;x++)pixels[y*w+x]=fn(x,y);upload.SetPixels(pixels);upload.Apply();Graphics.Blit(upload,t);}
            Upload(source,(x,y)=>new Color(.5f+.3f*Mathf.Sin(x*.7f),.5f+.3f*Mathf.Cos(y*.6f),.1f+x*.01f,.2f+y*.01f));
            var current=ReadSceneTarget(source);Upload(guide,(x,y)=>new Color(dx/w,dy/h,z,1));Upload(depth,(x,y)=>new Color(z,0,0,0));
            using var renderer=new OpaqueExposureRenderer();
            var input=new MotionBlurInput(source,guide,.02f);
            foreach(bool ortho in new[]{false,true})foreach(float oldZ in new[]{5f,8f})foreach(float phase in new[]{-.25f,-.0625f,.0625f,.25f})
            {
                Upload(previous,(x,y)=>new Color(oldZ,0,0,0));
                bool ok=renderer.TryRender(input,depth,previous,phase,.02f,ortho,32,out var frame);
                if(!ok)throw new InvalidOperationException(renderer.UnavailableReason);
                var actual=ReadSceneTarget(frame.color);var actualDepth=ReadSceneTarget(frame.eyeDepth);
                // Independently project one current and previous clip-space point.
                // Constant plane flow gives an analytic inverse for every pixel.
                double newW=ortho?1:z,oldW=ortho?1:oldZ;
                double clipX=(2*.5/w-1)*newW,oldClipX=(2*(.5-dx)/w-1)*oldW;
                double clipY=(2*.5/h-1)*newW,oldClipY=(2*(.5-dy)/h-1)*oldW;
                double phaseW=newW+(newW-oldW)*phase;
                double shiftX=((clipX+(clipX-oldClipX)*phase)/phaseW+1)*w/2-.5;
                double shiftY=((clipY+(clipY-oldClipY)*phase)/phaseW+1)*h/2-.5;
                double tx=-shiftX,ty=-shiftY;int ox=(int)Math.Floor(tx),oy=(int)Math.Floor(ty);
                int ax=(int)Math.Floor(tx+.5),ay=(int)Math.Floor(ty+.5);double fx=tx-ox,fy=ty-oy;
                float maximum=0,positive=0,alpha=0;
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                {
                    int p=y*w+x;var expected=current[p];double expectedDepth=z;
                    if(x+ax>=0&&x+ax<w&&y+ay>=0&&y+ay<h)
                    {
                        var sum=new double[4];expectedDepth=0;
                        for(int sy=0;sy<2;sy++)for(int sx=0;sx<2;sx++)
                        {
                            int xx=x+ox+sx,yy=y+oy+sy;bool valid=xx>=0&&xx<w&&yy>=0&&yy<h;
                            double weight=(sx==0?1-fx:fx)*(sy==0?1-fy:fy);var color=valid?current[yy*w+xx]:current[p];
                            for(int c=0;c<4;c++)sum[c]+=color[c]*weight;
                            expectedDepth+=(valid?z+(z-oldZ)*phase:z)*weight;
                        }
                        expected=new Color((float)sum[0],(float)sum[1],(float)sum[2],current[p].a);
                    }
                    for(int c=0;c<4;c++){maximum=Mathf.Max(maximum,Mathf.Abs(actual[p][c]-expected[c]));positive=Mathf.Max(positive,Mathf.Abs(actual[p][c]-current[p][c]));}
                    maximum=Mathf.Max(maximum,Mathf.Abs(actualDepth[p].r-(float)expectedDepth));alpha=Mathf.Max(alpha,Mathf.Abs(actual[p].a-current[p].a));
                }
                string label=ortho+"-"+oldZ+"-"+phase;
                Check(label+"-analytic-color-and-depth",maximum<.0002f,maximum);
                Check(label+"-nonzero-current-alpha-exact",positive>.001f&&alpha==0,positive);
            }
            renderer.TryRender(input,depth,previous,0,.02f,false,32,out var zero);Check("zero-phase-exact",ScenePixelsEqual(current,ReadSceneTarget(zero.color)));
            renderer.TryRender(new MotionBlurInput(source,guide,0),depth,previous,.25f,.02f,false,32,out var cold);Check("cold-clock-exact",ScenePixelsEqual(current,ReadSceneTarget(cold.color)));
            Check("owned-target-budget",renderer.TargetCount==2&&renderer.TargetBytes==(long)w*h*20);
            Check("owned-source-rejected",!renderer.TryRender(new MotionBlurInput(cold.color,guide,.02f),depth,previous,.25f,.02f,false,32,out _)&&renderer.TargetCount==0);
            Check("wrong-previous-depth-alias-rejected",!renderer.TryRender(input,depth,depth,.25f,.02f,false,32,out _));
            Check("jitter-not-silently-ignored",!renderer.TryRender(new MotionBlurInput(source,guide,.02f,new Vector2(.01f,0)),depth,previous,.25f,.02f,false,32,out _));
        }

        private void VerifyCoherentOpaqueFxExposure(Report report)
        {
            const int w=61,h=43;const float dx=3.125f,dy=-1.75f;
            void Check(string n,bool ok,float error=0)=>FrameworkCheck(report,"coherent-opaque-fx-"+n,ok,error);
            float Difference(Color[] a,Color[] b){float d=0;for(int i=0;i<a.Length;i++)for(int c=0;c<4;c++)d=Mathf.Max(d,Mathf.Abs(a[i][c]-b[i][c]));return d;}
            RenderTexture Target(RenderTextureFormat f){var t=Own(new RenderTexture(w,h,0,f,RenderTextureReadWrite.Linear){filterMode=FilterMode.Point});t.Create();return t;}
            var source=Target(RenderTextureFormat.ARGBFloat);var guide=Target(RenderTextureFormat.ARGBFloat);
            var depth=Target(RenderTextureFormat.RFloat);var previous=Target(RenderTextureFormat.RFloat);
            var sampleSource=Target(RenderTextureFormat.ARGBFloat);var sampleDepth=Target(RenderTextureFormat.RFloat);
            var upload=Own(new Texture2D(w,h,TextureFormat.RGBAFloat,false,true));var values=new Color[w*h];
            void Upload(RenderTexture t,Func<int,int,Color> fn)
            {for(int y=0;y<h;y++)for(int x=0;x<w;x++)values[y*w+x]=fn(x,y);upload.SetPixels(values);upload.Apply();Graphics.Blit(upload,t);}
            Upload(source,(x,y)=>new Color(.6f+.4f*Mathf.Sin(x*.8f),.6f+.4f*Mathf.Cos(y*.7f),.2f+x*.01f,.2f+y*.01f));var original=ReadSceneTarget(source);
            Upload(depth,(x,y)=>new Color(5,0,0,0));Upload(previous,(x,y)=>new Color(8,0,0,0));Upload(guide,(x,y)=>new Color(dx/w,dy/h,5,1));
            var go=Own(new GameObject("Shared opaque and transparent shutter camera"));var camera=go.AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;
            camera.aspect=w/(float)h;camera.orthographicSize=1.5f;camera.nearClipPlane=.1f;camera.farClipPlane=20;
            var mesh=Own(new Mesh());mesh.vertices=new[]{new Vector3(-.7f,-.7f,0),new Vector3(.7f,-.7f,0),new Vector3(.7f,.7f,0),new Vector3(-.7f,.7f,0)};
            mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.triangles=new[]{0,1,2,0,2,3};mesh.RecalculateBounds();
            var rear=new LowResolutionFxSurface {mesh=mesh,opacity=.65f,radialSoftness=.7f,linearRadiance=new Vector3(1.5f,.2f,.1f)};
            var front=new LowResolutionFxSurface {mesh=mesh,opacity=.7f,radialSoftness=.6f,linearRadiance=new Vector3(.1f,.6f,1.8f)};
            var settings=new HeavyFxSettings {enabled=true};settings.geometry.enabled=true;settings.geometry.surfaces=new[]{rear,front};settings.exposure.enabled=true;settings.exposure.reprojectOpaque=true;settings.exposure.samples=16;
            var refSettings=new HeavyFxSettings {enabled=true};refSettings.geometry.enabled=true;refSettings.geometry.surfaces=settings.geometry.surfaces;
            using var actualRenderer=new HeavyFxRenderer();using var referenceRenderer=new HeavyFxRenderer();
            void Pose(float t){rear.localToWorld=Matrix4x4.Translate(new Vector3(-.1f+.8f*t,0,5.1f));front.localToWorld=Matrix4x4.Translate(new Vector3(.1f-.6f*t,.1f,4.6f));}
            HeavyFxRenderer.Frame Actual(double time,float interval)
            {if(!actualRenderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,time,out var f,opaqueMotion:new MotionBlurInput(source,guide,interval),expectedPreviousDepth:previous))throw new InvalidOperationException(actualRenderer.UnavailableReason);return f;}
            // CPU authored image translation plus independent current geometry
            // draws at each sub-time. No OpaqueExposureRenderer in the oracle.
            Color[] Reference(float t)
            {
                double tx=-dx*t,ty=-dy*t;int ox=(int)Math.Floor(tx),oy=(int)Math.Floor(ty);double fx=tx-ox,fy=ty-oy;
                int ax=(int)Math.Floor(tx+.5),ay=(int)Math.Floor(ty+.5);
                bool Anchor(int x,int y)=>x+ax>=0&&x+ax<w&&y+ay>=0&&y+ay<h;
                Upload(sampleSource,(x,y)=>
                {
                    var originalPixel=original[y*w+x];if(!Anchor(x,y))return originalPixel;
                    var sum=new double[3];for(int yy=0;yy<2;yy++)for(int xx=0;xx<2;xx++)
                    {int px=x+ox+xx,py=y+oy+yy;var c=px>=0&&px<w&&py>=0&&py<h?original[py*w+px]:originalPixel;double weight=(xx==0?1-fx:fx)*(yy==0?1-fy:fy);for(int k=0;k<3;k++)sum[k]+=c[k]*weight;}
                    return new Color((float)sum[0],(float)sum[1],(float)sum[2],originalPixel.a);
                });
                Upload(sampleDepth,(x,y)=>
                {
                    if(!Anchor(x,y))return new Color(5,0,0,0);double sum=0;
                    for(int yy=0;yy<2;yy++)for(int xx=0;xx<2;xx++){int px=x+ox+xx,py=y+oy+yy;bool valid=px>=0&&px<w&&py>=0&&py<h;sum+=(valid?5-3*t:5)*(xx==0?1-fx:fx)*(yy==0?1-fy:fy);}
                    return new Color((float)sum,0,0,0);
                });
                Pose(t);if(!referenceRenderer.TryRender(sampleSource,new FogVolumeDepth(sampleDepth),camera,refSettings,0,out var f))throw new InvalidOperationException(referenceRenderer.UnavailableReason);
                return ReadSceneTarget(f.color);
            }
            foreach(var resolution in new[]{FxResolution.Full,FxResolution.Half,FxResolution.Quarter})
            {
                rear.resolution=front.resolution=resolution;settings.exposure.reprojectOpaque=true;actualRenderer.ResetMotionHistory();Pose(-1);Actual(0,0);Pose(0);var f=Actual(.02,.02f);var actual=ReadSceneTarget(f.color);
                Check(resolution+"-shared-phase-accounting",f.opaqueExposure&&f.geometryExposureSamples==16&&actualRenderer.TargetCount==12);
                var sum=new double[w*h*4];for(int i=0;i<16;i++){var sample=Reference(((i+.5f)/16*2-1)*.25f);for(int p=0;p<sample.Length;p++)for(int c=0;c<4;c++)sum[p*4+c]+=sample[p][c]/16;}
                var expected=new Color[w*h];for(int p=0;p<expected.Length;p++)for(int c=0;c<4;c++)expected[p][c]=(float)sum[p*4+c];
                float error=Difference(actual,expected);Check(resolution+"-independent-moving-background-and-occlusion",error<.0002f,error);
                var sharp=Reference(0);Check(resolution+"-positive-coverage-color-depth",Difference(actual,sharp)>.001f,Difference(actual,sharp));
                actualRenderer.ResetMotionHistory();Pose(-1);Actual(0,0);Pose(0);var replay=ReadSceneTarget(Actual(.02,.02f).color);Check(resolution+"-repeat-exact",Difference(actual,replay)==0,Difference(actual,replay));
                var paused=ReadSceneTarget(Actual(.02,0).color);Check(resolution+"-pause-exact",Difference(paused,sharp)==0,Difference(paused,sharp));
                settings.exposure.reprojectOpaque=false;actualRenderer.ResetMotionHistory();Pose(-1);Actual(0,0);Pose(0);var frozen=ReadSceneTarget(Actual(.02,.02f).color);
                Check(resolution+"-frozen-opaque-negative-control",Difference(frozen,expected)>.001f,Difference(frozen,expected));
                Check(resolution+"-disable-releases-warp-targets",actualRenderer.TargetCount==10);
            }
            settings.exposure.reprojectOpaque=true;actualRenderer.ResetMotionHistory();Pose(0);Actual(0,0);
            Check("clock-mismatch-rejected",!actualRenderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,.02,out _,opaqueMotion:new MotionBlurInput(source,guide,.04f),expectedPreviousDepth:previous)&&actualRenderer.TargetCount==0);
            Check("missing-opaque-input-rejected",!actualRenderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,1,out _)&&actualRenderer.TargetCount==0);
        }

        private IEnumerator VerifyHeavyFx(Report report)
        {
            yield return null;
            var oldRenderers=FindObjectsOfType<Renderer>();var forced=new bool[oldRenderers.Length];
            for(int i=0;i<oldRenderers.Length;i++){forced[i]=oldRenderers[i].forceRenderingOff;oldRenderers[i].forceRenderingOff=true;}
            var renderer=new HeavyFxRenderer();var saved=RenderTexture.active;
            try
            {
                void Check(string name,bool ok,float value=0)=>FrameworkCheck(report,"heavy-fx-"+name,ok,value);
                RenderTexture Target(int w,int h,RenderTextureFormat format,int bits=0)
                {var t=Own(new RenderTexture(w,h,bits,format,RenderTextureReadWrite.Linear){filterMode=FilterMode.Point,name="Joint FX fixture"});t.Create();return t;}
                void Upload(RenderTexture target,Func<int,int,Color> sample)
                {
                    var t=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point};var pixels=new Color[target.width*target.height];
                    for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)pixels[y*target.width+x]=sample(x,y);
                    try{t.SetPixels(pixels);t.Apply();Graphics.Blit(t,target);}finally{Destroy(t);}
                }
                var host=Own(new GameObject("Joint medium geometry optical camera"));var camera=host.AddComponent<Camera>();camera.enabled=false;
                camera.allowHDR=true;camera.allowMSAA=false;camera.nearClipPlane=.3f;camera.farClipPlane=15;camera.fieldOfView=55;
                camera.orthographic=true;camera.orthographicSize=2;camera.aspect=37f/25;camera.cullingMask=1<<26;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.13f,.21f,.31f,.375f);
                var mesh=Own(new Mesh{name="Independent covering joint planes"});mesh.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
                mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.triangles=new[]{0,2,1,0,3,2};mesh.RecalculateBounds();
                LowResolutionFxSurface Surface(float z,Vector3 color,float opacity,FxBlend blend)
                {return new LowResolutionFxSurface{mesh=mesh,localToWorld=Matrix4x4.TRS(new Vector3(0,0,z),Quaternion.identity,Vector3.one*50),linearRadiance=color,opacity=opacity,blend=blend};}
                var rear=Surface(8,new Vector3(1.2f,.3f,.2f),.3f,FxBlend.Alpha);
                var additive=Surface(6,new Vector3(.2f,1.3f,.3f),.2f,FxBlend.Additive);
                var front=Surface(4,new Vector3(.1f,.4f,1.6f),.4f,FxBlend.Alpha);
                var distortion=Surface(5,Vector3.one,.35f,FxBlend.Distortion);distortion.distortionOffset=new Vector2(.061f,-.043f);
                var light=new VolumetricSpotLight{position=new Vector3(-1,1,2),rotation=Quaternion.LookRotation(new Vector3(.2f,-.1f,1)),range=9,innerAngle=28,outerAngle=64,linearRadiance=new Vector3(6,3,2)};
                var light2=new VolumetricSpotLight{position=new Vector3(1,-.8f,2.5f),rotation=Quaternion.LookRotation(new Vector3(-.15f,.2f,1)),range=8,innerAngle=25,outerAngle=60,linearRadiance=new Vector3(1,3,5)};
                var emitter=new LensFlareEmitter{position=new Vector3(.5f,.2f,7),linearRadiance=new Vector3(.2f,.3f,.1f),occlusionRadius=.15f,
                    elements=new[]{new LensFlareElement{halfSize=new Vector2(.31f,.17f),softness=.9f},new LensFlareElement{shape=LensFlareShape.Ring,axisPosition=2,halfSize=Vector2.one*.2f,softness=.9f}}};
                var settings=new HeavyFxSettings{
                    geometry=new LowResolutionFxSettings{enabled=true,surfaces=new[]{rear,additive,front}},
                    medium=new VolumetricLightingSettings{enabled=true,mediumCenter=new Vector3(0,0,6),mediumHalfSize=new Vector3(4,3,4),extinction=.13f,samplesPerLight=256,lights=new[]{light,light2}},
                    optics=new LensFlareSettings{enabled=true,emitters=new[]{emitter}}};
                var source=Target(37,25,RenderTextureFormat.ARGBFloat);var depth=Target(37,25,RenderTextureFormat.RFloat);
                Upload(source,(x,y)=>new Color(.1f+x*.017f,.2f+y*.013f,.3f,.15f+x*.013f));Upload(depth,(x,y)=>new Color(12,0,0,0));
                Check("default-off-zero-resources",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,0,out _)&&renderer.TargetCount==0);
                settings.enabled=true;
                void Resolution(FxResolution value)
                {settings.medium.resolution=(VolumetricResolution)value;settings.optics.resolution=(LensFlareResolution)value;foreach(var s in settings.geometry.surfaces)s.resolution=value;}
                HeavyFxRenderer.Frame Render(string name,RenderTexture protection=null)
                {
                    var prior=RenderTexture.active;
                    if(!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,2.3,out var frame,protection))throw new InvalidOperationException(renderer.UnavailableReason);
                    var original=ReadSceneTarget(source);var raw=ReadSceneTarget(depth);var mask=protection!=null?ReadSceneTarget(protection):null;
                    var expected=HeavyFxFlatReference(camera,settings,original,raw,source.width,source.height,2.3,mask);
                    var actual=ReadSceneTarget(frame.color);float maximum=0,alpha=0;double sum=0;bool finite=true;int worst=0;
                    for(int i=0;i<actual.Length;i++)
                    {
                        alpha=Mathf.Max(alpha,Mathf.Abs(actual[i].a-original[i].a));
                        for(int c=0;c<3;c++){float difference=Mathf.Abs(actual[i][c]-expected[i][c]);if(difference>maximum){maximum=difference;worst=i;}sum+=difference;finite&=!float.IsNaN(actual[i][c])&&!float.IsInfinity(actual[i][c]);}
                    }
                    bool full=settings.medium.resolution==VolumetricResolution.Full&&settings.optics.resolution==LensFlareResolution.Full;
                    foreach(var s in settings.geometry.surfaces)full&=s.resolution==FxResolution.Full;
                    if(maximum>(full?.004f:.02f))
                    {
                        string guides="";
                        foreach(var r in new[]{FxResolution.Full,FxResolution.Half,FxResolution.Quarter})if(frame.TryGetLastBatch(r,out var e,out var d))
                        {
                            int px=Mathf.Clamp((worst%source.width)*e.width/source.width,0,e.width-1),py=Mathf.Clamp((worst/source.width)*e.height/source.height,0,e.height-1);
                            guides+=" "+r+" range="+ReadSceneTarget(d)[py*d.width+px].ToString("R")+" effect="+ReadSceneTarget(e)[py*e.width+px].ToString("R");
                        }
                        Debug.Log("[HeavyFxDiagnostic] "+name+" pixel="+(worst%source.width)+","+(worst/source.width)+" actual="+actual[worst].ToString("R")+" expected="+expected[worst].ToString("R")+" source="+original[worst].ToString("R")+" depth="+raw[worst].ToString("R")+guides);
                    }
                    Check(name+"-independent-whole-image-max",maximum<(full?.004f:.02f),maximum);
                    Check(name+"-independent-whole-image-mean",sum/(actual.Length*3)<(full?.0003:.002),(float)(sum/(actual.Length*3)));
                    Check(name+"-finite-alpha-current",finite&&alpha==0&&frame.IsCurrent&&prior==RenderTexture.active,alpha);
                    return frame;
                }
                foreach(var scale in new[]{FxResolution.Full,FxResolution.Half,FxResolution.Quarter})
                {
                    Resolution(scale);var frame=Render("shared-three-types-"+scale);
                    Check("shared-three-types-"+scale+"-one-upscale-batch",renderer.BatchCount==1&&frame.TryGetBatchInfo(0,out var info)&&info.medium&&info.optics&&info.surfaceCount==3);
                    Check("shared-three-types-"+scale+"-one-effect-and-guide",frame.TryGetLastBatch(scale,out var effect,out var range)&&effect.width==(source.width+(int)scale-1)/(int)scale&&range.width==effect.width&&renderer.TargetCount==6);
                    Check("shared-three-types-"+scale+"-current-mask-and-accounted-bytes",frame.repairMask.width==source.width&&frame.repairMask.format==RenderTextureFormat.R8&&renderer.TargetBytes==(long)source.width*source.height*33+(long)effect.width*effect.height*24+4);
                    SaveSsrPreview("heavy-fx-shared-"+scale,ReadSceneTarget(frame.color),source.width,source.height,false);
                }
                Upload(depth,(x,y)=>new Color(x<8?2:x<18?6.5f:12,0,0,0));Render("opaque-depth-cuts-through-medium-and-surfaces");
                Resolution(FxResolution.Full);var priorOrder=ReadSceneTarget(Render("ordered-medium-surfaces").color);
                settings.geometry.surfaces=new[]{additive,rear,front};var swapped=ReadSceneTarget(Render("swapped-surfaces").color);
                Check("geometry-order-is-noncommutative",!ScenePixelsEqual(priorOrder,swapped));settings.geometry.surfaces=new[]{rear,additive,front};
                front.localToWorld=Matrix4x4.TRS(new Vector3(0,0,2.5f),Quaternion.identity,Vector3.one*50);Render("surface-in-front-of-most-scattering");
                front.localToWorld=Matrix4x4.TRS(new Vector3(0,0,4),Quaternion.identity,Vector3.one*50);
                settings.geometry.surfaces=new[]{rear,additive,distortion,front};Resolution(FxResolution.Half);distortion.resolution=FxResolution.Quarter;front.resolution=FxResolution.Full;
                var barrier=Render("mixed-resolution-distortion-barriers");Check("mixed-barriers-four-batches",renderer.BatchCount==4);
                settings.geometry.surfaces=new[]{rear,additive,front};Resolution(FxResolution.Half);
                foreach(int projection in new[]{0,1,2})
                {camera.orthographic=projection==0;camera.ResetProjectionMatrix();if(projection==2){var p=camera.projectionMatrix;p.m02=.17f;p.m12=-.13f;camera.projectionMatrix=p;}Render("projection-"+projection);}
                camera.orthographic=true;camera.ResetProjectionMatrix();
                var protection=Target(source.width,source.height,RenderTextureFormat.RFloat);Upload(protection,(x,y)=>new Color(x<5?1:x==6?float.NaN:0,0,0,0));
                Upload(depth,(x,y)=>new Color(x%11==0?float.NaN:x%11==1?-1:x%11==2?.1f:12,0,0,0));Render("invalid-depth-and-protection",protection);
                Upload(depth,(x,y)=>Color.clear);settings.medium.affectSky=false;Render("sky-excluded-medium-with-foreground-surfaces");settings.medium.affectSky=true;
                settings.medium.attenuateBackground=false;Render("explicit-additive-only-medium");settings.medium.attenuateBackground=true;
                settings.medium.lights=Array.Empty<VolumetricSpotLight>();Render("extinction-without-light");settings.medium.lights=new[]{light,light2};
                var lease=Render("before-guide-loss");lease.TryGetLastBatch(FxResolution.Half,out _,out var guide);guide.Release();
                Check("guide-loss-invalidates-entire-frame",!lease.IsCurrent&&!renderer.TryGetFrame(out _));lease=Render("restore-guide");lease.opticalVisibility.Release();
                Check("optical-loss-invalidates-entire-frame",!lease.IsCurrent);lease=Render("restore-visibility");lease.TryGetLastBatch(FxResolution.Half,out var effectLost,out _);effectLost.Release();
                Check("effect-loss-invalidates-entire-frame",!lease.IsCurrent);lease=Render("restore-effect");lease.color.Release();
                Check("hdr-loss-invalidates-entire-frame",!lease.IsCurrent);lease=Render("restore-hdr");
                lease.repairMask.Release();Check("mask-loss-invalidates-entire-frame",!lease.IsCurrent);lease=Render("restore-mask");
                Check("own-source-alias-fails-and-releases",!renderer.TryRender(lease.color,new FogVolumeDepth(depth),camera,settings,0,out _)&&!lease.IsCurrent&&renderer.TargetCount==0);
                settings.geometry.fog.enabled=true;Check("duplicate-medium-rejected",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,0,out _)&&renderer.UnavailableReason!=null);settings.geometry.fog.enabled=false;
                settings.maximumTargetMiB=1;var big=Target(513,257,RenderTextureFormat.ARGBFloat);var bigDepth=Target(513,257,RenderTextureFormat.RFloat);
                Check("whole-pipeline-budget-before-allocation",!renderer.TryRender(big,new FogVolumeDepth(bigDepth),camera,settings,0,out _)&&renderer.TargetCount==0);settings.maximumTargetMiB=512;
                foreach(int width in new[]{1,3,61})
                {int height=width==1?7:width==3?1:43;source=Target(width,height,RenderTextureFormat.ARGBFloat);depth=Target(width,height,RenderTextureFormat.RFloat);camera.aspect=width/(float)height;
                    Upload(source,(x,y)=>new Color(.2f,.4f,.6f,.375f));Upload(depth,(x,y)=>new Color(12,0,0,0));Resolution(FxResolution.Quarter);Render("odd-size-"+width+"x"+height);
                    if(width==3)foreach(float x in new[]{-.0001f,.0001f}){camera.transform.position=new Vector3(x,0,0);Render("parallel-slab-resolvable-offset-"+x);camera.transform.position=Vector3.zero;}}
                foreach(var format in new[]{RenderTextureFormat.ARGBHalf,RenderTextureFormat.RGB111110Float})
                {source=Target(31,23,format);depth=Target(31,23,RenderTextureFormat.RHalf);camera.aspect=31f/23;Upload(source,(x,y)=>new Color(2,.5f,.25f,.375f));Upload(depth,(x,y)=>new Color(12,0,0,0));Render("input-format-"+format);}
                settings.enabled=false;Check("disable-releases-all",!renderer.TryRender(source,new FogVolumeDepth(depth),camera,settings,0,out _)&&renderer.TargetCount==0);
                settings.enabled=true;lease=Render("reenabled");renderer.Dispose();Check("dispose-invalidates-borrowed-frame",!lease.IsCurrent&&renderer.TargetCount==0);
                var finiteCases=VerifyHeavyFxFinite(report,camera,mesh);
                while(finiteCases.MoveNext())yield return finiteCases.Current;
                (finiteCases as IDisposable)?.Dispose();
                const int actualWidth=97,actualHeight=65;camera.aspect=actualWidth/(float)actualHeight;camera.depthTextureMode=DepthTextureMode.Depth;camera.renderingPath=RenderingPath.Forward;
                var actualTarget=Target(actualWidth,actualHeight,RenderTextureFormat.ARGBFloat,24);var actualDepth=Target(actualWidth,actualHeight,RenderTextureFormat.RFloat);camera.targetTexture=actualTarget;
                var wall=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));wall.layer=26;wall.transform.localScale=new Vector3(.6f,2.5f,1);
                wall.GetComponent<Renderer>().sharedMaterial=Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback")));
                var blocker=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));blocker.layer=25;blocker.transform.localScale=new Vector3(1.2f,1.1f,1);
                blocker.GetComponent<Renderer>().sharedMaterial=Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback")));
                light.shadow.enabled=light2.shadow.enabled=true;light.shadow.depthBias=light2.shadow.depthBias=0;light.shadow.normalBias=light2.shadow.normalBias=0;
                settings.medium.shadows.tileResolution=128;settings.medium.shadows.casters=new[]{new SceneShadowCaster{renderer=blocker.GetComponent<Renderer>(),cull=UnityEngine.Rendering.CullMode.Off}};
                settings.geometry.surfaces=new[]{rear,additive,distortion,front};Resolution(FxResolution.Half);distortion.resolution=FxResolution.Quarter;
                var post=host.AddComponent<OriginalStyleRenderPipeline>();post.heavyFx=settings;post.heavyFxTimeSeconds=2.3;Color[] actualInput=null;
                Func<Camera,RenderTexture,FogVolumeDepth> provider=(view,input)=>
                {actualInput=ReadSceneTarget(input);Graphics.Blit(Shader.GetGlobalTexture("_CameraDepthTexture"),actualDepth);return new FogVolumeDepth(actualDepth,FogDepthEncoding.Device);};
                post.heavyFxDepthProvider=provider;
                bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_HEAVY_FX")=="1",started=false,ended=false;
                try
                {
                    if(capture)started=RenderDocCaptureBridge.BeginOffscreenCapture();
                    for(int step=0;step<2;step++)
                    {
                        camera.transform.position=new Vector3(step*.13f,0,step*.1f);camera.orthographic=step==1;camera.ResetProjectionMatrix();
                        if(step==0){var p=camera.projectionMatrix;p.m02=.13f;p.m12=-.19f;camera.projectionMatrix=p;}
                        wall.transform.position=new Vector3(-.3f+step*.6f,0,3);
                        blocker.transform.SetPositionAndRotation(light.position+light.rotation*new Vector3(.1f+step*.5f,-.1f,3),light.rotation);
                        light.shadow.filter=step==0?SceneShadowFilter.Hard:SceneShadowFilter.Pcf3x3;camera.Render();
                        if(!post.TryGetHeavyFxFrame(out var actualFrame))throw new InvalidOperationException(post.HeavyFxUnavailableReason);
                        var raw=ReadSceneTarget(actualDepth);var eye=new Color[raw.Length];int opaque=0;
                        for(int i=0;i<raw.Length;i++)
                        {
                            float z=SystemInfo.usesReversedZBuffer?raw[i].r:1-raw[i].r;
                            if(z==0)eye[i]=Color.clear;
                            else{opaque++;eye[i]=new Color(camera.orthographic?camera.farClipPlane-z*(camera.farClipPlane-camera.nearClipPlane):camera.nearClipPlane*camera.farClipPlane/(camera.nearClipPlane+z*(camera.farClipPlane-camera.nearClipPlane)),0,0,0);}
                        }
                        var shadow=ReadSceneTarget(actualFrame.shadowAtlas);
                        Check("two-colored-current-shadow-tiles-"+step,actualFrame.shadowAtlas.width==256&&post.TryGetHeavyFxFrame(out _)&&actualFrame.IsCurrent);
                        var expected=HeavyFxFlatReference(camera,settings,actualInput,eye,actualWidth,actualHeight,2.3,null,shadow,actualFrame.shadowAtlas.width);
                        var actual=ReadSceneTarget(actualFrame.color);float max=0,alpha=0;double sum=0;
                        for(int i=0;i<actual.Length;i++){alpha=Mathf.Max(alpha,Mathf.Abs(actual[i].a-actualInput[i].a));for(int c=0;c<3;c++){float d=Mathf.Abs(actual[i][c]-expected[i][c]);max=Mathf.Max(max,d);sum+=d;}}
                        Check("actual-camera-current-shadow-mixed-max-"+step,max<.02f,max);
                        Check("actual-camera-current-shadow-mixed-mean-"+step,sum/(actual.Length*3)<.002,(float)(sum/(actual.Length*3)));
                        Check("actual-camera-opaque-depth-alpha-and-shared-plan-"+step,alpha==0&&opaque>20&&opaque<actual.Length&&actualFrame.IsCurrent&&actualFrame.TryGetBatchInfo(0,out var info)&&info.medium&&info.surfaceCount==2&&actualFrame.TryGetBatchInfo(2,out info)&&info.optics&&info.surfaceCount==1,alpha);
                        SaveSsrPreview("heavy-fx-actual-camera-"+step,ReadSceneTarget(actualTarget),actualWidth,actualHeight,false);
                    }
                }
                finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                if(capture)Check("requested-native-capture",started&&ended);
                var shadowed=ReadSceneTarget(post.TryGetHeavyFxFrame(out var shadowedFrame)?shadowedFrame.color:null);light.shadow.enabled=false;camera.Render();
                Check("dynamic-source-shadow-changes-joint-output",post.TryGetHeavyFxFrame(out var unshadowed)&&!ScenePixelsEqual(shadowed,ReadSceneTarget(unshadowed.color)));light.shadow.enabled=true;
                camera.Render();shadowed=ReadSceneTarget(post.TryGetHeavyFxFrame(out shadowedFrame)?shadowedFrame.color:null);light2.shadow.enabled=false;camera.Render();
                Check("second-colored-source-shadow-changes-joint-output",post.TryGetHeavyFxFrame(out unshadowed)&&!ScenePixelsEqual(shadowed,ReadSceneTarget(unshadowed.color)));light2.shadow.enabled=true;
                post.heavyFxDepthProvider=null;camera.Render();Check("missing-provider-no-stale-frame",!post.TryGetHeavyFxFrame(out _)&&post.HeavyFxUnavailableReason!=null);
                post.heavyFxDepthProvider=(view,input)=>throw new InvalidOperationException("Deliberate joint provider failure");camera.Render();Check("throwing-provider-no-stale-frame",!post.TryGetHeavyFxFrame(out _)&&post.HeavyFxUnavailableReason!=null);
                post.heavyFxDepthProvider=provider;post.volumetricLighting.enabled=true;camera.Render();Check("legacy-volume-bridge-exclusive",!post.TryGetHeavyFxFrame(out _)&&post.HeavyFxUnavailableReason!=null);post.volumetricLighting.enabled=false;
                post.fogVolumes.enabled=true;camera.Render();Check("fog-bridge-exclusive",!post.TryGetHeavyFxFrame(out _)&&post.HeavyFxUnavailableReason!=null);post.fogVolumes.enabled=false;
                settings.enabled=false;camera.Render();var disabled=ReadSceneTarget(actualTarget);settings.enabled=true;camera.Render();settings.enabled=false;camera.Render();
                Check("default-post-byte-exact-restored",ScenePixelsEqual(disabled,ReadSceneTarget(actualTarget))&&post.HeavyFxUnavailableReason==null);
                post.enabled=false;camera.targetTexture=null;host.SetActive(false);
            }
            finally
            {
                renderer.Dispose();RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;
                for(int i=0;i<oldRenderers.Length;i++)if(oldRenderers[i]!=null)oldRenderers[i].forceRenderingOff=forced[i];
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
        }

        private IEnumerator VerifyHeavyFxFinite(Report report,Camera camera,Mesh quad)
        {
            var fx=new HeavyFxRenderer();var active=RenderTexture.active;var oldTarget=camera.targetTexture;float oldAspect=camera.aspect;
            try
            {
                void Check(string name,bool ok,float value=0)=>FrameworkCheck(report,"heavy-fx-finite-"+name,ok,value);
                RenderTexture Target(RenderTextureFormat format,int bits=0)
                {var target=Own(new RenderTexture(113,79,bits,format,RenderTextureReadWrite.Linear));target.Create();return target;}
                var source=Target(RenderTextureFormat.ARGBFloat);var depth=Target(RenderTextureFormat.RFloat);var cameraTarget=Target(RenderTextureFormat.ARGBFloat,24);
                var input=new Color[113*79];var eyes=new Color[input.Length];
                for(int y=0;y<79;y++)for(int x=0;x<113;x++)
                {int i=y*113+x;input[i]=new Color(.12f+x*.003f,.17f+y*.002f,.22f,.375f);eyes[i]=new Color(x>=52&&x<=55?3:12,0,0,0);}
                void Upload(RenderTexture target,Color[] pixels)
                {var texture=new Texture2D(113,79,TextureFormat.RGBAFloat,false,true);try{texture.SetPixels(pixels);texture.Apply();Graphics.Blit(texture,target);}finally{Destroy(texture);}}
                Upload(source,input);Upload(depth,eyes);
                // Compare actual stored input texels before/after, including the
                // GPU's missing-channel expansion of RFloat (alpha is one).
                var uploadedSource=ReadSceneTarget(source);var uploadedDepth=ReadSceneTarget(depth);
                camera.aspect=113f/79;camera.targetTexture=cameraTarget;
                var texture=Own(new Texture2D(8,6,TextureFormat.RGBAFloat,false,true));var texels=new Color[48];
                for(int y=0;y<6;y++)for(int x=0;x<8;x++)texels[y*8+x]=new Color(.3f+x*.07f,.6f-y*.05f,.8f,(x+y+2)/14f);
                texture.SetPixels(texels);texture.Apply();
                var mesh=Own(Instantiate(quad));var vertex=new Color(.7f,.5f,.9f,.6f);mesh.colors=new[]{vertex,vertex,vertex,vertex};
                var surface=new LowResolutionFxSurface{mesh=mesh,localToWorld=Matrix4x4.TRS(new Vector3(.2f,.1f,7),Quaternion.Euler(0,0,17),new Vector3(2.1f,1.4f,1)),
                    texture=texture,textureST=new Vector4(.8f,.7f,.1f,.15f),vertexColor=true,linearRadiance=new Vector3(2,.7f,.2f),opacity=.65f,radialSoftness=.85f,softIntersectionDistance=10};
                var medium=new VolumetricLightingSettings{enabled=true,mediumCenter=new Vector3(0,0,6),mediumHalfSize=new Vector3(4,3,4),extinction=.13f};
                var settings=new HeavyFxSettings{enabled=true,medium=medium,geometry=new LowResolutionFxSettings{enabled=true,surfaces=new[]{surface}}};
                Color[] Render(string name)
                {
                    if(!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,1.25,out var frame))throw new InvalidOperationException(fx.UnavailableReason);
                    var expected=(Color[])input.Clone();
                    // Orthographic rays lie inside the box's lateral bounds. Its z
                    // interval is [2,10], so independent Beer attenuation is exact.
                    for(int i=0;i<expected.Length;i++)for(int c=0;c<3;c++)expected[i][c]*=(float)Math.Exp(-medium.extinction*(Math.Min(eyes[i].r,10)-2));
                    var referenceMesh=surface.mesh;var originalRadiance=surface.linearRadiance;
                    try
                    {
                        surface.mesh=mesh;surface.linearRadiance*=Mathf.Exp(-medium.extinction*5);
                        expected=LowFxTexturedPlaneReference(camera,surface,expected,eyes,settings.geometry.depthBias);
                    }
                    finally{surface.mesh=referenceMesh;surface.linearRadiance=originalRadiance;}
                    var result=ReadSceneTarget(frame.color);float maximum=0,alpha=0;double sum=0;
                    for(int i=0;i<result.Length;i++){alpha=Mathf.Max(alpha,Mathf.Abs(result[i].a-input[i].a));for(int c=0;c<3;c++){float d=Mathf.Abs(result[i][c]-expected[i][c]);maximum=Mathf.Max(maximum,d);sum+=d;}}
                    bool full=surface.resolution==FxResolution.Full;
                    Check(name+"-independent-all-pixels",maximum<(full?.004f:.02f)&&alpha==0,maximum);
                    Check(name+"-independent-mean",sum/(result.Length*3)<(full?.0003:.002),(float)(sum/(result.Length*3)));
                    Check(name+"-shared-medium-surface-batch",frame.TryGetBatchInfo(0,out var batch)&&batch.medium&&batch.surfaceCount==1&&fx.BatchCount==1);
                    return result;
                }
                foreach(var blend in new[]{FxBlend.Alpha,FxBlend.Additive})foreach(var scale in new[]{FxResolution.Full,FxResolution.Half,FxResolution.Quarter})
                {surface.blend=blend;surface.resolution=scale;medium.resolution=(VolumetricResolution)scale;Render("textured-vertex-soft-"+blend+"-"+scale);}
                surface.blend=FxBlend.Alpha;surface.resolution=FxResolution.Half;medium.resolution=VolumetricResolution.Half;
                var baseline=Render("mesh-baseline");
                var obj=Own(new GameObject("Joint finite MeshRenderer"));obj.layer=25;obj.transform.SetPositionAndRotation(new Vector3(.2f,.1f,7),Quaternion.Euler(0,0,17));obj.transform.localScale=new Vector3(2.1f,1.4f,1);
                obj.AddComponent<MeshFilter>().sharedMesh=mesh;var mr=obj.AddComponent<MeshRenderer>();var material=Own(new Material(Resources.Load<Shader>("AdvEnvironmentFallback")));mr.sharedMaterial=material;
                surface.renderer=mr;surface.mesh=null;
                Check("mesh-renderer-byte-equivalence-and-material",ScenePixelsEqual(baseline,Render("mesh-renderer"))&&mr.sharedMaterial==material&&mr.enabled);obj.SetActive(false);
                var skin=Own(new GameObject("Joint finite current skin"));skin.layer=25;skin.transform.position=new Vector3(0,0,7);
                var bone=Own(new GameObject("Joint independent bone"));bone.transform.SetParent(skin.transform,false);
                var skinMesh=Own(Instantiate(mesh));skinMesh.bindposes=new[]{Matrix4x4.identity};skinMesh.boneWeights=new[]{
                    new BoneWeight{boneIndex0=0,weight0=1},new BoneWeight{boneIndex0=0,weight0=1},new BoneWeight{boneIndex0=0,weight0=1},new BoneWeight{boneIndex0=0,weight0=1}};
                var skinned=skin.AddComponent<SkinnedMeshRenderer>();skinned.sharedMesh=skinMesh;skinned.bones=new[]{bone.transform};skinned.rootBone=bone.transform;
                skinned.updateWhenOffscreen=true;skinned.localBounds=new Bounds(Vector3.zero,Vector3.one*20);skinned.sharedMaterial=material;surface.renderer=skinned;
                Color[] initial=null;
                for(int step=0;step<3;step++)
                {
                    float time=step==1?1.7f:.25f;bone.transform.localPosition=new Vector3(Mathf.Sin(time)*.7f,Mathf.Cos(time)*.2f,0);bone.transform.localRotation=Quaternion.Euler(0,0,time*17);
                    surface.localToWorld=skin.transform.localToWorldMatrix*Matrix4x4.TRS(bone.transform.localPosition,bone.transform.localRotation,Vector3.one);
                    yield return null;camera.Render();var result=Render("current-bone-"+step);
                    if(step==0)initial=result;else Check(step==1?"bone-motion-changes-image":"bone-seek-restores-image",step==1?!ScenePixelsEqual(initial,result):ScenePixelsEqual(initial,result));
                }
                Check("skin-caller-material-preserved",skinned.sharedMaterial==material);skin.SetActive(false);surface.renderer=null;surface.mesh=mesh;
                surface.localToWorld=Matrix4x4.TRS(new Vector3(.2f,.1f,7),Quaternion.Euler(0,0,17),new Vector3(2.1f,1.4f,1));
                fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,0,out var lease);
                Check("own-mask-protection-alias-releases",!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,0,out _,lease.repairMask)&&!lease.IsCurrent&&fx.TargetCount==0);
                Check("explicit-nonfinite-time-rejected",!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,double.NaN,out _)&&fx.TargetCount==0);
                settings.effectEdgeThreshold=float.NaN;Check("invalid-reconstruction-rejected",!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,0,out _)&&fx.TargetCount==0);settings.effectEdgeThreshold=.001f;
                Check("source-protection-alias-rejected",!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,0,out _,source)&&fx.TargetCount==0);
                Check("invalid-depth-encoding-rejected",!fx.TryRender(source,new FogVolumeDepth(depth,(FogDepthEncoding)99),camera,settings,0,out _)&&fx.TargetCount==0);
                settings.medium.enabled=settings.geometry.enabled=false;
                Check("empty-all-children-zero-resources",!fx.TryRender(source,new FogVolumeDepth(depth),camera,settings,0,out _)&&fx.UnavailableReason==null&&fx.TargetCount==0);
                Check("caller-inputs-unchanged",ScenePixelsEqual(uploadedSource,ReadSceneTarget(source))&&ScenePixelsEqual(uploadedDepth,ReadSceneTarget(depth)));
            }
            finally{fx.Dispose();camera.targetTexture=oldTarget;camera.aspect=oldAspect;RenderTexture.active=active!=null&&active.IsCreated()?active:null;}
        }

        // Independent reference for covering view-parallel constant planes. Integrate the
        // world-space medium separately to opaque depth and each surface, not low targets.
        private static Color[] HeavyFxFlatReference(Camera camera,HeavyFxSettings settings,Color[] input,Color[] depth,
            int width,int height,double time,Color[] protection,Color[] shadow=null,int shadowSize=0)
        {
            var result=(Color[])input.Clone();
            // Crop each independently authored Spot tile, then use the established
            // single-tile dense oracle per light. Sum scatter; retain extinction once.
            VolumetricLightingSettings[] splitMedia=null;Color[][] splitShadows=null;int tileSize=shadowSize;
            int shadowCount=0;
            if(shadow!=null&&settings.medium!=null)foreach(var light in settings.medium.lights)
                if(light!=null&&light.enabled&&light.linearRadiance!=Vector3.zero&&light.shadow!=null&&light.shadow.enabled&&light.shadow.strength>0)shadowCount++;
            if(shadowCount>1)
            {
                var m=settings.medium;int grid=Mathf.CeilToInt(Mathf.Sqrt(shadowCount));tileSize=shadowSize/grid;
                splitMedia=new VolumetricLightingSettings[m.lights.Length];splitShadows=new Color[m.lights.Length][];int tile=0;
                for(int i=0;i<m.lights.Length;i++)
                {
                    var light=m.lights[i];splitMedia[i]=new VolumetricLightingSettings{enabled=true,mediumCenter=m.mediumCenter,mediumHalfSize=m.mediumHalfSize,
                        extinction=m.extinction,anisotropy=m.anisotropy,scatteringAlbedo=m.scatteringAlbedo,attenuateBackground=m.attenuateBackground,affectSky=m.affectSky,lights=new[]{light}};
                    if(light==null||!light.enabled||light.linearRadiance==Vector3.zero||light.shadow==null||!light.shadow.enabled||light.shadow.strength==0)continue;
                    var pixels=new Color[tileSize*tileSize];int tx=tile%grid,ty=tile/grid;
                    for(int y=0;y<tileSize;y++)for(int x=0;x<tileSize;x++)pixels[y*tileSize+x]=shadow[(ty*tileSize+y)*shadowSize+tx*tileSize+x];
                    splitShadows[i]=pixels;tile++;
                }
            }
            bool Protected(int i)=>protection!=null&&(float.IsNaN(protection[i].r)||float.IsInfinity(protection[i].r)||protection[i].r>0);
            bool ValidDepth(int i)=>!float.IsNaN(depth[i].r)&&!float.IsInfinity(depth[i].r)&&(depth[i].r==0||depth[i].r>=camera.nearClipPlane);
            Color Medium(int x,int y,float eye,bool sky)
            {
                if(settings.medium==null||!settings.medium.enabled)return new Color(0,0,0,1);
                var start=camera.ViewportToWorldPoint(new Vector3((x+.5f)/width,(y+.5f)/height,camera.nearClipPlane));
                var end=camera.ViewportToWorldPoint(new Vector3((x+.5f)/width,(y+.5f)/height,Mathf.Min(eye,camera.farClipPlane)));
                if(splitMedia==null)return VolumeDenseReference(settings.medium,start,end,sky,shadow,shadowSize,2048);
                var sum=Color.clear;
                for(int i=0;i<splitMedia.Length;i++)
                {var value=VolumeDenseReference(splitMedia[i],start,end,sky,splitShadows[i],tileSize,2048);for(int c=0;c<3;c++)sum[c]+=value[c];if(i==0)sum.a=value.a;}
                return sum;
            }
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int i=y*width+x;if(Protected(i)||!ValidDepth(i))continue;
                var medium=Medium(x,y,depth[i].r==0?camera.farClipPlane:depth[i].r,depth[i].r==0);
                for(int c=0;c<3;c++)result[i][c]=input[i][c]*medium.a+medium[c];
            }
            if(settings.geometry!=null&&settings.geometry.enabled)foreach(var s in settings.geometry.surfaces)
            {
                if(s==null||!s.enabled||s.opacity==0)continue;
                if(s.blend==FxBlend.Distortion)
                {
                    result=LowFxFlatReference(camera,new LowResolutionFxSettings{surfaces=new[]{s},depthBias=settings.geometry.depthBias},result,depth,width,height,false,protection);
                    continue;
                }
                float eye=-camera.worldToCameraMatrix.MultiplyPoint(s.localToWorld.MultiplyPoint(Vector3.zero)).z;
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                {
                    int i=y*width+x;if(Protected(i)||!ValidDepth(i)||eye<camera.nearClipPlane||eye>camera.farClipPlane||
                        (depth[i].r>0&&depth[i].r<eye-settings.geometry.depthBias))continue;
                    var medium=s.fog?Medium(x,y,eye,false):new Color(0,0,0,1);
                    for(int c=0;c<3;c++)
                    {
                        double color=s.linearRadiance[c]*medium.a+(s.blend==FxBlend.Alpha?medium[c]:0);
                        result[i][c]=(float)(color*s.opacity+result[i][c]*(s.blend==FxBlend.Alpha?1-s.opacity:1));
                    }
                }
            }
            if(settings.optics!=null&&settings.optics.enabled)
            {
                var prior=settings.optics.resolution;settings.optics.resolution=LensFlareResolution.Full;
                try
                {
                    FlareReference(camera,settings.optics,time,result,depth,width,height,false,null,out _,out _,out var optical);
                    for(int i=0;i<result.Length;i++)if(!Protected(i))result[i]=optical[i];
                }
                finally{settings.optics.resolution=prior;}
            }
            return result;
        }
    }
}
