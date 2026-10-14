Shader "CustomSRP/OilTerrainGrass"
{
    Properties
    {
        [MainTexture] _GrassTexture ("Grass Texture", 2D) = "white" {}
        [MainColor] _BaseColor ("Blade Color", Color) = (0.655, 0.722, 0.435, 1)
        _GroundColor ("Root Color", Color) = (0.377, 0.502, 0.310, 1)
        _AmbientColor ("Ambient", Color) = (0.55, 0.5, 0.44, 1)
        _GrassWidth ("Grass Width", Float) = 1
        _GrassHeight ("Grass Height", Float) = 1
        _WindStrength ("Wind", Range(0, 2)) = 0.55
        _RandomNormal ("Random Normal", Range(0, 0.5)) = 0.12
        _AlphaClip ("Alpha Clip", Range(0, 1)) = 0.48
        _Billboard ("Billboard", Float) = 0
        _UseTexColor ("Use Texture Color", Float) = 0
        _ShadeSteps ("Shade Steps", Range(2, 8)) = 4
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.42
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "IgnoreProjector" = "True"
            "PerformanceChecks" = "False"
        }

        LOD 200
        Cull Off
        ZWrite On
        ZTest LEqual

        HLSLINCLUDE
        #include "../ShaderLibrary/Common.hlsl"
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "CustomLit" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex OilTerrainGrassPassVertex
            #pragma fragment OilTerrainGrassPassFragment
            #pragma multi_compile _ OIL_MOBILE_PERF
            #include "OilTerrainGrassPass.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
