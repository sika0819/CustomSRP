Shader "CustomSRP/NoiseFire"
{
    // Dead-Rabbit/UnityFire noise flame (Smooth / Cut / Cycle) for Custom SRP.
    Properties
    {
        _MainTex ("Shape", 2D) = "white" {}
        _NoiseTex ("Noise", 2D) = "white" {}
        _BottomMask ("Bottom Mask", 2D) = "white" {}
        _ColorGradientTex ("Color Gradient", 2D) = "white" {}
        _DisplaceStrength ("Displace Strength", Range(0, 1)) = 0.1
        _Time0 ("Time Offset", Float) = 0
        _ColorScale ("Color Scale", Range(0, 2)) = 1
        _AlphaScale ("Alpha Scale", Range(0, 2)) = 1
        _Size ("Cut / Cycle Size", Range(0.0, 100)) = 40
        [KeywordEnum(Smooth, Cut, Cycle)] _FireMode ("Fire Mode", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual
        // UnityFire: Blend One OneMinusSrcAlpha (premultiplied)
        Blend One OneMinusSrcAlpha

        HLSLINCLUDE
        #include "../ShaderLibrary/Common.hlsl"
        ENDHLSL

        Pass
        {
            Name "NoiseFire"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma shader_feature_local _FIREMODE_CUT _FIREMODE_CYCLE
            #pragma vertex NoiseFireVertex
            #pragma fragment NoiseFireFragment
            #include "NoiseFirePass.hlsl"
            ENDHLSL
        }
    }
}
