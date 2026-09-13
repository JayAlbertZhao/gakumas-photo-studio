using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Independent mathematical grade; this does not reproduce Unity trackball parameter encoding.</summary>
    public static class ColorGradingMath
    {
        public static Color Evaluate(ColorGradingProfile profile, Color linearRgb)
        {
            if(profile==null || !profile.IsValid) throw new ArgumentException("Invalid color grading profile");
            return new Transform(profile).Evaluate(linearRgb);
        }
        public static float DecodeDomain(float value, ColorLutDomain domain, float maximum)
            => domain==ColorLutDomain.Linear ? value*maximum : (float)(Math.Exp(value*Math.Log(1+maximum))-1);
        public static float EncodeDomain(float value, ColorLutDomain domain, float maximum)
        {
            value=float.IsNaN(value)?0:Mathf.Clamp(value,0,maximum);
            return domain==ColorLutDomain.Linear?value/maximum:(float)(Math.Log(1+value)/Math.Log(1+maximum));
        }
        internal static float Curve(Vector2[] knots, float x)
        {
            x=Mathf.Clamp01(x);
            for(int i=1;i<knots.Length;i++)
                if(x<=knots[i].x) return Mathf.LerpUnclamped(knots[i-1].y,knots[i].y,(x-knots[i-1].x)/(knots[i].x-knots[i-1].x));
            return knots[knots.Length-1].y;
        }
        // Uchimura 2017, original mathematical graph: desmos.com/calculator/mbkwnuihbd.
        // Branch before exponentiation avoids inactive toe/shoulder overflow for HDR inputs.
        public static double GranTurismo(double x, double contrast, double start, double length, double toe, double pedestal)
        {
            x=Math.Max(0,x);
            double width=(1-start)*length/contrast;
            if(x>=start+width) return 1-(1-start-contrast*width)*Math.Exp(-contrast*(x-start-width)/(1-start-contrast*width));
            double line=start+contrast*(x-start);
            if(x>=start) return line;
            double u=x/start, blend=u*u*(3-2*u);
            return (start*Math.Pow(u,toe)+pedestal)*(1-blend)+line*blend;
        }
        // Derive the RGB/XYZ transform from sRGB primaries and D65; Bradford cone matrix.
        private static Matrix4x4 Rows(Vector3 a, Vector3 b, Vector3 c)
        {
            var matrix=Matrix4x4.identity;
            matrix.SetRow(0,new Vector4(a.x,a.y,a.z,0));matrix.SetRow(1,new Vector4(b.x,b.y,b.z,0));matrix.SetRow(2,new Vector4(c.x,c.y,c.z,0));return matrix;
        }
        private static Vector3 Xyz(Vector2 xy) => new Vector3(xy.x/xy.y,1,(1-xy.x-xy.y)/xy.y);
        public static Matrix4x4 WhiteBalance(Vector2 source, Vector2 target)
        {
            if(source==target) return Matrix4x4.identity;
            var primaries=Matrix4x4.identity;
            primaries.SetColumn(0,new Vector4(.64f/.33f,1,.03f/.33f,0));
            primaries.SetColumn(1,new Vector4(.30f/.60f,1,.10f/.60f,0));
            primaries.SetColumn(2,new Vector4(.15f/.06f,1,.79f/.06f,0));
            var rgbToXyz=primaries*Matrix4x4.Scale(primaries.inverse.MultiplyVector(Xyz(new Vector2(.3127f,.329f))));
            var cone=Rows(new Vector3(.8951f,.2664f,-.1614f),new Vector3(-.7502f,1.7135f,.0367f),new Vector3(.0389f,-.0685f,1.0296f));
            var from=cone.MultiplyVector(Xyz(source));var to=cone.MultiplyVector(Xyz(target));
            return rgbToXyz.inverse*cone.inverse*Matrix4x4.Scale(new Vector3(to.x/from.x,to.y/from.y,to.z/from.z))*cone*rgbToXyz;
        }
        internal sealed class Transform
        {
            private readonly ColorGradingProfile p;
            private readonly Matrix4x4 whiteBalance;
            private readonly double exposure;
            internal Transform(ColorGradingProfile profile) { p=profile;whiteBalance=WhiteBalance(p.sourceWhite,p.targetWhite);exposure=Math.Pow(2,p.exposureEV); }
            internal Color Evaluate(Color input)
            {
                var rgb=new Vector3(Sanitize(input.r),Sanitize(input.g),Sanitize(input.b));
                rgb=whiteBalance.MultiplyVector(rgb);
                for(int i=0;i<3;i++)
                {
                    double value=Math.Max(0,rgb[i])*exposure*p.colorFilter[i];
                    value=.18*Math.Pow(value/.18,p.contrast);
                    value=Math.Pow(Math.Max(0,value*p.gain[i]+p.lift[i]),1.0/p.gamma[i]);
                    rgb[i]=(float)(p.toneMapping==ColorToneMapping.GranTurismo?
                        GranTurismo(value,p.gtContrast,p.gtLinearStart,p.gtLinearLength,p.gtToePower,p.gtPedestal):Math.Min(1,value));
                }
                var color=new Color(Mathf.Clamp01(rgb.x),Mathf.Clamp01(rgb.y),Mathf.Clamp01(rgb.z),input.a);
                Color.RGBToHSV(color,out float hue,out float sat,out float val);
                float luma=color.r*.2126f+color.g*.7152f+color.b*.0722f;
                float shifted=Mathf.Repeat(hue+p.hueDegrees/360+Curve(p.hueVsHue,hue),1);
                float multiplier=p.saturation*Curve(p.hueVsSaturation,hue)*Curve(p.saturationVsSaturation,sat)*Curve(p.luminanceVsSaturation,luma);
                color=Color.HSVToRGB(shifted,sat,val,true);
                float shiftedLuma=color.r*.2126f+color.g*.7152f+color.b*.0722f;
                color.r=Mathf.Clamp01(shiftedLuma+(color.r-shiftedLuma)*multiplier);
                color.g=Mathf.Clamp01(shiftedLuma+(color.g-shiftedLuma)*multiplier);
                color.b=Mathf.Clamp01(shiftedLuma+(color.b-shiftedLuma)*multiplier);
                color.r=Curve(p.red,Curve(p.master,color.r));color.g=Curve(p.green,Curve(p.master,color.g));color.b=Curve(p.blue,Curve(p.master,color.b));color.a=input.a;
                return color;
            }
            private float Sanitize(float v) => float.IsNaN(v)?0:Mathf.Clamp(v,0,p.maximumInput);
        }
    }
}
