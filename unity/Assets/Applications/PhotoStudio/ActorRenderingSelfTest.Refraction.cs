using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private IEnumerator VerifyConvexRefraction(Report report)
        {
            yield return null;
            void Check(string n,bool ok,float e=0)=>FrameworkCheck(report,"convex-refraction-"+n,ok,e);
            var saved=RenderTexture.active;var shapes=new List<ConvexRefractionShape>();var module=new SceneRefractionRenderer();var skyCapture=new SceneSkyCapture();
            try
            {
                const int size=65;var host=Own(new GameObject("Independent convex refraction fixture"));var camera=host.AddComponent<Camera>();camera.enabled=false;
                camera.renderingPath=RenderingPath.Forward;camera.cullingMask=0;camera.allowHDR=true;camera.allowMSAA=false;camera.orthographic=true;camera.orthographicSize=1.25f;camera.aspect=1;
                camera.nearClipPlane=.1f;camera.farClipPlane=20;camera.transform.position=new Vector3(0,0,-3);
                RenderTexture Target(RenderTextureFormat f){var t=Own(new RenderTexture(size,size,0,f,RenderTextureReadWrite.Linear));t.filterMode=FilterMode.Point;t.Create();return t;}
                var source=Target(RenderTextureFormat.ARGBFloat);var opaque=Target(RenderTextureFormat.RFloat);
                var background=new Color(.03f,.05f,.09f,.7f);void Fill(RenderTexture t,Color c){RenderTexture.active=t;GL.Clear(false,true,c);}
                Fill(source,background);Fill(opaque,new Color(20,0,0,0));
                var cube=Own(new Cubemap(4,TextureFormat.RGBAFloat,false){filterMode=FilterMode.Point});
                var colors=new[]{new Color(1,.3f,.2f,1),new Color(.2f,1,.4f,1),new Color(.4f,.2f,1,1),new Color(1,.8f,.3f,1),new Color(.7f,.4f,.9f,1),new Color(.15f,.6f,.8f,1)};
                void Cube(){for(int f=0;f<6;f++)cube.SetPixels(Enumerable.Repeat(colors[f],16).ToArray(),(CubemapFace)f);cube.Apply(false);}
                Cube();
                var mesh=Own(new Mesh{name="Authored closed optical box"});
                var vertices=new[]{new Vector3(-.7f,-.65f,-.55f),new Vector3(.7f,-.65f,-.55f),new Vector3(.7f,.65f,-.55f),new Vector3(-.7f,.65f,-.55f),
                    new Vector3(-.7f,-.65f,.55f),new Vector3(.7f,-.65f,.55f),new Vector3(.7f,.65f,.55f),new Vector3(-.7f,.65f,.55f)};
                var indices=new[]{0,2,1,0,3,2,4,5,6,4,6,7,0,4,7,0,7,3,1,2,6,1,6,5,0,1,5,0,5,4,3,7,6,3,6,2};
                mesh.vertices=vertices;mesh.triangles=indices;mesh.RecalculateBounds();
                bool created=ConvexRefractionShape.TryCreate(mesh,0,1,out var shape,out var reason);Check("closed-convex-snapshot",created&&shape.PlaneCount==12&&shape.VertexCount==8);
                if(!created)throw new InvalidOperationException(reason);shapes.Add(shape);
                var surface=new SceneRefractionSurface{shape=shape,environment=cube};var settings=new SceneRefractionSettings{enabled=true,surfaces=new[]{surface}};
                SceneRefractionRenderer.Frame Render(){if(!module.TryRender(source,opaque,camera,settings,out var f))throw new InvalidOperationException(module.UnavailableReason);return f;}
                float opaqueDistance=20;float[] opaqueReference=null;int cases=0;int totalTir=0;
                Color[] Case(string name,bool expectVisible=true)
                {
                    var frame=Render();var pixels=ReadSceneTarget(frame.color);var remaining=ReadSceneTarget(frame.unresolvedThroughput);var depth=ReadSceneTarget(frame.firstEyeDepth);
                    var expected=new Color[pixels.Length];float error=0,residualError=0,depthError=0;int covered=0;
                    var world=vertices.Select(v=>new RefractionD(surface.localToWorld.MultiplyPoint3x4(v))).ToArray();
                    for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                    {
                        int i=y*size+x;RefractionRay(camera,x,y,size,size,out var origin,out var direction);
                        float obstruction=opaqueReference!=null?opaqueReference[i]:opaqueDistance;
                        expected[i]=background;var rest=Color.clear;float eye=obstruction;
                        if(RefractionHit(world,indices,origin,direction,out var distance,out var normal))
                        {
                            var hit=origin+direction*distance;float hitEye=-camera.worldToCameraMatrix.MultiplyPoint3x4(hit.Float).z;
                            // Independent CPU projection near-plane equation (Unity CPU clip range -w..w).
                            var plane=camera.projectionMatrix.GetRow(2)+camera.projectionMatrix.GetRow(3);
                            var vo=camera.worldToCameraMatrix.MultiplyPoint3x4(origin.Float);var vd=camera.worldToCameraMatrix.MultiplyVector(direction.Float);
                            double nearT=-(plane.x*vo.x+plane.y*vo.y+plane.z*vo.z+plane.w)/(plane.x*vd.x+plane.y*vd.y+plane.z*vd.z);
                            float nearEye=-camera.worldToCameraMatrix.MultiplyPoint3x4((origin+direction*nearT).Float).z;
                            bool reachesClip=hitEye>=nearEye;
                            if(!reachesClip&&RefractionD.Dot(direction,normal)<0&&RefractionHit(world,indices,hit,direction,out var beyond,out _))
                                reachesClip=-camera.worldToCameraMatrix.MultiplyPoint3x4((hit+direction*beyond).Float).z>=nearEye;
                            if(reachesClip&&Mathf.Max(nearEye,hitEye)<Mathf.Min(obstruction,camera.farClipPlane))
                            {
                                covered++;eye=Mathf.Max(nearEye,hitEye);expected[i].a=rest.a=1;
                                for(int c=0;c<3;c++)
                                {
                                    double eta=surface.indexOfRefraction[c]/(double)surface.exteriorIndexOfRefraction;
                                    var d=direction;var position=origin;double light=0,weight=1;
                                    if(RefractionD.Dot(direction,normal)<0)
                                    {
                                        position=hit;double f=RefractionInterface(d,normal,1/eta,out var refracted);
                                        light=f*Environment(d-normal*(2*RefractionD.Dot(d,normal)),c);weight=(1-f)/(eta*eta);d=refracted;
                                    }
                                    for(int b=0;b<surface.internalInterfaces&&weight>0;b++)
                                    {
                                        if(!RefractionHit(world,indices,position,d,out double t,out var n))break;
                                        position+=d*t;weight*=Math.Exp(-surface.absorption[c]*t);
                                        double f=RefractionInterface(d,n*(-1),eta,out var escaped);if(f==1)totalTir++;
                                        if(f<1)light+=weight*(1-f)*eta*eta*Environment(escaped,c);
                                        weight*=f;d-=n*(2*RefractionD.Dot(d,n));
                                    }
                                    expected[i][c]=(float)light;rest[c]=(float)(weight*eta*eta);
                                }
                            }
                        }
                        for(int c=0;c<4;c++){error=Mathf.Max(error,Mathf.Abs(pixels[i][c]-expected[i][c]));residualError=Mathf.Max(residualError,Mathf.Abs(remaining[i][c]-rest[c]));}
                        depthError=Mathf.Max(depthError,Mathf.Abs(depth[i].r-eye));
                    }
                    // Whole images, no pixel/edge exclusions. Independent CPU ray/triangle paths, no production planes.
                    Check(name+"-whole-independent-radiance",error<=.0003f,error);Check(name+"-whole-independent-residual",residualError<=.0003f,residualError);
                    Check(name+"-whole-primary-depth",depthError<=.00003f,depthError);Check(name+"-covered",expectVisible?covered>100:covered==0);
                    Check(name+"-owned-current-contract",frame.IsCurrent&&module.TargetCount==3&&module.TargetBytes==size*size*40);
                    if(error>.0003f||residualError>.0003f)
                    {
                        using(var writer=new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(_directory,"convex-refraction-"+name+"-failed-radiance.raw"))))
                            for(int i=0;i<pixels.Length;i++)for(int c=0;c<4;c++){writer.Write(pixels[i][c]);writer.Write(expected[i][c]);}
                    }
                    SaveSsrPreview("convex-refraction-"+name,pixels,size,size,false);cases++;return pixels;

                    double Environment(RefractionD direction,int channel)
                    {
                        var d=Quaternion.Inverse(surface.environmentRotation.normalized)*direction.Float;
                        float ax=Mathf.Abs(d.x),ay=Mathf.Abs(d.y),az=Mathf.Abs(d.z);int face=ax>=ay&&ax>=az?(d.x>0?0:1):ay>=az?(d.y>0?2:3):(d.z>0?4:5);
                        return colors[face][channel]*surface.radianceScale[channel];
                    }
                }
                surface.indexOfRefraction=Vector3.one;Case("matched-index");
                surface.indexOfRefraction=Vector3.one*1.5f;Case("glass");surface.indexOfRefraction=Vector3.one*2.4f;var diamond=Case("diamond");
                surface.absorption=new Vector3(.7f,.2f,.4f);var absorbed=Case("absorption");Check("positive-absorption-response",PixelError(diamond,absorbed)>.01f);surface.absorption=Vector3.zero;
                surface.internalInterfaces=1;Case("bounded-one-interface");surface.internalInterfaces=32;Case("bounded-thirty-two-interfaces");surface.internalInterfaces=8;
                camera.orthographic=false;camera.fieldOfView=40;Case("perspective");
                surface.localToWorld=Matrix4x4.TRS(new Vector3(.03f,-.07f,.1f),Quaternion.Euler(11,17,7),new Vector3(.87f,1.1f,.91f));Case("current-affine");
                surface.localToWorld=surface.localToWorld*Matrix4x4.Scale(new Vector3(-1,1,1));Case("mirrored-affine");surface.localToWorld=Matrix4x4.identity;
                surface.indexOfRefraction=new Vector3(2.32f,2.4f,2.48f);Case("rgb-dispersion");
                surface.environmentRotation=Quaternion.Euler(17,31,-9);Case("environment-rotation");surface.environmentRotation=Quaternion.identity;
                colors[4]=new Color(.13f,1.7f,.53f,1);Cube();Case("current-environment-update");
                Check("actual-tir-paths",totalTir>0,totalTir);Check("initial-whole-cases",cases==12,cases);
                var boxVertices=vertices;var boxIndices=indices;
                var gemVertices=new List<Vector3>();
                for(int ring=0;ring<2;ring++)for(int j=0;j<8;j++)
                {float angle=(j+.5f)*Mathf.PI/4,radius=ring==0?.39f:.83f;gemVertices.Add(new Vector3(radius*Mathf.Cos(angle),ring==0?.48f:.13f,radius*Mathf.Sin(angle)));}
                gemVertices.Add(new Vector3(0,-.79f,0));var gemIndices=new List<int>();
                void Triangle(int a,int b,int c)
                {if(Vector3.Dot(Vector3.Cross(gemVertices[b]-gemVertices[a],gemVertices[c]-gemVertices[a]),-gemVertices[a])>0){int swap=b;b=c;c=swap;}gemIndices.Add(a);gemIndices.Add(b);gemIndices.Add(c);}
                for(int j=1;j<7;j++)Triangle(0,j,j+1);
                for(int j=0;j<8;j++){int next=(j+1)%8;Triangle(j,next,8+next);Triangle(j,8+next,8+j);Triangle(8+j,8+next,16);}
                var gem=Own(new Mesh{name="Authored table crown pavilion diamond"});vertices=gemVertices.ToArray();indices=gemIndices.ToArray();gem.vertices=vertices;gem.triangles=indices;gem.RecalculateBounds();
                if(!ConvexRefractionShape.TryCreate(gem,0,1,out var gemShape,out reason))throw new InvalidOperationException(reason);shapes.Add(gemShape);surface.shape=gemShape;
                Case("faceted-diamond");surface.localToWorld=Matrix4x4.Rotate(Quaternion.Euler(13,29,5));Case("rotated-faceted-diamond");surface.localToWorld=Matrix4x4.identity;
                surface.shape=shape;vertices=boxVertices;indices=boxIndices;
                var varyingColors=(Color[])colors.Clone();for(int f=0;f<6;f++)colors[f]=new Color(.25f,.5f,.75f,1);Cube();
                surface.indexOfRefraction=Vector3.one*2.4f;camera.orthographic=true;var constant=Case("constant-environment-energy");var constantFrame=Render();var missing=ReadSceneTarget(constantFrame.unresolvedThroughput);float balance=0;
                for(int i=0;i<constant.Length;i++)if(missing[i].a>.5f)for(int c=0;c<3;c++)balance=Mathf.Max(balance,Mathf.Abs(constant[i][c]+missing[i][c]*colors[0][c]-colors[0][c]));
                Check("independent-escaped-plus-residual-balance",balance<=.00001f,balance);
                float eta=surface.indexOfRefraction.x,r0=(eta-1)/(eta+1);r0*=r0;float analytic=(1-r0)*Mathf.Pow(r0,surface.internalInterfaces);
                Check("analytic-normal-slab-residual",Mathf.Abs(missing[(size*size)/2].r-analytic)<=1e-7f,Mathf.Abs(missing[(size*size)/2].r-analytic));
                camera.orthographic=false;camera.fieldOfView=65;camera.transform.position=new Vector3(.13f,.07f,-.12f);camera.nearClipPlane=.01f;Case("inside-sensor-ior-radiance");
                surface.absorption=new Vector3(.3f,.7f,.2f);Case("inside-world-length-absorption");surface.absorption=Vector3.zero;
                surface.indexOfRefraction=Vector3.one*4;camera.fieldOfView=1;camera.transform.rotation=Quaternion.LookRotation(Vector3.one);surface.internalInterfaces=32;
                var trapped=Case("trapped-total-internal-reflection");var trappedResidual=ReadSceneTarget(Render().unresolvedThroughput);float trappedError=0;
                for(int i=0;i<trapped.Length;i++)for(int c=0;c<3;c++)trappedError=Mathf.Max(trappedError,Mathf.Abs(trapped[i][c]),Mathf.Abs(trappedResidual[i][c]-16));
                Check("trapped-light-not-faked-as-environment",trappedError<=.00001f,trappedError);
                camera.transform.rotation=Quaternion.identity;camera.fieldOfView=40;camera.transform.position=new Vector3(0,0,-.57f);camera.nearClipPlane=.1f;surface.indexOfRefraction=Vector3.one*2.4f;surface.internalInterfaces=8;
                Case("near-clipped-entry-keeps-optical-path");camera.transform.position=new Vector3(0,0,-3);camera.orthographic=true;
                opaqueDistance=2;Fill(opaque,new Color(2,0,0,0));Case("opaque-foreground",false);
                opaqueReference=new float[size*size];var occlusion=Own(new Texture2D(size,size,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point});var texels=new Color[size*size];
                for(int y=0;y<size;y++)for(int x=0;x<size;x++){int i=y*size+x;opaqueReference[i]=x>size/2&&y>size/3?2:20;texels[i]=new Color(opaqueReference[i],0,0,0);}occlusion.SetPixels(texels);occlusion.Apply(false);Graphics.Blit(occlusion,opaque);Case("asymmetric-opaque-foreground");
                opaqueReference=null;opaqueDistance=20;Fill(opaque,new Color(20,0,0,0));
                var far=new SceneRefractionSurface{shape=shape,environment=cube,indexOfRefraction=surface.indexOfRefraction,localToWorld=Matrix4x4.Translate(new Vector3(0,0,1.3f)),radianceScale=Vector3.one*.13f};
                settings.surfaces=new[]{far,surface};var farFirst=Case("primary-depth-far-first");settings.surfaces=new[]{surface,far};var nearFirst=Case("primary-depth-near-first");
                Check("submission-order-does-not-replace-primary-depth",PixelError(farFirst,nearFirst)==0,PixelError(farFirst,nearFirst));settings.surfaces=new[]{surface};
                surface.exteriorIndexOfRefraction=4;surface.indexOfRefraction=Vector3.one;camera.orthographic=false;camera.fieldOfView=40;Case("denser-exterior-interface");surface.exteriorIndexOfRefraction=1;surface.indexOfRefraction=Vector3.one*2.4f;
                camera.projectionMatrix=camera.CalculateObliqueMatrix(new Vector4(.13f,.07f,-1,-1));Case("oblique-near-projection");camera.ResetProjectionMatrix();
                camera.orthographic=true;var sheared=camera.projectionMatrix;sheared.m02=.13f;sheared.m12=-.07f;camera.projectionMatrix=sheared;Case("sheared-orthographic-projection");camera.ResetProjectionMatrix();camera.orthographic=false;
                for(int f=0;f<6;f++)colors[f]=varyingColors[f];Cube();
                vertices=gemVertices.ToArray();indices=gemIndices.ToArray();surface.shape=gemShape;surface.localToWorld=Matrix4x4.TRS(new Vector3(.05f,.04f,.1f),Quaternion.Euler(13,29,5),new Vector3(1.05f,.91f,1.13f));
                surface.absorption=new Vector3(.2f,.7f,.4f);surface.indexOfRefraction=new Vector3(2.32f,2.4f,2.48f);surface.environmentMip=2;
                var sky=new SceneSkySettings{enabled=true,zenith=new Vector3(.5f,1.25f,.25f),horizon=new Vector3(.5f,1.25f,.25f),ground=new Vector3(.5f,1.25f,.25f)};
                SceneSkyCapture.Frame skyFrame=default;
                void CaptureSky()
                {
                    if(!skyCapture.TryCapture(sky,64,true,out skyFrame))throw new InvalidOperationException(skyCapture.UnavailableReason);
                    surface.environment=skyFrame.radiance;for(int f=0;f<6;f++)colors[f]=new Color(sky.zenith.x,sky.zenith.y,sky.zenith.z,1);
                }
                CaptureSky();var oldSky=skyFrame;var firstSky=Case("native-sky-faceted-optics");
                Check("actual-sky-cube-descriptor",skyFrame.IsCurrent&&skyFrame.radiance.dimension==UnityEngine.Rendering.TextureDimension.Cube&&skyFrame.radiance.mipmapCount==7);
                sky.zenith=sky.horizon=sky.ground=new Vector3(.25f,3,.125f);CaptureSky();var latestSky=Case("native-sky-update-faceted-optics");
                Check("current-native-producer-update",!oldSky.IsCurrent&&skyFrame.IsCurrent&&PixelError(firstSky,latestSky)>.1f);
                if(Environment.GetEnvironmentVariable("GAKUMAS_SELFTEST_CAPTURE_REFRACTION")=="1")
                {
                    var currentOptics=Render();FsrCaptureDrain(currentOptics.color);bool began=RenderDocCaptureBridge.BeginOffscreenCapture(),ended=false;
                    try{CaptureSky();currentOptics=Render();FsrCaptureDrain(currentOptics.color);}
                    finally{if(began)ended=RenderDocCaptureBridge.EndOffscreenCapture();}
                    Check("native-current-cube-to-faceted-optics",began&&ended&&PixelError(ReadSceneTarget(currentOptics.color),latestSky)==0);
                    void Raw(string name,RenderTexture t,int channels)
                    {var data=ReadSceneTarget(t);using(var writer=new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(_directory,"native-refraction-"+name+".raw"))))foreach(var value in data)for(int c=0;c<channels;c++)writer.Write(value[c]);}
                    Raw("color",currentOptics.color,4);Raw("remaining",currentOptics.unresolvedThroughput,4);Raw("depth",currentOptics.firstEyeDepth,1);
                }
                surface.environment=cube;surface.environmentMip=0;surface.shape=shape;surface.localToWorld=Matrix4x4.identity;surface.absorption=Vector3.zero;surface.indexOfRefraction=Vector3.one*2.4f;
                vertices=boxVertices;indices=boxIndices;for(int f=0;f<6;f++)colors[f]=varyingColors[f];Cube();
                var beforeMutation=ReadSceneTarget(Render().color);var changed=(Vector3[])vertices.Clone();changed[0]*=1.2f;mesh.vertices=changed;
                var afterMutation=Case("immutable-source-snapshot");Check("source-mutation-does-not-change-owned-geometry",PixelError(beforeMutation,afterMutation)==0);mesh.vertices=vertices;
                var split=Own(new Mesh{name="Explicit split-facet input"});split.vertices=indices.Select(i=>vertices[i]).ToArray();split.triangles=Enumerable.Range(0,indices.Length).ToArray();
                bool welded=ConvexRefractionShape.TryCreate(split,0,1,out var splitShape,out _);Check("exact-seam-welded-closed-snapshot",welded&&splitShape.VertexCount==8&&split.vertexCount==36);
                if(!welded)throw new InvalidOperationException("Split seam fixture rejected");shapes.Add(splitShape);surface.shape=splitShape;Case("split-facet-seams");surface.shape=shape;
                var originalSource=source;var originalOpaque=opaque;var originalBackground=background;
                source=Target(RenderTextureFormat.ARGBHalf);background=new Color(.03125f,.0625f,.125f,.75f);Fill(source,background);Case("half-hdr-input");
                source=Target(RenderTextureFormat.RGB111110Float);background.a=1;Fill(source,background);Case("packed-hdr-input");
                source=originalSource;background=originalBackground;opaque=Target(RenderTextureFormat.RHalf);Fill(opaque,new Color(20,0,0,0));Case("half-eye-depth-input");opaque=originalOpaque;
                Check("expanded-whole-optical-cases",cases==33,cases);
                var beforeResize=Render();
                RenderTexture Large(RenderTextureFormat format,int length){var t=Own(new RenderTexture(length,length,0,format,RenderTextureReadWrite.Linear));t.Create();return t;}
                var largeSource=Large(RenderTextureFormat.ARGBFloat,128);var largeDepth=Large(RenderTextureFormat.RFloat,128);Fill(largeSource,background);Fill(largeDepth,new Color(20,0,0,0));
                Check("accepted-resize-invalidates-old-frame",module.TryRender(largeSource,largeDepth,camera,settings,out var resized)&&resized.IsCurrent&&!beforeResize.IsCurrent&&module.TargetBytes==128*128*40);
                settings.maximumTargetMiB=1;var tooLargeSource=Large(RenderTextureFormat.ARGBFloat,512);var tooLargeDepth=Large(RenderTextureFormat.RFloat,512);
                Check("target-budget-before-allocation",!module.TryRender(tooLargeSource,tooLargeDepth,camera,settings,out _)&&module.TargetCount==0&&!resized.IsCurrent);settings.maximumTargetMiB=256;
                settings.surfaces=new[]{surface,new SceneRefractionSurface{enabled=false,indexOfRefraction=new Vector3(float.NaN,0,0)}};
                Check("inactive-inputs-do-not-validate",Render().IsCurrent&&module.SubmittedSurfaces==1);settings.surfaces=new[]{surface};
                surface.environment=source;Check("non-cube-environment-rejected",!module.TryRender(source,opaque,camera,settings,out _)&&module.TargetCount==0);surface.environment=cube;
                surface.environmentMip=1;Check("missing-environment-mip-rejected",!module.TryRender(source,opaque,camera,settings,out _)&&module.TargetCount==0);surface.environmentMip=0;
                var lostCube=Own(new RenderTexture(16,16,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear){dimension=UnityEngine.Rendering.TextureDimension.Cube});lostCube.Create();lostCube.Release();surface.environment=lostCube;
                Check("lost-environment-rejected",!module.TryRender(source,opaque,camera,settings,out _)&&module.TargetCount==0);surface.environment=cube;
                surface.localToWorld=Matrix4x4.zero;Check("singular-transform-rejected",!module.TryRender(source,opaque,camera,settings,out _)&&module.TargetCount==0);surface.localToWorld=Matrix4x4.identity;
                surface.environmentRotation=new Quaternion(0,0,0,0);Check("zero-environment-rotation-rejected",!module.TryRender(source,opaque,camera,settings,out _)&&module.TargetCount==0);surface.environmentRotation=Quaternion.identity;
                var unsupported=camera.projectionMatrix;unsupported.m03=.25f;camera.projectionMatrix=unsupported;
                Check("displaced-perspective-sensor-rejected",!module.TryRender(source,opaque,camera,settings,out _)&&module.TargetCount==0);camera.ResetProjectionMatrix();
                unsupported=camera.projectionMatrix;unsupported.m33=.5f;camera.projectionMatrix=unsupported;
                Check("noncanonical-perspective-w-rejected",!module.TryRender(source,opaque,camera,settings,out _)&&module.TargetCount==0);camera.ResetProjectionMatrix();
                var current=Render();current.color.Release();Check("lost-target-invalidates-frame",!current.IsCurrent&&!module.TryGetFrame(out _));var replacement=Render();Check("recreated-target-current",replacement.IsCurrent);
                Check("owned-input-alias-rejected",!module.TryRender(replacement.color,opaque,camera,settings,out _)&&module.TargetCount==0);
                surface.indexOfRefraction.x=float.NaN;Check("invalid-ior-rejected",!module.TryRender(source,opaque,camera,settings,out _)&&module.TargetCount==0);surface.indexOfRefraction.x=2.32f;
                var invalid=Own(new Mesh());invalid.vertices=vertices;invalid.triangles=indices.Take(indices.Length-3).ToArray();
                Check("open-mesh-rejected",!ConvexRefractionShape.TryCreate(invalid,0,1,out _,out _));
                invalid.triangles=indices.Reverse().ToArray();Check("inward-winding-rejected",!ConvexRefractionShape.TryCreate(invalid,0,1,out _,out _));
                var dented=(Vector3[])vertices.Clone();dented[6]=Vector3.zero;invalid.vertices=dented;invalid.triangles=indices;Check("concave-mesh-rejected",!ConvexRefractionShape.TryCreate(invalid,0,1,out _,out _));
                invalid.vertices=vertices;invalid.triangles=Enumerable.Range(0,(ConvexRefractionShape.MaximumTriangles+1)*3).Select(i=>indices[i%indices.Length]).ToArray();
                Check("triangle-count-budget-rejected",!ConvexRefractionShape.TryCreate(invalid,0,1,out _,out _));Check("invalid-geometry-budget-rejected",!ConvexRefractionShape.TryCreate(mesh,0,0,out _,out _));
                Check("source-remains-unchanged",mesh.vertices.SequenceEqual(vertices)&&mesh.triangles.SequenceEqual(indices));
                var beforeDispose=Render();shape.Dispose();Check("disposed-shape-invalidates-render",!module.TryRender(source,opaque,camera,settings,out _)&&!beforeDispose.IsCurrent&&module.TargetCount==0);
                settings.enabled=false;Check("disabled-releases",!module.TryRender(source,opaque,camera,settings,out _)&&module.TargetCount==0);
            }
            finally{module.Dispose();skyCapture.Dispose();foreach(var shape in shapes)shape.Dispose();RenderTexture.active=saved!=null&&saved.IsCreated()?saved:null;foreach(var value in _owned)if(value!=null)Destroy(value);_owned.Clear();}
        }

        private readonly struct RefractionD
        {
            public readonly double x,y,z;
            public RefractionD(double a,double b,double c){x=a;y=b;z=c;}
            public RefractionD(Vector3 v){x=v.x;y=v.y;z=v.z;}
            public Vector3 Float=>new Vector3((float)x,(float)y,(float)z);
            public RefractionD Unit=>this*(1/Math.Sqrt(Dot(this,this)));
            public static RefractionD operator +(RefractionD a,RefractionD b)=>new RefractionD(a.x+b.x,a.y+b.y,a.z+b.z);
            public static RefractionD operator -(RefractionD a,RefractionD b)=>new RefractionD(a.x-b.x,a.y-b.y,a.z-b.z);
            public static RefractionD operator *(RefractionD a,double b)=>new RefractionD(a.x*b,a.y*b,a.z*b);
            public static double Dot(RefractionD a,RefractionD b)=>a.x*b.x+a.y*b.y+a.z*b.z;
            public static RefractionD Cross(RefractionD a,RefractionD b)=>new RefractionD(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
        }
        private static void RefractionRay(Camera camera,int x,int y,int width,int height,out RefractionD origin,out RefractionD direction)
        {
            // Solve the two image-plane equations in DOUBLE at eye z=0 and z=1.
            // Unity.ViewportToWorldPoint at a tiny near clip introduced reference
            // angular bias near TIR. Do not inherit its world-space cancellation.
            var p=camera.projectionMatrix;var inverse=camera.worldToCameraMatrix.inverse;
            double u=2*(x+.5)/width-1,v=2*(y+.5)/height-1;
            RefractionD View(double eye)
            {
                double z=-eye,a=p.m00-u*p.m30,b=p.m01-u*p.m31,c=p.m10-v*p.m30,d=p.m11-v*p.m31;
                double e=u*(p.m32*z+p.m33)-p.m02*z-p.m03,f=v*(p.m32*z+p.m33)-p.m12*z-p.m13,det=a*d-b*c;
                return new RefractionD((e*d-b*f)/det,(a*f-e*c)/det,z);
            }
            RefractionD World(RefractionD q,double w)=>new RefractionD(
                inverse.m00*q.x+inverse.m01*q.y+inverse.m02*q.z+inverse.m03*w,
                inverse.m10*q.x+inverse.m11*q.y+inverse.m12*q.z+inverse.m13*w,
                inverse.m20*q.x+inverse.m21*q.y+inverse.m22*q.z+inverse.m23*w);
            var start=View(0);origin=World(start,1);direction=World(View(1)-start,0).Unit;
        }
        private static bool RefractionHit(RefractionD[] vertices,int[] indices,RefractionD origin,RefractionD d,out double t,out RefractionD normal)
        {
            t=double.PositiveInfinity;normal=default;
            // Independent Moller-Trumbore intersection of actual authored triangles.
            var center=new RefractionD(0,0,0);foreach(var p in vertices)center+=p*(1.0/vertices.Length);
            for(int f=0;f<indices.Length;f+=3)
            {
                var a=vertices[indices[f]];var e1=vertices[indices[f+1]]-a;var e2=vertices[indices[f+2]]-a;
                var p=RefractionD.Cross(d,e2);double determinant=RefractionD.Dot(e1,p);if(Math.Abs(determinant)<1e-12)continue;
                var s=origin-a;double u=RefractionD.Dot(s,p)/determinant;if(u<0||u>1)continue;var q=RefractionD.Cross(s,e1);
                double v=RefractionD.Dot(d,q)/determinant;if(v<0||u+v>1)continue;double candidate=RefractionD.Dot(e2,q)/determinant;
                if(candidate<=1e-8||candidate>=t)continue;t=candidate;normal=RefractionD.Cross(e1,e2).Unit;
                if(RefractionD.Dot(normal,center-a)>0)normal=normal*(-1);
            }
            return !double.IsPositiveInfinity(t);
        }
        private static double RefractionInterface(RefractionD d,RefractionD n,double ratio,out RefractionD transmitted)
        {
            if(ratio==1){transmitted=d;return 0;}
            double cosine=Math.Max(0,Math.Min(1,-RefractionD.Dot(d,n))),sin=ratio*Math.Sqrt(Math.Max(0,1-cosine*cosine));
            if(sin>=1){transmitted=default;return 1;}
            double ct=Math.Sqrt(1-sin*sin);transmitted=(d*ratio+n*(ratio*cosine-ct)).Unit;
            // Independent angular-form Fresnel coefficients in double precision.
            double a=(ratio*cosine-ct)/(ratio*cosine+ct),b=(cosine-ratio*ct)/(cosine+ratio*ct);
            return .5*(a*a+b*b);
        }
    }
}
