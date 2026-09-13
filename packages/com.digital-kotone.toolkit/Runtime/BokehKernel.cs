using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>Own deterministic concentric sampler; 29/42 neighbours plus one centre.</summary>
    public static class BokehKernel
    {
        public const int Capacity = 42;
        public static int Fill(BokehDepthOfFieldSettings settings,float reciprocalAspect,Vector4[] destination)
        {
            if(settings==null||!settings.IsValid||destination==null||destination.Length<Capacity||
                float.IsNaN(reciprocalAspect)||float.IsInfinity(reciprocalAspect)||reciprocalAspect<=0)
                throw new ArgumentException("Valid bokeh settings, aspect and 42-slot destination required");
            Array.Clear(destination,0,destination.Length);
            int index=0;float curvature=1-settings.bladeCurvature,rotation=settings.bladeRotation*Mathf.Deg2Rad;
            for(int ring=1;ring<=3;ring++)
            {
                // Keep the inner seven-point flood neighbourhood in both budgets.
                // 7+9+13=29 is an independent layout, not a prefix of the 42-point layout.
                int points=settings.sampleCount==BokehSampleCount.Samples43?ring*7:(ring==1?7:ring==2?9:13);
                float radius=(ring+1f/7)/(3+1f/7);
                for(int point=0;point<points;point++)
                {
                    float phi=2*Mathf.PI*point/points;
                    float boundary=Mathf.Cos(Mathf.PI/settings.bladeCount)/Mathf.Cos(phi-2*Mathf.PI/settings.bladeCount*Mathf.Floor((settings.bladeCount*phi+Mathf.PI)/(2*Mathf.PI)));
                    float r=radius*Mathf.Pow(boundary,curvature)*settings.maximumRadius;
                    float x=r*Mathf.Cos(phi-rotation),y=r*Mathf.Sin(phi-rotation);
                    destination[index++]=new Vector4(x,y,Mathf.Sqrt(x*x+y*y),x*reciprocalAspect);
                }
            }
            return index;
        }
    }
}
