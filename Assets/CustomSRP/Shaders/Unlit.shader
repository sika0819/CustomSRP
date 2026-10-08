Shader "CustomSRP/Unlit"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        [HDR] _BaseColor ("Color", Color) = (1, 1, 1, 1)
        [Toggle(_VERTEX_COLORS)] _VertexColors ("Vertex Colors", Float) = 0
        // Shared UnlitInput CBUFFER slots (Particles shader drives these; keep layout aligned).
        [HideInInspector] _NearFadeDistance ("Near Fade Distance", Range(0.0, 10.0)) = 1
        [HideInInspector] _NearFadeRange ("Near Fade Range", Range(0.01, 10.0)) = 1
        [HideInInspector] _SoftParticlesDistance ("Soft Particles Distance", Range(0.0, 10.0)) = 0
        [HideInInspector] _SoftParticlesRange ("Soft Particles Range", Range(0.01, 10.0)) = 1
        [HideInInspector] _DistortionStrength ("Distortion Strength", Range(0.0, 0.2)) = 0.1
        [HideInInspector] _DistortionBlend ("Distortion Blend", Range(0.0, 1.0)) = 1
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        [Toggle(_CLIPPING)] _Clipping ("Alpha Clipping", Float) = 0
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

        HLSLINCLUDE
        #include "../ShaderLibrary/Common.hlsl"
        #include "UnlitInput.hlsl"
        ENDHLSL

        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
            ZWrite [_ZWrite]
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma shader_feature _CLIPPING
            #pragma shader_feature _VERTEX_COLORS
            #pragma multi_compile_instancing
            #pragma vertex UnlitPassVertex
            #pragma fragment UnlitPassFragment
            #include "UnlitPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Tags { "LightMode" = "ShadowCaster" }

            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma shader_feature _ _SHADOWS_CLIP _SHADOWS_DITHER
            #pragma multi_compile_instancing
            #pragma vertex ShadowCasterPassVertex
            #pragma fragment ShadowCasterPassFragment
            #include "ShadowCasterPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
