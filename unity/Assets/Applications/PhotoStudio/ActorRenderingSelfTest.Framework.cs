using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private static void FrameworkCheck(Report report, string name, bool accepted, float error = 0f)
        {
            report.checks.Add(new Check { name = name, accepted = accepted, maximumDifference = error });
        }

        private void VerifyActorVertexEncoding(Report report)
        {
            bool roundtrip = true, shaderDecode = true;
            for (int i = 0; i < 256; i++)
            {
                var packed = new Color32((byte)i, (byte)(255 - i), (byte)((i * 17) & 255), (byte)((i * 37) & 255));
                var fields = ActorVertexEncoding.Unpack(packed);
                roundtrip &= ActorVertexEncoding.Pack(fields).Equals(packed);
                // Independent arithmetic used by ActorSurface's normalized
                // COLOR input; exercise every byte without source assets.
                int high = Mathf.FloorToInt((i / 255f) * 15.9375f + 0.03125f);
                int low = i - high * 16;
                shaderDecode &= high == fields.outlineRed && low == fields.outlineGreen;
            }
            FrameworkCheck(report, "vertex-pack-all-256-byte-values-roundtrip", roundtrip);
            FrameworkCheck(report, "vertex-pack-surface-decoder-compatible", shaderDecode);
            var known = ActorVertexEncoding.Pack(new ActorVertexEncoding.Channels {
                outlineRed = 1, outlineGreen = 2, outlineBlue = 3, materialRamp = 4,
                outlineDepth = 5, outlineWidth = 6, rimMask = 7, reserved = 8 });
            FrameworkCheck(report, "vertex-pack-known-field-order", known.Equals(new Color32(0x12, 0x34, 0x56, 0x78)));
            foreach (int bad in new[] { -1, 16, int.MinValue, int.MaxValue })
            {
                bool rejected = false;
                try { ActorVertexEncoding.Pack(new ActorVertexEncoding.Channels { outlineDepth = bad }); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                FrameworkCheck(report, "vertex-pack-rejects-overflow-" + bad, rejected);
            }
            var vectors = new[] { Vector3.zero, new Vector3(2, -3, 0.5f), Vector3.one * 1e-7f };
            Vector4[] tangents = ActorVertexEncoding.OutlineTangents(vectors, -1f);
            bool lengths = true;
            for (int i = 0; i < vectors.Length; i++) lengths &= ((Vector3)tangents[i]).Equals(vectors[i]) && tangents[i].w == -1f;
            FrameworkCheck(report, "outline-authoring-preserves-vector-length-and-zero", lengths);
            bool invalid = false;
            try { ActorVertexEncoding.OutlineTangents(new[] { new Vector3(float.NaN, 0, 0) }); }
            catch (ArgumentException) { invalid = true; }
            FrameworkCheck(report, "outline-authoring-rejects-nonfinite", invalid);
        }

        private void VerifyNaturalWind(Report report)
        {
            var wind = new NaturalWindSettings();
            FrameworkCheck(report, "wind-default-noop", wind.Sample(1.25).Equals(Vector3.zero));
            wind.enabled = true;
            wind.useGustEnvelope = false;
            wind.sineAmplitude = Vector3.zero;
            wind.randomAmplitude = Vector3.zero;
            wind.steadyForce = new Vector3(0.03f, -0.01f, 0.02f);
            foreach (double time in new[] { -17.25, 0.0, 1.25, 1e10 })
                FrameworkCheck(report, "wind-steady-" + time, wind.Sample(time).Equals(wind.steadyForce));
            wind.steadyForce = Vector3.zero;
            wind.sineAmplitude = Vector3.right;
            wind.sineFrequency = 1f;
            for (int i = 0; i < 9; i++)
            {
                double t = i * 0.125;
                float error = (wind.Sample(t) - Vector3.right * (float)Math.Sin(t * Math.PI * 2)).magnitude;
                FrameworkCheck(report, "wind-sine-" + i, error < 1e-6f, error);
            }
            wind.sineAmplitude = Vector3.zero;
            wind.randomAmplitude = new Vector3(0.03f, 0.01f, 0.02f);
            wind.randomFrequency = 0.7f;
            UnityEngine.Random.State randomState = UnityEngine.Random.state;
            string randomBefore = JsonUtility.ToJson(randomState);
            var samples = new Vector3[101];
            bool bounded = true, changing = false;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = wind.Sample((i - 50) * 0.071);
                bounded &= Mathf.Abs(samples[i].x) <= 0.03f && Mathf.Abs(samples[i].y) <= 0.01f && Mathf.Abs(samples[i].z) <= 0.02f;
                if (i > 0) changing |= !samples[i].Equals(samples[0]);
            }
            bool reversible = true;
            for (int i = 100; i >= 0; i--) reversible &= wind.Sample((i - 50) * 0.071).Equals(samples[i]);
            FrameworkCheck(report, "wind-seek-order-independent", reversible);
            FrameworkCheck(report, "wind-random-bounded-and-nonconstant", bounded && changing);
            FrameworkCheck(report, "wind-does-not-touch-unity-rng", randomBefore == JsonUtility.ToJson(UnityEngine.Random.state));
            wind.seed++;
            FrameworkCheck(report, "wind-seed-changes-force", !wind.Sample(0).Equals(samples[50]));

            wind = new NaturalWindSettings { enabled = true, gustStrengthVariation = 0f, envelopeVariation = 0f };
            foreach (double t in new[] { 0.0, 4.0, 5.0, 6.9, 7.0, -1.0 })
                FrameworkCheck(report, "wind-calm-" + t, wind.Envelope(t) == 0f);
            foreach (double t in new[] { 1.0, 2.0, 3.0, 8.0 })
                FrameworkCheck(report, "wind-gust-" + t, wind.Envelope(t) == 1f);
            FrameworkCheck(report, "wind-fade-independent-midpoint", Mathf.Abs(wind.Envelope(0.5) - 0.5f) < 1e-6f);
            wind.gustStrengthVariation = 0.3f;
            wind.envelopeVariation = 0.2f;
            foreach (double boundary in new[] { -7.0, 0.0, 4.0, 7.0, 11.0 })
            {
                float delta = (wind.Sample(boundary - 1e-5) - wind.Sample(boundary + 1e-5)).magnitude;
                FrameworkCheck(report, "wind-continuous-boundary-" + boundary, delta < 1e-6f, delta);
            }
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e13 })
                FrameworkCheck(report, "wind-invalid-time-" + invalid, wind.Sample(invalid).Equals(Vector3.zero));
            wind.gustSeconds = 0f;
            FrameworkCheck(report, "wind-invalid-duration", !wind.IsValid && wind.Sample(1).Equals(Vector3.zero));
            wind.gustSeconds = float.Epsilon;
            wind.calmSeconds = 0f;
            FrameworkCheck(report, "wind-extreme-cycle-safe", wind.Sample(1e12).Equals(Vector3.zero));
            wind.gustSeconds = 4f;
            wind.steadyForce = new Vector3(float.NaN, 0, 0);
            FrameworkCheck(report, "wind-invalid-vector", !wind.IsValid && wind.Sample(1).Equals(Vector3.zero));

            foreach (float coefficient in new[] { 0f, 0.5f, 1f })
            foreach (bool global in new[] { false, true })
            {
                var rig = Own(new GameObject("Generated wind chain"));
                Transform parent = rig.transform;
                for (int i = 0; i < 3; i++)
                {
                    var bone = new GameObject("Wind link " + i);
                    bone.transform.SetParent(parent, false);
                    bone.transform.localPosition = new Vector3(0, -0.1f, 0);
                    var setting = bone.AddComponent<ActorAnimation.ActorSwingDynamicBone>();
                    setting.mass = setting.stiffness = setting.spring = setting.pendulum = 0f;
                    setting.wind = coefficient;
                    setting.useWindGlobalForce = global;
                    parent = bone.transform;
                }
                var dynamics = rig.AddComponent<HairDynamicsSystem>();
                dynamics.gravityStrength = dynamics.collisionStrength = 0f;
                dynamics.Initialize(rig.transform, rig.transform);
                dynamics.enabled = false;
                dynamics.naturalWind = new NaturalWindSettings { enabled = true, useGustEnvelope = false,
                    steadyForce = new Vector3(0.03f, 0, 0), sineAmplitude = Vector3.zero, randomAmplitude = Vector3.zero };
                dynamics.naturalWindTimeOverride = 1.0;
                MethodInfo step = typeof(HairDynamicsSystem).GetMethod("SimulateStep", BindingFlags.Instance | BindingFlags.NonPublic);
                var nodes = (IEnumerable)typeof(HairDynamicsSystem).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dynamics);
                object root = null;
                foreach (object node in nodes) if (DynamicField(node, "parent") == null) { root = node; break; }
                step.Invoke(dynamics, new object[] { 0.01667f, true, false, false });
                Vector3 velocity = (Vector3)DynamicField(root, "childSpeed");
                Vector3 expected = global ? dynamics.naturalWind.steadyForce * coefficient * (0.01667f * 40f) : Vector3.zero;
                float error = (velocity - expected).magnitude;
                FrameworkCheck(report, "wind-solver-child-coefficient-" + coefficient + "-global-" + global, error < 1e-6f, error);
                dynamics.naturalWind = null;
                dynamics.SendMessage("ResetSimulation");
                step.Invoke(dynamics, new object[] { 0.01667f, true, false, false });
                FrameworkCheck(report, "wind-detach-restores-noop-" + coefficient + "-global-" + global,
                    ((Vector3)DynamicField(root, "childSpeed")).sqrMagnitude < 1e-12f);
            }
        }
    }
}
