Shader "CustomSRP/OilOceanNPR"
{
    Properties
    {
        // 配色对齐 OilRiverNPR：深水 / 浅水 / 岸沫。
        _BaseColor ("Deep", Color) = (0.32, 0.48, 0.68, 0.88)
        _ShallowColor ("Shallow", Color) = (0.55, 0.62, 0.48, 0.72)
        _FoamColor ("Titanium White", Color) = (0.94, 0.97, 1.0, 1)
        _GlintColor ("Ochre Sun Dash", Color) = (1.0, 0.92, 0.55, 1)
        // 与河 _GlintMap 相同：单列竖带光痕，勿用横笔印章。
        _SparkleBrush ("Sun Glint Column", 2D) = "black" {}
        _PaintMap ("Water Paint (Monet)", 2D) = "gray" {}
        _PaintTile ("Paint Tile (m)", Range(80, 800)) = 280
        _PaintContrast ("Paint Contrast", Range(0.5, 3)) = 1.45
        _PaintRelief ("Paint Relief", Range(0, 1)) = 0.78
        _StrokeSlice ("Horizontal Slice", Range(0, 1)) = 0.72
        _StrokeWarp ("Stroke Warp", Range(0, 2)) = 1.05
        _StrokeBands ("Stroke Bands", Range(8, 128)) = 52
        _AlbedoBoost ("Albedo Boost", Range(0.6, 2)) = 1.08

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
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.52
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 1.05
        _ShadowTint ("Shadow Cool Tint", Color) = (0.42, 0.48, 0.62, 1)
        _ShadowWarm ("Shadow Warm Tint", Color) = (0.72, 0.55, 0.38, 1)
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.48
        _SpecularColor ("Specular", Color) = (1.0, 0.93, 0.62, 1)
        _SpecularThreshold ("Specular Threshold", Range(0, 1)) = 0.84
        _AmbientColor ("Ambient", Color) = (0.72, 0.76, 0.82, 1)

        // Ocean must not receive cascade/object shadows (flat sea → 圆影).
        [Toggle(_RECEIVE_SHADOWS)] _ReceiveShadows ("Receive Shadows", Float) = 0
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
            #include "OilOceanNPRPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
