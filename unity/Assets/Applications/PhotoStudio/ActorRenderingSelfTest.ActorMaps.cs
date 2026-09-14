using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        // Independent CPU texel addressing. No shader helper or GPU output enters
        // the expected material equation; all maps are authored linear float data.
        private sealed class ActorMapTexture
        {
            public Texture2D texture;
            public Color[][] levels;
            public bool fixedPointBilinear;
            public Color Sample(Vector2 uv, int level = 0)
            {
                int w = Math.Max(1, texture.width >> level), h = Math.Max(1, texture.height >> level);
                int Address(int p, int n) => texture.wrapMode == TextureWrapMode.Repeat ? (p % n + n) % n : Math.Min(n - 1, Math.Max(0, p));
                Color Texel(int x, int y) => levels[level][Address(y, h) * w + Address(x, w)];
                if (texture.filterMode == FilterMode.Point) return Texel(Mathf.FloorToInt(uv.x * w), Mathf.FloorToInt(uv.y * h));
                float u = uv.x * w - .5f, v = uv.y * h - .5f;
                if (fixedPointBilinear) { u = Mathf.Round(u * 256) / 256; v = Mathf.Round(v * 256) / 256; }
                int ix = Mathf.FloorToInt(u), iy = Mathf.FloorToInt(v); float a = u - ix, b = v - iy;
                // Native NVIDIA/D3D11 profile: quantized cross weight with exact
                // constant and linear marginals. Rounding four weights separately
                // loses those invariants at ties. No observed output is read here.
                float cross = fixedPointBilinear ? Mathf.Floor(a * b * 256 + .5f) / 256 : a * b;
                return Texel(ix, iy) * (1 - a - b + cross) + Texel(ix + 1, iy) * (a - cross)
                    + Texel(ix, iy + 1) * (b - cross) + Texel(ix + 1, iy + 1) * cross;
            }
        }

        private IEnumerator VerifyActorAuthoredMaps(Report report)
        {
            yield return null;
            void Check(string name, bool accepted, float error = 0) => FrameworkCheck(report, "actor-maps-" + name, accepted, error);
            var previous = FindObjectsOfType<Renderer>(); var forced = previous.Select(r => r.forceRenderingOff).ToArray();
            foreach (var r in previous) r.forceRenderingOff = true;
            string[] floats = { "_FaceDebugMode", "_UseCapturedActorShadow", "_CapturedActorTextureLodBias", "_CapturedDiffuseBlend", "_CapturedDirectScale",
                "_UseCapturedReceiverNormal", "_UseCapturedDirectSpecular", "_CapturedSkinSaturation", "_ActorEnvironmentIntensity", "_ActorAdditionalLightCount",
                "_CapturedActorOutputScale", "_CapturedType1DebugStage", "_CapturedType1OutputScale", "_CapturedType1BodyOutputScale", "_CapturedType1HairOutputScale",
                "_UseExactViewRimBasis", "_UseCapturedAmbientSH", "_FaceDecalCount" };
            float[] savedFloats = Array.ConvertAll(floats, Shader.GetGlobalFloat);
            string[] vectors = { "_CapturedLightDirection", "_CapturedCameraUp", "_CapturedShadeTint", "_ActorMatcapParameters", "_HeadRightDirection", "_HeadUpDirection",
                "_HeadDirection", "_ActorLightingScales", "_ActorKeyColor", "_CapturedLightColor", "_CapturedShadeAdditive", "_ActorRimColor", "_CapturedRimDirection", "_CapturedRimParameters" };
            var savedVectors = Array.ConvertAll(vectors, Shader.GetGlobalVector); var active = RenderTexture.active;
            try
            {
                const int size = 64;
                var material = Own(new Material(MaterialRepairer.FallbackShader()) { name = "Authored actor maps fixture" });
                material.SetFloat("_Cull", 0); material.SetFloat("_OutlineEnabled", 0); material.SetFloat("_VertexColor", 1);
                material.SetFloat("_DisableDefMap", 0); material.SetFloat("_StencilComp", 8); material.SetFloat("_StencilWriteMask", 0);
                material.SetFloat("_UseBump", 0); material.SetFloat("_UseReflection", 0); material.SetFloat("_UseEmission", 0); material.SetFloat("_EnableLayer", 0);
                material.SetVector("_Color", new Vector4(.75f, 1.25f, .5f, 1)); material.SetColor("_RampAddColor", Color.white);
                material.SetVector("_ActorColor", new Vector4(.8f, .6f, 1.2f, 1));
                var mesh = Own(new Mesh { name = "Authored actor map full-frame plane" });
                mesh.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }; mesh.uv2 = mesh.uv;
                mesh.colors32 = new[] { new Color32(17, 0x30, 5, 0), new Color32(17, 0x30, 5, 0), new Color32(17, 0xaf, 5, 0), new Color32(17, 0xaf, 5, 0) };
                mesh.triangles = new[] { 0,2,1,0,3,2 }; mesh.RecalculateBounds();
                var actor = Own(new GameObject("Authored actor map plane")); actor.layer = 30;
                actor.AddComponent<MeshFilter>().sharedMesh = mesh; actor.AddComponent<MeshRenderer>().sharedMaterial = material;
                var camera = Own(new GameObject("Authored actor map camera")).AddComponent<Camera>();
                camera.enabled = false; camera.allowHDR = true; camera.allowMSAA = false; camera.renderingPath = RenderingPath.Forward; camera.cullingMask = 1 << 30;
                camera.orthographic = true; camera.orthographicSize = 1; camera.aspect = 1; camera.transform.position = new Vector3(0,0,-3);
                camera.nearClipPlane = .1f; camera.farClipPlane = 20; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.magenta;
                var targets = Enumerable.Range(0,5).Select(i => { var t=Own(new RenderTexture(size,size,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));t.Create();return t; }).ToArray();
                var target=targets[4];camera.targetTexture=target;
                foreach (string n in floats) Shader.SetGlobalFloat(n, 0);
                foreach (string n in new[] { "_CapturedDiffuseBlend", "_CapturedDirectScale", "_UseCapturedReceiverNormal", "_UseCapturedDirectSpecular",
                    "_CapturedActorOutputScale", "_CapturedType1OutputScale", "_CapturedType1BodyOutputScale", "_CapturedType1HairOutputScale" }) Shader.SetGlobalFloat(n, 1);
                Shader.SetGlobalVector("_CapturedCameraUp", Vector3.up); Shader.SetGlobalVector("_ActorLightingScales", Vector4.zero);
                Shader.SetGlobalVector("_CapturedShadeAdditive", Vector4.zero); Shader.SetGlobalVector("_ActorRimColor", Vector4.zero);
                Shader.SetGlobalVector("_CapturedRimDirection", Vector3.back); Shader.SetGlobalVector("_CapturedRimParameters", new Vector4(0,0,2,0));
                var tint = new Color(.8f,.65f,1.1f,1); var key = new Color(.9f,1.2f,.7f,1);
                Shader.SetGlobalVector("_CapturedShadeTint", tint); Shader.SetGlobalVector("_ActorKeyColor", key); Shader.SetGlobalVector("_CapturedLightColor", Vector4.one);
                ActorMapTexture Make(string name, int width, int height, bool mips, Func<float,float,int,Color> paint)
                {
                    // ForceEnable quality may upgrade anisoLevel1 to native anisotropic9.
                    // The oracle's Point/Bilinear contract explicitly requests zero.
                    var t = Own(new Texture2D(width,height,TextureFormat.RGBAFloat,mips,true) { name = "Authored actor " + name, anisoLevel = 0 });
                    var result = new ActorMapTexture { texture = t, levels = new Color[t.mipmapCount][],
                        fixedPointBilinear = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 && SystemInfo.graphicsDeviceVendorID == 0x10de };
                    for (int level = 0; level < t.mipmapCount; level++)
                    {
                        int w = Math.Max(1,width >> level), h = Math.Max(1,height >> level); var pixels = result.levels[level] = new Color[w*h];
                        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) pixels[y*w+x] = paint((x+.5f)/w,(y+.5f)/h,level);
                        t.SetPixels(pixels,level);
                    }
                    t.Apply(false,false); return result;
                }
                Color Paint(int map, float u, float v, int level, bool mips)
                {
                    if (mips) { u = .125f + level * .1f; v = .875f - level * .08f; }
                    switch (map)
                    {
                        case 0: return new Color(.12f+.5f*u,.2f+.3f*v,.08f+.2f*u*v,1);
                        case 1: return new Color(.6f-.35f*v,.07f+.25f*u,.4f-.2f*u*v,u);
                        case 2: return new Color(.08f+.82f*u,.1f+.75f*v,.05f+.85f*u*v,.1f+.8f*(1-u)*v);
                        case 3: return new Color(.8f-.3f*v,.5f+.25f*u,.15f+.45f*v,1);
                        case 4: return new Color(.3f+.55f*u+.04f*v,.2f+.65f*u*u,.15f+.75f*Mathf.Sqrt(u),1-u*(.7f+.3f*v));
                        default: return new Color(.03f+.2f*v,.02f+.18f*u,.15f-.1f*u*v,.8f*v);
                    }
                }
                string[] properties = { "_MainTex", "_ShadeTex", "_DefTex", "_HighlightTex", "_RampTex", "_RampAddTex" };
                var spatial = Enumerable.Range(0,6).Select(i => Make(properties[i],i==4?32:8,i>=4?4:8,false,(u,v,l)=>Paint(i,u,v,l,false))).ToArray();
                var mipMaps = Enumerable.Range(0,4).Select(i => Make(properties[i]+" explicit mip chain",64,64,true,(u,v,l)=>Paint(i,u,v,l,true))).Concat(spatial.Skip(4)).ToArray();
                var lectureSizes=spatial.Take(4).Concat(new[]{Make("_RampTex 1024x4",1024,4,false,(u,v,l)=>Paint(4,u,v,l,false)),
                    Make("_RampAddTex 128x16",128,16,false,(u,v,l)=>Paint(5,u,v,l,false))}).ToArray();
                void Raw(string name, Color[] pixels)
                { using var writer = new BinaryWriter(File.Create(Path.Combine(_directory,"actor-maps-"+name+".raw"))); foreach (var p in pixels) for (int c = 0; c < 4; c++) writer.Write(p[c]); }
                float Difference(Color[] a, Color[] b)
                {
                    float e = 0; if (a.Length != b.Length) return float.MaxValue;
                    for (int i=0;i<a.Length;i++) for (int c=0;c<4;c++) { float d=Mathf.Abs(a[i][c]-b[i][c]); if(float.IsNaN(d)||float.IsInfinity(d))return float.MaxValue; e=Mathf.Max(e,d); } return e;
                }
                int cases = 0;
                for (int ci = 0; ci < 30; ci++)
                {
                    bool mips = ci >= 16 && ci < 22, large = ci >= 22;int type=mips?(ci<19?0:8):new[]{0,8,9,1}[large?(ci-22)%4:ci%4];
                    int sampler = mips?1:large?(ci<26?0:3):ci/4, bias = mips ? (ci-16)%3-1 : 0, mip = mips ? 2+bias : 0;
                    bool repeat = sampler >= 2, bilinear = (sampler%2)==1; int variant = type==1?2:0;
                    string name = (large?"lecture-size-":"")+(mips?"mip-"+bias:"sampler-"+sampler)+"-type-"+type;
                    var maps = mips?mipMaps:large?lectureSizes:spatial;
                    for(int i=0;i<maps.Length;i++) { maps[i].texture.filterMode=bilinear?FilterMode.Bilinear:FilterMode.Point; maps[i].texture.wrapMode=repeat?TextureWrapMode.Repeat:TextureWrapMode.Clamp; material.SetTexture(properties[i],maps[i].texture); }
                    var st = mips?new Vector4(4,4,0,0):new Vector4(1.5f,1.25f,-.1875f,.0625f);
                    material.SetTextureScale("_MainTex",new Vector2(st.x,st.y)); material.SetTextureOffset("_MainTex",new Vector2(st.z,st.w));
                    material.SetFloat("_ShaderType",type); material.SetFloat("_CapturedType1Variant",variant);
                    Shader.SetGlobalFloat("_CapturedActorTextureLodBias",bias);
                    var n = new Vector3(.6f,0,-.8f); mesh.normals = new[]{n,n,n,n};
                    var light = new Vector3(.25f,.1f,1).normalized;
                    Shader.SetGlobalVector("_CapturedLightDirection",new Vector4(light.x,light.y,light.z,0));
                    Shader.SetGlobalVector("_ActorMatcapParameters",new Vector4(.3f,.85f,.7f,0));
                    var headRotation = Quaternion.Euler(0,-38,0); var headRight = headRotation*Vector3.left; var headUp = Vector3.up; var headForward = headRotation*Vector3.forward;
                    Shader.SetGlobalVector("_HeadRightDirection",headRight); Shader.SetGlobalVector("_HeadUpDirection",headUp); Shader.SetGlobalVector("_HeadDirection",headForward);
                    float threshold = bilinear?.55f:sampler>=2?.95f:.15f, softness = bilinear?.12f:0;
                    material.SetVector("_SpecularThreshold",new Vector4(threshold,softness,0,0));
                    var expected = new Color[5][]; for(int i=0;i<5;i++) expected[i]=new Color[size*size];
                    var receiver = new Vector3(n.x,n.y,-n.z); var half=(light+Vector3.forward).normalized;
                    float nh=Mathf.Clamp01(Vector3.Dot(receiver,half)), nl=Mathf.Clamp01(Vector3.Dot(receiver,light)), nv=Mathf.Clamp01(-n.z);
                    var headNormal=(headRight*n.x+headUp*n.y+headForward*n.z).normalized;
                    var headReceiver=new Vector3(headNormal.x,headNormal.y,-headNormal.z);
                    for(int y=0;y<size;y++) for(int x=0;x<size;x++)
                    {
                        Vector2 uv=new Vector2((x+.5f)/size*st.x+st.z,(y+.5f)/size*st.y+st.w);
                        Color b=maps[0].Sample(uv,mip),s=maps[1].Sample(uv,mip),d=maps[2].Sample(uv,mip);
                        bool hair=type==8,skin=type==9,accessory=uv.x>.75f&&uv.y>.75f,detail=type!=1;
                        if(hair&&!accessory)
                        {
                            float response=Mathf.Pow(nh,4), gate=softness==0?(response>=threshold?1:0):Mathf.Clamp01((response-(threshold-softness))/(2*softness));
                            if(softness>0)gate=gate*gate*(3-2*gate);
                            b=Color.LerpUnclamped(b,maps[3].Sample(uv,mip),gate*Mathf.Clamp01(d.a));
                        }
                        Color add=detail?maps[5].Sample(new Vector2(Mathf.Clamp01(2*d.r-1+Vector3.Dot(n,Vector3.back)),(y+.5f)/size)):Color.clear;
                        b+=add*(1-add.a); s+=add*(1-add.a);
                        // Alpha remains the original shade mask; RGB additions do not alter it.
                        s.a=maps[1].Sample(uv,mip).a;
                        float common=Mathf.Clamp01(.5f*Vector3.Dot(receiver,light)+d.r-.15f);
                        float head=Mathf.Clamp01(.5f*Vector3.Dot(headReceiver,light)+d.r-.15f);
                        float coordinate=skin?Mathf.Lerp(common,Mathf.Max(common,head),Mathf.Clamp01(d.b)):common;
                        Color ramp=maps[4].Sample(new Vector2(coordinate,0));
                        Color cloth=Color.LerpUnclamped(b,s*tint,Mathf.Clamp01(ramp.a)*.7f);
                        Color skinColor=b*Color.LerpUnclamped(Color.white,ramp*Color.LerpUnclamped(Color.white,tint,Mathf.Clamp01(ramp.a)),.7f);
                        Color diffuse=Color.LerpUnclamped(cloth,skinColor,Mathf.Clamp01(s.a))*new Color(.75f,1.25f,.5f,1);
                        float metallic=skin?0:Mathf.Clamp01(d.b),smoothness=Mathf.Clamp01(d.g*.85f),a=Mathf.Max((1-smoothness)*(1-smoothness),.0078125f),a2=a*a;
                        Color direct=diffuse*(.96f*(1-metallic));
                        Color f0=Color.LerpUnclamped(new Color(.04f,.04f,.04f,1),diffuse,metallic);
                        float grazing=Mathf.Clamp01(smoothness+1-.96f*(1-metallic));
                        Color brdf=Color.LerpUnclamped(f0,new Color(grazing,grazing,grazing,1),Mathf.Pow(1-nv,4))/(1+a2);
                        float denominator=nh*nh*(a2-1)+1.00001f;
                        float spec=a2/Mathf.Max(denominator*denominator*Mathf.Max(Mathf.Pow(Vector3.Dot(light,half),2),.1f)*(4*a+2),1e-6f)*nl;
                        Color specular=brdf*(spec*Mathf.Clamp01(d.a)*(hair&&!accessory?0:1))*Color.LerpUnclamped(Color.white,add,detail?Mathf.Clamp01(add.a):0);
                        Color lit=(direct+specular)*key,final=lit*new Color(.8f,.6f,1.2f,1);
                        int index=y*size+x; Color[] values={diffuse,direct,specular,lit,final};
                        for(int stage=0;stage<5;stage++){values[stage].a=1;expected[stage][index]=values[stage];}
                    }
                    bool capture=Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_ACTOR_MAPS")=="1" && (ci==0||ci==13||ci==14||ci==21||ci==28);
                    int[] modes={16,19,20,23,0};for(int stage=0;stage<modes.Length;stage++)targets[stage].name="Authored actor maps "+name+"-stage-"+modes[stage];
                    bool began=false,ended=false; if(capture){FsrCaptureDrain(target);began=RenderDocCaptureBridge.BeginOffscreenCapture();}
                    try
                    {
                        for(int stage=0;stage<modes.Length;stage++)
                        {
                            string label=name+"-stage-"+modes[stage];target=targets[stage];camera.targetTexture=target;Shader.SetGlobalFloat("_FaceDebugMode",modes[stage]);camera.Render();
                            var actual=ReadSceneTarget(target);float e=Difference(expected[stage],actual);Check(label+"-whole-independent",e<=.0003f,e);
                            Check(label+"-whole-coverage",actual.All(p=>p.a==1));Raw(label,actual);Raw(label+"-expected",expected[stage]);
                            if(stage==4) {SaveSsrPreview("actor-maps-"+name,actual,size,size,false);Check(name+"-positive-nonconstant",actual.Max(p=>p.r)-actual.Min(p=>p.r)>.005f);}
                        }
                        if(capture)FsrCaptureDrain(target);
                    }
                    finally{if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    if(capture)Check(name+"-native",began&&ended);
                    if(ci==13)
                    {
                        var baseline=ReadSceneTarget(target);var quality=QualitySettings.anisotropicFiltering;
                        target.name="Authored actor maps "+name+"-forced-anisotropy";bool anisoBegan=false,anisoEnded=false;
                        try
                        {
                            QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;foreach(var map in maps)map.texture.anisoLevel=1;
                            if(capture){FsrCaptureDrain(target);anisoBegan=RenderDocCaptureBridge.BeginOffscreenCapture();}
                            camera.Render();var filtered=ReadSceneTarget(target);Raw(name+"-forced-anisotropy",filtered);
                            Check("forced-anisotropy-is-different-input",Difference(baseline,filtered)>.001f,Difference(baseline,filtered));
                            if(capture)FsrCaptureDrain(target);
                        }
                        finally
                        {
                            if(anisoBegan)anisoEnded=RenderDocCaptureBridge.EndOffscreenCapture();
                            foreach(var map in maps)map.texture.anisoLevel=0;QualitySettings.anisotropicFiltering=quality;
                        }
                        if(capture)Check(name+"-forced-anisotropy-native",anisoBegan&&anisoEnded);
                        camera.Render();Check("zero-anisotropy-restores-whole-frame",Difference(baseline,ReadSceneTarget(target))==0);
                    }
                    cases++;
                    yield return null;
                }
                Check("all-whole-map-cases",cases==30,cases);
            }
            finally
            {
                RenderTexture.active=active;
                for(int i=0;i<floats.Length;i++)Shader.SetGlobalFloat(floats[i],savedFloats[i]);
                for(int i=0;i<vectors.Length;i++)Shader.SetGlobalVector(vectors[i],savedVectors[i]);
                for(int i=0;i<previous.Length;i++)if(previous[i]!=null)previous[i].forceRenderingOff=forced[i];
                foreach(var item in _owned)if(item!=null)Destroy(item);_owned.Clear();
            }
        }
    }
}
