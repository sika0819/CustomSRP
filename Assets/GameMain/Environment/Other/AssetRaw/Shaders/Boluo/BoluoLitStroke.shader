// 族: 通用几何体 | 光: LitAlbedoObject | 雾: FinishLit
// 建筑 / 栏杆 / 木 / 透贴。albedo = _BaseMap × _BaseColor。
// mesh UV 或三平面。石头（含 TFF 油砖）走 Boluo/LitStrokeRock。合批别名 LitStrokeIndirect。
Shader "Boluo/LitStroke"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1, 1, 1, 1)
        [Normal] _BumpMap("Normal", 2D) = "bump" {}
        _BumpScale("Normal Scale", Range(0, 2)) = 0
        [Enum(Mesh UV,0,World,1,Object,2)] _WorldTriplanar("Triplanar", Float) = 0
        _TriplanarTiling("Triplanar Tiling", Float) = 0.25
        [ToggleUI] _BakedStroke("Baked Stroke (no overlay)", Float) = 0
        [HideInInspector] [ToggleUI] _RockSeason("Rock Winter Snow", Float) = 0
        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clip", Float) = 0
        _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Cull [_Cull]
        HLSLINCLUDE
        #pragma target 3.0
        #pragma multi_compile _ BOLUO_GPU_INDIRECT
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:BoluoGpuInstancingSetup
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #include "BoluoLitStrokeForward.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex BoluoShadowPassVertex
            #pragma fragment BoluoShadowPassFragment
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:BoluoGpuInstancingSetup
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "BoluoLitStrokeInput.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "BoluoOpaqueShadow.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex BoluoDepthOnlyVert
            #pragma fragment BoluoDepthOnlyFrag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:BoluoGpuInstancingSetup
            #include "BoluoLitStrokeInput.hlsl"
            #include "BoluoOpaqueDepth.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
