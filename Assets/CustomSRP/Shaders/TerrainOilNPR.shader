Shader "CustomSRP/TerrainOilNPR"
{
    Properties
    {
        [HideInInspector] [PerRendererData] _Control ("Control (RGBA)", 2D) = "red" {}
        [HideInInspector] _Splat0 ("Layer 0 (R)", 2D) = "grey" {}
        [HideInInspector] _Splat1 ("Layer 1 (G)", 2D) = "grey" {}
        [HideInInspector] _Splat2 ("Layer 2 (B)", 2D) = "grey" {}
        [HideInInspector] _Splat3 ("Layer 3 (A)", 2D) = "grey" {}
        [HideInInspector] _Normal0 ("Normal 0", 2D) = "bump" {}
        [HideInInspector] _Normal1 ("Normal 1", 2D) = "bump" {}
        [HideInInspector] _Normal2 ("Normal 2", 2D) = "bump" {}
        [HideInInspector] _Normal3 ("Normal 3", 2D) = "bump" {}
        [HideInInspector] [Gamma] _Metallic0 ("Metallic 0", Range(0, 1)) = 0
        [HideInInspector] [Gamma] _Metallic1 ("Metallic 1", Range(0, 1)) = 0
        [HideInInspector] [Gamma] _Metallic2 ("Metallic 2", Range(0, 1)) = 0
        [HideInInspector] [Gamma] _Metallic3 ("Metallic 3", Range(0, 1)) = 0
        [HideInInspector] _Smoothness0 ("Smoothness 0", Range(0, 1)) = 0.25
        [HideInInspector] _Smoothness1 ("Smoothness 1", Range(0, 1)) = 0.25
        [HideInInspector] _Smoothness2 ("Smoothness 2", Range(0, 1)) = 0.25
        [HideInInspector] _Smoothness3 ("Smoothness 3", Range(0, 1)) = 0.25
        [HideInInspector] _NormalScale0 ("Normal Scale 0", Float) = 1
        [HideInInspector] _NormalScale1 ("Normal Scale 1", Float) = 1
        [HideInInspector] _NormalScale2 ("Normal Scale 2", Float) = 1
        [HideInInspector] _NormalScale3 ("Normal Scale 3", Float) = 1
        [HideInInspector] _TerrainHolesTexture ("Holes Map (RGB)", 2D) = "white" {}

        _BaseColor ("Tint", Color) = (1, 1, 1, 1)

        [Toggle(_KUWAHARA_ON)] _Kuwahara ("Kuwahara Albedo", Float) = 1
        _KuwaharaRadius ("Kuwahara Radius (Control UV)", Range(0.0005, 0.02)) = 0.004
        [Toggle(_CANVAS_ON)] _Canvas ("Canvas Normal", Float) = 1
        _CanvasMap ("Canvas (RG Normal, B Thickness)", 2D) = "bump" {}
        _CanvasStrength ("Canvas Strength", Range(0, 1)) = 0.08
        _PaintThickness ("Paint Thickness", Range(0, 2)) = 0.14

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

        [Toggle(_INTERNAL_EDGE_ON)] _InternalEdge ("Internal Edge", Float) = 1
        _EdgeStrength ("Edge Strength", Range(0, 4)) = 0.4
        _EdgeColor ("Edge Color", Color) = (0.28, 0.22, 0.16, 1)

        _OutlineBrushMap ("Shadow Brush Mask", 2D) = "white" {}

        [Toggle(_RECEIVE_SHADOWS)] _ReceiveShadows ("Receive Shadows", Float) = 1
    }

    Dependency "AddPassShader" = "Hidden/CustomSRP/TerrainLitAdd"
    Dependency "BaseMapShader" = "Hidden/CustomSRP/TerrainLitBasemap"
    Dependency "BaseMapGenShader" = "Hidden/CustomSRP/TerrainLitBasemapGen"

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-100"
            "RenderType" = "Opaque"
            "TerrainCompatible" = "True"
        }

        HLSLINCLUDE
        #include "../ShaderLibrary/Common.hlsl"
        ENDHLSL

        Pass
        {
            Name "TerrainOilNPR"
            Tags { "LightMode" = "CustomLit" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma shader_feature_local _KUWAHARA_ON
            #pragma shader_feature_local _CANVAS_ON
            #pragma shader_feature_local _INTERNAL_EDGE_ON
            #pragma shader_feature_local _RECEIVE_SHADOWS
            #pragma vertex TerrainOilNPRPassVertex
            #pragma fragment TerrainOilNPRPassFragment
            #include "TerrainOilNPRPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex TerrainLitShadowPassVertex
            #pragma fragment TerrainLitShadowPassFragment
            #include "TerrainLitShadowPass.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
