using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    internal sealed class SceneDecalLightRenderer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LightData
        {
            public Vector4 positionRange, axisXLength, axisYWidth, axisZHeight;
            public Vector4 radianceShape, uv, parameters, response, clipRect;
        }
        private readonly List<LightData> _data = new List<LightData>();
        private readonly List<SceneDecalLight> _visible = new List<SceneDecalLight>();
        private readonly SceneLightShadowAtlas _shadows = new SceneLightShadowAtlas();
        public RenderTexture ShadowAtlas => _shadows.Atlas;
        public int ShadowMapCount => _shadows.MapCount;
        public int ShadowCasterDrawCalls => _shadows.CasterDrawCalls;
        private Material _material;
        private ComputeBuffer _buffer;
        public RenderTexture Accumulation { get; private set; }
        private Texture _atlas;
        private int _batchSize;
        public int SubmittedLights => _data.Count;
        public int CulledLights { get; private set; }
        public int DrawCalls { get; private set; }
        public int BufferCapacity => _buffer == null ? 0 : _buffer.count;
        public SceneDecalLightBackend Backend { get; private set; }
        public string FallbackReason { get; private set; }

        public bool Prepare(Camera camera, SceneDecalLightSettings settings, out string error)
        {
            try { return PrepareCore(camera, settings, out error); }
            catch (Exception exception) { Dispose(); error = "Decal-light preparation failed: " + exception.GetType().Name; return false; }
        }

        private bool PrepareCore(Camera camera, SceneDecalLightSettings settings, out string error)
        {
            error = null; _data.Clear(); _visible.Clear(); CulledLights = DrawCalls = 0; FallbackReason = null;
            if (settings == null || !settings.enabled) { Dispose(); return true; }
            if (settings.lights == null || settings.lights.Length > 4096 || settings.batchSize < 1 || settings.batchSize > 1024 ||
                (int)settings.backend < 0 || (int)settings.backend > 2)
            { error = "Invalid decal-light collection, backend or batch size"; return false; }
            _batchSize = settings.batchSize;
            var view = camera.worldToCameraMatrix; var vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * view;
            foreach (var light in settings.lights)
            {
                if (light == null || !light.enabled) continue;
                if (!Valid(light)) { error = "Invalid decal-light inputs"; return false; }
                error = SceneLightShadowAtlas.ValidateLight(light); if (error != null) return false;
                if (light.radiance == Vector3.zero || (light.diffuseScale == 0 && light.specularScale == 0)) continue;
                var rotation = light.rotation.normalized;
                Vector3 x = rotation * Vector3.right, y = rotation * Vector3.up, z = rotation * Vector3.forward;
                Vector3 size = light.shape == SceneDecalLightShape.Area ? new Vector3(light.halfSize.x + light.range * light.areaSpread.x,
                    light.halfSize.y + light.range * light.areaSpread.y, light.range * .5f) :
                    new Vector3(light.range + (light.shape == SceneDecalLightShape.Capsule ? light.halfLength : 0), light.range, light.range);
                Vector3 center = light.position + (light.shape == SceneDecalLightShape.Area ? z * light.range * .5f : Vector3.zero);
                bool spot = light.shape == SceneDecalLightShape.Spot;
                if (spot)
                {
                    // Cone clipped by a radial sphere. This oriented box contains the
                    // entire spherical sector, including the tip and grazing rays.
                    float radius = light.range * Mathf.Sin(light.spotOuterAngle * Mathf.Deg2Rad * .5f);
                    size = new Vector3(radius, radius, light.range * .5f); center = light.position + z * light.range * .5f;
                }
                if (!ClipRectangle(center, x, y, z, size, vp, out var rect)) { CulledLights++; continue; }
                _visible.Add(light);
                _data.Add(new LightData {
                    positionRange = new Vector4(light.position.x, light.position.y, light.position.z, light.range),
                    axisXLength = new Vector4(x.x, x.y, x.z, light.halfLength), axisYWidth = new Vector4(y.x, y.y, y.z, light.halfSize.x),
                    axisZHeight = new Vector4(z.x, z.y, z.z, light.halfSize.y), radianceShape = new Vector4(light.radiance.x, light.radiance.y, light.radiance.z, (int)light.shape),
                    uv = light.monitorUV, parameters = new Vector4(
                        spot ? Mathf.Cos(light.spotInnerAngle * Mathf.Deg2Rad * .5f) : light.areaSpread.x,
                        spot ? Mathf.Cos(light.spotOuterAngle * Mathf.Deg2Rad * .5f) : light.areaSpread.y,
                        light.receiverGroup, light.falloffExponent),
                    response = new Vector4(light.diffuseScale, light.specularScale, light.giWeight, light.backlightScale), clipRect = rect
                });
            }
            if (_data.Count == 0) { ReleaseGpu(); return true; }
            if (settings.monitor != null)
            {
                if (!settings.monitor.TryGetFrame(out var frame)) { error = "Decal lights require a current published monitor frame"; return false; }
                _atlas = frame.texture;
            }
            else _atlas = settings.atlas != null ? settings.atlas : Texture2D.whiteTexture;
            if (_atlas.dimension != TextureDimension.Tex2D || (_atlas is RenderTexture rt && (!rt.IsCreated() || rt.antiAliasing != 1)))
            { error = "Decal-light atlas requires a readable 2D non-MSAA GPU texture"; return false; }
            var instanced = Resources.Load<Shader>("SceneDecalLightInstanced");
            bool capable = SystemInfo.supportsInstancing && SystemInfo.graphicsShaderLevel >= 45 && instanced != null && instanced.isSupported;
            var selected = settings.backend;
            if (selected == SceneDecalLightBackend.Auto) selected = capable ? SceneDecalLightBackend.Instanced : SceneDecalLightBackend.Scalar;
            if (selected == SceneDecalLightBackend.Instanced && !capable)
            {
                if (!settings.allowInstancingFallback) { error = "Instanced decal lights unavailable"; return false; }
                selected = SceneDecalLightBackend.Scalar; FallbackReason = "Instancing/shader capability unavailable";
            }
            else if (settings.backend == SceneDecalLightBackend.Auto && !capable) FallbackReason = "Automatic scalar capability fallback";
            var shader = selected == SceneDecalLightBackend.Instanced ? instanced : Resources.Load<Shader>("SceneDecalLightScalar");
            if (shader == null || !shader.isSupported) { error = "Decal-light shader unavailable"; return false; }
            if (_material == null || Backend != selected) { ReleaseGpu(); _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; }
            Backend = selected;
            if (Backend == SceneDecalLightBackend.Instanced)
            {
                int capacity = Mathf.NextPowerOfTwo(_data.Count);
                if (_buffer == null || _buffer.count != capacity) { _buffer?.Dispose(); _buffer = new ComputeBuffer(capacity, Marshal.SizeOf<LightData>(), ComputeBufferType.Structured) { name = "Toolkit scene light instances" }; }
                _buffer.SetData(_data); _material.SetBuffer("_SceneLights", _buffer);
            }
            else { _buffer?.Dispose(); _buffer = null; }
            var target = camera.targetTexture;
            if (Accumulation == null || !Accumulation.IsCreated() || Accumulation.width != target.width || Accumulation.height != target.height)
            {
                ReleaseTarget(); Accumulation = new RenderTexture(target.width, target.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) {
                    name = "Toolkit direct decal-light accumulation", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave
                };
                if (!Accumulation.Create()) { error = "Decal-light accumulation allocation failed"; return false; }
            }
            return _shadows.Prepare(_visible, settings.shadows, Backend == SceneDecalLightBackend.Instanced, out error);
        }

        public void Record(CommandBuffer commands, RenderTexture[] buffers, Camera camera, Mesh quad, RenderTexture gi)
        {
            if (_data.Count == 0 || _material == null) return;
            _shadows.Record(commands); _shadows.Bind(_material);
            commands.SetRenderTarget(Accumulation); commands.ClearRenderTarget(false, true, Color.clear);
            for (int i = 0; i < 4; i++) _material.SetTexture("_G" + i, buffers[i]);
            var view = camera.worldToCameraMatrix; var projection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            _material.SetMatrix("_LightInverseViewProjection", (projection * view).inverse); _material.SetMatrix("_LightView", view);
            _material.SetVector("_LightCameraPosition", view.inverse.MultiplyPoint(Vector3.zero));
            _material.SetVector("_LightCameraForward", view.inverse.MultiplyVector(Vector3.back).normalized);
            _material.SetFloat("_LightOrthographic", camera.orthographic ? 1 : 0); _material.SetTexture("_LightAtlas", _atlas);
            _material.SetTexture("_LightGi", gi != null ? (Texture)gi : Texture2D.blackTexture);
            _material.SetFloat("_LightHasGi", gi != null ? 1 : 0);
            commands.BeginSample("Toolkit decal light volumes " + Backend);
            if (Backend == SceneDecalLightBackend.Instanced)
            {
                for (int offset = 0; offset < _data.Count; offset += _batchSize)
                {
                    var block = new MaterialPropertyBlock(); block.SetInt("_SceneLightOffset", offset);
                    commands.DrawProcedural(Matrix4x4.identity, _material, 0, MeshTopology.Triangles, 6, Mathf.Min(_batchSize, _data.Count - offset), block);
                    DrawCalls++;
                }
            }
            else for (int index = 0; index < _data.Count; index++)
            {
                var data = _data[index];
                var block = new MaterialPropertyBlock();
                _shadows.BindSingle(block, index);
                block.SetVector("_SinglePositionRange", data.positionRange); block.SetVector("_SingleAxisXLength", data.axisXLength);
                block.SetVector("_SingleAxisYWidth", data.axisYWidth); block.SetVector("_SingleAxisZHeight", data.axisZHeight);
                block.SetVector("_SingleRadianceShape", data.radianceShape); block.SetVector("_SingleUv", data.uv);
                block.SetVector("_SingleParameters", data.parameters); block.SetVector("_SingleResponse", data.response); block.SetVector("_SingleClipRect", data.clipRect);
                commands.DrawMesh(quad, Matrix4x4.identity, _material, 0, 0, block); DrawCalls++;
            }
            commands.EndSample("Toolkit decal light volumes " + Backend);
        }

        // Homogeneous XY planes preserve custom projection semantics. Crossing the eye uses full viewport.
        private static bool ClipRectangle(Vector3 center, Vector3 x, Vector3 y, Vector3 z, Vector3 size,
            Matrix4x4 vp, out Vector4 rect)
        {
            float minX = float.PositiveInfinity, minY = minX, maxX = float.NegativeInfinity, maxY = maxX;
            bool crossesEye = false, left = true, right = true, bottom = true, top = true;
            for (int iz = -1; iz <= 1; iz += 2) for (int iy = -1; iy <= 1; iy += 2) for (int ix = -1; ix <= 1; ix += 2)
            {
                Vector3 p = center + x * (size.x * ix) + y * (size.y * iy) + z * (size.z * iz);
                Vector4 clip = vp * new Vector4(p.x, p.y, p.z, 1);
                left &= clip.x < -clip.w; right &= clip.x > clip.w; bottom &= clip.y < -clip.w; top &= clip.y > clip.w;
                if (clip.w <= 1e-6f) { crossesEye = true; continue; }
                minX = Mathf.Min(minX, clip.x / clip.w); maxX = Mathf.Max(maxX, clip.x / clip.w);
                minY = Mathf.Min(minY, clip.y / clip.w); maxY = Mathf.Max(maxY, clip.y / clip.w);
            }
            rect = new Vector4(-1, -1, 1, 1);
            if (left || right || bottom || top) return false;
            if (crossesEye) return true;
            if (maxX < -1 || minX > 1 || maxY < -1 || minY > 1) return false;
            rect = new Vector4(Mathf.Max(-1, minX), Mathf.Max(-1, minY), Mathf.Min(1, maxX), Mathf.Min(1, maxY)); return true;
        }
        private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        private static bool Range(float x, float low, float high) => Finite(x) && x >= low && x <= high;
        private static bool Valid(SceneDecalLight light)
        {
            if ((int)light.shape < 0 || (int)light.shape > 3 || !Range(light.range, .001f, 10000) || !Range(light.halfLength, 0, 10000) ||
                !Range(light.halfSize.x, .001f, 10000) || !Range(light.halfSize.y, .001f, 10000) || !Range(light.areaSpread.x, 0, 10) || !Range(light.areaSpread.y, 0, 10) ||
                !Range(light.falloffExponent, 1, 8) || !Range(light.diffuseScale, 0, 4) || !Range(light.specularScale, 0, 4) || light.receiverGroup < 0 || light.receiverGroup > 255) return false;
            if (!Range(light.giWeight, 0, 1) || !Range(light.backlightScale, 0, 4)) return false;
            if (light.shape == SceneDecalLightShape.Spot && (!Range(light.spotOuterAngle, .1f, 179) ||
                !Range(light.spotInnerAngle, 0, light.spotOuterAngle))) return false;
            for (int i = 0; i < 3; i++) if (!Range(light.position[i], -1e6f, 1e6f) || !Range(light.radiance[i], 0, 65504)) return false;
            for (int i = 0; i < 4; i++) if (!Finite(light.rotation[i]) || !Finite(light.monitorUV[i])) return false;
            float norm = Quaternion.Dot(light.rotation, light.rotation); return Finite(norm) && norm > 1e-8f;
        }
        private void ReleaseGpu()
        { _shadows.Dispose(); ReleaseTarget(); _buffer?.Dispose(); _buffer = null; if (_material != null) UnityEngine.Object.Destroy(_material); _material = null; }
        private void ReleaseTarget()
        { if (Accumulation != null) { Accumulation.Release(); UnityEngine.Object.Destroy(Accumulation); } Accumulation = null; }
        public void Dispose() { ReleaseGpu(); _data.Clear(); _visible.Clear(); _atlas = null; CulledLights = DrawCalls = 0; }
    }
}
