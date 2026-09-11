using UnityEngine;
using VL.FaceSystem;

namespace Campus.Common
{
    // Serialization-compatible shell for the original face prefab.
    public sealed class CampusActorFaceCorrection : MonoBehaviour
    {
        public VLActorFaceModel faceModel;
        public Transform target;
        public AnimationCurve[] curves;
        public int[] blendShapeIndices;
    }
}
