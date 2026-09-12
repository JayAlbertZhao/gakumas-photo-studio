using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ActorAnimation;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Networking;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VL.FaceSystem;

namespace GakumasPhotoMode
{
    public partial class CharacterSceneRuntime : MonoBehaviour
    {
        protected BundleCatalog _catalog;
        protected List<BundleRecord> _faces;
        protected List<BundleRecord> _costumes;
        protected List<BundleRecord> _hairs;
        protected List<BundleRecord> _motions;
        protected List<string> _characterIds;
        protected string _characterId = "fktn";
        protected GameObject _characterRoot;
        protected GameObject _body;
        protected GameObject _face;
        protected Transform _faceHead;
        protected Transform _faceHeadPrefabParent;
        protected Vector3 _faceHeadPrefabLocalPosition;
        protected Quaternion _faceHeadPrefabLocalRotation;
        protected Vector3 _faceHeadPrefabLocalScale;
        protected GameObject _hair;
        protected GameObject _faceDriverRoot;
        protected Animator _bodyAnimator;
        protected Animator _faceAnimator;
        protected VLActorFaceModel _faceDriver;
        protected FaceExpressionRenderer _faceExpression;
        protected FaceDecalRuntime _faceDecals;
        protected ActorMaterialEffectRuntime _faceMaterialEffects;
        protected HairDynamicsSystem _hairDynamics;
        private NaturalWindSettings _naturalWind;
        private double? _naturalWindTime;
        protected HairDynamicsSystem _garmentDynamics;
        protected QuartzArmDeformationSystem _quartzArmDeformation;
        protected QuartzLegAndRotationDeformationSystem _quartzLegRotationDeformation;
        protected QuartzGarmentDeformationSystem _quartzGarmentDeformation;
        protected BreastDynamicsSystem _breastDynamics;
        protected BodySoftTissueDynamicsSystem _bodySoftTissueDynamics;
        protected HairDynamicsSystem _skirtDynamics;
        protected CapturedLookAtRuntime _capturedLookAt;
        protected FaceMotionLibrary _faceMotionLibrary;
        protected StoryTimelinePlayer _storyPlayer;
        protected readonly float[] _storyPreviousFaceWeights = new float[192];
        protected readonly float[] _storyCurrentFaceWeights = new float[192];
        protected PlayableGraph _motionGraph;
        public double PhotoMotionTime { get { return _bodyPlayable.IsValid() ? _bodyPlayable.GetTime() : 0d; } }
        public bool PhotoMotionPlaying { get { return _motionGraph.IsValid() && _motionGraph.IsPlaying(); } }
        protected AnimationClipPlayable _bodyPlayable;
        protected AnimationClipPlayable _facePlayable;
        protected AnimationMixerPlayable _storyMixer;
        protected AnimationClipPlayable _storyPreviousPlayable;
        protected AnimationClipPlayable _storyCurrentPlayable;
        protected readonly Dictionary<string, AnimationClip> _storyBodyClips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        protected readonly Dictionary<string, AudioClip> _voiceCache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        protected string _storyPreviousMotion;
        protected string _storyCurrentMotion;
        protected int _storyVoiceRequest;
        protected AudioSource _audioSource;
        protected OrbitPhotoCamera _orbit;
        protected Light _keyLight;
        protected bool _useLegacyRimDirection;
        protected bool _useRootOnlyRimDirection;
        protected bool _disableStoryActorProfile;
        protected StoryActorRenderProfile _storyActorRenderProfile;
        protected bool _storyActorRenderProfileActive;
        protected string _storyActorRenderProfileSignature = string.Empty;
        // CB0[153] expressed in the Unity camera basis after reconciling the
        // D3D view-axis signs and the captured Actor root. Transforming this
        // vector by the live photo camera reproduces the original r7 view-space
        // rim convention while preserving orbit-camera behaviour.
        protected static readonly Vector3 CapturedViewRimDirection =
            new Vector3(-0.29590727f, -0.29125261f, -0.90973117f).normalized;
        protected static readonly Vector3 CapturedActorLightDirection =
            new Vector3(0.38302225f, 0.42261827f, 0.82139379f).normalized;
        protected static readonly Vector3 CapturedActorRimViewDirection =
            new Vector3(-0.29704621f, -0.27563736f, 0.91421419f).normalized;
        protected static readonly Color CapturedActorShadeTint =
            new Color(0.85849059f, 0.76552117f, 0.74105555f, 1f);
        protected CapturedActorShadowMap _capturedActorShadowMap;
        protected ActorRenderControls _actorRenderControls;
        protected Cubemap _actorEnvironmentCube;
        protected Cubemap _actorEyeEnvironmentCube;
        protected Texture2DArray _actorEnvironmentArray;
        protected Texture2DArray _actorEyeEnvironmentArray;
        protected GameObject _photoStudioRoot;
        protected SupersamplePresenter _supersamplePresenter;
        protected OriginalRiverbedEnvironment _riverbedEnvironment;
        protected AdvStoryBackgroundRuntime _storyBackgroundRuntime;
        protected AdvStoryOverlayRuntime _storyOverlayRuntime;
        protected StoryActorDeclaration[] _storyActorDeclarations;
        protected StoryActorRendererEvent _storyActorRendererState;
        protected readonly Dictionary<string, GameObject> _storyActorExtras =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        protected StoryPropDeclaration[] _storyPropDeclarations;
        protected readonly Dictionary<string, GameObject> _storyProps =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        protected GameObject _storyPropRoot;
        protected bool _useAdvPhotoBackground;
        protected bool _useRiverbedBackground;
        protected bool _actorRenderProfileInitialized;
        protected OriginalStyleRenderPipeline.PresentationContext _renderContextProfile =
            OriginalStyleRenderPipeline.PresentationContext.StudioLocal;
        protected int _costumeIndex;
        protected string _requestedHairLabel;
        protected int _motionIndex;
        protected int _expressionIndex;
        protected int _voiceIndex;
        protected bool _paused;
        protected bool _ambientWallpaperDemo;
        protected bool _initialized;
        protected string _stagingRoot;
        protected string _status = "Starting";
        protected Color _storyActorColor = Color.white;
        protected Vector2 _storyShakePosition;
        protected bool _storyCameraDofActive;
        protected float _storyCameraDofFocalPoint = 4f;
        protected float _storyCameraDofFNumber = 4f;
        protected float _storyCameraDofMaxBlurSpread = 1.5f;
        protected StoryDepthOfFieldEvent _storyDepthOfFieldOverride;
        public Camera PreviewCamera { get; private set; }
        public int RendererCount { get; private set; }
        public int ErrorMaterialCount { get; private set; }
        public int CostumeCount { get { return _costumes == null ? 0 : _costumes.Count; } }
        public int MotionCount { get { return _motions == null ? 0 : _motions.Count; } }
        public int VoiceCount { get { return _catalog == null || _catalog.Manifest.voices == null ? 0 : _catalog.Manifest.voices.Length; } }
        public AnimationClip CurrentBodyClip { get { return _bodyPlayable.IsValid() ? _bodyPlayable.GetAnimationClip() : null; } }
        public AnimationClip CurrentFaceClip { get { return _facePlayable.IsValid() ? _facePlayable.GetAnimationClip() : null; } }
        public int FaceShapeCount { get { return _faceExpression == null ? 0 : _faceExpression.ShapeCount; } }
        public int ExpressionPresetCount { get { return _faceMotionLibrary == null ? 0 : _faceMotionLibrary.PhotoExpressionCount; } }
        public int HairDynamicBoneCount { get { return _hairDynamics == null ? 0 : _hairDynamics.SimulatedBoneCount; } }
        public int GarmentDynamicBoneCount { get { return _garmentDynamics == null ? 0 : _garmentDynamics.SimulatedBoneCount; } }
        public int BodySoftTissueDynamicBoneCount { get { return _bodySoftTissueDynamics == null ? 0 : _bodySoftTissueDynamics.SimulatedBoneCount; } }
        public int ClothDynamicBoneCount { get { return _skirtDynamics == null ? 0 : _skirtDynamics.SimulatedBoneCount; } }
        public int FaceActiveWeightCount { get { return _faceExpression == null ? 0 : _faceExpression.ActiveWeightCount; } }
        public float FaceMaximumWeight { get { return _faceExpression == null ? 0f : _faceExpression.MaximumWeight; } }
        public string CurrentExpression { get { return _faceMotionLibrary == null ? "unavailable" : _faceMotionLibrary.CurrentPhotoExpressionLabel; } }
        public string CurrentCostume { get { return _costumes == null || _costumes.Count == 0 ? null : _costumes[_costumeIndex].name; } }
        public string CurrentCharacter { get { return _characterId; } }
        public string CurrentOutfitOwner { get { return CurrentCostume == null ? null : RecordOwnerId(CurrentCostume); } }
        public string CurrentRenderContext
        {
            get
            {
                switch (_renderContextProfile)
                {
                    case OriginalStyleRenderPipeline.PresentationContext.CapturedRiverbed:
                        return "CAPTURED RIVERBED / EXACT ACTOR ENV";
                    case OriginalStyleRenderPipeline.PresentationContext.BakedAdv:
                        return "ADV PLATE / LOCAL ACTOR ENV";
                    default:
                        return "STUDIO / LOCAL ACTOR ENV";
                }
            }
        }
        public string CurrentMotion { get { return _motions == null || _motions.Count == 0 ? null : _motions[_motionIndex].name; } }
        public bool StoryActive { get { return _storyPlayer != null && _storyPlayer.IsActive; } }
        public float StoryTime { get { return _storyPlayer == null ? 0f : _storyPlayer.TimeSeconds; } }
        public float StoryDuration { get { return _storyPlayer == null ? 0f : _storyPlayer.Duration; } }
        public string StoryMessage { get { return _storyPlayer == null ? string.Empty : _storyPlayer.CurrentMessage; } }
        public string StoryMotion { get { return _storyPlayer == null ? string.Empty : _storyPlayer.CurrentMotion; } }
        public string StoryFaceMotion { get { return _storyPlayer == null ? string.Empty : _storyPlayer.CurrentFaceMotion; } }
        public Color StoryActorColor { get { return _storyActorColor; } }

        protected void InitializeRuntime(string stagingRoot)
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;
            _stagingRoot = stagingRoot;
            ConfigureApplication();
            string[] qualityCommandLine = RuntimeArguments;
            _catalog = new BundleCatalog();
            _catalog.Load(stagingRoot);
            _faceMotionLibrary = new FaceMotionLibrary();
            _faceMotionLibrary.Load(stagingRoot);
            _faces = _catalog.RecordsForRole("face").ToList();
            _costumes = _catalog.RecordsForRole("costume").ToList();
            _hairs = _catalog.RecordsForRole("hair").ToList();
            _motions = _catalog.RecordsForRole("motion").ToList();
            _requestedHairLabel = CommandLineValue("--hair-label");
            // Every fixed-camera GPA regression below replays the archived
            // riverbed frame whose Actor draw uses fktn hair-0004.  Falling
            // back to the normal base hair silently invalidates the exact-pose
            // coverage registration (most visibly the 9392-byte hirco draw),
            // even though the body/camera still look plausible.  Keep an
            // explicit --hair-label authoritative, but make the capture
            // harness select its own recorded asset contract by default.
            bool fixedGpaRegression = qualityCommandLine.Contains("--capture-gpa-camera-and-quit") ||
                qualityCommandLine.Contains("--capture-gpa-camera-sweep-and-quit") ||
                qualityCommandLine.Contains("--capture-gpa-camera-mask-sweep-and-quit") ||
                qualityCommandLine.Contains("--capture-actor-cube-transform-sweep-and-quit");
            if (fixedGpaRegression && string.IsNullOrEmpty(_requestedHairLabel))
                _requestedHairLabel = "hair-0004";
            _characterIds = _faces.Select(value => RecordOwnerId(value.name))
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string requestedCharacter = CommandLineValue("--character-id");
            if (!string.IsNullOrEmpty(requestedCharacter) &&
                _characterIds.Any(value => string.Equals(value, requestedCharacter, StringComparison.OrdinalIgnoreCase)))
                _characterId = requestedCharacter.ToLowerInvariant();
            BuildEnvironment();
            BuildCharacter();
            string requestedCostume = CommandLineValue("--costume-label");
            string requestedOutfitOwner = CommandLineValue("--outfit-owner");
            if (string.IsNullOrEmpty(requestedCostume)) requestedCostume = "cstm-0045";
            int capturedCostume = !string.IsNullOrEmpty(requestedOutfitOwner)
                ? _costumes.FindIndex(value =>
                    string.Equals(RecordOwnerId(value.name), requestedOutfitOwner, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(PartVariantId(value.name), "cstm-0000", StringComparison.OrdinalIgnoreCase))
                : _costumes.FindIndex(value =>
                    string.Equals(value.label, requestedCostume, StringComparison.OrdinalIgnoreCase));
            if (capturedCostume < 0)
            {
                Debug.LogWarning("[PhotoMode] Requested costume label was not found: " + requestedCostume);
            }
            SelectCostume(capturedCostume >= 0 ? capturedCostume : 0);
            int photoIdle = _motions.FindIndex(value => string.Equals(value.label, "photo-idle-001", StringComparison.OrdinalIgnoreCase));
            SelectMotion(photoIdle >= 0 ? photoIdle : 0);
            string requestedMotion = CommandLineValue("--motion-label");
            if (!string.IsNullOrEmpty(requestedMotion) && !SelectMotionByLabel(requestedMotion))
                Debug.LogWarning("[PhotoMode] Requested motion label was not found: " + requestedMotion);
            string requestedExpression = CommandLineValue("--photo-expression-motion");
            if (string.IsNullOrEmpty(requestedExpression) || !SelectExpressionMotion(requestedExpression))
                SelectExpression(0);
            if (!RuntimeArguments.Contains("--unity-shadow-map"))
            {
                _capturedActorShadowMap = PreviewCamera.gameObject.AddComponent<CapturedActorShadowMap>();
                _capturedActorShadowMap.Initialize(_characterRoot);
                // Actor materials select the captured fitted map. Keep the Unity
                // light shadow alive only for the studio floor receiver so the
                // character retains a contact shadow instead of floating.
                Debug.Log("[PhotoMode] Enabled captured actor-fitted 4096 self-shadow map");
            }
            _storyPlayer = gameObject.AddComponent<StoryTimelinePlayer>();
            bool openInPhotoMode = RuntimeArguments.Contains("--photo-mode") ||
                _ambientWallpaperDemo;
            if (_storyPlayer.Initialize(this, stagingRoot) && Application.isPlaying && !openInPhotoMode)
            {
                _storyPlayer.StartStory(8.45f);
            }
            _useAdvPhotoBackground = openInPhotoMode &&
                (RuntimeArguments.Contains("--photo-adv-background") ||
                 _ambientWallpaperDemo);
            _useRiverbedBackground = openInPhotoMode &&
                _riverbedEnvironment != null && _riverbedEnvironment.IsLoaded &&
                !RuntimeArguments.Contains("--legacy-studio-background") &&
                !_useAdvPhotoBackground;
            if (openInPhotoMode) ApplyPhotoBackgroundPreference();
            if (_ambientWallpaperDemo) ConfigureAmbientWallpaperDemo();
            _status = "Ready";
        }

        protected string CommandLineValue(string option)
        {
            string[] arguments = RuntimeArguments;
            for (int index = 0; index + 1 < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[index + 1];
                }
            }
            return null;
        }

        protected void BuildEnvironment()
        {
            _photoStudioRoot = new GameObject("PhotoStudioEnvironment");
            Camera camera = new GameObject("PhotoCamera").AddComponent<Camera>();
            PreviewCamera = camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.48f, 0.58f, 0.80f, 1f);
            camera.allowHDR = true;
            camera.allowMSAA = true;
            camera.nearClipPlane = 0.02f;
            camera.farClipPlane = 50f;
            camera.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.DepthNormals | DepthTextureMode.MotionVectors;
            _orbit = camera.gameObject.AddComponent<OrbitPhotoCamera>();
            if (!OriginalShaderUrpBootstrap.IsRequested)
            {
                camera.gameObject.AddComponent<OriginalStyleRenderPipeline>();
                _actorRenderControls = CreateRenderControls(camera);
                AttachApplicationCameraComponents(camera);
            }
            else
            {
                UniversalAdditionalCameraData cameraData = camera.GetUniversalAdditionalCameraData();
                cameraData.renderPostProcessing = false;
                cameraData.requiresDepthTexture = true;
                cameraData.requiresColorTexture = true;
            }
            _orbit.ApplyPose();

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.70f, 0.92f);
            RenderSettings.ambientEquatorColor = new Color(0.46f, 0.43f, 0.62f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.22f, 0.35f);
            RenderSettings.reflectionIntensity = 0.55f;

            _keyLight = new GameObject("KeyLight").AddComponent<Light>();
            _keyLight.type = LightType.Directional;
            _keyLight.intensity = 1.0f;
            _keyLight.color = Color.white;
            // Actor shading and Actor shadow projection use different captured
            // directions. CB0[148].xyz remains the stylized BRDF/ramp direction,
            // while VS 54E7B147 CB0[137..140] gives an orthographic shadow basis.
            // Its normalized depth row is the direction from the receiver toward
            // the shadow light; the value below is additionally transformed by the
            // inverse captured Actor-root rotation for this identity-root viewer.
            // Keeping the Unity light on the BRDF direction made
            // the coverage strength correct in pass77 but cast the self-shadow from
            // the wrong side of the face and braids.
            Vector3 capturedShadowDirection = new Vector3(
                -0.17343573f, 0.21206431f, 0.96174257f).normalized;
            _keyLight.transform.rotation = Quaternion.LookRotation(-capturedShadowDirection, Vector3.up);
            _keyLight.shadows = LightShadows.Soft;
            // Bound Actor CB0[144].x is exactly 1.0.  In the active PS it is
            // the final shadow-strength lerp before the cubic smoothstep, so
            // the old 0.48 local light setting weakened authored face/hair
            // shadow coverage even though the shader equation was otherwise
            // correct.
            _keyLight.shadowStrength = 1.0f;
            bool useUnityShadowBias = RuntimeArguments.Contains("--unity-shadow-bias");
            _keyLight.shadowBias = useUnityShadowBias ? 0.025f : 0f;
            _keyLight.shadowNormalBias = useUnityShadowBias ? 0.30f : 0f;

            Light fill = new GameObject("FillLight").AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.35f;
            fill.color = new Color(0.56f, 0.72f, 1f);
            fill.transform.rotation = Quaternion.Euler(18f, 150f, 0f);
            Vector3 fillDirection = -fill.transform.forward;
            Shader.SetGlobalVector("_ActorFillDirection", new Vector4(fillDirection.x, fillDirection.y, fillDirection.z, 0f));
            Shader.SetGlobalVector("_ActorFillColor", new Vector4(
                fill.color.r * fill.intensity, fill.color.g * fill.intensity, fill.color.b * fill.intensity, 1f));

            Light rim = new GameObject("RimLight").AddComponent<Light>();
            rim.type = LightType.Directional;
            rim.intensity = 0.48f;
            rim.color = new Color(0.72f, 0.58f, 1f);
            rim.transform.rotation = Quaternion.Euler(18f, 205f, 0f);
            Vector3 rimDirection = -rim.transform.forward;
            Shader.SetGlobalVector("_ActorRimLightDirection", new Vector4(rimDirection.x, rimDirection.y, rimDirection.z, 0f));
            Shader.SetGlobalVector("_ActorRimLightColor", new Vector4(
                rim.color.r * rim.intensity, rim.color.g * rim.intensity, rim.color.b * rim.intensity, 1f));

            GameObject backdrop = GameObject.CreatePrimitive(PrimitiveType.Quad);
            backdrop.name = "StudioBackdrop";
            backdrop.transform.SetParent(_photoStudioRoot.transform, false);
            backdrop.transform.position = new Vector3(0f, 1.72f, -1.10f);
            backdrop.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            backdrop.transform.localScale = new Vector3(7.6f, 4.2f, 1f);
            Shader backdropShader = Resources.Load<Shader>("StudioBackdrop");
            Renderer backdropRenderer = backdrop.GetComponent<Renderer>();
            backdropRenderer.sharedMaterial = new Material(backdropShader);
            if (!RuntimeArguments.Contains("--legacy-bright-studio"))
            {
                // The captured final pass uses max(sharp, low-res blur). The original
                // Actor is surrounded by broad dark mountain/foliage regions, while
                // the former pale studio backdrop leaked a brighter t1 value back into
                // authored hair strokes even after Actor HDR itself matched. Keep the
                // lower-energy studio surround as production; retain the old palette
                // only for controlled presentation A/Bs.
                backdropRenderer.sharedMaterial.SetColor("_TopColor", new Color(0.22f, 0.34f, 0.50f, 1f));
                backdropRenderer.sharedMaterial.SetColor("_MiddleColor", new Color(0.28f, 0.30f, 0.40f, 1f));
                backdropRenderer.sharedMaterial.SetColor("_BottomColor", new Color(0.54f, 0.38f, 0.42f, 1f));
                backdropRenderer.sharedMaterial.SetColor("_GlowColor", new Color(0.24f, 0.50f, 0.62f, 1f));
            }
            // The captured GPA shadow atlas contains only the five actor caster
            // draws. Studio geometry is a receiver/background, never a caster.
            backdropRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            backdropRenderer.receiveShadows = false;

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "StudioFloor";
            floor.transform.SetParent(_photoStudioRoot.transform, false);
            floor.transform.position = new Vector3(0f, -0.005f, 0f);
            floor.transform.localScale = new Vector3(1.8f, 1f, 1.8f);
            Shader floorShader = Resources.Load<Shader>("StudioFloor");
            Material floorMaterial = new Material(floorShader);
            floorMaterial.SetColor("_Color", new Color(0.87f, 0.73f, 0.78f, 1f));
            floorMaterial.SetFloat("_Glossiness", 0.36f);
            Renderer floorRenderer = floor.GetComponent<Renderer>();
            floorRenderer.sharedMaterial = floorMaterial;
            floorRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            floorRenderer.receiveShadows = true;

            BuildStudioRibbon(new Vector3(-1.52f, 1.40f, -0.90f), new Vector3(0.055f, 2.55f, 0.04f), 18f, new Color(0.98f, 0.73f, 0.38f));
            BuildStudioRibbon(new Vector3(1.46f, 1.56f, -0.92f), new Vector3(0.045f, 2.85f, 0.04f), -14f, new Color(0.48f, 0.91f, 0.94f));
            BuildStudioRibbon(new Vector3(1.02f, 2.38f, -0.88f), new Vector3(1.20f, 0.045f, 0.04f), -8f, new Color(0.96f, 0.57f, 0.72f));

            // The current DMM riverbed bundles carry Tuanjie ArchiveStorage
            // protection in addition to the old Gakumas header XOR.  Keep the
            // production viewer on its known-good studio path unless the exact
            // environment is explicitly requested; otherwise Unity can load the
            // serialized hierarchy but fail every streamed mesh/texture block.
            if (RuntimeArguments.Contains("--original-riverbed"))
            {
                _riverbedEnvironment = new OriginalRiverbedEnvironment();
                if (_riverbedEnvironment.TryLoad())
                {
                    _riverbedEnvironment.SetActive(false);
                    // Original scl_photo_riverbed layout-001 camera, expressed in
                    // the identity-root actor basis used by this viewer.  Orbit,
                    // pan and zoom remain fully interactive.
                    _orbit.ConfigureDefaultPose(
                        new Vector3(0f, 0.88f, 0f),
                        1.566f,
                        180f,
                        21.15f,
                        37.84929f);
                }
                else
                {
                    _riverbedEnvironment.Dispose();
                    _riverbedEnvironment = null;
                }
            }

            ApplyRenderContextProfile(true);

            // Recovered render-scale contract: keep the player window at its
            // requested size while the Actor,
            // temporal target, 3/16 blur and bloom chain run from a 3840x2160
            // camera target.  A separate camera presents the bilinear 2x
            // downsample without stealing orbit-camera ownership.  Fidelity is
            // the production default; the opt-out is retained for performance
            // diagnosis on weaker hardware.
            if (!OriginalShaderUrpBootstrap.IsRequested &&
                !RuntimeArguments.Contains("--native-render-resolution"))
            {
                Camera presenterCamera = new GameObject("SupersamplePresenterCamera").AddComponent<Camera>();
                _supersamplePresenter = presenterCamera.gameObject.AddComponent<SupersamplePresenter>();
                _supersamplePresenter.Initialize(camera, 2);
            }

            _audioSource = new GameObject("VoicePlayer").AddComponent<AudioSource>();
            _audioSource.spatialBlend = 0f;
        }

        protected void BuildActorEnvironmentCube(bool useCapturedRiverbedProfile)
        {
            if (_actorEnvironmentCube != null) DestroyImmediate(_actorEnvironmentCube);
            if (_actorEyeEnvironmentCube != null && _actorEyeEnvironmentCube != _actorEnvironmentCube)
                DestroyImmediate(_actorEyeEnvironmentCube);
            if (_actorEyeEnvironmentArray != null) DestroyImmediate(_actorEyeEnvironmentArray);
            if (_actorEnvironmentArray != null) DestroyImmediate(_actorEnvironmentArray);
            _actorEnvironmentCube = null;
            _actorEyeEnvironmentCube = null;
            _actorEnvironmentArray = null;
            _actorEyeEnvironmentArray = null;
            // Captured cube payloads need their recovered direction adapters.
            // The generated Unity cube below is authored in ordinary world
            // faces, including when captured resources are unavailable.
            Shader.SetGlobalFloat("_UseCapturedEnvironmentBasis", 0f);
            Shader.SetGlobalFloat("_UseCapturedType1ActorEnvironmentArray", 0f);
            Shader.SetGlobalFloat("_UseCapturedActorEnvironmentArray", 0f);
            Shader.SetGlobalFloat("_UseCapturedEyeEnvironmentArray", 0f);
            Shader.SetGlobalTexture("_ActorEnvironmentArray", Texture2D.blackTexture);
            Shader.SetGlobalTexture("_ActorEyeEnvironmentArray", Texture2D.blackTexture);
            // The archived reflection cubes belong to the captured riverbed
            // lighting setup.  A custom studio must not silently reuse them:
            // that mixed context makes pale materials look self-illuminated
            // against the darker backdrop.  Preserve the captured profile as
            // the regression default and expose an explicit coherent studio
            // profile for presentation/non-matching environments.
            if (useCapturedRiverbedProfile &&
                !RuntimeArguments.Contains("--legacy-actor-env") &&
                TryBuildCapturedActorEnvironmentCube())
            {
                Shader.SetGlobalFloat("_UseCapturedEnvironmentBasis", 1f);
                if (!TryBuildCapturedEnvironmentCube(
                        "CapturedEyeEnvironment", "CapturedEyeEnvironmentCube", out _actorEyeEnvironmentCube))
                    _actorEyeEnvironmentCube = _actorEnvironmentCube;
                Shader.SetGlobalTexture("_ActorEnvironmentCube", _actorEnvironmentCube);
                Shader.SetGlobalTexture("_ActorEyeEnvironmentCube", _actorEyeEnvironmentCube);
                if (TryBuildCapturedEnvironmentArray(
                        "CapturedActorEnvironment", "CapturedActorEnvironmentArray",
                        out _actorEnvironmentArray))
                {
                    Shader.SetGlobalTexture("_ActorEnvironmentArray", _actorEnvironmentArray);
                    // Exact Actor CB1 basis + explicit D3D face mapping closes
                    // PS9700 pre-modulation specular to 0.9912x mean. Keep the
                    // older Unity Cubemap path available only as a diagnostic.
                    Shader.SetGlobalFloat("_UseCapturedType1ActorEnvironmentArray",
                        RuntimeArguments.Contains("--legacy-unity-type1-cube")
                            ? 0f : 1f);
                }
                if (TryBuildCapturedEnvironmentArray(
                        "CapturedEyeEnvironment", "CapturedEyeEnvironmentArray",
                        out _actorEyeEnvironmentArray))
                    Shader.SetGlobalTexture("_ActorEyeEnvironmentArray", _actorEyeEnvironmentArray);
                Shader.SetGlobalFloat("_ActorEnvironmentIntensity", ActorEnvironmentIntensity(1f));
                return;
            }

            const int size = 16;
            _actorEnvironmentCube = new Cubemap(size, TextureFormat.RGBAHalf, true)
            {
                name = "ActorEnvironmentCube",
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            SetCubeFace(_actorEnvironmentCube, CubemapFace.PositiveY, new Color(0.72f, 0.84f, 1.18f));
            SetCubeFace(_actorEnvironmentCube, CubemapFace.NegativeY, new Color(0.22f, 0.19f, 0.24f));
            SetCubeFace(_actorEnvironmentCube, CubemapFace.PositiveX, new Color(0.62f, 0.48f, 0.68f));
            SetCubeFace(_actorEnvironmentCube, CubemapFace.NegativeX, new Color(0.38f, 0.62f, 0.78f));
            SetCubeFace(_actorEnvironmentCube, CubemapFace.PositiveZ, new Color(0.86f, 0.68f, 0.48f));
            SetCubeFace(_actorEnvironmentCube, CubemapFace.NegativeZ, new Color(0.42f, 0.45f, 0.66f));
            _actorEnvironmentCube.Apply(true, true);
            _actorEyeEnvironmentCube = _actorEnvironmentCube;
            Shader.SetGlobalTexture("_ActorEnvironmentCube", _actorEnvironmentCube);
            Shader.SetGlobalTexture("_ActorEyeEnvironmentCube", _actorEyeEnvironmentCube);
            Shader.SetGlobalFloat("_ActorEnvironmentIntensity", ActorEnvironmentIntensity(0.72f));
        }

        protected void ApplyRenderContextProfile(bool force = false)
        {
            string[] commandLine = RuntimeArguments;
            bool fixedGpaRegression =
                commandLine.Contains("--capture-gpa-camera-and-quit") ||
                commandLine.Contains("--capture-gpa-camera-sweep-and-quit") ||
                commandLine.Contains("--capture-gpa-camera-mask-sweep-and-quit") ||
                commandLine.Contains("--capture-actor-cube-transform-sweep-and-quit");
            bool explicitCaptured =
                commandLine.Contains("--captured-riverbed-render-profile");
            bool forceStudio =
                commandLine.Contains("--studio-render-profile") ||
                commandLine.Contains("--legacy-actor-env");
            bool riverbedVisible = !StoryActive && !_useAdvPhotoBackground &&
                _useRiverbedBackground && _riverbedEnvironment != null &&
                _riverbedEnvironment.IsLoaded;

            OriginalStyleRenderPipeline.PresentationContext desired =
                StoryActive || _useAdvPhotoBackground
                    ? OriginalStyleRenderPipeline.PresentationContext.BakedAdv
                    : !forceStudio &&
                      (fixedGpaRegression || explicitCaptured || riverbedVisible)
                        ? OriginalStyleRenderPipeline.PresentationContext.CapturedRiverbed
                        : OriginalStyleRenderPipeline.PresentationContext.StudioLocal;
            if (!force && _actorRenderProfileInitialized &&
                desired == _renderContextProfile)
                return;

            _renderContextProfile = desired;
            _actorRenderProfileInitialized = true;
            OriginalStyleRenderPipeline.SetPresentationContext(desired);
            // Archived SH belongs to the captured lighting context, not every
            // scene that happens to share the actor. Other contexts consume
            // Unity's per-renderer ambient/light-probe coefficients.
            Shader.SetGlobalFloat("_UseCapturedAmbientSH",
                desired == OriginalStyleRenderPipeline.PresentationContext.CapturedRiverbed ? 1f : 0f);
            BuildActorEnvironmentCube(
                desired == OriginalStyleRenderPipeline.PresentationContext.CapturedRiverbed);
            Debug.Log(string.Format(
                "[PhotoMode] Render context profile: {0}; background={1}; actorEnvironment={2}",
                desired,
                StoryActive ? "story" : _useAdvPhotoBackground ? "adv-plate" :
                    riverbedVisible ? "riverbed" : "studio",
                desired == OriginalStyleRenderPipeline.PresentationContext.CapturedRiverbed
                    ? "captured-riverbed"
                    : "local-studio"));
        }

        protected float ActorEnvironmentIntensity(float productionValue)
        {
            string[] commandLine = RuntimeArguments;
            if (commandLine.Contains("--actor-env-zero")) return 0f;
            if (commandLine.Contains("--actor-env-half")) return productionValue * 0.5f;
            if (commandLine.Contains("--actor-env-double")) return productionValue * 2f;
            return productionValue;
        }

        protected bool TryBuildCapturedActorEnvironmentCube()
        {
            return TryBuildCapturedEnvironmentCube(
                "CapturedActorEnvironment", "CapturedActorEnvironmentCube", out _actorEnvironmentCube);
        }

        protected bool TryBuildCapturedEnvironmentCube(string resourceName, string cubeName, out Cubemap result)
        {
            // GPA replay of the active type-9 face draw exposed the exact t3
            // reflection probe: 64x64, seven mips, six faces in D3D cube order,
            // DXGI_FORMAT_R9G9B9E5_SHAREDEXP. The compact payload is ordered
            // face-major (+X,-X,+Y,-Y,+Z,-Z), then mip-major, without row padding.
            result = null;
            TextAsset payloadAsset = Resources.Load<TextAsset>(resourceName);
            if (payloadAsset == null || payloadAsset.bytes == null) return false;

            const int size = 64;
            const int mipLevels = 7;
            byte[] payload = payloadAsset.bytes;
            int expectedBytes = 0;
            for (int face = 0; face < 6; face++)
                for (int mip = 0; mip < mipLevels; mip++)
                {
                    int dimension = Mathf.Max(1, size >> mip);
                    expectedBytes += dimension * dimension * 4;
                }
            if (payload.Length != expectedBytes)
            {
                Debug.LogWarning(string.Format(
                    "[PhotoMode] Captured actor environment payload is {0} bytes; expected {1}.",
                    payload.Length, expectedBytes));
                return false;
            }

            // The GPA payload is already linear HDR radiance.  Use the explicit
            // linear-data overload; the three-argument Cubemap constructor marks
            // the texture as colour data and can insert a colour-space round trip
            // before the shader samples the uploaded half floats.
            Cubemap cube = new Cubemap(size, TextureFormat.RGBAHalf, true, true)
            {
                name = cubeName,
                // Captured Actor sampler s3 is D3D11 filter 20:
                // MIN_MAG_LINEAR_MIP_POINT. Unity's Bilinear mode preserves
                // bilinear face filtering while selecting one mip instead of
                // blending adjacent mips as the former Trilinear setting did.
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            int offset = 0;
            for (int face = 0; face < 6; face++)
            {
                for (int mip = 0; mip < mipLevels; mip++)
                {
                    int dimension = Mathf.Max(1, size >> mip);
                    Color[] pixels = new Color[dimension * dimension];
                    // D3D mapped rows begin at the top. Cubemap.SetPixels uses
                    // Unity's bottom-left row convention, so reverse rows while
                    // retaining the native D3D cube face order.
                    for (int y = 0; y < dimension; y++)
                    {
                        int sourceY = dimension - 1 - y;
                        for (int x = 0; x < dimension; x++)
                        {
                            int source = offset + (sourceY * dimension + x) * 4;
                            uint packed = (uint)(payload[source] |
                                (payload[source + 1] << 8) |
                                (payload[source + 2] << 16) |
                                (payload[source + 3] << 24));
                            int exponent = (int)((packed >> 27) & 0x1f);
                            float scale = Mathf.Pow(2f, exponent - 24);
                            pixels[y * dimension + x] = new Color(
                                (packed & 0x1ff) * scale,
                                ((packed >> 9) & 0x1ff) * scale,
                                ((packed >> 18) & 0x1ff) * scale,
                                1f);
                        }
                    }
                    cube.SetPixels(pixels, (CubemapFace)face, mip);
                    offset += dimension * dimension * 4;
                }
            }
            cube.Apply(false, true);
            result = cube;
            Debug.Log(string.Format(
                "[PhotoMode] Loaded exact GPA reflection cube {0} (64px, 7 mips, R9G9B9E5 source).",
                resourceName));
            return true;
        }

        protected bool TryBuildCapturedEnvironmentArray(
            string resourceName, string arrayName, out Texture2DArray result)
        {
            // Preserve the six captured D3D faces as independent 2D slices.
            // The shader performs D3D's direction-to-face mapping explicitly,
            // avoiding Unity cubemap face rotations while retaining hardware
            // bilinear/trilinear filtering within every face and mip.
            result = null;
            TextAsset payloadAsset = Resources.Load<TextAsset>(resourceName);
            if (payloadAsset == null || payloadAsset.bytes == null) return false;
            const int size = 64;
            const int mipLevels = 7;
            byte[] payload = payloadAsset.bytes;
            int expectedBytes = 0;
            for (int face = 0; face < 6; face++)
                for (int mip = 0; mip < mipLevels; mip++)
                {
                    int dimension = Mathf.Max(1, size >> mip);
                    expectedBytes += dimension * dimension * 4;
                }
            if (payload.Length != expectedBytes) return false;

            Texture2DArray array = new Texture2DArray(
                size, size, 6, TextureFormat.RGBAHalf, true, true)
            {
                name = arrayName,
                // D3D sampler s3 is MIN_MAG_LINEAR_MIP_POINT (filter 20).
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            int offset = 0;
            for (int face = 0; face < 6; face++)
            {
                for (int mip = 0; mip < mipLevels; mip++)
                {
                    int dimension = Mathf.Max(1, size >> mip);
                    Color[] pixels = new Color[dimension * dimension];
                    // D3D cube face coordinates use V=0 at the mapped top.
                    // Store mapped row zero at Unity array V=0 so the explicit
                    // sampler can use the captured coordinate without another
                    // implicit per-face rotation.
                    for (int y = 0; y < dimension; y++)
                        for (int x = 0; x < dimension; x++)
                        {
                            int source = offset + (y * dimension + x) * 4;
                            uint packed = (uint)(payload[source] |
                                (payload[source + 1] << 8) |
                                (payload[source + 2] << 16) |
                                (payload[source + 3] << 24));
                            int exponent = (int)((packed >> 27) & 0x1f);
                            float scale = Mathf.Pow(2f, exponent - 24);
                            pixels[y * dimension + x] = new Color(
                                (packed & 0x1ff) * scale,
                                ((packed >> 9) & 0x1ff) * scale,
                                ((packed >> 18) & 0x1ff) * scale,
                                1f);
                        }
                    array.SetPixels(pixels, face, mip);
                    offset += dimension * dimension * 4;
                }
            }
            array.Apply(false, true);
            result = array;
            Debug.Log("[PhotoMode] Loaded exact GPA eye reflection array with explicit D3D cube mapping.");
            return true;
        }

        protected static void SetCubeFace(Cubemap cube, CubemapFace face, Color color)
        {
            Color[] pixels = new Color[cube.width * cube.height];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = color;
            cube.SetPixels(pixels, face);
        }

        protected void BuildStudioRibbon(Vector3 position, Vector3 scale, float rotation, Color color)
        {
            GameObject ribbon = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ribbon.name = "StudioAccent";
            ribbon.transform.SetParent(_photoStudioRoot.transform, false);
            ribbon.transform.position = position;
            ribbon.transform.rotation = Quaternion.Euler(0f, 0f, rotation);
            ribbon.transform.localScale = scale;
            Material material = new Material(Resources.Load<Shader>("StudioAccent"));
            material.color = color;
            Renderer ribbonRenderer = ribbon.GetComponent<Renderer>();
            ribbonRenderer.sharedMaterial = material;
            ribbonRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ribbonRenderer.receiveShadows = false;
            ribbon.GetComponent<Collider>().enabled = false;
        }

        protected static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null) return;
            root.layer = layer;
            foreach (Transform child in root.transform) SetLayerRecursively(child.gameObject, layer);
        }

        protected void BuildCharacter()
        {
            _characterRoot = new GameObject(_characterId);
            BundleRecord faceRecord = FaceForCharacter(_characterId);
            _face = InstantiatePart(faceRecord.name, "Face");
            _faceHead = FindDescendant(_face.transform, "Head_Face");
            if (_faceHead != null)
            {
                _faceHeadPrefabParent = _faceHead.parent;
                _faceHeadPrefabLocalPosition = _faceHead.localPosition;
                _faceHeadPrefabLocalRotation = _faceHead.localRotation;
                _faceHeadPrefabLocalScale = _faceHead.localScale;
            }
            Mesh[] faceMeshes = _catalog.LoadAll<Mesh>(faceRecord.name);
            MeshRenderer faceRenderer = _face.GetComponentInChildren<MeshRenderer>(true);
            if (faceRenderer != null && faceMeshes.Length > 0)
            {
                MeshFilter filter = faceRenderer.GetComponent<MeshFilter>();
                if (filter == null) filter = faceRenderer.gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = faceMeshes[0];
            }
            _faceDriverRoot = new GameObject("Root_Face");
            _faceDriverRoot.transform.SetParent(_characterRoot.transform, false);
            _faceDriver = _faceDriverRoot.AddComponent<VLActorFaceModel>();
            _faceAnimator = EnsureAnimator(_characterRoot);
            VLActorFaceModel sourceModel = _face.GetComponent<VLActorFaceModel>();
            _faceExpression = _face.AddComponent<FaceExpressionRenderer>();
            _faceExpression.Initialize(sourceModel, _faceDriver);
            _faceExpression.InitializeGaze(
                FindDescendant(_face.transform, "LeftEye"),
                FindDescendant(_face.transform, "RightEye"),
                PreviewCamera);
            _faceDecals = new FaceDecalRuntime();
            _faceDecals.Initialize(_face, _catalog);
            _faceMaterialEffects = new ActorMaterialEffectRuntime(_face.GetComponentsInChildren<Renderer>(true), _catalog);
            SetLayerRecursively(_characterRoot, OriginalStyleRenderPipeline.ActorLayer);
        }

        public void SelectCharacter(int index)
        {
            if (_characterIds == null || _characterIds.Count == 0) return;
            int normalized = (index % _characterIds.Count + _characterIds.Count) % _characterIds.Count;
            string selected = _characterIds[normalized];
            if (string.Equals(selected, _characterId, StringComparison.OrdinalIgnoreCase)) return;

            int costumeIndex = _costumeIndex;
            if (_faceMaterialEffects != null) _faceMaterialEffects.Dispose();
            _faceMaterialEffects = null;
            if (_motionGraph.IsValid()) _motionGraph.Destroy();
            if (_characterRoot != null) DestroyImmediate(_characterRoot);
            _body = null;
            _face = null;
            _faceHead = null;
            _faceHeadPrefabParent = null;
            _hair = null;
            _faceDriverRoot = null;
            _bodyAnimator = null;
            _faceAnimator = null;
            _faceDriver = null;
            _faceExpression = null;
            if (_faceDecals != null) _faceDecals.Clear();
            _faceDecals = null;
            _hairDynamics = null;
            _garmentDynamics = null;
            _quartzArmDeformation = null;
            _quartzLegRotationDeformation = null;
            _quartzGarmentDeformation = null;
            _bodySoftTissueDynamics = null;
            _skirtDynamics = null;
            _capturedLookAt = null;
            _characterId = selected;
            BuildCharacter();
            SelectCostume(costumeIndex);
            ApplySelectedPhotoExpression();
            if (_capturedActorShadowMap != null) _capturedActorShadowMap.Initialize(_characterRoot);
            _status = "Character: " + _characterId.ToUpperInvariant() +
                " / outfit body: " + CurrentOutfitOwner.ToUpperInvariant();
        }

        protected BundleRecord FaceForCharacter(string characterId)
        {
            BundleRecord face = _faces.FirstOrDefault(value =>
                string.Equals(RecordOwnerId(value.name), characterId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(PartVariantId(value.name), "base-0000", StringComparison.OrdinalIgnoreCase));
            if (face != null) return face;
            face = _faces.FirstOrDefault(value =>
                string.Equals(RecordOwnerId(value.name), characterId, StringComparison.OrdinalIgnoreCase));
            if (face == null) throw new InvalidDataException("No face bundle for character: " + characterId);
            return face;
        }

        protected GameObject InstantiatePart(string bundleName, string displayName)
        {
            GameObject prefab = _catalog.LoadPrefab(bundleName);
            GameObject instance = Instantiate(prefab, _characterRoot.transform);
            instance.name = displayName + "__" + bundleName;
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            return instance;
        }

        protected static Animator EnsureAnimator(GameObject target)
        {
            Animator animator = target.GetComponent<Animator>();
            return animator != null ? animator : target.AddComponent<Animator>();
        }

        public void SelectCostume(int index)
        {
            if (_costumes.Count == 0)
            {
                return;
            }
            _costumeIndex = (index % _costumes.Count + _costumes.Count) % _costumes.Count;
            if (_body != null)
            {
                RestoreFaceHeadToPrefab();
                ResetPart(_face);
                if (_hair != null) DestroyImmediate(_hair);
                DestroyImmediate(_body);
            }
            BundleRecord selectedCostume = _costumes[_costumeIndex];
            string selectedOwner = RecordOwnerId(selectedCostume.name);
            string selectedVariant = PartVariantId(selectedCostume.name);
            bool crossCharacter = !string.Equals(
                selectedOwner, _characterId, StringComparison.OrdinalIgnoreCase);
            HashSet<Transform> activeOutfitBones = null;
            if (crossCharacter)
            {
                BundleRecord recipientRig = _costumes.FirstOrDefault(value =>
                    string.Equals(RecordOwnerId(value.name), _characterId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(PartVariantId(value.name), selectedVariant, StringComparison.OrdinalIgnoreCase));
                if (recipientRig == null)
                    throw new InvalidDataException(string.Format(
                        "No recipient rig for {0} / {1}", _characterId, selectedVariant));
                _body = InstantiatePart(recipientRig.name, "BodyRig");
                GameObject donorBody = InstantiatePart(selectedCostume.name, "DonorOutfit");
                CrossCharacterCostumeAssembler.Result retarget =
                    CrossCharacterCostumeAssembler.Assemble(
                        _body, donorBody, selectedCostume.name);
                activeOutfitBones = retarget.activeBones;
                _body.name = string.Format("BodyRig__{0}__wearing__{1}",
                    _characterId, selectedOwner);
            }
            else
            {
                _body = InstantiatePart(selectedCostume.name, "Body");
            }
            BundleRecord hairRecord = HairForCostume(selectedCostume);
            _hair = InstantiatePart(hairRecord.name, "Hair");
            _bodyAnimator = EnsureAnimator(_body);
            Transform bodyHead = FindDescendant(_body.transform, "Head");
            // Include the face eye bones in the runtime humanoid before the
            // Avatar is built. This lets AnimationHumanStream.SolveIK drive the
            // original face bones instead of a separate eye-only approximation.
            AttachFaceHeadToBody(bodyHead);
            Avatar avatar = BuildHumanoidAvatar(_body);
            if (avatar != null && avatar.isValid && avatar.isHuman)
            {
                _bodyAnimator.avatar = avatar;
                _bodyAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            _quartzArmDeformation =
                _body.AddComponent<QuartzArmDeformationSystem>();
            _quartzArmDeformation.Initialize(_bodyAnimator);
            _quartzLegRotationDeformation =
                _body.AddComponent<QuartzLegAndRotationDeformationSystem>();
            _quartzLegRotationDeformation.Initialize(_bodyAnimator);
            _quartzGarmentDeformation =
                _body.AddComponent<QuartzGarmentDeformationSystem>();
            _quartzGarmentDeformation.Initialize(_bodyAnimator);
            if (_faceExpression != null) _faceExpression.SetFaceCorrectionPoseTarget(bodyHead);
            ActorHeadLightingDriver headLighting = _characterRoot.GetComponent<ActorHeadLightingDriver>();
            if (headLighting == null) headLighting = _characterRoot.AddComponent<ActorHeadLightingDriver>();
            headLighting.Initialize(bodyHead);
            _capturedLookAt = _body.AddComponent<CapturedLookAtRuntime>();
            _capturedLookAt.Initialize(
                _bodyAnimator, _faceExpression, PreviewCamera,
                _body.transform, bodyHead);
            _breastDynamics = _body.AddComponent<BreastDynamicsSystem>();
            _breastDynamics.strength = 1.0f;
            _breastDynamics.correctionStrength = 1.0f;
            _breastDynamics.Initialize(_body.transform);
            // LegSkin / UpLegSkin are recipient anatomy helpers: coincident,
            // rotation-only deformation chains rather than geometric hair tips.
            // Keep them out of the garment solver and preserve the captured
            // native contract (four driven roots, small damped follow-through).
            _bodySoftTissueDynamics =
                _body.AddComponent<BodySoftTissueDynamicsSystem>();
            _bodySoftTissueDynamics.strength = 1.0f;
            // These helpers belong to recipient anatomy, not to the donor
            // outfit's active-bone mask.  Filtering by activeOutfitBones made
            // both cross-character cases silently lose all four Skin chains.
            _bodySoftTissueDynamics.Initialize(_body.transform, null);
            _garmentDynamics = _body.AddComponent<HairDynamicsSystem>();
            // ActorSwing's segment settings, including hard/reference limits,
            // belong to the dynamic child while the resulting rotation is
            // written into its parent. With that recovered ownership restored,
            // transplanted sleeve/jacket branches can use the same solver rather
            // than being frozen as a cross-character safety fallback.
            // ActorAnimationManageData's recovered constructor initializes
            // propertyData.swingPowerWeight to 1.0f (propertyData + 0x08), and
            // ProcessDynamicBones multiplies the integrated segment delta by
            // that value exactly once.  The former 0.25 presentation clamp was
            // an uncaptured safety guess; it weakened gravity and pose-following
            // together and left loose coats visibly suspended around the torso.
            _garmentDynamics.strength = 1.0f;
            _garmentDynamics.InitializeGarment(
                bodyHead, FindDescendant(_body.transform, "Spine2"),
                _body.transform, activeOutfitBones);
            if (crossCharacter)
                Debug.Log("[Wardrobe] Donor garment ActorSwing enabled on recipient rig");
            _skirtDynamics = _body.AddComponent<HairDynamicsSystem>();
            _skirtDynamics.strength = 1.0f;
            _skirtDynamics.InitializeSkirt(
                bodyHead, FindDescendant(_body.transform, "Spine2"),
                _body.transform, activeOutfitBones);
            // Initialize all body-owned solvers before parenting the independent
            // hair prefab below Head.  Same-character selection intentionally
            // has no activeOutfitBones mask; attaching hair first therefore made
            // the garment solver enumerate and simulate the hair ActorSwing
            // graph a second time.  The production rig aggregates the parts but
            // keeps their component ownership distinct.
            AlignAndParent(_hair, "Head_Hair", bodyHead);
            _hairDynamics = _hair.AddComponent<HairDynamicsSystem>();
            _hairDynamics.Initialize(bodyHead,
                FindDescendant(_body.transform, "Spine2"), _body.transform);
            BindNaturalWind();
            // Actor "others" (for example fktn's phone) are separate prefabs
            // owned by the Actor descriptor, but their motion paths are rooted
            // at the body Animator. Recreate them after every costume/body
            // rebuild so ADV clips continue to resolve Root_Smartphone/... .
            RebuildStoryActorParts();
            ErrorMaterialCount = MaterialRepairer.RepairErrorMaterials(_characterRoot);
            if (_actorRenderControls != null) _actorRenderControls.Initialize(_characterRoot, _keyLight);
            SetLayerRecursively(_characterRoot, OriginalStyleRenderPipeline.ActorLayer);
            if (_capturedActorShadowMap != null) _capturedActorShadowMap.Initialize(_characterRoot);
            RendererCount = _characterRoot.GetComponentsInChildren<Renderer>(true).Length;
            foreach (string rendererContract in DescribeRenderers())
                Debug.Log("[PhotoMode] Renderer contract: " + rendererContract);
            if (_motions != null && _motions.Count > 0)
            {
                SelectMotion(_motionIndex);
            }
            _status = "Costume: " + (_costumes[_costumeIndex].label ?? _costumes[_costumeIndex].name);
        }

        public void SelectSolo1Swap(string characterId, string outfitOwnerId)
        {
            int characterIndex = _characterIds.FindIndex(value =>
                string.Equals(value, characterId, StringComparison.OrdinalIgnoreCase));
            int costumeIndex = _costumes.FindIndex(value =>
                string.Equals(RecordOwnerId(value.name), outfitOwnerId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(PartVariantId(value.name), "cstm-0000", StringComparison.OrdinalIgnoreCase));
            if (characterIndex < 0 || costumeIndex < 0)
            {
                _status = "Solo1 swap unavailable: " + characterId + " <- " + outfitOwnerId;
                return;
            }

            SelectCharacter(characterIndex);
            SelectCostume(costumeIndex);
            _status = characterId.ToUpperInvariant() + " wearing " +
                outfitOwnerId.ToUpperInvariant() + " SOLO1";
        }

        protected BundleRecord HairForCostume(BundleRecord costume)
        {
            // Body asset ownership and actor identity are intentionally separate.
            // A cross-character outfit keeps the recipient's face/hair identity,
            // while selecting that recipient's authored hair variant for the same
            // costume family when it exists. This mirrors the descriptor's
            // independent body/face/hair resource slots.
            string costumeVariant = PartVariantId(costume.name);
            if (!string.IsNullOrEmpty(_requestedHairLabel))
            {
                BundleRecord requested = _hairs.FirstOrDefault(value =>
                    string.Equals(RecordOwnerId(value.name), _characterId, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(value.label, _requestedHairLabel, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(PartVariantId(value.name), _requestedHairLabel, StringComparison.OrdinalIgnoreCase)));
                if (requested != null) return requested;
                Debug.LogWarning(string.Format(
                    "[PhotoMode] Requested hair label was not found for {0}: {1}",
                    _characterId, _requestedHairLabel));
            }
            BundleRecord exact = _hairs.FirstOrDefault(value =>
                string.Equals(RecordOwnerId(value.name), _characterId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(PartVariantId(value.name), costumeVariant, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            BundleRecord baseHair = _hairs.FirstOrDefault(value =>
                string.Equals(RecordOwnerId(value.name), _characterId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(PartVariantId(value.name), "base-0000", StringComparison.OrdinalIgnoreCase));
            if (baseHair != null) return baseHair;
            throw new InvalidDataException("No hair bundle for character: " + _characterId);
        }

        protected static string RecordOwnerId(string bundleName)
        {
            const string prefix = "mdl_chr_";
            if (string.IsNullOrEmpty(bundleName) || !bundleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            int separator = bundleName.IndexOf('-', prefix.Length);
            return separator <= prefix.Length
                ? string.Empty
                : bundleName.Substring(prefix.Length, separator - prefix.Length);
        }

        protected static string PartVariantId(string bundleName)
        {
            string owner = RecordOwnerId(bundleName);
            if (string.IsNullOrEmpty(owner)) return string.Empty;
            int start = "mdl_chr_".Length + owner.Length + 1;
            int suffix = bundleName.LastIndexOf('_');
            return suffix <= start ? string.Empty : bundleName.Substring(start, suffix - start);
        }

        public void SelectMotion(int index)
        {
            if (_motions.Count == 0)
            {
                return;
            }
            _motionIndex = (index % _motions.Count + _motions.Count) % _motions.Count;
            if (_faceMotionLibrary != null) _faceMotionLibrary.Select(_motions[_motionIndex].name);
            if (_motionGraph.IsValid())
            {
                _motionGraph.Destroy();
            }

            MotionRuntimeMetadata metadata =
                _catalog.LoadMotionRuntimeMetadata(_motions[_motionIndex].name);
            AnimationClip[] clips = metadata.clips;
            ApplyMotionDynamicsContract(
                _motions[_motionIndex].name,
                metadata.enableSeatedDynamicCorrection);
            Debug.Log("[PhotoMode] Motion clips: " + string.Join(", ", clips.Select(value => value.name)));
            AnimationClip bodyClip = clips.FirstOrDefault(value => value.name.EndsWith("_b", StringComparison.OrdinalIgnoreCase));
            AnimationClip faceClip = clips.FirstOrDefault(value => value.name.EndsWith("_f", StringComparison.OrdinalIgnoreCase));
            _motionGraph = PlayableGraph.Create("fktn-photo-motion");
            _motionGraph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            if (_faceDriver != null) _faceDriver.ClearWeights();
            if (bodyClip != null)
            {
                _bodyPlayable = AnimationClipPlayable.Create(_motionGraph, bodyClip);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(_motionGraph, "body", _bodyAnimator);
                if (_capturedLookAt != null)
                    output.SetSourcePlayable(_capturedLookAt.CreatePlayable(_motionGraph, _bodyPlayable));
                else
                    output.SetSourcePlayable(_bodyPlayable);
            }
            if (faceClip != null)
            {
                _facePlayable = AnimationClipPlayable.Create(_motionGraph, faceClip);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(_motionGraph, "face", _faceAnimator);
                output.SetSourcePlayable(_facePlayable);
            }
            _motionGraph.Play();
            _paused = false;
            _status = "Motion: " + (_motions[_motionIndex].label ?? _motions[_motionIndex].name);
        }

        protected void ApplyMotionDynamicsContract(
            string motionName,
            bool enableSeatedDynamicCorrection)
        {
            if (_hairDynamics != null)
                _hairDynamics.SetMotionRuntime(enableSeatedDynamicCorrection);
            if (_garmentDynamics != null)
                _garmentDynamics.SetMotionRuntime(enableSeatedDynamicCorrection);
            if (_skirtDynamics != null)
                _skirtDynamics.SetMotionRuntime(enableSeatedDynamicCorrection);
            if (_bodySoftTissueDynamics != null)
                _bodySoftTissueDynamics.SetMotionRuntime(enableSeatedDynamicCorrection);
            Debug.Log(string.Format(
                "[ActorSwing] Active motion={0} seatedCorrection={1}",
                motionName, enableSeatedDynamicCorrection));
        }

        protected bool SelectMotionByLabel(string label)
        {
            if (_motions == null || _motions.Count == 0) return false;
            int index = _motions.FindIndex(value =>
                string.Equals(value.label, label, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            SelectMotion(index);
            return true;
        }

        public void EvaluateMotion(float seconds)
        {
            if (!_motionGraph.IsValid()) return;
            AnimationClip bodyClip = null;
            AnimationClip faceClip = null;
            if (_bodyPlayable.IsValid())
            {
                _bodyPlayable.SetTime(seconds);
                bodyClip = _bodyPlayable.GetAnimationClip();
            }
            if (_facePlayable.IsValid())
            {
                _facePlayable.SetTime(seconds);
                faceClip = _facePlayable.GetAnimationClip();
            }
            _motionGraph.Evaluate(0f);
            if (bodyClip != null) bodyClip.SampleAnimation(_body, seconds);
            if (faceClip != null) faceClip.SampleAnimation(_characterRoot, seconds);
            if (_faceMotionLibrary != null && _faceDriver != null) _faceMotionLibrary.Sample(seconds, _faceDriver);
            SamplePhotoMaterialEffects(seconds);
            if (_faceExpression != null) _faceExpression.ApplyCurrentWeights();
        }

        public void EnterStoryMode()
        {
            ResetStoryDepthOfField();
            ResetStoryActorRenderProfile();
            ResetStoryPostProcessProfile();
            if (_capturedActorShadowMap != null)
                _capturedActorShadowMap.ResetRuntimeProfile();
            int storyCostume = _costumes.FindIndex(value => string.Equals(value.label, "story-casl-0000", StringComparison.OrdinalIgnoreCase));
            if (storyCostume >= 0 && storyCostume != _costumeIndex) SelectCostume(storyCostume);
            // SelectCostume rebuilds the normal photo-mode graph.  Until the
            // first ADV body event, that graph would keep evaluating its _f
            // playable and repopulate Root_Face after the clear below.  Native
            // ADV owns actor animation from this point, so detach the photo
            // graph immediately instead of waiting for ConfigureStoryBodyGraph.
            if (_motionGraph.IsValid()) _motionGraph.Destroy();
            _bodyPlayable = default(AnimationClipPlayable);
            _facePlayable = default(AnimationClipPlayable);
            if (_faceExpression != null)
            {
                _faceExpression.SelectPreset(0);
                // ActorMotion evaluates its paired facial clip, including the
                // humanoid eye-muscle curves. Preserve those authored eye-bone
                // rotations until an explicit ADV LookTarget takes ownership.
                _faceExpression.SetStoryMotionGazeMode(true);
                _faceExpression.SetStoryBlink(-1f);
                _faceExpression.SetAutomaticBlinkEnabled(true);
            }
            if (_faceMotionLibrary != null) _faceMotionLibrary.SelectPhotoExpression(0);
            if (_capturedLookAt != null) _capturedLookAt.ClearStoryLookTarget();
            _expressionIndex = 0;
            // Clear the photo-mode preset before the ADV graph evaluates its
            // first paired ActorMotion facial clip.
            if (_faceDriver != null) _faceDriver.ClearWeights();
            if (_faceExpression != null) _faceExpression.ApplyCurrentWeights();
            if (_orbit != null)
            {
                _orbit.enabled = true;
                _orbit.target = new Vector3(0f, 1.05f, 0f);
                _orbit.ResetStoryOffsets();
            }
            if (_photoStudioRoot != null) _photoStudioRoot.SetActive(false);
            if (_riverbedEnvironment != null) _riverbedEnvironment.SetActive(false);
            SetupStoryBackground();
            EnsureStoryOverlay();
            if (_storyOverlayRuntime != null) _storyOverlayRuntime.SetEnabled(true);
            SetStoryRuntimeObjectsEnabled(true);
            ApplyStoryShake(Vector2.zero);
            ApplyRenderContextProfile();
            _status = "Story: adv_dear_fktn_001";
        }

        protected void ApplyPhotoBackgroundPreference()
        {
            if (_useAdvPhotoBackground)
            {
                if (_photoStudioRoot != null) _photoStudioRoot.SetActive(false);
                if (_riverbedEnvironment != null) _riverbedEnvironment.SetActive(false);
                SetupStoryBackground();
                if (_storyBackgroundRuntime != null) _storyBackgroundRuntime.SetEnabled(true);
            }
            else
            {
                if (_storyBackgroundRuntime != null) _storyBackgroundRuntime.SetEnabled(false);
                bool riverbed = _useRiverbedBackground &&
                    _riverbedEnvironment != null && _riverbedEnvironment.IsLoaded;
                if (_riverbedEnvironment != null) _riverbedEnvironment.SetActive(riverbed);
                if (_photoStudioRoot != null) _photoStudioRoot.SetActive(!riverbed);
            }
            ApplyRenderContextProfile();
        }

        protected void TogglePhotoBackground()
        {
            if (StoryActive) return;
            _useAdvPhotoBackground = false;
            _useRiverbedBackground = !_useRiverbedBackground &&
                _riverbedEnvironment != null && _riverbedEnvironment.IsLoaded;
            ApplyPhotoBackgroundPreference();
            if (_storyOverlayRuntime != null) _storyOverlayRuntime.SetEnabled(false);
            _status = _useRiverbedBackground
                ? "Background: original riverbed"
                : "Background: studio";
        }

        public void ExitStoryMode()
        {
            SetStoryRuntimeObjectsEnabled(false);
            ResetStoryDepthOfField();
            ResetStoryActorRenderProfile();
            ResetStoryPostProcessProfile();
            // ADV actor colour is a persistent material input, not a transient
            // overlay.  The opening command in adv_dear_fktn_001 writes
            // #FFFFFF00; rebuilding the body/hair on exit creates fresh white
            // materials, but the independently owned face materials survive
            // the costume swap and otherwise retain alpha zero.  That leaves a
            // literal face-shaped hole when an automated photo capture follows
            // story playback.  ActorManager restores the photo actor colour on
            // mode exit, so do the same for every surviving actor material.
            ApplyStoryActorColor(null, 1f);
            ApplyStoryShake(Vector2.zero);
            if (_capturedActorShadowMap != null)
                _capturedActorShadowMap.ResetRuntimeProfile();
            int capturedCostume = _costumes.FindIndex(value =>
                string.Equals(value.label, "cstm-0045", StringComparison.OrdinalIgnoreCase));
            if (capturedCostume >= 0 && capturedCostume != _costumeIndex)
            {
                SelectCostume(capturedCostume);
            }
            if (_orbit != null)
            {
                _orbit.ExitStoryPose();
                _orbit.enabled = true;
                _orbit.ResetPose();
            }
            ApplyPhotoBackgroundPreference();
            if (_faceExpression != null)
            {
                _faceExpression.SetStoryMotionGazeMode(false);
                _faceExpression.SetStoryBlink(-1f);
            }
            if (_capturedLookAt != null) _capturedLookAt.ClearStoryLookTarget();
            SelectMotion(_motionIndex);
            _status = "Photo controls";
        }

        public void EvaluateStoryBody(string currentMotion, float currentTime, string previousMotion, float previousTime, float blend)
        {
            if (string.IsNullOrEmpty(currentMotion)) return;
            bool pairChanged = !string.Equals(_storyCurrentMotion, currentMotion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_storyPreviousMotion, previousMotion, StringComparison.OrdinalIgnoreCase);
            if (pairChanged) ConfigureStoryBodyGraph(previousMotion, currentMotion);
            if (!_motionGraph.IsValid() || !_storyMixer.IsValid()) return;
            if (_storyPreviousPlayable.IsValid()) _storyPreviousPlayable.SetTime(previousTime);
            if (_storyCurrentPlayable.IsValid()) _storyCurrentPlayable.SetTime(currentTime);
            _storyMixer.SetInputWeight(0, _storyPreviousPlayable.IsValid() ? 1f - Mathf.Clamp01(blend) : 0f);
            _storyMixer.SetInputWeight(1, Mathf.Clamp01(blend));
            _motionGraph.Evaluate(0f);
        }

        protected void ConfigureStoryBodyGraph(string previousMotion, string currentMotion)
        {
            if (_motionGraph.IsValid()) _motionGraph.Destroy();
            _storyPreviousMotion = previousMotion;
            _storyCurrentMotion = currentMotion;
            MotionRuntimeMetadata currentMetadata =
                _catalog.LoadMotionRuntimeMetadata(currentMotion);
            ApplyStoryPropConstraintContract(currentMotion, currentMetadata.propConstraints);
            ApplyMotionDynamicsContract(
                currentMotion, currentMetadata.enableSeatedDynamicCorrection);
            AnimationClip currentClip = LoadStoryBodyClip(currentMotion);
            AnimationClip previousClip = string.IsNullOrEmpty(previousMotion) ? null : LoadStoryBodyClip(previousMotion);
            _motionGraph = PlayableGraph.Create("fktn-story-motion");
            _motionGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _storyMixer = AnimationMixerPlayable.Create(_motionGraph, 2);
            if (previousClip != null)
            {
                _storyPreviousPlayable = AnimationClipPlayable.Create(_motionGraph, previousClip);
                _motionGraph.Connect(_storyPreviousPlayable, 0, _storyMixer, 0);
            }
            else
            {
                _storyPreviousPlayable = default(AnimationClipPlayable);
            }
            _storyCurrentPlayable = AnimationClipPlayable.Create(_motionGraph, currentClip);
            _motionGraph.Connect(_storyCurrentPlayable, 0, _storyMixer, 1);
            AnimationPlayableOutput output = AnimationPlayableOutput.Create(_motionGraph, "story-body", _bodyAnimator);
            if (_capturedLookAt != null)
                output.SetSourcePlayable(_capturedLookAt.CreatePlayable(_motionGraph, _storyMixer));
            else
                output.SetSourcePlayable(_storyMixer);
            _motionGraph.Play();
            Debug.Log(string.Format("[Story] Body {0} <- {1}", currentMotion, previousMotion ?? "-") );
        }

        protected AnimationClip LoadStoryBodyClip(string motionName)
        {
            AnimationClip result;
            if (_storyBodyClips.TryGetValue(motionName, out result)) return result;
            AnimationClip[] clips = _catalog.LoadAnimationClips(motionName);
            result = clips.FirstOrDefault(value => value.name.EndsWith("_b", StringComparison.OrdinalIgnoreCase)) ??
                clips.FirstOrDefault();
            if (result == null) throw new InvalidDataException("Story body clip missing: " + motionName);
            _storyBodyClips[motionName] = result;
            return result;
        }

        public void EvaluateStoryFace(
            string motionName, float localTime,
            string previousMotionName, float previousLocalTime, float blend,
            StoryFaceOverrideEvent[] overrides, float storyTime)
        {
            if (_faceMotionLibrary == null || _faceDriver == null) return;
            if (string.IsNullOrEmpty(motionName))
            {
                _faceDriver.ClearWeights();
            }
            else
            {
                _faceMotionLibrary.Sample(
                    motionName, localTime, false, _storyCurrentFaceWeights);
                bool hasPrevious = !string.IsNullOrEmpty(previousMotionName) && blend < 0.999999f &&
                    _faceMotionLibrary.Sample(
                        previousMotionName, previousLocalTime, false,
                        _storyPreviousFaceWeights);
                _faceDriver.ClearWeights();
                float currentWeight = Mathf.Clamp01(blend);
                for (int index = 0; index < _storyCurrentFaceWeights.Length; index++)
                {
                    float value = hasPrevious
                        ? Mathf.LerpUnclamped(
                            _storyPreviousFaceWeights[index],
                            _storyCurrentFaceWeights[index], currentWeight)
                        : _storyCurrentFaceWeights[index];
                    if (Mathf.Abs(value) > 0.0000001f)
                        _faceDriver.SetWeight(index, value);
                }
            }
            if (overrides != null)
            {
                foreach (StoryFaceOverrideEvent value in overrides)
                {
                    float factor = value.EvaluateMixWeight(storyTime);
                    if (value.weights == null) continue;
                    foreach (StoryFaceWeight weight in value.weights)
                    {
                        if (weight.index < 0 || weight.index >= 192) continue;
                        _faceDriver.SetWeight(weight.index, Mathf.Max(_faceDriver.GetWeight(weight.index), weight.value * factor));
                    }
                }
            }
            if (_faceDecals != null) _faceDecals.ApplyStoryOverrides(overrides, storyTime);
            if (_faceMaterialEffects != null)
                _faceMaterialEffects.Sample(blend > 0f ? motionName : previousMotionName,
                    blend > 0f ? localTime : previousLocalTime, false);
            if (_faceExpression != null) _faceExpression.ApplyCurrentWeights();
        }

        public void ApplyStoryLookTarget(StoryLookTargetEvent value, float tween)
        {
            if (_capturedLookAt == null) return;
            if (value == null)
            {
                _capturedLookAt.ClearStoryLookTarget();
                return;
            }
            if (RuntimeArguments.Contains("--story-lookat-off"))
            {
                _capturedLookAt.ClearStoryLookTarget();
                return;
            }
            if (string.Equals(value.kind, "reset", StringComparison.OrdinalIgnoreCase))
            {
                _capturedLookAt.ClearStoryLookTarget();
                return;
            }

            StoryLookTargetSetting setting;
            if (string.Equals(value.kind, "tween", StringComparison.OrdinalIgnoreCase) && value.from != null && value.to != null)
            {
                float t = Mathf.Clamp01(tween);
                float fromWeight = StoryLookTargetWeight(value.from);
                float toWeight = StoryLookTargetWeight(value.to);
                Vector3 target;
                float eyes;
                float head;
                float body;
                if (!Mathf.Approximately(fromWeight, toWeight))
                {
                    // Recovered ActorLookTargetMixerPlayable contract: when
                    // global weights differ, position snaps to the endpoint
                    // with the larger weight while the global weight blends.
                    // Only eyes pass through Uguiss.Timeline.Easing.InCirc;
                    // head/body remain linear.
                    float weight = Mathf.Lerp(fromWeight, toWeight, t);
                    setting = fromWeight < toWeight ? value.to : value.from;
                    if (!TryResolveStoryLookTarget(setting, out target))
                    {
                        _capturedLookAt.ClearStoryLookTarget();
                        return;
                    }
                    eyes = StoryInCirc(weight) * setting.eyesWeight;
                    head = weight * setting.headWeight;
                    body = weight * setting.bodyWeight;
                }
                else
                {
                    Vector3 fromTarget;
                    Vector3 toTarget;
                    if (!TryResolveStoryLookTarget(value.from, out fromTarget) ||
                        !TryResolveStoryLookTarget(value.to, out toTarget))
                    {
                        _capturedLookAt.ClearStoryLookTarget();
                        return;
                    }
                    target = Vector3.Lerp(fromTarget, toTarget, t);
                    // The current GameAssembly's equal-weight branch forwards
                    // the `to` subweights literally (no extra global multiply).
                    eyes = value.to.eyesWeight;
                    head = value.to.headWeight;
                    body = value.to.bodyWeight;
                }
                _capturedLookAt.SetStoryLookTarget(target, eyes, head, body);
                return;
            }

            setting = value.setting ?? value.to ?? value.from;
            if (setting == null)
            {
                _capturedLookAt.ClearStoryLookTarget();
                return;
            }
            Vector3 position;
            if (!TryResolveStoryLookTarget(setting, out position))
            {
                _capturedLookAt.ClearStoryLookTarget();
                return;
            }
            float settingWeight = StoryLookTargetWeight(setting);
            _capturedLookAt.SetStoryLookTarget(
                position,
                settingWeight * setting.eyesWeight,
                settingWeight * setting.headWeight,
                settingWeight * setting.bodyWeight);
        }

        protected float StoryLookTargetWeight(StoryLookTargetSetting setting)
        {
            if (setting == null) return 0f;
            if (setting.type == 2 &&
                !string.IsNullOrEmpty(setting.actorId) &&
                !string.Equals(setting.actorId, _characterId, StringComparison.OrdinalIgnoreCase))
                return 0f;
            return setting.weight;
        }

        protected bool TryResolveStoryLookTarget(
            StoryLookTargetSetting setting,
            out Vector3 position)
        {
            position = Vector3.zero;
            if (setting == null) return false;
            switch (setting.type)
            {
                case 0: // Campus.ADV.LookTargetType.Direction
                    position = _capturedLookAt.ResolveStoryDirectionTarget(
                        setting.azimuth, setting.elevation);
                    return true;
                case 1: // Camera
                    if (PreviewCamera == null) return false;
                    position = PreviewCamera.transform.position;
                    return true;
                case 2: // Actor + HumanBodyBones
                    if (!string.IsNullOrEmpty(setting.actorId) &&
                        !string.Equals(setting.actorId, _characterId, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (_bodyAnimator == null || !_bodyAnimator.isHuman) return false;
                    Transform bone = _bodyAnimator.GetBoneTransform(
                        (HumanBodyBones)setting.bones);
                    if (bone == null) return false;
                    position = bone.position;
                    return true;
                case 3: // TransformData.position
                    if (setting.transform == null || setting.transform.position == null)
                        return false;
                    position = setting.transform.position.ToVector3();
                    return true;
                default:
                    return false;
            }
        }

        protected static float StoryInCirc(float value)
        {
            float t = Mathf.Clamp01(value);
            return 1f - Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
        }

        public void ApplyStoryBlink(float weight)
        {
            if (_faceExpression != null) _faceExpression.SetStoryBlink(weight);
        }

        public void ApplyStoryActorColor(StoryActorColorEvent value, float tween)
        {
            Color color = Color.white;
            if (value != null && (string.IsNullOrEmpty(value.id) ||
                string.Equals(value.id, _characterId, StringComparison.OrdinalIgnoreCase)))
            {
                if (string.Equals(value.kind, "tween", StringComparison.OrdinalIgnoreCase))
                {
                    Color from = ParseStoryColor(value.fromColor, Color.white);
                    Color to = ParseStoryColor(value.toColor, Color.white);
                    color = Color.LerpUnclamped(from, to, Mathf.Clamp01(tween));
                }
                else
                {
                    color = ParseStoryColor(value.color, Color.white);
                }
            }

            _storyActorColor = color;
            if (_characterRoot == null) return;
            foreach (Material material in _characterRoot
                .GetComponentsInChildren<Renderer>(true)
                .SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null && material.HasProperty("_ActorColor"))
                .Distinct())
            {
                // ActorManager.Actor.SetColor forwards the same RGBA value to
                // VLDefaultActorController.color and outlineColor.  The local
                // outline pass is evidence-disabled, so one recovered shader
                // input owns the visible colour and fade contract here.
                material.SetVector("_ActorColor", color);
            }
        }

        protected static Color ParseStoryColor(string value, Color fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            Color parsed;
            return ColorUtility.TryParseHtmlString(value, out parsed) ? parsed : fallback;
        }

        protected void EnsureStoryOverlay()
        {
            if (_storyOverlayRuntime == null && _catalog != null)
                _storyOverlayRuntime = new AdvStoryOverlayRuntime(_catalog);
        }

        public void ApplyStoryFade(
            string layer, StoryFadeEvent value, float tween)
        {
            EnsureStoryOverlay();
            if (_storyOverlayRuntime == null) return;
            Color color = value == null
                ? Color.black
                : ParseStoryColor(value.color, Color.black);
            float alpha = value == null
                ? 0f
                : Mathf.LerpUnclamped(value.from, value.to, Mathf.Clamp01(tween));
            _storyOverlayRuntime.SetFade(layer, color, alpha);
        }

        public void ApplyStoryForeground(StoryForegroundEvent value, float weight)
        {
            EnsureStoryOverlay();
            if (_storyOverlayRuntime != null)
                _storyOverlayRuntime.SetForeground(value, weight);
        }

        public void ApplyStoryShake(Vector2 position)
        {
            _storyShakePosition = position;
            if (_supersamplePresenter != null)
                _supersamplePresenter.SetAdvShakePosition(position);
            EnsureStoryOverlay();
            if (_storyOverlayRuntime != null)
                _storyOverlayRuntime.SetShakePosition(position);
        }

        public void ApplyStoryActorRenderProfile(StoryActorRenderProfile profile)
        {
            bool active = !_disableStoryActorProfile && StoryActive &&
                profile != null && profile.active;
            if (!active)
            {
                if (_storyActorRenderProfileActive || _storyActorRenderProfile != null)
                    ResetStoryActorRenderProfile();
                return;
            }

            string signature = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}", profile.sourcePathId, profile.profilePathId,
                profile.profileName ?? string.Empty);
            _storyActorRenderProfile = profile;
            _storyActorRenderProfileActive = true;
            UpdateStoryActorRenderProfileGlobals();
            if (signature == _storyActorRenderProfileSignature) return;
            _storyActorRenderProfileSignature = signature;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[Story] Actor render profile: sourcePathId={0}, profilePathId={1}, profile={2}, overrides={3}, lightAngle={4}, rimAngle={5}",
                profile.sourcePathId, profile.profilePathId,
                profile.profileName ?? string.Empty, profile.explicitOverrideCount,
                ProfileVector(profile.mainLightAngle, new Vector2(-5f, 10f)),
                ProfileVector(profile.rimAngle, new Vector2(10f, 5f))));
        }

        protected void ResetStoryActorRenderProfile()
        {
            _storyActorRenderProfile = null;
            _storyActorRenderProfileActive = false;
            _storyActorRenderProfileSignature = string.Empty;
            ResetCapturedActorRenderGlobals();
        }

        protected void ResetCapturedActorRenderGlobals()
        {
            Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0.3f, 1f, 1f, 0f));
            Shader.SetGlobalVector("_ActorLightingScales", new Vector4(0f, 1f, 1f, 0f));
            Shader.SetGlobalVector("_ActorKeyColor", Vector4.one);
            Shader.SetGlobalVector("_ActorRimColor", new Vector4(0.7f, 0.7f, 0.7f, 1f));
            Shader.SetGlobalVector("_ActorEyeHighlightColor", Vector4.one);
            Vector3 lightDirection = _useRootOnlyRimDirection
                ? new Vector3(0.78512269f, 0.42261863f, -0.45274259f).normalized
                : CapturedActorLightDirection;
            Shader.SetGlobalVector("_CapturedLightDirection",
                new Vector4(lightDirection.x, lightDirection.y, lightDirection.z, 0f));
            Shader.SetGlobalVector("_CapturedLightColor", Vector4.one);
            Shader.SetGlobalVector("_CapturedShadeTint", CapturedActorShadeTint);
            Shader.SetGlobalVector("_CapturedShadeAdditive", new Vector4(0f, 0f, 0f, 1f));
            Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
            Shader.SetGlobalVector("_CapturedReflectionColor", Vector4.one);
            Shader.SetGlobalVector("_CapturedEyeReflectionColor", Vector4.one);
            Shader.SetGlobalFloat("_UseExactViewRimBasis",
                _useLegacyRimDirection || _useRootOnlyRimDirection ? 0f : 1f);
            Shader.SetGlobalVector("_CapturedRimViewDirection",
                new Vector4(CapturedActorRimViewDirection.x,
                    CapturedActorRimViewDirection.y,
                    CapturedActorRimViewDirection.z, 0f));
            Vector3 rimDirection = _useRootOnlyRimDirection
                ? new Vector3(0.93651113f, -0.27563763f, 0.21672747f).normalized
                : _useLegacyRimDirection
                    ? CapturedActorRimViewDirection
                    : new Vector3(0.29546950f, -0.17236277f, 0.93967480f).normalized;
            Shader.SetGlobalVector("_CapturedRimDirection",
                new Vector4(rimDirection.x, rimDirection.y, rimDirection.z, 0f));
            Shader.SetGlobalVector("_CapturedRimParameters", new Vector4(0.7f, 0.85f, 32f, 1f));
        }

        protected void UpdateStoryActorRenderProfileGlobals()
        {
            StoryActorRenderProfile profile = _storyActorRenderProfile;
            if (!_storyActorRenderProfileActive || profile == null) return;

            Color lightColor = ProfileColor(profile.lightColor, Color.white);
            Color shadeMultiply = ProfileColor(profile.shadeColorMultiply, Color.white);
            Color shadeAdditive = ProfileColor(
                profile.shadeColorAdditive, new Color(0f, 0f, 0f, 1f));
            Color rimColor = ProfileColor(profile.rimColor, new Color(0.5f, 0.5f, 0.5f, 1f));
            Color reflectionColor = ProfileColor(profile.reflectionColor, Color.white);
            Color eyeReflectionColor = ProfileColor(profile.eyeReflectionColor, Color.white);
            Vector2 lightAngle = ProfileVector(
                profile.mainLightAngle, new Vector2(-5f, 10f));
            Vector2 rimAngle = ProfileVector(profile.rimAngle, new Vector2(10f, 5f));
            int lightSpace = ProfileInt(profile.mainLightSpace, 0);

            // CampusActorParameterPass.CalcLightVector recovered at
            // GameAssembly RVA 0x2AA4F50. Local angles are view-relative and
            // cancel camera roll; Global angles use the native 180-degree yaw
            // convention. Both branches rotate Vector3.forward.
            Quaternion lightRotation = lightSpace == 1
                ? Quaternion.Euler(-lightAngle.x, lightAngle.y + 180f, 0f)
                : Quaternion.AngleAxis(
                      PreviewCamera == null ? 0f : -PreviewCamera.transform.eulerAngles.z,
                      Vector3.forward) *
                  Quaternion.Euler(-lightAngle.y, lightAngle.x, 0f);
            Vector3 lightDirection = (lightRotation * Vector3.forward).normalized;
            // UpdateActorCommand constructs rim as Euler(rim.y, rim.x, 0)
            // and stores the rotated forward vector in the Actor constant buffer.
            Vector3 rimViewDirection =
                (Quaternion.Euler(rimAngle.y, rimAngle.x, 0f) * Vector3.forward).normalized;
            Vector3 rimWorldDirection = PreviewCamera == null
                ? rimViewDirection
                : PreviewCamera.transform.TransformDirection(rimViewDirection).normalized;

            Shader.SetGlobalVector("_CapturedLightDirection",
                new Vector4(lightDirection.x, lightDirection.y, lightDirection.z,
                    lightSpace == 1 ? 1f : 0f));
            Shader.SetGlobalVector("_CapturedLightColor", lightColor);
            Shader.SetGlobalVector("_CapturedShadeTint", shadeMultiply);
            Shader.SetGlobalVector("_CapturedShadeAdditive", shadeAdditive);
            Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(
                ProfileFloat(profile.matCapOffset, 0.3f),
                ProfileFloat(profile.matCapSmoothScale, 1f),
                ProfileFloat(profile.shadeApplyRatio, 1f), 0f));
            Shader.SetGlobalVector("_ActorLightingScales", new Vector4(
                ProfileFloat(profile.giScale, 0f), ProfileFloat(profile.additiveLightScale, 1f),
                ProfileFloat(profile.additiveLightSpecularScale, 1f), 0f));
            Shader.SetGlobalVector("_ActorRimColor", rimColor);
            Shader.SetGlobalVector("_ActorEyeHighlightColor", ProfileColor(profile.eyeHighlightColor, Color.white));
            Shader.SetGlobalFloat("_CapturedSkinSaturation",
                ProfileFloat(profile.skinSaturation, 0f));
            Shader.SetGlobalVector("_CapturedReflectionColor", reflectionColor);
            Shader.SetGlobalVector("_CapturedEyeReflectionColor", eyeReflectionColor);
            Shader.SetGlobalVector("_CapturedRimViewDirection",
                new Vector4(rimViewDirection.x, rimViewDirection.y, rimViewDirection.z, 0f));
            Shader.SetGlobalVector("_CapturedRimDirection",
                new Vector4(rimWorldDirection.x, rimWorldDirection.y, rimWorldDirection.z, 0f));
            float rimIntensity = (rimColor.r + rimColor.g + rimColor.b) / 3f;
            Shader.SetGlobalVector("_CapturedRimParameters", new Vector4(
                rimIntensity,
                ProfileFloat(profile.rimBaseColorRatio, 1f),
                Mathf.Max(0.0001f, ProfileFloat(profile.rimPower, 32f)),
                1f));
        }

        public string StoryActorRenderProfileDiagnosticJson()
        {
            StoryActorRenderProfile profile = _storyActorRenderProfile;
            if (!_storyActorRenderProfileActive || profile == null)
                return string.Format(
                    "{{\"active\":false,\"disabled\":{0}}}",
                    _disableStoryActorProfile ? "true" : "false");
            Vector2 lightAngle = ProfileVector(
                profile.mainLightAngle, new Vector2(-5f, 10f));
            Vector2 rimAngle = ProfileVector(profile.rimAngle, new Vector2(10f, 5f));
            Color light = ProfileColor(profile.lightColor, Color.white);
            Color shade = ProfileColor(profile.shadeColorMultiply, Color.white);
            Color rim = ProfileColor(profile.rimColor, Color.white);
            string saturation = (1f + ProfileFloat(profile.skinSaturation, 0f))
                .ToString("R", CultureInfo.InvariantCulture);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"active\":true,\"sourcePathId\":{0},\"profilePathId\":{1},\"profile\":\"{2}\",\"mainLightSpace\":{3},\"mainLightAngle\":[{4:R},{5:R}],\"rimAngle\":[{6:R},{7:R}],\"lightColor\":[{8:R},{9:R},{10:R}],\"shadeMultiply\":[{11:R},{12:R},{13:R}],\"rimColor\":[{14:R},{15:R},{16:R}],\"rimBase\":{17:R},\"rimPower\":{18:R},\"skinSaturation\":{19}}}",
                profile.sourcePathId, profile.profilePathId,
                (profile.profileName ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\""),
                ProfileInt(profile.mainLightSpace, 0),
                lightAngle.x, lightAngle.y, rimAngle.x, rimAngle.y,
                light.r, light.g, light.b, shade.r, shade.g, shade.b,
                rim.r, rim.g, rim.b,
                ProfileFloat(profile.rimBaseColorRatio, 1f),
                ProfileFloat(profile.rimPower, 32f), saturation);
        }

        protected static float ProfileFloat(StoryFloatParameter parameter, float fallback)
        {
            return parameter == null ? fallback : parameter.value;
        }

        protected static int ProfileInt(StoryIntParameter parameter, int fallback)
        {
            return parameter == null ? fallback : parameter.value;
        }

        protected static Vector2 ProfileVector(
            StoryVector2Parameter parameter, Vector2 fallback)
        {
            return parameter == null || parameter.value == null
                ? fallback : parameter.value.ToVector2();
        }

        protected static Color ProfileColor(
            StoryColorParameter parameter, Color fallback)
        {
            return parameter == null || parameter.value == null
                ? fallback : parameter.value.ToColor();
        }

        public void ApplyStoryActorLighting(StoryActorLightingEvent value, float mixWeight)
        {
            if (_capturedActorShadowMap == null) return;
            if (value == null)
            {
                _capturedActorShadowMap.ResetRuntimeProfile();
                return;
            }

            float weight = Mathf.Clamp01(mixWeight);
            int directionalType = value.directionalType != null &&
                value.directionalType.overrideState && weight > 0f
                    ? value.directionalType.value
                    : 0;
            Vector2 lightDirectional = value.lightDirectional != null &&
                value.lightDirectional.overrideState && value.lightDirectional.value != null
                    ? Vector2.Lerp(Vector2.zero, value.lightDirectional.value.ToVector2(), weight)
                    : Vector2.zero;
            Vector2 mainLightAngle = value.mainLightAngle != null &&
                value.mainLightAngle.overrideState && value.mainLightAngle.value != null
                    ? Vector2.Lerp(Vector2.zero, value.mainLightAngle.value.ToVector2(), weight)
                    : Vector2.zero;
            int mainLightSpace = value.mainLightSpace != null &&
                value.mainLightSpace.overrideState && weight > 0f
                    ? value.mainLightSpace.value
                    : 0;
            float shadowStrength = value.shadowStrength != null &&
                value.shadowStrength.overrideState
                    ? Mathf.Lerp(1f, value.shadowStrength.value, weight)
                    : 1f;
            float useOffset = value.useOffset != null && value.useOffset.overrideState
                ? Mathf.Lerp(0f, value.useOffset.value, weight)
                : 0f;
            float facePartsStrength = value.facePartsShadowStrength != null &&
                value.facePartsShadowStrength.overrideState
                    ? Mathf.Lerp(0f, value.facePartsShadowStrength.value, weight)
                    : 0f;
            _capturedActorShadowMap.ApplyRuntimeProfile(
                directionalType,
                lightDirectional,
                mainLightAngle,
                mainLightSpace,
                shadowStrength,
                useOffset,
                facePartsStrength,
                string.Format("ADV actorlighting order={0} time={1:F3}", value.order, value.time));
        }

        public void ApplyStoryCamera(StoryCameraEvent value, float tween)
        {
            if (PreviewCamera == null || value == null) return;
            StoryCameraSetting setting;
            if (value.kind == "tween" && value.from != null && value.to != null)
            {
                ApplyCameraSetting(value.from, value.to, tween);
                return;
            }
            setting = value.setting ?? value.to ?? value.from;
            if (setting != null) ApplyCameraSetting(setting, setting, 1f);
        }

        protected void ApplyCameraSetting(StoryCameraSetting from, StoryCameraSetting to, float t)
        {
            if (from == null || to == null || from.transform == null || to.transform == null) return;
            Vector3 position = Vector3.Lerp(from.transform.position.ToVector3(), to.transform.position.ToVector3(), t);
            Quaternion rotation = Quaternion.Slerp(
                Quaternion.Euler(from.transform.rotation.ToVector3()),
                Quaternion.Euler(to.transform.rotation.ToVector3()), t);
            PreviewCamera.transform.position = position;
            PreviewCamera.transform.rotation = rotation;
            float focalLength = Mathf.Lerp(
                Mathf.Max(15f, from.focalLength),
                Mathf.Max(15f, to.focalLength), t);
            // Recovered CameraManager.SetCameraSetting writes focalLength but
            // never enables Unity's physical-camera gate.  Keeping that gate
            // on made vertical ADV framing aspect-dependent: the authored 30 mm
            // shot expanded to roughly 95 degrees vertically at 9:16.  A paired
            // 607x1080 replay against the source portrait instead matches the
            // ordinary 24 mm sensor-height conversion.  Preserve the former
            // interpretation only as an explicit diagnostic control.
            bool physicalProbe = RuntimeArguments.Contains(
                "--story-camera-physical");
            PreviewCamera.usePhysicalProperties = physicalProbe;
            PreviewCamera.focalLength = focalLength;
            if (!physicalProbe)
            {
                PreviewCamera.fieldOfView = Camera.FocalLengthToFieldOfView(
                    focalLength, PreviewCamera.sensorSize.y);
            }
            PreviewCamera.nearClipPlane = Mathf.Lerp(
                Mathf.Max(0.001f, from.nearClipPlane), Mathf.Max(0.001f, to.nearClipPlane), t);
            PreviewCamera.farClipPlane = Mathf.Lerp(
                Mathf.Max(PreviewCamera.nearClipPlane + 0.01f, from.farClipPlane),
                Mathf.Max(PreviewCamera.nearClipPlane + 0.01f, to.farClipPlane), t);
            StoryCameraDofSetting dofFrom = from.dofSetting;
            StoryCameraDofSetting dofTo = to.dofSetting;
            if (dofFrom != null && dofTo != null)
            {
                // CameraSettingMixer selects the destination boolean as soon as
                // a tween leaves t=0, while its three scalar DOF members lerp.
                StoryCameraDofSetting activeDof = t <= 0f ? dofFrom : dofTo;
                _storyCameraDofActive = activeDof.active;
                _storyCameraDofFocalPoint = Mathf.Lerp(dofFrom.focalPoint, dofTo.focalPoint, t);
                _storyCameraDofFNumber = Mathf.Lerp(dofFrom.fNumber, dofTo.fNumber, t);
                // CameraManager.SetCameraDOFSetting applies this exact 0.5
                // conversion before VLDOF receives maxBlurSpread.
                _storyCameraDofMaxBlurSpread =
                    Mathf.Lerp(dofFrom.maxBlurSpread, dofTo.maxBlurSpread, t) * 0.5f;
            }
            else
            {
                _storyCameraDofActive = false;
            }
            UpdateStoryDepthOfField();
            if (_orbit != null && StoryActive)
                _orbit.SetStoryPose(position, rotation, PreviewCamera.fieldOfView);
            UpdateStoryBackgroundScale();
        }

        public void ApplyStoryDepthOfField(StoryDepthOfFieldEvent value)
        {
            _storyDepthOfFieldOverride = value;
            UpdateStoryDepthOfField();
        }

        public void ApplyStoryParaffin(
            StoryParaffinProfile backgroundProfile,
            StoryParaffinEvent command,
            float mixWeight)
        {
            OriginalStyleRenderPipeline pipeline = PreviewCamera == null
                ? null
                : PreviewCamera.GetComponent<OriginalStyleRenderPipeline>();
            if (pipeline == null) return;
            pipeline.SetParaffin(backgroundProfile, command, mixWeight);
        }

        public void ApplyStoryPostProcessProfile(StoryPostProcessProfile profile)
        {
            OriginalStyleRenderPipeline pipeline = PreviewCamera == null
                ? null
                : PreviewCamera.GetComponent<OriginalStyleRenderPipeline>();
            if (pipeline == null) return;
            pipeline.SetStoryPostProcessProfile(profile);
        }

        protected void ResetStoryPostProcessProfile()
        {
            OriginalStyleRenderPipeline pipeline = PreviewCamera == null
                ? null
                : PreviewCamera.GetComponent<OriginalStyleRenderPipeline>();
            if (pipeline != null) pipeline.ResetStoryPostProcessProfile();
        }

        protected void ResetStoryDepthOfField()
        {
            _storyCameraDofActive = false;
            _storyCameraDofFocalPoint = 4f;
            _storyCameraDofFNumber = 4f;
            _storyCameraDofMaxBlurSpread = 1.5f;
            _storyDepthOfFieldOverride = null;
            OriginalStyleRenderPipeline pipeline = PreviewCamera == null
                ? null : PreviewCamera.GetComponent<OriginalStyleRenderPipeline>();
            if (pipeline != null)
                pipeline.SetDepthOfField(false, 3, 4f, 4f, 1.5f, 0f, true, 0.5f, 1f, 5, 1f, 0f);
        }

        protected void UpdateStoryDepthOfField()
        {
            OriginalStyleRenderPipeline pipeline = PreviewCamera == null
                ? null : PreviewCamera.GetComponent<OriginalStyleRenderPipeline>();
            if (pipeline == null) return;

            bool active = _storyCameraDofActive;
            int quality = 3;
            float focalPoint = _storyCameraDofFocalPoint;
            float fNumber = _storyCameraDofFNumber;
            float maxBlurSpread = _storyCameraDofMaxBlurSpread;
            float foregroundBlurExtrude = 0f;
            bool useFNumber = true;
            float smoothness = 0.5f;
            float focalSize = 1f;
            int bladeCount = 5;
            float bladeCurvature = 1f;
            float bladeRotation = 0f;

            StoryDepthOfFieldEvent value = _storyDepthOfFieldOverride;
            if (value != null)
            {
                active = true;
                if (value.quality != null && value.quality.overrideState) quality = value.quality.value;
                if (value.focalPoint != null && value.focalPoint.overrideState) focalPoint = value.focalPoint.value;
                if (value.fNumber != null && value.fNumber.overrideState) fNumber = value.fNumber.value;
                if (value.maxBlurSpread != null && value.maxBlurSpread.overrideState) maxBlurSpread = value.maxBlurSpread.value;
                if (value.foregroundBlurExtrude != null && value.foregroundBlurExtrude.overrideState)
                    foregroundBlurExtrude = value.foregroundBlurExtrude.value;
                if (value.useFNumber != null && value.useFNumber.overrideState) useFNumber = value.useFNumber.value;
                if (value.smoothness != null && value.smoothness.overrideState) smoothness = value.smoothness.value;
                if (value.focalSize != null && value.focalSize.overrideState) focalSize = value.focalSize.value;
                if (value.bladeCount != null && value.bladeCount.overrideState) bladeCount = value.bladeCount.value;
                if (value.bladeCurvature != null && value.bladeCurvature.overrideState)
                    bladeCurvature = value.bladeCurvature.value;
                if (value.bladeRotation != null && value.bladeRotation.overrideState)
                    bladeRotation = value.bladeRotation.value;
            }
            pipeline.SetDepthOfField(
                active, quality, focalPoint, fNumber, maxBlurSpread,
                foregroundBlurExtrude, useFNumber, smoothness, focalSize,
                bladeCount, bladeCurvature, bladeRotation);
        }

        protected void SetupStoryBackground()
        {
            if (_storyBackgroundRuntime == null && _catalog != null && PreviewCamera != null)
                _storyBackgroundRuntime = new AdvStoryBackgroundRuntime(_catalog, PreviewCamera);
            if (_storyBackgroundRuntime == null) return;
            string fallback = _storyBackgroundRuntime.FirstTwoDimensionalId();
            if (string.IsNullOrEmpty(_storyBackgroundRuntime.ActiveId) && !string.IsNullOrEmpty(fallback))
                _storyBackgroundRuntime.SetLayout(fallback);
            _storyBackgroundRuntime.SetEnabled(true);
            UpdateStoryBackgroundScale();
        }

        protected void UpdateStoryBackgroundScale()
        {
            if (_storyBackgroundRuntime != null) _storyBackgroundRuntime.UpdateCameraGeometry();
        }

        public void ConfigureStoryBackgrounds(StoryBackgroundDeclaration[] declarations)
        {
            if (_storyBackgroundRuntime == null && _catalog != null && PreviewCamera != null)
                _storyBackgroundRuntime = new AdvStoryBackgroundRuntime(_catalog, PreviewCamera);
            if (_storyBackgroundRuntime != null) _storyBackgroundRuntime.Configure(declarations);
        }

        public void ConfigureStoryActorParts(StoryActorDeclaration[] declarations)
        {
            _storyActorDeclarations = declarations ?? new StoryActorDeclaration[0];
            RebuildStoryActorParts();
        }

        protected void RebuildStoryActorParts()
        {
            foreach (GameObject value in _storyActorExtras.Values)
            {
                if (value != null) DestroyImmediate(value);
            }
            _storyActorExtras.Clear();
            if (_catalog == null || _body == null || _storyActorDeclarations == null) return;

            StoryActorDeclaration actor = _storyActorDeclarations.FirstOrDefault(value =>
                value != null && string.Equals(
                    value.id, _characterId, StringComparison.OrdinalIgnoreCase));
            if (actor == null || actor.others == null) return;
            foreach (string bundleName in actor.others)
            {
                if (string.IsNullOrEmpty(bundleName)) continue;
                try
                {
                    GameObject prefab = _catalog.LoadPrefab(bundleName);
                    GameObject instance = Instantiate(prefab, _body.transform);
                    instance.name = StoryActorPartRootName(bundleName);
                    instance.transform.localPosition = Vector3.zero;
                    instance.transform.localRotation = Quaternion.identity;
                    instance.transform.localScale = Vector3.one;
                    SetLayerRecursively(instance, OriginalStyleRenderPipeline.ActorLayer);
                    MaterialRepairer.RepairErrorMaterials(instance);
                    instance.SetActive(StoryActive);
                    _storyActorExtras[bundleName] = instance;
                    Debug.Log(string.Format(
                        "[StoryProp] Actor extra {0} instantiated as {1}: renderers={2}",
                        bundleName, instance.name,
                        instance.GetComponentsInChildren<Renderer>(true).Length));
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[StoryProp] Actor extra load failed " +
                        bundleName + ": " + exception.Message);
                }
            }
            ApplyStoryActorRenderer(_storyActorRendererState);
            if (_actorRenderControls != null) _actorRenderControls.RefreshRenderers();
            if (_capturedActorShadowMap != null)
                _capturedActorShadowMap.Initialize(_characterRoot);
        }

        protected static string StoryActorPartRootName(string bundleName)
        {
            if (bundleName.IndexOf("smartphone", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Root_Smartphone";
            string token = bundleName;
            const string prefix = "mdl_prp_";
            if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                token = token.Substring(prefix.Length);
            int separator = token.IndexOf('-');
            if (separator > 0) token = token.Substring(0, separator);
            return "Root_" + (token.Length == 0
                ? "Prop"
                : char.ToUpperInvariant(token[0]) + token.Substring(1));
        }

        public void ApplyStoryActorRenderer(StoryActorRendererEvent value)
        {
            _storyActorRendererState = value;
            HashSet<string> inactive = new HashSet<string>(
                value == null || value.inactiveAssets == null
                    ? new string[0]
                    : value.inactiveAssets,
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, GameObject> pair in _storyActorExtras)
            {
                if (pair.Value == null) continue;
                bool enabled = !inactive.Contains(pair.Key);
                foreach (Renderer renderer in pair.Value.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = enabled;
            }
        }

        protected void ApplyStoryPropConstraintContract(
            string motionName, VL.PropConstraintData[] contracts)
        {
            foreach (GameObject root in _storyActorExtras.Values)
            {
                if (root == null) continue;
                foreach (ActorAnimationConstraintTransformBone constraint in
                    root.GetComponentsInChildren<ActorAnimationConstraintTransformBone>(true))
                {
                    constraint.runtimeEvaluationEnabled = false;
                    constraint.ClearSource();
                }
                foreach (IConstraint constraint in root.GetComponentsInChildren<Component>(true)
                    .OfType<IConstraint>())
                {
                    constraint.constraintActive = false;
                    while (constraint.sourceCount > 0) constraint.RemoveSource(0);
                }
            }
            if (contracts == null || contracts.Length == 0) return;

            foreach (VL.PropConstraintData contract in contracts)
            {
                if (contract == null || string.IsNullOrEmpty(contract.prop)) continue;
                Transform prop = _storyActorExtras.Values
                    .Where(value => value != null)
                    .Select(value => FindDescendant(value.transform, contract.prop))
                    .FirstOrDefault(value => value != null);
                if (prop == null)
                {
                    Debug.LogWarning(string.Format(
                        "[StoryProp] Motion {0} prop target missing: {1}",
                        motionName, contract.prop));
                    continue;
                }
                if (contract.isFieldConstraint)
                {
                    Debug.LogWarning(string.Format(
                        "[StoryProp] Motion {0} requests unsupported field constraint for {1}",
                        motionName, contract.prop));
                    continue;
                }

                ActorAnimationConstraintTransformBone constraint =
                    prop.GetComponent<ActorAnimationConstraintTransformBone>();
                if (constraint == null)
                    constraint = prop.gameObject.AddComponent<ActorAnimationConstraintTransformBone>();
                constraint.ClearSource();
                constraint.executeType = (ActorAnimationConstraintExecuteType)contract.executeType;
                constraint.offsetPosition = Vector3.zero;
                constraint.offsetRotation = Quaternion.identity;
                const float originalHeightCoefficient = 0.8981165f;
                float humanScale = _bodyAnimator == null ? 1f : _bodyAnimator.humanScale;
                float adaptScale = contract.isAdaptScale
                    ? humanScale / originalHeightCoefficient
                    : 1f;
                constraint.offsetScale = Vector3.one * adaptScale;

                int sourceCount = 0;
                foreach (string targetName in contract.constraintTargets ?? new string[0])
                {
                    Transform target = FindDescendant(_body.transform, targetName);
                    if (target == null)
                    {
                        Debug.LogWarning(string.Format(
                            "[StoryProp] Motion {0} source bone missing: {1}",
                            motionName, targetName));
                        continue;
                    }
                    constraint.AddSource(target, Vector3.zero);
                    sourceCount++;
                }
                bool active = sourceCount > 0;
                if (active) constraint.constraintWeight0 = Vector3.one;
                constraint.runtimeEvaluationEnabled = active;
                Debug.Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "[StoryProp] Motion {0}: prop={1} sources={2} adaptScale={3} executeType={4} humanScale={5:R} offsetScale={6:R}",
                    motionName, contract.prop, sourceCount,
                    contract.isAdaptScale, contract.executeType,
                    humanScale, adaptScale));
            }
        }

        public void ConfigureStoryProps(StoryPropDeclaration[] declarations)
        {
            _storyPropDeclarations = declarations ?? new StoryPropDeclaration[0];
            if (_storyPropRoot != null) DestroyImmediate(_storyPropRoot);
            _storyProps.Clear();
            _storyPropRoot = new GameObject("StoryProps");
            foreach (StoryPropDeclaration declaration in _storyPropDeclarations)
            {
                if (declaration == null || string.IsNullOrEmpty(declaration.id) ||
                    string.IsNullOrEmpty(declaration.bundle)) continue;
                try
                {
                    GameObject prefab = _catalog.LoadPrefab(
                        declaration.bundle, declaration.objectName);
                    GameObject instance = Instantiate(prefab, _storyPropRoot.transform);
                    instance.name = declaration.id + "__" + declaration.objectName;
                    instance.transform.localPosition = Vector3.zero;
                    instance.transform.localRotation = Quaternion.identity;
                    instance.transform.localScale = Vector3.one;
                    MaterialRepairer.RepairErrorMaterials(instance);
                    SetLayerRecursively(instance, 0);
                    _storyProps[declaration.id] = instance;
                    Debug.Log(string.Format(
                        "[StoryProp] Prop {0}: bundle={1} object={2} renderers={3}",
                        declaration.id, declaration.bundle, declaration.objectName,
                        instance.GetComponentsInChildren<Renderer>(true).Length));
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[StoryProp] Prop load failed " +
                        declaration.id + ": " + exception.Message);
                }
            }
            _storyPropRoot.SetActive(StoryActive);
        }

        public void ApplyStoryPropLayout(string id, StoryPropLayoutEvent value)
        {
            GameObject instance;
            if (string.IsNullOrEmpty(id) || !_storyProps.TryGetValue(id, out instance) ||
                instance == null) return;
            // PropManager does not expose a declared prop to the scene until a
            // layout command owns it.  The focus script declares its black-room
            // prop at startup but first places it at 86.338 s; rendering the
            // prefab's identity pose before that command blacks out the entire
            // opening and contradicts both the command sequence and native
            // playback.
            if (value == null)
            {
                instance.SetActive(false);
                return;
            }
            instance.SetActive(true);
            StoryTransform transform = value.transform;
            instance.transform.localPosition = transform == null || transform.position == null
                ? Vector3.zero : transform.position.ToVector3();
            instance.transform.localRotation = transform == null || transform.rotation == null
                ? Quaternion.identity : Quaternion.Euler(transform.rotation.ToVector3());
            instance.transform.localScale = transform == null || transform.scale == null
                ? Vector3.one : transform.scale.ToVector3();
        }

        protected void SetStoryRuntimeObjectsEnabled(bool enabled)
        {
            foreach (GameObject value in _storyActorExtras.Values)
            {
                if (value != null) value.SetActive(enabled);
            }
            if (_storyPropRoot != null) _storyPropRoot.SetActive(enabled);
        }

        public string StoryPropDiagnosticJson()
        {
            string actor = string.Join(",", _storyActorExtras.Select(pair =>
            {
                GameObject value = pair.Value;
                bool visible = value != null && value.GetComponentsInChildren<Renderer>(true)
                    .Any(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy);
                Transform prop = value == null ? null : FindDescendant(value.transform, "Prop_Smartphone");
                return string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\":{{\"visible\":{1},\"position\":\"{2}\",\"rotation\":\"{3}\"}}",
                    pair.Key, visible ? "true" : "false",
                    prop == null ? "" : prop.position.ToString("R"),
                    prop == null ? "" : prop.rotation.eulerAngles.ToString("R"));
            }).ToArray());
            string props = string.Join(",", _storyProps.Select(pair => string.Format(
                CultureInfo.InvariantCulture,
                "\"{0}\":{{\"position\":\"{1}\",\"scale\":\"{2}\"}}",
                pair.Key,
                pair.Value == null ? "" : pair.Value.transform.position.ToString("R"),
                pair.Value == null ? "" : pair.Value.transform.localScale.ToString("R"))).ToArray());
            return "{\"actorExtras\":{" + actor + "},\"props\":{" + props + "}}";
        }

        public void ApplyStoryBackgroundLayout(string id)
        {
            if (_storyBackgroundRuntime == null) SetupStoryBackground();
            if (_storyBackgroundRuntime == null) return;
            _storyBackgroundRuntime.SetEnabled(true);
            _storyBackgroundRuntime.SetLayout(id);
        }

        public void ApplyStoryBackgroundTransform(StoryBackgroundTransformEvent value, float tween)
        {
            if (_storyBackgroundRuntime != null)
                _storyBackgroundRuntime.ApplyTransform(value, tween);
        }

        public void ApplyStoryLayout(StoryLayoutEvent value, float tween)
        {
            if (_characterRoot == null || value == null) return;
            StoryTransform from;
            StoryTransform to;
            if (string.Equals(value.kind, "tween", StringComparison.OrdinalIgnoreCase) &&
                value.from != null && value.to != null)
            {
                from = value.from;
                to = value.to;
            }
            else
            {
                from = value.transform ?? value.to ?? value.from;
                to = from;
                tween = 1f;
            }
            if (from == null || to == null) return;
            float t = Mathf.Clamp01(tween);
            if (from.position != null && to.position != null)
                _characterRoot.transform.position = Vector3.Lerp(
                    from.position.ToVector3(), to.position.ToVector3(), t);
            if (from.rotation != null && to.rotation != null)
                _characterRoot.transform.rotation = Quaternion.Euler(Vector3.Lerp(
                    from.rotation.ToVector3(), to.rotation.ToVector3(), t));
            if (from.scale != null && to.scale != null)
                _characterRoot.transform.localScale = Vector3.Lerp(
                    from.scale.ToVector3(), to.scale.ToVector3(), t);
        }

        public void PlayStoryVoice(string label, float offset)
        {
            VoiceRecord record = _catalog.Manifest.voices.FirstOrDefault(value => string.Equals(value.label, label, StringComparison.OrdinalIgnoreCase));
            if (record == null)
            {
                Debug.LogWarning("[Story] Voice missing: " + label);
                return;
            }
            _storyVoiceRequest++;
            StartCoroutine(LoadAndPlayStoryVoice(record, offset, _storyVoiceRequest));
        }

        protected IEnumerator LoadAndPlayStoryVoice(VoiceRecord record, float offset, int requestId)
        {
            AudioClip clip;
            if (!_voiceCache.TryGetValue(record.label, out clip))
            {
                string path = _catalog.ResolveVoicePath(record);
                using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV))
                {
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning("[Story] Voice load failed: " + request.error);
                        yield break;
                    }
                    clip = DownloadHandlerAudioClip.GetContent(request);
                    clip.name = record.label;
                    _voiceCache[record.label] = clip;
                }
            }
            if (requestId != _storyVoiceRequest) yield break;
            _audioSource.clip = clip;
            _audioSource.time = Mathf.Clamp(offset, 0f, Mathf.Max(0f, clip.length - 0.01f));
            _audioSource.Play();
            Debug.Log(string.Format("[Story] Voice {0} @ {1:0.00}s", record.label, offset));
        }

        public void SetStoryAudioPaused(bool paused)
        {
            if (_audioSource == null) return;
            if (paused) _audioSource.Pause(); else _audioSource.UnPause();
        }

        public string[] DescribeRenderers()
        {
            return _characterRoot.GetComponentsInChildren<Renderer>(true)
                .Select(renderer => string.Format(
                    "{0} | type={1} enabled={2} active={3} bounds=({4}) size=({5}) materials={6}",
                    TransformPath(renderer.transform, _characterRoot.transform),
                    renderer.GetType().Name,
                    renderer.enabled,
                    renderer.gameObject.activeInHierarchy,
                    renderer.bounds.center.ToString("F3"),
                    renderer.bounds.size.ToString("F3"),
                    string.Join(",", renderer.sharedMaterials.Select(material => material == null ? "null" : material.name)) +
                    MeshSummary(renderer)))
                .ToArray();
        }

        protected static string MeshSummary(Renderer renderer)
        {
            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null)
            {
                return MeshDetails(skinned.sharedMesh);
            }
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter == null || filter.sharedMesh == null
                ? " mesh=null"
                : MeshDetails(filter.sharedMesh);
        }

        protected static string MeshDetails(Mesh mesh)
        {
            if (mesh == null) return " mesh=null";
            string indexCounts = string.Join(",", Enumerable.Range(0, mesh.subMeshCount)
                .Select(index => mesh.GetIndexCount(index).ToString()).ToArray());
            return " mesh=" + mesh.name +
                   " vertices=" + mesh.vertexCount +
                   " submeshIndices=[" + indexCounts + "]";
        }

        protected static Transform FindDescendant(Transform root, string name)
        {
            return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(transform => transform.name == name);
        }

        protected static void AlignAndParent(GameObject part, string anchorName, Transform target)
        {
            if (part == null || target == null) return;
            Transform anchor = FindDescendant(part.transform, anchorName);
            if (anchor == null) return;
            Quaternion rotationDelta = target.rotation * Quaternion.Inverse(anchor.rotation);
            part.transform.rotation = rotationDelta * part.transform.rotation;
            part.transform.position += target.position - anchor.position;
            part.transform.SetParent(target, true);
        }

        protected void ResetPart(GameObject part)
        {
            if (part == null) return;
            part.transform.SetParent(_characterRoot.transform, false);
            part.transform.localPosition = Vector3.zero;
            part.transform.localRotation = Quaternion.identity;
            part.transform.localScale = Vector3.one;
        }

        protected void RestoreFaceHeadToPrefab()
        {
            if (_faceHead == null || _faceHeadPrefabParent == null) return;
            _faceHead.SetParent(_faceHeadPrefabParent, false);
            _faceHead.localPosition = _faceHeadPrefabLocalPosition;
            _faceHead.localRotation = _faceHeadPrefabLocalRotation;
            _faceHead.localScale = _faceHeadPrefabLocalScale;
        }

        protected void AttachFaceHeadToBody(Transform bodyHead)
        {
            if (_faceHead == null || bodyHead == null) return;
            // ActorDescriptor/VL compose the face mesh at Actor root (its
            // vertices are authored in body space), while binding Head_Face
            // itself below the live body Head. Parenting the entire face prefab
            // below Head preserved the mesh only by a compensating transform
            // and left eye/decal bones near world origin. Reproduce the original
            // split ownership directly.
            _faceHead.SetParent(bodyHead, false);
            _faceHead.localPosition = Vector3.zero;
            _faceHead.localRotation = Quaternion.identity;
            _faceHead.localScale = Vector3.one;
        }

        protected static Avatar BuildHumanoidAvatar(GameObject body)
        {
            Dictionary<string, string> map = new Dictionary<string, string>
            {
                { "Hips", "Hips" }, { "Spine", "Spine" }, { "Chest", "Spine1" }, { "UpperChest", "Spine2" },
                { "Neck", "Neck" }, { "Head", "Head" },
                { "LeftEye", "LeftEye" }, { "RightEye", "RightEye" },
                { "LeftShoulder", "LeftShoulder" }, { "RightShoulder", "RightShoulder" },
                { "LeftUpperArm", "LeftArm" }, { "RightUpperArm", "RightArm" },
                { "LeftLowerArm", "LeftForeArm" }, { "RightLowerArm", "RightForeArm" },
                { "LeftHand", "LeftHand" }, { "RightHand", "RightHand" },
                { "LeftUpperLeg", "LeftUpLeg" }, { "RightUpperLeg", "RightUpLeg" },
                { "LeftLowerLeg", "LeftLeg" }, { "RightLowerLeg", "RightLeg" },
                { "LeftFoot", "LeftFoot" }, { "RightFoot", "RightFoot" },
                { "LeftToes", "LeftToeBase" }, { "RightToes", "RightToeBase" },
                { "Left Thumb Proximal", "LeftHandThumb1" }, { "Left Thumb Intermediate", "LeftHandThumb2" }, { "Left Thumb Distal", "LeftHandThumb3" },
                { "Right Thumb Proximal", "RightHandThumb1" }, { "Right Thumb Intermediate", "RightHandThumb2" }, { "Right Thumb Distal", "RightHandThumb3" },
                { "Left Index Proximal", "LeftHandIndex1" }, { "Left Index Intermediate", "LeftHandIndex2" }, { "Left Index Distal", "LeftHandIndex3" },
                { "Right Index Proximal", "RightHandIndex1" }, { "Right Index Intermediate", "RightHandIndex2" }, { "Right Index Distal", "RightHandIndex3" },
                { "Left Middle Proximal", "LeftHandMiddle1" }, { "Left Middle Intermediate", "LeftHandMiddle2" }, { "Left Middle Distal", "LeftHandMiddle3" },
                { "Right Middle Proximal", "RightHandMiddle1" }, { "Right Middle Intermediate", "RightHandMiddle2" }, { "Right Middle Distal", "RightHandMiddle3" },
                { "Left Ring Proximal", "LeftHandRing1" }, { "Left Ring Intermediate", "LeftHandRing2" }, { "Left Ring Distal", "LeftHandRing3" },
                { "Right Ring Proximal", "RightHandRing1" }, { "Right Ring Intermediate", "RightHandRing2" }, { "Right Ring Distal", "RightHandRing3" },
                { "Left Little Proximal", "LeftHandPinky1" }, { "Left Little Intermediate", "LeftHandPinky2" }, { "Left Little Distal", "LeftHandPinky3" },
                { "Right Little Proximal", "RightHandPinky1" }, { "Right Little Intermediate", "RightHandPinky2" }, { "Right Little Distal", "RightHandPinky3" },
            };

            HashSet<string> names = new HashSet<string>(body.GetComponentsInChildren<Transform>(true).Select(value => value.name));
            List<HumanBone> humanBones = new List<HumanBone>();
            foreach (KeyValuePair<string, string> entry in map)
            {
                if (!names.Contains(entry.Value)) continue;
                humanBones.Add(new HumanBone
                {
                    humanName = entry.Key,
                    boneName = entry.Value,
                    limit = new HumanLimit { useDefaultValues = true },
                });
            }

            SkeletonBone[] skeleton = body.GetComponentsInChildren<Transform>(true)
                .Select(value => new SkeletonBone
                {
                    name = value.name,
                    position = value.localPosition,
                    rotation = value.localRotation,
                    scale = value.localScale,
                })
                .ToArray();
            HumanDescription description = new HumanDescription
            {
                human = humanBones.ToArray(),
                skeleton = skeleton,
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };
            Avatar avatar = AvatarBuilder.BuildHumanAvatar(body, description);
            avatar.name = body.name + "-runtime-avatar";
            Debug.Log(string.Format("[PhotoMode] Runtime avatar valid={0} human={1}", avatar.isValid, avatar.isHuman));
            return avatar;
        }

        protected static string TransformPath(Transform transform, Transform stop)
        {
            List<string> parts = new List<string>();
            while (transform != null && transform != stop)
            {
                parts.Add(transform.name);
                transform = transform.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        protected void Update()
        {
            if (!_initialized)
            {
                return;
            }
            if (PreviewCamera != null)
            {
                Vector3 cameraUp = PreviewCamera.transform.up;
                Shader.SetGlobalVector("_CapturedCameraUp",
                    new Vector4(cameraUp.x, cameraUp.y, cameraUp.z, 0f));
            }
            ProcessApplicationInput();
            if (StoryActive) return;
            LoopPlayable(_bodyPlayable);
            LoopPlayable(_facePlayable);
            if (_faceMotionLibrary != null && _faceDriver != null && _facePlayable.IsValid())
                _faceMotionLibrary.Sample(_facePlayable.GetTime(), _faceDriver);
            SamplePhotoMaterialEffects(_facePlayable.IsValid() ? _facePlayable.GetTime() : PhotoMotionTime);
        }

        protected void LateUpdate()
        {
            if (!_initialized || PreviewCamera == null) return;
            Vector3 cameraUp = PreviewCamera.transform.up;
            Shader.SetGlobalVector("_CapturedCameraUp",
                new Vector4(cameraUp.x, cameraUp.y, cameraUp.z, 0f));
            if (_storyActorRenderProfileActive)
            {
                UpdateStoryActorRenderProfileGlobals();
            }
            else if (!_useLegacyRimDirection && !_useRootOnlyRimDirection)
            {
                Vector3 rimDirection = PreviewCamera.transform.TransformDirection(
                    CapturedViewRimDirection).normalized;
                Shader.SetGlobalVector("_CapturedRimDirection",
                    new Vector4(rimDirection.x, rimDirection.y, rimDirection.z, 0f));
            }
        }

        protected static void LoopPlayable(AnimationClipPlayable playable)
        {
            if (!playable.IsValid()) return;
            AnimationClip clip = playable.GetAnimationClip();
            if (clip != null && clip.length > 0.01f && playable.GetTime() >= clip.length)
            {
                playable.SetTime(playable.GetTime() % clip.length);
            }
        }

        public void TogglePause()
        {
            if (StoryActive)
            {
                _storyPlayer.TogglePause();
                _paused = _storyPlayer.IsPaused;
                _status = _paused ? "Story paused" : "Story playing";
                return;
            }
            if (!_motionGraph.IsValid()) return;
            _paused = !_paused;
            for (int index = 0; index < _motionGraph.GetRootPlayableCount(); index++)
            {
                _motionGraph.GetRootPlayable(index).SetSpeed(_paused ? 0 : 1);
            }
            // Zero clip speed still evaluates the look-at job. Stop graph
            // evaluation as well so orbiting a paused pose cannot move its
            // head independently of frozen world-space dynamics.
            if (_paused) _motionGraph.Stop();
            else _motionGraph.Play();
            _status = _paused ? "Paused" : "Playing";
        }

        public void SelectExpression(int index)
        {
            if (_faceExpression == null || _faceMotionLibrary == null) return;
            _faceMotionLibrary.SelectPhotoExpression(index);
            _expressionIndex = _faceMotionLibrary.PhotoExpressionIndex;
            ApplySelectedPhotoExpression();
            _status = "Expression: " + _faceMotionLibrary.CurrentPhotoExpressionLabel;
        }

        public bool SelectExpressionMotion(string motionName)
        {
            if (_faceExpression == null || _faceMotionLibrary == null) return false;
            if (!_faceMotionLibrary.SelectPhotoExpression(motionName)) return false;
            _expressionIndex = _faceMotionLibrary.PhotoExpressionIndex;
            ApplySelectedPhotoExpression();
            _status = "Expression: " + _faceMotionLibrary.CurrentPhotoExpressionLabel;
            return true;
        }

        protected void ApplySelectedPhotoExpression()
        {
            if (_faceExpression == null || _faceMotionLibrary == null) return;
            // All user-facing entries now come from PhotoFacialMotionGroup.
            // Keep the local renderer in driver-follow mode; guessed weighted
            // presets remain inaccessible diagnostic code.
            _faceExpression.SelectPreset(0);
            _faceExpression.SetAutomaticBlinkEnabled(
                !_faceMotionLibrary.CurrentPhotoExpressionDisablesAutoBlink);
            if (_faceDriver != null && _facePlayable.IsValid())
                _faceMotionLibrary.Sample(_facePlayable.GetTime(), _faceDriver, true, true);
            _faceExpression.ApplyCurrentWeights();
            SamplePhotoMaterialEffects(_facePlayable.IsValid() ? _facePlayable.GetTime() : PhotoMotionTime);
        }

        protected void SamplePhotoMaterialEffects(double seconds)
        {
            if (_faceMaterialEffects != null && _faceMotionLibrary != null)
                _faceMaterialEffects.Sample(_faceMotionLibrary.SelectedPhotoMotion, seconds, true);
        }

        public void SetFaceDebugShape(int index, float weight)
        {
            if (_faceExpression == null) return;
            _faceExpression.SetDebugShape(index, weight);
        }

        public void ClearFaceDebugShape()
        {
            if (_faceExpression != null) _faceExpression.ClearDebugShape();
        }

        public void SetHairDynamics(float value)
        {
            if (_hairDynamics != null) _hairDynamics.strength = Mathf.Clamp(value, 0f, 1.5f);
        }

        /// <summary>Shared wind for this actor's hair and clothing, including subsequent outfit rebuilds.</summary>
        public void SetNaturalWind(NaturalWindSettings settings)
        {
            _naturalWind = settings;
            BindNaturalWind();
        }

        /// <summary>Null follows scaled Unity time. A value freezes/samples the wind clock, not the solver state.</summary>
        public void SetNaturalWindTime(double? seconds)
        {
            if (seconds.HasValue && (double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value) || Math.Abs(seconds.Value) > 1e12))
                throw new ArgumentOutOfRangeException(nameof(seconds));
            _naturalWindTime = seconds;
            BindNaturalWind();
        }

        private void BindNaturalWind()
        {
            BindNaturalWind(_hairDynamics);
            BindNaturalWind(_garmentDynamics);
            BindNaturalWind(_skirtDynamics);
        }

        private void BindNaturalWind(HairDynamicsSystem dynamics)
        {
            if (dynamics == null) return;
            dynamics.naturalWind = _naturalWind;
            dynamics.naturalWindTimeOverride = _naturalWindTime;
        }

        public void PlayVoice(int index)
        {
            if (_catalog.Manifest.voices == null || _catalog.Manifest.voices.Length == 0) return;
            _voiceIndex = (index % _catalog.Manifest.voices.Length + _catalog.Manifest.voices.Length) % _catalog.Manifest.voices.Length;
            StartCoroutine(LoadAndPlayVoice(_catalog.Manifest.voices[_voiceIndex]));
        }

        protected IEnumerator LoadAndPlayVoice(VoiceRecord record)
        {
            string path = _catalog.ResolveVoicePath(record);
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _status = "Voice error: " + request.error;
                    yield break;
                }
                _audioSource.clip = DownloadHandlerAudioClip.GetContent(request);
                _audioSource.Play();
                _status = "Voice: " + record.label;
                Debug.Log(string.Format("[PhotoMode] Voice ready: {0} ({1:0.00}s)", record.label, _audioSource.clip.length));
            }
        }

        public string SaveScreenshot()
        {
            string directory = Path.Combine(_stagingRoot ?? BundleCatalog.DefaultStagingRoot, "captures");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, CaptureFilePrefix() + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
            RenderTexture texture = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = PreviewCamera.targetTexture;
            if (RuntimeArguments.Contains("--dump-post-inputs"))
            {
                OriginalStyleRenderPipeline.RequestPostInputDump(Path.Combine(
                    directory, Path.GetFileNameWithoutExtension(path) + "-post"));
            }
            PreviewCamera.targetTexture = texture;
            PreviewCamera.Render();
            RenderTexture.active = texture;
            Texture2D image = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
            image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
            PreviewCamera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            DestroyImmediate(image);
            DestroyImmediate(texture);
            _status = "Saved: " + Path.GetFileName(path);
            return path;
        }

        protected string SavePresentedFrameScreenshot()
        {
            string directory = Path.Combine(_stagingRoot ?? BundleCatalog.DefaultStagingRoot, "captures");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory,
                CaptureFilePrefix() + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-presented.png");
            int width = Mathf.Max(1, Screen.width);
            int height = Mathf.Max(1, Screen.height);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = null;
            Texture2D image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply(false, false);
            File.WriteAllBytes(path, image.EncodeToPNG());
            RenderTexture.active = previousActive;
            DestroyImmediate(image);
            _status = "Saved presented frame: " + Path.GetFileName(path);
            return path;
        }

        protected string CaptureFilePrefix()
        {
            return (_characterId ?? "actor") + "-wearing-" + (CurrentOutfitOwner ?? "unknown");
        }

        public void Shutdown()
        {
            _storyVoiceRequest++;
            if (_audioSource != null) _audioSource.Stop();
            foreach (AudioClip clip in _voiceCache.Values)
            {
                if (clip != null) DestroyImmediate(clip);
            }
            _voiceCache.Clear();
            if (_actorEnvironmentCube != null)
            {
                DestroyImmediate(_actorEnvironmentCube);
                _actorEnvironmentCube = null;
            }
            if (_actorEyeEnvironmentArray != null)
            {
                DestroyImmediate(_actorEyeEnvironmentArray);
                _actorEyeEnvironmentArray = null;
            }
            if (_actorEnvironmentArray != null)
            {
                DestroyImmediate(_actorEnvironmentArray);
                _actorEnvironmentArray = null;
            }
            if (_storyBackgroundRuntime != null)
            {
                _storyBackgroundRuntime.Dispose();
                _storyBackgroundRuntime = null;
            }
            if (_storyOverlayRuntime != null)
            {
                _storyOverlayRuntime.Dispose();
                _storyOverlayRuntime = null;
            }
            if (_riverbedEnvironment != null)
            {
                _riverbedEnvironment.Dispose();
                _riverbedEnvironment = null;
            }
            if (_motionGraph.IsValid()) _motionGraph.Destroy();
            if (_faceMaterialEffects != null)
            {
                _faceMaterialEffects.Dispose();
                _faceMaterialEffects = null;
            }
            if (_catalog != null)
            {
                _catalog.Dispose();
                _catalog = null;
            }
            _initialized = false;
        }

        protected void OnDestroy()
        {
            Shutdown();
        }
        protected string[] RuntimeArguments = Array.Empty<string>();

        protected virtual void ConfigureApplication() { }
        protected virtual void ConfigureAmbientWallpaperDemo() { }
        protected virtual void ProcessApplicationInput() { }
        protected virtual void AttachApplicationCameraComponents(Camera camera) { }
        protected virtual ActorRenderControls CreateRenderControls(Camera camera)
        {
            return camera.gameObject.AddComponent<ActorRenderControls>();
        }

        protected void ConfigureRendererFromArguments(string[] commandLine)
        {
            _disableStoryActorProfile = commandLine.Contains("--disable-story-actor-profile");
            float faceDebugMode = commandLine.Contains("--face-debug-base") ? 1f :
                                  commandLine.Contains("--face-debug-shade") ? 2f :
                                  commandLine.Contains("--face-debug-layer") ? 3f :
                                  commandLine.Contains("--face-debug-normal") ? 4f :
                                  commandLine.Contains("--actor-debug-skin") ? 5f :
                                  commandLine.Contains("--actor-debug-neck") ? 6f :
                                  commandLine.Contains("--actor-debug-base") ? 7f :
                                  commandLine.Contains("--actor-debug-shade") ? 8f :
                                  commandLine.Contains("--actor-debug-type") ? 9f :
                                  commandLine.Contains("--actor-debug-ramp-alpha") ? 10f :
                                  commandLine.Contains("--actor-debug-shade-alpha") ? 11f :
                                  commandLine.Contains("--actor-debug-shadow") ? 12f :
                                  commandLine.Contains("--actor-debug-definition") ? 13f :
                                  commandLine.Contains("--actor-debug-raw-base") ? 14f :
                                  commandLine.Contains("--actor-debug-raw-shade") ? 15f :
                                  commandLine.Contains("--actor-debug-captured-diffuse") ? 16f :
                                  commandLine.Contains("--actor-debug-ramp-coordinate") ? 17f :
                                  commandLine.Contains("--actor-debug-ramp-add") ? 18f :
                                  commandLine.Contains("--actor-debug-direct-diffuse") ? 19f :
                                  commandLine.Contains("--actor-debug-specular") ? 20f :
                                  commandLine.Contains("--actor-debug-brdf") ? 21f :
                                  commandLine.Contains("--actor-debug-rim") ? 22f :
                                  commandLine.Contains("--actor-debug-prelight") ? 23f :
                                  commandLine.Contains("--actor-debug-ramp-rgb") ? 24f :
                                  commandLine.Contains("--actor-debug-ramp-a") ? 25f :
                                  commandLine.Contains("--actor-debug-shade-a") ? 26f :
                                  commandLine.Contains("--actor-debug-base-a") ? 27f :
                                  commandLine.Contains("--actor-debug-ramp-add-a") ? 28f :
                                  commandLine.Contains("--actor-debug-receiver-normal") ? 29f :
                                  commandLine.Contains("--actor-debug-receiver-ndl") ? 30f : 0f;
            Shader.SetGlobalFloat("_FaceDebugMode", faceDebugMode);
            // The captured Base/Shade/Ramp equation is the production path for every
            // active Actor material. Type 9 additionally consumes the animated
            // head-basis normal emitted through VS TEXCOORD4 for its ramp coordinate.
            float capturedDiffuseBlend = commandLine.Contains("--actor-legacy-diffuse") ? 0f :
                                         commandLine.Contains("--actor-captured-diffuse") ? 1f : 1f;
            Shader.SetGlobalFloat("_CapturedDiffuseBlend", capturedDiffuseBlend);
            float capturedDirectScale = commandLine.Contains("--captured-direct-055") ? 0.55f :
                                        commandLine.Contains("--captured-direct-082") ? 0.82f : 1.0f;
            Shader.SetGlobalFloat("_CapturedDirectScale", capturedDirectScale);
            // Bound Actor PS variants sample Base/Shade/Definition/Highlight
            // with CB0[5].x - 1. CB0[5].x is zero in the archived frame.
            // Keep the old unbiased path only for a controlled A/B.
            float capturedActorTextureLodBias = commandLine.Contains("--actor-lod-bias-zero") ? 0f :
                                                commandLine.Contains("--actor-lod-bias-minus-two") ? -2f : -1f;
            Shader.SetGlobalFloat("_CapturedActorTextureLodBias", capturedActorTextureLodBias);
            // Surface color precedes scene fog and target quantization.
            // The former .97 fit mixed those stages into material shading.
            // Keep it only as an explicit historical regression control;
            // --actor-output-literal remains a compatible no-op.
            Shader.SetGlobalFloat("_CapturedActorOutputScale",
                commandLine.Contains("--legacy-actor-output-scale") ? 0.97f : 1f);
            Shader.SetGlobalFloat("_CapturedType4DebugStage",
                commandLine.Contains("--type4-debug-diffuse") ? 1f :
                commandLine.Contains("--type4-debug-direct-diffuse") ? 2f :
                commandLine.Contains("--type4-debug-ramp-coordinate") ? 3f :
                commandLine.Contains("--type4-debug-visibility") ? 4f :
                commandLine.Contains("--type4-debug-shadowed-ramp") ? 5f :
                commandLine.Contains("--type4-debug-raw-base") ? 6f :
                commandLine.Contains("--type4-debug-raw-shade") ? 7f :
                commandLine.Contains("--type4-debug-raw-definition") ? 8f :
                commandLine.Contains("--type4-debug-raw-ramp") ? 9f :
                commandLine.Contains("--type4-debug-light-source") ? 10f :
                commandLine.Contains("--type4-debug-rim-source") ? 11f :
                commandLine.Contains("--type4-debug-environment") ? 12f :
                commandLine.Contains("--type4-debug-environment-brdf") ? 13f :
                commandLine.Contains("--type4-debug-direct-specular") ? 14f :
                commandLine.Contains("--type4-debug-specular") ? 15f :
                commandLine.Contains("--type4-debug-receiver-normal") ? 16f :
                commandLine.Contains("--type4-debug-definition-alpha") ? 17f : 0f);
            Shader.SetGlobalFloat("_CapturedType4RampFlip",
                commandLine.Contains("--type4-unflipped-ramp") ? 0f : 1f);
            Shader.SetGlobalFloat("_CapturedType4DiffuseF0",
                commandLine.Contains("--type4-legacy-dielectric-f0") ? 0f : 1f);
            // exactdefinitiona4 and exactvisibility4 are numerically the
            // same field for the captured type-4 draw (r10.w -> r4.z).
            // The local shadow lookup is therefore not a visibility input
            // for this branch; keep it only as an explicit counterexample.
            Shader.SetGlobalFloat("_CapturedType4DefinitionVisibility",
                commandLine.Contains("--type4-shadowed-visibility") ? 0f : 1f);
            // Exhaustive 6-permutation × 8-sign replay comparison selects
            // mode 43: local cube direction (-Z,-Y,+X).  Keep all 48 modes
            // available for counterexample captures.
            int eyeCubeTransformMode = 43;
            const string eyeCubeTransformPrefix = "--eye-cube-transform-";
            foreach (string argument in commandLine)
            {
                if (!argument.StartsWith(eyeCubeTransformPrefix, StringComparison.Ordinal)) continue;
                int parsed;
                if (int.TryParse(argument.Substring(eyeCubeTransformPrefix.Length), out parsed))
                    eyeCubeTransformMode = Mathf.Clamp(parsed, 0, 47);
            }
            Shader.SetGlobalFloat("_CapturedEyeCubeTransformMode", eyeCubeTransformMode);
            int actorCubeTransformMode = 0;
            const string actorCubeTransformPrefix = "--actor-cube-transform-";
            foreach (string argument in RuntimeArguments)
            {
                if (!argument.StartsWith(actorCubeTransformPrefix, StringComparison.Ordinal)) continue;
                int parsed;
                if (int.TryParse(argument.Substring(actorCubeTransformPrefix.Length), out parsed))
                    actorCubeTransformMode = Mathf.Clamp(parsed, 0, 47);
            }
            Shader.SetGlobalFloat("_CapturedActorCubeTransformMode", actorCubeTransformMode);
            Shader.SetGlobalFloat("_UseCapturedEyeEnvironmentArray",
                commandLine.Contains("--explicit-d3d-eye-array") ? 1f : 0f);
            Shader.SetGlobalFloat("_UseCapturedActorEnvironmentArray",
                commandLine.Contains("--explicit-d3d-actor-array") ? 1f : 0f);
            // Type-1 production selects the explicit D3D face array only
            // after the captured payload is loaded. Other Actor variants
            // retain their already-validated Unity-cube path.
            Shader.SetGlobalFloat("_UseCapturedType1ActorEnvironmentArray", 0f);
            // Type 4 is a premultiplied One/OneMinusSrcAlpha eye draw.  The
            // shader zeros both RGB and alpha for this paired diagnostic so
            // it is equivalent to suppressing the original 9132-byte draw.
            Shader.SetGlobalFloat("_CapturedType4OutputScale",
                commandLine.Contains("--discard-type4-color") ? 0f : 1f);
            // Paired with the GPA discardtype1 replay. Type 1 is opaque
            // One/Zero in the captured frame, so MaterialRepairer switches
            // only this diagnostic to destination-preserving additive blend.
            Shader.SetGlobalFloat("_CapturedType1OutputScale",
                commandLine.Contains("--discard-type1-color") ? 0f : 1f);
            Shader.SetGlobalFloat("_CapturedType1BodyOutputScale",
                commandLine.Contains("--discard-type1-body-color") ? 0f : 1f);
            Shader.SetGlobalFloat("_CapturedType1HairOutputScale",
                commandLine.Contains("--discard-type1-hair-color") ? 0f : 1f);
            bool debugType1Body =
                commandLine.Contains("--type1-debug-body-diffuse") ||
                commandLine.Contains("--type1-debug-body-direct-diffuse") ||
                commandLine.Contains("--type1-debug-body-light-source") ||
                commandLine.Contains("--type1-debug-body-rim-source") ||
                commandLine.Contains("--type1-debug-body-view-normal") ||
                commandLine.Contains("--type1-debug-body-world-normal") ||
                commandLine.Contains("--type1-debug-body-rim-mask") ||
                commandLine.Contains("--type1-debug-body-rim-factor") ||
                commandLine.Contains("--type1-debug-body-specular") ||
                commandLine.Contains("--type1-debug-body-brdf") ||
                commandLine.Contains("--type1-debug-body-environment") ||
                commandLine.Contains("--type1-debug-body-env-brdf") ||
                commandLine.Contains("--type1-debug-body-direct-specular") ||
                commandLine.Contains("--type1-debug-body-specular-visibility") ||
                commandLine.Contains("--type1-debug-body-premod-specular") ||
                commandLine.Contains("--type1-debug-body-reflection-direction") ||
                commandLine.Contains("--type1-debug-body-reflection-lod") ||
                commandLine.Contains("--type1-debug-body-view-direction") ||
                commandLine.Contains("--type1-debug-body-geometric-normal") ||
                commandLine.Contains("--type1-debug-body-raw-view-vector");
            bool debugType1Hair =
                commandLine.Contains("--type1-debug-hair-diffuse") ||
                commandLine.Contains("--type1-debug-hair-direct-diffuse") ||
                commandLine.Contains("--type1-debug-hair-light-source") ||
                commandLine.Contains("--type1-debug-hair-rim-source") ||
                commandLine.Contains("--type1-debug-hair-view-normal") ||
                commandLine.Contains("--type1-debug-hair-world-normal") ||
                commandLine.Contains("--type1-debug-hair-rim-mask") ||
                commandLine.Contains("--type1-debug-hair-rim-factor") ||
                commandLine.Contains("--type1-debug-hair-specular") ||
                commandLine.Contains("--type1-debug-hair-brdf");
            Shader.SetGlobalFloat("_CapturedType1DebugVariant",
                debugType1Body ? 1f : debugType1Hair ? 2f : 0f);
            Shader.SetGlobalFloat("_CapturedType1DebugStage",
                commandLine.Contains("--type1-debug-body-diffuse") ||
                commandLine.Contains("--type1-debug-hair-diffuse") ? 1f :
                commandLine.Contains("--type1-debug-body-direct-diffuse") ||
                commandLine.Contains("--type1-debug-hair-direct-diffuse") ? 2f :
                commandLine.Contains("--type1-debug-body-light-source") ||
                commandLine.Contains("--type1-debug-hair-light-source") ? 3f :
                commandLine.Contains("--type1-debug-body-rim-source") ||
                commandLine.Contains("--type1-debug-hair-rim-source") ? 4f :
                commandLine.Contains("--type1-debug-body-view-normal") ||
                commandLine.Contains("--type1-debug-hair-view-normal") ? 5f :
                commandLine.Contains("--type1-debug-body-world-normal") ||
                commandLine.Contains("--type1-debug-hair-world-normal") ? 6f :
                commandLine.Contains("--type1-debug-body-rim-mask") ||
                commandLine.Contains("--type1-debug-hair-rim-mask") ? 7f :
                commandLine.Contains("--type1-debug-body-rim-factor") ||
                commandLine.Contains("--type1-debug-hair-rim-factor") ? 8f :
                commandLine.Contains("--type1-debug-body-specular") ||
                commandLine.Contains("--type1-debug-hair-specular") ? 9f :
                commandLine.Contains("--type1-debug-body-brdf") ||
                commandLine.Contains("--type1-debug-hair-brdf") ? 10f :
                commandLine.Contains("--type1-debug-body-environment") ? 11f :
                commandLine.Contains("--type1-debug-body-env-brdf") ? 12f :
                commandLine.Contains("--type1-debug-body-direct-specular") ? 13f :
                commandLine.Contains("--type1-debug-body-specular-visibility") ? 14f :
                commandLine.Contains("--type1-debug-body-premod-specular") ? 15f :
                commandLine.Contains("--type1-debug-body-reflection-direction") ? 16f :
                commandLine.Contains("--type1-debug-body-reflection-lod") ? 17f :
                commandLine.Contains("--type1-debug-body-view-direction") ? 18f :
                commandLine.Contains("--type1-debug-body-geometric-normal") ? 19f :
                commandLine.Contains("--type1-debug-body-raw-view-vector") ? 20f : 0f);
            // Offline GPA replay can now suppress only the captured
            // 7496-byte type-5 colour draw. Mirror that probe without
            // changing local raster/depth/ActorData so paired HDR dumps
            // isolate the reconstructed additive eye-highlight energy.
            Shader.SetGlobalFloat("_CapturedType5OutputScale",
                commandLine.Contains("--discard-type5-color") ? 0f : 1f);
            // Bound Actor PS 5C07/717B/7372 evaluates ramp and direct BRDF
            // in its camera-facing receiver basis (r6), not directly in the
            // interpolated world-normal basis.  Exact-pose pass151/152 closed
            // that contract against the replay, so it is now the production
            // default.  Keep the former world-normal path only for A/B.
            Shader.SetGlobalFloat("_UseCapturedReceiverNormal",
                commandLine.Contains("--legacy-world-receiver-normal") ? 0f : 1f);
            // Literal GGX denominator and visibility gate from active
            // Actor PS 5C07/717B/7372.  The former reconstruction used
            // smoothness where DXBC uses roughness^4-1 and gated the lobe
            // with world N.L instead of receiver-basis N.L.  Keep that
            // compensating approximation only for a controlled A/B.
            Shader.SetGlobalFloat("_UseCapturedDirectSpecular",
                commandLine.Contains("--legacy-direct-specular") ? 0f : 1f);
            // Exact values recovered from the active actor draws through a signed GPA
            // replay layer. CB0 is identical across all seven type 9/8/3 draws.
            // Pass80 tested both the literal CB0 directions and an inverse-root
            // transform derived from VS CB1. The literal constants converge much
            // better (Actor HDR luma 1.0089x vs 1.1489x), which shows the authored
            // BRDF/rim vectors are already in the receiver convention expected by
            // the extracted model. Keep the transformed path only as a diagnostic.
            bool transformCapturedShadingDirections = commandLine.Contains("--actor-local-shading-direction");
            _useRootOnlyRimDirection = transformCapturedShadingDirections;
            Shader.SetGlobalVector("_CapturedLightDirection", transformCapturedShadingDirections
                ? new Vector4(0.78512269f, 0.42261863f, -0.45274259f, 0f)
                : new Vector4(0.38302225f, 0.42261827f, 0.82139379f, 0f));
            Shader.SetGlobalVector("_CapturedShadeTint", new Vector4(0.85849059f, 0.76552117f, 0.74105555f, 1f));
            Shader.SetGlobalVector("_CapturedLightColor", Vector4.one);
            Shader.SetGlobalVector("_CapturedShadeAdditive", new Vector4(0f, 0f, 0f, 1f));
            Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
            Shader.SetGlobalVector("_CapturedReflectionColor", Vector4.one);
            Shader.SetGlobalVector("_CapturedEyeReflectionColor", Vector4.one);
            // The PS does not dot its interpolated normal directly with
            // CB0[153].xyz. It first forms r7 from CB0[65..67], while the
            // frozen-pose diagnostic stores normals in the original Actor
            // root's local basis. Combining those two rotations with the
            // captured +94.97-degree Actor root gives the literal local
            // direction below. An exact rim-only GPA replay closes this
            // combined basis at corr=0.9403 for type-8 hair and corr=0.9888
            // for type-0 costume after the same one-pixel registration. The
            // former raw-CB direction produced corr=0.0293/0.1748 and a
            // broad pale halo, so the combined direction is production.
            // Retain both earlier interpretations only for controlled A/B.
            _useLegacyRimDirection = commandLine.Contains("--legacy-rim-direction");
            Shader.SetGlobalFloat("_UseExactViewRimBasis",
                _useLegacyRimDirection || transformCapturedShadingDirections ? 0f : 1f);
            Shader.SetGlobalVector("_CapturedRimViewDirection",
                new Vector4(-0.29704621f, -0.27563736f, 0.91421419f, 0f));
            Shader.SetGlobalVector("_CapturedRimDirection", transformCapturedShadingDirections
                ? new Vector4(0.93651113f, -0.27563763f, 0.21672747f, 0f)
                : _useLegacyRimDirection
                    ? new Vector4(-0.29704621f, -0.27563736f, 0.91421419f, 0f)
                    : new Vector4(0.29546950f, -0.17236277f, 0.93967480f, 0f));
            Shader.SetGlobalVector("_CapturedRimParameters", new Vector4(0.7f, 0.85f, 32f, 1f));
            Shader.SetGlobalVector("_CapturedShadowCasterDirection",
                new Vector4(-0.17343573f, 0.21206431f, 0.96174257f, 0f));
            // Caster VS E82E31C9 uses these exact world-unit offsets while
            // every captured rasterizer reports fixed/slope depth bias zero.
            bool useCapturedCasterBias = !commandLine.Contains("--unity-shadow-bias");
            Shader.SetGlobalVector("_CapturedShadowCasterBias", useCapturedCasterBias
                ? new Vector4(-0.00079697941f, -0.00019924485f, 1f, 0f)
                : new Vector4(0f, 0f, 0f, 0f));
            Shader.SetGlobalVector("_CapturedSH0", new Vector4(0.00726612285f, -0.184132382f, 0.0522714779f, 0.368471146f));
            Shader.SetGlobalVector("_CapturedSH1", new Vector4(0.00590136182f, -0.144511163f, 0.0502534062f, 0.398529291f));
            Shader.SetGlobalVector("_CapturedSH2", new Vector4(0.00396161340f, -0.0267445855f, 0.0917166322f, 0.583782017f));
            Shader.SetGlobalVector("_CapturedSH3", new Vector4(-0.0104490332f, -0.0136711346f, -0.0209487341f, -0.00180071173f));
            Shader.SetGlobalVector("_CapturedSH4", new Vector4(-0.0123896031f, -0.00661265291f, -0.0159785878f, -0.00217014784f));
            Shader.SetGlobalVector("_CapturedSH5", new Vector4(-0.0224926770f, -0.0107651427f, -0.0217662323f, 0.000522873364f));
            Shader.SetGlobalVector("_CapturedSH6", new Vector4(-0.000540113775f, 0.00394574553f, 0.00150158303f, 1f));
            // CB0[154].x gates the complete SH term and is exactly zero in all seven
            // captured actor draws. Keep the coefficients archived, but honor the gate.
            Shader.SetGlobalFloat("_CapturedAmbientScale", 0f);
        }
    }
}
