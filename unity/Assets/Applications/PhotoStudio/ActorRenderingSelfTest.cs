using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    // Observe inside the render callback: Unity restores camera-global depth
    // after Camera.Render returns, so sampling it afterwards tests stale state.
    internal sealed class SphereFogDepthProbe : MonoBehaviour
    {
        public Action<RenderTexture> sample;
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (sample != null) sample(source);
            Graphics.Blit(source, destination);
        }
    }

    /// <summary>Asset-free rendering contracts using generated geometry and rigs.</summary>
    public sealed partial class ActorRenderingSelfTest : MonoBehaviour
    {
        [Serializable] private sealed class Check
        {
            public string name;
            public bool accepted;
            public Color expected, actual;
            public float maximumDifference;
        }
        [Serializable] private sealed class Report
        {
            public string schema = "photo-studio.actor-synthetic.v1";
            public string graphicsDevice;
            public string error;
            public bool accepted;
            public List<Check> checks = new List<Check>();
        }

        private string _directory;
        private Camera _camera;
        private RenderTexture _target;
        private Texture2D _readback;
        private Texture2D _preview;
        private Material _material;
        private Mesh _quad;
        private MeshRenderer _quadRenderer;
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();

        public static bool TryStart(GameObject owner)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--self-test-actor-rendering");
            if (index < 0) return false;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Debug.LogError("[ActorSelfTest] An output directory is required.");
                Application.Quit(2);
                return true;
            }
            try
            {
                string directory = Path.GetFullPath(args[index + 1]);
                owner.AddComponent<ActorRenderingSelfTest>()._directory = directory;
            }
            catch (Exception error)
            {
                Debug.LogError("[ActorSelfTest] Invalid output directory: " + error.Message);
                Application.Quit(2);
            }
            return true;
        }

        private IEnumerator Start()
        {
            yield return null;
            var report = new Report { graphicsDevice = SystemInfo.graphicsDeviceVersion };
            try
            {
                Directory.CreateDirectory(_directory);
                if (GraphicsSettings.currentRenderPipeline != null)
                    throw new InvalidOperationException("Synthetic contracts require the ordinary Built-in Player.");
                SetUp();
                // Head and surface normals are identical here. Both ramp paths
                // must therefore agree at every offset, not just the default.
                foreach (float offset in new[] { 0.3f, -0.5f, 0.8f })
                {
                    Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(offset, 1f, 1f, 0f));
                    _material.SetFloat("_ShaderType", 0f);
                    Color expected = Render("surface-offset-" + offset.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    _material.SetFloat("_ShaderType", 9f);
                    Color actual = Render("head-offset-" + offset.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    float difference = Mathf.Max(Mathf.Abs(expected.r-actual.r), Mathf.Abs(expected.g-actual.g), Mathf.Abs(expected.b-actual.b));
                    bool finite = !float.IsNaN(difference) && !float.IsInfinity(difference);
                    report.checks.Add(new Check { name = "equal-head-surface-offset-" + offset,
                        expected = expected, actual = actual, maximumDifference = difference,
                        accepted = finite && difference <= 0.00001f });
                }
                // Positive-control ramp response rejects an empty/constant render.
                Color dark = report.checks[2].expected, bright = report.checks[1].expected;
                float response = Mathf.Abs(dark.r-bright.r);
                report.checks.Add(new Check { name = "nonconstant-ramp-positive-control", expected = bright,
                    actual = dark, maximumDifference = response, accepted = response > 0.05f });
                VerifyStraightAlpha(report);
                VerifyViewProfileCorrection(report);
                VerifyHeadReflection(report);
                VerifyDynamicPresentation(report);
                VerifySkinSaturation(report);
                VerifyMaterialBaseTint(report);
                VerifyHairSpecularRegions(report);
                VerifyHairHighlightBasis(report);
                VerifyMainSpecularBasis(report);
                VerifyReflectionSphere(report);
                VerifyEnvironmentCoordinates(report);
                VerifyProjectionViewDirection(report);
                VerifyEyebrowHighlight(report);
                VerifyLayerControl(report);
                VerifyRimControl(report);
                VerifyMaterialSequence(report);
                VerifyRampAddSpecular(report);
                VerifyRampAddSignedView(report);
                VerifyAmbientMaterialResponse(report);
                VerifyAmbientInputContext(report);
                VerifyAdditionalLighting(report);
                VerifyHairCoverComposition(report);
                VerifyOutlineDepth(report);
                VerifyPresentationOwnership(report);
                VerifyCapturedMaterialUv(report);
                VerifyCapturedCamera(report);
                VerifyShadowSubtexelFiltering(report);
                VerifySceneDistanceFog(report);
                VerifySphereFog(report);
                VerifyNaturalWind(report);
                VerifyTemporalClassification(report);
                VerifyActorVertexEncoding(report);
                VerifySceneDepthData(report);
                VerifyScreenSpaceReflection(report);
                VerifyPlanarReflection(report);
                VerifySceneReflectionResolve(report);
                VerifyComputeDepthOracle(report);
                VerifyScreenSpaceReflection(report, SceneShaderBackend.Compute);
                VerifySsrRoughnessOracle(report);
                VerifyActorPlanarCapture(report);
                VerifyHdrMonitor(report);
                VerifySceneDeferred(report);
                VerifySceneDecalLights(report);
                VerifySceneGi(report);
                report.accepted = report.checks.TrueForAll(check => check.accepted);
            }
            catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
            finally
            {
                if (_camera != null) _camera.targetTexture = null;
                if (_target != null) _target.Release();
                foreach (UnityEngine.Object value in _owned) if (value != null) Destroy(value);
            }
            string bakeBundle = Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_GI_BAKE_BUNDLE");
            if (report.error == null && !string.IsNullOrEmpty(bakeBundle))
            {
                // Keep the historical suite before additive scene/probe state. Drive the
                // iterator explicitly so asynchronous fixture failures enter the same report.
                _owned.Clear();
                var fixture = VerifyRealGiBake(report, bakeBundle);
                while (true)
                {
                    bool more; object next = null;
                    try { more = fixture.MoveNext(); if (more) next = fixture.Current; }
                    catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); break; }
                    if (!more) break;
                    yield return next;
                }
                (fixture as IDisposable)?.Dispose();
                report.accepted = report.error == null && report.checks.TrueForAll(check => check.accepted);
            }
            if (!string.IsNullOrEmpty(_directory) && Directory.Exists(_directory))
                File.WriteAllText(Path.Combine(_directory, "actor-synthetic.json"), JsonUtility.ToJson(report, true));
            Debug.Log("[ActorSelfTest] accepted=" + report.accepted + "; checks=" + report.checks.Count);
            Application.Quit(report.accepted ? 0 : 2);
        }

        private T Own<T>(T value) where T : UnityEngine.Object { _owned.Add(value); return value; }

        private void VerifyStraightAlpha(Report report)
        {
            Material saved = _material;
            Color savedBackground = _camera.backgroundColor;
            float savedDebug = Shader.GetGlobalFloat("_FaceDebugMode");
            try
            {
                _material = Own(new Material(saved));
                _quadRenderer.sharedMaterial = _material;
                // Exercise production output, not a debug return that forces alpha 1.
                Shader.SetGlobalFloat("_FaceDebugMode", 0f);
                _camera.backgroundColor = new Color(0.17f, 0.43f, 0.71f, 0.65f);
                _quadRenderer.enabled = false;
                Color background = Render("straight-alpha-background");
                _quadRenderer.enabled = true;
                _material.SetFloat("_ZWrite", 0f);
                _material.SetFloat("_Cutoff", 0f);
                _material.SetFloat("_UseEmission", 1f);
                _material.SetTexture("_EmissionMap", Texture2D.whiteTexture);
                _material.SetColor("_EmissionColor", new Color(0.6f, 0.7f, 0.8f, 1f));
                var texture = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
                _material.SetTexture("_MainTex", texture);
                // Texture alpha and material alpha are independent inputs. Include
                // zero, fractional, opaque and the authored 240/255 overlay opacity.
                Vector2[] inputs = { new Vector2(0f, 1f), new Vector2(0.25f, 1f),
                    new Vector2(0.5f, 0.5f), new Vector2(1f, 240f/255f),
                    new Vector2(1f, 1f), new Vector2(1f, 0f) };
                foreach (int type in new[] { 0, 8 })
                for (int i = 0; i < inputs.Length; i++)
                {
                    string prefix = "straight-alpha-type-" + type + "-input-" + i;
                    _material.SetFloat("_ShaderType", type);
                    texture.SetPixel(0, 0, new Color(0.8f, 0.5f, 0.3f, inputs[i].x));
                    texture.Apply();
                    _material.SetColor("_Color", new Color(1f, 1f, 1f, inputs[i].y));
                    _material.SetFloat("_SrcBlend", 1f);
                    _material.SetFloat("_DstBlend", 0f);
                    _material.SetFloat("_SrcAlphaBlend", 1f);
                    _material.SetFloat("_DstAlphaBlend", 0f);
                    Color source = Render(prefix + "-opaque");
                    AddColorCheck(report, prefix + "-opaque-alpha", new Color(source.r, source.g, source.b, 1f), source);
                    report.checks.Add(new Check { name = prefix + "-nonblack-source",
                        actual = source, accepted = source.r > 0.1f && source.g > 0.1f && source.b > 0.1f });
                    float alpha = inputs[i].x * inputs[i].y;
                    _material.SetFloat("_SrcBlend", 5f);
                    _material.SetFloat("_DstBlend", 10f);
                    _material.SetFloat("_SrcAlphaBlend", 0f);
                    _material.SetFloat("_DstAlphaBlend", 10f);
                    Color expected = source * alpha + background * (1f - alpha);
                    expected.a = background.a * (1f - alpha);
                    AddColorCheck(report, prefix + "-destination-alpha", expected, Render(prefix + "-destination-alpha"));
                    _material.SetFloat("_SrcAlphaBlend", 1f);
                    _material.SetFloat("_DstAlphaBlend", 0f);
                    expected.a = alpha;
                    AddColorCheck(report, prefix + "-source-alpha", expected, Render(prefix + "-source-alpha"));
                    // SrcAlpha additive uses the same unpremultiplied source.
                    _material.SetFloat("_DstBlend", 1f);
                    expected = source * alpha + background;
                    expected.a = alpha;
                    AddColorCheck(report, prefix + "-additive", expected, Render(prefix + "-additive"));
                }
            }
            finally
            {
                _material = saved;
                _quadRenderer.sharedMaterial = saved;
                _quadRenderer.enabled = true;
                _camera.backgroundColor = savedBackground;
                Shader.SetGlobalFloat("_FaceDebugMode", savedDebug);
            }
        }

        private void VerifyViewProfileCorrection(Report report)
        {
            var owner = Own(new GameObject("Generated view profile face"));
            var source = owner.AddComponent<VL.FaceSystem.VLActorFaceModel>();
            Vector3[] vertices = { new Vector3(0, 0, 0.08f), new Vector3(0, -0.06f, 0.02f), new Vector3(0.01f, 0.02f, 0.01f) };
            var mesh = Own(new Mesh()); mesh.vertices = vertices; mesh.triangles = new[] { 0, 1, 2 };
            var filter = owner.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            owner.AddComponent<MeshRenderer>().sharedMaterial = _material;
            source.blendShapes.Add(new VL.FaceSystem.VLFaceBlendShape { blendShapeName = "expression" });
            source.blendShapes.Add(new VL.FaceSystem.VLFaceBlendShape { blendShapeName = "side090", blendShapeVertices =
                new List<VL.FaceSystem.VLFaceBlendShapeVertex> {
                    new VL.FaceSystem.VLFaceBlendShapeVertex { vertIndex = 0, position = Vector3.back * 0.006f },
                    new VL.FaceSystem.VLFaceBlendShapeVertex { vertIndex = 2, position = Vector3.right * 0.001f } } });
            source.blendShapes.Add(new VL.FaceSystem.VLFaceBlendShape { blendShapeName = "under045", blendShapeVertices =
                new List<VL.FaceSystem.VLFaceBlendShapeVertex> {
                    new VL.FaceSystem.VLFaceBlendShapeVertex { vertIndex = 1, position = Vector3.down * 0.01f } } });
            var correction = owner.AddComponent<Campus.Common.CampusActorFaceCorrection>();
            correction.faceModel = source;
            correction.blendShapeIndices = new[] { 2, 1 };
            // Generated asymmetric tangents make both the rise and the authored
            // falloff observable. No private curve or vertex data is embedded.
            correction.curves = new[] { AnimationCurve.Linear(-45f, 1f, 0f, 0f), new AnimationCurve(
                new Keyframe(40, 0, 0, 0.025f), new Keyframe(80, 1, 0.025f, -0.05f),
                new Keyframe(100, 0, -0.05f, 0), new Keyframe(180, 0, 0, 0)) };
            Transform head = Own(new GameObject("Generated view head")).transform;
            Camera camera = Own(new GameObject("Generated view camera")).AddComponent<Camera>(); camera.enabled = false;
            var face = owner.AddComponent<FaceExpressionRenderer>();
            try
            {
                bool initialized = face.Initialize(source, source);
                face.SetFaceCorrectionPoseTarget(head); face.InitializeGaze(null, null, camera);
                face.SetAutomaticBlinkEnabled(false);
                report.checks.Add(new Check { name = "view-profile-resolves-authored-shape-not-fixed-index",
                    accepted = initialized && face.ViewProfileCorrectionAvailable && face.ViewProfileCorrectionEnabled });
                float[] angles = { 0, 30, 60, 80, 90, 100, 180, -60 };
                float[] expectedWeights = { 0, 0, 0.5f, 1, 0.5f, 0, 0, 0.5f };
                for (int pose = 0; pose < 2; pose++)
                for (int orthographic = 0; orthographic < 2; orthographic++)
                for (int i = 0; i < angles.Length; i++)
                {
                    head.SetPositionAndRotation(new Vector3(2, 3, -4), pose == 0 ? Quaternion.identity : Quaternion.Euler(-35, 123, 47));
                    float angle = angles[i] * Mathf.Deg2Rad;
                    Vector3 view = head.right * Mathf.Sin(angle) + head.forward * Mathf.Cos(angle) + head.up * 0.7f;
                    camera.orthographic = orthographic != 0;
                    camera.transform.SetPositionAndRotation(head.position + (orthographic == 0 ? view * 4f : Vector3.one * 123f),
                        Quaternion.LookRotation(-view, head.up));
                    face.ApplyCurrentWeights(); Vector3[] actual = filter.sharedMesh.vertices;
                    float weight = expectedWeights[i];
                    string name = "view-profile-pose-" + pose + "-ortho-" + orthographic + "-yaw-" + angles[i];
                    AddColorCheck(report, name + "-angle", new Color(Mathf.Abs(angles[i])/180f, 0, 0, 1),
                        new Color(face.ViewProfileAngle/180f, 0, 0, 1));
                    Vector3 expected = vertices[0] + Vector3.back * 0.006f * weight;
                    AddColorCheck(report, name + "-nose", new Color(expected.x, expected.y, expected.z, weight),
                        new Color(actual[0].x, actual[0].y, actual[0].z, face.ViewProfileWeight));
                    AddColorCheck(report, name + "-chin", new Color(vertices[1].x, vertices[1].y, vertices[1].z, 1),
                        new Color(actual[1].x, actual[1].y, actual[1].z, 1));
                    expected = vertices[2] + Vector3.right * 0.001f * weight;
                    AddColorCheck(report, name + "-whole-authored-shape", new Color(expected.x, expected.y, expected.z, 1),
                        new Color(actual[2].x, actual[2].y, actual[2].z, 1));
                }
                source.SetWeight(1, 0.7f); source.SetWeight(2, 0.2f); face.ApplyCurrentWeights();
                Vector3[] authored = filter.sharedMesh.vertices;
                AddColorCheck(report, "view-profile-preserves-authored-weights", new Color(vertices[0].z - 0.006f * 0.7f, vertices[1].y - 0.01f * 0.2f, 0.7f, 0.2f),
                    new Color(authored[0].z, authored[1].y, source.GetWeight(1), source.GetWeight(2)));
                source.ClearWeights(); face.ViewProfileCorrectionEnabled = false; face.ApplyCurrentWeights();
                AddColorCheck(report, "view-profile-disabled-restores-base", new Color(vertices[0].z, vertices[2].x, 0, 1),
                    new Color(filter.sharedMesh.vertices[0].z, filter.sharedMesh.vertices[2].x, face.ViewProfileWeight, 1));
                face.ViewProfileCorrectionEnabled = true; face.SetDebugShape(1, 0.25f);
                AddColorCheck(report, "view-profile-manual-debug-owns-weight", new Color(vertices[0].z - 0.006f * 0.25f, 0, 0, 1),
                    new Color(filter.sharedMesh.vertices[0].z, face.ViewProfileWeight, 0, 1));
                face.ClearDebugShape(); face.SetFaceCorrectionPoseTarget(null); face.ApplyCurrentWeights();
                report.checks.Add(new Check { name = "view-profile-missing-head-is-neutral", accepted = face.ViewProfileWeight == 0f });
                head.rotation = Quaternion.identity; camera.orthographic = false; camera.transform.position = head.position;
                report.checks.Add(new Check { name = "view-profile-zero-and-invalid-direction-is-neutral",
                    accepted = FaceExpressionRenderer.ViewProfileYaw(head, camera, head.position) == 0f &&
                        FaceExpressionRenderer.ViewProfileYaw(head, camera, new Vector3(float.NaN, 0, 0)) == 0f });
                source.blendShapes[1].blendShapeName = "unrelated";
                face.SendMessage("ResolveViewProfileCorrection");
                report.checks.Add(new Check { name = "view-profile-unrelated-shape-not-selected", accepted = !face.ViewProfileCorrectionAvailable });
            }
            finally { owner.SetActive(false); }
        }

        private static object DynamicField(object node, string name)
        {
            return node.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(node);
        }

        private void VerifyDynamicPresentation(Report report)
        {
            // Two independent, generated chains exercise the actual presentation
            // method between simulation steps. No original rig or sampled pose.
            var owner = Own(new GameObject("Synthetic dynamic presentation"));
            var anchors = new Transform[2];
            for (int chain = 0; chain < anchors.Length; chain++)
            {
                anchors[chain] = new GameObject("Attachment " + chain).transform;
                anchors[chain].SetParent(owner.transform, false);
                anchors[chain].localPosition = new Vector3(chain * 0.4f, 2f, 0f);
                Transform parent = anchors[chain];
                for (int link = 0; link < 4; link++)
                {
                    var bone = new GameObject("Chain " + chain + " link " + link);
                    bone.transform.SetParent(parent, false);
                    bone.transform.localPosition = link == 0 ? new Vector3(0.04f, 0f, 0f) : new Vector3(0f, -0.1f, 0.01f);
                    bone.AddComponent<ActorAnimation.ActorSwingDynamicBone>().wind = 0f;
                    parent = bone.transform;
                }
            }
            var dynamics = owner.AddComponent<HairDynamicsSystem>();
            dynamics.gravityStrength = 0f;
            dynamics.collisionStrength = 0f;
            dynamics.Initialize(anchors[0], anchors[0]);
            dynamics.enabled = false;
            var nodes = new List<object>();
            foreach (object node in (IEnumerable)typeof(HairDynamicsSystem).GetField("_nodes",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dynamics)) nodes.Add(node);
            report.checks.Add(new Check { name = "dynamic-presentation-generated-rig",
                accepted = nodes.Count == 8 && dynamics.SimulatedBoneCount == 6 });
            string[] states = { "stationary", "translated", "rotated", "held-repeat", "restored", "after-step" };
            foreach (string state in states)
            {
                if (state == "translated" || state == "after-step")
                    for (int chain = 0; chain < anchors.Length; chain++)
                        anchors[chain].localPosition += new Vector3(0.006f * (chain + 1), -0.003f, 0.002f * (1 - 2 * chain));
                if (state == "rotated")
                    for (int chain = 0; chain < anchors.Length; chain++)
                        anchors[chain].localRotation = Quaternion.Euler(15f, 35f * (1 - 2 * chain), -10f);
                if (state == "restored")
                    for (int chain = 0; chain < anchors.Length; chain++)
                    {
                        anchors[chain].localPosition = new Vector3(chain * 0.4f, 2f, 0f);
                        anchors[chain].localRotation = Quaternion.identity;
                    }
                dynamics.SendMessage("ResetNodesToBasePose");
                dynamics.SendMessage("CaptureAuthoredPose");
                if (state == "after-step")
                    typeof(HairDynamicsSystem).GetMethod("SimulateStep", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(dynamics, new object[] { 0.01667f, false, true, false });
                string[] fields = { "position", "rotation", "childSpeed", "defaultPosition", "defaultRotation", "defaultWorldRotation", "ready" };
                // Nonzero history makes an accidental velocity reset observable.
                for (int i = 0; i < nodes.Count; i++)
                    nodes[i].GetType().GetField("childSpeed").SetValue(nodes[i], new Vector3(0.01f * (i + 1), -0.02f, 0.03f));
                var cached = new object[nodes.Count, fields.Length];
                for (int i = 0; i < nodes.Count; i++)
                    for (int j = 0; j < fields.Length; j++) cached[i,j] = DynamicField(nodes[i], fields[j]);
                // Repeat presentation must also leave the solver cache intact.
                dynamics.SendMessage("ApplyRuntimePose");
                dynamics.SendMessage("ApplyRuntimePose");
                float rootError = 0f, segmentError = 0f, rotationError = 0f, steppedError = 0f;
                bool cachePreserved = true;
                foreach (object node in nodes)
                {
                    int i = nodes.IndexOf(node);
                    Transform bone = (Transform)DynamicField(node,"bone");
                    object parent = DynamicField(node,"parent");
                    if (parent == null)
                        rootError = Mathf.Max(rootError, Vector3.Distance(bone.position, (Vector3)DynamicField(node,"authoredPosition")));
                    else
                    {
                        Transform parentBone = (Transform)DynamicField(parent,"bone");
                        Vector3 heldSegment = (Vector3)cached[i,0] - (Vector3)cached[nodes.IndexOf(parent),0];
                        segmentError = Mathf.Max(segmentError, Vector3.Distance(bone.position-parentBone.position, heldSegment));
                    }
                    rotationError = Mathf.Max(rotationError, Quaternion.Angle(bone.rotation, (Quaternion)cached[i,1]));
                    steppedError = Mathf.Max(steppedError, Vector3.Distance(bone.position,(Vector3)cached[i,0]));
                    for (int j = 0; j < fields.Length; j++) cachePreserved &= cached[i,j].Equals(DynamicField(node,fields[j]));
                }
                report.checks.Add(new Check { name = "dynamic-presentation-root-" + state,
                    maximumDifference = rootError, accepted = rootError < 0.000001f });
                report.checks.Add(new Check { name = "dynamic-presentation-segments-" + state,
                    maximumDifference = segmentError, accepted = segmentError < 0.000001f });
                report.checks.Add(new Check { name = "dynamic-presentation-rotations-" + state,
                    maximumDifference = rotationError, accepted = rotationError < 0.05f });
                report.checks.Add(new Check { name = "dynamic-presentation-cache-" + state, accepted = cachePreserved });
                if (state == "stationary" || state == "restored" || state == "after-step")
                    report.checks.Add(new Check { name = "dynamic-presentation-zero-offset-" + state,
                        maximumDifference = steppedError, accepted = steppedError < 0.000001f });
            }
        }

        private void VerifyHeadReflection(Report report)
        {
            var head = Own(new GameObject("Synthetic animated head"));
            var driver = head.AddComponent<ActorHeadLightingDriver>();
            driver.Initialize(head.transform);
            // A reflection reverses the head's right axis while preserving up
            // and forward. Test the actual publisher, including a later pose.
            foreach (Vector3 angles in new[] { Vector3.zero, new Vector3(15, 40, -12), new Vector3(-25, -65, 20) })
            {
                head.transform.rotation = Quaternion.Euler(angles);
                driver.SendMessage("LateUpdate");
                Vector3 x = Shader.GetGlobalVector("_HeadRightDirection");
                Vector3 y = Shader.GetGlobalVector("_HeadUpDirection");
                Vector3 z = Shader.GetGlobalVector("_HeadDirection");
                float determinant = Vector3.Dot(x, Vector3.Cross(y, z));
                report.checks.Add(new Check { name = "head-reflection-basis-" + angles,
                    maximumDifference = Mathf.Abs(determinant + 1f),
                    accepted = Mathf.Abs(determinant + 1f) < 0.00001f &&
                        (x + head.transform.right).sqrMagnitude < 0.00000001f &&
                        (y - head.transform.up).sqrMagnitude < 0.00000001f &&
                        (z - head.transform.forward).sqrMagnitude < 0.00000001f });
            }
            head.transform.rotation = Quaternion.identity;
            driver.SendMessage("LateUpdate");
            Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(1, 0, 0, 1));
            Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0.3f, 1, 1, 0));
            // With a +X light, a triangle-mask texel on the -X-facing side
            // must receive the same ramp as its reflected +X-facing normal.
            // Outside that mask the ordinary dark-side response must remain.
            Vector3 left = new Vector3(-0.6f, 0, -0.8f);
            Vector3 right = new Vector3(0.6f, 0, -0.8f);
            _quad.normals = new[] { right, right, right, right };
            _material.SetFloat("_ShaderType", 0f);
            Color reflected = Render("head-triangle-reference-lit-side");
            _quad.normals = new[] { left, left, left, left };
            Color unmasked = Render("head-triangle-reference-dark-side");
            _material.SetFloat("_ShaderType", 9f);
            Color masked = Render("head-triangle-reflected-light");
            float difference = Mathf.Abs(reflected.r - masked.r);
            report.checks.Add(new Check { name = "head-triangle-reflects-dark-side",
                expected = reflected, actual = masked, maximumDifference = difference,
                accepted = !float.IsNaN(difference) && difference < 0.00001f &&
                    reflected.r - unmasked.r > 0.4f });
            _material.SetVector("_DefValue", new Vector4(0.5f, 0, 0, 0));
            Color outside = Render("head-triangle-zero-mask");
            difference = Mathf.Abs(outside.r - unmasked.r);
            report.checks.Add(new Check { name = "head-triangle-preserves-zero-mask",
                expected = unmasked, actual = outside, maximumDifference = difference,
                accepted = !float.IsNaN(difference) && difference < 0.00001f });
            _quad.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            _material.SetVector("_DefValue", new Vector4(0.5f, 0, 1, 0));
            Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(0, 0, -1, 1));
            DestroyImmediate(head);
        }

        private void VerifySkinSaturation(Report report)
        {
            // Test the real diffuse path, before specular, rim and post effects.
            // Skin is a texture mask, including skin inside body materials;
            // neither the face material ID nor albedo brightness is the mask.
            var savedMaterial = Own(new Material(_material));
            string[] globals = { "_FaceDebugMode", "_CapturedDirectScale",
                "_CapturedDiffuseBlend", "_CapturedSkinSaturation" };
            float[] saved = Array.ConvertAll(globals, Shader.GetGlobalFloat);
            var baseMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var shadeMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var ramp = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            Color baseColor = new Color(0.8f, 0.35f, 0.15f, 1f);
            baseMap.SetPixel(0, 0, baseColor); baseMap.Apply();
            ramp.SetPixel(0, 0, new Color(1, 1, 1, 0)); ramp.Apply();
            try
            {
                _material.SetColor("_Color", Color.white);
                _material.SetTexture("_MainTex", baseMap);
                _material.SetTexture("_ShadeTex", shadeMap);
                _material.SetTexture("_RampTex", ramp);
                _material.SetTexture("_RampAddTex", Texture2D.blackTexture);
                _material.SetFloat("_EnableLayer", 0f);
                _material.SetFloat("_DisableDefMap", 1f);
                _material.SetVector("_DefValue", new Vector4(0.5f, 0, 0, 0));
                Shader.SetGlobalFloat("_FaceDebugMode", 19f);
                Shader.SetGlobalFloat("_CapturedDirectScale", 1f);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend", 1f);
                foreach (int type in new[] { 0, 1, 9 })
                {
                    _material.SetFloat("_ShaderType", type);
                    shadeMap.SetPixel(0, 0, Color.clear); shadeMap.Apply();
                    Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
                    Color baseline = Render("skin-type-" + type + "-identity");
                    Color expectedBase = baseColor * 0.96f; expectedBase.a = 1f;
                    AddColorCheck(report, "skin-type-" + type + "-base-positive-control", expectedBase, baseline);
                    float luma = baseline.r * 0.2126729f + baseline.g * 0.7151522f + baseline.b * 0.0721750f;
                    Color gray = new Color(luma, luma, luma, 1f);
                    foreach (float mask in new[] { 0f, 0.25f, 1f })
                    {
                        shadeMap.SetPixel(0, 0, new Color(0, 0, 0, mask)); shadeMap.Apply();
                        foreach (float delta in new[] { -1f, 0.5f })
                        {
                            Shader.SetGlobalFloat("_CapturedSkinSaturation", delta);
                            string name = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "skin-type-{0}-mask-{1}-delta-{2}", type, mask, delta);
                            Color expected = Color.LerpUnclamped(gray, baseline, 1f + delta * mask);
                            AddColorCheck(report, name, expected, Render(name));
                        }
                    }
                    Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
                    AddColorCheck(report, "skin-type-" + type + "-restored", baseline,
                        Render("skin-type-" + type + "-restored"));
                }
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial);
                for (int i = 0; i < globals.Length; i++) Shader.SetGlobalFloat(globals[i], saved[i]);
            }
        }

        private void VerifyMaterialBaseTint(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            Vector2[] savedUv = _quad.uv;
            string[] floats = { "_FaceDebugMode", "_CapturedDirectScale", "_CapturedDiffuseBlend",
                "_CapturedSkinSaturation", "_CapturedType5OutputScale" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedShadeTint", "_CapturedLightDirection", "_ActorKeyColor",
                "_CapturedLightColor", "_ActorRimColor", "_ActorEyeHighlightColor", "_ActorMatcapParameters" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            Texture2D Map(Color color)
            {
                var texture = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
                texture.SetPixel(0, 0, color); texture.Apply(); return texture;
            }
            Color baseColor = new Color(0.7f, 0.3f, 0.15f, 0.6f);
            Color shadeColor = new Color(0.2f, 0.4f, 0.55f, 0f);
            Color lookup = new Color(0.8f, 1.1f, 0.65f, 0.75f);
            Color shadeTint = new Color(0.9f, 0.7f, 1.3f, 1f);
            Color[] tints = { Color.white, new Color(0.25f, 1.5f, 0.6f, 1f), new Color(0f, 0f, 0f, 1f) };
            Texture2D shade = Map(shadeColor);
            try
            {
                _material.SetTexture("_MainTex", Map(baseColor)); _material.SetTexture("_ShadeTex", shade);
                _material.SetTexture("_RampTex", Map(lookup)); _material.SetTexture("_RampAddTex", Texture2D.blackTexture);
                _material.SetFloat("_EnableLayer", 0f); _material.SetFloat("_DisableDefMap", 1f);
                _material.SetVector("_DefValue", new Vector4(0.5f, 0f, 0f, 0f));
                _material.SetVector("_SpecularThreshold", new Vector4(10f, 10f, 0, 0));
                _material.SetFloat("_UseEmission", 0f); _material.SetFloat("_UseAlphaClip", 0f);
                Shader.SetGlobalFloat("_FaceDebugMode", 19f); Shader.SetGlobalFloat("_CapturedDirectScale", 1f);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend", 1f);
                Shader.SetGlobalVector("_CapturedShadeTint", shadeTint);
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0.3f, 1f, 1f, 0f));
                foreach (int type in new[] { 0, 4, 8, 9 }) foreach (float mask in new[] { 0f, 0.5f, 1f })
                {
                    _material.SetFloat("_ShaderType", type); shadeColor.a = mask;
                    shade.SetPixel(0, 0, shadeColor); shade.Apply();
                    Color raw = Color.Lerp(baseColor, shadeColor * shadeTint, lookup.a);
                    Color skin = baseColor * lookup * Color.Lerp(Color.white, shadeTint, lookup.a);
                    raw = Color.Lerp(raw, skin, mask);
                    foreach (float saturation in new[] { 0f, -1f }) for (int tint = 0; tint < tints.Length; tint++)
                    {
                        _material.SetVector("_Color", tints[tint]); Shader.SetGlobalFloat("_CapturedSkinSaturation", saturation);
                        float luma = raw.r * 0.2126729f + raw.g * 0.7151522f + raw.b * 0.0721750f;
                        Color expected = Color.LerpUnclamped(new Color(luma, luma, luma, 1f), raw, 1f + saturation * mask);
                        expected *= tints[tint] * 0.96f; expected.a = 1f;
                        string name = "base-tint-shade-" + type + "-" + mask + "-" + saturation + "-" + tint;
                        AddColorCheck(report, name, expected, Render(name));
                    }
                }
                // Painted highlights, Layer and RampAdd all precede BaseColor.
                // A late tint must color the entire mixture, including zero RGB.
                _material.SetTexture("_RampTex", Map(new Color(1, 1, 1, 0)));
                Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
                Color layer = new Color(0.3f, 0.8f, 0.1f, 0.5f);
                Color highlight = new Color(0.6f, 0.1f, 0.85f, 1f);
                Color added = new Color(0.4f, 0.15f, 0.65f, 0.25f);
                Color addTint = new Color(0.8f, 1.2f, 0.3f, 1f);
                // RampAddColor is a ShaderLab Color, unlike the literal Vector
                // used for BaseColor. Account for Unity's property conversion.
                Color shaderAddTint = QualitySettings.activeColorSpace == ColorSpace.Linear ? addTint.linear : addTint;
                Texture2D addMap = Map(added);
                _material.SetTexture("_LayerTex", Map(layer)); _material.SetTexture("_HighlightTex", Map(highlight));
                _material.SetVector("_RampAddColor", addTint); _material.SetFloat("_LayerWeight", 0.8f);
                _quad.uv = new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero };
                for (int effect = 0; effect < 5; effect++)
                {
                    bool useLayer = effect == 0 || effect == 3;
                    bool useHighlight = effect == 1 || effect == 4;
                    bool useAdd = effect >= 2;
                    _material.SetFloat("_ShaderType", useLayer ? 9f : useHighlight ? 8f : 0f);
                    _material.SetFloat("_EnableLayer", useLayer ? 1f : 0f);
                    _material.SetVector("_DefValue", new Vector4(0.5f, 0, 0, useHighlight ? 1f : 0f));
                    _material.SetVector("_SpecularThreshold", Vector4.zero);
                    _material.SetTexture("_RampAddTex", useAdd ? addMap : Texture2D.blackTexture);
                    Color raw = useLayer ? Color.Lerp(baseColor, layer, 0.4f) : useHighlight ? highlight : baseColor;
                    if (useAdd) raw += added * shaderAddTint * (1f - added.a);
                    for (int tint = 0; tint < tints.Length; tint++)
                    {
                        _material.SetVector("_Color", tints[tint]);
                        Color expected = raw * tints[tint] * 0.96f; expected.a = 1f;
                        string name = "base-tint-painted-" + effect + "-" + tint;
                        AddColorCheck(report, name, expected, Render(name));
                    }
                }
                // Type5 exits before the common material path. Retain its
                // once-only HDR tint, independent of its source alpha.
                _material.SetFloat("_ShaderType", 5f); _material.SetFloat("_EnableLayer", 0f);
                _material.SetVector("_DefValue", new Vector4(1, 0, 0, 0));
                _material.SetVector("_BaseMap_ST", new Vector4(1, 1, 0, 0));
                Shader.SetGlobalFloat("_FaceDebugMode", 0f); Shader.SetGlobalFloat("_CapturedType5OutputScale", 1f);
                Shader.SetGlobalVector("_ActorKeyColor", Vector4.one); Shader.SetGlobalVector("_CapturedLightColor", Vector4.one);
                Shader.SetGlobalVector("_ActorEyeHighlightColor", Vector4.one); Shader.SetGlobalVector("_ActorRimColor", Vector4.zero);
                for (int tint = 0; tint < tints.Length; tint++)
                {
                    _material.SetVector("_Color", tints[tint] * 3f);
                    Color expected = baseColor * tints[tint] * (3f * 0.96f); expected.a = 1f;
                    string name = "base-tint-eye-highlight-once-" + tint;
                    AddColorCheck(report, name, expected, Render(name));
                }
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial); _quad.uv = savedUv;
                for (int i = 0; i < floats.Length; i++) Shader.SetGlobalFloat(floats[i], savedFloats[i]);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], savedVectors[i]);
            }
        }

        private void VerifyHairSpecularRegions(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            Vector2[] savedUv = _quad.uv;
            string[] floats = { "_FaceDebugMode", "_CapturedDirectScale", "_CapturedDiffuseBlend",
                "_UseCapturedDirectSpecular", "_ActorEnvironmentIntensity", "_CapturedSkinSaturation",
                "_ActorAdditionalLightCount", "_UseCapturedReceiverNormal" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedLightDirection", "_ActorMatcapParameters", "_ActorLightingScales",
                "_ActorKeyColor", "_ActorRimColor", "_CapturedShadeAdditive" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            string[] arrays = { "_ActorAdditionalPositions", "_ActorAdditionalColors",
                "_ActorAdditionalDirections", "_ActorAdditionalSpots" };
            Vector4[][] savedArrays = Array.ConvertAll(arrays, Shader.GetGlobalVectorArray);
            var baseMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var highlight = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var ramp = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var rampAdd = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            Color baseColor = new Color(0.12f, 0.20f, 0.30f, 1f);
            Color highlightColor = new Color(0.75f, 0.60f, 0.45f, 1f);
            baseMap.SetPixel(0, 0, baseColor); baseMap.Apply();
            highlight.SetPixel(0, 0, highlightColor); highlight.Apply();
            ramp.SetPixel(0, 0, new Color(1, 1, 1, 0)); ramp.Apply();
            rampAdd.SetPixel(0, 0, Color.clear); rampAdd.Apply();
            try
            {
                _material.SetColor("_Color", Color.white);
                _material.SetTexture("_MainTex", baseMap);
                _material.SetTexture("_HighlightTex", highlight);
                _material.SetTexture("_RampTex", ramp);
                _material.SetTexture("_ShadeTex", Texture2D.blackTexture);
                _material.SetTexture("_RampAddTex", rampAdd);
                _material.SetFloat("_EnableLayer", 0f);
                _material.SetFloat("_UseEmission", 0f);
                _material.SetFloat("_DisableDefMap", 1f);
                _material.SetVector("_DefValue", new Vector4(0.5f, 0.5f, 0, 1));
                // This fixture isolates UV region ownership. Force the highlight
                // gate open; angular response is checked separately below.
                _material.SetVector("_SpecularThreshold", Vector4.zero);
                Shader.SetGlobalFloat("_CapturedDirectScale", 1f);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend", 1f);
                Shader.SetGlobalFloat("_UseCapturedDirectSpecular", 1f);
                Shader.SetGlobalFloat("_ActorEnvironmentIntensity", 0f);
                Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
                Shader.SetGlobalFloat("_ActorAdditionalLightCount", 0f);
                // Positive camera-relative specular; angular semantics are
                // validated independently by VerifyMainSpecularBasis.
                Shader.SetGlobalFloat("_UseCapturedReceiverNormal", 1f);
                Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(0, 0, 1, 0));
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0, 1, 1, 0));
                _material.SetFloat("_ShaderType", 0f);
                Shader.SetGlobalFloat("_FaceDebugMode", 20f);
                Color bodySpecular = Render("hair-spec-body-positive-control");
                report.checks.Add(new Check { name = "hair-spec-body-positive-control", actual = bodySpecular,
                    accepted = bodySpecular.r > 0.01f && !float.IsNaN(bodySpecular.r) && !float.IsInfinity(bodySpecular.r) });
                var coordinates = new[] { new Vector2(0.2f, 0.2f), new Vector2(0.8f, 0.2f),
                    new Vector2(0.2f, 0.8f), new Vector2(0.75f, 0.9f), new Vector2(0.9f, 0.75f),
                    new Vector2(0.8f, 0.8f) };
                for (int i = 0; i < coordinates.Length; i++)
                {
                    Vector2 uv = coordinates[i];
                    _quad.uv = new[] { uv, uv, uv, uv };
                    bool accessory = i == coordinates.Length - 1;
                    _material.SetFloat("_ShaderType", 8f);
                    Shader.SetGlobalFloat("_FaceDebugMode", 20f);
                    string name = "hair-spec-region-" + i;
                    AddColorCheck(report, name, accessory ? bodySpecular : Color.black, Render(name));
                    // The authored highlight must remain on strands, including
                    // the exact 0.75 boundary, and stay off the accessory UVs.
                    Shader.SetGlobalFloat("_FaceDebugMode", 19f);
                    Color diffuse = (accessory ? baseColor : highlightColor) * 0.96f; diffuse.a = 1f;
                    name = "hair-highlight-region-" + i;
                    AddColorCheck(report, name, diffuse, Render(name));
                }
                foreach (int type in new[] { 1, 9 })
                {
                    _material.SetFloat("_ShaderType", type);
                    Shader.SetGlobalFloat("_FaceDebugMode", 20f);
                    string name = "hair-spec-retains-nonhair-" + type;
                    AddColorCheck(report, name, bodySpecular, Render(name));
                }
                Shader.SetGlobalFloat("_FaceDebugMode", 23f);
                Shader.SetGlobalFloat("_CapturedDirectScale", 0f);
                Shader.SetGlobalVector("_ActorKeyColor", Vector4.zero);
                Shader.SetGlobalVector("_ActorRimColor", Vector4.zero);
                Shader.SetGlobalVector("_CapturedShadeAdditive", Vector4.zero);
                Shader.SetGlobalVector("_ActorLightingScales", new Vector4(0, 1, 1, 0));
                Shader.SetGlobalFloat("_ActorAdditionalLightCount", 1f);
                var positions = new Vector4[8]; positions[0] = new Vector4(0, 0, -2, 0.1f);
                var colors = new Vector4[8]; colors[0] = Vector4.one;
                var directions = new Vector4[8]; directions[0] = new Vector4(0, 0, 0, -1);
                Shader.SetGlobalVectorArray(arrays[0], positions);
                Shader.SetGlobalVectorArray(arrays[1], colors);
                Shader.SetGlobalVectorArray(arrays[2], directions);
                Shader.SetGlobalVectorArray(arrays[3], new Vector4[8]);
                _material.SetFloat("_ShaderType", 0f);
                Color additional = Render("hair-spec-additional-positive-control");
                report.checks.Add(new Check { name = "hair-spec-additional-positive-control", actual = additional,
                    accepted = additional.r > 0.001f && !float.IsNaN(additional.r) && !float.IsInfinity(additional.r) });
                _material.SetFloat("_ShaderType", 8f);
                AddColorCheck(report, "hair-spec-accessory-additional", additional, Render("hair-spec-accessory-additional"));
                _quad.uv = new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero };
                AddColorCheck(report, "hair-spec-strands-additional", Color.black, Render("hair-spec-strands-additional"));
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial);
                _quad.uv = savedUv;
                for (int i = 0; i < floats.Length; i++) Shader.SetGlobalFloat(floats[i], savedFloats[i]);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], savedVectors[i]);
                for (int i = 0; i < arrays.Length; i++) Shader.SetGlobalVectorArray(arrays[i],
                    savedArrays[i] != null && savedArrays[i].Length > 0 ? savedArrays[i] : new Vector4[8]);
            }
        }

        private void VerifyHairHighlightBasis(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            Vector3[] savedNormals = _quad.normals;
            Vector2[] savedUv = _quad.uv;
            string[] floats = { "_FaceDebugMode", "_UseCapturedReceiverNormal", "_UseCapturedActorShadow" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedLightDirection", "_ActorMatcapParameters", "_CapturedCameraUp" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            var baseMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var highlightMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var emptyRampAdd = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            Color baseColor = new Color(0.12f, 0.2f, 0.3f, 1f);
            Color highlightColor = new Color(0.75f, 0.6f, 0.45f, 1f);
            baseMap.SetPixel(0, 0, baseColor); baseMap.Apply();
            highlightMap.SetPixel(0, 0, highlightColor); highlightMap.Apply();
            emptyRampAdd.SetPixel(0, 0, Color.clear); emptyRampAdd.Apply();
            try
            {
                _material.SetVector("_Color", Vector4.one);
                _material.SetFloat("_ShaderType", 8f);
                _material.SetFloat("_UseBump", 0f);
                _material.SetFloat("_EnableLayer", 0f);
                _material.SetFloat("_UseAlphaClip", 0f);
                _material.SetFloat("_DisableDefMap", 1f);
                _material.SetVector("_DefValue", new Vector4(0.5f, 0, 0, 1));
                _material.SetTexture("_MainTex", baseMap);
                _material.SetTexture("_HighlightTex", highlightMap);
                _material.SetTexture("_RampAddTex", emptyRampAdd);
                _material.SetTexture("_ShadeTex", Texture2D.blackTexture);
                _material.SetTexture("_RampTex", Texture2D.whiteTexture);
                _quad.uv = new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero };
                Shader.SetGlobalFloat("_FaceDebugMode", 16f);
                Shader.SetGlobalFloat("_UseCapturedReceiverNormal", 1f);
                Shader.SetGlobalFloat("_UseCapturedActorShadow", 0f);
                // Zero shade strength isolates HighlightMap from the ramp's
                // intentional camera/world-space N.L selection.
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0, 1, 0, 0));
                Shader.SetGlobalVector("_CapturedCameraUp", Vector3.up);
                Vector3 point = _camera.ViewportToWorldPoint(new Vector3(32.5f/64f, 32.5f/64f,
                    -_camera.transform.position.z));
                Vector3 view = _camera.orthographic ? -_camera.transform.forward : (_camera.transform.position-point).normalized;
                Vector3 receiverX = Vector3.Cross(view, Vector3.up);
                Vector3 receiverY = Vector3.Cross(receiverX, view);
                var normals = new[] { Vector3.back, new Vector3(0.8f, 0, -0.6f), new Vector3(-0.8f, 0, -0.6f) };
                var lights = new[] { new Vector3(0.6f, 0.2f, 0.8f).normalized, new Vector3(-0.8f, 0.2f, 0.6f).normalized };
                float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
                for (int n = 0; n < normals.Length; n++)
                {
                    Vector3 normal = normals[n];
                    _quad.normals = new[] { normal, normal, normal, normal };
                    Vector3 receiver = new Vector3(Vector3.Dot(receiverX, normal),
                        Vector3.Dot(receiverY, normal), Vector3.Dot(view, normal));
                    for (int l = 0; l < lights.Length; l++)
                    {
                        Vector3 light = lights[l];
                        float response = Mathf.Pow(Mathf.Clamp01(Vector3.Dot(receiver,
                            (light+Vector3.forward).normalized)), 4f);
                        for (int threshold = 0; threshold < 2; threshold++)
                        {
                            float centre = threshold == 0 ? 0.4f : 0.15f;
                            float width = threshold == 0 ? 0.3f : 0f;
                            float t = width == 0f ? (response >= centre ? 1f : 0f)
                                : Mathf.Clamp01((response-centre+width)/(2f*width));
                            float weight = width == 0f ? t : t*t*(3f-2f*t);
                            _material.SetVector("_SpecularThreshold", new Vector4(centre, width, 0, 0));
                            Color expected = Color.Lerp(baseColor, highlightColor, weight);
                            for (int world = 0; world < 2; world++)
                            {
                                Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(light.x, light.y, light.z, world));
                                string name = "hair-basis-" + n + "-" + l + "-" + threshold + "-world-" + world;
                                Color actual = Render(name);
                                AddColorCheck(report, name, expected, actual);
                                minimum = Mathf.Min(minimum, actual.r); maximum = Mathf.Max(maximum, actual.r);
                            }
                        }
                    }
                }
                report.checks.Add(new Check { name = "hair-basis-nonconstant-highlight", maximumDifference = maximum-minimum,
                    accepted = !float.IsNaN(maximum-minimum) && maximum-minimum > 0.5f });
                foreach (int world in new[] { 0, 1 })
                {
                    Vector3 light = lights[0];
                    Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(light.x, light.y, light.z, world));
                    _material.SetFloat("_ShaderType", 0f);
                    AddColorCheck(report, "hair-basis-nonhair-" + world, baseColor, Render("hair-basis-nonhair-" + world));
                    _material.SetFloat("_ShaderType", 8f);
                    _material.SetVector("_DefValue", new Vector4(0.5f, 0, 0, 0));
                    AddColorCheck(report, "hair-basis-zero-mask-" + world, baseColor, Render("hair-basis-zero-mask-" + world));
                    _material.SetVector("_DefValue", new Vector4(0.5f, 0, 0, 1));
                }
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial);
                _quad.normals = savedNormals; _quad.uv = savedUv;
                for (int i = 0; i < floats.Length; i++) Shader.SetGlobalFloat(floats[i], savedFloats[i]);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], savedVectors[i]);
            }
        }

        private void VerifyMainSpecularBasis(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            Vector3[] savedNormals = _quad.normals;
            Vector2[] savedUv = _quad.uv;
            Vector3 savedPosition = _camera.transform.position;
            Quaternion savedRotation = _camera.transform.rotation;
            string[] floats = { "_FaceDebugMode", "_UseCapturedReceiverNormal", "_UseCapturedDirectSpecular",
                "_UseCapturedActorShadow", "_ActorEnvironmentIntensity", "_CapturedSkinSaturation",
                "_CapturedDiffuseBlend", "_CapturedType4DiffuseF0" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedLightDirection", "_ActorMatcapParameters", "_CapturedCameraUp" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            var baseMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var emptyRampAdd = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            Color albedo = new Color(0.12f, 0.2f, 0.3f, 1);
            baseMap.SetPixel(0, 0, albedo); baseMap.Apply();
            emptyRampAdd.SetPixel(0, 0, Color.clear); emptyRampAdd.Apply();
            // Derive the actual sampled world point independently from the
            // quad's z=0 plane. Camera tilt/roll also exercise receiver axes.
            Color Expected(Vector3 normal, Vector3 light, float smoothness, float metal, int type, bool captured = true)
            {
                // Use the known orthographic projection analytically. Inverting
                // a near/far camera ray loses precision at a narrow specular peak.
                float offset = (32.5f/64f*2f-1f)*_camera.orthographicSize;
                Vector3 origin = _camera.transform.position +
                    _camera.transform.right*(offset*_camera.aspect) + _camera.transform.up*offset;
                Vector3 direction = _camera.transform.forward;
                Vector3 point = origin-direction*(origin.z/direction.z);
                Vector3 view = _camera.orthographic ? -_camera.transform.forward : (_camera.transform.position-point).normalized;
                Vector3 x = Vector3.Cross(view, _camera.transform.up);
                Vector3 y = Vector3.Cross(x, view);
                Vector3 receiver = captured ? new Vector3(Vector3.Dot(x, normal),
                    Vector3.Dot(y, normal), Vector3.Dot(view, normal)) : normal;
                Vector3 half = (light+Vector3.forward).normalized;
                float nh = Mathf.Clamp01(Vector3.Dot(receiver, half));
                float lh = Mathf.Clamp01(Vector3.Dot(light, half));
                float a = Mathf.Max((1-smoothness)*(1-smoothness), 0.0078125f);
                float a2 = a*a;
                float denominator = nh*nh*(a2-1f)+1.00001f;
                float specular = a2 / Mathf.Max(denominator*denominator*Mathf.Max(lh*lh, 0.1f)*(4f*a+2f), 0.000001f);
                specular *= Mathf.Clamp01(Vector3.Dot(receiver, light));
                if (type == 9) metal = 0f;
                Color f0 = type == 4 ? albedo : Color.Lerp(new Color(0.04f, 0.04f, 0.04f, 1), albedo, metal);
                float grazing = Mathf.Clamp01(smoothness + (type == 4 ? 0.04f : 1f-0.96f*(1f-metal)));
                float fresnel = Mathf.Pow(1f-Mathf.Clamp01(Vector3.Dot(normal, view)), 4f);
                Color expected = Color.Lerp(f0, new Color(grazing, grazing, grazing, 1), fresnel) * (specular*0.6f/(1f+a2));
                expected.a = 1f;
                return expected;
            }
            try
            {
                _material.SetVector("_Color", Vector4.one);
                _material.SetFloat("_UseBump", 0f);
                _material.SetFloat("_EnableLayer", 0f);
                _material.SetFloat("_UseAlphaClip", 0f);
                _material.SetFloat("_DisableDefMap", 1f);
                _material.SetTexture("_MainTex", baseMap);
                _material.SetTexture("_RampAddTex", emptyRampAdd);
                _material.SetTexture("_ShadeTex", Texture2D.blackTexture);
                _material.SetTexture("_RampTex", Texture2D.whiteTexture);
                _quad.uv = new[] { Vector2.one, Vector2.one, Vector2.one, Vector2.one };
                foreach (string name in floats) Shader.SetGlobalFloat(name, 0f);
                Shader.SetGlobalFloat("_FaceDebugMode", 20f);
                Shader.SetGlobalFloat("_UseCapturedReceiverNormal", 1f);
                Shader.SetGlobalFloat("_UseCapturedDirectSpecular", 1f);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend", 1f);
                Shader.SetGlobalFloat("_CapturedType4DiffuseF0", 1f);
                // Isolate main specular from the ramp's world-dependent N.L.
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0, 1, 0, 0));
                var normals = new[] { Vector3.back, new Vector3(0.8f, 0, -0.6f) };
                var lights = new[] { new Vector3(0.6f, 0.2f, 0.8f).normalized, new Vector3(-0.8f, 0.2f, 0.6f).normalized };
                float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
                for (int camera = 0; camera < 2; camera++)
                {
                    _camera.transform.rotation = camera == 0 ? Quaternion.identity : Quaternion.Euler(12, 20, -15);
                    _camera.transform.position = -_camera.transform.forward*3f;
                    Shader.SetGlobalVector("_CapturedCameraUp", _camera.transform.up);
                    _material.SetFloat("_ShaderType", 0f);
                    for (int n = 0; n < normals.Length; n++)
                        for (int l = 0; l < lights.Length; l++)
                            for (int material = 0; material < 2; material++)
                            {
                                Vector3 normal = normals[n], light = lights[l];
                                _quad.normals = new[] { normal, normal, normal, normal };
                                float smoothness = material == 0 ? 0.15f : 0.65f;
                                float metal = material == 0 ? 0f : 0.6f;
                                _material.SetVector("_DefValue", new Vector4(0.5f, smoothness, metal, 0.6f));
                                Color expected = Expected(normal, light, smoothness, metal, 0);
                                for (int world = 0; world < 2; world++)
                                {
                                    Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(light.x, light.y, light.z, world));
                                    string name = "main-spec-basis-" + camera + "-" + n + "-" + l + "-" + material + "-world-" + world;
                                    Color actual = Render(name);
                                    AddColorCheck(report, name, expected, actual);
                                    minimum = Mathf.Min(minimum, actual.r); maximum = Mathf.Max(maximum, actual.r);
                                }
                            }
                }
                report.checks.Add(new Check { name = "main-spec-nonconstant-response", maximumDifference = maximum-minimum,
                    accepted = !float.IsNaN(maximum-minimum) && maximum-minimum > 0.005f });
                Vector3 front = Vector3.back, key = lights[0];
                _quad.normals = new[] { front, front, front, front };
                foreach (int world in new[] { 0, 1 })
                {
                    Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(key.x, key.y, key.z, world));
                    foreach (int type in new[] { 1, 4, 8, 9 })
                    {
                        _material.SetFloat("_ShaderType", type);
                        _material.SetVector("_DefValue", new Vector4(0.5f, 0.65f, 0.6f, 0.6f));
                        string name = "main-spec-material-" + type + "-world-" + world;
                        AddColorCheck(report, name, Expected(front, key, 0.65f, 0.6f, type), Render(name));
                    }
                    _material.SetFloat("_ShaderType", 0f);
                    _material.SetVector("_DefValue", new Vector4(0.5f, 0.65f, 0.6f, 0));
                    AddColorCheck(report, "main-spec-zero-mask-" + world, Color.black, Render("main-spec-zero-mask-" + world));
                    _material.SetFloat("_ShaderType", 8f);
                    _material.SetVector("_DefValue", new Vector4(0.5f, 0.65f, 0.6f, 0.6f));
                    _quad.uv = new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero };
                    AddColorCheck(report, "main-spec-no-strand-lobe-" + world, Color.black, Render("main-spec-no-strand-lobe-" + world));
                    _quad.uv = new[] { Vector2.one, Vector2.one, Vector2.one, Vector2.one };
                }
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial);
                _quad.normals = savedNormals; _quad.uv = savedUv;
                _camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
                for (int i = 0; i < floats.Length; i++) Shader.SetGlobalFloat(floats[i], savedFloats[i]);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], savedVectors[i]);
            }
        }

        private void VerifyRampAddSpecular(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            Vector2[] savedUv = _quad.uv;
            string[] floats = { "_FaceDebugMode", "_CapturedDirectScale", "_CapturedDiffuseBlend",
                "_UseCapturedDirectSpecular", "_ActorEnvironmentIntensity", "_CapturedSkinSaturation",
                "_ActorAdditionalLightCount", "_UseCapturedReceiverNormal" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedLightDirection", "_ActorMatcapParameters" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            var baseMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var ramp = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var rampAdd = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            Color baseColor = new Color(0.12f, 0.20f, 0.30f, 1f);
            Color texel = new Color(0.3f, 0.5f, 0.7f, 0f);
            Color tint = new Color(2f, 0.5f, 1.5f, 1f);
            // _RampAddColor is a ShaderLab Color property. Account for the
            // project's color-space conversion independently of texture alpha.
            Color linearTint = QualitySettings.activeColorSpace == ColorSpace.Linear ? tint.linear : tint;
            Color rampColor = texel * linearTint;
            baseMap.SetPixel(0, 0, baseColor); baseMap.Apply();
            ramp.SetPixel(0, 0, new Color(1, 1, 1, 0)); ramp.Apply();
            try
            {
                _material.SetColor("_Color", Color.white);
                _material.SetColor("_RampAddColor", tint);
                _material.SetTexture("_MainTex", baseMap);
                _material.SetTexture("_ShadeTex", Texture2D.blackTexture);
                _material.SetTexture("_RampTex", ramp);
                _material.SetTexture("_RampAddTex", rampAdd);
                _material.SetFloat("_EnableLayer", 0f);
                _material.SetFloat("_UseEmission", 0f);
                _material.SetFloat("_DisableDefMap", 1f);
                _material.SetVector("_DefValue", new Vector4(0.5f, 0.5f, 0, 1));
                Shader.SetGlobalFloat("_CapturedDirectScale", 1f);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend", 1f);
                Shader.SetGlobalFloat("_UseCapturedDirectSpecular", 1f);
                Shader.SetGlobalFloat("_ActorEnvironmentIntensity", 0f);
                Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
                Shader.SetGlobalFloat("_ActorAdditionalLightCount", 0f);
                // Keep this ratio fixture on a nondegenerate, lit BRDF lobe.
                Shader.SetGlobalFloat("_UseCapturedReceiverNormal", 1f);
                Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(0, 0, 1, 0));
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0, 1, 1, 0));
                Vector2 uv = new Vector2(0.8f, 0.8f);
                _quad.uv = new[] { uv, uv, uv, uv }; // Includes a hair accessory, not a strand.
                int[] types = { 0, 1, 1, 8, 9 };
                int[] variants = { 0, 1, 2, 0, 0 };
                for (int i = 0; i < types.Length; i++)
                {
                    _material.SetFloat("_ShaderType", types[i]);
                    _material.SetFloat("_CapturedType1Variant", variants[i]);
                    bool enabled = types[i] != 1 || variants[i] != 2;
                    string prefix = "ramp-add-type-" + types[i] + "-variant-" + variants[i];
                    rampAdd.SetPixel(0, 0, Color.clear); rampAdd.Apply();
                    Shader.SetGlobalFloat("_FaceDebugMode", 20f);
                    Color baseline = Render(prefix + "-positive-control");
                    report.checks.Add(new Check { name = prefix + "-positive-control", actual = baseline,
                        accepted = baseline.r > 0.01f && !float.IsNaN(baseline.r) && !float.IsInfinity(baseline.r) });
                    foreach (float alpha in new[] { 0f, 0.5f, 1f })
                    {
                        texel.a = alpha; rampAdd.SetPixel(0, 0, texel); rampAdd.Apply();
                        string suffix = alpha.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                        Shader.SetGlobalFloat("_FaceDebugMode", 20f);
                        Color multiplier = enabled ? Color.LerpUnclamped(Color.white, rampColor, alpha) : Color.white;
                        Color expected = baseline * multiplier; expected.a = 1f;
                        AddColorCheck(report, prefix + "-spec-alpha-" + suffix, expected, Render(prefix + "-spec-alpha-" + suffix));
                        // Diffuse still uses RGB * (1-alpha), independently of specular.
                        Shader.SetGlobalFloat("_FaceDebugMode", 19f);
                        expected = (baseColor + (enabled ? rampColor * (1f - alpha) : Color.clear)) * 0.96f;
                        expected.a = 1f;
                        AddColorCheck(report, prefix + "-diffuse-alpha-" + suffix, expected, Render(prefix + "-diffuse-alpha-" + suffix));
                    }
                    rampAdd.SetPixel(0, 0, Color.clear); rampAdd.Apply();
                    Shader.SetGlobalFloat("_FaceDebugMode", 20f);
                    AddColorCheck(report, prefix + "-restored", baseline, Render(prefix + "-restored"));
                }
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial);
                _quad.uv = savedUv;
                for (int i = 0; i < floats.Length; i++) Shader.SetGlobalFloat(floats[i], savedFloats[i]);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], savedVectors[i]);
            }
        }

        private void VerifyRampAddSignedView(Report report)
        {
            var saved = Own(new Material(_material));
            Vector3[] savedNormals = _quad.normals;
            bool savedProjection = _camera.orthographic;
            string[] floats = { "_FaceDebugMode", "_CapturedDirectScale", "_CapturedDiffuseBlend", "_CapturedSkinSaturation" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            var ramp = Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            ramp.SetPixel(0,0,new Color(1,1,1,0)); ramp.Apply();
            var addition = Own(new Texture2D(256,1,TextureFormat.RGBAFloat,false,true));
            addition.filterMode=FilterMode.Point; addition.wrapMode=TextureWrapMode.Clamp;
            for(int x=0;x<256;x++) addition.SetPixel(x,0,new Color(x/255f,1f-x/255f,.2f+.5f*x/255f,.25f));
            addition.Apply();
            Vector3[] normals={Vector3.forward,new Vector3(.8f,0,.6f),Vector3.right,new Vector3(.8f,0,-.6f)};
            int[] types={0,1,8,9,1,4}, variants={0,0,0,0,2,0};
            try
            {
                _material.SetColor("_Color",Color.white); _material.SetColor("_RampAddColor",Color.white);
                _material.SetTexture("_MainTex",Texture2D.blackTexture); _material.SetTexture("_ShadeTex",Texture2D.blackTexture);
                _material.SetTexture("_RampTex",ramp); _material.SetTexture("_RampAddTex",addition);
                _material.SetFloat("_EnableLayer",0); _material.SetFloat("_UseEmission",0); _material.SetFloat("_DisableDefMap",1);
                Shader.SetGlobalFloat("_FaceDebugMode",19); Shader.SetGlobalFloat("_CapturedDirectScale",1);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend",1); Shader.SetGlobalFloat("_CapturedSkinSaturation",0);
                for(int projection=0;projection<2;projection++)
                {
                    _camera.orthographic=projection==0;
                    Ray ray=_camera.ViewportPointToRay(new Vector3(32.5f/64f,32.5f/64f,0));
                    Vector3 point=ray.origin-ray.direction*(ray.origin.z/ray.direction.z);
                    Vector3 view=_camera.orthographic?-_camera.transform.forward:(_camera.transform.position-point).normalized;
                    for(int ni=0;ni<normals.Length;ni++)
                    {
                        Vector3 n=normals[ni]; _quad.normals=new[]{n,n,n,n};
                        foreach(float definition in new[]{.25f,.5f,.75f})
                        {
                            // Signed authored shading normals can point away
                            // even on a front-facing geometric triangle. Clamp
                            // only the completed coordinate, after the offset.
                            float coordinate=Mathf.Clamp01(2*definition-1+Vector3.Dot(n,view));
                            int x=Mathf.Clamp(Mathf.FloorToInt(coordinate*256),0,255);
                            Color sampled=addition.GetPixel(x,0)*(.75f*.96f); sampled.a=1;
                            _material.SetVector("_DefValue",new Vector4(definition,0,0,0));
                            for(int material=0;material<types.Length;material++)
                            {
                                _material.SetFloat("_ShaderType",types[material]); _material.SetFloat("_CapturedType1Variant",variants[material]);
                                bool enabled=material<4;
                                string name="ramp-coordinate-"+projection+"-normal-"+ni+"-def-"+definition+"-material-"+material;
                                AddColorCheck(report,name,enabled?sampled:Color.black,Render(name));
                            }
                        }
                    }
                }
                _material.SetFloat("_ShaderType",0); _material.SetFloat("_CapturedType1Variant",0);
                Color reference=Render("ramp-coordinate-reference");
                _material.SetTexture("_RampAddTex",Texture2D.blackTexture);
                AddColorCheck(report,"ramp-coordinate-disabled",Color.black,Render("ramp-coordinate-disabled"));
                _material.SetTexture("_RampAddTex",addition);
                AddColorCheck(report,"ramp-coordinate-restored",reference,Render("ramp-coordinate-restored"));
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(saved); _quad.normals=savedNormals; _camera.orthographic=savedProjection;
                for(int i=0;i<floats.Length;i++) Shader.SetGlobalFloat(floats[i],savedFloats[i]);
            }
        }

        private void VerifyAmbientMaterialResponse(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            string[] floats = { "_FaceDebugMode", "_CapturedDirectScale", "_CapturedDiffuseBlend",
                "_ActorEnvironmentIntensity", "_CapturedSkinSaturation", "_ActorAdditionalLightCount",
                "_UseCapturedAmbientSH" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedSH0", "_CapturedSH1", "_CapturedSH2", "_CapturedSH3",
                "_CapturedSH4", "_CapturedSH5", "_CapturedSH6", "_ActorLightingScales", "_ActorKeyColor",
                "_ActorRimColor", "_CapturedShadeAdditive", "_CapturedLightDirection", "_ActorMatcapParameters" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            var baseMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var ramp = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            Color baseColor = new Color(0.8f, 0.35f, 0.15f, 1f);
            Color irradiance = new Color(0.2f, 0.4f, 0.6f, 1f);
            baseMap.SetPixel(0, 0, baseColor); baseMap.Apply();
            ramp.SetPixel(0, 0, new Color(1, 1, 1, 0)); ramp.Apply();
            try
            {
                _material.SetColor("_Color", Color.white);
                _material.SetTexture("_MainTex", baseMap);
                _material.SetTexture("_ShadeTex", Texture2D.blackTexture);
                _material.SetTexture("_RampTex", ramp);
                _material.SetTexture("_RampAddTex", Texture2D.blackTexture);
                _material.SetFloat("_EnableLayer", 0f);
                _material.SetFloat("_UseEmission", 0f);
                _material.SetFloat("_DisableDefMap", 1f);
                Shader.SetGlobalFloat("_FaceDebugMode", 23f);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend", 1f);
                Shader.SetGlobalFloat("_ActorEnvironmentIntensity", 0f);
                Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
                Shader.SetGlobalFloat("_ActorAdditionalLightCount", 0f);
                // This existing fixture deliberately supplies archived-format
                // SH. Keep its coefficients/oracle and explicitly own the input.
                Shader.SetGlobalFloat("_UseCapturedAmbientSH", 1f);
                foreach (string name in vectors) Shader.SetGlobalVector(name, Vector4.zero);
                Shader.SetGlobalVector("_CapturedSH0", new Vector4(0, 0, 0, irradiance.r));
                Shader.SetGlobalVector("_CapturedSH1", new Vector4(0, 0, 0, irradiance.g));
                Shader.SetGlobalVector("_CapturedSH2", new Vector4(0, 0, 0, irradiance.b));
                Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(0, 0, -1, 1));
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0, 1, 1, 0));
                Shader.SetGlobalVector("_ActorLightingScales", new Vector4(1, 0, 0, 0));
                foreach (int type in new[] { 0, 1, 4, 9 })
                {
                    _material.SetFloat("_ShaderType", type);
                    foreach (float metallic in new[] { 0f, 0.5f, 1f })
                    {
                        _material.SetVector("_DefValue", new Vector4(0.5f, 0.5f, metallic, 0));
                        // Direct-light debug strength must not scale sky light.
                        foreach (float directScale in new[] { 0f, 2f })
                        {
                            Shader.SetGlobalFloat("_CapturedDirectScale", directScale);
                            string name = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "ambient-type-{0}-metal-{1}-direct-{2}", type, metallic, directScale);
                            Color expected = baseColor * irradiance * (0.96f * (type == 4 || type == 9 ? 1f : 1f - metallic));
                            expected.a = 1f;
                            AddColorCheck(report, name, expected, Render(name));
                        }
                    }
                }
                _material.SetFloat("_ShaderType", 0f);
                _material.SetVector("_DefValue", new Vector4(0.5f, 0.5f, 0, 0));
                Color reference = Render("ambient-positive-control");
                report.checks.Add(new Check { name = "ambient-positive-control", actual = reference,
                    accepted = reference.r > 0.1f && reference.g > 0.1f && reference.b > 0.05f });
                Shader.SetGlobalVector("_ActorLightingScales", Vector4.zero);
                AddColorCheck(report, "ambient-scale-zero", Color.black, Render("ambient-scale-zero"));
                Shader.SetGlobalVector("_ActorLightingScales", new Vector4(1, 0, 0, 0));
                AddColorCheck(report, "ambient-scale-restored", reference, Render("ambient-scale-restored"));
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial);
                for (int i = 0; i < floats.Length; i++) Shader.SetGlobalFloat(floats[i], savedFloats[i]);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], savedVectors[i]);
            }
        }

        private void VerifyAmbientInputContext(Report report)
        {
            var saved = Own(new Material(_material));
            var savedBlock = new MaterialPropertyBlock(); _quadRenderer.GetPropertyBlock(savedBlock);
            LightProbeUsage savedUsage = _quadRenderer.lightProbeUsage;
            Vector3[] savedNormals = _quad.normals;
            string[] floats = { "_FaceDebugMode", "_CapturedDirectScale", "_ActorEnvironmentIntensity",
                "_CapturedSkinSaturation", "_ActorAdditionalLightCount", "_UseCapturedAmbientSH" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedSH0", "_CapturedSH1", "_CapturedSH2", "_CapturedSH3",
                "_CapturedSH4", "_CapturedSH5", "_CapturedSH6", "_ActorLightingScales", "_ActorKeyColor",
                "_ActorRimColor", "_CapturedShadeAdditive", "_ActorMatcapParameters" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            string[] engineNames = { "unity_SHAr", "unity_SHAg", "unity_SHAb", "unity_SHBr",
                "unity_SHBg", "unity_SHBb", "unity_SHC" };
            // Authored test polynomials, not copied scene data. Include all
            // first/second-order terms and distinct RGB to reject stale inputs.
            Vector4[] engine = { new Vector4(.06f,-.04f,.08f,.3f), new Vector4(-.03f,.09f,.02f,.4f),
                new Vector4(.04f,.02f,-.06f,.5f), new Vector4(.03f,.02f,.01f,-.02f),
                new Vector4(-.02f,.01f,.03f,.02f), new Vector4(.01f,-.03f,.02f,.01f), new Vector4(.02f,-.01f,.03f,1) };
            Vector4[] archived = { new Vector4(-.02f,.04f,.01f,.7f), new Vector4(.06f,-.02f,.04f,.2f),
                new Vector4(.02f,.01f,-.04f,.3f), new Vector4(.01f,.02f,.03f,.01f),
                new Vector4(.02f,-.01f,.01f,-.02f), new Vector4(.01f,.01f,-.02f,.03f), new Vector4(.01f,.02f,-.01f,1) };
            Vector3[] normals = { Vector3.back, Vector3.right, Vector3.up,
                new Vector3(-1,2,-3).normalized, new Vector3(2,-3,1).normalized, new Vector3(3,1,2).normalized };
            var baseMap = Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            var ramp = Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            Color albedo = new Color(.7f,.4f,.2f,1);
            baseMap.SetPixel(0,0,albedo); baseMap.Apply();
            ramp.SetPixel(0,0,new Color(1,1,1,0)); ramp.Apply();
            Color Evaluate(Vector4[] coefficients, Vector3 n)
            {
                Color color = Color.black;
                // Independent scalar polynomial, no shader dot/swizzle helpers.
                for (int channel=0; channel<3; channel++)
                {
                    Vector4 a=coefficients[channel], b=coefficients[channel+3];
                    color[channel]=Mathf.Max(0,a.w+a.x*n.x+a.y*n.y+a.z*n.z+
                        b.x*n.x*n.y+b.y*n.y*n.z+b.z*n.z*n.z+b.w*n.x*n.z+
                        coefficients[6][channel]*(n.x*n.x-n.y*n.y));
                }
                color *= albedo*.96f; color.a=1; return color;
            }
            try
            {
                _material.SetColor("_Color",Color.white);
                _material.SetTexture("_MainTex",baseMap); _material.SetTexture("_ShadeTex",Texture2D.blackTexture);
                _material.SetTexture("_RampTex",ramp); _material.SetTexture("_RampAddTex",Texture2D.blackTexture);
                _material.SetFloat("_EnableLayer",0); _material.SetFloat("_UseEmission",0);
                _material.SetFloat("_DisableDefMap",1); _material.SetVector("_DefValue",new Vector4(.5f,0,0,0));
                Shader.SetGlobalFloat("_FaceDebugMode",23); Shader.SetGlobalFloat("_CapturedDirectScale",0);
                Shader.SetGlobalFloat("_ActorEnvironmentIntensity",0); Shader.SetGlobalFloat("_CapturedSkinSaturation",0);
                Shader.SetGlobalFloat("_ActorAdditionalLightCount",0);
                foreach(string name in vectors) Shader.SetGlobalVector(name,Vector4.zero);
                Shader.SetGlobalVector("_ActorMatcapParameters",new Vector4(0,1,1,0));
                Shader.SetGlobalVector("_ActorLightingScales",new Vector4(1,0,0,0));
                _quadRenderer.lightProbeUsage=LightProbeUsage.CustomProvided;
                for(int probe=0; probe<2; probe++)
                {
                    Vector4[] coefficients=(Vector4[])engine.Clone();
                    if(probe==1) for(int i=0;i<coefficients.Length;i++) coefficients[i]*=.35f;
                    var block=new MaterialPropertyBlock();
                    for(int i=0;i<engineNames.Length;i++) block.SetVector(engineNames[i],coefficients[i]);
                    _quadRenderer.SetPropertyBlock(block);
                    for(int context=0;context<3;context++)
                    {
                        // Local ignores populated archived SH; captured uses it;
                        // absent archived input retains the engine fallback.
                        Shader.SetGlobalFloat("_UseCapturedAmbientSH",context==0?0:1);
                        for(int i=0;i<7;i++) Shader.SetGlobalVector(vectors[i],context==2?Vector4.zero:archived[i]);
                        for(int ni=0;ni<normals.Length;ni++)
                        {
                            Vector3 n=normals[ni]; _quad.normals=new[]{n,n,n,n};
                            foreach(int type in new[]{0,4,9})
                            {
                                _material.SetFloat("_ShaderType",type);
                                string name="ambient-context-"+context+"-probe-"+probe+"-normal-"+ni+"-type-"+type;
                                Color expected=Evaluate(context==1?archived:coefficients,n);
                                AddColorCheck(report,name,expected,Render(name));
                            }
                        }
                    }
                }
                Shader.SetGlobalFloat("_UseCapturedAmbientSH",0);
                Color reference=Render("ambient-context-local-reference");
                Shader.SetGlobalVector("_ActorLightingScales",Vector4.zero);
                AddColorCheck(report,"ambient-context-gi-zero",Color.black,Render("ambient-context-gi-zero"));
                Shader.SetGlobalVector("_ActorLightingScales",new Vector4(1,0,0,0));
                AddColorCheck(report,"ambient-context-gi-restored",reference,Render("ambient-context-gi-restored"));
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(saved); _quad.normals=savedNormals;
                _quadRenderer.lightProbeUsage=savedUsage; _quadRenderer.SetPropertyBlock(savedBlock);
                for(int i=0;i<floats.Length;i++) Shader.SetGlobalFloat(floats[i],savedFloats[i]);
                for(int i=0;i<vectors.Length;i++) Shader.SetGlobalVector(vectors[i],savedVectors[i]);
            }
        }

        private void VerifyAdditionalLighting(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            Vector3[] savedNormals = _quad.normals;
            Vector2[] savedUv = _quad.uv;
            string[] floats = { "_FaceDebugMode", "_CapturedDirectScale", "_CapturedDiffuseBlend",
                "_ActorEnvironmentIntensity", "_CapturedSkinSaturation", "_ActorAdditionalLightCount" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_ActorLightingScales", "_ActorKeyColor", "_CapturedLightColor",
                "_ActorRimColor", "_CapturedShadeAdditive", "_CapturedLightDirection", "_ActorMatcapParameters" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            string[] arrays = { "_ActorAdditionalPositions", "_ActorAdditionalColors",
                "_ActorAdditionalDirections", "_ActorAdditionalSpots" };
            Vector4[][] savedArrays = Array.ConvertAll(arrays, Shader.GetGlobalVectorArray);
            var baseMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var ramp = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var rampAdd = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            Color albedo = new Color(0.2f, 0.35f, 0.6f, 1f);
            Color lamp = new Color(0.3f, 0.5f, 0.7f, 1f);
            baseMap.SetPixel(0, 0, albedo); baseMap.Apply();
            ramp.SetPixel(0, 0, new Color(1, 1, 1, 0)); ramp.Apply();
            rampAdd.SetPixel(0, 0, Color.clear); rampAdd.Apply();
            // Place lights relative to the actual sampled fragment, not the
            // quad centre (an even-sized render target has no centre pixel).
            Vector3 samplePosition = _camera.ViewportToWorldPoint(new Vector3(32.5f / 64f, 32.5f / 64f, 3f));
            var positions = new Vector4[8];
            var colors = new Vector4[8]; colors[0] = lamp;
            var directions = new Vector4[8]; directions[0].w = -1f;
            var spots = new Vector4[8];
            void LightAt(Vector3 direction, float distance = 2f)
            {
                Vector3 p = samplePosition + direction * distance;
                positions[0] = new Vector4(p.x, p.y, p.z, 0.01f);
                Shader.SetGlobalVectorArray(arrays[0], positions);
                Shader.SetGlobalVectorArray(arrays[1], colors);
                Shader.SetGlobalVectorArray(arrays[2], directions);
                Shader.SetGlobalVectorArray(arrays[3], spots);
            }
            void CheckColor(string name, Color expected)
            {
                expected.a = 1f;
                AddColorCheck(report, name, expected, Render(name));
            }
            const float attenuation = 0.2304f; // distance=2, range=10
            try
            {
                _material.SetColor("_Color", Color.white);
                _material.SetTexture("_MainTex", baseMap);
                _material.SetTexture("_ShadeTex", Texture2D.blackTexture);
                _material.SetTexture("_RampTex", ramp);
                _material.SetTexture("_RampAddTex", rampAdd);
                _material.SetFloat("_EnableLayer", 0f);
                _material.SetFloat("_UseEmission", 0f);
                _material.SetFloat("_DisableDefMap", 1f);
                _material.SetVector("_DefValue", new Vector4(0.5f, 0.5f, 0, 0));
                _quad.uv = new[] { Vector2.one, Vector2.one, Vector2.one, Vector2.one };
                Shader.SetGlobalFloat("_FaceDebugMode", 23f);
                Shader.SetGlobalFloat("_CapturedDirectScale", 1f);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend", 1f);
                Shader.SetGlobalFloat("_ActorEnvironmentIntensity", 0f);
                Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
                foreach (string name in vectors) Shader.SetGlobalVector(name, Vector4.zero);
                Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(0, 0, -1, 1));
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0.3f, 1, 1, 0));
                Shader.SetGlobalVector("_ActorLightingScales", new Vector4(0, 1, 0, 0));
                Color profile = new Color(0.8f, 0.5f, 0.3f, 1);
                Shader.SetGlobalVector("_CapturedLightColor", profile);
                foreach (int type in new[] { 0, 8, 9 })
                {
                    _material.SetFloat("_ShaderType", type);
                    foreach (Color key in new[] { Color.white, new Color(0.4f, 0.7f, 0.2f, 1) })
                    {
                        Shader.SetGlobalVector("_ActorKeyColor", key);
                        Color main = albedo * 0.96f * key * profile;
                        string prefix = "additional-type-" + type + (key == Color.white ? "-white" : "-tinted");
                        Shader.SetGlobalInt("_ActorAdditionalLightCount", 0);
                        CheckColor(prefix + "-unlit-control", main);
                        Shader.SetGlobalInt("_ActorAdditionalLightCount", 1);
                        Vector3[] axes = { Vector3.back, Vector3.right, Vector3.forward };
                        for (int i = 0; i < axes.Length; i++)
                        {
                            LightAt(axes[i]);
                            // At the normal profile the stylized light is not
                            // Lambert: side/back lights retain the ramped look.
                            CheckColor(prefix + "-direction-" + i, main + main * lamp * attenuation);
                        }
                    }
                }
                _material.SetFloat("_ShaderType", 0f);
                Shader.SetGlobalVector("_ActorKeyColor", Vector4.one);
                Color outgoing = albedo * 0.96f * profile;
                LightAt(Vector3.forward);
                foreach (float strength in new[] { 0f, 0.4f, 1f })
                {
                    Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(1.5f, 1, strength, 0));
                    CheckColor("additional-shade-floor-" + strength, outgoing + outgoing * lamp * (attenuation * strength));
                }
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0.3f, 1, 1, 0));
                foreach (float scale in new[] { 0f, 0.5f, 2f })
                {
                    Shader.SetGlobalVector("_ActorLightingScales", new Vector4(0, scale, 0, 0));
                    CheckColor("additional-scale-" + scale, outgoing + outgoing * lamp * (attenuation * scale));
                }
                Shader.SetGlobalVector("_ActorLightingScales", new Vector4(0, 1, 0, 0));
                positions[1] = positions[0]; colors[1] = lamp * 0.5f; directions[1].w = -1f;
                LightAt(Vector3.forward);
                Shader.SetGlobalInt("_ActorAdditionalLightCount", 2);
                CheckColor("additional-two-lights-add-once", outgoing + outgoing * lamp * (attenuation * 1.5f));
                Shader.SetGlobalInt("_ActorAdditionalLightCount", 1);
                LightAt(Vector3.forward, 12f);
                CheckColor("additional-outside-range", outgoing);
                directions[0] = new Vector4(0, 0, -1, 0.8f); spots[0].x = 0.9f;
                LightAt(Vector3.forward);
                CheckColor("additional-spot-inside", outgoing + outgoing * lamp * attenuation);
                directions[0] = new Vector4(0, 0, 1, 0.8f);
                LightAt(Vector3.forward);
                CheckColor("additional-spot-outside", outgoing);
                directions[0] = new Vector4(0, 0, 0, -1);
                LightAt(Vector3.back);
                // Isolate the extra specular lobe, including its roughness and
                // view response; the main key is zero so it cannot mask errors.
                Shader.SetGlobalVector("_ActorKeyColor", Vector4.zero);
                Shader.SetGlobalVector("_ActorLightingScales", new Vector4(0, 1, 1, 0));
                rampAdd.SetPixel(0, 0, new Color(0.4f, 0.7f, 0.2f, 1f)); rampAdd.Apply();
                _material.SetColor("_RampAddColor", Color.white);
                Vector3 view = _camera.orthographic ? -_camera.transform.forward : (_camera.transform.position - samplePosition).normalized;
                Vector3 half = (Vector3.back + view).normalized;
                Vector3[] normals = { Vector3.back, new Vector3(0.9f, 0, -0.4358899f).normalized };
                for (int i = 0; i < normals.Length; i++)
                {
                    Vector3 normal = normals[i];
                    _quad.normals = new[] { normal, normal, normal, normal };
                    foreach (float smoothness in new[] { 0f, 0.5f })
                        foreach (float metal in new[] { 0f, 0.5f, 1f })
                        {
                            _material.SetVector("_DefValue", new Vector4(0.5f, smoothness, metal, 0.6f));
                            float a = Mathf.Max((1f - smoothness) * (1f - smoothness), 0.0078125f);
                            float a2 = a * a;
                            float nh = Mathf.Clamp01(Vector3.Dot(normal, half));
                            float lh = Vector3.Dot(Vector3.back, half);
                            float divisor = nh * nh * (a2 - 1f) + 1.00001f;
                            float distribution = a2 / Mathf.Max(0.000001f, divisor * divisor *
                                Mathf.Max(0.1f, lh * lh) * (4f * a + 2f));
                            Color f0 = Color.Lerp(new Color(0.04f, 0.04f, 0.04f, 1), albedo, metal);
                            float grazing = Mathf.Clamp01(smoothness + 1f - 0.96f * (1f - metal));
                            float fresnel = Mathf.Pow(1f - Mathf.Clamp01(Vector3.Dot(normal, view)), 4f);
                            Color response = Color.Lerp(f0, new Color(grazing, grazing, grazing, 1), fresnel) / (1f + a2);
                            Color expected = response * new Color(0.4f, 0.7f, 0.2f, 1) * lamp * (distribution * 0.6f * attenuation);
                            CheckColor("additional-spec-normal-" + i + "-smooth-" + smoothness + "-metal-" + metal, expected);
                        }
                }
                Shader.SetGlobalVector("_ActorLightingScales", new Vector4(0, 1, 0, 0));
                CheckColor("additional-spec-scale-zero", Color.black);
                Shader.SetGlobalInt("_ActorAdditionalLightCount", 0);
                CheckColor("additional-removed", Color.black);
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial);
                _quad.normals = savedNormals; _quad.uv = savedUv;
                for (int i = 0; i < floats.Length; i++) Shader.SetGlobalFloat(floats[i], savedFloats[i]);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], savedVectors[i]);
                for (int i = 0; i < arrays.Length; i++) Shader.SetGlobalVectorArray(arrays[i],
                    savedArrays[i] != null && savedArrays[i].Length > 0 ? savedArrays[i] : new Vector4[8]);
            }
        }

        private void VerifyHairCoverComposition(Report report)
        {
            string[] floats = { "_FaceDebugMode", "_CapturedDirectScale", "_CapturedDiffuseBlend",
                "_ActorEnvironmentIntensity", "_CapturedSkinSaturation", "_CapturedActorOutputScale", "_ActorAdditionalLightCount",
                "_UseCapturedAmbientSH" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_ActorLightingScales", "_ActorKeyColor", "_CapturedLightColor",
                "_ActorRimColor", "_CapturedShadeAdditive", "_CapturedLightDirection", "_ActorMatcapParameters",
                "_HeadDirection", "_HeadUpDirection", "_ActorOutlineParameters" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            string[] arrays = { "_ActorAdditionalPositions", "_ActorAdditionalColors", "_ActorAdditionalDirections", "_ActorAdditionalSpots" };
            Vector4[][] savedArrays = Array.ConvertAll(arrays, Shader.GetGlobalVectorArray);
            var root = Own(new GameObject("Synthetic stencil composition"));
            var eye = Own(new Material(_material));
            var hair = Own(new Material(_material));
            var ramp = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var hairMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var eyeMap = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            Color eyeColor = new Color(0.2f, 0.5f, 0.8f, 1);
            Color hairColor = new Color(0.6f, 0.3f, 0.1f, 1);
            ramp.SetPixel(0, 0, new Color(1, 1, 1, 0)); ramp.Apply();
            eyeMap.SetPixel(0, 0, eyeColor); eyeMap.Apply();
            GameObject Plane(string name, Material material, float size, float z)
            {
                var plane = new GameObject(name);
                plane.transform.SetParent(root.transform, false);
                var mesh = Own(new Mesh());
                mesh.vertices = new[] { new Vector3(-size,-size,z), new Vector3(size,-size,z),
                    new Vector3(size,size,z), new Vector3(-size,size,z) };
                mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                mesh.triangles = new[] { 0,2,1,0,3,2 };
                mesh.RecalculateBounds();
                plane.AddComponent<MeshFilter>().sharedMesh = mesh;
                plane.AddComponent<MeshRenderer>().sharedMaterial = material;
                return plane;
            }
            ActorRenderControls controls = null;
            CommandBuffer staleLighting = null;
            SphericalHarmonicsL2 savedAmbientProbe = RenderSettings.ambientProbe;
            try
            {
                _quadRenderer.enabled = false;
                foreach (Material material in new[] { eye, hair })
                {
                    material.SetColor("_Color", Color.white);
                    material.SetTexture("_ShadeTex", Texture2D.blackTexture);
                    material.SetTexture("_RampTex", ramp);
                    material.SetTexture("_RampAddTex", Texture2D.blackTexture);
                    material.SetFloat("_EnableLayer", 0f);
                    material.SetFloat("_UseEmission", 0f);
                    material.SetFloat("_DisableDefMap", 1f);
                    material.SetFloat("_OutlineEnabled", 0f);
                    material.SetFloat("_SrcBlend", 1f); material.SetFloat("_DstBlend", 0f);
                    material.SetFloat("_ZWrite", 1f);
                    material.SetVector("_DefValue", new Vector4(0.5f, 0, 0, 0));
                }
                eye.SetTexture("_MainTex", eyeMap); eye.SetFloat("_ShaderType", 3f); eye.renderQueue = 2000;
                eye.SetFloat("_StencilRef", 68f); eye.SetFloat("_StencilReadMask", 108f);
                eye.SetFloat("_StencilWriteMask", 108f); eye.SetFloat("_StencilComp", (float)CompareFunction.Always);
                eye.SetFloat("_StencilPass", (float)StencilOp.Replace);
                hair.SetTexture("_MainTex", hairMap); hair.SetFloat("_ShaderType", 8f); hair.renderQueue = 2301;
                hair.SetFloat("_StencilRef", 64f); hair.SetFloat("_StencilReadMask", 108f);
                hair.SetFloat("_StencilWriteMask", 96f); hair.SetFloat("_StencilComp", (float)CompareFunction.GreaterEqual);
                hair.SetFloat("_StencilPass", (float)StencilOp.Keep);
                hair.SetVector("_HairFadeParameters", new Vector4(0.75f, 2f, 0.4f, 4f));
                Plane("Stencil eye region", eye, 0.3f, 0f);
                GameObject hairPlane = Plane("Opaque hair with marked bangs", hair, 1f, -0.2f);
                controls = _camera.gameObject.AddComponent<ActorRenderControls>();
                controls.Initialize(root, null); controls.outlines = false;
                foreach (string name in vectors) Shader.SetGlobalVector(name, Vector4.zero);
                Shader.SetGlobalVector("_CapturedLightColor", Vector4.one);
                Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(0, 0, -1, 1));
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0.3f, 1, 1, 0));
                Shader.SetGlobalVector("_HeadUpDirection", Vector3.up);
                Shader.SetGlobalFloat("_FaceDebugMode", 0f);
                Shader.SetGlobalFloat("_CapturedDirectScale", 1f / 0.96f);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend", 1f);
                Shader.SetGlobalFloat("_ActorEnvironmentIntensity", 0f);
                Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
                Shader.SetGlobalFloat("_CapturedActorOutputScale", 1f);
                Vector3 fragment = _camera.ViewportToWorldPoint(new Vector3(32.5f / 64f, 32.5f / 64f, 2.8f));
                Vector3 view = _camera.orthographic ? -_camera.transform.forward : (_camera.transform.position - fragment).normalized;
                Vector3[] forward = { Vector3.back, new Vector3(0.8f, 0, -0.6f),
                    Vector3.right, new Vector3(0, 0.6f, -0.8f) };
                for (int angle = 0; angle < forward.Length; angle++)
                {
                    Vector3 up = angle == 3 ? new Vector3(0, 0.8f, 0.6f) : Vector3.up;
                    Shader.SetGlobalVector("_HeadDirection", forward[angle]);
                    Shader.SetGlobalVector("_HeadUpDirection", up);
                    float horizontal = Mathf.Clamp01((0.75f - Vector3.Dot(view, forward[angle])) * 2f);
                    float vertical = Mathf.Clamp01((Mathf.Abs(Vector3.Dot(view, up)) - 0.4f) * 4f);
                    foreach (float mask in new[] { 0f, 0.5f, 1f })
                    {
                        hairColor.a = mask;
                        hairMap.SetPixel(0, 0, hairColor); hairMap.Apply();
                        controls.hairCover = true;
                        string name = "hair-cover-angle-" + angle + "-mask-" + mask;
                        float opacity = 1f - mask * (1f - Mathf.Max(horizontal, vertical));
                        Color expected = Color.Lerp(eyeColor, hairColor, opacity); expected.a = 1f;
                        AddColorCheck(report, name, expected, Render(name));
                        Color opaqueHair = hairColor; opaqueHair.a = 1f;
                        AddColorCheck(report, name + "-outside-stencil", opaqueHair, _readback.GetPixel(12, 32));
                    }
                }
                controls.hairCover = false;
                AddColorCheck(report, "hair-cover-disabled-retains-eye", eyeColor, Render("hair-cover-disabled-retains-eye"));
                controls.hairCover = true;
                Shader.SetGlobalVector("_HeadDirection", Vector3.right);
                Shader.SetGlobalVector("_HeadUpDirection", Vector3.up);
                Color restored = hairColor; restored.a = 1f;
                AddColorCheck(report, "hair-cover-enabled-oblique-restored", restored, Render("hair-cover-enabled-oblique-restored"));
                report.checks.Add(new Check { name = "hair-cover-actual-command-submitted",
                    accepted = controls.HairCoverDrawCount == 1 && controls.OutlineDrawCount == 0 });

                // Opaque hair accepts Ref >= masked stencil. Its coverage
                // pass must handle the complement, including eyebrow bit 8,
                // and ignore bits outside the authored read mask.
                hair.SetFloat("_StencilComp", (float)CompareFunction.Never);
                eye.SetFloat("_StencilWriteMask", 255f);
                foreach (int stencil in new[] { 0, 4, 8, 64, 68, 72, 76, 96, 108, 192, 200 })
                {
                    eye.SetFloat("_StencilRef", stencil);
                    bool covered = 64 < (stencil & 108);
                    string name = "hair-cover-stencil-" + stencil;
                    AddColorCheck(report, name, covered ? restored : eyeColor, Render(name));
                }
                eye.SetFloat("_StencilRef", 72f);
                eye.SetFloat("_StencilWriteMask", 108f);
                hair.SetFloat("_StencilComp", (float)CompareFunction.GreaterEqual);

                // Even fully faded bangs own depth. Their back-facing outline
                // lies behind that depth and must not fill the eyebrow region.
                var outline = Own(new Material(hair));
                Color outlineColor = new Color(0.1f, 0.2f, 0.7f, 1f);
                outline.SetFloat("_StencilComp", (float)CompareFunction.Never);
                outline.SetFloat("_Cull", (float)CullMode.Back);
                outline.SetFloat("_OutlineEnabled", 1f);
                outline.SetFloat("_VertexColor", 0f);
                outline.SetVector("_OutlineColor", outlineColor);
                outline.SetShaderPassEnabled("ActorHairCover", false);
                GameObject outlinePlane = Plane("Hair outline behind view-faded bangs", outline, 0.3f, -0.1f);
                Mesh outlineMesh = outlinePlane.GetComponent<MeshFilter>().sharedMesh;
                outlineMesh.triangles = new[] { 0,1,2,0,2,3 };
                controls.Initialize(root, null); controls.outlines = true;
                controls.outlineWidth = Vector2.zero;
                Shader.SetGlobalVector("_HeadDirection", Vector3.back);
                hairColor.a = 1f; hairMap.SetPixel(0, 0, hairColor); hairMap.Apply();
                AddColorCheck(report, "hair-cover-zero-alpha-depth-blocks-own-outline", eyeColor,
                    Render("hair-cover-zero-alpha-depth-blocks-own-outline"));
                hair.SetFloat("_ZWrite", 0f);
                AddColorCheck(report, "hair-cover-depth-optout-retains-own-outline", outlineColor,
                    Render("hair-cover-depth-optout-retains-own-outline"));
                hair.SetFloat("_ZWrite", 1f);
                hairColor.a = 0.5f; hairMap.SetPixel(0, 0, hairColor); hairMap.Apply();
                Color halfCovered = Color.Lerp(eyeColor, hairColor, 0.5f); halfCovered.a = 1f;
                AddColorCheck(report, "hair-cover-half-alpha-precedes-own-outline", halfCovered,
                    Render("hair-cover-half-alpha-precedes-own-outline"));
                outlinePlane.SetActive(false); controls.outlines = false;
                var occluder = Own(new Material(eye));
                occluder.SetTexture("_MainTex", Texture2D.whiteTexture);
                occluder.SetFloat("_ShaderType", 0f); occluder.SetFloat("_StencilWriteMask", 0f);
                occluder.renderQueue = 2400;
                GameObject nearOccluder = Plane("Near depth occluder", occluder, 0.3f, -0.4f);
                AddColorCheck(report, "hair-cover-respects-nearer-depth", Color.white, Render("hair-cover-respects-nearer-depth"));
                nearOccluder.SetActive(false);

                // CommandBuffer.DrawRenderer does not bind light probes. Supply
                // hostile prior draw state: coverage must match the ordinary
                // hair's scene probe, not inherit whichever draw ran last.
                string[] shNames={"unity_SHAr","unity_SHAg","unity_SHAb","unity_SHBr","unity_SHBg","unity_SHBb","unity_SHC"};
                staleLighting=new CommandBuffer{name="Self-test stale lighting before hair coverage"};
                foreach(string name in shNames) staleLighting.SetGlobalVector(name,Vector4.zero);
                staleLighting.SetGlobalVector("unity_SHAr",new Vector4(0,0,0,3));
                _camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque,staleLighting);
                Shader.SetGlobalFloat("_UseCapturedAmbientSH",0);
                Shader.SetGlobalFloat("_FaceDebugMode",23);
                Shader.SetGlobalFloat("_CapturedDirectScale",0);
                Shader.SetGlobalVector("_ActorLightingScales",new Vector4(1,0,0,0));
                foreach(Renderer renderer in root.GetComponentsInChildren<Renderer>()) renderer.lightProbeUsage=LightProbeUsage.Off;
                hairColor.a=0; hairMap.SetPixel(0,0,hairColor); hairMap.Apply();
                Color[] ambientColors={new Color(.2f,.4f,.6f),new Color(.6f,.1f,.2f),Color.black};
                Renderer hairRenderer=hairPlane.GetComponent<Renderer>();
                LightProbeUsage[] usages={LightProbeUsage.Off,LightProbeUsage.BlendProbes,LightProbeUsage.CustomProvided};
                for(int usage=0;usage<usages.Length;usage++)
                for(int probe=0;probe<ambientColors.Length;probe++)
                {
                    var sh=new SphericalHarmonicsL2(); sh.AddAmbientLight(ambientColors[probe]);
                    RenderSettings.ambientProbe=sh;
                    hairRenderer.lightProbeUsage=usages[usage];
                    var block=new MaterialPropertyBlock();
                    Color irradiance=ambientColors[probe];
                    if(usage==2)
                    {
                        irradiance=new Color(.1f+.2f*probe,.6f-.2f*probe,.3f);
                        foreach(string property in shNames) block.SetVector(property,Vector4.zero);
                        for(int channel=0;channel<3;channel++) block.SetVector(shNames[channel],new Vector4(0,0,0,irradiance[channel]));
                    }
                    hairRenderer.SetPropertyBlock(block);
                    string name="hair-cover-ambient-usage-"+usage+"-probe-"+probe;
                    Color expected=hairColor*irradiance*.96f; expected.a=1;
                    AddColorCheck(report,name,expected,Render(name));
                    // Outside the stencil, ordinary forward hair uses the same
                    // scene probe. Check both against the independent constant.
                    AddColorCheck(report,name+"-ordinary",expected,_readback.GetPixel(12,32));
                }
                var partialProbe=new MaterialPropertyBlock();
                partialProbe.SetVector("unity_SHAr",new Vector4(0,0,0,.3f));
                hairRenderer.SetPropertyBlock(partialProbe);
                Color partialExpected=new Color(hairColor.r*.3f*.96f,0,0,1);
                AddColorCheck(report,"hair-cover-ambient-custom-missing-coefficients",partialExpected,
                    Render("hair-cover-ambient-custom-missing-coefficients"));
                AddColorCheck(report,"hair-cover-ambient-custom-missing-ordinary",partialExpected,_readback.GetPixel(12,32));
            }
            finally
            {
                if(staleLighting!=null)
                {
                    _camera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque,staleLighting);
                    staleLighting.Release();
                }
                RenderSettings.ambientProbe=savedAmbientProbe;
                if (controls != null) DestroyImmediate(controls);
                root.SetActive(false);
                _quadRenderer.enabled = true;
                for (int i = 0; i < floats.Length; i++) Shader.SetGlobalFloat(floats[i], savedFloats[i]);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], savedVectors[i]);
                for (int i = 0; i < arrays.Length; i++) Shader.SetGlobalVectorArray(arrays[i],
                    savedArrays[i] != null && savedArrays[i].Length > 0 ? savedArrays[i] : new Vector4[8]);
            }
        }

        private void VerifyOutlineDepth(Report report)
        {
            // Back-facing planes are culled by the ordinary surface pass but
            // drawn by the real supplemental outline pass. Zero extrusion
            // isolates depth ownership from the outline width calculation.
            var root = Own(new GameObject("Synthetic outline depth ordering"));
            var near = Own(new Material(_material));
            var far = Own(new Material(_material));
            Color nearColor = new Color(0.2f, 0.5f, 0.8f, 1f);
            Color farColor = new Color(0.7f, 0.3f, 0.1f, 1f);
            GameObject Plane(string name, Material material, float z, Color color)
            {
                material.SetFloat("_Cull", (float)CullMode.Back);
                material.SetFloat("_OutlineEnabled", 1f);
                material.SetFloat("_VertexColor", 0f);
                material.SetFloat("_UseAlphaClip", 0f);
                material.SetFloat("_ZWrite", 1f);
                material.SetVector("_ActorColor", Vector4.one);
                material.SetVector("_OutlineColor", color);
                var mesh = Own(new Mesh());
                mesh.vertices = new[] { new Vector3(-1,-1,z), new Vector3(1,-1,z),
                    new Vector3(1,1,z), new Vector3(-1,1,z) };
                mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                mesh.triangles = new[] { 0,1,2,0,2,3 };
                mesh.RecalculateBounds();
                var plane = new GameObject(name);
                plane.transform.SetParent(root.transform, false);
                plane.AddComponent<MeshFilter>().sharedMesh = mesh;
                plane.AddComponent<MeshRenderer>().sharedMaterial = material;
                return plane;
            }
            var nearPlane = Plane("Near outline", near, -0.2f, nearColor);
            var farPlane = Plane("Far outline", far, 0.2f, farColor);
            string[] vectors = { "_ActorOutlineParameters", "_ActorKeyColor" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            string[] arrays = { "_ActorAdditionalPositions", "_ActorAdditionalColors", "_ActorAdditionalDirections", "_ActorAdditionalSpots" };
            Vector4[][] savedArrays = Array.ConvertAll(arrays, Shader.GetGlobalVectorArray);
            float savedCount = Shader.GetGlobalFloat("_ActorAdditionalLightCount");
            float savedDebug = Shader.GetGlobalFloat("_FaceDebugMode");
            float savedNear = _camera.nearClipPlane, savedFar = _camera.farClipPlane;
            Vector3 savedPosition = _quadRenderer.transform.position;
            var lateSurface = Own(new Material(_material));
            lateSurface.SetTexture("_MainTex", Texture2D.whiteTexture);
            lateSurface.SetVector("_Color", Vector4.one);
            lateSurface.SetFloat("_ZWrite", 0f);
            lateSurface.SetFloat("_SrcBlend", (float)BlendMode.One);
            lateSurface.SetFloat("_DstBlend", (float)BlendMode.One);
            lateSurface.SetFloat("_SrcAlphaBlend", (float)BlendMode.Zero);
            lateSurface.SetFloat("_DstAlphaBlend", (float)BlendMode.One);
            lateSurface.renderQueue = 3000;
            ActorRenderControls controls = null;
            void Check(string name, Color expected) { AddColorCheck(report, name, expected, Render(name)); }
            try
            {
                _quadRenderer.enabled = false;
                controls = _camera.gameObject.AddComponent<ActorRenderControls>();
                controls.Initialize(root, null);
                controls.hairCover = false;
                controls.outlines = true;
                controls.outlineWidth = Vector2.zero;
                farPlane.SetActive(false);
                Check("outline-depth-near-alone", nearColor);
                farPlane.SetActive(true); nearPlane.SetActive(false);
                Check("outline-depth-far-alone", farColor);
                nearPlane.SetActive(true);
                Check("outline-depth-near-then-far", nearColor);
                report.checks.Add(new Check { name = "outline-depth-actual-commands-submitted",
                    accepted = controls.OutlineDrawCount == 2 && controls.HairCoverDrawCount == 0 });
                farPlane.transform.SetAsFirstSibling(); controls.RefreshRenderers();
                Check("outline-depth-far-then-near", nearColor);
                near.SetFloat("_ZWrite", 0f);
                Check("outline-depth-optout-far-then-near", nearColor);
                nearPlane.transform.SetAsFirstSibling(); controls.RefreshRenderers();
                Check("outline-depth-optout-near-then-far", farColor);
                near.SetFloat("_ZWrite", 1f); far.SetFloat("_ZWrite", 0f);
                Check("outline-depth-restored-near-blocks-far-optout", nearColor);
                farPlane.SetActive(false);
                _quadRenderer.sharedMaterial = lateSurface;
                _quadRenderer.enabled = true;
                Shader.SetGlobalFloat("_FaceDebugMode", 14f); // Unlit raw white base.
                _quadRenderer.transform.position = new Vector3(0, 0, 0.5f);
                Check("outline-depth-blocks-late-surface-behind", nearColor);
                Color composed = nearColor + Color.white; composed.a = 1f;
                near.SetFloat("_ZWrite", 0f);
                Check("outline-depth-optout-allows-late-surface-behind", composed);
                near.SetFloat("_ZWrite", 1f);
                _quadRenderer.transform.position = new Vector3(0, 0, -0.5f);
                Check("outline-depth-allows-late-surface-in-front", composed);
                _quadRenderer.enabled = false;
                controls.outlines = false;
                Check("outline-depth-disabled-background-control", _camera.backgroundColor);
                controls.outlines = true;
                Check("outline-depth-restored", nearColor);

                // The upper blue nibble counts clip-depth units (0..15),
                // unlike normalized RGB/width nibbles. Place an opaque plane
                // on either side of the independently computed depth shift.
                _camera.nearClipPlane = 0.1f; _camera.farClipPlane = 10f;
                lateSurface.renderQueue = 2000;
                lateSurface.SetFloat("_ZWrite", 1f);
                lateSurface.SetFloat("_DstBlend", (float)BlendMode.Zero);
                _quadRenderer.enabled = true;
                near.SetFloat("_VertexColor", 1f);
                Mesh outlineMesh = nearPlane.GetComponent<MeshFilter>().sharedMesh;
                foreach (int nibble in new[] { 0, 1, 8, 15 })
                {
                    Color packed = new Color(0, 0, (nibble * 16 + 7) / 255f, 1);
                    outlineMesh.colors = new[] { packed, packed, packed, packed };
                    float unitDistance = (10f - 0.1f) * (0.001f / 15f);
                    foreach (float fraction in new[] { 0.5f, 1.5f })
                    {
                        _quadRenderer.transform.position = new Vector3(0, 0,
                            -0.2f + Mathf.Max(nibble, 1) * unitDistance * fraction);
                        bool hidden = nibble > 0 && fraction < 1f;
                        Check("outline-packed-depth-" + nibble + "-plane-" + fraction,
                            hidden ? Color.white : nearColor);
                    }
                }

                _quadRenderer.enabled = false;
                near.SetFloat("_VertexColor", 0f);
                controls.outlineWidth = new Vector2(18.7f, 18.7f);
                outlineMesh.vertices = new[] { new Vector3(-0.113f,-0.137f,-0.2f),
                    new Vector3(0.113f,-0.137f,-0.2f), new Vector3(0.113f,0.137f,-0.2f),
                    new Vector3(-0.113f,0.137f,-0.2f) };
                // A different geometric normal makes an invented fallback
                // visible even when the authored tangent is zero or tiny.
                outlineMesh.normals = new[] { Vector3.right, Vector3.right, Vector3.right, Vector3.right };
                outlineMesh.RecalculateBounds();
                Vector3[] authored = { Vector3.zero, new Vector3(0.000001f, 0.000002f, 0),
                    new Vector3(0.02f, 0, 0), new Vector3(0.5f, 0, 0), Vector3.right,
                    new Vector3(1.2f, 0.4f, 0) };
                for (int transformCase = 0; transformCase < 3; transformCase++)
                {
                    Vector3 objectScale = transformCase == 0 ? Vector3.one : new Vector3(1.6f, 0.7f, 1.1f);
                    Vector3 wardrobe = transformCase == 2 ? new Vector3(0.75f, 1.2f, 1f) : Vector3.one;
                    nearPlane.transform.localScale = objectScale;
                    near.SetVector("_WardrobeScaleCorrection", wardrobe);
                    Vector3 scale = Vector3.Scale(objectScale, wardrobe);
                    for (int tangentCase = 0; tangentCase < authored.Length; tangentCase++)
                    {
                        Vector3 tangent = authored[tangentCase];
                        Vector4 value = new Vector4(tangent.x, tangent.y, tangent.z, tangentCase % 2 == 0 ? 1 : -1);
                        outlineMesh.tangents = new[] { value, value, value, value };
                        string name = "outline-extrusion-transform-" + transformCase + "-tangent-" + tangentCase;
                        Render(name);
                        // Analytic orthographic rectangle, checked over the
                        // entire image rather than only a favorable sample.
                        Vector3 center = Vector3.Scale(tangent * 0.187f, scale);
                        float halfX = 0.113f * scale.x, halfY = 0.137f * scale.y;
                        float maximum = 0f;
                        int expectedCoverage = 0;
                        Color worstExpected = _camera.backgroundColor, worstActual = _readback.GetPixel(0, 0);
                        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                        {
                            float px = ((x + 0.5f) / 64f * 2f - 1f) * 1.2f;
                            float py = ((y + 0.5f) / 64f * 2f - 1f) * 1.2f;
                            bool covered = Mathf.Abs(px - center.x) < halfX && Mathf.Abs(py - center.y) < halfY;
                            if (covered) expectedCoverage++;
                            Color expected = covered ? nearColor : _camera.backgroundColor;
                            Color actual = _readback.GetPixel(x, y);
                            float difference = Mathf.Max(Mathf.Abs(expected.r-actual.r), Mathf.Abs(expected.g-actual.g),
                                Mathf.Abs(expected.b-actual.b), Mathf.Abs(expected.a-actual.a));
                            if (float.IsNaN(difference)) difference = float.PositiveInfinity;
                            if (difference > maximum) { maximum = difference; worstExpected = expected; worstActual = actual; }
                        }
                        report.checks.Add(new Check { name = name, expected = worstExpected, actual = worstActual,
                            maximumDifference = maximum, accepted = expectedCoverage > 0 && maximum < 0.00001f });
                    }
                }
            }
            finally
            {
                if (controls != null) DestroyImmediate(controls);
                root.SetActive(false);
                _quadRenderer.sharedMaterial = _material;
                _quadRenderer.transform.position = savedPosition;
                _quadRenderer.enabled = true;
                _camera.nearClipPlane = savedNear; _camera.farClipPlane = savedFar;
                Shader.SetGlobalFloat("_FaceDebugMode", savedDebug);
                Shader.SetGlobalFloat("_ActorAdditionalLightCount", savedCount);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], savedVectors[i]);
                for (int i = 0; i < arrays.Length; i++) Shader.SetGlobalVectorArray(arrays[i],
                    savedArrays[i] != null && savedArrays[i].Length > 0 ? savedArrays[i] : new Vector4[8]);
            }
        }

        private void VerifyMaterialSequence(Report report)
        {
            Action<string,bool> check=(name,accepted)=>report.checks.Add(new Check{name="material-sequence-"+name,accepted=accepted});
            check("name-boundary",ActorMaterialEffectRuntime.MatchesMaterial("m_eye_character__photo-toon","m_eye") &&
                !ActorMaterialEffectRuntime.MatchesMaterial("m_eyebrow_character","m_eye"));
            check("frame-first",ActorMaterialEffectRuntime.TextureFrame(3,3,10,0).Equals(new Vector4(1f/3,1f/3,0,2f/3)));
            check("frame-second",ActorMaterialEffectRuntime.TextureFrame(3,3,10,0.1).z==1f/3);
            check("frame-wrap",ActorMaterialEffectRuntime.TextureFrame(3,3,10,0.9).z==0);
            check("frame-after-wrap",ActorMaterialEffectRuntime.TextureFrame(3,3,10,1.0).z==1f/3);
            check("single-tile",ActorMaterialEffectRuntime.TextureFrame(1,1,1,999999).Equals(new Vector4(1,1,0,0)));
            check("invalid-grid",ActorMaterialEffectRuntime.TextureFrame(0,1,10,0).Equals(Vector4.zero));
            check("invalid-time",ActorMaterialEffectRuntime.TextureFrame(3,3,10,double.NaN).Equals(Vector4.zero) &&
                ActorMaterialEffectRuntime.TextureFrame(3,3,10,-1).Equals(Vector4.zero) &&
                ActorMaterialEffectRuntime.TextureFrame(3,3,10,double.MaxValue).Equals(Vector4.zero));
            var owner=Own(new GameObject("Generated material sequence"));
            var renderer=owner.AddComponent<MeshRenderer>();
            var original=Own(new Material(_material.shader){name="m_eye_generated"});
            var untouched=Own(new Material(_material.shader){name="m_eyebrow_generated"});
            var external=Own(new Material(_material.shader){name="m_eye_external"});
            original.SetFloat("_LayerWeight",0.4f);
            renderer.sharedMaterials=new[]{original,untouched};
            var block=new MaterialPropertyBlock();block.SetVector("_BaseMap_ST",new Vector4(1,1,0,0.2f));renderer.SetPropertyBlock(block,0);
            var effect=owner.AddComponent<Campus.Common.CampusActorMaterialEffect>();
            effect.overrideProperty=new Campus.Common.ActorTextureOverride {materialName="m_eye",col=Texture2D.redTexture,tileX=3,tileY=3,tileFPS=10};
            var timing=new VL.MotionEffect {effect=effect,startTime=0.25f,duration=1f};
            using(var runtime=new ActorMaterialEffectRuntime(new[]{renderer},null))
            {
                runtime.Bind(new[]{timing});runtime.Apply(0);
                check("before-start",runtime.ActiveMaterialCount==0 && renderer.sharedMaterials[0]==original);
                runtime.Apply(0.3);
                Material replacement=renderer.sharedMaterials[0];
                check("texture-applied",runtime.ActiveMaterialCount==1 && replacement!=original && replacement.GetTexture("_MainTex")==Texture2D.redTexture);
                check("other-slot-preserved",renderer.sharedMaterials[1]==untouched);
                check("other-property-preserved",replacement.GetFloat("_LayerWeight")==0.4f);
                renderer.GetPropertyBlock(block,0);
                check("property-block-preserved",block.GetVector("_BaseMap_ST").w==0.2f);
                runtime.Apply(1.25);
                check("end-exclusive-restores",renderer.sharedMaterials[0]==original && runtime.ActiveMaterialCount==0);
                runtime.Apply(0.35);Vector4 a=renderer.sharedMaterials[0].GetVector("_ActorTextureFrame");runtime.Apply(0.35);
                check("paused-sampling",renderer.sharedMaterials[0].GetVector("_ActorTextureFrame").Equals(a));
                runtime.Apply(0.85);runtime.Apply(0.35);
                check("seek-reproducible",renderer.sharedMaterials[0].GetVector("_ActorTextureFrame").Equals(a));
                runtime.Clear();check("clear-restores",renderer.sharedMaterials[0]==original);
                var other=owner.AddComponent<Campus.Common.CampusActorMaterialEffect>();
                other.overrideProperty=new Campus.Common.ActorTextureOverride {materialName="m_eye",col=Texture2D.whiteTexture};
                runtime.Bind(new[]{timing,new VL.MotionEffect{effect=other,startTime=0.5f,duration=0.2f}});
                runtime.Apply(0.6);check("overlap-latest-active",renderer.sharedMaterials[0].GetTexture("_MainTex")==Texture2D.whiteTexture);
                runtime.Apply(0.8);check("overlap-release-previous",renderer.sharedMaterials[0].GetTexture("_MainTex")==Texture2D.redTexture);
                runtime.Clear();check("overlap-restores-original",renderer.sharedMaterials[0]==original);
                runtime.Bind(new[]{timing});runtime.Apply(0.5);renderer.sharedMaterials=new[]{external,untouched};runtime.Clear();
                check("external-slot-owner-preserved",renderer.sharedMaterials[0]==external);
            }
            var saved=Own(new Material(_material));float stage=Shader.GetGlobalFloat("_CapturedType4DebugStage");
            var atlas=Own(new Texture2D(3,2,TextureFormat.RGBAFloat,false,true));atlas.filterMode=FilterMode.Point;
            Color[] colors={new Color(0.2f,0.3f,0.4f,1),new Color(0.3f,0.4f,0.5f,1),new Color(0.4f,0.5f,0.6f,1),
                new Color(0.6f,0.2f,0.3f,1),new Color(0.7f,0.3f,0.4f,1),new Color(0.8f,0.4f,0.5f,1)};
            atlas.SetPixels(colors);atlas.Apply();
            try
            {
                _material.SetFloat("_ShaderType",4f);_material.SetFloat("_DisableDefMap",0f);
                _material.SetFloat("_UseAlphaClip",0f);_material.SetTexture("_MainTex",atlas);
                _material.SetTexture("_ShadeTex",atlas);_material.SetTexture("_DefTex",atlas);
                for(int frame=0;frame<6;frame++)
                {
                    _material.SetVector("_ActorTextureFrame",ActorMaterialEffectRuntime.TextureFrame(3,2,4,(frame+0.25)/4));
                    Color color=colors[(1-frame/3)*3+frame%3];
                    foreach(int debug in new[]{6,7,8})
                    {
                        Shader.SetGlobalFloat("_CapturedType4DebugStage",debug);
                        Color expected=debug==8?new Color(color.r,color.b,color.g,color.a):color;
                        string name="material-sequence-atlas-"+frame+"-map-"+debug;
                        AddColorCheck(report,name,expected,Render(name));
                    }
                }
            }
            finally { _material.CopyPropertiesFromMaterial(saved);Shader.SetGlobalFloat("_CapturedType4DebugStage",stage); }
        }

        private void VerifyRimControl(Report report)
        {
            string[] vectors = { "_ActorMatcapParameters", "_ActorLightingScales", "_CapturedLightDirection",
                "_CapturedLightColor", "_CapturedShadeTint", "_CapturedShadeAdditive", "_ActorRimColor",
                "_CapturedRimViewDirection", "_CapturedRimDirection", "_CapturedRimParameters" };
            Vector4[] saved = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            float savedBasis = Shader.GetGlobalFloat("_UseExactViewRimBasis");
            float savedDebug = Shader.GetGlobalFloat("_FaceDebugMode");
            float savedDiffuse = Shader.GetGlobalFloat("_CapturedDiffuseBlend");
            float savedSaturation = Shader.GetGlobalFloat("_CapturedSkinSaturation");
            var material = Own(new Material(_material));
            Color32[] savedColors = _quad.colors32;
            Quaternion savedRotation = _quadRenderer.transform.rotation;
            Vector3 cameraPosition = _camera.transform.position;
            Quaternion cameraRotation = _camera.transform.rotation;
            var owner = Own(new GameObject("Generated rim controls camera"));
            var camera = owner.AddComponent<Camera>(); camera.enabled = false;
            var controls = owner.AddComponent<ActorRenderControls>(); controls.enabled = false;
            MethodInfo apply = typeof(ActorRenderControls).GetMethod("ApplyOverride", BindingFlags.Instance | BindingFlags.NonPublic);
            Action update = () => apply.Invoke(controls, null);
            Action<string, bool> check = (name, accepted) => report.checks.Add(new Check { name = "rim-control-" + name, accepted = accepted });
            Vector4[] initial = new Vector4[vectors.Length];
            for (int i = 0; i < initial.Length; i++)
            {
                initial[i] = new Vector4(0.1f + i * 0.03f, 0.2f, 0.3f, 0.4f);
                Shader.SetGlobalVector(vectors[i], initial[i]);
            }
            Shader.SetGlobalFloat("_UseExactViewRimBasis", 0f);
            Func<int, Vector4> current = i => Shader.GetGlobalVector(vectors[i]);
            try
            {
                update();
                check("default-untouched", Array.TrueForAll(Array.ConvertAll(vectors, Shader.GetGlobalVector),
                    value => Array.IndexOf(initial, value) >= 0) && Shader.GetGlobalFloat("_UseExactViewRimBasis") == 0f);
                controls.overrideRim = true; controls.rimAngle = new Vector2(65f, 25f);
                controls.rimPower = 8f; controls.rimBaseColorRatio = 0.4f;
                controls.rimColor = new Color(0.2f, 0.6f, 0.9f); controls.rimIntensity = 2f;
                update();
                check("keeps-main-light", current(0).Equals(initial[0]) && current(2).Equals(initial[2]) && current(3).Equals(initial[3]));
                check("color-intensity", current(6).Equals(new Vector4(0.4f, 1.2f, 1.8f, 1f)));
                check("power-tint-view-basis", current(9).y == 0.4f && current(9).z == 8f && Shader.GetGlobalFloat("_UseExactViewRimBasis") == 1f);
                Vector4 firstView = current(7);
                camera.transform.rotation = Quaternion.Euler(18f, 73f, -31f); update();
                Vector3 expectedView = new Vector3(Mathf.Sin(65f * Mathf.Deg2Rad) * Mathf.Cos(25f * Mathf.Deg2Rad),
                    -Mathf.Sin(25f * Mathf.Deg2Rad), Mathf.Cos(65f * Mathf.Deg2Rad) * Mathf.Cos(25f * Mathf.Deg2Rad));
                check("view-direction-roll-independent", firstView.Equals(current(7)) && ((Vector3)current(7) - expectedView).magnitude < 1e-6f);
                check("world-direction-matches-view-matrix", (camera.worldToCameraMatrix.MultiplyVector(current(8)) - expectedView).magnitude < 1e-6f);
                Vector4 later = current(9); later.y += 0.000001f;
                Shader.SetGlobalVector(vectors[9], later); update();
                controls.overrideRim = false; update();
                check("small-script-write-restored", current(9).Equals(later));
                check("original-color-direction-basis-restored", current(6).Equals(initial[6]) && current(7).Equals(initial[7]) &&
                    current(8).Equals(initial[8]) && Shader.GetGlobalFloat("_UseExactViewRimBasis") == 0f);
                controls.overrideRim = true; update();
                Vector4 late = current(7); late.x += 0.000001f;
                Shader.SetGlobalVector(vectors[7], late); Shader.SetGlobalFloat("_UseExactViewRimBasis", 0.25f);
                controls.overrideRim = false; update();
                check("late-writer-preserved", current(7).Equals(late) && Shader.GetGlobalFloat("_UseExactViewRimBasis") == 0.25f);
                controls.overrideLighting = true; controls.overrideRim = true; update();
                controls.overrideRim = false; update();
                check("lighting-retains-shared-color", current(6).Equals((Vector4)controls.rimColor));
                controls.overrideRim = true; update(); controls.overrideLighting = false; update();
                check("rim-retains-shared-color", current(6).Equals(new Vector4(0.4f, 1.2f, 1.8f, 1f)) && current(0).Equals(initial[0]));
                controls.overrideRim = false; update();
                check("both-released-color-restored", current(6).Equals(initial[6]));
                controls.overrideLighting = true; update();
                Vector4 smallLight = current(3); smallLight.x += 0.000001f;
                Shader.SetGlobalVector(vectors[3], smallLight); update(); controls.overrideLighting = false; update();
                check("small-main-light-write-restored", current(3).Equals(smallLight));
                controls.overrideRim = true; controls.rimAngle = new Vector2(float.NaN, float.PositiveInfinity);
                controls.rimPower = -1f; controls.rimBaseColorRatio = 3f; controls.rimIntensity = float.NaN; update();
                check("invalid-inputs-finite-clamped", current(7).Equals(new Vector4(0, 0, 1, 0)) && current(9).y == 1f &&
                    current(9).z == 0.01f && current(6).Equals(new Vector4(0, 0, 0, 1)));
                controls.enabled = true; controls.enabled = false;
                check("disable-releases", current(6).Equals(initial[6]) && current(7).Equals(late) &&
                    Shader.GetGlobalFloat("_UseExactViewRimBasis") == 0.25f);

                // Generated constant maps and analytic plane normal: actual actor
                // rim output, not merely a check that shader globals were set.
                Color baseColor = new Color(0.2f, 0.45f, 0.7f, 1f);
                var map = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
                map.SetPixel(0, 0, baseColor); map.Apply();
                var ramp = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
                ramp.SetPixel(0, 0, new Color(1, 1, 1, 0)); ramp.Apply();
                _material.SetTexture("_MainTex", map); _material.SetTexture("_RampTex", ramp);
                _material.SetTexture("_ShadeTex", Texture2D.blackTexture);
                _material.SetTexture("_RampAddTex", Texture2D.blackTexture);
                _material.SetFloat("_DisableDefMap", 1f); _material.SetVector("_DefValue", new Vector4(1, 0, 0, 0));
                _material.SetFloat("_EnableLayer", 0f); _material.SetFloat("_UseBump", 0f);
                _material.SetColor("_Color", Color.white); _material.SetVector("_SpecularThreshold", new Vector4(10, 10, 10, 10));
                Shader.SetGlobalFloat("_FaceDebugMode", 22f); Shader.SetGlobalFloat("_CapturedDiffuseBlend", 1f);
                Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
                controls.rimColor = new Color(0.3f, 0.7f, 1.2f); controls.rimIntensity = 1.5f;
                controls.rimAngle = new Vector2(65f, 20f);
                for (int pose = 0; pose < 2; pose++)
                {
                    Quaternion rotation = pose == 0 ? Quaternion.identity : Quaternion.Euler(27, 68, -23);
                    _camera.transform.SetPositionAndRotation(rotation * cameraPosition, rotation);
                    _quadRenderer.transform.rotation = rotation;
                    camera.transform.rotation = rotation;
                    foreach (float power in new[] { 2f, 8f }) foreach (float tint in new[] { 0f, 0.6f, 1f })
                    {
                        controls.rimPower = power; controls.rimBaseColorRatio = tint; update();
                        float factor = Mathf.Pow(1f - Mathf.Cos(65f * Mathf.Deg2Rad) * Mathf.Cos(20f * Mathf.Deg2Rad), power);
                        foreach (int mask in new[] { 0, 8, 15 })
                        {
                            Color32 packed = new Color32(0, 0, 0, (byte)(mask * 16 + 3));
                            _quad.colors32 = new[] { packed, packed, packed, packed };
                            foreach (int type in new[] { 0, 8, 9 })
                            {
                                _material.SetFloat("_ShaderType", type);
                                Color expected = Color.Lerp(Color.white, baseColor, tint) * controls.rimColor *
                                    (controls.rimIntensity * factor * mask / 15f); expected.a = 1f;
                                string name = "rim-control-gpu-" + pose + "-" + power + "-" + tint + "-" + mask + "-" + type;
                                AddColorCheck(report, name, expected, Render(name));
                            }
                        }
                    }
                }
            }
            finally
            {
                controls.overrideLighting = false; controls.overrideRim = false; update();
                _material.CopyPropertiesFromMaterial(material); _quad.colors32 = savedColors;
                _quadRenderer.transform.rotation = savedRotation;
                _camera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], saved[i]);
                Shader.SetGlobalFloat("_UseExactViewRimBasis", savedBasis);
                Shader.SetGlobalFloat("_FaceDebugMode", savedDebug);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend", savedDiffuse);
                Shader.SetGlobalFloat("_CapturedSkinSaturation", savedSaturation);
            }
        }

        private void VerifyLayerControl(Report report)
        {
            var first = Own(new Material(_material.shader));
            var second = Own(new Material(_material.shader));
            var disabled = Own(new Material(_material.shader));
            var missingMap = Own(new Material(_material.shader));
            first.SetFloat("_EnableLayer",1f); first.SetFloat("_LayerWeight",0.2f);
            second.SetFloat("_EnableLayer",1f); second.SetFloat("_LayerWeight",0.6f);
            first.SetTexture("_LayerTex",Texture2D.whiteTexture); second.SetTexture("_LayerTex",Texture2D.whiteTexture);
            disabled.SetFloat("_EnableLayer",0f); disabled.SetFloat("_LayerWeight",0.4f);
            missingMap.SetFloat("_EnableLayer",1f); missingMap.SetFloat("_LayerWeight",0.1f);
            var actor = Own(new GameObject("Generated layer controls actor"));
            actor.AddComponent<MeshRenderer>().sharedMaterials = new[]{first,second,first,disabled,missingMap};
            var cameraOwner = Own(new GameObject("Generated layer controls camera"));
            cameraOwner.AddComponent<Camera>().enabled=false;
            var controls = cameraOwner.AddComponent<ActorRenderControls>();
            controls.enabled=false;
            controls.Initialize(actor,null);
            MethodInfo apply = typeof(ActorRenderControls).GetMethod("ApplyLayerOverride",BindingFlags.Instance|BindingFlags.NonPublic);
            Action<string,bool> check = (name,accepted)=>report.checks.Add(new Check{name="layer-control-"+name,accepted=accepted});
            apply.Invoke(controls,null);
            check("default-preserves-distinct-weights",first.GetFloat("_LayerWeight")==0.2f && second.GetFloat("_LayerWeight")==0.6f);
            check("unique-enabled-materials",controls.LayerMaterialCount==2);
            controls.overrideLayer=true; controls.layerWeight=0.75f; apply.Invoke(controls,null);
            check("override-applied",first.GetFloat("_LayerWeight")==0.75f && second.GetFloat("_LayerWeight")==0.75f);
            check("disabled-material-unchanged",disabled.GetFloat("_LayerWeight")==0.4f && disabled.GetFloat("_EnableLayer")==0f);
            check("missing-map-unchanged",missingMap.GetFloat("_LayerWeight")==0.1f);
            controls.overrideLayer=false; apply.Invoke(controls,null);
            check("distinct-weights-restored",first.GetFloat("_LayerWeight")==0.2f && second.GetFloat("_LayerWeight")==0.6f);
            controls.overrideLayer=true; apply.Invoke(controls,null);
            first.SetFloat("_LayerWeight",0.3f); apply.Invoke(controls,null);
            controls.overrideLayer=false; apply.Invoke(controls,null);
            check("new-script-weight-restored",first.GetFloat("_LayerWeight")==0.3f);
            controls.overrideLayer=true; apply.Invoke(controls,null);
            second.SetFloat("_LayerWeight",0.9f);
            controls.overrideLayer=false; apply.Invoke(controls,null);
            check("late-script-write-preserved",second.GetFloat("_LayerWeight")==0.9f);
            controls.overrideLayer=true; controls.layerWeight=2f; apply.Invoke(controls,null);
            check("upper-clamp",first.GetFloat("_LayerWeight")==1f);
            controls.layerWeight=-1f; apply.Invoke(controls,null);
            check("lower-clamp",first.GetFloat("_LayerWeight")==0f);
            controls.layerWeight=float.NaN; apply.Invoke(controls,null);
            check("finite-weight",first.GetFloat("_LayerWeight")==0f);
            controls.RefreshRenderers();
            check("refresh-restores",first.GetFloat("_LayerWeight")==0.3f && second.GetFloat("_LayerWeight")==0.9f);
            controls.layerWeight=1f; apply.Invoke(controls,null);
            var replacement=Own(new GameObject("Replacement layer controls actor"));
            controls.Initialize(replacement,null);
            check("actor-change-releases-old",controls.LayerMaterialCount==0 && first.GetFloat("_LayerWeight")==0.3f);
            controls.Initialize(actor,null); apply.Invoke(controls,null);
            controls.enabled=true; controls.enabled=false;
            check("disable-restores",first.GetFloat("_LayerWeight")==0.3f && second.GetFloat("_LayerWeight")==0.9f);
            // The existing Layer shader must still use UV2 and both atlas halves.
            // Point-sampled generated inputs make the color/Ramp expectation independent of rendering.
            var savedMaterial=Own(new Material(_material)); Vector2[] savedUv2=_quad.uv2;
            string[] globals={"_FaceDebugMode","_CapturedDiffuseBlend","_CapturedDirectScale","_CapturedSkinSaturation"};
            float[] savedGlobals=Array.ConvertAll(globals,Shader.GetGlobalFloat);
            Vector4 savedLight=Shader.GetGlobalVector("_CapturedLightDirection");
            Vector4 savedParameters=Shader.GetGlobalVector("_ActorMatcapParameters");
            var atlas=Own(new Texture2D(4,2,TextureFormat.RGBAFloat,false,true));
            atlas.filterMode=FilterMode.Point;atlas.wrapMode=TextureWrapMode.Clamp;
            Color[] colors={new Color(0.8f,0.2f,0.4f,0.5f),new Color(0.1f,0.7f,0.3f,1f)};
            Color[] definitions={new Color(0.75f,0,0.4f,0),new Color(0.55f,0,0.8f,0)};
            for(int y=0;y<2;y++)for(int x=0;x<2;x++) {atlas.SetPixel(x,y,colors[y]);atlas.SetPixel(x+2,y,definitions[y]);}
            atlas.Apply();
            var ramp=Own(new Texture2D(256,1,TextureFormat.RGBAFloat,false,true));
            ramp.filterMode=FilterMode.Point;ramp.wrapMode=TextureWrapMode.Clamp;
            for(int x=0;x<256;x++)ramp.SetPixel(x,0,new Color(1,1,1,1-x/255f));ramp.Apply();
            var baseMap=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            Color baseColor=new Color(0.2f,0.4f,0.6f,1);baseMap.SetPixel(0,0,baseColor);baseMap.Apply();
            var shadeMap=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            shadeMap.SetPixel(0,0,Color.clear);shadeMap.Apply();
            try
            {
                _material.SetFloat("_ShaderType",0f);_material.SetFloat("_EnableLayer",1f);
                _material.SetFloat("_DisableDefMap",1f);_material.SetVector("_DefValue",new Vector4(0.35f,0,0.2f,0));
                _material.SetVector("_Color",Vector4.one);_material.SetTexture("_MainTex",baseMap);
                _material.SetTexture("_LayerTex",atlas);_material.SetTexture("_ShadeTex",shadeMap);
                _material.SetTexture("_RampTex",ramp);_material.SetTexture("_RampAddTex",Texture2D.blackTexture);
                Shader.SetGlobalFloat("_FaceDebugMode",19f);Shader.SetGlobalFloat("_CapturedDiffuseBlend",1f);
                Shader.SetGlobalFloat("_CapturedDirectScale",1f);Shader.SetGlobalFloat("_CapturedSkinSaturation",0f);
                Shader.SetGlobalVector("_CapturedLightDirection",new Vector4(1,0,0,1));
                Shader.SetGlobalVector("_ActorMatcapParameters",new Vector4(0.3f,1,1,0));
                for(int location=0;location<2;location++)
                {
                    Vector2 uv2=location==0?new Vector2(0.25f,0.25f):new Vector2(0.75f,0.75f);
                    _quad.uv2=new[]{uv2,uv2,uv2,uv2};
                    foreach(float weight in new[]{0f,0.5f,1f})
                    {
                        _material.SetFloat("_LayerWeight",weight);
                        float mask=colors[location].a*weight;
                        float definitionR=Mathf.Lerp(0.35f,definitions[location].r,mask);
                        float metallic=Mathf.Lerp(0.2f,definitions[location].b,mask);
                        int rampIndex=Mathf.Clamp((int)((definitionR-0.15f)*256f),0,255);
                        Color expected=Color.Lerp(baseColor,colors[location],mask)*(rampIndex/255f)*0.96f*(1-metallic);
                        expected.a=1f;
                        string name="layer-atlas-"+location+"-"+weight;
                        AddColorCheck(report,name,expected,Render(name));
                    }
                }
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial);_quad.uv2=savedUv2;
                for(int i=0;i<globals.Length;i++)Shader.SetGlobalFloat(globals[i],savedGlobals[i]);
                Shader.SetGlobalVector("_CapturedLightDirection",savedLight);
                Shader.SetGlobalVector("_ActorMatcapParameters",savedParameters);
            }
        }

        private void VerifyEyebrowHighlight(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            Vector2[] savedUv = _quad.uv;
            string[] floats = { "_FaceDebugMode", "_CapturedDiffuseBlend", "_CapturedSkinSaturation",
                "_UseCapturedActorShadow", "_ActorEnvironmentIntensity", "_UseCapturedDirectSpecular" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedShadeTint", "_ActorMatcapParameters", "_CapturedLightDirection" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            var baseMap = Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            var shadeMap = Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            var rampMap = Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            var rampAddMap = Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            Color baseColor = new Color(0.2f,0.5f,0.7f,1f);
            Color materialColor = new Color(1.2f,0.7f,1.1f,1f);
            Color ramp = new Color(0.4f,0.6f,0.2f,0.3f);
            Color tint = new Color(0.8f,1.1f,0.7f,1f);
            baseMap.SetPixel(0,0,baseColor); baseMap.Apply();
            shadeMap.SetPixel(0,0,new Color(0.1f,0.3f,0.4f,1f)); shadeMap.Apply();
            rampMap.SetPixel(0,0,ramp); rampMap.Apply();
            Vector2[] coordinates = { new Vector2(0.96875f,0.99f), new Vector2(0.99f,0.96875f),
                new Vector2(0.96874f,0.99f), new Vector2(0.99f,0.96874f),
                new Vector2(0.99f,0.99f), new Vector2(0.968751f,0.968751f) };
            try
            {
                _material.SetTexture("_MainTex",baseMap); _material.SetTexture("_ShadeTex",shadeMap);
                _material.SetTexture("_RampTex",rampMap); _material.SetTexture("_RampAddTex",rampAddMap);
                _material.SetColor("_Color",materialColor); _material.SetColor("_RampAddColor",Color.white);
                _material.SetFloat("_DisableDefMap",1f); _material.SetFloat("_EnableLayer",0f);
                _material.SetFloat("_UseReflection",0f); _material.SetFloat("_UseBump",0f);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend",1f); Shader.SetGlobalFloat("_CapturedSkinSaturation",0f);
                Shader.SetGlobalFloat("_UseCapturedActorShadow",0f); Shader.SetGlobalFloat("_ActorEnvironmentIntensity",0f);
                Shader.SetGlobalFloat("_UseCapturedDirectSpecular",1f);
                Shader.SetGlobalVector("_CapturedShadeTint",tint);
                Shader.SetGlobalVector("_ActorMatcapParameters",new Vector4(0.3f,1f,1f,0f));
                Shader.SetGlobalVector("_CapturedLightDirection",new Vector4(0,0,-1,0));
                for (int configuration=0;configuration<2;configuration++)
                {
                    Color add = configuration==0 ? Color.clear : new Color(0.4f,0.6f,0.8f,0.5f);
                    rampAddMap.SetPixel(0,0,add); rampAddMap.Apply();
                    _material.SetVector("_DefValue",new Vector4(0.5f,0.3f,0,configuration==0?0f:0.65f));
                    // BaseColor also tints the additive diffuse lookup. Keeping
                    // it on BaseMap alone would encode the old tint-stage bug.
                    Color ramped = (baseColor+add*(1f-add.a))*ramp*Color.Lerp(Color.white,tint,ramp.a)*materialColor;
                    ramped.a=1f;
                    Color highlight = ramped*2f*Color.Lerp(Color.white,add,add.a);
                    highlight.a=0f;
                    foreach (int type in new[]{0,6,9})
                    {
                        string prefix="eyebrow-highlight-"+configuration+"-type-"+type;
                        _material.SetFloat("_ShaderType",type);
                        Vector2 outside=new Vector2(0.5f,0.5f);
                        _quad.uv=new[]{outside,outside,outside,outside};
                        Shader.SetGlobalFloat("_FaceDebugMode",16f);
                        AddColorCheck(report,prefix+"-ramped-input",ramped,Render(prefix+"-ramped-input"));
                        Shader.SetGlobalFloat("_FaceDebugMode",20f);
                        Color baseline=Render(prefix+"-baseline");
                        for (int i=0;i<coordinates.Length;i++)
                        {
                            Vector2 uv=coordinates[i]; _quad.uv=new[]{uv,uv,uv,uv};
                            Color expected=baseline+(type==6 && i>=4 ? highlight : Color.clear);
                            AddColorCheck(report,prefix+"-uv-"+i,expected,Render(prefix+"-uv-"+i));
                        }
                        _quad.uv=new[]{outside,outside,outside,outside};
                        AddColorCheck(report,prefix+"-restored",baseline,Render(prefix+"-restored"));
                    }
                }
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial); _quad.uv=savedUv;
                for(int i=0;i<floats.Length;i++) Shader.SetGlobalFloat(floats[i],savedFloats[i]);
                for(int i=0;i<vectors.Length;i++) Shader.SetGlobalVector(vectors[i],savedVectors[i]);
            }
        }

        private void VerifyReflectionSphere(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            Vector3[] savedNormals = _quad.normals;
            Vector2[] savedUv = _quad.uv;
            Vector3 savedPosition = _camera.transform.position;
            Quaternion savedRotation = _camera.transform.rotation;
            bool savedOrthographic = _camera.orthographic;
            string[] floats = { "_FaceDebugMode", "_ActorEnvironmentIntensity", "_UseCapturedActorShadow", "_UseCapturedDirectSpecular" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedLightDirection", "_CapturedCameraUp", "_ActorMatcapParameters" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            var map = Own(new Texture2D(16, 16, TextureFormat.RGBAFloat, false, true));
            map.filterMode = FilterMode.Point;
            map.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    map.SetPixel(x, y, new Color(0.1f+x*0.03f, 0.15f+y*0.02f, 0.2f+(x+y)*0.01f, 0.25f));
            map.Apply();
            try
            {
                _material.SetTexture("_ReflectionSphereMap", map);
                _material.SetTexture("_RampAddTex", Texture2D.blackTexture);
                _material.SetFloat("_DisableDefMap", 1f);
                _material.SetFloat("_UseBump", 0f);
                _material.SetFloat("_EnableLayer", 0f);
                _material.SetVector("_DefValue", new Vector4(0.5f, 0.3f, 0.4f, 0.6f));
                Shader.SetGlobalFloat("_FaceDebugMode", 20f);
                Shader.SetGlobalFloat("_ActorEnvironmentIntensity", 0f);
                Shader.SetGlobalFloat("_UseCapturedActorShadow", 0f);
                Shader.SetGlobalFloat("_UseCapturedDirectSpecular", 1f);
                Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(0,0,-1,0));
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0,1,1,0));
                Vector4[] decodes = { new Vector4(1,1,0,0), new Vector4(2,2,0,1), new Vector4(1.5f,2,0,0.5f) };
                Vector3[] normals = { new Vector3(0.35f,0.2f,-1).normalized, new Vector3(-0.4f,-0.3f,-1).normalized };
                for (int cameraCase = 0; cameraCase < 2; cameraCase++)
                {
                    _camera.orthographic = cameraCase == 0;
                    _camera.transform.SetPositionAndRotation(new Vector3(0,0,-3), cameraCase == 0
                        ? Quaternion.identity : Quaternion.Euler(5,-7,19));
                    Shader.SetGlobalVector("_CapturedCameraUp", _camera.transform.up);
                    // Compute the point on the actual quad plane, independently
                    // of the shader's camera-facing projection.
                    Ray ray = _camera.ViewportPointToRay(new Vector3(32.5f/64f,32.5f/64f,0));
                    Vector3 point = ray.origin-ray.direction*(ray.origin.z/ray.direction.z);
                    Vector3 view = _camera.orthographic ? -_camera.transform.forward : (_camera.transform.position-point).normalized;
                    Vector3 horizontal = Vector3.Cross(view, _camera.transform.up).normalized;
                    Vector3 vertical = Vector3.Cross(horizontal, view).normalized;
                    for (int normalCase = 0; normalCase < normals.Length; normalCase++)
                    {
                        Vector3 n = normals[normalCase];
                        _quad.normals = new[] { n,n,n,n };
                        float u = 0.5f+0.5f*Vector3.Dot(horizontal,n);
                        float v = 0.5f+0.5f*Vector3.Dot(vertical,n);
                        Color texel = map.GetPixel(Mathf.Clamp((int)(u*16),0,15),Mathf.Clamp((int)(v*16),0,15));
                        for (int decode = 0; decode < decodes.Length; decode++)
                        {
                            Vector4 hdr = decodes[decode];
                            _material.SetVector("_ReflectionSphereMap_HDR", hdr);
                            foreach (int type in new[] { 0, 4, 8 })
                            {
                                _material.SetFloat("_ShaderType", type);
                                Vector2 uv = new Vector2(0.85f,0.85f);
                                _quad.uv = new[] { uv,uv,uv,uv };
                                _material.SetFloat("_UseReflection", 0f);
                                string name = "reflection-sphere-"+cameraCase+"-"+normalCase+"-"+decode+"-type-"+type;
                                Color baseline = Render(name+"-off");
                                _material.SetFloat("_UseReflection", 1f);
                                Color expected = texel * (hdr.x*Mathf.Pow(Mathf.Max(1f+(texel.a-1f)*hdr.w,0f),hdr.y)*0.6f);
                                expected.a = 0f;
                                AddColorCheck(report, name, baseline+expected, Render(name));
                                _material.SetFloat("_UseReflection", 0f);
                                AddColorCheck(report, name+"-restored", baseline, Render(name+"-restored"));
                            }
                        }
                    }
                }
                _material.SetFloat("_UseReflection", 1f);
                _material.SetFloat("_ShaderType", 0f);
                _material.SetVector("_DefValue", new Vector4(0.5f,0.3f,0.4f,0));
                AddColorCheck(report, "reflection-sphere-zero-mask", Color.black, Render("reflection-sphere-zero-mask"));
                _material.SetFloat("_ShaderType", 8f);
                _material.SetVector("_DefValue", new Vector4(0.5f,0.3f,0.4f,1));
                _quad.uv = new[] { Vector2.zero,Vector2.zero,Vector2.zero,Vector2.zero };
                AddColorCheck(report, "reflection-sphere-no-strand-brdf", Color.black, Render("reflection-sphere-no-strand-brdf"));
                var source = Own(new Material(_material));
                var repaired = Own(new Material(_material.shader));
                MethodInfo configure = typeof(MaterialRepairer).GetMethod("ConfigureFromSource", BindingFlags.Static|BindingFlags.NonPublic);
                // Round-trip our declared material control. Enabling an absent
                // source keyword is ignored by Unity and cannot test import.
                source.SetFloat("_UseReflection", 0f);
                configure.Invoke(null, new object[] { source,repaired });
                report.checks.Add(new Check { name="reflection-sphere-import-disabled", accepted=repaired.GetFloat("_UseReflection")==0f });
                source.SetFloat("_UseReflection", 1f);
                source.SetVector("_ReflectionSphereMap_HDR", new Vector4(2,2,0,1));
                configure.Invoke(null, new object[] { source,repaired });
                report.checks.Add(new Check { name="reflection-sphere-import-enabled", accepted=repaired.GetFloat("_UseReflection")==1f &&
                    repaired.GetTexture("_ReflectionSphereMap")==map && repaired.GetVector("_ReflectionSphereMap_HDR").Equals(new Vector4(2,2,0,1)) });
                source.SetTexture("_ReflectionSphereMap",Texture2D.whiteTexture);
                configure.Invoke(null,new object[]{source,repaired});
                report.checks.Add(new Check{name="reflection-sphere-import-uniform",accepted=repaired.GetFloat("_UseReflection")==1f &&
                    repaired.GetTexture("_ReflectionSphereMap")==Texture2D.whiteTexture});
                source.SetTexture("_ReflectionSphereMap",null);
                configure.Invoke(null,new object[]{source,repaired});
                report.checks.Add(new Check{name="reflection-sphere-import-missing",accepted=repaired.GetFloat("_UseReflection")==0f});
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(savedMaterial);
                _quad.normals=savedNormals; _quad.uv=savedUv;
                _camera.orthographic=savedOrthographic;
                _camera.transform.SetPositionAndRotation(savedPosition,savedRotation);
                for (int i=0;i<floats.Length;i++) Shader.SetGlobalFloat(floats[i],savedFloats[i]);
                for (int i=0;i<vectors.Length;i++) Shader.SetGlobalVector(vectors[i],savedVectors[i]);
            }
        }

        private void VerifyProjectionViewDirection(Report report)
        {
            var saved = Own(new Material(_material));
            Vector3[] savedNormals = _quad.normals;
            Quaternion savedActorRotation = _quadRenderer.transform.rotation;
            Vector3 savedPosition = _camera.transform.position;
            Quaternion savedRotation = _camera.transform.rotation;
            bool savedOrthographic = _camera.orthographic;
            float savedFov = _camera.fieldOfView;
            float savedDebug = Shader.GetGlobalFloat("_FaceDebugMode");
            Vector4 savedUp = Shader.GetGlobalVector("_CapturedCameraUp");
            try
            {
                _material.SetFloat("_ShaderType", 0f); _material.SetFloat("_UseBump", 0f);
                _material.SetFloat("_UseAlphaClip", 0f);
                Shader.SetGlobalFloat("_FaceDebugMode", 29f);
                int[] pixels = { 16, 32, 48 };
                Vector3[] normals = { new Vector3(0.4f, 0.3f, -0.8660254f).normalized,
                    new Vector3(0.7f, 0.2f, -0.6855655f).normalized };
                for (int projection = 0; projection < 2; projection++) for (int pose = 0; pose < 2; pose++)
                {
                    _camera.orthographic = projection == 0; _camera.fieldOfView = 20f;
                    Quaternion rotation = pose == 0 ? Quaternion.identity : Quaternion.Euler(12f, 23f, -17f);
                    _quadRenderer.transform.rotation = rotation;
                    Shader.SetGlobalVector("_CapturedCameraUp", rotation * Vector3.up);
                    foreach (float distance in new[] { 1.25f, 4.5f }) for (int normalCase = 0; normalCase < normals.Length; normalCase++)
                    {
                        Vector3 normal = normals[normalCase];
                        _quad.normals = new[] { normal, normal, normal, normal };
                        _camera.transform.SetPositionAndRotation(rotation * (Vector3.back * distance), rotation);
                        string name = "projection-view-" + projection + "-pose-" + pose + "-distance-" + distance + "-normal-" + normalCase;
                        Render(name);
                        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
                        foreach (int pixel in pixels)
                        {
                            float coordinate = ((pixel + 0.5f) / 64f * 2f - 1f);
                            float extent = _camera.orthographic ? _camera.orthographicSize : distance * Mathf.Tan(10f * Mathf.Deg2Rad);
                            Vector3 point = rotation * new Vector3(coordinate * extent * _camera.aspect, coordinate * extent, 0f);
                            // Parallel camera rays do not depend on this point
                            // or on camera distance; perspective rays still do.
                            Vector3 view = _camera.orthographic ? rotation * Vector3.back : (_camera.transform.position - point).normalized;
                            Vector3 worldNormal = rotation * normal;
                            Vector3 horizontal = Vector3.Cross(view, rotation * Vector3.up);
                            Vector3 vertical = Vector3.Cross(horizontal, view);
                            Color expected = new Color(Mathf.Max(Vector3.Dot(horizontal, worldNormal), 0f),
                                Mathf.Max(Vector3.Dot(vertical, worldNormal), 0f), Mathf.Max(Vector3.Dot(view, worldNormal), 0f), 1f);
                            Color actual = _readback.GetPixel(pixel, pixel);
                            AddColorCheck(report, name + "-pixel-" + pixel, expected, actual);
                            minimum = Mathf.Min(minimum, actual.b); maximum = Mathf.Max(maximum, actual.b);
                        }
                        if (projection != 0) report.checks.Add(new Check { name = name + "-perspective-varies",
                            maximumDifference = maximum - minimum, accepted = maximum - minimum > 0.02f });
                    }
                }
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(saved); _quad.normals = savedNormals;
                _quadRenderer.transform.rotation = savedActorRotation;
                _camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
                _camera.orthographic = savedOrthographic; _camera.fieldOfView = savedFov;
                Shader.SetGlobalFloat("_FaceDebugMode", savedDebug);
                Shader.SetGlobalVector("_CapturedCameraUp", savedUp);
            }
        }

        private void VerifyEnvironmentCoordinates(Report report)
        {
            var saved = Own(new Material(_material));
            Vector2[] savedUv = _quad.uv;
            Quaternion savedActorRotation = _quadRenderer.transform.rotation;
            Vector3 savedPosition = _camera.transform.position;
            Quaternion savedRotation = _camera.transform.rotation;
            string[] floats = { "_FaceDebugMode", "_ActorEnvironmentIntensity", "_UseCapturedEnvironmentBasis",
                "_CapturedActorCubeTransformMode", "_CapturedEyeCubeTransformMode",
                "_UseCapturedActorEnvironmentArray", "_UseCapturedType1ActorEnvironmentArray",
                "_UseCapturedEyeEnvironmentArray", "_UseCapturedActorShadow", "_CapturedType4DiffuseF0",
                "_CapturedType1DebugStage", "_CapturedType4DebugStage" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedReflectionColor", "_CapturedEyeReflectionColor", "_ActorMatcapParameters" };
            Vector4[] savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector);
            Texture savedCube = Shader.GetGlobalTexture("_ActorEnvironmentCube");
            Texture savedEyeCube = Shader.GetGlobalTexture("_ActorEyeEnvironmentCube");
            Color[] faces = { new Color(2,1,0.5f), new Color(0.5f,3,1), new Color(1,0.5f,4),
                new Color(4,2,0.5f), new Color(0.5f,4,2), new Color(2,0.5f,3) };
            Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            var cube = Own(new Cubemap(8, TextureFormat.RGBAFloat, true));
            cube.filterMode = FilterMode.Point;
            cube.wrapMode = TextureWrapMode.Clamp;
            for (int face = 0; face < 6; face++) for (int mip = 0; mip < cube.mipmapCount; mip++)
            {
                int size = Mathf.Max(1, cube.width >> mip);
                var pixels = new Color[size * size];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = faces[face];
                cube.SetPixels(pixels, (CubemapFace)face, mip);
            }
            cube.Apply(false, false);
            int Face(Vector3 direction)
            {
                Vector3 a = new Vector3(Mathf.Abs(direction.x), Mathf.Abs(direction.y), Mathf.Abs(direction.z));
                if (a.x >= a.y && a.x >= a.z) return direction.x >= 0 ? 0 : 1;
                if (a.y >= a.z) return direction.y >= 0 ? 2 : 3;
                return direction.z >= 0 ? 4 : 5;
            }
            try
            {
                _material.SetVector("_Color", Vector4.one);
                _material.SetFloat("_UseBump", 0f); _material.SetFloat("_UseReflection", 0f);
                _material.SetFloat("_EnableLayer", 0f); _material.SetFloat("_UseAlphaClip", 0f);
                _material.SetFloat("_DisableDefMap", 1f);
                _material.SetVector("_DefValue", new Vector4(0.5f, 0.5f, 0f, 0.6f));
                _material.SetTexture("_RampAddTex", Texture2D.blackTexture);
                Vector2 uv = new Vector2(0.85f, 0.85f); // Hair accessory, not the painted-strand lobe.
                _quad.uv = new[] { uv, uv, uv, uv };
                Shader.SetGlobalTexture("_ActorEnvironmentCube", cube);
                Shader.SetGlobalTexture("_ActorEyeEnvironmentCube", cube);
                Shader.SetGlobalFloat("_FaceDebugMode", 20f);
                Shader.SetGlobalFloat("_UseCapturedActorShadow", 0f);
                Shader.SetGlobalFloat("_CapturedType4DiffuseF0", 0f);
                Shader.SetGlobalFloat("_CapturedType1DebugStage", 0f); Shader.SetGlobalFloat("_CapturedType4DebugStage", 0f);
                Shader.SetGlobalFloat("_UseCapturedActorEnvironmentArray", 0f);
                Shader.SetGlobalFloat("_UseCapturedType1ActorEnvironmentArray", 0f);
                Shader.SetGlobalFloat("_UseCapturedEyeEnvironmentArray", 0f);
                Shader.SetGlobalFloat("_CapturedActorCubeTransformMode", 0f);
                Shader.SetGlobalFloat("_CapturedEyeCubeTransformMode", 43f);
                Shader.SetGlobalVector("_CapturedReflectionColor", Vector4.one);
                Shader.SetGlobalVector("_CapturedEyeReflectionColor", Vector4.one);
                Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(0.3f, 1f, 1f, 0f));
                for (int captured = 0; captured < 2; captured++) for (int face = 0; face < axes.Length; face++)
                {
                    Shader.SetGlobalFloat("_UseCapturedEnvironmentBasis", captured);
                    Vector3 normal = axes[face];
                    Quaternion rotation = Quaternion.LookRotation(-normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up);
                    _quadRenderer.transform.rotation = rotation;
                    _camera.transform.SetPositionAndRotation(normal * 3f, rotation);
                    // Parallel rays use camera orientation, while a perspective
                    // projection uses the independently derived sample point.
                    float offset = (32.5f / 64f * 2f - 1f) * _camera.orthographicSize;
                    Vector3 point = rotation * new Vector3(offset, offset, 0f);
                    Vector3 view = _camera.orthographic ? -_camera.transform.forward : (_camera.transform.position - point).normalized;
                    Vector3 reflection = Vector3.Reflect(-view, normal);
                    float fresnel = Mathf.Pow(1f - Mathf.Clamp01(Vector3.Dot(normal, view)), 4f);
                    float brdf = Mathf.Lerp(0.04f, 0.54f, fresnel) / (1f + Mathf.Pow(0.5f, 4f));
                    foreach (int type in new[] { 0, 1, 4, 8, 9 })
                    {
                        _material.SetFloat("_ShaderType", type);
                        Vector3 direction = reflection;
                        if (captured != 0 && type == 1)
                            direction = Quaternion.Euler(0f, -94.9698f, 0f) * direction;
                        if (captured != 0 && type == 4)
                            direction = new Vector3(-direction.z, -direction.y, direction.x);
                        string name = "environment-coordinates-" + captured + "-face-" + face + "-type-" + type;
                        Shader.SetGlobalFloat("_ActorEnvironmentIntensity", 0f);
                        Color baseline = Render(name + "-off");
                        Shader.SetGlobalFloat("_ActorEnvironmentIntensity", 1f);
                        Color contribution = faces[Face(direction)] * (brdf * 0.6f); contribution.a = 0f;
                        AddColorCheck(report, name, baseline + contribution, Render(name));
                        Shader.SetGlobalFloat("_ActorEnvironmentIntensity", 0f);
                        AddColorCheck(report, name + "-restored", baseline, Render(name + "-restored"));
                    }
                }
            }
            finally
            {
                _material.CopyPropertiesFromMaterial(saved); _quad.uv = savedUv;
                _quadRenderer.transform.rotation = savedActorRotation;
                _camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
                Shader.SetGlobalTexture("_ActorEnvironmentCube", savedCube);
                Shader.SetGlobalTexture("_ActorEyeEnvironmentCube", savedEyeCube);
                for (int i = 0; i < floats.Length; i++) Shader.SetGlobalFloat(floats[i], savedFloats[i]);
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], savedVectors[i]);
            }
        }

        private static void AddColorCheck(Report report, string name, Color expected, Color actual)
        {
            float difference = Mathf.Max(Mathf.Abs(expected.r - actual.r),
                Mathf.Abs(expected.g - actual.g), Mathf.Abs(expected.b - actual.b), Mathf.Abs(expected.a - actual.a));
            report.checks.Add(new Check { name = name, expected = expected, actual = actual,
                maximumDifference = difference, accepted = !float.IsNaN(difference) && difference < 0.00001f });
        }

        private void VerifyPresentationOwnership(Report report)
        {
            RenderTexture previous = _camera.targetTexture;
            var host = Own(new GameObject("Self-test presenter"));
            host.AddComponent<Camera>().enabled = false;
            var presenter = host.AddComponent<SupersamplePresenter>();
            try
            {
                presenter.Initialize(_camera, 2);
                RenderTexture sourceTarget = _camera.targetTexture;
                RenderTexture normalPresentation;
                bool normal = SupersamplePresenter.TryGetPresentationTarget(_camera, out normalPresentation);
                report.checks.Add(new Check { name = "presenter-owns-normal-source",
                    accepted = normal && normalPresentation != null && sourceTarget != previous });
                _camera.targetTexture = previous;
                RenderTexture foreignPresentation;
                bool redirected = SupersamplePresenter.TryGetPresentationTarget(_camera, out foreignPresentation);
                report.checks.Add(new Check { name = "presenter-does-not-steal-offscreen-target",
                    accepted = !redirected && foreignPresentation == null });
                _camera.targetTexture = sourceTarget;
                RenderTexture restoredPresentation;
                bool restored = SupersamplePresenter.TryGetPresentationTarget(_camera, out restoredPresentation);
                report.checks.Add(new Check { name = "presenter-registration-survives-offscreen-render",
                    accepted = restored && restoredPresentation == normalPresentation });
            }
            finally
            {
                _camera.targetTexture = previous;
                DestroyImmediate(host);
            }
        }

        private void VerifyCapturedMaterialUv(Report report)
        {
            var actor = Own(new GameObject("Synthetic UV actor"));
            var renderer = actor.AddComponent<MeshRenderer>();
            var first = Own(new Material(_material) { name = "Synthetic UV first" });
            var second = Own(new Material(_material) { name = "Synthetic UV second" });
            Vector4 original = new Vector4(1, 1, 0, 0.1f);
            first.SetVector("_BaseMap_ST", original);
            renderer.sharedMaterials = new[] { first, second };
            var block = new MaterialPropertyBlock();
            block.SetFloat("_UvGlobalSentinel", 19f);
            renderer.SetPropertyBlock(block);
            block.Clear();
            block.SetFloat("_UvSlotSentinel", 37f);
            block.SetVector("_BaseMap_ST", original);
            renderer.SetPropertyBlock(block, 0);
            var entry = new CapturedMaterialUvState.Entry {
                renderer = actor.name, material = first.name, baseMapST = new[] { 1f, 1f, 0f, 0.2f } };
            var other = new CapturedMaterialUvState.Entry {
                renderer = actor.name, material = second.name, baseMapST = new[] { 1f, 1f, 0f, 0.6f } };
            var document = new CapturedMaterialUvState.Document {
                schema = CapturedMaterialUvState.Schema, materials = new[] { entry } };
            CapturedMaterialUvState.Session session;
            string error;
            string validJson = JsonUtility.ToJson(document);
            var invalidDocuments = new Dictionary<string, string> {
                { "schema", "{\"schema\":\"wrong\",\"materials\":[]}" },
                { "empty", JsonUtility.ToJson(new CapturedMaterialUvState.Document {
                    schema = CapturedMaterialUvState.Schema, materials = new CapturedMaterialUvState.Entry[0] }) },
                { "duplicate", JsonUtility.ToJson(new CapturedMaterialUvState.Document {
                    schema = CapturedMaterialUvState.Schema, materials = new[] { entry, entry } }) },
                { "partial-invalid", JsonUtility.ToJson(new CapturedMaterialUvState.Document {
                    schema = CapturedMaterialUvState.Schema, materials = new[] { entry, new CapturedMaterialUvState.Entry {
                        renderer = actor.name, material = "missing", baseMapST = new[] { 1f, 1f, 0f, 0f } } } }) },
                { "nonfinite", validJson.Replace("0.2", "NaN") },
                { "arity", JsonUtility.ToJson(new CapturedMaterialUvState.Document {
                    schema = CapturedMaterialUvState.Schema, materials = new[] { new CapturedMaterialUvState.Entry {
                        renderer = actor.name, material = first.name, baseMapST = new[] { 1f, 1f } } } }) }
            };
            foreach (var pair in invalidDocuments)
            {
                bool applied = CapturedMaterialUvState.TryApply(actor, pair.Value, out session, out error);
                block.Clear(); renderer.GetPropertyBlock(block, 0);
                report.checks.Add(new Check { name = "captured-uv-reject-" + pair.Key,
                    accepted = !applied && session == null && !string.IsNullOrEmpty(error) &&
                        block.GetVector("_BaseMap_ST").Equals(original) });
            }
            bool missingGeometry = CapturedMaterialUvState.TryApply(actor, validJson, out session, out error, new HashSet<Renderer>());
            report.checks.Add(new Check { name = "captured-uv-reject-missing-geometry", accepted = !missingGeometry });
            renderer.sharedMaterials = new[] { first, first };
            bool ambiguous = CapturedMaterialUvState.TryApply(actor, validJson, out session, out error);
            report.checks.Add(new Check { name = "captured-uv-reject-ambiguous-material", accepted = !ambiguous });
            renderer.sharedMaterials = new[] { first, second };
            document.materials = new[] { entry, other };
            bool valid = CapturedMaterialUvState.TryApply(actor, JsonUtility.ToJson(document), out session, out error,
                new HashSet<Renderer> { renderer });
            block.Clear(); renderer.GetPropertyBlock(block, 0);
            bool preservedSlot = block.GetFloat("_UvSlotSentinel") == 37f;
            bool firstMatched = block.GetVector("_BaseMap_ST").Equals(new Vector4(1, 1, 0, 0.2f));
            block.Clear(); renderer.GetPropertyBlock(block, 1);
            bool preservedGlobal = block.GetFloat("_UvGlobalSentinel") == 19f;
            report.checks.Add(new Check { name = "captured-uv-exact-property-blocks",
                accepted = valid && session.Verify(out error) && firstMatched &&
                    block.GetVector("_BaseMap_ST").Equals(new Vector4(1, 1, 0, 0.6f)) });
            report.checks.Add(new Check { name = "captured-uv-preserves-unrelated-state",
                accepted = preservedSlot && preservedGlobal && first.GetVector("_BaseMap_ST").Equals(original) });
            block.SetVector("_BaseMap_ST", new Vector4(1, 1, 0, 0.600001f));
            renderer.SetPropertyBlock(block, 1);
            report.checks.Add(new Check { name = "captured-uv-rejects-later-property-write",
                accepted = valid && !session.Verify(out error) });
            string path;
            bool ordinary = CapturedMaterialUvState.TryReadOption(new[] { "--photo-mode", CapturedMaterialUvState.Option, "state.json" }, out path, out error);
            bool missingPath = CapturedMaterialUvState.TryReadOption(new[] { CapturedMaterialUvState.Option }, out path, out error);
            bool acceptedOption = CapturedMaterialUvState.TryReadOption(new[] { "--capture-gpa-camera-and-quit", "--use-captured-posed-geometry",
                CapturedMaterialUvState.Option, "state.json" }, out path, out error);
            report.checks.Add(new Check { name = "captured-uv-command-line-scope",
                accepted = !ordinary && !missingPath && acceptedOption && path == "state.json" });
        }

        private void VerifyCapturedCamera(Report report)
        {
            var host = Own(new GameObject("Synthetic captured camera"));
            var camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.targetTexture = _target;
            Vector3 origin = new Vector3(0.1f, 0.3f, -3f);
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1, 1, -1)) *
                Matrix4x4.TRS(origin, Quaternion.Euler(12, 7, 0), Vector3.one).inverse;
            // Deliberately not the target's square pixel aspect: matrices are
            // authoritative, including the camera getter derived from them.
            Matrix4x4 projection = Matrix4x4.Perspective(40, 16f/9f, 0.2f, 100);
            projection.m02 = 0.15f; projection.m12 = -0.05f;
            var document = new CapturedCameraState.Document { schema = CapturedCameraState.Schema,
                width = 64, height = 64, nearClip = 0.2f, farClip = 100,
                worldToCamera = CapturedCameraState.Rows(view), projection = CapturedCameraState.Rows(projection) };
            string validJson = JsonUtility.ToJson(document), error;
            CapturedCameraState.Session session;
            var invalid = new Dictionary<string, Action<CapturedCameraState.Document>> {
                { "schema", d => d.schema = "wrong" },
                { "dimensions", d => d.width = 0 },
                { "clip-planes", d => d.farClip = d.nearClip },
                { "arity", d => d.worldToCamera = new float[15] },
                { "singular", d => d.projection = new float[16] },
                { "non-affine", d => d.worldToCamera[12] = 0.1f },
                { "non-rigid", d => d.worldToCamera[0] *= 2 },
                { "handedness", d => d.worldToCamera = CapturedCameraState.Rows(Matrix4x4.identity) },
                { "gpu-y-flip", d => d.projection[5] = -d.projection[5] },
                { "orthographic", d => d.projection = CapturedCameraState.Rows(Matrix4x4.identity) }
            };
            foreach (var pair in invalid)
            {
                var bad = JsonUtility.FromJson<CapturedCameraState.Document>(validJson);
                pair.Value(bad);
                bool parsed = CapturedCameraState.TryParse(JsonUtility.ToJson(bad), out session, out error);
                report.checks.Add(new Check { name = "captured-camera-reject-" + pair.Key,
                    accepted = !parsed && session == null && !string.IsNullOrEmpty(error) });
            }
            foreach (string literal in new[] { "NaN", "Infinity" })
            {
                bool parsed = CapturedCameraState.TryParse(validJson.Replace("\"nearClip\":0.2", "\"nearClip\":" + literal),
                    out session, out error);
                report.checks.Add(new Check { name = "captured-camera-reject-" + literal, accepted = !parsed });
            }
            bool oversized = CapturedCameraState.TryParse(new string(' ', 8193), out session, out error);
            report.checks.Add(new Check { name = "captured-camera-reject-oversized", accepted = !oversized });
            string inputPath = Path.Combine(_directory, "synthetic-camera-input.json");
            File.WriteAllText(inputPath, validJson);
            if (!CapturedCameraState.TryLoad(inputPath, out session, out error)) throw new InvalidOperationException(error);
            camera.targetTexture = null;
            Vector3 before = camera.transform.position;
            bool wrongTarget = false;
            try { session.Apply(camera); } catch (InvalidOperationException) { wrongTarget = true; }
            report.checks.Add(new Check { name = "captured-camera-reject-target-before-mutation",
                accepted = wrongTarget && camera.transform.position.Equals(before) });
            camera.targetTexture = _target;
            camera.orthographic = true;
            session.Apply(camera);
            bool exact = true;
            for (int i = 0; i < 16; i++) exact &= camera.worldToCameraMatrix[i] == view[i] && camera.projectionMatrix[i] == projection[i];
            string appliedJson = session.VerifiedJson();
            File.WriteAllText(Path.Combine(_directory, "synthetic-camera-applied.json"), appliedJson);
            report.checks.Add(new Check { name = "captured-camera-exact-view-projection-and-origin",
                accepted = exact && Vector3.Distance(camera.transform.position, origin) < 0.000001f &&
                    !camera.orthographic && session.Verify(out error) });
            report.checks.Add(new Check { name = "captured-camera-input-hash-and-gpu-report",
                accepted = session.sourceSha256.Length == 64 && appliedJson.Contains(session.sourceSha256) &&
                    appliedJson.Contains("\"viewProjection\"") });
            bool duplicateApply = false;
            try { session.Apply(camera); } catch (InvalidOperationException) { duplicateApply = true; }
            report.checks.Add(new Check { name = "captured-camera-reject-reapply", accepted = duplicateApply });
            var changed = projection; changed.m02 += 0.000001f; camera.projectionMatrix = changed;
            report.checks.Add(new Check { name = "captured-camera-reject-later-projection-write", accepted = !session.Verify(out error) });
            camera.projectionMatrix = projection;
            camera.transform.position += new Vector3(0.000001f, 0, 0);
            report.checks.Add(new Check { name = "captured-camera-reject-later-transform-write", accepted = !session.Verify(out error) });
            camera.transform.position = session.position;
            camera.targetTexture = null;
            report.checks.Add(new Check { name = "captured-camera-reject-later-target-write", accepted = !session.Verify(out error) });
            camera.targetTexture = _target;
            camera.orthographic = true;
            report.checks.Add(new Check { name = "captured-camera-reject-later-projection-mode", accepted = !session.Verify(out error) });
            camera.targetTexture = null;
            var required = new[] { "--capture-gpa-camera-and-quit", "--use-captured-posed-geometry", "--capture-presented-window" };
            var args = new List<string>(required) { CapturedCameraState.Option, "state.json" };
            string path;
            bool goodOption = CapturedCameraState.TryReadOption(args.ToArray(), out path, out error);
            report.checks.Add(new Check { name = "captured-camera-command-line-valid", accepted = goodOption && path == "state.json" });
            foreach (string flag in required)
            {
                var missing = new List<string>(args); missing.Remove(flag);
                report.checks.Add(new Check { name = "captured-camera-command-line-requires-" + flag,
                    accepted = !CapturedCameraState.TryReadOption(missing.ToArray(), out path, out error) });
            }
            foreach (string flag in new[] { CapturedCameraState.Option, "--self-test-actor-rendering", "--capture-and-quit", "--validate-actor-rendering" })
            {
                var conflict = new List<string>(args) { flag, "extra" };
                report.checks.Add(new Check { name = "captured-camera-command-line-conflict-" + flag,
                    accepted = !CapturedCameraState.TryReadOption(conflict.ToArray(), out path, out error) });
            }
            args.RemoveAt(args.Count-1);
            bool missingPath = CapturedCameraState.TryReadOption(args.ToArray(), out path, out error);
            bool equalsPath = CapturedCameraState.TryReadOption(new[] { CapturedCameraState.Option + "=state.json" }, out path, out error);
            bool absent = CapturedCameraState.TryReadOption(new[] { "--photo-mode" }, out path, out error);
            report.checks.Add(new Check { name = "captured-camera-command-line-missing-equals-and-absent",
                accepted = !missingPath && !equalsPath && absent && path == null });
        }

        private void VerifyShadowSubtexelFiltering(Report report)
        {
            // Independent oracle: four half-texel-offset bilinear comparisons.
            // Do not duplicate the shader's collapsed separable kernel here.
            const int size = 4;
            var depth = Own(new Texture2D(size, size, TextureFormat.RGBAFloat, false, true));
            depth.filterMode = FilterMode.Point;
            depth.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    depth.SetPixel(x, y, new Color(((x + y * 3) % 5) / 4f, 0, 0, 1));
            depth.Apply();
            Vector3 savedPosition = _camera.transform.position;
            float savedType = _material.GetFloat("_ShaderType");
            float savedDebug = Shader.GetGlobalFloat("_FaceDebugMode");
            _camera.transform.position = new Vector3(0, 0, -1);
            _material.SetFloat("_ShaderType", 8f);
            Shader.SetGlobalFloat("_FaceDebugMode", 12f);
            Shader.SetGlobalFloat("_UseCapturedActorShadow", 1f);
            Shader.SetGlobalFloat("_UseExactCapturedActorShadowMatrix", 1f);
            Shader.SetGlobalFloat("_CapturedActorShadowStrength", 1f);
            Shader.SetGlobalFloat("_CapturedActorShadowUseOffset", 0f);
            Shader.SetGlobalTexture("_CapturedActorShadowTex", depth);
            Shader.SetGlobalVector("_CapturedActorShadowTexelSize", new Vector4(1f/size, 1f/size, size, size));
            var coordinates = new List<Vector2>();
            foreach (float y in new[] { 0f, 0.25f, 0.5f, 0.75f })
                foreach (float x in new[] { 0f, 0.25f, 0.5f, 0.75f })
                    coordinates.Add(new Vector2((1f+x)/size, (1f+y)/size));
            coordinates.AddRange(new[] { Vector2.zero, Vector2.one, new Vector2(0, 1), new Vector2(1, 0) });
            for (int i = 0; i < coordinates.Count; i++)
            {
                Vector2 uv = coordinates[i];
                Matrix4x4 matrix = Matrix4x4.zero;
                matrix.m03 = uv.x; matrix.m13 = uv.y; matrix.m23 = 0.5f; matrix.m33 = 1f;
                Shader.SetGlobalMatrix("_CapturedActorWorldToShadow", matrix);
                float expected = 0f;
                foreach (float dy in new[] { -0.5f, 0.5f })
                    foreach (float dx in new[] { -0.5f, 0.5f })
                        expected += ShadowBilinearOracle(depth, uv + new Vector2(dx/size, dy/size), 0.5f) * 0.25f;
                Color actual = Render("shadow-subtexel-" + i);
                float difference = Mathf.Max(Mathf.Abs(actual.r-expected), Mathf.Abs(actual.g-expected), Mathf.Abs(actual.b-expected));
                report.checks.Add(new Check { name = "shadow-subtexel-bilinear-" + i,
                    expected = new Color(expected, expected, expected, 1), actual = actual,
                    maximumDifference = difference, accepted = !float.IsNaN(difference) && difference <= 0.00001f });
            }
            // Shadow offset is an authored Definition.R response, independent
            // of whether the central shadow-map comparison is lit. Include
            // partial weights and neutral controls, not only all-or-none cases.
            Vector4 savedDefinition = _material.GetVector("_DefValue");
            var offsetCases = new[] {
                new Vector3(0.25f, 1f, 1f), new Vector3(0.5f, 1f, 1f),
                new Vector3(0.75f, 1f, 1f), new Vector3(1f, 1f, 1f),
                new Vector3(0.75f, 0.5f, 0.5f), new Vector3(0.75f, 0f, 1f),
                new Vector3(0.75f, 1f, 0f)
            };
            Matrix4x4 offsetMatrix = Matrix4x4.zero;
            offsetMatrix.m03 = 0.5f; offsetMatrix.m13 = 0.5f;
            offsetMatrix.m23 = 0.5f; offsetMatrix.m33 = 1f;
            Shader.SetGlobalMatrix("_CapturedActorWorldToShadow", offsetMatrix);
            foreach (float storedDepth in new[] { 0.25f, 0.75f })
            {
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        depth.SetPixel(x, y, new Color(storedDepth, 0, 0, 1));
                depth.Apply();
                float comparison = (SystemInfo.usesReversedZBuffer
                    ? 0.5f > storedDepth : 0.5f < storedDepth) ? 1f : 0f;
                foreach (int type in new[] { 0, 8, 9 })
                {
                    _material.SetFloat("_ShaderType", type);
                    for (int i = 0; i < offsetCases.Length; i++)
                    {
                        Vector3 settings = offsetCases[i];
                        _material.SetVector("_DefValue", new Vector4(settings.x, 0, 0, 1));
                        Shader.SetGlobalFloat("_CapturedActorShadowUseOffset", settings.y);
                        Shader.SetGlobalFloat("_CapturedActorShadowStrength", settings.z);
                        float expected = Mathf.Lerp(1f,
                            comparison + Mathf.Max(2f*settings.x-1f, 0f)*settings.y, settings.z);
                        string name = "shadow-definition-offset-" + type + "-" + comparison + "-" + i;
                        Color actual = Render(name);
                        float difference = Mathf.Max(Mathf.Abs(actual.r-expected), Mathf.Abs(actual.g-expected), Mathf.Abs(actual.b-expected));
                        report.checks.Add(new Check { name = name,
                            expected = new Color(expected, expected, expected, 1), actual = actual,
                            maximumDifference = difference, accepted = !float.IsNaN(difference) && difference <= 0.00001f });
                    }
                }
            }
            _material.SetVector("_DefValue", savedDefinition);
            Shader.SetGlobalFloat("_CapturedActorShadowUseOffset", 0f);
            Shader.SetGlobalFloat("_CapturedActorShadowStrength", 1f);
            Shader.SetGlobalFloat("_UseCapturedActorShadow", 0f);
            Shader.SetGlobalFloat("_UseExactCapturedActorShadowMatrix", 0f);
            Shader.SetGlobalFloat("_FaceDebugMode", savedDebug);
            _material.SetFloat("_ShaderType", savedType);
            _camera.transform.position = savedPosition;
        }

        private static float ShadowBilinearOracle(Texture2D depth, Vector2 uv, float reference)
        {
            float x = uv.x * depth.width - 0.5f, y = uv.y * depth.height - 0.5f;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float result = 0f;
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    float sample = depth.GetPixel(Mathf.Clamp(ix+dx, 0, depth.width-1),
                        Mathf.Clamp(iy+dy, 0, depth.height-1)).r;
                    bool lit = SystemInfo.usesReversedZBuffer ? reference > sample : reference < sample;
                    float wx = dx == 0 ? 1f-(x-ix) : x-ix;
                    float wy = dy == 0 ? 1f-(y-iy) : y-iy;
                    result += (lit ? 1f : 0f) * wx * wy;
                }
            return result;
        }

        private void VerifySceneDistanceFog(Report report)
        {
            var savedContext = OriginalStyleRenderPipeline.CurrentPresentationContext;
            var host = Own(new GameObject("Self-test scene fog camera"));
            var camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 10f;
            var pipeline = host.AddComponent<OriginalStyleRenderPipeline>();
            var material = (Material)typeof(OriginalStyleRenderPipeline).GetField(
                "_postMaterial", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(pipeline);
            var apply = typeof(OriginalStyleRenderPipeline).GetMethod(
                "ApplySceneDistanceFog", BindingFlags.Instance | BindingFlags.NonPublic);
            var source = Own(new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
            source.Create();
            var depth = Own(new Texture2D(8, 8, TextureFormat.RFloat, false, true));
            depth.filterMode = FilterMode.Point;
            depth.wrapMode = TextureWrapMode.Clamp;
            var readback = Own(new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true));
            var temporaries = new List<RenderTexture>();
            RenderTexture previous = RenderTexture.active;
            Color background = new Color(1.25f, 0.5f, 0.125f, 0.375f);
            Color linearFog = new Color(0.5f, 0.6f, 0.7f, 1f).linear;
            float[] distances = { 0.1f, 0.3f, 1.5f, 4f, 8f, 9.99f, 10f, 10f };
            Vector3[] settings = {
                new Vector3(0.0081f, 0.3f, 0f), new Vector3(0.7f, 0.3f, 0f),
                new Vector3(0.7f, 1f, 0f), new Vector3(0.7f, 0.3f, 0.4f),
                new Vector3(0f, 0.3f, 0f), new Vector3(0.7f, 0f, 0f) };
            try
            {
                if (material == null || material.FindPass("SCENE_DISTANCE_FOG") != 11)
                    throw new InvalidOperationException("Scene fog production pass missing");
                material.SetTexture("_CameraDepthTexture", depth);
                OriginalStyleRenderPipeline.SetPresentationContext(OriginalStyleRenderPipeline.PresentationContext.StudioLocal);
                RenderTexture.active = source;
                GL.Clear(false, true, background);
                foreach (var context in new[] { OriginalStyleRenderPipeline.PresentationContext.StudioLocal,
                    OriginalStyleRenderPipeline.PresentationContext.BakedAdv })
                {
                    OriginalStyleRenderPipeline.SetPresentationContext(context);
                    var unchanged = (RenderTexture)apply.Invoke(pipeline, new object[] { source, temporaries });
                    report.checks.Add(new Check { name = "scene-fog-no-riverbed-leak-" + context,
                        accepted = ReferenceEquals(source, unchanged) && temporaries.Count == 0 });
                }
                OriginalStyleRenderPipeline.SetPresentationContext(OriginalStyleRenderPipeline.PresentationContext.CapturedRiverbed);
                Vector4 parameters;
                Color selectedColor;
                bool selected = pipeline.TryGetSceneDistanceFog(out parameters, out selectedColor);
                report.checks.Add(new Check { name = "scene-fog-captured-profile-selected",
                    accepted = selected && parameters == new Vector4(0.0081f, 0.3f, 0f, 0f) && selectedColor == linearFog });
                pipeline.overrideSceneDistanceFog = true;
                foreach (bool orthographic in new[] { false, true })
                {
                    camera.orthographic = orthographic;
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    {
                        int index = (x + y) % 8;
                        float forward = orthographic ? (distances[index] - 0.1f) / 9.9f
                            : (10f - 1f / distances[index]) / 9.9f;
                        if (index >= 6) forward = 1f;
                        float raw = SystemInfo.usesReversedZBuffer ? 1f - forward : forward;
                        depth.SetPixel(x, y, new Color(raw, 0f, 0f, 0f));
                    }
                    depth.Apply();
                    for (int setting = 0; setting < settings.Length; setting++)
                    {
                        Vector3 config = settings[setting];
                        pipeline.sceneFogDensity = config.x;
                        pipeline.sceneFogMaximumOpacity = config.y;
                        pipeline.sceneFogSkyWeight = config.z;
                        var target = (RenderTexture)apply.Invoke(pipeline, new object[] { source, temporaries });
                        RenderTexture.active = target;
                        readback.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                        readback.Apply();
                        float maximum = 0f;
                        bool finite = true;
                        Color worstExpected = background, worstActual = background;
                        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                        {
                            int index = (x + y) % 8;
                            double weight = config.x > 0 ? 1.0 - Math.Exp(-distances[index] * config.x) : 0.0;
                            weight *= index >= 6 ? config.z : 1.0;
                            double opacity = Math.Max(0.0, Math.Min(1.0, Math.Min(weight, config.y)));
                            Color expected = background * (float)(1.0 - opacity) + linearFog * (float)(weight * opacity);
                            expected.a = background.a;
                            Color actual = readback.GetPixel(x, y);
                            for (int channel = 0; channel < 4; channel++)
                                finite &= !float.IsNaN(actual[channel]) && !float.IsInfinity(actual[channel]);
                            float difference = Mathf.Max(Mathf.Abs(actual.r - expected.r), Mathf.Abs(actual.g - expected.g),
                                Mathf.Abs(actual.b - expected.b), Mathf.Abs(actual.a - expected.a));
                            if (difference > maximum) { maximum = difference; worstExpected = expected; worstActual = actual; }
                        }
                        report.checks.Add(new Check { name = "scene-fog-actual-pipeline-" + orthographic + "-" + setting,
                            expected = worstExpected, actual = worstActual, maximumDifference = maximum,
                            accepted = finite && maximum <= 0.00001f &&
                                ((config.x == 0f || config.y == 0f) ? ReferenceEquals(target, source) : !ReferenceEquals(target, source)) });
                        foreach (RenderTexture temporary in temporaries) RenderTexture.ReleaseTemporary(temporary);
                        temporaries.Clear();
                    }
                }
                pipeline.overrideSceneDistanceFog = false;
                OriginalStyleRenderPipeline.SetPresentationContext(OriginalStyleRenderPipeline.PresentationContext.StudioLocal);
                report.checks.Add(new Check { name = "scene-fog-override-release-restores-local",
                    accepted = !pipeline.TryGetSceneDistanceFog(out parameters, out selectedColor) });
            }
            finally
            {
                foreach (RenderTexture temporary in temporaries) RenderTexture.ReleaseTemporary(temporary);
                RenderTexture.active = previous;
                source.Release();
                OriginalStyleRenderPipeline.SetPresentationContext(savedContext);
            }
        }

        // Numerical quadrature is intentionally independent of the production
        // chord/intersection formula. Test rays come from Camera, not the shader's
        // inverse view-projection implementation.
        private static double IntegrateSphereFog(SphereFogSettings fog, Vector3 start, Vector3 end)
        {
            const int steps = 2048;
            Vector3 segment = end - start;
            double sum = 0.0;
            for (int i = 0; i < steps; i++)
            {
                Vector3 point = start + segment * ((i + 0.5f) / steps) - fog.center;
                double squared = (double)point.x * point.x + (double)point.y * point.y + (double)point.z * point.z;
                sum += Math.Max(0.0, 1.0 - squared / ((double)fog.radius * fog.radius));
            }
            return sum * segment.magnitude / steps * fog.density;
        }

        private void VerifySphereFog(Report report)
        {
            var host = Own(new GameObject("Self-test sphere fog camera"));
            var camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 20f;
            camera.aspect = 4f / 3f;
            camera.fieldOfView = 60f;
            camera.orthographicSize = 2.4f;
            var pipeline = host.AddComponent<OriginalStyleRenderPipeline>();
            var material = (Material)typeof(OriginalStyleRenderPipeline).GetField(
                "_postMaterial", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(pipeline);
            var apply = typeof(OriginalStyleRenderPipeline).GetMethod(
                "ApplySphereFog", BindingFlags.Instance | BindingFlags.NonPublic);
            const int width = 32, height = 24;
            var source = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
            source.Create();
            var depth = Own(new Texture2D(width, height, TextureFormat.RFloat, false, true));
            depth.filterMode = FilterMode.Point;
            depth.wrapMode = TextureWrapMode.Clamp;
            var readback = Own(new Texture2D(width, height, TextureFormat.RGBAFloat, false, true));
            var temporaries = new List<RenderTexture>();
            RenderTexture previous = RenderTexture.active;
            Color background = new Color(1.25f, 0.5f, 0.125f, 0.375f);
            var fog = pipeline.sphereFog;
            try
            {
                if (material == null || material.FindPass("SPHERE_FOG") != 12)
                    throw new InvalidOperationException("Sphere fog production pass missing");
                RenderTexture.active = source;
                GL.Clear(true, true, background);
                var unchanged = (RenderTexture)apply.Invoke(pipeline, new object[] { source, temporaries });
                report.checks.Add(new Check { name = "sphere-fog-default-no-allocation",
                    accepted = ReferenceEquals(source, unchanged) && temporaries.Count == 0 });
                fog.enabled = true;
                fog.center = Vector3.zero;
                fog.radius = 2f;
                fog.density = 0.7f;
                float full = fog.EvaluateOpticalDepth(Vector3.back * 4f, Vector3.forward * 4f);
                report.checks.Add(new Check { name = "sphere-fog-full-chord-optical-depth",
                    maximumDifference = Mathf.Abs(full - 4f * fog.radius * fog.density / 3f),
                    accepted = Mathf.Abs(full - 4f * fog.radius * fog.density / 3f) < 0.000001f });
                report.checks.Add(new Check { name = "sphere-fog-tangent-and-zero-segment",
                    accepted = fog.EvaluateOpticalDepth(new Vector3(2,0,-4), new Vector3(2,0,4)) == 0f &&
                        fog.EvaluateOpticalDepth(Vector3.zero, Vector3.zero) == 0f });
                material.SetTexture("_CameraDepthTexture", depth);
                for (int projection = 0; projection < 3; projection++)
                {
                    camera.orthographic = projection == 1;
                    camera.ResetProjectionMatrix();
                    if (projection == 2)
                    {
                        Matrix4x4 shifted = camera.projectionMatrix;
                        shifted[0, 2] = 0.22f;
                        shifted[1, 2] = -0.31f;
                        camera.projectionMatrix = shifted;
                    }
                    for (int pose = 0; pose < 2; pose++)
                    {
                        camera.transform.SetPositionAndRotation(pose == 0 ? Vector3.zero : new Vector3(3, 2, -4),
                            pose == 0 ? Quaternion.identity : Quaternion.Euler(13, 37, 5));
                        for (int setting = 0; setting < 9; setting++)
                        {
                            fog.center = camera.transform.TransformPoint(setting == 1 ? Vector3.zero :
                                setting == 2 ? new Vector3(0, 0, -4) :
                                setting == 3 ? new Vector3(10, 0, 3) : new Vector3(0.35f, 0.6f, 3f));
                            fog.radius = setting == 7 ? 0f : 1.5f;
                            fog.density = setting == 6 ? 0f : setting == 8 ? float.NaN : 0.7f;
                            fog.maximumOpacity = setting == 5 ? 0.05f : 0.8f;
                            fog.affectSky = setting != 4;
                            var expectedPixels = new Color[width * height];
                            double maximumCpuError = 0.0;
                            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                            {
                                int index = (x + 3 * y) % 5;
                                float distance = index == 0 ? 0.3f : index == 1 ? 1f : index == 2 ? 3.2f : index == 3 ? 8f : 20f;
                                float forward = camera.orthographic ? (distance - 0.3f) / 19.7f :
                                    (1f / 0.3f - 1f / distance) / (1f / 0.3f - 1f / 20f);
                                if (index == 4) forward = 1f;
                                depth.SetPixel(x, y, new Color(SystemInfo.usesReversedZBuffer ? 1f - forward : forward, 0, 0, 0));
                                float u = (x + 0.5f) / width, v = (y + 0.5f) / height;
                                Vector3 start = camera.ViewportToWorldPoint(new Vector3(u, v, 0.3f));
                                Vector3 end = camera.ViewportToWorldPoint(new Vector3(u, v, distance));
                                double integral = fog.IsActive ? IntegrateSphereFog(fog, start, end) : 0.0;
                                maximumCpuError = Math.Max(maximumCpuError, Math.Abs(integral - fog.EvaluateOpticalDepth(start, end)));
                                double opacity = index == 4 && !fog.affectSky ? 0.0 :
                                    Math.Min(1.0 - Math.Exp(-integral), fog.maximumOpacity);
                                Color expected = Color.Lerp(background, fog.color.linear, (float)opacity);
                                expected.a = background.a;
                                expectedPixels[y * width + x] = expected;
                            }
                            depth.Apply();
                            var target = (RenderTexture)apply.Invoke(pipeline, new object[] { source, temporaries });
                            RenderTexture.active = target;
                            readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                            readback.Apply();
                            float maximum = 0f;
                            bool finite = true;
                            Color worstExpected = background, worstActual = background;
                            Color[] actualPixels = readback.GetPixels();
                            for (int i = 0; i < actualPixels.Length; i++) for (int c = 0; c < 4; c++)
                            {
                                float actual = actualPixels[i][c];
                                finite &= !float.IsNaN(actual) && !float.IsInfinity(actual);
                                float difference = Mathf.Abs(actual - expectedPixels[i][c]);
                                if (difference > maximum) { maximum = difference; worstExpected = expectedPixels[i]; worstActual = actualPixels[i]; }
                            }
                            string name = projection + "-" + pose + "-" + setting;
                            report.checks.Add(new Check { name = "sphere-fog-gpu-quadrature-" + name,
                                expected = worstExpected, actual = worstActual, maximumDifference = maximum,
                                accepted = finite && maximum < 0.0001f &&
                                    (fog.IsActive ? !ReferenceEquals(target, source) : ReferenceEquals(target, source)) });
                            report.checks.Add(new Check { name = "sphere-fog-cpu-quadrature-" + name,
                                maximumDifference = (float)maximumCpuError, accepted = maximumCpuError < 0.0001 });
                            foreach (var temporary in temporaries) RenderTexture.ReleaseTemporary(temporary);
                            temporaries.Clear();
                        }
                    }
                }
                // Real Camera.Render depth, not a supplied RFloat texture. A
                // foreground quad must occlude a rear fog sphere; clear pixels
                // may see the sphere. This detects Y/depth-space wiring errors.
                var occluder = Own(new GameObject("Self-test fog occluder"));
                occluder.layer = 30;
                occluder.AddComponent<MeshFilter>().sharedMesh = _quad;
                var occluderMaterial = Own(new Material(_material));
                occluderMaterial.SetFloat("_ZWrite", 1f);
                occluderMaterial.SetFloat("_Cull", 0f);
                occluder.AddComponent<MeshRenderer>().sharedMaterial = occluderMaterial;
                camera.cullingMask = 1 << 30;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = background;
                camera.allowMSAA = false;
                camera.targetTexture = source;
                var depthProbe = host.AddComponent<SphereFogDepthProbe>();
                for (int projection = 0; projection < 3; projection++)
                {
                    pipeline.enabled = false;
                    // The pass is invoked by the probe while the real camera's
                    // depth globals are bound, without the other post effects.
                    typeof(OriginalStyleRenderPipeline).GetMethod("EnsureResources",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(pipeline, null);
                    camera.orthographic = projection == 1;
                    camera.ResetProjectionMatrix();
                    if (projection == 2)
                    {
                        Matrix4x4 shifted = camera.projectionMatrix;
                        shifted[0, 2] = 0.22f;
                        shifted[1, 2] = -0.31f;
                        camera.projectionMatrix = shifted;
                    }
                    // Keep edges away from exact pixel centers: top-left raster
                    // fill rules must not be confused with fog depth failures.
                    occluder.transform.SetPositionAndRotation(camera.transform.TransformPoint(new Vector3(0, 0.36f, 2f)), camera.transform.rotation);
                    occluder.transform.localScale = new Vector3(0.53f, 0.82f, 1f);
                    fog.enabled = true;
                    fog.radius = 1.4f;
                    fog.density = 1.2f;
                    fog.maximumOpacity = 0.8f;
                    fog.affectSky = true;
                    fog.center = camera.transform.TransformPoint(new Vector3(0.35f, 0.6f, 4f));
                    Color[] before = null, after = null;
                    Color[] rawDepthPixels = null;
                    float tolerance = 0.0001f;
                    depthProbe.sample = rendered =>
                    {
                        RenderTexture.active = rendered;
                        readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                        readback.Apply();
                        before = readback.GetPixels();
                        var nativeDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
                        Debug.Log("[SphereFogSelfTest] depth=" + (nativeDepth == null ? "null" : nativeDepth.name + " " + nativeDepth.width + "x" + nativeDepth.height));
                        if (nativeDepth != null)
                        {
                            var depthCopy = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                            Graphics.Blit(nativeDepth, depthCopy);
                            RenderTexture.active = depthCopy;
                            readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                            readback.Apply();
                            rawDepthPixels = readback.GetPixels();
                            RenderTexture.ReleaseTemporary(depthCopy);
                        }
                        var target = (RenderTexture)apply.Invoke(pipeline, new object[] { rendered, temporaries });
                        tolerance = rendered.format == RenderTextureFormat.ARGBFloat ? 0.0001f : 0.002f;
                        RenderTexture.active = target;
                        readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                        readback.Apply();
                        after = readback.GetPixels();
                        Debug.Log("[SphereFogSelfTest] camera=" + projection + "; format=" + rendered.format);
                    };
                    camera.Render();
                    if (before == null || after == null) throw new InvalidOperationException("Real fog camera callback did not execute");
                    var preview = Own(new Texture2D(width, height, TextureFormat.RGBA32, false, true));
                    preview.SetPixels(after);
                    preview.Apply();
                    File.WriteAllBytes(Path.Combine(_directory, "sphere-fog-real-depth-" + projection + ".png"), preview.EncodeToPNG());
                    float maximum = 0f;
                    int occluded = 0, fogged = 0;
                    string worst = "";
                    bool finite = true;
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    {
                        float u = (x + 0.5f) / width, v = (y + 0.5f) / height;
                        Vector3 atPlane = camera.transform.InverseTransformPoint(camera.ViewportToWorldPoint(new Vector3(u, v, 2f)));
                        bool hitsQuad = Mathf.Abs(atPlane.x) < 0.53f && Mathf.Abs(atPlane.y - 0.36f) < 0.82f;
                        Vector3 start = camera.ViewportToWorldPoint(new Vector3(u, v, 0.3f));
                        Vector3 end = camera.ViewportToWorldPoint(new Vector3(u, v, hitsQuad ? 2f : 20f));
                        double integral = IntegrateSphereFog(fog, start, end);
                        float opacity = (float)Math.Min(1.0 - Math.Exp(-integral), fog.maximumOpacity);
                        int i = y * width + x;
                        Color expected = Color.Lerp(before[i], fog.color.linear, opacity);
                        expected.a = before[i].a;
                        for (int c = 0; c < 4; c++)
                        {
                            float difference = Mathf.Abs(after[i][c] - expected[c]);
                            finite &= !float.IsNaN(difference) && !float.IsInfinity(difference);
                            if (difference > maximum)
                            {
                                maximum = difference;
                                worst = x + "," + y + " expected=" + expected + " actual=" + after[i] + " quad=" + hitsQuad +
                                    " depth=" + (rawDepthPixels == null ? "unavailable" : rawDepthPixels[i].r.ToString("R"));
                            }
                        }
                        if (hitsQuad && opacity == 0f) occluded++;
                        if (!hitsQuad && opacity > 0.1f && Mathf.Abs(after[i].r - before[i].r) > 0.01f) fogged++;
                    }
                    report.checks.Add(new Check { name = "sphere-fog-real-camera-depth-" + projection,
                        maximumDifference = maximum, accepted = finite && maximum < tolerance && occluded > 10 && fogged > 5 });
                    Debug.Log("[SphereFogSelfTest] worst=" + worst + "; occluded=" + occluded + "; fogged=" + fogged);
                    foreach (var temporary in temporaries) RenderTexture.ReleaseTemporary(temporary);
                    temporaries.Clear();
                }
                depthProbe.sample = null;
                camera.targetTexture = null;
                fog.enabled = false;
                unchanged = (RenderTexture)apply.Invoke(pipeline, new object[] { source, temporaries });
                pipeline.sphereFog = null;
                var absent = (RenderTexture)apply.Invoke(pipeline, new object[] { source, temporaries });
                report.checks.Add(new Check { name = "sphere-fog-disable-and-null-no-allocation",
                    accepted = ReferenceEquals(source, unchanged) && ReferenceEquals(source, absent) && temporaries.Count == 0 });
                pipeline.sphereFog = fog;
                fog.enabled = true;
                apply.Invoke(pipeline, new object[] { source, temporaries });
                foreach (var temporary in temporaries) RenderTexture.ReleaseTemporary(temporary);
                temporaries.Clear();
                var otherHost = Own(new GameObject("Self-test independent fog camera"));
                otherHost.AddComponent<Camera>().enabled = false;
                var other = otherHost.AddComponent<OriginalStyleRenderPipeline>();
                unchanged = (RenderTexture)apply.Invoke(other, new object[] { source, temporaries });
                report.checks.Add(new Check { name = "sphere-fog-second-camera-independent",
                    accepted = !other.sphereFog.enabled && ReferenceEquals(source, unchanged) && temporaries.Count == 0 });
            }
            finally
            {
                foreach (var temporary in temporaries) RenderTexture.ReleaseTemporary(temporary);
                camera.targetTexture = null;
                RenderTexture.active = previous;
                source.Release();
            }
        }

        private void SetUp()
        {
            Shader shader = MaterialRepairer.FallbackShader();
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Reconstructed actor shader unavailable.");
            _material = Own(new Material(shader));
            _material.SetFloat("_Cull", 0f);
            _material.SetFloat("_VertexColor", 0f);
            _material.SetFloat("_DisableDefMap", 1f);
            _material.SetVector("_DefValue", new Vector4(0.5f, 0f, 1f, 0f));
            _material.SetTexture("_MainTex", Texture2D.whiteTexture);
            _material.SetTexture("_ShadeTex", Texture2D.blackTexture);
            var ramp = Own(new Texture2D(256, 1, TextureFormat.RGBAFloat, false, true));
            ramp.wrapMode = TextureWrapMode.Clamp;
            ramp.filterMode = FilterMode.Point;
            for (int x = 0; x < 256; x++)
            {
                float value = x / 255f;
                ramp.SetPixel(x, 0, new Color(value, value, value, 1f-value));
            }
            ramp.Apply();
            _material.SetTexture("_RampTex", ramp);
            var quad = Own(new Mesh { name = "Self-test generated quad" });
            _quad = quad;
            quad.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) };
            quad.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            quad.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            quad.uv2 = quad.uv;
            quad.triangles = new[] { 0,2,1,0,3,2 };
            quad.RecalculateBounds();
            var actor = Own(new GameObject("Self-test generated actor"));
            actor.AddComponent<MeshFilter>().sharedMesh = quad;
            _quadRenderer = actor.AddComponent<MeshRenderer>();
            _quadRenderer.sharedMaterial = _material;
            _camera = Own(new GameObject("Self-test camera")).AddComponent<Camera>();
            _camera.enabled = false;
            _camera.transform.position = new Vector3(0,0,-3);
            _camera.transform.rotation = Quaternion.identity;
            _camera.orthographic = true;
            _camera.orthographicSize = 1.2f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.magenta;
            _camera.allowHDR = true;
            _camera.allowMSAA = false;
            _target = Own(new RenderTexture(64,64,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));
            _target.Create();
            _camera.targetTexture = _target;
            _readback = Own(new Texture2D(64,64,TextureFormat.RGBAFloat,false,true));
            _preview = Own(new Texture2D(64,64,TextureFormat.RGBA32,false,true));
            Shader.SetGlobalFloat("_FaceDebugMode", 16f);
            Shader.SetGlobalFloat("_UseCapturedActorShadow", 0f);
            Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(0,0,-1,1));
            Shader.SetGlobalVector("_CapturedCameraUp", Vector3.up);
            Shader.SetGlobalVector("_CapturedShadeTint", Vector4.one);
            Shader.SetGlobalVector("_HeadRightDirection", Vector3.left);
            Shader.SetGlobalVector("_HeadUpDirection", Vector3.up);
            Shader.SetGlobalVector("_HeadDirection", Vector3.forward);
        }

        private Color Render(string name)
        {
            _camera.Render();
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = _target;
                _readback.ReadPixels(new Rect(0,0,64,64),0,0);
                _readback.Apply();
                // Assertions use float readback; PNG is only a bounded preview.
                _preview.SetPixels(_readback.GetPixels());
                _preview.Apply();
                File.WriteAllBytes(Path.Combine(_directory, name + ".png"), _preview.EncodeToPNG());
                return _readback.GetPixel(32,32);
            }
            finally { RenderTexture.active = previous; }
        }
    }
}
