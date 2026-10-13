#ifndef CUSTOM_OIL_OCEAN_NPR_INPUT_INCLUDED
#define CUSTOM_OIL_OCEAN_NPR_INPUT_INCLUDED

TEXTURE2D(_ShoreHeightMap);
SAMPLER(sampler_ShoreHeightMap);

#include "OilImpasto.hlsl"
#include "OilPaintNoise.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _FoamColor;
    float4 _GlintColor;
    float4 _AmbientColor;
    float4 _ShadowTint;
    float4 _ShadowWarm;
    float4 _SpecularColor;
    float4 _ShoreOriginSize;
    float _ShadeSteps;
    float _SpecularThreshold;
    float _ShadowLift;
    float _ShadeLift;
    float _ShadowWobble;
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
    float _ShoreFoamWidth;
    float _ShoreHeightScale;
    float _ShoreWaterLevel;
    float _ShoreFeatherM;
    float _ShoreMapStrength;
CBUFFER_END

float2 ShoreHeightUV(float2 worldXZ)
{
    float2 size = max(_ShoreOriginSize.zw, float2(1.0, 1.0));
    return (worldXZ - _ShoreOriginSize.xy) / size;
}

float SampleShoreHeight01(float2 worldXZ)
{
    float2 uv = ShoreHeightUV(worldXZ);
    float inside = (float)(all(uv >= 0.0) && all(uv <= 1.0));
    return SAMPLE_TEXTURE2D(_ShoreHeightMap, sampler_ShoreHeightMap, saturate(uv)).r * inside;
}

half CoastWaterMask(float2 worldXZ)
{
    if (_ShoreMapStrength <= 0.001)
    {
        return 1.0h;
    }

    float bed = SampleShoreHeight01(worldXZ) * max(_ShoreHeightScale, 1.0);
    float wl = (float)_ShoreWaterLevel;
    // Overlook: a fixed feather reads as a contour line. FBM breaks the isoline.
    float feather = max((float)_ShoreFeatherM, 4.0);
    float n = OilFbm(worldXZ * 0.008);
    feather *= lerp(0.75, 1.35, n);
    float wlPerturb = (n - 0.5) * min(feather * 0.25, 8.0);
    return 1.0h - smoothstep(wl + wlPerturb, wl + wlPerturb + feather, bed);
}

#endif
