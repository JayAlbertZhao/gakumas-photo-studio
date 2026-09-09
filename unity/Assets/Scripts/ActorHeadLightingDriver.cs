using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Reconstructs the runtime head-space vectors consumed by Campus/Actor ShaderType 9.
    /// They are deliberately absent from serialized materials and must follow the animated
    /// head bone every frame.
    /// </summary>
    public sealed class ActorHeadLightingDriver : MonoBehaviour
    {
        private Transform _head;

        public void Initialize(Transform head)
        {
            _head = head;
            Publish();
        }

        private void LateUpdate()
        {
            Publish();
        }

        private void Publish()
        {
            if (_head == null) return;
            Vector3 forward = _head.forward.normalized;
            Vector3 up = _head.up.normalized;
            Vector3 right = _head.right.normalized;
            Vector3 center = _head.position + up * 0.10f;
            Shader.SetGlobalVector("_HeadDirection", new Vector4(forward.x, forward.y, forward.z, 1f));
            Shader.SetGlobalVector("_HeadUpDirection", new Vector4(up.x, up.y, up.z, 1f));
            Shader.SetGlobalVector("_HeadRightDirection", new Vector4(right.x, right.y, right.z, 1f));
            Shader.SetGlobalVector("_HeadPosition", new Vector4(center.x, center.y, center.z, 1f));
        }
    }
}
