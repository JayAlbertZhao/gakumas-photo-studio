using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Built-in pipeline actor passes and a small, independent lighting workbench.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class ActorRenderControls : MonoBehaviour
    {
        public bool outlines = true;
        public bool hairCover = true;
        [Tooltip("Width range in centimetres. Studio defaults, not universal game settings.")]
        public Vector2 outlineWidth = new Vector2(0.04f, 0.12f);
        public float outlineRange = 3f;
        public bool overrideLighting;
        public bool worldSpaceLight;
        public Vector2 lightAngle = new Vector2(-5f, 10f);
        [Range(-1f, 1f)] public float diffuseOffset = 0.3f;
        [Range(0f, 2f)] public float smoothnessScale = 1f;
        [Range(0f, 1f)] public float shadeStrength = 1f;
        [Range(0f, 2f)] public float giScale;
        [Range(0f, 3f)] public float additionalLightScale = 1f;
        [Range(0f, 3f)] public float additionalSpecularScale = 1f;
        [ColorUsage(false, true)] public Color lightColor = Color.white;
        [ColorUsage(false, true)] public Color shadeColor = Color.white;
        [ColorUsage(false, true)] public Color shadeAdditive = Color.black;
        [ColorUsage(false, true)] public Color rimColor = new Color(0.7f, 0.7f, 0.7f);
        public bool showPanel;
        public int OutlineDrawCount { get; private set; }
        public int HairCoverDrawCount { get; private set; }
        public int AdditionalLightCount { get; private set; }

        private Camera _camera;
        private GameObject _actor;
        private Light _key;
        private Shader _shader;
        private Shader _supplementalShader;
        private readonly Dictionary<Material, Material> _supplemental = new Dictionary<Material, Material>();
        private CommandBuffer _commands;
        private Renderer[] _renderers = Array.Empty<Renderer>();
        private Light[] _lights = Array.Empty<Light>();
        private float _nextLightScan;
        private GameObject _testLights;
        private readonly Vector4[] _positions = new Vector4[8];
        private readonly Vector4[] _colors = new Vector4[8];
        private readonly Vector4[] _directions = new Vector4[8];
        private readonly Vector4[] _spots = new Vector4[8];
        private readonly List<Light> _selected = new List<Light>();
        private static readonly string[] OverrideGlobals = {
            "_ActorMatcapParameters", "_ActorLightingScales", "_CapturedLightDirection",
            "_CapturedLightColor", "_CapturedShadeTint", "_CapturedShadeAdditive", "_ActorRimColor"
        };
        private readonly Vector4[] _savedGlobals = new Vector4[OverrideGlobals.Length];
        private readonly Vector4[] _appliedGlobals = new Vector4[OverrideGlobals.Length];
        private bool _wasOverridden;

        public void Initialize(GameObject actor, Light key)
        {
            _actor = actor;
            _key = key;
            _shader = MaterialRepairer.FallbackShader();
            _supplementalShader = Resources.Load<Shader>("ActorSupplemental");
            RefreshRenderers();
            Debug.Log("[ActorRendering] Bound " + _renderers.Length + " renderers; shader=" +
                (_shader == null ? "missing" : _shader.name));
            using (var debugMaterial = new MaterialScope(_supplementalShader))
                for (int pass = 0; pass < debugMaterial.material.passCount; pass++)
                    Debug.Log("[ActorRendering] Shader pass " + pass + "=" + debugMaterial.material.GetPassName(pass));
        }

        public void RefreshRenderers()
        {
            foreach (Material material in _supplemental.Values) Destroy(material);
            _supplemental.Clear();
            _renderers = _actor == null ? Array.Empty<Renderer>() : _actor.GetComponentsInChildren<Renderer>(true);
        }

        private sealed class MaterialScope : IDisposable
        {
            public readonly Material material;
            public MaterialScope(Shader shader) { material = new Material(shader); }
            public void Dispose() { Destroy(material); }
        }

        private void OnEnable()
        {
            _camera = GetComponent<Camera>();
            _commands = new CommandBuffer { name = "Photo Studio: body outline, hair coverage, hair outline" };
            _camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _commands);
            string[] args = Environment.GetCommandLineArgs();
            outlines = Array.IndexOf(args, "--no-actor-outline") < 0;
            hairCover = Array.IndexOf(args, "--no-hair-cover") < 0;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8)) showPanel = !showPanel;
        }

        private void OnPreCull()
        {
            if (_commands == null) return;
            ApplyOverride();
            Shader.SetGlobalVector("_ActorKeyColor", _key != null && _key.isActiveAndEnabled
                ? (Vector4)(_key.color.linear * _key.intensity) : Vector4.one);
            PublishLights();
            float focal = _camera.orthographic ? 1f :
                Mathf.Tan(31f * Mathf.Deg2Rad * 0.5f) /
                Mathf.Max(0.01f, Mathf.Tan(_camera.fieldOfView * Mathf.Deg2Rad * 0.5f));
            Shader.SetGlobalVector("_ActorOutlineParameters", new Vector4(
                Mathf.Max(0f, outlineWidth.x), Mathf.Max(0f, outlineWidth.y),
                1f / Mathf.Max(0.01f, outlineRange), focal));
            _commands.Clear();
            OutlineDrawCount = 0;
            HairCoverDrawCount = 0;
            // Supplementary materials are never assigned to renderers, so their
            // Always passes only execute here. Unknown LightMode passes in a
            // built-in shader are otherwise stripped by the installed URP tools.
            if (outlines) DrawPass("ACTOR_OUTLINE", -1);
            // Coverage owns the visible hair surface depth, including faded
            // bangs. Submit it before the hair's back-facing outline shell.
            if (hairCover) DrawPass("ACTOR_HAIR_COVER", 1);
            if (outlines) DrawPass("ACTOR_OUTLINE", 1);
        }

        private void DrawPass(string passName, int hairFilter)
        {
            foreach (Renderer renderer in _renderers)
            {
                if (renderer == null || !renderer.enabled || renderer.forceRenderingOff ||
                    !renderer.gameObject.activeInHierarchy ||
                    (_camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
                Material[] materials = renderer.sharedMaterials;
                for (int submesh = 0; submesh < materials.Length; submesh++)
                {
                    Material material = materials[submesh];
                    if (material == null || material.shader != _shader) continue;
                    bool isHair = Mathf.Abs(material.GetFloat("_ShaderType") - 8f) <= 0.25f;
                    if ((hairFilter > 0 && !isHair) || (hairFilter < 0 && isHair)) continue;
                    bool isHairCover = passName == "ACTOR_HAIR_COVER";
                    if (isHairCover)
                    {
                        if (!material.GetShaderPassEnabled("ActorHairCover")) continue;
                    }
                    else if (material.GetFloat("_OutlineEnabled") < 0.5f ||
                        !material.GetShaderPassEnabled("ActorOutline")) continue;
                    Material drawMaterial;
                    if (!_supplemental.TryGetValue(material, out drawMaterial))
                    {
                        drawMaterial = new Material(_supplementalShader) { hideFlags = HideFlags.HideAndDontSave };
                        _supplemental.Add(material, drawMaterial);
                    }
                    // Follow live material animation, layer weights and fades.
                    // DrawRenderer also carries the renderer's property block.
                    drawMaterial.CopyPropertiesFromMaterial(material);
                    int pass = drawMaterial.FindPass(passName);
                    if (pass < 0) continue;
                    _commands.DrawRenderer(renderer, drawMaterial, submesh, pass);
                    if (isHairCover) HairCoverDrawCount++; else OutlineDrawCount++;
                }
            }
        }

        private void PublishLights()
        {
            if (Time.unscaledTime >= _nextLightScan)
            {
                _lights = FindObjectsOfType<Light>();
                _nextLightScan = Time.unscaledTime + 0.5f;
            }
            _selected.Clear();
            foreach (Light light in _lights)
            {
                if (light == null || !light.isActiveAndEnabled || light.intensity <= 0f ||
                    (light.type != LightType.Point && light.type != LightType.Spot) ||
                    (light.cullingMask & (1 << OriginalStyleRenderPipeline.ActorLayer)) == 0) continue;
                _selected.Add(light);
            }
            Vector3 center = _actor == null ? transform.position : _actor.transform.position;
            _selected.Sort((a, b) => {
                int comparison = (a.transform.position - center).sqrMagnitude.CompareTo(
                    (b.transform.position - center).sqrMagnitude);
                return comparison != 0 ? comparison : a.GetInstanceID().CompareTo(b.GetInstanceID());
            });
            AdditionalLightCount = Mathf.Min(8, _selected.Count);
            for (int i = 0; i < AdditionalLightCount; i++)
            {
                Light light = _selected[i];
                Vector3 position = light.transform.position;
                _positions[i] = new Vector4(position.x, position.y, position.z,
                    1f / Mathf.Max(0.0001f, light.range * light.range));
                _colors[i] = light.color.linear * light.intensity;
                Vector3 direction = light.transform.forward;
                _directions[i] = new Vector4(direction.x, direction.y, direction.z,
                    light.type == LightType.Spot ? Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5f) : -1f);
                _spots[i] = new Vector4(Mathf.Cos(light.innerSpotAngle * Mathf.Deg2Rad * 0.5f), 0f, 0f, 0f);
            }
            Shader.SetGlobalInt("_ActorAdditionalLightCount", AdditionalLightCount);
            Shader.SetGlobalVectorArray("_ActorAdditionalPositions", _positions);
            Shader.SetGlobalVectorArray("_ActorAdditionalColors", _colors);
            Shader.SetGlobalVectorArray("_ActorAdditionalDirections", _directions);
            Shader.SetGlobalVectorArray("_ActorAdditionalSpots", _spots);
        }

        private void ApplyOverride()
        {
            if (!overrideLighting) { RestoreOverride(); return; }
            for (int i = 0; i < OverrideGlobals.Length; i++)
            {
                Vector4 current = Shader.GetGlobalVector(OverrideGlobals[i]);
                if (!_wasOverridden || current != _appliedGlobals[i]) _savedGlobals[i] = current;
            }
            _wasOverridden = true;
            Shader.SetGlobalVector("_ActorMatcapParameters", new Vector4(diffuseOffset, smoothnessScale, shadeStrength, 0f));
            Shader.SetGlobalVector("_ActorLightingScales", new Vector4(giScale, additionalLightScale, additionalSpecularScale, 0f));
            Quaternion rotation = worldSpaceLight
                ? Quaternion.Euler(-lightAngle.x, lightAngle.y + 180f, 0f)
                : Quaternion.AngleAxis(-transform.eulerAngles.z, Vector3.forward) *
                    Quaternion.Euler(-lightAngle.y, lightAngle.x, 0f);
            Vector3 direction = rotation * Vector3.forward;
            Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(direction.x, direction.y, direction.z, worldSpaceLight ? 1f : 0f));
            Shader.SetGlobalVector("_CapturedLightColor", lightColor);
            Shader.SetGlobalVector("_CapturedShadeTint", shadeColor);
            Shader.SetGlobalVector("_CapturedShadeAdditive", shadeAdditive);
            Shader.SetGlobalVector("_ActorRimColor", rimColor);
            for (int i = 0; i < OverrideGlobals.Length; i++)
                _appliedGlobals[i] = Shader.GetGlobalVector(OverrideGlobals[i]);
        }

        private void RestoreOverride()
        {
            if (!_wasOverridden) return;
            for (int i = 0; i < OverrideGlobals.Length; i++)
                if (Shader.GetGlobalVector(OverrideGlobals[i]) == _appliedGlobals[i])
                    Shader.SetGlobalVector(OverrideGlobals[i], _savedGlobals[i]);
            _wasOverridden = false;
        }

        public void ToggleTestLights()
        {
            if (_testLights != null) { Destroy(_testLights); _testLights = null; return; }
            _testLights = new GameObject("Studio rendering test lights");
            for (int i = 0; i < 2; i++)
            {
                Light light = new GameObject(i == 0 ? "Warm point" : "Cool point").AddComponent<Light>();
                light.transform.SetParent(_testLights.transform, false);
                light.type = LightType.Point;
                light.range = 3f;
                light.intensity = 0.4f;
                light.color = i == 0 ? new Color(1f, 0.25f, 0.05f) : new Color(0.1f, 0.35f, 1f);
                light.transform.position = (_actor == null ? Vector3.zero : _actor.transform.position) +
                    new Vector3(i == 0 ? -0.8f : 0.8f, 1.3f, 0.5f);
                light.cullingMask = 1 << OriginalStyleRenderPipeline.ActorLayer;
            }
            _nextLightScan = 0f;
        }

        private void OnGUI()
        {
            if (!showPanel) return;
            GUILayout.BeginArea(new Rect(Screen.width - 310, 20, 290, 495), GUI.skin.box);
            GUILayout.Label("ACTOR RENDERING / F8");
            outlines = GUILayout.Toggle(outlines, "Smooth-normal outline");
            hairCover = GUILayout.Toggle(hairCover, "Hair over eyes (stencil only)");
            overrideLighting = GUILayout.Toggle(overrideLighting, "Override story lighting");
            GUI.enabled = overrideLighting;
            worldSpaceLight = GUILayout.Toggle(worldSpaceLight, "World-space main light");
            lightAngle.x = Slider("Light X", lightAngle.x, -180f, 180f);
            lightAngle.y = Slider("Light Y", lightAngle.y, -90f, 90f);
            diffuseOffset = Slider("Diffuse offset", diffuseOffset, -1f, 1f);
            shadeStrength = Slider("Shade strength", shadeStrength, 0f, 1f);
            smoothnessScale = Slider("Smoothness", smoothnessScale, 0f, 2f);
            giScale = Slider("Ambient", giScale, 0f, 2f);
            additionalLightScale = Slider("Additional lights", additionalLightScale, 0f, 3f);
            GUI.enabled = true;
            if (GUILayout.Button("Toggle two test lights")) ToggleTestLights();
            GUILayout.Label(string.Format("Outline {0} / Hair {1} / Lights {2}", OutlineDrawCount, HairCoverDrawCount, AdditionalLightCount));
            GUILayout.EndArea();
        }

        private static float Slider(string label, float value, float min, float max)
        {
            GUILayout.Label(label + ": " + value.ToString("0.00"));
            return GUILayout.HorizontalSlider(value, min, max);
        }

        private void OnDisable()
        {
            RestoreOverride();
            if (_camera != null && _commands != null)
                _camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _commands);
            if (_commands != null) _commands.Release();
            _commands = null;
            foreach (Material material in _supplemental.Values) Destroy(material);
            _supplemental.Clear();
            Shader.SetGlobalInt("_ActorAdditionalLightCount", 0);
            if (_testLights != null) Destroy(_testLights);
        }
    }
}
