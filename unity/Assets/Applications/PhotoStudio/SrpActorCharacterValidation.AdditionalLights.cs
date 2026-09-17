using System;
using System.Linq;
using UnityEngine;
using GakumasPhotoMode.Examples;

namespace GakumasPhotoMode
{
    public sealed partial class SrpActorCharacterValidation
    {
        // Real caller-owned materials/skin, ordinary and temporal full passes.
        // Independent geometric/radiometric formula checks live in the generated
        // fixture; here paired controls check actual integration and appearance.
        private void VerifyDesktopAdditionalLights(Report report,DesktopHostExample example,Bounds character,
            ActorForwardParameters inputs,Action<int> view,Func<string,Color[]> run)
        {
            var s=example.Configuration;var camera=example.RenderCamera;Color[] depth=null;
            void Observe(DesktopFrameRenderer.Frame frame){depth=ReadPixels(frame.eyeDepth);}
            void Check(string name,bool accepted,float value=0)=>report.checks.Add(new Check {
                name="desktop-character-additional-"+name,accepted=accepted,value=value });
            Color[] Render(string name){example.ResetHistory();return run("additional-"+name);}
            try
            {
                example.FrameProduced+=Observe;
                s.temporal.enabled=s.actorMotion.enabled=s.includeSceneMotion=s.motionBlur.enabled=false;
                s.effects.enabled=s.bloom.enabled=s.fsr.enabled=s.depthOfField.enabled=false;
                s.planar.enabled=s.reflections.enabled=false;s.colorGrade=null;
                s.diffusion.enabled=true;s.diffusion.intensity=s.diffusion.radiusPixels=0;s.diffusion.downsample=1;
                s.allowImmutableUnreadableMotionMeshes=true;
                inputs.SetFloat("_ActorEnvironmentIntensity",0);inputs.SetVector("_ActorRimColor",Vector4.zero);
                inputs.SetVector("_ActorLightingScales",new Vector4(0,1,0,0));
                foreach(int angle in new[]{0,90,180})
                {
                    view(angle);string label="view-"+angle;
                    var toward=(camera.transform.position-character.center).normalized;float h=character.size.y;
                    var first=new ActorAdditionalLight {position=character.center+toward*h*.65f-camera.transform.right*h*.3f,
                        range=h*1.7f,radiance=new Vector3(2.4f,.35f,.12f)};
                    var second=new ActorAdditionalLight {position=character.center+toward*h*.45f+camera.transform.right*h*.35f+camera.transform.up*h*.25f,
                        range=h*1.5f,radiance=new Vector3(.15f,.6f,2.8f)};
                    inputs.SetVector("_ActorKeyColor",Vector4.zero);inputs.SetAdditionalLightMode(ActorAdditionalLightMode.ArtDirected);
                    inputs.SetAdditionalLights(Array.Empty<ActorAdditionalLight>());var dark=Render(label+"-dark");var actorDepth=depth;
                    int mask=camera.cullingMask;Color[] excludedDepth,excludedColor;
                    try{camera.cullingMask=1<<22;excludedColor=Render(label+"-excluded");excludedDepth=depth;}
                    finally{camera.cullingMask=mask;}
                    Save("desktop-character-additional-"+label+"-depth",actorDepth);
                    Save("desktop-character-additional-"+label+"-excluded-depth",excludedDepth);
                    var actor=new bool[Size*Size];int visible=0;
                    for(int p=0;p<actor.Length;p++){actor[p]=Mathf.Abs(actorDepth[p].r-excludedDepth[p].r)>.0001f;if(actor[p])visible++;}
                    Check(label+"-nonempty-visible-actor",visible>3000,visible);
                    inputs.SetAdditionalLightMode(ActorAdditionalLightMode.LegacyKeyModulated);inputs.SetAdditionalLights(new[]{first});
                    var legacy=Render(label+"-legacy-key-zero");
                    Check(label+"-legacy-key-zero-stays-dark",MaximumDifference(dark,legacy)<.00001f,MaximumDifference(dark,legacy));
                    inputs.SetAdditionalLightMode(ActorAdditionalLightMode.ArtDirected);var point=Render(label+"-point-key-zero");
                    Check(label+"-point-lights-dimmed-main",Changed(dark,point,.001f)>1000,Changed(dark,point,.001f));
                    var spot=first;spot.shape=ActorAdditionalLightShape.Spot;spot.innerAngle=20;spot.outerAngle=65;
                    spot.direction=(first.position-character.center).normalized;inputs.SetAdditionalLights(new[]{spot});var away=Render(label+"-spot-away");
                    Check(label+"-spot-away-rejects-character",MaximumDifference(dark,away)<.00001f,MaximumDifference(dark,away));
                    spot.direction=-spot.direction;inputs.SetAdditionalLights(new[]{spot});var cone=Render(label+"-spot-inward");
                    Check(label+"-spot-cone-visible",Changed(dark,cone,.001f)>500,Changed(dark,cone,.001f));
                    Check(label+"-spot-cone-bounds-point-volume",Changed(point,cone,.001f)>100,Changed(point,cone,.001f));
                    inputs.SetAdditionalLights(new[]{first,second});var pair=Render(label+"-two-colors-key-zero");
                    Check(label+"-second-color-adds-light",Changed(point,pair,.001f)>1000,Changed(point,pair,.001f));
                    int positive=0,negative=0;
                    for(int p=0;p<pair.Length;p++)
                    {
                        if(actor[p]){if(pair[p].maxColorComponent>dark[p].maxColorComponent+.001f)positive++;
                            if(pair[p].r<dark[p].r-.001f||pair[p].g<dark[p].g-.001f||pair[p].b<dark[p].b-.001f)negative++;}
                    }
                    Check(label+"-positive-actor-only-light",positive>1000&&negative==0,negative);
                    // Transparent Actor draws can shade without owning depth;
                    // depth equality is NOT a scene-only coverage mask. Compare
                    // two independently rendered actor-excluded scene controls.
                    Color[] excludedLit;
                    try{camera.cullingMask=1<<22;excludedLit=Render(label+"-excluded-lit");}
                    finally{camera.cullingMask=mask;}
                    Check(label+"-scene-color-unchanged",MaximumDifference(excludedColor,excludedLit)==0,MaximumDifference(excludedColor,excludedLit));
                    inputs.SetVector("_ActorKeyColor",Vector4.one*.125f);inputs.SetAdditionalLights(Array.Empty<ActorAdditionalLight>());
                    var dim=Render(label+"-dim-main");inputs.SetAdditionalLights(new[]{first,second});var dimPair=Render(label+"-two-colors-dim-main");
                    float localDifference=0;
                    for(int p=0;p<pair.Length;p++)for(int c=0;c<3;c++)localDifference=Mathf.Max(localDifference,Mathf.Abs((dimPair[p][c]-dim[p][c])-(pair[p][c]-dark[p][c])));
                    // Actual composed output is RGBAHalf, unlike the float32
                    // generated oracle. Main-light addition rounds at storage.
                    Check(label+"-local-response-independent-of-main-radiance",localDifference<.003f,localDifference);
                    inputs.SetVector("_ActorKeyColor",Vector4.zero);s.actorMotion.enabled=true;
                    var temporal=Render(label+"-motion-two-colors");
                    Check(label+"-temporal-full-pass-keeps-local-variant",MaximumDifference(pair,temporal)<.002f,MaximumDifference(pair,temporal));
                    s.actorMotion.enabled=false;inputs.SetVector("_ActorLightingScales",new Vector4(0,1,1,0));
                    var specular=Render(label+"-local-specular");
                    Check(label+"-local-specular-positive-control",Changed(pair,specular,.001f)>20,Changed(pair,specular,.001f));
                    inputs.SetVector("_ActorLightingScales",new Vector4(0,1,0,0));
                    Check(label+"-repeat-reproduces-local-color",MaximumDifference(pair,Render(label+"-restored"))==0);
                }
            }
            finally{example.FrameProduced-=Observe;}
        }
    }
}
