Shader "CustomSRP/TerrainLit"
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
        _Metallic ("Metallic Fallback", Range(0, 1)) = 0
        _Smoothness ("Smoothness Fallback", Range(0, 1)) = 0.25
        _Fresnel ("Fresnel", Range(0, 1)) = 1

        [Toggle(_RECEIVE_SHADOWS)] _ReceiveShadows ("Receive Shadows", Float) = 1
    }

    // Distant basemap / extra splat passes — keep Unity Terrain happy.
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
        #pragma multi_compile_fog
        ENDHLSL

        Pass
        {
            Name "TerrainLit"
            Tags { "LightMode" = "CustomLit" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma shader_feature _RECEIVE_SHADOWS
            #pragma multi_compile _ _SHADOW_FILTER_MEDIUM _SHADOW_FILTER_HIGH
            #pragma multi_compile _ _SOFT_CASCADE_BLEND
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma vertex TerrainLitPassVertex
            #pragma fragment TerrainLitPassFragment
            #include "TerrainLitPass.hlsl"
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
