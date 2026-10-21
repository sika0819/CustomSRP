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
    float _OilFoliage;
    float _OilCrossBillboard;
    float _NormalStrength;
    float _EdgeFade;
    float _OilGrassWindStrength;
CBUFFER_END

// 风力参数：与草风力系统共享全局 uniform。
float3 _OilGrassWindDir;

#include "OilShadowBrush.hlsl"
#include "OilTreeCrossBillboard.hlsl"

half SampleShadowBrush(float3 positionWS, float2 baseUV)
{
#if defined(OIL_MOBILE_PERF)
    return SampleOilShadowBrushCheap(positionWS, baseUV, 0.25);
#else
    return SampleOilShadowBrush(positionWS, baseUV, 0.25);
#endif
}

struct InputConfig
{
    Fragment fragment;
    float2 baseUV;
};

InputConfig GetInputConfig(float4 positionSS, float2 baseUV)
{
    InputConfig c;
    c.fragment = GetFragment(positionSS);
    c.baseUV = baseUV;
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

float4 GetBase(InputConfig c)
{
    return GetBaseRaw(c.baseUV) * _BaseColor;
}

float GetCutoff(InputConfig c)
{
    return _Cutoff;
}

#endif
