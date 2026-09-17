using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class SrpActorCharacterValidation
    {
        // Explicit producer-before-consumer diagnostic. Does not enable the
        // external-reference option in the application or promise mesh collision.
        private void VerifyExternalReferenceCharacter(Report report, Action<int> view, Func<string, Color[]> run)
        {
            var root = app.CharacterRoot;
            var label = typeof(HairDynamicsSystem).GetField("_systemLabel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var swings = root.GetComponentsInChildren<HairDynamicsSystem>(true)
                .OrderBy(s => (string)label.GetValue(s) == "Skirt" ? 0 :
                    (string)label.GetValue(s) == "Garment" ? 1 : 2).ToArray();
            var breasts = root.GetComponentsInChildren<BreastDynamicsSystem>(true);
            var slides = root.GetComponentsInChildren<BodySoftTissueDynamicsSystem>(true);
            var bones = root.GetComponentsInChildren<Transform>(true);
            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var positions = bones.Select(b => b.localPosition).ToArray();
            var rotations = bones.Select(b => b.localRotation).ToArray();
            var oldMatrices = skins.Select(s => s.forceMatrixRecalculationPerRender).ToArray();
            var oldAutomatic = swings.Select(s => s.automaticSimulation).ToArray();
            var oldExternal = swings.Select(s => s.useExternalReferenceLimits).ToArray();
            var oldWind = swings.Select(s => s.naturalWind).ToArray();
            var oldTime = swings.Select(s => s.naturalWindTimeOverride).ToArray();
            var oldDiagnosticWind = swings.Select(s => s.windStrength).ToArray();
            var oldBreasts = breasts.Select(s => s.automaticSimulation).ToArray();
            var oldSlides = slides.Select(s => s.automaticSimulation).ToArray();
            var helpers = new List<MonoBehaviour>();
            helpers.AddRange(root.GetComponentsInChildren<QuartzArmDeformationSystem>(true));
            helpers.AddRange(root.GetComponentsInChildren<QuartzLegAndRotationDeformationSystem>(true));
            helpers.AddRange(root.GetComponentsInChildren<QuartzGarmentDeformationSystem>(true));
            void Check(string name, bool accepted, float value = 0) => report.checks.Add(new Check {
                name = "external-reference-character-" + name, accepted = accepted, value = value });
            void Restore()
            {
                for (int i = 0; i < bones.Length; i++)
                { bones[i].localPosition = positions[i]; bones[i].localRotation = rotations[i]; }
            }
            void Pose(float seconds)
            { app.EvaluateMotion(seconds); foreach (var helper in helpers) helper.SendMessage("LateUpdate"); }
            float[] State() => bones.SelectMany(b => new[] { b.localPosition.x, b.localPosition.y,
                b.localPosition.z, b.localRotation.x, b.localRotation.y, b.localRotation.z, b.localRotation.w }).ToArray();
            float Difference(float[] a, float[] b) => a.Zip(b, (x, y) =>
                Finite(x) && Finite(y) ? Mathf.Abs(x - y) : float.PositiveInfinity).Max();
            var legacy = new List<float[]>(); var enabled = new List<float[]>();
            var legacyImages = new Dictionary<int, Color[]>(); var enabledImages = new Dictionary<int, Color[]>();
            var wind = new NaturalWindSettings { enabled = true, seed = 17,
                steadyForce = new Vector3(.015f, 0, .005f), sineAmplitude = new Vector3(.005f, .001f, .004f),
                randomAmplitude = new Vector3(.004f, .001f, .004f) };
            try
            {
                foreach (var skin in skins) skin.forceMatrixRecalculationPerRender = true;
                foreach (var s in swings)
                { s.automaticSimulation = false; s.naturalWind = wind; s.naturalWindTimeOverride = null; s.windStrength = 0; }
                foreach (var s in breasts) s.automaticSimulation = false;
                foreach (var s in slides) s.automaticSimulation = false;
                Check("separate-producer-and-consumer", swings.Any(s => (string)label.GetValue(s) == "Skirt") &&
                    swings.Any(s => (string)label.GetValue(s) == "Garment"));
                foreach (string mode in new[] { "legacy", "enabled", "replay", "disabled" })
                {
                    bool external = mode == "enabled" || mode == "replay";
                    Restore(); Pose(0);
                    foreach (var s in swings) { s.useExternalReferenceLimits = external; s.ResetSimulation(); }
                    foreach (var s in breasts) s.ResetSimulation();
                    foreach (var s in slides) s.ResetSimulation();
                    float error = 0; int corrections = 0; bool finite = true;
                    for (int frame = 0; frame < 180; frame++)
                    {
                        float seconds = frame * .01667f; Pose(seconds);
                        foreach (var s in swings)
                        { s.AdvanceSimulation(.01667f, seconds); corrections += s.ExternalReferenceLimitCorrections; }
                        foreach (var s in breasts) s.AdvanceSimulation(.01667f);
                        foreach (var s in slides) s.AdvanceSimulation(.01667f);
                        float[] state = State(); finite &= state.All(Finite);
                        if (mode == "legacy") legacy.Add(state);
                        else
                        {
                            error = Mathf.Max(error, Difference(state, mode == "replay" ? enabled[frame] : legacy[frame]));
                            if (mode == "enabled") enabled.Add(state);
                        }
                    }
                    Check(mode + "-180-frames-finite", finite);
                    Check(mode + "-corrections", external ? corrections > 0 : corrections == 0, corrections);
                    if (mode != "legacy") Check(mode + "-all-bones", mode == "enabled" ? error > 1e-5f && Finite(error) : error == 0, error);
                    foreach (int angle in new[] { 0, 90, 180 })
                    {
                        view(angle); var pixels = run("reference-" + mode + "-view-" + angle);
                        if (mode == "legacy") legacyImages[angle] = pixels;
                        else if (mode == "enabled") enabledImages[angle] = pixels;
                        else
                        {
                            float difference = MaximumDifference(mode == "replay" ? enabledImages[angle] : legacyImages[angle], pixels);
                            Check(mode + "-view-" + angle + "-color-exact", difference == 0, difference);
                        }
                    }
                }
                int changed = legacyImages.Sum(pair => Changed(pair.Value, enabledImages[pair.Key], .001f));
                Check("external-limit-visible-skinned-response", changed > 10, changed);
            }
            finally
            {
                Restore();
                for (int i = 0; i < skins.Length; i++) skins[i].forceMatrixRecalculationPerRender = oldMatrices[i];
                for (int i = 0; i < swings.Length; i++)
                {
                    swings[i].automaticSimulation = oldAutomatic[i]; swings[i].useExternalReferenceLimits = oldExternal[i];
                    swings[i].naturalWind = oldWind[i]; swings[i].naturalWindTimeOverride = oldTime[i]; swings[i].windStrength = oldDiagnosticWind[i];
                }
                for (int i = 0; i < breasts.Length; i++) breasts[i].automaticSimulation = oldBreasts[i];
                for (int i = 0; i < slides.Length; i++) slides[i].automaticSimulation = oldSlides[i];
            }
        }

        // Caller-owned character only. Tests deterministic moving geometry and
        // wind response, not equality to unavailable original engine output.
        private void VerifySecondaryCharacter(Report report, Action<int> view, Func<string, Color[]> run)
        {
            var root = app.CharacterRoot;
            var swings = root.GetComponentsInChildren<HairDynamicsSystem>(true);
            var breasts = root.GetComponentsInChildren<BreastDynamicsSystem>(true);
            var slides = root.GetComponentsInChildren<BodySoftTissueDynamicsSystem>(true);
            var bones = root.GetComponentsInChildren<Transform>(true);
            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var oldMatrixRecalculation = skins.Select(s => s.forceMatrixRecalculationPerRender).ToArray();
            var originalPositions = bones.Select(b => b.localPosition).ToArray();
            var originalRotations = bones.Select(b => b.localRotation).ToArray();
            var oldSwingAutomatic = swings.Select(s => s.automaticSimulation).ToArray();
            var oldBreastAutomatic = breasts.Select(s => s.automaticSimulation).ToArray();
            var oldSlideAutomatic = slides.Select(s => s.automaticSimulation).ToArray();
            var oldWind = swings.Select(s => s.naturalWind).ToArray();
            var oldTime = swings.Select(s => s.naturalWindTimeOverride).ToArray();
            var oldDiagnosticWind = swings.Select(s => s.windStrength).ToArray();
            var helpers = new List<MonoBehaviour>();
            helpers.AddRange(root.GetComponentsInChildren<QuartzArmDeformationSystem>(true));
            helpers.AddRange(root.GetComponentsInChildren<QuartzLegAndRotationDeformationSystem>(true));
            helpers.AddRange(root.GetComponentsInChildren<QuartzGarmentDeformationSystem>(true));
            void Check(string name, bool accepted, float value = 0) => report.checks.Add(new Check {
                name = "secondary-character-" + name, accepted = accepted, value = value });
            void RestoreTransforms()
            {
                for (int i = 0; i < bones.Length; i++)
                { bones[i].localPosition = originalPositions[i]; bones[i].localRotation = originalRotations[i]; }
            }
            void Pose(float seconds)
            {
                app.EvaluateMotion(seconds);
                // Preserve the existing 700/710/720 dependency order explicitly;
                // several offline samples execute within one Unity frame.
                foreach (var helper in helpers) helper.SendMessage("LateUpdate");
            }
            float[] State()
            {
                var result = new float[bones.Length * 7];
                for (int i = 0; i < bones.Length; i++)
                {
                    Vector3 p = bones[i].localPosition; Quaternion q = bones[i].localRotation;
                    result[i * 7] = p.x; result[i * 7 + 1] = p.y; result[i * 7 + 2] = p.z;
                    result[i * 7 + 3] = q.x; result[i * 7 + 4] = q.y;
                    result[i * 7 + 5] = q.z; result[i * 7 + 6] = q.w;
                }
                return result;
            }
            float Difference(float[] a, float[] b)
            {
                float result = 0;
                for (int i = 0; i < a.Length; i++)
                { if (!Finite(a[i]) || !Finite(b[i])) return float.PositiveInfinity; result = Mathf.Max(result, Mathf.Abs(a[i] - b[i])); }
                return result;
            }
            try
            {
                // Unity otherwise may reuse a skinned palette across manual
                // draws in one update. Bone motion alone is not a render control.
                foreach (var skin in skins) skin.forceMatrixRecalculationPerRender = true;
                Check("active-swing-nodes", swings.Sum(s => s.SimulatedBoneCount) > 0, swings.Sum(s => s.SimulatedBoneCount));
                Check("active-slide-nodes", slides.Sum(s => s.SimulatedNodeCount) > 0, slides.Sum(s => s.SimulatedNodeCount));
                Check("active-bilateral-nodes", breasts.Sum(s => s.DrivenBoneCount) > 0, breasts.Sum(s => s.DrivenBoneCount));
                foreach (var s in swings) { s.automaticSimulation = false; s.naturalWindTimeOverride = null; s.windStrength = 0; }
                foreach (var s in breasts) s.automaticSimulation = false;
                foreach (var s in slides) s.automaticSimulation = false;
                var wind = new NaturalWindSettings { enabled = true, seed = 17,
                    steadyForce = new Vector3(.015f, 0, .005f),
                    sineAmplitude = new Vector3(.005f, .001f, .004f),
                    randomAmplitude = new Vector3(.004f, .001f, .004f) };
                var snapshots = new List<float[]>();
                var referenceImages = new Dictionary<int, Color[]>();
                var noWindImages = new Dictionary<int, Color[]>();
                float replayError = 0, windDifference = 0;
                foreach (string mode in new[] { "wind", "no-wind", "replay" })
                {
                    RestoreTransforms(); Pose(0);
                    foreach (var s in swings) { s.naturalWind = mode == "no-wind" ? null : wind; s.ResetSimulation(); }
                    foreach (var s in breasts) s.ResetSimulation();
                    foreach (var s in slides) s.ResetSimulation();
                    bool finite = true;
                    for (int frame = 0; frame < 180; frame++)
                    {
                        float seconds = frame * .01667f;
                        Pose(seconds);
                        foreach (var s in swings) s.AdvanceSimulation(.01667f, seconds);
                        foreach (var s in breasts) s.AdvanceSimulation(.01667f);
                        foreach (var s in slides) s.AdvanceSimulation(.01667f);
                        float[] state = State(); finite &= state.All(Finite);
                        if (mode == "wind") snapshots.Add(state);
                        else if (mode == "replay") replayError = Mathf.Max(replayError, Difference(state, snapshots[frame]));
                        else windDifference = Mathf.Max(windDifference, Difference(state, snapshots[frame]));
                    }
                    Check(mode + "-three-seconds-finite", finite);
                    foreach (int angle in new[] { 0, 90, 180 })
                    {
                        view(angle); var pixels = run("secondary-" + mode + "-view-" + angle);
                        if (mode == "wind") referenceImages[angle] = pixels;
                        else if (mode == "no-wind") noWindImages[angle] = pixels;
                        else Check("view-" + angle + "-reset-replay-color-exact",
                            MaximumDifference(referenceImages[angle], pixels) == 0, MaximumDifference(referenceImages[angle], pixels));
                    }
                }
                Check("all-bones-all-180-frames-reset-replay-exact", replayError == 0, replayError);
                Check("natural-wind-moves-authored-bones", windDifference > 1e-5f && Finite(windDifference), windDifference);
                foreach (int angle in new[] { 0, 90, 180 })
                {
                    int changed = Changed(referenceImages[angle], noWindImages[angle], .001f);
                    Check("view-" + angle + "-wind-visible-positive-control", changed > 10, changed);
                }
            }
            finally
            {
                RestoreTransforms();
                for (int i = 0; i < skins.Length; i++) skins[i].forceMatrixRecalculationPerRender = oldMatrixRecalculation[i];
                for (int i = 0; i < swings.Length; i++)
                {
                    swings[i].automaticSimulation = oldSwingAutomatic[i]; swings[i].naturalWind = oldWind[i];
                    swings[i].naturalWindTimeOverride = oldTime[i]; swings[i].windStrength = oldDiagnosticWind[i];
                }
                for (int i = 0; i < breasts.Length; i++) breasts[i].automaticSimulation = oldBreastAutomatic[i];
                for (int i = 0; i < slides.Length; i++) slides[i].automaticSimulation = oldSlideAutomatic[i];
            }
        }
    }
}
