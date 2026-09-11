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
    public sealed class PhotoModeApp : CharacterSceneRuntime
    {
        private bool _ambientReactionActive;
        private int _ambientReactionSequence;
        private float _ambientNextReactionAt;
        private float _ambientReturnToIdleAt;
        private string _capturedMaterialUvPath;
        private string _capturedCameraPath;
        private bool _showUi = true;
        private GUIStyle _panelStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _sectionStyle;
        private GUIStyle _captionStyle;
        private GUIStyle _buttonStyle;
        private Texture2D _panelTexture;
        private Texture2D _buttonTexture;

        private void Awake()
        {
            RuntimeArguments = Environment.GetCommandLineArgs();
            ResetCapturedActorRenderGlobals();
            Shader.SetGlobalFloat("_UseCapturedAmbientSH", 0f);
            if (Application.isPlaying)
            {
                string[] commandLine = Environment.GetCommandLineArgs();
                string capturedUvError;
                if (!CapturedMaterialUvState.TryReadOption(commandLine, out _capturedMaterialUvPath, out capturedUvError))
                {
                    Debug.LogError("[PhotoMode] " + capturedUvError);
                    Application.Quit(3);
                    return;
                }
                string capturedCameraError;
                if (!CapturedCameraState.TryReadOption(commandLine, out _capturedCameraPath, out capturedCameraError))
                {
                    Debug.LogError("[PhotoMode] " + capturedCameraError);
                    Application.Quit(3);
                    return;
                }
                _ambientWallpaperDemo = commandLine.Contains("--ambient-wallpaper-demo");
                ConfigureRendererFromArguments(commandLine);
                if (ActorRenderingSelfTest.TryStart(gameObject)) { enabled = false; return; }
                Initialize(BundleCatalog.DefaultStagingRoot);
                if (commandLine.Contains("--hide-ui")) _showUi = false;
                if (commandLine.Contains("--renderdoc-capture-and-quit"))
                {
                    StartCoroutine(CaptureRenderDocAndQuit());
                }
                else if (commandLine.Contains("--capture-story-and-quit"))
                {
                    StartCoroutine(CaptureStoryAndQuit());
                }
                else if (commandLine.Contains("--capture-gpa-camera-and-quit"))
                {
                    StartCoroutine(CaptureGpaCameraAndQuit());
                }
                else if (commandLine.Contains("--capture-gpa-camera-sweep-and-quit"))
                {
                    StartCoroutine(CaptureGpaCameraSweepAndQuit());
                }
                else if (commandLine.Contains("--capture-gpa-camera-mask-sweep-and-quit"))
                {
                    StartCoroutine(CaptureGpaCameraMaskSweepAndQuit());
                }
                else if (commandLine.Contains("--capture-actor-cube-transform-sweep-and-quit"))
                {
                    StartCoroutine(CaptureActorCubeTransformSweepAndQuit());
                }
                else if (commandLine.Contains("--capture-dynamics-and-quit"))
                {
                    StartCoroutine(CaptureDynamicsAndQuit());
                }
                else if (commandLine.Contains("--capture-expression-and-quit"))
                {
                    StartCoroutine(CaptureExpressionAndQuit());
                }
                else if (commandLine.Contains("--capture-and-quit"))
                {
                    StartCoroutine(CaptureAndQuit());
                }
            }
        }

        protected override void ConfigureAmbientWallpaperDemo()
        {
            _showUi = false;
            if (_orbit != null)
            {
                // A quiet, asymmetric desktop composition. Camera controls remain live;
                // this is intentionally still a normal window rather than a WorkerW host.
                _orbit.ConfigureDefaultPose(
                    new Vector3(0.18f, 0.96f, 0f),
                    3.18f,
                    180f,
                    1.5f,
                    31f);
            }
            if (_hairDynamics != null)
            {
                _hairDynamics.strength = 0.92f;
                _hairDynamics.windStrength = 0f;
                _hairDynamics.gravityStrength = 1.0f;
            }
            if (_skirtDynamics != null) _skirtDynamics.strength = 1.0f;
            if (_garmentDynamics != null) _garmentDynamics.strength = 1.0f;
            if (_bodySoftTissueDynamics != null) _bodySoftTissueDynamics.strength = 1.0f;
            if (_faceExpression != null)
            {
                // Original photography layouts explicitly serialize
                // EnableLookAtFlag=0.  The first raw demo instead aimed both eyes
                // at an off-centre camera target, producing the obvious sideways
                // stare. Keep the idle/reaction face motion, but leave eye bones at
                // their authored neutral direction in this photo-derived mode.
                _faceExpression.SetStoryGazeMode(true);
                _faceExpression.SetStoryGaze(0f, 0f);
            }
            SelectMotionByLabel("photo-idle-001");
            _ambientReactionActive = false;
            _ambientNextReactionAt = Time.unscaledTime +
                (Environment.GetCommandLineArgs().Contains("--capture-and-quit") ? 0.75f : 8f);
            Debug.Log("[AmbientDemo] Ready: 30 fps, ADV backdrop, idle/reaction loop, UI hidden");
        }

        private void UpdateAmbientWallpaperDemo()
        {
            if (StoryActive || _paused) return;
            float now = Time.unscaledTime;
            if (_ambientReactionActive)
            {
                if (now < _ambientReturnToIdleAt) return;
                SelectMotionByLabel((_ambientReactionSequence & 1) == 0
                    ? "photo-idle-001"
                    : "photo-idle-002");
                _ambientReactionActive = false;
                _ambientNextReactionAt = now + 11f;
                return;
            }

            bool clicked = Input.GetMouseButtonDown(0);
            if (!clicked && now < _ambientNextReactionAt) return;
            string reaction = (_ambientReactionSequence++ & 1) == 0
                ? "photo-react-a"
                : "photo-react-b";
            if (!SelectMotionByLabel(reaction))
            {
                _ambientNextReactionAt = now + 11f;
                return;
            }
            float duration = CurrentBodyClip == null ? 3f : CurrentBodyClip.length;
            _ambientReactionActive = true;
            _ambientReturnToIdleAt = now + Mathf.Clamp(duration, 1.5f, 8f);
            if (clicked && VoiceCount > 0) PlayVoice(_ambientReactionSequence % VoiceCount);
        }

        private IEnumerator CaptureAndQuit()
        {
            Debug.Log("[PhotoMode] Automated capture requested");
            PlayVoice(0);
            yield return new WaitForSecondsRealtime(2f);
            if (Environment.GetCommandLineArgs().Contains("--dump-face-diagnostics"))
                DumpFaceDiagnostics("normal");
            string path;
            if (Environment.GetCommandLineArgs().Contains("--capture-presented-window"))
            {
                // SaveScreenshot renders a fresh off-screen 1920x1080 frame. That
                // changes the temporal target size and correctly invalidates history,
                // but it cannot tell us what the settled window actually presents.
                // Keep this diagnostic on the ordinary player frames long enough for
                // the captured temporal resolve to converge, then read the backbuffer.
                for (int frame = 0; frame < 32; frame++) yield return null;
                if (Environment.GetCommandLineArgs().Contains("--dump-post-inputs"))
                {
                    string directory = Path.Combine(
                        _stagingRoot ?? BundleCatalog.DefaultStagingRoot, "captures");
                    string prefix = Path.Combine(directory,
                        "fktn-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-presented-post");
                    OriginalStyleRenderPipeline.RequestPostInputDump(prefix);
                }
                yield return new WaitForEndOfFrame();
                path = SavePresentedFrameScreenshot();
            }
            else
            {
                path = SaveScreenshot();
            }
            Debug.Log("[PhotoMode] Automated capture saved: " + path);
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit(0);
        }

        private IEnumerator CaptureRenderDocAndQuit()
        {
            _showUi = false;
            Debug.Log("[RenderDoc] Automated local shader capture requested");
            yield return new WaitForSecondsRealtime(2.0f);
            bool triggered = RenderDocCaptureBridge.TriggerCapture();
            yield return new WaitForSecondsRealtime(triggered ? 2.0f : 0.25f);
            Debug.Log("[RenderDoc] Automated local shader capture finished; triggered=" + triggered);
            Application.Quit(triggered ? 0 : 2);
        }

        private IEnumerator CaptureDynamicsAndQuit()
        {
            Debug.Log("[PhotoMode] Automated dynamics capture requested");
            _showUi = false;
            bool capturePresented = Environment.GetCommandLineArgs().Contains("--capture-presented-window");
            if (_storyPlayer != null && _storyPlayer.IsActive) _storyPlayer.StopStory();
            yield return null;
            string requestedMotion = CommandLineValue("--motion-label");
            if (string.IsNullOrEmpty(requestedMotion))
            {
                int action = _motions.FindIndex(value => string.Equals(value.label, "photo-react-b", StringComparison.OrdinalIgnoreCase));
                SelectMotion(action >= 0 ? action : Mathf.Min(1, _motions.Count - 1));
            }
            _orbit.target = new Vector3(0f, 0.94f, 0f);
            _orbit.distance = 4.15f;
            _orbit.fov = 30f;
            _orbit.ApplyPose();
            yield return new WaitForSecondsRealtime(0.65f);
            if (Environment.GetCommandLineArgs().Contains("--dump-captured-shadow-map"))
            {
                string prefix = Path.Combine(
                    _stagingRoot ?? BundleCatalog.DefaultStagingRoot,
                    "research", "render-diagnostics", "pass256-dynamics-shadow",
                    CaptureFilePrefix() + "-full-body");
                CapturedActorShadowMap.RequestShadowMapDump(prefix);
            }
            if (capturePresented) yield return new WaitForEndOfFrame();
            string first = capturePresented ? SavePresentedFrameScreenshot() : SaveScreenshot();
            if (_quartzGarmentDeformation != null)
                _quartzGarmentDeformation.LogCurrentState("capture-action-a");
            yield return new WaitForSecondsRealtime(0.30f);
            if (capturePresented) yield return new WaitForEndOfFrame();
            string second = capturePresented ? SavePresentedFrameScreenshot() : SaveScreenshot();
            if (_quartzGarmentDeformation != null)
                _quartzGarmentDeformation.LogCurrentState("capture-action-b");
            if (!_paused) TogglePause();
            Transform captureHead = FindDescendant(_body.transform, "Head");
            _orbit.target = captureHead == null ? new Vector3(0f, 1.47f, 0f) : captureHead.position;
            _orbit.distance = 1.32f;
            _orbit.yaw = 166f;
            _orbit.pitch = 1f;
            _orbit.fov = 29f;
            _orbit.ApplyPose();
            if (_faceExpression != null)
            {
                _faceExpression.ForceBlink();
                // The original 95 ms wall-clock guess often reached the
                // reopening tail after a long first frame. Synchronize the
                // evidence capture to the serialized curve's plateau instead.
                float blinkDeadline = Time.realtimeSinceStartup + 0.30f;
                while (_faceExpression.BlinkWeight < 0.99f &&
                       Time.realtimeSinceStartup < blinkDeadline)
                    yield return null;
            }
            if (capturePresented) yield return new WaitForEndOfFrame();
            string third = capturePresented ? SavePresentedFrameScreenshot() : SaveScreenshot();
            _orbit.yaw = 204f;
            _orbit.ApplyPose();
            yield return new WaitForSecondsRealtime(0.38f);
            if (capturePresented) yield return new WaitForEndOfFrame();
            string fourth = capturePresented ? SavePresentedFrameScreenshot() : SaveScreenshot();
            if (_hairDynamics != null) _hairDynamics.LogCurrentState("capture-final");
            if (_breastDynamics != null)
                _breastDynamics.LogCurrentState("capture-final-breast");
            if (_bodySoftTissueDynamics != null)
                _bodySoftTissueDynamics.LogCurrentState("capture-final-soft-tissue");
            if (_garmentDynamics != null)
                _garmentDynamics.LogCurrentState("capture-final-garment");
            if (_quartzArmDeformation != null)
                _quartzArmDeformation.LogCurrentState("capture-final-quartz-arm");
            if (_quartzLegRotationDeformation != null)
                _quartzLegRotationDeformation.LogCurrentState("capture-final-quartz-leg-rotation");
            if (_quartzGarmentDeformation != null)
                _quartzGarmentDeformation.LogCurrentState("capture-final-quartz-garment");
            if (_skirtDynamics != null)
                _skirtDynamics.LogCurrentState("capture-final-skirt");
            Debug.Log(string.Format("[PhotoMode] Dynamics captures saved: {0}, {1}, {2}, {3}; hair mean={4:0.00} max={5:0.00} braid={6:0.00} max={7:0.00} variation={8:0.00}; skirt entries={9} edges={10} terminals={11} colliders={12} quartz={13} chainLayers={14} chainCorrections={15} peakChainCorrections={16} mean={17:0.00} max={18:0.00}; blink={19:0.00} gaze={20}; face active={21} max={22:0.000}",
                first, second, third, fourth,
                _hairDynamics == null ? 0f : _hairDynamics.MeanAngularOffset,
                _hairDynamics == null ? 0f : _hairDynamics.MaxAngularOffset,
                _hairDynamics == null ? 0f : _hairDynamics.BraidMeanAngularOffset,
                _hairDynamics == null ? 0f : _hairDynamics.BraidMaxAngularOffset,
                _hairDynamics == null ? 0f : _hairDynamics.BraidAngularVariation,
                _skirtDynamics == null ? 0 : _skirtDynamics.DynamicEntryCount,
                _skirtDynamics == null ? 0 : _skirtDynamics.SimulatedBoneCount,
                _skirtDynamics == null ? 0 : _skirtDynamics.TerminalEntryCount,
                _skirtDynamics == null ? 0 : _skirtDynamics.StaticColliderCount,
                _skirtDynamics == null ? 0 : _skirtDynamics.QuartzDriverCount,
                _skirtDynamics == null ? 0 : _skirtDynamics.ChainLayerCount,
                _skirtDynamics == null ? 0 : _skirtDynamics.ChainCollisionCorrections,
                _skirtDynamics == null ? 0 : _skirtDynamics.PeakChainCollisionCorrections,
                _skirtDynamics == null ? 0f : _skirtDynamics.MeanAngularOffset,
                _skirtDynamics == null ? 0f : _skirtDynamics.MaxAngularOffset,
                _faceExpression == null ? 0f : _faceExpression.BlinkWeight,
                _faceExpression == null ? Vector2.zero : _faceExpression.GazeAngles,
                _faceExpression == null ? 0 : _faceExpression.ActiveWeightCount,
                _faceExpression == null ? 0f : _faceExpression.MaximumWeight));
            if (_garmentDynamics != null)
            {
                Debug.Log(string.Format(
                    "[PhotoMode] Garment ActorSwing capture: entries={0} edges={1} terminals={2} chainLayers={3} chainCorrections={4} peakChainCorrections={5} mean={6:0.000} max={7:0.000}",
                    _garmentDynamics.DynamicEntryCount,
                    _garmentDynamics.SimulatedBoneCount,
                    _garmentDynamics.TerminalEntryCount,
                    _garmentDynamics.ChainLayerCount,
                    _garmentDynamics.ChainCollisionCorrections,
                    _garmentDynamics.PeakChainCollisionCorrections,
                    _garmentDynamics.MeanAngularOffset,
                    _garmentDynamics.MaxAngularOffset));
            }
            yield return new WaitForSecondsRealtime(0.25f);
            Application.Quit(0);
        }

        private IEnumerator CaptureExpressionAndQuit()
        {
            Debug.Log("[PhotoMode] Automated original photo-expression capture requested");
            _showUi = false;
            if (_storyPlayer != null && _storyPlayer.IsActive) _storyPlayer.StopStory();
            yield return null;
            int idle = _motions.FindIndex(value =>
                string.Equals(value.label, "photo-idle-001", StringComparison.OrdinalIgnoreCase));
            SelectMotion(idle >= 0 ? idle : 0);
            ApplySelectedPhotoExpression();
            Transform captureHead = FindDescendant(_body.transform, "Head");
            _orbit.target = captureHead == null ? new Vector3(0f, 1.47f, 0f) : captureHead.position;
            _orbit.distance = 1.30f;
            _orbit.yaw = 180f;
            _orbit.pitch = 1f;
            _orbit.fov = 29f;
            _orbit.ApplyPose();
            yield return new WaitForSecondsRealtime(0.75f);
            bool capturePresented = Environment.GetCommandLineArgs().Contains("--capture-presented-window");
            if (capturePresented) yield return new WaitForEndOfFrame();
            string path = capturePresented ? SavePresentedFrameScreenshot() : SaveScreenshot();
            Debug.Log(string.Format(
                "[PhotoMode] Original photo expression captured: path={0}; label={1}; motion={2}; disableAutoBlink={3}; faceActive={4}; faceMax={5:0.000}",
                path,
                _faceMotionLibrary == null ? "unavailable" : _faceMotionLibrary.CurrentPhotoExpressionLabel,
                _faceMotionLibrary == null ? "unavailable" : _faceMotionLibrary.CurrentPhotoExpressionMotion,
                _faceMotionLibrary != null && _faceMotionLibrary.CurrentPhotoExpressionDisablesAutoBlink,
                _faceExpression == null ? 0 : _faceExpression.ActiveWeightCount,
                _faceExpression == null ? 0f : _faceExpression.MaximumWeight));
            if (_faceExpression != null)
                Debug.Log("[PhotoMode] Original photo expression face diagnostic: " +
                    _faceExpression.DiagnosticJson());
            yield return new WaitForSecondsRealtime(0.25f);
            Application.Quit(0);
        }

        private IEnumerator CaptureGpaCameraAndQuit()
        {
            _showUi = false;
            Debug.Log("[PhotoMode] Automated GPA-camera capture requested");
            if (_storyPlayer != null && _storyPlayer.IsActive) _storyPlayer.StopStory();
            yield return null;
            int idle = _motions.FindIndex(value =>
                string.Equals(value.label, "photo-idle-002", StringComparison.OrdinalIgnoreCase));
            SelectMotion(idle >= 0 ? idle : 0);
            // The 120-frame ActorData sweep ranked this phase highest against
            // the captured GPA silhouette/category mask: Actor IoU 0.883925,
            // face IoU 0.661020.  This is a diagnostic pose, not a production
            // animation override.
            if (!_paused) TogglePause();
            EvaluateMotion(27.43889f);
            // Face clips are sampled on the shared character root and some source
            // clips key that root.  The archived IA/camera streams were explicitly
            // converted into captured Actor-local coordinates, so lock the diagnostic
            // root before applying either contract.  Production/story transforms are
            // untouched because this path exists only for the automated GPA capture.
            if (_characterRoot != null)
            {
                _characterRoot.transform.position = Vector3.zero;
                _characterRoot.transform.rotation = Quaternion.identity;
                _characterRoot.transform.localScale = Vector3.one;
            }
            ApplyCapturedGpaCamera();
            string[] captureArgs = Environment.GetCommandLineArgs();
            bool diagnosticGaze = captureArgs.Contains("--diagnostic-gaze-right") ||
                captureArgs.Contains("--diagnostic-gaze-left") ||
                captureArgs.Contains("--diagnostic-gaze-right-soft") ||
                captureArgs.Contains("--diagnostic-gaze-left-soft") ||
                captureArgs.Contains("--diagnostic-gaze-up") ||
                captureArgs.Contains("--diagnostic-gaze-down");
            if (_faceExpression != null && diagnosticGaze)
            {
                float gazeYaw = captureArgs.Contains("--diagnostic-gaze-right") ? 9f :
                    captureArgs.Contains("--diagnostic-gaze-left") ? -9f :
                    captureArgs.Contains("--diagnostic-gaze-right-soft") ? 4.5f :
                    captureArgs.Contains("--diagnostic-gaze-left-soft") ? -4.5f : 0f;
                float gazePitch = captureArgs.Contains("--diagnostic-gaze-up") ? -5.5f :
                    captureArgs.Contains("--diagnostic-gaze-down") ? 5.5f : 0f;
                _faceExpression.SetStoryGaze(gazeYaw, gazePitch);
            }
            for (int frame = 0; frame < (diagnosticGaze ? 40 : 1); frame++) yield return null;
            HashSet<Renderer> capturedRenderers = null;
            if (Environment.GetCommandLineArgs().Contains("--use-captured-posed-geometry"))
                capturedRenderers = ApplyCapturedPosedGeometry();
            CapturedCameraState.Session capturedCamera = null;
            if (_capturedCameraPath != null)
            {
                string error = null;
                if (capturedRenderers == null || capturedRenderers.Count == 0 ||
                    !CapturedCameraState.TryLoad(_capturedCameraPath, out capturedCamera, out error))
                {
                    Debug.LogError("[PhotoMode] Captured camera rejected: " + (error ?? "No captured geometry was loaded."));
                    Application.Quit(3);
                    yield break;
                }
                try
                {
                    capturedCamera.Apply(PreviewCamera);
                    // This process is a fixed capture, not interactive photography.
                    // Prevent the legacy orbit pose from rewriting camera-position globals.
                    _orbit.enabled = false;
                }
                catch (Exception exception)
                {
                    Debug.LogError("[PhotoMode] Captured camera rejected: " + exception.Message);
                    Application.Quit(3);
                    yield break;
                }
            }
            CapturedMaterialUvState.Session capturedUv = null;
            if (_capturedMaterialUvPath != null)
            {
                string error;
                if (!CapturedMaterialUvState.TryLoad(_characterRoot, _capturedMaterialUvPath, capturedRenderers, out capturedUv, out error))
                {
                    Debug.LogError("[PhotoMode] Captured material UV rejected: " + error);
                    Application.Quit(3);
                    yield break;
                }
            }
            if (Environment.GetCommandLineArgs().Contains("--dump-captured-shadow-map"))
            {
                string dumpPass = Environment.GetCommandLineArgs().Contains("--use-captured-posed-geometry")
                    ? "gpa-camera-shadow-exact-pose-pass110"
                    : "gpa-camera-shadow-pass98";
                string prefix = Path.Combine(
                    _stagingRoot ?? BundleCatalog.DefaultStagingRoot,
                    "research", "render-diagnostics", dumpPass,
                    "local-captured-actor-shadow");
                CapturedActorShadowMap.RequestShadowMapDump(prefix);
            }
            if (Environment.GetCommandLineArgs().Contains("--dump-local-posed-meshes"))
            {
                string poseDumpPass = Environment.GetCommandLineArgs().Contains("--use-captured-posed-geometry")
                    ? "gpa-camera-pose-exact-pass129"
                    : "gpa-camera-pose-pass100";
                DumpBakedActorMeshes(Path.Combine(
                    _stagingRoot ?? BundleCatalog.DefaultStagingRoot,
                    "research", "render-diagnostics", poseDumpPass,
                    "local-sample55"));
            }
            if (Environment.GetCommandLineArgs().Contains("--dump-face-diagnostics"))
                DumpFaceDiagnostics("gpa-camera");
            string path;
            if (Environment.GetCommandLineArgs().Contains("--capture-presented-window"))
            {
                for (int frame = 0; frame < 32; frame++) yield return null;
                if (capturedUv != null && !WriteCapturedMaterialUvReport(capturedUv))
                { Application.Quit(3); yield break; }
                if (captureArgs.Contains("--capture-actor-rendering-pass"))
                {
                    if (!RenderDocCaptureBridge.TriggerCapture()) { Application.Quit(2); yield break; }
                    yield return null;
                    yield return null;
                }
                if (Environment.GetCommandLineArgs().Contains("--dump-post-inputs"))
                {
                    string directory = Path.Combine(
                        _stagingRoot ?? BundleCatalog.DefaultStagingRoot, "captures");
                    string prefix = Path.Combine(directory,
                        "fktn-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-presented-post");
                    OriginalStyleRenderPipeline.RequestPostInputDump(prefix);
                }
                yield return new WaitForEndOfFrame();
                if (capturedCamera != null && !WriteCapturedCameraReport(capturedCamera))
                { Application.Quit(3); yield break; }
                path = SavePresentedFrameScreenshot();
            }
            else
            {
                if (capturedUv != null && !WriteCapturedMaterialUvReport(capturedUv))
                { Application.Quit(3); yield break; }
                path = SaveScreenshot();
            }
            Debug.Log(string.Format(
                "[PhotoMode] GPA-camera capture saved: {0}; position={1}; forward={2}; up={3}; fov={4:0.000000}; blink={5:0.000}",
                path, PreviewCamera.transform.position, PreviewCamera.transform.forward,
                PreviewCamera.transform.up, PreviewCamera.fieldOfView,
                _faceExpression == null ? 0f : _faceExpression.BlinkWeight));
            if (_actorRenderControls != null)
                Debug.Log("[ActorRendering] GPA-camera submitted passes: outline=" +
                    _actorRenderControls.OutlineDrawCount + "; hairCover=" + _actorRenderControls.HairCoverDrawCount);
            yield return new WaitForSecondsRealtime(0.25f);
            Application.Quit(0);
        }

        private IEnumerator CaptureGpaCameraSweepAndQuit()
        {
            _showUi = false;
            Debug.Log("[PhotoMode] Automated GPA-camera idle sweep requested");
            if (_storyPlayer != null && _storyPlayer.IsActive) _storyPlayer.StopStory();
            yield return null;
            ApplyCapturedGpaCamera();
            if (_faceExpression != null) _faceExpression.SetStoryGaze(0f, 0f);

            string[] labels = { "photo-idle-001", "photo-idle-002" };
            const int sampleCount = 12;
            foreach (string label in labels)
            {
                int motion = _motions.FindIndex(value =>
                    string.Equals(value.label, label, StringComparison.OrdinalIgnoreCase));
                if (motion < 0) continue;
                SelectMotion(motion);
                if (!_paused) TogglePause();
                AnimationClip clip = CurrentBodyClip;
                float duration = clip == null ? 1f : clip.length;
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    float time = duration * sample / sampleCount;
                    EvaluateMotion(time);
                    yield return null;
                    string path = SaveScreenshot();
                    Debug.Log(string.Format(
                        "[PhotoMode] GPA-camera sweep sample: motion={0} sample={1}/{2} time={3:0.000000} duration={4:0.000000} path={5}",
                        label, sample, sampleCount, time, duration, path));
                }
            }
            yield return new WaitForSecondsRealtime(0.25f);
            Application.Quit(0);
        }

        private IEnumerator CaptureActorCubeTransformSweepAndQuit()
        {
            _showUi = false;
            Debug.Log("[PhotoMode] Automated Actor cube transform sweep requested");
            if (_storyPlayer != null && _storyPlayer.IsActive) _storyPlayer.StopStory();
            yield return null;
            int idle = _motions.FindIndex(value =>
                string.Equals(value.label, "photo-idle-002", StringComparison.OrdinalIgnoreCase));
            SelectMotion(idle >= 0 ? idle : 0);
            if (!_paused) TogglePause();
            EvaluateMotion(27.43889f);
            if (_characterRoot != null)
            {
                _characterRoot.transform.position = Vector3.zero;
                _characterRoot.transform.rotation = Quaternion.identity;
                _characterRoot.transform.localScale = Vector3.one;
            }
            ApplyCapturedGpaCamera();
            yield return null;
            ApplyCapturedPosedGeometry();
            for (int frame = 0; frame < 8; frame++) yield return null;

            string directory = Path.Combine(
                _stagingRoot ?? BundleCatalog.DefaultStagingRoot,
                "research", "gpa", "actor-cube-transform-basis-pass246", "sweep-1x");
            Directory.CreateDirectory(directory);
            for (int mode = 0; mode < 48; mode++)
            {
                Shader.SetGlobalFloat("_CapturedActorCubeTransformMode", mode);
                yield return null;
                string prefix = Path.Combine(directory,
                    string.Format("environment-mode-{0:D2}", mode));
                OriginalStyleRenderPipeline.RequestCurrentHdrDump(prefix);
                yield return new WaitForEndOfFrame();
                Debug.Log(string.Format(
                    "[PhotoMode] Actor cube transform sweep sample: mode={0}; prefix={1}",
                    mode, prefix));
            }
            yield return new WaitForSecondsRealtime(0.25f);
            Application.Quit(0);
        }

        private IEnumerator CaptureGpaCameraMaskSweepAndQuit()
        {
            _showUi = false;
            Debug.Log("[PhotoMode] Automated GPA-camera ActorData mask sweep requested");
            if (_storyPlayer != null && _storyPlayer.IsActive) _storyPlayer.StopStory();
            yield return null;
            ApplyCapturedGpaCamera();
            if (_faceExpression != null) _faceExpression.SetStoryGaze(0f, 0f);

            string directory = Path.Combine(
                _stagingRoot ?? BundleCatalog.DefaultStagingRoot,
                "research", "render-diagnostics", "gpa-camera-mask-sweep-pass94");
            Directory.CreateDirectory(directory);
            string[] labels = { "photo-idle-001", "photo-idle-002" };
            const int sampleCount = 60;
            foreach (string label in labels)
            {
                int motion = _motions.FindIndex(value =>
                    string.Equals(value.label, label, StringComparison.OrdinalIgnoreCase));
                if (motion < 0) continue;
                SelectMotion(motion);
                if (!_paused) TogglePause();
                AnimationClip clip = CurrentBodyClip;
                float duration = clip == null ? 1f : clip.length;
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    float time = duration * sample / sampleCount;
                    EvaluateMotion(time);
                    yield return null;
                    string prefix = Path.Combine(directory, string.Format(
                        "{0}-sample-{1:D3}-time-{2:000.000000}", label, sample, time));
                    RenderTexture target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32)
                    {
                        antiAliasing = 1
                    };
                    RenderTexture previousTarget = PreviewCamera.targetTexture;
                    OriginalStyleRenderPipeline.RequestActorDataPngDump(prefix);
                    PreviewCamera.targetTexture = target;
                    PreviewCamera.Render();
                    PreviewCamera.targetTexture = previousTarget;
                    DestroyImmediate(target);
                    Debug.Log(string.Format(
                        "[PhotoMode] GPA-camera mask sweep sample: motion={0} sample={1}/{2} time={3:0.000000} duration={4:0.000000} prefix={5}",
                        label, sample, sampleCount, time, duration, prefix));
                }
            }
            yield return new WaitForSecondsRealtime(0.25f);
            Application.Quit(0);
        }

        private void ApplyCapturedGpaCamera()
        {
            // VS 54E7B147BC883DC9 writes SV_POSITION from CB0[77..80].
            // Legacy pass91 approximation: its basis-transpose inverse loses the
            // captured projection offset. Use --captured-camera-state for an explicit
            // independently recovered input contract; retain the old fallback for
            // compatibility rather than silently changing older capture baselines.
            Vector3 actorLocalPosition = new Vector3(-0.003873869f, 1.50305927f, 1.44595370f);
            Vector3 actorLocalForward = new Vector3(0.000462058f, -0.12805113f, -0.99176746f);
            Vector3 actorLocalUp = new Vector3(0.000059637f, 0.99177005f, -0.12803192f);
            // Story playback enables Unity's physical camera. Its sensor gate then
            // changes the effective projection when SaveScreenshot switches from the
            // 1440x900 window to a 1920x1080 target, despite fieldOfView reporting the
            // same number. The GPA matrix factorization is a plain vertical-FOV
            // perspective contract, so disable the physical gate explicitly.
            PreviewCamera.usePhysicalProperties = false;
            // The recovered camera is relative to the captured Actor root (CB1's
            // world_from_actor), not the animated body prefab root.  Some idle clips
            // key the body root by roughly 18 degrees; following that transform made
            // the diagnostic camera nondeterministic across imported clip evaluation
            // and invalidated same-camera pixel comparisons.
            Transform actorRoot = _characterRoot == null ? null : _characterRoot.transform;
            Vector3 position = actorRoot == null
                ? actorLocalPosition
                : actorRoot.TransformPoint(actorLocalPosition);
            Vector3 forward = actorRoot == null
                ? actorLocalForward
                : actorRoot.TransformDirection(actorLocalForward);
            Vector3 up = actorRoot == null
                ? actorLocalUp
                : actorRoot.TransformDirection(actorLocalUp);
            _orbit.SetStoryPose(
                position, Quaternion.LookRotation(forward.normalized, up.normalized), 29.862842f);
        }

        private IEnumerator CaptureStoryAndQuit()
        {
            Debug.Log("[Story] Automated timeline capture requested");
            _showUi = false;
            if (_storyPlayer == null)
            {
                Debug.LogError("[Story] Timeline player unavailable");
                Application.Quit(2);
                yield break;
            }

            if (!_storyPlayer.IsPaused) _storyPlayer.TogglePause();
            bool capturePresented = Environment.GetCommandLineArgs().Contains("--capture-presented-window");
            bool traceFace = Environment.GetCommandLineArgs().Contains("--trace-story-face");
            bool traceFaceTransition = Environment.GetCommandLineArgs().Contains("--trace-story-face-transition");
            bool traceFaceOverride = Environment.GetCommandLineArgs().Contains("--trace-story-face-override");
            bool traceFaceOverrideSingle = Environment.GetCommandLineArgs().Contains("--trace-story-face-override-single");
            bool traceLookAt = Environment.GetCommandLineArgs().Contains("--trace-story-lookat");
            bool traceBackground = Environment.GetCommandLineArgs().Contains("--trace-story-background");
            bool traceActorColor = Environment.GetCommandLineArgs().Contains("--trace-story-actor-color");
            bool traceOverlay = Environment.GetCommandLineArgs().Contains("--trace-story-overlay");
            bool traceShake = Environment.GetCommandLineArgs().Contains("--trace-story-shake");
            bool traceDof = Environment.GetCommandLineArgs().Contains("--trace-story-dof");
            bool traceProps = Environment.GetCommandLineArgs().Contains("--trace-story-props");
            bool traceParaffin = Environment.GetCommandLineArgs().Contains("--trace-story-paraffin");
            bool classroomOnly = Environment.GetCommandLineArgs().Contains(
                "--capture-story-classroom-only");
            bool dumpClassroomPost = Environment.GetCommandLineArgs().Contains(
                "--dump-story-classroom-post");
            float classroomCaptureTime = 184.50f;
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                const string prefix = "--story-capture-time=";
                if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                float parsed;
                if (float.TryParse(argument.Substring(prefix.Length),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    classroomCaptureTime = Mathf.Max(0f, parsed);
            }

            float[] checkpoints = classroomOnly
                ? new[] { classroomCaptureTime }
                : traceFaceOverrideSingle
                ? new[] { 71.056895f }
                : traceFaceOverride
                ? new[]
                {
                    70.756895f, 70.831895f, 70.906895f, 70.981895f, 71.056895f,
                    107.115000f, 107.190000f, 107.265000f, 107.340000f, 107.415000f,
                    179.847667f, 179.877667f, 179.907667f, 179.937667f, 179.967667f,
                    179.981000f, 180.005167f, 180.029333f, 180.053500f, 180.077667f,
                }
                : traceFaceTransition
                ? new[] { 31.101333f, 31.156333f, 31.211333f, 31.266333f, 31.321333f }
                : traceLookAt
                ? new[]
                {
                    27.468000f, 27.668000f, 27.868000f,
                     28.068000f, 28.268000f, 34.375191f,
                 }
                : traceBackground
                ? new[]
                {
                    5.10f, 5.30f, 8.30f, 11.70f, 13.40f, 14.20f,
                    19.90f, 26.70f, 26.80f, 31.20f, 33.20f, 40.80f,
                    183.60f, 183.80f, 184.50f,
                }
                : traceActorColor
                ? new[] { 5.30f, 8.30f, 10.95f, 10.99f, 11.03f, 11.07f, 11.11f, 11.70f }
                : traceShake
                ? new[]
                {
                    40.727861f, 40.772861f, 40.817861f, 40.907861f,
                    40.932861f, 40.957861f, 41.002861f, 41.047861f,
                    41.137861f, 41.187861f, 41.277861f, 41.417861f,
                }
                : traceDof
                ? new[]
                {
                    13.95f, 14.20f, 19.80f, 19.90f, 26.70f, 26.80f,
                    183.70f, 183.80f, 184.50f, 185.10f,
                }
                : traceProps
                ? new[]
                {
                    10.95f, 19.90f, 26.70f, 26.80f,
                    86.30f, 86.38f, 99.50f, 99.58f,
                    103.28f, 103.36f, 156.72f, 156.80f,
                    169.72f, 169.80f, 183.58f, 183.68f,
                }
                : traceParaffin
                ? new[]
                {
                    86.30f, 86.38f, 99.50f, 99.58f,
                    103.28f, 103.36f, 156.72f, 156.80f,
                    169.72f, 169.80f, 183.58f, 183.68f,
                }
                : traceOverlay
                ? new[]
                {
                    0.00f, 0.40f, 0.82f, 4.40f, 4.67f, 4.94f,
                    5.08f, 5.21f, 5.34f, 5.58f, 6.35f, 7.10f,
                    7.34f, 7.58f, 7.84f,
                }
                : new[] { 11.9f, 27.35f, 28.10f, 44.45f, 77.52f, 88.20f, 181.10f };
            foreach (float checkpoint in checkpoints)
            {
                _storyPlayer.Seek(checkpoint, false);
                if ((traceBackground || traceParaffin) && _storyBackgroundRuntime != null)
                {
                    float deadline = Time.realtimeSinceStartup + 8f;
                    while (!_storyBackgroundRuntime.IsReady && Time.realtimeSinceStartup < deadline)
                        yield return null;
                    if (!_storyBackgroundRuntime.IsReady)
                        Debug.LogWarning(string.Format(
                            CultureInfo.InvariantCulture,
                            "[Story] Background load timed out at t={0:R}: id={1} source={2}",
                            checkpoint,
                            _storyBackgroundRuntime.ActiveId,
                            _storyBackgroundRuntime.ActiveSource));
                }
                yield return new WaitForSecondsRealtime(0.65f);
                if (dumpClassroomPost && Mathf.Abs(checkpoint - 184.50f) < 0.01f)
                {
                    string directory = Path.Combine(
                        _stagingRoot ?? BundleCatalog.DefaultStagingRoot, "captures");
                    OriginalStyleRenderPipeline.RequestPostInputDump(Path.Combine(
                        directory, "story-classroom-" +
                        DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-post"));
                    yield return null;
                }
                if (capturePresented) yield return new WaitForEndOfFrame();
                string path = capturePresented ? SavePresentedFrameScreenshot() : SaveScreenshot();
                Debug.Log(string.Format(
                    "[Story] Capture t={0:0.00} path={1} body={2} face={3} hairMean={4:0.00} hairMax={5:0.00} braidMean={6:0.00} braidMax={7:0.00} braidVariation={8:0.00} skirtMean={9:0.00} skirtMax={10:0.00} skirtChainCollisions={11} skirtChainLayers={12} skirtColliders={13} blink={14:0.00} gaze={15} faceActive={16} faceMax={17:0.000}",
                    checkpoint, path, StoryMotion, StoryFaceMotion,
                    _hairDynamics == null ? 0f : _hairDynamics.MeanAngularOffset,
                    _hairDynamics == null ? 0f : _hairDynamics.MaxAngularOffset,
                    _hairDynamics == null ? 0f : _hairDynamics.BraidMeanAngularOffset,
                    _hairDynamics == null ? 0f : _hairDynamics.BraidMaxAngularOffset,
                    _hairDynamics == null ? 0f : _hairDynamics.BraidAngularVariation,
                    _skirtDynamics == null ? 0f : _skirtDynamics.MeanAngularOffset,
                    _skirtDynamics == null ? 0f : _skirtDynamics.MaxAngularOffset,
                    _skirtDynamics == null ? 0 : _skirtDynamics.ChainCollisionCorrections,
                    _skirtDynamics == null ? 0 : _skirtDynamics.ChainLayerCount,
                    _skirtDynamics == null ? 0 : _skirtDynamics.StaticColliderCount,
                    _faceExpression == null ? 0f : _faceExpression.BlinkWeight,
                    _faceExpression == null ? Vector2.zero : _faceExpression.GazeAngles,
                    FaceActiveWeightCount, FaceMaximumWeight));
                if (traceFace && _faceExpression != null)
                    Debug.Log(string.Format(
                        "[Story] Face diagnostic t={0:0.00}: {1}",
                        checkpoint, _faceExpression.DiagnosticJson()));
                if (traceFaceTransition)
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[Story] Face transition driver t={0:R} weights={1}",
                        checkpoint, FaceDriverWeightsJson()));
                if (traceFaceOverride || traceFaceOverrideSingle)
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[Story] Face override runtime t={0:R} decals={1} weights={2}",
                        checkpoint,
                        _faceDecals == null ? "{}" : _faceDecals.DiagnosticJson(),
                        FaceDriverWeightsJson()));
                if (traceLookAt)
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[Story] LookTarget runtime t={0:R} contract={1}",
                        checkpoint,
                        _capturedLookAt == null
                            ? "{\"active\":false,\"reason\":\"unavailable\"}"
                            : _capturedLookAt.StoryDiagnosticJson()));
                if (traceBackground)
                {
                    OriginalStyleRenderPipeline postPipeline = PreviewCamera == null
                        ? null
                        : PreviewCamera.GetComponent<OriginalStyleRenderPipeline>();
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[Story] Background runtime t={0:R} id={1} source={2} clipping={3} actorProfile={4} postProfile={5}",
                        checkpoint,
                        _storyBackgroundRuntime == null ? "" : _storyBackgroundRuntime.ActiveId,
                        _storyBackgroundRuntime == null ? "" : _storyBackgroundRuntime.ActiveSource,
                        _storyBackgroundRuntime == null
                            ? "{\"exact\":false,\"reason\":\"runtime-unavailable\"}"
                            : _storyBackgroundRuntime.ClippingDiagnosticJson(),
                        StoryActorRenderProfileDiagnosticJson(),
                        postPipeline == null
                            ? "{\"active\":false,\"reason\":\"pipeline-unavailable\"}"
                            : postPipeline.StoryPostProcessProfileDiagnosticJson()));
                }
                if (traceActorColor)
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[Story] ActorColor runtime t={0:R} rgba=({1:R},{2:R},{3:R},{4:R})",
                        checkpoint,
                        _storyActorColor.r, _storyActorColor.g,
                        _storyActorColor.b, _storyActorColor.a));
                if (traceOverlay)
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[Story] Overlay runtime t={0:R} content={1:R} main={2:R} foreground={3:R} source={4} layout={5}",
                        checkpoint,
                        _storyOverlayRuntime == null ? 0f : _storyOverlayRuntime.ContentAlpha,
                        _storyOverlayRuntime == null ? 0f : _storyOverlayRuntime.MainAlpha,
                        _storyOverlayRuntime == null ? 0f : _storyOverlayRuntime.ForegroundAlpha,
                        _storyOverlayRuntime == null ? "" : _storyOverlayRuntime.ForegroundSource,
                        _storyOverlayRuntime == null
                            ? "{\"mode\":\"runtime-unavailable\"}"
                            : _storyOverlayRuntime.LayoutDiagnosticJson()));
                if (traceShake)
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[Story] Shake runtime t={0:R} position=({1:R},{2:R}) radius={3:R}",
                        checkpoint,
                        _storyShakePosition.x, _storyShakePosition.y,
                        _storyShakePosition.magnitude));
                if (traceDof)
                {
                    OriginalStyleRenderPipeline pipeline = PreviewCamera == null
                        ? null : PreviewCamera.GetComponent<OriginalStyleRenderPipeline>();
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[Story] DOF runtime t={0:R} focalLength={1:R} contract={2}",
                        checkpoint,
                        PreviewCamera == null ? 0f : PreviewCamera.focalLength,
                        pipeline == null
                            ? "{\"active\":false,\"reason\":\"pipeline-unavailable\"}"
                            : pipeline.DepthOfFieldDiagnosticJson()));
                }
                if (traceProps)
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[Story] Prop runtime t={0:R} contract={1}",
                        checkpoint, StoryPropDiagnosticJson()));
                if (traceParaffin)
                {
                    OriginalStyleRenderPipeline pipeline = PreviewCamera == null
                        ? null : PreviewCamera.GetComponent<OriginalStyleRenderPipeline>();
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[Story] Paraffin runtime t={0:R} background={1} contract={2}",
                        checkpoint,
                        _storyBackgroundRuntime == null ? "" : _storyBackgroundRuntime.ActiveSource,
                        pipeline == null
                            ? "{\"active\":false,\"reason\":\"pipeline-unavailable\"}"
                            : pipeline.ParaffinDiagnosticJson()));
                }
            }
            yield return new WaitForSecondsRealtime(0.25f);
            Application.Quit(0);
        }

        private string FaceDriverWeightsJson()
        {
            if (_faceDriver == null) return "{}";
            System.Text.StringBuilder builder = new System.Text.StringBuilder(512);
            builder.Append('{');
            bool first = true;
            for (int index = 0; index < 192; index++)
            {
                float value = _faceDriver.GetWeight(index);
                if (Mathf.Abs(value) <= 0.0000001f) continue;
                if (!first) builder.Append(',');
                first = false;
                builder.Append('"').Append(index).Append("\":");
                builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
            builder.Append('}');
            return builder.ToString();
        }

        private void DumpBakedActorMeshes(string directory)
        {
            if (_characterRoot == null) return;
            Directory.CreateDirectory(directory);
            Transform actor = _characterRoot.transform;
            Renderer[] renderers = _characterRoot.GetComponentsInChildren<Renderer>(true);
            List<string> records = new List<string>();
            int sequence = 0;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Mesh mesh = null;
                bool ownsMesh = false;
                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null && skinned.sharedMesh != null)
                {
                    mesh = new Mesh { name = renderer.name + "-baked-pass100" };
                    skinned.BakeMesh(mesh);
                    ownsMesh = true;
                }
                else
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null) mesh = filter.sharedMesh;
                }
                if (mesh == null) continue;
                Vector3[] vertices = mesh.vertices;
                string safeName = new string(renderer.name.Select(character =>
                    char.IsLetterOrDigit(character) || character == '-' || character == '_'
                        ? character : '_').ToArray());
                string fileName = string.Format("mesh-{0:D2}-{1}-{2}-vertices-f32.bin",
                    sequence, safeName, vertices.Length);
                using (BinaryWriter writer = new BinaryWriter(File.Open(
                    Path.Combine(directory, fileName), FileMode.Create, FileAccess.Write, FileShare.Read)))
                {
                    foreach (Vector3 vertex in vertices)
                    {
                        Vector3 actorLocal = actor.InverseTransformPoint(renderer.transform.TransformPoint(vertex));
                        writer.Write(actorLocal.x);
                        writer.Write(actorLocal.y);
                        writer.Write(actorLocal.z);
                    }
                }
                Vector4[] tangents = mesh.tangents;
                Vector2[] uv0 = mesh.uv;
                Vector2[] uv1 = mesh.uv2;
                Color[] colors = mesh.colors;
                string attributeFileName = string.Format(
                    "mesh-{0:D2}-{1}-{2}-attributes-f32.bin", sequence, safeName, vertices.Length);
                using (BinaryWriter writer = new BinaryWriter(File.Open(
                    Path.Combine(directory, attributeFileName), FileMode.Create, FileAccess.Write, FileShare.Read)))
                {
                    for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                    {
                        Vector4 tangent = vertexIndex < tangents.Length ? tangents[vertexIndex] : new Vector4(1f, 0f, 0f, 1f);
                        Vector3 actorTangent = actor.InverseTransformDirection(
                            renderer.transform.TransformDirection(new Vector3(tangent.x, tangent.y, tangent.z))).normalized;
                        Vector2 texcoord0 = vertexIndex < uv0.Length ? uv0[vertexIndex] : Vector2.zero;
                        Vector2 texcoord1 = vertexIndex < uv1.Length ? uv1[vertexIndex] : Vector2.zero;
                        Color color = vertexIndex < colors.Length ? colors[vertexIndex] : Color.white;
                        writer.Write(actorTangent.x); writer.Write(actorTangent.y); writer.Write(actorTangent.z); writer.Write(tangent.w);
                        writer.Write(texcoord0.x); writer.Write(texcoord0.y);
                        writer.Write(texcoord1.x); writer.Write(texcoord1.y);
                        writer.Write(color.r); writer.Write(color.g); writer.Write(color.b); writer.Write(color.a);
                    }
                }
                records.Add(string.Format(
                    "{{\"sequence\":{0},\"renderer\":\"{1}\",\"file\":\"{2}\",\"attribute_file\":\"{3}\",\"attribute_layout\":\"float32 actor_tangent.xyzw, uv0.xy, uv1.xy, color.rgba; 48 bytes/vertex\",\"vertex_count\":{4},\"submesh_count\":{5},\"shared_mesh\":\"{6}\"}}",
                    sequence, renderer.name.Replace("\\", "\\\\").Replace("\"", "\\\""), fileName, attributeFileName,
                    vertices.Length, mesh.subMeshCount,
                    mesh.name.Replace("\\", "\\\\").Replace("\"", "\\\"")));
                if (ownsMesh) DestroyImmediate(mesh);
                sequence++;
            }
            string manifest = "{\"schema\":\"digital-kotone.local-baked-actor-pose.v1\",\"coordinate_space\":\"character-root-local\",\"motion\":\"photo-idle-002\",\"time_seconds\":27.43889,\"meshes\":[" +
                string.Join(",", records.ToArray()) + "]}\n";
            File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest);
            Debug.Log(string.Format("[PhotoMode] Baked Actor pose dumped: {0}; renderers={1}", directory, records.Count));
        }

        private void DumpFaceDiagnostics(string label)
        {
            string diagnosticPass = Environment.GetCommandLineArgs().Contains("--face-skinning-diagnostic")
                ? "face-skinning-pass175" : "chin-pass174";
            string directory = Path.Combine(
                _stagingRoot ?? BundleCatalog.DefaultStagingRoot,
                "research", "render-diagnostics", diagnosticPass,
                label + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            DumpBakedActorMeshes(directory);
            if (_faceExpression != null)
                File.WriteAllText(Path.Combine(directory, "face-runtime-diagnostic.json"),
                    _faceExpression.DiagnosticJson() + "\n");
            Debug.Log("[PhotoMode] Face diagnostic dumped: " + directory);
        }

        private bool WriteCapturedCameraReport(CapturedCameraState.Session session)
        {
            try
            {
                string directory = Path.Combine(_stagingRoot ?? BundleCatalog.DefaultStagingRoot, "captures");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "camera-state-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
                File.WriteAllText(path, session.VerifiedJson());
                Debug.Log("[PhotoMode] Verified captured camera: " + path);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("[PhotoMode] Captured camera rejected before capture: " + exception.Message);
                return false;
            }
        }

        private bool WriteCapturedMaterialUvReport(CapturedMaterialUvState.Session session)
        {
            try
            {
                string json = session.VerifiedJson();
                string directory = Path.Combine(_stagingRoot ?? BundleCatalog.DefaultStagingRoot, "captures");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "material-uv-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
                File.WriteAllText(path, json + "\n");
                Debug.Log("[PhotoMode] Captured material UV verified: " + path);
                return true;
            }
            catch (Exception error)
            {
                Debug.LogError("[PhotoMode] Captured material UV verification failed: " + error.Message);
                return false;
            }
        }

        private HashSet<Renderer> ApplyCapturedPosedGeometry()
        {
            var capturedRenderers = new HashSet<Renderer>();
            if (_characterRoot == null) return capturedRenderers;
            string streamDirectory = Path.Combine(
                _stagingRoot ?? BundleCatalog.DefaultStagingRoot,
                "research", "gpa", "actor-input-assembler-pass99d", "analysis",
                "captured-actor-local-streams");
            Transform actor = _characterRoot.transform;
            Renderer[] renderers = _characterRoot.GetComponentsInChildren<Renderer>(true);
            int replaced = 0;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || renderer.gameObject.name.EndsWith("__captured-pose")) continue;
                Mesh source = null;
                bool wasSkinned = false;
                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null && skinned.sharedMesh != null)
                {
                    source = new Mesh { name = skinned.sharedMesh.name + "__captured-pose" };
                    skinned.BakeMesh(source);
                    wasSkinned = true;
                }
                else
                {
                    MeshFilter existingFilter = renderer.GetComponent<MeshFilter>();
                    if (existingFilter != null && existingFilter.sharedMesh != null)
                    {
                        source = Instantiate(existingFilter.sharedMesh);
                        source.name = existingFilter.sharedMesh.name + "__captured-pose";
                    }
                }
                if (source == null) continue;

                int vertexCount = source.vertexCount;
                string streamPath = Path.Combine(streamDirectory,
                    string.Format("captured-pose-{0}-attributes-v2-f32.bin", vertexCount));
                if (!File.Exists(streamPath))
                {
                    DestroyImmediate(source);
                    continue;
                }
                Vector3[] positions = new Vector3[vertexCount];
                Vector3[] normals = new Vector3[vertexCount];
                Vector4[] tangents = new Vector4[vertexCount];
                Color[] colors = new Color[vertexCount];
                using (BinaryReader reader = new BinaryReader(File.Open(
                    streamPath, FileMode.Open, FileAccess.Read, FileShare.Read)))
                {
                    for (int index = 0; index < vertexCount; index++)
                    {
                        Vector3 actorPosition = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        Vector3 actorNormal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        Vector4 actorTangent = new Vector4(
                            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        positions[index] = renderer.transform.InverseTransformPoint(actor.TransformPoint(actorPosition));
                        normals[index] = renderer.transform.InverseTransformDirection(actor.TransformDirection(actorNormal)).normalized;
                        Vector3 tangentDirection = renderer.transform.InverseTransformDirection(
                            actor.TransformDirection(new Vector3(actorTangent.x, actorTangent.y, actorTangent.z))).normalized;
                        tangents[index] = new Vector4(
                            tangentDirection.x, tangentDirection.y, tangentDirection.z, actorTangent.w);
                        // The D3D IA stream records backend texture coordinates. Unity's
                        // imported mesh/texture pair carries the matching platform origin
                        // conversion already; assigning these values back through Mesh.uv
                        // applies that conversion twice and exposes normally transparent
                        // hair-card margins. Consume them for stream alignment but retain
                        // the source mesh UVs until the backend convention is decoded.
                        reader.ReadSingle(); reader.ReadSingle();
                        reader.ReadSingle(); reader.ReadSingle();
                        colors[index] = new Color(
                            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    }
                }
                source.vertices = positions;
                if (!Environment.GetCommandLineArgs().Contains("--captured-pose-source-normals"))
                    source.normals = normals;
                source.tangents = tangents;
                source.colors = colors;
                source.RecalculateBounds();

                if (wasSkinned)
                {
                    GameObject target = renderer.gameObject;
                    MeshFilter filter = target.GetComponent<MeshFilter>();
                    if (filter == null) filter = target.AddComponent<MeshFilter>();
                    filter.sharedMesh = source;
                    MeshRenderer staticRenderer = target.AddComponent<MeshRenderer>();
                    staticRenderer.sharedMaterials = renderer.sharedMaterials;
                    staticRenderer.shadowCastingMode = renderer.shadowCastingMode;
                    staticRenderer.receiveShadows = renderer.receiveShadows;
                    staticRenderer.lightProbeUsage = renderer.lightProbeUsage;
                    staticRenderer.reflectionProbeUsage = renderer.reflectionProbeUsage;
                    staticRenderer.probeAnchor = renderer.probeAnchor;
                    staticRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                    renderer.enabled = false;
                    capturedRenderers.Add(staticRenderer);
                }
                else
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    filter.sharedMesh = source;
                    if (_faceExpression != null) _faceExpression.enabled = false;
                    capturedRenderers.Add(renderer);
                }
                replaced++;
            }
            // The diagnostic swaps skinned renderers for static ones. Explicit
            // outline/hair commands must bind those replacements as well.
            if (_actorRenderControls != null) _actorRenderControls.RefreshRenderers();
            Debug.Log(string.Format(
                "[PhotoMode] Exact captured posed geometry + tangent/color attributes applied (Unity-import UVs retained): renderers={0}; source={1}",
                replaced, streamDirectory));
            return capturedRenderers;
        }

        private void DrawLegacyGUI()
        {
            if (!_initialized) return;
            GUILayout.BeginArea(new Rect(16, 16, 330, 520), GUI.skin.box);
            GUILayout.Label("GAKUMAS PHOTO MODE — fktn");
            GUILayout.Label(_status);
            GUILayout.Space(8);
            GUILayout.Label("Costume");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(42))) SelectCostume(_costumeIndex - 1);
            GUILayout.Label(_costumes[_costumeIndex].label ?? _costumes[_costumeIndex].name);
            if (GUILayout.Button(">", GUILayout.Width(42))) SelectCostume(_costumeIndex + 1);
            GUILayout.EndHorizontal();
            GUILayout.Label("Motion");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(42))) SelectMotion(_motionIndex - 1);
            GUILayout.Label(_motions[_motionIndex].label ?? _motions[_motionIndex].name);
            if (GUILayout.Button(">", GUILayout.Width(42))) SelectMotion(_motionIndex + 1);
            GUILayout.EndHorizontal();
            if (GUILayout.Button(_paused ? "Resume" : "Pause")) TogglePause();
            GUILayout.Space(8);
            GUILayout.Label("Voice");
            if (_catalog.Manifest.voices != null)
            {
                foreach (VoiceRecord voice in _catalog.Manifest.voices)
                {
                    int index = Array.IndexOf(_catalog.Manifest.voices, voice);
                    if (GUILayout.Button(voice.label)) PlayVoice(index);
                }
            }
            GUILayout.Space(8);
            GUILayout.Label("Camera FOV: " + _orbit.fov.ToString("0"));
            _orbit.fov = GUILayout.HorizontalSlider(_orbit.fov, 15f, 70f);
            GUILayout.Label("Key light: " + _keyLight.intensity.ToString("0.00"));
            _keyLight.intensity = GUILayout.HorizontalSlider(_keyLight.intensity, 0f, 3f);
            if (GUILayout.Button("Save screenshot (P)")) SaveScreenshot();
            GUILayout.Space(8);
            GUILayout.Label("RMB orbit · MMB pan · wheel zoom");
            GUILayout.Label("A/D motion · W/S costume · Space pause");
            GUILayout.Label(string.Format("Renderers: {0} · repaired materials: {1}", RendererCount, ErrorMaterialCount));
            GUILayout.EndArea();
        }

        private void OnGUI()
        {
            if (!_initialized || !_showUi) return;
            EnsureGuiStyles();
            float scale = Mathf.Clamp(Screen.height / 900f, 0.82f, 1.22f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            GUILayout.BeginArea(new Rect(22, 22, 310, 824), _panelStyle);
            GUILayout.Label("GAKUMAS PHOTO STUDIO", _titleStyle);
            GUILayout.Label(_characterId.ToUpperInvariant() + "  /  BODY " +
                (CurrentOutfitOwner ?? "-").ToUpperInvariant(), _captionStyle);
            GUILayout.Label(CurrentRenderContext, _captionStyle);
            GUILayout.Space(10);
            GUILayout.Label(_status, _captionStyle);
            GUILayout.Space(12);
            if (StoryActive)
            {
                DrawStoryGUI();
                GUILayout.EndArea();
                GUI.matrix = Matrix4x4.identity;
                return;
            }
            GUILayout.Label("CHARACTER", _sectionStyle);
            GUILayout.BeginHorizontal();
            int characterIndex = _characterIds.FindIndex(value => string.Equals(value, _characterId, StringComparison.OrdinalIgnoreCase));
            if (GUILayout.Button("<", _buttonStyle, GUILayout.Width(42), GUILayout.Height(30))) SelectCharacter(characterIndex - 1);
            GUILayout.Label(_characterId.ToUpperInvariant(), _captionStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", _buttonStyle, GUILayout.Width(42), GUILayout.Height(30))) SelectCharacter(characterIndex + 1);
            GUILayout.EndHorizontal();
            GUILayout.Space(9);
            GUILayout.Label("OUTFIT BODY  /  OWNER", _sectionStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", _buttonStyle, GUILayout.Width(42), GUILayout.Height(30))) SelectCostume(_costumeIndex - 1);
            GUILayout.Label((_costumes[_costumeIndex].label ?? _costumes[_costumeIndex].name) +
                " / " + (CurrentOutfitOwner ?? "-").ToUpperInvariant(), _captionStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", _buttonStyle, GUILayout.Width(42), GUILayout.Height(30))) SelectCostume(_costumeIndex + 1);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("FKTN <- HMSZ SOLO1", _buttonStyle, GUILayout.Height(28)))
                SelectSolo1Swap("fktn", "hmsz");
            if (GUILayout.Button("HMSZ <- FKTN SOLO1", _buttonStyle, GUILayout.Height(28)))
                SelectSolo1Swap("hmsz", "fktn");
            GUILayout.EndHorizontal();
            GUILayout.Space(9);
            GUILayout.Label("MOTION", _sectionStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", _buttonStyle, GUILayout.Width(42), GUILayout.Height(30))) SelectMotion(_motionIndex - 1);
            GUILayout.Label(_motions[_motionIndex].label ?? _motions[_motionIndex].name, _captionStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", _buttonStyle, GUILayout.Width(42), GUILayout.Height(30))) SelectMotion(_motionIndex + 1);
            GUILayout.EndHorizontal();
            if (GUILayout.Button(_paused ? "RESUME" : "PAUSE", _buttonStyle, GUILayout.Height(30))) TogglePause();
            GUILayout.Space(9);
            GUILayout.Label("EXPRESSION", _sectionStyle);
            if (_faceExpression != null)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("<", _buttonStyle, GUILayout.Width(42), GUILayout.Height(30))) SelectExpression(_expressionIndex - 1);
                GUILayout.Label(CurrentExpression, _captionStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(">", _buttonStyle, GUILayout.Width(42), GUILayout.Height(30))) SelectExpression(_expressionIndex + 1);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(9);
            GUILayout.Label("LOOK AT", _sectionStyle);
            if (_capturedLookAt != null)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("NATURAL", _buttonStyle, GUILayout.Height(28)))
                    _capturedLookAt.SetMode(CapturedLookAtMode.Natural);
                if (GUILayout.Button("GAZE", _buttonStyle, GUILayout.Height(28)))
                    _capturedLookAt.SetMode(CapturedLookAtMode.Gaze);
                if (GUILayout.Button("COCCHI", _buttonStyle, GUILayout.Height(28)))
                    _capturedLookAt.SetMode(CapturedLookAtMode.Cocchi);
                GUILayout.EndHorizontal();
                GUILayout.Label(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}  EYE {1:0.00}  ANGLE {2:0.0}",
                    _capturedLookAt.Mode,
                    _capturedLookAt.ControllerEyeWeight,
                    _capturedLookAt.VirtualAngle), _captionStyle);
            }
            GUILayout.Space(9);
            GUILayout.Label("VOICE", _sectionStyle);
            if (_catalog.Manifest.voices != null)
            {
                foreach (VoiceRecord voice in _catalog.Manifest.voices)
                {
                    int index = Array.IndexOf(_catalog.Manifest.voices, voice);
                    if (GUILayout.Button(voice.label, _buttonStyle, GUILayout.Height(27))) PlayVoice(index);
                }
            }
            GUILayout.Space(10);
            GUILayout.Label("LENS  " + _orbit.fov.ToString("0") + " mm", _sectionStyle);
            _orbit.fov = GUILayout.HorizontalSlider(_orbit.fov, 15f, 70f);
            GUILayout.Label("KEY LIGHT  " + _keyLight.intensity.ToString("0.00"), _sectionStyle);
            _keyLight.intensity = GUILayout.HorizontalSlider(_keyLight.intensity, 0.25f, 1.5f);
            if (_hairDynamics != null)
            {
                GUILayout.Label("HAIR DYNAMICS  " + _hairDynamics.strength.ToString("0.00"), _sectionStyle);
                _hairDynamics.strength = GUILayout.HorizontalSlider(_hairDynamics.strength, 0f, 1.5f);
                GUILayout.Label("HAIR WIND  " + _hairDynamics.windStrength.ToString("0.00"), _sectionStyle);
                _hairDynamics.windStrength = GUILayout.HorizontalSlider(_hairDynamics.windStrength, 0f, 1.2f);
            }
            if (_skirtDynamics != null)
            {
                GUILayout.Label("SKIRT DYNAMICS  " + _skirtDynamics.strength.ToString("0.00"), _sectionStyle);
                _skirtDynamics.strength = GUILayout.HorizontalSlider(_skirtDynamics.strength, 0f, 1.5f);
            }
            if (_garmentDynamics != null && _garmentDynamics.SimulatedBoneCount > 0)
            {
                GUILayout.Label("GARMENT SWING  " + _garmentDynamics.strength.ToString("0.00"), _sectionStyle);
                _garmentDynamics.strength = GUILayout.HorizontalSlider(_garmentDynamics.strength, 0f, 1.0f);
            }
            if (_bodySoftTissueDynamics != null && _bodySoftTissueDynamics.SimulatedBoneCount > 0)
            {
                GUILayout.Label("BODY SOFT TISSUE  " + _bodySoftTissueDynamics.strength.ToString("0.00"), _sectionStyle);
                _bodySoftTissueDynamics.strength = GUILayout.HorizontalSlider(_bodySoftTissueDynamics.strength, 0f, 1.5f);
                GUILayout.Label(string.Format("{0} chains / {1:0.00} mm / rms {2:0.00}",
                    _bodySoftTissueDynamics.SimulatedBoneCount,
                    _bodySoftTissueDynamics.MeanDisplacementMillimeters,
                    _bodySoftTissueDynamics.ObservedRmsDisplacementMillimeters), _captionStyle);
            }
            GUILayout.Space(10);
            if (GUILayout.Button("CAPTURE  [ P ]", _buttonStyle, GUILayout.Height(36))) SaveScreenshot();
            if (GUILayout.Button("RESET CAMERA  [ R ]", _buttonStyle, GUILayout.Height(28))) _orbit.ResetPose();
            string backgroundLabel = _useAdvPhotoBackground ? "ADV CITY" :
                _useRiverbedBackground ? "ORIGINAL RIVERBED" : "STUDIO";
            if (GUILayout.Button("BACKGROUND: " + backgroundLabel + "  [ B ]",
                    _buttonStyle, GUILayout.Height(28))) TogglePhotoBackground();
            GUILayout.Space(10);
            GUILayout.Label("RMB orbit   MMB pan   Wheel zoom", _captionStyle);
            GUILayout.Label("A/D motion   W/S outfit   Space pause", _captionStyle);
            GUILayout.Label(_riverbedEnvironment != null && _riverbedEnvironment.IsLoaded
                ? "B toggle original riverbed / studio background"
                : "B background toggle (original riverbed unavailable)", _captionStyle);
            GUILayout.Label("F1 / Tab hide controls", _captionStyle);
            GUILayout.Label(string.Format("{0} renderers / {1} mats / {2} hair / {3} face",
                RendererCount, ErrorMaterialCount, HairDynamicBoneCount, FaceShapeCount), _captionStyle);
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }

        private void DrawStoryGUI()
        {
            GUILayout.Label("ORIGINAL STORY", _sectionStyle);
            GUILayout.Label(string.Format("adv_dear_fktn_001   {0:0.00} / {1:0.00}s", StoryTime, StoryDuration), _captionStyle);
            float seek = GUILayout.HorizontalSlider(StoryTime, 0f, Mathf.Max(0.01f, StoryDuration));
            if (Mathf.Abs(seek - StoryTime) > 0.08f) _storyPlayer.Seek(seek, false);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_storyPlayer.IsPaused ? "RESUME" : "PAUSE", _buttonStyle, GUILayout.Height(30))) TogglePause();
            if (GUILayout.Button("RESTART", _buttonStyle, GUILayout.Height(30))) _storyPlayer.Seek(0f, false);
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
            GUILayout.Label("BODY MOTION", _sectionStyle);
            GUILayout.Label(ShortAssetName(StoryMotion), _captionStyle);
            GUILayout.Label("FACIAL MOTION", _sectionStyle);
            GUILayout.Label(ShortAssetName(StoryFaceMotion), _captionStyle);
            GUILayout.Space(8);
            GUILayout.Label("DIALOGUE", _sectionStyle);
            GUIStyle messageStyle = new GUIStyle(_captionStyle) { wordWrap = true };
            GUILayout.Label(string.IsNullOrEmpty(StoryMessage) ? "..." : StoryMessage, messageStyle, GUILayout.MinHeight(64));
            GUILayout.Space(8);
            if (_hairDynamics != null)
            {
                GUILayout.Label("HAIR DYNAMICS  " + _hairDynamics.strength.ToString("0.00"), _sectionStyle);
                _hairDynamics.strength = GUILayout.HorizontalSlider(_hairDynamics.strength, 0f, 1.5f);
                GUILayout.Label(string.Format("all {0:0.00}° / braid {1:0.00}° ±{2:0.00}°",
                    _hairDynamics.MeanAngularOffset, _hairDynamics.BraidMeanAngularOffset, _hairDynamics.BraidAngularVariation), _captionStyle);
                GUILayout.Label("HAIR WIND  " + _hairDynamics.windStrength.ToString("0.00"), _sectionStyle);
                _hairDynamics.windStrength = GUILayout.HorizontalSlider(_hairDynamics.windStrength, 0f, 1.2f);
            }
            if (_skirtDynamics != null)
            {
                GUILayout.Label("SKIRT DYNAMICS  " + _skirtDynamics.strength.ToString("0.00"), _sectionStyle);
                _skirtDynamics.strength = GUILayout.HorizontalSlider(_skirtDynamics.strength, 0f, 1.5f);
                GUILayout.Label(string.Format("{0} edges / {1} colliders / {2:0.00} deg / {3} chain hits",
                    _skirtDynamics.SimulatedBoneCount, _skirtDynamics.StaticColliderCount,
                    _skirtDynamics.MeanAngularOffset, _skirtDynamics.ChainCollisionCorrections), _captionStyle);
            }
            GUILayout.Space(12);
            if (GUILayout.Button("CAPTURE  [ P ]", _buttonStyle, GUILayout.Height(36))) SaveScreenshot();
            if (GUILayout.Button("RESET VIEW  [ R ]", _buttonStyle, GUILayout.Height(28))) _orbit.ResetStoryOffsets();
            if (GUILayout.Button("PHOTO MODE", _buttonStyle, GUILayout.Height(28))) _storyPlayer.StopStory();
            GUILayout.Space(10);
            GUILayout.Label("Timeline drives body / face / voice / camera", _captionStyle);
            GUILayout.Label("RMB orbit   MMB pan   Wheel zoom", _captionStyle);
            GUILayout.Label("R reset view   Space pause", _captionStyle);
            GUILayout.Label(string.Format("{0} hair / {1} skirt / {2} face shapes / {3} active", HairDynamicBoneCount, ClothDynamicBoneCount, FaceShapeCount, FaceActiveWeightCount), _captionStyle);
        }

        private static string ShortAssetName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "waiting for event";
            return value.Length <= 38 ? value : "..." + value.Substring(value.Length - 35);
        }

        private void EnsureGuiStyles()
        {
            if (_panelStyle != null) return;
            _panelTexture = SolidTexture(new Color(0.045f, 0.055f, 0.10f, 0.91f));
            _buttonTexture = SolidTexture(new Color(0.16f, 0.20f, 0.32f, 0.95f));
            _panelStyle = new GUIStyle(GUI.skin.box);
            _panelStyle.padding = new RectOffset(18, 18, 17, 17);
            _panelStyle.normal.background = _panelTexture;
            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.fontSize = 20;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.alignment = TextAnchor.MiddleLeft;
            _titleStyle.normal.textColor = new Color(0.98f, 0.91f, 0.52f);
            _sectionStyle = new GUIStyle(GUI.skin.label);
            _sectionStyle.fontSize = 11;
            _sectionStyle.fontStyle = FontStyle.Bold;
            _sectionStyle.normal.textColor = new Color(0.54f, 0.90f, 0.96f);
            _captionStyle = new GUIStyle(GUI.skin.label);
            _captionStyle.fontSize = 11;
            _captionStyle.alignment = TextAnchor.MiddleLeft;
            _captionStyle.wordWrap = false;
            _captionStyle.normal.textColor = new Color(0.84f, 0.86f, 0.94f);
            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.fontSize = 11;
            _buttonStyle.fontStyle = FontStyle.Bold;
            _buttonStyle.normal.background = _buttonTexture;
            _buttonStyle.normal.textColor = Color.white;
            _buttonStyle.hover.textColor = new Color(0.98f, 0.91f, 0.52f);
            _buttonStyle.active.textColor = new Color(0.54f, 0.90f, 0.96f);
        }

        private static Texture2D SolidTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        public void Initialize(string stagingRoot)
        {
            RuntimeArguments = Environment.GetCommandLineArgs();
            InitializeRuntime(stagingRoot);
        }

        protected override void ConfigureApplication()
        {
            Application.targetFrameRate = _ambientWallpaperDemo ? 30 : 60;
            Application.runInBackground = true;
            QualitySettings.antiAliasing = 8;
            // Respect each texture's sampling contract. ForceEnable upgrades
            // bilinear base maps to anisotropic samplers, changing thin details
            // even when their pixels, UVs and authored filter settings match.
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
            string[] qualityCommandLine = Environment.GetCommandLineArgs();
            QualitySettings.shadowDistance = qualityCommandLine.Contains("--shadow-distance-6") ? 6f :
                                             qualityCommandLine.Contains("--shadow-distance-8") ? 8f :
                                             qualityCommandLine.Contains("--shadow-distance-12") ? 12f : 20f;
            QualitySettings.shadowProjection = qualityCommandLine.Contains("--close-fit-shadows")
                ? UnityEngine.ShadowProjection.CloseFit
                : UnityEngine.ShadowProjection.StableFit;
            QualitySettings.shadowResolution = UnityEngine.ShadowResolution.VeryHigh;
            // Local RenderDoc pass83 proved Unity was replaying the exact five
            // reconstructed caster submeshes three times into a 4096² atlas—one
            // set per active cascade. The original GPA pass contains the same
            // five index counts only once and its receiver VS carries one direct
            // orthographic world-to-shadow matrix, so production must be a
            // single-cascade shadow rather than Unity's generic cascade stack.
            QualitySettings.shadowCascades = 1;
            // Unity applies -screen-width/-screen-height before this component
            // initializes.  Do not overwrite an explicit capture aspect with
            // the interactive 1440x900 floor: Screen.SetResolution is deferred,
            // so the old code produced one correctly-sized frame and then
            // silently switched a portrait capture back to landscape while an
            // additive ADV scene was loading.
            bool explicitScreenSize = qualityCommandLine.Contains("-screen-width") ||
                                      qualityCommandLine.Contains("-screen-height");
            if (Application.isPlaying && !explicitScreenSize)
            {
                Screen.fullScreenMode = FullScreenMode.Windowed;
                if (Screen.width < 1200 || Screen.height < 720) Screen.SetResolution(1440, 900, false);
            }
        }

        protected override void ProcessApplicationInput()
        {
            if (!StoryActive && Input.GetKeyDown(KeyCode.A)) SelectMotion(_motionIndex - 1);
            if (!StoryActive && Input.GetKeyDown(KeyCode.D)) SelectMotion(_motionIndex + 1);
            if (!StoryActive && Input.GetKeyDown(KeyCode.W)) SelectCostume(_costumeIndex - 1);
            if (!StoryActive && Input.GetKeyDown(KeyCode.S)) SelectCostume(_costumeIndex + 1);
            if (!StoryActive && Input.GetKeyDown(KeyCode.Q))
                SelectCharacter(_characterIds.FindIndex(value => string.Equals(value, _characterId, StringComparison.OrdinalIgnoreCase)) - 1);
            if (!StoryActive && Input.GetKeyDown(KeyCode.E))
                SelectCharacter(_characterIds.FindIndex(value => string.Equals(value, _characterId, StringComparison.OrdinalIgnoreCase)) + 1);
            if (!StoryActive && Input.GetKeyDown(KeyCode.B)) TogglePhotoBackground();
            if (!StoryActive && Input.GetKeyDown(KeyCode.L) && _capturedLookAt != null)
                _capturedLookAt.CycleMode();
            if (_ambientWallpaperDemo) UpdateAmbientWallpaperDemo();
            if (Input.GetKeyDown(KeyCode.Space)) TogglePause();
            if (Input.GetKeyDown(KeyCode.P)) SaveScreenshot();
            if (Input.GetKeyDown(KeyCode.F1) || Input.GetKeyDown(KeyCode.Tab)) _showUi = !_showUi;
            if (Input.GetKeyDown(KeyCode.R))
            {
                if (StoryActive) _orbit.ResetStoryOffsets();
                else _orbit.ResetPose();
            }
        }

        protected override ActorRenderControls CreateRenderControls(Camera camera)
        {
            return camera.gameObject.AddComponent<PhotoActorRenderControls>();
        }

        protected override void AttachApplicationCameraComponents(Camera camera)
        {
            ActorRenderingValidation.AttachIfRequested(camera.gameObject);
        }
    }
}
