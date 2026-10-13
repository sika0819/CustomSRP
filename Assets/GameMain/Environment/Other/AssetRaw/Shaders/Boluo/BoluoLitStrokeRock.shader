// 族: 石 | UsePass LitStroke | 光/雾看材质 _RockSeason
// ShiTou 油砖物体三平面。合批开 BOLUO_GPU_INDIRECT，不要换成 Indirect。
Shader "Boluo/LitStrokeRock"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1, 1, 1, 1)
        [Normal] _BumpMap("Normal", 2D) = "bump" {}
        _BumpScale("Normal Scale", Range(0, 2)) = 0
        [Enum(Mesh UV,0,World,1,Object,2)] _WorldTriplanar("Triplanar", Float) = 0
        _TriplanarTiling("Triplanar Tiling", Float) = 0.25
        [ToggleUI] _RockSeason("Rock Winter Snow", Float) = 0
        [ToggleUI] _BakedStroke("Baked Stroke (no overlay)", Float) = 0
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
        UsePass "Boluo/LitStroke/ForwardLit"
        UsePass "Boluo/LitStroke/ShadowCaster"
        UsePass "Boluo/LitStroke/DepthOnly"
    }

    FallBack Off
}
