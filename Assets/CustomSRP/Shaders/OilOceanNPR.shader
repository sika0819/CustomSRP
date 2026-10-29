Shader "CustomSRP/OilOceanNPR"
{
    Properties
    {
        // 深灰蓝水体；近岸叠色略暖，高光鹅黄/橘。
        _BaseColor ("Deep", Color) = (0.14, 0.18, 0.36, 0.96)
        _ShallowColor ("Shallow", Color) = (0.22, 0.24, 0.42, 0.90)
        _FoamColor ("Titanium White", Color) = (0.94, 0.97, 1.0, 1)
        _GlintColor ("Ochre Sun Dash", Color) = (0.92, 0.78, 0.42, 1)
        _TintShallow ("Tint Shallow", Color) = (0.78, 0.68, 0.48, 1)
        _TintDeep ("Tint Deep", Color) = (0.28, 0.24, 0.42, 1)
        _TintStrength ("Tint Strength", Range(0, 1)) = 0.4

        // 与河 _GlintMap 相同：单列竖带光痕，勿用横笔印章。
        _SparkleBrush ("Sun Glint Column", 2D) = "black" {}
        _PaintMap ("Water Paint (Monet)", 2D) = "gray" {}
        _PaintTile ("Paint Tile (m)", Range(80, 800)) = 280
        _PaintContrast ("Paint Contrast", Range(0.5, 3)) = 1.45
        _PaintRelief ("Paint Relief", Range(0, 1)) = 0.78
        _StrokeSlice ("Horizontal Slice", Range(0, 1)) = 0.72
        _StrokeWarp ("Stroke Warp", Range(0, 2)) = 1.05
        _StrokeBands ("Stroke Bands", Range(8, 128)) = 52
        _AlbedoBoost ("Albedo Boost", Range(0.6, 2)) = 1.0

        [NoScaleOffset] _BumpMap ("Wave Normal", 2D) = "bump" {}
        _BumpScale ("Bump Scale", Range(0, 2)) = 0.9
        _BumpTile ("Bump Tile (m)", Range(20, 200)) = 80
        // 只采 xy 一层。zw 留在向量里给旧材质，不再单独做交叉混合。
        _BumpTiling ("Bump Tiling (xy)", Vector) = (1, 2, 2, 1)
        _BumpScroll ("Bump Scroll (xy)", Vector) = (0.03, -0.02, -0.025, 0.018)

        // 对齐河：Intersection Noise × Depth × Shore Clip 画岸沫（无 Foam 笔刷贴图）。
        _IntersectionNoise ("Intersection Noise", 2D) = "white" {}
        _FoamStrength ("Foam Strength", Range(0, 2)) = 1.55
        _IntersectionClipping ("Shore Clip", Range(0, 1)) = 0.2
        _IntersectionTiling ("Intersection Tiling", Float) = 0.2
        _IntersectionSpeed ("Intersection Speed", Float) = 0.3
        _IntersectionFalloff ("Intersection Falloff", Float) = 1
        _IntersectionDistortion ("Intersection Distort", Float) = 0.12
        _RefractionStrength ("Refraction", Range(0, 1)) = 0.55
        _DepthVertical ("Depth Absorption", Range(0.3, 4)) = 1.35
        // 岸沫眼深窗尺度（水平米×0.05 映射）。越大白沫带越宽。
        _ShoreFoamWidth ("Shore Foam Width (m)", Range(8, 28)) = 16

        _SunPathStrength ("Sun Path Strength", Range(0, 4)) = 2.4
        _SunPathWidth ("Sun Path Narrowness", Range(0.1, 40)) = 4.5

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 3
        _ReflectStrength ("Reflection Glaze (Amount)", Range(0, 1)) = 1
        _ReflectDistort ("Reflection Distort (Warp)", Range(0, 0.35)) = 0.35
        _ReflectSteps ("Reflection Steps", Range(2, 6)) = 3

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
        #include "OilOceanNPRInput.hlsl"
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
            #pragma vertex OilOceanNPRPassVertex
            #pragma fragment OilOceanNPRPassFragment
            // Reflect path reads global _OilSkySunrise.
            #include "OilOceanNPRPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
