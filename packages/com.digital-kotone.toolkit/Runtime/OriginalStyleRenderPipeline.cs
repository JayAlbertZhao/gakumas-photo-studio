using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace GakumasPhotoMode
{
    [ExecuteAlways]
    [RequireComponent(typeof(Camera))]
    public sealed class OriginalStyleRenderPipeline : MonoBehaviour
    {
        public enum PresentationContext
        {
            StudioLocal,
            CapturedRiverbed,
            BakedAdv,
        }

        public const int ActorLayer = 8;

        private static PresentationContext _presentationContext =
            PresentationContext.StudioLocal;

        public static PresentationContext CurrentPresentationContext
        {
            get { return _presentationContext; }
        }

        public static void SetPresentationContext(PresentationContext value)
        {
            _presentationContext = value;
        }

        [Range(0.1f, 3f)] public float exposure = 0.98f;
        [Range(0f, 2f)] public float saturation = 1.07f;
        [Range(0.5f, 1.5f)] public float contrast = 1.05f;
        [Range(0f, 2f)] public float bloomIntensity = 0.13f;
        [Range(0f, 1f)] public float occlusionStrength = 0.42f;
        [Range(0f, 4f)] public float outlineWidth = 1.15f;
        [Range(0f, 2f)] public float outlineStrength = 0f;
        [Range(0f, 1f)] public float temporalBlend = 0.95f;

        // An explicit local override is separate from the captured riverbed
        // profile. Studio/ADV do not inherit riverbed fog merely by sharing an
        // actor. Density is the effective inverse-distance coefficient.
        public bool overrideSceneDistanceFog;
        [Min(0f)] public float sceneFogDensity = 0.0081f;
        public Color sceneFogColor = new Color(0.5f, 0.6f, 0.7f, 1f);
        [Range(0f, 1f)] public float sceneFogMaximumOpacity = 0.3f;
        [Range(0f, 1f)] public float sceneFogSkyWeight;

        // Opt-in and owned by this camera; no scene search or global registry.
        public SphereFogSettings sphereFog = new SphereFogSettings();
        // Explicit alternative to both legacy fog passes. Single-depth opaque input;
        // forward transparent layers must use the separate shared-binding workflow.
        public FogVolumeSettings fogVolumes = new FogVolumeSettings();
        public Func<Camera, RenderTexture, FogVolumeDepth> fogVolumeDepthProvider;
        private FogVolumeRenderer _fogVolumes;
        public string FogVolumeUnavailableReason { get; private set; }
        public bool TryGetFogVolumeFrame(out FogVolumeRenderer.Frame frame)
        {frame=default;return _fogVolumes!=null&&_fogVolumes.TryGetFrame(out frame);}
        public VolumetricLightingSettings volumetricLighting=new VolumetricLightingSettings();
        public Func<Camera,RenderTexture,FogVolumeDepth> volumetricDepthProvider;
        private VolumetricLightingRenderer _volumetricLighting;
        public string VolumetricLightingUnavailableReason {get;private set;}
        public bool TryGetVolumetricLightingFrame(out VolumetricLightingRenderer.Frame frame)
        {frame=default;return _volumetricLighting!=null&&_volumetricLighting.TryGetFrame(out frame);}
        public LensFlareSettings lensFlares = new LensFlareSettings();
        public Func<Camera, RenderTexture, FogVolumeDepth> lensFlareDepthProvider;
        // Explicit timeline, so seeking/pause never depends on this component's wall clock.
        public double lensFlareTimeSeconds;
        private LensFlareRenderer _lensFlares;
        public string LensFlareUnavailableReason { get; private set; }
        public bool TryGetLensFlareFrame(out LensFlareRenderer.Frame frame)
        { frame=default; return _lensFlares!=null && _lensFlares.TryGetFrame(out frame); }
        public LowResolutionFxSettings lowResolutionFx = new LowResolutionFxSettings();
        public Func<Camera, RenderTexture, FogVolumeDepth> lowResolutionFxDepthProvider;
        private LowResolutionFxRenderer _lowResolutionFx;
        public string LowResolutionFxUnavailableReason { get; private set; }
        public bool TryGetLowResolutionFxFrame(out LowResolutionFxRenderer.Frame frame)
        { frame=default; return _lowResolutionFx!=null && _lowResolutionFx.TryGetFrame(out frame); }
        public HeavyFxSettings heavyFx = new HeavyFxSettings();
        public Func<Camera,RenderTexture,FogVolumeDepth> heavyFxDepthProvider;
        public RenderTexture heavyFxProtection;
        public double heavyFxTimeSeconds;
        private HeavyFxRenderer _heavyFx;
        public string HeavyFxUnavailableReason {get;private set;}
        public bool TryGetHeavyFxFrame(out HeavyFxRenderer.Frame frame)
        {frame=default;return _heavyFx!=null&&_heavyFx.TryGetFrame(out frame);}
        public TemporalClassification temporalClassification;
        // Explicit alternative to the unchanged legacy temporal pass. Same camera only.
        public SceneDeferredCamera sceneTemporalSource;
        // Independent opt-in current-motion consumer, after DOF and before Bloom.
        public SceneDeferredCamera sceneMotionBlurSource;
        public BokehDepthOfFieldSettings bokehDepthOfField = new BokehDepthOfFieldSettings();
        // Called after temporal resolve, before DOF. The host must supply matching
        // positive eye depth in R (including any applied color de-jitter).
        public Func<Camera, RenderTexture, RenderTexture> bokehDepthProvider;
        private BokehDepthOfFieldRenderer _bokehDepthOfField;
        public string BokehDepthOfFieldUnavailableReason { get; private set; }
        public bool TryGetBokehDepthOfFieldFrame(out BokehDepthOfFieldRenderer.Frame frame)
        {frame=default;return _bokehDepthOfField!=null&&_bokehDepthOfField.TryGetFrame(out frame);}
        public ScreenSpaceReflection screenSpaceReflection;
        public SceneReflectionResolve sceneReflectionResolve;

        // Caller owns the immutable LUT; explicit replacement, never a second grade over the old LUT.
        public bool useAuthoredColorGrading;
        public ColorGradingLut authoredColorLut;
        private ColorGradingRenderer _authoredColorRenderer;
        private Material _authoredComposite;
        public string AuthoredColorUnavailableReason { get; private set; }
        public bool TryGetAuthoredColorFrame(out ColorGradingRenderer.Frame frame)
        { frame=default;return _authoredColorRenderer!=null && _authoredColorRenderer.TryGetFrame(out frame); }

        private Camera _sourceCamera;
        private Camera _actorCamera;
        private Material _postMaterial;
        private Material _depthOfFieldMaterial;
        private Material _paraffinMaterial;
        private Shader _actorDataShader;
        private RenderTexture _actorData;
        private RenderTexture _history;
        private static string _requestedPostDumpPrefix;
        private static string _requestedCurrentHdrDumpPrefix;
        private static string _requestedActorDataPngPrefix;
        private Texture3D _capturedColorLut;
        private Texture3D _classroomColorLut;
        private bool _capturedLutAttempted;
        private bool _historyValid;
        private int _historyFrame = -1;
        private bool _depthOfFieldActive;
        private int _depthOfFieldQuality = 3;
        private float _depthOfFieldFocalPoint = 4f;
        private float _depthOfFieldFNumber = 4f;
        private float _depthOfFieldMaxBlurSpread = 1.5f;
        private float _depthOfFieldForegroundBlurExtrude;
        private bool _depthOfFieldUseFNumber = true;
        private float _depthOfFieldSmoothness = 0.5f;
        private float _depthOfFieldFocalSize = 1f;
        private int _depthOfFieldBladeCount = 5;
        private float _depthOfFieldBladeCurvature = 1f;
        private float _depthOfFieldBladeRotation;
        private readonly Vector4[] _bokehKernel = new Vector4[42];
        private readonly RuntimeFlare _paraffinFlare0 = new RuntimeFlare();
        private readonly RuntimeFlare _paraffinFlare1 = new RuntimeFlare();
        private bool _paraffinProfileActive;
        private bool _paraffinCommandActive;
        private float _paraffinBaseAspect = 16f / 9f;
        private float _paraffinMixWeight;
        private long _paraffinSourcePathId;
        private StoryPostProcessProfile _storyPostProfile;
        private float _storyBloomThresholdLinear = 0.89000553f;
        private float _storyBloomIntensityLinear = 0.18920708f;
        private int _storyBloomDiffusion = 6;
        private float _storyPostExposure = 0.965936303f;
        private float _storyDiffusionStride = 0.36f;
        private float _storyDiffusionContrastThreshold = 0.5f;
        private float _storyDiffusionContrastPower = 0.075f;
        private float _storyDiffusionBlend = 0.52f;
        private float _storyChromaticAberration = 0.05f;
        private float _storyColorContrast = 5.5f;
        private float _storyColorSaturation = 7.5f;
        private float _storyColorHueShift;
        private bool _storyPostProfileColorCompatible = true;
        private bool _storyPostProfileClassroomColorCompatible;
        private bool _storyPostProfileTonemappingCompatible = true;
        private int _lastStoryColorLutSelectionLog = -1;

        private sealed class RuntimeFlare
        {
            public int type;
            public bool fixToBaseAspect;
            public Vector2 center;
            public Color color0;
            public Color color1;
            public Vector2 size;

            public void SetNone()
            {
                type = 0;
                fixToBaseAspect = false;
                center = Vector2.zero;
                color0 = Color.clear;
                color1 = Color.clear;
                size = Vector2.zero;
            }
        }

        private void OnEnable()
        {
            _sourceCamera = GetComponent<Camera>();
            _sourceCamera.allowHDR = true;
            _sourceCamera.depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.DepthNormals | DepthTextureMode.MotionVectors;
            EnsureResources();
        }

        private void EnsureResources()
        {
            if (_postMaterial == null)
            {
                Shader post = Resources.Load<Shader>("OriginalStylePost");
                if (post != null) _postMaterial = new Material(post) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_depthOfFieldMaterial == null)
            {
                Shader dof = Resources.Load<Shader>("AdvDepthOfField");
                if (dof != null)
                    _depthOfFieldMaterial = new Material(dof) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_paraffinMaterial == null)
            {
                Shader paraffin = Resources.Load<Shader>("AdvParaffin");
                if (paraffin != null)
                    _paraffinMaterial = new Material(paraffin) { hideFlags = HideFlags.HideAndDontSave };
            }
            if(!useAuthoredColorGrading) EnsureCapturedColorLut();
            if (_actorDataShader == null) _actorDataShader = Resources.Load<Shader>("ActorDataReplacement");
            if (_actorCamera == null)
            {
                GameObject cameraObject = new GameObject("ActorDataCamera") { hideFlags = HideFlags.HideAndDontSave };
                cameraObject.transform.SetParent(transform, false);
                _actorCamera = cameraObject.AddComponent<Camera>();
                _actorCamera.enabled = false;
            }
        }

        private void EnsureCapturedColorLut()
        {
            if (_capturedLutAttempted) return;
            _capturedLutAttempted = true;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--legacy-tonemap") >= 0) return;

            string capturedPath = Path.Combine(
                BundleCatalog.DefaultStagingRoot,
                "research", "gpa", "post-cbuffers-lut-pass50", "post-lut3d-mip0.raw");
            _capturedColorLut = LoadCapturedColorLut(
                capturedPath, "GakumasCapturedPhotoModeLut32");
            string classroomPath = Path.Combine(
                BundleCatalog.DefaultStagingRoot,
                "research", "gpa", "color-grading-lut-pass299",
                "classroom-color-lut-32-r11g11b10.raw");
            _classroomColorLut = LoadCapturedColorLut(
                classroomPath, "GakumasClassroomColorLut32");
        }

        private static Texture3D LoadCapturedColorLut(string path, string textureName)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning("[PhotoMode] Captured 3D color LUT unavailable: " + path);
                return null;
            }
            byte[] bytes = File.ReadAllBytes(path);
            const int size = 32;
            if (bytes.Length != size * size * size * 4)
            {
                Debug.LogWarning("[PhotoMode] Captured 3D color LUT has unexpected size: " + bytes.Length);
                return null;
            }

            Color[] pixels = new Color[size * size * size];
            for (int index = 0; index < pixels.Length; index++)
            {
                uint packed = BitConverter.ToUInt32(bytes, index * 4);
                pixels[index] = new Color(
                    DecodeUnsignedFloat(packed & 0x7ffu, 6),
                    DecodeUnsignedFloat((packed >> 11) & 0x7ffu, 6),
                    DecodeUnsignedFloat((packed >> 22) & 0x3ffu, 5),
                    1f);
            }
            Texture3D texture = new Texture3D(
                size, size, size, TextureFormat.RGBAHalf, false, true)
            {
                name = textureName,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            Debug.Log("[PhotoMode] Loaded captured 32x32x32 R11G11B10 color LUT: " + path);
            return texture;
        }

        private static float DecodeUnsignedFloat(uint bits, int mantissaBits)
        {
            uint mantissaMask = (1u << mantissaBits) - 1u;
            uint exponent = (bits >> mantissaBits) & 0x1fu;
            uint mantissa = bits & mantissaMask;
            if (exponent == 0u)
                return mantissa * Mathf.Pow(2f, 1 - 15 - mantissaBits);
            if (exponent == 0x1fu)
                return mantissa == 0u ? 65504f : 0f;
            return (1f + mantissa / (float)(1 << mantissaBits)) * Mathf.Pow(2f, (int)exponent - 15);
        }

        /// <summary>Invalidate accumulation after an explicit diagnostic state change.</summary>
        public void ResetTemporalHistory()
        {
            _historyValid = false;
            _historyFrame = -1;
            sceneTemporalSource?.ResetTemporalColorHistory();
            sceneMotionBlurSource?.ResetMotionBlurHistory();
        }

        private void OnPreCull()
        {
            EnsureResources();
            if (_sourceCamera == null || _actorCamera == null || _actorDataShader == null) return;

            int width = Mathf.Max(1, _sourceCamera.pixelWidth);
            int height = Mathf.Max(1, _sourceCamera.pixelHeight);
            if (_actorData == null || _actorData.width != width || _actorData.height != height)
            {
                ReleaseActorData();
                _actorData = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf)
                {
                    name = "ActorNormalId",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _actorData.Create();
            }

            _actorCamera.CopyFrom(_sourceCamera);
            _actorCamera.enabled = false;
            _actorCamera.clearFlags = CameraClearFlags.SolidColor;
            _actorCamera.backgroundColor = Color.clear;
            _actorCamera.cullingMask = 1 << ActorLayer;
            _actorCamera.allowHDR = true;
            _actorCamera.allowMSAA = false;
            _actorCamera.targetTexture = _actorData;
            _actorCamera.RenderWithShader(_actorDataShader, string.Empty);
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            EnsureResources();
            if (_postMaterial == null)
            {
                Graphics.Blit(source, destination);
                return;
            }

            List<RenderTexture> temporaries = new List<RenderTexture>();
            EnsureHistory(source.width, source.height);
            string postDumpPrefix = _requestedPostDumpPrefix;
            if (!string.IsNullOrEmpty(postDumpPrefix)) _requestedPostDumpPrefix = null;
            string currentHdrDumpPrefix = _requestedCurrentHdrDumpPrefix;
            if (!string.IsNullOrEmpty(currentHdrDumpPrefix)) _requestedCurrentHdrDumpPrefix = null;
            string actorDataPngPrefix = _requestedActorDataPngPrefix;
            if (!string.IsNullOrEmpty(actorDataPngPrefix)) _requestedActorDataPngPrefix = null;

            if (!string.IsNullOrEmpty(actorDataPngPrefix) && _actorData != null)
                DumpActorDataPng(_actorData, actorDataPngPrefix);

            _postMaterial.SetTexture("_ActorDataTex", _actorData != null ? _actorData : Texture2D.blackTexture);
            // GPA draw order is Actor MRT -> scene fill/fog -> edge resolve -> temporal.
            // The objects named Hidden/VL/PostEffect/Diffusion are the later 720x405
            // scene downsample + separable Gaussian used by final PS 6460E6E1; there is
            // no full-resolution skin-only blur here. Bypass the former identity blit in
            // production so it cannot add an extra resample/format round-trip. Retain the
            // old face blur only behind an explicit diagnostic switch.
            RenderTexture current = source;
            if (sceneReflectionResolve != null &&
                sceneReflectionResolve.TryComposite(_sourceCamera, source, out RenderTexture resolved)) current = resolved;
            else if (screenSpaceReflection != null &&
                screenSpaceReflection.TryComposite(_sourceCamera, source, out RenderTexture reflected)) current = reflected;
            if(heavyFx!=null&&heavyFx.enabled)
            {
                _fogVolumes?.Dispose();_fogVolumes=null;
                _lowResolutionFx?.Dispose();_lowResolutionFx=null;
                _volumetricLighting?.Dispose();_volumetricLighting=null;
                _lensFlares?.Dispose();_lensFlares=null;
                current=ApplyHeavyFx(current);
            }
            else
            {
                _heavyFx?.Dispose();_heavyFx=null;HeavyFxUnavailableReason=null;
                if(fogVolumes!=null&&fogVolumes.enabled)current=ApplyFogVolumes(current);
                else
                {
                    _fogVolumes?.Dispose();_fogVolumes=null;FogVolumeUnavailableReason=null;
                    current = ApplySceneDistanceFog(current, temporaries);
                    current = ApplySphereFog(current, temporaries);
                }
                current=ApplyLowResolutionFx(current);
                current=ApplyVolumetricLighting(current);
                current=ApplyLensFlares(current);
            }
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--legacy-diffusion") >= 0)
            {
                RenderTexture legacyDiffused = GetTemporary(
                    source.width, source.height, RenderTextureFormat.ARGBHalf, temporaries);
                _postMaterial.SetFloat("_DiffusionStrength", 0.12f);
                Graphics.Blit(current, legacyDiffused, _postMaterial, 8);
                current = legacyDiffused;
            }
            if (!string.IsNullOrEmpty(currentHdrDumpPrefix))
                DumpHalfSurface(current, currentHdrDumpPrefix);
            if (!string.IsNullOrEmpty(postDumpPrefix))
            {
                // Preserve the exact current HDR before temporal history alters it.
                DumpHalfSurface(current, postDumpPrefix + "-t0-current");
                if (_actorData != null) DumpHalfSurface(_actorData, postDumpPrefix + "-actor-data");
            }

            RenderTexture temporal;
            bool sceneColorResolved=false;
            if(sceneTemporalSource!=null&&sceneTemporalSource.temporalAntialiasing!=null&&sceneTemporalSource.temporalAntialiasing.enabled)
            {
                sceneColorResolved=sceneTemporalSource.TryResolveTemporalColor(_sourceCamera,current,temporalClassification,out temporal);
                if(!sceneColorResolved)temporal=current;
                // A failed explicit scene resolve passes through; never silently read unrelated host motion.
                _historyValid=false;_historyFrame=-1;
            }
            else
            {
                bool continuousFrame = _historyValid && _historyFrame == Time.frameCount - 1;
                temporal = GetTemporary(source.width, source.height, SupersamplePresenter.SceneColorFormat, temporaries);
                _postMaterial.SetTexture("_HistoryTex", _history != null ? _history : Texture2D.blackTexture);
                _postMaterial.SetFloat("_HistoryValid", continuousFrame ? 1f : 0f);
                _postMaterial.SetFloat("_TemporalBlend", temporalBlend);
                BindTemporalClassification(source.width, source.height);
                Graphics.Blit(current, temporal, _postMaterial, 7);
                Graphics.Blit(temporal, _history);
                _historyValid = true;
                _historyFrame = Time.frameCount;
            }

            // VLPostProcessPass executes DOF after temporal resolve and before
            // the later diffusion/bloom/final-color chain.  Keep temporal
            // history sharp, then route every subsequent post input through the
            // resolved DOF surface.
            RenderTexture postInput = ApplyDepthOfField(temporal, temporaries);
            if(sceneMotionBlurSource!=null&&sceneMotionBlurSource.motionBlur!=null&&sceneMotionBlurSource.motionBlur.enabled)
            {
                bool dejittered=sceneColorResolved&&sceneMotionBlurSource==sceneTemporalSource;
                if(sceneMotionBlurSource.TryResolveMotionBlur(_sourceCamera,postInput,dejittered,temporalClassification,out var blurred))postInput=blurred;
                // Invalid/paused sources retain this current HDR; no stale frame or unrelated host motion fallback.
            }

            int width = Mathf.Max(1, source.width / 2);
            int height = Mathf.Max(1, source.height / 2);
            RenderTexture first = GetTemporary(width, height, source.format, temporaries);
            _postMaterial.SetFloat("_BloomThreshold", _storyBloomThresholdLinear);
            _postMaterial.SetFloat("_BloomKnee", 0f);
            Graphics.Blit(postInput, first, _postMaterial, 0);

            RenderTexture[] levels = new RenderTexture[
                Mathf.Clamp(_storyBloomDiffusion, 2, 7)];
            levels[0] = first;
            for (int index = 1; index < levels.Length; index++)
            {
                width = Mathf.Max(1, width / 2);
                height = Mathf.Max(1, height / 2);
                levels[index] = GetTemporary(width, height, source.format, temporaries);
                // PS 0D42B11B samples +/- one source texel at every level.
                _postMaterial.SetFloat("_KawaseOffset", 0.5f);
                Graphics.Blit(levels[index - 1], levels[index], _postMaterial, 1);
            }

            RenderTexture bloom = levels[levels.Length - 1];
            for (int index = levels.Length - 2; index >= 0; index--)
            {
                RenderTexture upsampled = GetTemporary(levels[index].width, levels[index].height, source.format, temporaries);
                _postMaterial.SetTexture("_BloomTex", bloom);
                Graphics.Blit(levels[index], upsampled, _postMaterial, 2);
                bloom = upsampled;
            }

            // Native order is temporal/DOF -> bloom pyramid -> VLParaffin ->
            // low-resolution diffusion/final composite.  In particular the
            // flare is present in sharp t0 and diffusion t1, but it does not
            // feed the already-completed bloom pyramid.
            postInput = ApplyParaffin(postInput, temporaries);

            // Captured final post t1: a 720x405 copy at 3840x2160 output
            // (3/16 resolution), then the exact 9-tap 0.36-texel separable blur.
            int capturedBlurWidth = Mathf.Max(1, Mathf.RoundToInt(source.width * (3f / 16f)));
            int capturedBlurHeight = Mathf.Max(1, Mathf.RoundToInt(source.height * (3f / 16f)));
            RenderTexture blurSource = GetTemporary(capturedBlurWidth, capturedBlurHeight, source.format, temporaries);
            RenderTexture blurHorizontal = GetTemporary(capturedBlurWidth, capturedBlurHeight, source.format, temporaries);
            RenderTexture blurVertical = GetTemporary(capturedBlurWidth, capturedBlurHeight, source.format, temporaries);
            _postMaterial.SetVector("_CapturedBlurTexelSize", new Vector4(
                1f / capturedBlurWidth, 1f / capturedBlurHeight, capturedBlurWidth, capturedBlurHeight));
            _postMaterial.SetFloat("_CapturedDiffusionStride", _storyDiffusionStride);
            Graphics.Blit(postInput, blurSource, _postMaterial, 9);
            _postMaterial.SetVector("_CapturedGaussianDirection", new Vector4(1f, 0f, 0f, 0f));
            Graphics.Blit(blurSource, blurHorizontal, _postMaterial, 10);
            _postMaterial.SetVector("_CapturedGaussianDirection", new Vector4(0f, 1f, 0f, 0f));
            Graphics.Blit(blurHorizontal, blurVertical, _postMaterial, 10);

            if (!string.IsNullOrEmpty(postDumpPrefix))
            {
                DumpHalfSurface(temporal, postDumpPrefix + "-t0-temporal");
                if (!ReferenceEquals(postInput, temporal))
                    DumpHalfSurface(postInput, postDumpPrefix + "-t0-dof");
                DumpHalfSurface(blurVertical, postDumpPrefix + "-t1-blur");
                DumpHalfSurface(bloom, postDumpPrefix + "-t3-bloom-unscaled");
            }

            RenderTexture occlusionRaw = GetTemporary(Mathf.Max(1, source.width / 2), Mathf.Max(1, source.height / 2), RenderTextureFormat.RHalf, temporaries);
            RenderTexture occlusionBlurred = GetTemporary(occlusionRaw.width, occlusionRaw.height, RenderTextureFormat.RHalf, temporaries);
            _postMaterial.SetFloat("_AORadius", 2.25f);
            _postMaterial.SetFloat("_AOIntensity", 1.55f);
            Graphics.Blit(postInput, occlusionRaw, _postMaterial, 3);
            Graphics.Blit(occlusionRaw, occlusionBlurred, _postMaterial, 4);

            _postMaterial.SetTexture("_BloomTex", bloom);
            _postMaterial.SetTexture("_BlurTex", blurVertical);
            _postMaterial.SetTexture("_OcclusionTex", occlusionBlurred);
            _postMaterial.SetTexture("_ActorDataTex", _actorData != null ? _actorData : Texture2D.blackTexture);
            Texture3D activeColorLut =
                _storyPostProfileClassroomColorCompatible && _classroomColorLut != null
                    ? _classroomColorLut
                    : _capturedColorLut;
            _postMaterial.SetTexture("_CapturedColorLut",
                activeColorLut != null ? activeColorLut : Texture2D.blackTexture);
            _postMaterial.SetFloat("_UseCapturedColorLut", activeColorLut != null ? 1f : 0f);
            string[] commandLine = Environment.GetCommandLineArgs();
            bool bakedAdvBackdrop =
                _presentationContext == PresentationContext.BakedAdv;
            float capturedPostInputScale = Array.IndexOf(commandLine, "--captured-post-080") >= 0 ? 0.80f :
                                           Array.IndexOf(commandLine, "--captured-post-120") >= 0 ? 1.20f : 1.00f;
            _postMaterial.SetFloat("_CapturedPostInputScale", capturedPostInputScale);
            _postMaterial.SetFloat("_CapturedPostExposure", _storyPostExposure);
            _postMaterial.SetFloat("_CapturedBloomIntensity", _storyBloomIntensityLinear);
            _postMaterial.SetFloat("_CapturedDiffusionStride", _storyDiffusionStride);
            _postMaterial.SetFloat("_CapturedDiffusionContrastThreshold",
                _storyDiffusionContrastThreshold);
            _postMaterial.SetFloat("_CapturedDiffusionContrastPower",
                _storyDiffusionContrastPower);
            _postMaterial.SetFloat("_CapturedDiffusionBlend", _storyDiffusionBlend);
            _postMaterial.SetFloat("_CapturedDiffusionNormalization",
                1f / Mathf.Max(0.000001f, 1f + _storyDiffusionBlend));
            _postMaterial.SetFloat("_CapturedChromaticRadialScale",
                Mathf.Max(0f, _storyChromaticAberration) * 0.05f);
            // With the recovered 2x render scale, t0 is 3840x2160 and the 3/16
            // blur target is the original 720x405.  The literal max(t0,t1)
            // contract now has the lowest aggregate Actor error, so the former
            // native-resolution 0.80 compensation is diagnostic-only.
            float capturedBlurLiftScale = Array.IndexOf(commandLine, "--captured-blur-lift-080") >= 0
                ? 0.80f
                : bakedAdvBackdrop ? 0.82f : 1.00f;
            _postMaterial.SetFloat("_CapturedBlurLiftScale", capturedBlurLiftScale);
            // PASS293 recovered the source plate as an opaque UI/UIBackground
            // scene draw before the shared post stack, not as a post-composited
            // image. The fifteen-checkpoint A/B passed with the literal 1.0
            // scales, so that is now production. Retain the old empirical
            // compensation only as an explicit regression probe.
            bool legacyBakedAdvCompensation = Array.IndexOf(
                commandLine, "--legacy-baked-adv-compensation") >= 0;
            _postMaterial.SetFloat("_CapturedBackgroundBlurLiftScale",
                bakedAdvBackdrop && legacyBakedAdvCompensation ? 0.55f : 1.00f);
            _postMaterial.SetFloat("_CapturedBloomScale",
                bakedAdvBackdrop && legacyBakedAdvCompensation ? 0.68f : 1.00f);
            _postMaterial.SetFloat("_CapturedGlobalBlurLiftCalibration",
                Array.IndexOf(commandLine, "--global-captured-blur-calibration") >= 0 ? 1f : 0f);
            _postMaterial.SetFloat("_BloomIntensity", bloomIntensity);
            _postMaterial.SetFloat("_OcclusionStrength", occlusionStrength);
            _postMaterial.SetFloat("_Exposure", exposure);
            _postMaterial.SetFloat("_Saturation", saturation);
            _postMaterial.SetFloat("_Contrast", contrast);
            _postMaterial.SetFloat("_OutlineWidth", outlineWidth);
            float activeOutline = Array.IndexOf(commandLine, "--legacy-outline") >= 0
                ? 0.72f
                : outlineStrength;
            _postMaterial.SetFloat("_OutlineStrength", activeOutline);
            _postMaterial.SetColor("_OutlineColor", new Color(0.28f, 0.18f, 0.20f, 0.86f));
            // The captured frame keeps the sharp Actor/temporal surface at
            // 3840x2160 but executes the final LUT composite at the 1920x1080
            // presentation size.  When a 2x source target is active, write the
            // final pass to the presenter's window-sized target rather than
            // grading at 4K and downsampling the already-tonemapped result.
            RenderTexture finalDestination;
            if (!SupersamplePresenter.TryGetPresentationTarget(
                    _sourceCamera, out finalDestination))
                finalDestination = destination;
            RenderTexture finalColor = GetTemporary(
                finalDestination.width, finalDestination.height,
                RenderTextureFormat.ARGBHalf, temporaries);
            if(useAuthoredColorGrading)
                ApplyAuthoredColor(postInput,finalColor,temporaries);
            else
            {
                ReleaseAuthoredColor();
                Graphics.Blit(postInput, finalColor, _postMaterial, 5);
            }
            // The original frame has a full-resolution temporal accumulation target. Once
            // temporal resolve is active, a second full-strength FXAA pass unnecessarily
            // softens eyelashes and hair cards, so finalColor is presented directly.
            Graphics.Blit(finalColor, finalDestination);

            foreach (RenderTexture temporary in temporaries) RenderTexture.ReleaseTemporary(temporary);
        }

        private static RenderTexture GetTemporary(int width, int height, RenderTextureFormat format, ICollection<RenderTexture> collection)
        {
            RenderTexture texture = RenderTexture.GetTemporary(width, height, 0, format, RenderTextureReadWrite.Default);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            collection.Add(texture);
            return texture;
        }

        private void ApplyAuthoredColor(RenderTexture source,RenderTexture destination,ICollection<RenderTexture> temporaries)
        {
            AuthoredColorUnavailableReason=null;
            try
            {
                var shader=Resources.Load<Shader>("AuthoredPostComposite");
                if(authoredColorLut==null || !authoredColorLut.IsValid || shader==null || !shader.isSupported)
                    throw new InvalidOperationException("Live authored LUT and HDR composite shader required");
                if(_authoredComposite==null) _authoredComposite=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                _authoredComposite.CopyPropertiesFromMaterial(_postMaterial);
                var hdr=GetTemporary(destination.width,destination.height,RenderTextureFormat.ARGBFloat,temporaries);
                hdr.name="Toolkit authored pre-grade HDR";
                Graphics.Blit(source,hdr,_authoredComposite,0);
                if(_authoredColorRenderer==null) _authoredColorRenderer=new ColorGradingRenderer();
                if(!_authoredColorRenderer.TryRender(hdr,authoredColorLut,out var frame))
                    throw new InvalidOperationException(_authoredColorRenderer.UnavailableReason);
                Graphics.Blit(frame.color,destination);
            }
            catch(Exception error)
            {
                _authoredColorRenderer?.Dispose();
                AuthoredColorUnavailableReason="Authored color unavailable: "+error.Message;
                Graphics.Blit(source,destination); // Explicit failure: ungraded HDR, never private/old LUT fallback.
            }
        }

        private void ReleaseAuthoredColor()
        {
            _authoredColorRenderer?.Dispose();_authoredColorRenderer=null;
            if(_authoredComposite!=null) DestroyImmediate(_authoredComposite);
            _authoredComposite=null;AuthoredColorUnavailableReason=null;
        }

        public bool TryGetSceneDistanceFog(out Vector4 parameters, out Color linearColor)
        {
            parameters = Vector4.zero;
            linearColor = Color.black;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--disable-scene-fog") >= 0)
                return false;
            if (!overrideSceneDistanceFog && _presentationContext != PresentationContext.CapturedRiverbed)
                return false;
            float density = overrideSceneDistanceFog ? Mathf.Max(0f, sceneFogDensity) : 0.0081f;
            float cap = overrideSceneDistanceFog ? Mathf.Clamp01(sceneFogMaximumOpacity) : 0.3f;
            float skyWeight = overrideSceneDistanceFog ? Mathf.Clamp01(sceneFogSkyWeight) : 0f;
            if (density <= 0f || cap <= 0f) return false;
            parameters = new Vector4(density, cap, skyWeight, 0f);
            linearColor = (overrideSceneDistanceFog ? sceneFogColor : new Color(0.5f, 0.6f, 0.7f, 1f)).linear;
            return true;
        }

        private RenderTexture ApplySceneDistanceFog(
            RenderTexture source, ICollection<RenderTexture> temporaries)
        {
            Vector4 parameters;
            Color color;
            if (!TryGetSceneDistanceFog(out parameters, out color) || _postMaterial == null || _sourceCamera == null)
                return source;
            float near = _sourceCamera.nearClipPlane;
            float far = _sourceCamera.farClipPlane;
            bool reversed = SystemInfo.usesReversedZBuffer;
            _postMaterial.SetVector("_SceneFogParameters", parameters);
            _postMaterial.SetVector("_SceneFogColor", new Vector4(color.r, color.g, color.b, 0f));
            _postMaterial.SetVector("_SceneFogDepthDecode", new Vector4(
                reversed ? 1f / near - 1f / far : 1f / far - 1f / near,
                reversed ? 1f / far : 1f / near, near, far));
            _postMaterial.SetFloat("_SceneFogOrthographic", _sourceCamera.orthographic ? 1f : 0f);
            _postMaterial.SetFloat("_SceneFogReversedZ", reversed ? 1f : 0f);
            RenderTexture fogged = GetTemporary(source.width, source.height, source.format, temporaries);
            // Preserve the pre-fog surface exactly, then blend a fog-only
            // contribution once. Sampling/copying via another shader would
            // introduce an unrelated resample and rounding step.
            Graphics.CopyTexture(source, 0, 0, fogged, 0, 0);
            Graphics.Blit(null, fogged, _postMaterial, 11);
            return fogged;
        }

        private void BindTemporalClassification(int width, int height)
        {
            Texture mask = null;
            bool active = temporalClassification != null &&
                temporalClassification.TryGetMask(_sourceCamera, width, height, out mask);
            _postMaterial.SetFloat("_TemporalMaskEnabled", active ? 1f : 0f);
            _postMaterial.SetTexture("_TemporalFlagsTex", active ? mask : Texture2D.blackTexture);
            _postMaterial.SetVector("_TemporalJitterUv", active ?
                new Vector4(temporalClassification.jitterUv.x, temporalClassification.jitterUv.y, 0, 0) : Vector4.zero);
        }

        private RenderTexture ApplyHeavyFx(RenderTexture source)
        {
            HeavyFxUnavailableReason=null;
            if((lowResolutionFx!=null&&lowResolutionFx.enabled)||(volumetricLighting!=null&&volumetricLighting.enabled)||
                (lensFlares!=null&&lensFlares.enabled)||(fogVolumes!=null&&fogVolumes.enabled)||(sphereFog!=null&&sphereFog.enabled)||
                TryGetSceneDistanceFog(out _,out _)||(heavyFx.geometry!=null&&heavyFx.geometry.enabled&&heavyFx.geometry.fog!=null&&heavyFx.geometry.fog.enabled))
            {_heavyFx?.Dispose();HeavyFxUnavailableReason="Joint FX requires exclusive opaque input; disable independent FX/fog bridges";return source;}
            try
            {
                if(heavyFxDepthProvider==null){_heavyFx?.Dispose();HeavyFxUnavailableReason="Explicit current joint FX depth provider required";return source;}
                var depth=heavyFxDepthProvider(_sourceCamera,source);
                if(_heavyFx==null)_heavyFx=new HeavyFxRenderer();
                if(_heavyFx.TryRender(source,depth,_sourceCamera,heavyFx,heavyFxTimeSeconds,out var frame,heavyFxProtection))return frame.color;
                HeavyFxUnavailableReason=_heavyFx.UnavailableReason;return source;
            }
            catch(Exception error){_heavyFx?.Dispose();HeavyFxUnavailableReason="Joint FX depth provider failed: "+error.GetType().Name;return source;}
        }

        private RenderTexture ApplyLowResolutionFx(RenderTexture source)
        {
            LowResolutionFxUnavailableReason=null;
            if(lowResolutionFx==null || !lowResolutionFx.enabled) { _lowResolutionFx?.Dispose(); _lowResolutionFx=null; return source; }
            try
            {
                if(lowResolutionFxDepthProvider==null) { _lowResolutionFx?.Dispose(); LowResolutionFxUnavailableReason="Explicit current opaque FX depth provider required"; return source; }
                var depth=lowResolutionFxDepthProvider(_sourceCamera,source);
                if(_lowResolutionFx==null) _lowResolutionFx=new LowResolutionFxRenderer();
                if(_lowResolutionFx.TryRender(source,depth,_sourceCamera,lowResolutionFx,out var frame)) return frame.color;
                LowResolutionFxUnavailableReason=_lowResolutionFx.UnavailableReason; return source;
            }
            catch(Exception error) { _lowResolutionFx?.Dispose(); LowResolutionFxUnavailableReason="FX depth provider failed: "+error.GetType().Name; return source; }
        }

        private RenderTexture ApplyLensFlares(RenderTexture source)
        {
            LensFlareUnavailableReason=null;
            if (lensFlares==null || !lensFlares.enabled) { _lensFlares?.Dispose(); _lensFlares=null; return source; }
            try
            {
                if (lensFlareDepthProvider==null) { _lensFlares?.Dispose(); LensFlareUnavailableReason="Explicit current flare depth provider required"; return source; }
                var depth=lensFlareDepthProvider(_sourceCamera,source);
                if (_lensFlares==null) _lensFlares=new LensFlareRenderer();
                if (_lensFlares.TryRender(source,depth,_sourceCamera,lensFlares,lensFlareTimeSeconds,out var frame)) return frame.color;
                LensFlareUnavailableReason=_lensFlares.UnavailableReason; return source;
            }
            catch (Exception e) { _lensFlares?.Dispose(); LensFlareUnavailableReason="Flare depth provider failed: "+e.GetType().Name; return source; }
        }

        private RenderTexture ApplyVolumetricLighting(RenderTexture source)
        {
            VolumetricLightingUnavailableReason=null;
            if(volumetricLighting==null||!volumetricLighting.enabled){_volumetricLighting?.Dispose();_volumetricLighting=null;return source;}
            try
            {
                if(volumetricDepthProvider==null){_volumetricLighting?.Dispose();VolumetricLightingUnavailableReason="Explicit current volumetric depth provider required";return source;}
                var depth=volumetricDepthProvider(_sourceCamera,source);
                if(_volumetricLighting==null)_volumetricLighting=new VolumetricLightingRenderer();
                if(_volumetricLighting.TryRender(source,depth,_sourceCamera,volumetricLighting,out var frame))return frame.color;
                VolumetricLightingUnavailableReason=_volumetricLighting.UnavailableReason;return source;
            }
            catch(Exception e){_volumetricLighting?.Dispose();VolumetricLightingUnavailableReason="Volumetric depth provider failed: "+e.GetType().Name;return source;}
        }

        private RenderTexture ApplyFogVolumes(RenderTexture source)
        {
            FogVolumeUnavailableReason=null;
            try
            {
                if(fogVolumeDepthProvider==null){_fogVolumes?.Dispose();FogVolumeUnavailableReason="Explicit current fog depth provider required";return source;}
                if(!FogVolumeBinding.TryCreate(fogVolumes,_sourceCamera,source.width,source.height,out var binding,out var reason))
                {_fogVolumes?.Dispose();FogVolumeUnavailableReason=reason;return source;}
                var depth=fogVolumeDepthProvider(_sourceCamera,source);
                if(_fogVolumes==null)_fogVolumes=new FogVolumeRenderer();
                if(_fogVolumes.TryRender(source,depth,binding,out var frame))return frame.color;
                FogVolumeUnavailableReason=_fogVolumes.UnavailableReason;return source;
            }
            catch(Exception e){_fogVolumes?.Dispose();FogVolumeUnavailableReason="Fog depth provider failed: "+e.GetType().Name;return source;}
        }

        private RenderTexture ApplySphereFog(
            RenderTexture source, ICollection<RenderTexture> temporaries)
        {
            if (sphereFog == null || !sphereFog.IsActive || _postMaterial == null || _sourceCamera == null)
                return source;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--disable-sphere-fog") >= 0)
                return source;
            // Use the actual camera matrices, including lens shift/custom view.
            // RenderTexture projection is required even for a screen presenter:
            // this stage reads and writes offscreen targets.
            Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(_sourceCamera.projectionMatrix, true) *
                _sourceCamera.worldToCameraMatrix;
            if (viewProjection.determinant == 0f) return source;
            Matrix4x4 inverse = viewProjection.inverse;
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(inverse[i]) || float.IsInfinity(inverse[i])) return source;
            Color linear = sphereFog.color.linear;
            _postMaterial.SetMatrix("_SphereFogInverseViewProjection", inverse);
            _postMaterial.SetVector("_SphereFogCenterRadius", new Vector4(
                sphereFog.center.x, sphereFog.center.y, sphereFog.center.z, sphereFog.radius));
            _postMaterial.SetVector("_SphereFogParameters", new Vector4(
                sphereFog.density, Mathf.Clamp01(sphereFog.maximumOpacity), sphereFog.affectSky ? 1f : 0f, 0f));
            _postMaterial.SetVector("_SphereFogColor", new Vector4(linear.r, linear.g, linear.b, 0f));
            RenderTexture fogged = GetTemporary(source.width, source.height, source.format, temporaries);
            Graphics.CopyTexture(source, 0, 0, fogged, 0, 0);
            Graphics.Blit(null, fogged, _postMaterial, 12);
            return fogged;
        }

        private RenderTexture ApplyDepthOfField(
            RenderTexture source, ICollection<RenderTexture> temporaries)
        {
            if(bokehDepthOfField!=null&&bokehDepthOfField.enabled)
            {
                BokehDepthOfFieldUnavailableReason=null;
                try
                {
                    if(bokehDepthProvider==null){_bokehDepthOfField?.Dispose();BokehDepthOfFieldUnavailableReason="Explicit current linear-depth provider required";return source;}
                    var depth=bokehDepthProvider(_sourceCamera,source);
                    if(_bokehDepthOfField==null)_bokehDepthOfField=new BokehDepthOfFieldRenderer();
                    if(_bokehDepthOfField.TryRender(source,depth,bokehDepthOfField,out var frame))return frame.color;
                    BokehDepthOfFieldUnavailableReason=_bokehDepthOfField.UnavailableReason;return source;
                }
                catch(Exception error){_bokehDepthOfField?.Dispose();BokehDepthOfFieldUnavailableReason="Bokeh depth provider failed: "+error.GetType().Name;return source;}
            }
            _bokehDepthOfField?.Dispose();_bokehDepthOfField=null;BokehDepthOfFieldUnavailableReason=null;
            if (!_depthOfFieldActive || _depthOfFieldMaterial == null) return source;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--disable-story-dof") >= 0)
                return source;
            // Current ADV camera data and its explicit DOF clip select quality
            // 3 (Bokeh).  Quality 1/2 use VL's separate MRT route and are kept
            // disabled until that shader contract is recovered.
            if (_depthOfFieldQuality != 3) return source;

            float focalLengthMillimetres = _sourceCamera != null && _sourceCamera.usePhysicalProperties
                ? _sourceCamera.focalLength
                : 12f / Mathf.Tan((_sourceCamera == null ? 60f : _sourceCamera.fieldOfView) *
                    Mathf.Deg2Rad * 0.5f);
            float focalLengthMetres = focalLengthMillimetres / 1000f;
            float focalPoint = Mathf.Max(focalLengthMetres + 0.0001f, _depthOfFieldFocalPoint);
            float aperture = Mathf.Max(0.05f, _depthOfFieldFNumber);
            float apertureDiameter = focalLengthMillimetres / aperture;
            float maxCoC = (apertureDiameter * focalLengthMetres) /
                Mathf.Max(0.0001f, focalPoint - focalLengthMetres);
            // Current native DrawVLDOFBokeh: spread * 0.5 * 30 / 1080.
            // Camera-authored settings have already received the independent
            // CameraManager 0.5 conversion before reaching this component.
            float maxRadius = Mathf.Max(0f, _depthOfFieldMaxBlurSpread) *
                0.5f * 30f / 1080f;
            if (maxRadius <= 0.0000001f) return source;

            int halfWidth = Mathf.Max(1, source.width / 2);
            int halfHeight = Mathf.Max(1, source.height / 2);
            float reciprocalAspect = 1f / (halfWidth / (float)halfHeight);
            PrepareBokehKernel(maxRadius, reciprocalAspect);

            float foreground = Mathf.Clamp(_depthOfFieldForegroundBlurExtrude, 0f, 0.5f) * 2f;
            foreground *= foreground;
            bool foregroundActive = foreground > 0.0000001f;
            _depthOfFieldMaterial.SetVector("_SourceSize", new Vector4(
                source.width, source.height, 1f / source.width, 1f / source.height));
            _depthOfFieldMaterial.SetVector("_CoCParams", new Vector4(
                focalPoint, maxCoC, maxRadius, reciprocalAspect));
            // Recovered ShaderVariablesVLDOFBokeh layout.  z is the authored
            // foreground extrusion used by CalcBokehCoc; w is its independently
            // clamped/squared coverage control for the foreground blur output.
            _depthOfFieldMaterial.SetVector("_BokehConstants", new Vector4(
                2f / 1080f, 4f / 1080f,
                _depthOfFieldForegroundBlurExtrude, foreground));
            _depthOfFieldMaterial.SetVectorArray("_BokehKernel", _bokehKernel);

            RenderTexture fullCoC = GetTemporary(
                source.width, source.height, RenderTextureFormat.RHalf, temporaries);
            fullCoC.filterMode = FilterMode.Point;
            RenderTexture prefilterBack = GetTemporary(
                halfWidth, halfHeight, RenderTextureFormat.ARGBHalf, temporaries);
            RenderTexture blurredBack = GetTemporary(
                halfWidth, halfHeight, RenderTextureFormat.ARGBHalf, temporaries);
            RenderTexture floodBack = GetTemporary(
                halfWidth, halfHeight, RenderTextureFormat.ARGBHalf, temporaries);
            RenderTexture postBack = GetTemporary(
                halfWidth, halfHeight, RenderTextureFormat.ARGBHalf, temporaries);
            RenderTexture destination = GetTemporary(
                source.width, source.height, RenderTextureFormat.ARGBHalf, temporaries);

            Graphics.Blit(source, fullCoC, _depthOfFieldMaterial, 0);
            _depthOfFieldMaterial.SetTexture("_FullCoCTexture", fullCoC);
            _depthOfFieldMaterial.SetTexture("_SourceGatherTexture", source);
            _depthOfFieldMaterial.SetTexture("_CoCGatherTexture", fullCoC);
            Graphics.Blit(source, prefilterBack, _depthOfFieldMaterial, 1);

            RenderTexture postFront = null;
            if (foregroundActive)
            {
                RenderTexture prefilterFront = GetTemporary(
                    halfWidth, halfHeight, RenderTextureFormat.ARGBHalf, temporaries);
                RenderTexture inflateFirst = GetTemporary(
                    Mathf.Max(1, halfWidth / 2), Mathf.Max(1, halfHeight / 2),
                    RenderTextureFormat.RHalf, temporaries);
                RenderTexture inflateSecond = GetTemporary(
                    Mathf.Max(1, halfWidth / 4), Mathf.Max(1, halfHeight / 4),
                    RenderTextureFormat.RHalf, temporaries);
                RenderTexture blurredFront = GetTemporary(
                    halfWidth, halfHeight, RenderTextureFormat.ARGBHalf, temporaries);
                postFront = GetTemporary(
                    halfWidth, halfHeight, RenderTextureFormat.ARGBHalf, temporaries);

                Graphics.Blit(source, prefilterFront, _depthOfFieldMaterial, 2);
                _depthOfFieldMaterial.SetTexture("_InflateTexture", prefilterFront);
                Graphics.Blit(prefilterFront, inflateFirst, _depthOfFieldMaterial, 3);
                inflateFirst.filterMode = FilterMode.Point;
                _depthOfFieldMaterial.SetTexture("_InflateTexture", inflateFirst);
                Graphics.Blit(inflateFirst, inflateSecond, _depthOfFieldMaterial, 4);
                _depthOfFieldMaterial.SetTexture("_InflatedCoCTexture", inflateSecond);

                Graphics.Blit(prefilterBack, blurredBack, _depthOfFieldMaterial, 5);
                Graphics.Blit(prefilterFront, blurredFront, _depthOfFieldMaterial, 6);
                Graphics.Blit(blurredBack, floodBack, _depthOfFieldMaterial, 7);
                Graphics.Blit(floodBack, postBack, _depthOfFieldMaterial, 8);
                Graphics.Blit(blurredFront, postFront, _depthOfFieldMaterial, 9);
                _depthOfFieldMaterial.SetTexture("_DOFFrontTexture", postFront);
            }
            else
            {
                Graphics.Blit(prefilterBack, blurredBack, _depthOfFieldMaterial, 5);
                Graphics.Blit(blurredBack, floodBack, _depthOfFieldMaterial, 7);
                Graphics.Blit(floodBack, postBack, _depthOfFieldMaterial, 8);
            }

            _depthOfFieldMaterial.SetTexture("_DOFBackTexture", postBack);
            _depthOfFieldMaterial.SetTexture("_DOFBackGatherTexture", postBack);
            _depthOfFieldMaterial.SetTexture("_FullCoCTexture", fullCoC);
            Graphics.Blit(source, destination, _depthOfFieldMaterial,
                foregroundActive ? 11 : 10);
            return destination;
        }

        private void PrepareBokehKernel(float maxRadius, float reciprocalAspect)
        {
            const int rings = 4;
            const int pointsPerRing = 7;
            float bladeCount = Mathf.Clamp(_depthOfFieldBladeCount, 3, 9);
            float curvature = 1f - Mathf.Clamp01(_depthOfFieldBladeCurvature);
            float rotation = _depthOfFieldBladeRotation * Mathf.Deg2Rad;
            int index = 0;
            for (int ring = 1; ring < rings; ring++)
            {
                float bias = 1f / pointsPerRing;
                float radius = (ring + bias) / (rings - 1f + bias);
                int points = ring * pointsPerRing;
                for (int point = 0; point < points; point++)
                {
                    float phi = 2f * Mathf.PI * point / points;
                    float numerator = Mathf.Cos(Mathf.PI / bladeCount);
                    float denominator = Mathf.Cos(phi - (2f * Mathf.PI / bladeCount) *
                        Mathf.Floor((bladeCount * phi + Mathf.PI) / (2f * Mathf.PI)));
                    float r = radius * Mathf.Pow(numerator / denominator, curvature);
                    float uRadius = r * Mathf.Cos(phi - rotation) * maxRadius;
                    float vRadius = r * Mathf.Sin(phi - rotation) * maxRadius;
                    _bokehKernel[index++] = new Vector4(
                        uRadius, vRadius,
                        Mathf.Sqrt(uRadius * uRadius + vRadius * vRadius),
                        uRadius * reciprocalAspect);
                }
            }
        }

        public void SetDepthOfField(
            bool active, int quality, float focalPoint, float fNumber,
            float maxBlurSpread, float foregroundBlurExtrude,
            bool useFNumber, float smoothness, float focalSize,
            int bladeCount, float bladeCurvature, float bladeRotation)
        {
            _depthOfFieldActive = active;
            _depthOfFieldQuality = quality;
            _depthOfFieldFocalPoint = focalPoint;
            _depthOfFieldFNumber = fNumber;
            _depthOfFieldMaxBlurSpread = maxBlurSpread;
            _depthOfFieldForegroundBlurExtrude = foregroundBlurExtrude;
            _depthOfFieldUseFNumber = useFNumber;
            _depthOfFieldSmoothness = smoothness;
            _depthOfFieldFocalSize = focalSize;
            _depthOfFieldBladeCount = bladeCount;
            _depthOfFieldBladeCurvature = bladeCurvature;
            _depthOfFieldBladeRotation = bladeRotation;
        }

        public string DepthOfFieldDiagnosticJson()
        {
            CultureInfo culture = CultureInfo.InvariantCulture;
            return "{\"active\":" + (_depthOfFieldActive ? "true" : "false") +
                ",\"quality\":" + _depthOfFieldQuality.ToString(culture) +
                ",\"focalPoint\":" + _depthOfFieldFocalPoint.ToString("R", culture) +
                ",\"fNumber\":" + _depthOfFieldFNumber.ToString("R", culture) +
                ",\"maxBlurSpread\":" + _depthOfFieldMaxBlurSpread.ToString("R", culture) +
                ",\"foregroundBlurExtrude\":" + _depthOfFieldForegroundBlurExtrude.ToString("R", culture) +
                ",\"useFNumber\":" + (_depthOfFieldUseFNumber ? "true" : "false") +
                ",\"smoothness\":" + _depthOfFieldSmoothness.ToString("R", culture) +
                ",\"focalSize\":" + _depthOfFieldFocalSize.ToString("R", culture) +
                ",\"bladeCount\":" + _depthOfFieldBladeCount.ToString(culture) +
                ",\"bladeCurvature\":" + _depthOfFieldBladeCurvature.ToString("R", culture) +
                ",\"bladeRotation\":" + _depthOfFieldBladeRotation.ToString("R", culture) + "}";
        }

        public void SetParaffin(
            StoryParaffinProfile backgroundProfile,
            StoryParaffinEvent command,
            float mixWeight)
        {
            _paraffinProfileActive = backgroundProfile != null && backgroundProfile.active;
            _paraffinCommandActive = command != null;
            _paraffinMixWeight = command == null ? 0f : Mathf.Clamp01(mixWeight);
            _paraffinBaseAspect = backgroundProfile != null && backgroundProfile.baseAspect != null
                ? Mathf.Max(0.000001f, backgroundProfile.baseAspect.value)
                : 16f / 9f;
            _paraffinSourcePathId = backgroundProfile == null ? 0 : backgroundProfile.sourcePathId;

            ResolveFlare(
                _paraffinProfileActive ? backgroundProfile.flare0 : null,
                command == null ? null : command.flare0,
                _paraffinMixWeight,
                _paraffinFlare0);
            ResolveFlare(
                _paraffinProfileActive ? backgroundProfile.flare1 : null,
                command == null ? null : command.flare1,
                _paraffinMixWeight,
                _paraffinFlare1);
        }

        public void SetStoryPostProcessProfile(StoryPostProcessProfile profile)
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--disable-story-post-profile") >= 0)
            {
                ResetStoryPostProcessProfile();
                return;
            }

            _storyPostProfile = profile != null && profile.active ? profile : null;
            if (_storyPostProfile == null)
            {
                ResetStoryPostProcessProfile();
                return;
            }

            StoryBloomProfile bloom = profile.bloom;
            _storyBloomThresholdLinear = Mathf.GammaToLinearSpace(
                ProfileFloat(bloom == null ? null : bloom.threshold, 0.95f));
            float bloomIntensity = ProfileFloat(
                bloom == null ? null : bloom.intensity, 2.5f);
            // SetupVLBloom packs exp2(intensity / 10) - 1 in _Bloom_Settings.x.
            _storyBloomIntensityLinear = Mathf.Pow(2f, bloomIntensity / 10f) - 1f;
            _storyBloomDiffusion = Mathf.Clamp(
                ProfileInt(bloom == null ? null : bloom.diffusion, 6), 2, 7);

            StoryColorAdjustmentsProfile color = profile.colorAdjustments;
            float postExposure = ProfileFloat(
                color == null ? null : color.postExposure, -0.05f);
            _storyPostExposure = Mathf.Pow(2f, postExposure);
            _storyColorContrast = ProfileFloat(
                color == null ? null : color.contrast, 5.5f);
            _storyColorSaturation = ProfileFloat(
                color == null ? null : color.saturation, 7.5f);
            _storyColorHueShift = ProfileFloat(
                color == null ? null : color.hueShift, 0f);
            // PASS299 replayed the game's exact ColorGradingComputePass DXBC.
            // Keep separate immutable LUTs for the captured riverbed profile and
            // the recovered classroom profile; postExposure remains outside the
            // LUT and is applied independently above.
            bool neutralHueAndFilter =
                Mathf.Abs(_storyColorHueShift) < 0.00001f &&
                IsWhite(ProfileColor(
                    color == null ? null : color.colorFilter, Color.white));
            _storyPostProfileColorCompatible =
                Mathf.Abs(_storyColorContrast - 5.5f) < 0.00001f &&
                Mathf.Abs(_storyColorSaturation - 7.5f) < 0.00001f &&
                neutralHueAndFilter;
            _storyPostProfileClassroomColorCompatible =
                Mathf.Abs(_storyColorContrast - 6f) < 0.00001f &&
                Mathf.Abs(_storyColorSaturation - 8.5f) < 0.00001f &&
                neutralHueAndFilter;

            StoryDiffusionProfile diffusion = profile.diffusion;
            if (diffusion != null && diffusion.active)
            {
                _storyDiffusionStride = ProfileFloat(diffusion.diffusion, 0.36f);
                _storyDiffusionContrastThreshold =
                    ProfileFloat(diffusion.contrastThreshold, 0.5f);
                _storyDiffusionContrastPower =
                    ProfileFloat(diffusion.contrastPower, 0.075f);
                _storyDiffusionBlend = ProfileFloat(diffusion.blend, 0.52f);
            }
            else
            {
                _storyDiffusionStride = 0f;
                _storyDiffusionContrastThreshold = 0.5f;
                _storyDiffusionContrastPower = 0f;
                _storyDiffusionBlend = 0f;
            }

            StoryChromaticAberrationProfile chromatic = profile.chromaticAberration;
            _storyChromaticAberration = chromatic != null && chromatic.active
                ? ProfileFloat(chromatic.intensity, 0.05f)
                : 0f;

            StoryTonemappingProfile tonemapping = profile.tonemapping;
            _storyPostProfileTonemappingCompatible = tonemapping != null &&
                tonemapping.active &&
                ProfileInt(tonemapping.mode, 5) == 5 &&
                Mathf.Abs(ProfileFloat(tonemapping.toeStrength, 0f)) < 0.00001f &&
                Mathf.Abs(ProfileFloat(tonemapping.toeLength, 0.5f) - 0.5f) < 0.00001f &&
                Mathf.Abs(ProfileFloat(tonemapping.shoulderStrength, 0.2f) - 0.2f) < 0.00001f &&
                Mathf.Abs(ProfileFloat(tonemapping.shoulderLength, 1f) - 1f) < 0.00001f &&
                Mathf.Abs(ProfileFloat(tonemapping.shoulderAngle, 0f)) < 0.00001f &&
                Mathf.Abs(ProfileFloat(tonemapping.gtLinearSectionStart, 0.9f) - 0.9f) < 0.00001f &&
                Mathf.Abs(ProfileFloat(tonemapping.gtContrast, 1f) - 1f) < 0.00001f &&
                Mathf.Abs(ProfileFloat(tonemapping.gtBlackBrightness, 1f) - 1f) < 0.00001f &&
                Mathf.Abs(ProfileFloat(tonemapping.gtLinearSelectionLength, 0.5f) - 0.5f) < 0.00001f;
            int colorLutSelection = _storyPostProfileColorCompatible ? 0 :
                _storyPostProfileClassroomColorCompatible ? 1 : 2;
            if (colorLutSelection != _lastStoryColorLutSelectionLog)
            {
                _lastStoryColorLutSelectionLog = colorLutSelection;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[PhotoMode] Story color LUT selection: riverbedExact={0} " +
                    "classroomExact={1} contrast={2:R} saturation={3:R} hue={4:R}",
                    _storyPostProfileColorCompatible,
                    _storyPostProfileClassroomColorCompatible,
                    _storyColorContrast, _storyColorSaturation, _storyColorHueShift));
            }
        }

        public void ResetStoryPostProcessProfile()
        {
            _storyPostProfile = null;
            _storyBloomThresholdLinear = 0.89000553f;
            _storyBloomIntensityLinear = 0.18920708f;
            _storyBloomDiffusion = 6;
            _storyPostExposure = 0.965936303f;
            _storyDiffusionStride = 0.36f;
            _storyDiffusionContrastThreshold = 0.5f;
            _storyDiffusionContrastPower = 0.075f;
            _storyDiffusionBlend = 0.52f;
            _storyChromaticAberration = 0.05f;
            _storyColorContrast = 5.5f;
            _storyColorSaturation = 7.5f;
            _storyColorHueShift = 0f;
            _storyPostProfileColorCompatible = true;
            _storyPostProfileClassroomColorCompatible = false;
            _storyPostProfileTonemappingCompatible = true;
            _lastStoryColorLutSelectionLog = -1;
        }

        public string StoryPostProcessProfileDiagnosticJson()
        {
            CultureInfo culture = CultureInfo.InvariantCulture;
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--disable-story-post-profile") >= 0)
                return "{\"active\":false,\"disabled\":true}";
            if (_storyPostProfile == null)
                return "{\"active\":false}";
            return "{\"active\":true" +
                ",\"sceneProfile\":\"" + EscapeJson(_storyPostProfile.sceneProfileName) + "\"" +
                ",\"commonProfile\":\"" + EscapeJson(_storyPostProfile.commonProfileName) + "\"" +
                ",\"bloomThresholdLinear\":" + _storyBloomThresholdLinear.ToString("R", culture) +
                ",\"bloomIntensityLinear\":" + _storyBloomIntensityLinear.ToString("R", culture) +
                ",\"bloomDiffusion\":" + _storyBloomDiffusion.ToString(culture) +
                ",\"postExposure\":" + _storyPostExposure.ToString("R", culture) +
                ",\"diffusion\":[" + _storyDiffusionStride.ToString("R", culture) + "," +
                    _storyDiffusionContrastThreshold.ToString("R", culture) + "," +
                    _storyDiffusionContrastPower.ToString("R", culture) + "," +
                    _storyDiffusionBlend.ToString("R", culture) + "]" +
                ",\"chromaticAberration\":" + _storyChromaticAberration.ToString("R", culture) +
                ",\"colorAdjustments\":[" + _storyColorContrast.ToString("R", culture) + "," +
                    _storyColorSaturation.ToString("R", culture) + "," +
                    _storyColorHueShift.ToString("R", culture) + "]" +
                ",\"capturedLutColorCompatible\":" +
                    (_storyPostProfileColorCompatible ? "true" : "false") +
                ",\"classroomLutColorCompatible\":" +
                    (_storyPostProfileClassroomColorCompatible ? "true" : "false") +
                ",\"capturedLutTonemappingCompatible\":" +
                    (_storyPostProfileTonemappingCompatible ? "true" : "false") + "}";
        }

        private static float ProfileFloat(StoryFloatParameter value, float fallback)
        {
            return value != null && value.overrideState ? value.value : fallback;
        }

        private static int ProfileInt(StoryIntParameter value, int fallback)
        {
            return value != null && value.overrideState ? value.value : fallback;
        }

        private static Color ProfileColor(StoryColorParameter value, Color fallback)
        {
            return value != null && value.overrideState && value.value != null
                ? value.value.ToColor()
                : fallback;
        }

        private static bool IsWhite(Color value)
        {
            return Mathf.Abs(value.r - 1f) < 0.00001f &&
                Mathf.Abs(value.g - 1f) < 0.00001f &&
                Mathf.Abs(value.b - 1f) < 0.00001f;
        }

        private static string EscapeJson(string value)
        {
            return string.IsNullOrEmpty(value)
                ? string.Empty
                : value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void ResolveFlare(
            StoryFlareParameter baselineParameter,
            StoryFlareParameter commandParameter,
            float weight,
            RuntimeFlare result)
        {
            StoryFlareSetting baseline = baselineParameter != null &&
                baselineParameter.overrideState ? baselineParameter.value : null;
            StoryFlareSetting target = commandParameter != null &&
                commandParameter.overrideState ? commandParameter.value : baseline;
            if (baseline == null && target == null)
            {
                result.SetNone();
                return;
            }

            int baselineType = baseline == null ? 0 : baseline.type;
            bool baselineFix = baseline != null && baseline.fixToBaseAspect;
            Vector2 baselineCenter = Vector2Of(baseline == null ? null : baseline.center);
            Color baselineColor0 = ColorOf(baseline == null ? null : baseline.color0);
            Color baselineColor1 = ColorOf(baseline == null ? null : baseline.color1);
            Vector2 baselineSize = Vector2Of(baseline == null ? null : baseline.size);
            int targetType = target == null ? 0 : target.type;
            bool targetFix = target != null && target.fixToBaseAspect;
            Vector2 targetCenter = Vector2Of(target == null ? null : target.center);
            Color targetColor0 = ColorOf(target == null ? null : target.color0);
            Color targetColor1 = ColorOf(target == null ? null : target.color1);
            Vector2 targetSize = Vector2Of(target == null ? null : target.size);

            float t = Mathf.Clamp01(weight);
            // FlareParameter.Interp switches discrete fields to `to` for any
            // positive weight and linearly interpolates every vector/color.
            result.type = t <= 0f ? baselineType : targetType;
            result.fixToBaseAspect = t <= 0f ? baselineFix : targetFix;
            result.center = Vector2.Lerp(baselineCenter, targetCenter, t);
            result.color0 = Color.Lerp(baselineColor0, targetColor0, t);
            result.color1 = Color.Lerp(baselineColor1, targetColor1, t);
            result.size = Vector2.Lerp(baselineSize, targetSize, t);
        }

        private static Vector2 Vector2Of(StoryVector2 value)
        {
            return value == null ? Vector2.zero : new Vector2(value.x, value.y);
        }

        private static Color ColorOf(StoryColor value)
        {
            return value == null ? Color.clear : value.ToColor();
        }

        private RenderTexture ApplyParaffin(
            RenderTexture source,
            ICollection<RenderTexture> temporaries)
        {
            // Retained as an automated A/B validation switch. It bypasses only
            // the draw, leaving timeline/profile resolution and diagnostics live.
            if (_paraffinMaterial == null || Array.IndexOf(
                    Environment.GetCommandLineArgs(),
                    "--disable-story-paraffin-render") >= 0)
                return source;
            RenderTexture current = ApplySphereFlare(source, _paraffinFlare0, temporaries);
            return ApplySphereFlare(current, _paraffinFlare1, temporaries);
        }

        private RenderTexture ApplySphereFlare(
            RenderTexture source,
            RuntimeFlare flare,
            ICollection<RenderTexture> temporaries)
        {
            // The focus scene and its classroom hand-off use only native
            // FlareType.None (0) and Sphere (1). Other pass types remain
            // deliberately inactive until their shader bytecode is captured.
            if (flare == null || flare.type != 1 ||
                flare.size.x <= 0f || flare.size.y <= 0f)
                return source;

            float currentAspect = source.width / (float)Mathf.Max(1, source.height);
            float aspectFix = flare.fixToBaseAspect
                ? currentAspect / Mathf.Max(0.000001f, _paraffinBaseAspect)
                : 1f;
            _paraffinMaterial.SetVector("_FlareTransform", new Vector4(
                -flare.center.x / aspectFix,
                -flare.center.y,
                aspectFix / flare.size.x,
                1f / flare.size.y));
            // DrawFlare calls Color.linear before binding both gradient colors.
            _paraffinMaterial.SetColor("_FlareColor0", flare.color0.linear);
            _paraffinMaterial.SetColor("_FlareColor1", flare.color1.linear);
            RenderTexture destination = GetTemporary(
                source.width, source.height, source.format, temporaries);
            Graphics.Blit(source, destination, _paraffinMaterial, 0);
            return destination;
        }

        public string ParaffinDiagnosticJson()
        {
            CultureInfo culture = CultureInfo.InvariantCulture;
            return "{\"profileActive\":" + (_paraffinProfileActive ? "true" : "false") +
                ",\"commandActive\":" + (_paraffinCommandActive ? "true" : "false") +
                ",\"mixWeight\":" + _paraffinMixWeight.ToString("R", culture) +
                ",\"baseAspect\":" + _paraffinBaseAspect.ToString("R", culture) +
                ",\"sourcePathId\":" + _paraffinSourcePathId.ToString(culture) +
                ",\"flare0\":" + FlareDiagnosticJson(_paraffinFlare0, culture) +
                ",\"flare1\":" + FlareDiagnosticJson(_paraffinFlare1, culture) + "}";
        }

        private static string FlareDiagnosticJson(RuntimeFlare value, CultureInfo culture)
        {
            return "{\"type\":" + value.type.ToString(culture) +
                ",\"fixToBaseAspect\":" + (value.fixToBaseAspect ? "true" : "false") +
                ",\"center\":[" + value.center.x.ToString("R", culture) + "," +
                value.center.y.ToString("R", culture) + "]" +
                ",\"size\":[" + value.size.x.ToString("R", culture) + "," +
                value.size.y.ToString("R", culture) + "]}";
        }

        private static void DumpHalfSurface(RenderTexture source, string prefix)
        {
            string directory = Path.GetDirectoryName(prefix);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            Texture2D image = new Texture2D(source.width, source.height, TextureFormat.RGBAHalf, false, true);
            image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            image.Apply(false, false);
            File.WriteAllBytes(prefix + ".rgba16f.raw", image.GetRawTextureData());
            File.WriteAllText(prefix + ".json", string.Format(
                "{{\"width\":{0},\"height\":{1},\"format\":\"RGBA16_FLOAT\",\"row_pitch\":{2},\"origin\":\"bottom-left\"}}\n",
                source.width, source.height, source.width * 8));
            RenderTexture.active = previous;
            DestroyImmediate(image);
        }

        private static void DumpActorDataPng(RenderTexture source, string prefix)
        {
            string directory = Path.GetDirectoryName(prefix);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            // Eight-bit alpha is sufficient for the exact 1/16 material codes;
            // PNG makes dense motion sweeps two orders of magnitude smaller than
            // retaining a full RGBA16F surface for every candidate pose.
            Texture2D image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            image.Apply(false, false);
            File.WriteAllBytes(prefix + "-actor-data.png", image.EncodeToPNG());
            File.WriteAllText(prefix + "-actor-data.json", string.Format(
                "{{\"width\":{0},\"height\":{1},\"format\":\"RGBA8_UNORM\",\"origin\":\"bottom-left\",\"material_code\":\"round(alpha*16)-1\"}}\n",
                source.width, source.height));
            RenderTexture.active = previous;
            DestroyImmediate(image);
        }

        private void ReleaseActorData()
        {
            if (_actorData == null) return;
            _actorData.Release();
            DestroyImmediate(_actorData);
            _actorData = null;
        }

        private void EnsureHistory(int width, int height)
        {
            if (_history != null && _history.width == width && _history.height == height) return;
            ReleaseHistory();
            _history = new RenderTexture(width, height, 0, SupersamplePresenter.SceneColorFormat)
            {
                name = "TaaAccumulation",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            _history.Create();
            _historyValid = false;
            _historyFrame = -1;
        }

        private void ReleaseHistory()
        {
            if (_history != null)
            {
                _history.Release();
                DestroyImmediate(_history);
                _history = null;
            }
            _historyValid = false;
            _historyFrame = -1;
        }

        public static void RequestPostInputDump(string prefix)
        {
            _requestedPostDumpPrefix = prefix;
        }

        public static void RequestCurrentHdrDump(string prefix)
        {
            _requestedCurrentHdrDumpPrefix = prefix;
        }

        public static void RequestActorDataPngDump(string prefix)
        {
            _requestedActorDataPngPrefix = prefix;
        }

        private void OnDisable()
        {
            ReleaseAuthoredColor();
            _bokehDepthOfField?.Dispose();_bokehDepthOfField=null;
            _fogVolumes?.Dispose();_fogVolumes=null;
            _volumetricLighting?.Dispose();_volumetricLighting=null;
            _lensFlares?.Dispose();_lensFlares=null;
            _lowResolutionFx?.Dispose();_lowResolutionFx=null;
            _heavyFx?.Dispose();_heavyFx=null;
            ReleaseActorData();
            ReleaseHistory();
            if (_postMaterial != null) DestroyImmediate(_postMaterial);
            if (_depthOfFieldMaterial != null) DestroyImmediate(_depthOfFieldMaterial);
            if (_paraffinMaterial != null) DestroyImmediate(_paraffinMaterial);
            if (_actorCamera != null) DestroyImmediate(_actorCamera.gameObject);
            if (_capturedColorLut != null) DestroyImmediate(_capturedColorLut);
            if (_classroomColorLut != null) DestroyImmediate(_classroomColorLut);
            _postMaterial = null;
            _depthOfFieldMaterial = null;
            _paraffinMaterial = null;
            _actorCamera = null;
            _capturedColorLut = null;
            _classroomColorLut = null;
            _capturedLutAttempted = false;
        }
    }
}
