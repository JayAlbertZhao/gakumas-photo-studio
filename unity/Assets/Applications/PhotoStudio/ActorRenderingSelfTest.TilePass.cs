using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    // An isolated request-only SRP host. It does not replace the normal application pipeline.
    internal sealed class TilePassTestRequest { public Action<ScriptableRenderContext> record; }
    internal sealed class TilePassTestAsset : RenderPipelineAsset
    {
        protected override RenderPipeline CreatePipeline() => new TilePassTestPipeline();
    }
    internal sealed class TilePassTestPipeline : RenderPipeline
    {
        protected override void Render(ScriptableRenderContext context, Camera[] cameras) { }
        protected override bool IsRenderRequestSupported<T>(Camera camera, T request) => request is TilePassTestRequest;
        protected override void ProcessRenderRequests<T>(ScriptableRenderContext context, Camera camera, T request)
        {
            if (!(request is TilePassTestRequest value)) throw new ArgumentException("Unexpected tile request");
            context.SetupCameraProperties(camera); value.record(context); context.Submit();
        }
    }

    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyTileRenderPass(Report report)
        {
            yield return null;
            var previousGraphics = GraphicsSettings.renderPipelineAsset;
            var previousQuality = QualitySettings.renderPipeline;
            var previousActive = RenderTexture.active;
            Debug.Log("[TileSelfTest] renderingThreadingMode="+SystemInfo.renderingThreadingMode);
            using var renderer = new TileRenderPass();
            try
            {
                var pipeline = Own(ScriptableObject.CreateInstance<TilePassTestAsset>());
                GraphicsSettings.renderPipelineAsset = pipeline; QualitySettings.renderPipeline = pipeline;
                var camera = Own(new GameObject("Tile request camera")).AddComponent<Camera>(); camera.enabled = false;
                var mesh = Own(new Mesh { name = "Authored tile plane" });
                mesh.vertices = new[] { new Vector3(-1,-1,.5f), new Vector3(1,-1,.5f), new Vector3(1,1,.5f), new Vector3(-1,1,.5f) };
                mesh.triangles = new[] { 0,2,1,0,3,2 }; mesh.RecalculateBounds();
                var material = Own(new Material(Resources.Load<Shader>("TileRenderPassReference")));
                material.SetMatrix("_ClipFromLocal", Matrix4x4.identity);
                RenderTexture Target(int width, int height, GraphicsFormat format, string name)
                {
                    var descriptor = new RenderTextureDescriptor(width,height,format,0);
                    if (GraphicsFormatUtility.IsDepthStencilFormat(format))
                    { descriptor.graphicsFormat = GraphicsFormat.None; descriptor.depthStencilFormat = format; }
                    var target = Own(new RenderTexture(descriptor) { name = name, filterMode = FilterMode.Point });
                    if (!target.Create()) throw new InvalidOperationException("Tile output allocation failed"); return target;
                }
                TileRenderPass.Plan Plan(int width, int height)
                {
                    var guide = Target(width,height,GraphicsFormat.R16G16B16A16_SFloat,"Tile reused normal to guide");
                    var color = Target(width,height,GraphicsFormat.B10G11R11_UFloatPack32,"Tile reused GI to color");
                    return new TileRenderPass.Plan { enabled = true, width = width, height = height,
                        backend = TileRenderPass.BackendPolicy.AllowEmulation, depthAttachment = 5,
                        attachments = new[] {
                            new TileRenderPass.Attachment { name="base",format=GraphicsFormat.R8G8B8A8_SRGB },
                            new TileRenderPass.Attachment { name="mos",format=GraphicsFormat.R8G8B8A8_UNorm },
                            new TileRenderPass.Attachment { name="normal / guide",format=guide.graphicsFormat,target=guide,store=true },
                            new TileRenderPass.Attachment { name="scene emission",format=GraphicsFormat.B10G11R11_UFloatPack32 },
                            new TileRenderPass.Attachment { name="GI / color",format=color.graphicsFormat,target=color,store=true },
                            new TileRenderPass.Attachment { name="depth",format=GraphicsFormat.D32_SFloat,clearDepth=1 }
                        },
                        subpasses = new[] {
                            new TileRenderPass.Subpass { colors=new[]{0,1,2,3,4},draws=new[]{new TileRenderPass.Draw{mesh=mesh,material=material,shaderPass=0}} },
                            new TileRenderPass.Subpass { colors=new[]{3},inputs=new[]{0,1,2,4},depthReadOnly=true,draws=new[]{new TileRenderPass.Draw{mesh=mesh,material=material,shaderPass=1}} },
                            new TileRenderPass.Subpass { colors=new[]{2,4},inputs=new[]{3},depthReadOnly=true,draws=new[]{new TileRenderPass.Draw{mesh=mesh,material=material,shaderPass=2}} }
                        }
                    };
                }
                uint sequence = 0;
                TileRenderPass.Submission Run(string name, TileRenderPass.Plan plan, bool native = false)
                {
                    for(int i=0;i<plan.subpasses.Length;i++)plan.subpasses[i].name="Tile "+name+" / "+i;
                    camera.targetTexture=plan.attachments[4].target;
                    TileRenderPass.Submission submitted=default;
                    var request=new TilePassTestRequest { record=context => {
                        if(!renderer.TryRecord(context,plan,out submitted,out string error))throw new InvalidOperationException(error);
                    } };
                    FrameworkCheck(report,"tile-pass-"+name+"-host",RenderPipeline.SupportsRenderRequest(camera,request));
                    bool capture=native && Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_PASS")=="1";
                    string captureCase=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_TILE_PASS_CASE");
                    capture=capture && (string.IsNullOrEmpty(captureCase)||captureCase==name);
                    bool began=false,ended=false;
                    if(capture)began=RenderDocCaptureBridge.BeginOffscreenCapture();
                    try { RenderPipeline.SubmitRenderRequest(camera,request); if(capture)ReadSceneTarget(camera.targetTexture); }
                    finally { if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture(); }
                    if(capture)FrameworkCheck(report,"tile-pass-"+name+"-native",began&&ended);
                    int drawCount=0;foreach(var subpass in plan.subpasses)drawCount+=subpass.draws.Length;
                    FrameworkCheck(report,"tile-pass-"+name+"-submission",submitted.sequence==++sequence && submitted.subpasses==plan.subpasses.Length && submitted.draws==drawCount && submitted.nativeApi==TileRenderPass.HasNativeApi,submitted.draws);
                    return submitted;
                }
                Color[] Compare(string name, RenderTexture target, Func<int,int,Color> expected, Color[] admissible = null)
                {
                    var values=ReadSceneTarget(target);float error=0;int finite=0;
                    for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)
                    {
                        Color want=expected(x,y),value=values[y*target.width+x];
                        float difference=0;
                        for(int c=0;c<4;c++)
                        {
                            if(!float.IsNaN(value[c])&&!float.IsInfinity(value[c]))finite++;
                            difference=Mathf.Max(difference,Mathf.Abs(value[c]-want[c]));
                        }
                        if(admissible!=null)
                        {
                            difference=float.PositiveInfinity;
                            foreach(var candidate in admissible)
                            {
                                float d=0;for(int c=0;c<4;c++)d=Mathf.Max(d,Mathf.Abs(value[c]-candidate[c]));
                                difference=Mathf.Min(difference,d);
                            }
                        }
                        error=Mathf.Max(error,difference);
                    }
                    FrameworkCheck(report,"tile-pass-"+name+"-whole",error<=.0003f && finite==values.Length*4,error);
                    if(admissible!=null)FrameworkCheck(report,"tile-pass-"+name+"-uniform",Array.TrueForAll(values,p=>p.Equals(values[0])));
                    SaveSsrPreview("tile-pass-"+name,values,target.width,target.height,false);
                    using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"tile-pass-"+name+".raw")));
                    foreach(var value in values)for(int c=0;c<4;c++)writer.Write(value[c]);
                    return values;
                }
                void Outputs(string name, TileRenderPass.Plan plan, Func<int,int,Color> emission)
                {
                    Compare(name+"-guide",plan.attachments[2].target,(x,y)=>{Color e=emission(x,y);return new Color(e.g,e.b,.5f,7);});
                    Compare(name+"-color",plan.attachments[4].target,(x,y)=>{Color e=emission(x,y);return new Color(e.b,e.g*.5f,e.r*2,1);});
                }
                var baseline=Plan(64,64);
                var budget=Run("baseline",baseline,true);
                FrameworkCheck(report,"tile-pass-256-color-separate-depth",budget.colorTileBits==256 && budget.depthBits==32,budget.colorTileBits);
                FrameworkCheck(report,"tile-pass-explicit-nominal-byte-accounting",budget.nominalBytes==64*64*28 && budget.transientBytes==64*64*16 && budget.storedBytes==64*64*12 && budget.loadedBytes==0,budget.nominalBytes);
                Outputs("baseline",baseline,(x,y)=>new Color(.625f,.375f,.75f));
                var original=ReadSceneTarget(baseline.attachments[4].target);

                baseline.attachments[5].clearDepth=0;
                Run("wrong-logical-depth-clear",baseline,true);
                Outputs("wrong-logical-depth-clear",baseline,(x,y)=>Color.clear);
                FrameworkCheck(report,"tile-pass-depth-counterfactual-changes-image",!ScenePixelsEqual(original,ReadSceneTarget(baseline.attachments[4].target)));
                baseline.attachments[5].clearDepth=1;
                baseline.subpasses[1].inputs=new[]{1,0,2,4};
                Run("swapped-inputs",baseline,true);
                Outputs("swapped-inputs",baseline,(x,y)=>new Color(.25f,.25f,.75f));
                FrameworkCheck(report,"tile-pass-input-order-counterfactual-changes-image",!ScenePixelsEqual(original,ReadSceneTarget(baseline.attachments[4].target)));
                baseline.subpasses[1].inputs=new[]{0,1,2,4};

                material.SetVector("_Base",new Vector4(.21404114f,0,0,1));
                material.SetVector("_Mos",new Vector4(0,.5f,0,1));
                Run("srgb-unorm-midpoint",baseline,true);
                // Quantization is part of this fixture's input contract. The API permits
                // either adjacent format value, including during blend conversion. Enumerate
                // those discrete bins BEFORE inspecting output; do not fit a scalar tolerance.
                // All exactly representable fixtures below still require a single exact field.
                float[] Neighbors(double value,int mantissa)
                {
                    if(value==0)return new[]{0f};
                    double step=Math.Pow(2,Math.Floor(Math.Log(value,2))-mantissa);
                    return new[]{(float)(Math.Floor(value/step)*step),(float)(Math.Ceiling(value/step)*step)};
                }
                var guideChoices=new List<Color>();var colorChoices=new List<Color>();
                foreach(int baseByte in new[]{127,128})foreach(int mosByte in new[]{127,128})
                {
                    double b=Math.Pow((baseByte/255.0+.055)/1.055,2.4),m=mosByte/255.0;
                    foreach(float sourceR in Neighbors(b*m*.5,6))foreach(float sourceG in Neighbors(m*.125,6))
                    foreach(float r in Neighbors(.125+sourceR,6))foreach(float g in Neighbors(.25+sourceG,6))
                    {
                        guideChoices.Add(new Color(g,.75f,.5f,7));
                        foreach(float finalB in Neighbors(r*2,5))colorChoices.Add(new Color(.75f,g*.5f,finalB,1));
                    }
                }
                Compare("srgb-unorm-midpoint-guide",baseline.attachments[2].target,(x,y)=>Color.clear,guideChoices.ToArray());
                Compare("srgb-unorm-midpoint-color",baseline.attachments[4].target,(x,y)=>Color.clear,colorChoices.ToArray());
                material.SetVector("_Base",new Vector4(1,0,0,1));material.SetVector("_Mos",new Vector4(0,1,0,1));

                var spatial=Plan(65,47);
                Material Variant(Color baseColor,bool pattern)
                {
                    var value=Own(new Material(material));value.SetColor("_Base",baseColor);
                    value.SetFloat("_Pattern",pattern?1:0);value.SetFloat("_Height",47);return value;
                }
                var far=Variant(Color.blue,false);var near=Variant(Color.red,true);var occluded=Variant(Color.green,false);
                var first=new TileRenderPass.Draw {mesh=mesh,material=far,localToWorld=Matrix4x4.Translate(new Vector3(0,0,-.25f))};
                var second=new TileRenderPass.Draw {mesh=mesh,material=near,localToWorld=Matrix4x4.TRS(new Vector3(0,0,.25f),Quaternion.identity,new Vector3(.5f,.5f,1))};
                var third=new TileRenderPass.Draw {mesh=mesh,material=occluded,localToWorld=Matrix4x4.Translate(new Vector3(0,0,-.375f))};
                spatial.subpasses[0].draws=new[]{first,second,third};
                Color Spatial(int x,int y)
                {
                    float u=(x+.5f)/65,v=(y+.5f)/47;
                    int cell=((x/4)+(int)(Mathf.Min(y+.5f,47-y-.5f)/4))%4;
                    if(u<.25f||u>=.75f||v<.25f||v>=.75f||cell==3)return new Color(.125f,.375f,2.75f);
                    return cell%2==0?new Color(.625f,.375f,.75f):new Color(.125f,1.375f,.75f);
                }
                Run("odd-spatial-depth-cutout",spatial,true);Outputs("odd-spatial-depth-cutout",spatial,Spatial);
                var spatialColor=ReadSceneTarget(spatial.attachments[4].target);
                spatial.subpasses[0].draws=new[]{third,second,first};
                Run("depth-order-independent",spatial);Outputs("depth-order-independent",spatial,Spatial);
                FrameworkCheck(report,"tile-pass-depth-order-exact-image",ScenePixelsEqual(spatialColor,ReadSceneTarget(spatial.attachments[4].target)));
                var host=Own(new GameObject("Explicit renderer tile geometry"));host.AddComponent<MeshFilter>().sharedMesh=mesh;
                var meshRenderer=host.AddComponent<MeshRenderer>();meshRenderer.sharedMaterial=near;meshRenderer.enabled=false;
                host.transform.position=new Vector3(0,0,.25f);host.transform.localScale=new Vector3(.5f,.5f,1);
                var rendererDraw=new TileRenderPass.Draw {renderer=meshRenderer,material=near};
                spatial.subpasses[0].draws=new[]{first,rendererDraw,third};
                Run("borrowed-renderer",spatial);Outputs("borrowed-renderer",spatial,Spatial);
                spatial.subpasses[0].draws=new[]{first,second,third};
                var originalSubpasses=spatial.subpasses;
                spatial.subpasses=new[]{originalSubpasses[0],new TileRenderPass.Subpass{colors=new[]{3},depthReadOnly=true},originalSubpasses[1],originalSubpasses[2]};
                Run("preserve-across-gap",spatial,true);Outputs("preserve-across-gap",spatial,Spatial);
                spatial.subpasses=originalSubpasses;

                var stored=Plan(64,64);
                foreach(var attachment in stored.attachments)
                {
                    if(attachment.target==null)attachment.target=Target(64,64,attachment.format,"Tile externally stored "+attachment.name);
                    attachment.store=true;
                }
                budget=Run("all-external-stored",stored,true);Outputs("all-external-stored",stored,(x,y)=>new Color(.625f,.375f,.75f));
                FrameworkCheck(report,"tile-pass-stored-versus-transient-exact",ScenePixelsEqual(original,ReadSceneTarget(stored.attachments[4].target)));
                FrameworkCheck(report,"tile-pass-all-stored-byte-accounting",budget.transientBytes==0 && budget.storedBytes==64*64*28 && budget.loadedBytes==0,budget.storedBytes);
                var consumerPasses=stored.subpasses;
                stored.subpasses=new[]{consumerPasses[0]};
                Run("store-producer-only",stored,true);
                stored.subpasses=consumerPasses;stored.subpasses[0].draws=Array.Empty<TileRenderPass.Draw>();
                foreach(var attachment in stored.attachments)attachment.initialContents=TileRenderPass.InitialContents.Load;
                budget=Run("load-prior-producer",stored,true);Outputs("load-prior-producer",stored,(x,y)=>new Color(.625f,.375f,.75f));
                FrameworkCheck(report,"tile-pass-load-byte-accounting",budget.loadedBytes==64*64*28,budget.loadedBytes);
                foreach(var attachment in stored.attachments)attachment.initialContents=TileRenderPass.InitialContents.Clear;
                Run("clear-replaces-stale",stored);Outputs("clear-replaces-stale",stored,(x,y)=>Color.clear);

                Run("before-rejections",baseline);
                var beforeRejection=ReadSceneTarget(baseline.attachments[4].target);

                // Each invalid layout is checked through TryRecord as well as Validate.
                // A default context would be invalid if recording ever got past validation.
                void Reject(string name,Action change,Action restore,string reason)
                {
                    try
                    {
                        change();bool valid=TileRenderPass.Validate(baseline,out var estimate,out string why);
                        bool recorded=renderer.TryRecord(default,baseline,out var result,out string error);
                        FrameworkCheck(report,"tile-pass-reject-"+name,!valid&&!recorded && estimate.sequence==0 && result.sequence==0 && why!=null&&why.Contains(reason)&&error==why);
                    }
                    finally { restore(); }
                }
                Reject("disabled",()=>baseline.enabled=false,()=>baseline.enabled=true,"disabled");
                Reject("backend-enum",()=>baseline.backend=(TileRenderPass.BackendPolicy)9,()=>baseline.backend=TileRenderPass.BackendPolicy.AllowEmulation,"layout");
                if(!TileRenderPass.HasNativeApi)Reject("native-on-emulation",()=>baseline.backend=TileRenderPass.BackendPolicy.RequireNative,()=>baseline.backend=TileRenderPass.BackendPolicy.AllowEmulation,"Native");
                else FrameworkCheck(report,"tile-pass-native-policy-validated",TileRenderPass.HasNativeApi);
                Reject("zero-width",()=>baseline.width=0,()=>baseline.width=64,"layout");
                Reject("oversize",()=>baseline.width=4097,()=>baseline.width=64,"layout");
                Reject("color-budget",()=>baseline.maximumColorTileBits=255,()=>baseline.maximumColorTileBits=256,"budget");
                Reject("draw-budget",()=>baseline.maximumDraws=2,()=>baseline.maximumDraws=4096,"draw budget");
                Reject("missing-attachment",()=>baseline.attachments[0]=null,()=>baseline.attachments[0]=new TileRenderPass.Attachment{format=GraphicsFormat.R8G8B8A8_SRGB},"descriptor");
                Reject("compressed",()=>baseline.attachments[0].format=GraphicsFormat.RGBA_DXT5_UNorm,()=>baseline.attachments[0].format=GraphicsFormat.R8G8B8A8_SRGB,"descriptor");
                Reject("nan-clear",()=>baseline.attachments[0].clearColor=new Color(float.NaN,0,0),()=>baseline.attachments[0].clearColor=Color.clear,"descriptor");
                Reject("depth-range",()=>baseline.attachments[5].clearDepth=2,()=>baseline.attachments[5].clearDepth=1,"descriptor");
                Reject("depth-role",()=>baseline.depthAttachment=0,()=>baseline.depthAttachment=5,"descriptor");
                Reject("transient-load",()=>baseline.attachments[0].initialContents=TileRenderPass.InitialContents.Load,()=>baseline.attachments[0].initialContents=TileRenderPass.InitialContents.Clear,"Transient");
                Reject("transient-store",()=>baseline.attachments[0].store=true,()=>baseline.attachments[0].store=false,"Transient");
                var guideTarget=baseline.attachments[2].target;
                Reject("target-size",()=>baseline.width=63,()=>baseline.width=64,"External");
                Reject("target-format",()=>baseline.attachments[2].format=GraphicsFormat.R32G32B32A32_SFloat,()=>baseline.attachments[2].format=GraphicsFormat.R16G16B16A16_SFloat,"External");
                Reject("target-alias",()=>baseline.attachments[3].target=baseline.attachments[4].target,()=>baseline.attachments[3].target=null,"unique");
                var uncreated=Own(new RenderTexture(guideTarget.descriptor));
                Reject("uncreated-target",()=>baseline.attachments[2].target=uncreated,()=>baseline.attachments[2].target=guideTarget,"created");
                var colors=baseline.subpasses[0].colors;var inputs=baseline.subpasses[1].inputs;
                Reject("color-duplicate",()=>baseline.subpasses[0].colors=new[]{0,0},()=>baseline.subpasses[0].colors=colors,"duplicate");
                Reject("color-depth",()=>baseline.subpasses[0].colors=new[]{5},()=>baseline.subpasses[0].colors=colors,"color index");
                Reject("input-range",()=>baseline.subpasses[1].inputs=new[]{6},()=>baseline.subpasses[1].inputs=inputs,"input attachment");
                Reject("input-duplicate",()=>baseline.subpasses[1].inputs=new[]{0,0},()=>baseline.subpasses[1].inputs=inputs,"duplicate");
                Reject("read-write-feedback",()=>baseline.subpasses[1].inputs=new[]{3},()=>baseline.subpasses[1].inputs=inputs,"simultaneous");
                Reject("writable-depth-input",()=>{baseline.subpasses[1].inputs=new[]{5};baseline.subpasses[1].depthReadOnly=false;},()=>{baseline.subpasses[1].inputs=inputs;baseline.subpasses[1].depthReadOnly=true;},"read-only");
                var attachments=baseline.attachments;
                Reject("unused-attachment",()=>{var more=new TileRenderPass.Attachment[7];Array.Copy(attachments,more,6);more[6]=new TileRenderPass.Attachment();baseline.attachments=more;baseline.maximumColorTileBits=512;},()=>{baseline.attachments=attachments;baseline.maximumColorTileBits=256;},"Unused");
                var draw=baseline.subpasses[0].draws[0];
                Reject("null-draw",()=>baseline.subpasses[0].draws[0]=null,()=>baseline.subpasses[0].draws[0]=draw,"draw source");
                Reject("both-draw-sources",()=>draw.renderer=meshRenderer,()=>draw.renderer=null,"draw source");
                Reject("invalid-pass",()=>draw.shaderPass=material.passCount,()=>draw.shaderPass=0,"material pass");
                Reject("invalid-submesh",()=>draw.submesh=1,()=>draw.submesh=0,"triangle submesh");
                Reject("singular-transform",()=>draw.localToWorld=Matrix4x4.zero,()=>draw.localToWorld=Matrix4x4.identity,"transform");
                Reject("sampled-attachment",()=>material.SetTexture("_ForbiddenSample",guideTarget),()=>material.SetTexture("_ForbiddenSample",null),"ordinary sampled");
                var block=new MaterialPropertyBlock();block.SetTexture("_ForbiddenSample",guideTarget);
                Reject("property-block-alias",()=>draw.properties=block,()=>draw.properties=null,"ordinary sampled");
                Reject("renderer-property-block",()=>{baseline.subpasses[0].draws[0]=rendererDraw;meshRenderer.SetPropertyBlock(block);},()=>{baseline.subpasses[0].draws[0]=draw;meshRenderer.SetPropertyBlock(null);},"implicit property block");
                baseline.chargePackedHdrAs64Bits=false;
                FrameworkCheck(report,"tile-pass-explicit-192-bit-policy",TileRenderPass.Validate(baseline,out budget,out _) && budget.colorTileBits==192,budget.colorTileBits);
                baseline.chargePackedHdrAs64Bits=true;
                FrameworkCheck(report,"tile-pass-invalid-plans-preserve-borrowed-outputs",ScenePixelsEqual(beforeRejection,ReadSceneTarget(baseline.attachments[4].target)));
                Run("restored-after-rejection",baseline);Outputs("restored-after-rejection",baseline,(x,y)=>new Color(.625f,.375f,.75f));
                renderer.Dispose();renderer.Dispose();
                FrameworkCheck(report,"tile-pass-disposed-rejects-before-context",!renderer.TryRecord(default,baseline,out _,out string disposed)&&disposed.Contains("Disposed"));
                // Keep borrowed GPU targets alive through the submitted frame, including
                // Direct rendering mode, before the fixture tears down its SRP host.
                yield return null;
                FrameworkCheck(report,"tile-pass-borrowed-lifetimes-retained",guideTarget.IsCreated()&&baseline.attachments[4].target.IsCreated()&&mesh.vertexCount==4&&material!=null&&meshRenderer!=null);
            }
            finally
            {
                GraphicsSettings.renderPipelineAsset=previousGraphics;QualitySettings.renderPipeline=previousQuality;
                // Submit leaves native attachments bound even when RenderTexture.active is
                // already null. Explicitly unbind before destroying the fixture's targets.
                Graphics.SetRenderTarget(previousActive);
                RenderTexture.active=previousActive;
                foreach(var item in _owned)if(item!=null)Destroy(item);_owned.Clear();
            }
            FrameworkCheck(report,"tile-pass-restores-default-pipelines",GraphicsSettings.renderPipelineAsset==previousGraphics&&QualitySettings.renderPipeline==previousQuality&&RenderTexture.active==previousActive);
        }
    }
}
