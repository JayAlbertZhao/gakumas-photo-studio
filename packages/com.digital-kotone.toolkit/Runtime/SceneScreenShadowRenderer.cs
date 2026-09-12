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
        public bool IsCreated => Geometry != null && Geometry.IsCreated() && Visibility != null && Visibility.IsCreated();
        public int CapsuleCount { get; private set; }
        public int ResolveDrawCalls { get; private set; }
        private Material _material;
        private readonly Vector4[] _a = new Vector4[16], _b = new Vector4[16];
        private Vector4 _parameters;
        private bool _usesGtao, _minimumAmbient;
        private Vector4 _gtaoParameters, _gtaoQuality;

        public bool Prepare(SceneScreenShadowSettings settings, RenderTexture target, bool hasMain, out string error)
        {
            try { return PrepareCore(settings, target, hasMain, out error); }
            catch (Exception exception) { Dispose(); error = "Screen shadow preparation failed: " + exception.GetType().Name; return false; }
        }
        private bool PrepareCore(SceneScreenShadowSettings s, RenderTexture target, bool hasMain, out string error)
        {
            error = null; CapsuleCount = ResolveDrawCalls = 0; _usesGtao = false;
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
                    (g.combineWithCapsules != SceneAmbientCombination.Multiply && g.combineWithCapsules != SceneAmbientCombination.Minimum))
                { error = "Invalid GTAO configuration"; return false; }
                _usesGtao = g.strength > 0;
                _minimumAmbient = g.combineWithCapsules == SceneAmbientCombination.Minimum;
                _gtaoParameters = new Vector4(g.radius, g.strength, g.normalBias, g.falloffStart);
                _gtaoQuality = new Vector4(g.slices, g.stepsPerSide, g.maxRadiusPixels, g.thicknessBlend);
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
            if (!IsCreated || Geometry.width != target.width || Geometry.height != target.height)
            {
                ReleaseTargets();
                Geometry = new RenderTexture(target.width, target.height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) {
                    name = "Toolkit screen geometry normal depth", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
                };
                var descriptor = new RenderTextureDescriptor(target.width, target.height) { graphicsFormat = format, depthBufferBits = 0, msaaSamples = 1, sRGB = false };
                Visibility = new RenderTexture(descriptor) { name = "Toolkit screen shadow and occlusion", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                if (!Geometry.Create() || !Visibility.Create() || Visibility.graphicsFormat != format) { error = "Screen shadow allocation/format failed"; return false; }
            }
            if (_material == null) _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _parameters = new Vector4(s.capsuleSamples, s.capsuleStrength, s.capsuleMaxDistance, s.capsuleNormalBias);
            return true;
        }
        public void Record(CommandBuffer commands, SceneLightShadowAtlas main, Matrix4x4 view, Matrix4x4 projection, Mesh quad)
        {
            if (!IsCreated) return;
            _material.DisableKeyword("SCENE_MAIN_LIGHT_SHADOWS"); main?.BindMain(_material);
            _material.SetTexture("_ScreenGeometry", Geometry); _material.SetMatrix("_ScreenInverseViewProjection", (projection * view).inverse);
            _material.SetMatrix("_ScreenView", view); _material.SetVectorArray("_CapsuleA", _a); _material.SetVectorArray("_CapsuleB", _b);
            _material.SetInt("_CapsuleCount", CapsuleCount); _material.SetVector("_CapsuleParameters", _parameters);
            _material.DisableKeyword("SCENE_GTAO");
            if (_usesGtao)
            {
                _material.EnableKeyword("SCENE_GTAO");
                _material.SetMatrix("_ScreenViewProjection", projection * view);
                _material.SetVector("_GtaoPixelSize", new Vector4(1f / Geometry.width, 1f / Geometry.height, Geometry.width, Geometry.height));
                _material.SetVector("_GtaoParameters", _gtaoParameters); _material.SetVector("_GtaoQuality", _gtaoQuality);
                _material.SetFloat("_GtaoMinimumAmbient", _minimumAmbient ? 1 : 0);
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
            if (Geometry != null) { Geometry.Release(); UnityEngine.Object.Destroy(Geometry); Geometry = null; }
            if (Visibility != null) { Visibility.Release(); UnityEngine.Object.Destroy(Visibility); Visibility = null; }
        }
        public void Dispose()
        {
            ReleaseTargets(); if (_material != null) UnityEngine.Object.Destroy(_material); _material = null;
            CapsuleCount = ResolveDrawCalls = 0; _usesGtao = false;
        }
    }
}
