using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>GPU geometry-only shutter quadrature; opaque color/depth stay at
    /// the current time. This is not full-scene temporal or aperture supersampling.</summary>
    [Serializable]
    public sealed class FxGeometryExposureSettings
    {
        public bool enabled;
        // Requires an explicit unmixed opaque motion/depth endpoint input.
        public bool reprojectOpaque;
        // Optional current-visible forward ownership; requires uint compute targets.
        public bool forwardOpaqueOwnership;
        public float opaqueDepthTolerance=.02f;
        public int samples=8;
        public float shutterAngle=180;
        public float maximumSampleInterval=.25f;
        public int maximumTrackedVertices=1000000;
        public float cameraCutDistance=1,cameraCutAngle=30;
        public bool IsValid => (samples==2||samples==4||samples==8||samples==16||samples==32)&&
            MotionBlurSettings.Range(shutterAngle,0,360)&&MotionBlurSettings.Range(maximumSampleInterval,.001f,10)&&
            MotionBlurSettings.Range(opaqueDepthTolerance,.000001f,10000)&&
            maximumTrackedVertices>0&&maximumTrackedVertices<=4000000&&
            MotionBlurSettings.Range(cameraCutDistance,0,100000)&&MotionBlurSettings.Range(cameraCutAngle,0,180);
    }

    // Actual GPU DrawRenderer/DrawMesh endpoint snapshots, not CPU BakeMesh.
    // Every surface keeps its own endpoints, including overlapping alpha layers.
    internal sealed class FxGeometryExposure : IDisposable
    {
        private sealed class State
        {
            public Mesh mesh;public Renderer renderer;public int submesh,count;public int[] indices;
            public Texture texture;public uint textureRevision,revision;
            public Vector4 uv;public Vector3 radiance;public float opacity,softness,intersection;
            public bool vertexColor;public FxBlend blend;public CullMode cull;
        }
        private sealed class Entry
        {
            public LowResolutionFxSurface surface;public State previous,pending;
            public RenderTexture oldVertices,newVertices;public Material snapshot;
            public bool continuous;
        }
        private readonly Dictionary<LowResolutionFxSurface,Entry> entries=new Dictionary<LowResolutionFxSurface,Entry>();
        private readonly List<Entry> active=new List<Entry>();
        private Matrix4x4 previousView,previousProjection,view,projection;
        private Camera previousCamera,camera;
        private double previousTime,time;
        private int previousWidth,previousHeight,width,height;
        private bool clock;
        public int Samples {get;private set;}=1;
        public float HalfDisplacementScale {get;private set;}
        public float SampleInterval {get;private set;}
        public int SnapshotDrawCalls {get;private set;}
        public long TextureBytes {get;private set;}
        public int TargetCount=>entries.Count*2;
        public int TrackedVertices {get;private set;}
        public bool IsCreated
        {get {foreach(var e in active)if(!Created(e))return false;return true;}}
        public bool Owns(RenderTexture t)
        {foreach(var e in entries.Values)if(t==e.oldVertices||t==e.newVertices)return true;return false;}
        private static bool Created(Entry e)=>e.oldVertices!=null&&e.oldVertices.IsCreated()&&e.newVertices!=null&&e.newVertices.IsCreated();
        public bool Prepare(FxGeometryExposureSettings settings,Camera currentCamera,IList<LowResolutionFxSurface> surfaces,
            double seconds,int w,int h,long remainingBytes,out string error)
        {
            error=null;Samples=1;HalfDisplacementScale=0;SampleInterval=0;SnapshotDrawCalls=TrackedVertices=0;active.Clear();
            if(settings==null||!settings.enabled){Dispose();return true;}
            if(!settings.IsValid||currentCamera==null||double.IsNaN(seconds)||double.IsInfinity(seconds))
            {error="Invalid explicit FX exposure settings, camera or time";return false;}
            if(!SystemInfo.supportsGeometryShaders){error="FX exposure endpoint snapshots require geometry shaders";return false;}
            camera=currentCamera;time=seconds;width=w;height=h;
            view=camera.worldToCameraMatrix;projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);
            double interval=time-previousTime;
            bool continuous=clock&&camera==previousCamera&&w==previousWidth&&h==previousHeight&&
                interval>=.000001&&interval<=settings.maximumSampleInterval&&SameProjection(projection,previousProjection)&&
                Vector3.Distance(view.inverse.MultiplyPoint(Vector3.zero),previousView.inverse.MultiplyPoint(Vector3.zero))<=settings.cameraCutDistance&&
                Vector3.Angle(view.inverse.MultiplyVector(Vector3.forward),previousView.inverse.MultiplyVector(Vector3.forward))<=settings.cameraCutAngle&&
                Vector3.Angle(view.inverse.MultiplyVector(Vector3.up),previousView.inverse.MultiplyVector(Vector3.up))<=settings.cameraCutAngle;
            var present=new HashSet<LowResolutionFxSurface>();long wanted=0;
            SampleInterval=continuous?(float)interval:0;
            foreach(var s in surfaces)
            {
                if(!present.Add(s)){error="FX exposure requires unique surface instances";return false;}
                if(s.blend==FxBlend.Distortion||FxForwardLightingBinding.Lit(s))
                {error="FX exposure currently supports unlit alpha/additive surfaces without distortion";return false;}
                if(s.renderer!=null&&(s.renderer.isPartOfStaticBatch||s.renderer.HasPropertyBlock()))
                {error="FX exposure cannot index static batches or override renderer property blocks";return false;}
                var mesh=s.mesh!=null?s.mesh:s.renderer is SkinnedMeshRenderer skin?skin.sharedMesh:s.renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if(mesh==null||!mesh.isReadable||mesh.vertexCount<1||mesh.vertexCount>settings.maximumTrackedVertices-TrackedVertices)
                {error="FX exposure needs readable triangle topology within its tracked-vertex budget";return false;}
                var state=new State {mesh=mesh,renderer=s.renderer,submesh=s.submesh,count=mesh.vertexCount,indices=mesh.GetIndices(s.submesh),
                    texture=s.texture,textureRevision=s.texture!=null?s.texture.updateCount:0,revision=s.motionRevision,uv=s.textureST,
                    radiance=s.linearRadiance,opacity=s.opacity,softness=s.radialSoftness,intersection=s.softIntersectionDistance,
                    vertexColor=s.vertexColor,blend=s.blend,cull=s.cull};
                int tw=Mathf.Min(1024,Mathf.Min(SystemInfo.maxTextureSize,Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(mesh.vertexCount)))));
                int th=(mesh.vertexCount+tw-1)/tw;
                wanted+=(long)tw*th*32;
                if(th>SystemInfo.maxTextureSize||wanted>remainingBytes){error="FX exposure endpoint texture budget exceeded";return false;}
                if(!entries.TryGetValue(s,out var entry))
                {entry=new Entry {surface=s};entries.Add(s,entry);}
                entry.continuous=continuous&&Compatible(entry.previous,state)&&Created(entry);
                if(!Created(entry)||entry.oldVertices.width!=tw||entry.oldVertices.height!=th)
                {
                    Release(entry);entry.oldVertices=Target(tw,th,"previous");entry.newVertices=Target(tw,th,"current");
                    var shader=Resources.Load<Shader>("FxExposure");
                    if(shader==null||!shader.isSupported){error="FX endpoint snapshot shader unavailable";return false;}
                    entry.snapshot=new Material(shader){hideFlags=HideFlags.HideAndDontSave};entry.continuous=false;
                }
                entry.pending=state;active.Add(entry);TrackedVertices+=mesh.vertexCount;
            }
            var removed=new List<LowResolutionFxSurface>();foreach(var pair in entries)if(!present.Contains(pair.Key)){Release(pair.Value);removed.Add(pair.Key);}
            foreach(var key in removed)entries.Remove(key);
            TextureBytes=wanted;
            bool moving=settings.reprojectOpaque&&continuous;foreach(var e in active)moving|=e.continuous;
            if(moving&&settings.shutterAngle>0){Samples=settings.samples;HalfDisplacementScale=settings.shutterAngle/720;}
            return true;
        }
        public void Capture()
        {
            foreach(var e in active)
            {
                var t=e.newVertices;var m=e.snapshot;
                m.SetVector("_VertexTextureSize",new Vector4(1f/t.width,1f/t.height,t.width,t.height));
                using var commands=new CommandBuffer {name="Toolkit FX actual geometry shutter endpoint"};
                commands.SetRenderTarget(t);commands.SetViewport(new Rect(0,0,t.width,t.height));commands.ClearRenderTarget(false,true,Color.clear);
                if(e.surface.renderer!=null)commands.DrawRenderer(e.surface.renderer,m,e.surface.submesh,2);
                else commands.DrawMesh(e.surface.mesh,e.surface.localToWorld,m,e.surface.submesh,2);
                Graphics.ExecuteCommandBuffer(commands);SnapshotDrawCalls++;
            }
        }
        public void Bind(Material material,LowResolutionFxSurface surface,float phase)
        {
            if(phase==0||!entries.TryGetValue(surface,out var e)||!e.continuous)
            {material.DisableKeyword("FX_GEOMETRY_EXPOSURE");return;}
            material.EnableKeyword("FX_GEOMETRY_EXPOSURE");material.SetTexture("_FxExposurePreviousVertices",e.oldVertices);
            material.SetMatrix("_FxExposurePreviousViewProjection",previousProjection*previousView);
            material.SetMatrix("_FxExposurePreviousView",previousView);
            material.SetVector("_FxExposureSnapshot",new Vector4(e.oldVertices.width,phase,0,0));
        }
        public void Complete()
        {
            foreach(var e in active){var swap=e.oldVertices;e.oldVertices=e.newVertices;e.newVertices=swap;e.previous=e.pending;}
            previousView=view;previousProjection=projection;previousCamera=camera;previousTime=time;previousWidth=width;previousHeight=height;clock=true;
        }
        public void ResetHistory(){clock=false;Samples=1;HalfDisplacementScale=SampleInterval=0;foreach(var e in entries.Values)e.continuous=false;}
        private static bool SameProjection(Matrix4x4 a,Matrix4x4 b)
        {for(int i=0;i<16;i++)if(a[i]!=b[i])return false;return true;}
        private static bool Compatible(State a,State b)
        {
            if(a==null||a.mesh!=b.mesh||a.renderer!=b.renderer||a.submesh!=b.submesh||a.count!=b.count||a.texture!=b.texture||a.textureRevision!=b.textureRevision||
                a.revision!=b.revision||a.uv!=b.uv||a.radiance!=b.radiance||a.opacity!=b.opacity||a.softness!=b.softness||a.intersection!=b.intersection||
                a.vertexColor!=b.vertexColor||a.blend!=b.blend||a.cull!=b.cull||a.indices.Length!=b.indices.Length)return false;
            for(int i=0;i<a.indices.Length;i++)if(a.indices[i]!=b.indices[i])return false;return true;
        }
        private static RenderTexture Target(int w,int h,string label)
        {
            var t=new RenderTexture(new RenderTextureDescriptor(w,h,GraphicsFormat.R32G32B32A32_SFloat,0))
                {name="Toolkit FX "+label+" actual vertex snapshot",filterMode=FilterMode.Point,hideFlags=HideFlags.HideAndDontSave};
            if(t.Create()&&t.graphicsFormat==GraphicsFormat.R32G32B32A32_SFloat)return t;
            t.Release();UnityEngine.Object.Destroy(t);throw new InvalidOperationException("FX exposure snapshot allocation failed");
        }
        private static void Release(Entry e)
        {foreach(var t in new[]{e.oldVertices,e.newVertices})if(t!=null){t.Release();UnityEngine.Object.Destroy(t);}if(e.snapshot!=null)UnityEngine.Object.Destroy(e.snapshot);e.oldVertices=e.newVertices=null;e.snapshot=null;}
        public void Dispose(){foreach(var e in entries.Values)Release(e);entries.Clear();active.Clear();ResetHistory();TextureBytes=0;TrackedVertices=SnapshotDrawCalls=0;}
    }
}
