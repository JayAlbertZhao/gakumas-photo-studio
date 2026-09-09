using System;
using UnityEngine;

namespace ActorAnimation
{
    public enum ActorAnimationConstraintExecuteType
    {
        ActorAnimationJob = 0,
        LateUpdate = 1,
        Both = 2,
    }

    // Serialization-compatible implementation of the game's transform constraint.
    // The original executes executeType=0 in its animation job.  The reconstructed
    // runtime evaluates the same math in LateUpdate so downloaded clip curves can
    // still drive constraintWeight0 without rebuilding the proprietary job graph.
    [ExecuteAlways]
    public sealed class ActorAnimationConstraintTransformBone : MonoBehaviour
    {
        public ActorAnimationConstraintExecuteType executeType;
        public Transform constraintSource0;
        public Transform constraintSource1;
        public Transform constraintSource2;
        public Transform constraintSource3;
        public Transform fieldConstraintSource0;
        public Vector3 constraintWeight0;
        public Vector3 constraintWeight1;
        public Vector3 constraintWeight2;
        public Vector3 constraintWeight3;
        public Vector3 fieldConstraintWeight0;
        public Vector3 offsetPosition;
        public Quaternion offsetRotation = Quaternion.identity;
        public Vector3 offsetScale = Vector3.one;

        [NonSerialized] public bool runtimeEvaluationEnabled;

        public void ClearSource()
        {
            constraintSource0 = null;
            constraintSource1 = null;
            constraintSource2 = null;
            constraintSource3 = null;
            fieldConstraintSource0 = null;
            constraintWeight0 = Vector3.zero;
            constraintWeight1 = Vector3.zero;
            constraintWeight2 = Vector3.zero;
            constraintWeight3 = Vector3.zero;
            fieldConstraintWeight0 = Vector3.zero;
        }

        public void AddSource(Transform source)
        {
            AddSource(source, Vector3.zero);
        }

        public void AddSource(Transform source, Vector3 weight)
        {
            if (constraintSource0 == null)
            {
                constraintSource0 = source;
                constraintWeight0 = weight;
            }
            else if (constraintSource1 == null)
            {
                constraintSource1 = source;
                constraintWeight1 = weight;
            }
            else if (constraintSource2 == null)
            {
                constraintSource2 = source;
                constraintWeight2 = weight;
            }
            else if (constraintSource3 == null)
            {
                constraintSource3 = source;
                constraintWeight3 = weight;
            }
            else if (fieldConstraintSource0 == null)
            {
                fieldConstraintSource0 = source;
                fieldConstraintWeight0 = weight;
            }
        }

        private void LateUpdate()
        {
            if (!runtimeEvaluationEnabled) return;
            EvaluateConstraint();
        }

        public void EvaluateConstraint()
        {
            Transform[] sources =
            {
                constraintSource0,
                constraintSource1,
                constraintSource2,
                constraintSource3,
                fieldConstraintSource0,
            };
            Vector3[] weights =
            {
                constraintWeight0,
                constraintWeight1,
                constraintWeight2,
                constraintWeight3,
                fieldConstraintWeight0,
            };

            float positionWeight = SumAxis(weights, sources, 0);
            if (positionWeight == 0f)
            {
                transform.position = offsetPosition;
            }
            else
            {
                Vector3 position = offsetPosition;
                for (int i = 0; i < sources.Length; i++)
                {
                    if (sources[i] != null)
                        position += sources[i].position * (weights[i].x / positionWeight);
                }
                transform.position = position;
            }

            float rotationWeight = SumAxis(weights, sources, 1);
            if (rotationWeight == 0f)
            {
                transform.rotation = offsetRotation;
            }
            else
            {
                Quaternion rotation = Quaternion.identity;
                for (int i = 0; i < sources.Length; i++)
                {
                    if (sources[i] == null) continue;
                    Quaternion partial = Quaternion.SlerpUnclamped(
                        Quaternion.identity, sources[i].rotation,
                        weights[i].y / rotationWeight);
                    rotation = rotation * partial;
                }
                transform.rotation = rotation * offsetRotation;
            }

            float scaleWeight = SumAxis(weights, sources, 2);
            if (scaleWeight == 0f)
            {
                transform.localScale = offsetScale;
            }
            else
            {
                Vector3 scale = Vector3.zero;
                for (int i = 0; i < sources.Length; i++)
                {
                    if (sources[i] != null)
                        scale += sources[i].localScale * (weights[i].z / scaleWeight);
                }
                transform.localScale = Vector3.Scale(scale, offsetScale);
            }
        }

        private static float SumAxis(Vector3[] weights, Transform[] sources, int axis)
        {
            float value = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                if (sources[i] == null) continue;
                value += axis == 0 ? weights[i].x : axis == 1 ? weights[i].y : weights[i].z;
            }
            return value;
        }
    }
}
