#ifndef CUSTOM_OIL_IMPASTO_INCLUDED
#define CUSTOM_OIL_IMPASTO_INCLUDED

// Scenic cameras sit kilometers out. Splat tiles and sine sparkles mip to a flat wash.
// One world-space paint tile stays large enough to read as a brushstroke.
//
// Impasto helpers preserve authored hue: stroke height lifts/digs value and
// ridge specular; paint maps are structure, not a second palette.

TEXTURE2D(_PaintMap);
SAMPLER(sampler_PaintMap);
TEXTURE2D(_PaintNormal);
SAMPLER(sampler_PaintNormal);

#include "OilImpastoCore.hlsl"

// Paint samplers are Mirror. Odd tiles reverse the height slope; the stored
// normal does not, so the lit flank would face the wrong way without this flip.
float3 MirrorCorrectOilNormal(float3 nTS, float2 st)
{
    float2 odd = abs(fmod(floor(st), 2.0));
    nTS.x = lerp(nTS.x, -nTS.x, step(0.5, odd.x));
    nTS.y = lerp(nTS.y, -nTS.y, step(0.5, odd.y));
    return nTS;
}

float3 LoadOilPaintNormal(float2 st, float scale)
{
    return MirrorCorrectOilNormal(
        DecodeNormal(SAMPLE_TEXTURE2D(_PaintNormal, sampler_PaintNormal, st), scale),
        st);
}

float3 LoadOilPaintNormalBias(float2 st, float scale)
{
    return MirrorCorrectOilNormal(
        DecodeNormal(SAMPLE_TEXTURE2D_BIAS(_PaintNormal, sampler_PaintNormal, st, -1.25), scale),
        st);
}

float3 LoadOilPaintNormalGrad(float2 st, float2 dx, float2 dy, float scale)
{
    return MirrorCorrectOilNormal(
        DecodeNormal(SAMPLE_TEXTURE2D_GRAD(_PaintNormal, sampler_PaintNormal, st, dx, dy), scale),
        st);
}

// seam 1 = center sample, matching the paint lerp(offset, center, seam).
float3 BlendOilPaintNormal(float3 center, float3 offset, half seam)
{
    return normalize(lerp(offset, center, saturate((float)seam)));
}

half3 SamplePaintSeamless(float2 worldXZ, float tileMeters)
{
    float tile = max(tileMeters, 80.0);
    float2 uv = worldXZ / tile;
#if defined(OIL_MOBILE_PERF)
    // Seam blend is invisible at renderScale 0.5 — one tap, biased so the stroke mip survives.
    return (half3)SAMPLE_TEXTURE2D_BIAS(_PaintMap, sampler_PaintMap, uv, -1.25).rgb;
#else
    float2 f = frac(uv);
    float2 edge = min(f, 1.0 - f);
    float border = min(edge.x, edge.y);
    half w = (half)smoothstep(0.0, 0.16, border);
    half3 center = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, uv).rgb;
    half3 offset = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, uv + 0.5).rgb;
    return lerp(offset, center, w);
#endif
}

#endif
