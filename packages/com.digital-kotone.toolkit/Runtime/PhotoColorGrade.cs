using UnityEngine;

namespace GakumasPhotoMode
{
    [ExecuteAlways]
    [RequireComponent(typeof(Camera))]
    public sealed class PhotoColorGrade : MonoBehaviour
    {
        [Range(0.5f, 2f)] public float exposure = 1.03f;
        [Range(0f, 2f)] public float saturation = 1.02f;
        [Range(0.5f, 2f)] public float contrast = 1.03f;
        [Range(0f, 1f)] public float vignette = 0.13f;
        private Material _material;

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (_material == null)
            {
                Shader shader = Resources.Load<Shader>("PhotoColorGrade");
                if (shader != null) _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_material == null)
            {
                Graphics.Blit(source, destination);
                return;
            }
            _material.SetFloat("_Exposure", exposure);
            _material.SetFloat("_Saturation", saturation);
            _material.SetFloat("_Contrast", contrast);
            _material.SetFloat("_Vignette", vignette);
            Graphics.Blit(source, destination, _material);
        }

        private void OnDestroy()
        {
            if (_material != null) DestroyImmediate(_material);
        }
    }
}
