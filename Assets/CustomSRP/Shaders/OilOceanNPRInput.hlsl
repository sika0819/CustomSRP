#ifndef CUSTOM_OIL_OCEAN_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_OCEAN_NPR_INPUT_INCLUDED

#include "OilImpasto.hlsl"
#include "OilPaintNoise.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _ShallowColor;
    float4 _FoamColor;
    float4 _GlintColor;
    float4 _TintShallow;
    float4 _TintDeep;
    float _TintStrength;
    float _SunPathStrength;
    float _SunPathWidth;
    float _FoamStrength;
    float _IntersectionClipping;
    float _IntersectionTiling;
    float _IntersectionSpeed;
    float _IntersectionFalloff;
    float _IntersectionDistortion;
    float _RefractionStrength;
    float _DepthVertical;
    float4 _PaintMap_ST;
    float _PaintTile;
    float _PaintContrast;
    float _PaintRelief;
    float _StrokeSlice;
    float _StrokeWarp;
    float _StrokeBands;
    float _AlbedoBoost;
    float _ShoreFoamWidth;
    #define OIL_REFLECT_STRENGTH_DECLARED
    float _ReflectStrength;
    float _ReflectDistort;
    float _ReflectSteps;
    float _BumpScale;
    float _BumpTile;
    float4 _BumpTiling;
    float4 _BumpScroll;
    float _ShadeSteps;
CBUFFER_END

// 海面 Monet / 日道 Glint 共用世界滚动（向后漂）.
#define OCEAN_PAINT_SCROLL float2(-0.022, 0.06)

// 贴图笔触沿 U。U 铺到世界 Z（横向），V 铺到世界 X（纵向）。
// 主滚动留在 V 上，纹路横着，水流顺着视线前后走。
float2 OceanMonetPaintUv(float2 worldXZ, float t, float tileMeters, float bands)
{
    float tile = max(tileMeters, 20.0);
    float2 st = worldXZ.yx / tile;
    st += OCEAN_PAINT_SCROLL * t;

    float b = max(bands, 8.0);
    float zBand = floor(st.y * b) / b;
    float n = OilValueNoise(float2(zBand * b * 0.37, floor(t * 0.35) * 0.17));
    st.x += (n - 0.5) * _StrokeWarp * 0.1;
    st.y = lerp(st.y, zBand, saturate(_StrokeSlice));
    return st;
}

#endif
