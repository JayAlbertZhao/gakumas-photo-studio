using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>GPU clip-position history for the exact explicit Actor draw stream.
    /// Internal producer; the owning compositor supplies completion/reset policy.</summary>
    internal sealed class ActorTemporalHistory : IDisposable
    {
        private sealed class Entry
        {
            public Renderer renderer;
            public Mesh mesh;
            public int submesh,pass,temporalPass,snapshotPass,id;
            public int[] indices;
            public uint indexCount,indexStart,baseVertex;
            public int vertexCount;
            public bool readable;
            public Material material;
            public RenderTexture previous,current;
            public bool history,active;
        }
        private readonly Dictionary<(Renderer,int,string),Entry> entries=new Dictionary<(Renderer,int,string),Entry>();
        private readonly List<Entry> active=new List<Entry>();
        private Matrix4x4 previousView,previousProjection;
        private ulong previousSequence;
        private uint previousRevision;
        private int nextId=1;
        public RenderTexture Motion { get; private set; }
        public RenderTexture PreviousDepth { get; private set; }
        public bool Continuous { get; private set; }
        public int SnapshotDrawCount => active.Count;
        public long NominalTextureBytes { get; private set; }
        private bool borrowedMotion;
        private bool TargetsCreated=>Motion!=null&&Motion.IsCreated()&&PreviousDepth!=null&&PreviousDepth.IsCreated()&&
            Motion.graphicsFormat==GraphicsFormat.R16G16B16A16_SFloat&&PreviousDepth.graphicsFormat==GraphicsFormat.R32_SFloat&&
            Motion.width==PreviousDepth.width&&Motion.height==PreviousDepth.height;
        public bool IsCreated
        {get {if(!TargetsCreated)return false;foreach(var e in active)if(e.previous==null||!e.previous.IsCreated()||e.current==null||!e.current.IsCreated())return false;return true;}}
        private static readonly string[] Reserved=("_ActorPreviousClip _ActorClipSize _ActorMotionSize _ActorMotionInverseProjection _ActorPreviousInverseProjection _ActorMotionHistory _ActorMotionIdentity _ActorMotionFlags").Split(' ');

        public void Prepare(Camera camera,ActorForwardDrawSet.PreparedFrame draws,SceneMotionSettings settings,
            ulong sequence,uint revision,int width,int height,int maximumMiB,bool immutableUnreadable,RenderTexture reuseMotion=null)
        {
            if(!SystemInfo.supportsGeometryShaders||SystemInfo.supportedRenderTargetCount<3||!SystemInfo.supportsSeparatedRenderTargetsBlend)
                throw new InvalidOperationException("Actor temporal requires geometry snapshots and three independent MRTs");
            if(settings.maximumTrackedVertices<1||settings.maximumTrackedVertices>4000000||camera.farClipPlane>65504||
                float.IsNaN(settings.cameraCutDistance)||float.IsInfinity(settings.cameraCutDistance)||settings.cameraCutDistance<0||
                float.IsNaN(settings.cameraCutAngle)||settings.cameraCutAngle<0||settings.cameraCutAngle>180)
                throw new InvalidOperationException("Actor temporal vertex/depth budget invalid");
            bool borrow=reuseMotion!=null;
            bool resize=!TargetsCreated||Motion.width!=width||Motion.height!=height||borrowedMotion!=borrow||(borrow&&Motion!=reuseMotion);
            // Count retained as well as active snapshots before allocating. Retired
            // draw IDs stay reserved until disable/recreate to prevent false matches.
            long bytes=(long)width*height*(borrow?4:12);
            if(!resize)foreach(var e in entries.Values)if(e.previous!=null)bytes+=(long)e.previous.width*e.previous.height*32;
            foreach(var draw in draws.draws)
            {
                var skin=draw.renderer as SkinnedMeshRenderer;var filter=draw.renderer.GetComponent<MeshFilter>();
                var mesh=skin!=null?skin.sharedMesh:filter!=null?filter.sharedMesh:null;
                if(mesh==null||mesh.vertexCount<1)throw new InvalidOperationException("Actor temporal mesh unavailable");
                int w=Mathf.Min(Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(mesh.vertexCount))),1024),h=(mesh.vertexCount+w-1)/w;
                bytes+=(long)w*h*32;
                if(!resize&&entries.TryGetValue((draw.renderer,draw.submesh,draw.material.GetPassName(draw.pass).ToUpperInvariant()),out var old)&&old.previous!=null)
                    bytes-=(long)old.previous.width*old.previous.height*32;
            }
            if(maximumMiB<1||maximumMiB>2048||bytes>(long)maximumMiB*1048576)throw new InvalidOperationException("Actor temporal owned texture budget exceeded");
            if(resize)
            {
                Dispose();borrowedMotion=borrow;Motion=borrow?reuseMotion:Target(width,height,GraphicsFormat.R16G16B16A16_SFloat,"Toolkit Actor motion depth identity Half4");
                PreviousDepth=Target(width,height,GraphicsFormat.R32_SFloat,"Toolkit Actor expected previous depth");
            }
            var view=camera.worldToCameraMatrix;var projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);
            var inverse=view.inverse;var previousInverse=previousView.inverse;
            Continuous=previousSequence!=0&&sequence==previousSequence+1&&revision==previousRevision&&
                Vector3.Distance(inverse.MultiplyPoint(Vector3.zero),previousInverse.MultiplyPoint(Vector3.zero))<=settings.cameraCutDistance&&
                Vector3.Angle(inverse.MultiplyVector(Vector3.back),previousInverse.MultiplyVector(Vector3.back))<=settings.cameraCutAngle&&
                Vector3.Angle(inverse.MultiplyVector(Vector3.up),previousInverse.MultiplyVector(Vector3.up))<=settings.cameraCutAngle;
            foreach(var entry in entries.Values)entry.active=false;
            active.Clear();int vertices=0;
            var shader=Resources.Load<Shader>("ActorTemporal");
            if(shader==null||!shader.isSupported)throw new InvalidOperationException("Actor temporal shader unavailable");
            var rendererBlock=new MaterialPropertyBlock();var submeshBlock=new MaterialPropertyBlock();
            foreach(var draw in draws.draws)
            {
                draw.renderer.GetPropertyBlock(rendererBlock);draw.renderer.GetPropertyBlock(submeshBlock,draw.submesh);
                var block=submeshBlock.isEmpty?rendererBlock:submeshBlock;
                foreach(var name in Reserved)if(block.HasProperty(name))throw new InvalidOperationException("Actor property block overrides reserved temporal input: "+name);
                string pass=draw.material.GetPassName(draw.pass).ToUpperInvariant();
                var key=(draw.renderer,draw.submesh,pass);
                var skin=draw.renderer as SkinnedMeshRenderer;
                var mesh=skin!=null?skin.sharedMesh:draw.renderer.GetComponent<MeshFilter>().sharedMesh;
                if(mesh==null||(!mesh.isReadable&&!immutableUnreadable)||mesh.GetTopology(draw.submesh)!=MeshTopology.Triangles)
                    throw new InvalidOperationException("Actor temporal requires readable triangles or explicit immutable GPU-only topology");
                vertices+=mesh.vertexCount;
                if(vertices>settings.maximumTrackedVertices)throw new InvalidOperationException("Actor temporal vertex budget exceeded");
                string stem=pass=="ACTOR_FORWARD_HDR"?"ACTOR_FORWARD":pass;
                if(!entries.TryGetValue(key,out var e))
                {
                    if(nextId>127)throw new InvalidOperationException("Actor temporal Half4 identity exhausted; disable motion for a frame or recreate after GPU completion");
                    e=new Entry { id=nextId++,renderer=draw.renderer,submesh=draw.submesh,pass=draw.pass,
                        material=new Material(shader) { hideFlags=HideFlags.HideAndDontSave } };
                    entries.Add(key,e);
                }
                e.active=true;e.material.CopyPropertiesFromMaterial(draw.material);
                e.temporalPass=e.material.FindPass(stem+"_TEMPORAL");e.snapshotPass=e.material.FindPass(stem+"_CLIP_SNAPSHOT");
                if(e.temporalPass<0||e.snapshotPass<0)throw new InvalidOperationException("Actor temporal full pass unavailable: "+pass+" temporal="+e.temporalPass+" snapshot="+e.snapshotPass);
                int[] indices=mesh.isReadable?mesh.GetIndices(draw.submesh):null;
                uint indexCount=mesh.GetIndexCount(draw.submesh),indexStart=mesh.GetIndexStart(draw.submesh),baseVertex=mesh.GetBaseVertex(draw.submesh);
                bool same=e.mesh==mesh&&e.vertexCount==mesh.vertexCount&&e.readable==mesh.isReadable&&
                    e.indexCount==indexCount&&e.indexStart==indexStart&&e.baseVertex==baseVertex&&(!mesh.isReadable||Same(e.indices,indices));
                int w=Mathf.Min(Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(mesh.vertexCount))),1024);
                int h=(mesh.vertexCount+w-1)/w;
                if(h>SystemInfo.maxTextureSize)throw new InvalidOperationException("Actor clip snapshot exceeds texture limit");
                if(e.previous==null||!e.previous.IsCreated()||e.current==null||!e.current.IsCreated()||e.previous.width!=w||e.previous.height!=h)
                {
                    Release(e.previous);Release(e.current);same=false;
                    e.previous=Target(w,h,GraphicsFormat.R32G32B32A32_SFloat,"Toolkit previous actual Actor clip vertices");
                    e.current=Target(w,h,GraphicsFormat.R32G32B32A32_SFloat,"Toolkit current actual Actor clip vertices");
                }
                e.material.SetTexture("_ActorPreviousClip",e.previous);
                e.material.SetVector("_ActorClipSize",new Vector4(w,h,0,0));
                e.material.SetVector("_ActorMotionSize",new Vector4(width,height,0,0));
                e.material.SetMatrix("_ActorMotionInverseProjection",projection.inverse);
                e.material.SetMatrix("_ActorPreviousInverseProjection",previousProjection.inverse);
                e.material.SetFloat("_ActorMotionHistory",Continuous&&same&&e.history?1:0);
                e.material.SetFloat("_ActorMotionIdentity",e.id);
                // A blended layer does not own a unique composited color. Keep its
                // exact depth/coverage, conservatively bypass temporal color reuse.
                bool blended=pass=="ACTOR_HAIR_COVER"||draw.queue>2500||
                    e.material.GetFloat("_SrcBlend")!=1||e.material.GetFloat("_DstBlend")!=0;
                e.material.SetFloat("_ActorMotionFlags",(int)draw.temporalFlags|(blended?2:0));
                e.mesh=mesh;e.indices=indices;e.vertexCount=mesh.vertexCount;e.readable=mesh.isReadable;
                e.indexCount=indexCount;e.indexStart=indexStart;e.baseVertex=baseVertex;active.Add(e);
            }
            NominalTextureBytes=(long)width*height*(borrowedMotion?4:12);
            foreach(var e in entries.Values)if(e.previous!=null)NominalTextureBytes+=(long)e.previous.width*e.previous.height*32;
            foreach(var texture in draws.sampled)
            {
                if(texture==Motion||texture==PreviousDepth)throw new InvalidOperationException("Actor material samples temporal output");
                foreach(var e in entries.Values)if(texture==e.previous||texture==e.current)throw new InvalidOperationException("Actor material samples clip snapshot storage");
            }
            // These become committed only in Complete after the owning frame succeeds.
            pendingView=view;pendingProjection=projection;pendingSequence=sequence;pendingRevision=revision;
        }
        private Matrix4x4 pendingView,pendingProjection;
        private ulong pendingSequence;
        private uint pendingRevision;
        public void RecordSnapshots(CommandBuffer commands)
        {
            commands.BeginSample("Toolkit actual Actor clip snapshots");
            foreach(var e in active)
            {
                commands.SetRenderTarget(e.current);commands.SetViewport(new Rect(0,0,e.current.width,e.current.height));
                commands.ClearRenderTarget(false,true,Color.clear);
                commands.DrawRenderer(e.renderer,e.material,e.submesh,e.snapshotPass);
            }
            commands.EndSample("Toolkit actual Actor clip snapshots");
        }
        public void RecordActors(CommandBuffer commands,RenderTexture color,RenderTexture hardwareDepth,SceneMotionHistory sceneMotion=null)
        {
            commands.SetRenderTarget(Motion);commands.ClearRenderTarget(false,true,Color.clear);
            commands.SetRenderTarget(PreviousDepth);commands.ClearRenderTarget(false,true,Color.clear);
            sceneMotion?.Record(commands);
            commands.SetRenderTarget(new[]{new RenderTargetIdentifier(color),new RenderTargetIdentifier(Motion),new RenderTargetIdentifier(PreviousDepth)},hardwareDepth);
            commands.SetViewport(new Rect(0,0,color.width,color.height));
            foreach(var e in active)commands.DrawRenderer(e.renderer,e.material,e.submesh,e.temporalPass);
        }
        public void Complete()
        {
            foreach(var e in active){var old=e.previous;e.previous=e.current;e.current=old;e.history=true;}
            foreach(var e in entries.Values)if(!e.active)e.history=false;
            previousView=pendingView;previousProjection=pendingProjection;previousSequence=pendingSequence;previousRevision=pendingRevision;
        }
        public void ResetHistory(){previousSequence=0;Continuous=false;foreach(var e in entries.Values)e.history=false;}
        private static bool Same(int[] a,int[] b){if(a==null||a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        private static RenderTexture Target(int w,int h,GraphicsFormat format,string name)
        {
            var t=new RenderTexture(new RenderTextureDescriptor(w,h,format,0)){name=name,hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
            if(t.Create()&&t.graphicsFormat==format)return t;Release(t);throw new InvalidOperationException("Exact Actor temporal format unavailable");
        }
        private static void Release(RenderTexture t){if(t==null)return;t.Release();Destroy(t);}
        private static void Destroy(UnityEngine.Object o){if(o==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(o);else UnityEngine.Object.DestroyImmediate(o);}
        public void Dispose()
        {
            if(!borrowedMotion)Release(Motion);Release(PreviousDepth);Motion=PreviousDepth=null;borrowedMotion=false;
            foreach(var e in entries.Values){Release(e.previous);Release(e.current);Destroy(e.material);}
            entries.Clear();active.Clear();nextId=1;ResetHistory();NominalTextureBytes=0;
        }
    }
}
