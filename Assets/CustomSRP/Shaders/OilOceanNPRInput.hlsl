#ifndef CUSTOM_OIL_OCEAN_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_OCEAN_NPR_INPUT_INCLUDED

#include "OilImpasto.hlsl"
#include "OilPaintNoise.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _ShallowColor;
    float4 _FoamColor;
    float4 _GlintColor;
    float4 _AmbientColor;
    float4 _SpecularColor;
    float _ShadeSteps;
    float _SpecularThreshold;
    float _SunPathStrength;
    float _SunPathWidth;
    float _FoamStrength;
    float _IntersectionTiling;
    float _IntersectionSpeed;
    float _IntersectionClipping;
    float _IntersectionDistortion;
    float _IntersectionLength;
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
    float _ReflectStrength;
CBUFFER_END

// 海面 Monet 纹理 UV 滚动（世界 XZ / 秒）。Glint V 共用此方向，表观漂向一致。
#define OCEAN_PAINT_SCROLL float2(0.045, -0.12)

// 对齐河：横向 Monet 切片。海贴图已是横笔，不再 st.yx 对调。
float2 OceanMonetPaintUv(float2 worldXZ, float t, float tileMeters, float bands)
{
    float tile = max(tileMeters, 80.0);
    float2 st = worldXZ / tile;
    st += OCEAN_PAINT_SCROLL * t;

    float b = max(bands, 8.0);
    float zBand = floor(st.y * b) / b;
    float n = OilValueNoise(float2(zBand * b * 0.37, floor(t * 0.35) * 0.17));
    st.x += (n - 0.5) * _StrokeWarp * 0.1;
    st.y = lerp(st.y, zBand, saturate(_StrokeSlice));
    return st;
}

#endif
