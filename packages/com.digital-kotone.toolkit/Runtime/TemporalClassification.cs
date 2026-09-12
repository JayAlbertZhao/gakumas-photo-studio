using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    [Flags]
    public enum TemporalPixelFlags { Normal = 0, ExcludeTaa = 2, NoJitter = 4 }

    /// <summary>
    /// Opt-in, camera-local visible-surface classification. Does not change the
    /// source materials or overload ActorData's unrelated material discriminator.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class TemporalClassification : MonoBehaviour
    {
        [Serializable]
        public sealed class Surface
        {
            public Renderer renderer;
            [Min(0)] public int materialIndex;
            public TemporalPixelFlags flags = TemporalPixelFlags.ExcludeTaa;
            public CullMode cull = CullMode.Back;
            public Texture alphaMask;
            public Vector4 alphaMaskST = new Vector4(1, 1, 0, 0);
            [Range(0f, 1f)] public float alphaCutoff;
            public Vector3 vertexScale = Vector3.one;
        }

        public Surface[] surfaces = Array.Empty<Surface>();
        // The host supplies its applied projection jitter in texture UV units.
        // Existing Photo Studio cameras do not introduce projection jitter.
        public Vector2 jitterUv;
        private Camera _camera;
        private CommandBuffer _commands;
        private RenderTexture _mask;
        private Shader _shader;
        private readonly List<Material> _materials = new List<Material>();
        private int _preparedFrame = -1;
        public int SubmittedSurfaces { get; private set; }

        private void OnEnable()
        {
            _camera = GetComponent<Camera>();
            _shader = Resources.Load<Shader>("TemporalClassification");
            _commands = new CommandBuffer { name = "Toolkit temporal surface classification" };
            _camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _commands);
        }

        private void OnPreCull()
        {
            _preparedFrame = -1;
            SubmittedSurfaces = 0;
            if (_commands == null) return;
            _commands.Clear();
            if (surfaces == null || surfaces.Length == 0)
            {
                ReleaseResources();
                return;
            }
            // This backend shares the ordinary camera depth attachment. XR,
            // viewport atlases and multisample targets require a separate adapter.
            if (_shader == null || !_shader.isSupported || GraphicsSettings.currentRenderPipeline != null ||
                _camera.stereoEnabled || _camera.rect != new Rect(0, 0, 1, 1) ||
                (_camera.targetTexture != null ? _camera.targetTexture.antiAliasing > 1 :
                    _camera.allowMSAA && QualitySettings.antiAliasing > 1)) return;
            int width = _camera.targetTexture != null ? _camera.targetTexture.width : _camera.pixelWidth;
            int height = _camera.targetTexture != null ? _camera.targetTexture.height : _camera.pixelHeight;
            if (width < 1 || height < 1) return;
            if (_mask == null || _mask.width != width || _mask.height != height)
            {
                ReleaseMask();
                RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8)
                    ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
                _mask = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
                {
                    name = "ToolkitTemporalFlags", filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave
                };
                _mask.Create();
            }
            // Depth means the sampled camera depth texture, which can omit
            // forward materials without ShadowCaster passes. Use the active
            // framebuffer's actual depth attachment instead.
            _commands.SetRenderTarget(new RenderTargetIdentifier(_mask), BuiltinRenderTextureType.CurrentActive);
            _commands.ClearRenderTarget(false, true, Color.clear);
            for (int i = 0; i < surfaces.Length; i++)
            {
                Surface surface = surfaces[i];
                if (surface == null || surface.renderer == null || surface.flags == TemporalPixelFlags.Normal ||
                    (((int)surface.flags) & ~6) != 0 || !ValidSurface(surface) || surface.materialIndex < 0 ||
                    surface.materialIndex >= surface.renderer.sharedMaterials.Length ||
                    !surface.renderer.enabled || surface.renderer.forceRenderingOff ||
                    !surface.renderer.gameObject.activeInHierarchy ||
                    (_camera.cullingMask & (1 << surface.renderer.gameObject.layer)) == 0) continue;
                while (_materials.Count <= SubmittedSurfaces)
                    _materials.Add(new Material(_shader) { hideFlags = HideFlags.HideAndDontSave });
                Material material = _materials[SubmittedSurfaces];
                material.SetFloat("_TemporalFlags", (int)surface.flags / 255f);
                material.SetFloat("_Cull", (int)surface.cull);
                material.SetTexture("_TemporalAlphaMask", surface.alphaMask != null ? surface.alphaMask : Texture2D.whiteTexture);
                material.SetVector("_TemporalAlphaMaskST", surface.alphaMaskST);
                material.SetFloat("_TemporalAlphaCutoff", surface.alphaCutoff);
                material.SetVector("_TemporalVertexScale", surface.vertexScale);
                _commands.DrawRenderer(surface.renderer, material, surface.materialIndex, 0);
                SubmittedSurfaces++;
            }
            // Unity saves/restores targets around command-buffer execution.
            _preparedFrame = Time.frameCount;
        }

        internal bool TryGetMask(Camera camera, int width, int height, out Texture mask)
        {
            mask = null;
            if (!isActiveAndEnabled || camera != _camera || _preparedFrame != Time.frameCount ||
                SubmittedSurfaces == 0 || _mask == null || _mask.width != width || _mask.height != height ||
                float.IsNaN(jitterUv.x) || float.IsNaN(jitterUv.y) ||
                Mathf.Abs(jitterUv.x) > 0.5f || Mathf.Abs(jitterUv.y) > 0.5f) return false;
            mask = _mask;
            return true;
        }

        private static bool ValidSurface(Surface surface)
        {
            int cull = (int)surface.cull;
            if (cull < 0 || cull > 2 || !Finite(surface.alphaCutoff) || surface.alphaCutoff < 0 || surface.alphaCutoff > 1) return false;
            for (int i = 0; i < 4; i++) if (!Finite(surface.alphaMaskST[i])) return false;
            for (int i = 0; i < 3; i++) if (!Finite(surface.vertexScale[i])) return false;
            return true;
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private void ReleaseMask()
        {
            if (_mask == null) return;
            _mask.Release();
            Destroy(_mask);
            _mask = null;
        }

        private void ReleaseResources()
        {
            ReleaseMask();
            foreach (Material material in _materials) if (material != null) Destroy(material);
            _materials.Clear();
        }

        private void OnDisable()
        {
            _preparedFrame = -1;
            SubmittedSurfaces = 0;
            if (_commands != null)
            {
                if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _commands);
                _commands.Release();
                _commands = null;
            }
            ReleaseResources();
        }
    }
}
