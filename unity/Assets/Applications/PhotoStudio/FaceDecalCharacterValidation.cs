using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Explicit local-data acceptance run. Uses generated decal art only; never runs in normal playback.</summary>
    public sealed class FaceDecalCharacterValidation : MonoBehaviour
    {
        [Serializable] private sealed class Check { public string name; public bool accepted; public double value; }
        [Serializable] private sealed class Report
        {
            public string schema="photo-studio.face-decal-character.v1", graphicsDevice, costume, error;
            public bool accepted; public int vertices, shapes;
            public List<Check> checks=new List<Check>();
        }
        private PhotoModeApp app; private string directory;
        private readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
        private T Own<T>(T value) where T:UnityEngine.Object { owned.Add(value);return value; }
        public static bool TryStart(PhotoModeApp app)
        {
            var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--validate-face-decal-character");
            if(index<0)return false;
            try
            {
                if(index+1>=args.Length||args[index+1].StartsWith("--",StringComparison.Ordinal)||!args.Contains("--photo-mode"))
                    throw new ArgumentException("--validate-face-decal-character requires --photo-mode and an output directory.");
                var component=app.gameObject.AddComponent<FaceDecalCharacterValidation>();component.app=app;component.directory=Path.GetFullPath(args[index+1]);
            }
            catch(Exception error){Debug.LogException(error);Application.Quit(3);}return true;
        }
        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(2);
            var report=new Report{graphicsDevice=SystemInfo.graphicsDeviceVersion,costume=app.CurrentCostume};
            FaceExpressionRenderer face=null;Camera camera=null;RenderTexture previousTarget=null;FaceDecalLayer layer=null;
            Vector3 previousPosition=Vector3.zero;Quaternion previousRotation=Quaternion.identity;
            bool oldGpu=false,wasPaused=app.IsPlaybackPaused;var skins=new Dictionary<SkinnedMeshRenderer,bool>();
            void Check(string name,bool ok,double value=0)=>report.checks.Add(new Check{name=name,accepted=ok,value=value});
            void Compare(string name,Color[] a,Color[] b,bool exact=false)
            {
                double max=0,sum=0;bool finite=true,alpha=true;
                for(int i=0;i<a.Length;i++)for(int c=0;c<4;c++){double d=Math.Abs((double)a[i][c]-b[i][c]);max=Math.Max(max,d);sum+=d;finite&=!double.IsNaN(d)&&!double.IsInfinity(d);if(c==3)alpha&=a[i].a==b[i].a;}
                Check(name+"-whole-image-max",finite&&max<=(exact?0:.02),max);
                Check(name+"-whole-image-mean",finite&&sum/(a.Length*4)<=(exact?0:.00005),sum/(a.Length*4));
                Check(name+"-alpha-exact",alpha);
            }
            try
            {
                Directory.CreateDirectory(directory);app.SetPlaybackPaused(true);app.EvaluateMotion(0);
                face=app.CharacterRoot.GetComponentInChildren<FaceExpressionRenderer>();
                if(face==null)throw new InvalidOperationException("Missing local face renderer.");
                camera=(Camera)typeof(FaceExpressionRenderer).GetField("_lookCamera",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(face);
                if(camera==null)throw new InvalidOperationException("Missing character camera.");
                previousTarget=camera.targetTexture;previousPosition=camera.transform.position;previousRotation=camera.transform.rotation;oldGpu=face.GpuDeformationEnabled;
                foreach(var skin in app.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>()){skins.Add(skin,skin.updateWhenOffscreen);skin.updateWhenOffscreen=true;}
                face.GpuDeformationEnabled=false;face.SetAutomaticBlinkEnabled(false);face.SetStoryBlink(-1);face.SelectPreset(0);face.SetStoryGaze(0,0);face.SendMessage("UpdateGaze");face.ApplyCurrentWeights();
                var filter=face.GetComponentInChildren<MeshFilter>();var receiver=filter.GetComponent<Renderer>();
                report.vertices=filter.sharedMesh.vertexCount;report.shapes=face.ShapeCount;
                Check("actual-local-character",report.vertices>1000&&report.shapes>100,report.vertices);
                var target=Own(new RenderTexture(512,512,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                var atlas=Own(new Texture2D(16,8,TextureFormat.RGBAFloat,false,true));atlas.wrapMode=TextureWrapMode.Clamp;atlas.filterMode=FilterMode.Bilinear;
                var pixels=new Color[128];for(int y=0;y<8;y++)for(int x=0;x<16;x++)pixels[y*16+x]=new Color(.85f,.08f+y*.014f,.22f+x*.02f,.15f+.65f*Mathf.Clamp01(1-Vector2.Distance(new Vector2((x+.5f)/16,(y+.5f)/8),new Vector2(.5f,.5f))*2));atlas.SetPixels(pixels);atlas.Apply();
                var bounds=receiver.bounds;
                var projector=Own(new GameObject("Independent authored cheek projection")).AddComponent<FaceDecalProjector>();
                projector.transform.position=bounds.center;projector.size=bounds.size*1.03f;projector.opacity=.55f;projector.edgeFeather=.08f;projector.uvRotationDegrees=13;
                var material=receiver.sharedMaterial;
                var surface=new SceneDepthData.Surface{renderer=receiver,cull=(CullMode)(int)material.GetFloat("_Cull"),
                    vertexScale=material.GetVector("_WardrobeScaleCorrection"),alphaMask=material.GetTexture("_MainTex"),
                    alphaCutoff=material.GetFloat("_UseAlphaClip")>.5f?material.GetFloat("_Cutoff"):0};
                // A face mesh can contain eye/highlight/skin submeshes. Register every
                // depth-writing face submesh explicitly instead of assuming slot0 is skin.
                var registered=receiver.sharedMaterials.Select((m,index)=>new {m,index}).Where(v=>v.m.GetFloat("_ZWrite")>.5f).Select(v=>
                    new FaceDecalRenderer.Receiver{surface=new SceneDepthData.Surface{renderer=receiver,materialIndex=v.index,
                        cull=(CullMode)(int)v.m.GetFloat("_Cull"),vertexScale=v.m.GetVector("_WardrobeScaleCorrection"),
                        alphaMask=v.m.GetTexture("_MainTex"),alphaCutoff=v.m.GetFloat("_UseAlphaClip")>.5f?v.m.GetFloat("_Cutoff"):0}}).ToArray();
                Check("explicit-depth-writing-face-submeshes",registered.Length>0,registered.Length);
                Debug.Log("[FaceDecalCharacterValidation] receiver="+receiver.name+" bounds="+bounds+" scale="+surface.vertexScale+" cull="+surface.cull+
                    " zwrite="+material.GetFloat("_ZWrite")+" type="+material.GetFloat("_ShaderType")+" clip="+surface.alphaCutoff+" submeshes="+filter.sharedMesh.subMeshCount+
                    " materials="+receiver.sharedMaterials.Length+" localBounds="+filter.sharedMesh.bounds+" transform="+receiver.localToWorldMatrix);
                layer=camera.gameObject.AddComponent<FaceDecalLayer>();layer.atlas=atlas;layer.projectors=new[]{projector};layer.receivers=registered;
                string before=Snapshot(receiver);var global=Shader.GetGlobalVector("_CapturedLightDirection");
                Color[] Render(string name=null){camera.Render();var result=Read(target);if(name!=null)Save(name,result);return result;}
                void View(float angle)
                {
                    var direction=Quaternion.Euler(0,angle,0)*Vector3.forward;
                    camera.transform.SetPositionAndRotation(bounds.center+direction*Mathf.Max(.8f,bounds.size.magnitude*1.5f),Quaternion.LookRotation(-direction));face.ApplyCurrentWeights();
                }
                View(0);layer.decalsEnabled=false;var neutral=Render("neutral-off");
                // Every local shape is exercised, not a preset proxy. The same current
                // camera/pose is used for CPU and GPU, with no pixel masks or outlier exclusions.
                for(int shape=0;shape<face.ShapeCount;shape++)
                {
                    face.GpuDeformationEnabled=false;face.SetDebugShape(shape,.75f);face.ApplyCurrentWeights();
                    layer.decalsEnabled=false;var off=Render();layer.decalsEnabled=true;var cpu=Render(shape==0?"shape-000-cpu-on":null);
                    Check("shape-"+shape+"-submitted",layer.SubmittedReceivers==registered.Length&&layer.UnavailableReason==null);
                    int changed=cpu.Where((c,i)=>Math.Abs(c.r-off[i].r)>.002).Count();Check("shape-"+shape+"-positive-overlay",changed>20,changed);
                    Check("shape-"+shape+"-overlay-alpha",cpu.Where((c,i)=>c.a!=off[i].a).Count()==0);
                    face.GpuDeformationEnabled=true;Check("shape-"+shape+"-active-gpu",face.IsGpuDeformationActive&&face.GpuDeformationUnavailableReason==null);
                    Compare("shape-"+shape+"-cpu-gpu",cpu,Render(shape==0?"shape-000-gpu-on":null));
                    face.GpuDeformationEnabled=false;layer.decalsEnabled=false;Compare("shape-"+shape+"-off-restored",off,Render(),true);
                    if(shape%12==0)Debug.Log("[FaceDecalCharacterValidation] completed shape "+shape+"/"+face.ShapeCount);
                }
                face.ClearDebugShape();face.SelectPreset(0);face.ApplyCurrentWeights();Compare("neutral-restored",neutral,Render("neutral-restored"),true);
                int view=0;
                foreach(float angle in new[]{0f,45f,90f,180f,-90f})
                {
                    face.GpuDeformationEnabled=false;app.EvaluateMotion(view%2==0?0:.7f);face.SelectPreset(view%4);face.ApplyCurrentWeights();View(angle);
                    layer.decalsEnabled=false;var off=Render("view-"+view+"-off");layer.decalsEnabled=true;var cpu=Render("view-"+view+"-cpu-on");
                    face.GpuDeformationEnabled=true;Compare("view-"+view+"-cpu-gpu",cpu,Render("view-"+view+"-gpu-on"));
                    Check("view-"+view+"-alpha-and-occlusion",cpu.Where((c,i)=>c.a!=off[i].a).Count()==0);
                    face.GpuDeformationEnabled=false;layer.decalsEnabled=false;Compare("view-"+view+"-default-restored",off,Render(),true);view++;
                }
                Check("source-material-block-global-intact",Snapshot(receiver)==before&&Shader.GetGlobalVector("_CapturedLightDirection")==global);
                face.SelectPreset(0);app.EvaluateMotion(0);face.ApplyCurrentWeights();
                VerifyPlanar(report,face,atlas,projector,registered,Compare,Check);
                report.accepted=report.checks.All(c=>c.accepted);
            }
            catch(Exception error){report.error=error.ToString();Debug.LogException(error);}
            finally
            {
                if(layer!=null){layer.enabled=false;Destroy(layer);}
                if(face!=null){face.GpuDeformationEnabled=oldGpu;face.ClearDebugShape();}
                if(camera!=null){camera.targetTexture=previousTarget;camera.transform.SetPositionAndRotation(previousPosition,previousRotation);}
                foreach(var pair in skins)if(pair.Key!=null)pair.Key.updateWhenOffscreen=pair.Value;
                foreach(var value in owned)if(value!=null){if(value is RenderTexture rt)rt.Release();Destroy(value);}app.SetPlaybackPaused(wasPaused);
            }
            File.WriteAllText(Path.Combine(directory,"face-decal-character.json"),JsonUtility.ToJson(report,true));
            Debug.Log("[FaceDecalCharacterValidation] accepted="+report.accepted+"; checks="+report.checks.Count);Application.Quit(report.accepted?0:2);
        }
        private void VerifyPlanar(Report report,FaceExpressionRenderer face,Texture atlas,FaceDecalProjector projector,FaceDecalRenderer.Receiver[] receivers,
            Action<string,Color[],Color[],bool> compare,Action<string,bool,double> check)
        {
            var renderers=app.CharacterRoot.GetComponentsInChildren<Renderer>().Where(r=>r.enabled&&!r.forceRenderingOff).ToArray();var bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
            float distance=Mathf.Max(1,bounds.size.y)*2;var center=bounds.center;var normal=Vector3.back;
            var host=Own(new GameObject("Authored decal real Planar camera"));var camera=host.AddComponent<Camera>();camera.enabled=false;camera.allowMSAA=false;camera.allowHDR=true;
            camera.fieldOfView=40;camera.nearClipPlane=.03f;camera.farClipPlane=distance*5;camera.cullingMask=1<<29;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.clear;
            var target=Own(new RenderTexture(512,512,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
            var mirror=Own(GameObject.CreatePrimitive(PrimitiveType.Quad));mirror.layer=29;mirror.transform.localScale=Vector3.one*distance*3;
            mirror.GetComponent<Renderer>().sharedMaterial=Own(new Material(Resources.Load<Shader>("StudioAccent")));
            var planar=host.AddComponent<PlanarReflection>();planar.reflectionsEnabled=true;planar.reflectedLayers=~0;planar.resolutionScale=1;planar.maximumRoughnessMip=0;
            planar.receivers=new[]{new PlanarReflection.Receiver{surface=new SceneDepthData.Surface{renderer=mirror.GetComponent<Renderer>()}}};
            planar.planePoint=center-normal*distance*.6f;planar.planeNormal=normal;mirror.transform.SetPositionAndRotation(planar.planePoint,Quaternion.LookRotation(-normal));
            camera.transform.position=center-normal*distance*.3f;camera.transform.LookAt(center-normal*distance*1.2f);
            var lighting=new ActorPlanarLighting{lightDirection=new Vector3(.3f,.4f,1),lightColor=Vector3.one*.8f,ambientColor=Vector3.one*.2f};
            using(var actors=new ActorPlanarCaptureSet())using(var decals=new FaceDecalRenderer())
            {
                Color[] Render(bool overlay,string name)
                {
                    planar.reflectedSurfaces=Array.Empty<PlanarReflection.Draw>();
                    if(!actors.TryRefresh(renderers,camera.worldToCameraMatrix*PlanarReflection.ReflectionMatrix(planar.planePoint,planar.planeNormal),lighting,out var error))throw new InvalidOperationException(error);
                    if(!decals.TryPrepare(atlas,new[]{projector.Snapshot()},receivers))throw new InvalidOperationException(decals.UnavailableReason);
                    planar.reflectedSurfaces=overlay?actors.Draws.Concat(decals.PlanarDraws()).ToArray():actors.Draws;camera.Render();
                    if(!planar.TryGetReflection(camera,512,512,out _))throw new InvalidOperationException(planar.UnavailableReason);
                    var capture=(RenderTexture)typeof(PlanarReflection).GetField("_capture",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(planar);
                    var result=Read(capture);Save(name,result);return result;
                }
                var off=Render(false,"planar-off");var cpu=Render(true,"planar-cpu-on");
                int changed=cpu.Where((c,i)=>Math.Abs(c.r-off[i].r)>.002).Count();check("planar-actual-overlay-color",changed>20,changed);
                check("planar-whole-coverage-exact",cpu.Where((c,i)=>c.a!=off[i].a).Count()==0,0);
                face.GpuDeformationEnabled=true;compare("planar-cpu-gpu",cpu,Render(true,"planar-gpu-on"),false);
                face.GpuDeformationEnabled=false;compare("planar-default-restored",off,Render(false,"planar-off-restored"),true);
                planar.enabled=false;planar.reflectedSurfaces=Array.Empty<PlanarReflection.Draw>();
            }
        }
        private static Color[] Read(RenderTexture target)
        {
            var before=RenderTexture.active;var copy=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true);
            try{RenderTexture.active=target;copy.ReadPixels(new Rect(0,0,target.width,target.height),0,0);copy.Apply();return copy.GetPixels();}
            finally{RenderTexture.active=before;Destroy(copy);}
        }
        private void Save(string name,Color[] pixels)
        {
            var image=new Texture2D(512,512,TextureFormat.RGBA32,false,true);
            try{image.SetPixels(QualitySettings.activeColorSpace==ColorSpace.Linear?pixels.Select(c=>c.gamma).ToArray():pixels);image.Apply();File.WriteAllBytes(Path.Combine(directory,name+".png"),image.EncodeToPNG());}
            finally{Destroy(image);}
        }
        private static string Snapshot(Renderer r)
        {
            var block=new MaterialPropertyBlock();r.GetPropertyBlock(block);var slot=new MaterialPropertyBlock();r.GetPropertyBlock(slot,0);
            var parts=new List<string>();
            foreach(var m in r.sharedMaterials)
            {
                parts.Add(m.GetInstanceID()+":"+m.shader.GetInstanceID()+":"+m.renderQueue+":"+string.Join(",",m.shaderKeywords.OrderBy(k=>k)));
                for(int i=0;i<m.shader.GetPropertyCount();i++)
                {
                    string name=m.shader.GetPropertyName(i);var type=m.shader.GetPropertyType(i);parts.Add(name);
                    if(type==ShaderPropertyType.Texture){var t=m.GetTexture(name);parts.Add((t==null?0:t.GetInstanceID())+":"+m.GetTextureScale(name).ToString("R")+":"+m.GetTextureOffset(name).ToString("R"));}
                    else if(type==ShaderPropertyType.Color||type==ShaderPropertyType.Vector)parts.Add(m.GetVector(name).ToString("R"));
                    else parts.Add(m.GetFloat(name).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                    foreach(var b in new[]{block,slot})
                    {
                        parts.Add(b.HasProperty(name).ToString());if(!b.HasProperty(name))continue;
                        if(type==ShaderPropertyType.Texture){var t=b.GetTexture(name);parts.Add((t==null?0:t.GetInstanceID()).ToString());}
                        else if(type==ShaderPropertyType.Color||type==ShaderPropertyType.Vector)parts.Add(b.GetVector(name).ToString("R"));
                        else parts.Add(b.GetFloat(name).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
            return string.Join("|",parts);
        }
    }
}
