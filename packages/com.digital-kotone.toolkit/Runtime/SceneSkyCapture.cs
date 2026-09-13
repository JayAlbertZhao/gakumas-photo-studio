using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit native six-face sky capture. No scene discovery, global GI update or CPU readback.</summary>
    public sealed class SceneSkyCapture : IDisposable
    {
        public readonly struct Frame
        {
            private readonly SceneSkyCapture owner;
            private readonly uint generation;
            public readonly RenderTexture radiance;
            internal Frame(SceneSkyCapture value) { owner = value; generation = value.generation; radiance = value.cube; }
            public bool IsCurrent => owner != null && owner.valid && owner.generation == generation && owner.cube == radiance && radiance != null && radiance.IsCreated();
        }
        private readonly SceneSkyMaterial material = new SceneSkyMaterial();
        private Camera camera;
        private Skybox sky;
        private RenderTexture cube;
        private uint generation;
        private bool valid, capturing;
        public string UnavailableReason { get; private set; }
        public int TargetCount => cube != null ? 1 : 0;
        public bool TryCapture(SceneSkySettings settings, int size, bool mipmaps, out Frame frame)
        {
            frame = default;
            if (capturing) { UnavailableReason = "Recursive sky capture is not supported"; return false; }
            generation++; valid = false;
            if (size < 4 || size > 2048 || !Mathf.IsPowerOfTwo(size)) return Fail("Sky cube size must be a power of two in4..2048");
            if (!SystemInfo.supportsRenderToCubemap || !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Sample)) return Fail("Native HDR cubemap rendering unavailable");
            if (settings != null && settings.enabled && settings.source != SceneSkySource.Gradient && cube != null && settings.texture == cube)
                return Fail("Sky input aliases its current capture output");
            if (!material.TryUpdate(settings, out var skyMaterial)) return Fail(material.UnavailableReason);
            var active = RenderTexture.active; capturing = true;
            try
            {
                if (camera == null)
                {
                    var host = new GameObject("Toolkit sky capture camera") { hideFlags = HideFlags.HideAndDontSave };
                    camera = host.AddComponent<Camera>(); camera.enabled = false; camera.cullingMask = 0;
                    camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
                    camera.clearFlags = CameraClearFlags.Skybox; camera.nearClipPlane = .01f; camera.farClipPlane = 10;
                    sky = host.AddComponent<Skybox>();
                }
                sky.material = skyMaterial;
                if (cube == null || !cube.IsCreated() || cube.width != size || cube.useMipMap != mipmaps)
                {
                    ReleaseCube();
                    cube = new RenderTexture(new RenderTextureDescriptor(size, size, GraphicsFormat.R16G16B16A16_SFloat, 16) {
                        dimension = TextureDimension.Cube, useMipMap = mipmaps, autoGenerateMips = false, msaaSamples = 1
                    }) { name = "Toolkit authored sky radiance cube", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp,
                        filterMode = mipmaps ? FilterMode.Trilinear : FilterMode.Bilinear };
                    if (!cube.Create() || cube.graphicsFormat != GraphicsFormat.R16G16B16A16_SFloat) return Fail("HDR sky cubemap allocation failed");
                }
                if (!camera.RenderToCubemap(cube, 63)) return Fail("Native six-face sky capture failed");
                if (mipmaps) cube.GenerateMips();
                valid = true; UnavailableReason = null; frame = new Frame(this); return true;
            }
            catch (Exception error) { return Fail("Sky capture failed: " + error.GetType().Name); }
            finally { capturing = false; RenderTexture.active = active != null && active.IsCreated() ? active : null; }
        }
        private bool Fail(string reason) { UnavailableReason = reason; Release(); return false; }
        private void ReleaseCube()
        {
            if (cube != null) { if (RenderTexture.active == cube) RenderTexture.active = null; cube.Release(); UnityEngine.Object.Destroy(cube); }
            cube = null;
        }
        private void Release()
        {
            valid = false; if (camera != null) UnityEngine.Object.Destroy(camera.gameObject); camera = null; sky = null;
            ReleaseCube(); material.Dispose();
        }
        public void Dispose() { generation++; Release(); }
    }
}
