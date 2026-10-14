#ifndef CUSTOM_OIL_RIVER_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_RIVER_NPR_INPUT_INCLUDED

#include "OilImpasto.hlsl"
#include "OilPaintNoise.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _ShallowColor;
    float4 _IntersectionColor;
    float4 _AmbientColor;
    float4 _ShadowTint;
    float4 _ShadowWarm;
    float4 _SpecularColor;
    float4 _GlintColor;
    float4 _MeshFlowScale;
    float4 _BankUv;
    float _PaintTile;
    float _PaintContrast;
    float _PaintRelief;
    float _StrokeSlice;
    float _StrokeWarp;
    float _StrokeBands;
    float _GlintTile;
    float _GlintStrength;
    float _IntersectionTiling;
    float _IntersectionSpeed;
    float _IntersectionLength;
    float _IntersectionFalloff;
    float _IntersectionClipping;
    float _IntersectionDistortion;
    float _ShoreSteep;
    float _AlbedoBoost;
    float _ShadeSteps;
    float _ShadowLift;
    float _ShadeLift;
    float _ShadowWobble;
    float _SpecularThreshold;
    float _SpecularBoost;
CBUFFER_END

float2 RiverMeshFlowUv(float2 meshUv)
{
    return meshUv * _MeshFlowScale.xy + _MeshFlowScale.zw;
}

half RiverJoinFade(float2 meshUv)
{
    return (half)smoothstep(0.0, 0.18, min(meshUv.y, 1.0 - saturate(meshUv.y)));
}

// 仅作 Depth 失败时的窄边兜底。edgeFrac 钳在很小，禁止再刷出大片 UV 白带。
half RiverThinUvShoreMask(float2 meshUv)
{
    float uMin = _BankUv.x;
    float uMax = max(_BankUv.y, uMin + 1e-4);
    float edgeFrac = min(max(_BankUv.z, 0.02), 0.08);
    float channel = uMax - uMin;
    float u = meshUv.x;

    float edgeDist = min(abs(u - uMin), abs(u - uMax)) / channel;
    half channelBank = 1.0h - (half)smoothstep(0.0, edgeFrac, edgeDist);
    channelBank *= (half)(step(uMin, u) * step(u, uMax));

    // mesh 绝对边宽约 4% UV，不再用 BankUv.z（0.32 会盖住大半河面）。
    half meshEdge = 1.0h - (half)smoothstep(0.0, 0.04, min(u, 1.0 - u));
    return saturate(max(channelBank, meshEdge));
}

half3 RiverFlowNormal(float2 flowUv, float t, out half crest)
{
    float2 p = flowUv * 0.085 + float2(t * 0.55, -t * 1.35);
    half sx = (half)sin(p.x * 1.7 + p.y * 0.4);
    half sy = (half)sin(p.y * 2.1 - p.x * 0.35 + t * 0.8);
    crest = saturate(sx * 0.45h + sy * 0.35h + 0.5h);
    return normalize(half3(sx * 0.14h, 1.0h, sy * 0.11h));
}

// Monet：横向笔触切片 + 沿流轻微滚动。flowUv 已是米制 Mesh UV。
// 贴图本身是竖向笔触（沿 V），采样前 st.yx 转 90°，河面读成横向碎笔。
float2 RiverMonetPaintUv(float2 flowUv, float t, float tileMeters, float bands)
{
    float tile = max(tileMeters, 20.0);
    float2 st = flowUv / tile;
    st += float2(t * 0.045, -t * 0.22);

    // 沿流向切片 → 河面横向色带；再交换轴对齐贴图竖笔。
    float b = max(bands, 8.0);
    float vBand = floor(st.y * b) / b;
    float n = OilValueNoise(float2(vBand * b * 0.37, floor(t * 0.35) * 0.17));
    st.x += (n - 0.5) * _StrokeWarp * 0.1;
    st.y = lerp(st.y, vBand, saturate(_StrokeSlice));
    return st.yx;
}

half3 SampleRiverPaint(float2 st)
{
    float2 f = frac(st);
    float2 edge = min(f, 1.0 - f);
    float border = min(edge.x, edge.y);
    half w = (half)smoothstep(0.0, 0.14, border);
    half3 center = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, st).rgb;
    half3 offset = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, st + 0.5).rgb;
    return lerp(offset, center, w);
}

#endif
