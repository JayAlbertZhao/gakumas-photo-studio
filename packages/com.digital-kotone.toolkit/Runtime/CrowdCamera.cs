using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Default-off Built-in Forward adapter; native depth precedes transparent rendering.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class CrowdCamera : MonoBehaviour
    {
        public CrowdDefinition definition;
        public CrowdSettings settings = new CrowdSettings();
        public CrowdPose[] poses = Array.Empty<CrowdPose>();
        public string UnavailableReason { get; private set; }
        public string FallbackReason => crowd.FallbackReason;
        public CrowdBackend Backend => crowd.Backend;
        public ulong RenderSequence { get; private set; }
        public int AllocatedBuffers => crowd.AllocatedBuffers;
        public long ResourceBytes => crowd.ResourceBytes;
        // Native depth is updated. This separate crowd-only linear eye target is not _CameraDepthTexture.
        public RenderTexture LinearEyeDepth { get; private set; }
        private readonly CrowdRenderer crowd = new CrowdRenderer();
        private Camera view;
        private CommandBuffer commands;
        private bool prepared;
        private void OnEnable()
        {
            view = GetComponent<Camera>(); commands = new CommandBuffer { name = "Toolkit crowd after opaque / before transparent" };
            view.AddCommandBuffer(CameraEvent.AfterForwardOpaque, commands);
        }
        private void OnPreCull()
        {
            if (commands == null) return;
            commands.Clear(); prepared = false; LinearEyeDepth = null; UnavailableReason = null;
            if (GraphicsSettings.currentRenderPipeline != null || view.actualRenderingPath != RenderingPath.Forward ||
                (view.clearFlags != CameraClearFlags.SolidColor && view.clearFlags != CameraClearFlags.Skybox))
            { UnavailableReason = "Crowd camera requires Built-in Forward with host color/depth clear"; crowd.Dispose(); return; }
            try
            {
                if (!crowd.Prepare(view.targetTexture, view, definition, settings, poses)) { UnavailableReason = crowd.UnavailableReason; return; }
                crowd.Record(commands); LinearEyeDepth = new CrowdRenderer.Frame(crowd).linearEyeDepth; prepared = true;
            }
            catch (Exception error) { commands.Clear(); UnavailableReason = "Crowd camera preparation failed: " + error.Message; crowd.Dispose(); }
        }
        private void OnPostRender() { if (prepared) RenderSequence++; }
        private void OnDisable()
        {
            if (view != null && commands != null) view.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, commands);
            commands?.Clear(); commands?.Dispose(); commands = null; crowd.Dispose(); prepared = false; LinearEyeDepth = null; UnavailableReason = "Disabled crowd";
        }
    }
}
