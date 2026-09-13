using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Current-depth visibility, instanced authored optical elements, optional reduced-resolution HDR resolve.</summary>
    public sealed partial class LensFlareRenderer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct EmitterData { public Vector4 screen, occlusion, options, radiance; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ElementData { public Vector4 centerAxisX, axisYSource, tintSoftness, shape, atlas; }
        public readonly struct Frame
        {
            private readonly LensFlareRenderer owner;
            private readonly uint generation;
            public readonly RenderTexture color, artifacts, visibility;
            internal Frame(LensFlareRenderer value)
            { owner=value; generation=value.generation; color=value.color; artifacts=value.artifacts; visibility=value.visibility; }
            public bool IsCurrent => owner != null && owner.hasFrame && generation == owner.generation && owner.Created;
        }
        private readonly FogVolumeSettings viewSettings = new FogVolumeSettings { enabled = true };
        private readonly List<EmitterData> emitters = new List<EmitterData>();
        private readonly List<ElementData> elements = new List<ElementData>();
        private ComputeBuffer emitterBuffer, elementBuffer;
        private Material material;
        private RenderTexture color, artifacts, visibility;
        private uint generation;
        private bool hasFrame;
        public int DrawCalls { get; private set; }
        public int EmitterCount => emitters.Count;
        public int ElementCount => elements.Count;
        public int TargetCount => Created ? 3 : 0;
        public string UnavailableReason { get; private set; }
        private bool Created => color != null && color.IsCreated() && artifacts != null && artifacts.IsCreated() && visibility != null && visibility.IsCreated();
        public bool TryGetFrame(out Frame frame)
        { frame=default; if (!hasFrame || !Created) return false; frame=new Frame(this); return true; }

        public bool TryRender(RenderTexture source, FogVolumeDepth depth, Camera camera, LensFlareSettings settings,
            double timeSeconds, out Frame frame, RenderTexture protection = null)
        {
            frame=default; generation++; hasFrame=false; DrawCalls=0; UnavailableReason=null;
            if (settings == null || !settings.enabled) { Release(); return false; }
            if (double.IsNaN(timeSeconds) || double.IsInfinity(timeSeconds) || Math.Abs(timeSeconds) > 1e9)
                return Fail("Flare time must be finite and within one billion seconds");
            if (!settings.Validate(out var reason)) return Fail(reason);
            if (!Valid(source) || !Valid(depth.texture) || source.sRGB || depth.texture.sRGB || source == depth.texture || Owns(source) || Owns(depth.texture) ||
                source.width != depth.texture.width || source.height != depth.texture.height ||
                (source.format != RenderTextureFormat.ARGBFloat && source.format != RenderTextureFormat.ARGBHalf && source.format != RenderTextureFormat.RGB111110Float) ||
                (depth.encoding != FogDepthEncoding.LinearEye && depth.encoding != FogDepthEncoding.Device) ||
                (depth.encoding == FogDepthEncoding.LinearEye && depth.texture.format != RenderTextureFormat.RFloat && depth.texture.format != RenderTextureFormat.RHalf) ||
                (depth.encoding == FogDepthEncoding.Device && depth.texture.format != RenderTextureFormat.RFloat && depth.texture.format != RenderTextureFormat.Depth) ||
                (protection != null && (!Valid(protection) || protection.sRGB || Owns(protection) || protection == source || protection == depth.texture ||
                    protection.width != source.width || protection.height != source.height || (protection.format != RenderTextureFormat.R8 && protection.format != RenderTextureFormat.RFloat))))
                return Fail("Flare requires distinct matching linear HDR, depth and optional protection targets");
            if (settings.atlas != null && (settings.atlas.mipmapCount != 1 || GraphicsFormatUtility.IsSRGBFormat(settings.atlas.graphicsFormat) ||
                settings.atlas.width > 4096 || settings.atlas.height > 4096 || !SystemInfo.IsFormatSupported(settings.atlas.graphicsFormat, FormatUsage.Sample)))
                return Fail("Flare atlas requires a caller-owned linear non-mipmapped Texture2D, at most 4096 square");
            if (!FogVolumeBinding.TryCreate(viewSettings, camera, source.width, source.height, out var view, out reason)) return Fail(reason);
            var shader=Resources.Load<Shader>("LensFlare");
            if (shader == null || !shader.isSupported || !SystemInfo.supportsInstancing ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.Sample) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.Render) || !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.Sample))
                return Fail("Flare shader, instancing or float targets unavailable");
            var saved=RenderTexture.active;
            try
            {
                Prepare(camera, source.width, source.height, settings, timeSeconds);
                if (elements.Count == 0) { Release(); return false; }
                int scale=(int)settings.resolution, width=(source.width+scale-1)/scale, height=(source.height+scale-1)/scale;
                if (!Created || color.width != source.width || color.height != source.height || artifacts.width != width || artifacts.height != height || visibility.width != emitters.Count)
                {
                    ReleaseTargets();
                    color=Allocate(source.width, source.height, RenderTextureFormat.ARGBFloat, "HDR output");
                    artifacts=Allocate(width, height, RenderTextureFormat.ARGBFloat, "optical artifacts");
                    visibility=Allocate(emitters.Count, 1, RenderTextureFormat.RFloat, "source visibility");
                }
                Upload(ref emitterBuffer, emitters); Upload(ref elementBuffer, elements);
                if (material == null) material=new Material(shader) { hideFlags=HideFlags.HideAndDontSave };
                view.Apply(material);
                material.SetBuffer("_FlareEmitters", emitterBuffer); material.SetBuffer("_FlareElements", elementBuffer);
                material.SetTexture("_FlareDepth", depth.texture); material.SetTexture("_FlareVisibility", visibility);
                material.SetTexture("_FlareArtifacts", artifacts); material.SetTexture("_FlareProtection", protection);
                material.SetTexture("_FlareAtlas", settings.atlas != null ? settings.atlas : Texture2D.whiteTexture);
                material.SetVector("_FlareInput", new Vector4((int)depth.encoding, protection != null ? 1 : 0, width, height));
                Graphics.Blit(source, visibility, material, 0); DrawCalls++;
                var commands=new CommandBuffer { name="Toolkit instanced optical flare elements" };
                try
                {
                    commands.SetRenderTarget(artifacts); commands.ClearRenderTarget(false, true, Color.clear);
                    commands.SetViewport(new Rect(0, 0, width, height));
                    commands.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 6, elements.Count);
                    Graphics.ExecuteCommandBuffer(commands); DrawCalls++;
                }
                finally { commands.Release(); }
                Graphics.Blit(source, color, material, 2); DrawCalls++;
                hasFrame=true; frame=new Frame(this); return true;
            }
            catch (Exception error) { return Fail("Flare render failed: " + error.GetType().Name); }
            finally { RenderTexture.active=saved != null && saved.IsCreated() ? saved : null; }
        }

        private void Prepare(Camera camera, int width, int height, LensFlareSettings settings, double time)
        {
            emitters.Clear(); elements.Clear();
            var view=camera.worldToCameraMatrix; var projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            float aspect=width/(float)height;
            Vector2 Project(Vector3 point)
            {
                Vector4 p=projection*new Vector4(point.x, point.y, point.z, 1);
                var uv=new Vector2(p.x/p.w, p.y/p.w)*.5f+Vector2.one*.5f;
                if (SystemInfo.graphicsUVStartsAtTop) uv.y=1-uv.y;
                return uv;
            }
            foreach (var emitter in settings.emitters)
            {
                if (emitter == null || !emitter.enabled || emitter.intensity == 0) continue;
                var local=view.MultiplyPoint(emitter.position); float eye=-local.z;
                Vector2 uv=Vector2.one*.5f, radius=Vector2.zero; float gain=0;
                if (eye >= camera.nearClipPlane && eye <= camera.farClipPlane)
                {
                    uv=Project(local);
                    if (!FogVolumeSettings.Range(uv.x,-1e6f,1e6f) || !FogVolumeSettings.Range(uv.y,-1e6f,1e6f)) continue;
                    var rx=Project(local+Vector3.right*emitter.occlusionRadius)-uv;
                    var ry=Project(local+Vector3.up*emitter.occlusionRadius)-uv;
                    // Camera-facing source disk; projection skew is rejected below rather than approximated.
                    radius=new Vector2(Mathf.Abs(rx.x),Mathf.Abs(ry.y));
                    if (Mathf.Abs(rx.y)>1e-5f || Mathf.Abs(ry.x)>1e-5f || !FogVolumeSettings.Range(radius.x,0,1e6f) || !FogVolumeSettings.Range(radius.y,0,1e6f))
                        throw new InvalidOperationException("Flare requires a non-skewed projected source disk");
                    float edge=Mathf.Min(uv.x,uv.y,1-uv.x,1-uv.y);
                    gain=edge>=0 ? 1 : emitter.offscreenMargin>0 ? Mathf.Clamp01(1+edge/emitter.offscreenMargin) : 0;
                    float distance=local.magnitude;
                    gain*=Mathf.Clamp01((emitter.fadeEndDistance-distance)/(emitter.fadeEndDistance-emitter.fadeStartDistance));
                    if (emitter.directionalAttenuation)
                    {
                        Vector3 toCamera=view.inverse.MultiplyPoint(Vector3.zero)-emitter.position;
                        float cosine=Vector3.Dot(emitter.rotation.normalized*Vector3.forward,toCamera.normalized);
                        float inner=Mathf.Cos(emitter.innerAngle*Mathf.Deg2Rad*.5f), outer=Mathf.Cos(emitter.outerAngle*Mathf.Deg2Rad*.5f);
                        gain*=inner>outer ? Mathf.Clamp01((cosine-outer)/(inner-outer)) : cosine>=outer ? 1 : 0;
                    }
                    gain*=emitter.intensity*(float)(1+emitter.pulseAmplitude*Math.Sin((time*emitter.pulseFrequency%1)*2*Math.PI+emitter.pulsePhaseDegrees*Math.PI/180));
                }
                int sourceIndex=emitters.Count;
                emitters.Add(new EmitterData {
                    screen=new Vector4(uv.x,uv.y,eye,gain), occlusion=new Vector4(radius.x,radius.y,emitter.depthBias,emitter.occlusion?1:0),
                    options=new Vector4(settings.occlusionSamplesPerAxis,emitter.outsideScreenVisibility,0,0), radiance=emitter.linearRadiance
                });
                foreach (var element in emitter.elements)
                {
                    if (element == null || !element.enabled || element.intensity == 0) continue;
                    Vector2 center=uv+(Vector2.one*.5f-uv)*element.axisPosition+new Vector2(element.offset.x/aspect,element.offset.y);
                    double angle=(element.rotationDegrees+element.rotationSpeed*time)%360*Math.PI/180;
                    if (element.alignToAxis) angle+=Math.Atan2(.5f-uv.y,(.5f-uv.x)*aspect);
                    float c=(float)Math.Cos(angle), s=(float)Math.Sin(angle);
                    Vector2 size=element.halfSize*emitter.scale;
                    elements.Add(new ElementData {
                        centerAxisX=new Vector4(center.x,center.y,c*size.x/aspect,s*size.x),
                        axisYSource=new Vector4(-s*size.y/aspect,c*size.y,sourceIndex,(int)element.shape),
                        tintSoftness=new Vector4(element.linearTint.x*element.intensity,element.linearTint.y*element.intensity,element.linearTint.z*element.intensity,element.softness),
                        shape=new Vector4(element.ringRadius,element.ringWidth,element.sides,element.falloffExponent),
                        atlas=new Vector4(element.atlasRect.x,element.atlasRect.y,element.atlasRect.width,element.atlasRect.height)
                    });
                }
            }
        }
        private static void Upload<T>(ref ComputeBuffer buffer,List<T> values) where T:struct
        {
            int capacity=Mathf.NextPowerOfTwo(Mathf.Max(1,values.Count));
            if (buffer == null || buffer.count != capacity) { buffer?.Dispose(); buffer=new ComputeBuffer(capacity,Marshal.SizeOf<T>(),ComputeBufferType.Structured); }
            buffer.SetData(values);
        }
        private static bool Valid(RenderTexture t) => t!=null && t.IsCreated() && t.antiAliasing==1 && !t.useMipMap && !t.useDynamicScale && t.dimension==TextureDimension.Tex2D && t.volumeDepth==1;
        private bool Owns(RenderTexture t) => t!=null && (t==color || t==artifacts || t==visibility);
        private bool Fail(string reason) { Release(); UnavailableReason=reason; return false; }
        private static RenderTexture Allocate(int width,int height,RenderTextureFormat format,string label)
        {
            var t=new RenderTexture(width,height,0,format,RenderTextureReadWrite.Linear) { name="Toolkit flare "+label,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave };
            if (!t.Create()) { UnityEngine.Object.Destroy(t); throw new InvalidOperationException("Flare target allocation failed"); }
            return t;
        }
        private void ReleaseTargets()
        {
            if (Owns(RenderTexture.active)) RenderTexture.active=null;
            foreach (var t in new[]{color,artifacts,visibility}) if (t!=null) { t.Release(); UnityEngine.Object.Destroy(t); }
            color=artifacts=visibility=null; hasFrame=false;
        }
        private void Release()
        {
            ReleaseShared();
            ReleaseTargets(); emitterBuffer?.Dispose(); elementBuffer?.Dispose(); emitterBuffer=elementBuffer=null;
            if (material!=null) UnityEngine.Object.Destroy(material); material=null; emitters.Clear(); elements.Clear(); DrawCalls=0;
        }
        public void Dispose() { generation++; Release(); }
    }
}
