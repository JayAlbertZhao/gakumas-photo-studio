using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit desktop frame orchestration. Record in a valid SRP context,
    /// submit it in the host, then finish effects. No pipeline switch, scene discovery,
    /// implicit Submit, presentation or asset loading. Retire only after GPU completion.</summary>
    public sealed class DesktopFrameRenderer : IDisposable
    {
        public sealed class Settings
        {
            public bool enabled;
            public readonly TileSceneRenderer.Settings scene = new TileSceneRenderer.Settings();
            public readonly ActorForwardDrawSet.Settings actors = new ActorForwardDrawSet.Settings();
            // Opt-in destructive scene attachment reuse; defaults preserve scene inputs.
            public SrpActorForward.Storage actorStorage;
            public readonly SceneMotionSettings actorMotion=new SceneMotionSettings();
            public uint actorMotionRevision;
            public int actorMotionMaximumMiB=256;
            public bool allowImmutableUnreadableMotionMeshes;
            public bool includeSceneMotion,reuseSceneMotionStorage;
            public int sceneMotionMaximumMiB=128;
            public readonly FrameTemporalAntialiasing.Settings temporal=new FrameTemporalAntialiasing.Settings();
            public int temporalMaximumMiB=256;
            public readonly SceneDirectionalShadowSettings selfShadow = new SceneDirectionalShadowSettings();
            public Vector3 selfShadowDirection = new Vector3(.6f, 1, .7f);
            public readonly SrpTilePlanarReflection.Settings planar = new SrpTilePlanarReflection.Settings();
            public readonly SrpTileReflection.Settings reflections = new SrpTileReflection.Settings();
            public readonly HeavyFxSettings effects = new HeavyFxSettings();
            public readonly BokehDepthOfFieldSettings depthOfField = new BokehDepthOfFieldSettings();
            public readonly MotionBlurSettings motionBlur=new MotionBlurSettings();
            public int motionBlurMaximumMiB=256;
            // Applied raster jitter, also used with temporal disabled.
            public Vector2 motionBlurJitterUv;
            // Caller-owned immutable LUT, applied after HDR effects and before UI.
            public ColorGradingLut colorGrade;
        }

        public readonly struct OpaqueFrame
        {
            internal readonly DesktopFrameRenderer owner;
            public readonly ulong sequence;
            public readonly SrpActorForward.Frame actors;
            public readonly SrpActorShadow.Frame? selfShadow;
            public readonly SrpTileReflection.Frame? reflections;
            public readonly SrpTilePlanarReflection.Frame? planar;
            public TileSceneRenderer.PreparedFrame Scene => IsCurrent ? owner.scene : null;
            public bool IsCurrent => owner != null && owner.CurrentOpaque(sequence);
            internal OpaqueFrame(DesktopFrameRenderer value)
            {
                owner = value; sequence = value.sequence; actors = value.actorFrame;
                selfShadow = value.shadowFrame; reflections = value.reflectionFrame; planar = value.planarFrame;
            }
        }

        public readonly struct Frame
        {
            private readonly DesktopFrameRenderer owner;
            public readonly ulong sequence;
            public readonly RenderTexture color, eyeDepth;
            public readonly OpaqueFrame opaque;
            public readonly FrameTemporalAntialiasing.Frame? temporal;
            public readonly FrameMotionBlur.Frame? motionBlur;
            public bool IsCurrent => owner != null && owner.CurrentFinal(sequence);
            internal Frame(DesktopFrameRenderer value)
            {
                owner = value; sequence = value.sequence; color = value.finalColor;
                eyeDepth = value.actorFrame.eyeDepth; opaque = new OpaqueFrame(value);
                temporal=value.temporalFrame;
                motionBlur=value.motionBlurFrame;
            }
        }

        public Camera Camera { get; }
        public Settings Configuration { get; }
        /// <summary>Includes failed attempts that may have recorded GPU work. This
        /// does not indicate GPU completion. Submit/complete before retiring.</summary>
        public bool HasPendingWork => phase != Phase.Idle && !disposed;
        public bool UsedReflectionHistory => reflection.UsedHistory;
        /// <summary>Owned Actor targets only; excludes borrowed scene storage and
        /// driver overhead. A nominal allocation count, not measured VRAM or traffic.</summary>
        public long ActorNominalTextureBytes => actor.NominalTextureBytes;
        public long TemporalNominalTextureBytes => temporal.NominalTextureBytes;
        public long MotionBlurNominalTextureBytes=>motionBlur.NominalTextureBytes;
        public long MotionNominalTextureBytes=>actor.MotionNominalTextureBytes+actor.SceneMotionNominalTextureBytes;
        private enum Phase { Idle, Recording, Opaque, Finishing, Complete, Failed }
        private Phase phase;
        private readonly SrpActorShadow shadow = new SrpActorShadow();
        private readonly SrpActorForward actor;
        private readonly SrpTileReflection reflection;
        private readonly SrpTilePlanarReflection planar;
        private readonly HeavyFxRenderer effects = new HeavyFxRenderer();
        private readonly FrameTemporalAntialiasing temporal=new FrameTemporalAntialiasing();
        private readonly BokehDepthOfFieldRenderer dof = new BokehDepthOfFieldRenderer();
        private readonly FrameMotionBlur motionBlur=new FrameMotionBlur();
        private readonly ColorGradingRenderer grade = new ColorGradingRenderer();
        private TileSceneRenderer.PreparedFrame scene;
        private ActorForwardDrawSet.PreparedFrame draws;
        private SrpActorForward.Frame actorFrame;
        private SrpActorShadow.Frame? shadowFrame;
        private SrpTileReflection.Frame? reflectionFrame;
        private SrpTilePlanarReflection.Frame? planarFrame;
        private HeavyFxRenderer.Frame? effectFrame;
        private FrameTemporalAntialiasing.Frame? temporalFrame;
        private BokehDepthOfFieldRenderer.Frame? dofFrame;
        private FrameMotionBlur.Frame? motionBlurFrame;
        private ColorGradingRenderer.Frame? gradeFrame;
        private RenderTexture finalColor;
        private ulong sequence;
        private bool disposed;

        public DesktopFrameRenderer(Camera camera, Settings settings)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            Camera = camera; Configuration = settings ?? throw new ArgumentNullException(nameof(settings));
            actor = new SrpActorForward(camera, new SrpActorForward.Settings { enabled = true });
            reflection = new SrpTileReflection(camera, settings.reflections);
            planar = new SrpTilePlanarReflection(camera, settings.planar);
        }

        private bool CurrentOpaque(ulong value) => !disposed && Configuration.enabled && value == sequence &&
            (phase == Phase.Opaque || phase == Phase.Finishing || phase == Phase.Complete) &&
            scene != null && scene.IsRecorded && actorFrame.IsCurrent &&
            (!shadowFrame.HasValue || shadowFrame.Value.IsCurrent) &&
            (!reflectionFrame.HasValue || reflectionFrame.Value.IsCurrent) &&
            (!planarFrame.HasValue || planarFrame.Value.IsCurrent);
        private bool CurrentFinal(ulong value) => phase == Phase.Complete && CurrentOpaque(value) &&
            finalColor != null && finalColor.IsCreated() &&
            (!effectFrame.HasValue || effectFrame.Value.IsCurrent) &&
            (!temporalFrame.HasValue || temporalFrame.Value.IsCurrent) &&
            (!dofFrame.HasValue || dofFrame.Value.IsCurrent) &&
            (!motionBlurFrame.HasValue || motionBlurFrame.Value.IsCurrent) &&
            (!gradeFrame.HasValue || gradeFrame.Value.IsCurrent);

        public bool TryRecord(ScriptableRenderContext context, ulong value, ulong sceneRevision,
            out OpaqueFrame frame, out string error)
        {
            frame = default; error = Validate(value);
            if (error != null) return false;
            phase = Phase.Recording; sequence = value;
            try
            {
                var s = Configuration;
                actor.Configuration.storage = s.actorStorage;
                actor.Configuration.motion.enabled=s.actorMotion.enabled;
                actor.Configuration.motion.maximumTrackedVertices=s.actorMotion.maximumTrackedVertices;
                actor.Configuration.motion.cameraCutDistance=s.actorMotion.cameraCutDistance;
                actor.Configuration.motion.cameraCutAngle=s.actorMotion.cameraCutAngle;
                actor.Configuration.motionRevision=s.actorMotionRevision;
                actor.Configuration.motionMaximumMiB=s.actorMotionMaximumMiB;
                actor.Configuration.allowImmutableUnreadableMotionMeshes=s.allowImmutableUnreadableMotionMeshes;
                actor.Configuration.includeSceneMotion=s.includeSceneMotion;actor.Configuration.reuseSceneMotionStorage=s.reuseSceneMotionStorage;
                actor.Configuration.sceneMotionMaximumMiB=s.sceneMotionMaximumMiB;
                if (!TileSceneRenderer.TryPrepare(Camera, s.scene, out scene, out error)) return Fail(error);
                if (s.selfShadow.enabled)
                {
                    if (!shadow.TryRecord(context, s.selfShadowDirection, s.selfShadow, value, out var current, out error)) return Fail(error);
                    shadowFrame = current;
                }
                // Keep caller settings untouched, including its optional external ticket.
                var a = s.actors;
                var settings = new ActorForwardDrawSet.Settings {
                    renderers = a.renderers, parameters = a.parameters, outlines = a.outlines,
                    hairCover = a.hairCover, maximumDraws = a.maximumDraws,
                    configureMaterial = a.configureMaterial, selfShadow = shadowFrame ?? a.selfShadow,temporalFlags=a.temporalFlags,
                    excludeMotionBlur=a.excludeMotionBlur
                };
                if (!ActorForwardDrawSet.TryPrepare(Camera, settings, out draws, out error)) return Fail(error);
                if (!scene.TryRecord(context, out _, out error)) return Fail(error);
                if (s.planar.enabled)
                {
                    if (!planar.TryRecord(context, scene, value, out var current, out error)) return Fail(error);
                    planarFrame = current;
                }
                if (s.reflections.enabled)
                {
                    SrpTileReflection.Frame current;
                    bool ok = planarFrame.HasValue
                        ? reflection.TryRecord(context, scene, value, sceneRevision, planarFrame.Value, out current, out error)
                        : reflection.TryRecord(context, scene, value, sceneRevision, out current, out error);
                    if (!ok) return Fail(error);
                    reflectionFrame = current;
                }
                bool drawn = reflectionFrame.HasValue
                    ? actor.TryRecord(context, scene, draws, value, reflectionFrame.Value, out actorFrame, out error)
                    : actor.TryRecord(context, scene, draws, value, out actorFrame, out error);
                if (!drawn) return Fail(error);
                phase = Phase.Opaque; frame = new OpaqueFrame(this); return true;
            }
            catch (Exception exception) { error = "Desktop frame recording failed: " + exception.Message; return Fail(error); }
        }

        /// <summary>Call only after the host has submitted the matching context.
        /// Immediate FX, opt-in temporal resolve, DOF and grading commands follow
        /// queued opaque work. Scene reflection history is independent. No UI is drawn.</summary>
        public bool TryFinishAfterSubmission(OpaqueFrame input, double timeSeconds, out Frame frame, out string error)
        {
            frame = default; error = null;
            if (disposed || phase != Phase.Opaque || !ReferenceEquals(input.owner, this) || !input.IsCurrent ||
                double.IsNaN(timeSeconds) || double.IsInfinity(timeSeconds))
            { error = "Requires this host's current opaque frame after submission and finite explicit time"; return false; }
            phase = Phase.Finishing;
            try
            {
                var s = Configuration; finalColor = actorFrame.color;
                if (s.effects.enabled)
                {
                    if (effects.TryRender(finalColor, new FogVolumeDepth(actorFrame.eyeDepth), Camera, s.effects, timeSeconds, out var current))
                    { effectFrame = current; finalColor = current.color; }
                    else if (effects.UnavailableReason != null) { error = effects.UnavailableReason; return Fail(error); }
                    // An enabled but empty effects list is an explicit no-op.
                }
                var preTemporalColor=finalColor;
                if(s.temporal.enabled)
                {
                    if(!temporal.TryRender(actorFrame,finalColor,s.temporal,s.temporalMaximumMiB,out var current,out error))return Fail(error);
                    temporalFrame=current;finalColor=current.color;
                }
                else temporal.ResetHistory();
                if (s.depthOfField.enabled)
                {
                    if (!dof.TryRender(finalColor, actorFrame.eyeDepth, s.depthOfField, out var current))
                    { error = dof.UnavailableReason; return Fail(error); }
                    dofFrame = current; finalColor = current.color;
                }
                if(s.motionBlur.enabled)
                {
                    var jitter=s.temporal.enabled?s.temporal.jitterUv:s.motionBlurJitterUv;
                    if(!motionBlur.TryRender(actorFrame,finalColor,preTemporalColor,s.motionBlur,timeSeconds,jitter,
                        temporalFrame.HasValue,s.motionBlurMaximumMiB,out var current,out error))return Fail(error);
                    motionBlurFrame=current;finalColor=current.color;
                }
                else motionBlur.ResetHistory();
                if (s.colorGrade != null)
                {
                    if (!grade.TryRender(finalColor, s.colorGrade, out var current))
                    { error = grade.UnavailableReason; return Fail(error); }
                    gradeFrame = current; finalColor = current.color;
                }
                phase = Phase.Complete; frame = new Frame(this); return true;
            }
            catch (Exception exception) { error = "Desktop frame effects failed: " + exception.Message; return Fail(error); }
        }

        private string Validate(ulong value)
        {
            if (disposed || !Configuration.enabled || Camera == null || GraphicsSettings.currentRenderPipeline == null)
                return "Requires an enabled desktop frame host in an explicit SRP";
            if (phase != Phase.Idle) return "Retire the previous attempt after GPU completion before reuse";
            if (value == 0 || value <= sequence) return "Requires a positive monotonic frame sequence";
            var s = Configuration;
            if (!s.scene.enabled || s.scene.surfaces == null || s.actors.renderers == null || s.scene.depthStencil == null)
                return "Requires explicit scene surfaces, Actor renderers and stored scene depth";
            if (s.planar.enabled && !s.reflections.enabled) return "Planar requires the current reflection resolver";
            if (s.selfShadow.enabled && s.actors.selfShadow.HasValue) return "Choose either the host shadow producer or an external shadow ticket";
            var actors = new HashSet<Renderer>(s.actors.renderers);
            foreach (var surface in s.scene.surfaces)
                if (surface != null && actors.Contains(surface.renderer)) return "Actor geometry must not enter scene-only reflection history";
            if (s.colorGrade != null && !s.colorGrade.IsValid) return "Invalid caller-owned color LUT";
            if (s.depthOfField.enabled && !s.depthOfField.IsValid) return "Invalid depth-of-field settings";
            if(s.temporal.enabled&&(!s.actorMotion.enabled||!s.temporal.IsValid||s.temporalMaximumMiB<1||s.temporalMaximumMiB>2048))
                return "Temporal resolve requires enabled Actor motion, valid settings and budget";
            if(s.motionBlur.enabled&&(!s.actorMotion.enabled||!s.motionBlur.IsValid||s.motionBlurMaximumMiB<1||s.motionBlurMaximumMiB>2048||
                !MotionBlurSettings.Range(s.motionBlurJitterUv.x,-.5f,.5f)||!MotionBlurSettings.Range(s.motionBlurJitterUv.y,-.5f,.5f)))
                return "Frame motion blur requires enabled Actor motion, valid settings, jitter and budget";
            return null;
        }
        private bool Fail(string error) { phase = Phase.Failed; finalColor = null; return false; }

        /// <summary>The caller MUST establish GPU completion, including any commands
        /// recorded before a failure. This method does not wait, fence or submit.</summary>
        public void RetireAfterGpuCompletion()
        {
            if (disposed) return;
            if (phase == Phase.Failed) { reflection.ResetHistory();actor.ResetMotionHistoryAfterGpuCompletion();temporal.ResetHistory(); }
            if (phase == Phase.Failed) motionBlur.ResetHistory();
            draws?.Dispose(); draws = null; scene?.Dispose(); scene = null;
            shadowFrame = null; planarFrame = null; reflectionFrame = null;
            effectFrame = null; temporalFrame=null; dofFrame = null; gradeFrame = null;
            motionBlurFrame=null;
            actorFrame = default; finalColor = null; phase = Phase.Idle;
        }
        public void ResetHistoryAfterGpuCompletion()
        { RetireAfterGpuCompletion(); reflection.ResetHistory();actor.ResetMotionHistoryAfterGpuCompletion();temporal.ResetHistory();motionBlur.ResetHistory(); }
        /// <summary>Dispose only after submitted GPU work no longer uses these resources.</summary>
        public void Dispose()
        {
            if (disposed) return;
            RetireAfterGpuCompletion(); disposed = true;
            shadow.Dispose(); actor.Dispose(); planar.Dispose(); reflection.Dispose();
            effects.Dispose(); temporal.Dispose(); dof.Dispose(); motionBlur.Dispose(); grade.Dispose();
        }
    }
}
