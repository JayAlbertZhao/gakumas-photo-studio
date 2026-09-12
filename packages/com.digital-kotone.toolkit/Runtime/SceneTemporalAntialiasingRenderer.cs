using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class SceneTemporalAntialiasingRenderer : IDisposable
    {
        public RenderTexture VisibleGeometry { get; private set; }
        public RenderTexture VisibleIdentityFlags { get; private set; }
        private readonly RenderTexture[] _color=new RenderTexture[2],_geometry=new RenderTexture[2],_metadata=new RenderTexture[2];
        private readonly List<Material> _visibleMaterials=new List<Material>();
        private Material _resolve;
        private Shader _shader;
        private Vector4 _historyConfig,_rejection;
        private Vector2 _currentJitter;
        private Matrix4x4 _view,_projection,_previousView,_previousProjection;
        private Vector2 _previousJitter;
        private float[] _signature;
        private uint _lastSequence;
        private int _read,_write,_lastFrame=-1;
        private bool _history,_continuous,_hasResult;
        public int VisibilityDrawCalls { get; private set; }
        public int ResolveDrawCalls { get; private set; }
        public int TargetCount => VisibleGeometry==null?0:8;
        public bool IsCreated => Created(VisibleGeometry)&&Created(VisibleIdentityFlags)&&Created(_color)&&Created(_geometry)&&Created(_metadata);
        public bool HasResult(uint sequence)=>_hasResult&&IsCreated&&_lastSequence==sequence;
        public RenderTexture Color => _color[_read];
        public RenderTexture Geometry => _geometry[_read];
        public RenderTexture Metadata => _metadata[_read];
        public Vector2 PreparedJitter => _currentJitter;

        public bool Prepare(SceneTemporalAntialiasingSettings settings,Camera camera,SceneMotionHistory motion,SceneDeferredCamera.Surface[] surfaces,out string error)
        {
            error=null;VisibilityDrawCalls=ResolveDrawCalls=0;_hasResult=false;
            if(settings==null||!settings.IsValid){error="Invalid scene TAA configuration";return false;}
            if(motion==null||!motion.IsCreated){error="Scene TAA requires explicit scene motion";return false;}
            foreach(var s in surfaces)if((((int)s.temporalFlags)&~6)!=0){error="Invalid scene TAA surface flags";return false;}
            _historyConfig=new Vector4(0,settings.historyWeight,settings.maximumHistory,settings.varianceGamma);
            _rejection=new Vector4(settings.depthTolerance,settings.normalThreshold,settings.reactiveThreshold,0);
            _currentJitter=settings.jitterUv;_shader=Resources.Load<Shader>("SceneTemporalAntialiasing");
            var format=GraphicsFormat.R32G32B32A32_SFloat;
            if(_shader==null||!_shader.isSupported||SystemInfo.supportedRenderTargetCount<3||!SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))
            {error="Scene TAA requires three float4 render/sample attachments";return false;}
            var target=camera.targetTexture;
            if(!IsCreated||VisibleGeometry.width!=target.width||VisibleGeometry.height!=target.height)
            {
                ReleaseTargets();ResetHistory();VisibleGeometry=Target(target,"Toolkit TAA visible mesh normal depth");
                VisibleIdentityFlags=Target(target,"Toolkit TAA visible identity flags");
                for(int i=0;i<2;i++)
                {
                    _color[i]=Target(target,"Toolkit TAA history HDR alpha "+i);
                    _geometry[i]=Target(target,"Toolkit TAA history mesh normal depth "+i);
                    _metadata[i]=Target(target,"Toolkit TAA history identity age flags weight "+i);
                }
            }
            if(_resolve==null)_resolve=new Material(_shader){hideFlags=HideFlags.HideAndDontSave};
            _view=camera.worldToCameraMatrix;_projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);
            var signature=new List<float>{settings.historyWeight,settings.maximumHistory,settings.maximumFrameGap,settings.depthTolerance,settings.normalThreshold,settings.varianceGamma,settings.reactiveThreshold,
                settings.contentRevision&65535,settings.contentRevision>>16};
            foreach(var s in surfaces)signature.Add((int)s.temporalFlags);
            int gap=Time.frameCount-_lastFrame;
            _continuous=_history&&motion.Continuous&&gap>=0&&gap<=settings.maximumFrameGap&&Same(signature,_signature)&&Difference(_projection,_previousProjection)<.1f;
            _signature=signature.ToArray();return true;
        }

        public void RecordVisibility(CommandBuffer commands,SceneMotionHistory motion,SceneDeferredCamera.Surface[] surfaces)
        {
            commands.BeginSample("Toolkit TAA actual framebuffer visibility");
            commands.SetRenderTarget(new[]{new RenderTargetIdentifier(VisibleGeometry),new RenderTargetIdentifier(VisibleIdentityFlags)},BuiltinRenderTextureType.CurrentActive);
            commands.ClearRenderTarget(false,true,UnityEngine.Color.clear);
            foreach(var s in surfaces)
            {
                var r=s.renderer;if(!r.enabled||r.forceRenderingOff||!r.gameObject.activeInHierarchy)continue;
                if(_visibleMaterials.Count<=VisibilityDrawCalls)_visibleMaterials.Add(new Material(_shader){hideFlags=HideFlags.HideAndDontSave});
                var m=_visibleMaterials[VisibilityDrawCalls++];m.SetMatrix("_ViewProjection",_projection*_view);m.SetMatrix("_View",_view);
                m.SetVector("_VertexScale",s.vertexScale);m.SetTexture("_AlphaMap",s.inputs.albedoMap!=null?s.inputs.albedoMap:Texture2D.whiteTexture);
                m.SetVector("_AlphaST",s.inputs.uvST);m.SetFloat("_Alpha",s.inputs.alpha);m.SetFloat("_Cutoff",s.alphaCutoff);m.SetFloat("_Cull",(int)s.cull);
                m.SetFloat("_Flags",(int)s.temporalFlags);m.SetTexture("_MotionNormalIdentity",motion.PreviousNormal);
                m.SetVector("_Size",new Vector4(1f/VisibleGeometry.width,1f/VisibleGeometry.height,VisibleGeometry.width,VisibleGeometry.height));
                commands.DrawRenderer(r,m,s.materialIndex,0);
            }
            commands.EndSample("Toolkit TAA actual framebuffer visibility");
        }

        public bool Resolve(uint sequence,RenderTexture source,SceneMotionHistory motion,Texture flags,Mesh quad,out RenderTexture output,out string error)
        {
            output=null;error=null;
            if(!IsCreated){error="Scene TAA attachments unavailable";ResetHistory();return false;}
            if(_hasResult&&_lastSequence==sequence){error="Scene TAA already consumed this render";return false;}
            if(source==null||!source.IsCreated()||source.width!=VisibleGeometry.width||source.height!=VisibleGeometry.height||source.sRGB||source.antiAliasing!=1||source.useDynamicScale||source.dimension!=TextureDimension.Tex2D||
                (source.format!=RenderTextureFormat.ARGBHalf&&source.format!=RenderTextureFormat.ARGBFloat)||Owns(source))
            {error="Scene TAA requires a distinct matching linear HDR source";ResetHistory();return false;}
            _continuous&=sequence-_lastSequence==1;_write=1-_read;
            var m=_resolve;m.SetTexture("_CurrentColor",source);m.SetTexture("_VisibleGeometry",VisibleGeometry);m.SetTexture("_VisibleIdentityFlags",VisibleIdentityFlags);
            m.SetTexture("_Motion",motion.Motion);m.SetTexture("_MotionNormalIdentity",motion.PreviousNormal);
            m.SetTexture("_HistoryColor",_color[_read]);m.SetTexture("_HistoryGeometry",_geometry[_read]);m.SetTexture("_HistoryMetadata",_metadata[_read]);
            m.SetTexture("_ExternalFlags",flags!=null?flags:Texture2D.blackTexture);m.SetFloat("_ExternalFlagsEnabled",flags!=null?1:0);
            m.SetMatrix("_InverseViewProjection",(_projection*_view).inverse);m.SetMatrix("_View",_view);
            m.SetMatrix("_HistoryInverseViewProjection",(_previousProjection*_previousView).inverse);m.SetMatrix("_HistoryView",_previousView);
            m.SetVector("_Size",new Vector4(1f/source.width,1f/source.height,source.width,source.height));
            m.SetVector("_Jitter",new Vector4(_currentJitter.x,_currentJitter.y,_previousJitter.x,_previousJitter.y));
            _historyConfig.x=_continuous?1:0;m.SetVector("_History",_historyConfig);m.SetVector("_Rejection",_rejection);
            var commands=new CommandBuffer{name="Toolkit scene motion color temporal resolve"};
            var active=RenderTexture.active;
            try
            {
                commands.SetRenderTarget(new[]{new RenderTargetIdentifier(_color[_write]),new RenderTargetIdentifier(_geometry[_write]),new RenderTargetIdentifier(_metadata[_write])},BuiltinRenderTextureType.None);
                commands.DrawMesh(quad,Matrix4x4.identity,m,0,1);Graphics.ExecuteCommandBuffer(commands);ResolveDrawCalls++;
            }
            finally {RenderTexture.active=active;commands.Release();}
            _read=_write;_previousView=_view;_previousProjection=_projection;_previousJitter=_currentJitter;
            _lastFrame=Time.frameCount;_lastSequence=sequence;_history=_hasResult=true;output=Color;return true;
        }
        public void ResetHistory(){_history=_continuous=_hasResult=false;_lastFrame=-1;}
        private bool Owns(RenderTexture source){if(source==VisibleGeometry||source==VisibleIdentityFlags)return true;foreach(var a in new[]{_color,_geometry,_metadata})foreach(var t in a)if(source==t)return true;return false;}
        private static bool Same(List<float> a,float[] b){if(b==null||a.Count!=b.Length)return false;for(int i=0;i<a.Count;i++)if(a[i]!=b[i])return false;return true;}
        private static float Difference(Matrix4x4 a,Matrix4x4 b){float d=0;for(int i=0;i<16;i++)d=Mathf.Max(d,Mathf.Abs(a[i]-b[i]));return d;}
        private static bool Created(RenderTexture t)=>t!=null&&t.IsCreated();
        private static bool Created(RenderTexture[] a){foreach(var t in a)if(!Created(t))return false;return true;}
        private static RenderTexture Target(RenderTexture source,string name)
        {
            var t=new RenderTexture(source.width,source.height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){name=name,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};t.Create();
            if(!t.IsCreated()||t.graphicsFormat!=GraphicsFormat.R32G32B32A32_SFloat){Release(t);throw new InvalidOperationException("Scene TAA float4 allocation failed");}return t;
        }
        private static void Release(RenderTexture t){if(t!=null){t.Release();UnityEngine.Object.Destroy(t);}}
        private void ReleaseTargets(){Release(VisibleGeometry);Release(VisibleIdentityFlags);VisibleGeometry=VisibleIdentityFlags=null;foreach(var a in new[]{_color,_geometry,_metadata})for(int i=0;i<2;i++){Release(a[i]);a[i]=null;}}
        public void Dispose(){ReleaseTargets();ResetHistory();foreach(var m in _visibleMaterials)UnityEngine.Object.Destroy(m);_visibleMaterials.Clear();if(_resolve!=null)UnityEngine.Object.Destroy(_resolve);_resolve=null;VisibilityDrawCalls=ResolveDrawCalls=0;}
    }
}
