using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class SceneGtaoTemporalRenderer : IDisposable
    {
        public RenderTexture Raw { get; private set; }
        private readonly RenderTexture[] _ao=new RenderTexture[2],_normal=new RenderTexture[2];
        private int _read, _write, _nextPhase, _historyFrame=-1;
        private Material _rawMaterial,_resolveMaterial;
        private float[] _signature;
        private Matrix4x4 _previousView,_previousProjection,_view,_projection;
        private bool _prepared,_continuous;
        private SceneGtaoTemporalSettings _settings;
        public bool HistoryAvailable { get; private set; }
        public int Phase { get; private set; }
        public int RawDrawCalls { get; private set; }
        public int ResolveDrawCalls { get; private set; }
        public RenderTexture Result => _ao[_write];
        public RenderTexture NormalIdentity => _normal[_write];
        public int TargetCount => Raw==null?0:5;
        public bool IsCreated => Raw!=null&&Raw.IsCreated()&&Created(_ao)&&Created(_normal);

        public bool Prepare(SceneGtaoSettings g,Camera camera,SceneMotionHistory motion,out string error)
        {
            error=null;RawDrawCalls=ResolveDrawCalls=0;_prepared=false;
            if(motion==null||!motion.IsCreated){error="Temporal GTAO requires enabled scene motion correspondence";return false;}
            _settings=g.temporal;
            var shader=Resources.Load<Shader>("SceneGtaoTemporal");var format=GraphicsFormat.R32G32B32A32_SFloat;
            if(shader==null||!shader.isSupported||!SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))
            {error="Temporal GTAO requires float4 history render/sample";return false;}
            var t=camera.targetTexture;
            if(!IsCreated||Raw.width!=t.width||Raw.height!=t.height)
            {
                ReleaseTargets();ResetHistory();
                Raw=Target(t,"Toolkit GTAO current rotated visibility");
                for(int i=0;i<2;i++){_ao[i]=Target(t,"Toolkit GTAO history visibility depth age weight "+i);_normal[i]=Target(t,"Toolkit GTAO history normal identity "+i);}
                if(!IsCreated){error="Temporal GTAO target allocation failed";return false;}
            }
            if(_rawMaterial==null)_rawMaterial=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
            if(_resolveMaterial==null)_resolveMaterial=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
            _view=camera.worldToCameraMatrix;_projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);
            float[] signature={g.radius,g.strength,g.normalBias,g.falloffStart,g.thicknessBlend,g.slices,g.stepsPerSide,g.maxRadiusPixels,
                (float)g.resolution,g.reconstructionDepthTolerance,g.reconstructionNormalThreshold,(float)g.combineWithCapsules,
                _settings.rotateSamples?1:0,_settings.historyWeight,_settings.maximumHistory,_settings.maximumFrameGap,
                _settings.depthTolerance,_settings.normalThreshold,_settings.varianceGamma,_settings.clampPadding,_settings.reactiveThreshold,_settings.maximumHistoryDeviation};
            int gap=Time.frameCount-_historyFrame;
            _continuous=HistoryAvailable&&motion.Continuous&&gap>=0&&gap<=_settings.maximumFrameGap&&Same(signature,_signature)&&MatrixDifference(_projection,_previousProjection)<.1f;
            if(!_continuous)_nextPhase=0;
            _signature=signature;Phase=_settings.rotateSamples?_nextPhase:0;_write=1-_read;_prepared=true;return true;
        }
        public float SliceOffset => _settings.rotateSamples?(Phase+.5f)/6-.5f:0;
        public bool RotateSamples => _settings.rotateSamples;
        public void Record(CommandBuffer commands,RenderTexture geometry,RenderTexture coarse,SceneMotionHistory motion,Mesh quad,
            Vector4 parameters,Vector4 quality,Vector4 reconstruction)
        {
            if(!_prepared||!IsCreated)return;
            var m=_rawMaterial;
            m.SetTexture("_ScreenGeometry",geometry);m.SetMatrix("_ScreenInverseViewProjection",(_projection*_view).inverse);
            m.SetMatrix("_ScreenView",_view);m.SetMatrix("_ScreenViewProjection",_projection*_view);
            m.SetVector("_GtaoPixelSize",new Vector4(1f/Raw.width,1f/Raw.height,Raw.width,Raw.height));
            m.SetVector("_GtaoParameters",parameters);m.SetVector("_GtaoQuality",quality);m.SetFloat("_GtaoSliceOffset",SliceOffset);
            m.DisableKeyword("SCENE_GTAO_HALF");m.DisableKeyword("SCENE_GTAO_ROTATED");
            if(RotateSamples)m.EnableKeyword("SCENE_GTAO_ROTATED");
            if(coarse!=null)
            {
                m.EnableKeyword("SCENE_GTAO_HALF");m.SetTexture("_GtaoCoarse",coarse);
                m.SetVector("_GtaoCoarseSize",new Vector4(1f/coarse.width,1f/coarse.height,coarse.width,coarse.height));m.SetVector("_GtaoReconstruction",reconstruction);
            }
            commands.BeginSample("Toolkit GTAO rotated sample and temporal resolve");
            commands.SetRenderTarget(Raw);commands.DrawMesh(quad,Matrix4x4.identity,m,0,0);RawDrawCalls++;
            m=_resolveMaterial;
            m.SetTexture("_CurrentAo",Raw);m.SetTexture("_ScreenGeometry",geometry);
            m.SetTexture("_Motion",motion.Motion);m.SetTexture("_MotionNormalIdentity",motion.PreviousNormal);
            m.SetTexture("_HistoryAo",_ao[_read]);m.SetTexture("_HistoryNormalIdentity",_normal[_read]);
            m.SetMatrix("_HistoryInverseViewProjection",(_previousProjection*_previousView).inverse);m.SetMatrix("_HistoryView",_previousView);
            m.SetMatrix("_ScreenInverseViewProjection",(_projection*_view).inverse);m.SetMatrix("_ScreenView",_view);
            m.SetVector("_TemporalSize",new Vector4(1f/Raw.width,1f/Raw.height,Raw.width,Raw.height));
            m.SetVector("_TemporalHistory",new Vector4(_continuous?1:0,_settings.historyWeight,_settings.maximumHistory,0));
            m.SetVector("_TemporalRejection",new Vector4(_settings.depthTolerance,_settings.normalThreshold,_settings.reactiveThreshold,_settings.maximumHistoryDeviation));
            m.SetVector("_TemporalClamp",new Vector4(_settings.varianceGamma,_settings.clampPadding,0,0));
            commands.SetRenderTarget(new[]{new RenderTargetIdentifier(Result),new RenderTargetIdentifier(NormalIdentity)},BuiltinRenderTextureType.None);
            commands.DrawMesh(quad,Matrix4x4.identity,m,0,1);ResolveDrawCalls++;
            commands.EndSample("Toolkit GTAO rotated sample and temporal resolve");
        }
        public void Complete()
        {
            if(!_prepared)return;
            _read=_write;_previousView=_view;_previousProjection=_projection;_historyFrame=Time.frameCount;
            HistoryAvailable=true;_nextPhase=(Phase+1)%6;_prepared=false;
        }
        public void ResetHistory(){HistoryAvailable=false;_continuous=false;_nextPhase=0;_historyFrame=-1;_prepared=false;}
        private static bool Same(float[] a,float[] b){if(b==null||a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        private static float MatrixDifference(Matrix4x4 a,Matrix4x4 b){float d=0;for(int i=0;i<16;i++)d=Mathf.Max(d,Mathf.Abs(a[i]-b[i]));return d;}
        private static bool Created(RenderTexture[] textures){foreach(var t in textures)if(t==null||!t.IsCreated())return false;return true;}
        private static RenderTexture Target(RenderTexture source,string name)
        {
            var t=new RenderTexture(source.width,source.height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){name=name,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
            t.Create();
            if(!t.IsCreated()||t.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat)
            {Release(t);throw new InvalidOperationException("Temporal GTAO requires exact RGBA32 float targets");}
            return t;
        }
        private static void Release(RenderTexture t){if(t!=null){t.Release();UnityEngine.Object.Destroy(t);}}
        private void ReleaseTargets(){Release(Raw);Raw=null;for(int i=0;i<2;i++){Release(_ao[i]);Release(_normal[i]);_ao[i]=_normal[i]=null;}}
        public void Dispose(){ReleaseTargets();ResetHistory();if(_rawMaterial!=null)UnityEngine.Object.Destroy(_rawMaterial);if(_resolveMaterial!=null)UnityEngine.Object.Destroy(_resolveMaterial);_rawMaterial=_resolveMaterial=null;RawDrawCalls=ResolveDrawCalls=0;_signature=null;}
    }
}
