using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class SceneScreenShadowRenderer : IDisposable
    {
        public RenderTexture Geometry { get; private set; }
        public RenderTexture Visibility { get; private set; }
        public RenderTexture GtaoCoarse { get; private set; }
        public SceneGtaoTemporalRenderer Temporal { get; private set; }
        public bool IsCreated => Geometry != null && Geometry.IsCreated() && Visibility != null && Visibility.IsCreated() &&
            (!_usesHalf || (GtaoCoarse != null && GtaoCoarse.IsCreated())) && (Temporal==null||Temporal.IsCreated);
        public int TargetCount => (Geometry != null ? 1 : 0) + (Visibility != null ? 1 : 0) + (GtaoCoarse != null ? 1 : 0);
        public int CapsuleCount { get; private set; }
        public int ResolveDrawCalls { get; private set; }
        public int GtaoCoarseDrawCalls { get; private set; }
        private Material _material, _coarseMaterial;
        private readonly Vector4[] _a = new Vector4[16], _b = new Vector4[16];
        private Vector4 _parameters;
        private bool _usesGtao, _minimumAmbient, _usesHalf;
        private Vector4 _gtaoParameters, _gtaoQuality, _reconstruction;

        public bool Prepare(SceneScreenShadowSettings settings, RenderTexture target, bool hasMain, out string error)
        {
            try { return PrepareCore(settings, target, hasMain, out error); }
            catch (Exception exception) { Dispose(); error = "Screen shadow preparation failed: " + exception.GetType().Name; return false; }
        }
        private bool PrepareCore(SceneScreenShadowSettings s, RenderTexture target, bool hasMain, out string error)
        {
            error = null; CapsuleCount = ResolveDrawCalls = GtaoCoarseDrawCalls = 0; _usesGtao = _usesHalf = false;
            if (s == null || !s.enabled) { Dispose(); return true; }
            if (s.capsules == null || s.capsules.Length > 16 || s.capsuleSamples < 8 || s.capsuleSamples > 64 || !Mathf.IsPowerOfTwo(s.capsuleSamples) ||
                !Range(s.capsuleStrength, 0, 1) || !Range(s.capsuleMaxDistance, .001f, 10000) || !Range(s.capsuleNormalBias, 0, s.capsuleMaxDistance))
            { error = "Invalid screen shadow capsule configuration"; return false; }
            var g = s.gtao;
            if (g != null && g.enabled)
            {
                if (!Range(g.radius, .001f, 10000) || !Range(g.strength, 0, 1) || !Range(g.normalBias, 0, g.radius) ||
                    !Range(g.falloffStart, 0, 1) || !Range(g.thicknessBlend, 0, 1) || g.slices < 1 || g.slices > 16 ||
                    g.stepsPerSide < 2 || g.stepsPerSide > 32 || g.maxRadiusPixels < 1 || g.maxRadiusPixels > 256 ||
                    (g.combineWithCapsules != SceneAmbientCombination.Multiply && g.combineWithCapsules != SceneAmbientCombination.Minimum) ||
                    (g.resolution != SceneGtaoResolution.Full && g.resolution != SceneGtaoResolution.Half) ||
                    (g.resolution == SceneGtaoResolution.Half && (!Range(g.reconstructionDepthTolerance, .000001f, 10000) ||
                        !Range(g.reconstructionNormalThreshold, 0, .9999f))))
                { error = "Invalid GTAO configuration"; return false; }
                if(g.temporal!=null&&g.temporal.enabled&&!g.temporal.IsValid)
                {error="Invalid temporal GTAO configuration";return false;}
                _usesGtao = g.strength > 0;
                _minimumAmbient = g.combineWithCapsules == SceneAmbientCombination.Minimum;
                _gtaoParameters = new Vector4(g.radius, g.strength, g.normalBias, g.falloffStart);
                _gtaoQuality = new Vector4(g.slices, g.stepsPerSide, g.maxRadiusPixels, g.thicknessBlend);
                _usesHalf = _usesGtao && g.resolution == SceneGtaoResolution.Half;
                _reconstruction = new Vector4(g.reconstructionDepthTolerance, g.reconstructionNormalThreshold, 0, 0);
            }
            foreach (var c in s.capsules)
            {
                if (c == null) { error = "Missing capsule occluder"; return false; }
                if (!c.enabled) continue;
                if (!Range(c.radius, .001f, 10000)) { error = "Invalid capsule radius"; return false; }
                for (int i = 0; i < 3; i++) if (!Range(c.start[i], -1e6f, 1e6f) || !Range(c.end[i], -1e6f, 1e6f)) { error = "Invalid capsule endpoints"; return false; }
                _a[CapsuleCount] = new Vector4(c.start.x, c.start.y, c.start.z, c.radius);
                _b[CapsuleCount++] = new Vector4(c.end.x, c.end.y, c.end.z, 0);
            }
            if (s.capsuleStrength == 0) CapsuleCount = 0;
            if (!hasMain && CapsuleCount == 0 && !_usesGtao) { Dispose(); return true; }
            var shader = Resources.Load<Shader>("SceneScreenShadow");
            var format = GraphicsFormat.R8G8_UNorm;
            if (shader == null || !shader.isSupported || !SystemInfo.IsFormatSupported(format, FormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(format, FormatUsage.Sample) || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            { error = "Screen shadow requires RG8 render/sample and float geometry targets"; return false; }
            if (Geometry == null || !Geometry.IsCreated() || Visibility == null || !Visibility.IsCreated() ||
                Geometry.width != target.width || Geometry.height != target.height)
            {
                ReleaseTargets();
                Geometry = new RenderTexture(target.width, target.height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) {
                    name = "Toolkit screen geometry normal depth", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
                };
                var descriptor = new RenderTextureDescriptor(target.width, target.height) { graphicsFormat = format, depthBufferBits = 0, msaaSamples = 1, sRGB = false };
                Visibility = new RenderTexture(descriptor) { name = "Toolkit screen shadow and occlusion", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                if (!Geometry.Create() || !Visibility.Create() || Visibility.graphicsFormat != format) { error = "Screen shadow allocation/format failed"; return false; }
            }
            if (_usesHalf)
            {
                var coarseShader = Resources.Load<Shader>("SceneGtaoHalf");
                if (coarseShader == null || !coarseShader.isSupported ||
                    !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.Render) ||
                    !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.Sample))
                { error = "Half-resolution GTAO requires float4 render/sample and coarse shader"; return false; }
                int width = (target.width + 1) / 2, height = (target.height + 1) / 2;
                if (GtaoCoarse == null || !GtaoCoarse.IsCreated() || GtaoCoarse.width != width || GtaoCoarse.height != height)
                {
                    ReleaseCoarse();
                    GtaoCoarse = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) {
                        name = "Toolkit GTAO coarse receiver and visibility", hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
                    };
                    if (!GtaoCoarse.Create() || GtaoCoarse.graphicsFormat != GraphicsFormat.R32G32B32A32_SFloat)
                    { error = "Half-resolution GTAO allocation/format failed"; return false; }
                }
                if (_coarseMaterial == null) _coarseMaterial = new Material(coarseShader) { hideFlags = HideFlags.HideAndDontSave };
            }
            else ReleaseCoarse();
            if (_material == null) _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _parameters = new Vector4(s.capsuleSamples, s.capsuleStrength, s.capsuleMaxDistance, s.capsuleNormalBias);
            return true;
        }
        public bool PrepareTemporal(SceneGtaoSettings g,Camera camera,SceneMotionHistory motion,out string error)
        {
            error=null;
            if(!_usesGtao||g.temporal==null||!g.temporal.enabled){Temporal?.Dispose();Temporal=null;return true;}
            try
            {
                if(Temporal==null)Temporal=new SceneGtaoTemporalRenderer();
                return Temporal.Prepare(g,camera,motion,out error);
            }
            catch(Exception exception){error="Temporal GTAO preparation failed: "+exception.GetType().Name;return false;}
        }
        public void Record(CommandBuffer commands, SceneLightShadowAtlas main, Matrix4x4 view, Matrix4x4 projection, Mesh quad, SceneMotionHistory motion)
        {
            if (!IsCreated) return;
            _material.DisableKeyword("SCENE_MAIN_LIGHT_SHADOWS"); main?.BindMain(_material);
            _material.SetTexture("_ScreenGeometry", Geometry); _material.SetMatrix("_ScreenInverseViewProjection", (projection * view).inverse);
            _material.SetMatrix("_ScreenView", view); _material.SetVectorArray("_CapsuleA", _a); _material.SetVectorArray("_CapsuleB", _b);
            _material.SetInt("_CapsuleCount", CapsuleCount); _material.SetVector("_CapsuleParameters", _parameters);
            _material.DisableKeyword("SCENE_GTAO");
            _material.DisableKeyword("SCENE_GTAO_HALF");
            _material.DisableKeyword("SCENE_GTAO_HISTORY");
            if (_usesGtao)
            {
                _material.EnableKeyword("SCENE_GTAO");
                _material.SetMatrix("_ScreenViewProjection", projection * view);
                _material.SetVector("_GtaoPixelSize", new Vector4(1f / Geometry.width, 1f / Geometry.height, Geometry.width, Geometry.height));
                _material.SetVector("_GtaoParameters", _gtaoParameters); _material.SetVector("_GtaoQuality", _gtaoQuality);
                _material.SetFloat("_GtaoMinimumAmbient", _minimumAmbient ? 1 : 0);
                if (_usesHalf)
                {
                    _coarseMaterial.DisableKeyword("SCENE_GTAO_ROTATED");
                    if(Temporal!=null&&Temporal.RotateSamples){_coarseMaterial.EnableKeyword("SCENE_GTAO_ROTATED");_coarseMaterial.SetFloat("_GtaoSliceOffset",Temporal.SliceOffset);}
                    // A distinct material owns this draw's immutable recorded state.
                    _coarseMaterial.SetTexture("_ScreenGeometry", Geometry);
                    _coarseMaterial.SetMatrix("_ScreenInverseViewProjection", (projection * view).inverse);
                    _coarseMaterial.SetMatrix("_ScreenView", view); _coarseMaterial.SetMatrix("_ScreenViewProjection", projection * view);
                    _coarseMaterial.SetVector("_GtaoPixelSize", new Vector4(1f / Geometry.width, 1f / Geometry.height, Geometry.width, Geometry.height));
                    var size = new Vector4(1f / GtaoCoarse.width, 1f / GtaoCoarse.height, GtaoCoarse.width, GtaoCoarse.height);
                    _coarseMaterial.SetVector("_GtaoCoarseSize", size);
                    _coarseMaterial.SetVector("_GtaoParameters", _gtaoParameters); _coarseMaterial.SetVector("_GtaoQuality", _gtaoQuality);
                    commands.BeginSample("Toolkit half-resolution GTAO");
                    commands.SetRenderTarget(GtaoCoarse); commands.DrawMesh(quad, Matrix4x4.identity, _coarseMaterial, 0, 0);
                    commands.EndSample("Toolkit half-resolution GTAO"); GtaoCoarseDrawCalls++;
                    _material.EnableKeyword("SCENE_GTAO_HALF"); _material.SetTexture("_GtaoCoarse", GtaoCoarse);
                    _material.SetVector("_GtaoCoarseSize", size); _material.SetVector("_GtaoReconstruction", _reconstruction);
                }
                if(Temporal!=null)
                {
                    Temporal.Record(commands,Geometry,GtaoCoarse,motion,quad,_gtaoParameters,_gtaoQuality,_reconstruction);
                    _material.EnableKeyword("SCENE_GTAO_HISTORY");_material.SetTexture("_GtaoHistory",Temporal.Result);
                }
            }
            commands.BeginSample("Toolkit screen shadow and capsule AO resolve");
            commands.SetRenderTarget(Visibility); commands.DrawMesh(quad, Matrix4x4.identity, _material, 0, 0);
            commands.EndSample("Toolkit screen shadow and capsule AO resolve"); ResolveDrawCalls++;
        }
        public void Bind(Material material)
        {
            if (!IsCreated) return;
            material.DisableKeyword("SCENE_MAIN_LIGHT_SHADOWS"); material.EnableKeyword("SCENE_SCREEN_SHADOW");
            material.SetTexture("_ScreenShadowOcclusion", Visibility);
        }
        private static bool Range(float x, float low, float high) => !float.IsNaN(x) && !float.IsInfinity(x) && x >= low && x <= high;
        private void ReleaseTargets()
        {
            ReleaseCoarse();
            if (Geometry != null) { Geometry.Release(); UnityEngine.Object.Destroy(Geometry); Geometry = null; }
            if (Visibility != null) { Visibility.Release(); UnityEngine.Object.Destroy(Visibility); Visibility = null; }
        }
        private void ReleaseCoarse()
        {
            if (GtaoCoarse != null) { GtaoCoarse.Release(); UnityEngine.Object.Destroy(GtaoCoarse); GtaoCoarse = null; }
            if (_coarseMaterial != null) UnityEngine.Object.Destroy(_coarseMaterial); _coarseMaterial = null;
        }
        public void Dispose()
        {
            Temporal?.Dispose();Temporal=null;
            ReleaseTargets(); if (_material != null) UnityEngine.Object.Destroy(_material); _material = null;
            CapsuleCount = ResolveDrawCalls = GtaoCoarseDrawCalls = 0; _usesGtao = _usesHalf = false;
        }
    }
}
