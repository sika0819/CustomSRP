Shader "Hidden/CustomSRP/TerrainLitBasemap"
{
    Properties
    {
        _MainTex ("Basemap (RGB)", 2D) = "grey" {}
        _BaseColor ("Tint", Color) = (1, 1, 1, 1)

        _PaintThickness ("Paint Thickness", Range(0, 2)) = 0.12

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 4
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.42
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 1.05
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.55
        _ShadowBrushScale ("Shadow Brush Scale", Range(0.2, 8)) = 1.35
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
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma multi_compile _ OIL_MOBILE_PERF
            // Same as TerrainOilNPR: force receive (Terrain mats drop local keywords).
            #define _RECEIVE_SHADOWS
            #define TERRAIN_SHADOW_RECEIVER
            #pragma vertex TerrainOilBasemapVertex
            #pragma fragment TerrainOilBasemapFragment
            #include "../ShaderLibrary/Common.hlsl"
            #include "TerrainOilBasemapPass.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}
