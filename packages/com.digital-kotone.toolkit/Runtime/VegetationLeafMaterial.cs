using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace GakumasPhotoMode
{
    /// <summary>Independent thin-sheet diffuse model; not a refractive or multiple-scattering medium.</summary>
    [Serializable]
    public sealed class VegetationLeafMaterial
    {
        public bool enabled;
        public bool twoSided = true;
        // Linear R, sampled with the owning surface's albedo UV transform.
        public Texture thicknessMap;
        [Range(0,16)] public float thickness = 1;
        public Vector3 absorption = new Vector3(2,.5f,1.5f);
        public Vector3 transmissionTint = new Vector3(.5f,.9f,.35f);
        [Range(0,1)] public float strength = .5f;

        internal bool Validate(out string error)
        {
            error = null;
            if (!enabled) return true;
            if (!Range(thickness,0,16) || !Range(strength,0,1) || !Vector(absorption,64) || !Vector(transmissionTint,1))
            { error = "Invalid leaf thickness/absorption/tint/strength"; return false; }
            if (thicknessMap != null && (thicknessMap.dimension != TextureDimension.Tex2D ||
                GraphicsFormatUtility.IsSRGBFormat(thicknessMap.graphicsFormat) ||
                (thicknessMap is RenderTexture rt && (!rt.IsCreated() || rt.antiAliasing != 1))))
            { error = "Leaf thickness requires a created linear non-MSAA 2D texture"; return false; }
            return true;
        }

        internal static void Bind(Material material, VegetationLeafMaterial leaf)
        {
            bool active = leaf != null && leaf.enabled;
            material.SetFloat("_LeafEnabled", active ? 1 : 0);
            if (!active) return;
            material.SetTexture("_LeafThicknessMap", leaf.thicknessMap != null ? leaf.thicknessMap : Texture2D.whiteTexture);
            material.SetVector("_LeafTintStrength", new Vector4(leaf.transmissionTint.x,leaf.transmissionTint.y,leaf.transmissionTint.z,leaf.strength));
            material.SetVector("_LeafAbsorptionThickness", new Vector4(leaf.absorption.x,leaf.absorption.y,leaf.absorption.z,leaf.thickness));
            material.SetFloat("_LeafTwoSided", leaf.twoSided ? 1 : 0);
        }
        private static bool Range(float v,float low,float high) => !float.IsNaN(v) && !float.IsInfinity(v) && v >= low && v <= high;
        private static bool Vector(Vector3 v,float high) => Range(v.x,0,high) && Range(v.y,0,high) && Range(v.z,0,high);
    }
}
