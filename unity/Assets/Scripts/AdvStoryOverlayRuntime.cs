using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Reconstructs the screen-space parts of the horizontal ADV UI used by
    /// FadeMixerPlayable and ForegroundMixerPlayable.  Native PASS284 evidence
    /// establishes two independent covers and a foreground CanvasGroup whose
    /// alpha is the Timeline input weight.  ScreenSpaceOverlay keeps these
    /// elements after the reconstructed camera post stack, as in the source UI.
    /// </summary>
    public sealed class AdvStoryOverlayRuntime : IDisposable
    {
        private const float AuthoredWidth = 3840f;
        private const float AuthoredHeight = 2160f;

        private readonly BundleCatalog _catalog;
        private readonly GameObject _root;
        private readonly RawImage _contentCover;
        private readonly RawImage _mainCover;
        private readonly RectTransform _foregroundRoot;
        private readonly RectTransform _foregroundFitter;
        private readonly RawImage _foregroundImage;
        private readonly CanvasGroup _foregroundFade;
        private readonly AspectRatioFitter _foregroundAspect;

        private string _foregroundSource;
        private Texture _foregroundTexture;
        private float _contentAlpha;
        private float _mainAlpha;
        private float _foregroundAlpha;
        private Vector2 _foregroundAuthoredPosition;
        private Vector2 _shakePosition;
        private string _foregroundLayoutDiagnostic = "{\"mode\":\"none\"}";

        public float ContentAlpha { get { return _contentAlpha; } }
        public float MainAlpha { get { return _mainAlpha; } }
        public float ForegroundAlpha { get { return _foregroundAlpha; } }
        public string ForegroundSource { get { return _foregroundSource ?? string.Empty; } }

        public AdvStoryOverlayRuntime(BundleCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _root = new GameObject(
                "ADVStoryOverlay",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            UnityEngine.Object.DontDestroyOnLoad(_root);

            Canvas canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            CanvasScaler scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(AuthoredWidth, AuthoredHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // UIManager serializes MainLayer, ForegroundLayer, and UILayer in
            // that order. ContentCover belongs to the content canvas, MainCover
            // remains above content, and authored foregrounds remain above the
            // Main cover. Preserve that visual stacking here.
            _contentCover = CreateRawImage("ContentCover", _root.transform);
            _mainCover = CreateRawImage("MainCover", _root.transform);
            _foregroundRoot = CreateRectTransform("Foreground", _root.transform);
            SetStretch(_foregroundRoot);
            _foregroundFitter = CreateRectTransform("Fitter", _foregroundRoot);
            SetStretch(_foregroundFitter);
            _foregroundImage = CreateRawImage("RawImage", _foregroundFitter);
            // CreateRawImage starts transparent because the two covers drive
            // their tint explicitly.  Foreground alpha is owned by its
            // CanvasGroup, so its vertex colour must remain opaque white.
            _foregroundImage.color = Color.white;
            _foregroundFade = _foregroundRoot.gameObject.AddComponent<CanvasGroup>();
            _foregroundAspect = _foregroundFitter.gameObject.AddComponent<AspectRatioFitter>();
            _foregroundAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;

            SetFade("Content", Color.black, 0f);
            SetFade("Main", Color.black, 0f);
            SetForeground(null, 0f);
            _root.SetActive(false);
        }

        public void SetEnabled(bool enabled)
        {
            if (_root != null) _root.SetActive(enabled);
        }

        public void SetFade(string layer, Color color, float alpha)
        {
            float value = Mathf.Clamp01(alpha);
            color.a *= value;
            if (string.Equals(layer, "Main", StringComparison.OrdinalIgnoreCase))
            {
                _mainAlpha = value;
                _mainCover.color = color;
                _mainCover.enabled = color.a > 0f;
            }
            else
            {
                _contentAlpha = value;
                _contentCover.color = color;
                _contentCover.enabled = color.a > 0f;
            }
        }

        public void SetForeground(StoryForegroundEvent value, float weight)
        {
            _foregroundAlpha = Mathf.Clamp01(weight);
            if (value == null || string.IsNullOrEmpty(value.src))
            {
                _foregroundFade.alpha = 0f;
                _foregroundImage.enabled = false;
                return;
            }

            if (!string.Equals(_foregroundSource, value.src, StringComparison.OrdinalIgnoreCase))
            {
                _foregroundTexture = LoadForegroundTexture(value.src);
                _foregroundSource = value.src;
                _foregroundImage.texture = _foregroundTexture;
                if (_foregroundTexture != null)
                    _foregroundAspect.aspectRatio =
                        (float)_foregroundTexture.width / Mathf.Max(1, _foregroundTexture.height);
                Debug.Log(_foregroundTexture == null
                    ? "[StoryOverlay] Foreground texture missing: " + value.src
                    : string.Format(
                        "[StoryOverlay] Foreground ready: {0} -> {1} {2}x{3}",
                        value.src, _foregroundTexture.name,
                        _foregroundTexture.width, _foregroundTexture.height));
            }

            ApplyForegroundLayout(value);
            StoryTransform2D transform = value.transform;
            Vector2 position = transform != null && transform.position != null
                ? transform.position.ToVector2() : Vector2.zero;
            Vector2 scale = transform != null && transform.scale != null
                ? transform.scale.ToVector2() : Vector2.one;
            float angle = transform == null ? 0f : transform.angle;
            _foregroundAuthoredPosition = position;
            _foregroundRoot.anchoredPosition = _foregroundAuthoredPosition + _shakePosition;
            _foregroundRoot.localScale = new Vector3(scale.x, scale.y, 1f);
            _foregroundRoot.localEulerAngles = new Vector3(0f, 0f, angle);

            _foregroundFade.alpha = _foregroundAlpha;
            _foregroundImage.enabled = _foregroundTexture != null && _foregroundAlpha > 0f;
        }

        public void SetShakePosition(Vector2 position)
        {
            _shakePosition = position;
            if (_foregroundRoot != null)
                _foregroundRoot.anchoredPosition =
                    _foregroundAuthoredPosition + _shakePosition;
        }

        private void ApplyForegroundLayout(StoryForegroundEvent value)
        {
            bool exact = value != null && value.layout != null &&
                Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--disable-story-foreground-layout") < 0;
            if (!exact)
            {
                SetStretch(_foregroundFitter);
                SetStretch(_foregroundImage.rectTransform);
                _foregroundAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                _foregroundAspect.aspectRatio = _foregroundTexture == null
                    ? 16f / 9f
                    : (float)_foregroundTexture.width /
                      Mathf.Max(1, _foregroundTexture.height);
                _foregroundLayoutDiagnostic =
                    "{\"mode\":\"fallback-fit\",\"textureAspect\":" +
                    _foregroundAspect.aspectRatio.ToString(
                        "R", System.Globalization.CultureInfo.InvariantCulture) + "}";
                return;
            }

            bool vertical = Screen.height > Screen.width;
            StoryForegroundLayoutSetting setting = vertical
                ? value.layout.vertical : value.layout.horizontal;
            if (setting == null || setting.aspectRatio == null ||
                setting.layoutRect == null || setting.clippingRect == null ||
                setting.aspectRatio.y <= 0f ||
                setting.clippingRect.width <= 0f ||
                setting.clippingRect.height <= 0f)
            {
                SetStretch(_foregroundFitter);
                SetStretch(_foregroundImage.rectTransform);
                _foregroundAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                _foregroundLayoutDiagnostic = "{\"mode\":\"invalid-fallback\"}";
                return;
            }

            StoryRect layout = setting.layoutRect;
            StoryRect clipping = setting.clippingRect;
            _foregroundAspect.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            _foregroundAspect.aspectRatio =
                setting.aspectRatio.x / setting.aspectRatio.y;

            RectTransform imageRect = _foregroundImage.rectTransform;
            imageRect.anchorMin = new Vector2(
                (layout.x - clipping.x) / clipping.width,
                (clipping.y + clipping.height - layout.y - layout.height) /
                    clipping.height);
            imageRect.anchorMax = new Vector2(
                (layout.x + layout.width - clipping.x) / clipping.width,
                (clipping.y + clipping.height - layout.y) / clipping.height);
            imageRect.anchoredPosition = Vector2.zero;
            imageRect.sizeDelta = Vector2.zero;
            imageRect.pivot = new Vector2(0.5f, 0.5f);

            _foregroundLayoutDiagnostic = string.Format(
                "{{\"mode\":\"exact\",\"orientation\":\"{0}\",\"sourcePathId\":{1}," +
                "\"aspect\":{2:R},\"anchorMin\":[{3:R},{4:R}],\"anchorMax\":[{5:R},{6:R}]}}",
                vertical ? "vertical" : "horizontal",
                value.layout.containerSourcePathId,
                _foregroundAspect.aspectRatio,
                imageRect.anchorMin.x, imageRect.anchorMin.y,
                imageRect.anchorMax.x, imageRect.anchorMax.y);
        }

        public string LayoutDiagnosticJson()
        {
            return _foregroundLayoutDiagnostic;
        }

        private Texture LoadForegroundTexture(string source)
        {
            Texture texture = _catalog.LoadAll<Texture2D>(source)
                .OrderByDescending(value => value == null ? 0L : (long)value.width * value.height)
                .FirstOrDefault();
            if (texture != null) return texture;

            Sprite sprite = _catalog.LoadAll<Sprite>(source).FirstOrDefault();
            if (sprite != null && sprite.texture != null) return sprite.texture;
            Material material = _catalog.LoadAll<Material>(source)
                .FirstOrDefault(value => value != null && value.mainTexture != null);
            if (material != null) return material.mainTexture;

            try
            {
                GameObject prefab = _catalog.LoadPrefab(source);
                RawImage raw = prefab.GetComponentInChildren<RawImage>(true);
                if (raw != null && raw.texture != null) return raw.texture;
                Image image = prefab.GetComponentInChildren<Image>(true);
                if (image != null && image.sprite != null) return image.sprite.texture;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[StoryOverlay] Foreground prefab probe failed: " + exception.Message);
            }

            return _catalog.LoadObjectsWithSubAssets(source).OfType<Texture>()
                .OrderByDescending(value => (long)value.width * value.height)
                .FirstOrDefault();
        }

        private static RawImage CreateRawImage(string name, Transform parent)
        {
            GameObject value = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            value.transform.SetParent(parent, false);
            RectTransform rect = value.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            RawImage image = value.GetComponent<RawImage>();
            image.raycastTarget = false;
            image.color = Color.clear;
            return image;
        }

        private static RectTransform CreateRectTransform(string name, Transform parent)
        {
            GameObject value = new GameObject(name, typeof(RectTransform));
            value.transform.SetParent(parent, false);
            return value.GetComponent<RectTransform>();
        }

        private static void SetStretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
        }

        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
        }
    }
}
