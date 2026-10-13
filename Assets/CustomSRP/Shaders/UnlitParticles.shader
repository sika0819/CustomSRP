Shader "CustomSRP/Particles/Unlit"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        [HDR] _BaseColor ("Color", Color) = (1, 1, 1, 1)
        [Toggle(_VERTEX_COLORS)] _VertexColors ("Vertex Colors", Float) = 0
        [Toggle(_FLIPBOOK_BLENDING)] _FlipbookBlending ("Flipbook Blending", Float) = 0
        [Toggle(_NEAR_FADE)] _NearFade ("Near Fade", Float) = 0
        _NearFadeDistance ("Near Fade Distance", Range(0.0, 10.0)) = 1
        _NearFadeRange ("Near Fade Range", Range(0.01, 10.0)) = 1
        [Toggle(_SOFT_PARTICLES)] _SoftParticles ("Soft Particles", Float) = 0
        _SoftParticlesDistance ("Soft Particles Distance", Range(0.0, 10.0)) = 0
        _SoftParticlesRange ("Soft Particles Range", Range(0.01, 10.0)) = 1
        [Toggle(_DISTORTION)] _Distortion ("Distortion", Float) = 0
        [NoScaleOffset] _DistortionMap ("Distortion Vectors", 2D) = "bump" {}
        _DistortionStrength ("Distortion Strength", Range(0.0, 0.2)) = 0.1
        _DistortionBlend ("Distortion Blend", Range(0.0, 1.0)) = 1
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        [Toggle(_CLIPPING)] _Clipping ("Alpha Clipping", Float) = 0
        [KeywordEnum(On, Clip, Dither, Off)] _Shadows ("Shadows", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
        [Enum(Off, 0, On, 1)] _ZWrite ("Z Write", Float) = 0
        // Mesh FX (butterfly wings) need Off; billboards can stay Back.
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
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
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            // multi_compile：蝴蝶等 FX 只在 Yoo/AB 里，shader_feature 真机会剥掉 → 无裁剪白片。
            #pragma multi_compile_local _ _CLIPPING
            #pragma shader_feature_local _VERTEX_COLORS
            #pragma shader_feature_local _FLIPBOOK_BLENDING
            #pragma shader_feature_local _NEAR_FADE
            #pragma shader_feature_local _SOFT_PARTICLES
            #pragma shader_feature_local _DISTORTION
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
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile_local _ _SHADOWS_CLIP
            #pragma shader_feature_local _SHADOWS_DITHER
            #pragma multi_compile_instancing
            #pragma vertex ShadowCasterPassVertex
            #pragma fragment ShadowCasterPassFragment
            #include "ShadowCasterPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
