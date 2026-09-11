using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Renders the photography camera into a larger HDR target, then presents a
    /// bilinear downsample to the window.  The captured game renders its sharp
    /// Actor/post input at 3840x2160 before producing a 1920x1080 presentation;
    /// this keeps that raster and post-process resolution contract without
    /// making the user's window cover a 4K desktop area.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class SupersamplePresenter : MonoBehaviour
    {
        private static readonly Dictionary<int, SupersamplePresenter> BySourceCamera =
            new Dictionary<int, SupersamplePresenter>();

        private Camera _sourceCamera;
        private Camera _presenterCamera;
        private RenderTexture _sourceTarget;
        private RenderTexture _presentationTarget;
        private int _scale = 2;
        private int _windowWidth;
        private int _windowHeight;
        private Vector2 _advShakePosition;

        private const float AdvAuthoredWidth = 3840f;
        private const float AdvAuthoredHeight = 2160f;

        public static RenderTextureFormat SceneColorFormat
        {
            get
            {
                // Scene RGB has no coverage alpha; ActorData and the final
                // presentation retain their separate four-channel formats.
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--legacy-half-scene-color") >= 0 ||
                    !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float))
                    return RenderTextureFormat.ARGBHalf;
                return RenderTextureFormat.RGB111110Float;
            }
        }

        public void Initialize(Camera sourceCamera, int scale)
        {
            _sourceCamera = sourceCamera;
            _scale = Mathf.Max(1, scale);
            _presenterCamera = GetComponent<Camera>();
            ConfigurePresenter();
            EnsureTarget();
        }

        private void LateUpdate()
        {
            EnsureTarget();
        }

        private void ConfigurePresenter()
        {
            if (_presenterCamera == null) _presenterCamera = GetComponent<Camera>();
            _presenterCamera.clearFlags = CameraClearFlags.Nothing;
            _presenterCamera.cullingMask = 0;
            _presenterCamera.depth = _sourceCamera == null ? 100f : _sourceCamera.depth + 100f;
            _presenterCamera.allowHDR = false;
            _presenterCamera.allowMSAA = false;
            _presenterCamera.useOcclusionCulling = false;
        }

        private void EnsureTarget()
        {
            if (_sourceCamera == null || _scale <= 1) return;
            int width = Mathf.Max(1, Screen.width);
            int height = Mathf.Max(1, Screen.height);
            if (_sourceTarget != null && _presentationTarget != null &&
                _windowWidth == width && _windowHeight == height)
                return;

            ReleaseTarget();
            _windowWidth = width;
            _windowHeight = height;
            _sourceTarget = new RenderTexture(
                width * _scale, height * _scale, 24, SceneColorFormat)
            {
                name = string.Format("PhotoSupersample{0}x_{1}x{2}", _scale, width, height),
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            _sourceTarget.Create();
            _presentationTarget = new RenderTexture(
                width, height, 0, RenderTextureFormat.ARGBHalf)
            {
                name = string.Format("PhotoPresentation_{0}x{1}", width, height),
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            _presentationTarget.Create();
            _sourceCamera.targetTexture = _sourceTarget;
            BySourceCamera[_sourceCamera.GetInstanceID()] = this;
            Debug.Log(string.Format(
                "[PhotoMode] Supersample presentation ready: render={0}x{1}, window={2}x{3}, scale={4}x",
                _sourceTarget.width, _sourceTarget.height, width, height, _scale));
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (_presentationTarget != null)
            {
                if (_advShakePosition.sqrMagnitude > 0.000001f)
                {
                    // ShakeMixerPlayable moves UIManager.MainLayer in the
                    // authored 3840x2160 canvas after the scene/post render.
                    // Shift the completed presentation surface at the same
                    // stage; negative source UV produces positive content
                    // displacement in destination space.
                    Graphics.Blit(
                        _presentationTarget, destination, Vector2.one,
                        new Vector2(
                            -_advShakePosition.x / AdvAuthoredWidth,
                            -_advShakePosition.y / AdvAuthoredHeight));
                }
                else
                {
                    Graphics.Blit(_presentationTarget, destination);
                }
            }
            else if (_sourceTarget != null)
                Graphics.Blit(_sourceTarget, destination);
            else
                Graphics.Blit(source, destination);
        }

        public void SetAdvShakePosition(Vector2 position)
        {
            _advShakePosition = position;
        }

        public static bool TryGetPresentationTarget(Camera sourceCamera, out RenderTexture target)
        {
            target = null;
            if (sourceCamera == null) return false;
            SupersamplePresenter presenter;
            if (BySourceCamera.TryGetValue(sourceCamera.GetInstanceID(), out presenter))
            {
                if (presenter != null && presenter._sourceCamera == sourceCamera &&
                    presenter._presentationTarget != null)
                {
                    // Explicit screenshots temporarily render this camera into
                    // another target. Do not redirect their final color into
                    // the window, or discard the normal camera registration.
                    if (sourceCamera.targetTexture != presenter._sourceTarget) return false;
                    target = presenter._presentationTarget;
                    return true;
                }
                BySourceCamera.Remove(sourceCamera.GetInstanceID());
            }
            return false;
        }

        private void ReleaseTarget()
        {
            if (_sourceCamera != null)
            {
                int sourceId = _sourceCamera.GetInstanceID();
                SupersamplePresenter registered;
                if (BySourceCamera.TryGetValue(sourceId, out registered) && registered == this)
                    BySourceCamera.Remove(sourceId);
            }
            if (_sourceCamera != null && _sourceCamera.targetTexture == _sourceTarget)
                _sourceCamera.targetTexture = null;
            if (_sourceTarget != null)
            {
                _sourceTarget.Release();
                DestroyImmediate(_sourceTarget);
                _sourceTarget = null;
            }
            if (_presentationTarget != null)
            {
                _presentationTarget.Release();
                DestroyImmediate(_presentationTarget);
                _presentationTarget = null;
            }
        }

        private void OnDisable()
        {
            ReleaseTarget();
        }

        private void OnDestroy()
        {
            ReleaseTarget();
        }
    }
}
