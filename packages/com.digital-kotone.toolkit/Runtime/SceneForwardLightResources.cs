using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>One consumer's explicit current light snapshot, GPU grid and independent shadow atlases.</summary>
    internal sealed class SceneForwardLightResources : IDisposable
    {
        private readonly SceneDecalLightRenderer snapshot = new SceneDecalLightRenderer();
        private readonly SceneLightShadowAtlas shadows = new SceneLightShadowAtlas("Toolkit Forward+ local shadows");
        private readonly SceneLightShadowAtlas mainShadow = new SceneLightShadowAtlas("Toolkit Forward+ directional shadows");
        private readonly SceneBakedShadowChannels bakedChannels = new SceneBakedShadowChannels();
        private ComputeShader compute;
        private ComputeBuffer lights, tiles;
        private int words, tilesX, tilesY, kernel, width, height;
        private Matrix4x4 view, vp;
        private bool orthographic;
        private SceneForwardLightSettings settings;
        public string FallbackReason { get; private set; }
        public SceneForwardLightBackend Backend { get; private set; }
        public int SubmittedLights => snapshot.PreparedLights.Count;
        public int CulledLights => snapshot.CulledLights;
        public int TileCount { get; private set; }
        public long GridBytes => tiles == null ? 0 : (long)tiles.count * 4;
        public long BufferBytes => GridBytes + (lights == null ? 0 : (long)lights.count * 144) + bakedChannels.Bytes;
        public int AllocatedBuffers => (lights != null ? 1 : 0) + (tiles != null ? 1 : 0) + bakedChannels.BufferCount;
        public int LocalShadowMapCount => shadows.MapCount;
        public int MainShadowMapCount => mainShadow.MapCount;
        public int ShadowTargetCount => (shadows.Atlas != null ? 1 : 0) + (mainShadow.Atlas != null ? 1 : 0);
        public bool Owns(RenderTexture texture) => texture != null && (texture == shadows.Atlas || texture == mainShadow.Atlas);
        public bool IsCreated => lights != null && lights.IsValid() && tiles != null && tiles.IsValid() &&
            bakedChannels.IsCreated && (shadows.Atlas == null || shadows.Atlas.IsCreated()) && (mainShadow.Atlas == null || mainShadow.Atlas.IsCreated());
        internal ComputeBuffer TileBuffer => tiles;
        internal List<SceneDecalLightRenderer.LightData> LightSnapshot => snapshot.PreparedLights;

        public bool Prepare(Camera camera, SceneForwardLightSettings input, int targetWidth, int targetHeight, out string error)
        {
            error = Validate(camera, input, targetWidth, targetHeight); FallbackReason = null;
            if (error != null) { Dispose(); return false; }
            try
            {
                settings = input; width = targetWidth; height = targetHeight;
                view = camera.worldToCameraMatrix; vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * view; orthographic = camera.orthographic;
                if (!snapshot.PrepareSnapshot(camera, settings.localLights, out error) || !PrepareBuffers(out error) ||
                    !shadows.Prepare(snapshot.PreparedSources, settings.localLights?.shadows, true, out error) ||
                    !mainShadow.PrepareDirectional(settings.lightDirection, settings.mainLightShadow,
                        settings.lightRadiance != Vector3.zero && (settings.diffuseScale > 0 || settings.specularScale > 0), out error))
                { Dispose(); return false; }
                bakedChannels.Prepare(snapshot.PreparedSources, true);
                return true;
            }
            catch (Exception exception) { error = "Forward light resources failed: " + exception.GetType().Name; Dispose(); return false; }
        }

        internal static string Validate(Camera camera, SceneForwardLightSettings settings, int width, int height)
        {
            if (settings == null || !settings.enabled) return "Disabled Forward lighting";
            if (camera == null || width < 1 || height < 1 || width > 4096 || height > 4096 || camera.rect != new Rect(0, 0, 1, 1) || camera.stereoEnabled || camera.allowDynamicResolution ||
                !SceneDeferredCamera.Matrix(camera.worldToCameraMatrix) || !SceneDeferredCamera.Matrix(camera.projectionMatrix))
                return "Forward light resources require valid full-viewport camera matrices and1..4096 target axes, no XR/dynamic resolution";
            if (SystemInfo.graphicsShaderLevel < 45) return "Forward light evaluation requires shader model4.5 structured buffers";
            if ((int)settings.backend < 0 || (int)settings.backend > 2 || (settings.tileSize != 8 && settings.tileSize != 16 && settings.tileSize != 32) || settings.maximumGridMiB < 1 || settings.maximumGridMiB > 128)
                return "Invalid Forward+ backend, tiles or grid budget";
            if (!Vector(settings.lightDirection, -1e6f, 1e6f) || settings.lightDirection.sqrMagnitude < 1e-8f ||
                !Vector(settings.lightRadiance, 0, 65504) || !Vector(settings.ambientIrradiance, 0, 65504) || !Range(settings.giBaseScale, 0, 4) ||
                !Range(settings.diffuseScale, 0, 4) || !Range(settings.specularScale, 0, 4) || !Range(settings.backlightScale, 0, 4) || !Range(settings.directionalGiWeight, 0, 1))
                return "Invalid Forward+ illumination";
            return SceneBakedShadowInput.ChannelValid(settings.mainBakedShadowChannel) ? null : "Invalid Forward main baked shadow channel";
        }
        private bool PrepareBuffers(out string error)
        {
            error = null; int count = SubmittedLights;
            int capacity = Mathf.NextPowerOfTwo(Mathf.Max(1, count));
            if (lights == null || !lights.IsValid() || lights.count != capacity)
            {
                lights?.Dispose(); lights = new ComputeBuffer(capacity, Marshal.SizeOf<SceneDecalLightRenderer.LightData>()) { name = "Toolkit Forward+ current lights" };
            }
            if (count > 0) lights.SetData(snapshot.PreparedLights);
            else lights.SetData(new[] { default(SceneDecalLightRenderer.LightData) });
            words = (count + 31) / 32; tilesX = (width + settings.tileSize - 1) / settings.tileSize; tilesY = (height + settings.tileSize - 1) / settings.tileSize;
            long elements = (long)tilesX * tilesY * words;
            if (compute == null) compute = Resources.Load<ComputeShader>("SceneForwardLightGrid");
            bool capable = SystemInfo.supportsComputeShaders && compute != null;
            bool fits = elements * 4 <= (long)settings.maximumGridMiB * 1024 * 1024;
            Backend = settings.backend == SceneForwardLightBackend.Auto ? SceneForwardLightBackend.Tiled : settings.backend;
            if (Backend == SceneForwardLightBackend.Tiled && (!capable || !fits))
            {
                string reason = !capable ? "Compute light-grid capability unavailable" : "Light-grid memory budget exceeded";
                if (!settings.allowBruteForceFallback) { error = reason; return false; }
                Backend = SceneForwardLightBackend.BruteForce; FallbackReason = reason;
            }
            TileCount = Backend == SceneForwardLightBackend.Tiled && count > 0 ? tilesX * tilesY : 0;
            int allocation = TileCount > 0 ? (int)elements : 1;
            if (tiles == null || !tiles.IsValid() || tiles.count != allocation)
            { tiles?.Dispose(); tiles = new ComputeBuffer(allocation, 4) { name = "Toolkit Forward+ tile light bitsets" }; }
            if (TileCount == 0) tiles.SetData(new uint[] { 0 });
            else kernel = compute.FindKernel("BuildTiles");
            return true;
        }
        public void Record(CommandBuffer commands)
        {
            mainShadow.Record(commands); shadows.Record(commands);
            if (TileCount == 0) return;
            commands.BeginSample("Toolkit Forward+ GPU tile construction");
            commands.SetComputeBufferParam(compute, kernel, "_SceneLights", lights); commands.SetComputeBufferParam(compute, kernel, "_ForwardTiles", tiles);
            commands.SetComputeIntParam(compute, "_ForwardLightCount", SubmittedLights); commands.SetComputeIntParam(compute, "_ForwardWords", words);
            commands.SetComputeIntParam(compute, "_ForwardTileSize", settings.tileSize); commands.SetComputeIntParam(compute, "_ForwardTilesX", tilesX); commands.SetComputeIntParam(compute, "_ForwardTilesY", tilesY);
            commands.SetComputeVectorParam(compute, "_ForwardTarget", new Vector4(width, height, SystemInfo.graphicsUVStartsAtTop ? 1 : 0, 0));
            commands.DispatchCompute(compute, kernel, (tilesX + 7) / 8, (tilesY + 7) / 8, words);
            commands.EndSample("Toolkit Forward+ GPU tile construction");
        }
        public void Bind(Material material)
        {
            material.SetMatrix("_ViewProjection", vp);
            material.SetVector("_CameraPosition", view.inverse.MultiplyPoint(Vector3.zero));
            material.SetVector("_CameraForward", view.inverse.MultiplyVector(Vector3.back).normalized); material.SetFloat("_Orthographic", orthographic ? 1 : 0);
            material.SetVector("_LightDirection", settings.lightDirection.normalized); material.SetVector("_LightRadiance", settings.lightRadiance);
            material.SetVector("_AmbientIrradiance", settings.ambientIrradiance);
            material.SetVector("_DirectionalResponse", new Vector4(settings.diffuseScale, settings.specularScale, settings.directionalGiWeight, settings.backlightScale)); material.SetFloat("_GiBaseScale", settings.giBaseScale);
            material.SetBuffer("_SceneLights", lights); material.SetBuffer("_ForwardTiles", tiles);
            material.SetInt("_ForwardLightCount", SubmittedLights); material.SetInt("_ForwardWords", words); material.SetInt("_ForwardTileSize", settings.tileSize);
            material.SetInt("_ForwardTilesX", tilesX); material.SetInt("_ForwardTiled", Backend == SceneForwardLightBackend.Tiled ? 1 : 0);
            material.SetTexture("_LightAtlas", snapshot.PreparedAtlas != null ? snapshot.PreparedAtlas : Texture2D.whiteTexture);
            mainShadow.BindMain(material); material.SetTexture("_MainShadowAtlas", mainShadow.Atlas); shadows.Bind(material);
            bakedChannels.Bind(material); material.SetFloat("_MainBakedChannel", (int)settings.mainBakedShadowChannel);
        }
        private static bool Range(float x, float a, float b) => !float.IsNaN(x) && !float.IsInfinity(x) && x >= a && x <= b;
        private static bool Vector(Vector3 v, float a, float b) => Range(v.x, a, b) && Range(v.y, a, b) && Range(v.z, a, b);
        public void Dispose()
        {
            lights?.Dispose(); lights = null; tiles?.Dispose(); tiles = null; TileCount = 0;
            bakedChannels.Dispose(); snapshot.Dispose(); shadows.Dispose(); mainShadow.Dispose(); settings = null; FallbackReason = null;
        }
    }
}
