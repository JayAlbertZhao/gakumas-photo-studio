using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        private void VerifyProjectionJitter(Report report)
        {
            void Check(string n,bool ok,float v=0)=>FrameworkCheck(report,"projection-jitter-"+n,ok,v);
            Vector2 TextureUv(Matrix4x4 p,Vector4 point)
            {var clip=GL.GetGPUProjectionMatrix(p,true)*point;return new Vector2(clip.x,clip.y*(SystemInfo.graphicsUVStartsAtTop?-1:1))/clip.w*.5f+Vector2.one*.5f;}
            var sum=Vector2.zero;
            for(uint i=0;i<8;i++)
            {
                var offset=TemporalProjectionJitter.Offset(i);sum+=offset;
                Check("bounded-phase-"+i,Mathf.Abs(offset.x)<.5f&&Mathf.Abs(offset.y)<.5f);
                Check("explicit-phase-wrap-"+i,offset==TemporalProjectionJitter.Offset(i+8));
            }
            Check("eight-phase-zero-centroid",sum.magnitude<1e-6f,sum.magnitude);
            var perspective=Matrix4x4.Perspective(51,1.3f,.03f,100);
            var offAxis=perspective;offAxis.m02=.137f;offAxis.m12=-.071f;offAxis.m01=.043f;
            var orthographic=Matrix4x4.Ortho(-2,3,-1,2,.03f,100);
            int n=0;
            foreach(var basis in new[]{perspective,offAxis,orthographic})foreach(var size in new[]{new Vector2Int(1,1),new Vector2Int(127,93),new Vector2Int(512,512),new Vector2Int(3840,2160)})
            {
                Check("zero-exact-"+n,TemporalProjectionJitter.TryCreate(basis,size,Vector2.zero,out var zero)&&zero.projection==basis&&zero.correctionUv==Vector2.zero);
                for(uint phase=0;phase<8;phase++)
                {
                    var before=basis;var offset=TemporalProjectionJitter.Offset(phase);
                    bool ok=TemporalProjectionJitter.TryCreate(basis,size,offset,out var sample);Check("valid-"+n+"-"+phase,ok);
                    float error=0;foreach(var point in new[]{new Vector4(.2f,.13f,-.3f,1),new Vector4(-.4f,.7f,-2,1),new Vector4(.9f,-.6f,-37,1)})
                    {
                        var displacement=TextureUv(sample.projection,point)-TextureUv(basis,point);
                        error=Mathf.Max(error,Vector2.Scale(displacement+sample.correctionUv,(Vector2)size).magnitude);
                    }
                    Check("world-points-dejitter-to-stable-pixel-"+n+"-"+phase,error<.002f,error);
                    Check("correction-opposes-raster-displacement-"+n+"-"+phase,sample.correctionUv==-sample.rasterOffsetUv&&sample.correctionUv.sqrMagnitude>0);
                    Check("caller-projection-not-mutated-"+n+"-"+phase,basis==before);
                }
                n++;
            }
            Check("reject-zero-raster",!TemporalProjectionJitter.TryCreate(perspective,Vector2Int.zero,Vector2.zero,out _));
            Check("reject-oversized-raster",!TemporalProjectionJitter.TryCreate(perspective,new Vector2Int(16385,1),Vector2.zero,out _));
            Check("reject-nonfinite-offset",!TemporalProjectionJitter.TryCreate(perspective,Vector2Int.one,new Vector2(float.NaN,0),out _));
            Check("reject-outside-subpixel-range",!TemporalProjectionJitter.TryCreate(perspective,Vector2Int.one,new Vector2(.5001f,0),out _));
            Check("reject-singular-projection",!TemporalProjectionJitter.TryCreate(Matrix4x4.zero,Vector2Int.one,Vector2.zero,out _));
            var invalid=perspective;invalid.m00=float.PositiveInfinity;
            Check("reject-nonfinite-projection",!TemporalProjectionJitter.TryCreate(invalid,Vector2Int.one,Vector2.zero,out _));
        }
    }
}
