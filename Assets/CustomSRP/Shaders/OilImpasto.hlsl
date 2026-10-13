#ifndef CUSTOM_OIL_IMPASTO_INCLUDED
#define CUSTOM_OIL_IMPASTO_INCLUDED

// Scenic cameras sit kilometers out. Splat tiles and sine sparkles mip to a flat wash.
// One world-space paint tile stays large enough to read as a brushstroke.

TEXTURE2D(_PaintMap);
SAMPLER(sampler_PaintMap);

half3 SamplePaintSeamless(float2 worldXZ, float tileMeters)
{
    float tile = max(tileMeters, 80.0);
    float2 uv = worldXZ / tile;
    float2 f = frac(uv);
    float2 edge = min(f, 1.0 - f);
    float border = min(edge.x, edge.y);
    half w = (half)smoothstep(0.0, 0.16, border);
    half3 center = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, uv).rgb;
    half3 offset = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, uv + 0.5).rgb;
    return lerp(offset, center, w);
}

half PaintLuma(half3 paint)
{
    return max(paint.r, max(paint.g, paint.b));
}

half PaintStroke(half luma, half contrast)
{
    return saturate((luma - 0.5h) * contrast + 0.5h);
}

// stroke 0.5 leaves color alone, so a missing map does not tint the surface.
half3 ImpastoDeviation(half3 color, half stroke, half relief)
{
    half w = (stroke - 0.5h) * saturate(relief);
    half up = saturate(w * 2.0h);
    half down = saturate(-w * 2.0h);
    half3 warm = saturate(color * half3(1.24h, 1.08h, 0.74h) + 0.04h);
    half3 cool = color * half3(0.48h, 0.64h, 0.82h);
    half3 painted = lerp(color, warm, up);
    return lerp(painted, cool, down);
}

#endif
