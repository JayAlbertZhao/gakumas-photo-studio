using UnityEngine;

namespace GakumasPhotoMode
{
    public enum SceneShaderBackend { Raster = 0, Compute = 1 }

    // Selection never changes project quality settings or invents hardware support.
    internal static class SceneComputeSupport
    {
        internal static bool Select(SceneShaderBackend requested, bool allowFallback, ComputeShader asset,
            string kernelName, RenderTextureFormat format, out SceneShaderBackend selected,
            out int kernel, out string fallback, out string error)
        {
            selected = SceneShaderBackend.Raster; kernel = -1; fallback = error = null;
            if (requested == SceneShaderBackend.Raster) return true;
            if (requested != SceneShaderBackend.Compute) { error = "Invalid scene shader backend"; return false; }
            if (!SystemInfo.supportsComputeShaders) fallback = "Compute shaders unsupported";
            else if (!SystemInfo.SupportsRandomWriteOnRenderTextureFormat(format)) fallback = "RandomWrite format unsupported: " + format;
            else if (asset == null || !asset.HasKernel(kernelName)) fallback = "Compute resource/kernel missing: " + kernelName;
            else
            {
                kernel = asset.FindKernel(kernelName);
                if (!asset.IsSupported(kernel)) fallback = "Compute kernel unsupported: " + kernelName;
            }
            if (fallback != null)
            {
                kernel = -1;
                if (!allowFallback) { error = fallback; return false; }
                return true;
            }
            selected = SceneShaderBackend.Compute; return true;
        }
    }
}
