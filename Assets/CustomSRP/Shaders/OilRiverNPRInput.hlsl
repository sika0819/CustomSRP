#ifndef CUSTOM_OIL_RIVER_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_RIVER_NPR_INPUT_INCLUDED

#include "OilImpasto.hlsl"
#include "OilPaintNoise.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _ShallowColor;
    float4 _SpecularColor;
    float4 _GlintColor;
    float4 _MeshFlowScale;
    float4 _BumpTiling;
    float4 _BumpScroll;
    float _PaintTile;
    float _PaintContrast;
    float _PaintRelief;
    float _StrokeSlice;
    float _StrokeWarp;
    float _StrokeBands;
    float _GlintTile;
    float _GlintStrength;
    float _ShoreFoamWidth;
    float _IntersectionTiling;
    float _IntersectionSpeed;
    float _IntersectionLength;
    float _IntersectionFalloff;
    float _IntersectionClipping;
    float _IntersectionDistortion;
    float _IntersectionAlpha;

// 拍岸沫宽度（米）：优先 _ShoreFoamWidth，旧材质回退 _IntersectionLength。
float OilRiverShoreFoamWidthM()
{
    float w = _ShoreFoamWidth;
    if (w < 0.05)
    {
        w = _IntersectionLength;
    }
    return max(w, 0.2);
}
    float _AlbedoBoost;
    float _ShadeSteps;
    float _SpecularThreshold;
    float _ReflectStrength;
    float _BumpScale;
    float _BumpTile;
CBUFFER_END

TEXTURE2D(_BumpMap);
SAMPLER(sampler_BumpMap);

float2 RiverMeshFlowUv(float2 meshUv)
{
    return meshUv * _MeshFlowScale.xy + _MeshFlowScale.zw;
}

half3 RiverFlowNormal(float2 flowUv, float t, out half crest)
{
    float2 p = flowUv * 0.085 + float2(t * 0.55, -t * 1.35);
    half sx = (half)sin(p.x * 1.7 + p.y * 0.4);
    half sy = (half)sin(p.y * 2.1 - p.x * 0.35 + t * 0.8);
    crest = saturate(sx * 0.45h + sy * 0.35h + 0.5h);
    return normalize(half3(sx * 0.14h, 1.0h, sy * 0.11h));
}

// Bump along Mesh Flow UV so normals follow river bends (not world XZ).
// Feeds reflect / glint only — same role as OilOceanBumpNormal.
half3 RiverBumpNormal(float2 flowUv, float2 paintSt, half3 baseN)
{
    float tile = max(_BumpTile, 10.0);
    float2 tiling = max(_BumpTiling.xy, float2(0.01, 0.01));
    // 同海 / Monet：bump 贴图竖笔，flowUv.yx 转横后再乘非等比 tiling。
    float2 uv = (flowUv.yx / tile) * tiling + _BumpScroll.xy * _Time.y;
    half3 nTS = (half3)DecodeNormal(
        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv), _BumpScale);
    half2 offset = half2(nTS.x, nTS.y);
#if !defined(OIL_MOBILE_PERF)
    half3 paintC = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt).rgb;
    half3 paintDx = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt + float2(0.02, 0.0)).rgb;
    half3 paintDz = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt + float2(0.0, 0.02)).rgb;
    half h0 = PaintLuma(paintC);
    half hx = PaintLuma(paintDx) - h0;
    half hz = PaintLuma(paintDz) - h0;
    offset += half2(hx, hz) * (half)_BumpScale * 1.8h;
#endif
    half3 bumpN = normalize(half3(offset.x, 1.0h, offset.y));
    return normalize(lerp(baseN, bumpN, saturate((half)_BumpScale)));
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
#if defined(OIL_MOBILE_PERF)
    return (half3)SAMPLE_TEXTURE2D_BIAS(_PaintMap, sampler_PaintMap, st, -1.25).rgb;
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
