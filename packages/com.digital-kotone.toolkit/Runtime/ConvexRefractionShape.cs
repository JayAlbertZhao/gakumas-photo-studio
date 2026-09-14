using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Owned immutable triangle/half-space snapshot for an independently authored convex solid.</summary>
    public sealed class ConvexRefractionShape : IDisposable
    {
        public const int MaximumTriangles = 128;
        internal Mesh Mesh { get; private set; }
        internal readonly Vector4[] Planes;
        public int PlaneCount { get; }
        public int VertexCount { get; }
        public Bounds LocalBounds { get; }
        // CPU-readable mesh and native streams, plus the fixed plane table. Excludes driver overhead.
        public long GeometryBytes { get; }
        public bool IsCreated => Mesh != null;
        private ConvexRefractionShape(Mesh mesh, Vector4[] planes, int count, long bytes)
        { Mesh=mesh; Planes=planes; PlaneCount=count; VertexCount=mesh.vertexCount; LocalBounds=mesh.bounds; GeometryBytes=bytes; }

        public static bool TryCreate(Mesh source, int submesh, int maximumGeometryMiB,
            out ConvexRefractionShape shape, out string reason)
        {
            shape=null; reason="Requires readable closed convex triangles within the declared geometry budget";
            if(source==null || !source.isReadable || source.vertexCount>65536 || submesh<0 || submesh>=source.subMeshCount ||
                source.GetTopology(submesh)!=MeshTopology.Triangles || source.GetIndexCount(submesh)<12 ||
                source.GetIndexCount(submesh)>MaximumTriangles*3 || maximumGeometryMiB<1 || maximumGeometryMiB>256)return false;
            Mesh owned=null;
            try
            {
                var input=source.vertices; var triangles=source.GetTriangles(submesh);
                if(triangles.Length%3!=0)return false;
                var points=new List<Vector3>(); var ids=new Dictionary<Vector3,int>(); var indices=new int[triangles.Length];
                for(int i=0;i<triangles.Length;i++)
                {
                    int index=triangles[i]; if(index<0 || index>=input.Length || !Finite(input[index]))return false;
                    var p=input[index];
                    // Exact position welding accepts split facet normals/UVs without changing the source.
                    if(!ids.TryGetValue(p,out int id)){id=points.Count; ids.Add(p,id); points.Add(p);}
                    indices[i]=id;
                }
                if(points.Count<4)return false;
                var bounds=new Bounds(points[0],Vector3.zero); foreach(var p in points)bounds.Encapsulate(p);
                float extent=bounds.size.magnitude;
                if(!Number(extent) || extent<1e-4f || extent>1e4f)return false;
                var center=Vector3.zero; foreach(var p in points)center+=p/points.Count;
                var edges=new Dictionary<(int,int),(int count,int direction)>();
                var planes=new Vector4[MaximumTriangles]; int count=indices.Length/3;
                for(int i=0;i<count;i++)
                {
                    int ia=indices[i*3],ib=indices[i*3+1],ic=indices[i*3+2];
                    if(ia==ib || ib==ic || ic==ia)return false;
                    var a=points[ia]; var cross=Vector3.Cross(points[ib]-a,points[ic]-a); float length=cross.magnitude;
                    if(length<extent*extent*1e-10f || !Number(length))return false;
                    var n=cross/length; float w=-Vector3.Dot(n,a);
                    if(Vector3.Dot(n,center)+w>=-extent*1e-7f)return false;
                    foreach(var p in points)if(Vector3.Dot(n,p)+w>extent*2e-6f)return false;
                    planes[i]=new Vector4(n.x,n.y,n.z,w);
                    Edge(ia,ib); Edge(ib,ic); Edge(ic,ia);
                }
                foreach(var e in edges.Values)if(e.count!=2 || e.direction!=0)return false;
                long bytes=2L*(points.Count*12L+indices.Length*4L)+MaximumTriangles*16L;
                if(bytes>(long)maximumGeometryMiB*1048576)return false;
                owned=new Mesh{name="Toolkit immutable convex refraction shape",hideFlags=HideFlags.HideAndDontSave,indexFormat=IndexFormat.UInt32};
                owned.SetVertices(points); owned.SetTriangles(indices,0,false); owned.bounds=bounds;
                shape=new ConvexRefractionShape(owned,planes,count,bytes); reason=null; return true;

                void Edge(int a,int b)
                {
                    var key=a<b?(a,b):(b,a); edges.TryGetValue(key,out var old);
                    edges[key]=(old.count+1,old.direction+(a<b?1:-1));
                }
            }
            catch(Exception e){if(owned!=null)UnityEngine.Object.Destroy(owned); reason="Convex shape creation failed: "+e.GetType().Name; return false;}
        }
        private static bool Number(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x);
        private static bool Finite(Vector3 v)=>Number(v.x)&&Number(v.y)&&Number(v.z);
        public void Dispose(){if(Mesh!=null)UnityEngine.Object.Destroy(Mesh);Mesh=null;}
    }
}
