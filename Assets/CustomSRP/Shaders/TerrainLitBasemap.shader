Shader "Hidden/CustomSRP/TerrainLitBasemap"
{
    Properties
    {
        _MainTex ("Basemap (RGB)", 2D) = "grey" {}
        _BaseColor ("Tint", Color) = (1, 1, 1, 1)

        [Toggle(_CANVAS_ON)] _Canvas ("Canvas Normal", Float) = 1
        _CanvasMap ("Canvas (RG Normal, B Thickness)", 2D) = "bump" {}
        _CanvasStrength ("Canvas Strength", Range(0, 1)) = 0.08
        _PaintThickness ("Paint Thickness", Range(0, 2)) = 0.12

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 4
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.42
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 0.95
        _ShadowTint ("Shadow Cool Tint", Color) = (0.42, 0.48, 0.72, 1)
        _ShadowWarm ("Shadow Warm Tint", Color) = (0.62, 0.45, 0.32, 1)
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.4
        _ShadowBrushScale ("Shadow Brush Scale", Range(0.2, 8)) = 1.2
        _SpecularColor ("Specular", Color) = (0.5, 0.45, 0.38, 1)
        _SpecularThreshold ("Specular Threshold", Range(0, 1)) = 0.88
        _AmbientColor ("Ambient", Color) = (0.52, 0.5, 0.44, 1)

        _OutlineBrushMap ("Shadow Brush Mask", 2D) = "white" {}

        [Toggle(_RECEIVE_SHADOWS)] _ReceiveShadows ("Receive Shadows", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-100"
            "RenderType" = "Opaque"
            "TerrainCompatible" = "True"
        }

        Pass
        {
            Tags { "LightMode" = "CustomLit" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma shader_feature_local _CANVAS_ON
            #pragma shader_feature_local _RECEIVE_SHADOWS
            #pragma vertex TerrainOilBasemapVertex
            #pragma fragment TerrainOilBasemapFragment
            #include "../ShaderLibrary/Common.hlsl"
            #include "TerrainOilBasemapPass.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}
