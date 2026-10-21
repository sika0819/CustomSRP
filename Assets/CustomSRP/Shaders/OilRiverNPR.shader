Shader "CustomSRP/OilRiverNPR"
{
    Properties
    {
        _BaseColor ("Deep", Color) = (0.18, 0.30, 0.46, 0.92)
        _ShallowColor ("Shallow", Color) = (0.32, 0.40, 0.34, 0.82)
        _IntersectionColor ("Shore Edge", Color) = (0.78, 0.84, 0.86, 0.95)

        // 对齐 Boluo Mobile/Ocean 平躺河：FlowUvMode=MeshUV，FallSheet=0。
        _MeshFlowScale ("Mesh Flow Scale (m per UV)", Vector) = (50, -100, 0, 0)
        _BankUv ("River Bank UV (min,max,edgeFrac)", Vector) = (0, 1, 0.16, 0)

        _IntersectionNoise ("Intersection Noise", 2D) = "white" {}
        _PaintMap ("Water Paint (Monet)", 2D) = "gray" {}
        _GlintMap ("Sun Glint Dabs", 2D) = "black" {}

        _PaintTile ("Paint Tile (m)", Range(20, 400)) = 72
        _PaintContrast ("Paint Contrast", Range(0.5, 3)) = 1.45
        _PaintRelief ("Paint Relief", Range(0, 1)) = 0.78
        _StrokeSlice ("Horizontal Slice", Range(0, 1)) = 0.72
        _StrokeWarp ("Stroke Warp", Range(0, 2)) = 1.05
        _StrokeBands ("Stroke Bands", Range(8, 128)) = 52
        _GlintTile ("Glint Tile (m)", Range(10, 200)) = 36
        _GlintStrength ("Glint Strength", Range(0, 2)) = 1.15
        _GlintColor ("Glint Tint", Color) = (1.0, 0.92, 0.55, 1)

        _IntersectionTiling ("Intersection Tiling", Float) = 0.2
        _IntersectionSpeed ("Intersection Speed", Float) = 0.3
        // 浅水带宽度（米）；约 1.8 为半透白边甜点。
        _IntersectionLength ("Shore Depth Width (m)", Float) = 1.8
        _IntersectionFalloff ("Intersection Falloff", Float) = 1
        _IntersectionClipping ("Shore Clip", Range(0, 1)) = 0.2
        _IntersectionDistortion ("Intersection Distort", Float) = 0.12
        _ShoreSteep ("Shore Steep Max Ny", Range(0.2, 0.98)) = 0.92
        _AlbedoBoost ("Albedo Boost", Range(0.6, 2)) = 0.85

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 3
        _SpecularColor ("Specular", Color) = (0.78, 0.74, 0.52, 1)
        _SpecularThreshold ("Specular Threshold", Range(0, 1)) = 0.84
        _SpecularBoost ("Sun Glint Boost", Range(0, 2)) = 0.55
        _AmbientColor ("Ambient", Color) = (0.48, 0.54, 0.62, 1)
        _ReflectStrength ("Reflection Glaze", Range(0, 1)) = 1

        [Toggle(_PREMULTIPLY_ALPHA)] _PremulAlpha ("Premultiply Alpha", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
        [Enum(Off, 0, On, 1)] _ZWrite ("Z Write", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        HLSLINCLUDE
        #include "../ShaderLibrary/Common.hlsl"
        #include "OilRiverNPRInput.hlsl"
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "CustomLit" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile _ OIL_MOBILE_PERF
            #pragma shader_feature_local _PREMULTIPLY_ALPHA
            #pragma shader_feature_local _OIL_NO_VIOLET_UMBRA
            #pragma vertex OilRiverNPRPassVertex
            #pragma fragment OilRiverNPRPassFragment
            // Reflect path reads global _OilSkySunrise.
            #include "OilRiverNPRPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
