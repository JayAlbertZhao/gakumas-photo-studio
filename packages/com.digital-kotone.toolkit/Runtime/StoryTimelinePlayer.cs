using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed class StoryTimelinePlayer : MonoBehaviour
    {
        private CharacterSceneRuntime _app;
        private StoryTimeline _timeline;
        private int _lastVoiceIndex = -1;

        public bool IsActive { get; private set; }
        public bool IsPaused { get; private set; }
        public float TimeSeconds { get; private set; }
        public float Duration { get { return _timeline == null ? 0f : _timeline.duration; } }
        public string StoryId { get { return _timeline == null ? "unavailable" : _timeline.story_id; } }
        public string CurrentMessage { get; private set; }
        public string CurrentMotion { get; private set; }
        public string CurrentFaceMotion { get; private set; }

        public bool Initialize(CharacterSceneRuntime app, string stagingRoot)
        {
            _app = app;
            string path = Path.Combine(stagingRoot, "story-timeline.json");
            if (!File.Exists(path))
            {
                Debug.LogWarning("[Story] Timeline missing: " + path);
                return false;
            }
            _timeline = JsonUtility.FromJson<StoryTimeline>(File.ReadAllText(path));
            if (_timeline == null || _timeline.body_motions == null)
            {
                Debug.LogWarning("[Story] Timeline invalid: " + path);
                return false;
            }
            _app.ConfigureStoryActorParts(_timeline.actors);
            _app.ConfigureStoryProps(_timeline.props);
            _app.ConfigureStoryBackgrounds(_timeline.backgrounds);
            Debug.Log(string.Format(
                "[Story] Loaded {0}: {1:0.00}s, {2} body, {3} face, {4} voices, {5} cameras, {6} background-layout, {7} background-transform, {8} fade, {9} foreground, {10} shake, {11} dof, {12} actor-lighting, {13} actors, {14} actor-renderer, {15} props, {16} prop-layout, {17} paraffin",
                StoryId, Duration, _timeline.body_motions.Length,
                _timeline.face_motions == null ? 0 : _timeline.face_motions.Length,
                _timeline.voices == null ? 0 : _timeline.voices.Length,
                _timeline.cameras == null ? 0 : _timeline.cameras.Length,
                _timeline.background_layouts == null ? 0 : _timeline.background_layouts.Length,
                _timeline.background_transforms == null ? 0 : _timeline.background_transforms.Length,
                _timeline.fades == null ? 0 : _timeline.fades.Length,
                _timeline.foregrounds == null ? 0 : _timeline.foregrounds.Length,
                _timeline.shakes == null ? 0 : _timeline.shakes.Length,
                _timeline.dofs == null ? 0 : _timeline.dofs.Length,
                _timeline.actor_lighting == null ? 0 : _timeline.actor_lighting.Length,
                _timeline.actors == null ? 0 : _timeline.actors.Length,
                _timeline.actor_renderers == null ? 0 : _timeline.actor_renderers.Length,
                _timeline.props == null ? 0 : _timeline.props.Length,
                _timeline.prop_layouts == null ? 0 : _timeline.prop_layouts.Length,
                _timeline.paraffins == null ? 0 : _timeline.paraffins.Length));
            return true;
        }

        public void StartStory(float startTime = 0f)
        {
            if (_timeline == null) return;
            IsActive = true;
            IsPaused = false;
            _app.EnterStoryMode();
            Seek(startTime, true);
        }

        public void StopStory()
        {
            if (!IsActive) return;
            IsActive = false;
            IsPaused = false;
            _app.ExitStoryMode();
        }

        public void TogglePause()
        {
            if (!IsActive) return;
            IsPaused = !IsPaused;
            _app.SetStoryAudioPaused(IsPaused);
        }

        public void Seek(float seconds, bool playVoice = false)
        {
            if (_timeline == null) return;
            TimeSeconds = Mathf.Clamp(seconds, 0f, Duration);
            _lastVoiceIndex = -1;
            Evaluate(playVoice);
        }

        private void Update()
        {
            if (!IsActive || _timeline == null) return;
            if (!IsPaused)
            {
                TimeSeconds += UnityEngine.Time.deltaTime;
                if (TimeSeconds >= Duration)
                {
                    TimeSeconds = 0f;
                    _lastVoiceIndex = -1;
                }
            }
            Evaluate(!IsPaused);
        }

        private void Evaluate(bool playVoice)
        {
            StoryMotionEvent body = Latest(_timeline.body_motions, TimeSeconds);
            StoryMotionEvent previousBody = Previous(_timeline.body_motions, body);
            float bodyLocal = 0f;
            float previousBodyLocal = 0f;
            if (body != null)
            {
                bodyLocal = body.clipIn + Mathf.Max(0f, TimeSeconds - body.time);
                previousBodyLocal = previousBody == null ? 0f :
                    previousBody.clipIn + Mathf.Max(0f, TimeSeconds - previousBody.time);
                float normalized = body.transition <= 0.001f
                    ? 1f
                    : Mathf.Clamp01((TimeSeconds - body.time) / body.transition);
                float blend = StoryEase(body.ease, normalized);
                _app.EvaluateStoryBody(
                    body.motion, bodyLocal,
                    previousBody == null ? null : previousBody.motion,
                    previousBodyLocal, blend);
                CurrentMotion = body.motion;
            }
            _app.ApplyStoryActorRenderer(Latest(
                _timeline.actor_renderers, TimeSeconds));

            StoryMotionEvent face = Latest(_timeline.face_motions, TimeSeconds);
            StoryMotionEvent previousFace = Previous(_timeline.face_motions, face);
            StoryFaceOverrideEvent[] overrides = ActiveOverrides(TimeSeconds);
            if (face != null)
            {
                float local = face.clipIn + Mathf.Max(0f, TimeSeconds - face.time);
                float previousLocal = previousFace == null
                    ? 0f
                    : previousFace.clipIn + Mathf.Max(0f, TimeSeconds - previousFace.time);
                float normalized = face.transition <= 0.001f
                    ? 1f
                    : Mathf.Clamp01((TimeSeconds - face.time) / face.transition);
                float blend = StoryEase(face.ease, normalized);
                _app.EvaluateStoryFace(
                    face.motion, local,
                    previousFace == null ? null : previousFace.motion,
                    previousLocal, blend, overrides, TimeSeconds);
                CurrentFaceMotion = face.motion;
            }
            else
            {
                // ActorMotionPlayable contains the motion bundle's paired _f
                // animation as well as its _b animation.  Until a higher-priority
                // ActorFacialMotion command takes ownership, evaluate that paired
                // face with its independent facial transition contract.
                if (body != null)
                {
                    float normalized = body.facialTransition <= 0.001f
                        ? 1f
                        : Mathf.Clamp01((TimeSeconds - body.time) / body.facialTransition);
                    float blend = StoryEase(body.ease, normalized);
                    _app.EvaluateStoryFace(
                        body.motion, bodyLocal,
                        previousBody == null ? null : previousBody.motion,
                        previousBodyLocal, blend, overrides, TimeSeconds);
                    CurrentFaceMotion = body.motion;
                }
                else
                {
                    _app.EvaluateStoryFace(null, 0f, null, 0f, 1f, overrides, TimeSeconds);
                    CurrentFaceMotion = string.Empty;
                }
            }

            StoryCameraEvent camera = Latest(_timeline.cameras, TimeSeconds);
            if (camera != null)
            {
                float t = camera.kind == "tween" && camera.duration > 0.001f
                    ? Mathf.Clamp01((TimeSeconds - camera.time) / camera.duration)
                    : 1f;
                _app.ApplyStoryCamera(camera, t);
            }
            _app.ApplyStoryDepthOfField(Active(_timeline.dofs, TimeSeconds));

            StoryBackgroundLayoutEvent backgroundLayout = Latest(
                _timeline.background_layouts, TimeSeconds);
            string backgroundId = backgroundLayout == null ? null : backgroundLayout.id;
            _app.ApplyStoryBackgroundLayout(backgroundId);
            StoryBackgroundDeclaration backgroundDeclaration = BackgroundForId(backgroundId);
            _app.ApplyStoryActorRenderProfile(
                backgroundDeclaration == null ? null : backgroundDeclaration.actorProfile);
            _app.ApplyStoryPostProcessProfile(
                backgroundDeclaration == null ? null : backgroundDeclaration.postProfile);
            StoryBackgroundTransformEvent backgroundTransform = LatestForId(
                _timeline.background_transforms, TimeSeconds, backgroundId);
            if (backgroundTransform != null)
            {
                float normalized = backgroundTransform.kind == "tween" &&
                    backgroundTransform.duration > 0.001f
                        ? Mathf.Clamp01((TimeSeconds - backgroundTransform.time) /
                            backgroundTransform.duration)
                        : 1f;
                _app.ApplyStoryBackgroundTransform(
                    backgroundTransform,
                    StoryEase(backgroundTransform.ease, normalized));
            }
            else
            {
                _app.ApplyStoryBackgroundTransform(null, 1f);
            }
            StoryParaffinEvent paraffin = Active(_timeline.paraffins, TimeSeconds);
            _app.ApplyStoryParaffin(
                backgroundDeclaration == null ? null : backgroundDeclaration.paraffin,
                paraffin,
                paraffin == null ? 0f : paraffin.EvaluateMixWeight(TimeSeconds));

            if (_timeline.props != null)
            {
                foreach (StoryPropDeclaration prop in _timeline.props)
                {
                    if (prop == null || string.IsNullOrEmpty(prop.id)) continue;
                    _app.ApplyStoryPropLayout(
                        prop.id,
                        LatestForId(_timeline.prop_layouts, TimeSeconds, prop.id));
                }
            }

            StoryLayoutEvent layout = Latest(_timeline.actor_layouts, TimeSeconds);
            if (layout != null)
            {
                float normalized = layout.kind == "tween" && layout.duration > 0.001f
                    ? Mathf.Clamp01((TimeSeconds - layout.time) / layout.duration)
                    : 1f;
                _app.ApplyStoryLayout(layout, StoryEase(layout.ease, normalized));
            }

            StoryActorColorEvent actorColor = Latest(_timeline.actor_colors, TimeSeconds);
            if (actorColor != null)
            {
                float normalized = actorColor.kind == "tween" && actorColor.duration > 0.001f
                    ? Mathf.Clamp01((TimeSeconds - actorColor.time) / actorColor.duration)
                    : 1f;
                _app.ApplyStoryActorColor(
                    actorColor,
                    StoryEase(actorColor.ease, normalized));
            }
            else
            {
                _app.ApplyStoryActorColor(null, 1f);
            }

            ApplyFadeLayer("Content");
            ApplyFadeLayer("Main");
            StoryForegroundEvent foreground = Active(_timeline.foregrounds, TimeSeconds);
            _app.ApplyStoryForeground(
                foreground,
                foreground == null ? 0f : foreground.EvaluateWeight(TimeSeconds));

            StoryShakeEvent shake = Active(_timeline.shakes, TimeSeconds);
            Vector2 shakePosition = Vector2.zero;
            if (shake != null)
            {
                float radius = shake.EvaluateRadius(TimeSeconds);
                if (radius > 0f)
                {
                    // Native RandomOnCircumference selects a fresh uniform
                    // angle every mixer evaluation, including a paused graph.
                    float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    shakePosition = new Vector2(
                        Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                }
            }
            _app.ApplyStoryShake(shakePosition);

            StoryLookTargetEvent lookTarget = Latest(_timeline.look_targets, TimeSeconds);
            if (lookTarget != null)
            {
                float t = lookTarget.kind == "tween" && lookTarget.duration > 0.001f
                    ? Mathf.Clamp01((TimeSeconds - lookTarget.time) / lookTarget.duration)
                    : 1f;
                _app.ApplyStoryLookTarget(lookTarget, t);
            }
            else
            {
                _app.ApplyStoryLookTarget(null, 1f);
            }

            _app.ApplyStoryBlink(StoryBlinkProgress(_timeline.eye_blinks, TimeSeconds));

            StoryActorLightingEvent actorLighting = Active(
                _timeline.actor_lighting, TimeSeconds);
            _app.ApplyStoryActorLighting(
                actorLighting,
                actorLighting == null ? 0f : actorLighting.EvaluateMixWeight(TimeSeconds));

            StoryMessageEvent message = Active(_timeline.messages, TimeSeconds);
            CurrentMessage = message == null ? string.Empty : CleanMessage(message.text);

            if (playVoice) EvaluateVoice();
        }

        private void ApplyFadeLayer(string layer)
        {
            StoryFadeEvent fade = LatestFade(_timeline.fades, TimeSeconds, layer);
            if (fade == null)
            {
                _app.ApplyStoryFade(layer, null, 0f);
                return;
            }
            float normalized = fade.duration > 0.000001f
                ? Mathf.Clamp01((TimeSeconds - fade.time) / fade.duration)
                : 1f;
            _app.ApplyStoryFade(layer, fade, StoryEase(fade.ease, normalized));
        }

        private void EvaluateVoice()
        {
            if (_timeline.voices == null) return;
            for (int index = 0; index < _timeline.voices.Length; index++)
            {
                StoryVoiceEvent voice = _timeline.voices[index];
                if (TimeSeconds < voice.time || TimeSeconds >= voice.time + Mathf.Max(voice.duration, 0.01f)) continue;
                if (_lastVoiceIndex == index) return;
                _lastVoiceIndex = index;
                _app.PlayStoryVoice(voice.voice, Mathf.Max(0f, TimeSeconds - voice.time));
                return;
            }
        }

        private StoryFaceOverrideEvent[] ActiveOverrides(float time)
        {
            if (_timeline.face_overrides == null) return new StoryFaceOverrideEvent[0];
            return _timeline.face_overrides
                .Where(value => value != null && time >= value.time && time <= value.time + value.duration)
                .ToArray();
        }

        private static T Latest<T>(T[] values, float time) where T : StoryTimedEvent
        {
            if (values == null) return null;
            T result = null;
            foreach (T value in values)
            {
                if (value == null || value.time > time) continue;
                if (result == null || value.time >= result.time) result = value;
            }
            return result;
        }

        private static StoryMotionEvent Previous(StoryMotionEvent[] values, StoryMotionEvent current)
        {
            if (values == null || current == null) return null;
            StoryMotionEvent result = null;
            foreach (StoryMotionEvent value in values)
            {
                if (value == null || value == current || value.time > current.time) continue;
                if (result == null || value.time >= result.time) result = value;
            }
            return result;
        }

        private static T LatestForId<T>(T[] values, float time, string id)
            where T : StoryIdentifiedTimedEvent
        {
            if (values == null || string.IsNullOrEmpty(id)) return null;
            T result = null;
            foreach (T value in values)
            {
                if (value == null || value.time > time ||
                    !string.Equals(value.id, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (result == null || value.time >= result.time) result = value;
            }
            return result;
        }

        private static StoryFadeEvent LatestFade(
            StoryFadeEvent[] values, float time, string layer)
        {
            if (values == null) return null;
            StoryFadeEvent result = null;
            foreach (StoryFadeEvent value in values)
            {
                if (value == null || value.time > time ||
                    !string.Equals(value.layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                if (result == null || value.time >= result.time) result = value;
            }
            return result;
        }

        private static float StoryEase(string name, float t)
        {
            t = Mathf.Clamp01(t);
            // Campus.ADV.ActorFacialMotionCommand binds an absent ease token
            // through EasingDataParameter.GetValue(InOutSine).  Keep the named
            // variants needed by extracted scripts explicit; unknown values
            // retain the command default rather than silently becoming linear.
            if (string.Equals(name, "Linear", StringComparison.OrdinalIgnoreCase)) return t;
            if (string.Equals(name, "InSine", StringComparison.OrdinalIgnoreCase))
                return 1f - Mathf.Cos(t * Mathf.PI * 0.5f);
            if (string.Equals(name, "OutSine", StringComparison.OrdinalIgnoreCase))
                return Mathf.Sin(t * Mathf.PI * 0.5f);
            if (string.Equals(name, "InQuad", StringComparison.OrdinalIgnoreCase))
                return t * t;
            if (string.Equals(name, "OutQuad", StringComparison.OrdinalIgnoreCase))
                return 1f - (1f - t) * (1f - t);
            if (string.Equals(name, "InOutQuad", StringComparison.OrdinalIgnoreCase))
                return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) * 0.5f;
            return 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI);
        }

        private static T Active<T>(T[] values, float time) where T : StoryTimedEvent
        {
            if (values == null) return null;
            return values.LastOrDefault(value => value != null && time >= value.time && time <= value.time + value.duration);
        }

        private StoryBackgroundDeclaration BackgroundForId(string id)
        {
            if (_timeline == null || _timeline.backgrounds == null || string.IsNullOrEmpty(id))
                return null;
            return _timeline.backgrounds.LastOrDefault(value =>
                value != null && string.Equals(value.id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static float StoryBlinkProgress(StoryBlinkEvent[] values, float time)
        {
            StoryBlinkEvent active = Active(values, time);
            if (active == null) return -1f;
            float duration = Mathf.Max(active.duration, 0.01f);
            // ActorEyeBlinkMixerPlayable passes TimelineClip.GetProgress(time,
            // useBaseDuration:true) into SetEyeBlinkWeight. The facial system
            // then evaluates VLActorEyeBlinkData; the progress itself is linear.
            return Mathf.Clamp01((time - active.time) / duration);
        }

        private static string CleanMessage(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string result = value.Replace("\\r\\n", "  ");
            result = System.Text.RegularExpressions.Regex.Replace(result, "<[^>]*>", string.Empty);
            return result.Length > 86 ? result.Substring(0, 86) + "..." : result;
        }
    }

    [Serializable]
    public sealed class StoryTimeline
    {
        public string schema_version;
        public string story_id;
        public float duration;
        public StoryMotionEvent[] body_motions;
        public StoryMotionEvent[] face_motions;
        public StoryFaceOverrideEvent[] face_overrides;
        public StoryVoiceEvent[] voices;
        public StoryCameraEvent[] cameras;
        public StoryDepthOfFieldEvent[] dofs;
        public StoryBackgroundDeclaration[] backgrounds;
        public StoryBackgroundLayoutEvent[] background_layouts;
        public StoryBackgroundTransformEvent[] background_transforms;
        public StoryLayoutEvent[] actor_layouts;
        public StoryActorColorEvent[] actor_colors;
        public StoryActorDeclaration[] actors;
        public StoryActorRendererEvent[] actor_renderers;
        public StoryPropDeclaration[] props;
        public StoryPropLayoutEvent[] prop_layouts;
        public StoryFadeEvent[] fades;
        public StoryForegroundEvent[] foregrounds;
        public StoryShakeEvent[] shakes;
        public StoryLookTargetEvent[] look_targets;
        public StoryBlinkEvent[] eye_blinks;
        public StoryActorLightingEvent[] actor_lighting;
        public StoryParaffinEvent[] paraffins;
        public StoryMessageEvent[] messages;
    }

    [Serializable]
    public class StoryTimedEvent
    {
        public float time;
        public float duration;
        public int order;
    }

    [Serializable]
    public class StoryIdentifiedTimedEvent : StoryTimedEvent
    {
        public string id;
    }

    [Serializable]
    public sealed class StoryMotionEvent : StoryTimedEvent
    {
        public string motion;
        public float clipIn;
        public float transition;
        public float facialTransition;
        public string ease;
    }

    [Serializable]
    public sealed class StoryVoiceEvent : StoryTimedEvent
    {
        public string voice;
    }

    [Serializable]
    public sealed class StoryFaceOverrideEvent : StoryTimedEvent
    {
        public float easeIn;
        public float easeOut;
        public float mixInDuration;
        public float mixOutDuration;
        public int mixInEaseType = 4;
        public int mixOutEaseType = 4;
        public StoryFaceWeight[] weights;
        public StoryFaceDecal[] decals;

        public float EvaluateMixWeight(float storyTime)
        {
            float elapsed = Mathf.Max(0f, storyTime - time);
            float remaining = Mathf.Max(0f, time + duration - storyTime);
            float factor = 1f;
            if (mixInDuration > 0.000001f)
                factor *= TimelineEase(mixInEaseType, Mathf.Clamp01(elapsed / mixInDuration));
            if (mixOutDuration > 0.000001f)
                factor *= TimelineEase(mixOutEaseType, Mathf.Clamp01(remaining / mixOutDuration));
            return factor;
        }

        private static float TimelineEase(int type, float t)
        {
            t = Mathf.Clamp01(t);
            // Uguiss.Timeline.EasingType.  Extracted ADV override clips use
            // InOutSine (4); keep the adjacent enum values literal for other
            // scripts rather than treating every easing curve as linear.
            if (type == 1) return t;
            if (type == 2) return 1f - Mathf.Cos(t * Mathf.PI * 0.5f);
            if (type == 3) return Mathf.Sin(t * Mathf.PI * 0.5f);
            if (type == 4) return 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI);
            return t;
        }
    }

    [Serializable]
    public sealed class StoryFaceWeight
    {
        public int index;
        public float value;
    }

    [Serializable]
    public sealed class StoryFaceDecal
    {
        public string path;
        public string attribute;
        public float value;
    }

    [Serializable]
    public sealed class StoryCameraEvent : StoryTimedEvent
    {
        public string kind;
        public StoryCameraSetting setting;
        public StoryCameraSetting from;
        public StoryCameraSetting to;
    }

    [Serializable]
    public sealed class StoryCameraSetting
    {
        public float focalLength = 50f;
        public float nearClipPlane = 0.1f;
        public float farClipPlane = 1000f;
        public StoryTransform transform;
        public StoryCameraDofSetting dofSetting;
    }

    [Serializable]
    public sealed class StoryCameraDofSetting
    {
        public bool active;
        public float focalPoint = 4f;
        public float fNumber = 4f;
        public float maxBlurSpread = 3f;
    }

    [Serializable]
    public sealed class StoryDepthOfFieldEvent : StoryTimedEvent
    {
        public StoryIntParameter quality;
        public StoryFloatParameter focalPoint;
        public StoryFloatParameter fNumber;
        public StoryFloatParameter maxBlurSpread;
        public StoryFloatParameter foregroundBlurExtrude;
        public StoryBoolParameter useFNumber;
        public StoryFloatParameter smoothness;
        public StoryFloatParameter focalSize;
        public StoryIntParameter bladeCount;
        public StoryFloatParameter bladeCurvature;
        public StoryFloatParameter bladeRotation;
    }

    [Serializable]
    public sealed class StoryLayoutEvent : StoryTimedEvent
    {
        public string kind;
        public string id;
        public StoryTransform transform;
        public StoryTransform from;
        public StoryTransform to;
        public string ease;
    }

    [Serializable]
    public sealed class StoryActorColorEvent : StoryIdentifiedTimedEvent
    {
        public string kind;
        public string color;
        public string fromColor;
        public string toColor;
        public string ease;
    }

    [Serializable]
    public sealed class StoryActorDeclaration
    {
        public string id;
        public string body;
        public string face;
        public string hair;
        public string[] others;
        public int order;
    }

    [Serializable]
    public sealed class StoryActorRendererEvent : StoryIdentifiedTimedEvent
    {
        public string[] inactiveAssets;
    }

    [Serializable]
    public sealed class StoryPropDeclaration
    {
        public string id;
        public string asset;
        public string bundle;
        public string objectName;
        public string heightTargetId;
        public int order;
    }

    [Serializable]
    public sealed class StoryPropLayoutEvent : StoryIdentifiedTimedEvent
    {
        public string kind;
        public StoryTransform transform;
    }

    [Serializable]
    public sealed class StoryFadeEvent : StoryTimedEvent
    {
        public string layer;
        public string color;
        public float from;
        public float to;
        public string ease;
    }

    [Serializable]
    public sealed class StoryForegroundEvent : StoryTimedEvent
    {
        public string src;
        public StoryTransform2D transform;
        public StoryForegroundLayoutProfile layout;
        public float easeIn;
        public float easeOut;
        public int mixInEaseType = 1;
        public int mixOutEaseType = 1;

        public float EvaluateWeight(float storyTime)
        {
            float elapsed = Mathf.Max(0f, storyTime - time);
            float remaining = Mathf.Max(0f, time + duration - storyTime);
            float factor = 1f;
            if (easeIn > 0.000001f)
                factor *= TimelineEase(mixInEaseType, Mathf.Clamp01(elapsed / easeIn));
            if (easeOut > 0.000001f)
                factor *= TimelineEase(mixOutEaseType, Mathf.Clamp01(remaining / easeOut));
            return factor;
        }

        private static float TimelineEase(int type, float t)
        {
            t = Mathf.Clamp01(t);
            if (type == 1) return t;
            if (type == 2) return 1f - Mathf.Cos(t * Mathf.PI * 0.5f);
            if (type == 3) return Mathf.Sin(t * Mathf.PI * 0.5f);
            if (type == 4) return 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI);
            return t;
        }
    }

    [Serializable]
    public sealed class StoryForegroundLayoutProfile
    {
        public int layoutType;
        public StoryForegroundLayoutSetting horizontal;
        public StoryForegroundLayoutSetting vertical;
        public long containerSourcePathId;
    }

    [Serializable]
    public sealed class StoryForegroundLayoutSetting
    {
        public StoryVector2 aspectRatio;
        public StoryRect layoutRect;
        public StoryRect clippingRect;
    }

    [Serializable]
    public sealed class StoryShakeEvent : StoryTimedEvent
    {
        public float strength = 10f;
        public float pulseDuration = 0.18f;
        public float interval = 0.05f;
        public int count = 2;
        public int ease = 1;

        public float EvaluateRadius(float storyTime)
        {
            float local = storyTime - time;
            if (local < 0f || local >= duration || pulseDuration <= 0.000001f)
                return 0f;
            float cycle = Mathf.Max(0.000001f, pulseDuration + Mathf.Max(0f, interval));
            float remaining = Mathf.Max(0f, pulseDuration - Mathf.Repeat(local, cycle));
            float normalized = Mathf.Clamp01(remaining / pulseDuration);
            return Mathf.Max(0f, strength) * Easing(ease, normalized);
        }

        private static float Easing(int type, float t)
        {
            t = Mathf.Clamp01(t);
            // Uguiss.Timeline.EasingType; the extracted focus command is
            // Linear (1). Preserve the adjacent common values for other ADV
            // scripts while retaining linear behavior for unknown types.
            if (type == 2) return 1f - Mathf.Cos(t * Mathf.PI * 0.5f);
            if (type == 3) return Mathf.Sin(t * Mathf.PI * 0.5f);
            if (type == 4) return 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI);
            if (type == 5) return t * t;
            if (type == 6) return 1f - (1f - t) * (1f - t);
            return t;
        }
    }

    [Serializable]
    public sealed class StoryBackgroundDeclaration
    {
        public string id;
        public string src;
        public int order;
        public StoryParaffinProfile paraffin;
        public StoryBackgroundClippingProfile clipping;
        public StoryActorRenderProfile actorProfile;
        public StoryPostProcessProfile postProfile;
    }

    [Serializable]
    public sealed class StoryActorRenderProfile
    {
        public bool active;
        public StoryColorParameter lightColor;
        public StoryIntParameter mainLightSpace;
        public StoryVector2Parameter mainLightAngle;
        public StoryFloatParameter matCapOffset;
        public StoryFloatParameter matCapSmoothScale;
        public StoryColorParameter shadeColorMultiply;
        public StoryColorParameter shadeColorAdditive;
        public StoryFloatParameter shadeApplyRatio;
        public StoryVector2Parameter rimAngle;
        public StoryFloatParameter rimBaseColorRatio;
        public StoryFloatParameter rimPower;
        public StoryColorParameter rimColor;
        public StoryFloatParameter giScale;
        public StoryFloatParameter additiveLightScale;
        public StoryFloatParameter additiveLightSpecularScale;
        public StoryFloatParameter skinSaturation;
        public StoryColorParameter eyeHighlightColor;
        public StoryColorParameter reflectionColor;
        public StoryColorParameter eyeReflectionColor;
        public long sourcePathId;
        public long profilePathId;
        public string profileName;
        public int explicitOverrideCount;
    }

    [Serializable]
    public sealed class StoryPostProcessProfile
    {
        public bool active;
        public StoryBloomProfile bloom;
        public StoryColorAdjustmentsProfile colorAdjustments;
        public StoryChromaticAberrationProfile chromaticAberration;
        public StoryDiffusionProfile diffusion;
        public StoryTonemappingProfile tonemapping;
        public long sceneProfilePathId;
        public string sceneProfileName;
        public long commonProfilePathId;
        public string commonProfileName;
    }

    [Serializable]
    public sealed class StoryBloomProfile
    {
        public bool active;
        public StoryFloatParameter intensity;
        public StoryFloatParameter threshold;
        public StoryIntParameter diffusion;
        public StoryColorParameter color;
        public long sourcePathId;
        public int explicitOverrideCount;
    }

    [Serializable]
    public sealed class StoryColorAdjustmentsProfile
    {
        public bool active;
        public StoryFloatParameter postExposure;
        public StoryFloatParameter contrast;
        public StoryColorParameter colorFilter;
        public StoryFloatParameter hueShift;
        public StoryFloatParameter saturation;
        public long sourcePathId;
        public int explicitOverrideCount;
    }

    [Serializable]
    public sealed class StoryChromaticAberrationProfile
    {
        public bool active;
        public StoryFloatParameter intensity;
        public long sourcePathId;
        public int explicitOverrideCount;
    }

    [Serializable]
    public sealed class StoryDiffusionProfile
    {
        public bool active;
        public StoryFloatParameter diffusion;
        public StoryFloatParameter contrastThreshold;
        public StoryFloatParameter contrastPower;
        public StoryFloatParameter blend;
        public long sourcePathId;
        public int explicitOverrideCount;
    }

    [Serializable]
    public sealed class StoryTonemappingProfile
    {
        public bool active;
        public StoryIntParameter mode;
        public StoryFloatParameter toeStrength;
        public StoryFloatParameter toeLength;
        public StoryFloatParameter shoulderStrength;
        public StoryFloatParameter shoulderLength;
        public StoryFloatParameter shoulderAngle;
        public StoryFloatParameter gamma;
        public StoryFloatParameter gtLinearSectionStart;
        public StoryFloatParameter gtContrast;
        public StoryFloatParameter gtBlackBrightness;
        public StoryFloatParameter gtLinearSelectionLength;
        public long sourcePathId;
        public int explicitOverrideCount;
    }

    [Serializable]
    public sealed class StoryBackgroundClippingProfile
    {
        public int mode;
        public int trimmingType;
        public StoryBackgroundClippingSetting horizontal;
        public StoryBackgroundClippingSetting vertical;
        public long containerSourcePathId;
        public long fitterSourcePathId;
    }

    [Serializable]
    public sealed class StoryBackgroundClippingSetting
    {
        public StoryVector2 baseSize;
        public StoryRect clippingRect;
    }

    [Serializable]
    public sealed class StoryRect
    {
        public float x;
        public float y;
        public float width;
        public float height;
    }

    [Serializable]
    public sealed class StoryParaffinProfile
    {
        public bool active;
        public StoryFloatParameter baseAspect;
        public StoryFlareParameter flare0;
        public StoryFlareParameter flare1;
        public long sourcePathId;
    }

    [Serializable]
    public sealed class StoryParaffinEvent : StoryTimedEvent
    {
        public float mixInDuration;
        public float mixOutDuration;
        public int mixInEaseType = 1;
        public int mixOutEaseType = 1;
        public StoryFlareParameter flare0;
        public StoryFlareParameter flare1;

        public float EvaluateMixWeight(float storyTime)
        {
            float elapsed = Mathf.Max(0f, storyTime - time);
            float remaining = Mathf.Max(0f, time + duration - storyTime);
            float factor = 1f;
            if (mixInDuration > 0.000001f)
                factor *= TimelineEase(mixInEaseType, Mathf.Clamp01(elapsed / mixInDuration));
            if (mixOutDuration > 0.000001f)
                factor *= TimelineEase(mixOutEaseType, Mathf.Clamp01(remaining / mixOutDuration));
            return factor;
        }

        private static float TimelineEase(int type, float t)
        {
            t = Mathf.Clamp01(t);
            if (type == 1) return t;
            if (type == 2) return 1f - Mathf.Cos(t * Mathf.PI * 0.5f);
            if (type == 3) return Mathf.Sin(t * Mathf.PI * 0.5f);
            if (type == 4) return 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI);
            return t;
        }
    }

    [Serializable]
    public sealed class StoryFlareParameter
    {
        public bool overrideState;
        public StoryFlareSetting value;
    }

    [Serializable]
    public sealed class StoryFlareSetting
    {
        public int type;
        public bool fixToBaseAspect;
        public StoryVector2 center;
        public StoryColor color0;
        public StoryColor color1;
        public StoryVector2 size;
    }

    [Serializable]
    public sealed class StoryColor
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public Color ToColor()
        {
            return new Color(r, g, b, a);
        }
    }

    [Serializable]
    public sealed class StoryBackgroundLayoutEvent : StoryIdentifiedTimedEvent
    {
    }

    [Serializable]
    public sealed class StoryBackgroundTransformEvent : StoryIdentifiedTimedEvent
    {
        public string kind;
        public StoryTransform2D setting;
        public StoryTransform2D from;
        public StoryTransform2D to;
        public string ease;
    }

    [Serializable]
    public sealed class StoryTransform2D
    {
        public StoryVector2 position;
        public StoryVector2 scale;
        public float angle;
    }

    [Serializable]
    public sealed class StoryLookTargetEvent : StoryTimedEvent
    {
        public string kind;
        public StoryLookTargetSetting setting;
        public StoryLookTargetSetting from;
        public StoryLookTargetSetting to;
    }

    [Serializable]
    public sealed class StoryLookTargetSetting
    {
        public int type;
        public string actorId;
        public int bones;
        public float azimuth;
        public float elevation;
        public float weight;
        public float eyesWeight;
        public float headWeight;
        public float bodyWeight;
        public StoryTransform transform;
    }

    [Serializable]
    public sealed class StoryBlinkEvent : StoryTimedEvent
    {
    }

    [Serializable]
    public sealed class StoryActorLightingEvent : StoryTimedEvent
    {
        public float mixInDuration;
        public float mixOutDuration;
        public int mixInEaseType = 1;
        public int mixOutEaseType = 1;
        public StoryFloatParameter shadowStrength;
        public StoryFloatParameter useOffset;
        public StoryIntParameter directionalType;
        public StoryVector2Parameter lightDirectional;
        public StoryFloatParameter facePartsShadowStrength;
        public StoryVector2Parameter mainLightAngle;
        public StoryIntParameter mainLightSpace;

        public float EvaluateMixWeight(float storyTime)
        {
            float elapsed = Mathf.Max(0f, storyTime - time);
            float remaining = Mathf.Max(0f, time + duration - storyTime);
            float factor = 1f;
            if (mixInDuration > 0.000001f)
                factor *= TimelineEase(mixInEaseType, Mathf.Clamp01(elapsed / mixInDuration));
            if (mixOutDuration > 0.000001f)
                factor *= TimelineEase(mixOutEaseType, Mathf.Clamp01(remaining / mixOutDuration));
            return factor;
        }

        private static float TimelineEase(int type, float t)
        {
            t = Mathf.Clamp01(t);
            if (type == 1) return t;
            if (type == 2) return 1f - Mathf.Cos(t * Mathf.PI * 0.5f);
            if (type == 3) return Mathf.Sin(t * Mathf.PI * 0.5f);
            if (type == 4) return 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI);
            return t;
        }
    }

    [Serializable]
    public sealed class StoryFloatParameter
    {
        public bool overrideState;
        public float value;
    }

    [Serializable]
    public sealed class StoryColorParameter
    {
        public bool overrideState;
        public StoryColor value;
    }

    [Serializable]
    public sealed class StoryIntParameter
    {
        public bool overrideState;
        public int value;
    }

    [Serializable]
    public sealed class StoryBoolParameter
    {
        public bool overrideState;
        public bool value;
    }

    [Serializable]
    public sealed class StoryVector2Parameter
    {
        public bool overrideState;
        public StoryVector2 value;
    }

    [Serializable]
    public sealed class StoryVector2
    {
        public float x;
        public float y;

        public Vector2 ToVector2()
        {
            return new Vector2(x, y);
        }
    }

    [Serializable]
    public sealed class StoryTransform
    {
        public StoryVector3 position;
        public StoryVector3 rotation;
        public StoryVector3 scale;
    }

    [Serializable]
    public sealed class StoryVector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }
    }

    [Serializable]
    public sealed class StoryMessageEvent : StoryTimedEvent
    {
        public string name;
        public string text;
    }
}
