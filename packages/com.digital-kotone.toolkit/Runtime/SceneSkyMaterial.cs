using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Owns only its skybox material; callers borrow it until update failure or disposal.</summary>
    public sealed class SceneSkyMaterial : IDisposable
    {
        private Material material;
        public string UnavailableReason { get; private set; }
        public int MaterialCount => material != null ? 1 : 0;
        public bool TryUpdate(SceneSkySettings settings, out Material result)
        {
            result = null; UnavailableReason = settings == null ? "Missing sky settings" : settings.Validate();
            if (UnavailableReason == null && GraphicsSettings.currentRenderPipeline != null) UnavailableReason = "Sky material currently requires Built-in rendering";
            if (UnavailableReason != null) { Dispose(); return false; }
            var shader = Resources.Load<Shader>("SceneSkybox");
            if (shader == null || !shader.isSupported) { UnavailableReason = "Authored skybox shader unavailable"; Dispose(); return false; }
            if (material == null) material = new Material(shader) { name = "Toolkit authored sky material", hideFlags = HideFlags.HideAndDontSave };
            material.SetFloat("_SkyMode", (int)settings.source); material.SetFloat("_SkySourceMip", settings.sourceMip);
            material.SetTexture("_SkyCube", settings.source == SceneSkySource.Cubemap ? settings.texture : null);
            material.SetTexture("_SkyPanorama", settings.source == SceneSkySource.Equirectangular ? settings.texture : null);
            material.SetMatrix("_SkyRotation", Matrix4x4.Rotate(Quaternion.Inverse(settings.rotation.normalized)));
            material.SetVector("_SkyZenith", settings.zenith); material.SetVector("_SkyHorizon", settings.horizon); material.SetVector("_SkyGround", settings.ground);
            material.SetFloat("_SkyGradientPower", settings.gradientPower);
            material.SetVector("_SkyTintExposure", new Vector4(settings.tint.x, settings.tint.y, settings.tint.z, Mathf.Pow(2, settings.exposure)));
            material.SetVector("_SkySunDirection", settings.sunDirection.normalized); material.SetVector("_SkySunRadiance", settings.sunRadiance);
            float outer = settings.sunRadiusDegrees * Mathf.Deg2Rad, inner = outer * (1 - settings.sunSoftness);
            // Squared chord lengths retain tiny authored disks when cos(angle)
            // would round to1 in float32.
            float outerChord = 2 * Mathf.Sin(outer * .5f), innerChord = 2 * Mathf.Sin(inner * .5f);
            material.SetVector("_SkySun", new Vector4(settings.sunEnabled ? 1 : 0, outerChord * outerChord, innerChord * innerChord, settings.sunSoftness));
            result = material; return true;
        }
        public void Dispose() { if (material != null) UnityEngine.Object.Destroy(material); material = null; }
    }
}
