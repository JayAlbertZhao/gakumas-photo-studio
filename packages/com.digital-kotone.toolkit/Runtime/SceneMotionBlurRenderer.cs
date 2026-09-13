using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class SceneMotionBlurRenderer : IDisposable
    {
        public RenderTexture Guide {get;private set;}
        public int VisibilityDrawCalls {get;private set;}
        public int ResolveDrawCalls => renderer.DrawCalls;
        public int TargetCount => (Guide!=null?1:0)+renderer.TargetCount;
        public Vector2 PreparedJitter {get;private set;}
        private readonly MotionBlurRenderer renderer=new MotionBlurRenderer();
        private readonly List<Material> materials=new List<Material>();
        private MotionBlurSettings settings;
        private Shader shader;
        private Matrix4x4 view,projection;
        private Vector2 previousJitter,jitterDelta;
        private double previousTime,pendingTime;
        private float interval;
        private bool clock,pending,hasResult;
        private uint lastSequence;
        public bool IsCreated => Guide!=null&&Guide.IsCreated();
        public bool HasResult(uint sequence)=>hasResult&&lastSequence==sequence&&IsCreated&&renderer.TryGetFrame(out _);
        public bool TryGetFrame(out MotionBlurRenderer.Frame frame)=>renderer.TryGetFrame(out frame);

        public bool Prepare(MotionBlurSettings config,Camera camera,SceneMotionHistory motion,double time,Vector2 jitter,out string error)
        {
            error=null;hasResult=pending=false;VisibilityDrawCalls=0;
            if(config==null||!config.enabled||!config.IsValid||double.IsNaN(time)||double.IsInfinity(time)||
               !MotionBlurSettings.Range(jitter.x,-.5f,.5f)||!MotionBlurSettings.Range(jitter.y,-.5f,.5f))
            {error="Invalid scene motion blur settings/time/jitter";return false;}
            if(motion==null||!motion.IsCreated){error="Scene motion blur requires explicit current scene motion";return false;}
            shader=Resources.Load<Shader>("SceneMotionBlurGuide");var format=GraphicsFormat.R32G32B32A32_SFloat;
            if(shader==null||!shader.isSupported||!SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))
            {error="Scene motion blur visibility shader/float target unavailable";return false;}
            var target=camera.targetTexture;
            if(!IsCreated||Guide.width!=target.width||Guide.height!=target.height)
            {
                ReleaseGuide();ResetHistory();
                Guide=new RenderTexture(target.width,target.height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){name="Toolkit motion blur visible UV depth",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};Guide.Create();
                if(!IsCreated||Guide.graphicsFormat!=format){error="Scene motion blur guide allocation failed";return false;}
            }
            settings=config.Snapshot();view=camera.worldToCameraMatrix;projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);
            pendingTime=time;PreparedJitter=jitter;jitterDelta=jitter-previousJitter;
            double elapsed=time-previousTime;
            interval=clock&&motion.Continuous&&elapsed>=.000001&&elapsed<=config.maximumSampleInterval?(float)elapsed:0;
            pending=true;return true;
        }
        public void RecordVisibility(CommandBuffer commands,SceneMotionHistory motion,SceneDeferredCamera.Surface[] surfaces)
        {
            commands.BeginSample("Toolkit motion blur actual framebuffer visibility");
            commands.SetRenderTarget(Guide,BuiltinRenderTextureType.CurrentActive);commands.ClearRenderTarget(false,true,Color.clear);
            foreach(var surface in surfaces)
            {
                var r=surface.renderer;if(!r.enabled||r.forceRenderingOff||!r.gameObject.activeInHierarchy)continue;
                if(materials.Count<=VisibilityDrawCalls)materials.Add(new Material(shader){hideFlags=HideFlags.HideAndDontSave});
                var material=materials[VisibilityDrawCalls++];material.SetMatrix("_ViewProjection",projection*view);material.SetMatrix("_View",view);
                material.SetVector("_VertexScale",surface.vertexScale);material.SetTexture("_AlphaMap",surface.inputs.albedoMap!=null?surface.inputs.albedoMap:Texture2D.whiteTexture);
                material.SetVector("_AlphaST",surface.inputs.uvST);material.SetFloat("_Alpha",surface.inputs.alpha);material.SetFloat("_Cutoff",surface.alphaCutoff);material.SetFloat("_Cull",(int)surface.cull);
                material.SetTexture("_Motion",motion.Motion);material.SetFloat("_Correspondence",interval>0?1:0);
                // NoJitter color is in a different coordinate convention when mixed with resolved TAA.
                material.SetFloat("_Excluded",surface.excludeMotionBlur||(((int)surface.temporalFlags)&4)!=0?1:0);
                commands.DrawRenderer(r,material,surface.materialIndex,0);
            }
            commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);commands.EndSample("Toolkit motion blur actual framebuffer visibility");
        }
        public void Complete()
        {
            if(!pending)return;
            previousTime=pendingTime;previousJitter=PreparedJitter;clock=true;pending=false;
        }
        public bool Resolve(uint sequence,RenderTexture source,bool dejittered,RenderTexture flags,out RenderTexture output,out string error)
        {
            output=null;error=null;
            if(!IsCreated){error="Scene motion blur current guide unavailable";hasResult=false;return false;}
            if(hasResult&&lastSequence==sequence){error="Scene motion blur already consumed this render";return false;}
            var input=new MotionBlurInput(source,Guide,interval,jitterDelta,dejittered?-PreparedJitter:Vector2.zero,flags);
            if(!renderer.TryRender(input,settings,out var frame)){error=renderer.UnavailableReason;hasResult=false;return false;}
            lastSequence=sequence;hasResult=true;output=frame.color;return true;
        }
        public void ResetHistory(){clock=pending=hasResult=false;interval=0;}
        private void ReleaseGuide(){if(Guide!=null){if(RenderTexture.active==Guide)RenderTexture.active=null;Guide.Release();UnityEngine.Object.Destroy(Guide);}Guide=null;}
        public void Dispose(){ReleaseGuide();renderer.Dispose();ResetHistory();foreach(var material in materials)if(material!=null)UnityEngine.Object.Destroy(material);materials.Clear();VisibilityDrawCalls=0;}
    }
}
