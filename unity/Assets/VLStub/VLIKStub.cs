using ActorAnimation;
using UnityEngine;

namespace VL.IK
{
    public class EffectorBase : MonoBehaviour
    {
    }

    public sealed class CustomLookAtEffector : MonoBehaviour
    {
        public Transform target;
        [Range(0f, 1f)] public float customUseWeight;
        [Range(0f, 1f)] public float weight;
        [Range(0f, 1f)] public float eyesWeight;
        [Range(0f, 1f)] public float headWeight;
        [Range(0f, 1f)] public float bodyWeight;
        public bool useCustomTargetPosition;
        public Vector3 customTargetPosition;
    }

    public sealed class IKBodyEffector : EffectorBase
    {
        [SerializeField]
        private ActorAnimationIKHandleType handleType;

        public ActorAnimationIKHandleType HandleType
        {
            get { return handleType; }
            set { handleType = value; }
        }
    }

    public sealed class IKGoalEffector : EffectorBase
    {
        [SerializeField]
        private ActorAnimationIKHandleType handleType;

        public AvatarIKGoal goal;

        public ActorAnimationIKHandleType HandleType
        {
            get { return handleType; }
            set { handleType = value; }
        }
    }

    public sealed class IKHintEffector : EffectorBase
    {
        [SerializeField]
        private ActorAnimationIKHandleType handleType;

        public AvatarIKHint hint;

        public ActorAnimationIKHandleType HandleType
        {
            get { return handleType; }
            set { handleType = value; }
        }
    }

    public sealed class LookAtEffector : EffectorBase
    {
        [Range(0f, 1f)] public float eyesWeight;
        [Range(0f, 1f)] public float headWeight;
        [Range(0f, 1f)] public float bodyWeight;
    }
}
