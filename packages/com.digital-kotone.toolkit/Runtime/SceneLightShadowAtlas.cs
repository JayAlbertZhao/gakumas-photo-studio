using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    // Per-camera producer. Does not borrow captured-actor globals or any original shader ABI.
    internal sealed class SceneLightShadowAtlas : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ShadowData
        {
            public Matrix4x4 worldToShadow;
            public Vector4 atlasST, depth, options;
        }
        private readonly List<ShadowData> _data = new List<ShadowData>();
        private readonly List<int> _indices = new List<int>();
        private readonly List<Material> _materials = new List<Material>();
        private SceneShadowCaster[] _casters;
        private ComputeBuffer _buffer;
        private int _tileSize, _grid;
        public RenderTexture Atlas { get; private set; }
        public int MapCount => _indices.Count;
        public int CasterDrawCalls { get; private set; }

        public static string ValidateLight(SceneDecalLight light)
        {
            var input = light.shadow;
            if (input == null || !input.enabled) return null;
            if (light.shape != SceneDecalLightShape.Spot) return "Light-source shadows currently require Spot shape";
            if (!Range(input.strength, 0, 1) || !Range(input.nearPlane, .001f, light.range) || input.nearPlane >= light.range ||
                !Range(input.depthBias, 0, light.range) || !Range(input.normalBias, 0, light.range) || (int)input.filter < 0 || (int)input.filter > 1)
                return "Invalid light-source shadow strength, clipping, bias or filter";
            return null;
        }

        public bool Prepare(List<SceneDecalLight> lights, SceneLightShadowSettings settings, bool instanced, out string error)
        {
            error = null; _indices.Clear(); _data.Clear(); CasterDrawCalls = 0;
            for (int i = 0; i < lights.Count; i++)
            {
                _data.Add(default);
                if (lights[i].shadow != null && lights[i].shadow.enabled && lights[i].shadow.strength > 0) _indices.Add(i);
            }
            if (_indices.Count == 0) { Dispose(); return true; }
            if (settings == null || settings.tileResolution < 32 || settings.tileResolution > 2048 || !Mathf.IsPowerOfTwo(settings.tileResolution) ||
                settings.maxShadowedLights < 1 || settings.maxShadowedLights > 16 || _indices.Count > settings.maxShadowedLights ||
                settings.casters == null || settings.casters.Length > 1024)
            { error = "Invalid shadow atlas settings or shadow-light/caster budget exceeded"; return false; }
            foreach (var caster in settings.casters)
            { error = ValidateCaster(caster); if (error != null) return false; }
            var shader = Resources.Load<Shader>("SceneLightShadowCaster");
            if (shader == null || !shader.isSupported || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat))
            { error = "Light-source shadow shader or RFloat target unavailable"; return false; }
            _casters = settings.casters; _tileSize = settings.tileResolution; _grid = Mathf.CeilToInt(Mathf.Sqrt(_indices.Count));
            int size = _grid * _tileSize;
            if (size > Mathf.Min(SystemInfo.maxTextureSize, 4096)) { error = "Light-source shadow atlas exceeds texture limit"; return false; }
            if (Atlas == null || !Atlas.IsCreated() || Atlas.width != size)
            {
                ReleaseAtlas(); Atlas = new RenderTexture(size, size, 24, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) {
                    name = "Toolkit light-source shadow atlas", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave
                };
                if (!Atlas.Create()) { error = "Light-source shadow allocation failed"; return false; }
            }
            for (int tile = 0; tile < _indices.Count; tile++)
            {
                int index = _indices[tile]; var light = lights[index]; var input = light.shadow;
                var view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(light.position, light.rotation.normalized, Vector3.one).inverse;
                var projection = GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(light.spotOuterAngle, 1, input.nearPlane, light.range), true);
                float y = (float)(tile / _grid) / _grid;
                // SetViewport uses native target coordinates. Only the projected local UV
                // is flipped in HLSL; flipping the tile row here would exchange atlas rows.
                _data[index] = new ShadowData {
                    worldToShadow = projection * view,
                    atlasST = new Vector4(1f / _grid, 1f / _grid, (float)(tile % _grid) / _grid, y),
                    depth = new Vector4(input.nearPlane, light.range, input.depthBias, input.normalBias),
                    options = new Vector4(input.strength, (int)input.filter, 1f / size, 0)
                };
            }
            if (instanced)
            {
                int capacity = Mathf.NextPowerOfTwo(lights.Count);
                if (_buffer == null || _buffer.count != capacity)
                { _buffer?.Dispose(); _buffer = new ComputeBuffer(capacity, Marshal.SizeOf<ShadowData>(), ComputeBufferType.Structured) { name = "Toolkit scene light shadow metadata" }; }
                _buffer.SetData(_data);
            }
            else { _buffer?.Dispose(); _buffer = null; }
            // One owned material per recorded draw: DrawRenderer has no property-block argument.
            int count = _indices.Count * _casters.Length;
            while (_materials.Count < count) _materials.Add(new Material(shader) { hideFlags = HideFlags.HideAndDontSave });
            while (_materials.Count > count) { int last = _materials.Count - 1; UnityEngine.Object.Destroy(_materials[last]); _materials.RemoveAt(last); }
            return true;
        }

        public void Record(CommandBuffer commands)
        {
            if (Atlas == null) return;
            commands.BeginSample("Toolkit light-source shadow depth");
            commands.SetRenderTarget(Atlas); commands.ClearRenderTarget(true, true, Color.white);
            int materialIndex = 0;
            for (int tile = 0; tile < _indices.Count; tile++)
            {
                commands.SetViewport(new Rect(tile % _grid * _tileSize, tile / _grid * _tileSize, _tileSize, _tileSize));
                var data = _data[_indices[tile]];
                foreach (var caster in _casters)
                {
                    var material = _materials[materialIndex++]; var renderer = caster.renderer;
                    if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy) continue;
                    material.SetMatrix("_ShadowViewProjection", data.worldToShadow); material.SetFloat("_ShadowFar", data.depth.y);
                    material.SetVector("_ShadowVertexScale", caster.vertexScale); material.SetFloat("_Cull", (int)caster.cull);
                    material.SetTexture("_ShadowAlphaMap", caster.alphaMap != null ? caster.alphaMap : Texture2D.whiteTexture);
                    material.SetVector("_ShadowUvST", caster.uvST); material.SetFloat("_ShadowAlpha", caster.alpha); material.SetFloat("_ShadowCutoff", caster.cutoff);
                    commands.DrawRenderer(renderer, material, caster.materialIndex, 0); CasterDrawCalls++;
                }
            }
            commands.EndSample("Toolkit light-source shadow depth");
            // The next SetRenderTarget restores a full viewport; no host camera matrices were changed.
        }
        public void Bind(Material material)
        {
            if (Atlas == null) { material.DisableKeyword("SCENE_LIGHT_SHADOWS"); return; }
            material.EnableKeyword("SCENE_LIGHT_SHADOWS"); material.SetTexture("_LightShadowAtlas", Atlas);
            if (_buffer != null) material.SetBuffer("_SceneLightShadows", _buffer);
        }
        public void BindSingle(MaterialPropertyBlock block, int index)
        {
            if (Atlas == null) return;
            var data = _data[index]; block.SetMatrix("_SingleShadowMatrix", data.worldToShadow);
            block.SetVector("_SingleShadowST", data.atlasST); block.SetVector("_SingleShadowDepth", data.depth); block.SetVector("_SingleShadowOptions", data.options);
        }
        private static bool Range(float x, float min, float max) => !float.IsNaN(x) && !float.IsInfinity(x) && x >= min && x <= max;
        private static string ValidateCaster(SceneShadowCaster caster)
        {
            if (caster == null || caster.renderer == null) return "Missing shadow caster renderer";
            var renderer = caster.renderer;
            var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>()?.sharedMesh : null;
            if (mesh == null || caster.materialIndex < 0 || caster.materialIndex >= mesh.subMeshCount ||
                caster.materialIndex >= renderer.sharedMaterials.Length || mesh.GetTopology(caster.materialIndex) != MeshTopology.Triangles ||
                !mesh.HasVertexAttribute(VertexAttribute.Position) || (caster.alphaMap != null && !mesh.HasVertexAttribute(VertexAttribute.TexCoord0)))
                return "Shadow caster requires a MeshRenderer/SkinnedMeshRenderer triangle submesh and matching attributes/material slot";
            if (renderer.HasPropertyBlock()) return "Shadow caster property blocks are not supported; supply explicit inputs";
            if ((int)caster.cull < 0 || (int)caster.cull > 2 || !Range(caster.alpha, 0, 1) || !Range(caster.cutoff, 0, 1)) return "Invalid shadow caster cull/alpha";
            for (int i = 0; i < 3; i++) if (!Range(caster.vertexScale[i], -1e6f, 1e6f) || Mathf.Abs(caster.vertexScale[i]) < 1e-6f) return "Invalid shadow caster vertex scale";
            for (int i = 0; i < 4; i++) if (!Range(caster.uvST[i], -1e6f, 1e6f)) return "Invalid shadow caster UV transform";
            var matrix = renderer.localToWorldMatrix;
            for (int i = 0; i < 16; i++) if (!Range(matrix[i], -1e12f, 1e12f)) return "Invalid shadow caster transform";
            if (!Range(Mathf.Abs(matrix.determinant), 1e-12f, 1e24f)) return "Singular shadow caster transform";
            if (caster.alphaMap != null && (caster.alphaMap.dimension != TextureDimension.Tex2D ||
                (caster.alphaMap is RenderTexture rt && (!rt.IsCreated() || rt.antiAliasing != 1)))) return "Shadow alpha map requires a created non-MSAA 2D texture";
            return null;
        }
        private void ReleaseAtlas() { if (Atlas != null) { Atlas.Release(); UnityEngine.Object.Destroy(Atlas); } Atlas = null; }
        public void Dispose()
        {
            ReleaseAtlas(); _buffer?.Dispose(); _buffer = null;
            foreach (var material in _materials) if (material != null) UnityEngine.Object.Destroy(material);
            _materials.Clear(); _data.Clear(); _indices.Clear(); _casters = null; CasterDrawCalls = 0;
        }
    }
}
