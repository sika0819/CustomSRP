Shader "CustomSRP/OilNPR"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Color", Color) = (0.75, 0.55, 0.4, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [Toggle(_CLIPPING)] _Clipping ("Alpha Clipping", Float) = 0

        [Toggle(_KUWAHARA_ON)] _Kuwahara ("Kuwahara Albedo", Float) = 1
        _KuwaharaRadius ("Kuwahara Radius (UV)", Range(0.005, 0.12)) = 0.04
        [Toggle(_CANVAS_ON)] _Canvas ("Canvas Normal", Float) = 1
        _CanvasMap ("Canvas (RG Normal, B Thickness)", 2D) = "bump" {}
        _CanvasStrength ("Canvas Strength", Range(0, 1)) = 0.1
        _PaintThickness ("Paint Thickness", Range(0, 2)) = 0.18

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 4
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.42
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 0.95
        _ShadowTint ("Shadow Cool Tint", Color) = (0.42, 0.48, 0.72, 1)
        _ShadowWarm ("Shadow Warm Tint", Color) = (0.62, 0.45, 0.32, 1)
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.45
        _ShadowBrushScale ("Shadow Brush Scale", Range(0.2, 8)) = 1.8
        _SpecularColor ("Specular", Color) = (0.55, 0.48, 0.4, 1)
        _SpecularThreshold ("Specular Threshold", Range(0, 1)) = 0.86
        _AmbientColor ("Ambient", Color) = (0.55, 0.5, 0.44, 1)

        [Toggle(_INTERNAL_EDGE_ON)] _InternalEdge ("Internal Edge", Float) = 1
        _EdgeStrength ("Edge Strength", Range(0, 4)) = 0.55
        _EdgeColor ("Edge Color", Color) = (0.28, 0.2, 0.16, 1)

        [Toggle(_OUTLINE_ON)] _Outline ("Outline", Float) = 1
        _OutlineWidth ("Outline Width", Range(0, 0.1)) = 0.028
        _OutlineColor ("Outline Color", Color) = (0.14, 0.1, 0.08, 1)
        _OutlineBrushMap ("Outline Brush Mask", 2D) = "white" {}
        _OutlineBrushScale ("Outline Brush Scale", Range(0.2, 8)) = 2.5
        _OutlineNoise ("Outline Width Noise", Range(0, 1)) = 0.55
        _OutlineNoiseScale ("Outline Noise Scale", Range(0.2, 12)) = 3.5
        _OutlineWobble ("Outline Side Wobble", Range(0, 2)) = 0.85
        _OutlineBreak ("Outline Stroke Break", Range(0, 0.8)) = 0.12

        [Toggle(_RECEIVE_SHADOWS)] _ReceiveShadows ("Receive Shadows", Float) = 1
        [KeywordEnum(On, Clip, Dither, Off)] _Shadows ("Shadows", Float) = 0

        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("Z Write", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        LOD 300

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
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma shader_feature_local _CLIPPING
            #pragma shader_feature_local _KUWAHARA_ON
            #pragma shader_feature_local _CANVAS_ON
            #pragma shader_feature_local _INTERNAL_EDGE_ON
            #pragma shader_feature_local _RECEIVE_SHADOWS
            #pragma multi_compile_instancing
            #pragma vertex OilNPRPassVertex
            #pragma fragment OilNPRPassFragment
            #include "OilNPRPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Front
            ZWrite On
            Blend One Zero

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma vertex OilNPROutlineVertex
            #pragma fragment OilNPROutlineFragment
            #include "OilNPROutlinePass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma shader_feature_local _ _SHADOWS_CLIP _SHADOWS_DITHER
            #pragma multi_compile_instancing
            #pragma vertex ShadowCasterPassVertex
            #pragma fragment ShadowCasterPassFragment
            #include "ShadowCasterPass.hlsl"
            ENDHLSL
        }
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        LOD 150

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
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma shader_feature_local _CLIPPING
            #pragma shader_feature_local _KUWAHARA_ON
            #pragma shader_feature_local _CANVAS_ON
            #pragma shader_feature_local _INTERNAL_EDGE_ON
            #pragma shader_feature_local _RECEIVE_SHADOWS
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

            HLSLPROGRAM
            #pragma target 3.5
            #pragma shader_feature_local _ _SHADOWS_CLIP _SHADOWS_DITHER
            #pragma multi_compile_instancing
            #pragma vertex ShadowCasterPassVertex
            #pragma fragment ShadowCasterPassFragment
            #include "ShadowCasterPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
