using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifySceneMainShadows(Report report)
        {
            yield return null;
            var previous = FindObjectsOfType<Renderer>(); var forced = new bool[previous.Length];
            for (int i = 0; i < previous.Length; i++) { forced[i] = previous[i].forceRenderingOff; previous[i].forceRenderingOff = true; }
            try
            {
                void Check(string name, bool ok, float error = 0) => FrameworkCheck(report, "scene-main-shadow-" + name, ok, error);
                var host = Own(new GameObject("Main shadow host")); var camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward;
                camera.transform.position = new Vector3(0, 0, -4); camera.orthographic = true; camera.orthographicSize = 2; camera.aspect = 1;
                camera.nearClipPlane = .1f; camera.farClipPlane = 40; camera.cullingMask = 1 << 26; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                var target = Own(new RenderTexture(129, 129, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)); target.Create(); camera.targetTexture = target;
                var stage = host.AddComponent<SceneDeferredCamera>(); stage.sceneEnabled = true; stage.sceneLayers = 1 << 25;
                stage.lightDirection = Vector3.back; stage.lightRadiance = new Vector3(3, 2, 1); stage.ambientIrradiance = new Vector3(.2f, .15f, .1f);
                var receiver = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); receiver.layer = 25; receiver.transform.localScale = Vector3.one * 3.8f;
                var receiverMesh = Own(Instantiate(receiver.GetComponent<MeshFilter>().sharedMesh)); receiverMesh.uv2 = receiverMesh.uv; receiver.GetComponent<MeshFilter>().sharedMesh = receiverMesh;
                var surface = new SceneDeferredCamera.Surface { renderer = receiver.GetComponent<Renderer>(), cull = CullMode.Off };
                surface.inputs.albedo = new Vector3(.5f, .4f, .3f); surface.inputs.mos = new Vector3(.1f, .7f, .4f); surface.inputs.emission = new Vector3(.12f, .08f, .04f); stage.surfaces = new[] { surface };
                var occluder = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); occluder.layer = 24;
                occluder.transform.position = new Vector3(.2f, .25f, -1); occluder.transform.localScale = new Vector3(.8f, .6f, 1);
                var caster = new SceneShadowCaster { renderer = occluder.GetComponent<Renderer>(), cull = CullMode.Off };
                var borrowed = caster.renderer.sharedMaterial; var borrowedMesh = occluder.GetComponent<MeshFilter>().sharedMesh;
                var settings = stage.mainLightShadow; settings.origin = new Vector3(0, 0, -3); settings.halfSize = Vector2.one * 2; settings.farPlane = 6; settings.resolution = 256;
                settings.casters = new[] { caster };
                SceneDeferredCamera.Frame Render() { camera.Render(); if (!stage.TryGetFrame(out var f)) throw new InvalidOperationException(stage.UnavailableReason); return f; }
                Color[] Pixels() => ReadSceneTarget(target);
                Vector3 Rgb(Color c) => new Vector3(c.r, c.g, c.b);
                float Error(Vector3 a, Vector3 b) => Mathf.Max(Mathf.Abs(a.x-b.x), Mathf.Abs(a.y-b.y), Mathf.Abs(a.z-b.z));
                var baselineFrame = Render(); var baseline = Pixels();
                Check("disabled-no-main-depth-allocation", baselineFrame.mainLightShadowDepth == null && stage.MainShadowTargetCount == 0 && stage.MainShadowCasterDrawCalls == 0);
                settings.enabled = true; Transform deformBone = null;
                void Oracle(string name, bool expectOcclusion = true)
                {
                    settings.enabled = false; Render(); var lit = Pixels(); var radiance = stage.lightRadiance;
                    stage.lightRadiance = Vector3.zero; Render(); var basis = Pixels(); stage.lightRadiance = radiance; settings.enabled = true;
                    var frame = Render(); var actual = Pixels(); var g0 = ReadSceneTarget(frame.albedoCoverage); var g1 = ReadSceneTarget(frame.normalGroup);
                    var inverse = ((deformBone != null ? deformBone.localToWorldMatrix : caster.renderer.localToWorldMatrix) * Matrix4x4.Scale(caster.vertexScale)).inverse;
                    var light = stage.lightDirection.normalized; var forward = -light; var right = Vector3.Cross(settings.up.normalized, forward).normalized; var up = Vector3.Cross(forward, right);
                    bool active = Array.IndexOf(settings.casters, caster) >= 0 && caster.renderer.enabled && !caster.renderer.forceRenderingOff && caster.renderer.gameObject.activeInHierarchy;
                    float worst = 0; int samples = 0, blockedCount = 0, clearCount = 0;
                    for (int y = 0; y < 129; y++) for (int x = 0; x < 129; x++)
                    {
                        int index = y * 129 + x; if (g0[index].a < .5f) continue;
                        var ray = camera.ViewportPointToRay(new Vector3((x + .5f)/129, (y + .5f)/129));
                        if (!new Plane(Vector3.forward, Vector3.zero).Raycast(ray, out float distance)) continue;
                        var world = ray.GetPoint(distance) + Rgb(g1[index]).normalized * settings.normalBias;
                        var fromOrigin = world - settings.origin; float sx = Vector3.Dot(fromOrigin, right), sy = Vector3.Dot(fromOrigin, up), axial = Vector3.Dot(fromOrigin, forward);
                        float edge = 4 * Mathf.Max(settings.halfSize.x, settings.halfSize.y) / settings.resolution;
                        if (Mathf.Abs(Mathf.Abs(sx)-settings.halfSize.x) < edge || Mathf.Abs(Mathf.Abs(sy)-settings.halfSize.y) < edge) continue;
                        var local = inverse.MultiplyPoint(world); var localDirection = inverse.MultiplyVector(light);
                        float t = -local.z / localDirection.z; var hit = local + localDirection * t;
                        if (Mathf.Abs(Mathf.Abs(hit.x)-.5f) < .05f || Mathf.Abs(Mathf.Abs(hit.y)-.5f) < .05f) continue;
                        float alpha = caster.alpha;
                        if (caster.alphaMap is Texture2D map)
                        {
                            var uv = new Vector2((hit.x+.5f)*caster.uvST.x+caster.uvST.z, (hit.y+.5f)*caster.uvST.y+caster.uvST.w);
                            if (Mathf.Abs(uv.x-.5f) < .05f) continue;
                            alpha *= map.GetPixel(Mathf.Clamp(Mathf.FloorToInt(uv.x*map.width),0,map.width-1), Mathf.Clamp(Mathf.FloorToInt(uv.y*map.height),0,map.height-1)).a;
                        }
                        bool blocked = active && caster.cull != CullMode.Front && alpha >= caster.cutoff && t > settings.depthBias &&
                            Mathf.Abs(hit.x) < .5f && Mathf.Abs(hit.y) < .5f && Mathf.Abs(sx) <= settings.halfSize.x && Mathf.Abs(sy) <= settings.halfSize.y &&
                            axial >= settings.nearPlane && axial <= settings.farPlane && axial-t >= settings.nearPlane && axial-t <= settings.farPlane;
                        var expected = Rgb(basis[index]) + (Rgb(lit[index])-Rgb(basis[index])) * (blocked ? 1-settings.strength : 1);
                        worst = Mathf.Max(worst, Error(expected, Rgb(actual[index]))); samples++;
                        if (blocked) blockedCount++; else clearCount++;
                    }
                    Check(name + "-cpu-ray-interiors", worst < .003f && samples > 8000 && clearCount > 1000 && (expectOcclusion ? blockedCount > 30 : blockedCount == 0), worst);
                }
                Oracle("axis-aligned"); var first = Render(); var reference = Pixels(); var depth = ReadSceneTarget(first.mainLightShadowDepth);
                int occupied = 0; float depthError = 0;
                foreach (var p in depth) if (p.r < .9f) { occupied++; depthError = Mathf.Max(depthError, Mathf.Abs(p.r-1f/3)); }
                Check("actual-orthographic-axial-depth-not-constant-clip-w", occupied > 1000 && depthError < .000001f && stage.MainShadowCasterDrawCalls == 1, depthError);
                SaveSsrPreview("scene-main-shadow-reference", reference,129,129,false);
                occluder.transform.position += new Vector3(-.6f, -.4f, 0); Oracle("moving-caster"); occluder.transform.position -= new Vector3(-.6f, -.4f, 0);
                stage.lightDirection = new Vector3(.4f,-.25f,-1); Oracle("oblique-main-light"); stage.lightDirection = Vector3.back;
                settings.up = new Vector3(.7f,1,0); Oracle("rolled-shadow-basis"); settings.up = Vector3.up;
                settings.halfSize = new Vector2(1.2f,.8f); Oracle("rectangular-coverage"); settings.halfSize = Vector2.one*2;
                settings.origin += new Vector3(.5f,-.25f,0); Oracle("moving-coverage-origin"); settings.origin -= new Vector3(.5f,-.25f,0);
                settings.origin += Vector3.right*7; Oracle("receiver-outside-xy-coverage",false); settings.origin -= Vector3.right*7;
                settings.nearPlane = 2.5f; Oracle("caster-before-near",false); settings.nearPlane = .05f;
                settings.farPlane = 2.5f; Oracle("receiver-after-far",false); settings.farPlane = 6;
                occluder.transform.position += Vector3.forward*8; Oracle("caster-after-far-and-receiver",false); occluder.transform.position -= Vector3.forward*8;
                settings.origin += Vector3.back*4; Oracle("all-geometry-beyond-coverage",false); settings.origin -= Vector3.back*4;
                settings.strength = .35f; Oracle("partial-strength"); settings.strength = 1;
                settings.depthBias = 1.1f; Oracle("world-depth-bias",false); settings.depthBias = .002f;
                settings.normalBias = 1.1f; Oracle("world-normal-bias",false); settings.normalBias = 0;
                settings.filter = SceneShadowFilter.Pcf3x3; Oracle("pcf-interiors"); var soft = Pixels(); settings.filter = SceneShadowFilter.Hard; Render();
                Check("pcf-changes-shadow-edge", PixelError(soft, Pixels()) > .01f);
                caster.renderer.enabled = false; Oracle("disabled-caster",false); caster.renderer.enabled = true;
                caster.renderer.forceRenderingOff = true; Oracle("forced-off-caster",false); caster.renderer.forceRenderingOff = false;
                occluder.SetActive(false); Oracle("inactive-caster",false); occluder.SetActive(true);
                settings.casters = Array.Empty<SceneShadowCaster>(); Oracle("no-casters",false); settings.casters = new[] { caster };
                caster.cull = CullMode.Back; Oracle("backface-culling"); caster.cull = CullMode.Front; Oracle("frontface-culling",false); caster.cull = CullMode.Off;
                caster.alpha = 0; caster.cutoff = .5f; Oracle("empty-alpha-cutout",false); caster.alpha = 1; caster.cutoff = 0;
                caster.vertexScale = new Vector3(1.4f,.7f,1); Oracle("explicit-vertex-scale"); caster.vertexScale = Vector3.one;
                occluder.transform.rotation = Quaternion.Euler(25,-15,20); Oracle("sloping-caster"); occluder.transform.rotation = Quaternion.identity;
                var alphaMap = Own(new Texture2D(2,1,TextureFormat.RGBAFloat,false,true)); alphaMap.filterMode = FilterMode.Point; alphaMap.wrapMode = TextureWrapMode.Clamp;
                alphaMap.SetPixels(new[] {new Color(1,1,1,0),Color.white}); alphaMap.Apply(); caster.alphaMap = alphaMap; caster.cutoff = .5f;
                Oracle("texture-alpha-half-caster"); caster.uvST = new Vector4(-1,1,1,0); Oracle("cutout-uv-flip"); caster.uvST = new Vector4(1,1,0,0); caster.alphaMap = null; caster.cutoff = 0;
                camera.orthographic = false; camera.fieldOfView = 50; Oracle("perspective-host"); camera.orthographic = true;
                camera.nearClipPlane = 3; camera.projectionMatrix = Matrix4x4.Ortho(-2,2,-2,2,.1f,40); Oracle("custom-host-projection"); camera.ResetProjectionMatrix(); camera.nearClipPlane = .1f;
                var giMap = Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true)); giMap.SetPixel(0,0,new Color(2,1,.5f,1)); giMap.Apply();
                surface.gi = new SceneGiInput {source=SceneGiSource.Lightmap, lightmap=giMap}; stage.giBaseScale=.4f; stage.directionalGiWeight=1;
                Oracle("gi-modulated-direct-with-independent-base-gi-and-emission"); stage.directionalDiffuseScale=0; Oracle("specular-shadow-only"); stage.directionalDiffuseScale=1; stage.directionalSpecularScale=0; Oracle("diffuse-shadow-only");
                var normals=receiverMesh.normals; var opposite=new Vector3[normals.Length]; for(int i=0;i<normals.Length;i++) opposite[i]=-normals[i]; receiverMesh.normals=opposite;
                stage.directionalBacklight=.8f; Oracle("inverse-diffuse-shadow"); stage.directionalBacklight=0; receiverMesh.normals=normals; stage.directionalSpecularScale=1;
                surface.gi.source=SceneGiSource.None; stage.directionalGiWeight=0; stage.giBaseScale=1;

                var nearer=Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); nearer.layer=24; nearer.transform.position=occluder.transform.position+Vector3.back; nearer.transform.localScale=occluder.transform.localScale;
                var nearCaster=new SceneShadowCaster {renderer=nearer.GetComponent<Renderer>(), cull=CullMode.Off};
                settings.casters=new[]{caster,nearCaster}; var nearest=ReadSceneTarget(Render().mainLightShadowDepth); float nearestError=0; int nearestCount=0;
                foreach(var p in nearest) if(p.r<.9f) {nearestCount++;nearestError=Mathf.Max(nearestError,Mathf.Abs(p.r-1f/6));}
                Check("nearest-geometry-selects-one-sixth-depth",nearestCount>1000&&nearestError<.000001f,nearestError);
                settings.casters=new[]{nearCaster,caster}; Check("reversed-draw-order-keeps-nearest-depth",ScenePixelsEqual(nearest,ReadSceneTarget(Render().mainLightShadowDepth)));
                settings.casters=new[]{caster}; nearer.SetActive(false);

                var skinHost=Own(new GameObject("Main shadow skinned caster")); skinHost.layer=24; skinHost.transform.position=occluder.transform.position; skinHost.transform.localScale=Vector3.one*.7f;
                var bone=Own(new GameObject("Main shadow bone")).transform; bone.SetParent(skinHost.transform,false);
                var skin=skinHost.AddComponent<SkinnedMeshRenderer>(); var skinMesh=Own(Instantiate(borrowedMesh)); var weights=new BoneWeight[skinMesh.vertexCount];
                for(int i=0;i<weights.Length;i++) weights[i]=new BoneWeight {boneIndex0=0,weight0=1};
                skinMesh.boneWeights=weights; skinMesh.bindposes=new[]{Matrix4x4.identity}; skin.sharedMesh=skinMesh; skin.bones=new[]{bone}; skin.rootBone=bone;
                skin.sharedMaterial=borrowed; skin.updateWhenOffscreen=true; skin.localBounds=new Bounds(Vector3.zero,Vector3.one*5);
                var control=Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); control.layer=24; control.transform.position=skinHost.transform.position; control.transform.localScale=skinHost.transform.localScale;
                var controlMesh=Own(Instantiate(borrowedMesh)); control.GetComponent<MeshFilter>().sharedMesh=controlMesh;
                var originalRenderer=caster.renderer; caster.renderer=skin; deformBone=bone; Color[] initialSkin=null;
                for(int i=0;i<3;i++)
                {
                    bone.localPosition=new Vector3(-i*.45f,i*.2f,0); bone.localRotation=Quaternion.Euler(i*10,i*15,i*20);
                    yield return null; Oracle("skinned-pose-"+i); var skinPixels=Pixels(); var skinDepth=ReadSceneTarget(Render().mainLightShadowDepth);
                    if(i==0) initialSkin=skinPixels; else Check("bone-motion-changes-shadow-"+i,PixelError(initialSkin,skinPixels)>.1f);
                    var vertices=borrowedMesh.vertices; var deformation=Matrix4x4.TRS(bone.localPosition,bone.localRotation,bone.localScale);
                    for(int v=0;v<vertices.Length;v++) vertices[v]=deformation.MultiplyPoint(vertices[v]); controlMesh.vertices=vertices; controlMesh.RecalculateBounds();
                    caster.renderer=control.GetComponent<Renderer>(); var cf=Render();
                    Check("skin-vs-cpu-static-depth-"+i,PixelError(skinDepth,ReadSceneTarget(cf.mainLightShadowDepth))<.000001f,PixelError(skinDepth,ReadSceneTarget(cf.mainLightShadowDepth)));
                    Check("skin-vs-cpu-static-complete-image-"+i,PixelError(skinPixels,Pixels())<.003f,PixelError(skinPixels,Pixels())); caster.renderer=skin;
                }
                SaveSsrPreview("scene-main-shadow-skinned",Pixels(),129,129,false); caster.renderer=originalRenderer; deformBone=null; skinHost.SetActive(false); control.SetActive(false);

                var spot=new SceneDecalLight {shape=SceneDecalLightShape.Spot,position=new Vector3(0,0,-2),range=5,spotInnerAngle=90,spotOuterAngle=90,radiance=new Vector3(.3f,.7f,1.5f)};
                spot.shadow.enabled=true; stage.decalLighting.enabled=true; stage.decalLighting.lights=new[]{spot}; stage.decalLighting.shadows.casters=new[]{caster};
                var mixed=Render(); var mixedPixels=Pixels();
                Check("independent-main-and-spot-depth-resources",stage.MainShadowTargetCount==1&&stage.LightShadowMapCount==1&&mixed.mainLightShadowDepth!=mixed.lightShadowAtlas);
                foreach(var backend in new[]{SceneDecalLightBackend.Scalar,SceneDecalLightBackend.Instanced}) {stage.decalLighting.backend=backend;Oracle("main-plus-spot-"+backend);Check("mixed-backend-image-"+backend,PixelError(mixedPixels,Pixels())<.003f,PixelError(mixedPixels,Pixels()));}
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_MAIN_SHADOW") == "1")
                {
                    bool started=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try {if(started){Render();Pixels();}} finally{if(started)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("requested-native-main-and-spot-capture",started&&ended);
                }
                SaveSsrPreview("scene-main-shadow-plus-spot",Pixels(),129,129,false);
                stage.decalLighting.enabled=false;

                var firstFrame=Render(); var firstPixels=Pixels(); var firstDepth=ReadSceneTarget(firstFrame.mainLightShadowDepth);
                var host2=Own(new GameObject("Independent main shadow host")); var camera2=host2.AddComponent<Camera>(); camera2.CopyFrom(camera);camera2.enabled=false;camera2.transform.position=camera.transform.position;
                var target2=Own(new RenderTexture(97,65,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target2.Create();camera2.targetTexture=target2;
                var stage2=host2.AddComponent<SceneDeferredCamera>();stage2.sceneEnabled=true;stage2.sceneLayers=stage.sceneLayers;stage2.surfaces=stage.surfaces;stage2.lightRadiance=Vector3.one;
                stage2.mainLightShadow=new SceneDirectionalShadowSettings {enabled=true,origin=new Vector3(.1f,.2f,-2),farPlane=9,resolution=128,casters=new[]{caster}};
                camera2.Render(); Check("per-camera-owned-main-depth",stage2.TryGetFrame(out var second)&&second.mainLightShadowDepth!=firstFrame.mainLightShadowDepth&&second.mainLightShadowDepth.width==128&&firstFrame.IsCurrent);
                Check("other-camera-does-not-overwrite-main-output-or-depth",ScenePixelsEqual(firstPixels,Pixels())&&ScenePixelsEqual(firstDepth,ReadSceneTarget(firstFrame.mainLightShadowDepth)));host2.SetActive(false);camera2.targetTexture=null;
                var oldFrame=Render();var oldDepth=oldFrame.mainLightShadowDepth;settings.resolution=128;Render();Check("resolution-change-releases-old-depth",!oldFrame.IsCurrent&&!oldDepth.IsCreated());settings.resolution=256;
                var lostFrame=Render();var lost=lostFrame.mainLightShadowDepth;lost.Release();Check("lost-depth-invalidates-published-frame",!lostFrame.IsCurrent);var rebuilt=Render();Check("lost-depth-recreated",rebuilt.mainLightShadowDepth.IsCreated()&&rebuilt.mainLightShadowDepth!=lost);
                camera.targetTexture=target2;Render();Check("host-resize-keeps-independent-shadow-resolution",stage.TryGetFrame(out var resized)&&resized.albedoCoverage.width==97&&resized.mainLightShadowDepth.width==256);camera.targetTexture=target;
                void Reject(string name){camera.Render();Check(name,!stage.TryGetFrame(out _)&&stage.MainShadowTargetCount==0&&stage.LightTargetCount==0&&!string.IsNullOrEmpty(stage.UnavailableReason));}
                settings.nearPlane=settings.farPlane;Reject("near-equals-far-rejected");settings.nearPlane=.05f;
                settings.origin=new Vector3(float.NaN,0,-3);Reject("nan-origin-rejected");settings.origin=new Vector3(0,0,-3);
                settings.up=Vector3.forward;Reject("parallel-up-rejected");settings.up=Vector3.zero;Reject("zero-up-rejected");settings.up=Vector3.up;
                settings.halfSize=new Vector2(0,2);Reject("zero-width-rejected");settings.halfSize=Vector2.one*2;
                settings.strength=float.NaN;Reject("nan-strength-rejected");settings.strength=1;
                settings.depthBias=-1;Reject("negative-bias-rejected");settings.depthBias=.002f;
                settings.normalBias=float.PositiveInfinity;Reject("infinite-normal-bias-rejected");settings.normalBias=0;
                settings.filter=(SceneShadowFilter)999;Reject("unknown-filter-rejected");settings.filter=SceneShadowFilter.Hard;
                settings.resolution=33;Reject("non-power-of-two-rejected");settings.resolution=256;
                settings.casters=null;Reject("null-caster-list-rejected");settings.casters=new[]{caster};
                caster.materialIndex=1;Reject("invalid-caster-submesh-rejected");caster.materialIndex=0;
                var block=new MaterialPropertyBlock();block.SetFloat("_Unrelated",1);caster.renderer.SetPropertyBlock(block);Reject("caster-property-block-rejected");caster.renderer.SetPropertyBlock(null);
                settings.strength=0;Render();Check("zero-strength-no-target-and-full-unshadowed-image",stage.MainShadowTargetCount==0&&ScenePixelsEqual(baseline,Pixels()));settings.strength=1;
                stage.lightRadiance=Vector3.zero;Render();Check("zero-radiance-no-main-target",stage.MainShadowTargetCount==0);settings.nearPlane=7;Reject("invalid-request-rejected-even-with-zero-radiance");settings.nearPlane=.05f;stage.lightRadiance=new Vector3(3,2,1);
                stage.directionalDiffuseScale=stage.directionalSpecularScale=0;Render();Check("zero-direct-response-no-main-target",stage.MainShadowTargetCount==0);stage.directionalDiffuseScale=stage.directionalSpecularScale=1;
                settings.enabled=false;Render();Check("disabled-restores-complete-original-image",ScenePixelsEqual(baseline,Pixels())&&stage.MainShadowTargetCount==0);
                Check("borrowed-mesh-material-and-renderer-state-preserved",caster.renderer.sharedMaterial==borrowed&&occluder.GetComponent<MeshFilter>().sharedMesh==borrowedMesh&&!caster.renderer.HasPropertyBlock()&&caster.renderer.enabled&&!caster.renderer.forceRenderingOff);
                settings.enabled=true;var finalDepth=Render().mainLightShadowDepth;stage.enabled=false;Check("disable-releases-main-depth",!finalDepth.IsCreated()&&stage.MainShadowTargetCount==0);
                camera.targetTexture=null;host.SetActive(false);receiver.SetActive(false);occluder.SetActive(false);
            }
            finally
            {
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();
            }
        }
    }
}
