using UnityEngine;

namespace GakumasPhotoMode
{
    internal sealed partial class SceneLightShadowAtlas
    {
        private static bool Extended(SceneDecalLight light)=>light.shape==SceneDecalLightShape.Capsule||light.shape==SceneDecalLightShape.Area;
        private static int ExtendedCount(SceneDecalLight light)
        {int n=light.shadow.extendedSamplesPerAxis;return light.shape==SceneDecalLightShape.Area?n*n:n;}

        private void PrepareExtended(SceneDecalLight light,int index,int firstTile)
        {
            var input=light.shadow;int nx=input.extendedSamplesPerAxis,ny=light.shape==SceneDecalLightShape.Area?nx:1;
            var rotation=light.rotation.normalized;
            var x=rotation*Vector3.right*(light.shape==SceneDecalLightShape.Area?light.halfSize.x:light.halfLength);
            var y=light.shape==SceneDecalLightShape.Area?rotation*Vector3.up*light.halfSize.y:Vector3.zero;
            // The full artistic support is wider than a sphere centered on each
            // sampled source. Conservative radial depth bounds prevent missing
            // side receivers/blockers in the original capsule/trapezoid volume.
            float far=light.shape==SceneDecalLightShape.Capsule?light.range+2*light.halfLength:
                new Vector3(light.range,2*light.halfSize.x+light.areaSpread.x*light.range,2*light.halfSize.y+light.areaSpread.y*light.range).magnitude;
            var packed=Matrix4x4.zero;
            packed.SetRow(0,new Vector4(light.position.x,light.position.y,light.position.z,1));
            packed.SetRow(1,new Vector4(x.x,x.y,x.z,0));packed.SetRow(2,new Vector4(y.x,y.y,y.z,0));packed.m33=1;
            _data[index]=new ShadowData {
                worldToShadow=packed,atlasST=new Vector4(1f/_grid,1f/_grid,nx,ny),
                depth=new Vector4(input.nearPlane,far,input.depthBias,input.normalBias),
                options=new Vector4(input.strength,(int)input.filter,1f/Atlas.width,-(firstTile+1))
            };
            var projection=GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(90,1,input.nearPlane/Mathf.Sqrt(3),far),true);
            for(int sy=0;sy<ny;sy++)for(int sx=0;sx<nx;sx++)
            {
                var origin=light.position+x*(2f*(sx+.5f)/nx-1)+y*(2f*(sy+.5f)/ny-1);
                var sample=_data[index];sample.worldToShadow=Matrix4x4.Translate(-origin);
                sample.atlasST=new Vector4(1f/_grid,1f/_grid,0,0);sample.options.w=firstTile+(sy*nx+sx)*6+1;
                for(int face=0;face<6;face++)
                {
                    var forward=face==0?Vector3.right:face==1?Vector3.left:face==2?Vector3.up:face==3?Vector3.down:face==4?Vector3.forward:Vector3.back;
                    var up=face==2||face==3?Vector3.forward:Vector3.up;
                    var view=Matrix4x4.Scale(new Vector3(1,1,-1))*Matrix4x4.TRS(origin,Quaternion.LookRotation(forward,up),Vector3.one).inverse;
                    _views.Add(projection*view);_mapData.Add(sample);
                }
            }
        }
    }
}
