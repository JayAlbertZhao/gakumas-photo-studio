using System;
using System.IO;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifyAdditionalAuthoring(Report report)
        {
            var saved=Own(new Material(_material));var normals=_quad.normals;var uv=_quad.uv;var vertices=_quad.vertices;
            var p=new ActorForwardParameters();
            void Check(string name,bool ok,float error=0)=>FrameworkCheck(report,"actor-light-authoring-"+name,ok,error);
            var baseMap=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            var shade=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
            var ramp=Own(new Texture2D(4,1,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});
            var albedo=new Vector3(.2f,.35f,.6f);baseMap.SetPixel(0,0,new Color(albedo.x,albedo.y,albedo.z,1));baseMap.Apply();
            shade.SetPixel(0,0,new Color(.07f,.1f,.2f,1));shade.Apply();
            float[] rampValues={.2f,.4f,.7f,1};for(int i=0;i<4;i++)ramp.SetPixel(i,0,new Color(rampValues[i],rampValues[i],rampValues[i],0));ramp.Apply();
            Color[] RenderCase(string name,ActorAdditionalLightMode mode)
            {
                p.SetAdditionalLightMode(mode);Material current=null;
                var settings=new ActorForwardDrawSet.Settings {parameters=p,renderers=new[]{_quadRenderer},outlines=false,hairCover=false,
                    configureMaterial=(r,i,m)=>current=m};
                if(!ActorForwardDrawSet.TryPrepare(_camera,settings,out var prepared,out var why))throw new InvalidOperationException(why);
                try
                {
                    Check(name+"-material-local-variant",current!=null&&current.IsKeywordEnabled("TOOLKIT_ACTOR_ADDITIVE_VOLUME")== (mode==ActorAdditionalLightMode.ArtDirected));
                    _quadRenderer.sharedMaterial=current;Render("actor-light-authoring-"+name);var pixels=_readback.GetPixels();
                    using var writer=new BinaryWriter(File.Create(Path.Combine(_directory,"actor-light-authoring-"+name+".raw")));
                    foreach(var pixel in pixels)for(int c=0;c<4;c++)writer.Write(pixel[c]);return pixels;
                }
                finally{_quadRenderer.sharedMaterial=_material;prepared.Dispose();}
            }
            double Attenuation(ActorAdditionalLight light,Vector3 point)
            {
                double x=(double)light.position.x-point.x,y=(double)light.position.y-point.y,z=(double)light.position.z-point.z;
                double d2=Math.Max(.0001,x*x+y*y+z*z),fade=Math.Max(0,1-d2/((double)light.range*light.range));
                double result=fade*fade/Math.Max(1,d2);
                if(light.shape==ActorAdditionalLightShape.Spot)
                {
                    var d=light.direction.normalized;double cone=-(x*d.x+y*d.y+z*d.z)/Math.Sqrt(d2);
                    double outer=Math.Cos(light.outerAngle*Math.PI/360),inner=Math.Max(outer+.0001,Math.Cos(light.innerAngle*Math.PI/360));
                    double t=Math.Max(0,Math.Min(1,(cone-outer)/(inner-outer)));result*=t*t*(3-2*t);
                }
                return result;
            }
            float Oracle(Color[] pixels,Vector3 diffuse,Vector3 main,float directScale,ActorAdditionalLightMode mode,ActorAdditionalLight[] lights)
            {
                float maximum=0;int tested=0;
                for(int y=0;y<64;y++)for(int x=0;x<64;x++)
                {
                    var point=_camera.ViewportToWorldPoint(new Vector3((x+.5f)/64,(y+.5f)/64,3));
                    bool covered=Mathf.Abs(point.x)<.9f&&Mathf.Abs(point.y)<.9f;
                    for(int c=0;c<4;c++)
                    {
                        double expected=c==3?1:covered?diffuse[c]*main[c]*directScale:(c==1?0:1);
                        if(c<3&&covered)foreach(var light in lights)
                            expected+=diffuse[c]*(mode==ActorAdditionalLightMode.ArtDirected?1:main[c]*directScale)*light.radiance[c]*Attenuation(light,point);
                        maximum=Mathf.Max(maximum,(float)Math.Abs(pixels[y*64+x][c]-expected));
                    }
                    if(covered)tested++;
                }
                if(tested<2000)throw new InvalidOperationException("No full generated Actor coverage");return maximum;
            }
            try
            {
                // Integer pixel boundaries (8..56 at ortho size 1.2) avoid
                // raster subpixel vertex snapping changing interpolated world
                // positions. The oracle models geometry, not backend rounding.
                _quad.vertices=new[]{new Vector3(-.9f,-.9f,0),new Vector3(.9f,-.9f,0),new Vector3(.9f,.9f,0),new Vector3(-.9f,.9f,0)};
                _quad.RecalculateBounds();
                _quad.normals=new[]{Vector3.back,Vector3.back,Vector3.back,Vector3.back};
                _quad.uv=new[]{Vector2.one,Vector2.one,Vector2.one,Vector2.one};
                _material.SetColor("_Color",Color.white);_material.SetTexture("_MainTex",baseMap);_material.SetTexture("_ShadeTex",shade);
                _material.SetTexture("_RampTex",ramp);_material.SetTexture("_RampAddTex",Texture2D.blackTexture);
                _material.SetFloat("_EnableLayer",0);_material.SetFloat("_UseEmission",0);_material.SetFloat("_DisableDefMap",1);
                _material.SetVector("_DefValue",new Vector4(.5f,.5f,0,0));
                p.SetFloat("_FaceDebugMode",23);p.SetVector("_ActorLightingScales",new Vector4(0,1,0,0));
                var tint=new Vector3(.8f,.5f,.3f);var profile=new Vector3(.9f,.7f,.6f);
                p.SetVector("_CapturedLightColor",profile);
                var lamp=new ActorAdditionalLight {position=new Vector3(.25f,.1f,-2),range=4,radiance=new Vector3(1.2f,.8f,.4f)};
                foreach(int type in new[]{0,8,9})foreach(int angle in new[]{0,1,2})foreach(float intensity in new[]{0f,.125f,1f})
                {
                    _material.SetFloat("_ShaderType",type);Vector3 direction=angle==0?Vector3.back:angle==1?Vector3.right:Vector3.forward;
                    p.SetVector("_CapturedLightDirection",new Vector4(direction.x,direction.y,direction.z,1));
                    p.SetVector("_ActorKeyColor",tint*intensity);p.SetFloat("_CapturedDirectScale",.6f);
                    var diffuse=albedo*(.96f*(angle==0?1:angle==1?.4f:.2f));var main=Vector3.Scale(tint*intensity,profile);
                    foreach(var mode in new[]{ActorAdditionalLightMode.LegacyKeyModulated,ActorAdditionalLightMode.ArtDirected})
                    {
                        Color[] front=null;
                        foreach(int side in new[]{-1,1})
                        {
                            lamp.position.z=side*2;p.SetAdditionalLights(new[]{lamp});
                            string label="type-"+type+"-angle-"+angle+"-main-"+intensity.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"-"+mode+"-side-"+side;
                            var pixels=RenderCase(label,mode);float error=Oracle(pixels,diffuse,main,.6f,mode,new[]{lamp});
                            Check(label+"-independent-whole-image",error<.000002f,error);
                            if(front==null)front=pixels;else Check(label+"-equal-distance-front-back",ScenePixelsEqual(front,pixels));
                        }
                    }
                }
                _material.SetFloat("_ShaderType",0);p.SetVector("_CapturedLightDirection",new Vector4(0,0,-1,1));p.SetVector("_ActorKeyColor",Vector4.zero);
                var response=albedo*.96f;
                foreach(int cone in new[]{0,1,2})
                {
                    lamp.shape=ActorAdditionalLightShape.Spot;lamp.position=new Vector3(.1f,.2f,-2);lamp.direction=cone==0?Vector3.forward:cone==1?new Vector3(.4f,0,1):Vector3.back;
                    lamp.innerAngle=12;lamp.outerAngle=55;p.SetAdditionalLights(new[]{lamp});
                    var pixels=RenderCase("spot-"+cone,ActorAdditionalLightMode.ArtDirected);float error=Oracle(pixels,response,Vector3.zero,.6f,ActorAdditionalLightMode.ArtDirected,new[]{lamp});
                    Check("spot-"+cone+"-independent-cone-volume",error<.000002f,error);
                }
                lamp.shape=ActorAdditionalLightShape.Point;lamp.position=new Vector3(.1f,.2f,-2);
                var second=lamp;second.position=new Vector3(-.6f,-.3f,-1.4f);second.radiance=new Vector3(.2f,.5f,.8f);
                var pair=new[]{lamp,second};p.SetAdditionalLights(pair);var copied=RenderCase("two-lights",ActorAdditionalLightMode.ArtDirected);
                float pairError=Oracle(copied,response,Vector3.zero,.6f,ActorAdditionalLightMode.ArtDirected,pair);Check("two-lights-add-once",pairError<.000002f,pairError);
                pair[0].radiance=Vector3.zero;Check("typed-input-copy",ScenePixelsEqual(copied,RenderCase("copied-input",ActorAdditionalLightMode.ArtDirected)));
                var invalid=second;invalid.range=0;bool rejected=false;
                try{p.SetAdditionalLights(new[]{lamp,invalid});}catch(ArgumentException){rejected=true;}
                Check("invalid-last-light-atomic",rejected&&ScenePixelsEqual(copied,RenderCase("invalid-preserves-binding",ActorAdditionalLightMode.ArtDirected)));
                p.SetAdditionalLights(Array.Empty<ActorAdditionalLight>());var empty=RenderCase("empty",ActorAdditionalLightMode.ArtDirected);
                float emptyError=Oracle(empty,response,Vector3.zero,.6f,ActorAdditionalLightMode.ArtDirected,Array.Empty<ActorAdditionalLight>());
                Check("empty-no-local-light",emptyError<.000002f,emptyError);
                bool badMode=false;try{p.SetAdditionalLightMode((ActorAdditionalLightMode)99);}catch(ArgumentOutOfRangeException){badMode=true;}
                Check("unknown-mode-rejected",badMode);
                Check("negative-radiance-rejected",!new ActorAdditionalLight{range=1,radiance=Vector3.left}.IsValid);
                Check("nonfinite-position-rejected",!new ActorAdditionalLight{range=1,position=new Vector3(float.NaN,0,0)}.IsValid);
                Check("spot-zero-direction-rejected",!new ActorAdditionalLight{shape=ActorAdditionalLightShape.Spot,range=1,outerAngle=30}.IsValid);
                Check("spot-reversed-cone-rejected",!new ActorAdditionalLight{shape=ActorAdditionalLightShape.Spot,range=1,direction=Vector3.forward,innerAngle=40,outerAngle=30}.IsValid);
                bool nullRejected=false,overflowRejected=false;
                try{p.SetAdditionalLights(null);}catch(ArgumentException){nullRejected=true;}
                try{p.SetAdditionalLights(new ActorAdditionalLight[9]);}catch(ArgumentException){overflowRejected=true;}
                Check("null-list-rejected",nullRejected);Check("more-than-eight-rejected",overflowRejected);
                var tiny=new ActorAdditionalLight{shape=ActorAdditionalLightShape.Spot,range=1,direction=Vector3.forward*.000002f,innerAngle=0,outerAngle=30};
                Check("tiny-nonzero-direction-valid",tiny.IsValid);p.SetAdditionalLights(new[]{tiny});
                Vector4 boundDirection=Vector4.zero;bool retainedMode=false;
                var binding=new ActorForwardDrawSet.Settings{parameters=p,renderers=new[]{_quadRenderer},outlines=false,hairCover=false,
                    configureMaterial=(r,i,m)=>{boundDirection=m.GetVectorArray("_ActorAdditionalDirections")[0];retainedMode=m.IsKeywordEnabled("TOOLKIT_ACTOR_ADDITIVE_VOLUME");}};
                bool preparedOk=ActorForwardDrawSet.TryPrepare(_camera,binding,out var validBinding,out var reason);
                try{Check("tiny-direction-normalized-in-actual-binding",preparedOk&&boundDirection.z==1&&boundDirection.x==0&&boundDirection.y==0&&retainedMode);}
                finally{validBinding?.Dispose();}
            }
            finally{_material.CopyPropertiesFromMaterial(saved);_quad.normals=normals;_quad.uv=uv;_quad.vertices=vertices;_quad.RecalculateBounds();}
        }
    }
}
