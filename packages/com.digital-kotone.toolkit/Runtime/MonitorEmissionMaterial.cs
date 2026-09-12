using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    [Serializable]
    public sealed class MonitorEmissionSettings
    {
        [Range(0, 3)] public int uvChannel;
        public Vector4 monitorUV = new Vector4(1, 1, 0, 0);
        public Vector3 linearTint = Vector3.one;
        [Range(0, 4096)] public float intensity = 1;
        public Texture ledPattern;
        public Vector2 ledTiling = Vector2.one;
        [Range(0, 1)] public float ledStrength;
        public CullMode cull = CullMode.Back;
        internal bool IsValid => uvChannel >= 0 && uvChannel <= 3 && HdrMonitor.Finite(monitorUV) &&
            HdrMonitor.Range(linearTint.x, 0, 4096) && HdrMonitor.Range(linearTint.y, 0, 4096) && HdrMonitor.Range(linearTint.z, 0, 4096) &&
            HdrMonitor.Range(intensity, 0, 4096) && HdrMonitor.Range(ledStrength, 0, 1) &&
            HdrMonitor.Range(ledTiling.x, -1e6f, 1e6f) && HdrMonitor.Range(ledTiling.y, -1e6f, 1e6f) &&
            (ledPattern == null || ledPattern.dimension == TextureDimension.Tex2D) && (int)cull >= 0 && (int)cull <= 2;
    }

    /// <summary>Owned emissive consumer material; caller explicitly assigns/removes it on their renderer.</summary>
    public sealed class MonitorEmissionMaterial : IDisposable
    {
        public Material Material { get; private set; }
        private bool _disposed;
        public bool TryBind(HdrMonitor.Frame frame, MonitorEmissionSettings settings, out string error)
        {
            error = null;
            if (_disposed) { error = "Monitor material disposed"; return false; }
            if (!frame.IsCurrent || settings == null || !settings.IsValid) { Unbind(); error = "Invalid monitor frame or emission settings"; return false; }
            if (Material == null)
            {
                Shader shader = Resources.Load<Shader>("MonitorEmission");
                if (shader == null || !shader.isSupported) { error = "Monitor emission shader unavailable"; return false; }
                Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            Material.SetTexture("_MonitorTex", frame.texture); Material.SetVector("_MonitorUV", settings.monitorUV);
            Material.SetFloat("_MonitorChannel", settings.uvChannel); Material.SetVector("_MonitorTint", settings.linearTint);
            Material.SetFloat("_MonitorIntensity", settings.intensity); Material.SetFloat("_Cull", (int)settings.cull);
            Material.SetTexture("_LedPattern", settings.ledPattern != null ? settings.ledPattern : Texture2D.whiteTexture);
            Material.SetVector("_LedTiling", settings.ledTiling); Material.SetFloat("_LedStrength", settings.ledStrength);
            return true;
        }
        public void Unbind() { if (Material != null) Material.SetTexture("_MonitorTex", Texture2D.blackTexture); }
        public void Dispose()
        {
            if (Material != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(Material); else UnityEngine.Object.DestroyImmediate(Material); Material = null; }
            _disposed = true;
        }
    }
}
