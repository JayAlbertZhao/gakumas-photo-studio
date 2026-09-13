using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Playables;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyMotionEffects(Report report)
        {
            yield return null;
            var others = FindObjectsOfType<Renderer>(); var forced = others.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in others) r.forceRenderingOff = true;
            var active = RenderTexture.active;
            var owner = new MotionEffectSequence(); var second = new MotionEffectSequence();
            PlayableGraph graph = default; MotionEffectGraph graphOwner = null;
            try
            {
                void Check(string id, bool ok, float difference = 0) => FrameworkCheck(report, "motion-effect-" + id, ok, difference);
                Check("seed-golden-values", MotionEffectSequence.SystemSeed(1, 0) == 2527132011u &&
                    MotionEffectSequence.SystemSeed(713, 0) == 270339802u && MotionEffectSequence.SystemSeed(812, 1) == 1749174919u);
                var camera = Own(new GameObject("MotionEffect actual camera")).AddComponent<Camera>();
                camera.enabled = false; camera.cullingMask = 1 << 25; camera.orthographic = true; camera.orthographicSize = 1;
                camera.aspect = 113f / 79; camera.nearClipPlane = .1f; camera.farClipPlane = 10;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.11f, .21f, .31f, .61f);
                camera.allowHDR = true; camera.allowMSAA = false;
                var target = Own(new RenderTexture(113, 79, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
                target.Create(); camera.targetTexture = target;
                Color[] Render() { camera.Render(); return ReadSceneTarget(target); }
                var baseline = Render();
                bool Exact(Color[] a, Color[] b) => a.Length == b.Length && a.Zip(b, (x, y) => x.Equals(y)).All(x => x);
                var attachment = Own(new GameObject("Explicit effect locator")).transform; attachment.position = new Vector3(0, 0, 3);
                var template = Own(new GameObject("Authored local particle template")); template.SetActive(false); template.layer = 25;
                var ps = template.AddComponent<ParticleSystem>(); var main = ps.main;
                main.duration = 4; main.loop = false; main.prewarm = false; main.startLifetime = 4; main.startSpeed = .4f;
                main.startSize = .18f; main.maxParticles = 32; main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.gravityModifier = 0; main.playOnAwake = true; main.stopAction = ParticleSystemStopAction.Destroy;
                var emission = ps.emission; emission.rateOverTime = 0; emission.rateOverDistance = 0;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0, 1) });
                var shape = ps.shape; shape.enabled = false;
                var material = Own(new Material(Shader.Find("Hidden/PhotoStudio/FaceDecalProbe")));
                material.SetVector("_ProbeTint", new Vector4(.91f, .27f, .16f, .61f));
                ps.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
                var sourceSeed = ps.randomSeed; bool sourceAuto = ps.useAutoRandomSeed;
                var randomState = JsonUtility.ToJson(UnityEngine.Random.state);
                float savedFixed = Time.fixedDeltaTime, savedScale = Time.timeScale;
                var entry = new MotionEffectSequence.Entry { id = "tear", prefab = template, attachment = attachment,
                    startSeconds = .25, durationSeconds = 2, seed = 713 };
                Check("bind", owner.TryBind(new[] { entry }, 3));
                entry.seed = 812; // Bind owns a copy, not the caller's mutable entries.
                Check("before-start", owner.TrySample(.249) && owner.ActiveCount == 0 && Exact(Render(), baseline));
                Check("at-start", owner.TrySample(.25) && owner.ActiveCount == 1);
                Check("sample", owner.TrySample(.75));
                var instance = owner.GetInstance("tear"); var actualPs = instance.GetComponent<ParticleSystem>();
                var particles = new ParticleSystem.Particle[32]; int count = actualPs.GetParticles(particles);
                Check("actual-particle-burst-count", count == 1, count);
                // Constant local velocity over each particle's actual age; no copy of the runtime stepping algorithm.
                float error = count == 1 ? (particles[0].position - Vector3.forward * (.4f * (particles[0].startLifetime - particles[0].remainingLifetime))).magnitude : 1;
                Check("independent-constant-velocity", error <= .00002f, error);
                Check("particle-age", count == 1 && Math.Abs(particles[0].remainingLifetime - 3.5f) <= .00002f, count == 1 ? particles[0].remainingLifetime : -1);
                Check("owned-seed-snapshot", !actualPs.useAutoRandomSeed && actualPs.randomSeed == MotionEffectSequence.SystemSeed(713, 0));
                Check("owned-clock-paused", actualPs.isPaused && !actualPs.main.playOnAwake && actualPs.main.stopAction == ParticleSystemStopAction.None);
                var image = Render(); Check("positive-rendered-particles", image.Where((p, i) => p.r != baseline[i].r).Count() > 20);
                SaveSsrPreview("motion-effect-particle", image, 113, 79, false);
                var originalParticle = particles[0];
                yield return null; yield return null;
                actualPs.GetParticles(particles);
                Check("no-frame-clock-drift", particles[0].position.Equals(originalParticle.position) && particles[0].remainingLifetime == originalParticle.remainingLifetime && Exact(image, Render()));
                foreach (double seek in new[] { 1.4, .25, 2.249, .75 }) Check("seek-" + seek, owner.TrySample(seek));
                Check("backward-whole-image-exact", Exact(image, Render()));
                actualPs.GetParticles(particles);
                Check("backward-particle-state-exact", particles[0].position.Equals(originalParticle.position) && particles[0].remainingLifetime == originalParticle.remainingLifetime && particles[0].randomSeed == originalParticle.randomSeed);
                Check("end-exclusive", owner.TrySample(2.25) && owner.ActiveCount == 0 && !instance.activeSelf && Exact(Render(), baseline));
                Check("negative-inactive", owner.TrySample(-1) && owner.ActiveCount == 0);
                Check("return-recreated", owner.TrySample(.75) && owner.GetInstance("tear") != instance && Exact(image, Render()));
                Check("repeat-bind", owner.TryBind(new[] { entry }, 3, true));
                Check("repeat-negative", owner.TrySample(-.25) && owner.ActiveCount == 0);
                Check("repeat-origin", owner.TrySample(3) && owner.ActiveCount == 0);
                Check("repeat-sample", owner.TrySample(3.75)); var loopImage = Render();
                Check("repeat-local-time", owner.TrySample(.75) && Exact(loopImage, Render()));
                Check("large-clock", owner.TrySample(999999999999.75) && Exact(loopImage, Render()));
                Check("overlap-bind", owner.TryBind(new[] { entry, new MotionEffectSequence.Entry { id = "spark", prefab = template,
                    attachment = attachment, startSeconds = .5, durationSeconds = .5, seed = 12 } }, 3));
                Check("overlap-both", owner.TrySample(.75) && owner.ActiveCount == 2 && owner.GetInstance("tear") != owner.GetInstance("spark"));
                Check("overlap-end", owner.TrySample(1) && owner.ActiveCount == 1 && owner.GetInstance("spark") == null);
                owner.Stop(); Check("stop-clears-immediately", owner.ActiveCount == 0 && Exact(baseline, Render()));
                Check("stop-retains-binding", owner.TrySample(.75) && owner.ActiveCount == 2);
                owner.Clear(); Check("clear-unbinds", owner.TrySample(.75) && owner.ActiveCount == 0);
                // Random shape samples exercise stable per-system seeds and real child simulation without double stepping.
                shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = .35f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0, 16) });
                var child = UnityEngine.Object.Instantiate(template, template.transform); child.name = "Child system"; child.SetActive(true);
                child.transform.localPosition = new Vector3(.4f, 0, 0);
                Check("multi-system-bind", owner.TryBind(new[] { entry }, 3)); Check("multi-system-sample", owner.TrySample(.75));
                var systems = owner.GetInstance("tear").GetComponentsInChildren<ParticleSystem>();
                Check("stable-child-seeds", systems.Length == 2 && systems[0].randomSeed == MotionEffectSequence.SystemSeed(812, 0) && systems[1].randomSeed == MotionEffectSequence.SystemSeed(812, 1) && systems[0].randomSeed != systems[1].randomSeed);
                Check("children-not-double-stepped", systems.All(p => Math.Abs(p.time - .5f) < .00002));
                ParticleSystem.Particle[][] Snapshot(GameObject root) => root.GetComponentsInChildren<ParticleSystem>().Select(p => {
                    var values = new ParticleSystem.Particle[p.main.maxParticles]; int n = p.GetParticles(values); return values.Take(n).ToArray(); }).ToArray();
                bool SameParticles(ParticleSystem.Particle[][] a, ParticleSystem.Particle[][] b) => a.Length == b.Length && a.Zip(b, (x, y) => x.Length == y.Length && x.Zip(y, (p, q) =>
                    p.position.Equals(q.position) && p.velocity.Equals(q.velocity) && p.rotation3D.Equals(q.rotation3D) &&
                    p.angularVelocity3D.Equals(q.angularVelocity3D) && p.startSize3D.Equals(q.startSize3D) && p.startColor.Equals(q.startColor) &&
                    p.remainingLifetime == q.remainingLifetime && p.startLifetime == q.startLifetime && p.randomSeed == q.randomSeed).All(v => v)).All(v => v);
                var scatterParticles = Snapshot(owner.GetInstance("tear"));
                var scatter = Render(); SaveSsrPreview("motion-effect-scatter", scatter, 113, 79, false);
                owner.Stop(); Check("independent-owner-bind", second.TryBind(new[] { entry }, 3)); Check("independent-owner-sample", second.TrySample(.75));
                Check("independent-owner-whole-image-exact", Exact(scatter, Render()));
                Check("independent-owner-all-particle-state-exact", SameParticles(scatterParticles, Snapshot(second.GetInstance("tear"))));
                second.Stop(); Check("random-seek-exact", owner.TrySample(1.7) && owner.TrySample(.75) && Exact(scatter, Render()));
                Check("random-seek-all-particle-state-exact", SameParticles(scatterParticles, Snapshot(owner.GetInstance("tear"))));
                owner.Stop(); entry.seed++;
                Check("changed-seed-bind", second.TryBind(new[] { entry }, 3) && second.TrySample(.75));
                Check("changed-seed-visible", !Exact(scatter, Render())); second.Stop(); entry.seed--;
                entry.positionOffset = new Vector3(.13f, -.17f, .2f); entry.rotationDegrees = new Vector3(17, 29, 31); entry.scale = new Vector3(-.8f, 1.2f, .7f);
                attachment.rotation = Quaternion.Euler(3, 7, 13); attachment.localScale = new Vector3(1.1f, .9f, 1.3f);
                Check("offset-bind", owner.TryBind(new[] { entry }, 3) && owner.TrySample(.75));
                Matrix4x4 expected = attachment.localToWorldMatrix * Matrix4x4.TRS(entry.positionOffset, Quaternion.Euler(entry.rotationDegrees), entry.scale);
                Matrix4x4 actual = owner.GetInstance("tear").transform.localToWorldMatrix; float matrixError = 0;
                for (int i = 0; i < 16; i++) matrixError = Mathf.Max(matrixError, Mathf.Abs(expected[i] - actual[i]));
                Check("attachment-trs", matrixError <= .00002f, matrixError);
                var moved = Render(); attachment.position += Vector3.right * .2f;
                Check("attachment-follows-current-pose", owner.TrySample(.75) && !Exact(moved, Render())); attachment.position -= Vector3.right * .2f;
                Check("attachment-seek-image-exact", owner.TrySample(.75) && Exact(moved, Render()));
                attachment.gameObject.SetActive(false); Check("inactive-attachment-removes", owner.TrySample(.75) && owner.ActiveCount == 0);
                attachment.gameObject.SetActive(true); Check("reactivate-attachment", owner.TrySample(.75) && Exact(moved, Render()));
                var doomed = owner.GetInstance("tear"); UnityEngine.Object.DestroyImmediate(doomed);
                Check("externally-destroyed-instance-recovered", owner.TrySample(.75) && Exact(moved, Render()));
                foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, 1e13 })
                {
                    Check("reject-time-" + invalid, !owner.TrySample(invalid) && owner.ActiveCount == 0 && Exact(Render(), baseline));
                    Check("recover-time-" + invalid, owner.TrySample(.75));
                }
                owner.Stop();
                Check("invalid-bind-unbinds", !owner.TryBind(new[] { entry, entry }, 3) && owner.TrySample(.75) && owner.ActiveCount == 0);
                var invalidEntry = new MotionEffectSequence.Entry { id = "bad", prefab = template, attachment = attachment };
                invalidEntry.seed = 0; Check("zero-seed-rejected", !owner.TryBind(new[] { invalidEntry }, 3)); invalidEntry.seed = 1;
                invalidEntry.durationSeconds = double.NaN; Check("invalid-duration-rejected", !owner.TryBind(new[] { invalidEntry }, 3)); invalidEntry.durationSeconds = 1;
                invalidEntry.scale = Vector3.zero; Check("singular-offset-rejected", !owner.TryBind(new[] { invalidEntry }, 3)); invalidEntry.scale = Vector3.one;
                invalidEntry.attachment = null; Check("missing-attachment-rejected", !owner.TryBind(new[] { invalidEntry }, 3));
                main.simulationSpace = ParticleSystemSimulationSpace.World; Check("world-space-requires-history", !owner.TryBind(new[] { entry }, 3)); main.simulationSpace = ParticleSystemSimulationSpace.Local;
                var collision = ps.collision; collision.enabled = true; Check("physics-requires-history", !owner.TryBind(new[] { entry }, 3)); collision.enabled = false;
                var sub = ps.subEmitters; sub.enabled = true; Check("subemitters-rejected", !owner.TryBind(new[] { entry }, 3)); sub.enabled = false;
                main.gravityModifier = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0, 1, 1));
                Check("gravity-curve-rejected", !owner.TryBind(new[] { entry }, 3)); main.gravityModifier = 0;
                emission.rateOverDistance = 1; Check("distance-emission-requires-history", !owner.TryBind(new[] { entry }, 3)); emission.rateOverDistance = 0;
                var script = template.AddComponent<FaceDecalProjector>(); Check("scripts-not-executed", !owner.TryBind(new[] { entry }, 3)); UnityEngine.Object.DestroyImmediate(script);
                Check("source-mutation-invalidation-bind", owner.TryBind(new[] { entry }, 3) && owner.TrySample(.75));
                main.loop = true; main.prewarm = true; Check("current-template-revalidated", !owner.TrySample(.75) && owner.ActiveCount == 0); main.prewarm = false; main.loop = false;
                var many = Enumerable.Range(0, 32).Select(i => new MotionEffectSequence.Entry { id = "budget-" + i, prefab = template,
                    attachment = attachment, durationSeconds = 60 }).ToArray();
                Check("budget-bind", owner.TryBind(many, 60)); Check("budget-fails-before-spawn", !owner.TrySample(59) && owner.ActiveCount == 0 && owner.GetInstance("budget-0") == null);
                Check("entry-capacity", !owner.TryBind(many.Concat(new[] { entry }).ToArray(), 60));
                main.maxParticles = 65537; Check("particle-capacity", !owner.TryBind(new[] { entry }, 3)); main.maxParticles = 32;
                main.gravityModifier = new ParticleSystem.MinMaxCurve(2, 4);
                var zero = main.gravityModifier; zero.constant = 0; zero.mode = ParticleSystemCurveMode.Constant; main.gravityModifier = zero;
                Check("constant-zero-ignores-unused-minimum", owner.TryBind(new[] { entry }, 3)); main.gravityModifier = 0;
                entry.attachment = template.transform; Check("template-cannot-own-attachment", !owner.TryBind(new[] { entry }, 3)); entry.attachment = attachment;
                var lostTemplate = UnityEngine.Object.Instantiate(template); lostTemplate.SetActive(false);
                var lostEntry = new MotionEffectSequence.Entry { id = "lost", prefab = lostTemplate, attachment = attachment };
                Check("lost-template-bind", owner.TryBind(new[] { lostEntry }, 3) && owner.TrySample(.5));
                UnityEngine.Object.DestroyImmediate(lostTemplate); Check("lost-template-cleans", !owner.TrySample(.5) && owner.ActiveCount == 0 && Exact(baseline, Render()));
                Check("source-template-unchanged", !template.activeSelf && ps.randomSeed == sourceSeed && ps.useAutoRandomSeed == sourceAuto && main.playOnAwake && main.stopAction == ParticleSystemStopAction.Destroy && ps.GetComponent<ParticleSystemRenderer>().sharedMaterial == material);
                Check("global-clock-random-unchanged", Time.fixedDeltaTime == savedFixed && Time.timeScale == savedScale && JsonUtility.ToJson(UnityEngine.Random.state) == randomState);
                owner.Clear();
                graph = PlayableGraph.Create("Actual motion effect graph"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = ScriptPlayable<MotionEffectPlayable>.Create(graph); var output = ScriptPlayableOutput.Create(graph, "effects"); output.SetSourcePlayable(playable);
                var behaviour = playable.GetBehaviour(); Check("playable-bind", behaviour.Sequence.TryBind(new[] { entry }, 3));
                playable.SetTime(.75); graph.Evaluate(0); Check("actual-graph-evaluate", behaviour.LastSampleAccepted && behaviour.Sequence.ActiveCount == 1); var graphImage = Render();
                playable.SetTime(1.6); graph.Evaluate(0); playable.SetTime(.75); graph.Evaluate(0); Check("actual-graph-seek-exact", Exact(graphImage, Render()));
                var clone = ScriptPlayable<MotionEffectPlayable>.Create(graph, behaviour);
                Check("playable-clone-independent-owner", !ReferenceEquals(behaviour.Sequence, clone.GetBehaviour().Sequence));
                var graphInstance = behaviour.Sequence.GetInstance("tear"); graph.Destroy(); graph = default;
                // Unity schedules graph destruction later in the frame. Test its real callback boundary.
                yield return null;
                Check("graph-destroy-cleans-owned-only", (graphInstance == null || !graphInstance.activeSelf) && behaviour.Sequence.ActiveCount == 0 && template != null && attachment != null && Exact(Render(), baseline));
                Check("graph-disposal-terminal", !behaviour.Sequence.TrySample(.75) && !behaviour.Sequence.TryBind(new[] { entry }, 3));
                graphOwner = new MotionEffectGraph();
                Check("owned-graph-bind", graphOwner.Sequence.TryBind(new[] { entry }, 3));
                Check("owned-graph-evaluate", graphOwner.TrySample(.75) && Exact(graphImage, Render()));
                graphOwner.Stop(); Check("owned-graph-stop-immediate", graphOwner.Sequence.ActiveCount == 0 && Exact(baseline, Render()));
                Check("owned-graph-stop-recover", graphOwner.TrySample(.75) && Exact(graphImage, Render()));
                Check("owned-graph-invalid-time", !graphOwner.TrySample(double.NaN) && graphOwner.Sequence.ActiveCount == 0 && Exact(baseline, Render()));
                Check("owned-graph-seek-recover", graphOwner.TrySample(.75) && Exact(graphImage, Render()));
                var ownedInstance = graphOwner.Sequence.GetInstance("tear"); graphOwner.Dispose(); graphOwner.Dispose();
                Check("owned-graph-dispose-immediate", !ownedInstance.activeSelf && graphOwner.Sequence.ActiveCount == 0 && Exact(baseline, Render()));
                Check("owned-graph-disposal-terminal", !graphOwner.TrySample(.75) && !graphOwner.Sequence.TryBind(new[] { entry }, 3));
                Check("lost-attachment-bind", owner.TryBind(new[] { entry }, 3) && owner.TrySample(.75));
                UnityEngine.Object.DestroyImmediate(attachment.gameObject); Check("lost-attachment-cleans", !owner.TrySample(.75) && owner.ActiveCount == 0);
                owner.Dispose(); owner.Dispose(); Check("dispose-idempotent-terminal", !owner.TrySample(0) && owner.ActiveCount == 0);
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy(); graphOwner?.Dispose(); owner.Dispose(); second.Dispose(); RenderTexture.active = active;
                for (int i = 0; i < others.Length; i++) if (others[i] != null) others[i].forceRenderingOff = forced[i];
                foreach (var value in _owned) if (value != null) UnityEngine.Object.Destroy(value);
                _owned.Clear();
            }
            yield return null;
        }
    }
}
