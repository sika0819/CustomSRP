Shader "CustomSRP/OilWaterfallNPR"
{
    Properties
    {
        // 与 OilRiver 同青蓝色板。
        _BaseColor ("Deep Sheet", Color) = (0.28, 0.58, 0.82, 0.62)
        _ShallowColor ("Shallow Sheet", Color) = (0.401, 0.805, 1, 0.45)
        _FoamColor ("Sheet Foam", Color) = (0.364, 0.619, 0.811, 1)
        _MistColor ("Mist", Color) = (0.82, 0.88, 0.92, 0.45)

        // 竖帘必须 Mesh UV；y 取负让纹理沿 -V 下落（Boluo Waterfall ≈ 50.9, -3.91）。
        _MeshFlowScale ("Mesh Flow Scale (m per UV)", Vector) = (50.9, -3.91, 0, 0)

        _FoamMap ("Foam Noise", 2D) = "white" {}
        _PaintMap ("Sheet Paint", 2D) = "gray" {}
        _FoamTiling ("Foam Tiling", Range(0.02, 1)) = 0.14
        _FoamSpeed ("Foam Speed", Range(0, 2)) = 0.2
        _CrestFoamStrength ("Sheet Foam Strength", Range(0, 2)) = 0.72
        _PaintContrast ("Paint Contrast", Range(0.5, 3)) = 1.5
        _PaintRelief ("Paint Relief", Range(0, 1)) = 0.3
        _PaintStretch ("Vertical Stretch", Range(0.2, 2.5)) = 1.4
        _AlbedoBoost ("Albedo Boost", Range(0.6, 2)) = 1.05

        _MistStrength ("Mist Strength", Range(0, 2)) = 0.45
        _MistSpeed ("Mist Drift", Range(0, 1.5)) = 0.35
        _MistFeather ("Mist Feather (uv)", Range(0.05, 0.45)) = 0.22

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 3
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.5
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 1.1
        _ShadowTint ("Shadow Cool", Color) = (0.45, 0.55, 0.65, 1)
        _ShadowWarm ("Shadow Warm", Color) = (0.70, 0.55, 0.40, 1)
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.5
        _SpecularColor ("Specular", Color) = (1.0, 0.95, 0.78, 1)
        _SpecularThreshold ("Specular Threshold", Range(0, 1)) = 0.78
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
        #include "OilWaterfallNPRInput.hlsl"
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "CustomLit" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile _ OIL_MOBILE_PERF
            #pragma shader_feature_local _PREMULTIPLY_ALPHA
            #pragma shader_feature_local _OIL_NO_VIOLET_UMBRA
            #pragma vertex OilWaterfallNPRPassVertex
            #pragma fragment OilWaterfallNPRPassFragment
            #include "OilWaterfallNPRPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
