using System;
using UnityEngine;

namespace ActorAnimation
{
    // Serialization shells for the original actor IK locator/correction data.
    // The game evaluates these through its animation jobs; Photo Studio keeps
    // the authoring data intact and reconstructs only the paths it has audited.
    public interface IActorAnimationBone
    {
    }

    public enum ActorAnimationIKHandleType
    {
        ReadOnlyTransformHandle = 0,
        TransformSceneHandle = 1,
    }

    public enum ActorAnimationIKCorrectionColliderType : byte
    {
        Sphere = 0,
        Capsule = 1,
    }

    public abstract class ActorAnimationIKCorrectionComponentBase : MonoBehaviour, IActorAnimationBone
    {
        [SerializeField]
        private bool isDirty;

        public bool IsDirty
        {
            get { return isDirty; }
        }

        public void SetDirty()
        {
            isDirty = true;
        }

        public void ResetDirty()
        {
            isDirty = false;
        }
    }

    public sealed class ActorAnimationIKCorrectionCollider :
        ActorAnimationIKCorrectionComponentBase,
        IComparable<ActorAnimationIKCorrectionCollider>
    {
        public ActorAnimationIKCorrectionColliderType type;
        [Range(0f, 1f)] public float weight;
        [Range(0f, 1f)] public float radius;
        [Range(0f, 1f)] public float radiusSub;
        [Range(0f, 1f)] public float dampArea;
        [Range(0f, 1f)] public float length;
        public Vector3 offset;
        public Vector3 rotation;

        [SerializeField]
        private int hierarchyDepth;

        public int HierarchyDepth
        {
            get { return hierarchyDepth; }
        }

        public void UpdateHierarchyDepth()
        {
            hierarchyDepth = GetHierarchyDepth();
        }

        public int GetHierarchyDepth()
        {
            int result = 0;
            Transform current = transform;
            while (current != null)
            {
                result++;
                current = current.parent;
            }
            return result;
        }

        public int CompareTo(ActorAnimationIKCorrectionCollider other)
        {
            if (ReferenceEquals(other, null)) return 1;
            return hierarchyDepth.CompareTo(other.hierarchyDepth);
        }
    }

    public sealed class ActorAnimationIKCorrectionGoal : ActorAnimationIKCorrectionComponentBase
    {
        public AvatarIKGoal goal;
        public bool enable;
        [Range(0f, 0.2f)] public float radius;
        public Vector3 offset;
    }

    public sealed class ActorAnimationIKBodyEffectorLocator : MonoBehaviour
    {
        public ActorAnimationIKHandleType handleType;
    }

    public sealed class ActorAnimationIKGoalEffectorLocator : MonoBehaviour
    {
        public ActorAnimationIKHandleType handleType;
        public AvatarIKGoal goal;
    }

    public sealed class ActorAnimationIKHintEffectorLocator : MonoBehaviour
    {
        public ActorAnimationIKHandleType handleType;
        public AvatarIKHint hint;
    }

    public sealed class ActorAnimationLookAtEffectorLocator : MonoBehaviour
    {
        [Range(0f, 1f)] public float eyesWeight;
        [Range(0f, 1f)] public float headWeight;
        [Range(0f, 1f)] public float bodyWeight;
    }

    public sealed class ActorAnimationFullBodyIKPullPartSetting : MonoBehaviour
    {
        [Range(0f, 1f)] public float lookAtClampWeight;
        [Range(0f, 1.5f)] public float stiffness;
        [Range(1, 50)] public int maxPullIteration;
        [Range(-0.2f, 0.2f)] public float handDown;
    }
}
