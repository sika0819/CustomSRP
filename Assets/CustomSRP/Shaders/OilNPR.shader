// Y billboard albedo capture: _OilBillboardAlbedoCapture in OilNPRInput.hlsl.
Shader "CustomSRP/OilNPR"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Color", Color) = (0.75, 0.55, 0.4, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [Toggle(_CLIPPING)] _Clipping ("Alpha Clipping", Float) = 0

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 4
        [HDR] _SpecColor ("Specular Color", Color) = (0.5, 0.5, 0.5, 1)
        _Smoothness ("Smoothness (Blinn-Phong exponent base)", Range(0, 1)) = 0
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.42
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 0.95
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.45
        _ShadowBrushScale ("Shadow Brush Scale", Range(0.2, 8)) = 1.8
        _AmbientColor ("Ambient", Color) = (0.55, 0.5, 0.44, 1)

        // Oil-paint volume (leaf): 草叶色/草根色作相对暗部色相；亮部保留原 albedo。
        _TopColor ("Volume Top", Color) = (0.50196, 0.74118, 0.18039, 1)
        _BottomColor ("Volume Bottom", Color) = (0.23922, 0.43922, 0.10196, 1)
        _GradientOffset ("Volume Y Offset", Range(-0.5, 0.5)) = 0
        _GradientSmoothness ("Volume Softness", Range(0.02, 0.5)) = 0.16
        // Height = mesh Y span; YMin = mesh bounds.min.y (OilFoliageVolumeFit MPB).
        _FoliageHeight ("Volume Height Span (OS.y)", Float) = 12
        _FoliageYMin ("Volume Y Min (OS.y)", Float) = 0
        [HideInInspector] [Toggle] _OilFoliage ("Oil Foliage Lighting", Float) = 0
        // 树干：更硬的油画色块 + 圆柱明暗；不用叶片绿体积色。
        [HideInInspector] [Toggle] _OilBark ("Oil Bark Lighting", Float) = 0

        // 0=Mesh UV, 1=World triplanar, 2=Object triplanar（与旧 Boluo LitStrokeRock 枚举一致）
        // 单向三向：单张 _BaseMap 从 X/Y/Z 三轴采样后按 |N| 混合。
        [Enum(Mesh UV,0,World,1,Object,2)] _WorldTriplanar ("Triplanar", Float) = 0
        _TriplanarTiling ("Triplanar Tiling", Float) = 0.25

        // Runtime-only (EnvTreeBillboardService); not authored on shared mats.
        [HideInInspector] [Toggle] _OilCrossBillboard ("Tree Cross Billboard", Float) = 0
        [HideInInspector] [Toggle] _OilYBillboard ("Tree Y Billboard", Float) = 0
        [NoScaleOffset] _BumpMap ("Normal (Billboard / Oil Rock)", 2D) = "bump" {}
        // Relative to albedo UV (same axes). >1 = finer oil strokes on rock.
        _BumpTiling ("Oil Normal Tile", Range(0.5, 8)) = 1
        // Billboard default stays low; ShiTou oil-rock mats author ~0.55–0.7.
        _NormalStrength ("Normal Strength", Range(0, 2)) = 0.35
        _EdgeFade ("Billboard Edge Fade", Range(0, 0.5)) = 0.12

        [Toggle(_RECEIVE_SHADOWS)] _ReceiveShadows ("Receive Shadows", Float) = 1
        [KeywordEnum(On, Clip, Dither, Off)] _Shadows ("Shadows", Float) = 0

        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("Z Write", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            // Unity MaterialInspector: LOD>=300 + mobile build target → "expensive" HelpBox.
            // This is a name/LOD heuristic, not instruction analysis. Opt out after mobile strip.
            "PerformanceChecks" = "False"
        }

        LOD 200

        HLSLINCLUDE
        #include "../ShaderLibrary/Common.hlsl"
        #include "OilNPRInput.hlsl"
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "CustomLit" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            // multi_compile：Yoo/AB 叶片材质不进主包场景时，shader_feature 会被剥掉 → 真机无裁剪/无影。
            #pragma multi_compile_local _ _CLIPPING
            #pragma multi_compile_local _ _RECEIVE_SHADOWS
            #pragma multi_compile _ OIL_MOBILE_PERF
            // 片树专用变体。shader_feature 在 Yoo/AB 材质上会被剥掉，跟 _CLIPPING 一样必须进包。
            // 近景材质不开这个关键字，编译结果不含片树法线采样和昼夜双光照。
            #pragma multi_compile_local _ OIL_Y_BILLBOARD
            // Editor bake only. Player shaders do not compile this variant.
            // OilNPRShaderStripper still drops it if a player compile sees the keyword.
#if UNITY_EDITOR
            #pragma multi_compile _ OIL_BILLBOARD_ALBEDO_CAPTURE
#endif
            #pragma multi_compile_instancing
            #pragma vertex OilNPRPassVertex
            #pragma fragment OilNPRPassFragment
            #include "OilNPRPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile_local _ _SHADOWS_CLIP
            #pragma shader_feature_local _SHADOWS_DITHER
            #pragma multi_compile_instancing
            #pragma vertex ShadowCasterPassVertex
            #pragma fragment ShadowCasterPassFragment
            #include "ShadowCasterPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
