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
        [HideInInspector] _Mask0 ("Mask 0 (R)", 2D) = "grey" {}
        [HideInInspector] _Mask1 ("Mask 1 (G)", 2D) = "grey" {}
        [HideInInspector] _Mask2 ("Mask 2 (B)", 2D) = "grey" {}
        [HideInInspector] _Mask3 ("Mask 3 (A)", 2D) = "grey" {}
        [HideInInspector] _MaskMapRemapScale0 ("Mask Remap Scale 0", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapScale1 ("Mask Remap Scale 1", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapScale2 ("Mask Remap Scale 2", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapScale3 ("Mask Remap Scale 3", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapOffset0 ("Mask Remap Offset 0", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapOffset1 ("Mask Remap Offset 1", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapOffset2 ("Mask Remap Offset 2", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapOffset3 ("Mask Remap Offset 3", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LayerHasMask0 ("Layer Has Mask 0", Float) = 1
        [HideInInspector] _LayerHasMask1 ("Layer Has Mask 1", Float) = 1
        [HideInInspector] _LayerHasMask2 ("Layer Has Mask 2", Float) = 1
        [HideInInspector] _LayerHasMask3 ("Layer Has Mask 3", Float) = 1
        _HeightTransition ("Mask Height Transition", Range(0, 1)) = 0.5
        [HideInInspector] _TerrainHolesTexture ("Holes Map (RGB)", 2D) = "white" {}

        _BaseColor ("Tint", Color) = (1, 1, 1, 1)

        _PaintThickness ("Paint Thickness", Range(0, 2)) = 0.14

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 4
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.42
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 1.05
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.55
        _ShadowBrushScale ("Shadow Brush Scale", Range(0.2, 8)) = 1.35
        _SpecularColor ("Specular", Color) = (0.5, 0.45, 0.38, 1)
        _AmbientColor ("Ambient", Color) = (0.52, 0.5, 0.44, 1)

        // Optional wet strip on terrain near one water Y (authored on the material).
        _ShoreWetY ("Shore Wet Y (m)", Float) = 5.7
        _ShoreWetBand ("Shore Wet Band (m)", Range(0.5, 20)) = 5
        _ShoreWetStrength ("Shore Wet Strength", Range(0, 1)) = 0

        _OutlineBrushMap ("Shadow Brush Mask", 2D) = "white" {}
        _RidgeRockMap ("Ridge Rock", 2D) = "gray" {}
        _RidgeAmount ("Ridge Rock Amount", Range(0, 1)) = 0
        _TerrainHeight ("Terrain Height (m)", Float) = 1185
        _OilDetail ("Oil Detail", Range(0, 1)) = 0.2

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
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma multi_compile _ _SHADOW_FILTER_MEDIUM _SHADOW_FILTER_HIGH
            #pragma multi_compile _ OIL_MOBILE_PERF
            // Terrain runtime mats often drop local keywords — never gate receive on keyword.
            #define _RECEIVE_SHADOWS
            #define TERRAIN_SHADOW_RECEIVER
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
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma vertex TerrainLitShadowPassVertex
            #pragma fragment TerrainLitShadowPassFragment
            #include "TerrainLitShadowPass.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
