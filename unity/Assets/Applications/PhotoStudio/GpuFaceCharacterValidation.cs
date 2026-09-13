using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Opt-in local-data validation, not part of normal application behavior.</summary>
    public sealed class GpuFaceCharacterValidation : MonoBehaviour
    {
        [Serializable] private sealed class Check { public string name; public bool accepted; public double value; }
        [Serializable] private sealed class Report
        {
            public string schema="photo-studio.gpu-face-character.v1",graphicsDevice,costume,error;
            public bool accepted; public int vertices,shapes; public long bufferBytes;
            public List<Check> checks=new List<Check>();
        }
        private PhotoModeApp app;private string directory;
        public static bool TryStart(PhotoModeApp app)
        {
            var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--validate-gpu-face-character");
            if(index<0)return false;
            try
            {
                if(index+1>=args.Length||args[index+1].StartsWith("--",StringComparison.Ordinal)||!args.Contains("--photo-mode"))
                    throw new ArgumentException("--validate-gpu-face-character requires --photo-mode and an output directory.");
                var check=app.gameObject.AddComponent<GpuFaceCharacterValidation>();check.app=app;check.directory=Path.GetFullPath(args[index+1]);
            }
            catch(Exception error){Debug.LogError(error);Application.Quit(3);}
            return true;
        }
        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(2);
            var report=new Report{graphicsDevice=SystemInfo.graphicsDeviceVersion,costume=app.CurrentCostume};
            Camera camera=null;RenderTexture target=null,oldTarget=null;FaceExpressionRenderer face=null;
            var skinStates=new Dictionary<SkinnedMeshRenderer,bool>();bool wasPaused=app.IsPlaybackPaused;
            Vector3 cameraPosition=Vector3.zero;Quaternion cameraRotation=Quaternion.identity;bool oldGpu=false;
            try
            {
                Directory.CreateDirectory(directory);app.SetPlaybackPaused(true);app.EvaluateMotion(0);
                face=app.CharacterRoot.GetComponentInChildren<FaceExpressionRenderer>();
                if(face==null)throw new InvalidOperationException("No local face renderer.");
                camera=(Camera)typeof(FaceExpressionRenderer).GetField("_lookCamera",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(face);
                if(camera==null)throw new InvalidOperationException("No actual character camera.");
                oldTarget=camera.targetTexture;cameraPosition=camera.transform.position;cameraRotation=camera.transform.rotation;oldGpu=face.GpuDeformationEnabled;
                foreach(var skin in app.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>()){skinStates.Add(skin,skin.updateWhenOffscreen);skin.updateWhenOffscreen=true;}
                face.GpuDeformationEnabled=false;face.SetAutomaticBlinkEnabled(false);face.SetStoryBlink(-1);face.SelectPreset(0);
                var filter=face.GetComponentInChildren<MeshFilter>();
                if(filter==null)throw new InvalidOperationException("No local face mesh.");
                report.vertices=filter.sharedMesh.vertexCount;report.shapes=face.ShapeCount;
                target=new RenderTexture(512,512,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){name="Local face CPU GPU comparison"};target.Create();camera.targetTexture=target;
                void Check(string name,bool ok,double value=0)=>report.checks.Add(new Check{name=name,accepted=ok,value=value});
                bool dump=Environment.GetEnvironmentVariable("GAKUMAS_GPU_FACE_DIAGNOSTICS")=="1";
                void Raw(string name,float[] values)
                {
                    if(!dump)return;var bytes=new byte[values.Length*4];Buffer.BlockCopy(values,0,bytes,0,bytes.Length);
                    File.WriteAllBytes(Path.Combine(directory,name+".raw"),bytes);
                }
                Color[] Render(string name)
                {
                    camera.Render();var old=RenderTexture.active;var copy=new Texture2D(512,512,TextureFormat.RGBAFloat,false,true);
                    try
                    {
                        RenderTexture.active=target;copy.ReadPixels(new Rect(0,0,512,512),0,0);copy.Apply();var pixels=copy.GetPixels();
                        if(dump)Raw(name+"-rgba",pixels.SelectMany(c=>new[]{c.r,c.g,c.b,c.a}).ToArray());
                        var image=new Texture2D(512,512,TextureFormat.RGBA32,false,true);
                        try{image.SetPixels(QualitySettings.activeColorSpace==ColorSpace.Linear?pixels.Select(c=>c.gamma).ToArray():pixels);image.Apply();File.WriteAllBytes(Path.Combine(directory,name+".png"),image.EncodeToPNG());}
                        finally{Destroy(image);}
                        return pixels;
                    }
                    finally{RenderTexture.active=old;Destroy(copy);}
                }
                Check("actual-local-data-not-triangle-fixture",report.vertices>1000&&report.shapes>100,report.vertices);
                var cases=new[]{(0f,0,0f),(45f,2,.35f),(90f,5,.7f),(180f,0,0f),(-90f,6,.35f),(0f,3,.7f)};
                for(int i=0;i<cases.Length;i++)
                {
                    var sample=cases[i];face.GpuDeformationEnabled=false;app.EvaluateMotion(sample.Item3);face.SelectPreset(sample.Item2);
                    face.SetStoryGaze(i%2==0?8:-8,i%3==0?4:-3);face.SetStoryBlink(i==5?.5f:-1);
                    face.SendMessage("UpdateGaze");face.ApplyCurrentWeights();
                    var renderer=filter.GetComponent<Renderer>();var bounds=renderer.bounds;
                    var direction=Quaternion.Euler(0,sample.Item1,0)*Vector3.back;
                    camera.transform.SetPositionAndRotation(bounds.center+direction*Mathf.Max(.8f,bounds.size.magnitude*1.5f),Quaternion.LookRotation(-direction));
                    face.ApplyCurrentWeights();
                    var p=filter.sharedMesh.vertices;var n=filter.sharedMesh.normals;var t=filter.sharedMesh.tangents;
                    var cpu=Render("case-"+i+"-cpu");float profile=face.ViewProfileWeight;
                    face.GpuDeformationEnabled=true;
                    Check("case-"+i+"-active-gpu",face.IsGpuDeformationActive&&face.GpuDeformationUnavailableReason==null);
                    if(!face.IsGpuDeformationActive)throw new InvalidOperationException(face.GpuDeformationUnavailableReason);
                    report.bufferBytes=face.GpuDeformationBufferBytes;
                    var raw=ReadStreams(filter.sharedMesh);double maximum=0;
                    if(dump)
                    {
                        Raw("case-"+i+"-cpu-pnt",Enumerable.Range(0,p.Length).SelectMany(v=>new[]{p[v].x,p[v].y,p[v].z,n[v].x,n[v].y,n[v].z,t[v].x,t[v].y,t[v].z,t[v].w}).ToArray());
                        Raw("case-"+i+"-gpu-pnt",raw.SelectMany(v=>v).ToArray());
                        var owner=typeof(FaceExpressionRenderer).GetField("_gpuFace",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(face);
                        foreach(var field in new[]{"restBuffer","entriesBuffer","startsBuffer","influenceBuffer","weightsBuffer","bonesBuffer"})
                        {
                            var buffer=(GraphicsBuffer)typeof(GpuFaceDeformer).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);
                            var words=new uint[buffer.count*buffer.stride/4];buffer.GetData(words);var bytes=new byte[words.Length*4];Buffer.BlockCopy(words,0,bytes,0,bytes.Length);
                            File.WriteAllBytes(Path.Combine(directory,"case-"+i+"-"+field+".raw"),bytes);
                        }
                    }
                    for(int v=0;v<p.Length;v++)for(int c=0;c<3;c++)
                    {
                        maximum=Math.Max(maximum,Math.Abs((double)raw[v][c]-p[v][c]));
                        if(n.Length==p.Length)maximum=Math.Max(maximum,Math.Abs((double)raw[v][c+3]-n[v][c]));
                        if(t.Length==p.Length)maximum=Math.Max(maximum,Math.Abs((double)raw[v][c+6]-t[v][c]));
                    }
                    Check("case-"+i+"-all-vertex-directions",maximum<=.00002,maximum);
                    Check("case-"+i+"-view-correction-preserved",face.ViewProfileWeight==profile,profile);
                    var gpu=Render("case-"+i+"-gpu");maximum=0;double sum=0;bool finite=true;
                    for(int v=0;v<cpu.Length;v++)for(int c=0;c<4;c++)
                    {double d=Math.Abs((double)cpu[v][c]-gpu[v][c]);maximum=Math.Max(maximum,d);sum+=d;finite&=!double.IsNaN(d)&&!double.IsInfinity(d);}
                    Check("case-"+i+"-whole-image-max",finite&&maximum<=.02,maximum);
                    Check("case-"+i+"-whole-image-mean",finite&&sum/(cpu.Length*4)<=.00005,sum/(cpu.Length*4));
                    face.GpuDeformationEnabled=false;var restored=Render("case-"+i+"-cpu-restored");
                    Check("case-"+i+"-cpu-default-restored-exact",cpu.SequenceEqual(restored));
                }
                // Report submission costs separately; not an end-to-end GPU frame-time benchmark.
                face.GpuDeformationEnabled=false;var timer=System.Diagnostics.Stopwatch.StartNew();
                for(int i=0;i<60;i++){face.SetDebugShape(i%face.ShapeCount,.35f+(i%3)*.1f);face.ApplyCurrentWeights();}timer.Stop();
                Check("cpu-60-shape-changes-observed-ms",timer.Elapsed.TotalMilliseconds>0,timer.Elapsed.TotalMilliseconds);
                face.GpuDeformationEnabled=true;timer.Restart();
                for(int i=0;i<60;i++){face.SetDebugShape(i%face.ShapeCount,.35f+(i%3)*.1f);face.ApplyCurrentWeights();}timer.Stop();
                Check("gpu-submit-60-shape-changes-observed-ms",timer.Elapsed.TotalMilliseconds>0,timer.Elapsed.TotalMilliseconds);
                report.accepted=report.checks.All(c=>c.accepted);
            }
            catch(Exception error){report.error=error.ToString();Debug.LogException(error);}
            finally
            {
                if(face!=null){face.GpuDeformationEnabled=oldGpu;face.ClearDebugShape();}
                if(camera!=null){camera.targetTexture=oldTarget;camera.transform.SetPositionAndRotation(cameraPosition,cameraRotation);}
                foreach(var entry in skinStates)if(entry.Key!=null)entry.Key.updateWhenOffscreen=entry.Value;
                if(target!=null){target.Release();Destroy(target);}app.SetPlaybackPaused(wasPaused);
            }
            File.WriteAllText(Path.Combine(directory,"gpu-face-character.json"),JsonUtility.ToJson(report,true));
            Debug.Log("[GpuFaceCharacterValidation] accepted="+report.accepted+"; checks="+report.checks.Count);Application.Quit(report.accepted?0:2);
        }
        private static float[][] ReadStreams(Mesh mesh)
        {
            var bytes=new byte[mesh.vertexBufferCount][];
            for(int s=0;s<bytes.Length;s++)using(var b=mesh.GetVertexBuffer(s)){var data=new uint[mesh.vertexCount*mesh.GetVertexBufferStride(s)/4];b.GetData(data);bytes[s]=new byte[data.Length*4];Buffer.BlockCopy(data,0,bytes[s],0,bytes[s].Length);}
            var result=new float[mesh.vertexCount][];var attrs=new[]{UnityEngine.Rendering.VertexAttribute.Position,UnityEngine.Rendering.VertexAttribute.Normal,UnityEngine.Rendering.VertexAttribute.Tangent};
            for(int v=0;v<result.Length;v++)
            {
                result[v]=new float[10];
                for(int a=0;a<3;a++)if(mesh.HasVertexAttribute(attrs[a]))
                {
                    int s=mesh.GetVertexAttributeStream(attrs[a]),offset=v*mesh.GetVertexBufferStride(s)+mesh.GetVertexAttributeOffset(attrs[a]);
                    for(int c=0;c<(a==2?4:3);c++)result[v][a*3+c]=BitConverter.ToSingle(bytes[s],offset+c*4);
                }
            }
            return result;
        }
    }
}
