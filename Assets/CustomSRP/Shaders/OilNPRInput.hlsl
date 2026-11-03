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
    float4 _TopColor;
    float4 _BottomColor;
    float _Cutoff;
    float _Clipping;
    float _ShadeSteps;
    float _Smoothness;
    float _ShadowLift;
    float _ShadeLift;
    float _ShadowWobble;
    float _ShadowBrushScale;
    float _GradientOffset;
    float _GradientSmoothness;
    float _FoliageHeight;
    float _FoliageYMin;
    float _OilFoliage;
    float _OilBark;
    // 0=Mesh UV, 1=World, 2=Object — matches Boluo LitStrokeRock / wood mat authoring
    float _WorldTriplanar;
    float _TriplanarTiling;
    float _OilCrossBillboard;
    float _OilYBillboard;
    float _BumpTiling;
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

// Y-billboard day/night mix: vegetation lighting at dawn/dusk/night, NPR at noon.
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

// Shared triplanar axes/origin with albedo. uvScale>1 densifies oil strokes only.
void OilTriplanarUVs(
    float3 position,
    float3 blendNormal,
    float uvScale,
    out float2 uvX,
    out float2 uvY,
    out float2 uvZ,
    out float3 blend)
{
    blend = abs(normalize(blendNormal));
    blend = max(blend, 1e-4);
    blend /= dot(blend, 1.0);
    float tiling = max(_TriplanarTiling, 1e-4);
    float3 p = position * tiling;
    float2 st = _BaseMap_ST.xy * max(uvScale, 1e-4);
    uvX = p.yz * st + _BaseMap_ST.zw;
    uvY = p.xz * st + _BaseMap_ST.zw;
    uvZ = p.xy * st + _BaseMap_ST.zw;
}

// 单向三向：一张 _BaseMap，yz/xz/xy 三轴采样，按 |N| 线性混合。
float4 SampleBaseTriplanar(float3 position, float3 normal)
{
    float2 uvX, uvY, uvZ;
    float3 blend;
    OilTriplanarUVs(position, normal, 1.0, uvX, uvY, uvZ, blend);
    float4 cx = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvX);
    float4 cy = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvY);
    float4 cz = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvZ);
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

// Whiteout triplanar: same axes as albedo; _BumpTiling densifies ShiTou oil brush.
// ShiTou_OilBrushNormal is authored along rock grain — no UV flip.
float3 SampleBumpTriplanar(
    float3 position,
    float3 blendNormal,
    float3 shadeNormal,
    float scale)
{
    float2 uvX, uvY, uvZ;
    float3 blend;
    OilTriplanarUVs(position, blendNormal, max(_BumpTiling, 1e-4), uvX, uvY, uvZ, blend);
    float3 nShade = normalize(shadeNormal);
    float3 nx = DecodeNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvX), scale);
    float3 ny = DecodeNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvY), scale);
    float3 nz = DecodeNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvZ), scale);
    nx = float3(nx.xy + nShade.zy, abs(nx.z) * nShade.x);
    ny = float3(ny.xy + nShade.xz, abs(ny.z) * nShade.y);
    nz = float3(nz.xy + nShade.xy, abs(nz.z) * nShade.z);
    return normalize(nx.zyx * blend.x + ny.xzy * blend.y + nz * blend.z);
}

// Mesh-UV props: baseUV already has BaseMap_ST; _BumpTiling densifies oil strokes.
float3 SampleBumpMeshWS(float2 uv, float3 geomNormal, float scale)
{
    float3 nGeom = normalize(geomNormal);
    float2 bumpUV = uv * max(_BumpTiling, 1e-4);
    float3 nTS = DecodeNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, bumpUV), scale);
    float3 tRef = abs(nGeom.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 t = normalize(tRef - nGeom * dot(nGeom, tRef));
    return NormalTangentToWorld(nTS, nGeom, float4(t, -1.0));
}

float3 GetOilPropBumpWS(InputConfig c, float3 geomNormalWS)
{
    float scale = max(_NormalStrength, 1e-4);
    // Object triplanar (ShiTou): same OS axes as albedo; _BumpTiling = stroke size.
    if (c.triplanarSpaceValid && _WorldTriplanar > 1.5)
    {
        float3 nOS = SampleBumpTriplanar(c.positionOS, c.normalOS, c.normalOS, scale);
        return TransformObjectToWorldNormal(nOS);
    }
    if (c.triplanarSpaceValid && _WorldTriplanar > 0.5)
    {
        return SampleBumpTriplanar(c.positionWS, c.normalWS, c.normalWS, scale);
    }
    return SampleBumpMeshWS(c.baseUV, geomNormalWS, scale);
}

float GetCutoff(InputConfig c)
{
    return _Cutoff;
}

#endif
