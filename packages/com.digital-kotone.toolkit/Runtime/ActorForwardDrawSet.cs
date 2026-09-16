using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Full ActorToon and supplemental passes, never reduced Planar materials.
    /// A preparation owns material snapshots. Geometry, source property blocks and texture
    /// contents remain borrowed and must not change before GPU completion.</summary>
    public static class ActorForwardDrawSet
    {
        private static readonly string[] FixedInputs=("_ShaderType _OutlineEnabled _Cull _SrcBlend _DstBlend _SrcAlphaBlend _DstAlphaBlend _ZWrite _ColorMask _StencilRef _StencilReadMask _StencilWriteMask _StencilComp _StencilPass").Split(' ');
        public sealed class Settings
        {
            public Renderer[] renderers=Array.Empty<Renderer>();
            public ActorForwardParameters parameters=new ActorForwardParameters();
            public SrpActorShadow.Frame? selfShadow;
            public bool outlines=true,hairCover=true;
            public int maximumDraws=1024;
            // Applied to owned main/supplemental materials, e.g. explicit per-renderer SH.
            // The callback must not change renderer, shader, queue or fixed render states.
            public Action<Renderer,int,Material> configureMaterial;
            // Explicit temporal classification, independent from material type.
            public Func<Renderer,int,TemporalPixelFlags> temporalFlags;
        }
        internal sealed class Draw
        { public Renderer renderer;public Material material;public int submesh,pass,queue;public float viewZ;public bool hair;public TemporalPixelFlags temporalFlags; }
        internal sealed class RendererState
        {
            public Renderer renderer;
            public bool enabled,forceOff,active,staticBatch,included;
            public int layer,vertices,submeshes;
            public Matrix4x4 transform;
            public Mesh mesh;
            public bool IsCurrent
            {
                get
                {
                    var r=renderer;
                    if(r==null||r.enabled!=enabled||r.forceRenderingOff!=forceOff||r.gameObject.activeInHierarchy!=active||
                        r.gameObject.layer!=layer||r.isPartOfStaticBatch!=staticBatch)return false;
                    if(!included)return true;
                    var current=MeshOf(r);
                    return current!=null&&current==mesh&&current.vertexCount==vertices&&current.subMeshCount==submeshes&&r.localToWorldMatrix.Equals(transform);
                }
            }
        }
        private static Mesh MeshOf(Renderer renderer)
        { var skin=renderer as SkinnedMeshRenderer;var filter=renderer.GetComponent<MeshFilter>();return skin!=null?skin.sharedMesh:filter!=null?filter.sharedMesh:null; }
        public sealed class PreparedFrame : IDisposable
        {
            private bool disposed;
            internal readonly List<Material> materials=new List<Material>();
            internal readonly List<Draw> draws=new List<Draw>();
            internal readonly HashSet<Texture> sampled=new HashSet<Texture>();
            internal readonly List<RendererState> rendererStates=new List<RendererState>();
            internal Matrix4x4 view,projection;
            internal RenderTexture target;
            internal int cullingMask,targetWidth,targetHeight;
            internal bool hadTarget,orthographic,dynamicResolution,stereo;
            internal float near,far;
            internal Rect viewport;
            public Camera Camera { get; internal set; }
            internal SrpActorShadow.Frame? selfShadow;
            public int DrawCount=>draws.Count;
            public int MaterialCount=>materials.Count;
            public bool IsValid
            {
                get
                {
                    if(disposed||(selfShadow.HasValue&&!selfShadow.Value.IsCurrent)||Camera==null||!Camera.worldToCameraMatrix.Equals(view)||!Camera.projectionMatrix.Equals(projection)||
                        Camera.cullingMask!=cullingMask||Camera.rect!=viewport||Camera.orthographic!=orthographic||Camera.nearClipPlane!=near||Camera.farClipPlane!=far||
                        Camera.allowDynamicResolution!=dynamicResolution||Camera.stereoEnabled!=stereo||Camera.targetTexture!=target||
                        (hadTarget&&(target==null||!target.IsCreated()||target.width!=targetWidth||target.height!=targetHeight)))return false;
                    foreach(var state in rendererStates)if(!state.IsCurrent)return false;
                    foreach(var d in draws)if(d.renderer==null||d.material==null||!d.renderer.enabled||d.renderer.forceRenderingOff||!d.renderer.gameObject.activeInHierarchy)return false;
                    foreach(var t in sampled)if(t==null||(t is RenderTexture rt&&!rt.IsCreated()))return false;
                    return true;
                }
            }
            public void Dispose()
            { if(disposed)return;disposed=true;foreach(var m in materials)Destroy(m);materials.Clear();draws.Clear();sampled.Clear();rendererStates.Clear(); }
        }
        public static bool TryPrepare(Camera camera,Settings settings,out PreparedFrame frame,out string error)
        {
            frame=null;error=null;
            if(camera==null||settings==null||settings.renderers==null||settings.renderers.Length>4096||settings.parameters==null||settings.maximumDraws<1||settings.maximumDraws>4096||
                !ActorForwardParameters.Finite(camera.worldToCameraMatrix)||!ActorForwardParameters.Finite(camera.projectionMatrix))
            { error="Invalid Actor Forward inputs";return false; }
            var result=new PreparedFrame { Camera=camera,selfShadow=settings.selfShadow,view=camera.worldToCameraMatrix,projection=camera.projectionMatrix,
                target=camera.targetTexture,hadTarget=camera.targetTexture!=null,cullingMask=camera.cullingMask,viewport=camera.rect,
                orthographic=camera.orthographic,near=camera.nearClipPlane,far=camera.farClipPlane,dynamicResolution=camera.allowDynamicResolution,stereo=camera.stereoEnabled };
            if(result.hadTarget){result.targetWidth=result.target.width;result.targetHeight=result.target.height;}
            try
            {
                var shader=Resources.Load<Shader>("PhotoModeFallback");var supplemental=Resources.Load<Shader>("ActorSupplemental");
                if(shader==null||!shader.isSupported||supplemental==null||!supplemental.isSupported)throw new ArgumentException("Full Actor shaders unavailable");
                var main=new List<Draw>();var bodyOutlines=new List<Draw>();var covers=new List<Draw>();var hairOutlines=new List<Draw>();
                var seen=new HashSet<int>();var rendererBlock=new MaterialPropertyBlock();var submeshBlock=new MaterialPropertyBlock();int drawCount=0;
                foreach(var renderer in settings.renderers)
                {
                    if(renderer==null)throw new ArgumentException("Null actor renderer");
                    if(!seen.Add(renderer.GetInstanceID()))throw new ArgumentException("Duplicate actor renderer");
                    bool included=renderer.enabled&&!renderer.forceRenderingOff&&renderer.gameObject.activeInHierarchy&&(camera.cullingMask&(1<<renderer.gameObject.layer))!=0;
                    var mesh=MeshOf(renderer);
                    result.rendererStates.Add(new RendererState { renderer=renderer,enabled=renderer.enabled,forceOff=renderer.forceRenderingOff,
                        active=renderer.gameObject.activeInHierarchy,layer=renderer.gameObject.layer,staticBatch=renderer.isPartOfStaticBatch,included=included,
                        mesh=mesh,vertices=mesh!=null?mesh.vertexCount:0,submeshes=mesh!=null?mesh.subMeshCount:0,transform=renderer.localToWorldMatrix });
                }
                // Capture the complete input set before callbacks: a callback for an
                // earlier renderer must not silently move a later or excluded one.
                foreach(var state in result.rendererStates)
                {
                    var renderer=state.renderer;
                    if(!state.included)continue;
                    if(!ActorForwardParameters.Finite(renderer.localToWorldMatrix))throw new ArgumentException("Invalid actor transform");
                    if(renderer.isPartOfStaticBatch)throw new ArgumentException("Static batching changes explicit actor submesh indexing");
                    renderer.GetPropertyBlock(rendererBlock);var sources=renderer.sharedMaterials;
                    for(int submesh=0;submesh<sources.Length;submesh++)
                    {
                        var source=sources[submesh];if(source==null||source.shader!=shader)throw new ArgumentException("Requires full toolkit ActorToon materials");
                        if(!SceneDepthData.ValidSurface(new SceneDepthData.Surface { renderer=renderer,materialIndex=submesh }))throw new ArgumentException("Invalid actor geometry/submesh");
                        renderer.GetPropertyBlock(submeshBlock,submesh);var block=submeshBlock.isEmpty?rendererBlock:submeshBlock;
                        float type=block.HasProperty("_ShaderType")?block.GetFloat("_ShaderType"):source.GetFloat("_ShaderType");
                        if(Array.IndexOf(new[]{0f,1f,2f,3f,4f,5f,6f,8f,9f},type)<0)throw new ArgumentException("Unsupported actor type");
                        bool outline=settings.outlines&&source.GetFloat("_OutlineEnabled")>.5f&&source.GetShaderPassEnabled("ActorOutline");
                        bool cover=settings.hairCover&&type==8&&source.GetShaderPassEnabled("ActorHairCover");
                        drawCount+=1+(outline?1:0)+(cover?1:0);
                        if(drawCount>settings.maximumDraws)throw new ArgumentException("Actor Forward draw budget exceeded");
                        Material Snapshot(Shader target)
                        {
                            var material=new Material(target) { name="Toolkit full Actor Forward snapshot",hideFlags=HideFlags.HideAndDontSave };
                            result.materials.Add(material);material.CopyPropertiesFromMaterial(source);settings.parameters.Apply(material);
                            if(settings.selfShadow.HasValue)settings.selfShadow.Value.Bind(material);
                            settings.configureMaterial?.Invoke(renderer,submesh,material);
                            if(material.shader!=target||material.renderQueue!=source.renderQueue)throw new ArgumentException("Actor configuration changed shader or queue");
                            foreach(var key in FixedInputs)if(material.GetFloat(key)!=source.GetFloat(key))throw new ArgumentException("Actor configuration changed fixed input: "+key);
                            var names=new HashSet<string>(material.GetTexturePropertyNames());names.UnionWith(ActorForwardParameters.TextureNames);
                            foreach(var key in names)
                            {
                                var texture=block.HasTexture(key)?block.GetTexture(key):material.GetTexture(key);
                                if(texture!=null)
                                {
                                    if(texture is RenderTexture rt&&(!rt.IsCreated()||rt.antiAliasing!=1||rt.memorylessMode!=RenderTextureMemoryless.None))throw new ArgumentException("Actor input must be stored and sampleable");
                                    result.sampled.Add(texture);
                                }
                            }
                            return material;
                        }
                        var temporalFlags=settings.temporalFlags?.Invoke(renderer,submesh)??TemporalPixelFlags.Normal;
                        if(((int)temporalFlags&~6)!=0)throw new ArgumentException("Unknown Actor temporal flags");
                        Draw Command(Material material,string name)=>new Draw { renderer=renderer,material=material,submesh=submesh,pass=material.FindPass(name),
                            queue=source.renderQueue,viewZ=result.view.MultiplyPoint(renderer.bounds.center).z,hair=type==8,temporalFlags=temporalFlags };
                        main.Add(Command(Snapshot(shader),"ACTOR_FORWARD_HDR"));
                        if(outline||cover)
                        {
                            var material=Snapshot(supplemental);
                            if(outline)(type==8?hairOutlines:bodyOutlines).Add(Command(material,"ACTOR_OUTLINE"));
                            if(cover)covers.Add(Command(material,"ACTOR_HAIR_COVER"));
                        }
                    }
                }
                main.Sort((a,b)=>{
                    int order=a.queue.CompareTo(b.queue);if(order!=0)return order;
                    if(a.queue>2500){order=a.viewZ.CompareTo(b.viewZ);if(order!=0)return order;}
                    order=a.renderer.GetInstanceID().CompareTo(b.renderer.GetInstanceID());return order!=0?order:a.submesh.CompareTo(b.submesh);
                });
                // Match the established Built-in boundary: all opaque main draws, body
                // outlines, depth-owning hair cover, hair outlines, then transparent main.
                foreach(var d in main)if(d.queue<=2500)result.draws.Add(d);
                result.draws.AddRange(bodyOutlines);result.draws.AddRange(covers);result.draws.AddRange(hairOutlines);
                foreach(var d in main)if(d.queue>2500)result.draws.Add(d);
                if(result.DrawCount>settings.maximumDraws)throw new ArgumentException("Actor Forward draw budget exceeded");
                foreach(var d in result.draws)if(d.pass<0)throw new ArgumentException("Full Actor pass missing");
                if(!result.IsValid)throw new ArgumentException("Actor inputs changed during preparation or target unavailable");
                frame=result;return true;
            }
            catch(Exception exception){result.Dispose();error="Actor Forward prepare failed: "+exception.Message;return false;}
        }
        private static void Destroy(UnityEngine.Object value)
        { if(value==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value); }
    }
}
