#if GAKUMAS_OPEN_SWING_REFERENCE
using OpenSwing;
using UnityEngine;

namespace Gakumas.Reproduction
{
    /// <summary>
    /// Optional caller-owned bridge for side-by-side reproduction runs.
    /// Define GAKUMAS_OPEN_SWING_REFERENCE only in a playground assembly that
    /// has imported the MIT Open Swing package. This hook never drives Update
    /// or LateUpdate and is disabled until the caller enables it explicitly.
    /// </summary>
    public sealed class OpenSwingReferenceHook : MonoBehaviour
    {
        [SerializeField] private ActorAnimationSwingSolver solver;
        [SerializeField] private bool enabledByDefault;

        public bool Enabled { get; set; }

        private void Awake()
        {
            Enabled = enabledByDefault;
        }

        public void RestoreBeforeAnimation()
        {
            if (Enabled && solver) solver.RestorePose();
        }

        public void SimulateAfterAnimation(float deltaTime)
        {
            if (!Enabled || !solver) return;
            solver.CapturePose();
            solver.Step(deltaTime);
        }

        public void ResetReference()
        {
            if (solver) solver.RequestReset();
        }
    }
}
#endif
