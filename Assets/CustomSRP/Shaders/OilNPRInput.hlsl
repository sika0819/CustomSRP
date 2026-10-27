#ifndef CUSTOM_OIL_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_NPR_INPUT_INCLUDED

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
float4 _BaseMap_TexelSize;

TEXTURE2D(_BumpMap);
SAMPLER(sampler_BumpMap);

TEXTURE2D(_OutlineBrushMap);
SAMPLER(sampler_OutlineBrushMap);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _OutlineBrushMap_ST;
    float4 _BaseColor;
    float4 _AmbientColor;
    float4 _SpecColor;
    float _Cutoff;
    float _Clipping;
    float _ShadeSteps;
    float _Smoothness;
    float _PaintThickness;
    float _ShadowLift;
    float _ShadeLift;
    float _ShadowWobble;
    float _ShadowBrushScale;
    // 0=Mesh UV, 1=World, 2=Object — matches Boluo LitStrokeRock / wood mat authoring
    float _WorldTriplanar;
    float _TriplanarTiling;
    float _OilFoliage;
    float _OilFoliageSkipDay;
    float _OilCrossBillboard;
    float _OilYBillboard;
    float _NormalStrength;
    float _EdgeFade;
    float _OilGrassWindStrength;
CBUFFER_END

// 风力参数：与草风力系统共享全局 uniform。
float3 _OilGrassWindDir;

// OilSkyboxTime: 0 dawn / 1 day / 2 dusk / 3 night (wraps to 4).
#ifndef OIL_SKY_PERIOD_DECLARED
#define OIL_SKY_PERIOD_DECLARED
float _OilSkyPeriod;
#endif

// Billboard SkipDay: foliage weight 1 at dawn/dusk/night, 0 at noon.
// Spans the full morning↔noon and noon↔evening arcs so WeatherService's
// 2s Hours lerp (SmoothStep) drives a continuous blend — not a 0.5 snap.
float OilFoliagePeriodWeight(float period)
{
    if (period < 0.0 || period > 4.0)
        period = 1.0;
    period = period - floor(period * 0.25) * 4.0;

    // 0→1 morning→noon: 1→0
    if (period < 1.0)
        return 1.0 - saturate(period);
    // 1→2 noon→evening: 0→1
    if (period < 2.0)
        return saturate(period - 1.0);
    // evening / night
    return 1.0;
}

// Near leaves (skipDay=0): returns material _OilFoliage as 0/1.
// Billboards (skipDay=1): returns 0..1 period weight for lighting lerp.
float OilEffectiveFoliageFlag(float foliageFlag, float skipDay)
{
    if (foliageFlag < 0.5)
        return 0.0;
    if (skipDay < 0.5)
        return foliageFlag;
    return OilFoliagePeriodWeight(_OilSkyPeriod);
}

#include "OilShadowBrush.hlsl"
#include "OilTreeCrossBillboard.hlsl"

half SampleShadowBrush(float3 positionWS, float2 baseUV)
{
#if defined(OIL_MOBILE_PERF)
    // LightingEx ignores brush/wobble; SoftFogStroke(0.5) is identity. Skip the tex tap.
    return 0.5h;
#else
    return SampleOilShadowBrush(positionWS, baseUV, 0.25);
#endif
}

struct InputConfig
{
    Fragment fragment;
    float2 baseUV;
    float3 positionWS;
    float3 positionOS;
    float3 normalWS;
    float3 normalOS;
    // ShadowCaster 等共用 Pass 只填 UV 时为 false，禁止误用原点三向采样。
    bool triplanarSpaceValid;
};

InputConfig GetInputConfig(float4 positionSS, float2 baseUV)
{
    InputConfig c;
    c.fragment = GetFragment(positionSS);
    c.baseUV = baseUV;
    c.positionWS = 0.0;
    c.positionOS = 0.0;
    c.normalWS = float3(0.0, 1.0, 0.0);
    c.normalOS = float3(0.0, 1.0, 0.0);
    c.triplanarSpaceValid = false;
    return c;
}

InputConfig GetInputConfig(
    float4 positionSS,
    float2 baseUV,
    float3 positionWS,
    float3 positionOS,
    float3 normalWS,
    float3 normalOS)
{
    InputConfig c = GetInputConfig(positionSS, baseUV);
    c.positionWS = positionWS;
    c.positionOS = positionOS;
    c.normalWS = normalWS;
    c.normalOS = normalOS;
    c.triplanarSpaceValid = true;
    return c;
}

float2 TransformBaseUV(float2 baseUV)
{
    return baseUV * _BaseMap_ST.xy + _BaseMap_ST.zw;
}

float4 GetBaseRaw(float2 uv)
{
    return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
}

// 单向三向：一张 _BaseMap，yz/xz/xy 三轴采样，按 |N| 线性混合。
float4 SampleBaseTriplanar(float3 position, float3 normal)
{
    float3 blend = abs(normal);
    blend = max(blend, 1e-4);
    blend /= dot(blend, 1.0);
    float tiling = max(_TriplanarTiling, 1e-4);
    float3 p = position * tiling;
    float4 cx = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.yz);
    float4 cy = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.xz);
    float4 cz = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.xy);
    return cx * blend.x + cy * blend.y + cz * blend.z;
}

float4 GetBase(InputConfig c)
{
    float4 raw;
    // Branch on material constant — SRP Batcher keeps these in UnityPerMaterial.
    if (c.triplanarSpaceValid && _WorldTriplanar > 1.5)
    {
        raw = SampleBaseTriplanar(c.positionOS, c.normalOS);
    }
    else if (c.triplanarSpaceValid && _WorldTriplanar > 0.5)
    {
        raw = SampleBaseTriplanar(c.positionWS, c.normalWS);
    }
    else
    {
        raw = GetBaseRaw(c.baseUV);
    }
    return raw * _BaseColor;
}

float GetCutoff(InputConfig c)
{
    return _Cutoff;
}

#endif
