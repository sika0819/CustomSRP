#ifndef CUSTOM_OIL_WATERFALL_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_WATERFALL_NPR_INPUT_INCLUDED

#include "OilImpasto.hlsl"
#include "OilPaintNoise.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _ShallowColor;
    float4 _FoamColor;
    float4 _MistColor;
    float4 _AmbientColor;
    float4 _ShadowTint;
    float4 _ShadowWarm;
    float4 _SpecularColor;
    float4 _MeshFlowScale;
    float _FoamTiling;
    float _FoamSpeed;
    float _CrestFoamStrength;
    float _PaintContrast;
    float _PaintRelief;
    float _PaintStretch;
    float _AlbedoBoost;
    float _MistStrength;
    float _MistSpeed;
    float _MistFeather;
    float _ShadeSteps;
    float _ShadowLift;
    float _ShadeLift;
    float _ShadowWobble;
    float _SpecularThreshold;
CBUFFER_END

float2 WaterfallMeshFlowUv(float2 meshUv)
{
    return meshUv * _MeshFlowScale.xy + _MeshFlowScale.zw;
}

half WaterfallJoinFade(float2 meshUv)
{
    return (half)smoothstep(0.0, 0.18, min(meshUv.y, 1.0 - saturate(meshUv.y)));
}

// 竖帘细条法线：沿 mesh 流向加速，顶点几乎不动。
half3 WaterfallSheetNormal(float2 flowUv, float t, out half crest)
{
    float2 p = flowUv * float2(0.12, 0.55 * max(_PaintStretch, 0.2))
        + float2(t * 0.2, -t * 2.6);
    half sx = (half)sin(p.x * 2.2 + p.y * 0.35);
    half sy = (half)sin(p.y * 3.0 - t * 2.0);
    crest = saturate(sx * 0.4h + sy * 0.4h + 0.5h);
    return normalize(half3(sx * 0.2h, 1.0h, sy * 0.12h));
}

#endif
