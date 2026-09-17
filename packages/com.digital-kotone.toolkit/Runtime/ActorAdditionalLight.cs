using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum ActorAdditionalLightMode { LegacyKeyModulated=0, ArtDirected=1 }
    public enum ActorAdditionalLightShape { Point=0, Spot=1 }

    /// <summary>Explicit linear Actor light, independent of Unity Light discovery.
    /// Spot direction points FROM the light; angles are full cone angles in degrees.
    /// Independent range model: square(1-distanceSquared/rangeSquared)/max(1,distanceSquared).
    /// It is not a recovered original attenuation formula or photometric unit.</summary>
    [Serializable]
    public struct ActorAdditionalLight
    {
        public ActorAdditionalLightShape shape;
        public Vector3 position,radiance,direction;
        public float range,innerAngle,outerAngle;
        public bool IsValid => (shape==ActorAdditionalLightShape.Point||shape==ActorAdditionalLightShape.Spot)&&
            Vector(position,1000000)&&Vector(radiance,65504)&&radiance.x>=0&&radiance.y>=0&&radiance.z>=0&&
            Scalar(range,.0001f,100000)&&
            (shape==ActorAdditionalLightShape.Point||(Vector(direction,1000000)&&direction.sqrMagnitude>1e-12f&&
                Scalar(innerAngle,0,179)&&Scalar(outerAngle,.001f,179)&&innerAngle<=outerAngle));
        private static bool Scalar(float x,float lo,float hi)=>!float.IsNaN(x)&&!float.IsInfinity(x)&&x>=lo&&x<=hi;
        private static bool Vector(Vector3 v,float maximum)=>Scalar(v.x,-maximum,maximum)&&Scalar(v.y,-maximum,maximum)&&Scalar(v.z,-maximum,maximum);
    }
}
