using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Private per-camera stable visibility/order/LOD and actual indirect instance lists.</summary>
    internal sealed class CrowdSelection : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] internal struct InstanceData
        { public Vector4 positionScale, rotationType, tint; }
        [StructLayout(LayoutKind.Sequential)] internal struct Key
        { public uint distance, index; }
        private sealed class KeyComparer : System.Collections.Generic.IComparer<Key>
        {
            public int Compare(Key a, Key b)
            { int order = a.distance.CompareTo(b.distance); return order != 0 ? order : a.index.CompareTo(b.index); }
        }
        private readonly KeyComparer comparer = new KeyComparer();
        internal ComputeBuffer Instances { get; private set; }
        internal ComputeBuffer Bounds { get; private set; }
        internal ComputeBuffer Order { get; private set; }
        internal ComputeBuffer Indices { get; private set; }
        internal ComputeBuffer Arguments { get; private set; }
        private ComputeBuffer scan, totals, offsets;
        private ComputeShader compute;
        private InstanceData[] instances;
        private Key[] order;
        private uint[] indices, arguments;
        private readonly Vector4[] planes = new Vector4[6];
        private Vector4[] bounds;
        private int count, types, blocks, budget;
        private Vector3 camera;
        public int Capacity { get; private set; }
        public int Dispatches { get; private set; }
        public long BufferBytes { get; private set; }
        public int BufferCount => Instances == null ? 0 : 8;
        public bool IsCreated => Instances != null && Instances.IsValid() && Bounds != null && Bounds.IsValid() &&
            Order != null && Order.IsValid() && Indices != null && Indices.IsValid() && Arguments != null && Arguments.IsValid() &&
            scan != null && scan.IsValid() && totals != null && totals.IsValid() && offsets != null && offsets.IsValid();

        public static long Bytes(int count, int types)
        {
            int capacity = Mathf.NextPowerOfTwo(Mathf.Max(1, count)), blocks = (capacity + 255) / 256, buckets = types * 2;
            return (long)capacity * (48 + 8 + buckets * 8) + (long)blocks * buckets * 8 + types * 16 + buckets * 20;
        }
        public void Prepare(Camera view, CrowdInstance[] input, Vector4[] prototypeBounds, uint[] drawArguments, int meshBudget)
        {
            int capacity = Mathf.NextPowerOfTwo(Mathf.Max(1, input.Length)), prototypeCount = prototypeBounds.Length;
            if (!IsCreated || Capacity != capacity || types != prototypeCount)
            {
                Dispose(); Capacity = capacity; types = prototypeCount; blocks = (Capacity + 255) / 256;
                Instances = New(Capacity, 48, "instances"); Bounds = New(types, 16, "prototype spheres"); Order = New(Capacity, 8, "stable distance order");
                Indices = New(Capacity * types * 2, 4, "compact draw lists"); scan = New(Capacity * types * 2, 4, "local rank scan");
                totals = New(blocks * types * 2, 4, "block totals"); offsets = New(blocks * types * 2, 4, "block offsets");
                Arguments = New(types * 10, 4, "indirect draw arguments", ComputeBufferType.IndirectArguments);
                instances = new InstanceData[Capacity]; order = new Key[Capacity]; indices = new uint[Capacity * types * 2]; arguments = new uint[types * 10];
                BufferBytes = Bytes(input.Length, types);
            }
            count = input.Length; budget = Mathf.Min(meshBudget, count); bounds = (Vector4[])prototypeBounds.Clone();
            camera = view.worldToCameraMatrix.inverse.MultiplyPoint(Vector3.zero);
            var frustum = GeometryUtility.CalculateFrustumPlanes(view.projectionMatrix * view.worldToCameraMatrix);
            for (int p = 0; p < 6; p++)
            {
                var n = frustum[p].normal; float inverse = 1 / n.magnitude;
                planes[p] = new Vector4(n.x * inverse, n.y * inverse, n.z * inverse, frustum[p].distance * inverse);
            }
            for (int i = 0; i < count; i++)
            {
                var source = input[i]; float yaw = source.yawDegrees * Mathf.Deg2Rad;
                instances[i] = new InstanceData {
                    positionScale = new Vector4(source.position.x, source.position.y, source.position.z, source.scale),
                    rotationType = new Vector4(Mathf.Sin(yaw), Mathf.Cos(yaw), source.prototype, source.hidden ? 1 : 0), tint = source.tint };
            }
            Array.Copy(drawArguments, arguments, arguments.Length);
            for (int bucket = 0; bucket < types * 2; bucket++) arguments[bucket * 5 + 1] = 0;
            Instances.SetData(instances); Bounds.SetData(bounds); Arguments.SetData(arguments); Dispatches = 0;
        }
        public void Record(CommandBuffer commands, bool gpu)
        {
            if (!gpu) { SelectCpu(); return; }
            if (compute == null) compute = Resources.Load<ComputeShader>("CrowdSelection");
            if (compute == null) throw new InvalidOperationException("Crowd selection compute unavailable");
            commands.BeginSample("Toolkit crowd GPU nearest budget / stable compact lists");
            commands.SetComputeIntParam(compute, "_CrowdCount", count); commands.SetComputeIntParam(compute, "_CrowdCapacity", Capacity);
            commands.SetComputeIntParam(compute, "_CrowdTypes", types); commands.SetComputeIntParam(compute, "_CrowdBlocks", blocks);
            commands.SetComputeIntParam(compute, "_CrowdMeshBudget", budget); commands.SetComputeVectorParam(compute, "_CrowdCamera", camera);
            commands.SetComputeVectorArrayParam(compute, "_CrowdPlanes", planes);
            int classify = Kernel(commands, "Classify"); Dispatch(commands, classify, blocks, 1);
            int sort = Kernel(commands, "Bitonic");
            for (int k = 2; k <= Capacity; k <<= 1) for (int j = k >> 1; j > 0; j >>= 1)
            {
                commands.SetComputeIntParam(compute, "_CrowdSortK", k); commands.SetComputeIntParam(compute, "_CrowdSortJ", j); Dispatch(commands, sort, blocks, 1);
            }
            Dispatch(commands, Kernel(commands, "ScanBlocks"), blocks, types * 2);
            Dispatch(commands, Kernel(commands, "ScanTotals"), types * 2, 1);
            Dispatch(commands, Kernel(commands, "Scatter"), blocks, types * 2);
            commands.EndSample("Toolkit crowd GPU nearest budget / stable compact lists");
        }
        private int Kernel(CommandBuffer commands, string name)
        {
            int kernel = compute.FindKernel(name);
            commands.SetComputeBufferParam(compute, kernel, "_CrowdInstances", Instances); commands.SetComputeBufferParam(compute, kernel, "_CrowdBounds", Bounds);
            commands.SetComputeBufferParam(compute, kernel, "_CrowdOrder", Order); commands.SetComputeBufferParam(compute, kernel, "_CrowdIndices", Indices);
            commands.SetComputeBufferParam(compute, kernel, "_CrowdScan", scan); commands.SetComputeBufferParam(compute, kernel, "_CrowdTotals", totals);
            commands.SetComputeBufferParam(compute, kernel, "_CrowdOffsets", offsets); commands.SetComputeBufferParam(compute, kernel, "_CrowdArguments", Arguments); return kernel;
        }
        private void Dispatch(CommandBuffer commands, int kernel, int x, int y)
        { commands.DispatchCompute(compute, kernel, x, y, 1); Dispatches++; }
        // Mono may retain double intermediates for float expressions. Exact GPU ordering
        // uses separate float32 operations, including ties, not wider CPU distance keys.
        private static float Single(float value) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value));
        private static float Add(float a, float b) => Single(a + b);
        private static float Multiply(float a, float b) => Single(a * b);
        private static Vector3 Rotate(Vector3 p, Vector4 yaw) => new Vector3(Add(Multiply(yaw.y, p.x), Multiply(yaw.x, p.z)), p.y, Add(Multiply(-yaw.x, p.x), Multiply(yaw.y, p.z)));
        private void SelectCpu()
        {
            for (int i = 0; i < Capacity; i++)
            {
                order[i] = new Key { distance = 0x7f800000u, index = (uint)i }; if (i >= count) continue;
                var item = instances[i]; var sphere = bounds[(int)item.rotationType.z];
                var rotated = Rotate((Vector3)sphere * item.positionScale.w, item.rotationType);
                var center = new Vector3(Add(item.positionScale.x, rotated.x), Add(item.positionScale.y, rotated.y), Add(item.positionScale.z, rotated.z));
                float radius = Multiply(sphere.w, item.positionScale.w); bool visible = item.rotationType.w < .5f;
                foreach (var p in planes) visible &= Add(Add(Add(Multiply(p.x, center.x), Multiply(p.y, center.y)), Multiply(p.z, center.z)), p.w) >= -radius;
                if (visible)
                {
                    var d = center - camera; float square = Add(Add(Multiply(d.x, d.x), Multiply(d.y, d.y)), Multiply(d.z, d.z));
                    order[i].distance = (uint)BitConverter.SingleToInt32Bits(square);
                }
            }
            Array.Sort(order, comparer);
            for (int bucket = 0; bucket < types * 2; bucket++) arguments[bucket * 5 + 1] = 0;
            for (int rank = 0; rank < count && order[rank].distance != 0x7f800000u; rank++)
            {
                uint id = order[rank].index; int bucket = (int)instances[id].rotationType.z * 2 + (rank < budget ? 0 : 1);
                uint local = arguments[bucket * 5 + 1]++; indices[bucket * Capacity + local] = id;
            }
            Order.SetData(order); Indices.SetData(indices); Arguments.SetData(arguments);
        }
        private static ComputeBuffer New(int count, int stride, string name, ComputeBufferType type = ComputeBufferType.Default)
        { return new ComputeBuffer(count, stride, type) { name = "Toolkit crowd " + name }; }
        public void Dispose()
        {
            Instances?.Dispose(); Bounds?.Dispose(); Order?.Dispose(); Indices?.Dispose(); Arguments?.Dispose(); scan?.Dispose(); totals?.Dispose(); offsets?.Dispose();
            Instances = Bounds = Order = Indices = Arguments = scan = totals = offsets = null;
            Capacity = count = types = blocks = Dispatches = 0; BufferBytes = 0; instances = null; order = null; indices = arguments = null;
        }
    }
}
