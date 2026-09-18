using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class SrpActorCharacterValidation
    {
        // A local continuity probe, not an original-game pose/collision oracle.
        // Keep the actual clip's compound bend while crossing the legacy gate
        // by just .04 degrees. A unit-gain helper must not jump tens of degrees.
        private void VerifySkirtHelperBoundary(Report report, Action<int> view, Func<string, Color[]> run)
        {
            var root = app.CharacterRoot;
            var bones = root.GetComponentsInChildren<Transform>(true);
            var positions = bones.Select(b => b.localPosition).ToArray();
            var rotations = bones.Select(b => b.localRotation).ToArray();
            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var matrixFlags = skins.Select(s => s.forceMatrixRecalculationPerRender).ToArray();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var driversField = typeof(HairDynamicsSystem).GetField("_quartzSkirtDrivers", flags);
            var apply = typeof(HairDynamicsSystem).GetMethod("ApplyQuartzDrivers", flags);
            var toEuler = typeof(HairDynamicsSystem).GetMethod("SignedEuler",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var solvers = root.GetComponentsInChildren<HairDynamicsSystem>(true)
                .Where(s => ((System.Collections.ICollection)driversField.GetValue(s)).Count > 0).ToArray();
            var drivers = solvers.SelectMany(s => ((System.Collections.IEnumerable)driversField.GetValue(s)).Cast<object>()).ToArray();
            bool authored = Environment.GetCommandLineArgs().Contains("--validate-authored-skirt-helpers");
            var oldAuthored = solvers.Select(s => s.useAuthoredSkirtHelpers).ToArray();
            if (drivers.Length == 0) throw new InvalidOperationException("No authored skirt helper to probe.");
            object Field(object value, string name) => value.GetType().GetField(name).GetValue(value);
            var left = drivers.First(d => ((Transform)Field(d, "referenceBone")).name.StartsWith("Left", StringComparison.Ordinal));
            var reference = (Transform)Field(left, "referenceBone");
            var rest = (Quaternion)Field(left, "referenceRestLocalRotation");
            var targets = drivers.Select(d => (Transform)Field(d, "transform")).ToArray();
            void Check(string name, bool accepted, float value = 0) => report.checks.Add(new Check {
                name = "skirt-boundary-" + name, accepted = accepted, value = value });
            float Angle(Quaternion a, Quaternion b)
            {
                Quaternion q = Quaternion.Inverse(a) * b;
                return 2f * Mathf.Atan2(new Vector3(q.x, q.y, q.z).magnitude, Mathf.Abs(q.w)) * Mathf.Rad2Deg;
            }
            void Restore()
            { for (int i = 0; i < bones.Length; i++) { bones[i].localPosition = positions[i]; bones[i].localRotation = rotations[i]; } }
            var pixels = new Dictionary<int, Color[]>();
            Quaternion[] negativeTargets = null; Quaternion negativeInput = Quaternion.identity;
            try
            {
                foreach (var solver in solvers) solver.useAuthoredSkirtHelpers = authored;
                foreach (var skin in skins) skin.forceMatrixRecalculationPerRender = true;
                if (authored)
                {
                    Check("explicit-assigned-reference-matches-counterexample", drivers.All(d =>
                        (Transform)Field(d, "authoredReferenceBone") == (Transform)Field(d, "referenceBone")));
                    // Positive control: a continuous output must not be obtained
                    // merely by suppressing every helper, including large bends.
                    float response = 0;
                    foreach (var axis in new[] { Vector3.right, Vector3.forward })
                    foreach (float degrees in new[] { -120f, 120f })
                    {
                        Restore(); reference.localRotation = Quaternion.AngleAxis(degrees, axis) * rest;
                        foreach (var solver in solvers) apply.Invoke(solver, null);
                        response = Mathf.Max(response, targets.Max(t => Quaternion.Angle(Quaternion.identity, t.localRotation)));
                    }
                    Check("large-bend-nonzero-positive-control", response > 5f && Finite(response), response);
                }
                Restore(); app.EvaluateMotion(.7f);
                var delta = (Vector3)toEuler.Invoke(null, new object[] { Quaternion.Inverse(rest) * reference.localRotation });
                Check("authored-helper-count", drivers.Length > 0, drivers.Length);
                Check("clip-compound-bend-y-observation", Finite(delta.y), delta.y);
                Check("clip-compound-bend-z-observation", Finite(delta.z), delta.z);
                foreach (bool positive in new[] { false, true })
                {
                    Restore(); app.EvaluateMotion(.7f);
                    reference.localRotation = rest * Quaternion.Euler(positive ? .02f : -.02f, delta.y, delta.z);
                    foreach (var solver in solvers) apply.Invoke(solver, null);
                    if (!positive) { negativeInput = reference.localRotation; negativeTargets = targets.Select(t => t.localRotation).ToArray(); }
                    else
                    {
                        Quaternion inputDelta = Quaternion.Inverse(negativeInput) * reference.localRotation;
                        float input = 2f * Mathf.Atan2(new Vector3(inputDelta.x, inputDelta.y, inputDelta.z).magnitude,
                            Mathf.Abs(inputDelta.w)) * Mathf.Rad2Deg;
                        float output = targets.Select((t, i) => Angle(negativeTargets[i], t.localRotation)).Max();
                        Check("input-is-nonzero-four-hundredths-degree", input > .03f && input < .05f, input);
                        Check("small-input-continuous-helper-output", output < 1f, output);
                        for (int i = 0; i < targets.Length; i++)
                            Check("helper-" + i + "-angle-jump-observation", true, Angle(negativeTargets[i], targets[i].localRotation));
                    }
                    foreach (int angle in new[] { 0, 90, 180 })
                    {
                        view(angle); var image = run("skirt-boundary-" + (positive ? "positive" : "negative") + "-view-" + angle);
                        if (!positive) pixels[angle] = image;
                        else Check("view-" + angle + "-changed-pixels-observation", true, Changed(pixels[angle], image, .001f));
                    }
                }
            }
            finally
            {
                Restore();
                for (int i = 0; i < solvers.Length; i++) solvers[i].useAuthoredSkirtHelpers = oldAuthored[i];
                for (int i = 0; i < skins.Length; i++) skins[i].forceMatrixRecalculationPerRender = matrixFlags[i];
            }
        }

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
            var oldSkirt = swings.Select(s => s.useAuthoredSkirtHelpers).ToArray();
            bool authoredSkirt = Environment.GetCommandLineArgs().Contains("--validate-authored-skirt-helpers");
            bool authoredSlide = Environment.GetCommandLineArgs().Contains("--validate-authored-garment-slides");
            var oldGarmentSlides = swings.Select(s => s.useAuthoredSlideDynamics).ToArray();
            var nodeField = typeof(HairDynamicsSystem).GetField("_nodes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object Field(object value, string name) => value.GetType().GetField(name).GetValue(value);
            var garmentSlideNodes = swings.Where(s => (string)label.GetValue(s) == "Garment")
                .SelectMany(s => ((System.Collections.IEnumerable)nodeField.GetValue(s)).Cast<object>())
                .Where(n => Field(n, "parent") != null && ((ActorAnimation.ActorSwingDynamicBone)Field(n, "setting")).dynamicType == 1).ToArray();
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
                name = (authoredSlide ? "garment-slide-character-" : authoredSkirt ? "authored-skirt-character-" : "external-reference-character-") + name, accepted = accepted, value = value });
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
                if (authoredSlide) Check("actual-positional-entries-owned", garmentSlideNodes.Length >= 2, garmentSlideNodes.Length);
                foreach (string mode in new[] { "legacy", "enabled", "replay", "disabled" })
                {
                    bool external = mode == "enabled" || mode == "replay";
                    Restore(); Pose(0);
                    foreach (var s in swings)
                    {
                        // Isolate the new helper while retaining the previously
                        // accepted external constraints in every comparison arm.
                        s.useExternalReferenceLimits = authoredSlide || authoredSkirt || external;
                        s.useAuthoredSkirtHelpers = authoredSlide || (authoredSkirt && external);
                        s.useAuthoredSlideDynamics = authoredSlide && external && (string)label.GetValue(s) == "Garment";
                        s.ResetSimulation();
                    }
                    foreach (var s in breasts) s.ResetSimulation();
                    foreach (var s in slides) s.ResetSimulation();
                    float error = 0; int corrections = 0; bool finite = true;
                    float slideResponse = 0, slideLimitError = 0;
                    for (int frame = 0; frame < 180; frame++)
                    {
                        float seconds = frame * .01667f; Pose(seconds);
                        foreach (var s in swings)
                        { s.AdvanceSimulation(.01667f, seconds); corrections += s.ExternalReferenceLimitCorrections; }
                        foreach (var s in breasts) s.AdvanceSimulation(.01667f);
                        foreach (var s in slides) s.AdvanceSimulation(.01667f);
                        float[] state = State(); finite &= state.All(Finite);
                        if (authoredSlide && external)
                            foreach (var node in garmentSlideNodes)
                            {
                                var bone = (Transform)Field(node, "bone");
                                Vector3 offset = bone.localPosition - (Vector3)Field(node, "restLocalPosition");
                                slideResponse = Mathf.Max(slideResponse, offset.magnitude);
                                var limit = ((ActorAnimation.ActorSwingDynamicBone)Field(node, "setting")).limitInfo;
                                if (limit == null || limit.useLimit == 0) continue;
                                var ranges = new[] { limit.axisX, limit.axisY, limit.axisZ };
                                for (int axis = 0; axis < 3; axis++)
                                    slideLimitError = Mathf.Max(slideLimitError,
                                        ranges[axis].x * .001f - offset[axis], offset[axis] - ranges[axis].y * .001f);
                            }
                        if (mode == "legacy") legacy.Add(state);
                        else
                        {
                            error = Mathf.Max(error, Difference(state, mode == "replay" ? enabled[frame] : legacy[frame]));
                            if (mode == "enabled") enabled.Add(state);
                        }
                    }
                    Check(mode + "-180-frames-finite", finite);
                    Check(mode + "-corrections", authoredSlide ? corrections >= 0 : authoredSkirt || external ? corrections > 0 : corrections == 0, corrections);
                    if (authoredSlide && external)
                    {
                        Check(mode + "-nonzero-local-translation", slideResponse > 1e-5f && Finite(slideResponse), slideResponse);
                        Check(mode + "-parent-frame-mm-bounds", slideLimitError < 2e-5f && Finite(slideLimitError), slideLimitError);
                    }
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
                Check(authoredSlide ? "slide-visible-skinned-response" : authoredSkirt ? "helper-visible-skinned-response" : "external-limit-visible-skinned-response", changed > 10, changed);
            }
            finally
            {
                Restore();
                for (int i = 0; i < skins.Length; i++) skins[i].forceMatrixRecalculationPerRender = oldMatrices[i];
                for (int i = 0; i < swings.Length; i++)
                {
                    swings[i].automaticSimulation = oldAutomatic[i]; swings[i].useExternalReferenceLimits = oldExternal[i];
                    swings[i].useAuthoredSkirtHelpers = oldSkirt[i];
                    swings[i].useAuthoredSlideDynamics = oldGarmentSlides[i];
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
