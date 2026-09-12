Shader "GakumasPhotoMode/ActorPlanarCapture"
{
    Properties
    {
        _CapMain ("Base", 2D) = "white" {}
        _CapShade ("Shade", 2D) = "white" {}
        _CapDef ("Definition", 2D) = "white" {}
        _CapRamp ("Ramp", 2D) = "white" {}
        _CapLayer ("Layer", 2D) = "black" {}
        _CapHighlight ("Hair highlight", 2D) = "black" {}
        _CapRampAdd ("Material ramp", 2D) = "black" {}
        _CapEmission ("Emission", 2D) = "black" {}
        _CapCull ("Cull", Float) = 2
        _CapZWrite ("ZWrite", Float) = 1
        _CapSrcBlend ("Source blend", Float) = 1
        _CapDstBlend ("Destination blend", Float) = 0
        _CapStencilRef ("Stencil ref", Float) = 0
        _CapStencilRead ("Stencil read", Float) = 255
        _CapStencilWrite ("Stencil write", Float) = 255
        _CapStencilComp ("Stencil compare", Float) = 8
        _CapStencilPass ("Stencil pass", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        CGINCLUDE
        #include "UnityCG.cginc"
        #include "ActorPlanarSurface.hlsl"
        ENDCG
        Pass
        {
            Name "REDUCED_CHARACTER_COLOR"
            Cull [_CapCull] ZWrite [_CapZWrite] ZTest LEqual
            Blend [_CapSrcBlend] [_CapDstBlend]
            ColorMask RGB
            Stencil { Ref [_CapStencilRef] ReadMask [_CapStencilRead] WriteMask [_CapStencilWrite] Comp [_CapStencilComp] Pass [_CapStencilPass] }
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex CaptureVertex
            #pragma fragment CaptureColor
            ENDCG
        }
        Pass
        {
            Name "REDUCED_CHARACTER_COVERAGE"
            Cull [_CapCull] ZWrite [_CapZWrite] ZTest LEqual
            Blend One OneMinusSrcAlpha
            ColorMask A
            Stencil { Ref [_CapStencilRef] ReadMask [_CapStencilRead] WriteMask [_CapStencilWrite] Comp [_CapStencilComp] Pass [_CapStencilPass] }
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex CaptureVertex
            #pragma fragment CaptureCoverage
            ENDCG
        }
    }
}
