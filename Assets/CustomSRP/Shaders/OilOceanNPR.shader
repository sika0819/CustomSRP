Shader "CustomSRP/OilOceanNPR"
{
    Properties
    {
        _BaseColor ("Tint", Color) = (0.88, 0.84, 1, 0.92)
        _DeepColor ("Ultramarine", Color) = (0.02, 0.16, 0.55, 1)
        _MidColor ("Sea Blue", Color) = (0.06, 0.42, 0.88, 1)
        _ShallowColor ("Light Sea Blue", Color) = (0.18, 0.58, 0.92, 1)
        _OchreTint ("Yellow Ochre", Color) = (0.55, 0.42, 0.18, 1)
        _FoamColor ("Titanium White", Color) = (0.92, 0.93, 0.9, 1)
        _GlintColor ("Ochre Sun Dash", Color) = (0.85, 0.7, 0.28, 1)
        _SparkleBrush ("Sun Stroke", 2D) = "black" {}
        _PaintThickness ("Paint Rim", Range(0, 2)) = 0.35
        _PaintMap ("Oil Paint", 2D) = "gray" {}
        _PaintTile ("Paint Tile (m)", Range(200, 8000)) = 4500
        _PaintContrast ("Paint Contrast", Range(0.5, 3)) = 1.2
        _PaintRelief ("Paint Relief", Range(0, 1)) = 0.7

        _FoamStrength ("Foam Strength", Range(0, 2)) = 1.7
        _FoamMap ("Foam Brush", 2D) = "white" {}
        _IntersectionTiling ("Foam Tile", Range(0.02, 0.25)) = 0.1
        _IntersectionSpeed ("Foam Drift (m/s)", Range(0, 4)) = 1.6
        _IntersectionClipping ("Foam Clip", Range(0.15, 0.9)) = 0.34
        _IntersectionDistortion ("Foam Distort (m)", Range(0, 40)) = 2
        _IntersectionLength ("Foam Depth (m)", Range(0.15, 1.2)) = 0.45
        _RefractionStrength ("Refraction", Range(0, 1)) = 0.7
        _DepthVertical ("Depth Absorption", Range(0.3, 4)) = 0.85
        _ShoreFoamWidth ("Shore Foam Width (m)", Range(8, 28)) = 18
        _ShoreHeightMap ("Terrain Height (01)", 2D) = "black" {}
        _ShoreOriginSize ("Shore Origin/Size XZ", Vector) = (0, 0, 16500, 16500)
        _ShoreHeightScale ("Shore Height Scale (m)", Float) = 1184.7
        _ShoreWaterLevel ("Shore Water Level (m)", Float) = 0.35
        _ShoreFeatherM ("Coast Feather (m)", Range(2, 80)) = 18
        _ShoreMapStrength ("Shore Map Strength", Range(0, 2)) = 1.35

        _SunPathStrength ("Sun Path Strength", Range(0, 4)) = 2.2
        _SunPathWidth ("Sun Path Narrowness", Range(0.1, 40)) = 6

        _ShadeSteps ("Shade Steps", Range(2, 8)) = 4
        _ShadeLift ("Shade Lift", Range(0, 1)) = 0.52
        _ShadowLift ("Shadow Lift", Range(0, 1.5)) = 1.05
        _ShadowTint ("Shadow Cool Tint", Color) = (0.22, 0.14, 0.42, 1)
        _ShadowWarm ("Shadow Warm Tint", Color) = (0.55, 0.4, 0.28, 1)
        _ShadowWobble ("Shadow Brush Edge", Range(0, 1)) = 0.45
        _SpecularColor ("Specular", Color) = (0.95, 0.9, 0.55, 1)
        _SpecularThreshold ("Specular Threshold", Range(0, 1)) = 0.92
        _AmbientColor ("Ambient", Color) = (0.42, 0.36, 0.62, 1)

        [Toggle(_RECEIVE_SHADOWS)] _ReceiveShadows ("Receive Shadows", Float) = 1
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
        #include "OilOceanNPRInput.hlsl"
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
            #pragma shader_feature_local _RECEIVE_SHADOWS
            #pragma shader_feature_local _PREMULTIPLY_ALPHA
            #pragma vertex OilOceanNPRPassVertex
            #pragma fragment OilOceanNPRPassFragment
            #include "OilOceanNPRPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "CustomSRP.Editor.CustomShaderGUI"
}
