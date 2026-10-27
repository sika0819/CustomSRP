Shader "CustomSRP/OilWaterfallNPR"
{
    Properties
    {
        // 参考油画大瀑：顶缘深蓝 → 竖帘白蓝齿 → 底雾。
        _BaseColor ("Deep Sheet", Color) = (0.12, 0.22, 0.48, 0.78)
        _ShallowColor ("Shallow Sheet", Color) = (0.55, 0.78, 0.95, 0.55)
        _MistColor ("Mist", Color) = (0.88, 0.90, 0.94, 0.55)

        // 竖帘必须 Mesh UV；y 取负让纹理沿 -V 下落（Boluo Waterfall ≈ 50.9, -3.91）。
        _MeshFlowScale ("Mesh Flow Scale (m per UV)", Vector) = (50.9, -3.91, 0, 0)

        _PaintMap ("Sheet Paint (Monet)", 2D) = "gray" {}
        _MistMap ("Mist / Spray", 2D) = "black" {}

        _PaintTile ("Paint Tile (m)", Range(5, 120)) = 28
        _PaintContrast ("Paint Contrast", Range(0.5, 3)) = 1.55
        _PaintRelief ("Paint Relief", Range(0, 1)) = 0.82
        _PaintStretch ("Vertical Stretch", Range(0.2, 2.5)) = 1.65
        _StrokeWarp ("Stroke Warp", Range(0, 2)) = 0.85
        _StrokeBands ("Vertical Bands", Range(8, 96)) = 40
        _AlbedoBoost ("Albedo Boost", Range(0.6, 2)) = 1.05

        _MistTile ("Mist Tile", Range(0.5, 12)) = 3.5
        _MistStrength ("Mist Strength", Range(0, 2)) = 0.75
        _MistSpeed ("Mist Drift", Range(0, 1.5)) = 0.4
        _MistFeather ("Mist Feather (uv)", Range(0.05, 0.45)) = 0.22
        _TeethStrength ("White Teeth", Range(0, 2)) = 1.05

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 3
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.52
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 1.05
        _ShadowTint ("Shadow Cool", Color) = (0.28, 0.36, 0.55, 1)
        _ShadowWarm ("Shadow Warm", Color) = (0.70, 0.55, 0.40, 1)
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.48
        _SpecularColor ("Specular", Color) = (0.95, 0.97, 1.0, 1)
        _SpecularThreshold ("Specular Threshold", Range(0, 1)) = 0.8
        _AmbientColor ("Ambient", Color) = (0.68, 0.76, 0.88, 1)

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
