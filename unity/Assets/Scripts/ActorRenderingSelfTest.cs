using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Asset-free GPU contracts using only a generated quad and ramp.</summary>
    public sealed class ActorRenderingSelfTest : MonoBehaviour
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
                report.accepted = report.checks.TrueForAll(check => check.accepted);
            }
            catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
            finally
            {
                if (_camera != null) _camera.targetTexture = null;
                if (_target != null) _target.Release();
                foreach (UnityEngine.Object value in _owned) if (value != null) Destroy(value);
            }
            if (!string.IsNullOrEmpty(_directory) && Directory.Exists(_directory))
                File.WriteAllText(Path.Combine(_directory, "actor-synthetic.json"), JsonUtility.ToJson(report, true));
            Debug.Log("[ActorSelfTest] accepted=" + report.accepted + "; checks=" + report.checks.Count);
            Application.Quit(report.accepted ? 0 : 2);
        }

        private T Own<T>(T value) where T : UnityEngine.Object { _owned.Add(value); return value; }

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
            quad.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) };
            quad.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            quad.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            quad.uv2 = quad.uv;
            quad.triangles = new[] { 0,2,1,0,3,2 };
            quad.RecalculateBounds();
            var actor = Own(new GameObject("Self-test generated actor"));
            actor.AddComponent<MeshFilter>().sharedMesh = quad;
            actor.AddComponent<MeshRenderer>().sharedMaterial = _material;
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
            Shader.SetGlobalVector("_HeadRightDirection", Vector3.right);
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
