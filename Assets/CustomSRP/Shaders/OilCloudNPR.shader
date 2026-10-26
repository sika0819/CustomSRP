Shader "CustomSRP/OilCloudNPR"
{
    // Cloud ring. Atlas: R belly, G edge, B SDF, A alpha.
    // Oil stack matches skybox/ocean: canvas ridges, impasto warm/cool, weave tooth.
    Properties
    {
        _CloudMap ("Cloud Map (R belly G edge B SDF A)", 2D) = "black" {}
        _NoiseMap ("Noise (B disturbs UV)", 2D) = "gray" {}
        _CloudBrush ("Cloud Stroke", 2D) = "gray" {}
        _OilCanvas ("Oil Canvas", 2D) = "gray" {}

        [Header(Shape)]
        _UVDisturbance ("UV Disturbance", Range(0, 0.2)) = 0.04
        _SdfSoftness ("SDF Softness", Range(0.01, 0.5)) = 0.14
        _SdfMin ("SDF At Full Cover", Range(0.003, 1.5)) = 0.48
        _SdfMax ("SDF At Empty Cover", Range(0.003, 1.5)) = 0.90

        [Header(Light)]
        _TopShadow ("Far-Side Zenith Mix", Range(0, 1)) = 1
        _TopHighlight ("Near-Side Highlight Mix", Range(0, 1)) = 0.92
        _EdgeIntensity ("Edge Light", Range(0, 3)) = 1.55

        [Header(Oil Brush)]
        _BrushScale ("Brush Scale", Range(0.5, 8)) = 6
        _BrushStrength ("Brush Strength", Range(0, 1)) = 0.72
        _BrushContrast ("Brush Contrast", Range(0.5, 4)) = 1.6
        _BrushRelief ("Brush Relief", Range(0, 1)) = 0.85
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-50"
            "IgnoreProjector" = "True"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        HLSLINCLUDE
        #include "../ShaderLibrary/Common.hlsl"
        #include "OilCloudNPRInput.hlsl"
        ENDHLSL

        Pass
        {
            Name "Cloud"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile _ OIL_MOBILE_PERF
            #pragma vertex CloudVertex
            #pragma fragment CloudFragment
            #include "OilCloudNPRPass.hlsl"
            ENDHLSL
        }
    }
}
