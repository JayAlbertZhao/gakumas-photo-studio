using System;
using System.Collections.Generic;
using UnityEngine;

namespace GakumasPhotoMode
{
    // Separate optional channel table leaves the existing144-byte light ABI intact.
    internal sealed class SceneBakedShadowChannels : IDisposable
    {
        private ComputeBuffer buffer;
        private int[] channels = Array.Empty<int>();
        public bool Active { get; private set; }
        public int BufferCount => buffer != null ? 1 : 0;
        public long Bytes => buffer != null ? (long)buffer.count * 4 : 0;
        public bool IsCreated => buffer == null || buffer.IsValid();
        public void Prepare(List<SceneDecalLight> lights, bool structured)
        {
            Active = false;
            for (int i = 0; i < lights.Count; i++) Active |= lights[i].bakedShadowChannel != SceneBakedShadowChannel.None;
            if (!Active) { Dispose(); return; }
            if (channels.Length != lights.Count) channels = new int[lights.Count];
            for (int i = 0; i < lights.Count; i++) channels[i] = (int)lights[i].bakedShadowChannel;
            if (!structured) { buffer?.Dispose(); buffer = null; return; }
            int capacity = Mathf.NextPowerOfTwo(lights.Count);
            if (buffer == null || !buffer.IsValid() || buffer.count != capacity)
            { buffer?.Dispose(); buffer = new ComputeBuffer(capacity, 4) { name = "Toolkit current baked shadow channels" }; }
            buffer.SetData(channels);
        }
        public void Bind(Material material)
        {
            if (Active) material.EnableKeyword("SCENE_BAKED_LIGHT_CHANNELS"); else material.DisableKeyword("SCENE_BAKED_LIGHT_CHANNELS");
            if (buffer != null) material.SetBuffer("_SceneBakedChannels", buffer);
        }
        public void BindSingle(MaterialPropertyBlock block, int index) => block.SetFloat("_SingleBakedChannel", Active ? channels[index] : 0);
        public void Dispose() { buffer?.Dispose(); buffer = null; channels = Array.Empty<int>(); Active = false; }
    }
}
