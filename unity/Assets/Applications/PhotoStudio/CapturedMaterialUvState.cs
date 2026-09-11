using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Explicit private UV inputs for fixed-geometry diagnostics only.</summary>
    public static class CapturedMaterialUvState
    {
        public const string Option = "--captured-material-uv";
        public const string Schema = "photo-studio.captured-material-uv.v1";
        [Serializable] public sealed class Entry
        {
            public string renderer;
            public string material;
            public float[] baseMapST;
        }
        [Serializable] public sealed class Document
        {
            public string schema;
            public Entry[] materials;
        }
        internal sealed class Binding
        {
            public Renderer renderer;
            public Material material;
            public int index;
            public Vector4 value;
            public MaterialPropertyBlock block;
        }
        public sealed class Session
        {
            internal Document document;
            internal string sourceSha256;
            internal readonly List<Binding> bindings = new List<Binding>();

            public bool Verify(out string error)
            {
                error = null;
                var block = new MaterialPropertyBlock();
                foreach (Binding binding in bindings)
                {
                    if (binding.renderer == null || !binding.renderer.enabled ||
                        !binding.renderer.gameObject.activeInHierarchy)
                    { error = "Captured UV renderer is no longer active."; return false; }
                    Material[] materials = binding.renderer.sharedMaterials;
                    if (binding.index >= materials.Length || materials[binding.index] != binding.material)
                    { error = "Captured UV material binding changed before capture."; return false; }
                    block.Clear();
                    binding.renderer.GetPropertyBlock(block, binding.index);
                    if (!block.GetVector("_BaseMap_ST").Equals(binding.value))
                    { error = "Captured UV property changed before capture."; return false; }
                }
                return true;
            }

            public string VerifiedJson()
            {
                string error;
                if (!Verify(out error)) throw new InvalidOperationException(error);
                return "{\"schema\":\"photo-studio.applied-material-uv.v1\",\"verified\":true," +
                    "\"sourceSha256\":\"" + sourceSha256 + "\",\"state\":" +
                    JsonUtility.ToJson(document) + "}";
            }
        }

        public static bool TryReadOption(string[] args, out string path, out string error)
        {
            path = error = null;
            int found = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(Option + "=", StringComparison.Ordinal))
                { error = "Use a separate path after " + Option; return false; }
                if (args[i] != Option) continue;
                if (found >= 0) { error = "Duplicate captured material UV option."; return false; }
                found = i;
            }
            if (found < 0) return true;
            if (found + 1 >= args.Length || string.IsNullOrWhiteSpace(args[found + 1]) || args[found + 1].StartsWith("--"))
            { error = "A captured material UV JSON path is required."; return false; }
            if (Array.IndexOf(args, "--capture-gpa-camera-and-quit") < 0 ||
                Array.IndexOf(args, "--use-captured-posed-geometry") < 0)
            { error = "Captured material UV requires the fixed GPA capture and captured posed geometry."; return false; }
            foreach (string arg in args)
                if (arg == "--validate-actor-rendering" || arg == "--self-test-actor-rendering" ||
                    (arg.StartsWith("--capture-", StringComparison.Ordinal) &&
                     arg != "--capture-gpa-camera-and-quit" && arg != "--capture-presented-window" &&
                     arg != "--capture-actor-rendering-pass"))
                { error = "Conflicting capture mode with captured material UV."; return false; }
            path = args[found + 1];
            return true;
        }

        public static bool TryLoad(GameObject actor, string path, ISet<Renderer> capturedRenderers, out Session session, out string error)
        {
            session = null;
            try
            {
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Captured UV JSON exceeds 64 KiB.");
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length > 65536) throw new InvalidDataException("Captured UV JSON exceeds 64 KiB.");
                string json = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                if (capturedRenderers == null) throw new InvalidDataException("Captured renderer set is required.");
                if (!TryApply(actor, json, out session, out error, capturedRenderers)) return false;
                using (SHA256 hash = SHA256.Create())
                    session.sourceSha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        public static bool TryApply(GameObject actor, string json, out Session session, out string error,
            ISet<Renderer> capturedRenderers = null)
        {
            session = null;
            error = null;
            try
            {
                if (actor == null || string.IsNullOrWhiteSpace(json) || json.Length > 65536)
                    throw new InvalidDataException("Missing actor or invalid captured UV document size.");
                Document document = JsonUtility.FromJson<Document>(json);
                if (document == null || document.schema != Schema || document.materials == null ||
                    document.materials.Length == 0 || document.materials.Length > 32)
                    throw new InvalidDataException("Invalid captured UV schema or material count.");
                var result = new Session { document = document };
                Renderer[] renderers = actor.GetComponentsInChildren<Renderer>(true);
                var keys = new HashSet<string>();
                foreach (Entry entry in document.materials)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.renderer) || string.IsNullOrWhiteSpace(entry.material) ||
                        entry.baseMapST == null || entry.baseMapST.Length != 4)
                        throw new InvalidDataException("Each captured UV entry requires renderer, material and four values.");
                    foreach (float value in entry.baseMapST)
                        if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Non-finite captured UV value.");
                    Renderer selected = null;
                    foreach (Renderer candidate in renderers)
                        if (candidate.enabled && candidate.gameObject.activeInHierarchy && candidate.name == entry.renderer)
                        {
                            if (selected != null) throw new InvalidDataException("Ambiguous captured UV renderer: " + entry.renderer);
                            selected = candidate;
                        }
                    if (selected == null) throw new InvalidDataException("Captured UV renderer not found: " + entry.renderer);
                    if (capturedRenderers != null && !capturedRenderers.Contains(selected))
                        throw new InvalidDataException("Captured geometry missing for UV renderer: " + entry.renderer);
                    int slot = -1;
                    Material[] materials = selected.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++)
                        if (materials[i] != null && materials[i].name == entry.material)
                        {
                            if (slot >= 0) throw new InvalidDataException("Ambiguous captured UV material: " + entry.material);
                            slot = i;
                        }
                    if (slot < 0) throw new InvalidDataException("Captured UV material not found: " + entry.material);
                    if (!keys.Add(selected.GetInstanceID() + ":" + slot)) throw new InvalidDataException("Duplicate captured UV binding.");
                    var block = new MaterialPropertyBlock();
                    selected.GetPropertyBlock(block, slot);
                    if (block.isEmpty) selected.GetPropertyBlock(block);
                    var uv = new Vector4(entry.baseMapST[0], entry.baseMapST[1], entry.baseMapST[2], entry.baseMapST[3]);
                    block.SetVector("_BaseMap_ST", uv);
                    result.bindings.Add(new Binding { renderer = selected, material = materials[slot],
                        index = slot, value = uv, block = block });
                }
                // Validate the entire document before mutating any property block.
                foreach (Binding binding in result.bindings)
                    binding.renderer.SetPropertyBlock(binding.block, binding.index);
                session = result;
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }
    }
}
