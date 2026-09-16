using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using GakumasPhotoMode.Examples;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifyDesktopExample(Report report)
        {
            var oldGraphics=GraphicsSettings.renderPipelineAsset;var oldQuality=QualitySettings.renderPipeline;
            var go=new GameObject("Public desktop example validation");var example=go.AddComponent<DesktopHostExample>();
            example.width=320;example.height=180;example.presentToScreen=false;
            void Check(string n,bool ok,float v=0)=>FrameworkCheck(report,"desktop-example-"+n,ok,v);
            float Difference(Color[] a,Color[] b){float e=0;for(int p=0;p<a.Length;p++)for(int c=0;c<4;c++)e=Mathf.Max(e,Mathf.Abs(a[p][c]-b[p][c]));return e;}
            Color[] Run(string name,double time)
            {
                bool requested=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_DESKTOP_EXAMPLE_CASE")==name;
                bool began=requested&&RenderDocCaptureBridge.BeginOffscreenCapture();bool ok;
                try { ok=example.RenderOffscreen(time); }
                finally { if(began)Check(name+"-native-capture",RenderDocCaptureBridge.EndOffscreenCapture()); }
                Check(name+"-render",ok);
                if(!ok)throw new InvalidOperationException(example.LastError);
                Check(name+"-explicit-time",example.LastRenderedTimeSeconds==time,(float)example.LastRenderedTimeSeconds);
                float expectedBodyY=1+.08f*Mathf.Sin((float)time*2),actualBodyY=example.Configuration.actors.renderers[0].transform.position.y;
                Check(name+"-explicit-body-pose",expectedBodyY==actualBodyY,actualBodyY);
                var pixels=ReadSceneTarget(example.Display);bool finite=true;float min=float.PositiveInfinity,max=0;
                foreach(var p in pixels)for(int c=0;c<4;c++) {finite&=!float.IsNaN(p[c])&&!float.IsInfinity(p[c]);if(c<3){min=Mathf.Min(min,p[c]);max=Mathf.Max(max,p[c]);}}
                Check(name+"-finite-nonconstant",finite&&max-min>.05f,max-min);
                var preview=new Color[pixels.Length];for(int i=0;i<pixels.Length;i++)preview[i]=pixels[i].gamma;
                SaveSsrPreview("desktop-example-"+name,preview,example.width,example.height,false);
                using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"desktop-example-"+name+".raw")));
                foreach(var p in pixels)for(int c=0;c<4;c++)writer.Write(p[c]);return pixels;
            }
            try
            {
                var bridge=typeof(RenderPipelineManager).GetMethod("DoRenderLoop_Internal",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
                Debug.Log("[DesktopRequestDiagnostic] native bridge request type="+bridge?.GetParameters()[2].ParameterType.FullName);
                example.width=127;bool invalidSize=false;
                try { example.Initialize(); } catch(ArgumentOutOfRangeException) { invalidSize=true; }
                Check("invalid-size-does-not-select-pipeline",invalidSize&&!example.IsInitialized&&GraphicsSettings.renderPipelineAsset==oldGraphics&&QualitySettings.renderPipeline==oldQuality);
                example.width=320;
                example.Initialize();example.RenderCamera.enabled=false;
                Check("explicit-generated-scene-initialized",example.IsInitialized&&example.Configuration.actors.renderers.Length==4&&example.Configuration.scene.surfaces.Length==3);
                Check("pipeline-selected-explicitly",GraphicsSettings.currentRenderPipeline!=oldGraphics);
                var competitorObject=new GameObject("Rejected second example");var competitor=competitorObject.AddComponent<DesktopHostExample>();
                try
                {
                    bool rejected=false;try { competitor.Initialize(); } catch(InvalidOperationException) { rejected=true; }
                    Check("second-owner-rejected-before-allocation",rejected&&!competitor.IsInitialized&&competitor.Display==null);
                }
                finally { competitorObject.SetActive(false);Destroy(competitorObject); }
                bool badTime=false;try { example.RenderOffscreen(double.NaN); } catch(ArgumentOutOfRangeException) { badTime=true; }
                Check("nonfinite-time-rejected-before-frame",badTime&&example.RenderedFrames==0&&!example.HasCompletedFrame);
                Run("cold",0);var warm=Run("warm",0);var repeat=Run("warm-repeat",0);
                Check("warm-repeat-whole-color",Difference(warm,repeat)<.00001f,Difference(warm,repeat));
                var moved=Run("animated",.7);Check("explicit-motion-changes-frame",Difference(warm,moved)>.01f,Difference(warm,moved));
                example.Configuration.effects.enabled=false;var noFx=Run("effects-off",.7);
                Check("transparent-effects-positive-control",Difference(moved,noFx)>.001f,Difference(moved,noFx));
                example.Configuration.effects.enabled=true;
                example.Configuration.planar.enabled=false;var noPlanar=Run("planar-off",.7);
                Check("actor-planar-positive-control",Difference(moved,noPlanar)>.001f,Difference(moved,noPlanar));
                example.Configuration.planar.enabled=true;
                example.Configuration.selfShadow.strength=0;var noSelf=Run("self-shadow-off",.7);
                Check("self-shadow-positive-control",Difference(moved,noSelf)>.00001f,Difference(moved,noSelf));
                example.Configuration.selfShadow.strength=1;
                example.Configuration.scene.mainLightShadow.strength=0;var noDrop=Run("drop-shadow-off",.7);
                Check("background-shadow-positive-control",Difference(moved,noDrop)>.00001f,Difference(moved,noDrop));
                example.Configuration.scene.mainLightShadow.strength=1;
                Run("restored",.7);var restore=Run("restored-warm",.7);
                Check("all-toggles-restore-whole-color",Difference(moved,restore)<.00001f,Difference(moved,restore));
                example.Configuration.depthOfField.enabled=false;var noDof=Run("dof-off",.7);
                Check("dof-positive-control",Difference(restore,noDof)>.00001f,Difference(restore,noDof));
                example.Configuration.depthOfField.enabled=true;
                example.Configuration.actors.parameters.SetVector("_CapturedLightColor",Vector4.zero);
                var noKey=Run("key-light-off",.7);
                Check("full-actor-lighting-positive-control",Difference(restore,noKey)>.01f,Difference(restore,noKey));
                example.Configuration.actors.parameters.SetVector("_CapturedLightColor",Vector4.one);
                var restoredLight=Run("key-light-restored",.7);
                Check("full-actor-lighting-restores-color",Difference(restore,restoredLight)<.00001f,Difference(restore,restoredLight));
                var frames=example.RenderedFrames;var effect=example.Configuration.effects.geometry.surfaces[0];
                float opacity=effect.opacity;effect.opacity=float.NaN;
                bool failed=example.RenderOffscreen(.7);effect.opacity=opacity;
                Check("failed-post-not-a-completed-or-presentable-frame",!failed&&!example.HasCompletedFrame&&example.LastError!=null&&example.RenderedFrames==frames);
                Check("failed-post-keeps-last-display-bytes",Difference(restoredLight,ReadSceneTarget(example.Display))==0);
                Run("recovered",.7);var recovered=Run("recovered-warm",.7);
                Check("failure-retires-work-and-recovers-color",example.HasCompletedFrame&&example.LastError==null&&Difference(restore,recovered)<.00001f,Difference(restore,recovered));
                var display=example.Display;example.Shutdown();
                Check("shutdown-releases-owned-display",!display.IsCreated()&&!example.IsInitialized);
                Check("shutdown-restores-caller-pipeline",GraphicsSettings.renderPipelineAsset==oldGraphics&&QualitySettings.renderPipeline==oldQuality);
                example.Initialize();example.RenderCamera.enabled=false;Run("reinitialized",0);
                Check("reinitialize-resets-sequence",example.RenderedFrames==1);
                var foreign=ScriptableObject.CreateInstance<TilePassTestAsset>();
                try
                {
                    GraphicsSettings.renderPipelineAsset=foreign;QualitySettings.renderPipeline=foreign;
                    bool rejected=false;try { example.RenderOffscreen(0); } catch(InvalidOperationException) { rejected=true; }
                    Check("foreign-pipeline-rejects-render",rejected);
                    example.Shutdown();
                    Check("shutdown-preserves-new-owner-pipeline",GraphicsSettings.renderPipelineAsset==foreign&&QualitySettings.renderPipeline==foreign);
                }
                finally { GraphicsSettings.renderPipelineAsset=oldGraphics;QualitySettings.renderPipeline=oldQuality;Destroy(foreign); }
            }
            finally { example.Shutdown();go.SetActive(false);Destroy(go); }
        }
    }
}
