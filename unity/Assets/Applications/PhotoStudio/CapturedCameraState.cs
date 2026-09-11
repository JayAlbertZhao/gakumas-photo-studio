using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Explicit camera matrices for fixed-geometry diagnostics; no captured data is embedded.</summary>
    public static class CapturedCameraState
    {
        public const string Option = "--captured-camera-state";
        public const string Schema = "photo-studio.captured-camera.v1";
        [Serializable] public sealed class Document
        {
            public string schema;
            public int width, height;
            public float nearClip, farClip;
            // Row-major Unity camera matrices, before GL.GetGPUProjectionMatrix.
            public float[] worldToCamera;
            public float[] projection;
        }

        public sealed class Session
        {
            internal Document document;
            internal string sourceSha256;
            internal Matrix4x4 view, projection;
            internal Camera camera;
            internal Vector3 position;
            internal Quaternion rotation;
            internal float aspect;
            internal RenderTexture target;

            public void Apply(Camera value)
            {
                if (value == null) throw new ArgumentNullException("value");
                if (camera != null) throw new InvalidOperationException("Camera state is already applied.");
                if (value.targetTexture == null || value.targetTexture.width != document.width ||
                    value.targetTexture.height != document.height)
                    throw new InvalidOperationException("Captured camera requires its declared render-target dimensions.");
                camera = value;
                target = value.targetTexture;
                Matrix4x4 inverse = view.inverse;
                position = inverse.GetColumn(3);
                rotation = Quaternion.LookRotation(-inverse.GetColumn(2), inverse.GetColumn(1));
                camera.transform.SetPositionAndRotation(position, rotation);
                // Keep shader camera-position globals and the explicit view in agreement.
                position = camera.transform.position;
                rotation = camera.transform.rotation;
                camera.usePhysicalProperties = false;
                camera.orthographic = false;
                camera.nearClipPlane = document.nearClip;
                camera.farClipPlane = document.farClip;
                camera.aspect = (float)document.width / document.height;
                camera.worldToCameraMatrix = view;
                camera.projectionMatrix = projection;
                // Unity derives the getter from an explicit projection. It need
                // not equal the pixel aspect (off-axis/anamorphic inputs), even
                // when float rounding is the only difference. Preserve it exactly.
                aspect = camera.aspect;
            }

            public bool Verify(out string error)
            {
                error = null;
                if (camera == null || !camera.gameObject.activeInHierarchy || camera.targetTexture != target ||
                    target == null || target.width != document.width || target.height != document.height)
                { error = "Captured camera or its render target changed before capture."; return false; }
                string changed = !camera.transform.position.Equals(position) ? "position" :
                    !camera.transform.rotation.Equals(rotation) ? "rotation" :
                    !Exact(camera.worldToCameraMatrix, view) ? "view matrix" :
                    !Exact(camera.projectionMatrix, projection) ? "projection matrix" :
                    camera.usePhysicalProperties ? "physical properties" : camera.orthographic ? "projection mode" :
                    camera.nearClipPlane != document.nearClip ? "near clip" :
                    camera.farClipPlane != document.farClip ? "far clip" :
                    camera.aspect != aspect ? "aspect" : null;
                if (changed != null)
                { error = "Captured camera " + changed + " changed before capture."; return false; }
                return true;
            }

            public string VerifiedJson()
            {
                string error;
                if (!Verify(out error)) throw new InvalidOperationException(error);
                Matrix4x4 gpu = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                return "{\"schema\":\"photo-studio.applied-camera.v1\",\"verified\":true," +
                    "\"sourceSha256\":\"" + sourceSha256 + "\",\"state\":" + JsonUtility.ToJson(document) +
                    ",\"gpu\":" + JsonUtility.ToJson(new GpuState { projection = Rows(gpu),
                        viewProjection = Rows(gpu * camera.worldToCameraMatrix) }) + "}";
            }
        }

        [Serializable] private sealed class GpuState { public float[] projection, viewProjection; }

        private static bool Exact(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (a[i] != b[i]) return false;
            return true;
        }

        internal static float[] Rows(Matrix4x4 matrix)
        {
            var values = new float[16];
            for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) values[row*4+col] = matrix[row, col];
            return values;
        }

        private static Matrix4x4 Matrix(float[] values)
        {
            if (values == null || values.Length != 16) throw new InvalidDataException("Camera matrices require 16 row-major values.");
            var matrix = new Matrix4x4();
            for (int i = 0; i < 16; i++)
            {
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i])) throw new InvalidDataException("Non-finite camera matrix.");
                matrix[i/4, i%4] = values[i];
            }
            float determinant = matrix.determinant;
            if (float.IsNaN(determinant) || float.IsInfinity(determinant) || Mathf.Abs(determinant) < 0.0000001f)
                throw new InvalidDataException("Singular or numerically invalid camera matrix.");
            return matrix;
        }

        public static bool TryParse(string json, out Session session, out string error)
        {
            session = null; error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) throw new InvalidDataException("Invalid camera document size.");
                Document d = JsonUtility.FromJson<Document>(json);
                if (d == null || d.schema != Schema || d.width < 1 || d.width > 16384 || d.height < 1 || d.height > 16384 ||
                    float.IsNaN(d.nearClip) || float.IsInfinity(d.nearClip) || float.IsNaN(d.farClip) || float.IsInfinity(d.farClip) ||
                    d.nearClip <= 0f || d.farClip <= d.nearClip)
                    throw new InvalidDataException("Invalid camera schema, dimensions or clip planes.");
                Matrix4x4 view = Matrix(d.worldToCamera), projection = Matrix(d.projection);
                if (view[3,0] != 0f || view[3,1] != 0f || view[3,2] != 0f || view[3,3] != 1f)
                    throw new InvalidDataException("Camera view must be affine.");
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
                {
                    Vector3 a = view.GetRow(i), b = view.GetRow(j);
                    if (Mathf.Abs(Vector3.Dot(a, b) - (i == j ? 1f : 0f)) > 0.0001f)
                        throw new InvalidDataException("Camera view must have a rigid basis.");
                }
                if (view.determinant >= 0f) throw new InvalidDataException("Camera view must use Unity's negative-Z forward convention.");
                Vector3 origin = view.inverse.GetColumn(3);
                if (float.IsNaN(origin.x) || float.IsNaN(origin.y) || float.IsNaN(origin.z) ||
                    Mathf.Abs(origin.x) > 1000000f || Mathf.Abs(origin.y) > 1000000f || Mathf.Abs(origin.z) > 1000000f)
                    throw new InvalidDataException("Captured camera origin is outside the supported range.");
                if (projection[0,0] <= 0f || projection[1,1] <= 0f ||
                    projection[3,0] != 0f || projection[3,1] != 0f || projection[3,2] != -1f || projection[3,3] != 0f)
                    throw new InvalidDataException("Expected a Unity CPU perspective projection, not a GPU-adjusted matrix.");
                session = new Session { document = d, view = view, projection = projection };
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        public static bool TryLoad(string path, out Session session, out string error)
        {
            session = null;
            try
            {
                if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Captured camera JSON exceeds 8 KiB.");
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length > 8192) throw new InvalidDataException("Captured camera JSON exceeds 8 KiB.");
                if (!TryParse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), out session, out error)) return false;
                using (SHA256 hash = SHA256.Create())
                    session.sourceSha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
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
                if (found >= 0) { error = "Duplicate captured camera option."; return false; }
                found = i;
            }
            if (found < 0) return true;
            if (found+1 >= args.Length || string.IsNullOrWhiteSpace(args[found+1]) || args[found+1].StartsWith("--"))
            { error = "A captured camera JSON path is required."; return false; }
            if (Array.IndexOf(args, "--capture-gpa-camera-and-quit") < 0 ||
                Array.IndexOf(args, "--use-captured-posed-geometry") < 0 || Array.IndexOf(args, "--capture-presented-window") < 0)
            { error = "Captured camera requires fixed GPA capture, captured geometry and presented-window capture."; return false; }
            foreach (string arg in args)
                if (arg == "--validate-actor-rendering" || arg == "--self-test-actor-rendering" ||
                    (arg.StartsWith("--capture-", StringComparison.Ordinal) && arg != "--capture-gpa-camera-and-quit" &&
                     arg != "--capture-presented-window" && arg != "--capture-actor-rendering-pass"))
                { error = "Conflicting capture mode with captured camera."; return false; }
            path = args[found+1]; return true;
        }
    }
}
