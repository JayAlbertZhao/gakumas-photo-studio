using System;
using System.Collections.Generic;
using UnityEngine;

namespace VL.FaceSystem
{
    public sealed class VLActorFaceModel : MonoBehaviour
    {
        public Mesh mesh;
        public Material[] sharedMaterials;
        public uint renderingLayerMask;
        public Transform rootBone;
        public Transform[] bones;
        public Matrix4x4[] bindposes;
        public Bounds localBounds;
        public uint[] boneWeightAndIndices;
        public List<VLFaceBlendShape> blendShapes = new List<VLFaceBlendShape>();
        public VLFaceBlendShapeWeights blendShapeWeights = new VLFaceBlendShapeWeights();

        public float GetWeight(int index)
        {
            return blendShapeWeights == null ? 0f : blendShapeWeights.Get(index);
        }

        public void SetWeight(int index, float value)
        {
            if (blendShapeWeights == null) blendShapeWeights = new VLFaceBlendShapeWeights();
            blendShapeWeights.Set(index, value);
        }

        public void ClearWeights()
        {
            if (blendShapeWeights == null) blendShapeWeights = new VLFaceBlendShapeWeights();
            blendShapeWeights.Clear();
        }
    }

    [Serializable]
    public sealed class VLFaceBlendShape
    {
        public string blendShapeName;
        public List<VLFaceBlendShapeVertex> blendShapeVertices = new List<VLFaceBlendShapeVertex>();
    }

    [Serializable]
    public sealed class VLFaceBlendShapeVertex
    {
        public int vertIndex;
        public Vector3 position;
    }

    [Serializable]
    public sealed class VLFaceBlendShapeWeights
    {
        public VLFaceWeight16 w0 = new VLFaceWeight16();
        public VLFaceWeight16 w1 = new VLFaceWeight16();
        public VLFaceWeight16 w2 = new VLFaceWeight16();
        public VLFaceWeight16 w3 = new VLFaceWeight16();
        public VLFaceWeight16 w4 = new VLFaceWeight16();
        public VLFaceWeight16 w5 = new VLFaceWeight16();
        public VLFaceWeight16 w6 = new VLFaceWeight16();
        public VLFaceWeight16 w7 = new VLFaceWeight16();
        public VLFaceWeight16 w8 = new VLFaceWeight16();
        public VLFaceWeight16 w9 = new VLFaceWeight16();
        public VLFaceWeight16 w10 = new VLFaceWeight16();
        public VLFaceWeight16 w11 = new VLFaceWeight16();

        public float Get(int index)
        {
            VLFaceWeight16 group = Group(index >> 4);
            return group == null ? 0f : group.Get(index & 15);
        }

        public void Set(int index, float value)
        {
            VLFaceWeight16 group = Group(index >> 4);
            if (group != null) group.Set(index & 15, value);
        }

        public void Clear()
        {
            for (int index = 0; index < 12; index++) Group(index).Clear();
        }

        private VLFaceWeight16 Group(int index)
        {
            switch (index)
            {
                case 0: return w0; case 1: return w1; case 2: return w2; case 3: return w3;
                case 4: return w4; case 5: return w5; case 6: return w6; case 7: return w7;
                case 8: return w8; case 9: return w9; case 10: return w10; case 11: return w11;
                default: return null;
            }
        }
    }

    [Serializable]
    public sealed class VLFaceWeight16
    {
        public float w0, w1, w2, w3, w4, w5, w6, w7, w8, w9, w10, w11, w12, w13, w14, w15;

        public float Get(int index)
        {
            switch (index)
            {
                case 0: return w0; case 1: return w1; case 2: return w2; case 3: return w3;
                case 4: return w4; case 5: return w5; case 6: return w6; case 7: return w7;
                case 8: return w8; case 9: return w9; case 10: return w10; case 11: return w11;
                case 12: return w12; case 13: return w13; case 14: return w14; case 15: return w15;
                default: return 0f;
            }
        }

        public void Set(int index, float value)
        {
            switch (index)
            {
                case 0: w0 = value; break; case 1: w1 = value; break; case 2: w2 = value; break; case 3: w3 = value; break;
                case 4: w4 = value; break; case 5: w5 = value; break; case 6: w6 = value; break; case 7: w7 = value; break;
                case 8: w8 = value; break; case 9: w9 = value; break; case 10: w10 = value; break; case 11: w11 = value; break;
                case 12: w12 = value; break; case 13: w13 = value; break; case 14: w14 = value; break; case 15: w15 = value; break;
            }
        }

        public void Clear()
        {
            w0 = w1 = w2 = w3 = w4 = w5 = w6 = w7 = w8 = w9 = w10 = w11 = w12 = w13 = w14 = w15 = 0f;
        }
    }

    public sealed class VLActorFaceBindVertex : MonoBehaviour
    {
        public int vertexIndex;
        public Vector3 basePosition;
    }
}
