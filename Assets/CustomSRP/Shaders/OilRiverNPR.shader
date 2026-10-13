Shader "CustomSRP/OilRiverNPR"
{
    Properties
    {
        _BaseColor ("Deep", Color) = (0.28, 0.58, 0.82, 0.55)
        _ShallowColor ("Shallow", Color) = (0.401, 0.805, 1, 0.28)
        _FoamColor ("Foam Tint", Color) = (0.364, 0.619, 0.811, 1)
        _IntersectionColor ("Shore Foam", Color) = (0.94, 0.97, 1.0, 0.95)

        // 对齐 Boluo Mobile/Ocean 平躺河：FlowUvMode=MeshUV，FallSheet=0。
        _MeshFlowScale ("Mesh Flow Scale (m per UV)", Vector) = (50, -100, 0, 0)
        _BankUv ("River Bank UV (min,max,edgeFrac)", Vector) = (0, 1, 0.16, 0)

        _FoamMap ("Foam Noise", 2D) = "white" {}
        _IntersectionNoise ("Intersection Noise", 2D) = "white" {}
        _PaintMap ("Water Paint", 2D) = "gray" {}

        _FoamTiling ("Foam Tiling", Float) = 0.14
        _FoamSpeed ("Foam Speed", Float) = 0.2
        _CrestFoamStrength ("River Foam", Range(0, 2)) = 0.35
        _PaintContrast ("Paint Contrast", Range(0.5, 3)) = 1.2
        _PaintRelief ("Paint Relief", Range(0, 1)) = 0.22

        _IntersectionTiling ("Intersection Tiling", Float) = 0.2
        _IntersectionSpeed ("Intersection Speed", Float) = 0.3
        // 浅水带宽度（米）；约 1.8 为半透白边甜点。
        _IntersectionLength ("Shore Depth Width (m)", Float) = 1.8
        _IntersectionFalloff ("Intersection Falloff", Float) = 1
        _IntersectionClipping ("Shore Clip", Range(0, 1)) = 0.2
        _IntersectionDistortion ("Intersection Distort", Float) = 0.12
        _ShoreSteep ("Shore Steep Max Ny", Range(0.2, 0.98)) = 0.92
        _AlbedoBoost ("Albedo Boost", Range(0.6, 2)) = 1.05

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 3
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.5
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 1.1
        _ShadowTint ("Shadow Cool", Color) = (0.45, 0.55, 0.65, 1)
        _ShadowWarm ("Shadow Warm", Color) = (0.70, 0.55, 0.40, 1)
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.4
        _SpecularColor ("Specular", Color) = (1.0, 0.95, 0.78, 1)
        _SpecularThreshold ("Specular Threshold", Range(0, 1)) = 0.88
        _SpecularBoost ("Sun Glint Boost", Range(0, 2)) = 0.45
        _AmbientColor ("Ambient", Color) = (0.70, 0.78, 0.85, 1)

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
            #include "OilRiverNPRPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
