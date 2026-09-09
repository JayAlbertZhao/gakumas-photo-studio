using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    [DefaultExecutionOrder(-200)]
    [RequireComponent(typeof(Camera))]
    public sealed class CapturedActorShadowMap : MonoBehaviour
    {
        public enum DirectionalMode
        {
            Fixed = 0,
            Matcap = 1,
            ShadowLight = 2
        }

        public const int Resolution = 4096;

        // The captured GPA basis transformed through the inverse captured actor-root
        // rotation. It is orthonormal and independent of the photo camera orbit.
        private static readonly Vector3 CapturedU = new Vector3(
            -0.98472745f, -0.02223584f, -0.17267733f).normalized;
        private static readonly Vector3 CapturedV = new Vector3(
             0.01523349f,  0.97700275f, -0.21268184f).normalized;
        private static readonly Vector3 CapturedDepth = new Vector3(
            -0.17343537f,  0.21206414f,  0.96174269f).normalized;
        private static readonly Vector4 CapturedCasterBias = new Vector4(
            -0.00079697941f, -0.00019924485f, 1f, 0f);
        // The first three coefficients of the captured world-to-UV row have
        // magnitude 1/spanU. Native GetShadowBias scales both depth and normal
        // offsets by 1/projection.m00, so arbitrary projections use the same
        // exact captured bias scaled by their horizontal world span.
        private static readonly float CapturedProjectionSpanU = 1f /
            new Vector3(-1.50826922f, 0.01754881f, -0.22146318f).magnitude;
        private static readonly Vector3[] FrustumCorners = new Vector3[4];

        // Original Actor world-to-shadow rows multiplied by the captured
        // Actor local-to-world matrix.  Unlike the basis-only reconstruction,
        // this retains the exact projection scale and translation of the GPA
        // frame.  It intentionally covers the photographed upper body rather
        // than wasting most of the 4096 surface on off-screen legs.
        private static readonly Matrix4x4 CapturedActorLocalToShadow = new Matrix4x4(
            new Vector4(-1.50826922f,  0.01754881f, -0.22146318f, 0f),
            new Vector4(-0.03405778f,  1.12549651f,  0.27078906f, 0f),
            new Vector4(-0.26448324f, -0.24500716f,  1.22806899f, 0f),
            new Vector4( 0.51385475f, -0.88786421f,  0.27247787f, 1f));

        private Camera _sourceCamera;
        private Camera _shadowCamera;
        private Shader _replacementShader;
        private RenderTexture _shadowMap;
        private GameObject _actorRoot;
        private Renderer[] _casters = Array.Empty<Renderer>();
        private bool _projectionValid;
        private int _rendererSignature;
        private int _fitAfterFrame;
        private int _casterSubmeshDrawCount;
        private int _dynamicProjectionUpdates;
        private Vector3 _dynamicProjectionMinSpan = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        private Vector3 _dynamicProjectionMaxSpan = Vector3.zero;
        private bool _loggedDynamicProjection;
        private bool _loggedExactContract;
        private bool _runtimeProfileActive;
        private DirectionalMode _directionalMode = DirectionalMode.Fixed;
        private Vector2 _lightDirectional = Vector2.zero;
        private Vector2 _mainLightAngle = Vector2.zero;
        private int _mainLightSpace;
        private float _shadowStrength = 1f;
        private float _useOffset;
        private float _facePartsShadowStrength;
        private string _runtimeProfileSource = string.Empty;
        private string _loggedRuntimeProfileSignature = string.Empty;
        private bool _commandLineProfileActive;
        private Vector3 _lastLightU = CapturedU;
        private Vector3 _lastLightV = CapturedV;
        private Vector3 _lastLightDepth = CapturedDepth;
        private Vector4 _lastCasterBias = CapturedCasterBias;
        private static string _requestedDumpPrefix;

        public static void RequestShadowMapDump(string prefix)
        {
            _requestedDumpPrefix = prefix;
        }

        public void ApplyRuntimeProfile(
            int directionalMode,
            Vector2 lightDirectional,
            Vector2 mainLightAngle,
            int mainLightSpace,
            float shadowStrength,
            float useOffset,
            float facePartsShadowStrength,
            string source)
        {
            // Command-line profiles are reproducible diagnostics and therefore
            // take precedence over automatic story enter/exit updates for the
            // entire process lifetime.
            bool commandLineSource = string.Equals(
                source, "command-line diagnostic", StringComparison.Ordinal);
            if (_commandLineProfileActive && !commandLineSource) return;
            DirectionalMode nextMode = (DirectionalMode)Mathf.Clamp(directionalMode, 0, 2);
            bool directionalChanged = !_runtimeProfileActive ||
                nextMode != _directionalMode ||
                (lightDirectional - _lightDirectional).sqrMagnitude > 0.00000001f ||
                (mainLightAngle - _mainLightAngle).sqrMagnitude > 0.00000001f ||
                mainLightSpace != _mainLightSpace;
            _runtimeProfileActive = true;
            _directionalMode = nextMode;
            _lightDirectional = lightDirectional;
            _mainLightAngle = mainLightAngle;
            _mainLightSpace = mainLightSpace;
            _shadowStrength = Mathf.Clamp01(shadowStrength);
            _useOffset = Mathf.Clamp01(useOffset);
            _facePartsShadowStrength = Mathf.Clamp01(facePartsShadowStrength);
            _runtimeProfileSource = string.IsNullOrEmpty(source) ? "runtime" : source;
            if (directionalChanged) _projectionValid = false;
            LogRuntimeProfileIfChanged();
        }

        public void ResetRuntimeProfile()
        {
            if (_commandLineProfileActive) return;
            if (!_runtimeProfileActive) return;
            _runtimeProfileActive = false;
            _directionalMode = DirectionalMode.Fixed;
            _lightDirectional = Vector2.zero;
            _mainLightAngle = Vector2.zero;
            _mainLightSpace = 0;
            _shadowStrength = 1f;
            _useOffset = 0f;
            _facePartsShadowStrength = 0f;
            _runtimeProfileSource = string.Empty;
            _projectionValid = false;
            _loggedRuntimeProfileSignature = string.Empty;
            Debug.Log("[PhotoMode] Actor shadow runtime profile reset to captured fallback");
        }

        public void Initialize(GameObject actorRoot)
        {
            _actorRoot = actorRoot;
            _projectionValid = false;
            // Retain the former delayed/frozen fit only for an explicit A/B.  The
            // recovered VL ActorShadowPass updates its aggregate renderer Bounds on
            // every shadow pass before deriving the camera/frustum projection.
            _fitAfterFrame = Time.frameCount + 8;
            RefreshCasters();
            EnsureResources();
        }

        private void OnEnable()
        {
            _sourceCamera = GetComponent<Camera>();
            EnsureResources();
            TryApplyCommandLineProfile();
        }

        private void TryApplyCommandLineProfile()
        {
            string fixedX = CommandLineValue("--actor-shadow-fixed-x");
            string fixedY = CommandLineValue("--actor-shadow-fixed-y");
            string matcapX = CommandLineValue("--actor-shadow-matcap-x");
            string matcapY = CommandLineValue("--actor-shadow-matcap-y");
            string strength = CommandLineValue("--actor-shadow-strength");
            string useOffset = CommandLineValue("--actor-shadow-use-offset");
            bool shadowLight = Environment.GetCommandLineArgs().Contains(
                "--actor-shadow-shadow-light");
            bool hasDirectionalOverride = fixedX != null || fixedY != null ||
                matcapX != null || matcapY != null || shadowLight;
            if (!hasDirectionalOverride && strength == null && useOffset == null) return;

            DirectionalMode mode = shadowLight
                ? DirectionalMode.ShadowLight
                : matcapX != null || matcapY != null
                    ? DirectionalMode.Matcap
                    : DirectionalMode.Fixed;
            Vector2 fixedAngle = new Vector2(ParseFloat(fixedX, 0f), ParseFloat(fixedY, 0f));
            Vector2 matcapAngle = new Vector2(ParseFloat(matcapX, 0f), ParseFloat(matcapY, 0f));
            int matcapSpace = Environment.GetCommandLineArgs().Contains("--actor-shadow-matcap-global") ? 1 : 0;
            _commandLineProfileActive = true;
            ApplyRuntimeProfile(
                (int)mode, fixedAngle, matcapAngle, matcapSpace,
                ParseFloat(strength, 1f), ParseFloat(useOffset, 0f), 0f,
                "command-line diagnostic");
        }

        private static string CommandLineValue(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < args.Length; index++)
                if (string.Equals(args[index], key, StringComparison.OrdinalIgnoreCase))
                    return args[index + 1];
            return null;
        }

        private static float ParseFloat(string value, float fallback)
        {
            float result;
            return value != null && float.TryParse(
                value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                ? result
                : fallback;
        }

        private void LogRuntimeProfileIfChanged()
        {
            string signature = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1:F5},{2:F5}|{3:F5},{4:F5}|{5}|{6:F5}|{7:F5}|{8:F5}|{9}",
                _directionalMode, _lightDirectional.x, _lightDirectional.y,
                _mainLightAngle.x, _mainLightAngle.y, _mainLightSpace,
                _shadowStrength, _useOffset, _facePartsShadowStrength,
                _runtimeProfileSource);
            if (signature == _loggedRuntimeProfileSignature) return;
            _loggedRuntimeProfileSignature = signature;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[PhotoMode] Actor shadow runtime profile: mode={0}, lightDirectional=({1:F4},{2:F4}), mainLightAngle=({3:F4},{4:F4}), mainLightSpace={5}, strength={6:F4}, useOffset={7:F4}, facePartsStrength={8:F4}, source={9}",
                _directionalMode, _lightDirectional.x, _lightDirectional.y,
                _mainLightAngle.x, _mainLightAngle.y, _mainLightSpace,
                _shadowStrength, _useOffset, _facePartsShadowStrength,
                _runtimeProfileSource));
        }

        private void EnsureResources()
        {
            if (_sourceCamera == null) _sourceCamera = GetComponent<Camera>();
            if (_replacementShader == null)
                _replacementShader = Resources.Load<Shader>("CapturedActorShadow");
            if (_shadowCamera == null)
            {
                GameObject cameraObject = new GameObject("CapturedActorShadowCamera")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                _shadowCamera = cameraObject.AddComponent<Camera>();
                _shadowCamera.enabled = false;
                _shadowCamera.orthographic = true;
                _shadowCamera.clearFlags = CameraClearFlags.SolidColor;
                _shadowCamera.backgroundColor = SystemInfo.usesReversedZBuffer ? Color.black : Color.white;
                _shadowCamera.cullingMask = 1 << OriginalStyleRenderPipeline.ActorLayer;
                _shadowCamera.allowHDR = false;
                _shadowCamera.allowMSAA = false;
                _shadowCamera.useOcclusionCulling = false;
                _shadowCamera.renderingPath = RenderingPath.Forward;
            }
            if (_shadowMap == null)
            {
                UnityEngine.Experimental.Rendering.GraphicsFormat format =
                    SystemInfo.IsFormatSupported(
                        UnityEngine.Experimental.Rendering.GraphicsFormat.R16_UNorm,
                        UnityEngine.Experimental.Rendering.FormatUsage.Render)
                    ? UnityEngine.Experimental.Rendering.GraphicsFormat.R16_UNorm
                    : UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat;
                RenderTextureDescriptor descriptor = new RenderTextureDescriptor(Resolution, Resolution)
                {
                    graphicsFormat = format,
                    depthBufferBits = 24,
                    msaaSamples = 1,
                    volumeDepth = 1,
                    dimension = TextureDimension.Tex2D,
                    useMipMap = false,
                    autoGenerateMips = false,
                    sRGB = false
                };
                _shadowMap = new RenderTexture(descriptor)
                {
                    name = "GakumasCapturedActorShadow4096",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _shadowMap.Create();
                Debug.Log("[PhotoMode] Captured actor shadow surface format: " + format);
            }
        }

        private void RefreshCasters()
        {
            if (_actorRoot == null)
            {
                _casters = Array.Empty<Renderer>();
                return;
            }
            Renderer[] renderers = _actorRoot.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => renderer != null &&
                                   renderer.enabled &&
                                   renderer.gameObject.activeInHierarchy &&
                                   renderer.shadowCastingMode != ShadowCastingMode.Off)
                .ToArray();
            int signature = renderers.Aggregate(17, (value, renderer) => value * 31 + renderer.GetInstanceID());
            if (signature == _rendererSignature && _casters.Length > 0) return;
            _rendererSignature = signature;
            _casters = renderers;
            _casterSubmeshDrawCount = _casters.Sum(CountCasterSubmeshes);
            _projectionValid = false;
            Debug.Log(string.Format(
                "[PhotoMode] Captured actor shadow casters: renderers={0}, submeshDraws={1}; {2}",
                _casters.Length, _casterSubmeshDrawCount,
                string.Join("; ", _casters.Select(renderer => renderer.name + "=" + CountCasterSubmeshes(renderer)))));
        }

        private static int CountCasterSubmeshes(Renderer renderer)
        {
            Mesh mesh = null;
            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null) mesh = skinned.sharedMesh;
            MeshRenderer meshRenderer = renderer as MeshRenderer;
            if (meshRenderer != null)
            {
                MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                if (filter != null) mesh = filter.sharedMesh;
            }
            if (mesh == null) return renderer.sharedMaterials.Length > 0 ? renderer.sharedMaterials.Length : 1;
            return Mathf.Min(mesh.subMeshCount, Mathf.Max(1, renderer.sharedMaterials.Length));
        }

        private static Bounds TransformAabb(Bounds bounds, Matrix4x4 matrix)
        {
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            bool initialized = false;
            Bounds transformed = default(Bounds);
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 point = matrix.MultiplyPoint3x4(
                    center + Vector3.Scale(extents, new Vector3(x, y, z)));
                if (!initialized)
                {
                    transformed = new Bounds(point, Vector3.zero);
                    initialized = true;
                }
                else transformed.Encapsulate(point);
            }
            return transformed;
        }

        private void FitProductionProjection(bool forceLog)
        {
            if (_casters.Length == 0 || _sourceCamera == null) return;

            Vector3 lightU;
            Vector3 lightV;
            Vector3 lightDepth;
            ResolveDirectionalBasis(out lightU, out lightV, out lightDepth);

            // ActorShadowPass.UpdateShadowData first aggregates current Renderer
            // bounds, transforms that AABB into camera-local space, clamps its
            // positive-Z interval to Settings.far (15 by default), then clamps
            // X/Y to the camera frustum at the far actor depth.  Only after
            // that does it transform the resulting camera-local box into the
            // directional-light basis and create its orthographic projection.
            Bounds worldBounds = _casters[0].bounds;
            for (int index = 1; index < _casters.Length; index++)
                worldBounds.Encapsulate(_casters[index].bounds);
            Bounds cameraBounds = TransformAabb(worldBounds, _sourceCamera.transform.worldToLocalMatrix);
            Vector3 cameraMin = cameraBounds.min;
            Vector3 cameraMax = cameraBounds.max;
            // Both serialized DrawActorShadowPass features in the current
            // game's data.unity3d override the constructor default with far=2.
            const float productionFar = 2f;
            cameraMin.z = Mathf.Max(0f, cameraMin.z);
            cameraMax.z = Mathf.Min(cameraMax.z, cameraMin.z + productionFar);
            if (cameraMax.z <= cameraMin.z + 0.0001f) return;

            _sourceCamera.CalculateFrustumCorners(
                new Rect(0f, 0f, 1f, 1f), 1f,
                Camera.MonoOrStereoscopicEye.Mono, FrustumCorners);
            float frustumMinX = float.PositiveInfinity;
            float frustumMinY = float.PositiveInfinity;
            float frustumMaxX = float.NegativeInfinity;
            float frustumMaxY = float.NegativeInfinity;
            foreach (Vector3 corner in FrustumCorners)
            {
                frustumMinX = Mathf.Min(frustumMinX, corner.x * cameraMax.z);
                frustumMinY = Mathf.Min(frustumMinY, corner.y * cameraMax.z);
                frustumMaxX = Mathf.Max(frustumMaxX, corner.x * cameraMax.z);
                frustumMaxY = Mathf.Max(frustumMaxY, corner.y * cameraMax.z);
            }
            cameraMin.x = Mathf.Max(cameraMin.x, frustumMinX);
            cameraMin.y = Mathf.Max(cameraMin.y, frustumMinY);
            cameraMax.x = Mathf.Min(cameraMax.x, frustumMaxX);
            cameraMax.y = Mathf.Min(cameraMax.y, frustumMaxY);
            if (cameraMax.x <= cameraMin.x + 0.0001f ||
                cameraMax.y <= cameraMin.y + 0.0001f) return;

            float minU = float.PositiveInfinity, maxU = float.NegativeInfinity;
            float minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
            float minD = float.PositiveInfinity, maxD = float.NegativeInfinity;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 cameraPoint = new Vector3(
                    x < 0 ? cameraMin.x : cameraMax.x,
                    y < 0 ? cameraMin.y : cameraMax.y,
                    z < 0 ? cameraMin.z : cameraMax.z);
                Vector3 worldPoint = _sourceCamera.transform.TransformPoint(cameraPoint);
                float u = Vector3.Dot(worldPoint, lightU);
                float v = Vector3.Dot(worldPoint, lightV);
                float d = Vector3.Dot(worldPoint, lightDepth);
                minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                minV = Mathf.Min(minV, v); maxV = Mathf.Max(maxV, v);
                minD = Mathf.Min(minD, d); maxD = Mathf.Max(maxD, d);
            }

            // The native pass has no guessed planar percentage margin: the
            // camera-frustum intersection above is its visibility bound. A small camera-depth
            // pad is needed only because Unity Camera cannot express the
            // symmetric negative-near Ortho matrix used by the render pass.
            const float depthMargin = 0.01f;
            float spanU = Mathf.Max(0.1f, maxU - minU);
            float spanV = Mathf.Max(0.1f, maxV - minV);
            float spanD = Mathf.Max(0.1f, maxD - minD);
            float biasScale = spanU / CapturedProjectionSpanU;
            _lastCasterBias = new Vector4(
                CapturedCasterBias.x * biasScale,
                CapturedCasterBias.y * biasScale,
                CapturedCasterBias.z,
                CapturedCasterBias.w);
            float centerU = (minU + maxU) * 0.5f;
            float centerV = (minV + maxV) * 0.5f;
            float centerD = (minD + maxD) * 0.5f;
            Vector3 centerWorld = lightU * centerU + lightV * centerV + lightDepth * centerD;

            _shadowCamera.transform.rotation = Quaternion.LookRotation(-lightDepth, lightV);
            _shadowCamera.transform.position = centerWorld + lightDepth * (spanD * 0.5f + depthMargin);
            _shadowCamera.nearClipPlane = 0.001f;
            _shadowCamera.farClipPlane = spanD + depthMargin * 2f;
            _shadowCamera.orthographicSize = spanV * 0.5f;
            _shadowCamera.aspect = spanU / spanV;
            _projectionValid = true;
            _dynamicProjectionUpdates++;
            Vector3 span = new Vector3(spanU, spanV, spanD);
            _dynamicProjectionMinSpan = Vector3.Min(_dynamicProjectionMinSpan, span);
            _dynamicProjectionMaxSpan = Vector3.Max(_dynamicProjectionMaxSpan, span);
            if (forceLog || !_loggedDynamicProjection)
            {
                _loggedDynamicProjection = true;
                Debug.Log(string.Format(
                    "[PhotoMode] Actor shadow production projection: renderers={0}, submeshDraws={1}, span=({2:F5},{3:F5},{4:F5}), cameraDepth=({5:F5},{6:F5}), center={7}, basisU={8}, basisV={9}, depth={10}",
                    _casters.Length, _casterSubmeshDrawCount, spanU, spanV, spanD,
                    cameraMin.z, cameraMax.z, centerWorld, lightU, lightV, lightDepth));
            }
        }

        private void ResolveDirectionalBasis(
            out Vector3 lightU,
            out Vector3 lightV,
            out Vector3 lightDepth)
        {
            if (!_runtimeProfileActive || _sourceCamera == null)
            {
                lightU = CapturedU;
                lightV = CapturedV;
                lightDepth = CapturedDepth;
                _lastLightU = lightU;
                _lastLightV = lightV;
                _lastLightDepth = lightDepth;
                return;
            }

            Quaternion worldRotation;
            if (_directionalMode == DirectionalMode.Matcap)
            {
                Quaternion angle = Quaternion.Euler(_mainLightAngle.x, _mainLightAngle.y, 0f);
                // Native UpdateShadowData builds a camera-local quaternion. A
                // Global main-light angle is premultiplied by inverse(camera)
                // there; converting it back to the world basis cancels that
                // camera term. Local angles remain view-relative.
                worldRotation = _mainLightSpace == 1
                    ? angle
                    : _sourceCamera.transform.rotation * angle;
            }
            else if (_directionalMode == DirectionalMode.ShadowLight)
            {
                Light shadowLight = RenderSettings.sun;
                if (shadowLight == null || shadowLight.type != LightType.Directional)
                    shadowLight = FindObjectsOfType<Light>()
                        .FirstOrDefault(value => value != null &&
                            value.enabled && value.type == LightType.Directional);
                worldRotation = shadowLight != null
                    ? shadowLight.transform.rotation
                    : _sourceCamera.transform.rotation;
            }
            else
            {
                // VLActorShadow.Fixed: Internal_FromEulerRad receives
                // (lightDirectional.y, -lightDirectional.x, 0) in camera-local
                // space. Restore the corresponding world-space light basis.
                Quaternion angle = Quaternion.Euler(
                    _lightDirectional.y, -_lightDirectional.x, 0f);
                worldRotation = _sourceCamera.transform.rotation * angle;
            }

            lightU = (worldRotation * Vector3.right).normalized;
            lightV = (worldRotation * Vector3.up).normalized;
            lightDepth = -(worldRotation * Vector3.forward).normalized;
            _lastLightU = lightU;
            _lastLightV = lightV;
            _lastLightDepth = lightDepth;
        }

        private void OnPreCull()
        {
            EnsureResources();
            RefreshCasters();
            if (_replacementShader == null || _shadowCamera == null || _shadowMap == null || _casters.Length == 0)
            {
                Shader.SetGlobalFloat("_UseCapturedActorShadow", 0f);
                return;
            }
            string[] commandLine = Environment.GetCommandLineArgs();
            bool useExactCapturedMatrix = commandLine.Contains("--exact-captured-shadow-matrix") ||
                commandLine.Contains("--capture-gpa-camera-and-quit") ||
                commandLine.Contains("--capture-gpa-camera-sweep-and-quit") ||
                commandLine.Contains("--capture-gpa-camera-mask-sweep-and-quit");
            bool freezeProductionProjection = commandLine.Contains("--freeze-shadow-projection");
            // The exact matrix does not depend on the renderer-bounds fit.  Do
            // not defer automated evidence captures by eight frames: depending
            // on startup timing that allowed the capture coroutine to quit one
            // frame before the requested R16 dump was rendered.
            if (freezeProductionProjection && !useExactCapturedMatrix && !_projectionValid && Time.frameCount < _fitAfterFrame)
            {
                Shader.SetGlobalFloat("_UseCapturedActorShadow", 0f);
                return;
            }
            if (!useExactCapturedMatrix && !freezeProductionProjection)
                FitProductionProjection(false);
            else if (!_projectionValid)
                FitProductionProjection(true);
            if (!_projectionValid)
            {
                Shader.SetGlobalFloat("_UseCapturedActorShadow", 0f);
                return;
            }

            float diagnosticType = commandLine.Contains("--shadow-disable-type0") ? 0f :
                                   commandLine.Contains("--shadow-disable-type1") ? 1f :
                                   commandLine.Contains("--shadow-disable-type8") ? 8f :
                                   commandLine.Contains("--shadow-only-type0") ? 100f :
                                   commandLine.Contains("--shadow-only-type1") ? 101f :
                                   commandLine.Contains("--shadow-only-type8") ? 108f : -1f;
            Shader.SetGlobalFloat("_CapturedShadowDiagnosticType", diagnosticType);
            Shader.SetGlobalFloat("_CapturedActorShadowStrength", _shadowStrength);
            Shader.SetGlobalFloat("_CapturedActorShadowUseOffset", _useOffset);
            Shader.SetGlobalFloat("_CapturedActorFacePartsShadowStrength", _facePartsShadowStrength);
            Vector3 casterDirection = useExactCapturedMatrix
                ? CapturedDepth
                : _lastLightDepth;
            Vector4 casterBias = useExactCapturedMatrix
                ? CapturedCasterBias
                : _lastCasterBias;
            if (commandLine.Contains("--unity-shadow-bias")) casterBias = Vector4.zero;
            Shader.SetGlobalVector("_CapturedShadowCasterDirection",
                new Vector4(casterDirection.x, casterDirection.y, casterDirection.z, 0f));
            Shader.SetGlobalVector("_CapturedShadowCasterBias", casterBias);
            Matrix4x4 exactWorldToShadow = CapturedActorLocalToShadow * _actorRoot.transform.worldToLocalMatrix;
            Shader.SetGlobalMatrix("_CapturedActorWorldToShadow", exactWorldToShadow);
            Shader.SetGlobalFloat("_UseExactCapturedActorShadowMatrix", useExactCapturedMatrix ? 1f : 0f);
            if (!_loggedExactContract)
            {
                _loggedExactContract = true;
                Debug.Log(string.Format(
                    "[PhotoMode] Captured actor shadow contract: exact={0}; frame={1}; root={2}; localToWorld={3}; exactWorldToShadow={4}",
                    useExactCapturedMatrix, Time.frameCount, _actorRoot.transform.name,
                    _actorRoot.transform.localToWorldMatrix, exactWorldToShadow));
            }

            _shadowCamera.targetTexture = _shadowMap;
            List<Material> diagnosticCullMaterials = new List<Material>();
            List<float> diagnosticCullValues = new List<float>();
            bool frontCullType8 = commandLine.Contains("--shadow-front-cull-type8");
            bool noCullType8 = commandLine.Contains("--shadow-no-cull-type8");
            if (frontCullType8 || noCullType8)
            {
                foreach (Renderer caster in _casters)
                foreach (Material material in caster.sharedMaterials)
                {
                    if (material == null || !material.HasProperty("_ShaderType") ||
                        !material.HasProperty("_Cull") ||
                        Mathf.Abs(material.GetFloat("_ShaderType") - 8f) >= 0.25f) continue;
                    diagnosticCullMaterials.Add(material);
                    diagnosticCullValues.Add(material.GetFloat("_Cull"));
                    material.SetFloat("_Cull", frontCullType8 ? 1f : 0f);
                }
            }
            bool previousInvertCulling = GL.invertCulling;
            try
            {
                if (commandLine.Contains("--shadow-invert-culling")) GL.invertCulling = !previousInvertCulling;
                _shadowCamera.clearFlags = CameraClearFlags.SolidColor;
                _shadowCamera.RenderWithShader(_replacementShader, string.Empty);
            }
            finally
            {
                // RenderWithShader is synchronous, but restore the global even
                // if the diagnostic render faults before reaching the normal path.
                GL.invertCulling = previousInvertCulling;
                for (int index = 0; index < diagnosticCullMaterials.Count; index++)
                    diagnosticCullMaterials[index].SetFloat("_Cull", diagnosticCullValues[index]);
            }

            Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(_shadowCamera.projectionMatrix, true);
            Matrix4x4 worldToShadow = gpuProjection * _shadowCamera.worldToCameraMatrix;
            string dumpPrefix = _requestedDumpPrefix;
            if (!string.IsNullOrWhiteSpace(dumpPrefix))
            {
                _requestedDumpPrefix = null;
                DumpShadowMap(dumpPrefix, useExactCapturedMatrix,
                    useExactCapturedMatrix ? exactWorldToShadow : worldToShadow);
            }

            Shader.SetGlobalTexture("_CapturedActorShadowTex", _shadowMap);
            if (!useExactCapturedMatrix)
                Shader.SetGlobalMatrix("_CapturedActorWorldToShadow", worldToShadow);
            Shader.SetGlobalVector("_CapturedActorShadowTexelSize", new Vector4(
                1f / Resolution, 1f / Resolution, Resolution, Resolution));
            Shader.SetGlobalFloat("_UseCapturedActorShadow", 1f);
        }

        private void DumpShadowMap(string prefix, bool exact, Matrix4x4 worldToShadow)
        {
            string directory = Path.GetDirectoryName(prefix);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _shadowMap;
            Texture2D image = new Texture2D(Resolution, Resolution, TextureFormat.R16, false, true);
            image.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
            image.Apply(false, false);
            File.WriteAllBytes(prefix + "-r16.raw", image.GetRawTextureData());
            File.WriteAllText(prefix + "-r16.json", string.Format(
                CultureInfo.InvariantCulture,
                "{{\"width\":{0},\"height\":{1},\"format\":\"R16_UNORM\",\"row_pitch\":{2},\"origin\":\"bottom-left\",\"exact\":{3},\"world_to_shadow\":\"{4}\",\"runtime_profile_active\":{5},\"directional_mode\":{6},\"light_directional\":[{7:R},{8:R}],\"main_light_angle\":[{9:R},{10:R}],\"main_light_space\":{11},\"shadow_strength\":{12:R},\"use_offset\":{13:R},\"face_parts_shadow_strength\":{14:R},\"basis_u\":[{15:R},{16:R},{17:R}],\"basis_v\":[{18:R},{19:R},{20:R}],\"basis_depth\":[{21:R},{22:R},{23:R}],\"caster_bias\":[{24:R},{25:R},{26:R},{27:R}]}}\n",
                Resolution, Resolution, Resolution * 2, exact ? "true" : "false",
                worldToShadow.ToString().Replace("\n", " ").Replace("\r", "").Replace("\t", ","),
                _runtimeProfileActive ? "true" : "false", (int)_directionalMode,
                _lightDirectional.x, _lightDirectional.y,
                _mainLightAngle.x, _mainLightAngle.y, _mainLightSpace,
                _shadowStrength, _useOffset, _facePartsShadowStrength,
                _lastLightU.x, _lastLightU.y, _lastLightU.z,
                _lastLightV.x, _lastLightV.y, _lastLightV.z,
                _lastLightDepth.x, _lastLightDepth.y, _lastLightDepth.z,
                exact ? CapturedCasterBias.x : _lastCasterBias.x,
                exact ? CapturedCasterBias.y : _lastCasterBias.y,
                exact ? CapturedCasterBias.z : _lastCasterBias.z,
                exact ? CapturedCasterBias.w : _lastCasterBias.w));
            RenderTexture.active = previous;
            DestroyImmediate(image);
            Debug.Log(string.Format("[PhotoMode] Captured actor shadow map dumped: {0}; exact={1}", prefix, exact));
        }

        private void OnDisable()
        {
            LogDynamicProjectionSummary();
            Shader.SetGlobalFloat("_UseCapturedActorShadow", 0f);
            ReleaseResources();
        }

        private void OnDestroy()
        {
            LogDynamicProjectionSummary();
            Shader.SetGlobalFloat("_UseCapturedActorShadow", 0f);
            ReleaseResources();
        }

        private void LogDynamicProjectionSummary()
        {
            if (_dynamicProjectionUpdates <= 0) return;
            Debug.Log(string.Format(
                "[PhotoMode] Actor shadow production projection summary: updates={0}, minSpan={1}, maxSpan={2}",
                _dynamicProjectionUpdates, _dynamicProjectionMinSpan, _dynamicProjectionMaxSpan));
            _dynamicProjectionUpdates = 0;
        }

        private void ReleaseResources()
        {
            if (_shadowMap != null)
            {
                _shadowMap.Release();
                DestroyImmediate(_shadowMap);
                _shadowMap = null;
            }
            if (_shadowCamera != null)
            {
                DestroyImmediate(_shadowCamera.gameObject);
                _shadowCamera = null;
            }
        }
    }
}
