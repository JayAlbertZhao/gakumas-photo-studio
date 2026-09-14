using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Opt-in scene material/GI/directional-light consumer of TileRenderPass.
    /// Prepared draws own their materials; the caller owns the SRP, geometry and output lifetime.</summary>
    public static class TileSceneRenderer
    {
        public sealed class Settings
        {
            public bool enabled;
            public TileRenderPass.BackendPolicy backend = TileRenderPass.BackendPolicy.RequireNative;
            public SceneDeferredCamera.Surface[] surfaces = Array.Empty<SceneDeferredCamera.Surface>();
            // Exact packed HDR final color. Alpha is implicitly one, including background.
            public RenderTexture output;
            // Optional Half4 world normal + independent coverage/group/GI metadata.
            public RenderTexture normalIdentity;
            public Color background = Color.black;
            public Vector3 lightDirection = new Vector3(0, 0, -1);
            public Vector3 lightRadiance = Vector3.one, ambientIrradiance = Vector3.one * .1f;
            public float giBaseScale = 1, directionalGiWeight;
            public float directionalDiffuseScale = 1, directionalSpecularScale = 1, directionalBacklight;
            public SceneBakedShadowChannel mainBakedShadowChannel;
            // Explicit opt-in: a real geometry prepass and existing local/current-shadow evaluation.
            public bool positionLighting;
            // Optional unmapped geometry normals / SSR eligibility, also required by material decals.
            public bool geometryDepthId;
            public float reflectionSmoothnessThreshold = .5f;
            public LayerMask reflectionExcludedLayers;
            public SceneDeferredCamera.Decal[] decals = Array.Empty<SceneDeferredCamera.Decal>();
            public SceneDecalLightSettings localLights;
            public SceneDirectionalShadowSettings mainLightShadow;
            public SceneForwardLightBackend localLightBackend = SceneForwardLightBackend.Auto;
            public bool allowLightFallback = true;
            public int maximumAttachmentMiB = 128;
        }

        /// <summary>One-shot immutable material snapshot, not a GPU-completion fence.
        /// Dispose only after all recorded work has finished using the borrowed resources.</summary>
        public sealed class PreparedFrame : IDisposable
        {
            private TileRenderPass.Plan _plan;
            private readonly TileRenderPass _renderer = new TileRenderPass();
            private readonly List<Material> _materials = new List<Material>();
            private Mesh _quad;
            private TileScenePositionResources _position;
            private bool _recorded, _disposed;
            public TileRenderPass.Submission Budget { get; private set; }
            public TileRenderPass.Submission DepthBudget => _position != null ? _position.Budget : default;
            // Owned by this frame. Consumers may read only after recording/completion; never release it.
            public RenderTexture EyeDepth => _position?.EyeDepth;
            public RenderTexture GeometryDepthId => _position?.GeometryDepthId;
            public int LocalLightCount => _position?.LocalLightCount ?? 0;
            public int ShadowMapCount => _position?.ShadowMapCount ?? 0;
            public long LightBufferBytes => _position?.LightBufferBytes ?? 0;
            public SceneForwardLightBackend LocalLightBackend => _position?.Backend ?? SceneForwardLightBackend.BruteForce;
            internal PreparedFrame() { }
            internal void Add(Material material) => _materials.Add(material);
            internal void SetPosition(TileScenePositionResources position) => _position=position;
            internal void Initialize(TileRenderPass.Plan plan, Mesh quad, TileRenderPass.Submission budget)
            { _plan = plan; _quad = quad; Budget = budget; }
            public bool TryRecord(ScriptableRenderContext context, out TileRenderPass.Submission submission, out string error)
            {
                submission = default;
                if (_disposed || _recorded) { error = "Tile scene frame disposed or already recorded"; return false; }
                if (_position != null)
                {
                    if (!TileRenderPass.Validate(_plan,out _,out error) || !_position.Validate(out error)) return false;
                    if (GraphicsSettings.currentRenderPipeline == null) { error="Tile render passes require an explicitly selected SRP host"; return false; }
                    // Once auxiliary commands begin this frame cannot be retried after a partial failure.
                    _recorded=true;
                    if (!_position.Record(context,out error)) return false;
                }
                if (!_renderer.TryRecord(context, _plan, out submission, out error)) return false;
                _recorded = true; return true;
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true; _renderer.Dispose(); _position?.Dispose(); _position=null;
                foreach (var material in _materials) DestroyOwned(material);
                _materials.Clear(); DestroyOwned(_quad); _quad = null; _plan = null;
            }
        }

        /// <summary>No render commands, camera mutation or output allocation. All input textures
        /// must remain separate from every attachment, and geometry must stay valid until completion.</summary>
        public static bool TryPrepare(Camera camera, Settings settings, out PreparedFrame frame, out string error)
        {
            frame = null; error = Validate(camera, settings);
            if (error != null) return false;
            int decalCount=TileSceneDecals.Count(settings.decals);
            var result = new PreparedFrame(); Mesh quad = null;
            try
            {
                var shader = Resources.Load<Shader>("TileScene");
                if (shader == null || !shader.isSupported) { error = "Tile scene shader unavailable"; return false; }
                var geometryShader=decalCount>0?Resources.Load<Shader>("TileSceneDecal"):shader;
                if(geometryShader==null||!geometryShader.isSupported) { error="Tile scene decal shader unavailable";return false; }
                Material Material()
                {
                    var material = new Material(shader) { name = "Toolkit tile scene snapshot", hideFlags = HideFlags.HideAndDontSave };
                    result.Add(material); return material;
                }
                Matrix4x4 view = camera.worldToCameraMatrix;
                Matrix4x4 vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * view;
                var geometry = new List<TileRenderPass.Draw>();
                foreach (var surface in settings.surfaces)
                {
                    var material = decalCount>0?new Material(geometryShader) { name="Toolkit stamped tile geometry",hideFlags=HideFlags.HideAndDontSave }:Material();
                    if(decalCount>0)result.Add(material);
                    SceneDeferredCamera.BindInputs(material, surface.inputs);
                    material.SetMatrix("_ViewProjection", vp);
                    material.SetVector("_VertexScale", surface.vertexScale);
                    material.SetInt("_Cull", (int)surface.cull); material.SetFloat("_Cutoff", surface.alphaCutoff);
                    material.SetFloat("_ReceiverGroup", surface.receiverGroup);
                    material.SetFloat("_SceneGiMode", 0);
                    if (surface.gi != null && !surface.gi.Bind(material, surface.renderer, out error)) return false;
                    SceneBakedShadowInput.Bind(material, surface.renderer, surface.bakedShadow, true);
                    geometry.Add(new TileRenderPass.Draw { renderer = surface.renderer, submesh = surface.materialIndex,
                        material = material, shaderPass = 0 });
                }
                quad = new Mesh { name = "Toolkit tile scene fullscreen", hideFlags = HideFlags.HideAndDontSave };
                quad.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) };
                quad.triangles = new[] { 0,2,1,0,3,2 }; quad.RecalculateBounds();
                var lighting = Material();
                lighting.SetMatrix("_InverseViewProjection", vp.inverse);
                lighting.SetVector("_CameraPosition", view.inverse.MultiplyPoint(Vector3.zero));
                lighting.SetVector("_CameraForward", view.inverse.MultiplyVector(Vector3.back).normalized);
                lighting.SetFloat("_Orthographic", camera.orthographic ? 1 : 0);
                lighting.SetVector("_LightDirection", settings.lightDirection.normalized);
                lighting.SetVector("_LightRadiance", settings.lightRadiance);
                lighting.SetVector("_AmbientIrradiance", settings.ambientIrradiance);
                lighting.SetVector("_DirectionalResponse", new Vector4(settings.directionalDiffuseScale, settings.directionalSpecularScale,
                    settings.directionalGiWeight, settings.directionalBacklight));
                lighting.SetFloat("_GiBaseScale", settings.giBaseScale);
                lighting.SetFloat("_MainBakedChannel", (int)settings.mainBakedShadowChannel);
                var resolve = Material(); resolve.SetColor("_Background", settings.background);
                TileScenePositionResources position=null;
                if(settings.positionLighting || settings.geometryDepthId || decalCount>0)
                {
                    position=new TileScenePositionResources(); result.SetPosition(position);
                    if(!position.Prepare(camera,settings,out error))return false;
                    if(settings.positionLighting)lighting=position.Lighting;
                }
                if(decalCount>0)geometry.AddRange(TileSceneDecals.Prepare(settings.decals,position.EyeDepth,position.GeometryDepthId,
                    view,vp,quad,result));
                var plan = new TileRenderPass.Plan { enabled = true, backend = settings.backend,
                    width = settings.output.width, height = settings.output.height, depthAttachment = 5,
                    maximumAttachmentMiB = settings.maximumAttachmentMiB, maximumDraws = 4098+3*decalCount,
                    attachments = new[] {
                        new TileRenderPass.Attachment { name="Tile scene base / mask R", format=GraphicsFormat.R8G8B8A8_SRGB },
                        new TileRenderPass.Attachment { name="Tile scene MOS / mask GBA332", format=GraphicsFormat.R8G8B8A8_UNorm },
                        new TileRenderPass.Attachment { name="Tile scene normal / identity", format=GraphicsFormat.R16G16B16A16_SFloat,
                            target=settings.normalIdentity, store=settings.normalIdentity!=null },
                        new TileRenderPass.Attachment { name="Tile scene emission / lighting", format=GraphicsFormat.B10G11R11_UFloatPack32 },
                        new TileRenderPass.Attachment { name="Tile scene GI / final color", format=GraphicsFormat.B10G11R11_UFloatPack32,
                            target=settings.output, store=true },
                        new TileRenderPass.Attachment { name="Tile scene depth", format=decalCount>0?GraphicsFormat.D32_SFloat_S8_UInt:GraphicsFormat.D32_SFloat }
                    }, subpasses = new[] {
                        new TileRenderPass.Subpass { name="Tile scene material and baked inputs", colors=new[]{0,1,2,3,4}, draws=geometry.ToArray() },
                        new TileRenderPass.Subpass { name="Tile scene directional PBR", colors=new[]{3}, inputs=new[]{0,1,2,4}, depthReadOnly=true,
                            draws=new[]{new TileRenderPass.Draw { mesh=quad,material=lighting,shaderPass=1 }} },
                        new TileRenderPass.Subpass { name="Tile scene GI attachment reuse", colors=new[]{4}, inputs=new[]{3,2}, depthReadOnly=true,
                            draws=new[]{new TileRenderPass.Draw { mesh=quad,material=resolve,shaderPass=2 }} }
                    }
                };
                if (!TileRenderPass.Validate(plan, out var budget, out error)) return false;
                if(position!=null && budget.nominalBytes+position.Budget.nominalBytes>(long)settings.maximumAttachmentMiB*1048576)
                { error="Tile scene combined main and depth attachment budget exceeded"; return false; }
                result.Initialize(plan, quad, budget); quad = null; frame = result; return true;
            }
            catch (Exception exception) { error = "Tile scene preparation failed: " + exception.GetType().Name; return false; }
            finally { if (frame == null) result.Dispose(); DestroyOwned(quad); }
        }

        private static string Validate(Camera camera, Settings s)
        {
            if (s == null || !s.enabled) return "Tile scene disabled";
            string decalError=TileSceneDecals.Validate(s.decals);
            if(decalError!=null)return decalError;
            int decalCount=TileSceneDecals.Count(s.decals);
            bool depthId=s.geometryDepthId||decalCount>0,needsPosition=s.positionLighting||depthId;
            if(depthId && !Range(s.reflectionSmoothnessThreshold,1))return "Invalid geometry DepthID smoothness threshold";
            if(decalCount>0 && (!SystemInfo.supportsSeparatedRenderTargetsBlend ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat_S8_UInt,FormatUsage.Render)))
                return "Tile decals require independent MRT blending and depth/stencil support";
            if(decalCount>0)
                foreach(var format in new[]{GraphicsFormat.R8G8B8A8_SRGB,GraphicsFormat.R8G8B8A8_UNorm,
                    GraphicsFormat.R16G16B16A16_SFloat,GraphicsFormat.B10G11R11_UFloatPack32})
                    if(!SystemInfo.IsFormatSupported(format,FormatUsage.Blend))return "Tile decal attachment does not support blending";
            // Keep the consumer's platform contract limited to its tested desktop paths.
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                return "Tile scene currently supports desktop Vulkan or explicit D3D11 emulation";
            if (QualitySettings.activeColorSpace != ColorSpace.Linear || camera == null || camera.stereoEnabled ||
                camera.allowDynamicResolution || camera.rect != new Rect(0,0,1,1) ||
                !SceneDeferredCamera.Matrix(camera.worldToCameraMatrix) || !SceneDeferredCamera.Matrix(camera.projectionMatrix))
                return "Tile scene requires finite full-viewport Linear camera without XR/dynamic resolution";
            var inverse=(GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)*camera.worldToCameraMatrix).inverse;
            for(int y=-1;y<=1;y+=2)for(int x=-1;x<=1;x+=2)
            {
                var endpoint=inverse*new Vector4(x,y,0,1);
                if(!Finite(endpoint.w)||Mathf.Abs(endpoint.w)<1e-8f||!Finite(new Vector3(endpoint.x,endpoint.y,endpoint.z)/endpoint.w))
                    return "Tile scene requires finite camera-ray endpoints";
                if(needsPosition)
                {
                    var near=inverse*new Vector4(x,y,1,1);
                    if(!Finite(near.w)||Mathf.Abs(near.w)<1e-8f||!Finite(new Vector3(near.x,near.y,near.z)/near.w))
                        return "Tile position requires finite near endpoints";
                    var a=camera.worldToCameraMatrix*(endpoint/endpoint.w);var b=camera.worldToCameraMatrix*(near/near.w);
                    if(!Finite(b.z)||Mathf.Abs(a.z-b.z)<1e-6f)return "Tile position requires distinct finite eye depths";
                }
            }
            if(!s.positionLighting && ((s.localLights!=null&&s.localLights.enabled) || (s.mainLightShadow!=null&&s.mainLightShadow.enabled)))
                return "Local lights and current shadows require explicit positionLighting";
            if (s.output == null || s.output.graphicsFormat != GraphicsFormat.B10G11R11_UFloatPack32 ||
                s.surfaces == null || s.surfaces.Length < 1 || s.surfaces.Length > 4096)
                return "Tile scene requires packed HDR output and 1..4096 explicit surfaces";
            if(needsPosition && (s.maximumAttachmentMiB<1 || s.maximumAttachmentMiB>512 ||
                (long)s.output.width*s.output.height*(36+(depthId?4:0)+(decalCount>0?4:0))>(long)s.maximumAttachmentMiB*1048576))
                return "Tile scene combined main and depth attachment budget exceeded";
            if (!Positive(s.lightRadiance) || !Positive(s.ambientIrradiance) || !Finite(s.lightDirection) ||
                s.lightDirection.sqrMagnitude < 1e-8f || !Finite(s.lightDirection.sqrMagnitude) ||
                !Range(s.giBaseScale,4) || !Range(s.directionalGiWeight,1) || !Range(s.directionalDiffuseScale,4) ||
                !Range(s.directionalSpecularScale,4) || !Range(s.directionalBacklight,4) ||
                !Positive(new Vector3(s.background.r,s.background.g,s.background.b)) || !Range(s.background.a,1) ||
                !SceneBakedShadowInput.ChannelValid(s.mainBakedShadowChannel)) return "Invalid tile scene lighting";
            var seen = new HashSet<(Renderer,int)>();
            foreach (var surface in s.surfaces)
            {
                if (surface == null || surface.renderer == null || !SceneDeferredCamera.Inputs(surface.inputs) ||
                    !Range(surface.alphaCutoff,1) || !Finite(surface.vertexScale) ||
                    Mathf.Abs(surface.vertexScale.x*surface.vertexScale.y*surface.vertexScale.z)<1e-8f ||
                    !Finite(surface.vertexScale.x*surface.vertexScale.y*surface.vertexScale.z) ||
                    surface.receiverGroup<0 || surface.receiverGroup>255 || (int)surface.cull<0 || (int)surface.cull>2)
                    return "Invalid tile scene surface";
                if (surface.leaf != null && surface.leaf.enabled) return "Tile scene leaf transmission is not integrated";
                var renderer=surface.renderer; var skin=renderer as SkinnedMeshRenderer; var filter=renderer.GetComponent<MeshFilter>();
                var mesh=skin!=null?skin.sharedMesh:renderer is MeshRenderer&&filter!=null?filter.sharedMesh:null;
                if (mesh==null || surface.materialIndex<0 || surface.materialIndex>=mesh.subMeshCount ||
                    surface.materialIndex>=renderer.sharedMaterials.Length || !seen.Add((renderer,surface.materialIndex)) ||
                    !mesh.HasVertexAttribute(VertexAttribute.Normal) || !mesh.HasVertexAttribute(VertexAttribute.TexCoord0) ||
                    (surface.inputs.normalMap!=null&&!mesh.HasVertexAttribute(VertexAttribute.Tangent))) return "Invalid tile scene geometry";
                if (surface.gi!=null && !surface.gi.Validate(renderer,mesh,out var giError)) return giError;
                if (surface.bakedShadow!=null && !surface.bakedShadow.Validate(renderer,mesh,out var shadowError)) return shadowError;
                foreach (var texture in new[]{surface.inputs.albedoMap,surface.inputs.normalMap,surface.inputs.mosMap,surface.inputs.emissionMap})
                    if (texture!=null && (texture.dimension!=TextureDimension.Tex2D ||
                        (texture is RenderTexture rt && (!rt.IsCreated() || rt.antiAliasing!=1)))) return "Invalid tile scene material texture";
            }
            return null;
        }
        private static bool Finite(float x) => !float.IsNaN(x)&&!float.IsInfinity(x);
        private static bool Finite(Vector3 x) => Finite(x.x)&&Finite(x.y)&&Finite(x.z);
        private static bool Range(float x,float max) => Finite(x)&&x>=0&&x<=max;
        private static bool Positive(Vector3 x) => Range(x.x,65504)&&Range(x.y,65504)&&Range(x.z,65504);
        private static void DestroyOwned(UnityEngine.Object value)
        { if(value==null)return; if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value); }
    }
}
