Shader "CustomSRP/OilSkyboxNPR"
{
    Properties
    {
        [Header(Time)]
        _Period ("Period (0 Dawn@06 1 Day@12 2 Dusk@18 3 Night@00)", Range(0, 4)) = 1

        [Header(Oil Canvas)]
        _OilCanvas ("OilCanvas", 2D) = "gray" {}
        _BrushScale ("Brush Scale", Range(0.5, 8)) = 8
        _BrushStrength ("Brush Strength", Range(0, 1)) = 1
        _BrushContrast ("Brush Contrast", Range(0.5, 4)) = 1.8
        _BrushRelief ("Brush Relief", Range(0, 1)) = 1

        [Header(Sun Moon Stars)]
        _SunTex ("Sun Texture", 2D) = "white" {}
        _MoonTex ("Moon Texture", 2D) = "white" {}
        _StarsTex ("Stars Atlas (4x4)", 2D) = "black" {}
        _StarDensity ("Star Density", Range(0.25, 3)) = 1
        _StarSize ("Star Size", Range(0.25, 3)) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
        }

        HLSLINCLUDE
        #include "../ShaderLibrary/Common.hlsl"
        #include "OilSkyboxNPRInput.hlsl"
        ENDHLSL

        Cull Off
        ZWrite Off
        ZTest LEqual

        Pass
        {
            Fog { Mode Off }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex OilSkyboxNPRVertex
            #pragma fragment OilSkyboxNPRFragment
            #include "OilSkyboxNPRPass.hlsl"
            ENDHLSL
        }
    }
}
