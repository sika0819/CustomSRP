#ifndef CUSTOM_OIL_WATERFALL_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_WATERFALL_NPR_INPUT_INCLUDED

#include "OilImpasto.hlsl"
#include "OilPaintNoise.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _ShallowColor;
    float4 _MistColor;
    float4 _AmbientColor;
    float4 _ShadowTint;
    float4 _ShadowWarm;
    float4 _SpecularColor;
    float4 _MeshFlowScale;
    float _PaintTile;
    float _PaintContrast;
    float _PaintRelief;
    float _PaintStretch;
    float _StrokeWarp;
    float _StrokeBands;
    float _AlbedoBoost;
    float _MistTile;
    float _MistStrength;
    float _MistSpeed;
    float _MistFeather;
    float _TeethStrength;
    float _ShadeSteps;
    float _ShadowLift;
    float _ShadeLift;
    float _SpecularThreshold;
CBUFFER_END

float2 WaterfallMeshFlowUv(float2 meshUv)
{
    return meshUv * _MeshFlowScale.xy + _MeshFlowScale.zw;
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

// 油画竖帘：沿 U 切片成竖条，V 向下滚动。贴图本身是竖笔，不转轴。
float2 WaterfallMonetPaintUv(float2 flowUv, float t, float tileMeters, float bands)
{
    float tile = max(tileMeters, 5.0);
    float2 st = flowUv / tile;
    st.y *= max(_PaintStretch, 0.2);
    // 竖帘下落放慢：原先 -t*1.55 偏快，笔触读成闪带。
    st += float2(t * 0.008, -t * 0.45);

    float b = max(bands, 8.0);
    float uBand = floor(st.x * b) / b;
    float n = OilValueNoise(float2(uBand * b * 0.41, floor(t * 0.18) * 0.19));
    st.y += (n - 0.5) * _StrokeWarp * 0.12;
    st.x = lerp(st.x, uBand, 0.72);
    return st;
}

half3 SampleWaterfallPaint(float2 st)
{
#if defined(OIL_MOBILE_PERF)
    return (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, st).rgb;
#else
    float2 f = frac(st);
    float2 edge = min(f, 1.0 - f);
    float border = min(edge.x, edge.y);
    half w = (half)smoothstep(0.0, 0.14, border);
    half3 center = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, st).rgb;
    half3 offset = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, st + 0.5).rgb;
    return lerp(offset, center, w);
#endif
}

#endif
