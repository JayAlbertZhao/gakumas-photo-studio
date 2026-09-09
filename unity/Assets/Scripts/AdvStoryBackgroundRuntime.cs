using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Reproduces the visual part of Campus.ADV.EnvironmentManager.BackgroundData.
    /// Native PASS282 evidence establishes that horizontal ADV applies the
    /// authored Transform2D position directly as RectTransform anchored pixels,
    /// local scale directly, and angle as local Z Euler.  This class maps that
    /// 3840x2160 authored canvas contract to the camera-facing background quad;
    /// streamed 3D scene bundles remain in authored world space.
    /// </summary>
    public sealed class AdvStoryBackgroundRuntime : IDisposable
    {
        private const float AuthoredCanvasHeight = 2160f;
        private const float QuadDistance = 8f;
        private const int MaximumEnvironmentDecals = 4;
        private static readonly int EnvironmentDecalCountId =
            Shader.PropertyToID("_EnvironmentDecalCount");
        private static readonly int EnvironmentDecalWorldToDecalId =
            Shader.PropertyToID("_EnvironmentDecalWorldToDecal");
        private static readonly int EnvironmentDecalUvScaleBiasId =
            Shader.PropertyToID("_EnvironmentDecalUvScaleBias");
        private static readonly int EnvironmentDecalBaseAtlasId =
            Shader.PropertyToID("_EnvironmentDecalBaseAtlas");
        private static readonly int EnvironmentDecalDefinitionAtlasId =
            Shader.PropertyToID("_EnvironmentDecalDefinitionAtlas");
        private static readonly int EnvironmentDecalDefinitionScaleId =
            Shader.PropertyToID("_EnvironmentDecalDefinitionScale");

        private readonly BundleCatalog _catalog;
        private readonly Camera _camera;
        private readonly Scene _ownerScene;
        private readonly Dictionary<string, StoryBackgroundDeclaration> _declarations =
            new Dictionary<string, StoryBackgroundDeclaration>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SceneRecord> _scenes =
            new Dictionary<string, SceneRecord>(StringComparer.OrdinalIgnoreCase);

        private GameObject _quad;
        private Material _quadMaterial;
        private Texture _quadTexture;
        private string _activeId;
        private string _activeSource;
        private bool _enabled = true;
        private Vector2 _position;
        private Vector2 _scale = Vector2.one;
        private float _angle;
        private StoryBackgroundClippingProfile _clippingProfile;
        private bool _exactClippingActive;
        private bool _horizontalClipping;
        private float _fitterScale;
        private Vector2 _fitterPosition;
        private Vector2 _fitterSize;
        private string _quadMaterialMode = "none";

        private sealed class SceneRecord
        {
            public string source;
            public string path;
            public Scene scene;
            public Scene ownerScene;
            public GameObject root;
            public AsyncOperation operation;
            public readonly List<Material> ownedMaterials = new List<Material>();
        }

        public AdvStoryBackgroundRuntime(BundleCatalog catalog, Camera camera)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
            _ownerScene = camera.gameObject.scene;
        }

        public string ActiveId { get { return _activeId ?? string.Empty; } }
        public string ActiveSource { get { return _activeSource ?? string.Empty; } }
        public bool IsReady
        {
            get
            {
                if (string.IsNullOrEmpty(_activeSource)) return true;
                if (!IsStreamedScene(_activeSource)) return _quadTexture != null;
                SceneRecord record;
                return _scenes.TryGetValue(_activeSource, out record) &&
                    record.operation == null && record.root != null;
            }
        }

        public void Configure(StoryBackgroundDeclaration[] declarations)
        {
            _declarations.Clear();
            if (declarations == null) return;
            foreach (StoryBackgroundDeclaration value in declarations)
            {
                if (value == null || string.IsNullOrEmpty(value.id) || string.IsNullOrEmpty(value.src))
                    continue;
                _declarations[value.id] = value;
            }
            Debug.Log(string.Format(
                "[StoryBackground] Configured {0} declarations: {1}",
                _declarations.Count,
                string.Join(", ", _declarations.Values
                    .OrderBy(value => value.order)
                    .Select(value => value.id + "=" + value.src))));
        }

        public string FirstTwoDimensionalId()
        {
            StoryBackgroundDeclaration value = _declarations.Values
                .OrderBy(item => item.order)
                .FirstOrDefault(item => !IsStreamedScene(item.src));
            return value == null ? null : value.id;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            RefreshVisibility();
        }

        public void SetLayout(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                if (!string.IsNullOrEmpty(_activeId))
                    Debug.Log("[StoryBackground] Layout cleared");
                _activeId = null;
                _activeSource = null;
                _clippingProfile = null;
                RefreshVisibility();
                return;
            }

            StoryBackgroundDeclaration declaration;
            if (!_declarations.TryGetValue(id, out declaration))
            {
                Debug.LogWarning("[StoryBackground] Unknown layout id: " + id);
                _activeId = null;
                _activeSource = null;
                _clippingProfile = null;
                RefreshVisibility();
                return;
            }
            if (string.Equals(_activeId, id, StringComparison.OrdinalIgnoreCase))
            {
                RefreshVisibility();
                return;
            }

            _activeId = declaration.id;
            _activeSource = declaration.src;
            _clippingProfile = declaration.clipping;
            _position = Vector2.zero;
            _scale = Vector2.one;
            _angle = 0f;

            if (IsStreamedScene(_activeSource))
                EnsureScene(_activeSource);
            else
                EnsureQuad(_activeSource);
            RefreshVisibility();
            UpdateCameraGeometry();
            Debug.Log(string.Format(
                "[StoryBackground] Layout {0} -> {1} ({2})",
                _activeId, _activeSource,
                IsStreamedScene(_activeSource) ? "streamed-scene" : "2d"));
        }

        public void ApplyTransform(StoryBackgroundTransformEvent value, float tween)
        {
            StoryTransform2D from = null;
            StoryTransform2D to = null;
            if (value != null && string.Equals(value.id, _activeId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(value.kind, "tween", StringComparison.OrdinalIgnoreCase) &&
                    value.from != null && value.to != null)
                {
                    from = value.from;
                    to = value.to;
                }
                else
                {
                    from = value.setting ?? value.to ?? value.from;
                    to = from;
                    tween = 1f;
                }
            }

            if (from == null || to == null)
            {
                _position = Vector2.zero;
                _scale = Vector2.one;
                _angle = 0f;
            }
            else
            {
                float t = Mathf.Clamp01(tween);
                _position = Vector2.Lerp(PositionOf(from), PositionOf(to), t);
                _scale = Vector2.Lerp(ScaleOf(from), ScaleOf(to), t);
                _angle = Mathf.LerpAngle(from.angle, to.angle, t);
            }
            UpdateCameraGeometry();
        }

        public void UpdateCameraGeometry()
        {
            if (_quad == null || _camera == null || _quadTexture == null) return;
            float height = 2f * QuadDistance * Mathf.Tan(0.5f * _camera.fieldOfView * Mathf.Deg2Rad);
            float textureAspect = Mathf.Max(0.01f,
                (float)_quadTexture.width / Mathf.Max(1f, _quadTexture.height));
            float viewportAspect = Mathf.Max(0.01f, _camera.aspect);
            bool disableExact = Array.IndexOf(
                Environment.GetCommandLineArgs(),
                "--disable-story-background-clipping") >= 0;
            StoryBackgroundClippingSetting clipping = null;
            _horizontalClipping = viewportAspect >= 1f;
            if (_clippingProfile != null && !disableExact)
            {
                clipping = _horizontalClipping
                    ? _clippingProfile.horizontal
                    : _clippingProfile.vertical;
            }

            Vector2 fittedPosition = Vector2.zero;
            Vector2 fittedSize;
            float authoredParentHeight;
            if (clipping != null && clipping.baseSize != null &&
                clipping.clippingRect != null &&
                clipping.clippingRect.width > 0f && clipping.clippingRect.height > 0f)
            {
                // UIManager changes orientation at width >= height. Its authored
                // reference is 3840x2160 horizontally and 2160x3840 vertically;
                // preserve the 2160 short axis for arbitrary window aspects.
                authoredParentHeight = _horizontalClipping
                    ? AuthoredCanvasHeight
                    : AuthoredCanvasHeight / viewportAspect;
                float parentWidth = authoredParentHeight * viewportAspect;
                float parentHeight = authoredParentHeight;
                float clippingAspect = clipping.clippingRect.width /
                    clipping.clippingRect.height;
                bool parentAspectNotWider = parentWidth / parentHeight <= clippingAspect;
                bool fitInParent = _clippingProfile.mode == 1;
                _fitterScale = fitInParent == parentAspectNotWider
                    ? parentWidth / clipping.clippingRect.width
                    : parentHeight / clipping.clippingRect.height;
                fittedPosition = new Vector2(
                    ((clipping.baseSize.x - clipping.clippingRect.width) * 0.5f -
                        clipping.clippingRect.x) * _fitterScale,
                    ((clipping.baseSize.y - clipping.clippingRect.height) * 0.5f -
                        clipping.clippingRect.y) * _fitterScale);
                // UpdateRect drives sizeDelta from baseSize * scale minus the
                // parent rect. clippingRect selects the envelope and offset;
                // it is not the displayed RawImage size.
                fittedSize = new Vector2(
                    clipping.baseSize.x * _fitterScale,
                    clipping.baseSize.y * _fitterScale);
                _exactClippingActive = true;
            }
            else
            {
                // A/B and data-missing fallback retained from PASS282.
                authoredParentHeight = AuthoredCanvasHeight;
                float baseHeight = authoredParentHeight;
                float baseWidth = baseHeight * textureAspect;
                if (baseWidth < authoredParentHeight * viewportAspect)
                {
                    baseWidth = authoredParentHeight * viewportAspect;
                    baseHeight = baseWidth / textureAspect;
                }
                fittedSize = new Vector2(baseWidth, baseHeight);
                _fitterScale = 0f;
                _exactClippingActive = false;
            }

            _fitterPosition = fittedPosition;
            _fitterSize = fittedSize;
            Vector2 scaledFitterPosition = Vector2.Scale(fittedPosition, _scale);
            float radians = _angle * Mathf.Deg2Rad;
            float cosine = Mathf.Cos(radians);
            float sine = Mathf.Sin(radians);
            Vector2 rotatedFitterPosition = new Vector2(
                scaledFitterPosition.x * cosine - scaledFitterPosition.y * sine,
                scaledFitterPosition.x * sine + scaledFitterPosition.y * cosine);
            Vector2 finalPosition = _position + rotatedFitterPosition;
            float worldPerAuthoredPixel = height / authoredParentHeight;
            _quad.transform.localPosition = new Vector3(
                finalPosition.x * worldPerAuthoredPixel,
                finalPosition.y * worldPerAuthoredPixel,
                QuadDistance);
            // Unity's built-in Quad front face points along local -Z.  The
            // camera-facing plate sits on +Z in camera space, so turn it toward
            // the camera before applying the authored screen-plane angle.
            _quad.transform.localRotation = Quaternion.Euler(0f, 180f, -_angle);
            _quad.transform.localScale = new Vector3(
                fittedSize.x * worldPerAuthoredPixel * Mathf.Max(0.0001f, _scale.x),
                fittedSize.y * worldPerAuthoredPixel * Mathf.Max(0.0001f, _scale.y),
                1f);
        }

        public string ClippingDiagnosticJson()
        {
            CultureInfo culture = CultureInfo.InvariantCulture;
            return "{\"exact\":" + (_exactClippingActive ? "true" : "false") +
                ",\"horizontal\":" + (_horizontalClipping ? "true" : "false") +
                ",\"mode\":" + (_clippingProfile == null ? "0" :
                    _clippingProfile.mode.ToString(culture)) +
                ",\"fitterScale\":" + _fitterScale.ToString("R", culture) +
                ",\"fitterPosition\":[" + _fitterPosition.x.ToString("R", culture) + "," +
                    _fitterPosition.y.ToString("R", culture) + "]" +
                ",\"fitterSize\":[" + _fitterSize.x.ToString("R", culture) + "," +
                    _fitterSize.y.ToString("R", culture) + "]" +
                ",\"containerSourcePathId\":" + (_clippingProfile == null ? "0" :
                    _clippingProfile.containerSourcePathId.ToString(culture)) +
                ",\"fitterSourcePathId\":" + (_clippingProfile == null ? "0" :
                    _clippingProfile.fitterSourcePathId.ToString(culture)) +
                ",\"materialMode\":\"" + _quadMaterialMode + "\"" +
                ",\"shader\":\"" + (_quadMaterial == null || _quadMaterial.shader == null
                    ? "" : _quadMaterial.shader.name) + "\"" +
                ",\"shaderSupported\":" + (_quadMaterial != null &&
                    _quadMaterial.shader != null && _quadMaterial.shader.isSupported
                    ? "true" : "false") +
                ",\"textureColorSpace\":\"" + (_quadTexture == null
                    ? "" : TextureColorSpace(_quadTexture)) + "\"}";
        }

        private void EnsureQuad(string source)
        {
            Texture texture = LoadBackgroundTexture(source);
            if (texture == null)
            {
                Debug.LogWarning("[StoryBackground] Texture missing: " + source);
                return;
            }
            if (_quad == null)
            {
                _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _quad.name = "ADVBackground2D";
                Collider collider = _quad.GetComponent<Collider>();
                if (collider != null) collider.enabled = false;
                _quad.transform.SetParent(_camera.transform, false);
                Renderer renderer = _quad.GetComponent<Renderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            ConfigureQuadMaterial(source);
            _quadTexture = texture;
            _quadMaterial.mainTexture = texture;
            _quad.name = "ADVBackground2D__" + source;
            Debug.Log(string.Format(
                "[StoryBackground] Texture ready: {0} {1}x{2} format={3} graphicsFormat={4} activeColorSpace={5} materialMode={6} shader={7} supported={8}",
                texture.name, texture.width, texture.height, texture.GetType().Name,
                texture.graphicsFormat, TextureColorSpace(texture),
                _quadMaterialMode,
                _quadMaterial == null || _quadMaterial.shader == null
                    ? "<null>" : _quadMaterial.shader.name,
                _quadMaterial != null && _quadMaterial.shader != null &&
                    _quadMaterial.shader.isSupported));
        }

        private static string TextureColorSpace(Texture texture)
        {
            return texture == null
                ? "unknown"
                : GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat)
                    ? "sRGB" : "Linear";
        }

        private void ConfigureQuadMaterial(string source)
        {
            string[] commandLine = Environment.GetCommandLineArgs();
            bool requestSourceMaterial = Array.IndexOf(
                commandLine,
                "--source-story-background-material") >= 0;
            bool requestRecoveredMaterial = Array.IndexOf(
                commandLine, "--recovered-story-background-shader") >= 0;
            Material material = null;
            string mode = "unlit-texture";
            if (requestRecoveredMaterial)
            {
                Shader recoveredShader = Resources.Load<Shader>("AdvBackgroundPlate");
                if (recoveredShader != null && recoveredShader.isSupported)
                {
                    material = new Material(recoveredShader)
                    {
                        name = "ADVBackground2D__RecoveredUIBackground"
                    };
                    material.SetColor("_LightmapScaleColor", Color.white);
                    material.SetFloat("_RecoveredAmbientScale", 1f);
                    material.SetFloat("_RecoveredDirectScale", Array.IndexOf(
                        commandLine, "--recovered-story-background-direct") >= 0 ? 1f : 0f);
                    mode = Array.IndexOf(
                        commandLine, "--recovered-story-background-direct") >= 0
                            ? "recovered-ui-background-ambient-direct"
                            : "recovered-ui-background-ambient";
                }
                else
                {
                    Debug.LogWarning(string.Format(
                        "[StoryBackground] Recovered UIBackground shader unavailable: shader={0} supported={1}",
                        recoveredShader == null ? "<null>" : recoveredShader.name,
                        recoveredShader != null && recoveredShader.isSupported));
                }
            }
            if (requestSourceMaterial && material == null)
            {
                Material sourceMaterial = _catalog.LoadAll<Material>(source)
                    .Concat(_catalog.LoadObjectsWithSubAssets(source).OfType<Material>())
                    .FirstOrDefault(value => value != null &&
                        string.Equals(value.name, "UIBackground", StringComparison.Ordinal));
                if (sourceMaterial == null)
                {
                    try
                    {
                        GameObject prefab = _catalog.LoadPrefab(source);
                        foreach (Component component in prefab.GetComponentsInChildren<Component>(true))
                        {
                            if (component == null) continue;
                            System.Reflection.PropertyInfo property =
                                component.GetType().GetProperty("material");
                            if (property == null ||
                                !typeof(Material).IsAssignableFrom(property.PropertyType))
                                continue;
                            Material candidate = property.GetValue(component, null) as Material;
                            if (candidate == null ||
                                !string.Equals(candidate.name, "UIBackground", StringComparison.Ordinal))
                                continue;
                            sourceMaterial = candidate;
                            break;
                        }
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning(
                            "[StoryBackground] Source material prefab probe failed: " +
                            exception.Message);
                    }
                }
                if (sourceMaterial != null && sourceMaterial.shader != null &&
                    sourceMaterial.shader.isSupported &&
                    !string.Equals(sourceMaterial.shader.name,
                        "Hidden/InternalErrorShader", StringComparison.Ordinal))
                {
                    material = new Material(sourceMaterial)
                    {
                        name = "ADVBackground2D__SourceUIBackground"
                    };
                    mode = "source-ui-background";
                }
                else
                {
                    Debug.LogWarning(string.Format(
                        "[StoryBackground] Source UIBackground material unavailable or unsupported for {0}: material={1} shader={2} supported={3}",
                        source,
                        sourceMaterial == null ? "<null>" : sourceMaterial.name,
                        sourceMaterial == null || sourceMaterial.shader == null
                            ? "<null>" : sourceMaterial.shader.name,
                        sourceMaterial != null && sourceMaterial.shader != null &&
                            sourceMaterial.shader.isSupported));
                }
            }
            if (material == null)
            {
                Shader shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default");
                material = new Material(shader) { name = "ADVBackground2D__Material" };
            }
            if (_quadMaterial != null) UnityEngine.Object.DestroyImmediate(_quadMaterial);
            _quadMaterial = material;
            _quadMaterialMode = mode;
            Renderer renderer = _quad.GetComponent<Renderer>();
            renderer.sharedMaterial = _quadMaterial;
        }

        private Texture LoadBackgroundTexture(string source)
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
                foreach (Component component in prefab.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    System.Reflection.PropertyInfo property = component.GetType().GetProperty("texture");
                    if (property == null || !typeof(Texture).IsAssignableFrom(property.PropertyType)) continue;
                    texture = property.GetValue(component, null) as Texture;
                    if (texture != null) return texture;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[StoryBackground] Prefab texture probe failed: " + exception.Message);
            }

            return _catalog.LoadObjectsWithSubAssets(source).OfType<Texture>()
                .OrderByDescending(value => (long)value.width * value.height)
                .FirstOrDefault();
        }

        private bool IsStreamedScene(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            AssetBundle bundle;
            try { bundle = _catalog.GetBundle(source); }
            catch (KeyNotFoundException) { return false; }
            string[] paths = bundle.GetAllScenePaths();
            return paths != null && paths.Length > 0;
        }

        private SceneRecord EnsureScene(string source)
        {
            SceneRecord cached;
            if (_scenes.TryGetValue(source, out cached)) return cached;
            AssetBundle bundle = _catalog.GetBundle(source);
            string[] paths = bundle.GetAllScenePaths();
            if (paths == null || paths.Length == 0)
                throw new InvalidDataException("Background bundle exposes no scene: " + source);
            string path = paths[0];
            cached = new SceneRecord { source = source, path = path };
            _scenes[source] = cached;
            AsyncOperation operation = SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
            if (operation == null)
                throw new InvalidDataException("SceneManager rejected ADV scene: " + path);
            cached.operation = operation;
            operation.completed += ignored => CompleteSceneLoad(cached);
            Debug.Log("[StoryBackground] Loading additive scene: " + path);
            return cached;
        }

        private void CompleteSceneLoad(SceneRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.path)) return;
            string path = record.path;
            Scene scene = SceneManager.GetSceneByPath(path);
            if (!scene.IsValid() || !scene.isLoaded)
                scene = SceneManager.GetSceneByName(Path.GetFileNameWithoutExtension(path));
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError("[StoryBackground] Completed ADV scene was not found: " + path);
                return;
            }

            bool legacyOwnerContext = HasCommandLineArgument(
                "--legacy-story-scene-owner-context");
            Scene ownerScene = _ownerScene.IsValid() ? _ownerScene :
                SceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();
            GameObject container = new GameObject("ADVBackground3D__" + record.source);
            if (!legacyOwnerContext) SceneManager.MoveGameObjectToScene(container, scene);
            TraceSceneLightingContract(scene, ownerScene,
                roots.SelectMany(value => value.GetComponentsInChildren<Light>(true)).ToArray());
            foreach (GameObject root in roots) root.transform.SetParent(container.transform, true);
            TraceCanvasMonitorContract(container.transform);
            ConfigureEnvironmentDecals(record.source, container.transform);
            foreach (Camera camera in container.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
            foreach (AudioListener listener in container.GetComponentsInChildren<AudioListener>(true))
                listener.enabled = false;
            foreach (Collider collider in container.GetComponentsInChildren<Collider>(true)) collider.enabled = false;

            // Scene-authored volumes target the game's original HDRP extension
            // stack.  Several of those custom overrides have no executable
            // type in this reconstruction and poison the whole camera output
            // with the error-shader colour.  The Photo Studio already owns its
            // reconstructed post stack, so keep scene geometry and lights but
            // prevent the foreign volume stack from becoming global state.
            Volume[] volumes = container.GetComponentsInChildren<Volume>(true);
            foreach (Volume volume in volumes) volume.enabled = false;

            Renderer[] renderers = container.GetComponentsInChildren<Renderer>(true);
            Light[] sceneLights = container.GetComponentsInChildren<Light>(true);
            TraceSceneMaterialContract(renderers, sceneLights);
            int repaired = RepairSceneMaterials(
                record.source, renderers, sceneLights, record.ownedMaterials);
            Material[] materials = renderers
                .SelectMany(value => value.sharedMaterials)
                .Where(value => value != null)
                .Distinct()
                .ToArray();
            int unsupported = materials.Count(value =>
                value.shader == null || !value.shader.isSupported ||
                string.Equals(value.shader.name, "Hidden/InternalErrorShader", StringComparison.Ordinal));
            string shaderSummary = string.Join(", ", materials
                .GroupBy(value => value.shader == null ? "<null>" : value.shader.name)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(12)
                .Select(group => group.Key + "=" + group.Count()));
            record.scene = scene;
            record.ownerScene = ownerScene;
            record.root = container;
            record.operation = null;
            RefreshVisibility();
            Debug.Log(string.Format(
                "[StoryBackground] Loaded additive scene {0}: roots={1} renderers={2} volumesDisabled={3} repairedMaterials={4} unsupportedMaterials={5} shaders=[{6}]",
                path, roots.Length, renderers.Length, volumes.Length, repaired, unsupported, shaderSummary));
        }

        private void TraceCanvasMonitorContract(Transform sceneRoot)
        {
            if (!HasCommandLineArgument("--trace-story-monitor") || sceneRoot == null) return;
            const string sourceBundle = "fbx_mdl_env_cmn-parts_electronics-computer-00";
            foreach (Transform value in sceneRoot.GetComponentsInChildren<Transform>(true)
                         .Where(item => item != null &&
                             item.name.IndexOf("computer", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Component[] components = value.GetComponents<Component>();
                Debug.Log(string.Format(
                    "[StoryMonitor] sceneObject path={0} activeSelf={1} activeHierarchy={2} localPosition={3} localRotation={4} localScale={5} components=[{6}]",
                    HierarchyPath(value), value.gameObject.activeSelf,
                    value.gameObject.activeInHierarchy, value.localPosition,
                    value.localEulerAngles, value.localScale,
                    string.Join(",", components.Where(item => item != null)
                        .Select(item => item.GetType().FullName))));
            }

            foreach (Camera camera in sceneRoot.GetComponentsInChildren<Camera>(true))
            {
                Debug.Log(string.Format(
                    "[StoryMonitor] camera path={0} enabled={1} active={2} target={3} targetSize={4}x{5} targetFormat={6} depth={7:R} cullingMask=0x{8:X8}",
                    HierarchyPath(camera.transform), camera.enabled,
                    camera.gameObject.activeInHierarchy,
                    camera.targetTexture == null ? "<screen>" : camera.targetTexture.name,
                    camera.targetTexture == null ? 0 : camera.targetTexture.width,
                    camera.targetTexture == null ? 0 : camera.targetTexture.height,
                    camera.targetTexture == null ? "none" : camera.targetTexture.format.ToString(),
                    camera.depth, camera.cullingMask));
            }
            foreach (Canvas canvas in sceneRoot.GetComponentsInChildren<Canvas>(true))
            {
                Debug.Log(string.Format(
                    "[StoryMonitor] canvas path={0} enabled={1} active={2} mode={3} worldCamera={4} targetDisplay={5}",
                    HierarchyPath(canvas.transform), canvas.enabled,
                    canvas.gameObject.activeInHierarchy, canvas.renderMode,
                    canvas.worldCamera == null ? "<none>" : HierarchyPath(canvas.worldCamera.transform),
                    canvas.targetDisplay));
            }
            foreach (ReflectionProbe probe in sceneRoot.GetComponentsInChildren<ReflectionProbe>(true))
            {
                Texture texture = probe.texture;
                Debug.Log(string.Format(
                    "[StoryMonitor] reflectionProbe path={0} enabled={1} active={2} mode={3} importance={4} intensity={5:R} boxProjection={6} blendDistance={7:R} center={8} size={9} texture={10} textureSize={11}x{12}",
                    HierarchyPath(probe.transform), probe.enabled,
                    probe.gameObject.activeInHierarchy, probe.mode,
                    probe.importance, probe.intensity, probe.boxProjection,
                    probe.blendDistance, probe.center, probe.size,
                    texture == null ? "<none>" : texture.name,
                    texture == null ? 0 : texture.width,
                    texture == null ? 0 : texture.height));
            }
            foreach (Renderer renderer in sceneRoot.GetComponentsInChildren<Renderer>(true)
                         .Where(item => item != null &&
                             HierarchyPath(item.transform).IndexOf(
                                 "electronics-computer", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                List<ReflectionProbeBlendInfo> probes = new List<ReflectionProbeBlendInfo>();
                renderer.GetClosestReflectionProbes(probes);
                Debug.Log(string.Format(
                    "[StoryMonitor] rendererProbes path={0} usage={1} probes=[{2}]",
                    HierarchyPath(renderer.transform), renderer.reflectionProbeUsage,
                    string.Join(",", probes.Select(item =>
                        (item.probe == null ? "<null>" : HierarchyPath(item.probe.transform)) +
                        "@" + item.weight.ToString("R", CultureInfo.InvariantCulture)))));
            }

            try
            {
                GameObject[] prefabs = _catalog.LoadAll<GameObject>(sourceBundle);
                Debug.Log(string.Format(
                    "[StoryMonitor] sourceBundle={0} prefabs=[{1}]",
                    sourceBundle, string.Join(",", prefabs.Where(item => item != null)
                        .Select(item => item.name))));
                foreach (GameObject prefab in prefabs.Where(item => item != null &&
                             item.transform.parent == null))
                {
                    foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    {
                        Material[] materials = renderer.sharedMaterials;
                        Debug.Log(string.Format(
                            "[StoryMonitor] prefabRenderer root={0} path={1} activeSelf={2} activeHierarchy={3} localPosition={4} localRotation={5} localScale={6} renderer={7} mesh={8} materials=[{9}]",
                            prefab.name, HierarchyPath(renderer.transform),
                            renderer.gameObject.activeSelf, renderer.gameObject.activeInHierarchy,
                            renderer.transform.localPosition, renderer.transform.localEulerAngles,
                            renderer.transform.localScale, renderer.GetType().FullName,
                            RendererMeshName(renderer),
                            string.Join(",", materials.Select(MaterialDiagnostic))));
                        foreach (Material material in materials)
                        {
                            if (material == null) continue;
                            if (material.HasProperty("_LEDMap"))
                            {
                                Texture led = material.GetTexture("_LEDMap");
                                if (led != null) DumpMonitorTexture(
                                    led, "monitor-led-map-runtime.png");
                            }
                            if (string.Equals(material.name,
                                    "m_electronics_computer_00_std00",
                                    StringComparison.Ordinal))
                            {
                                foreach (string property in new[]
                                         { "_BaseMap", "_BumpMap", "_DefMap" })
                                {
                                    Texture texture = material.HasProperty(property)
                                        ? material.GetTexture(property) : null;
                                    if (texture != null) DumpMonitorTexture(texture,
                                        "computer-std00-" + property.TrimStart('_').ToLowerInvariant() +
                                        "-runtime.png");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[StoryMonitor] source prefab probe failed: " + exception);
            }
        }

        private static void ConfigureEnvironmentDecals(string sceneSource, Transform sceneRoot)
        {
            Shader.SetGlobalFloat(EnvironmentDecalCountId, 0f);
            if (sceneRoot == null || !string.Equals(
                    sceneSource, "env_3d_adv_classroom-00-00-noon",
                    StringComparison.OrdinalIgnoreCase))
                return;
            if (HasCommandLineArgument("--disable-story-environment-decals"))
            {
                Debug.Log("[StoryEnvironmentDecal] disabledByCommandLine=True");
                return;
            }

            DecalProjector[] projectors = sceneRoot
                .GetComponentsInChildren<DecalProjector>(true)
                .Where(value => value != null &&
                    value.gameObject.name.IndexOf(
                        "school-classroom-monitor", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(value => value.gameObject.name, StringComparer.Ordinal)
                .Take(MaximumEnvironmentDecals)
                .ToArray();
            if (projectors.Length == 0)
            {
                Debug.LogWarning("[StoryEnvironmentDecal] Classroom monitor projectors missing");
                return;
            }

            Matrix4x4[] worldToDecal = new Matrix4x4[MaximumEnvironmentDecals];
            Vector4[] uvScaleBias = new Vector4[MaximumEnvironmentDecals];
            Texture baseAtlas = null;
            Texture definitionAtlas = null;
            Vector4 definitionScale = new Vector4(0f, 1f, 1f, 1f);
            for (int index = 0; index < projectors.Length; index++)
            {
                DecalProjector projector = projectors[index];
                Matrix4x4 localToWorld;
                if (projector.scaleMode == DecalScaleMode.InheritFromHierarchy)
                {
                    localToWorld = projector.transform.localToWorldMatrix *
                        Matrix4x4.Rotate(Quaternion.Euler(-90f, 0f, 0f));
                }
                else
                {
                    localToWorld = Matrix4x4.TRS(
                        projector.transform.position,
                        projector.transform.rotation * Quaternion.Euler(-90f, 0f, 0f),
                        Vector3.one);
                }
                Vector3 sourceSize = projector.size;
                Vector3 sourcePivot = projector.pivot;
                Vector3 decalSize = new Vector3(
                    sourceSize.x, sourceSize.z, sourceSize.y);
                Vector3 decalOffset = new Vector3(
                    sourcePivot.x, -sourcePivot.z, sourcePivot.y);
                Matrix4x4 decalToWorld = localToWorld *
                    Matrix4x4.Translate(decalOffset) * Matrix4x4.Scale(decalSize);
                worldToDecal[index] = decalToWorld.inverse;
                uvScaleBias[index] = new Vector4(
                    projector.uvScale.x, projector.uvScale.y,
                    projector.uvBias.x, projector.uvBias.y);

                Material material = projector.material;
                if (material != null)
                {
                    if (baseAtlas == null && material.HasProperty("_BaseMap"))
                        baseAtlas = material.GetTexture("_BaseMap");
                    if (definitionAtlas == null && material.HasProperty("_DefMap"))
                        definitionAtlas = material.GetTexture("_DefMap");
                    if (material.HasProperty("_DefScale"))
                        definitionScale = material.GetVector("_DefScale");
                }
                // The built-in Photo Studio camera has no URP decal pass.  Keep
                // the source component as serialized evidence but prevent a
                // future renderer switch from drawing the same decal twice.
                projector.fadeFactor = 0f;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[StoryEnvironmentDecal] projector index={0} name={1} world=({2:R},{3:R},{4:R}) size=({5:R},{6:R},{7:R}) pivot=({8:R},{9:R},{10:R}) uvScaleBias=({11:R},{12:R},{13:R},{14:R})",
                    index, projector.gameObject.name,
                    projector.transform.position.x, projector.transform.position.y,
                    projector.transform.position.z,
                    sourceSize.x, sourceSize.y, sourceSize.z,
                    sourcePivot.x, sourcePivot.y, sourcePivot.z,
                    uvScaleBias[index].x, uvScaleBias[index].y,
                    uvScaleBias[index].z, uvScaleBias[index].w));
            }
            for (int index = projectors.Length; index < MaximumEnvironmentDecals; index++)
            {
                worldToDecal[index] = Matrix4x4.identity;
                uvScaleBias[index] = Vector4.zero;
            }

            Shader.SetGlobalMatrixArray(EnvironmentDecalWorldToDecalId, worldToDecal);
            Shader.SetGlobalVectorArray(EnvironmentDecalUvScaleBiasId, uvScaleBias);
            Shader.SetGlobalTexture(EnvironmentDecalBaseAtlasId,
                baseAtlas == null ? Texture2D.blackTexture : baseAtlas);
            Shader.SetGlobalTexture(EnvironmentDecalDefinitionAtlasId,
                definitionAtlas == null ? Texture2D.whiteTexture : definitionAtlas);
            Shader.SetGlobalVector(EnvironmentDecalDefinitionScaleId, definitionScale);
            Shader.SetGlobalFloat(EnvironmentDecalCountId,
                baseAtlas == null ? 0f : projectors.Length);
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[StoryEnvironmentDecal] configured count={0} base={1} definition={2} defScale=({3:R},{4:R},{5:R},{6:R}) exactProjectorContract=True",
                baseAtlas == null ? 0 : projectors.Length,
                baseAtlas == null ? "<missing>" : baseAtlas.name,
                definitionAtlas == null ? "<missing>" : definitionAtlas.name,
                definitionScale.x, definitionScale.y,
                definitionScale.z, definitionScale.w));
        }

        private void DumpMonitorTexture(Texture source, string fileName)
        {
            RenderTexture temporary = RenderTexture.GetTemporary(
                source.width, source.height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(source.width, source.height,
                    TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                readable.Apply(false, false);
                string directory = Path.Combine(
                    _catalog.StagingRoot, "research", "pass296-canvas-monitor");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, fileName);
                File.WriteAllBytes(path, readable.EncodeToPNG());
                Debug.Log("[StoryMonitor] dumpedTexture path=" + path);
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        private static string RendererMeshName(Renderer renderer)
        {
            MeshRenderer meshRenderer = renderer as MeshRenderer;
            if (meshRenderer != null)
            {
                MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                return filter == null || filter.sharedMesh == null ? "<none>" : filter.sharedMesh.name;
            }
            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            return skinned == null || skinned.sharedMesh == null ? "<none>" : skinned.sharedMesh.name;
        }

        private static string MaterialDiagnostic(Material material)
        {
            if (material == null) return "<null>";
            string shader = material.shader == null ? "<null>" : material.shader.name;
            List<string> textures = new List<string>();
            foreach (string property in material.GetTexturePropertyNames())
            {
                Texture texture = material.GetTexture(property);
                if (texture != null)
                    textures.Add(property + "=" + texture.name + ":" + texture.width + "x" + texture.height);
            }
            return material.name + "{" + shader + ",supported=" +
                   (material.shader != null && material.shader.isSupported) +
                   ",textures=[" + string.Join(";", textures) + "]}";
        }

        private static int RepairSceneMaterials(
            string sceneSource, Renderer[] renderers,
            Light[] sceneLights, List<Material> owned)
        {
            Shader fallback = Resources.Load<Shader>("AdvEnvironmentFallback");
            if (fallback == null) fallback = Shader.Find("Standard");
            if (fallback == null) return 0;
            bool legacyLighting = HasCommandLineArgument("--legacy-environment-fallback");
            bool calibratedClassroom = string.Equals(sceneSource,
                "env_3d_adv_classroom-00-00-noon",
                StringComparison.OrdinalIgnoreCase);
            float bakedScale = CommandLineFloat(
                "--story-environment-baked-scale=", calibratedClassroom ? 0.75f : 1f);
            float bakedChroma = CommandLineFloat(
                "--story-environment-baked-chroma=", calibratedClassroom ? 0f : 1f);
            float directScale = CommandLineFloat(
                "--story-environment-direct-scale=", calibratedClassroom ? 0.25f : 1f);
            float reflectionScale = CommandLineFloat(
                "--story-environment-reflection-scale=", calibratedClassroom ? 0.25f : 1f);
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[StoryBackgroundLighting] fallbackProfile source={0} calibratedClassroom={1} exact=False bakedScale={2:R} bakedChroma={3:R} directScale={4:R} reflectionScale={5:R}",
                sceneSource, calibratedClassroom, bakedScale, bakedChroma,
                directScale, reflectionScale));
            Light sourceMainLight = sceneLights
                .Where(value => value != null && value.type == LightType.Directional &&
                    value.enabled && value.gameObject.activeInHierarchy)
                .OrderByDescending(value => value.intensity)
                .FirstOrDefault();
            float useSourceMainLight = sourceMainLight == null ? 0f : CommandLineFloat(
                "--story-environment-source-main-light=", 1f);
            Vector3 sourceMainDirection = sourceMainLight == null
                ? Vector3.up : -sourceMainLight.transform.forward;
            Color sourceMainColor = sourceMainLight == null
                ? Color.black : sourceMainLight.color.linear * sourceMainLight.intensity;
            Light[] sourceSpots = sceneLights
                .Where(value => value != null && value.type == LightType.Spot &&
                    value.enabled && value.gameObject.activeInHierarchy)
                .OrderBy(value => HierarchyPath(value.transform), StringComparer.Ordinal)
                .Take(8)
                .ToArray();
            bool useSourceSpots = CommandLineFloat(
                "--story-environment-source-spots=", 1f) > 0.5f;
            LightmapData[] activeLightmaps = LightmapSettings.lightmaps;
            Texture sourceShadowMask = activeLightmaps == null ? null :
                activeLightmaps.Where(value => value != null)
                    .Select(value => value.shadowMask)
                    .FirstOrDefault(value => value != null);
            bool hasSourceShadowMask = sourceShadowMask != null;
            bool useSourceShadowMask = calibratedClassroom && hasSourceShadowMask &&
                HasCommandLineArgument("--enable-story-environment-shadowmask") &&
                !HasCommandLineArgument("--disable-story-environment-shadowmask");
            bool useSourceRealtimeShadow = calibratedClassroom && CommandLineFloat(
                "--story-environment-source-realtime-shadow=", 0f) > 0.5f;
            Vector4 sourceMainOcclusionSelector = sourceMainLight == null
                ? Vector4.zero
                : OcclusionChannelSelector(
                    sourceMainLight.bakingOutput.occlusionMaskChannel);
            Vector4[] sourceSpotPositionRange = new Vector4[8];
            Vector4[] sourceSpotDirectionScale = new Vector4[8];
            Vector4[] sourceSpotColorOffset = new Vector4[8];
            Vector4[] sourceSpotOcclusionSelector = new Vector4[8];
            for (int index = 0; index < sourceSpots.Length; index++)
            {
                Light spot = sourceSpots[index];
                Vector3 position = spot.transform.position;
                Vector3 direction = spot.transform.forward;
                float range = Mathf.Max(0.001f, spot.range);
                float outerCos = Mathf.Cos(0.5f * Mathf.Deg2Rad * spot.spotAngle);
                float innerAngle = spot.innerSpotAngle > 0f
                    ? spot.innerSpotAngle : spot.spotAngle * 0.8f;
                float innerCos = Mathf.Cos(0.5f * Mathf.Deg2Rad * innerAngle);
                float angleScale = 1f / Mathf.Max(0.001f, innerCos - outerCos);
                float angleOffset = -outerCos * angleScale;
                Color color = spot.color.linear * spot.intensity;
                sourceSpotPositionRange[index] = new Vector4(
                    position.x, position.y, position.z, 1f / (range * range));
                sourceSpotDirectionScale[index] = new Vector4(
                    direction.x, direction.y, direction.z, angleScale);
                sourceSpotColorOffset[index] = new Vector4(
                    color.r, color.g, color.b, angleOffset);
                sourceSpotOcclusionSelector[index] = OcclusionChannelSelector(
                    spot.bakingOutput.occlusionMaskChannel);
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[StoryBackgroundLighting] fallbackSourceSpot index={0} name={1} use={2} position=[{3:R},{4:R},{5:R}] direction=[{6:R},{7:R},{8:R}] color=[{9:R},{10:R},{11:R}] range={12:R} inner={13:R} outer={14:R} occlusionMaskChannel={15} selector=[{16:R},{17:R},{18:R},{19:R}]",
                    index, spot.name, useSourceSpots,
                    position.x, position.y, position.z,
                    direction.x, direction.y, direction.z,
                    color.r, color.g, color.b, range,
                    innerAngle, spot.spotAngle,
                    spot.bakingOutput.occlusionMaskChannel,
                    sourceSpotOcclusionSelector[index].x,
                    sourceSpotOcclusionSelector[index].y,
                    sourceSpotOcclusionSelector[index].z,
                    sourceSpotOcclusionSelector[index].w));
            }
            if (sourceMainLight != null)
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[StoryBackgroundLighting] fallbackSourceMain name={0} use={1:R} direction=[{2:R},{3:R},{4:R}] color=[{5:R},{6:R},{7:R}] intensity={8:R} bakeType={9} mixedMode={10} occlusionMaskChannel={11} selector=[{12:R},{13:R},{14:R},{15:R}]",
                    sourceMainLight.name, useSourceMainLight,
                    sourceMainDirection.x, sourceMainDirection.y, sourceMainDirection.z,
                    sourceMainColor.r, sourceMainColor.g, sourceMainColor.b,
                    sourceMainLight.intensity,
                    sourceMainLight.bakingOutput.lightmapBakeType,
                    sourceMainLight.bakingOutput.mixedLightingMode,
                    sourceMainLight.bakingOutput.occlusionMaskChannel,
                    sourceMainOcclusionSelector.x,
                    sourceMainOcclusionSelector.y,
                    sourceMainOcclusionSelector.z,
                    sourceMainOcclusionSelector.w));
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[StoryBackgroundLighting] fallbackSourceShadowMask available={0} use={1} texture={2} exactStaticOcclusion={3} combine=min(realtime,baked) enable=--enable-story-environment-shadowmask control=--disable-story-environment-shadowmask",
                hasSourceShadowMask, useSourceShadowMask,
                sourceShadowMask == null ? "<none>" : sourceShadowMask.name,
                useSourceShadowMask));
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[StoryBackgroundLighting] fallbackSourceRealtimeShadow use={0} stockForwardBase=True flag=--story-environment-source-realtime-shadow=",
                useSourceRealtimeShadow));

            Dictionary<Material, Material> replacements = new Dictionary<Material, Material>();
            int repaired = 0;
            foreach (Renderer renderer in renderers)
            {
                Material[] values = renderer.sharedMaterials;
                bool changed = false;
                for (int index = 0; index < values.Length; index++)
                {
                    Material source = values[index];
                    if (source == null || source.shader == null) continue;
                    string shaderName = source.shader.name;
                    bool environment = shaderName.StartsWith("Environment/", StringComparison.OrdinalIgnoreCase);
                    bool particle = shaderName.StartsWith("Campus/Effect/", StringComparison.OrdinalIgnoreCase);
                    if (!environment && !particle) continue;

                    Material replacement;
                    if (!replacements.TryGetValue(source, out replacement))
                    {
                        replacement = new Material(fallback)
                        {
                            name = source.name + "__adv-environment-fallback"
                        };
                        CopyFirstTexture(source, replacement,
                            new[] { "_BaseMap", "_MainTex", "_BaseColorMap", "_BaseTex", "_Albedo" },
                            "_MainTex");
                        Color color = FirstColor(source,
                            new[] { "_BaseColor", "_Color", "_TintColor" }, Color.white);
                        replacement.SetColor("_Color", color);
                        bool hasNormal = CopyFirstTexture(source, replacement,
                            new[] { "_BumpMap", "_NormalMap" }, "_BumpMap");
                        bool hasDefinition = CopyFirstTexture(source, replacement,
                            new[] { "_DefMap", "_MaskMap", "_MetallicGlossMap" }, "_DefMap");
                        CopyFirstTexture(source, replacement,
                            new[] { "_EmissionMap", "_EmissionTex" }, "_EmissionMap");
                        Color emission = FirstColor(source,
                            new[] { "_EmissionColor", "_EmissiveColor" }, Color.white);
                        replacement.SetColor("_EmissionColor", emission);

                        Vector4 tiling = FirstVector(source,
                            new[] { "_Tiling" }, new Vector4(1f, 1f, 0f, 0f));
                        Vector4 offset = FirstVector(source,
                            new[] { "_Offset" }, Vector4.zero);
                        replacement.SetVector("_Tiling", tiling);
                        replacement.SetVector("_Offset", offset);
                        replacement.SetVector("_DefValue", FirstVector(source,
                            new[] { "_DefValue", "_DefScale" }, Vector4.one));
                        replacement.SetFloat("_BumpScale", FirstFloat(source,
                            new[] { "_BumpScale" }, 1f));
                        replacement.SetFloat("_EmissionAlbedoScale", FirstFloat(source,
                            new[] { "_EmissionAlbedoScale" }, 0f));

                        bool cutout = shaderName.EndsWith("Vegetation", StringComparison.OrdinalIgnoreCase) ||
                            source.IsKeywordEnabled("_ALPHATEST_ON") ||
                            (source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip") > 0.5f);
                        bool transparent = particle || source.renderQueue >= 3000 ||
                            source.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT");
                        float sourceBlend = FirstFloat(source, new[] { "_SrcBlend" },
                            transparent ? 5f : 1f);
                        float destinationBlend = FirstFloat(source, new[] { "_DstBlend" },
                            transparent ? 10f : 0f);
                        float alphaSourceBlend = FirstFloat(source,
                            new[] { "_AlphaSrcBlend", "_SrcBlendAlpha" }, sourceBlend);
                        float alphaDestinationBlend = FirstFloat(source,
                            new[] { "_AlphaDstBlend", "_DstBlendAlpha" }, destinationBlend);
                        bool premultiply = source.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ||
                            (transparent && Mathf.Approximately(sourceBlend, 1f) &&
                                Mathf.Approximately(destinationBlend, 10f));
                        bool sourceDefault = string.Equals(shaderName, "Environment/Default",
                            StringComparison.OrdinalIgnoreCase);
                        bool sourceEmission = string.Equals(shaderName, "Environment/Emission",
                            StringComparison.OrdinalIgnoreCase);
                        bool sourceVegetation = string.Equals(shaderName, "Environment/Vegetation",
                            StringComparison.OrdinalIgnoreCase);
                        replacement.SetFloat("_UseAlphaClip", cutout ? 1f : 0f);
                        replacement.SetFloat("_Cutoff", source.HasProperty("_Cutoff")
                            ? source.GetFloat("_Cutoff") : 0.5f);
                        replacement.SetFloat("_Cull",
                            shaderName.EndsWith("Vegetation", StringComparison.OrdinalIgnoreCase) ? 0f :
                            source.HasProperty("_Cull") ? source.GetFloat("_Cull") : 2f);
                        replacement.SetFloat("_SrcBlend", sourceBlend);
                        replacement.SetFloat("_DstBlend", destinationBlend);
                        replacement.SetFloat("_AlphaSrcBlend", alphaSourceBlend);
                        replacement.SetFloat("_AlphaDstBlend", alphaDestinationBlend);
                        replacement.SetFloat("_ZWrite", FirstFloat(source,
                            new[] { "_ZWrite" }, transparent ? 0f : 1f));
                        replacement.SetFloat("_AlphaToMask", FirstFloat(source,
                            new[] { "_AlphaToMask" }, 0f));
                        replacement.SetFloat("_UseEmission",
                            sourceEmission ? 1f : 0f);
                        replacement.SetFloat("_UseDefinition",
                            (sourceDefault || sourceVegetation) && hasDefinition ? 1f : 0f);
                        replacement.SetFloat("_UseNormal",
                            (sourceDefault || sourceVegetation) && hasNormal ? 1f : 0f);
                        replacement.SetFloat("_ShaderFamily", sourceDefault ? 0f :
                            sourceEmission ? 1f : sourceVegetation ? 2f : 3f);
                        replacement.SetFloat("_Premultiply", premultiply ? 1f : 0f);
                        replacement.SetFloat("_EnvironmentReflections", FirstFloat(source,
                            new[] { "_EnvironmentReflections" }, 1f));
                        replacement.SetFloat("_SpecularHighlights", FirstFloat(source,
                            new[] { "_SpecularHighlights" }, 1f));
                        replacement.SetFloat("_LegacyLighting", legacyLighting ? 1f : 0f);
                        replacement.SetFloat("_EnvironmentBakedScale", bakedScale);
                        replacement.SetFloat("_EnvironmentBakedChroma", bakedChroma);
                        replacement.SetFloat("_EnvironmentDirectScale", directScale);
                        replacement.SetFloat("_EnvironmentReflectionScale", reflectionScale);
                        replacement.SetFloat("_EnvironmentUseSourceMainLight", useSourceMainLight);
                        replacement.SetVector("_EnvironmentSourceMainLightDirection",
                            new Vector4(sourceMainDirection.x, sourceMainDirection.y,
                                sourceMainDirection.z, 0f));
                        replacement.SetVector("_EnvironmentSourceMainLightColor",
                            new Vector4(sourceMainColor.r, sourceMainColor.g,
                                sourceMainColor.b, 1f));
                        replacement.SetFloat("_EnvironmentSourceSpotCount",
                            useSourceSpots ? sourceSpots.Length : 0f);
                        replacement.SetVectorArray("_EnvironmentSourceSpotPositionRange",
                            sourceSpotPositionRange);
                        replacement.SetVectorArray("_EnvironmentSourceSpotDirectionScale",
                            sourceSpotDirectionScale);
                        replacement.SetVectorArray("_EnvironmentSourceSpotColorOffset",
                            sourceSpotColorOffset);
                        replacement.SetVectorArray("_EnvironmentSourceSpotOcclusionSelector",
                            sourceSpotOcclusionSelector);
                        replacement.SetFloat("_EnvironmentUseSourceShadowMask",
                            useSourceShadowMask ? 1f : 0f);
                        replacement.SetFloat("_EnvironmentUseSourceRealtimeShadow",
                            useSourceRealtimeShadow ? 1f : 0f);
                        if (sourceShadowMask != null)
                            replacement.SetTexture("_EnvironmentSourceShadowMask",
                                sourceShadowMask);
                        replacement.SetVector("_EnvironmentSourceMainOcclusionSelector",
                            sourceMainOcclusionSelector);
                        replacement.SetFloat("_EnvironmentDebugMode", CommandLineFloat(
                            "--story-environment-debug=", 0f));
                        replacement.renderQueue = source.renderQueue >= 0 ? source.renderQueue :
                            transparent ? 3000 : cutout ? 2450 : 2000;
                        replacements[source] = replacement;
                        owned.Add(replacement);
                        repaired++;
                    }
                    values[index] = replacement;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = values;
            }
            return repaired;
        }

        private static Vector4 OcclusionChannelSelector(int channel)
        {
            switch (channel)
            {
                case 0: return new Vector4(1f, 0f, 0f, 0f);
                case 1: return new Vector4(0f, 1f, 0f, 0f);
                case 2: return new Vector4(0f, 0f, 1f, 0f);
                case 3: return new Vector4(0f, 0f, 0f, 1f);
                default: return Vector4.zero;
            }
        }

        private static bool CopyFirstTexture(
            Material source, Material target, IEnumerable<string> names, string targetName)
        {
            foreach (string name in names)
            {
                if (!source.HasProperty(name)) continue;
                Texture texture = source.GetTexture(name);
                if (texture == null) continue;
                target.SetTexture(targetName, texture);
                target.SetTextureScale(targetName, source.GetTextureScale(name));
                target.SetTextureOffset(targetName, source.GetTextureOffset(name));
                return true;
            }
            return false;
        }

        private static Color FirstColor(
            Material source, IEnumerable<string> names, Color fallback)
        {
            foreach (string name in names)
                if (source.HasProperty(name)) return source.GetColor(name);
            return fallback;
        }

        private static Vector4 FirstVector(
            Material source, IEnumerable<string> names, Vector4 fallback)
        {
            foreach (string name in names)
                if (source.HasProperty(name)) return source.GetVector(name);
            return fallback;
        }

        private static float FirstFloat(
            Material source, IEnumerable<string> names, float fallback)
        {
            foreach (string name in names)
                if (source.HasProperty(name)) return source.GetFloat(name);
            return fallback;
        }

        private static bool HasCommandLineArgument(string value)
        {
            return Array.IndexOf(Environment.GetCommandLineArgs(), value) >= 0;
        }

        private static float CommandLineFloat(string prefix, float fallback)
        {
            string argument = Environment.GetCommandLineArgs().FirstOrDefault(value =>
                value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            float parsed;
            return argument != null && float.TryParse(
                argument.Substring(prefix.Length), NumberStyles.Float,
                CultureInfo.InvariantCulture, out parsed)
                ? parsed : fallback;
        }

        private static void TraceSceneMaterialContract(Renderer[] renderers, Light[] lights)
        {
            bool traceMaterials = HasCommandLineArgument("--trace-story-scene-materials");
            bool traceRenderers = HasCommandLineArgument("--trace-story-scene-renderers");
            if (!traceMaterials && !traceRenderers) return;
            Material[] sources = renderers
                .SelectMany(value => value.sharedMaterials)
                .Where(value => value != null)
                .Distinct()
                .ToArray();
            string lightmaps = string.Join(",", renderers
                .GroupBy(value => value.lightmapIndex)
                .OrderBy(value => value.Key)
                .Select(value => value.Key + "=" + value.Count()));
            string lightSummary = string.Join(",", lights
                .GroupBy(value => value.type)
                .OrderBy(value => value.Key)
                .Select(value => value.Key + "=" + value.Count()));
            Debug.Log(string.Format(
                "[StoryBackgroundMaterial] sources={0} renderers={1} lightmapIndices=[{2}] lights=[{3}]",
                sources.Length, renderers.Length, lightmaps, lightSummary));
            if (traceRenderers)
            {
                foreach (Renderer renderer in renderers
                    .Where(value => value != null)
                    .OrderBy(value => HierarchyPath(value.transform), StringComparer.Ordinal))
                {
                    Vector4 st = renderer.lightmapScaleOffset;
                    Bounds bounds = renderer.bounds;
                    string materials = string.Join(",", renderer.sharedMaterials
                        .Where(value => value != null)
                        .Select(value => value.name));
                    Mesh mesh = null;
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null) mesh = filter.sharedMesh;
                    SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                    if (skinned != null) mesh = skinned.sharedMesh;
                    Debug.Log(string.Format(CultureInfo.InvariantCulture,
                        "[StoryBackgroundRenderer] path={0} type={1} active={2} enabled={3} lightmapIndex={4} lightmapST=[{5:R},{6:R},{7:R},{8:R}] realtimeLightmapIndex={9} lightProbeUsage={10} reflectionProbeUsage={11} probeAnchor={12} receiveShadows={13} boundsCenter=[{14:R},{15:R},{16:R}] boundsSize=[{17:R},{18:R},{19:R}] mesh={20} materials=[{21}]",
                        HierarchyPath(renderer.transform), renderer.GetType().Name,
                        renderer.gameObject.activeInHierarchy, renderer.enabled,
                        renderer.lightmapIndex, st.x, st.y, st.z, st.w,
                        renderer.realtimeLightmapIndex, renderer.lightProbeUsage,
                        renderer.reflectionProbeUsage,
                        renderer.probeAnchor == null
                            ? "<none>" : HierarchyPath(renderer.probeAnchor),
                        renderer.receiveShadows,
                        bounds.center.x, bounds.center.y, bounds.center.z,
                        bounds.size.x, bounds.size.y, bounds.size.z,
                        mesh == null ? "<none>" : mesh.name, materials));
                }
            }
            if (!traceMaterials) return;
            foreach (Material source in sources.OrderBy(value => value.name, StringComparer.Ordinal))
            {
                string shaderName = source.shader == null ? "<null>" : source.shader.name;
                string keywords = string.Join(",", source.shaderKeywords.OrderBy(value => value));
                Debug.Log(string.Format(
                    "[StoryBackgroundMaterial] name={0} shader={1} queue={2} keywords=[{3}] base={4} bump={5} def={6}",
                    source.name, shaderName, source.renderQueue, keywords,
                    TextureName(source, new[] { "_BaseMap", "_MainTex" }),
                    TextureName(source, new[] { "_BumpMap", "_NormalMap" }),
                    TextureName(source, new[] { "_DefMap", "_MaskMap" })));
            }
        }

        private static void TraceSceneLightingContract(
            Scene sourceScene, Scene ownerScene, Light[] lights)
        {
            if (!HasCommandLineArgument("--trace-story-scene-lighting") &&
                !HasCommandLineArgument("--trace-story-scene-materials")) return;

            Scene previous = SceneManager.GetActiveScene();
            Debug.Log("[StoryBackgroundLighting] owner " +
                SceneLightingSummary(ownerScene, false));
            bool switched = sourceScene.IsValid() && sourceScene.isLoaded &&
                SceneManager.SetActiveScene(sourceScene);
            Debug.Log("[StoryBackgroundLighting] source " +
                SceneLightingSummary(sourceScene, switched));
            foreach (Light light in lights
                .Where(value => value != null)
                .OrderBy(value => value.name, StringComparer.Ordinal))
            {
                Transform transform = light.transform;
                LightBakingOutput baking = light.bakingOutput;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[StoryBackgroundLighting] light={0} path={1} type={2} enabled={3} active={4} color=[{5:R},{6:R},{7:R},{8:R}] intensity={9:R} range={10:R} spotAngle={11:R} shadows={12} shadowStrength={13:R} bakeType={14} mixedMode={15} occlusionMaskChannel={16} probeOcclusionLightIndex={17} position=[{18:R},{19:R},{20:R}] rotation=[{21:R},{22:R},{23:R}]",
                    light.name, HierarchyPath(transform), light.type, light.enabled,
                    light.gameObject.activeInHierarchy,
                    light.color.r, light.color.g, light.color.b, light.color.a,
                    light.intensity, light.range, light.spotAngle, light.shadows,
                    light.shadowStrength, baking.lightmapBakeType,
                    baking.mixedLightingMode, baking.occlusionMaskChannel,
                    baking.probeOcclusionLightIndex,
                    transform.position.x, transform.position.y, transform.position.z,
                    transform.eulerAngles.x, transform.eulerAngles.y,
                    transform.eulerAngles.z));
            }
            if (switched && previous.IsValid() && previous.isLoaded)
                SceneManager.SetActiveScene(previous);
            Debug.Log("[StoryBackgroundLighting] restored " +
                SceneLightingSummary(previous, true));
        }

        private static string SceneLightingSummary(Scene scene, bool active)
        {
            LightmapData[] lightmaps = LightmapSettings.lightmaps;
            string lightmapTextures = lightmaps == null ? string.Empty :
                string.Join(",", lightmaps.Select((value, index) =>
                    index + ":" +
                    TextureContractSummary(value == null ? null : value.lightmapColor) + "/" +
                    TextureContractSummary(value == null ? null : value.lightmapDir) + "/" +
                    TextureContractSummary(value == null ? null : value.shadowMask)));
            Material skybox = RenderSettings.skybox;
            string customReflectionName = "<none>";
            try
            {
                Cubemap customReflection = RenderSettings.customReflection;
                if (customReflection != null) customReflectionName = customReflection.name;
            }
            catch (ArgumentException)
            {
                customReflectionName = "<non-cubemap>";
            }
            Light sun = RenderSettings.sun;
            SphericalHarmonicsL2 ambientProbe = RenderSettings.ambientProbe;
            string ambientProbeSummary = string.Join(",", Enumerable.Range(0, 9)
                .Select(index => string.Format(CultureInfo.InvariantCulture,
                    "{0}:[{1:R},{2:R},{3:R}]", index,
                    ambientProbe[0, index], ambientProbe[1, index],
                    ambientProbe[2, index])));
            return string.Format(CultureInfo.InvariantCulture,
                "scene={0} active={1} ambientMode={2} ambientIntensity={3:R} ambientSky=[{4:R},{5:R},{6:R},{7:R}] ambientEquator=[{8:R},{9:R},{10:R},{11:R}] ambientGround=[{12:R},{13:R},{14:R},{15:R}] ambientLight=[{16:R},{17:R},{18:R},{19:R}] reflectionMode={20} reflectionIntensity={21:R} reflectionBounces={22} customReflection={23} skybox={24} sun={25} fog={26} fogColor=[{27:R},{28:R},{29:R},{30:R}] fogMode={31} fogDensity={32:R} fogStart={33:R} fogEnd={34:R} lightmapsMode={35} lightmaps={36} lightmapTextures=[{37}] probes={38} ambientProbe=[{39}]",
                scene.IsValid() ? scene.name : "<invalid>", active,
                RenderSettings.ambientMode, RenderSettings.ambientIntensity,
                RenderSettings.ambientSkyColor.r, RenderSettings.ambientSkyColor.g,
                RenderSettings.ambientSkyColor.b, RenderSettings.ambientSkyColor.a,
                RenderSettings.ambientEquatorColor.r, RenderSettings.ambientEquatorColor.g,
                RenderSettings.ambientEquatorColor.b, RenderSettings.ambientEquatorColor.a,
                RenderSettings.ambientGroundColor.r, RenderSettings.ambientGroundColor.g,
                RenderSettings.ambientGroundColor.b, RenderSettings.ambientGroundColor.a,
                RenderSettings.ambientLight.r, RenderSettings.ambientLight.g,
                RenderSettings.ambientLight.b, RenderSettings.ambientLight.a,
                RenderSettings.defaultReflectionMode, RenderSettings.reflectionIntensity,
                RenderSettings.reflectionBounces,
                customReflectionName,
                skybox == null ? "<none>" : skybox.name,
                sun == null ? "<none>" : sun.name,
                RenderSettings.fog,
                RenderSettings.fogColor.r, RenderSettings.fogColor.g,
                RenderSettings.fogColor.b, RenderSettings.fogColor.a,
                RenderSettings.fogMode, RenderSettings.fogDensity,
                RenderSettings.fogStartDistance, RenderSettings.fogEndDistance,
                LightmapSettings.lightmapsMode,
                lightmaps == null ? 0 : lightmaps.Length,
                lightmapTextures,
                LightmapSettings.lightProbes == null
                    ? 0 : LightmapSettings.lightProbes.count,
                ambientProbeSummary);
        }

        private static string TextureContractSummary(Texture texture)
        {
            if (texture == null) return "<none>";
            Texture2D texture2D = texture as Texture2D;
            return string.Format(CultureInfo.InvariantCulture,
                "{0}#id{1}:{2}x{3}:gfx={4}:fmt={5}:mips={6}:readable={7}",
                texture.name, texture.GetInstanceID(), texture.width, texture.height,
                texture.graphicsFormat,
                texture2D == null ? "<non-2d>" : texture2D.format.ToString(),
                texture.mipmapCount,
                texture2D != null && texture2D.isReadable);
        }

        private static string HierarchyPath(Transform transform)
        {
            if (transform == null) return "<null>";
            List<string> names = new List<string>();
            for (Transform current = transform; current != null; current = current.parent)
                names.Add(current.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private static string TextureName(Material source, IEnumerable<string> names)
        {
            foreach (string name in names)
            {
                if (!source.HasProperty(name)) continue;
                Texture texture = source.GetTexture(name);
                if (texture != null) return texture.name;
            }
            return "<none>";
        }

        private void RefreshVisibility()
        {
            bool show2D = _enabled && !string.IsNullOrEmpty(_activeSource) &&
                !IsStreamedScene(_activeSource);
            if (_quad != null) _quad.SetActive(show2D);
            foreach (SceneRecord record in _scenes.Values)
            {
                bool active = _enabled &&
                    string.Equals(record.source, _activeSource, StringComparison.OrdinalIgnoreCase);
                if (record.root != null) record.root.SetActive(active);
            }

            if (!HasCommandLineArgument("--legacy-story-scene-owner-context"))
            {
                Scene target = _ownerScene;
                SceneRecord activeRecord;
                if (_enabled && !string.IsNullOrEmpty(_activeSource) &&
                    _scenes.TryGetValue(_activeSource, out activeRecord) &&
                    activeRecord.scene.IsValid() && activeRecord.scene.isLoaded &&
                    activeRecord.root != null)
                    target = activeRecord.scene;
                if (target.IsValid() && target.isLoaded &&
                    SceneManager.GetActiveScene() != target)
                {
                    SceneManager.SetActiveScene(target);
                    if (HasCommandLineArgument("--trace-story-scene-lighting"))
                        Debug.Log("[StoryBackgroundLighting] activated " +
                            SceneLightingSummary(target, true));
                }
            }
        }

        private static Vector2 PositionOf(StoryTransform2D value)
        {
            return value == null || value.position == null
                ? Vector2.zero : value.position.ToVector2();
        }

        private static Vector2 ScaleOf(StoryTransform2D value)
        {
            return value == null || value.scale == null
                ? Vector2.one : value.scale.ToVector2();
        }

        public void Dispose()
        {
            Shader.SetGlobalFloat(EnvironmentDecalCountId, 0f);
            if (_ownerScene.IsValid() && _ownerScene.isLoaded &&
                SceneManager.GetActiveScene() != _ownerScene)
                SceneManager.SetActiveScene(_ownerScene);
            if (_quadMaterial != null) UnityEngine.Object.DestroyImmediate(_quadMaterial);
            if (_quad != null) UnityEngine.Object.DestroyImmediate(_quad);
            _quadMaterial = null;
            _quad = null;
            _quadTexture = null;
            foreach (SceneRecord record in _scenes.Values)
            {
                if (record.root != null) UnityEngine.Object.DestroyImmediate(record.root);
                foreach (Material material in record.ownedMaterials)
                    if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (record.scene.IsValid() && record.scene.isLoaded)
                    SceneManager.UnloadSceneAsync(record.scene);
            }
            _scenes.Clear();
            _declarations.Clear();
        }
    }
}
