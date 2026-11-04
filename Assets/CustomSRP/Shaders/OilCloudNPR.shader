Shader "CustomSRP/OilCloudNPR"
{
    // Port of Boluo/Sky/GenshinCloudsMobile lighting (no OilCanvasNormal flank).
    // UV disturb = _NoiseMap. Shape/light from CloudMap SDF.
    Properties
    {
        _CloudMap("云图（R底部渐变 G边缘光 B形状SDF A透明度）", 2D) = "white" {}
        _NoiseMap("噪声图（B 扰动）", 2D) = "gray" {}
        _NoiseSpeed("噪声漂移速度", Range(0, 0.05)) = 0.0027
        _UVDisturbance("UV 扰动强度", Range(0, 0.2)) = 0.04
        [Header(Cloud Shape)]
        _SdfSoftness("SDF 边缘软度", Range(0.01, 0.5)) = 0.14
        _SdfMin("云量=1 时 SDF 阈值（越小云越大）", Range(0.003, 1.5)) = 0.48
        _SdfMax("云量=0 时 SDF 阈值（>1 云消失）", Range(0.003, 1.5)) = 0.90
        [Header(Lighting)]
        _TopShadow("云顶天顶色混合（远日面）", Range(0, 1)) = 1
        _TopHighlight("云顶受光混合（近日面）", Range(0, 1)) = 0.92
        _EdgeIntensity("边缘光强度", Range(0, 3)) = 1.55
    }

    SubShader
    {
        LOD 0

        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-50"
            "IgnoreProjector" = "True"
        }

        Cull Back
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
            #pragma vertex CloudVertex
            #pragma fragment CloudFragment
            #include "OilCloudNPRPass.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
