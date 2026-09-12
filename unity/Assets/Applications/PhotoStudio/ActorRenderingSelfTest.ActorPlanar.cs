using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifyActorPlanarCapture(Report report)
        {
            const int width = 97, height = 73;
            var host = Own(new GameObject("Character planar test camera")); var camera = host.AddComponent<Camera>();
            camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false;
            camera.renderingPath = RenderingPath.Forward; camera.orthographic = true; camera.orthographicSize = 1;
            camera.nearClipPlane = .05f; camera.farClipPlane = 15; camera.aspect = (float)width / height;
            camera.transform.position = new Vector3(0, 0, -1); camera.transform.rotation = Quaternion.Euler(0, 180, 0);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear; camera.cullingMask = 1 << 26;
            var target = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
            var mirror = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); mirror.layer = 26;
            mirror.transform.position = new Vector3(0, 0, -2); mirror.transform.rotation = Quaternion.Euler(0, 180, 0); mirror.transform.localScale = new Vector3(5, 5, 1);
            mirror.GetComponent<Renderer>().sharedMaterial = Own(new Material(Resources.Load<Shader>("StudioAccent")));
            var actor = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); actor.layer = 27; actor.transform.localScale = new Vector3(1.5f, 1.5f, 1);
            var source = Own(new Material(Resources.Load<Shader>("PhotoModeFallback")));
            source.SetFloat("_StencilComp", 8); source.SetFloat("_StencilPass", 0); source.SetFloat("_StencilWriteMask", 0);
            source.SetFloat("_VertexColor", 0); source.SetFloat("_DisableDefMap", 1); source.SetVector("_DefValue", new Vector4(.5f, 0, 0, 0));
            source.SetTexture("_MainTex", ResolveTexture(new[] { new Color(.8f, .2f, .1f, .25f) }, 1, 1));
            source.SetTexture("_RampTex", Texture2D.whiteTexture); source.SetTexture("_ShadeTex", Texture2D.whiteTexture);
            source.SetTexture("_RampAddTex", Texture2D.blackTexture); source.SetShaderPassEnabled("ActorHairCover", false);
            actor.GetComponent<Renderer>().sharedMaterial = source;
            var planar = host.AddComponent<PlanarReflection>(); planar.reflectionsEnabled = true; planar.resolutionScale = 1;
            planar.planePoint = mirror.transform.position; planar.planeNormal = Vector3.forward; planar.reflectedLayers = 1 << 27;
            planar.maximumRoughnessMip = 0;
            planar.receivers = new[] { new PlanarReflection.Receiver { surface = new SceneDepthData.Surface { renderer = mirror.GetComponent<Renderer>() } } };
            var lighting = new ActorPlanarLighting { ambientColor = Vector3.zero, lightColor = Vector3.one, lightDirection = Vector3.back };
            using (var set = new ActorPlanarCaptureSet())
            {
                Color[] Render()
                {
                    bool ok = set.TryRefresh(new[] { actor.GetComponent<Renderer>() }, camera.worldToCameraMatrix * PlanarReflection.ReflectionMatrix(planar.planePoint, planar.planeNormal), lighting, out var error);
                    if (!ok) throw new InvalidOperationException(error);
                    planar.reflectedSurfaces = set.Draws; camera.Render();
                    return ReadSceneTarget(PlanarField<RenderTexture>(planar, "_capture"));
                }
                Color Center(Color[] p) => p[(height / 2) * width + width / 2];
                void Check(string name, bool ok, float value = 0) => FrameworkCheck(report, "actor-planar-" + name, ok, value);
                Color expected = new Color(.8f, .2f, .1f, 1);
                foreach (int type in new[] { 0, 1, 2, 3, 4, 5, 6, 8, 9 })
                {
                    source.SetFloat("_ShaderType", type); source.SetFloat("_SrcBlend", type == 2 ? 5 : 1);
                    source.SetFloat("_DstBlend", type == 2 || type == 4 ? 10 : type == 5 ? 1 : 0);
                    source.SetFloat("_ZWrite", type == 2 || type == 4 || type == 5 ? 0 : 1);
                    Color actual = Center(Render()); float alpha = type == 2 || type == 4 ? .25f : 1;
                    Color oracle = new Color(expected.r * alpha, expected.g * alpha, expected.b * alpha, alpha);
                    float error = PixelError(new[] { oracle }, new[] { actual });
                    Check("type-" + type + "-color-and-independent-coverage", error < .001f, error);
                }
                source.SetFloat("_ShaderType", 0); source.SetFloat("_SrcBlend", 1); source.SetFloat("_DstBlend", 0); source.SetFloat("_ZWrite", 1);
                Color[] baseline = Render(); var firstMaterial = set.Draws[0].material;
                Render(); Check("reuses-owned-material-and-leaves-source", firstMaterial == set.Draws[0].material && firstMaterial != source && actor.GetComponent<Renderer>().sharedMaterial == source && set.MaterialCount == 1);
                source.SetVector("_Color", new Vector4(2, .5f, 0, 1));
                var tinted = Center(Render()); Check("live-literal-vector-tint", Mathf.Abs(tinted.r - 1.6f) < .002f && Mathf.Abs(tinted.g - .1f) < .001f && tinted.b == 0);
                source.SetVector("_Color", Vector4.one);
                var block = new MaterialPropertyBlock(); block.SetVector("_Color", new Vector4(0, 1, 0, 1)); actor.GetComponent<Renderer>().SetPropertyBlock(block);
                var overridden = Center(Render()); Check("renderer-property-block-snapshot", overridden.r == 0 && overridden.g > .19f && overridden.b == 0);
                var perMaterial = new MaterialPropertyBlock(); perMaterial.SetVector("_ActorTextureFrame", Vector4.zero); actor.GetComponent<Renderer>().SetPropertyBlock(perMaterial, 0);
                Check("per-material-block-replaces-renderer-block", PixelError(new[] { Center(Render()) }, new[] { Center(baseline) }) == 0);
                actor.GetComponent<Renderer>().SetPropertyBlock(null, 0); actor.GetComponent<Renderer>().SetPropertyBlock(null);
                source.SetFloat("_UseAlphaClip", 1); source.SetFloat("_Cutoff", .5f);
                Check("cutout-removes-color-and-mask", SsrHitCount(Render()) == 0);
                source.SetFloat("_Cutoff", .1f); Check("cutout-negative-control-recovers", SsrHitCount(Render()) > 500);
                source.SetFloat("_UseAlphaClip", 0);
                source.SetTexture("_MainTex", ResolveTexture(new[] { Color.red, Color.green }, 2, 1));
                source.SetVector("_ActorTextureFrame", new Vector4(.01f, 1, .1f, 0));
                Check("atlas-first-frame", Center(Render()).r > .99f && Center(Render()).g == 0);
                source.SetVector("_ActorTextureFrame", new Vector4(.01f, 1, .8f, 0));
                Check("atlas-second-frame", Center(Render()).g > .99f && Center(Render()).r == 0);
                source.SetVector("_ActorTextureFrame", Vector4.zero);
                source.SetTexture("_MainTex", ResolveTexture(new[] { expected }, 1, 1));
                source.SetTexture("_LayerTex", ResolveTexture(new[] { new Color(.1f, .7f, .3f, 1), new Color(.5f, 0, 0, 0) }, 2, 1));
                source.SetFloat("_EnableLayer", 1); source.SetFloat("_LayerWeight", 1);
                var layer = Center(Render()); Check("uv2-layer-changes-color-and-definition", Mathf.Abs(layer.g - .7f) < .001f && Mathf.Abs(layer.r - .1f) < .001f);
                source.SetFloat("_LayerWeight", 0); Check("layer-zero-restores", PixelError(new[] { Center(Render()) }, new[] { expected }) < .001f);
                source.SetFloat("_EnableLayer", 0);
                source.SetTexture("_RampTex", ResolveTexture(new[] { new Color(.1f, .2f, .3f, 1), Color.white }, 2, 1));
                lighting.lightDirection = Vector3.forward; float dark = Center(Render()).r;
                lighting.lightDirection = Vector3.back; float bright = Center(Render()).r;
                Check("explicit-light-and-ramp-positive-control", Mathf.Abs(bright - dark) > .1f);
                source.SetTexture("_RampTex", Texture2D.whiteTexture);
                source.SetFloat("_UseEmission", 1); source.SetTexture("_EmissionMap", Texture2D.whiteTexture); source.SetColor("_EmissionColor", new Color(.5f, .25f, 0, 1));
                var emissive = Center(Render()); Color linear = QualitySettings.activeColorSpace == ColorSpace.Linear ? new Color(.5f, .25f, 0).linear : new Color(.5f, .25f, 0);
                Check("emission-color-space-once", Mathf.Abs(emissive.r - expected.r - linear.r) < .002f && Mathf.Abs(emissive.g - expected.g - linear.g) < .002f);
                block.Clear(); block.SetColor("_EmissionColor", new Color(.5f, .25f, 0, 1)); actor.GetComponent<Renderer>().SetPropertyBlock(block);
                var blockColor = Center(Render());
                Check("emission-block-color-space-once", PixelError(new[] { emissive }, new[] { blockColor }) < .002f);
                block.SetVector("_EmissionColor", new Vector4(.5f, .25f, 0, 1)); actor.GetComponent<Renderer>().SetPropertyBlock(block);
                var blockVector = Center(Render());
                Check("emission-block-retains-color-semantics-on-reuse", PixelError(new[] { emissive }, new[] { blockVector }) < .002f);
                // SetVector after SetColor retains Unity's color flag. Clear it
                // to test a genuinely literal vector, rather than assume a cast.
                block.Clear(); block.SetVector("_EmissionColor", new Vector4(.5f, .25f, 0, 1)); actor.GetComponent<Renderer>().SetPropertyBlock(block);
                blockVector = Center(Render());
                Check("emission-block-linear-vector-preserved", Mathf.Abs(blockVector.r - expected.r - .5f) < .002f && Mathf.Abs(blockVector.g - expected.g - .25f) < .002f);
                actor.GetComponent<Renderer>().SetPropertyBlock(null);
                source.SetFloat("_UseEmission", 0);
                var globals = Shader.GetGlobalVector("_CapturedLightDirection"); Shader.SetGlobalVector("_CapturedLightDirection", new Vector4(100, 0, 0, 0));
                var unchanged = Center(Render()); Shader.SetGlobalVector("_CapturedLightDirection", globals);
                Check("independent-of-main-camera-global-light", PixelError(new[] { unchanged }, new[] { expected }) < .001f);
                source.SetVector("_WardrobeScaleCorrection", new Vector4(.5f, 1, 1, 0)); int small = SsrHitCount(Render());
                source.SetVector("_WardrobeScaleCorrection", new Vector4(1, 1, 1, 0)); int normal = SsrHitCount(Render());
                Check("vertex-scale-color-coverage-consistency", small > 300 && normal > small * 1.7f);
                Color[] beforeFade = Render();
                source.SetVector("_ActorColor", new Vector4(1, 1, 1, .5f)); int faded = SsrHitCount(Render());
                Check("actor-fade-color-and-mask-partial", faded > normal * .4f && faded < normal * .6f);
                source.SetVector("_ActorColor", new Vector4(1, 1, 1, 0));
                Check("actor-fade-zero-removes-color-and-mask", ScenePixelsEqual(Render(), new Color[width * height]));
                source.SetVector("_ActorColor", Vector4.one);
                Check("actor-fade-restores", ScenePixelsEqual(beforeFade, Render()));
                source.SetVector("_Color", new Vector4(float.NaN, 1, 1, 1));
                Check("nonfinite-source-fails-closed", !set.TryRefresh(new[] { actor.GetComponent<Renderer>() }, Matrix4x4.identity, lighting, out _) && set.Draws.Length == 0 && set.MaterialCount == 0);
                source.SetVector("_Color", Vector4.one); Render();
                source.SetFloat("_ShaderType", 7);
                Check("unknown-type-fails-closed", !set.TryRefresh(new[] { actor.GetComponent<Renderer>() }, Matrix4x4.identity, lighting, out _) && set.Draws.Length == 0);
                source.SetFloat("_ShaderType", 0); Render();
                source.SetFloat("_ColorMask", 0);
                Check("unsupported-color-mask-fails-closed", !set.TryRefresh(new[] { actor.GetComponent<Renderer>() }, Matrix4x4.identity, lighting, out _) && set.Draws.Length == 0);
                source.SetFloat("_ColorMask", 15); Render();
                actor.SetActive(false); Check("removed-actor-releases-owned-materials", set.TryRefresh(new[] { actor.GetComponent<Renderer>() }, Matrix4x4.identity, lighting, out _) && set.MaterialCount == 0 && set.Draws.Length == 0);
                actor.SetActive(true); Render();
                VerifyActorPlanarStencil(report, set, source, actor, planar, camera, lighting, width, height);
                set.Dispose(); Check("disposed-set-rejects-refresh", !set.TryRefresh(new[] { actor.GetComponent<Renderer>() }, Matrix4x4.identity, lighting, out _) && set.MaterialCount == 0);
            }
            planar.enabled = false; camera.targetTexture = null; target.Release();
            host.SetActive(false); actor.SetActive(false); mirror.SetActive(false);
        }

        private void VerifyActorPlanarStencil(Report report, ActorPlanarCaptureSet set, Material source, GameObject actor,
            PlanarReflection planar, Camera camera, ActorPlanarLighting lighting, int width, int height)
        {
            var eye = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); eye.layer = 27; eye.transform.position = new Vector3(0, 0, -.2f); eye.transform.localScale = new Vector3(2.2f, .7f, 1);
            var eyeMaterial = Own(new Material(source)); eyeMaterial.SetFloat("_ShaderType", 4); eyeMaterial.SetFloat("_ZWrite", 0);
            eyeMaterial.SetFloat("_SrcBlend", 1); eyeMaterial.SetFloat("_DstBlend", 10); eyeMaterial.renderQueue = 2002;
            eyeMaterial.SetTexture("_MainTex", ResolveTexture(new[] { new Color(0, .8f, 0, .5f) }, 1, 1));
            eyeMaterial.SetFloat("_StencilRef", 32); eyeMaterial.SetFloat("_StencilReadMask", 32); eyeMaterial.SetFloat("_StencilComp", 3);
            eyeMaterial.SetFloat("_StencilPass", 0); eyeMaterial.SetFloat("_StencilWriteMask", 0); eye.GetComponent<Renderer>().sharedMaterial = eyeMaterial;
            source.SetFloat("_StencilRef", 32); source.SetFloat("_StencilReadMask", 255); source.SetFloat("_StencilWriteMask", 32); source.SetFloat("_StencilPass", 2);
            var renderers = new[] { eye.GetComponent<Renderer>(), actor.GetComponent<Renderer>() };
            Color[] Render()
            {
                if (!set.TryRefresh(renderers, camera.worldToCameraMatrix * PlanarReflection.ReflectionMatrix(planar.planePoint, planar.planeNormal), lighting, out var error)) throw new InvalidOperationException(error);
                planar.reflectedSurfaces = set.Draws; camera.Render(); return ReadSceneTarget(PlanarField<RenderTexture>(planar, "_capture"));
            }
            var baseline = Render(); int center = (height / 2) * width + width / 2, outside = (height / 2) * width + width / 2 + 33;
            FrameworkCheck(report, "actor-planar-stencil-eye-interior-composition", Mathf.Abs(baseline[center].r - .4f) < .002f && Mathf.Abs(baseline[center].g - .5f) < .002f && baseline[center].a == 1);
            FrameworkCheck(report, "actor-planar-stencil-rejects-color-and-coverage-outside-white", baseline[outside].Equals(Color.clear));
            eyeMaterial.SetFloat("_StencilComp", 8); var leaked = Render();
            FrameworkCheck(report, "actor-planar-stencil-negative-control-visible-outside", leaked[outside].g > .3f && Mathf.Abs(leaked[outside].a - .5f) < .001f);
            eyeMaterial.SetFloat("_StencilComp", 3); var recovered = Render();
            FrameworkCheck(report, "actor-planar-stencil-is-cleared-and-replayed-every-frame", ScenePixelsEqual(baseline, recovered));
            SaveSsrPreview("actor-planar-stencil-color", recovered, width, height, false);
            var hair = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); hair.layer = 27;
            hair.transform.position = new Vector3(0, 0, -.4f); hair.transform.localScale = new Vector3(2.2f, .5f, 1);
            var hairMaterial = Own(new Material(source)); hair.GetComponent<Renderer>().sharedMaterial = hairMaterial;
            hairMaterial.SetFloat("_ShaderType", 8); hairMaterial.renderQueue = 2301;
            hairMaterial.SetFloat("_StencilRef", 0); hairMaterial.SetFloat("_StencilReadMask", 32);
            hairMaterial.SetFloat("_StencilWriteMask", 0); hairMaterial.SetFloat("_StencilComp", 7); hairMaterial.SetFloat("_StencilPass", 0);
            hairMaterial.SetTexture("_MainTex", ResolveTexture(new[] { new Color(0, 0, 1, .25f) }, 1, 1));
            hairMaterial.SetShaderPassEnabled("ActorHairCover", true);
            var head = Own(new GameObject("Explicit capture head basis")); head.transform.rotation = Quaternion.Euler(0, 180, 0); lighting.head = head.transform;
            renderers = new[] { hair.GetComponent<Renderer>(), eye.GetComponent<Renderer>(), actor.GetComponent<Renderer>() };
            Color[] covered = Render();
            FrameworkCheck(report, "actor-planar-hair-cover-matches-less-stencil-and-opacity", PixelError(new[] { covered[center] }, new[] { new Color(.1f, .125f, .7625f, 1) }) < .002f && set.MaterialCount == 4);
            FrameworkCheck(report, "actor-planar-hair-primary-complementary-stencil", PixelError(new[] { covered[outside] }, new[] { Color.blue }) < .002f);
            hairMaterial.SetShaderPassEnabled("ActorHairCover", false); Color[] noCover = Render();
            FrameworkCheck(report, "actor-planar-hair-cover-disable-negative-control", PixelError(new[] { noCover[center] }, new[] { recovered[center] }) == 0 && set.MaterialCount == 3);
            hairMaterial.SetShaderPassEnabled("ActorHairCover", true);
            FrameworkCheck(report, "actor-planar-hair-cover-refresh-recovers", ScenePixelsEqual(covered, Render()));
            lighting.head = null; hair.SetActive(false); head.SetActive(false);
            planar.reflectedSurfaces[0].coverageShaderPass = 999; camera.Render();
            FrameworkCheck(report, "actor-planar-invalid-custom-mask-rejected", !planar.TryGetReflection(camera, width, height, out _) && planar.UnavailableReason == "Invalid custom coverage pass");
            eye.SetActive(false);
        }
    }
}
