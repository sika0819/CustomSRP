Shader "CustomSRP/OilNPR"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Color", Color) = (0.75, 0.55, 0.4, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [Toggle(_CLIPPING)] _Clipping ("Alpha Clipping", Float) = 0

        _PaintThickness ("Paint Thickness", Range(0, 2)) = 0.18

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 4
        [HDR] _SpecColor ("Specular Color", Color) = (0.5, 0.5, 0.5, 1)
        _Smoothness ("Smoothness (Blinn-Phong exponent base)", Range(0, 1)) = 0
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.42
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 0.95
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.45
        _ShadowBrushScale ("Shadow Brush Scale", Range(0.2, 8)) = 1.8
        _AmbientColor ("Ambient", Color) = (0.55, 0.5, 0.44, 1)

        // 0=Mesh UV, 1=World triplanar, 2=Object triplanar（与旧 Boluo LitStrokeRock 枚举一致）
        // 单向三向：单张 _BaseMap 从 X/Y/Z 三轴采样后按 |N| 混合。
        [Enum(Mesh UV,0,World,1,Object,2)] _WorldTriplanar ("Triplanar", Float) = 0
        _TriplanarTiling ("Triplanar Tiling", Float) = 0.25

        [Toggle] _OilFoliage ("Foliage (ambient × albedo)", Float) = 0
        // Cross-card LOD billboards: yaw kept card to camera, drop the orthogonal card.
        [Toggle] _OilCrossBillboard ("Tree Cross Billboard", Float) = 0
        [NoScaleOffset] _BumpMap ("Billboard Baked Normal (RGB)", 2D) = "bump" {}
        _NormalStrength ("Billboard Normal Strength", Range(0, 1)) = 0.85
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
