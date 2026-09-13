using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public enum ColorLutDomain { Linear = 0, LogOnePlus = 1 }
    public enum ColorToneMapping { Clip = 0, GranTurismo = 1 }

    /// <summary>Independent linear-sRGB/D65 to display-linear-sRGB grade. No private profiles.</summary>
    [Serializable]
    public sealed class ColorGradingProfile
    {
        public int schemaVersion = 1;
        public int size = 32;
        public ColorLutDomain domain = ColorLutDomain.LogOnePlus;
        public float maximumInput = 64;
        public float exposureEV;
        public Vector3 colorFilter = Vector3.one;
        public float contrast = 1;
        public float hueDegrees;
        public float saturation = 1;
        public Vector2 sourceWhite = new Vector2(.3127f, .329f);
        public Vector2 targetWhite = new Vector2(.3127f, .329f);
        public Vector3 lift = Vector3.zero;
        public Vector3 gamma = Vector3.one;
        public Vector3 gain = Vector3.one;
        public ColorToneMapping toneMapping = ColorToneMapping.GranTurismo;
        public float gtContrast = 1;
        public float gtLinearStart = .22f;
        public float gtLinearLength = .4f;
        public float gtToePower = 1.33f;
        public float gtPedestal;
        // Piecewise-linear, ordered knots. Cyclic hue curves require equal endpoint values.
        public Vector2[] master = IdentityCurve();
        public Vector2[] red = IdentityCurve();
        public Vector2[] green = IdentityCurve();
        public Vector2[] blue = IdentityCurve();
        public Vector2[] hueVsHue = ConstantCurve(0);
        public Vector2[] hueVsSaturation = ConstantCurve(1);
        public Vector2[] saturationVsSaturation = ConstantCurve(1);
        public Vector2[] luminanceVsSaturation = ConstantCurve(1);

        public static Vector2[] IdentityCurve() => new[] { Vector2.zero, Vector2.one };
        public static Vector2[] ConstantCurve(float value) => new[] { new Vector2(0,value), new Vector2(1,value) };
        internal static bool Range(float x, float lo, float hi) => !float.IsNaN(x) && !float.IsInfinity(x) && x >= lo && x <= hi;
        private static bool Range(Vector3 v, float lo, float hi) => Range(v.x,lo,hi) && Range(v.y,lo,hi) && Range(v.z,lo,hi);
        private static bool White(Vector2 v) => Range(v.x,.2f,.5f) && Range(v.y,.2f,.5f) && v.x+v.y<.95f;
        private static bool Curve(Vector2[] keys, float lo, float hi, bool cyclic = false)
        {
            if(keys==null || keys.Length<2 || keys.Length>256 || keys[0].x!=0 || keys[keys.Length-1].x!=1) return false;
            for(int i=0;i<keys.Length;i++)
                if(!Range(keys[i].x,0,1) || !Range(keys[i].y,lo,hi) || (i>0 && keys[i].x<=keys[i-1].x)) return false;
            return !cyclic || keys[0].y==keys[keys.Length-1].y;
        }
        public bool IsValid => schemaVersion==1 && (size==16 || size==32 || size==64) &&
            (domain==ColorLutDomain.Linear || domain==ColorLutDomain.LogOnePlus) && Range(maximumInput,1,65504) &&
            Range(exposureEV,-16,16) && Range(colorFilter,0,16) && Range(contrast,.1f,2) &&
            Range(hueDegrees,-360,360) && Range(saturation,0,2) && White(sourceWhite) && White(targetWhite) &&
            Range(lift,-1,1) && Range(gamma,.1f,4) && Range(gain,0,4) &&
            (toneMapping==ColorToneMapping.Clip || toneMapping==ColorToneMapping.GranTurismo) &&
            Range(gtContrast,.1f,4) && Range(gtLinearStart,.001f,.95f) && Range(gtLinearLength,0,.95f) &&
            Range(gtToePower,.1f,4) && Range(gtPedestal,0,.2f) &&
            Curve(master,0,1) && Curve(red,0,1) && Curve(green,0,1) && Curve(blue,0,1) &&
            Curve(hueVsHue,-.5f,.5f,true) && Curve(hueVsSaturation,0,2,true) &&
            Curve(saturationVsSaturation,0,2) && Curve(luminanceVsSaturation,0,2);

        public string ToJson(bool pretty = true)
        {
            if(!IsValid) throw new ArgumentException("Invalid color grading profile");
            return JsonUtility.ToJson(this,pretty);
        }
        public static ColorGradingProfile FromJson(string json)
        {
            if(string.IsNullOrWhiteSpace(json) || json.Length>131072) throw new ArgumentException("Invalid profile JSON");
            var profile=new ColorGradingProfile{schemaVersion=0};
            JsonUtility.FromJsonOverwrite(json,profile);
            if(profile==null || !profile.IsValid) throw new ArgumentException("Invalid color grading profile");
            return profile;
        }
    }
}
