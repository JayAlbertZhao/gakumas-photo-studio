using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Opt-in standalone visual probes; uses only the user's loaded model.</summary>
    public sealed class ActorRenderingValidation : MonoBehaviour
    {
        [Serializable] private sealed class Frame
        {
            public string name;
            public string actorPoseDigest;
            public string expression;
            public double photoMotionTime;
            public bool photoMotionPlaying;
            public int width, height, outlineDraws, hairCoverDraws, additionalLights;
            public float skinSaturationDelta;
            public Vector3 cameraPosition;
            public Vector4 matcapParameters, lightingScales, lightDirection, keyColor, profileLightColor;
        }
        [Serializable] private sealed class Report
        {
            public string schema = "photo-studio.actor-rendering-probes.v2";
            public string graphicsDevice;
            public bool temporalHistoryReset = true;
            public int repeatChangedPixels = -1;
            public int repeatMaxChannelDifference = -1;
            public int profileRestoreChangedPixels = -1;
            public int lightRemovalChangedPixels = -1;
            public int skinRestoreChangedPixels = -1;
            public int ambientRestoreChangedPixels = -1;
            public int additionalRestoreChangedPixels = -1;
            public bool pausedGraphStopped;
            public bool pausedOrbitPosePreserved;
            public int pausedOrbitComparedFrames;
            public bool animationResumed;
            public double resumeStartTime, resumeEndTime;
            public List<Frame> frames = new List<Frame>();
        }

        private string _directory;
        private ActorRenderControls _controls;
        private OriginalStyleRenderPipeline _pipeline;
        private Color32[] _repeatReference;
        private string _pausedPoseDigest;
        private double _pausedMotionTime;
        private readonly Report _report = new Report();

        public static void AttachIfRequested(GameObject camera)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--validate-actor-rendering");
            if (index < 0 || index + 1 >= args.Length) return;
            var validation = camera.AddComponent<ActorRenderingValidation>();
            validation._directory = Path.GetFullPath(args[index + 1]);
        }

        private IEnumerator Start()
        {
            _controls = GetComponent<ActorRenderControls>();
            _pipeline = GetComponent<OriginalStyleRenderPipeline>();
            PhotoModeApp app = FindObjectOfType<PhotoModeApp>();
            OrbitPhotoCamera orbit = GetComponent<OrbitPhotoCamera>();
            Directory.CreateDirectory(_directory);
            _report.graphicsDevice = SystemInfo.graphicsDeviceVersion;
            if (app == null || _controls == null || _pipeline == null || orbit == null || app.StoryActive)
                throw new InvalidOperationException("Actor probes require --photo-mode and the reconstructed renderer.");
            yield return new WaitForSecondsRealtime(2f);
            app.TogglePause();
            Time.timeScale = 0f;
            orbit.target = new Vector3(0f, 1.38f, 0f);
            orbit.distance = 1.25f;
            orbit.pitch = 0f;
            orbit.yaw = 180f;
            orbit.ApplyPose();
            yield return Capture("01-front");
            yield return Capture("01b-front-repeat");
            _controls.hairCover = false;
            yield return Capture("02-no-hair-cover");
            _controls.hairCover = true;
            _controls.outlines = false;
            yield return Capture("03-no-outline");
            _controls.outlines = true;
            orbit.yaw = 225f;
            yield return Capture("04-quarter");
            _controls.hairCover = false;
            yield return Capture("04b-quarter-no-cover");
            _controls.hairCover = true;
            orbit.yaw = 270f;
            yield return Capture("05-side");
            // A front/side sweep cannot expose broken posterior hair. Keep
            // the same paused rig while checking both rear quarters and back.
            orbit.yaw = 315f;
            yield return Capture("05b-rear-quarter");
            // Stay in front of the physical studio backdrop at z=-1.10.
            orbit.distance = 1f;
            orbit.yaw = 0f;
            yield return Capture("05c-back");
            _controls.outlines = false;
            yield return Capture("05d-back-no-outline");
            _controls.outlines = true;
            _controls.hairCover = false;
            yield return Capture("05e-back-no-cover");
            _controls.hairCover = true;
            orbit.distance = 1.25f;
            orbit.yaw = 45f;
            yield return Capture("05f-other-rear-quarter");
            orbit.yaw = 180f;
            _controls.overrideLighting = true;
            _controls.diffuseOffset = -0.5f;
            yield return Capture("06-offset-minus");
            _controls.diffuseOffset = 0.8f;
            yield return Capture("07-offset-plus");
            _controls.diffuseOffset = 0.3f;
            _controls.shadeStrength = 0f;
            yield return Capture("08-no-shade");
            _controls.shadeStrength = 1f;
            _controls.worldSpaceLight = true;
            _controls.lightAngle = new Vector2(25f, 150f);
            yield return Capture("09-world-light", expectedWorldSpace: true);
            // Strong lateral directions expose the reflected face-triangle
            // response; the default studio light can leave it almost inactive.
            _controls.lightAngle = new Vector2(0f, 90f);
            yield return Capture("09a-world-light-left", expectedWorldSpace: true);
            _controls.lightAngle = new Vector2(0f, -90f);
            yield return Capture("09b-world-light-right", expectedWorldSpace: true);
            _controls.lightAngle = new Vector2(25f, 150f);
            orbit.yaw = 225f;
            yield return Capture("10-world-light-orbit", expectedWorldSpace: true);
            orbit.yaw = 270f;
            yield return Capture("10a-world-light-side", expectedWorldSpace: true);
            orbit.pitch = -20f;
            yield return Capture("10b-world-light-elevated", expectedWorldSpace: true);
            orbit.pitch = 0f;
            orbit.yaw = 180f;
            _controls.overrideLighting = false;
            yield return Capture("11-profile-restored");
            _controls.ToggleTestLights();
            yield return Capture("12-point-lights");
            foreach (Light light in FindObjectsOfType<Light>())
                if (light.name == "Warm point" || light.name == "Cool point")
                {
                    light.type = LightType.Spot;
                    light.spotAngle = 55f;
                    light.innerSpotAngle = 30f;
                    light.transform.LookAt(new Vector3(0f, 1.3f, 0f));
                }
            yield return Capture("12b-spot-lights");
            _controls.ToggleTestLights();
            yield return Capture("13-point-lights-removed");
            Vector3 lightTarget = orbit.target;
            float lightDistance = orbit.distance;
            Color profileColor = _controls.lightColor;
            float specularScale = _controls.additionalSpecularScale;
            orbit.target = new Vector3(0f, 0.8f, 0f);
            orbit.distance = 3.2f;
            yield return Capture("13a-fullbody-no-lights", expectedLights: 0);
            _controls.ToggleTestLights();
            yield return Capture("13b-fullbody-point", expectedLights: 2);
            foreach (Light light in FindObjectsOfType<Light>())
                if (light.name == "Warm point" || light.name == "Cool point")
                {
                    light.type = LightType.Spot;
                    light.spotAngle = 55f;
                    light.innerSpotAngle = 30f;
                    light.transform.LookAt(new Vector3(0f, 1.0f, 0f));
                }
            yield return Capture("13c-fullbody-spot", expectedLights: 2);
            _controls.overrideLighting = true;
            _controls.lightColor = new Color(0.35f, 0.6f, 1f);
            yield return Capture("13d-fullbody-tinted-spot", expectedLights: 2);
            _controls.additionalSpecularScale = 0f;
            yield return Capture("13e-fullbody-tinted-no-extra-specular", expectedLights: 2);
            _controls.ToggleTestLights();
            yield return Capture("13f-fullbody-tinted-no-lights", expectedLights: 0);
            _controls.overrideLighting = false;
            _controls.lightColor = profileColor;
            _controls.additionalSpecularScale = specularScale;
            yield return Capture("13g-fullbody-lights-restored", expectedLights: 0);
            orbit.target = lightTarget;
            orbit.distance = lightDistance;
            var layerMaterials = new List<Material>();
            var weights = new List<float>();
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
                foreach (Material material in renderer.sharedMaterials)
                    if (material != null && material.HasProperty("_EnableLayer") &&
                        material.GetFloat("_EnableLayer") > 0.5f && !layerMaterials.Contains(material))
                    {
                        layerMaterials.Add(material);
                        weights.Add(material.GetFloat("_LayerWeight"));
                        material.SetFloat("_LayerWeight", 1f);
                    }
            yield return Capture("14-layer-full");
            for (int i = 0; i < layerMaterials.Count; i++) layerMaterials[i].SetFloat("_LayerWeight", weights[i]);
            _controls.overrideLighting = true;
            _controls.giScale = 0.5f;
            _controls.worldSpaceLight = false;
            _controls.lightAngle = new Vector2(-5f, 10f);
            yield return Capture("15-ambient");
            // Include metallic outfit regions, not only a face close-up.
            Vector3 ambientTarget = orbit.target;
            float ambientDistance = orbit.distance;
            orbit.target = new Vector3(0f, 0.8f, 0f);
            orbit.distance = 3.2f;
            _controls.giScale = 0f;
            yield return Capture("15a-fullbody-noambient", expectedGi: 0f);
            _controls.giScale = 0.5f;
            yield return Capture("15b-fullbody-ambient", expectedGi: 0.5f);
            _controls.giScale = 0f;
            yield return Capture("15c-fullbody-restored", expectedGi: 0f);
            orbit.target = ambientTarget;
            orbit.distance = ambientDistance;
            _controls.giScale = 0f;
            _controls.rimColor = new Color(0.1f, 0.8f, 0.2f);
            yield return Capture("16-colored-rim");
            _controls.overrideLighting = false;
            // A face-only close-up cannot detect ignored skin inside body
            // materials. Keep the real rig and outfit, and include the legs.
            Vector3 previousTarget = orbit.target;
            float previousDistance = orbit.distance;
            float previousSkin = Shader.GetGlobalFloat("_CapturedSkinSaturation");
            orbit.target = new Vector3(0f, 0.8f, 0f);
            orbit.distance = 3.2f;
            Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
            yield return Capture("18-skin-neutral", 0f);
            Shader.SetGlobalFloat("_CapturedSkinSaturation", -1f);
            yield return Capture("18a-skin-desaturated", -1f);
            Shader.SetGlobalFloat("_CapturedSkinSaturation", 0.5f);
            yield return Capture("18b-skin-saturated", 0.5f);
            Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
            yield return Capture("18c-skin-restored", 0f);
            Shader.SetGlobalFloat("_CapturedSkinSaturation", previousSkin);
            orbit.target = previousTarget;
            orbit.distance = previousDistance;
            _controls.showPanel = true;
            yield return Capture("17-render-panel");
            _controls.showPanel = false;
            Time.timeScale = 1f;
            _report.resumeStartTime = app.PhotoMotionTime;
            app.TogglePause();
            double previousMotionTime = _report.resumeStartTime;
            // This sequence deliberately resumes the existing animation and
            // dynamics. No hand-written pose or substitute rig is used.
            for (int i = 0; i < 12; i++)
            {
                orbit.yaw = 165f + i * 3f;
                yield return Capture("motion-" + i.ToString("00"));
                // A short looping clip may finish below its starting time.
                // Require observed advancement, not a larger final timestamp.
                double motionTime = app.PhotoMotionTime;
                _report.animationResumed |= app.PhotoMotionPlaying && motionTime > previousMotionTime;
                previousMotionTime = motionTime;
            }
            _report.resumeEndTime = app.PhotoMotionTime;
            _report.animationResumed &= app.PhotoMotionPlaying;
            File.WriteAllText(Path.Combine(_directory, "rendering-probes.json"), JsonUtility.ToJson(_report, true));
            Debug.Log("[ActorRenderingValidation] Captured " + _report.frames.Count + " probes to " + _directory);
            Application.Quit(_report.repeatChangedPixels == 0 &&
                _report.profileRestoreChangedPixels == 0 && _report.lightRemovalChangedPixels == 0 &&
                _report.skinRestoreChangedPixels == 0 && _report.ambientRestoreChangedPixels == 0 &&
                _report.additionalRestoreChangedPixels == 0 && _report.pausedGraphStopped &&
                _report.pausedOrbitPosePreserved && _report.pausedOrbitComparedFrames == 11 &&
                _report.animationResumed ? 0 : 2);
        }

        private IEnumerator Capture(string name, float? expectedSkin = null, float? expectedGi = null, int? expectedLights = null, bool? expectedWorldSpace = null)
        {
            // Let the camera, controls and material bindings settle first.
            for (int frame = 0; frame < 24; frame++) yield return null;
            // Different lighting/camera states must not inherit the previous
            // state's accumulated image. Normal playback still keeps history.
            _pipeline.ResetTemporalHistory();
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--trace-rendering-stability") >= 0 &&
                (name == "01-front" || name == "01b-front-repeat" || name == "11-profile-restored"))
                OriginalStyleRenderPipeline.RequestPostInputDump(Path.Combine(_directory, name));
            if (name == "04-quarter" && Array.IndexOf(Environment.GetCommandLineArgs(), "--capture-actor-rendering-pass") >= 0)
            {
                if (!RenderDocCaptureBridge.TriggerCapture()) { Application.Quit(2); yield break; }
                yield return null;
                yield return null;
            }
            yield return new WaitForEndOfFrame();
            if (name == "05c-back")
                foreach (HairDynamicsSystem dynamics in FindObjectsOfType<HairDynamicsSystem>())
                    dynamics.LogCurrentState("rear-hair");
            if (expectedSkin.HasValue && Shader.GetGlobalFloat("_CapturedSkinSaturation") != expectedSkin.Value)
                throw new InvalidOperationException("Skin saturation probe input was overwritten: " + name);
            if (expectedGi.HasValue && Shader.GetGlobalVector("_ActorLightingScales").x != expectedGi.Value)
                throw new InvalidOperationException("Ambient probe input was overwritten: " + name);
            if (expectedLights.HasValue && _controls.AdditionalLightCount != expectedLights.Value)
                throw new InvalidOperationException("Additional light probe input differs: " + name);
            if (expectedWorldSpace.HasValue && Shader.GetGlobalVector("_CapturedLightDirection").w != (expectedWorldSpace.Value ? 1f : 0f))
                throw new InvalidOperationException("Light-space probe input was overwritten: " + name);
            Texture2D image = ScreenCapture.CaptureScreenshotAsTexture();
            if (image == null) throw new InvalidOperationException("Presented-frame readback failed: " + name);
            if (name == "01-front" || name == "18-skin-neutral" || name == "15a-fullbody-noambient" ||
                name == "13a-fullbody-no-lights") _repeatReference = image.GetPixels32();
            if (name == "01b-front-repeat" || name == "11-profile-restored" ||
                name == "13-point-lights-removed" || name == "18c-skin-restored" || name == "15c-fullbody-restored" ||
                name == "13g-fullbody-lights-restored")
            {
                Color32[] pixels = image.GetPixels32();
                int changed = 0, maximum = 0;
                if (pixels.Length != _repeatReference.Length)
                    throw new InvalidOperationException("Repeat capture dimensions changed.");
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 a = _repeatReference[i], b = pixels[i];
                    int delta = Mathf.Max(Mathf.Abs(a.r-b.r), Mathf.Abs(a.g-b.g), Mathf.Abs(a.b-b.b), Mathf.Abs(a.a-b.a));
                    if (delta != 0) changed++;
                    maximum = Mathf.Max(maximum, delta);
                }
                if (name == "01b-front-repeat")
                {
                    _report.repeatChangedPixels = changed;
                    _report.repeatMaxChannelDifference = maximum;
                }
                else if (name == "11-profile-restored") _report.profileRestoreChangedPixels = changed;
                else if (name == "18c-skin-restored") { _report.skinRestoreChangedPixels = changed; _repeatReference = null; }
                else if (name == "15c-fullbody-restored") { _report.ambientRestoreChangedPixels = changed; _repeatReference = null; }
                else if (name == "13g-fullbody-lights-restored") { _report.additionalRestoreChangedPixels = changed; _repeatReference = null; }
                else { _report.lightRemovalChangedPixels = changed; _repeatReference = null; }
                string reference = name == "18c-skin-restored" ? "18-skin-neutral" :
                    name == "15c-fullbody-restored" ? "15a-fullbody-noambient" :
                    name == "13g-fullbody-lights-restored" ? "13a-fullbody-no-lights" : "01-front";
                Debug.Log("[ActorRenderingValidation] " + name + " vs " + reference + ": changed pixels=" + changed +
                    " max channel difference=" + maximum);
            }
            if (name == "01-front" && (_controls.OutlineDrawCount == 0 || _controls.HairCoverDrawCount == 0))
            {
                Camera sourceCamera = GetComponent<Camera>();
                Debug.LogError("[ActorRenderingValidation] Camera enabled=" + sourceCamera.enabled +
                    "; active=" + sourceCamera.gameObject.activeInHierarchy + "; mask=" + sourceCamera.cullingMask +
                    "; controls=" + _controls.enabled + "; outline=" + _controls.OutlineDrawCount +
                    "; cover=" + _controls.HairCoverDrawCount + "; renderPipeline=" +
                    UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline);
                foreach (Renderer renderer in GetActorProbeRenderers())
                    Debug.LogError("[ActorRenderingValidation] Renderer " + renderer.name +
                        "; enabled=" + renderer.enabled + "; active=" + renderer.gameObject.activeInHierarchy +
                        "; forceOff=" + renderer.forceRenderingOff + "; layer=" + renderer.gameObject.layer);
                var privateFields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var commands = (UnityEngine.Rendering.CommandBuffer)typeof(ActorRenderControls).GetField("_commands", privateFields).GetValue(_controls);
                var bound = (Renderer[])typeof(ActorRenderControls).GetField("_renderers", privateFields).GetValue(_controls);
                var shader = (Shader)typeof(ActorRenderControls).GetField("_shader", privateFields).GetValue(_controls);
                Debug.LogError("[ActorRenderingValidation] Command bytes=" + commands.sizeInBytes + "; boundRenderers=" + bound.Length);
                foreach (Renderer renderer in bound)
                    if (renderer != null) foreach (Material material in renderer.sharedMaterials)
                        if (material != null) Debug.LogError("[ActorRenderingValidation] Bound material " + material.name +
                            "; matchingShader=" + (material.shader == shader) + "; outlineEnabled=" + material.GetFloat("_OutlineEnabled") +
                            "; outlinePass=" + material.GetShaderPassEnabled("ActorOutline") + "; coverPass=" + material.GetShaderPassEnabled("ActorHairCover"));
                Destroy(image);
                Debug.LogError("[ActorRenderingValidation] Required actor passes are missing; check shader stripping and material bindings.");
                Application.Quit(2);
                yield break;
            }
            File.WriteAllBytes(Path.Combine(_directory, name + ".png"), image.EncodeToPNG());
            PhotoModeApp app = FindObjectOfType<PhotoModeApp>();
            string poseDigest = ActorPoseDigest();
            if (name == "01-front")
            {
                _pausedPoseDigest = poseDigest;
                _pausedMotionTime = app.PhotoMotionTime;
                _report.pausedGraphStopped = !app.PhotoMotionPlaying;
                _report.pausedOrbitPosePreserved = true;
            }
            else if (name.StartsWith("01", StringComparison.Ordinal) ||
                name.StartsWith("02", StringComparison.Ordinal) || name.StartsWith("03", StringComparison.Ordinal) ||
                name.StartsWith("04", StringComparison.Ordinal) || name.StartsWith("05", StringComparison.Ordinal))
            {
                _report.pausedOrbitComparedFrames++;
                _report.pausedGraphStopped &= !app.PhotoMotionPlaying;
                _report.pausedOrbitPosePreserved &= poseDigest == _pausedPoseDigest &&
                    app.PhotoMotionTime == _pausedMotionTime;
            }
            _report.frames.Add(new Frame {
                name = name, width = image.width, height = image.height,
                actorPoseDigest = poseDigest, expression = app.CurrentExpression,
                photoMotionTime = app.PhotoMotionTime, photoMotionPlaying = app.PhotoMotionPlaying,
                outlineDraws = _controls.OutlineDrawCount, hairCoverDraws = _controls.HairCoverDrawCount,
                additionalLights = _controls.AdditionalLightCount, cameraPosition = transform.position,
                matcapParameters = Shader.GetGlobalVector("_ActorMatcapParameters"),
                keyColor = Shader.GetGlobalVector("_ActorKeyColor"),
                profileLightColor = Shader.GetGlobalVector("_CapturedLightColor"),
                lightingScales = Shader.GetGlobalVector("_ActorLightingScales"),
                lightDirection = Shader.GetGlobalVector("_CapturedLightDirection"),
                skinSaturationDelta = Shader.GetGlobalFloat("_CapturedSkinSaturation")
            });
            Destroy(image);
            Debug.Log("[ActorRenderingValidation] " + name);
        }

        private static Renderer[] GetActorProbeRenderers()
        {
            PhotoModeApp app = FindObjectOfType<PhotoModeApp>();
            return app != null && app.CharacterRoot != null
                ? app.CharacterRoot.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
        }

        private static string ActorPoseDigest()
        {
            // Same-process diagnostic only: instance IDs define a stable order,
            // not a cross-process rig identifier. No pose writes are performed.
            Transform[] transforms = FindObjectsOfType<Transform>();
            Array.Sort(transforms, (left, right) => left.GetInstanceID().CompareTo(right.GetInstanceID()));
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            using (var hash = SHA256.Create())
            {
                foreach (Transform value in transforms)
                {
                    if (value.gameObject.layer != OriginalStyleRenderPipeline.ActorLayer) continue;
                    writer.Write(value.GetInstanceID());
                    Matrix4x4 matrix = value.localToWorldMatrix;
                    for (int i = 0; i < 16; i++) writer.Write(matrix[i]);
                    var skin = value.GetComponent<SkinnedMeshRenderer>();
                    if (skin != null && skin.sharedMesh != null)
                        for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++) writer.Write(skin.GetBlendShapeWeight(i));
                }
                writer.Flush();
                return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
