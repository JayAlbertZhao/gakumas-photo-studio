using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class SceneMotionHistory : IDisposable
    {
        private sealed class Snapshot
        {
            public Mesh source;
            public int vertexCount;
            public int[] indices;
            public Texture alphaMap;
            public uint alphaRevision, revision;
            public Vector4 uvST;
            public float alpha, cutoff;
            public CullMode cull;
        }
        private sealed class Entry
        {
            public int id;
            public SceneDeferredCamera.Surface surface;
            public Snapshot previous, pending;
            public RenderTexture previousVertices, currentVertices;
            public Material material;
            public bool reusable;
        }
        public RenderTexture Motion { get; private set; }
        public RenderTexture PreviousNormal { get; private set; }
        public bool IsCreated
        {
            get
            {
                if(Motion==null||!Motion.IsCreated()||PreviousNormal==null||!PreviousNormal.IsCreated())return false;
                foreach(var entry in _active)if(!VerticesCreated(entry))return false;
                return true;
            }
        }
        public int TargetCount => (Motion != null ? 1 : 0) + (PreviousNormal != null ? 1 : 0);
        public int DrawCalls { get; private set; }
        public int TrackedVertices { get; private set; }
        public int SnapshotTargetCount => _entries.Count*2;
        public int SnapshotDrawCalls { get; private set; }
        public bool HistoryAvailable { get; private set; }
        public bool Continuous { get; private set; }
        private readonly Dictionary<(Renderer,int),Entry> _entries = new Dictionary<(Renderer,int),Entry>();
        private readonly List<Entry> _active = new List<Entry>();
        private Matrix4x4 _previousView, _previousProjection, _view, _projection;
        private int _nextId = 1;
        private bool _prepared;
        private Shader _shader;

        public bool Prepare(SceneMotionSettings settings, Camera camera, SceneDeferredCamera.Surface[] surfaces, out string error)
        {
            try { return PrepareCore(settings,camera,surfaces,out error); }
            catch(Exception exception) { Dispose(); error="Scene motion snapshot failed: "+exception.GetType().Name+": "+exception.Message; return false; }
        }
        private bool PrepareCore(SceneMotionSettings settings, Camera camera, SceneDeferredCamera.Surface[] surfaces, out string error)
        {
            error=null;DrawCalls=SnapshotDrawCalls=TrackedVertices=0;_prepared=false;_active.Clear();
            if(settings==null||!settings.enabled){Dispose();return true;}
            if(settings.maximumTrackedVertices<1||settings.maximumTrackedVertices>4000000||
                !Range(settings.cameraCutDistance,0,100000)||!Range(settings.cameraCutAngle,0,180))
            {error="Invalid scene motion configuration";return false;}
            _shader=Resources.Load<Shader>("SceneMotion");
            var format=GraphicsFormat.R32G32B32A32_SFloat;
            if(_shader==null||!_shader.isSupported||!SystemInfo.supportsGeometryShaders||
                !SystemInfo.IsFormatSupported(format,FormatUsage.Render)||!SystemInfo.IsFormatSupported(format,FormatUsage.Sample))
            {error="Scene motion requires geometry shaders and float4 MRT render/sample";return false;}
            var target=camera.targetTexture;
            if(!IsCreated||Motion.width!=target.width||Motion.height!=target.height)
            {
                ReleaseTargets();ResetHistory();
                Motion=Target(target.width,target.height,24,"Toolkit scene motion previous depth");
                PreviousNormal=Target(target.width,target.height,0,"Toolkit scene previous normal identity");
                if(!Motion.Create()||!PreviousNormal.Create()||Motion.graphicsFormat!=format||PreviousNormal.graphicsFormat!=format)
                {error="Scene motion target allocation/format failed";return false;}
            }
            _view=camera.worldToCameraMatrix;_projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);
            var inverse=_view.inverse;var previousInverse=_previousView.inverse;
            // Correspondence is to this camera's last successful render, not Unity's last
            // game-loop tick. On-demand cameras may render several times per tick or skip ticks.
            Continuous=HistoryAvailable&&
                Vector3.Distance(inverse.MultiplyPoint(Vector3.zero),previousInverse.MultiplyPoint(Vector3.zero))<=settings.cameraCutDistance&&
                Vector3.Angle(inverse.MultiplyVector(Vector3.back),previousInverse.MultiplyVector(Vector3.back))<=settings.cameraCutAngle&&
                Vector3.Angle(inverse.MultiplyVector(Vector3.up),previousInverse.MultiplyVector(Vector3.up))<=settings.cameraCutAngle;
            // Previous projection is explicitly preserved, including FOV/near/off-axis changes.
            // The consumer still must reject previous depth, normal and visibility mismatches.
            foreach(var entry in _entries.Values)entry.pending=null;
            foreach(var surface in surfaces)
            {
                var renderer=surface.renderer;
                if(!renderer.enabled||renderer.forceRenderingOff||!renderer.gameObject.activeInHierarchy)continue;
                if(renderer.isPartOfStaticBatch){error="Scene motion snapshots cannot index a statically combined renderer";return false;}
                var skin=renderer as SkinnedMeshRenderer;
                Mesh source=skin!=null?skin.sharedMesh:renderer.GetComponent<MeshFilter>().sharedMesh;
                int count=source.vertexCount;
                if(count<1||count>settings.maximumTrackedVertices-TrackedVertices)
                {error="Scene motion vertex budget exceeded";return false;}
                if(source.GetTopology(surface.materialIndex)!=MeshTopology.Triangles)
                {error="Scene motion requires triangle topology";return false;}
                var key=(renderer,surface.materialIndex);
                if(!_entries.TryGetValue(key,out var entry))
                {
                    if(_nextId>16777215){error="Scene motion identity range exhausted; disable to reset";return false;}
                    entry=new Entry{id=_nextId++,material=new Material(_shader){hideFlags=HideFlags.HideAndDontSave}};_entries.Add(key,entry);
                }
                entry.surface=surface;
                if(!source.isReadable){error="Scene motion topology validation requires a readable mesh";return false;}
                var input=surface.inputs;
                var pending=new Snapshot{source=source,vertexCount=count,indices=source.GetIndices(surface.materialIndex),
                    alphaMap=input.albedoMap,alphaRevision=input.albedoMap!=null?input.albedoMap.updateCount:0,
                    revision=surface.motionRevision,uvST=input.uvST,alpha=input.alpha,cutoff=surface.alphaCutoff,cull=surface.cull};
                entry.reusable=Continuous&&Compatible(entry.previous,pending);
                int width=Mathf.Min(Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(count*2))),Mathf.Min(SystemInfo.maxTextureSize,1024));
                int height=(count*2+width-1)/width;
                if(height>SystemInfo.maxTextureSize){error="Scene motion vertex snapshot exceeds texture limit";return false;}
                if(!VerticesCreated(entry)||entry.previousVertices.width!=width||entry.previousVertices.height!=height)
                {
                    ReleaseVertices(entry);entry.reusable=false;
                    entry.previousVertices=Target(width,height,0,"Toolkit scene previous vertex snapshot");
                    entry.currentVertices=Target(width,height,0,"Toolkit scene current vertex snapshot");
                    if(!entry.previousVertices.Create()||!entry.currentVertices.Create()||entry.previousVertices.graphicsFormat!=format||entry.currentVertices.graphicsFormat!=format)
                    {error="Scene motion vertex target allocation/format failed";return false;}
                }
                entry.pending=pending;_active.Add(entry);TrackedVertices+=count;
            }
            _prepared=true;return true;
        }
        public void Record(CommandBuffer commands)
        {
            if(!_prepared||!IsCreated)return;
            commands.BeginSample("Toolkit scene motion correspondence");
            // Save the actual DrawRenderer stream and its engine-supplied matrix. CPU BakeMesh
            // can disagree with that stream's update time, scale and root-bone basis.
            foreach(var entry in _active)
            {
                var m=entry.material;var s=entry.surface;var current=entry.currentVertices;
                m.SetVector("_VertexScale",s.vertexScale);
                m.SetVector("_VertexTextureSize",new Vector4(1f/current.width,1f/current.height,current.width,current.height));
                commands.SetRenderTarget(current);commands.ClearRenderTarget(false,true,Color.clear);
                commands.DrawRenderer(s.renderer,m,s.materialIndex,1);SnapshotDrawCalls++;
            }
            commands.SetRenderTarget(new[]{new RenderTargetIdentifier(Motion),new RenderTargetIdentifier(PreviousNormal)},Motion);
            commands.ClearRenderTarget(true,true,Color.clear);
            foreach(var entry in _active)
            {
                var m=entry.material;var s=entry.surface;
                m.SetMatrix("_ViewProjection",_projection*_view);m.SetMatrix("_PreviousViewProjection",_previousProjection*_previousView);
                m.SetMatrix("_PreviousView",_previousView);m.SetTexture("_PreviousVertices",entry.previousVertices);
                m.SetVector("_MotionSize",new Vector4(1f/Motion.width,1f/Motion.height,Motion.width,Motion.height));
                m.SetVector("_VertexScale",s.vertexScale);m.SetFloat("_HistoryValid",entry.reusable?1:0);m.SetFloat("_SurfaceIdentity",entry.id);
                m.SetFloat("_Cull",(int)s.cull);m.SetTexture("_AlphaMap",s.inputs.albedoMap!=null?s.inputs.albedoMap:Texture2D.whiteTexture);
                m.SetVector("_UvST",s.inputs.uvST);m.SetFloat("_Alpha",s.inputs.alpha);m.SetFloat("_Cutoff",s.alphaCutoff);
                commands.DrawRenderer(s.renderer,m,s.materialIndex,0);DrawCalls++;
            }
            commands.EndSample("Toolkit scene motion correspondence");
        }
        public void Complete()
        {
            if(!_prepared)return;
            var unused=new List<(Renderer,int)>();
            foreach(var pair in _entries)
            {
                var entry=pair.Value;
                if(entry.pending==null){Release(entry);unused.Add(pair.Key);}
                else
                {
                    entry.previous=entry.pending;entry.pending=null;
                    var saved=entry.previousVertices;entry.previousVertices=entry.currentVertices;entry.currentVertices=saved;
                }
            }
            foreach(var key in unused)_entries.Remove(key);
            _previousView=_view;_previousProjection=_projection;HistoryAvailable=true;_prepared=false;
        }
        public void ResetHistory(){HistoryAvailable=Continuous=false;_prepared=false;}
        private static bool Compatible(Snapshot a,Snapshot b)
        {
            if(a==null||a.source!=b.source||a.vertexCount!=b.vertexCount||a.indices.Length!=b.indices.Length||a.revision!=b.revision||
                a.alphaMap!=b.alphaMap||a.alphaRevision!=b.alphaRevision||a.uvST!=b.uvST||a.alpha!=b.alpha||a.cutoff!=b.cutoff||a.cull!=b.cull)return false;
            // RenderTexture alpha is externally mutable without a reliable CPU revision.
            if(b.alphaMap is RenderTexture&&b.cutoff>0)return false;
            for(int i=0;i<a.indices.Length;i++)if(a.indices[i]!=b.indices[i])return false;
            return true;
        }
        private static bool Range(float x,float low,float high)=>!float.IsNaN(x)&&!float.IsInfinity(x)&&x>=low&&x<=high;
        private static RenderTexture Target(int width,int height,int depth,string name)=>new RenderTexture(width,height,depth,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){name=name,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
        private void ReleaseTargets()
        {
            if(Motion!=null){Motion.Release();UnityEngine.Object.Destroy(Motion);Motion=null;}
            if(PreviousNormal!=null){PreviousNormal.Release();UnityEngine.Object.Destroy(PreviousNormal);PreviousNormal=null;}
        }
        private static bool VerticesCreated(Entry e)=>e.previousVertices!=null&&e.previousVertices.IsCreated()&&e.currentVertices!=null&&e.currentVertices.IsCreated();
        private static void ReleaseVertices(Entry e)
        {
            if(e.previousVertices!=null){e.previousVertices.Release();UnityEngine.Object.Destroy(e.previousVertices);e.previousVertices=null;}
            if(e.currentVertices!=null){e.currentVertices.Release();UnityEngine.Object.Destroy(e.currentVertices);e.currentVertices=null;}
        }
        private static void Release(Entry entry)
        {ReleaseVertices(entry);if(entry.material!=null)UnityEngine.Object.Destroy(entry.material);}
        public void Dispose()
        {
            ReleaseTargets();foreach(var entry in _entries.Values)Release(entry);_entries.Clear();_active.Clear();
            ResetHistory();DrawCalls=SnapshotDrawCalls=TrackedVertices=0;_nextId=1;
        }
    }
}
