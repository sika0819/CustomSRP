#ifndef CUSTOM_OIL_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_NPR_INPUT_INCLUDED

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
float4 _BaseMap_TexelSize;

TEXTURE2D(_CanvasMap);
SAMPLER(sampler_CanvasMap);

TEXTURE2D(_OutlineBrushMap);
SAMPLER(sampler_OutlineBrushMap);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _CanvasMap_ST;
    float4 _OutlineBrushMap_ST;
    float4 _BaseColor;
    float4 _AmbientColor;
    float4 _ShadowTint;
    float4 _ShadowWarm;
    float4 _SpecularColor;
    float4 _EdgeColor;
    float4 _OutlineColor;
    float _Cutoff;
    float _ShadeSteps;
    float _SpecularThreshold;
    float _CanvasStrength;
    float _PaintThickness;
    float _EdgeStrength;
    float _OutlineWidth;
    float _KuwaharaRadius;
    float _ShadowLift;
    float _ShadeLift;
    float _ShadowWobble;
    float _ShadowBrushScale;
    float _OutlineNoise;
    float _OutlineNoiseScale;
    float _OutlineWobble;
    float _OutlineBreak;
    float _OutlineBrushScale;
CBUFFER_END

#include "OilShadowBrush.hlsl"

half SampleShadowBrush(float3 positionWS, float2 baseUV)
{
    return SampleOilShadowBrush(positionWS, baseUV, 0.25);
}

struct InputConfig
{
    Fragment fragment;
    float2 baseUV;
    float2 canvasUV;
};

InputConfig GetInputConfig(float4 positionSS, float2 baseUV, float2 canvasUV = 0.0)
{
    InputConfig c;
    c.fragment = GetFragment(positionSS);
    c.baseUV = baseUV;
    c.canvasUV = canvasUV;
    return c;
}

float2 TransformBaseUV(float2 baseUV)
{
    return baseUV * _BaseMap_ST.xy + _BaseMap_ST.zw;
}

float2 TransformCanvasUV(float2 uv)
{
    return uv * _CanvasMap_ST.xy + _CanvasMap_ST.zw;
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

half4 SampleCanvas(float2 uv)
{
    return (half4)SAMPLE_TEXTURE2D(_CanvasMap, sampler_CanvasMap, uv);
}

half3 GetCanvasNormalTS(half4 canvas, half strength)
{
    half2 nxy = canvas.rg * 2.0h - 1.0h;
    // Keep Z dominant so weak canvas never sparkles.
    strength = min(strength, 0.5h);
    return normalize(half3(nxy * strength, 1.0h));
}

half GetCanvasThicknessMask(half4 canvas)
{
    return canvas.b;
}

#endif
