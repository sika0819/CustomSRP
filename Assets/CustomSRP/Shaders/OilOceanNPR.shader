Shader "CustomSRP/OilOceanNPR"
{
    Properties
    {
        // 配色对齐 OilRiverNPR：深水 / 浅水 / 岸沫。
        _BaseColor ("Deep", Color) = (0.10, 0.22, 0.36, 0.96)
        _ShallowColor ("Shallow", Color) = (0.18, 0.28, 0.36, 0.90)
        _FoamColor ("Titanium White", Color) = (0.78, 0.84, 0.88, 1)
        _GlintColor ("Ochre Sun Dash", Color) = (0.92, 0.78, 0.42, 1)
        // 与河 _GlintMap 相同：单列竖带光痕，勿用横笔印章。
        _SparkleBrush ("Sun Glint Column", 2D) = "black" {}
        _PaintMap ("Water Paint (Monet)", 2D) = "gray" {}
        _PaintTile ("Paint Tile (m)", Range(80, 800)) = 280
        _PaintContrast ("Paint Contrast", Range(0.5, 3)) = 1.45
        _PaintRelief ("Paint Relief", Range(0, 1)) = 0.78
        _StrokeSlice ("Horizontal Slice", Range(0, 1)) = 0.72
        _StrokeWarp ("Stroke Warp", Range(0, 2)) = 1.05
        _StrokeBands ("Stroke Bands", Range(8, 128)) = 52
        _AlbedoBoost ("Albedo Boost", Range(0.6, 2)) = 0.85

        _FoamStrength ("Foam Strength", Range(0, 2)) = 1.55
        _FoamMap ("Foam Brush", 2D) = "white" {}
        _IntersectionTiling ("Foam Tile", Range(0.02, 0.25)) = 0.1
        _IntersectionSpeed ("Foam Drift (m/s)", Range(0, 4)) = 1.6
        _IntersectionClipping ("Foam Clip", Range(0.05, 0.9)) = 0.15
        _IntersectionDistortion ("Foam Distort (m)", Range(0, 40)) = 2
        // 岸沫探针系数：实际探针米 ≈ Length×11（约 5~8m），edge 收窄后带宽接近。
        _IntersectionLength ("Foam Width Scale", Range(0.3, 1.0)) = 0.55
        _RefractionStrength ("Refraction", Range(0, 1)) = 0.55
        _DepthVertical ("Depth Absorption", Range(0.3, 4)) = 1.35
        _ShoreFoamWidth ("Shore Foam Width (m)", Range(8, 28)) = 16

        _SunPathStrength ("Sun Path Strength", Range(0, 4)) = 2.4
        _SunPathWidth ("Sun Path Narrowness", Range(0.1, 40)) = 4.5

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 3
        _SpecularColor ("Specular", Color) = (0.78, 0.74, 0.52, 1)
        _SpecularThreshold ("Specular Threshold", Range(0, 1)) = 0.84
        _AmbientColor ("Ambient", Color) = (0.48, 0.54, 0.62, 1)
        _ReflectStrength ("Reflection Glaze", Range(0, 1)) = 1

        // Ocean must not receive cascade/object shadows (flat sea → 圆影).
        [Toggle(_RECEIVE_SHADOWS)] _ReceiveShadows ("Receive Shadows", Float) = 0
        // Same as river: no violet umbra, or night water goes magenta under moon fill.
        [Toggle(_OIL_NO_VIOLET_UMBRA)] _NoVioletUmbra ("No Violet Umbra", Float) = 1
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
            #pragma shader_feature_local _OIL_NO_VIOLET_UMBRA
            #pragma vertex OilOceanNPRPassVertex
            #pragma fragment OilOceanNPRPassFragment
            // Reflect path reads global _OilSkySunrise.
            #include "OilOceanNPRPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
