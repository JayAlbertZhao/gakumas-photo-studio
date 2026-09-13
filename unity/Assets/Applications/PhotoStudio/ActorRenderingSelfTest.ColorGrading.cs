using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyColorGrading(Report report)
        {
            yield return null;
            var previous=FindObjectsOfType<Renderer>();var forced=new bool[previous.Length];
            for(int i=0;i<previous.Length;i++){forced[i]=previous[i].forceRenderingOff;previous[i].forceRenderingOff=true;}
            var savedActive=RenderTexture.active;var savedAnisotropy=QualitySettings.anisotropicFiltering;
            var renderer=new ColorGradingRenderer();var other=new ColorGradingRenderer();var luts=new List<ColorGradingLut>();
            try
            {
                void Check(string name,bool accepted,float error=0)=>FrameworkCheck(report,"color-grade-"+name,accepted,error);
                ColorGradingLut Bake(ColorGradingProfile p){var lut=ColorGradingLut.Bake(p);luts.Add(lut);return lut;}
                RenderTexture Target(int width,int height,RenderTextureFormat format=RenderTextureFormat.ARGBFloat)
                {
                    var t=Own(new RenderTexture(width,height,0,format,RenderTextureReadWrite.Linear){name="Color grading input "+width+"x"+height,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});t.Create();return t;
                }
                void Upload(RenderTexture t,Func<int,int,Color> pixel)
                {
                    var texture=Own(new Texture2D(t.width,t.height,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point});var colors=new Color[t.width*t.height];
                    for(int y=0;y<t.height;y++)for(int x=0;x<t.width;x++)colors[y*t.width+x]=pixel(x,y);
                    texture.SetPixels(colors);texture.Apply();var active=RenderTexture.active;Graphics.Blit(texture,t);RenderTexture.active=active;
                }
                float Difference(Color a,Color b){float d=0;for(int c=0;c<4;c++)d=Mathf.Max(d,Mathf.Abs(a[c]-b[c]));return d;}
                bool Throws(Action action){try{action();return false;}catch(ArgumentException){return true;}}
                var profile=new ColorGradingProfile{domain=ColorLutDomain.Linear,maximumInput=1,toneMapping=ColorToneMapping.Clip};
                var source=Target(129,73);Upload(source,(x,y)=>new Color(x/128f,y/72f,(x*17+y*23)%127/126f,.1f+.8f*x/128));
                Check("default-profile-valid",new ColorGradingProfile().IsValid);
                bool gridCapture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_COLOR_GRADE_GRID")=="1";
                bool gridStarted=gridCapture&&RenderDocCaptureBridge.BeginOffscreenCapture();
                foreach(int size in new[]{16,32,64})
                {
                    profile.size=size;var lut=Bake(profile);Check("identity-bake-format-layout-"+size,lut.IsValid&&lut.Size==size&&lut.Texture.mipmapCount==1&&lut.Texture.format==TextureFormat.RGBAFloat);
                    var values=lut.CopyValues();float nodeError=0;
                    for(int b=0;b<size;b++)for(int g=0;g<size;g++)for(int r=0;r<size;r++)nodeError=Mathf.Max(nodeError,Difference(values[r+size*(g+size*b)],new Color(r/(float)(size-1),g/(float)(size-1),b/(float)(size-1),1)));
                    Check("identity-all-cube-nodes-r-fastest-"+size,nodeError<.000001f,nodeError);
                    Check("identity-draw-"+size,renderer.TryRender(source,lut,out var frame)&&frame.IsCurrent&&renderer.DrawCalls==1);
                    float exact,interval;ColorLutReferenceError(lut,ReadSceneTarget(source),ReadSceneTarget(frame.color),out exact,out interval);
                    Check("identity-all-pixels-local-trilinear-interval-"+size,interval<.000005f,interval);
                    // Canonical ideal error is a metric; local sampling/weight controls below are the numerical gates.
                    Check("identity-canonical-error-"+size,!float.IsNaN(exact),exact);
                    var copy=lut.CopyValues();copy[0]=Color.magenta;Check("values-copy-does-not-mutate-"+size,lut.CopyValues()[0]==values[0]);
                }
                if(gridCapture)Check("requested-native-grid-capture",gridStarted&&RenderDocCaptureBridge.EndOffscreenCapture());
                // Isolate the actual eight hardware filter weights with three RGB one-hot node uploads.
                // This is a validation-only payload; the production API still owns immutable authored LUTs.
                var weightSource=Target(65,49);Upload(weightSource,(x,y)=>new Color((5+(x+.27f)/65)/15,(7+(y+.39f)/49)/15,(3+((x*23+y*17)%113+.41f)/113)/15,1));
                var weightInput=ReadSceneTarget(weightSource);var weightSum=new double[weightInput.Length];double maximumWeightError=0,maximumQuantizedError=0;bool weightsInRange=true;
                for(int group=0;group<3;group++)
                {
                    var probeProfile=new ColorGradingProfile{size=16,domain=ColorLutDomain.Linear,maximumInput=1,toneMapping=ColorToneMapping.Clip};var probe=Bake(probeProfile);var basis=new Color[16*16*16];
                    for(int i=0;i<basis.Length;i++)basis[i]=new Color(0,0,0,1);
                    for(int c=0;c<3;c++)
                    {
                        int corner=group*3+c;if(corner>=8)continue;int x=corner&1,y=(corner>>1)&1,z=(corner>>2)&1;int index=5+x+16*(7+y+16*(3+z));var color=basis[index];color[c]=1;basis[index]=color;
                    }
                    var tex=new Texture3D(16,16,16,TextureFormat.RGBAFloat,false,true){name="Color grading filter-weight probe"};tex.SetPixels(basis);tex.Apply(false,true);
                    var textureField=typeof(ColorGradingLut).GetField("texture",BindingFlags.Instance|BindingFlags.NonPublic);Destroy(probe.Texture);textureField.SetValue(probe,tex);
                    typeof(ColorGradingLut).GetField("values",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(probe,basis);
                    Check("basis-filter-probe-render-"+group,renderer.TryRender(weightSource,probe,out var weights));var values=ReadSceneTarget(weights.color);
                    for(int i=0;i<values.Length;i++)for(int channel=0;channel<3;channel++)
                    {
                        int corner=group*3+channel;if(corner>=8)continue;
                        double u=weightInput[i].r*15-5,v=weightInput[i].g*15-7,w=weightInput[i].b*15-3;
                        double expected=((corner&1)==0?1-u:u)*((corner&2)==0?1-v:v)*((corner&4)==0?1-w:w);
                        double uq=Math.Round(u*256)/256,vq=Math.Round(v*256)/256,wq=Math.Round(w*256)/256;
                        double quantized=((corner&1)==0?1-uq:uq)*((corner&2)==0?1-vq:vq)*((corner&4)==0?1-wq:wq);
                        maximumWeightError=Math.Max(maximumWeightError,Math.Abs(values[i][channel]-expected));maximumQuantizedError=Math.Max(maximumQuantizedError,Math.Abs(values[i][channel]-quantized));weightSum[i]+=values[i][channel];
                        weightsInRange&=values[i][channel]>=0&&values[i][channel]<=1;
                    }
                }
                double sumError=0;foreach(double sum in weightSum)sumError=Math.Max(sumError,Math.Abs(sum-1));
                Check("eight-filter-weights-form-partition",sumError<.000001,(float)sumError);
                Check("eight-filter-weights-nonnegative-and-bounded",weightsInRange);
                Check("eight-filter-weights-local-address-and-rounding-bound",maximumWeightError<(3*.6+1)/256+.00001,(float)maximumWeightError);
                Check("eight-filter-weights-difference-after-address-rounding",maximumQuantizedError<1.0/256+.00001,(float)maximumQuantizedError);
                profile.size=32;var neutral=Bake(profile);
                var snapshot=neutral.CopyValues();profile.red=ColorGradingProfile.ConstantCurve(0);Check("bake-is-immutable-snapshot",ScenePixelsEqual(snapshot,neutral.CopyValues()));profile.red=ColorGradingProfile.IdentityCurve();
                Check("profile-json-roundtrip",profile.ToJson(false)==ColorGradingProfile.FromJson(profile.ToJson(false)).ToJson(false));
                Check("invalid-null-profile-rejected",Throws(()=>ColorGradingLut.Bake(null)));
                foreach(Action<ColorGradingProfile> invalid in new Action<ColorGradingProfile>[] {
                    p=>p.schemaVersion=2,p=>p.size=17,p=>p.maximumInput=float.NaN,p=>p.exposureEV=float.PositiveInfinity,p=>p.domain=(ColorLutDomain)9,p=>p.toneMapping=(ColorToneMapping)9,
                    p=>p.gamma=Vector3.zero,p=>p.sourceWhite=Vector2.zero,p=>p.gtLinearLength=1,p=>p.master=null,p=>p.red=new[]{new Vector2(0,0),new Vector2(0,1),Vector2.one},
                    p=>p.hueVsHue=new[]{new Vector2(0,0),new Vector2(1,.1f)},p=>p.blue=new[]{Vector2.zero,new Vector2(1,float.NaN)}})
                {
                    var p=new ColorGradingProfile();invalid(p);Check("invalid-profile-rejected-"+report.checks.Count,!p.IsValid&&Throws(()=>ColorGradingLut.Bake(p)));
                }
                Check("invalid-json-rejected",Throws(()=>ColorGradingProfile.FromJson("{}"))&&Throws(()=>ColorGradingProfile.FromJson("null")));

                // Independent scalar branch-weight reference for the published GT curve, not a LUT self-comparison.
                foreach(double contrast in new[]{.7,1,1.6})foreach(double start in new[]{.12,.22,.7})foreach(double length in new[]{0,.4,.8})
                {
                    double max=0;
                    for(int i=0;i<512;i++)
                    {
                        double x=Math.Pow(i/511.0,3)*16,m=start,l0=(1-m)*length/contrast,s0=m+l0,s1=m+contrast*l0,u=Math.Min(1,x/m),w0=1-u*u*(3-2*u),w2=x>=s0?1:0;
                        double expected=(m*Math.Pow(x/m,1.33)) * w0+(m+contrast*(x-m))*(1-w0-w2)+(1-(1-s1)*Math.Exp(-contrast*(x-s0)/(1-s1)))*w2;
                        max=Math.Max(max,Math.Abs(ColorGradingMath.GranTurismo(x,contrast,m,length,1.33,0)-expected));
                    }
                    Check("gt-published-piecewise-double-"+contrast+"-"+start+"-"+length,max<1e-12,(float)max);
                }
                var mid=new Color(.125f,.25f,.5f,.37f);
                profile.exposureEV=1;Check("exposure-ev-doubles-before-tone-map",Difference(ColorGradingMath.Evaluate(profile,mid),new Color(.25f,.5f,1,.37f))<.000001f);profile.exposureEV=0;
                profile.colorFilter=new Vector3(.5f,1,.25f);Check("linear-color-filter-analytic",Difference(ColorGradingMath.Evaluate(profile,mid),new Color(.0625f,.25f,.125f,.37f))<.000001f);profile.colorFilter=Vector3.one;
                profile.hueDegrees=120;Check("hue-primary-rotation",Difference(ColorGradingMath.Evaluate(profile,Color.red),Color.green)<.000001f);profile.hueDegrees=0;
                float gray=mid.r*.2126f+mid.g*.7152f+mid.b*.0722f;
                profile.saturation=0;Check("zero-saturation-achromatic-luminance",Difference(ColorGradingMath.Evaluate(profile,mid),new Color(gray,gray,gray,.37f))<.000001f);profile.saturation=1;
                profile.lift=Vector3.one*.1f;profile.gain=Vector3.one*.5f;profile.gamma=Vector3.one*2;
                var expectedLgg=new Color(Mathf.Sqrt(mid.r*.5f+.1f),Mathf.Sqrt(mid.g*.5f+.1f),Mathf.Sqrt(mid.b*.5f+.1f),mid.a);
                Check("lift-gain-before-inverse-gamma-analytic",Difference(ColorGradingMath.Evaluate(profile,mid),expectedLgg)<.000001f);profile.lift=Vector3.zero;profile.gain=profile.gamma=Vector3.one;
                var d65=new Vector2(.3127f,.329f);var d50=new Vector2(.34567f,.3585f);
                var adaptation=ColorGradingMath.WhiteBalance(d65,d50);var adapted=adaptation.MultiplyVector(Vector3.one);
                // Independent direct XYZ->linear-sRGB reference at the selected D50 chromaticity.
                double wx=.34567/.3585,wz=(1-.34567-.3585)/.3585;
                var expectedWhite=new Vector3((float)(12831.0/3959*wx-329.0/214-1974.0/3959*wz),(float)(-851781.0/878810*wx+1648619.0/878810+36519.0/878810*wz),(float)(705.0/12673*wx-2585.0/12673+705.0/667*wz));
                Check("bradford-d65-d50-white-reference",Vector3.Distance(adapted,expectedWhite)<.000002f,Vector3.Distance(adapted,expectedWhite));
                var restored=ColorGradingMath.WhiteBalance(d50,d65).MultiplyVector(adapted);Check("bradford-roundtrip-white",Vector3.Distance(restored,Vector3.one)<.000002f,Vector3.Distance(restored,Vector3.one));
                profile.sourceWhite=d50;profile.targetWhite=d65;var coloredWhite=ColorGradingMath.Evaluate(profile,new Color(adapted.x*.4f,adapted.y*.4f,adapted.z*.4f,1));
                Check("white-balance-removes-reference-cast",Difference(coloredWhite,new Color(.4f,.4f,.4f,1))<.000002f);profile.sourceWhite=profile.targetWhite=d65;
                profile.master=new[]{Vector2.zero,new Vector2(.5f,.25f),Vector2.one};Check("master-piecewise-knot",Difference(ColorGradingMath.Evaluate(profile,new Color(.5f,.5f,.5f,1)),new Color(.25f,.25f,.25f,1))<.000001f);profile.master=ColorGradingProfile.IdentityCurve();
                profile.red=ColorGradingProfile.ConstantCurve(.2f);Check("red-curve-only-red",Difference(ColorGradingMath.Evaluate(profile,mid),new Color(.2f,.25f,.5f,.37f))<.000001f);profile.red=ColorGradingProfile.IdentityCurve();
                profile.hueVsHue=ColorGradingProfile.ConstantCurve(1f/3);Check("hue-curve-primary-rotation",Difference(ColorGradingMath.Evaluate(profile,Color.red),Color.green)<.000001f);profile.hueVsHue=ColorGradingProfile.ConstantCurve(0);
                foreach(string key in new[]{"hueVsSaturation","saturationVsSaturation","luminanceVsSaturation"})
                {
                    var field=typeof(ColorGradingProfile).GetField(key);field.SetValue(profile,ColorGradingProfile.ConstantCurve(0));Check(key+"-desaturates",Difference(ColorGradingMath.Evaluate(profile,mid),new Color(gray,gray,gray,.37f))<.000001f);field.SetValue(profile,ColorGradingProfile.ConstantCurve(1));
                }

                // A genuinely non-identity author-created profile, with no file or private LUT input.
                var grade=new ColorGradingProfile{size=32,maximumInput=64,exposureEV=.6f,colorFilter=new Vector3(1.08f,.97f,.89f),contrast=1.08f,hueDegrees=13,saturation=.88f,
                    sourceWhite=d50,targetWhite=d65,lift=new Vector3(.008f,.004f,.001f),gamma=new Vector3(1.08f,1.02f,.95f),gain=new Vector3(1.01f,.98f,1.06f)};
                grade.master=new[]{Vector2.zero,new Vector2(.3f,.27f),new Vector2(.7f,.75f),Vector2.one};
                grade.hueVsSaturation=new[]{new Vector2(0,1),new Vector2(.35f,.7f),new Vector2(.7f,1.2f),new Vector2(1,1)};
                var authored=Bake(grade);
                File.WriteAllText(Path.Combine(_directory,"color-grade-authored-profile.json"),authored.ProfileJson);
                File.WriteAllText(Path.Combine(_directory,"color-grade-neutral-profile.json"),neutral.ProfileJson);
                Upload(source,(x,y)=>new Color(Mathf.Pow(x/128f,3)*100-.03f,Mathf.Pow(y/72f,2)*16,(x*19+y*31)%211/211f*4,.1f+.8f*x/128));
                ColorGradingRenderer.Frame Render(string name,ColorGradingLut lut)
                {
                    var active=RenderTexture.active;Check(name+"-render",renderer.TryRender(source,lut,out var frame)&&frame.IsCurrent&&renderer.DrawCalls==1);
                    var raw=ReadSceneTarget(source);var output=ReadSceneTarget(frame.color);float max,interval;ColorLutReferenceError(lut,raw,output,out max,out interval);
                    Check(name+"-all-pixels-local-trilinear",interval<.000005f,interval);Check(name+"-canonical-reference-difference",!float.IsNaN(max),max);
                    bool range=true;float alpha=0;for(int i=0;i<output.Length;i++){alpha=Mathf.Max(alpha,Mathf.Abs(raw[i].a-output[i].a));for(int c=0;c<3;c++)range&=!float.IsNaN(output[i][c])&&output[i][c]>=0&&output[i][c]<=1;}
                    Check(name+"-display-linear-range-and-source-alpha",range&&alpha==0,alpha);Check(name+"-caller-active-restored",RenderTexture.active==active);return frame;
                }
                var first=Render("authored-log-domain",authored);SaveSsrPreview("color-grade-authored-hdr-input",ReadSceneTarget(source),source.width,source.height,false);SaveSsrPreview("color-grade-authored-result",ReadSceneTarget(first.color),source.width,source.height,false);
                var authoredBaseline=ReadSceneTarget(first.color);float change=0;var ungraded=ReadSceneTarget(source);for(int i=0;i<ungraded.Length;i++)change+=Mathf.Abs(ungraded[i].r-authoredBaseline[i].r);Check("nonidentity-hdr-grade-nonvacuous",change/ungraded.Length>1,change/ungraded.Length);
                double coarseError=0;
                foreach(int resolution in new[]{16,32,64})
                {
                    var quality=ColorGradingProfile.FromJson(authored.ProfileJson);quality.size=resolution;var candidate=Bake(quality);
                    var pixels=ReadSceneTarget(Render("analytic-grade-resolution-"+resolution,candidate).color);double sum=0;float largest=0;
                    for(int i=0;i<pixels.Length;i++){var expected=ColorGradingMath.Evaluate(quality,ungraded[i]);for(int c=0;c<3;c++){float error=Mathf.Abs(pixels[i][c]-expected[c]);sum+=error;largest=Mathf.Max(largest,error);}}
                    double mean=sum/(pixels.Length*3);if(resolution==16)coarseError=mean;
                    Check("analytic-grade-mae-"+resolution,mean>0&&mean<.02,(float)mean);
                    Check("analytic-grade-maximum-difference-"+resolution,!float.IsNaN(largest),largest);
                    if(resolution==64)Check("64-cube-improves-16-cube-analytic-mae",mean<coarseError*.5,(float)(mean/coarseError));
                }
                source.filterMode=FilterMode.Trilinear;source.wrapMode=TextureWrapMode.Repeat;source.anisoLevel=16;authored.Texture.filterMode=FilterMode.Point;authored.Texture.wrapMode=TextureWrapMode.Repeat;QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;
                var filtered=Render("caller-sampler-negative-control",authored);Check("explicit-samplers-ignore-caller-state",ScenePixelsEqual(authoredBaseline,ReadSceneTarget(filtered.color)));QualitySettings.anisotropicFiltering=savedAnisotropy;
                Check("old-frame-invalidated",!first.IsCurrent);other.TryRender(source,authored,out var separate);Check("independent-renderer-targets",separate.IsCurrent&&separate.color!=filtered.color);
                foreach(var format in new[]{RenderTextureFormat.ARGBFloat,RenderTextureFormat.ARGBHalf,RenderTextureFormat.RGB111110Float})
                foreach(var size in new[]{new Vector2Int(1,1),new Vector2Int(1,17),new Vector2Int(19,1),new Vector2Int(97,81)})
                {
                    source=Target(size.x,size.y,format);Upload(source,(x,y)=>new Color(.2f,.4f,32,.37f));var f=Render("format-"+format+"-size-"+size.x+"x"+size.y,authored);Check("dimensions-"+format+"-"+size.x+"x"+size.y,f.color.width==size.x&&f.color.height==size.y);
                }
                var lease=Render("before-target-loss",authored);lease.color.Release();Check("target-loss-invalidates",!lease.IsCurrent&&!renderer.TryGetFrame(out _));var rebuilt=Render("after-target-loss",authored);
                Check("output-alias-rejected",!renderer.TryRender(rebuilt.color,authored,out _)&&!rebuilt.IsCurrent&&renderer.DrawCalls==0);
                Render("after-alias-rejection",authored);Check("missing-lut-rejected",!renderer.TryRender(source,null,out _)&&!renderer.TryGetFrame(out _));
                var disposable=Bake(grade);var valid=Render("before-lut-disposal",disposable);disposable.Dispose();Check("disposed-lut-rejected",!renderer.TryRender(source,disposable,out _)&&!valid.IsCurrent);
                Check("other-renderer-remains-valid",separate.IsCurrent);
                var wrong=Target(4,4,RenderTextureFormat.RFloat);Check("non-hdr-source-rejected",!renderer.TryRender(wrong,authored,out _));Check("missing-source-rejected",!renderer.TryRender(null,authored,out _));
                var final=Render("before-renderer-dispose",authored);renderer.Dispose();Check("renderer-dispose-invalidates",!final.IsCurrent&&!final.color.IsCreated()&&authored.IsValid);

                var host=Own(new GameObject("Authored color production camera"));host.SetActive(false);var camera=host.AddComponent<Camera>();camera.enabled=false;camera.allowHDR=true;camera.allowMSAA=false;camera.cullingMask=1<<26;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.24f,.52f,1);camera.renderingPath=RenderingPath.Forward;camera.orthographic=true;camera.orthographicSize=2;camera.aspect=97f/81;camera.transform.position=new Vector3(0,0,-4);
                var outputTarget=Own(new RenderTexture(97,81,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));outputTarget.Create();camera.targetTexture=outputTarget;
                var panel=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));panel.layer=26;panel.transform.localScale=new Vector3(2.4f,2.8f,1);var panelMaterial=Own(new Material(Resources.Load<Shader>("StudioAccent")));panelMaterial.SetColor("_Color",new Color(2.3f,.52f,.16f,1));panel.GetComponent<Renderer>().sharedMaterial=panelMaterial;
                var post=host.AddComponent<OriginalStyleRenderPipeline>();post.useAuthoredColorGrading=true;post.authoredColorLut=authored;host.SetActive(true);camera.Render();
                Check("production-no-private-lut-attempt",!(bool)typeof(OriginalStyleRenderPipeline).GetField("_capturedLutAttempted",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(post));
                Check("production-actual-post-result",post.TryGetAuthoredColorFrame(out var production)&&production.IsCurrent&&post.AuthoredColorUnavailableReason==null);
                bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_COLOR_GRADE")=="1",started=false,ended=false;if(capture)started=RenderDocCaptureBridge.BeginOffscreenCapture();
                try
                {
                    camera.Render();Check("production-captured-authored-grade",post.TryGetAuthoredColorFrame(out var a)&&a.IsCurrent);
                    SaveSsrPreview("color-grade-production-authored",ReadSceneTarget(outputTarget),97,81,false);
                    post.authoredColorLut=neutral;camera.Render();Check("production-captured-neutral-grade",post.TryGetAuthoredColorFrame(out var b)&&b.IsCurrent);
                    SaveSsrPreview("color-grade-production-neutral",ReadSceneTarget(outputTarget),97,81,false);
                }
                finally {if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                if(capture)Check("requested-native-capture",started&&ended);
                post.authoredColorLut=null;camera.Render();Check("production-missing-lut-invalidates-no-silent-old-grade",!post.TryGetAuthoredColorFrame(out _)&&post.AuthoredColorUnavailableReason!=null);
                post.useAuthoredColorGrading=false;camera.Render();var legacy=ReadSceneTarget(outputTarget);Check("production-default-old-path",!post.TryGetAuthoredColorFrame(out _)&&post.AuthoredColorUnavailableReason==null);
                post.useAuthoredColorGrading=true;post.authoredColorLut=authored;camera.Render();post.useAuthoredColorGrading=false;camera.Render();Check("production-disabled-default-byte-restored",ScenePixelsEqual(legacy,ReadSceneTarget(outputTarget))&&authored.IsValid);
                post.enabled=false;camera.targetTexture=null;host.SetActive(false);
            }
            finally
            {
                renderer.Dispose();other.Dispose();foreach(var lut in luts)lut.Dispose();QualitySettings.anisotropicFiltering=savedAnisotropy;RenderTexture.active=savedActive!=null&&savedActive.IsCreated()?savedActive:null;
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];foreach(var item in _owned)if(item!=null)Destroy(item);_owned.Clear();
            }
        }

        // Ideal double trilinear plus a desktop diagnostic interval, gated by the eight-weight probe above.
        // Address precision is specified by D3D11; cross-weight precision is measured, not an API guarantee.
        private static void ColorLutReferenceError(ColorGradingLut lut,Color[] input,Color[] output,out float maximum,out float intervalResidual)
        {
            var nodes=lut.CopyValues();int n=lut.Size;maximum=intervalResidual=0;
            for(int pixel=0;pixel<input.Length;pixel++)
            {
                var pos=new double[3];var low=new int[3];var fraction=new double[3];
                for(int c=0;c<3;c++)
                {
                    double v=Math.Max(0,Math.Min(lut.MaximumInput,input[pixel][c]));if(double.IsNaN(v))v=0;
                    double d=lut.Domain==ColorLutDomain.Linear?v/lut.MaximumInput:Math.Log(1+v)/Math.Log(1+lut.MaximumInput);
                    pos[c]=d*(n-1);low[c]=(int)Math.Floor(pos[c]);fraction[c]=pos[c]-low[c];
                }
                for(int channel=0;channel<3;channel++)
                {
                    double reference=0,min=double.PositiveInfinity,max=double.NegativeInfinity;
                    for(int z=0;z<=1;z++)for(int y=0;y<=1;y++)for(int x=0;x<=1;x++)
                    {
                        double value=nodes[Math.Min(n-1,low[0]+x)+n*(Math.Min(n-1,low[1]+y)+n*Math.Min(n-1,low[2]+z))][channel];
                        reference+=value*(x==0?1-fraction[0]:fraction[0])*(y==0?1-fraction[1]:fraction[1])*(z==0?1-fraction[2]:fraction[2]);min=Math.Min(min,value);max=Math.Max(max,value);
                    }
                    double error=Math.Abs(output[pixel][channel]-reference);
                    // Eight coefficient errors <=1/256 plus partition sum imply <=4/256 total variation;
                    // three address axes contribute <=1.8/256. Round their sum up to 6/256 of the local range.
                    double bound=(max-min)*6/256.0+2e-6;
                    maximum=Math.Max(maximum,(float)error);intervalResidual=Math.Max(intervalResidual,(float)Math.Max(0,error-bound));
                }
            }
        }
    }
}
