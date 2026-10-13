#ifndef CUSTOM_OIL_RIVER_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_RIVER_NPR_INPUT_INCLUDED

#include "OilImpasto.hlsl"
#include "OilPaintNoise.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _ShallowColor;
    float4 _FoamColor;
    float4 _IntersectionColor;
    float4 _AmbientColor;
    float4 _ShadowTint;
    float4 _ShadowWarm;
    float4 _SpecularColor;
    float4 _MeshFlowScale;
    float4 _BankUv;
    float _FoamTiling;
    float _FoamSpeed;
    float _CrestFoamStrength;
    float _PaintContrast;
    float _PaintRelief;
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

#endif
