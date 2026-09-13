using System;
using UnityEngine.Playables;

namespace GakumasPhotoMode
{
    /// <summary>Optional ScriptPlayable adapter. Owns one sequence, no global clock or default app hookup.</summary>
    public sealed class MotionEffectPlayable : PlayableBehaviour
    {
        private readonly MotionEffectSequence sequence = new MotionEffectSequence();
        public MotionEffectSequence Sequence => sequence;
        public bool LastSampleAccepted { get; private set; }
        public override object Clone() => new MotionEffectPlayable();
        public override void PrepareFrame(Playable playable, FrameData info)
        {
            LastSampleAccepted = sequence.TrySample(playable.GetTime());
        }
        // A paused graph retains the sampled pose, matching timeline scrubbing.
        public override void OnGraphStop(Playable playable) { sequence.Stop(); }
        public override void OnPlayableDestroy(Playable playable) { sequence.Dispose(); }
    }

    /// <summary>Convenient manual graph owner. Stop/Dispose hide effects immediately, before Unity's deferred callbacks.</summary>
    public sealed class MotionEffectGraph : IDisposable
    {
        private PlayableGraph graph;
        private readonly ScriptPlayable<MotionEffectPlayable> playable;
        private readonly MotionEffectPlayable behaviour;
        private bool disposed;
        public MotionEffectSequence Sequence => behaviour.Sequence;

        public MotionEffectGraph()
        {
            graph = PlayableGraph.Create("Authored MotionEffect");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            playable = ScriptPlayable<MotionEffectPlayable>.Create(graph);
            behaviour = playable.GetBehaviour();
            var output = ScriptPlayableOutput.Create(graph, "effects");
            output.SetSourcePlayable(playable);
        }

        public bool TrySample(double seconds)
        {
            if (disposed || double.IsNaN(seconds) || double.IsInfinity(seconds) || Math.Abs(seconds) > 1e12)
                return Sequence.TrySample(seconds);
            playable.SetTime(seconds);
            graph.Evaluate(0);
            return behaviour.LastSampleAccepted;
        }

        public void Stop() { Sequence.Stop(); }
        public void Dispose()
        {
            if (disposed) return;
            Sequence.Dispose();
            if (graph.IsValid()) graph.Destroy();
            disposed = true;
        }
    }
}
