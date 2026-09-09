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
            public int width, height, outlineDraws, hairCoverDraws, additionalLights;
            public Vector3 cameraPosition;
            public Vector4 matcapParameters, lightingScales, lightDirection;
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
            public List<Frame> frames = new List<Frame>();
        }

        private string _directory;
        private ActorRenderControls _controls;
        private OriginalStyleRenderPipeline _pipeline;
        private Color32[] _repeatReference;
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
            yield return Capture("09-world-light");
            // Strong lateral directions expose the reflected face-triangle
            // response; the default studio light can leave it almost inactive.
            _controls.lightAngle = new Vector2(0f, 90f);
            yield return Capture("09a-world-light-left");
            _controls.lightAngle = new Vector2(0f, -90f);
            yield return Capture("09b-world-light-right");
            _controls.lightAngle = new Vector2(25f, 150f);
            orbit.yaw = 225f;
            yield return Capture("10-world-light-orbit");
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
            _controls.giScale = 0f;
            _controls.rimColor = new Color(0.1f, 0.8f, 0.2f);
            yield return Capture("16-colored-rim");
            _controls.overrideLighting = false;
            _controls.showPanel = true;
            yield return Capture("17-render-panel");
            _controls.showPanel = false;
            Time.timeScale = 1f;
            app.TogglePause();
            // This sequence deliberately resumes the existing animation and
            // dynamics. No hand-written pose or substitute rig is used.
            for (int i = 0; i < 12; i++)
            {
                orbit.yaw = 165f + i * 3f;
                yield return Capture("motion-" + i.ToString("00"));
            }
            File.WriteAllText(Path.Combine(_directory, "rendering-probes.json"), JsonUtility.ToJson(_report, true));
            Debug.Log("[ActorRenderingValidation] Captured " + _report.frames.Count + " probes to " + _directory);
            Application.Quit(_report.repeatChangedPixels == 0 &&
                _report.profileRestoreChangedPixels == 0 && _report.lightRemovalChangedPixels == 0 ? 0 : 2);
        }

        private IEnumerator Capture(string name)
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
            Texture2D image = ScreenCapture.CaptureScreenshotAsTexture();
            if (image == null) throw new InvalidOperationException("Presented-frame readback failed: " + name);
            if (name == "01-front") _repeatReference = image.GetPixels32();
            if (name == "01b-front-repeat" || name == "11-profile-restored" || name == "13-point-lights-removed")
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
                else { _report.lightRemovalChangedPixels = changed; _repeatReference = null; }
                Debug.Log("[ActorRenderingValidation] " + name + " vs 01-front: changed pixels=" + changed +
                    " max channel difference=" + maximum);
            }
            if (name == "01-front" && (_controls.OutlineDrawCount == 0 || _controls.HairCoverDrawCount == 0))
            {
                Destroy(image);
                Debug.LogError("[ActorRenderingValidation] Required actor passes are missing; check shader stripping and material bindings.");
                Application.Quit(2);
                yield break;
            }
            File.WriteAllBytes(Path.Combine(_directory, name + ".png"), image.EncodeToPNG());
            _report.frames.Add(new Frame {
                name = name, width = image.width, height = image.height,
                actorPoseDigest = ActorPoseDigest(),
                outlineDraws = _controls.OutlineDrawCount, hairCoverDraws = _controls.HairCoverDrawCount,
                additionalLights = _controls.AdditionalLightCount, cameraPosition = transform.position,
                matcapParameters = Shader.GetGlobalVector("_ActorMatcapParameters"),
                lightingScales = Shader.GetGlobalVector("_ActorLightingScales"),
                lightDirection = Shader.GetGlobalVector("_CapturedLightDirection")
            });
            Destroy(image);
            Debug.Log("[ActorRenderingValidation] " + name);
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
