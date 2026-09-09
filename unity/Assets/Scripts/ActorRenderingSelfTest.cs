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
        private Mesh _quad;
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
                VerifyHeadReflection(report);
                VerifySkinSaturation(report);
                VerifyHairSpecularRegions(report);
                VerifyRampAddSpecular(report);
                VerifyPresentationOwnership(report);
                VerifyCapturedMaterialUv(report);
                VerifyCapturedCamera(report);
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

        private void VerifyHairSpecularRegions(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            Vector2[] savedUv = _quad.uv;
            string[] floats = { "_FaceDebugMode", "_CapturedDirectScale", "_CapturedDiffuseBlend",
                "_UseCapturedDirectSpecular", "_ActorEnvironmentIntensity", "_CapturedSkinSaturation",
                "_ActorAdditionalLightCount" };
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
                _material.SetVector("_SpecularThreshold", new Vector4(0.15f, 0, 0, 0));
                Shader.SetGlobalFloat("_CapturedDirectScale", 1f);
                Shader.SetGlobalFloat("_CapturedDiffuseBlend", 1f);
                Shader.SetGlobalFloat("_UseCapturedDirectSpecular", 1f);
                Shader.SetGlobalFloat("_ActorEnvironmentIntensity", 0f);
                Shader.SetGlobalFloat("_CapturedSkinSaturation", 0f);
                Shader.SetGlobalFloat("_ActorAdditionalLightCount", 0f);
                Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(0, 0, -1, 1));
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

        private void VerifyRampAddSpecular(Report report)
        {
            var savedMaterial = Own(new Material(_material));
            Vector2[] savedUv = _quad.uv;
            string[] floats = { "_FaceDebugMode", "_CapturedDirectScale", "_CapturedDiffuseBlend",
                "_UseCapturedDirectSpecular", "_ActorEnvironmentIntensity", "_CapturedSkinSaturation",
                "_ActorAdditionalLightCount" };
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
                Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(0, 0, -1, 1));
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
