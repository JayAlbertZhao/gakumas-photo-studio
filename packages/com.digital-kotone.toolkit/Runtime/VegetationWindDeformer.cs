using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Owned current wind mesh for native color/depth/shadow/motion draws.
    /// Caller must stop drawing on update failure and before disposal.</summary>
    public sealed class VegetationWindDeformer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Rest { public Vector4 position, normal, tangent, coefficients; }
        private Mesh mesh;
        private ComputeShader shader;
        private GraphicsBuffer restBuffer;
        private readonly GraphicsBuffer[] dummy = new GraphicsBuffer[4], outputs = new GraphicsBuffer[4];
        private Rest[] rest;
        private Vector3[] positions, normals;
        private Vector4[] tangents;
        private Bounds restBounds;
        private int streamCount, kernel;
        private readonly int[] strides = new int[4], streams = { -1, -1, -1 }, offsets = { -1, -1, -1 };
        private static readonly VertexAttribute[] Channels = { VertexAttribute.Position, VertexAttribute.Normal, VertexAttribute.Tangent };
        private bool disposed;

        public Mesh Mesh => LastUpdateSucceeded && mesh != null ? mesh : null;
        public VegetationWindBackend Backend { get; private set; }
        public bool LastUpdateSucceeded { get; private set; }
        public string UnavailableReason { get; private set; }
        public string FallbackReason { get; private set; }
        public int DispatchCount { get; private set; }
        public int VertexCount => rest?.Length ?? 0;
        // Proposed owned GPU buffers, including cloned vertex/index storage;
        // excludes CPU snapshots, caller assets and driver overhead.
        public long GpuResourceBytes { get; private set; }

        private VegetationWindDeformer() { }

        /// <param name="coefficients">One explicit(branch weight, flutter phase cycles,
        /// flutter weight) value per vertex, all in[0,1]. Keep coefficients constant
        /// across a leaf for the analytic per-leaf normal field.</param>
        public static bool TryCreate(Mesh source, Vector3[] coefficients, VegetationWindBackend backend,
            bool allowCpuFallback, int maximumResourceMiB, out VegetationWindDeformer result, out string reason)
        {
            result = null; reason = null; var value = new VegetationWindDeformer();
            try { value.Initialize(source, coefficients, backend, allowCpuFallback, maximumResourceMiB); result = value; return true; }
            catch (Exception error) { reason = error.Message; value.Dispose(); return false; }
        }

        private void Initialize(Mesh source, Vector3[] coefficients, VegetationWindBackend requested, bool allowFallback, int budget)
        {
            Require(source != null && source.isReadable && source.vertexCount > 0 && source.vertexCount <= 262144,
                "Vegetation requires a readable static mesh with1..262144 vertices");
            Require(source.bindposes.Length == 0 && source.blendShapeCount == 0, "Vegetation wind source must be static; do not silently discard a skin/shape rig");
            Require(coefficients != null && coefficients.Length == source.vertexCount && (int)requested >= 0 && (int)requested <= 2 && budget >= 1 && budget <= 2048,
                "Invalid vegetation coefficient count/backend/budget");
            for (int i = 0; i < source.subMeshCount; i++) Require(source.GetTopology(i) == MeshTopology.Triangles, "Vegetation requires triangle submeshes");
            Require(source.subMeshCount > 0, "Vegetation requires indexed geometry");
            var asset = Resources.Load<ComputeShader>("VegetationWind");
            bool gpu = SystemInfo.supportsComputeShaders && asset != null && asset.IsSupported(asset.FindKernel("DeformVegetation"));
            Backend = requested == VegetationWindBackend.Cpu ? VegetationWindBackend.Cpu : VegetationWindBackend.Gpu;
            if (Backend == VegetationWindBackend.Gpu && !gpu)
            {
                Require(allowFallback, "Vegetation compute unavailable and CPU fallback disabled");
                Backend = VegetationWindBackend.Cpu; FallbackReason = "Vegetation compute unavailable; explicit CPU fallback";
            }
            streamCount = source.vertexBufferCount; Require(streamCount >= 1 && streamCount <= 4, "Vegetation expects1..4 vertex streams");
            long bytes;
            // Dispose only the acquired wrapper, never the caller's index storage.
            using (var indices = source.GetIndexBuffer())
            { Require(indices != null && indices.IsValid(), "Vegetation source index buffer unavailable"); bytes = (long)indices.count * indices.stride; }
            for (int s = 0; s < streamCount; s++) { strides[s] = source.GetVertexBufferStride(s); bytes += (long)strides[s] * source.vertexCount; }
            for (int c = 0; c < Channels.Length; c++)
            {
                var attr = Channels[c];
                if (!source.HasVertexAttribute(attr)) { Require(c == 2, "Vegetation position and normal are required"); continue; }
                Require(source.GetVertexAttributeFormat(attr) == VertexAttributeFormat.Float32 && source.GetVertexAttributeDimension(attr) == (c == 2 ? 4 : 3),
                    "Vegetation position/normal/tangent require Float32 dimensions3/3/4");
                streams[c] = source.GetVertexAttributeStream(attr); offsets[c] = source.GetVertexAttributeOffset(attr);
                Require((offsets[c] & 3) == 0 && (strides[streams[c]] & 3) == 0, "Vegetation channels require word alignment");
            }
            if (Backend == VegetationWindBackend.Gpu) bytes += (long)source.vertexCount * 64 + 64;
            Require(bytes <= (long)budget * 1048576, "Vegetation owned GPU resource budget exceeded before allocation");
            var sourcePositions = source.vertices; var sourceNormals = source.normals; var sourceTangents = source.tangents;
            rest = new Rest[source.vertexCount]; restBounds = new Bounds(sourcePositions[0], Vector3.zero);
            for (int i = 0; i < rest.Length; i++)
            {
                var p = sourcePositions[i]; var n = sourceNormals[i]; var t = streams[2] >= 0 ? sourceTangents[i] : new Vector4(1,0,0,1); var c = coefficients[i];
                Require(Vector(p, 10000) && Vector(n, 10000) && n.sqrMagnitude > 1e-12f && Vector((Vector3)t, 10000) &&
                    (streams[2] < 0 || (t.w == 1 || t.w == -1) && Vector3.Cross(n, t).sqrMagnitude > 1e-12f), "Invalid vegetation rest position/normal/tangent");
                Require(Range(c.x, 0, 1) && Range(c.y, 0, 1) && Range(c.z, 0, 1), "Vegetation coefficients must be finite in[0,1]");
                rest[i] = new Rest { position = new Vector4(p.x,p.y,p.z,1), normal = n, tangent = t,
                    coefficients = new Vector4(c.x, c.z, (float)Math.Sin(c.y * Math.PI * 2), (float)Math.Cos(c.y * Math.PI * 2)) };
                restBounds.Encapsulate(p);
            }
            mesh = UnityEngine.Object.Instantiate(source); mesh.name = source.name + "__vegetation_wind"; mesh.hideFlags = HideFlags.HideAndDontSave;
            if (Backend == VegetationWindBackend.Gpu)
            {
                mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                shader = UnityEngine.Object.Instantiate(asset); kernel = shader.FindKernel("DeformVegetation");
                restBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rest.Length, 64); restBuffer.name = "Toolkit vegetation immutable rest and leaf coefficients"; restBuffer.SetData(rest);
                for (int s = 0; s < 4; s++) dummy[s] = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 4, 4);
            }
            else
            {
                positions = new Vector3[rest.Length]; normals = new Vector3[rest.Length];
                if (streams[2] >= 0) tangents = new Vector4[rest.Length];
                mesh.MarkDynamic();
            }
            GpuResourceBytes = bytes;
        }

        public bool TryUpdate(VegetationWindSettings settings, double seconds, Matrix4x4 localToWorld)
        {
            LastUpdateSucceeded = false; UnavailableReason = null;
            try
            {
                Require(!disposed && mesh != null && mesh.vertexCount == rest.Length, "Vegetation owner disposed, output destroyed or vertex count changed");
                Require(settings != null, "Vegetation settings missing");
                Vector3 root = Vector3.zero, axis = Vector3.up, wind = Vector3.zero, flutter = Vector3.zero; float inverseHeight = 1, sine = 0, cosine = 1;
                if (settings.enabled)
                {
                    Require(Range(seconds, -1e9, 1e9) && Vector(settings.rootLocal,10000) && Vector(settings.upLocal,10000) && settings.upLocal.sqrMagnitude > 1e-12f &&
                        Range(settings.height,.001,10000) && Range(settings.phaseCycles,-1e6,1e6) && Range(settings.flutterFrequencyHz,0,100) &&
                        Vector(settings.displacementWorld,1000) && Vector(settings.flutterWorld,1000) && Range(settings.naturalWindScale,0,100), "Invalid vegetation field/clock");
                    for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) Require(Range(localToWorld[r,c],-1e6,1e6), "Vegetation transform must be finite");
                    Require(localToWorld.m30 == 0 && localToWorld.m31 == 0 && localToWorld.m32 == 0 && localToWorld.m33 == 1 &&
                        Math.Abs(localToWorld.determinant) > 1e-8f, "Vegetation requires nonsingular affine local-to-world");
                    var natural = settings.naturalWind;
                    Require(natural == null || !natural.enabled || natural.IsValid && Vector(natural.steadyForce,10000) &&
                        Vector(natural.sineAmplitude,10000) && Vector(natural.randomAmplitude,10000) && Range(natural.sineFrequency,0,100) &&
                        Range(natural.randomFrequency,0,100) && Range(natural.gustSeconds,0,1e9) && Range(natural.calmSeconds,0,1e9), "Invalid enabled natural wind");
                    var world = settings.displacementWorld + (natural != null ? natural.Sample(seconds) * settings.naturalWindScale : Vector3.zero);
                    var inverse = localToWorld.inverse; wind = inverse.MultiplyVector(world); flutter = inverse.MultiplyVector(settings.flutterWorld);
                    Require(Vector(wind,10000) && Vector(flutter,10000), "Vegetation local displacement exceeds domain");
                    root = settings.rootLocal; axis = Normalize(settings.upLocal); inverseHeight = 1 / settings.height;
                    // Smoothstep's maximum derivative is1.5/height. This bound
                    // keeps every per-leaf rank-one Jacobian determinant >=0.2.
                    Require(1.5f * inverseHeight * (Mathf.Abs(Vector3.Dot(axis,wind)) + Mathf.Abs(Vector3.Dot(axis,flutter))) <= .8f,
                        "Vegetation field would fold or approach a singular normal transform");
                    double phase = seconds * settings.flutterFrequencyHz + settings.phaseCycles; phase -= Math.Floor(phase);
                    sine = (float)Math.Sin(phase * Math.PI * 2); cosine = (float)Math.Cos(phase * Math.PI * 2);
                }
                // Unity vector equality has a distance tolerance. A small but
                // nonzero authored displacement must still deform the mesh.
                bool atRest = wind.Equals(Vector3.zero) && flutter.Equals(Vector3.zero);
                var bounds = restBounds; bounds.extents += Abs(wind) + Abs(flutter);
                bounds.Expand(2 * (Mathf.Max(bounds.center.magnitude,bounds.extents.magnitude) * .00001f + .0001f));
                mesh.bounds = bounds;
                if (Backend == VegetationWindBackend.Cpu)
                {
                    for (int i = 0; i < rest.Length; i++)
                    {
                        var v = rest[i]; Vector3 p = v.position, n = v.normal; Vector4 t = v.tangent;
                        if (!atRest)
                        {
                            float h = Mathf.Clamp01(Vector3.Dot(p-root,axis)*inverseHeight), profile = h*h*(3-2*h);
                            float wave = sine*v.coefficients.w+cosine*v.coefficients.z;
                            Vector3 d = wind*v.coefficients.x+flutter*(v.coefficients.y*wave), g = axis*(6*h*(1-h)*inverseHeight);
                            p += d*profile; n = Normalize(n-g*(Vector3.Dot(d,n)/(1+Vector3.Dot(d,g))));
                            if (tangents != null) { Vector3 transformed = (Vector3)t+d*Vector3.Dot(g,t); transformed = Normalize(transformed-n*Vector3.Dot(n,transformed)); t = new Vector4(transformed.x,transformed.y,transformed.z,t.w); }
                        }
                        positions[i] = p; normals[i] = n; if (tangents != null) tangents[i] = t;
                    }
                    mesh.SetVertices(positions); mesh.SetNormals(normals); if (tangents != null) mesh.SetTangents(tangents); mesh.bounds = bounds;
                }
                else
                {
                    Require(shader != null && restBuffer != null && restBuffer.IsValid() && mesh.vertexBufferCount == streamCount, "Vegetation GPU resource/layout lost");
                    for (int c = 0; c < Channels.Length; c++)
                    {
                        bool present = mesh.HasVertexAttribute(Channels[c]);
                        Require(present == (streams[c] >= 0) && (!present || mesh.GetVertexAttributeStream(Channels[c]) == streams[c] &&
                            mesh.GetVertexAttributeOffset(Channels[c]) == offsets[c] && mesh.GetVertexAttributeFormat(Channels[c]) == VertexAttributeFormat.Float32 &&
                            mesh.GetVertexAttributeDimension(Channels[c]) == (c == 2 ? 4 : 3)), "Vegetation owned vertex channels changed");
                    }
                    shader.SetBuffer(kernel,"_VegetationRest",restBuffer); shader.SetInt("_VegetationCount",rest.Length); shader.SetInt("_VegetationAtRest",atRest ? 1 : 0);
                    shader.SetVector("_VegetationRootHeight",new Vector4(root.x,root.y,root.z,inverseHeight)); shader.SetVector("_VegetationAxis",axis);
                    shader.SetVector("_VegetationWind",wind); shader.SetVector("_VegetationFlutter",flutter); shader.SetVector("_VegetationClock",new Vector4(sine,cosine,0,0));
                    shader.SetInts("_VegetationStrides",strides); shader.SetInts("_VegetationChannels",streams[0],streams[1],streams[2],0); shader.SetInts("_VegetationOffsets",offsets[0],offsets[1],offsets[2],0);
                    for (int s = 0; s < 4; s++)
                    {
                        if (s < streamCount)
                        {
                            Require(mesh.GetVertexBufferStride(s) == strides[s], "Vegetation owned stride changed"); outputs[s] = mesh.GetVertexBuffer(s);
                            Require(outputs[s] != null && outputs[s].IsValid() && (outputs[s].target & GraphicsBuffer.Target.Raw) != 0, "Vegetation output lacks writable Raw stream");
                        }
                        else Require(dummy[s] != null && dummy[s].IsValid(), "Vegetation padding buffer lost");
                        shader.SetBuffer(kernel,"_VegetationOutput"+s,s < streamCount ? outputs[s] : dummy[s]);
                    }
                    shader.Dispatch(kernel,(rest.Length+63)/64,1,1); DispatchCount++;
                }
                LastUpdateSucceeded = true; return true;
            }
            catch (Exception error) { UnavailableReason = error.Message; return false; }
            finally { for (int s = 0; s < 4; s++) { outputs[s]?.Dispose(); outputs[s] = null; } }
        }
        private static bool Range(double v,double low,double high) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= low && v <= high;
        private static bool Vector(Vector3 v,float max) => Range(v.x,-max,max) && Range(v.y,-max,max) && Range(v.z,-max,max);
        // Inputs are validated nondegenerate; preserve small valid directions
        // instead of Unity Vector3.normalized's small-vector zero fallback.
        private static Vector3 Normalize(Vector3 v) => v / v.magnitude;
        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x),Mathf.Abs(v.y),Mathf.Abs(v.z));
        private static void Require(bool condition,string error) { if (!condition) throw new ArgumentException(error); }
        public void Dispose()
        {
            disposed = true; LastUpdateSucceeded = false; restBuffer?.Dispose(); restBuffer = null;
            for (int s = 0; s < 4; s++) { outputs[s]?.Dispose(); outputs[s] = null; dummy[s]?.Dispose(); dummy[s] = null; }
            if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh); if (shader != null) UnityEngine.Object.DestroyImmediate(shader);
            mesh = null; shader = null; rest = null; positions = normals = null; tangents = null; GpuResourceBytes = 0;
        }
    }
}
