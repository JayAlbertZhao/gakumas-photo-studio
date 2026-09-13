using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Owned full-resolution overlay draws for explicitly registered receivers.
    /// Prepare after animation; draw with the same camera/depth as the receiver. Prepared
    /// materials are borrowed until the next Prepare/Clear/Dispose; do not retain commands.
    /// No source material/property-block mutation, global shader state or GPU readback.</summary>
    public sealed class FaceDecalRenderer : IDisposable
    {
        [Serializable]
        public sealed class Receiver
        {
            public SceneDepthData.Surface surface = new SceneDepthData.Surface();
            public CompareFunction stencilComparison = CompareFunction.Always;
            [Range(0, 255)] public int stencilReference, stencilReadMask = 255;
        }
        public readonly struct Draw
        {
            public readonly SceneDepthData.Surface surface;
            public readonly Material material;
            internal Draw(SceneDepthData.Surface surface, Material material) { this.surface = surface; this.material = material; }
        }
        private readonly List<Material> materials = new List<Material>();
        private Draw[] draws = Array.Empty<Draw>();
        private Texture atlas;
        private bool disposed;
        private readonly MaterialPropertyBlock rendererBlock = new MaterialPropertyBlock(), slotBlock = new MaterialPropertyBlock();
        private static readonly string[] ReservedProperties = {
            "_AuthoredDecalAtlas","_AuthoredDecalReceiverMask","_AuthoredDecalCount","_AuthoredDecalWorldToLocal","_AuthoredDecalUv",
            "_AuthoredDecalTint","_AuthoredDecalSettings","_AuthoredDecalRotation","_AuthoredDecalDirection","_AuthoredDecalReceiverUv",
            "_AuthoredDecalVertexScale","_AuthoredDecalCutoff","_AuthoredDecalCull","_AuthoredDecalStencilComp","_AuthoredDecalStencilRef","_AuthoredDecalStencilRead" };
        public bool LastPrepareSucceeded { get; private set; }
        public string UnavailableReason { get; private set; }
        public int ProjectorCount { get; private set; }
        public int MaterialCount => materials.Count;
        public IReadOnlyList<Draw> Draws => LastPrepareSucceeded ? draws : Array.Empty<Draw>();

        public bool TryPrepare(Texture atlas, FaceDecalProjector.Data[] projectors, Receiver[] receivers)
        {
            LastPrepareSucceeded = false; UnavailableReason = null; draws = Array.Empty<Draw>(); ProjectorCount = 0;
            try
            {
                if (disposed) throw new ObjectDisposedException(nameof(FaceDecalRenderer));
                if (projectors == null || projectors.Length > 8 || receivers == null || receivers.Length > 128)
                    throw new ArgumentException("At most8 projectors and128 receivers are supported; arrays are required.");
                if (projectors.Length == 0 || receivers.Length == 0) { Clear(); LastPrepareSucceeded = true; return true; }
                RequireTexture(atlas);
                var matrices = new Matrix4x4[8]; var uv = new Vector4[8]; var color = new Vector4[8];
                var settings = new Vector4[8]; var rotation = new Vector4[8]; var direction = new Vector4[8];
                int count = 0;
                bool requireNormals = false;
                foreach (var data in projectors)
                {
                    Validate(data); var p = data.pose;
                    if (p.opacity == 0 || p.tint.w == 0) continue;
                    requireNormals |= p.angleFadeStart < 180;
                    Matrix4x4 world = data.parentToWorld * Matrix4x4.TRS(p.positionOffset, Quaternion.Euler(p.rotationDegrees), p.scale);
                    Matrix4x4 volume = world * Matrix4x4.Translate(p.pivot) * Matrix4x4.Scale(p.size);
                    matrices[count] = volume.inverse;
                    RequireMatrix(matrices[count]);
                    uv[count] = new Vector4(p.uvScale.x, p.uvScale.y, p.uvBias.x, p.uvBias.y);
                    color[count] = p.tint;
                    settings[count] = new Vector4(p.opacity, p.edgeFeather, (int)p.blend, p.angleFadeStart == 180 ? 0 : 1);
                    float radians = p.uvRotationDegrees * Mathf.Deg2Rad;
                    rotation[count] = new Vector4(Mathf.Cos(radians), Mathf.Sin(radians), Mathf.Cos(p.angleFadeStart*Mathf.Deg2Rad), Mathf.Cos(p.angleFadeEnd*Mathf.Deg2Rad));
                    direction[count] = -((Vector3)world.GetColumn(2)).normalized;
                    count++;
                }
                var snapshots = new List<Receiver>(); var seen = new HashSet<long>();
                foreach (var receiver in receivers)
                {
                    if (receiver == null || !SceneDepthData.ValidSurface(receiver.surface)) throw new ArgumentException("Invalid receiver/submesh/geometry inputs.");
                    var s = receiver.surface;
                    RequireGeometry(s, requireNormals);
                    RequireReceiverBlock(s);
                    if ((int)receiver.stencilComparison < 1 || (int)receiver.stencilComparison > 8 || receiver.stencilReference < 0 || receiver.stencilReference > 255 || receiver.stencilReadMask < 0 || receiver.stencilReadMask > 255)
                        throw new ArgumentException("Invalid stencil comparison.");
                    RequireMatrix(s.renderer.localToWorldMatrix);
                    if (s.alphaMask != null) RequireTexture(s.alphaMask);
                    for (int i=0;i<3;i++) if (!Range(s.vertexScale[i],-1e4f,1e4f)) throw new ArgumentException("Invalid vertex scale.");
                    if (!seen.Add(((long)s.renderer.GetInstanceID()<<32)|(uint)s.materialIndex)) throw new ArgumentException("Duplicate receiver submesh.");
                    if (!s.renderer.enabled || s.renderer.forceRenderingOff || !s.renderer.gameObject.activeInHierarchy) continue;
                    snapshots.Add(new Receiver { stencilComparison=receiver.stencilComparison, stencilReference=receiver.stencilReference, stencilReadMask=receiver.stencilReadMask,
                        surface=new SceneDepthData.Surface {renderer=s.renderer, materialIndex=s.materialIndex, cull=s.cull, vertexScale=s.vertexScale,
                            alphaMask=s.alphaMask, alphaMaskST=s.alphaMaskST, alphaCutoff=s.alphaCutoff} });
                }
                if (count == 0 || snapshots.Count == 0) { Clear(); LastPrepareSucceeded = true; return true; }
                var shader = Resources.Load<Shader>("AuthoredFaceDecal");
                if (shader == null || !shader.isSupported) throw new NotSupportedException("AuthoredFaceDecal shader unavailable.");
                while (materials.Count > snapshots.Count) { Release(materials[materials.Count-1]); materials.RemoveAt(materials.Count-1); }
                while (materials.Count < snapshots.Count) materials.Add(new Material(shader) {name="Owned authored face decal",hideFlags=HideFlags.HideAndDontSave});
                var prepared = new Draw[snapshots.Count];
                for (int i=0;i<snapshots.Count;i++)
                {
                    if (materials[i] == null) materials[i] = new Material(shader) {hideFlags=HideFlags.HideAndDontSave};
                    var m = materials[i]; var receiver = snapshots[i]; var s = receiver.surface;
                    m.SetTexture("_AuthoredDecalAtlas",atlas); m.SetInt("_AuthoredDecalCount",count);
                    m.SetMatrixArray("_AuthoredDecalWorldToLocal",matrices);m.SetVectorArray("_AuthoredDecalUv",uv);m.SetVectorArray("_AuthoredDecalTint",color);
                    m.SetVectorArray("_AuthoredDecalSettings",settings);m.SetVectorArray("_AuthoredDecalRotation",rotation);m.SetVectorArray("_AuthoredDecalDirection",direction);
                    m.SetTexture("_AuthoredDecalReceiverMask",s.alphaMask ?? Texture2D.whiteTexture);m.SetVector("_AuthoredDecalReceiverUv",s.alphaMaskST);
                    m.SetVector("_AuthoredDecalVertexScale",s.vertexScale);m.SetFloat("_AuthoredDecalCutoff",s.alphaCutoff);m.SetInt("_AuthoredDecalCull",(int)s.cull);
                    m.SetInt("_AuthoredDecalStencilComp",(int)receiver.stencilComparison);m.SetInt("_AuthoredDecalStencilRef",receiver.stencilReference);m.SetInt("_AuthoredDecalStencilRead",receiver.stencilReadMask);
                    prepared[i] = new Draw(s,m);
                }
                this.atlas = atlas; requiresNormals = requireNormals; draws = prepared; ProjectorCount = count; LastPrepareSucceeded = true; return true;
            }
            catch (Exception error) { UnavailableReason = error.Message; Clear(); return false; }
        }

        /// <summary>Append to a freshly cleared host buffer after receiver depth/color.
        /// Does not change render targets, projection, depth, alpha or stencil contents.</summary>
        public bool Record(CommandBuffer commands)
        {
            if (commands == null || !Current()) return false;
            foreach (var draw in draws) commands.DrawRenderer(draw.surface.renderer,draw.material,draw.surface.materialIndex,0);
            return true;
        }
        /// <summary>Append AFTER the original character draws. Overlay does not add coverage.</summary>
        public PlanarReflection.Draw[] PlanarDraws()
        {
            if (!Current()) return Array.Empty<PlanarReflection.Draw>();
            var result = new PlanarReflection.Draw[draws.Length];
            for (int i=0;i<draws.Length;i++) result[i] = new PlanarReflection.Draw {surface=draws[i].surface,material=draws[i].material,shaderPass=0,coverageMaterial=draws[i].material,coverageShaderPass=1};
            return result;
        }
        private bool Current()
        {
            if (!LastPrepareSucceeded || disposed) return false;
            try
            {
                if (draws.Length > 0) RequireTexture(atlas);
                foreach (var draw in draws)
                {
                    var r = draw.surface.renderer;
                    if (draw.material == null || !SceneDepthData.ValidSurface(draw.surface) || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy)
                        throw new InvalidOperationException("Prepared receiver/material is no longer current; prepare again.");
                    if (draw.surface.alphaMask != null) RequireTexture(draw.surface.alphaMask);
                    RequireMatrix(r.localToWorldMatrix);
                    RequireGeometry(draw.surface, requiresNormals);
                    RequireReceiverBlock(draw.surface);
                }
                return true;
            }
            catch (Exception error) { UnavailableReason=error.Message;Clear();return false; }
        }
        internal static void Validate(FaceDecalProjector.Data data)
        {
            RequireMatrix(data.parentToWorld);var p=data.pose;
            if(p==null)throw new ArgumentException("Missing projector pose.");
            foreach(var v in new[]{p.positionOffset,p.rotationDegrees,p.scale,p.size,p.pivot})
                for(int c=0;c<3;c++)if(!Range(v[c],-1e4f,1e4f))throw new ArgumentException("Invalid projector vector.");
            for(int c=0;c<3;c++)if(Mathf.Abs(p.scale[c])<.00001f||p.size[c]<.00001f)throw new ArgumentException("Degenerate projector size/scale.");
            for(int c=0;c<2;c++)if(!Range(p.uvScale[c],-1e4f,1e4f)||!Range(p.uvBias[c],-1e4f,1e4f))throw new ArgumentException("Invalid atlas transform.");
            for(int c=0;c<4;c++)if(!Range(p.tint[c],0,c==3?1:64))throw new ArgumentException("Invalid literal linear tint.");
            if(!Range(p.opacity,0,1)||!Range(p.edgeFeather,0,.5f)||!Range(p.angleFadeStart,0,180)||!Range(p.angleFadeEnd,p.angleFadeStart,180)||
                !Range(p.uvRotationDegrees,-1e6f,1e6f)||(int)p.blend<0||(int)p.blend>1)throw new ArgumentException("Invalid projector scalar.");
        }
        private void RequireReceiverBlock(SceneDepthData.Surface surface)
        {
            surface.renderer.GetPropertyBlock(rendererBlock);surface.renderer.GetPropertyBlock(slotBlock,surface.materialIndex);
            var block=slotBlock.isEmpty?rendererBlock:slotBlock;
            foreach(var name in ReservedProperties)if(block.HasProperty(name))
                throw new ArgumentException("Receiver property block overrides reserved authored decal input: "+name);
        }
        private bool requiresNormals;
        private static void RequireGeometry(SceneDepthData.Surface s, bool angular)
        {
            var mesh = s.renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : s.renderer.GetComponent<MeshFilter>().sharedMesh;
            if (mesh.GetTopology(s.materialIndex) != MeshTopology.Triangles || !mesh.HasVertexAttribute(VertexAttribute.Position) ||
                mesh.GetVertexAttributeDimension(VertexAttribute.Position) != 3 ||
                (angular && (!mesh.HasVertexAttribute(VertexAttribute.Normal) || mesh.GetVertexAttributeDimension(VertexAttribute.Normal) != 3)) ||
                (s.alphaMask != null && s.alphaCutoff > 0 && (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0) || mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0) < 2)))
                throw new ArgumentException("Triangle positions, angular-fade normals and cutout UV0 must match the source surface.");
        }
        private static void RequireMatrix(Matrix4x4 m)
        {
            for(int i=0;i<16;i++)if(!Range(m[i],-1e8f,1e8f))throw new ArgumentException("Nonfinite/out-of-range transform.");
            if(m.m30!=0||m.m31!=0||m.m32!=0||m.m33!=1||Mathf.Abs(m.determinant)<1e-12f)throw new ArgumentException("Invertible affine transform required.");
        }
        private static void RequireTexture(Texture texture)
        {
            if(texture==null||texture.dimension!=TextureDimension.Tex2D||texture.width<1||texture.height<1||texture.width>16384||texture.height>16384||
                (texture is RenderTexture rt&&(!rt.IsCreated()||rt.antiAliasing!=1||rt.format==RenderTextureFormat.Depth||rt.format==RenderTextureFormat.Shadowmap)))throw new ArgumentException("Current sampleable non-MSAA 2D color texture required (up to16384 per dimension).");
        }
        private static bool Range(float v,float lo,float hi)=>!float.IsNaN(v)&&!float.IsInfinity(v)&&v>=lo&&v<=hi;
        private static void Release(Material m){if(m!=null)UnityEngine.Object.DestroyImmediate(m);}
        public void Clear(){foreach(var m in materials)Release(m);materials.Clear();draws=Array.Empty<Draw>();atlas=null;ProjectorCount=0;LastPrepareSucceeded=false;}
        public void Dispose(){Clear();disposed=true;}
    }
}
